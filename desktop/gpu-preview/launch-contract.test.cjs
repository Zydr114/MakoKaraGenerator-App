'use strict';

const { test } = require('node:test');
const assert = require('node:assert/strict');
const childProcess = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const privateFs = require('../runtime/private-fs.cjs');
const { readLaunchConfig } = require('./launch-contract.cjs');

test('launcher identity uses the exact Server hex contract and owned PID', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'mako-desktop-'));
  const runDir = privateFs.ensurePrivateDir(path.join(root, '.local', 'run'));
  const file = path.join(runDir, 'launch.json');
  const valid = {
    root,
    consumerId: 'a'.repeat(32),
    consumerSecret: 'b'.repeat(64),
    pageUrl: 'http://127.0.0.1:5173',
    ready: true,
    baseUrl: 'http://127.0.0.1:4567',
    hostInstanceId: 'host',
    mainPid: process.pid,
  };
  const write = (value) => fs.writeFileSync(file, JSON.stringify(value), { mode: 0o600 });
  try {
    write(valid);
    assert.equal(readLaunchConfig(file, root, process.pid).consumerId, valid.consumerId);
    write({ ...valid, consumerId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa' });
    assert.throws(() => readLaunchConfig(file, root, process.pid), /identity/);
    write({ ...valid, mainPid: process.pid + 1 });
    assert.throws(() => readLaunchConfig(file, root, process.pid), /ready/);

    if (process.platform === 'win32') {
      const localAppData = path.join(root, 'local-app-data');
      const winRunDir = privateFs.ensurePrivateDir(path.join(localAppData, 'makokara-generator', 'run'));
      const winFile = path.join(winRunDir, 'launch.json');
      fs.writeFileSync(winFile, JSON.stringify(valid));
      assert.equal(
        readLaunchConfig(winFile, root, process.pid, { env: { LOCALAPPDATA: localAppData } }).consumerId,
        valid.consumerId,
      );

      const badRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'mako-desktop-bad-'));
      try {
        const badRunDir = path.join(badRoot, '.local', 'run');
        fs.mkdirSync(badRunDir, { recursive: true });
        childProcess.execFileSync('icacls.exe', [badRunDir, '/grant', '*S-1-1-0:(R)'], { windowsHide: true });
        const badFile = path.join(badRunDir, 'launch.json');
        fs.writeFileSync(badFile, JSON.stringify({ ...valid, root: badRoot }));
        privateFs.clearCachesForTesting();
        assert.throws(() => readLaunchConfig(badFile, badRoot, process.pid), /protected current-user-only inheritable DACL/);
      } finally {
        fs.rmSync(badRoot, { recursive: true, force: true });
      }
    } else {
      write(valid);
      fs.chmodSync(file, 0o644);
      assert.throws(() => readLaunchConfig(file, root, process.pid), /private/);
    }
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
