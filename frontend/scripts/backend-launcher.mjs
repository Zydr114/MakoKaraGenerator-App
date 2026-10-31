// 启动真实 Backend.Server 的可复用部分，供 dev-backend.mjs 与集成测试共用。
//
// 不直接运行 dotnet：本机没有共享 ASP.NET runtime，且新建工程需要匹配的 Worker 做音频准备。
// 这里调用后端仓库交付的稳定启动器 tools/backend-web-host/host.py run（self-contained Server＋完整 Worker），
// 解析其 stdout 的 WEB_BACKEND_READY，再由本进程读取私有 bootstrap 文件。凭证只写入 0600 的输出文件，不打印。
// 设计见 docs/web-client-w0-design.md §4.1。

import { spawn } from 'node:child_process';
import { existsSync, readFileSync, statSync } from 'node:fs';
import { mkdir, open, rename, rm } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';

export const PROJECT_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
export const DEV_DIR = join(PROJECT_ROOT, '.dev');
export const DEFAULT_BOOTSTRAP_FILE = join(DEV_DIR, 'backend.json');
export const DEFAULT_PROFILE = join(DEV_DIR, 'backend-profile');
export const DEV_ORIGINS = ['http://localhost:5173', 'http://127.0.0.1:5173'];

/** 本项目固定消费的后端合同版本（docs/backend/contract/contract-manifest.json）。 */
export const EXPECTED_PRODUCT_VERSION = '0.108.0';

const READY_TIMEOUT_MS = 120_000;
const STOP_TIMEOUT_MS = 120_000;

export function resolveBackendRepo() {
  return resolve(PROJECT_ROOT, process.env.MAKO_BACKEND_REPO ?? '../MakoKaraGenerator-Backend');
}

/**
 * 找到匹配发布的 Server＋Worker 目录。优先 MAKO_BACKEND_DIST，其次 W8 匹配发布和后端交接文档推荐的发布目录，
 * 最后是本仓库 .dev/backend-dist/<版本>：后端仓库还在继续提交时，前端从交付提交的独立克隆发布到这里，
 * 不往后端仓库写东西（docs/backend/README.md 记录来源提交）。
 */
export function resolveDist(explicit) {
  const repo = resolveBackendRepo();
  const candidates = explicit
    ? [resolve(explicit)]
    : process.env.MAKO_BACKEND_DIST
      ? [resolve(process.env.MAKO_BACKEND_DIST)]
      : [
          join(repo, 'artifacts/w8-authoring-20261030/dist-final1'),
          join(repo, 'artifacts/web-backend', EXPECTED_PRODUCT_VERSION),
          join(DEV_DIR, 'backend-dist', EXPECTED_PRODUCT_VERSION),
        ];
  for (const dir of candidates) {
    if (existsSync(join(dir, 'deployment.json'))) {
      const manifest = JSON.parse(readFileSync(join(dir, 'deployment.json'), 'utf8'));
      if (!explicit && !process.env.MAKO_BACKEND_DIST && manifest.version !== EXPECTED_PRODUCT_VERSION) continue;
      return { dist: dir, version: manifest.version };
    }
  }
  throw new Error(
    `找不到后端部署（deployment.json）。已检查：\n  ${candidates.join('\n  ')}\n` +
      '请在后端仓库执行 python3 tools/backend-web-host/host.py publish --dist <新目录>，' +
      '然后设置 MAKO_BACKEND_DIST 指向该目录。',
  );
}

/** 以 0600 原子写入启动信息；先写临时文件再改名，读取方不会看到半个文件。 */
export async function writeBootstrapFile(file, info) {
  await mkdir(dirname(file), { recursive: true, mode: 0o700 });
  const temp = `${file}.${process.pid}.tmp`;
  const handle = await open(temp, 'w', 0o600);
  try {
    await handle.writeFile(JSON.stringify(info, null, 2) + '\n');
  } finally {
    await handle.close();
  }
  await rename(temp, file);
}

/**
 * 启动后端并等待就绪。
 * @param {{ profile?: string, bootstrapFile?: string, dist?: string, origins?: string[],
 *           log?: (line: string) => void }} options
 * @returns {Promise<{ info: { baseUrl: string, hostInstanceId: string, bootstrapToken: string },
 *                     ready: object, stop: () => Promise<number|null>, exited: Promise<number|null> }>}
 */
export async function startBackend(options = {}) {
  const log = options.log ?? ((line) => console.log(line));
  const repo = resolveBackendRepo();
  const hostScript = join(repo, 'tools/backend-web-host/host.py');
  if (!existsSync(hostScript)) {
    throw new Error(`找不到后端启动器 ${hostScript}；用 MAKO_BACKEND_REPO 指定后端仓库。`);
  }
  const { dist, version } = resolveDist(options.dist);
  if (version !== EXPECTED_PRODUCT_VERSION) {
    log(`[backend] 警告：部署版本 ${version} 与本项目固定的合同产品版本 ${EXPECTED_PRODUCT_VERSION} 不一致。`);
  }
  const profile = resolve(options.profile ?? DEFAULT_PROFILE);
  const bootstrapFile = resolve(options.bootstrapFile ?? DEFAULT_BOOTSTRAP_FILE);
  await mkdir(profile, { recursive: true, mode: 0o700 });
  // 上一次异常退出可能遗留旧凭证文件；它指向已经不存在的进程，先删除。
  await rm(bootstrapFile, { force: true });

  const args = [hostScript, 'run', '--dist', dist, '--profile', profile];
  for (const origin of options.origins ?? DEV_ORIGINS) args.push('--origin', origin);
  log(`[backend] 部署 ${dist}（${version}）`);
  log(`[backend] profile ${profile}`);

  // 独立进程组：终端 Ctrl+C 只到达本进程，由本进程向启动器转发恰好一次 SIGTERM。
  const child = spawn('python3', args, {
    cwd: repo,
    stdio: ['ignore', 'pipe', 'pipe'],
    detached: true,
    env: { ...process.env, PYTHONUNBUFFERED: '1' },
  });

  let exitCode = null;
  const exited = new Promise((resolveExit) => {
    child.on('exit', (code, signal) => {
      exitCode = code ?? (signal ? 128 : null);
      resolveExit(exitCode);
    });
  });
  child.stderr.on('data', (chunk) => {
    for (const line of String(chunk).split('\n')) if (line) log(`[backend:stderr] ${line}`);
  });

  const ready = await new Promise((resolveReady, rejectReady) => {
    const timer = setTimeout(() => {
      rejectReady(new Error(`后端在 ${READY_TIMEOUT_MS / 1000} 秒内没有就绪。`));
    }, READY_TIMEOUT_MS);
    const lines = createInterface({ input: child.stdout });
    lines.on('line', (line) => {
      // host.py 的 stdout 只含地址、hostInstanceId、路径与 pid，不含凭证。
      if (line.startsWith('WEB_BACKEND_READY ')) {
        clearTimeout(timer);
        try {
          resolveReady(JSON.parse(line.slice('WEB_BACKEND_READY '.length)));
        } catch (error) {
          rejectReady(error);
        }
      }
      log(`[backend] ${line}`);
    });
    exited.then((code) => {
      clearTimeout(timer);
      rejectReady(new Error(`后端启动器提前退出（${code}）；日志见 profile/runs/*/server.log。`));
    });
  }).catch(async (error) => {
    if (exitCode === null) {
      process.kill(child.pid, 'SIGTERM');
      await exited;
    }
    throw error;
  });

  const credentialFile = ready.credentialFile;
  const mode = statSync(credentialFile).mode & 0o777;
  if (mode !== 0o600) throw new Error(`bootstrap 凭证文件权限应为 0600，实际 ${mode.toString(8)}。`);
  const credential = JSON.parse(readFileSync(credentialFile, 'utf8'));
  const info = {
    baseUrl: ready.addresses[0],
    hostInstanceId: ready.hostInstanceId,
    bootstrapToken: credential.bootstrapToken,
    version: ready.version,
    pid: ready.pid,
    runDirectory: ready.runDirectory,
    writtenAtUtc: new Date().toISOString(),
  };
  await writeBootstrapFile(bootstrapFile, info);
  log(`[backend] 就绪：${info.baseUrl} host=${info.hostInstanceId}；启动信息已写入 ${bootstrapFile}（0600）`);

  let stopping = null;
  const stop = () => {
    stopping ??= (async () => {
      if (exitCode === null) {
        // 只向自己启动的启动器 PID 发信号；启动器再转发给 Server 并等待排水。不按进程名杀进程。
        process.kill(child.pid, 'SIGTERM');
        const timeout = new Promise((r) => setTimeout(() => r('timeout'), STOP_TIMEOUT_MS));
        if ((await Promise.race([exited, timeout])) === 'timeout') {
          log('[backend] 等待退出超时，仍在运行的启动器 PID: ' + child.pid);
        }
      }
      await rm(bootstrapFile, { force: true });
      return exitCode;
    })();
    return stopping;
  };
  exited.then(() => rm(bootstrapFile, { force: true }));

  return { info, ready, stop, exited };
}
