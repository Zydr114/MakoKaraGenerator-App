// App-owned build and process lifecycle. No sibling repository or pre-existing deployment fallback.
import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { mkdir, readdir, readFile, realpath, rm } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';
import { startBackend, writeBootstrapFile } from '../frontend/scripts/backend-launcher.mjs';
import { randomBytes } from 'node:crypto';
import { buildDesktopNative, augmentDesktopDeployment } from './desktop-build.mjs';
import { desktopBuildState, recordWebBuild, webInputs } from './build-state.mjs';
import desktopSessionPaths from '../desktop/gpu-preview/session-paths.cjs';
import ordinaryExec from '../desktop/gpu-preview/ordinary-exec.cjs';
import webServer from '../desktop/runtime/web-server.cjs';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const LOCAL = join(ROOT, '.local');
const { positionals, values } = parseArgs({ allowPositionals: true, options: { port: { type: 'string' },'cdp-port':{type:'string'},'slow-consumer-ms':{type:'string'},'session-name':{type:'string'},'performance-diagnostics':{type:'boolean'},'ordinary-scheduling':{type:'boolean'} } });
const command = positionals[0];
// 桌面版用自己的固定端口，避开 Vite 常用的 5173；端口固定，页面存在本机的设置（主题、渲染设备）才留得住。
const DESKTOP_PORT = 5180;
const port = Number(values.port ?? (command === 'desktop' ? DESKTOP_PORT : 5173));
const sessionName=values['session-name'];
if(sessionName!==undefined&&command!=='desktop')throw new Error('Named sessions are only available for desktop');
const cdpPort=values['cdp-port']===undefined?undefined:Number(values['cdp-port']);
if(cdpPort!==undefined&&(command!=='desktop'||!Number.isInteger(cdpPort)||cdpPort<1024||cdpPort>65535||cdpPort===port))throw new Error('CDP port must be a separate explicit desktop acceptance port');
if(values['performance-diagnostics']&&(!cdpPort||!sessionName))throw new Error('Performance diagnostics require an explicit named CDP acceptance session');
if(values['ordinary-scheduling']&&(!cdpPort||!sessionName))throw new Error('Ordinary scheduling requires an explicit named CDP acceptance session');
const slowConsumerMs=values['slow-consumer-ms']===undefined?undefined:Number(values['slow-consumer-ms']);
if(slowConsumerMs!==undefined&&(!cdpPort||!Number.isInteger(slowConsumerMs)||slowConsumerMs<1||slowConsumerMs>1000))throw new Error('Slow consumer requires an explicit desktop acceptance session');
if (!['build', 'build:desktop', 'desktop', 'start', 'dev'].includes(command) || positionals.length !== 1 || !Number.isInteger(port) || port < 1024 || port > 65535) {
  console.error(`用法：node scripts/app.mjs <build|build:desktop|desktop|start|dev> [--port 端口]（默认 5173，桌面版 ${DESKTOP_PORT}）`);
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

/** Web 构建，并记下构建时的源码输入（桌面版启动时据此判断是否过期）。 */
async function buildWeb() {
  const inputs = await webInputs(ROOT);
  await run('pnpm', ['build'], join(ROOT, 'frontend'));
  await recordWebBuild(ROOT, inputs);
}

/** 完整构建：桌面版先构建原生预览库；再构建 Web、发布匹配的 Server＋Worker，发布目录记为当前构建。 */
async function buildApp(desktop) {
  const native = desktop ? await buildDesktopNative(ROOT) : null;
  await buildWeb();
  const stamp = new Date().toISOString().replaceAll(/[^0-9TZ]/g, '');
  const dist = join(LOCAL, 'backend-dist', stamp);
  await run('python3', [join(ROOT, 'backend/tools/backend-web-host/host.py'), 'publish', '--dist', dist], join(ROOT, 'backend'));
  const gpuPreview = native ? await augmentDesktopDeployment(ROOT, dist, native) : undefined;
  await writeBootstrapFile(join(LOCAL, 'backend-current.json'), { dist, gpuPreview });
}

/** 桌面版启动前：构建过期就先重建（后端或原生源码变了重建整个桌面版，只有前端变了只重建 Web）。 */
async function ensureDesktopBuild() {
  let state = await desktopBuildState(ROOT);
  if (state.deployment) {
    console.log(`[app] ${state.deployment}：先构建桌面版（原生预览库、Web、后端发布），第一次或后端有改动时要几分钟。`);
    await buildApp(true);
    state = await desktopBuildState(ROOT);
  } else if (state.web) {
    console.log(`[app] ${state.web}：先重新构建 Web。`);
    await buildWeb();
    state = await desktopBuildState(ROOT);
  }
  if (state.deployment || state.web) throw new Error(`桌面版构建后仍不可用：${state.deployment ?? state.web}`);
  return state;
}

/** 清掉已经退出的桌面启动器留下的私有启动文件（异常退出时来不及删）；只看文件名里的 PID 是否还是本启动器。 */
async function removeStaleDesktopRunFiles() {
  const directory = join(LOCAL, 'run');
  for (const name of await readdir(directory).catch(() => [])) {
    const match = /^desktop(?:-backend)?-(\d+)\.json$/.exec(name);
    if (!match || Number(match[1]) === process.pid) continue;
    let alive = false;
    try {
      alive = (await readFile(`/proc/${match[1]}/cmdline`, 'utf8')).split('\0').some((arg) => arg.endsWith('scripts/app.mjs'));
    } catch { /* 进程已经不在 */ }
    if (!alive) await rm(join(directory, name), { force: true });
  }
}

if (command === 'build' || command === 'build:desktop') {
  await buildApp(command === 'build:desktop');
  console.log(command === 'build:desktop' ? '[app] 桌面版构建完成；pnpm desktop 启动。' : '[app] Web 与匹配 Server＋Worker 构建完成；pnpm start 启动。');
} else {
  const desktopBuild = command === 'desktop' ? await ensureDesktopBuild() : undefined;
  if (desktopBuild) await removeStaleDesktopRunFiles();
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
    if (web) await web.close();
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
      const { dist:desktopDist, manifest:desktopManifest } = desktopBuild;
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
      web = await webServer.startStaticWebServer({
        distRoot,
        port,
        getBootstrap: () => backend.info,
      });
    }
    if (desktopSession) await writeBootstrapFile(desktopConfigFile, { ...desktopSession, ready:true, baseUrl:backend.info.baseUrl, hostInstanceId:backend.info.hostInstanceId, mainPid:desktopChild.pid });
    console.log(`[app] ${command === 'dev' ? '开发' : command === 'desktop' ? '桌面' : '构建'}页面：http://127.0.0.1:${port}（Ctrl+C 停止）`);
  } catch (error) {
    console.error(`[app] ${error.message}`);
    await shutdown(1);
  }
}
