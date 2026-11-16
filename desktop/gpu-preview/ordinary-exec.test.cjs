'use strict';

const { test } = require('node:test');
const assert = require('node:assert/strict');
const { execFileSync, spawnSync } = require('node:child_process');
const { ordinaryCommand } = require('./ordinary-exec.cjs');

test('explicit acceptance wrapper verifies its new child scheduler before exec without a shell', () => {
  const marker = 'space and $ literal';
  if (process.platform === 'win32') {
    const launch = ordinaryCommand(ordinaryCommand('x', []).executable, [
      '-c',
      'import ctypes,ctypes.wintypes,json,sys;k=ctypes.WinDLL("kernel32",use_last_error=True);k.GetCurrentProcess.restype=ctypes.wintypes.HANDLE;k.GetPriorityClass.argtypes=[ctypes.wintypes.HANDLE];k.GetPriorityClass.restype=ctypes.wintypes.DWORD;print(json.dumps({"priorityClass":k.GetPriorityClass(k.GetCurrentProcess()),"argument":sys.argv[1]}))',
      marker,
    ]);
    const value = JSON.parse(execFileSync(launch.executable, launch.args, { encoding: 'utf8' }));
    assert.deepEqual(value, { priorityClass: 0x20, argument: marker });

    const exitLaunch = ordinaryCommand(launch.executable, ['-c', 'import sys;sys.exit(7)']);
    const result = spawnSync(exitLaunch.executable, exitLaunch.args, { encoding: 'utf8' });
    assert.equal(result.status, 7);
    return;
  }

  const launch = ordinaryCommand('python3', [
    '-c',
    'import os,json,sys;print(json.dumps({"policy":os.sched_getscheduler(0),"nice":os.getpriority(os.PRIO_PROCESS,0),"argument":sys.argv[1]}))',
    marker,
  ]);
  const value = JSON.parse(execFileSync(launch.executable, launch.args, { encoding: 'utf8' }));
  assert.deepEqual(value, { policy: 0, nice: 0, argument: marker });
});
