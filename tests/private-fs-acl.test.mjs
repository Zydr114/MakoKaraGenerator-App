import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import vm from 'node:vm';
import { createRequire } from 'node:module';
import test from 'node:test';
import assert from 'node:assert/strict';

const require = createRequire(import.meta.url);
const sourcePath = new URL('../desktop/runtime/private-fs.cjs', import.meta.url);
const source = fs.readFileSync(sourcePath, 'utf8');
const sid = 'S-1-5-21-123-456-789-1001';
const safeAcl = () => ({ protected: true, rules: [{ sid, type: 'Allow', rights: 2032127, inheritance: 3, propagation: 0 }] });

// Simulate foreign grants in memory only; never grant broad access on disk.
function fixture() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'mako-acl-mock-'));
  const acls = new Map();
  const writes = [];
  const reads = [];
  const mockCp = {
    execFileSync(command, args, options) {
      if (command === 'whoami.exe') return `"user","${sid}"`;
      if (command === 'icacls.exe') {
        // Model /grant:r faithfully: it replaces this SID's grants only.
        const target = args[0];
        const prior = acls.get(target)?.rules ?? [];
        acls.set(target, { protected: true, rules: [...prior.filter(rule => rule.sid !== sid), ...safeAcl().rules] });
        return '';
      }
      assert.equal(command, 'powershell.exe');
      const script = Buffer.from(args.at(-1), 'base64').toString('utf16le');
      const target = options.env.MAKO_ACL_TARGET;
      if (script.includes('SetSecurityDescriptorSddlForm')) {
        assert.ok(script.includes('D:P(A;OICI;FA;;;'));
        writes.push(target);
        acls.set(target, safeAcl());
        return '';
      }
      reads.push(target);
      return JSON.stringify(acls.get(target) ?? { protected: false, rules: [] });
    },
  };
  const module = { exports: {} };
  vm.runInNewContext(source, {
    require(name) { return name === 'node:child_process' ? mockCp : require(name); },
    module, exports: module.exports, Buffer,
    process: { platform: 'win32', env: process.env },
  }, { filename: sourcePath.pathname });
  const options = { getVolumeInfo: () => ({ fileSystemFlags: 8 }) };
  return { root, acls, writes, reads, api: module.exports, options };
}

test('private directory replaces foreign explicit grants with the complete SID DACL', t => {
  const f = fixture();
  t.after(() => fs.rmSync(f.root, { recursive: true }));
  f.acls.set(f.root, { protected: true, rules: [...safeAcl().rules, { sid: 'S-1-1-0', type: 'Allow' }] });
  f.api.ensurePrivateDir(f.root, f.options);
  assert.deepEqual(f.acls.get(f.root), safeAcl());
  assert.deepEqual(f.writes, [f.root]);
  assert.deepEqual(f.reads, [f.root]);
});

test('cached ancestor cannot admit an unverified existing child and exact cache avoids per-file shells', t => {
  const f = fixture();
  t.after(() => fs.rmSync(f.root, { recursive: true }));
  const child = path.join(f.root, 'existing');
  fs.mkdirSync(child);
  f.api.ensurePrivateDir(f.root, f.options);
  f.acls.set(child, { protected: false, rules: [...safeAcl().rules, { sid: 'S-1-1-0', type: 'Allow' }] });
  assert.throws(() => f.api.assertPrivateDir(child, f.options), /current-user-only/);
  f.api.ensurePrivateDir(child, f.options);
  assert.deepEqual(f.writes, [f.root, child]);
  const calls = f.reads.length;
  f.api.ensurePrivateDir(child, f.options);
  f.api.assertPrivateDir(child, f.options);
  assert.equal(f.reads.length, calls);
});

test('in-memory malformed ACLs are rejected', t => {
  const f = fixture();
  t.after(() => fs.rmSync(f.root, { recursive: true }));
  for (const acl of [
    { protected: false, rules: safeAcl().rules },
    { protected: true, rules: [] },
    { protected: true, rules: [{ ...safeAcl().rules[0], inheritance: 0 }] },
    { protected: true, rules: [{ ...safeAcl().rules[0], rights: 1 }] },
    { protected: true, rules: [...safeAcl().rules, { sid: 'S-1-5-18', type: 'Allow' }] },
  ]) {
    assert.throws(() => f.api.assertWindowsDirectoryAcl(acl, sid, 'test', f.root), /current-user-only/);
  }
});

test('Windows own temporary directory receives only the current SID inheritable DACL', { skip: process.platform !== 'win32' }, t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'mako-acl-real-'));
  t.after(() => fs.rmSync(root, { recursive: true }));
  const api = require('../desktop/runtime/private-fs.cjs');
  api.ensurePrivateDir(root);
  api.clearCachesForTesting();
  api.assertPrivateDir(root);
});
