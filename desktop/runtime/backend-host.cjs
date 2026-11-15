'use strict';

const childProcess = require('node:child_process');
const crypto = require('node:crypto');
const fs = require('node:fs');
const fsp = require('node:fs/promises');
const path = require('node:path');
const readline = require('node:readline');

const ROLES = Object.freeze({
  server: 'MakoKaraGenerator.Backend.Server',
  worker: 'MakoKaraGenerator.Backend.Worker',
});

const SCRIPT_ABI_DLL = 'MakoKaraGenerator.Scripting.Abstractions.dll';
const DEFAULT_READY_TIMEOUT_MS = 120_000;
const DEFAULT_STOP_TIMEOUT_MS = 120_000;

function loadExpectedProductVersion() {
  const candidates = [
    path.join(__dirname, '..', 'contract-manifest.json'),
    path.join(__dirname, '..', '..', 'frontend', 'docs', 'backend', 'contract', 'contract-manifest.json'),
  ];
  for (const candidate of candidates) {
    if (fs.existsSync(candidate)) {
      const parsed = JSON.parse(fs.readFileSync(candidate, 'utf8'));
      if (typeof parsed.productVersion === 'string') return parsed.productVersion;
    }
  }
  throw new Error('Missing contract-manifest.json to determine expected productVersion.');
}

const EXPECTED_PRODUCT_VERSION = loadExpectedProductVersion();

function compatibleProductVersion(version, expected = EXPECTED_PRODUCT_VERSION) {
  const parse = (value) => /^(\d+)\.(\d+)\.(\d+)$/.exec(String(value))?.slice(1).map(Number);
  const actual = parse(version);
  const contract = parse(expected);
  return !!actual && !!contract && actual[0] === contract[0] && actual[1] === contract[1] && actual[2] >= contract[2];
}

function sha256FileSync(filePath) {
  const hash = crypto.createHash('sha256');
  const buffer = Buffer.allocUnsafe(1024 * 1024);
  const fd = fs.openSync(filePath, 'r');
  try {
    for (;;) {
      const read = fs.readSync(fd, buffer, 0, buffer.length, null);
      if (read === 0) break;
      hash.update(buffer.subarray(0, read));
    }
  } finally {
    fs.closeSync(fd);
  }
  return hash.digest('hex');
}

function isInsideRoot(root, target) {
  return target.startsWith(root + path.sep);
}

function inventorySync(rootDir, currentDir = rootDir, prefix = '', out = {}) {
  const entries = fs.readdirSync(currentDir, { withFileTypes: true }).sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
  for (const entry of entries) {
    const fullPath = path.join(currentDir, entry.name);
    const relPath = prefix + entry.name;
    if (entry.isSymbolicLink()) {
      throw new Error(`Deployment cannot contain symlinks: ${relPath}`);
    }
    if (entry.isDirectory()) {
      inventorySync(rootDir, fullPath, `${relPath}/`, out);
    } else if (entry.isFile()) {
      out[relPath] = sha256FileSync(fullPath);
    }
  }
  return out;
}

function ensureRegularNonSymlinkFile(filePath, label) {
  let info;
  try {
    info = fs.lstatSync(filePath);
  } catch {
    throw new Error(`${label} is missing: ${filePath}`);
  }
  if (info.isSymbolicLink() || !info.isFile()) {
    throw new Error(`${label} must be a regular non-symlink file: ${filePath}`);
  }
}

function validateScriptAbi(dist) {
  const paths = Object.keys(ROLES).map((role) => path.join(dist, role, SCRIPT_ABI_DLL));
  for (const filePath of paths) {
    try {
      ensureRegularNonSymlinkFile(filePath, SCRIPT_ABI_DLL);
    } catch {
      throw new Error(`Server/Worker ${SCRIPT_ABI_DLL} bytes differ or are missing; publish a fresh matched deployment`);
    }
  }
  if (sha256FileSync(paths[0]) !== sha256FileSync(paths[1])) {
    throw new Error(`Server/Worker ${SCRIPT_ABI_DLL} bytes differ or are missing; publish a fresh matched deployment`);
  }
}

function validateDist(distDir) {
  const dist = fs.realpathSync(path.resolve(distDir));
  const deploymentPath = path.join(dist, 'deployment.json');
  ensureRegularNonSymlinkFile(deploymentPath, 'deployment.json');
  const manifest = JSON.parse(fs.readFileSync(deploymentPath, 'utf8'));
  if (manifest.rid !== 'linux-x64' || manifest.selfContained !== true) {
    throw new Error('deployment.json is not a self-contained linux-x64 deployment');
  }
  if (!manifest.files || typeof manifest.files !== 'object' || Array.isArray(manifest.files)) {
    throw new Error('deployment.json is missing a valid files inventory');
  }
  for (const [name, expected] of Object.entries(manifest.files)) {
    const target = path.resolve(dist, name);
    if (!isInsideRoot(dist, target)) {
      throw new Error(`deployment integrity mismatch: ${name}`);
    }
    let info;
    try {
      info = fs.lstatSync(target);
    } catch {
      throw new Error(`deployment integrity mismatch: ${name}`);
    }
    if (info.isSymbolicLink() || !info.isFile() || sha256FileSync(target) !== expected) {
      throw new Error(`deployment integrity mismatch: ${name}`);
    }
  }
  const actual = inventorySync(dist);
  delete actual['deployment.json'];
  const actualKeys = Object.keys(actual);
  const expectedKeys = Object.keys(manifest.files);
  if (actualKeys.length !== expectedKeys.length || actualKeys.some((key) => actual[key] !== manifest.files[key])) {
    throw new Error('deployment contains extra or missing files; publish a fresh directory');
  }
  for (const [role, appName] of Object.entries(ROLES)) {
    const appPath = path.join(dist, role, appName);
    try {
      fs.accessSync(appPath, fs.constants.X_OK);
    } catch {
      throw new Error(`${role} apphost is not executable`);
    }
  }
  const runtimeDll = path.join(dist, 'worker', 'script-refs', 'net10.0', 'System.Runtime.dll');
  try {
    ensureRegularNonSymlinkFile(runtimeDll, 'System.Runtime.dll');
  } catch {
    throw new Error('complete Worker script-refs are required');
  }
  validateScriptAbi(dist);
  return manifest;
}

function validateDesktopDeployment(distDir, expectedElectron = '45.2.1') {
  const dist = fs.realpathSync(path.resolve(distDir));
  const deployment = validateDist(dist);
  const gpuManifestPath = path.join(dist, 'gpu-preview-manifest.json');
  ensureRegularNonSymlinkFile(gpuManifestPath, 'gpu-preview-manifest.json');
  const gpuManifest = JSON.parse(fs.readFileSync(gpuManifestPath, 'utf8'));
  const gpuManifestHash = sha256FileSync(gpuManifestPath);
  if (
    gpuManifest.abiVersion !== 1 ||
    gpuManifest.electron !== expectedElectron ||
    deployment.gpuPreview?.sha256 !== gpuManifestHash
  ) {
    throw new Error('Desktop native manifest mismatch');
  }
  if (!gpuManifest.files || typeof gpuManifest.files !== 'object' || Array.isArray(gpuManifest.files)) {
    throw new Error('Desktop native manifest files missing');
  }
  for (const [name, expected] of Object.entries(gpuManifest.files)) {
    const target = path.resolve(dist, name);
    if (!isInsideRoot(dist, target)) {
      throw new Error(`Desktop native payload escaped deployment: ${name}`);
    }
    let info;
    try {
      info = fs.lstatSync(target);
    } catch {
      throw new Error(`Desktop native payload missing: ${name}`);
    }
    if (info.isSymbolicLink() || !info.isFile() || sha256FileSync(target) !== expected) {
      throw new Error(`Desktop native payload mismatch: ${name}`);
    }
  }
  return { dist, deployment, gpuManifest };
}

function sanitizedBackendEnvironment(source = process.env) {
  const env = {};
  for (const [key, value] of Object.entries(source)) {
    if (value !== undefined && !key.startsWith('MAKO_BACKEND_')) {
      env[key] = value;
    }
  }
  if (source.MAKO_BACKEND_GPU_PREVIEW_BENCHMARK === '1') {
    env.MAKO_BACKEND_GPU_PREVIEW_BENCHMARK = '1';
  }
  return env;
}

function isExecutableFile(candidate) {
  if (!candidate || !path.isAbsolute(candidate)) return false;
  try {
    if (!fs.statSync(candidate).isFile()) return false;
    fs.accessSync(candidate, fs.constants.X_OK);
    return true;
  } catch {
    return false;
  }
}

function resolveExecutableTool(explicitPath, defaultName, envPath = process.env.PATH) {
  if (explicitPath !== undefined && explicitPath !== null) {
    return isExecutableFile(explicitPath) ? explicitPath : null;
  }
  const systemCandidate = path.join('/usr/bin', defaultName);
  if (isExecutableFile(systemCandidate)) return systemCandidate;
  for (const directory of String(envPath ?? '').split(path.delimiter)) {
    if (!path.isAbsolute(directory)) continue;
    const candidate = path.join(directory, defaultName);
    if (isExecutableFile(candidate)) return candidate;
  }
  return null;
}

function isValidOrigin(value) {
  try {
    const parsed = new URL(value);
    return (
      (parsed.protocol === 'http:' || parsed.protocol === 'https:') &&
      Boolean(parsed.hostname) &&
      !parsed.username &&
      !parsed.password &&
      parsed.pathname === '/' &&
      !parsed.search &&
      !parsed.hash &&
      parsed.origin === value
    );
  } catch {
    return false;
  }
}

function isLoopbackUrl(value) {
  try {
    const parsed = new URL(value);
    const host = parsed.hostname.toLowerCase();
    return (
      (parsed.protocol === 'http:' || parsed.protocol === 'https:') &&
      (host === 'localhost' || host === '127.0.0.1' || host === '::1' || host === '[::1]' || host.startsWith('127.'))
    );
  } catch {
    return false;
  }
}

async function writePrivateJsonAtomic(filePath, info) {
  await fsp.mkdir(path.dirname(filePath), { recursive: true, mode: 0o700 });
  const temp = `${filePath}.${process.pid}.tmp`;
  const handle = await fsp.open(temp, 'w', 0o600);
  try {
    await handle.writeFile(JSON.stringify(info, null, 2) + '\n');
  } finally {
    await handle.close();
  }
  await fsp.rename(temp, filePath);
}

async function startBackendProcess(options = {}) {
  if (process.platform !== 'linux' || process.arch !== 'x64') {
    throw new Error('This backend launcher is validated for Linux x64 only.');
  }
  const log = options.log ?? ((line) => console.log(line));
  const spawnFn = options.spawnProcess ?? childProcess.spawn;
  const dist = fs.realpathSync(path.resolve(options.dist));
  const manifest = validateDist(dist);
  const origins = options.origins ?? [];
  if (origins.length === 0 || origins.some((origin) => !isValidOrigin(origin))) {
    throw new Error('Origins must be explicit http(s) origins with no path or credentials.');
  }

  const requireMediaTools = options.requireMediaTools ?? false;
  const ffmpegPath = resolveExecutableTool(options.ffmpeg, 'ffmpeg');
  const ffprobePath = resolveExecutableTool(options.ffprobe, 'ffprobe');
  if (options.ffmpeg !== undefined && !ffmpegPath) {
    throw new Error(`Native tool must be an absolute executable file: ${options.ffmpeg}`);
  }
  if (options.ffprobe !== undefined && !ffprobePath) {
    throw new Error(`Native tool must be an absolute executable file: ${options.ffprobe}`);
  }
  if (requireMediaTools && (!ffmpegPath || !ffprobePath)) {
    throw new Error('Cannot locate executable ffmpeg/ffprobe in /usr/bin or PATH.');
  }
  if (!ffmpegPath || !ffprobePath) {
    log('[backend] 警告：系统 PATH 中未同时找到 ffmpeg/ffprobe，音频准备、分析与导出能力将停用。');
  }

  const profile = path.resolve(options.profile);
  await fsp.mkdir(profile, { recursive: true, mode: 0o700 });
  for (const name of ['state', 'projects', 'runs']) {
    await fsp.mkdir(path.join(profile, name), { recursive: true, mode: 0o700 });
  }
  const runDir = await fsp.mkdtemp(path.join(profile, 'runs', 'run-'));
  await fsp.chmod(runDir, 0o700);

  const credentialFile = path.join(runDir, 'bootstrap.json');
  if (options.bootstrapFile) {
    await fsp.rm(path.resolve(options.bootstrapFile), { force: true });
  }

  const env = sanitizedBackendEnvironment(process.env);
  if (options.env) {
    for (const [key, value] of Object.entries(options.env)) {
      if (value !== undefined && !key.startsWith('MAKO_BACKEND_')) {
        env[key] = value;
      }
    }
  }
  Object.assign(env, {
    MAKO_BACKEND_RESOURCE_ROOT: path.join(runDir, 'resources'),
    MAKO_BACKEND_CREDENTIAL_FILE: credentialFile,
    MAKO_BACKEND_STATE_ROOT: path.join(profile, 'state'),
    MAKO_BACKEND_ALLOWED_ROOTS: JSON.stringify({ projects: path.join(profile, 'projects') }),
    MAKO_BACKEND_ALLOWED_ORIGINS: origins.join(';'),
    MAKO_BACKEND_WORKER_PATH: path.join(dist, 'worker', ROLES.worker),
  });
  if (ffmpegPath) env.MAKO_BACKEND_FFMPEG_PATH = ffmpegPath;
  if (ffprobePath) env.MAKO_BACKEND_FFPROBE_PATH = ffprobePath;

  const serverExe = path.join(dist, 'server', ROLES.server);
  const serverArgs = ['--urls', 'http://127.0.0.1:0'];
  const launch = options.ordinaryScheduling
    ? require('../gpu-preview/ordinary-exec.cjs').ordinaryCommand(serverExe, serverArgs)
    : { executable: serverExe, args: serverArgs };

  log(`[backend] 部署 ${dist}（${manifest.version}）`);
  log(`[backend] profile ${profile}`);

  const child = spawnFn(launch.executable, launch.args, {
    cwd: options.cwd ?? dist,
    stdio: ['ignore', 'pipe', 'pipe'],
    detached: true,
    env,
  });

  const serverLogStream = fs.createWriteStream(path.join(runDir, 'server.log'), { flags: 'a', mode: 0o600 });
  const writeLogLine = (line) => {
    try {
      serverLogStream.write(line + '\n');
    } catch { /* ignore log stream write failures during shutdown */ }
  };

  let exitCode = null;
  let spawnError;
  const exited = new Promise((resolveExit) => {
    child.once('error', (error) => {
      spawnError = error;
      exitCode = 1;
      serverLogStream.end();
      resolveExit(1);
    });
    child.on('exit', (code, signal) => {
      exitCode = code ?? (signal ? 128 : null);
      serverLogStream.end();
      resolveExit(exitCode);
    });
  });

  if (child.stderr) {
    child.stderr.on('data', (chunk) => {
      for (const line of String(chunk).split('\n')) {
        if (!line) continue;
        writeLogLine(line);
        log(`[backend:stderr] ${line}`);
      }
    });
  }

  const readyTimeoutMs = options.readyTimeoutMs ?? DEFAULT_READY_TIMEOUT_MS;
  const stopTimeoutMs = options.stopTimeoutMs ?? DEFAULT_STOP_TIMEOUT_MS;

  try {
    const readyLine = await new Promise((resolveReady, rejectReady) => {
      const timer = setTimeout(() => {
        rejectReady(new Error(`后端在 ${readyTimeoutMs / 1000} 秒内没有就绪。`));
      }, readyTimeoutMs);
      const lines = readline.createInterface({ input: child.stdout });
      lines.on('line', (line) => {
        writeLogLine(line);
        log(`[backend] ${line}`);
        if (line.startsWith('BACKEND_READY ')) {
          clearTimeout(timer);
          lines.removeAllListeners('line');
          const rest = line.slice('BACKEND_READY '.length).trim();
          const parts = rest.split(/\s+/);
          resolveReady({ address: parts[0], hostInstanceId: parts[1] });
        }
      });
      exited.then((code) => {
        clearTimeout(timer);
        lines.close();
        rejectReady(spawnError ?? new Error(`后端启动器提前退出（${code}）；日志见 ${path.join(runDir, 'server.log')}。`));
      });
    });

    const mode = fs.statSync(credentialFile).mode & 0o777;
    if (mode !== 0o600) {
      throw new Error(`bootstrap 凭证文件权限应为 0600，实际 ${mode.toString(8)}。`);
    }
    const credential = JSON.parse(fs.readFileSync(credentialFile, 'utf8'));
    const addresses = Array.isArray(credential.addresses) ? credential.addresses : [];
    if (addresses.length === 0 || addresses.some((addr) => !isLoopbackUrl(addr))) {
      throw new Error('backend did not bind the requested loopback address');
    }
    if (typeof credential.bootstrapToken !== 'string' || !credential.bootstrapToken) {
      throw new Error('bootstrap 凭证文件内容无效。');
    }
    const hostInstanceId = credential.hostInstanceId ?? readyLine.hostInstanceId;
    const ready = {
      addresses,
      hostInstanceId,
      credentialFile,
      runDirectory: runDir,
      version: manifest.version,
      pid: child.pid,
    };
    const info = {
      baseUrl: addresses[0],
      hostInstanceId,
      bootstrapToken: credential.bootstrapToken,
      version: manifest.version,
      pid: child.pid,
      runDirectory: runDir,
      writtenAtUtc: new Date().toISOString(),
    };

    const bootstrapFile = options.bootstrapFile ? path.resolve(options.bootstrapFile) : null;
    if (bootstrapFile) {
      await writePrivateJsonAtomic(bootstrapFile, info);
      log(`[backend] 就绪：${info.baseUrl} host=${info.hostInstanceId}；启动信息已写入 ${bootstrapFile}（0600）`);
    } else {
      log(`[backend] 就绪：${info.baseUrl} host=${info.hostInstanceId}`);
    }

    let stopping = null;
    const stop = () => {
      stopping ??= (async () => {
        if (exitCode === null && child.pid) {
          try {
            process.kill(child.pid, 'SIGTERM');
          } catch (error) {
            if (error.code !== 'ESRCH') throw error;
          }
          let stopTimer;
          const timeout = new Promise((resolveTimeout) => {
            stopTimer = setTimeout(() => resolveTimeout('timeout'), stopTimeoutMs);
          });
          try {
            if ((await Promise.race([exited, timeout])) === 'timeout') {
              log(`[backend] 等待退出超时，仍在运行的后端 PID: ${child.pid}`);
            }
          } finally {
            clearTimeout(stopTimer);
          }
        }
        await fsp.rm(credentialFile, { force: true }).catch(() => {});
        if (bootstrapFile) await fsp.rm(bootstrapFile, { force: true }).catch(() => {});
        return exitCode;
      })();
      return stopping;
    };

    exited.then(() => {
      void fsp.rm(credentialFile, { force: true }).catch(() => {});
      if (bootstrapFile) void fsp.rm(bootstrapFile, { force: true }).catch(() => {});
    });

    return { info, ready, stop, exited };
  } catch (error) {
    if (exitCode === null && child.pid) {
      try {
        process.kill(child.pid, 'SIGTERM');
      } catch (killError) {
        if (killError.code !== 'ESRCH') throw killError;
      }
      await exited;
    }
    await fsp.rm(credentialFile, { force: true }).catch(() => {});
    if (options.bootstrapFile) {
      await fsp.rm(path.resolve(options.bootstrapFile), { force: true }).catch(() => {});
    }
    throw error;
  }
}

module.exports = {
  EXPECTED_PRODUCT_VERSION,
  ROLES,
  compatibleProductVersion,
  isLoopbackUrl,
  isValidOrigin,
  resolveExecutableTool,
  sanitizedBackendEnvironment,
  sha256FileSync,
  startBackendProcess,
  validateDesktopDeployment,
  validateDist,
  validateScriptAbi,
  writePrivateJsonAtomic,
};
