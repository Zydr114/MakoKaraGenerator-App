'use strict';

const { test } = require('node:test');
const assert = require('node:assert/strict');
const { execFileSync, spawnSync } = require('node:child_process');
const { once } = require('node:events');
const os = require('node:os');
const { ordinaryCommand, spawnOrdinary } = require('./ordinary-exec.cjs');

test('Linux acceptance wrapper verifies scheduler before exec without a shell', { skip: process.platform !== 'linux' }, () => {
  const marker = 'space and $ literal';
  const launch = ordinaryCommand('python3', [
    '-c',
    'import os,json,sys;print(json.dumps({"policy":os.sched_getscheduler(0),"nice":os.getpriority(os.PRIO_PROCESS,0),"argument":sys.argv[1]}))',
    marker,
  ]);
  const value = JSON.parse(execFileSync(launch.executable, launch.args, { encoding: 'utf8' }));
  assert.deepEqual(value, { policy: 0, nice: 0, argument: marker });
});

test('ordinary child keeps target PID, normal priority, stdin shutdown and exit status', { timeout: 10000 }, async (t) => {
  const child = spawnOrdinary(process.execPath, [
    '-e', 'process.stdout.write(JSON.stringify({pid:process.pid,argument:process.argv[1]}));process.stdin.once("data",()=>process.exit(7))',
    'space and $ literal',
  ], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
  t.after(() => { if (child.exitCode === null) child.kill(); });
  const exited = once(child, 'exit');
  const [data] = await once(child.stdout, 'data');
  assert.deepEqual(JSON.parse(data), { pid: child.pid, argument: 'space and $ literal' });
  assert.equal(os.getPriority(child.pid), os.constants.priority.PRIORITY_NORMAL);
  child.stdin.end('SHUTDOWN\n');
  const [code, signal] = await exited;
  assert.equal(code, 7);
  assert.equal(signal, null);
});

test('Windows priority setup failure terminates only the newly spawned child', { skip: process.platform !== 'win32' }, (t) => {
  const failure = new Error('priority denied');
  let killed = 0;
  const child = { pid: 12345, kill: () => { killed += 1; } };
  t.mock.method(os, 'setPriority', () => { throw failure; });
  assert.throws(() => spawnOrdinary('target.exe', [], {}, () => child), (error) => error === failure);
  assert.equal(killed, 1);
});

test('ordinary launch PID is the target identity used by desktop and backend cleanup', () => {
  const launch = ordinaryCommand(process.execPath, [
    '-e', 'process.stdout.write(JSON.stringify({pid:process.pid,argument:process.argv[1]}))',
    'space and $ literal',
  ]);
  const result = spawnSync(launch.executable, launch.args, { encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  const target = JSON.parse(result.stdout);
  assert.equal(target.pid, result.pid);
  assert.equal(target.argument, 'space and $ literal');
});
