// App-owned build and process lifecycle. No sibling repository or pre-existing deployment fallback.
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { createReadStream, existsSync } from 'node:fs';
import { mkdir, realpath, stat, rm } from 'node:fs/promises';
import { dirname, extname, join, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';
import { startBackend, resolveDist, writeBootstrapFile } from '../frontend/scripts/backend-launcher.mjs';
import { randomBytes } from 'node:crypto';
import { buildDesktopNative, augmentDesktopDeployment, verifyDesktopDeployment } from './desktop-build.mjs';
import desktopSessionPaths from '../desktop/gpu-preview/session-paths.cjs';
import ordinaryExec from '../desktop/gpu-preview/ordinary-exec.cjs';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const LOCAL = join(ROOT, '.local');
const { positionals, values } = parseArgs({ allowPositionals: true, options: { port: { type: 'string', default: '5173' },'cdp-port':{type:'string'},'slow-consumer-ms':{type:'string'},'session-name':{type:'string'},'performance-diagnostics':{type:'boolean'},'ordinary-scheduling':{type:'boolean'} } });
const command = positionals[0];
const port = Number(values.port);
const sessionName=values['session-name'];
if(sessionName!==undefined&&command!=='desktop')throw new Error('Named sessions are only available for desktop');
const cdpPort=values['cdp-port']===undefined?undefined:Number(values['cdp-port']);
if(cdpPort!==undefined&&(command!=='desktop'||!Number.isInteger(cdpPort)||cdpPort<1024||cdpPort>65535||cdpPort===port))throw new Error('CDP port must be a separate explicit desktop acceptance port');
if(values['performance-diagnostics']&&(!cdpPort||!sessionName))throw new Error('Performance diagnostics require an explicit named CDP acceptance session');
if(values['ordinary-scheduling']&&(!cdpPort||!sessionName))throw new Error('Ordinary scheduling requires an explicit named CDP acceptance session');
const slowConsumerMs=values['slow-consumer-ms']===undefined?undefined:Number(values['slow-consumer-ms']);
if(slowConsumerMs!==undefined&&(!cdpPort||!Number.isInteger(slowConsumerMs)||slowConsumerMs<1||slowConsumerMs>1000))throw new Error('Slow consumer requires an explicit desktop acceptance session');
if (!['build', 'build:desktop', 'desktop', 'start', 'dev'].includes(command) || positionals.length !== 1 || !Number.isInteger(port) || port < 1024 || port > 65535) {
  console.error('用法：node scripts/app.mjs <build|build:desktop|desktop|start|dev> [--port 5173]');
  process.exit(1);
}
await mkdir(LOCAL, { recursive: true, mode: 0o700 });
if (await realpath(LOCAL) !== LOCAL) throw new Error('App .local 不能通过符号链接引用外部目录。');
const sessionPaths=command==='desktop'?desktopSessionPaths.prepareDesktopSession(ROOT,sessionName):undefined;
if (process.env.MAKO_BACKEND_REPO && resolve(process.env.MAKO_BACKEND_REPO) !== join(ROOT, 'backend')) {
  throw new Error('App 禁止引用外部 MAKO_BACKEND_REPO；请移除该环境变量。');
}

function run(executable, args, cwd) {
  return new Promise((resolveRun, reject) => {
    const child = spawn(executable, args, { cwd, stdio: 'inherit' });
    const forward = (signal) => child.kill(signal);
    process.on('SIGINT', forward);
    process.on('SIGTERM', forward);
    child.on('error', reject);
    child.on('exit', (code, signal) => {
      process.off('SIGINT', forward);
      process.off('SIGTERM', forward);
      if (code === 0) resolveRun();
      else reject(new Error(`${executable} 退出：${code ?? signal}`));
    });
  });
}

if (command === 'build' || command === 'build:desktop') {
  const native = command === 'build:desktop' ? await buildDesktopNative(ROOT) : null;
  await run('pnpm', ['build'], join(ROOT, 'frontend'));
  const stamp = new Date().toISOString().replaceAll(/[^0-9TZ]/g, '');
  const dist = join(LOCAL, 'backend-dist', stamp);
  await run('python3', [join(ROOT, 'backend/tools/backend-web-host/host.py'), 'publish', '--dist', dist], join(ROOT, 'backend'));
  const gpuPreview = native ? await augmentDesktopDeployment(ROOT, dist, native) : undefined;
  await writeBootstrapFile(join(LOCAL, 'backend-current.json'), { dist, gpuPreview });
  console.log('[app] Web 与匹配 Server＋Worker 构建完成；pnpm start 启动。');
} else {
  const bootstrapFile = command === 'desktop' ? join(LOCAL,'run',`desktop-backend-${process.pid}.json`) : join(LOCAL, 'run/backend.json');
  const origins = [`http://127.0.0.1:${port}`, `http://localhost:${port}`];
  const distRoot = join(ROOT, 'frontend/dist');
  if (command !== 'dev' && !existsSync(join(distRoot, 'index.html'))) throw new Error('缺少 Web 构建；请先运行 pnpm build。');
  let backend;
  let startup;
  let web;
  let stopping = false;
  let desktopChild;
  let desktopExited;
  let desktopConfigFile;
  async function shutdown(code = 0) {
    if (stopping) return;
    stopping = true;
    if (desktopChild?.pid && desktopChild.exitCode === null && desktopChild.signalCode === null) {
      await new Promise(done => {
        const force = setTimeout(() => { if (desktopChild.exitCode === null && desktopChild.signalCode === null) desktopChild.kill('SIGKILL'); }, 15000);
        desktopExited.then(() => { clearTimeout(force); done(); });
        desktopChild.kill('SIGTERM');
      });
    }
    if (web) await new Promise((done) => {
      const result = web.close(done);
      if (result?.then) result.then(done, done);
      web.closeAllConnections?.();
    });
    if (startup) backend = await startup.catch(() => null);
    if (backend) await backend.stop();
    if (desktopConfigFile) await rm(desktopConfigFile, { force:true });
    console.log('[app] 自有 Web 与后端进程已停止。');
    process.exit(code);
  }
  for (const signal of ['SIGINT', 'SIGTERM', 'SIGHUP']) process.on(signal, () => void shutdown());
  try {
    let desktopSession;
    if (command === 'desktop') {
      const desktopDist=resolveDist().dist;
      const desktopManifest=await verifyDesktopDeployment(ROOT, desktopDist);
      const { default:electron } = await import('../desktop/node_modules/electron/index.js');
      desktopConfigFile = join(LOCAL, 'run', `desktop-${process.pid}.json`);
      desktopSession = { consumerId:randomBytes(16).toString('hex'), consumerSecret:randomBytes(32).toString('hex'), pageUrl:origins[0], root:ROOT,
        brokerAddon:join(desktopDist,'desktop/gpu_preview_broker.node'),brokerAddonSha256:desktopManifest.files['desktop/gpu_preview_broker.node'],slowConsumerMs,sessionName,performanceDiagnostics:!!values['performance-diagnostics'],ordinaryScheduling:!!values['ordinary-scheduling'] };
      await writeBootstrapFile(desktopConfigFile, { ...desktopSession, ready:false });
      const electronArgs=[...(cdpPort?[`--remote-debugging-port=${cdpPort}`,'--remote-debugging-address=127.0.0.1']:[]),join(ROOT,'desktop/main.cjs'),'--launch-config',desktopConfigFile];
      const launch=values['ordinary-scheduling']?ordinaryExec.ordinaryCommand(electron,electronArgs):{executable:electron,args:electronArgs};
      desktopChild = spawn(launch.executable,launch.args, { cwd:ROOT, env:{...process.env,ELECTRON_RUN_AS_NODE:undefined}, stdio:['ignore','pipe','pipe'] });
      desktopExited = new Promise(done => { desktopChild.once('exit',done); desktopChild.once('error',done); });
      desktopChild.stdout.on('data', data => process.stdout.write(data));
      desktopChild.stderr.on('data', data => process.stderr.write(data));
      desktopChild.on('error', () => { if (!stopping) void shutdown(1); });
      desktopChild.once('exit', code => { if (!stopping) void shutdown(code || 0); });
      if (!desktopChild.pid) throw new Error('Electron did not start');
    }
    startup = startBackend({ profile: sessionPaths?.profile??join(LOCAL,'profile'), bootstrapFile, origins,
      ordinaryScheduling:!!values['ordinary-scheduling'],
      ...(desktopSession ? { env:{ MAKO_DESKTOP_CONSUMER_ID:desktopSession.consumerId, MAKO_DESKTOP_CONSUMER_SECRET:desktopSession.consumerSecret, MAKO_DESKTOP_MAIN_PID:String(desktopChild.pid) } } : {}),
    });
    backend = await startup;
    if (stopping) await new Promise(() => {});
    backend.exited.then((code) => { if (!stopping) void shutdown(code || 1); });
    if (command === 'dev') {
      process.env.MAKO_APP_BOOTSTRAP_FILE = bootstrapFile;
      process.env.MAKO_APP_PORT = String(port);
      const { createServer: createViteServer } = await import('../frontend/node_modules/vite/dist/node/index.js');
      web = await createViteServer({ root: join(ROOT, 'frontend'), configFile: join(ROOT, 'frontend/vite.config.ts'), server: { host: '127.0.0.1', port, strictPort: true } });
      await web.listen();
    } else {
      const hosts = new Set([`127.0.0.1:${port}`, `localhost:${port}`]);
      const loopback = new Set(['127.0.0.1', '::1', '::ffff:127.0.0.1']);
      const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.svg': 'image/svg+xml', '.json': 'application/json; charset=utf-8', '.woff2': 'font/woff2' };
      web = createServer((req, res) => { void (async () => {
        res.setHeader('X-Content-Type-Options', 'nosniff');
        if (!hosts.has(req.headers.host) || !loopback.has(req.socket.remoteAddress)) { res.writeHead(403).end(); return; }
        const pathname = new URL(req.url, origins[0]).pathname;
        if (pathname === '/__mako/bootstrap') {
          res.setHeader('Cache-Control', 'no-store');
          const sameOrigin = req.headers.origin === undefined || req.headers.origin === `http://${req.headers.host}`;
          if (req.method !== 'GET' || req.headers['sec-fetch-site'] !== 'same-origin' || !sameOrigin) { res.writeHead(403).end(); return; }
          const { baseUrl, hostInstanceId, bootstrapToken } = backend.info;
          res.setHeader('Content-Type', 'application/json; charset=utf-8');
          res.end(JSON.stringify({ baseUrl, hostInstanceId, bootstrapToken }));
          return;
        }
        if (!['GET', 'HEAD'].includes(req.method)) { res.writeHead(405).end(); return; }
        let path;
        try { path = resolve(distRoot, '.' + decodeURIComponent(pathname)); } catch { res.writeHead(400).end(); return; }
        if (!path.startsWith(distRoot + sep) && path !== distRoot) { res.writeHead(403).end(); return; }
        if (!extname(path)) path = join(distRoot, 'index.html');
        let info;
        try { info = await stat(path); } catch { res.writeHead(404).end(); return; }
        if (!info.isFile()) { res.writeHead(404).end(); return; }
        res.setHeader('Content-Type', types[extname(path)] ?? 'application/octet-stream');
        res.setHeader('Cache-Control', 'no-cache');
        res.setHeader('Content-Length', info.size);
        if (req.method === 'HEAD') res.end();
        else createReadStream(path).on('error', () => { if (!res.headersSent) res.writeHead(500); res.end(); }).pipe(res);
      })().catch(() => { if (!res.headersSent) res.writeHead(500); res.end(); }); });
      await new Promise((done, reject) => { web.once('error', reject); web.listen(port, '127.0.0.1', done); });
    }
    if (desktopSession) await writeBootstrapFile(desktopConfigFile, { ...desktopSession, ready:true, baseUrl:backend.info.baseUrl, hostInstanceId:backend.info.hostInstanceId, mainPid:desktopChild.pid });
    console.log(`[app] ${command === 'dev' ? '开发' : command === 'desktop' ? '桌面' : '构建'}页面：http://127.0.0.1:${port}（Ctrl+C 停止）`);
  } catch (error) {
    console.error(`[app] ${error.message}`);
    await shutdown(1);
  }
}
