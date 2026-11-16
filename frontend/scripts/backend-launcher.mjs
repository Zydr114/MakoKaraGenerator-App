// 启动真实 Backend.Server 的可复用部分，供 dev-backend.mjs、app.mjs 与集成测试共用。
//
// App 使用本仓库 build 发布的 self-contained Server＋Worker，独立运行音频准备。
// 直接由 Node.js 校验部署清单与 ABI，准备受控 profile 环境并启动 Server（无 Python 依赖），
// 解析其 stdout 的 BACKEND_READY 后读取私有 0600 凭证文件。凭证不打印到日志或 URL。

import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import { existsSync, readFileSync, realpathSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const backendHost = require('../../desktop/runtime/backend-host.cjs');

export const PROJECT_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
export const DEV_DIR = join(PROJECT_ROOT, '.dev');
export const DEFAULT_BOOTSTRAP_FILE = join(DEV_DIR, 'backend.json');
export const DEFAULT_PROFILE = join(DEV_DIR, 'backend-profile');
export const DEV_ORIGINS = ['http://localhost:5173', 'http://127.0.0.1:5173'];

/** 本项目固定消费的后端合同版本（docs/backend/contract/contract-manifest.json）。 */
export const EXPECTED_PRODUCT_VERSION = backendHost.EXPECTED_PRODUCT_VERSION;

/**
 * 部署版本能否运行：与合同产品版本同一 MAJOR.MINOR，且补丁号不低于合同。
 * 补丁版本只修程序、不改 HTTP 合同，所以更新的补丁可以放行（启动时警告）；更旧或次版本不同的一律拒绝。
 */
export function compatibleProductVersion(version, expected = EXPECTED_PRODUCT_VERSION) {
  return backendHost.compatibleProductVersion(version, expected);
}

export function resolveBackendRepo() {
  const local = resolve(PROJECT_ROOT, '../backend');
  if (!backendHost.isSamePath(realpathSync(local), local)) throw new Error('App backend 不能通过符号链接引用外部源码。');
  if (process.env.MAKO_BACKEND_REPO && !backendHost.isSamePath(resolve(process.env.MAKO_BACKEND_REPO), local)) {
    throw new Error('App 只允许使用本仓库 backend；请移除外部 MAKO_BACKEND_REPO。');
  }
  return local;
}

/** App 只运行根 build 生成的本地匹配 Server＋Worker，禁止回退兄弟仓库 artifacts。 */
export function resolveDist(explicit) {
  const rawLocalRoot = resolve(PROJECT_ROOT, '../.local/backend-dist');
  const localRoot = existsSync(rawLocalRoot) ? realpathSync(rawLocalRoot) : rawLocalRoot;
  const pointer = resolve(PROJECT_ROOT, '../.local/backend-current.json');
  let selected = explicit ?? process.env.MAKO_BACKEND_DIST;
  if (!selected && existsSync(pointer)) selected = JSON.parse(readFileSync(pointer, 'utf8')).dist;
  if (!selected) throw new Error('App 尚无后端构建；请在 App 根执行 pnpm build。');
  const dist = realpathSync(resolve(selected));
  if (!backendHost.isInsideRoot(localRoot, dist)) throw new Error('App 后端部署必须位于本仓库 .local/backend-dist/。');
  if (!existsSync(join(dist, 'deployment.json'))) throw new Error(`后端部署不完整：${dist}`);
  const manifest = JSON.parse(readFileSync(join(dist, 'deployment.json'), 'utf8'));
  if (!compatibleProductVersion(manifest.version)) {
    throw new Error(`App 后端部署与固定合同产品版本不一致：部署 ${manifest.version}，合同 ${EXPECTED_PRODUCT_VERSION}（只接受同一次版本、补丁号不低于合同的部署）。`);
  }
  return { dist, version: manifest.version };
}

/** 以 0600 原子写入启动信息；先写临时文件再改名，读取方不会看到半个文件。 */
export async function writeBootstrapFile(file, info) {
  return backendHost.writePrivateJsonAtomic(file, info);
}

/**
 * 启动后端并等待就绪。
 * @param {{ profile?: string, bootstrapFile?: string, dist?: string, origins?: string[],
 *           log?: (line: string) => void, env?: Record<string,string>, ordinaryScheduling?: boolean }} options
 * @returns {Promise<{ info: { baseUrl: string, hostInstanceId: string, bootstrapToken: string },
 *                     ready: object, stop: () => Promise<number|null>, exited: Promise<number|null> }>}
 */
export async function startBackend(options = {}) {
  const log = options.log ?? ((line) => console.log(line));
  const repo = resolveBackendRepo();
  const { dist, version } = resolveDist(options.dist);
  if (version !== EXPECTED_PRODUCT_VERSION) {
    log(`[backend] 警告：部署版本 ${version} 高于本项目固定的合同产品版本 ${EXPECTED_PRODUCT_VERSION}，按补丁兼容放行；合同尚未重新冻结。`);
  }
  const profile = resolve(options.profile ?? DEFAULT_PROFILE);
  const bootstrapFile = resolve(options.bootstrapFile ?? DEFAULT_BOOTSTRAP_FILE);
  return backendHost.startBackendProcess({
    dist,
    profile,
    bootstrapFile,
    origins: options.origins ?? DEV_ORIGINS,
    log,
    env: options.env,
    ordinaryScheduling: options.ordinaryScheduling,
    cwd: existsSync(repo) ? repo : dist,
    spawnProcess: spawn,
  });
}
