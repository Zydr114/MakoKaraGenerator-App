import { execFileSync } from 'node:child_process';
import {
  closeSync, constants, copyFileSync, lstatSync, mkdirSync, mkdtempSync, openSync,
  readdirSync, readFileSync, readSync, realpathSync, rmSync, statSync, writeFileSync,
} from 'node:fs';
import { basename, dirname, join, resolve, win32 } from 'node:path';
import { pathToFileURL } from 'node:url';
import { parseArgs } from 'node:util';
import backendHost from '../desktop/runtime/backend-host.cjs';
import { stageWindowsMedia, windowsMediaInputs } from './package-windows-media.mjs';

const ROOT = resolve(import.meta.dirname, '..');
const ELECTRON_VERSION = '45.2.1';
const APP_NAME = 'MakoKaraGenerator';
const RELEASE_ROOT = join(ROOT, '.local/release');
const CONTRACT = 'frontend/docs/backend/contract/contract-manifest.json';
const ELECTRON_RESOURCES = [
  'LICENSE', 'LICENSES.chromium.html', 'chrome_100_percent.pak', 'chrome_200_percent.pak', 'icudtl.dat',
  'locales/en-US.pak', 'resources.pak', 'snapshot_blob.bin', 'v8_context_snapshot.bin', 'vk_swiftshader_icd.json',
];
const ELECTRON_DLLS = ['d3dcompiler_47.dll', 'dxcompiler.dll', 'electron_wer.dll', 'ffmpeg.dll', 'vk_swiftshader.dll', 'vulkan-1.dll'];
const DEVICE_NAME = /^(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|(?:COM|LPT)[0-9\u00b9\u00b2\u00b3])$/i;
const INVALID_COMPONENT = /[<>:"/\\|?*\x00-\x1f]/;

function validateComponent(name) {
  if (!name || name.length > 255 || name.trim() !== name || name.endsWith('.') ||
      INVALID_COMPONENT.test(name) || DEVICE_NAME.test(name.split('.')[0].trimEnd())) {
    throw new Error(`Invalid Windows filename component: ${JSON.stringify(name)}`);
  }
}

export function validateExeName(name) {
  validateComponent(name);
  if (!/^.+\.exe$/i.test(name)) throw new Error('--exe-name must be a filename ending in .exe');
  return name;
}

export function validateZipPath(value, cwd = process.cwd()) {
  if (!value || /^[\\/]|^[a-z]:(?![\\/])/i.test(value)) {
    throw new Error('--out must be a relative or fully qualified drive path ending in .zip');
  }
  const components = value.replace(/^[a-z]:[\\/]/i, '').split(/[\\/]/);
  for (const component of components) {
    if (component !== '.' && component !== '..') validateComponent(component);
  }
  if (!/^.+\.zip$/i.test(components.at(-1))) throw new Error('--out must end in .zip');
  return win32.resolve(cwd, value);
}

function requireEntry(file, directory = false) {
  const info = lstatSync(file, { throwIfNoEntry: false });
  if (!(directory ? info?.isDirectory() : info?.isFile())) {
    throw new Error(`Expected a regular, non-linked ${directory ? 'directory' : 'file'}: ${file}`);
  }
}

function copyTree(source, target, filter = () => true, prefix = '') {
  requireEntry(source, true);
  mkdirSync(target, { recursive: true });
  const names = new Set();
  for (const entry of readdirSync(source, { withFileTypes: true })) {
    const relative = prefix + entry.name;
    if (!filter(relative)) continue;
    validateComponent(entry.name);
    const key = entry.name.toLowerCase();
    if (names.has(key)) throw new Error(`Windows filename collision: ${relative}`);
    names.add(key);
    const from = join(source, entry.name);
    const to = join(target, entry.name);
    if (entry.isDirectory()) copyTree(from, to, filter, `${relative}/`);
    else {
      requireEntry(from);
      copyFileSync(from, to, constants.COPYFILE_EXCL);
    }
  }
}

export function assertWindowsX64Pe(file) {
  requireEntry(file);
  const handle = openSync(file, 'r');
  const dosHeader = Buffer.alloc(64);
  const peHeader = Buffer.alloc(26);
  try {
    if (readSync(handle, dosHeader, 0, dosHeader.length, 0) !== dosHeader.length ||
        dosHeader.readUInt16LE(0) !== 0x5a4d ||
        readSync(handle, peHeader, 0, peHeader.length, dosHeader.readUInt32LE(60)) !== peHeader.length ||
        peHeader.readUInt32LE(0) !== 0x4550 || peHeader.readUInt16LE(4) !== 0x8664 ||
        peHeader.readUInt16LE(24) !== 0x20b) {
      throw new Error(`Expected Windows x64 PE: ${file}`);
    }
  } finally {
    closeSync(handle);
  }
}

export function verifyWindowsDeployment(dist, productVersion) {
  const validated = backendHost.validateDesktopDeployment(dist, ELECTRON_VERSION);
  const { deployment, gpuManifest } = validated;
  if (!backendHost.compatibleProductVersion(deployment.version, productVersion) ||
      gpuManifest.platform !== 'win-x64' || gpuManifest.defaultTransport !== 'shm' ||
      !gpuManifest.files['desktop/shm_bridge.node']) {
    throw new Error('Expected a compatible Windows x64 desktop deployment with matched SHM');
  }
  const nativeTools = ['mkg-presentation.exe', 'avcodec-63.dll', 'avformat-63.dll', 'avutil-61.dll'];
  const notices = ['FFmpeg-LICENSE.txt', 'zlib-LICENSE.txt', 'FFmpeg-Windows-Provenance.txt', 'ffmpeg-9.0.2.tar.xz',
    'zlib-1.4.tar.gz', 'build_windows_devkit.ps1'];
  for (const file of [...nativeTools, ...notices]) {
    if (!deployment.files[`server/media-tools/${file}`]) throw new Error(`Windows deployment must inventory server/media-tools/${file}`);
  }
  for (const relative of [
    ...nativeTools.map((file) => `server/media-tools/${file}`),
    'desktop/shm_bridge.node',
    ...Object.entries(backendHost.ROLES).map(([role, name]) => `${role}/${name}.exe`),
  ]) {
    assertWindowsX64Pe(join(validated.dist, relative));
  }
  return validated;
}

function stageApp(stage, root, productVersion, exeName) {
  const app = join(stage, 'resources/app');
  mkdirSync(join(app, 'assets'), { recursive: true });
  writeFileSync(join(app, 'package.json'), JSON.stringify({
    name: 'makokara-generator', productName: 'MakoKara Generator', version: productVersion, private: true, main: 'main.cjs',
  }, null, 2) + '\n');
  for (const [input, output] of [
    [CONTRACT, 'contract-manifest.json'],
    ['desktop/main.cjs', 'main.cjs'],
    ['desktop/assets/icon-128.png', 'assets/icon-128.png'],
  ]) {
    requireEntry(join(root, input));
    copyFileSync(join(root, input), join(app, output), constants.COPYFILE_EXCL);
  }
  const codeFile = (name) => name.endsWith('.cjs') && !name.endsWith('.test.cjs') && name !== 'ordinary-exec.cjs';
  copyTree(join(root, 'desktop/runtime'), join(app, 'runtime'), codeFile);
  copyTree(join(root, 'desktop/gpu-preview'), join(app, 'gpu-preview'), codeFile);
  writeFileSync(join(stage, 'README-PORTABLE.txt'), [
    'Extract the entire ZIP before running the app.',
    `Start ${exeName} from the extracted folder.`,
    'User data is stored under %LOCALAPPDATA%\\makokara-generator (or %MAKO_RUNTIME_HOME%\\makokara-generator).',
    'The user data directory must be on a volume that supports persistent Windows ACLs.',
    'Bundled media tool license and source information: resources\\media-tools.',
    'Deployment helper notices: resources\\backend-dist\\server\\media-tools.',
    '',
  ].join('\r\n'));
}

export function stageWindowsApp({ stage, root, electronDist, backendDist, productVersion, exeName }) {
  validateExeName(exeName);
  requireEntry(join(electronDist, 'version'));
  if (readFileSync(join(electronDist, 'version'), 'utf8').trim() !== ELECTRON_VERSION) {
    throw new Error(`Expected Electron ${ELECTRON_VERSION} in ${electronDist}`);
  }
  for (const file of ['electron.exe', ...ELECTRON_DLLS]) {
    assertWindowsX64Pe(join(electronDist, file));
  }
  for (const relative of ELECTRON_RESOURCES) {
    requireEntry(join(electronDist, relative));
  }
  const collision = readdirSync(electronDist).find((name) => name.toLowerCase() === exeName.toLowerCase() && name !== 'electron.exe');
  if (collision) throw new Error(`--exe-name collides with Electron runtime file: ${collision}`);
  copyTree(electronDist, stage, (name) => name !== 'electron.exe' && !/^resources\/(?:default_app\.asar|app|app\.asar)$/i.test(name));
  copyFileSync(join(electronDist, 'electron.exe'), join(stage, exeName), constants.COPYFILE_EXCL);
  const resources = join(stage, 'resources');
  const stagedBackend = join(resources, 'backend-dist');
  copyTree(backendDist, stagedBackend);
  verifyWindowsDeployment(stagedBackend, productVersion);
  copyTree(join(root, 'frontend/dist'), join(resources, 'frontend-dist'));
  requireEntry(join(resources, 'frontend-dist/index.html'));
  stageApp(stage, root, productVersion, exeName);
}

function assertOutputOutsideInputs(output, inputs) {
  for (const input of inputs) {
    if (backendHost.isSamePath(input, output) || backendHost.isInsideRoot(input, output)) {
      throw new Error(`--out must be outside packaging inputs: ${input}`);
    }
  }
}

function canonicalFuturePath(output) {
  if (lstatSync(output, { throwIfNoEntry: false })) return realpathSync(output);
  const parent = dirname(output);
  if (parent === output) throw new Error(`ZIP output volume does not exist: ${output}`);
  return join(canonicalFuturePath(parent), basename(output));
}

export function createWindowsZip(stageParent, output, tar) {
  if (lstatSync(output, { throwIfNoEntry: false })) throw Object.assign(new Error(`ZIP already exists: ${output}`), { code: 'EEXIST' });
  const parent = dirname(output);
  mkdirSync(parent, { recursive: true });
  const scratch = mkdtempSync(join(parent, '.mako-zip-'));
  try {
    const tempZip = join(scratch, 'portable.zip');
    execFileSync(tar, ['-a', '--options', 'zip:hdrcharset=UTF-8', '-cf', tempZip, '-C', stageParent, APP_NAME], { stdio: 'inherit', windowsHide: true });
    copyFileSync(tempZip, output, constants.COPYFILE_EXCL);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
}

async function main() {
  const { values } = parseArgs({ options: {
    build: { type: 'boolean' },
    out: { type: 'string' },
    'exe-name': { type: 'string', default: `${APP_NAME}.exe` },
    'ffmpeg-dir': { type: 'string' },
    help: { type: 'boolean' },
  } });
  if (values.help) {
    console.log('Usage: pnpm package:windows [--ffmpeg-dir <built export media bundle>] [--build] [--exe-name MakoKaraGenerator.exe] [--out path.zip]');
    console.log('Build the export media bundle with scripts/build-windows-export-tools.ps1 (default: .local/windows-export-tools).');
    return;
  }
  if (process.platform !== 'win32' || process.arch !== 'x64') throw new Error('Windows Portable packaging requires Windows x64');
  const exeName = validateExeName(values['exe-name']);
  const { productVersion } = JSON.parse(readFileSync(join(ROOT, CONTRACT), 'utf8'));
  if (!/^\d+\.\d+\.\d+$/.test(productVersion)) throw new Error('Invalid contract productVersion');
  const output = values.out === undefined
    ? join(RELEASE_ROOT, `${APP_NAME}-${productVersion}-windows-x64.zip`)
    : validateZipPath(values.out);
  if (lstatSync(output, { throwIfNoEntry: false })) throw new Error(`ZIP already exists: ${output}`);
  const mediaInputs = windowsMediaInputs(ROOT, values);
  const electronDist = join(ROOT, 'desktop/node_modules/electron/dist');
  const { desktopBuildState } = await import('./build-state.mjs');
  if (values.build) {
    execFileSync(process.execPath, [join(ROOT, 'scripts/app.mjs'), 'build:desktop'], { cwd: ROOT, stdio: 'inherit', windowsHide: true });
  }
  const state = await desktopBuildState(ROOT);
  if (state.web || state.deployment) throw new Error(`Desktop build missing or stale (web=${state.web ?? 'ok'}, deployment=${state.deployment ?? 'ok'}); run with --build`);
  verifyWindowsDeployment(state.dist, productVersion);
  assertOutputOutsideInputs(canonicalFuturePath(output), [electronDist, join(ROOT, 'frontend/dist'), state.dist, mediaInputs.directory].map((input) => realpathSync(input)));

  mkdirSync(RELEASE_ROOT, { recursive: true });
  const scratch = mkdtempSync(join(RELEASE_ROOT, '.windows-package-'));
  try {
    const stage = join(scratch, APP_NAME);
    const tar = join(process.env.SystemRoot ?? 'C:\\Windows', 'System32/tar.exe');
    console.log(`[package:windows] Staging Windows x64 ${productVersion}`);
    stageWindowsApp({ stage, root: ROOT, electronDist, backendDist: state.dist, productVersion, exeName });
    stageWindowsMedia(mediaInputs, join(stage, 'resources'));
    for (const tool of ['ffmpeg.exe', 'ffprobe.exe']) assertWindowsX64Pe(join(stage, 'resources', 'media-tools', tool));
    createWindowsZip(scratch, output, tar);
    console.log(`[package:windows] ${output} (${(statSync(output).size / 1024 ** 2).toFixed(1)} MiB)`);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => {
    console.error(`[package:windows] ${error.message}`);
    process.exitCode = 1;
  });
}
