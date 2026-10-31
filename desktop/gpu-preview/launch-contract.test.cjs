'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { readLaunchConfig } = require('./launch-contract.cjs');
test('launcher identity uses the exact Server hex contract and owned PID', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'mako-desktop-'));
  fs.mkdirSync(path.join(root, '.local/run'), { recursive:true });
  const file = path.join(root, '.local/run/launch.json');
  const valid = { root, consumerId:'a'.repeat(32), consumerSecret:'b'.repeat(64), pageUrl:'http://127.0.0.1:5173', ready:true, baseUrl:'http://127.0.0.1:4567', hostInstanceId:'host', mainPid:process.pid };
  const write = value => fs.writeFileSync(file, JSON.stringify(value), { mode:0o600 });
  try {
    write(valid); assert.equal(readLaunchConfig(file, root, process.pid).consumerId, valid.consumerId);
    write({ ...valid, consumerId:'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa' }); assert.throws(() => readLaunchConfig(file, root, process.pid), /identity/);
    write({ ...valid, mainPid:process.pid+1 }); assert.throws(() => readLaunchConfig(file, root, process.pid), /ready/);
    write(valid); fs.chmodSync(file,0o644); assert.throws(() => readLaunchConfig(file, root, process.pid), /private/);
  } finally { fs.rmSync(root,{ recursive:true, force:true }); }
});
