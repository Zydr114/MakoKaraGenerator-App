import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import test from 'node:test';
import backendHost from '../desktop/runtime/backend-host.cjs';
import privateFs from '../desktop/runtime/private-fs.cjs';
import portable from '../desktop/runtime/windows-portable.cjs';
import {
  assertWindowsX64Pe, createWindowsZip, stageWindowsApp, validateExeName, validateZipPath, verifyWindowsDeployment,
} from '../scripts/package-windows.mjs';

const windowsOnly = { skip: process.platform !== 'win32' || process.arch !== 'x64' };
const version = backendHost.EXPECTED_PRODUCT_VERSION;
const desktopFile = 'desktop/shm_bridge.node';
const nativeTools = ['mkg-presentation.exe', 'avutil-61.dll', 'avcodec-63.dll', 'avformat-63.dll'];
const nativeNotices = ['FFmpeg-LICENSE.txt', 'zlib-LICENSE.txt', 'FFmpeg-Windows-Provenance.txt', 'ffmpeg-9.0.2.tar.xz',
  'zlib-1.4.tar.gz', 'build_windows_devkit.ps1'];
const hash = (content) => createHash('sha256').update(content).digest('hex');

function fixtureRoot(context) {
  const root = mkdtempSync(join(tmpdir(), 'mako package test '));
  context.after(() => rmSync(root, { force: true, recursive: true }));
  return root;
}

function put(root, relative, content) {
  const target = join(root, relative);
  mkdirSync(dirname(target), { recursive: true });
  writeFileSync(target, content);
  return target;
}

function pe(machine = 0x8664) {
  const header = Buffer.alloc(90);
  header.writeUInt16LE(0x5a4d, 0);
  header.writeUInt32LE(64, 60);
  header.writeUInt32LE(0x4550, 64);
  header.writeUInt16LE(machine, 68);
  header.writeUInt16LE(0x20b, 88);
  return header;
}

function deploymentFixture(root, omit) {
  const dist = join(root, 'build-output');
  const gpuManifest = JSON.stringify({
    abiVersion: 1, electron: '45.2.1', platform: 'win-x64', defaultTransport: 'shm', files: { [desktopFile]: hash(pe()) },
  });
  const files = {
    'gpu-preview-manifest.json': gpuManifest,
    'server/MakoKaraGenerator.Backend.Server.exe': pe(),
    'worker/MakoKaraGenerator.Backend.Worker.exe': pe(),
    'server/MakoKaraGenerator.Scripting.Abstractions.dll': 'script-abi',
    'worker/MakoKaraGenerator.Scripting.Abstractions.dll': 'script-abi',
    'worker/script-refs/net10.0/System.Runtime.dll': 'script-refs',
    [desktopFile]: pe(),
    ...Object.fromEntries(nativeTools.map((file) => [`server/media-tools/${file}`, pe()])),
    ...Object.fromEntries(nativeNotices.map((file) => [`server/media-tools/${file}`, 'notice'])),
  };
  if (omit) delete files[omit];
  for (const [file, content] of Object.entries(files)) put(dist, file, content);
  put(dist, 'deployment.json', JSON.stringify({
    version, rid: 'win-x64', selfContained: true, gpuPreview: { sha256: hash(gpuManifest) },
    files: Object.fromEntries(Object.entries(files).map(([file, content]) => [file, hash(content)])),
  }));
  return dist;
}

test('exe and output names reject Windows aliases, ADS, and path tricks', () => {
  for (const valid of ['MakoKaraGenerator.exe', 'Custom Build.EXE', '\u6d4b\u8bd5.exe']) {
    assert.equal(validateExeName(valid), valid);
  }
  for (const invalid of ['.exe', 'x.exe.', 'x.exe ', 'x.com', 'a/b.exe', 'a\\b.exe', 'C:x.exe', 'x.exe:stream',
    'NUL.extra.exe', 'AUX.exe', 'COM\u00b9.exe', 'CON .exe', 'x?.exe', 'x\n.exe']) {
    assert.throws(() => validateExeName(invalid));
  }
  assert.equal(validateZipPath('output/Custom Build.ZIP', 'D:\\repo'), 'D:\\repo\\output\\Custom Build.ZIP');
  assert.equal(validateZipPath('E:\\Custom Build.zip', 'D:\\repo'), 'E:\\Custom Build.zip');
  for (const invalid of ['archive.tar', 'C:archive.zip', '\\archive.zip', '\\\\?\\C:\\archive.zip',
    'archive.zip:stream', 'out /archive.zip', 'NUL.zip', 'out/COM1/archive.zip']) {
    assert.throws(() => validateZipPath(invalid, 'D:\\repo'));
  }
});

test('PE headers reject mismatched or missing inputs', (context) => {
  const root = fixtureRoot(context);
  assert.doesNotThrow(() => assertWindowsX64Pe(put(root, 'x64.exe', pe())));
  assert.throws(() => assertWindowsX64Pe(put(root, 'arm64.exe', pe(0xaa64))), /x64 PE/);
  assert.throws(() => assertWindowsX64Pe(put(root, 'text.exe', 'not a PE')), /x64 PE/);
});

test('verified deployment must include its helper and dependency notices', windowsOnly, (context) => {
  const root = fixtureRoot(context);
  verifyWindowsDeployment(deploymentFixture(join(root, 'verified')), version);
  const withoutNotice = deploymentFixture(join(root, 'missing'), 'server/media-tools/FFmpeg-Windows-Provenance.txt');
  assert.throws(() => verifyWindowsDeployment(withoutNotice, version), /inventory.*Provenance/);
  const withoutHelper = deploymentFixture(join(root, 'no-helper'), 'server/media-tools/mkg-presentation.exe');
  assert.throws(() => verifyWindowsDeployment(withoutHelper, version), /inventory.*mkg-presentation/);
});

test('Windows ZIP stages a renamed runtime, matched deployment, and only production app files', windowsOnly, (context) => {
  const root = fixtureRoot(context);
  const electronDist = join(root, 'electron-dist');
  for (const file of ['LICENSE', 'LICENSES.chromium.html', 'icudtl.dat', 'resources.pak', 'resources/default_app.asar',
    'chrome_100_percent.pak', 'chrome_200_percent.pak', 'locales/en-US.pak', 'snapshot_blob.bin', 'v8_context_snapshot.bin', 'vk_swiftshader_icd.json']) {
    put(electronDist, file, file);
  }
  put(electronDist, 'version', '45.2.1');
  for (const file of ['electron.exe', 'd3dcompiler_47.dll', 'dxcompiler.dll', 'electron_wer.dll', 'ffmpeg.dll', 'vk_swiftshader.dll', 'vulkan-1.dll']) {
    put(electronDist, file, pe());
  }
  put(root, 'frontend/dist/index.html', '<html></html>');
  put(root, 'frontend/docs/backend/contract/contract-manifest.json', JSON.stringify({ productVersion: version }));
  put(root, 'desktop/main.cjs', '');
  put(root, 'desktop/assets/icon-128.png', 'icon');
  put(root, 'desktop/runtime/windows-portable.cjs', '');
  put(root, 'desktop/runtime/unused.test.cjs', '');
  put(root, 'desktop/gpu-preview/preload.cjs', '');
  const backendDist = deploymentFixture(root);
  const stageParent = join(root, 'stage');
  const stage = join(stageParent, 'MakoKaraGenerator');
  const exeName = 'Mako Custom \u6d4b\u8bd5.exe';
  stageWindowsApp({ stage, root, electronDist, backendDist, productVersion: version, exeName });
  assert.deepEqual(readFileSync(join(stage, exeName)), pe());
  assert.equal(existsSync(join(stage, 'electron.exe')), false);
  assert.equal(existsSync(join(stage, 'resources/default_app.asar')), false);
  assert.equal(existsSync(join(stage, 'resources/app/runtime/unused.test.cjs')), false);
  assert.equal(JSON.parse(readFileSync(join(stage, 'resources/app/package.json'))).main, 'main.cjs');

  const resourcesDir = join(stage, 'resources');
  assert.throws(() => portable.resolveWindowsPortableResources(resourcesDir), /media-tools/);
  put(resourcesDir, 'media-tools/ffmpeg.exe', pe());
  assert.throws(() => portable.resolveWindowsPortableResources(resourcesDir), /ffprobe.exe/);
  put(resourcesDir, 'media-tools/ffprobe.exe', pe());
  const resources = portable.resolveWindowsPortableResources(resourcesDir);
  assert.equal(resources.shmAddon, join(resourcesDir, 'backend-dist', desktopFile));
  const base = join(root, 'private runtime');
  const ensurePrivateDir = context.mock.method(privateFs, 'ensurePrivateDir', (target) => {
    assert.equal(target, join(base, 'temp'));
    return target;
  });
  assert.deepEqual(portable.windowsPortableBackendOptions(resources, { base }), {
    ffmpeg: join(resourcesDir, 'media-tools/ffmpeg.exe'), ffprobe: join(resourcesDir, 'media-tools/ffprobe.exe'),
    requireMediaTools: true, cwd: join(base, 'temp'), env: { TEMP: join(base, 'temp'), TMP: join(base, 'temp') },
  });
  assert.equal(ensurePrivateDir.mock.callCount(), 1);

  const tar = join(process.env.SystemRoot, 'System32/tar.exe');
  const output = join(root, 'output', 'portable custom.zip');
  createWindowsZip(stageParent, output, tar);
  const extracted = join(root, 'extracted');
  mkdirSync(extracted);
  execFileSync(tar, ['-xf', output, '-C', extracted]);
  for (const entry of [exeName, 'resources/media-tools/ffprobe.exe', 'resources/frontend-dist/index.html',
    'resources/backend-dist/server/media-tools/FFmpeg-LICENSE.txt', 'resources/backend-dist/desktop/shm_bridge.node']) {
    assert.ok(existsSync(join(extracted, 'MakoKaraGenerator', entry)), entry);
  }
  assert.throws(() => createWindowsZip(stageParent, output, tar), { code: 'EEXIST' });
});

test('packaged resolution rejects a linked frontend without looking in the checkout', windowsOnly, (context) => {
  const root = fixtureRoot(context);
  const frontend = dirname(put(root, 'unrelated/index.html', '<html></html>'));
  const resources = join(root, 'resources');
  mkdirSync(resources);
  symlinkSync(frontend, join(resources, 'frontend-dist'), 'junction');
  assert.throws(() => portable.resolveWindowsPortableResources(resources), /linked.*frontend-dist/);
});

test('packaged backend spawn receives absolute bundled tools and private Windows temp paths', windowsOnly, async (context) => {
  const root = fixtureRoot(context);
  const resources = {
    ffmpeg: put(root, 'resources/media-tools/ffmpeg.exe', pe()),
    ffprobe: put(root, 'resources/media-tools/ffprobe.exe', pe()),
  };
  const base = join(root, 'runtime');
  context.mock.method(privateFs, 'ensurePrivateDir', (target) => {
    mkdirSync(target, { recursive: true });
    return target;
  });
  const inherited = Object.entries(process.env).find(([key]) => key.toUpperCase() === 'TEMP');
  delete process.env.TEMP;
  process.env.Temp = join(root, 'untrusted temp');
  context.after(() => {
    delete process.env.Temp;
    if (inherited) process.env[inherited[0]] = inherited[1];
  });
  const intercepted = new Error('intercepted before spawn');
  await assert.rejects(backendHost.startBackendProcess({
    ...portable.windowsPortableBackendOptions(resources, { base }),
    dist: deploymentFixture(root),
    origins: ['http://127.0.0.1:5180'],
    profile: join(base, 'profile'),
    log: () => {},
    spawnProcess: (_command, args, { cwd, env }) => {
      assert.deepEqual(args, ['--urls', 'http://127.0.0.1:0']);
      assert.equal(cwd, join(base, 'temp'));
      assert.equal(env.MAKO_BACKEND_FFMPEG_PATH, resources.ffmpeg);
      assert.equal(env.MAKO_BACKEND_FFPROBE_PATH, resources.ffprobe);
      assert.equal(env.TEMP, cwd);
      assert.equal(env.TMP, cwd);
      assert.equal(Object.hasOwn(env, 'Temp'), false);
      throw intercepted;
    },
  }), (error) => error === intercepted);
});
