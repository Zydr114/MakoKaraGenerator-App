// 构建是否过期：按源码文件内容的哈希比较，不看修改时间（切换提交、复制目录都会改时间）。
// Web 构建在 .local/web-build.json 里记下当时的输入；后端发布与原生预览库的输入记在各自的部署清单里。
import { execFile } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { promisify } from 'node:util';
import { resolveDist, writeBootstrapFile } from '../frontend/scripts/backend-launcher.mjs';
import { nativeSources, verifyDesktopDeployment } from './desktop-build.mjs';

const execute = promisify(execFile);
const hash = async (file) => createHash('sha256').update(await readFile(file)).digest('hex');

async function trackedInputs(cwd, paths) {
  const { stdout } = await execute('git', ['ls-files', '-co', '--exclude-standard', '--', ...paths], { cwd, maxBuffer: 16 * 1024 * 1024 });
  const result = {};
  for (const file of [...new Set(stdout.split('\n').filter(Boolean))].sort()) {
    if (existsSync(join(cwd, file))) result[file] = await hash(join(cwd, file));
  }
  return result;
}

/** Web 构建（frontend/dist）的输入。 */
export function webInputs(root) {
  return trackedInputs(join(root, 'frontend'), ['src', 'assets', 'index.html', 'console.html', 'vite.config.ts', 'scripts/vite-bootstrap-plugin.ts', 'tsconfig.json', 'package.json', 'pnpm-lock.yaml']);
}

/** 后端发布的输入，规则同 backend/tools/backend-web-host/host.py 的 source_inputs。 */
export function backendInputs(root) {
  const paths = [
    'src', 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props',
    'global.json', 'NuGet.Config', 'NuGet.config',
    'build/MakoMediaPresentation.Native.targets', 'native/media-presentation',
  ];
  return trackedInputs(join(root, 'backend'), paths);
}

const WEB_STAMP = (root) => join(root, '.local/web-build.json');
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);

/** 构建成功后记下构建开始前的输入；构建期间源码变了就不记，下次启动会重建。 */
export async function recordWebBuild(root, inputs) {
  if (!same(inputs, await webInputs(root))) {
    console.warn('[app] 构建期间前端源码有改动，没有记录这次 Web 构建；下次启动桌面版会重新构建。');
    return;
  }
  await writeBootstrapFile(WEB_STAMP(root), { schemaVersion: 1, builtAt: new Date().toISOString(), inputs });
}

async function webReason(root) {
  if (!existsSync(join(root, 'frontend/dist/index.html'))) return '还没有 Web 构建';
  let stamp;
  try {
    stamp = JSON.parse(await readFile(WEB_STAMP(root), 'utf8'));
  } catch {
    return '没有 Web 构建记录';
  }
  return same(stamp.inputs, await webInputs(root)) ? null : '前端源码有改动';
}

/**
 * 桌面版启动前检查：deployment 不为 null 时要重新构建整个桌面版（原生库、Web、后端发布），
 * 只有 web 不为 null 时只重建 Web。返回值里的文字说明原因；通过时带上已经校验过的部署。
 */
export async function desktopBuildState(root) {
  const web = await webReason(root);
  if (!existsSync(join(root, 'desktop/node_modules/electron/index.js'))) return { web, deployment: '还没有安装桌面运行时（Electron）' };
  let dist;
  try {
    dist = resolveDist().dist;
  } catch {
    return { web, deployment: '还没有桌面版构建' };
  }
  let manifest;
  try {
    manifest = await verifyDesktopDeployment(root, dist);
  } catch {
    return { web, deployment: '当前构建不是可用的桌面版（缺少 GPU 预览原生库或校验不通过）' };
  }
  const deployment = JSON.parse(await readFile(join(dist, 'deployment.json'), 'utf8'));
  if (!same(deployment.sourceInputs, await backendInputs(root))) return { web, deployment: '后端源码有改动' };
  if (!same(manifest.sourceInputs, await nativeSources(root))) return { web, deployment: 'GPU 预览原生源码有改动' };
  return { web, deployment: null, dist, manifest };
}
