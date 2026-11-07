import { mock, test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn as actualSpawn } from 'node:child_process';
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
await writeFile(join(dist, 'deployment.json'), JSON.stringify({ version: productVersion }));
let credentialFile;
let ownedChild;
let blockParent;
let spawnMissing = false;
mock.module('node:child_process', { exports: {
  spawn() {
    if (spawnMissing) return actualSpawn('mako-nonexistent-test-executable', [], { stdio: ['ignore', 'pipe', 'pipe'] });
    if (blockParent) writeFileSync(blockParent, 'not a directory');
    const payload = JSON.stringify({ credentialFile, addresses: ['http://127.0.0.1:12345'], hostInstanceId: 'fixture', version: productVersion, pid: 123, runDirectory: privateDir });
    ownedChild = actualSpawn(process.execPath, ['-e', `console.log('WEB_BACKEND_READY ' + ${JSON.stringify(payload)}); setInterval(() => {}, 1000);`], { stdio: ['ignore', 'pipe', 'pipe'] });
    return ownedChild;
  },
} });
const { startBackend, resolveDist } = await import('../frontend/scripts/backend-launcher.mjs');

try {
  for (const kind of ['permissions', 'invalid-json', 'bootstrap-write']) {
    await test(`启动就绪后 ${kind} 失败必须回收自有进程`, async () => {
      credentialFile = join(privateDir, `${kind}.json`);
      await writeFile(credentialFile, kind === 'invalid-json' ? '{' : JSON.stringify({ bootstrapToken: 'test-fixture-secret' }), { mode: 0o600 });
      await chmod(credentialFile, kind === 'permissions' ? 0o644 : 0o600);
      const output = kind === 'bootstrap-write' ? join(privateDir, 'blocked/output.json') : join(privateDir, 'output.json');
      blockParent = kind === 'bootstrap-write' ? join(privateDir, 'blocked') : null;
      await assert.rejects(startBackend({ dist, profile: join(privateDir, 'profile'), bootstrapFile: output, log() {} }));
      assert.notEqual(ownedChild.exitCode ?? ownedChild.signalCode, null);
      assert.throws(() => process.kill(ownedChild.pid, 0), { code: 'ESRCH' });
    });
  }
  await test('找不到启动器可执行程序时拒绝启动', async () => {
    spawnMissing = true;
    blockParent = null;
    try { await assert.rejects(startBackend({ dist, profile: join(privateDir, 'profile'), bootstrapFile: join(privateDir, 'output.json'), log() {} }), { code: 'ENOENT' }); }
    finally { spawnMissing = false; }
  });
  await test('正常停止回收自有进程且清除bootstrap', async () => {
    credentialFile = join(privateDir, 'valid.json');
    await writeFile(credentialFile, JSON.stringify({ bootstrapToken: 'test-fixture-secret' }), { mode: 0o600 });
    const instance = await startBackend({ dist, profile: join(privateDir, 'profile'), bootstrapFile: join(privateDir, 'output.json'), log() {} });
    await instance.stop();
    await assert.rejects(access(join(privateDir, 'output.json')), { code: 'ENOENT' });
    assert.throws(() => process.kill(ownedChild.pid, 0), { code: 'ESRCH' });
  });
  await test('部署目录符号链接不能逃出 App', async () => {
    const link = join(distRoot, `outside-${process.pid}`);
    await symlink(privateDir, link);
    try { assert.throws(() => resolveDist(link), /必须位于/); }
    finally { await rm(link); }
  });
  await test('旧产品部署在启动前拒绝', async () => {
    await writeFile(join(dist, 'deployment.json'), JSON.stringify({ version: '0.109.0' }));
    try { assert.throws(() => resolveDist(dist), /产品版本不一致/); }
    finally { await writeFile(join(dist, 'deployment.json'), JSON.stringify({ version: productVersion })); }
  });
  await test('同次版本的更新补丁放行，次版本不同仍拒绝', async () => {
    const [major, minor, patch] = productVersion.split('.').map(Number);
    try {
      await writeFile(join(dist, 'deployment.json'), JSON.stringify({ version: `${major}.${minor}.${patch + 22}` }));
      assert.equal(resolveDist(dist).version, `${major}.${minor}.${patch + 22}`);
      await writeFile(join(dist, 'deployment.json'), JSON.stringify({ version: `${major}.${minor + 1}.0` }));
      assert.throws(() => resolveDist(dist), /产品版本不一致/);
    } finally { await writeFile(join(dist, 'deployment.json'), JSON.stringify({ version: productVersion })); }
  });
} finally {
  mock.restoreAll();
  await rm(dist, { recursive: true, force: true });
  await rm(privateDir, { recursive: true, force: true });
}
