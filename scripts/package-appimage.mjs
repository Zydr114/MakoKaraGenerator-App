import { execFileSync } from 'node:child_process';
import {
  chmodSync,
  copyFileSync,
  existsSync,
  lstatSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  readlinkSync,
  rmSync,
  statSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { createRequire } from 'node:module';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { desktopBuildState } from './build-state.mjs';

const require = createRequire(import.meta.url);
const backendHost = require('../desktop/runtime/backend-host.cjs');

const ROOT = resolve(import.meta.dirname, '..');
const ELECTRON_VERSION = '45.2.1';
const APP_ID = 'makokara-generator';
const APP_NAME = 'MakoKara Generator';

/** 递归复制目录：后端和前端产物禁止符号链接，Electron 运行时目录保留原有符号链接。 */
function copyDirectory(srcDir, dstDir, { allowSymlinks = false, filter } = {}) {
  mkdirSync(dstDir, { recursive: true, mode: 0o755 });
  chmodSync(dstDir, 0o755);
  for (const entry of readdirSync(srcDir, { withFileTypes: true })) {
    const srcPath = join(srcDir, entry.name);
    const dstPath = join(dstDir, entry.name);
    if (filter && !filter(entry.name, srcPath)) continue;
    const stat = lstatSync(srcPath);
    if (stat.isSymbolicLink()) {
      if (!allowSymlinks) {
        throw new Error(`打包输入包含不允许的符号链接：${srcPath}`);
      }
      symlinkSync(readlinkSync(srcPath), dstPath);
    } else if (stat.isDirectory()) {
      copyDirectory(srcPath, dstPath, { allowSymlinks, filter });
    } else if (stat.isFile()) {
      copyFileSync(srcPath, dstPath);
      chmodSync(dstPath, stat.mode & 0o100 ? 0o755 : 0o644);
    }
  }
}

function generateAppIcons(appDir, appResourcesDir) {
  const logoSource = join(ROOT, 'frontend/assets/logo/logo-icon.png');
  const icon128Source = join(ROOT, 'desktop/assets/icon-128.png');
  const ffmpegBin = backendHost.resolveExecutableTool('ffmpeg');
  const hicolor128 = join(appDir, 'usr/share/icons/hicolor/128x128/apps');
  const hicolor256 = join(appDir, 'usr/share/icons/hicolor/256x256/apps');
  const assetsDir = join(appResourcesDir, 'assets');
  mkdirSync(hicolor128, { recursive: true });
  mkdirSync(hicolor256, { recursive: true });
  mkdirSync(assetsDir, { recursive: true });

  copyFileSync(icon128Source, join(hicolor128, `${APP_ID}.png`));
  copyFileSync(icon128Source, join(assetsDir, 'icon-128.png'));
  copyFileSync(join(ROOT, 'desktop/assets/logo-wide.webp'), join(assetsDir, 'logo-wide.webp'));
  copyFileSync(join(ROOT, 'desktop/assets/splash-bg.webp'), join(assetsDir, 'splash-bg.webp'));

  const icon256Path = join(hicolor256, `${APP_ID}.png`);
  if (ffmpegBin && existsSync(logoSource)) {
    execFileSync(
      ffmpegBin,
      ['-y', '-loglevel', 'error', '-i', logoSource, '-vf', 'scale=256:256:flags=lanczos', '-frames:v', '1', icon256Path],
      { stdio: 'inherit', timeout: 30_000 },
    );
  } else {
    copyFileSync(icon128Source, icon256Path);
  }
  copyFileSync(icon256Path, join(appDir, `${APP_ID}.png`));
  copyFileSync(icon256Path, join(appDir, '.DirIcon'));
  copyFileSync(icon256Path, join(assetsDir, 'icon.png'));
}

function writeDesktopMetadata(appDir) {
  const desktopFile = join(appDir, `${APP_ID}.desktop`);
  const content = [
    '[Desktop Entry]',
    'Type=Application',
    `Name=${APP_NAME}`,
    'GenericName=Karaoke Generator',
    'Comment=Karaoke lyric synchronization, visual styling, and video generator',
    `Exec=${APP_ID} %U`,
    `Icon=${APP_ID}`,
    'Terminal=false',
    'Categories=AudioVideo;Audio;Video;',
    `StartupWMClass=${APP_ID}`,
    'StartupNotify=true',
    '',
  ].join('\n');
  writeFileSync(desktopFile, content, 'utf8');

  const appsDir = join(appDir, 'usr/share/applications');
  mkdirSync(appsDir, { recursive: true });
  writeFileSync(join(appsDir, `${APP_ID}.desktop`), content, 'utf8');

  const validator = backendHost.resolveExecutableTool('desktop-file-validate');
  if (validator) {
    execFileSync(validator, [desktopFile], { stdio: 'inherit', timeout: 15_000 });
  }

  const appRunPath = join(appDir, 'AppRun');
  // Do NOT add Electron's directory to LD_LIBRARY_PATH, so the backend GPU Worker
  // loads the system libvulkan.so.1 instead of Electron's bundled libvulkan.so.1.
  const appRun = [
    '#!/usr/bin/env sh',
    'set -eu',
    'SELF_DIR="${APPDIR:-$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)}"',
    `exec "$SELF_DIR/usr/lib/${APP_ID}/electron" "$@"`,
    '',
  ].join('\n');
  writeFileSync(appRunPath, appRun, { mode: 0o755 });
  chmodSync(appRunPath, 0o755);
}

function stageAppResources(appLibDir, frontendDist, backendDist, productVersion) {
  const resourcesDir = join(appLibDir, 'resources');
  rmSync(join(resourcesDir, 'default_app.asar'), { force: true });

  const appResourcesDir = join(resourcesDir, 'app');
  mkdirSync(join(appResourcesDir, 'runtime'), { recursive: true });
  mkdirSync(join(appResourcesDir, 'gpu-preview'), { recursive: true });
  mkdirSync(join(appResourcesDir, 'splash'), { recursive: true });

  writeFileSync(
    join(appResourcesDir, 'package.json'),
    JSON.stringify(
      {
        name: APP_ID,
        productName: APP_NAME,
        version: productVersion,
        private: true,
        main: 'main.cjs',
      },
      null,
      2,
    ) + '\n',
    'utf8',
  );
  copyFileSync(join(ROOT, 'backend/web/contract-manifest.json'), join(appResourcesDir, 'contract-manifest.json'));
  copyFileSync(join(ROOT, 'desktop/main.cjs'), join(appResourcesDir, 'main.cjs'));
  copyDirectory(join(ROOT, 'desktop/runtime'), join(appResourcesDir, 'runtime'), {
    filter: (name) => name.endsWith('.cjs') && !name.endsWith('.test.cjs'),
  });
  copyDirectory(join(ROOT, 'desktop/gpu-preview'), join(appResourcesDir, 'gpu-preview'), {
    filter: (name) => name.endsWith('.cjs') && !name.endsWith('.test.cjs') && name !== 'ordinary-exec.cjs',
  });
  copyDirectory(join(ROOT, 'desktop/splash'), join(appResourcesDir, 'splash'), {
    filter: (name) => /\.(?:cjs|html|js)$/i.test(name) && !name.endsWith('.test.cjs'),
  });

  copyDirectory(frontendDist, join(resourcesDir, 'frontend-dist'), {
    allowSymlinks: false,
    filter: (name, srcPath) => {
      if (/(?:^|[/\\])(?:kuromoji[/\\]dict|ipadic)|\.dat\.gz$/i.test(srcPath) || /\.dat\.gz$/i.test(name)) {
        throw new Error(`AppImage 前端产物违规包含 IPADIC/词典资产：${srcPath}`);
      }
      return true;
    },
  });
  copyDirectory(backendDist, join(resourcesDir, 'backend-dist'), { allowSymlinks: false });
  backendHost.validateDesktopDeployment(join(resourcesDir, 'backend-dist'), ELECTRON_VERSION);
  return appResourcesDir;
}

function buildAppImage(appImageTool, appDir, outputPath) {
  rmSync(outputPath, { force: true });
  const env = { ...process.env, ARCH: 'x86_64' };
  const args = ['--no-appstream', appDir, outputPath];
  try {
    execFileSync(appImageTool, args, { env, stdio: 'inherit', timeout: 90_000 });
  } catch (error) {
    // Fallback to extract-and-run if FUSE cannot mount appimagetool inside an environment.
    execFileSync(appImageTool, ['--appimage-extract-and-run', ...args], { env, stdio: 'inherit', timeout: 90_000 });
  }
  chmodSync(outputPath, 0o755);
}

async function main() {
  const { values } = parseArgs({
    options: {
      build: { type: 'boolean', default: false },
      'keep-appdir': { type: 'boolean', default: false },
      out: { type: 'string' },
      tool: { type: 'string' },
    },
  });

  if (process.platform !== 'linux' || process.arch !== 'x64') {
    throw new Error(`当前仅支持打包 linux/x64 AppImage（当前为 ${process.platform}/${process.arch}）。`);
  }

  let state = await desktopBuildState(ROOT);
  if ((state.web !== null || state.deployment !== null) && values.build) {
    execFileSync(process.execPath, [join(ROOT, 'scripts/app.mjs'), 'build:desktop'], {
      cwd: ROOT,
      stdio: 'inherit',
    });
    state = await desktopBuildState(ROOT);
  }
  if (state.web !== null || state.deployment !== null) {
    throw new Error(
      `构建产物尚未就绪或已过期（web=${state.web ?? 'ok'}，deployment=${state.deployment ?? 'ok'}）。请先执行 pnpm run build:desktop，或加 --build。`,
    );
  }

  const electronDist = join(ROOT, 'desktop/node_modules/electron/dist');
  if (!existsSync(join(electronDist, 'electron'))) {
    throw new Error(`缺少 Electron x64 运行时：${electronDist}`);
  }

  const appImageTool = resolve(
    values.tool ?? process.env.APPIMAGETOOL ?? join(ROOT, '.local/tools/appimagetool-x86_64.AppImage'),
  );
  if (!existsSync(appImageTool)) {
    throw new Error(`找不到 appimagetool：${appImageTool}`);
  }

  const contractManifest = JSON.parse(readFileSync(join(ROOT, 'backend/web/contract-manifest.json'), 'utf8'));
  const productVersion = contractManifest.productVersion;
  const validated = backendHost.validateDesktopDeployment(state.dist, ELECTRON_VERSION);
  if (!backendHost.compatibleProductVersion(validated.deployment.version, productVersion)) {
    throw new Error(`部署版本 ${validated.deployment.version} 与契约产品版本 ${productVersion} 不兼容。`);
  }

  const releaseRoot = join(ROOT, '.local/release');
  const appDir = join(releaseRoot, 'AppDir-linux-x64');
  const outputPath = resolve(values.out ?? join(releaseRoot, `MakoKaraGenerator-${productVersion}-x86_64.AppImage`));
  mkdirSync(releaseRoot, { recursive: true });
  rmSync(appDir, { recursive: true, force: true });

  const appLibDir = join(appDir, 'usr/lib', APP_ID);
  console.log(`[package:appimage] 正在组装 AppDir（版本 ${productVersion}）...`);
  copyDirectory(electronDist, appLibDir, { allowSymlinks: true });
  const appResourcesDir = stageAppResources(appLibDir, join(ROOT, 'frontend/dist'), validated.dist, productVersion);
  generateAppIcons(appDir, appResourcesDir);
  writeDesktopMetadata(appDir);

  console.log(`[package:appimage] 正在生成 ${outputPath}...`);
  buildAppImage(appImageTool, appDir, outputPath);
  const sizeMb = (statSync(outputPath).size / (1024 * 1024)).toFixed(1);
  console.log(`[package:appimage] 完成：${outputPath} (${sizeMb} MiB)`);

  if (!values['keep-appdir']) {
    rmSync(appDir, { recursive: true, force: true });
  }
}

await main();
