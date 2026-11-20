import { constants, copyFileSync, lstatSync, mkdirSync, readFileSync, realpathSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import backendHost from '../desktop/runtime/backend-host.cjs';

const RECIPE = 'build-windows-export-tools.ps1';
const MANIFEST = 'media-bundle.json';
const PINNED_FILES = Object.freeze({
  'ffmpeg-946fcce07b.tar.gz': '86f82e82456005881d1b97e9ce02f8a40bf2d181c8c958f8ae736886fdba2a34',
  'x264-31e19f92.tar.gz': 'd053c9d86988d6bc78237ca5205865c5ddf99c98ef4cd9927eec8f6d388f6dd9',
  'zlib-1.4.tar.gz': '02dd159783677a749c04adbdd02b55b8a2bcc7e054f51b76b549199a916ad584',
  'FFmpeg-LICENSE.txt': '2e1d16c72fd74e12063776371da757322f8b77589386532f4fd8634bde7de1af',
  'FFmpeg-COPYING.GPLv2.txt': '8177f97513213526df2cf6184d8ff986c675afb514d4e68a404010521b880643',
  'x264-COPYING.txt': '32b1062f7da84967e7019d01ab805935caa7ab7321a7ced0e30ebe75e5df1670',
  'zlib-LICENSE.txt': 'e32ff4e00d9d94930537635291da39e7e612703334bf6fde8c7f1686fe8a45a2',
  'LLVM-LICENSE.txt': '8d85c1057d742e597985c7d4e6320b015a9139385cff4cbae06ffc0ebe89afee',
  'MinGW-w64-runtime-LICENSE.txt': '1db8da07b436c68833c0673ffee3d9fcb2526047f3820b81661865dfedc79a1f',
  'ffmpeg.exe': 'fbda9ad54687b5f041eb9db75b3f8f066b7e6b240e36b130b3aac01077356974',
  'ffprobe.exe': 'b9630651a2b7cd852da88708a2c4efe167e50461a75e39e2294e6cb3c3fea4aa',
});
const PAYLOAD = [RECIPE, 'FFmpeg-Provenance.txt', ...Object.keys(PINNED_FILES)];

function requireMediaFile(input) {
  if (!lstatSync(input, { throwIfNoEntry: false })?.isFile() || !backendHost.isSamePath(realpathSync(input), input)) {
    throw new Error(`Missing or linked export media bundle file: ${input}`);
  }
}

export function verifyMediaPin(input, sha256) {
  requireMediaFile(input);
  if (backendHost.sha256FileSync(input) !== sha256) {
    throw new Error(`SHA-256 mismatch for export media bundle file: ${input}`);
  }
}

export function windowsMediaInputs(root, options) {
  if (options['ffmpeg-source'] !== undefined) {
    throw new Error('--ffmpeg-source is no longer supported; --ffmpeg-dir must contain the built tools and their matching sources');
  }
  const directory = resolve(options['ffmpeg-dir'] ?? join(root, '.local/windows-export-tools'));
  const manifest = readBundleManifest(directory);
  for (const name of PAYLOAD) verifyMediaPin(join(directory, name), manifest.files[name]);
  return { directory };
}

function readBundleManifest(directory) {
  if (!lstatSync(directory, { throwIfNoEntry: false })?.isDirectory() ||
      !backendHost.isSamePath(realpathSync(directory), directory)) {
    throw new Error(`--ffmpeg-dir must be a non-linked bundle directory from ${RECIPE}: ${directory}`);
  }
  const manifestPath = join(directory, MANIFEST);
  if (!lstatSync(manifestPath, { throwIfNoEntry: false })) {
    throw new Error(`Missing export media bundle manifest: ${manifestPath}; run ${RECIPE}`);
  }
  requireMediaFile(manifestPath);
  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  const files = manifest?.files;
  if (manifest?.schemaVersion !== 1 || manifest?.target !== 'windows-x64' ||
      !files || typeof files !== 'object' || Array.isArray(files) || Object.keys(files).length !== PAYLOAD.length ||
      !PAYLOAD.every((name) => Object.hasOwn(files, name) && typeof files[name] === 'string' && /^[0-9a-f]{64}$/.test(files[name]))) {
    throw new Error(`Invalid Windows export media bundle manifest: ${manifestPath}`);
  }
  const pins = { ...PINNED_FILES, [RECIPE]: backendHost.sha256FileSync(join(import.meta.dirname, RECIPE)) };
  for (const [name, sha256] of Object.entries(pins)) {
    if (files[name] !== sha256) throw new Error(`Export media bundle pin mismatch for ${name}; rebuild with ${RECIPE}`);
  }
  return { schemaVersion: 1, target: 'windows-x64', files };
}

export function stageWindowsMedia({ directory }, resources) {
  const manifest = readBundleManifest(directory);
  const tools = join(resources, 'media-tools');
  mkdirSync(tools);
  for (const name of PAYLOAD) {
    const source = join(directory, name);
    requireMediaFile(source);
    const target = join(tools, name);
    copyFileSync(source, target, constants.COPYFILE_EXCL);
    verifyMediaPin(target, manifest.files[name]);
  }
  writeFileSync(join(tools, MANIFEST), `${JSON.stringify(manifest, null, 2)}\n`, { flag: 'wx' });
}
