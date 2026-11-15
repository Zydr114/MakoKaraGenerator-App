import { mock, test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn as actualSpawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { access, mkdtemp, mkdir, chmod, writeFile, rm, symlink } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { tmpdir } from 'node:os';

const root = resolve(import.meta.dirname, '..');
const productVersion = JSON.parse(readFileSync(join(root, 'frontend/docs/backend/contract/contract-manifest.json'), 'utf8')).productVersion;
const distRoot = join(root, '.local/backend-dist');
await mkdir(distRoot, { recursive: true });
const dist = await mkdtemp(join(distRoot, 'launcher-test-'));
const privateDir = await mkdtemp(join(tmpdir(), 'mako-launcher-test-'));

function sha256(content) {
  return createHash('sha256').update(content).digest('hex');
}

async function writeFixtureDist(targetDist, version = productVersion) {
  await mkdir(join(targetDist, 'server'), { recursive: true });
  await mkdir(join(targetDist, 'worker', 'script-refs', 'net10.0'), { recursive: true });
  const files = {
    'server/MakoKaraGenerator.Backend.Server': Buffer.from('#!/bin/sh\nexit 0\n'),
    'server/MakoKaraGenerator.Scripting.Abstractions.dll': Buffer.from('abi-v1'),
    'worker/MakoKaraGenerator.Backend.Worker': Buffer.from('#!/bin/sh\nexit 0\n'),
    'worker/MakoKaraGenerator.Scripting.Abstractions.dll': Buffer.from('abi-v1'),
    'worker/script-refs/net10.0/System.Runtime.dll': Buffer.from('runtime'),
  };
  const inventory = {};
  for (const [rel, body] of Object.entries(files)) {
    const abs = join(targetDist, rel);
    await writeFile(abs, body, { mode: rel.endsWith('.dll') ? 0o644 : 0o755 });
    inventory[rel] = sha256(body);
  }
  await writeFile(
    join(targetDist, 'deployment.json'),
    JSON.stringify({ version, rid: 'linux-x64', selfContained: true, files: inventory }),
  );
}

async function updateDistVersion(targetDist, version) {
  const manifest = JSON.parse(readFileSync(join(targetDist, 'deployment.json'), 'utf8'));
  manifest.version = version;
  await writeFile(join(targetDist, 'deployment.json'), JSON.stringify(manifest));
}

await writeFixtureDist(dist, productVersion);

let credentialMode = 'valid';
let ownedChild;
let blockParent;
let spawnMissing = false;
mock.module('node:child_process', {
  exports: {
    spawn(_executable, _args, spawnOptions) {
      if (spawnMissing) {
        return actualSpawn('mako-nonexistent-test-executable', [], { stdio: ['ignore', 'pipe', 'pipe'] });
      }
      if (blockParent) writeFileSync(blockParent, 'not a directory');
      const credFile = spawnOptions.env.MAKO_BACKEND_CREDENTIAL_FILE;
      const mode = credentialMode;
      const childScript = `
        const fs = require('node:fs');
        const path = require('node:path');
        const credFile = ${JSON.stringify(credFile)};
        const mode = ${JSON.stringify(mode)};
        fs.mkdirSync(path.dirname(credFile), { recursive: true, mode: 0o700 });
        const payload = mode === 'invalid-json'
          ? '{'
          : JSON.stringify({ protocolVersion: '1', hostInstanceId: 'fixture', addresses: ['http://127.0.0.1:12345'], bootstrapToken: 'test-fixture-secret-0123456789abcdef' });
        fs.writeFileSync(credFile, payload, { mode: 0o600 });
        if (mode === 'permissions') fs.chmodSync(credFile, 0o644);
        console.log('BACKEND_READY http://127.0.0.1:12345 fixture');
        setInterval(() => {}, 1000);
      `;
      ownedChild = actualSpawn(process.execPath, ['-e', childScript], { stdio: ['ignore', 'pipe', 'pipe'] });
      return ownedChild;
    },
  },
});

const { startBackend, resolveDist } = await import('../frontend/scripts/backend-launcher.mjs');

try {
  for (const kind of ['permissions', 'invalid-json', 'bootstrap-write']) {
    await test(`启动就绪后 ${kind} 失败必须回收自有进程`, async () => {
      credentialMode = kind === 'bootstrap-write' ? 'valid' : kind;
      const output = kind === 'bootstrap-write' ? join(privateDir, 'blocked/output.json') : join(privateDir, 'output.json');
      blockParent = kind === 'bootstrap-write' ? join(privateDir, 'blocked') : null;
      await assert.rejects(startBackend({ dist, profile: join(privateDir, 'profile'), bootstrapFile: output, log() {} }));
      assert.notEqual(ownedChild.exitCode ?? ownedChild.signalCode, null);
      assert.throws(() => process.kill(ownedChild.pid, 0), { code: 'ESRCH' });
    });
  }
  await test('找不到后端可执行程序时拒绝启动', async () => {
    spawnMissing = true;
    blockParent = null;
    try {
      await assert.rejects(startBackend({ dist, profile: join(privateDir, 'profile'), bootstrapFile: join(privateDir, 'output.json'), log() {} }), { code: 'ENOENT' });
    } finally {
      spawnMissing = false;
    }
  });
  await test('正常停止回收自有进程且清除bootstrap', async () => {
    credentialMode = 'valid';
    blockParent = null;
    const output = join(privateDir, 'output-clean.json');
    const instance = await startBackend({ dist, profile: join(privateDir, 'profile'), bootstrapFile: output, log() {} });
    await instance.stop();
    await assert.rejects(access(output), { code: 'ENOENT' });
    assert.throws(() => process.kill(ownedChild.pid, 0), { code: 'ESRCH' });
  });
  await test('部署目录符号链接不能逃出 App', async () => {
    const link = join(distRoot, `outside-${process.pid}`);
    await symlink(privateDir, link);
    try {
      assert.throws(() => resolveDist(link), /必须位于/);
    } finally {
      await rm(link);
    }
  });
  await test('旧产品部署在启动前拒绝', async () => {
    await updateDistVersion(dist, '0.109.0');
    try {
      assert.throws(() => resolveDist(dist), /产品版本不一致/);
    } finally {
      await updateDistVersion(dist, productVersion);
    }
  });
  await test('同次版本的更新补丁放行，次版本不同仍拒绝', async () => {
    const [major, minor, patch] = productVersion.split('.').map(Number);
    try {
      await updateDistVersion(dist, `${major}.${minor}.${patch + 22}`);
      assert.equal(resolveDist(dist).version, `${major}.${minor}.${patch + 22}`);
      await updateDistVersion(dist, `${major}.${minor + 1}.0`);
      assert.throws(() => resolveDist(dist), /产品版本不一致/);
    } finally {
      await updateDistVersion(dist, productVersion);
    }
  });
} finally {
  mock.restoreAll();
  await rm(dist, { recursive: true, force: true });
  await rm(privateDir, { recursive: true, force: true });
}
