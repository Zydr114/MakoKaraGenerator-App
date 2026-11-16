// App-owned build and process lifecycle. No sibling repository or pre-existing deployment fallback.
import { execFileSync, spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { readdir, readFile, realpath, rm } from 'node:fs/promises';
import { delimiter, dirname, isAbsolute, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';
import { startBackend, writeBootstrapFile } from '../frontend/scripts/backend-launcher.mjs';
import { randomBytes } from 'node:crypto';
import { buildDesktopNative, augmentDesktopDeployment } from './desktop-build.mjs';
import { desktopBuildState, recordWebBuild, webInputs } from './build-state.mjs';
import { resolvePnpmCommand } from './pnpm-command.mjs';
import desktopSessionPaths from '../desktop/gpu-preview/session-paths.cjs';
import ordinaryExec from '../desktop/gpu-preview/ordinary-exec.cjs';
import privateFs from '../desktop/runtime/private-fs.cjs';
import appPaths from '../desktop/runtime/app-paths.cjs';
import webServer from '../desktop/runtime/web-server.cjs';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const LOCAL = join(ROOT, '.local');
const runtimePaths = appPaths.appRuntimePaths(ROOT);
const RUNTIME = runtimePaths.runtimeRoot;
const { positionals, values } = parseArgs({
  allowPositionals: true,
  options: {
    port: { type: 'string' },
    'cdp-port': { type: 'string' },
    'slow-consumer-ms': { type: 'string' },
    'session-name': { type: 'string' },
    'performance-diagnostics': { type: 'boolean' },
    'ordinary-scheduling': { type: 'boolean' },
  },
});
const command = positionals[0];
// 桌面版用自己的固定端口，避开 Vite 常用的 5173；端口固定，页面存在本机的设置（主题、渲染设备）才留得住。
const DESKTOP_PORT = 5180;
const port = Number(values.port ?? (command === 'desktop' ? DESKTOP_PORT : 5173));
const sessionName = values['session-name'];
if (sessionName !== undefined && command !== 'desktop') throw new Error('Named sessions are only available for desktop');
const cdpPort = values['cdp-port'] === undefined ? undefined : Number(values['cdp-port']);
if (cdpPort !== undefined && (command !== 'desktop' || !Number.isInteger(cdpPort) || cdpPort < 1024 || cdpPort > 65535 || cdpPort === port)) {
  throw new Error('CDP port must be a separate explicit desktop acceptance port');
}
if (values['performance-diagnostics'] && (!cdpPort || !sessionName)) {
  throw new Error('Performance diagnostics require an explicit named CDP acceptance session');
}
if (values['ordinary-scheduling'] && (!cdpPort || !sessionName)) {
  throw new Error('Ordinary scheduling requires an explicit named CDP acceptance session');
}
const slowConsumerMs = values['slow-consumer-ms'] === undefined ? undefined : Number(values['slow-consumer-ms']);
if (slowConsumerMs !== undefined && (!cdpPort || !Number.isInteger(slowConsumerMs) || slowConsumerMs < 1 || slowConsumerMs > 1000)) {
  throw new Error('Slow consumer requires an explicit desktop acceptance session');
}
if (!['build', 'build:desktop', 'desktop', 'start', 'dev'].includes(command) || positionals.length !== 1 || !Number.isInteger(port) || port < 1024 || port > 65535) {
  console.error(`用法：node scripts/app.mjs <build|build:desktop|desktop|start|dev> [--port 端口]（默认 5173，桌面版 ${DESKTOP_PORT}）`);
  process.exit(1);
}
privateFs.ensurePrivateDir(LOCAL, { label: 'App .local' });
if (!privateFs.isSamePath(await realpath(LOCAL), LOCAL)) {
  throw new Error('App .local 不能通过符号链接引用外部目录。');
}
const sessionPaths = command === 'desktop'
  ? desktopSessionPaths.prepareDesktopSession(ROOT, sessionName, { runtimeRoot: RUNTIME })
  : undefined;
if (process.env.MAKO_BACKEND_REPO && !privateFs.isSamePath(resolve(process.env.MAKO_BACKEND_REPO), join(ROOT, 'backend'))) {
  throw new Error('App 禁止引用外部 MAKO_BACKEND_REPO；请移除该环境变量。');
}

function resolvePythonCommand() {
  if (process.platform !== 'win32') return 'python3';
  for (const dir of String(process.env.PATH ?? '').split(delimiter)) {
    if (!dir || !isAbsolute(dir)) continue;
    if (dir.toLowerCase().includes(`${join('microsoft', 'windowsapps')}`.toLowerCase())) continue;
    const candidate = join(dir, 'python.exe');
    if (existsSync(candidate)) return candidate;
  }
  return 'python';
}

function run(executable, args, cwd, spawnOptions = {}) {
  return new Promise((resolveRun, reject) => {
    const child = spawn(executable, args, { ...spawnOptions, cwd, stdio: 'inherit', windowsHide: true });
    const forward = (signal) => {
      try {
        child.kill(signal);
      } catch { /* ignore already-exited child */ }
    };
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
  const pnpm = resolvePnpmCommand(['build']);
  await run(pnpm.executable, pnpm.args, join(ROOT, 'frontend'), pnpm.spawnOptions);
  await recordWebBuild(ROOT, inputs);
}

/** 完整构建：桌面版先构建原生预览库；再构建 Web、发布匹配的 Server＋Worker，发布目录记为当前构建。 */
async function buildApp(desktop) {
  const native = desktop ? await buildDesktopNative(ROOT) : null;
  await buildWeb();
  const stamp = new Date().toISOString().replaceAll(/[^0-9TZ]/g, '');
  const dist = join(LOCAL, 'backend-dist', stamp);
  await run(
    resolvePythonCommand(),
    [join(ROOT, 'backend/tools/backend-web-host/host.py'), 'publish', '--dist', dist],
    join(ROOT, 'backend'),
  );
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

function parseLinuxStartTicks(statContent) {
  const closeParen = statContent.lastIndexOf(')');
  if (closeParen === -1) return null;
  const rest = statContent.slice(closeParen + 2).trim().split(/\s+/);
  // Field 22 (1-indexed) is index 19 after comm (field 2).
  return rest[19] ?? null;
}

function queryWindowsProcesses(pids) {
  if (pids.length === 0) return new Map();
  const psScript = [
    "$ProgressPreference = 'SilentlyContinue'",
    '$ids = @(' + pids.map((pid) => Number(pid)).filter(Number.isInteger).join(',') + ')',
    '$out = @{}',
    'foreach ($id in $ids) {',
    '  try {',
    '    $p = Get-Process -Id $id -ErrorAction Stop',
    '    $ticks = $p.StartTime.ToFileTimeUtc().ToString()',
    '    $cmd = (Get-CimInstance Win32_Process -Filter ("ProcessId = " + $id) -ErrorAction SilentlyContinue).CommandLine',
    '    $out[[string]$id] = @{ ticks = $ticks; cmdline = [string]$cmd }',
    '  } catch {}',
    '}',
    'ConvertTo-Json -InputObject $out -Compress',
  ].join('\n');
  const encoded = Buffer.from(psScript, 'utf16le').toString('base64');
  try {
    const raw = execFileSync(
      'powershell.exe',
      ['-NoProfile', '-NonInteractive', '-EncodedCommand', encoded],
      { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true },
    ).trim();
    if (!raw) return new Map();
    const parsed = JSON.parse(raw);
    return new Map(Object.entries(parsed));
  } catch {
    return new Map();
  }
}

async function getCurrentCreationTicks() {
  if (process.platform === 'linux') {
    try {
      return parseLinuxStartTicks(await readFile('/proc/self/stat', 'utf8'));
    } catch {
      return null;
    }
  }
  if (process.platform === 'win32') {
    return queryWindowsProcesses([process.pid]).get(String(process.pid))?.ticks ?? null;
  }
  return null;
}

function isPidAlive(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return error.code !== 'ESRCH';
  }
}

/** 清掉已经退出的桌面启动器留下的私有启动文件（异常退出时来不及删）；通过 (pid, creationTicks) 核对进程身份。 */
async function removeStaleDesktopRunFiles() {
  const directory = runtimePaths.run;
  const names = await readdir(directory).catch(() => []);
  const entries = [];
  const livePids = new Set();

  for (const name of names) {
    const match = /^desktop(?:-backend)?-(\d+)\.json$/.exec(name);
    if (!match) continue;
    const pid = Number(match[1]);
    if (pid === process.pid) continue;
    let recordedTicks = null;
    try {
      const parsed = JSON.parse(await readFile(join(directory, name), 'utf8'));
      if (typeof parsed?.launcherCreationTicks === 'string') {
        recordedTicks = parsed.launcherCreationTicks;
      }
    } catch { /* ignore unreadable json */ }
    const alive = isPidAlive(pid);
    if (alive) livePids.add(pid);
    entries.push({ name, pid, alive, recordedTicks });
  }

  // Fallback: desktop-backend-<pid>.json may inherit launcherCreationTicks from desktop-<pid>.json
  const ticksByPid = new Map();
  for (const entry of entries) {
    if (entry.recordedTicks && !ticksByPid.has(entry.pid)) {
      ticksByPid.set(entry.pid, entry.recordedTicks);
    }
  }

  const winInfo = process.platform === 'win32' && livePids.size > 0
    ? queryWindowsProcesses([...livePids])
    : new Map();

  for (const entry of entries) {
    if (!entry.alive) {
      await rm(join(directory, entry.name), { force: true });
      continue;
    }
    const expectedTicks = entry.recordedTicks ?? ticksByPid.get(entry.pid) ?? null;
    let sameProcess = false;
    if (process.platform === 'linux') {
      try {
        const currentTicks = parseLinuxStartTicks(await readFile(`/proc/${entry.pid}/stat`, 'utf8'));
        const cmdline = await readFile(`/proc/${entry.pid}/cmdline`, 'utf8');
        const matchesCmd = cmdline.split('\0').some((arg) => arg.endsWith('scripts/app.mjs'));
        sameProcess = expectedTicks ? (currentTicks === expectedTicks && matchesCmd) : matchesCmd;
      } catch {
        sameProcess = false;
      }
    } else if (process.platform === 'win32') {
      const info = winInfo.get(String(entry.pid));
      if (info) {
        const matchesCmd = typeof info.cmdline === 'string' && /scripts[\\/]app\.mjs/.test(info.cmdline);
        sameProcess = expectedTicks ? info.ticks === expectedTicks : matchesCmd;
      }
    }
    if (!sameProcess) {
      await rm(join(directory, entry.name), { force: true });
    }
  }
}

if (command === 'build' || command === 'build:desktop') {
  await buildApp(command === 'build:desktop');
  console.log(command === 'build:desktop' ? '[app] 桌面版构建完成；pnpm desktop 启动。' : '[app] Web 与匹配 Server＋Worker 构建完成；pnpm start 启动。');
} else {
  const desktopBuild = command === 'desktop' ? await ensureDesktopBuild() : undefined;
  if (desktopBuild) await removeStaleDesktopRunFiles();
  privateFs.ensurePrivateDir(runtimePaths.run, { label: 'App run directory' });
  const bootstrapFile = command === 'desktop' ? join(runtimePaths.run, `desktop-backend-${process.pid}.json`) : join(runtimePaths.run, 'backend.json');
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
      await new Promise((done) => {
        const force = setTimeout(() => {
          if (desktopChild.exitCode === null && desktopChild.signalCode === null) {
            try { desktopChild.kill('SIGKILL'); } catch { /* ignore */ }
          }
        }, 15000);
        desktopExited.then(() => { clearTimeout(force); done(); });
        try {
          desktopChild.kill(process.platform === 'win32' ? undefined : 'SIGTERM');
        } catch {
          clearTimeout(force);
          done();
        }
      });
    }
    if (web) await web.close();
    if (startup) backend = await startup.catch(() => null);
    if (backend) await backend.stop();
    if (desktopConfigFile) await rm(desktopConfigFile, { force: true });
    console.log('[app] 自有 Web 与后端进程已停止。');
    process.exit(code);
  }
  const signals = process.platform === 'win32' ? ['SIGINT', 'SIGTERM', 'SIGBREAK'] : ['SIGINT', 'SIGTERM', 'SIGHUP'];
  for (const signal of signals) process.on(signal, () => void shutdown());
  if (process.env.MAKO_APP_SHUTDOWN_STDIN === '1') {
    process.stdin.setEncoding('utf8');
    let stdinBuf = '';
    process.stdin.on('data', (chunk) => {
      stdinBuf += chunk;
      if (stdinBuf.split(/\r?\n/).some((line) => line.trim() === 'SHUTDOWN')) void shutdown(0);
    });
    process.stdin.on('end', () => void shutdown(0));
    process.stdin.resume();
  }
  try {
    let desktopSession;
    let launcherCreationTicks;
    if (command === 'desktop') {
      launcherCreationTicks = await getCurrentCreationTicks();
      const { dist: desktopDist, manifest: desktopManifest } = desktopBuild;
      const { default: electron } = await import('../desktop/node_modules/electron/index.js');
      desktopConfigFile = join(runtimePaths.run, `desktop-${process.pid}.json`);
      desktopSession = {
        consumerId: randomBytes(16).toString('hex'),
        consumerSecret: randomBytes(32).toString('hex'),
        pageUrl: origins[0],
        root: ROOT,
        launcherPid: process.pid,
        launcherCreationTicks,
        brokerAddon: join(desktopDist, 'desktop/gpu_preview_broker.node'),
        brokerAddonSha256: desktopManifest.files['desktop/gpu_preview_broker.node'],
        slowConsumerMs,
        sessionName,
        performanceDiagnostics: !!values['performance-diagnostics'],
        ordinaryScheduling: !!values['ordinary-scheduling'],
      };
      await writeBootstrapFile(desktopConfigFile, { ...desktopSession, ready: false });
      const electronArgs = [
        ...(cdpPort ? [`--remote-debugging-port=${cdpPort}`, '--remote-debugging-address=127.0.0.1'] : []),
        join(ROOT, 'desktop/main.cjs'),
        '--launch-config',
        desktopConfigFile,
      ];
      const spawnDesktop = values['ordinary-scheduling'] ? ordinaryExec.spawnOrdinary : spawn;
      desktopChild = spawnDesktop(electron, electronArgs, {
        cwd: ROOT,
        env: { ...process.env, ELECTRON_RUN_AS_NODE: undefined },
        stdio: ['ignore', 'pipe', 'pipe'],
        windowsHide: true,
      });
      desktopExited = new Promise((done) => { desktopChild.once('exit', done); desktopChild.once('error', done); });
      desktopChild.stdout.on('data', (data) => process.stdout.write(data));
      desktopChild.stderr.on('data', (data) => process.stderr.write(data));
      desktopChild.on('error', () => { if (!stopping) void shutdown(1); });
      desktopChild.once('exit', (code) => { if (!stopping) void shutdown(code || 0); });
      if (!desktopChild.pid) throw new Error('Electron did not start');
    }
    startup = startBackend({
      profile: sessionPaths?.profile ?? runtimePaths.profile,
      bootstrapFile,
      origins,
      ordinaryScheduling: !!values['ordinary-scheduling'],
      ...(desktopSession
        ? {
            env: {
              MAKO_DESKTOP_CONSUMER_ID: desktopSession.consumerId,
              MAKO_DESKTOP_CONSUMER_SECRET: desktopSession.consumerSecret,
              MAKO_DESKTOP_MAIN_PID: String(desktopChild.pid),
            },
          }
        : {}),
    });
    backend = await startup;
    if (stopping) await new Promise(() => {});
    backend.exited.then((code) => { if (!stopping) void shutdown(code || 1); });
    if (command === 'dev') {
      process.env.MAKO_APP_BOOTSTRAP_FILE = bootstrapFile;
      process.env.MAKO_APP_PORT = String(port);
      const { createServer: createViteServer } = await import('../frontend/node_modules/vite/dist/node/index.js');
      web = await createViteServer({
        root: join(ROOT, 'frontend'),
        configFile: join(ROOT, 'frontend/vite.config.ts'),
        server: { host: '127.0.0.1', port, strictPort: true },
      });
      await web.listen();
    } else {
      web = await webServer.startStaticWebServer({
        distRoot,
        port,
        getBootstrap: () => backend.info,
      });
    }
    if (desktopSession) {
      await writeBootstrapFile(desktopConfigFile, {
        ...desktopSession,
        ready: true,
        baseUrl: backend.info.baseUrl,
        hostInstanceId: backend.info.hostInstanceId,
        mainPid: desktopChild.pid,
      });
    }
    console.log(`[app] ${command === 'dev' ? '开发' : command === 'desktop' ? '桌面' : '构建'}页面：http://127.0.0.1:${port}（Ctrl+C 停止）`);
  } catch (error) {
    console.error(`[app] ${error.message}`);
    await shutdown(1);
  }
}
