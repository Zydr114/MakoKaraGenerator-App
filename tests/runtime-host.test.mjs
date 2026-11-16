import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { chmodSync, mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const require = createRequire(import.meta.url);
const backendHost = require('../desktop/runtime/backend-host.cjs');
const { startStaticWebServer } = require('../desktop/runtime/web-server.cjs');
const { prepareStandaloneSession } = require('../desktop/runtime/standalone-paths.cjs');

const sha256 = (buf) => createHash('sha256').update(buf).digest('hex');

const isWindows = process.platform === 'win32';
const rid = backendHost.platformRid();
const serverExe = `server/${backendHost.executableName(backendHost.ROLES.server)}`;
const workerExe = `worker/${backendHost.executableName(backendHost.ROLES.worker)}`;
const skiaLib = isWindows ? 'worker/SkiaSharp.dll' : 'worker/libSkiaSharp.so';
const gpuLib = isWindows ? 'worker/mako_gpu_preview.dll' : 'worker/libmako_gpu_preview.so';

function makeFixtureDeployment(rootDir, { withGpu = true } = {}) {
  mkdirSync(join(rootDir, 'server'), { recursive: true });
  mkdirSync(join(rootDir, 'worker', 'script-refs', 'net10.0'), { recursive: true });
  const files = {
    [serverExe]: Buffer.from(isWindows ? 'MZ-fixture-server' : '#!/bin/sh\nexit 0\n'),
    'server/MakoKaraGenerator.Scripting.Abstractions.dll': Buffer.from('abi-match'),
    [workerExe]: Buffer.from(isWindows ? 'MZ-fixture-worker' : '#!/bin/sh\nexit 0\n'),
    'worker/MakoKaraGenerator.Scripting.Abstractions.dll': Buffer.from('abi-match'),
    'worker/script-refs/net10.0/System.Runtime.dll': Buffer.from('sys'),
  };
  if (withGpu) {
    mkdirSync(join(rootDir, 'desktop'), { recursive: true });
    files['desktop/gpu_preview_broker.node'] = Buffer.from('broker');
    files[skiaLib] = Buffer.from('skia');
    files[gpuLib] = Buffer.from('gpu');
  }
  const inventory = {};
  for (const [rel, body] of Object.entries(files)) {
    const abs = join(rootDir, rel);
    writeFileSync(abs, body, { mode: rel.endsWith('.dll') ? 0o644 : 0o755 });
    inventory[rel] = sha256(body);
  }
  let gpuPreview;
  if (withGpu) {
    const gpuManifest = {
      schemaVersion: 1,
      abiVersion: 1,
      platform: rid,
      electron: '45.2.1',
      files: {
        'desktop/gpu_preview_broker.node': inventory['desktop/gpu_preview_broker.node'],
        [skiaLib]: inventory[skiaLib],
        [gpuLib]: inventory[gpuLib],
      },
    };
    const gpuText = JSON.stringify(gpuManifest, null, 2) + '\n';
    writeFileSync(join(rootDir, 'gpu-preview-manifest.json'), gpuText);
    inventory['gpu-preview-manifest.json'] = sha256(Buffer.from(gpuText));
    gpuPreview = { manifest: 'gpu-preview-manifest.json', sha256: inventory['gpu-preview-manifest.json'], abiVersion: 1 };
  }
  writeFileSync(
    join(rootDir, 'deployment.json'),
    JSON.stringify({ version: backendHost.EXPECTED_PRODUCT_VERSION, rid, selfContained: true, gpuPreview, files: inventory }),
  );
}

test('sanitizedBackendEnvironment strips MAKO_BACKEND_* except exact synthetic benchmark opt-in', () => {
  const results = ['1', 'true', '0', ''].map((v) =>
    backendHost.sanitizedBackendEnvironment({
      PATH: 'fixture',
      MAKO_BACKEND_GPU_PREVIEW_BENCHMARK: v,
      MAKO_BACKEND_WORKER_PATH: 'foreign',
      MAKO_BACKEND_GPU_PREVIEW_BENCHMARK_OTHER: '1',
    }),
  );
  assert.deepEqual(results, [
    { PATH: 'fixture', MAKO_BACKEND_GPU_PREVIEW_BENCHMARK: '1' },
    { PATH: 'fixture' },
    { PATH: 'fixture' },
    { PATH: 'fixture' },
  ]);
});

test('validateDist and validateDesktopDeployment reject tampering, symlinks, extra files and ABI drift', () => {
  const tmp = mkdtempSync(join(tmpdir(), 'mako-dist-test-'));
  try {
    makeFixtureDeployment(tmp, { withGpu: true });
    assert.equal(backendHost.validateDesktopDeployment(tmp, '45.2.1').gpuManifest.abiVersion, 1);

    // Extra file detection
    writeFileSync(join(tmp, 'extra.txt'), 'nope');
    assert.throws(() => backendHost.validateDist(tmp), /extra or missing/);
    rmSync(join(tmp, 'extra.txt'));

    // Non-executable apphost
    if (!isWindows) {
      chmodSync(join(tmp, serverExe), 0o644);
      assert.throws(() => backendHost.validateDist(tmp), /not executable/);
      chmodSync(join(tmp, serverExe), 0o755);
    }

    // Tampered payload
    writeFileSync(join(tmp, gpuLib), 'tampered');
    assert.throws(() => backendHost.validateDesktopDeployment(tmp, '45.2.1'), /integrity mismatch/);

    // Symlink inside deployment
    makeFixtureDeployment(tmp, { withGpu: true });
    if (isWindows) {
      const junctionTarget = mkdtempSync(join(tmpdir(), 'mako-junction-target-'));
      try {
        symlinkSync(junctionTarget, join(tmp, 'worker/sym-dir'), 'junction');
        assert.throws(() => backendHost.validateDist(tmp), /symlinks/);
      } finally {
        rmSync(join(tmp, 'worker/sym-dir'), { recursive: true, force: true });
        rmSync(junctionTarget, { recursive: true, force: true });
      }
    } else {
      rmSync(join(tmp, skiaLib));
      symlinkSync('/etc/hosts', join(tmp, skiaLib));
      assert.throws(() => backendHost.validateDist(tmp), /integrity mismatch|symlinks/);
      rmSync(join(tmp, skiaLib));
    }

    // ABI mismatch
    makeFixtureDeployment(tmp, { withGpu: true });
    writeFileSync(join(tmp, 'worker/MakoKaraGenerator.Scripting.Abstractions.dll'), 'abi-v2');
    const manifest = JSON.parse(require('node:fs').readFileSync(join(tmp, 'deployment.json'), 'utf8'));
    manifest.files['worker/MakoKaraGenerator.Scripting.Abstractions.dll'] = sha256(Buffer.from('abi-v2'));
    writeFileSync(join(tmp, 'deployment.json'), JSON.stringify(manifest));
    assert.throws(() => backendHost.validateDist(tmp), /bytes differ/);
  } finally {
    rmSync(tmp, { recursive: true, force: true });
  }
});

test('startStaticWebServer enforces loopback, Host, bootstrap same-origin and serves static assets', async () => {
  const tmp = mkdtempSync(join(tmpdir(), 'mako-web-test-'));
  writeFileSync(join(tmp, 'index.html'), '<!doctype html><html>ok</html>');
  writeFileSync(join(tmp, 'icon.webp'), 'webp-bytes');
  let bootstrap = null;
  const server = await startStaticWebServer({
    distRoot: tmp,
    port: 0,
    getBootstrap: () => bootstrap,
  });
  try {
    const base = server.origins[0];
    const indexRes = await fetch(`${base}/`);
    assert.equal(indexRes.status, 200);
    assert.equal(indexRes.headers.get('x-content-type-options'), 'nosniff');
    assert.equal(await indexRes.text(), '<!doctype html><html>ok</html>');

    const webpRes = await fetch(`${base}/icon.webp`);
    assert.equal(webpRes.status, 200);
    assert.equal(webpRes.headers.get('content-type'), 'image/webp');

    // Direct /__mako/bootstrap without sec-fetch-site: same-origin is rejected
    const forbidden = await fetch(`${base}/__mako/bootstrap`);
    assert.equal(forbidden.status, 403);

    // Same-origin header before backend ready returns 503
    const notReady = await fetch(`${base}/__mako/bootstrap`, { headers: { 'sec-fetch-site': 'same-origin' } });
    assert.equal(notReady.status, 503);

    bootstrap = { baseUrl: 'http://127.0.0.1:45555', hostInstanceId: 'h1', bootstrapToken: 'secret-token' };
    const readyRes = await fetch(`${base}/__mako/bootstrap`, {
      headers: { 'sec-fetch-site': 'same-origin', Origin: base },
    });
    assert.equal(readyRes.status, 200);
    assert.equal(readyRes.headers.get('cache-control'), 'no-store');
    assert.deepEqual(await readyRes.json(), bootstrap);
  } finally {
    await server.close();
    rmSync(tmp, { recursive: true, force: true });
  }
});

test('prepareStandaloneSession creates private runtime directories and rejects symlinks', () => {
  const tmp = mkdtempSync(join(tmpdir(), 'mako-runtime-dir-test-'));
  try {
    const env = { LOCALAPPDATA: tmp, XDG_DATA_HOME: tmp };
    const paths = prepareStandaloneSession('makokara-generator', env);
    assert.equal(paths.base, join(tmp, 'makokara-generator'));
    assert.equal(paths.profile, join(tmp, 'makokara-generator', 'profile'));

    const symlinkBase = join(tmp, 'sym-app');
    symlinkSync(tmp, symlinkBase, isWindows ? 'junction' : 'dir');
    assert.throws(() => prepareStandaloneSession('sym-app', env), /symlink/);
  } finally {
    rmSync(tmp, { recursive: true, force: true });
  }
});
