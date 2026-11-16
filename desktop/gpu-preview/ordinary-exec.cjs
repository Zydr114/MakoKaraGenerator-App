'use strict';

const { spawn } = require('node:child_process');
const os = require('node:os');

// Acceptance only: adjust this new child before exec, never an existing PID.
const posixSource =
  'import os,sys\nos.sched_setscheduler(0,os.SCHED_OTHER,os.sched_param(0))\nos.setpriority(os.PRIO_PROCESS,0,0)\nassert os.sched_getscheduler(0)==os.SCHED_OTHER and os.getpriority(os.PRIO_PROCESS,0)==0\nos.execvp(sys.argv[1],sys.argv[1:])';

function ordinaryCommand(executable, args) {
  if (process.platform === 'win32') {
    return { executable, args };
  }
  return { executable: 'python3', args: ['-c', posixSource, executable, ...args] };
}

function spawnOrdinary(executable, args, options, spawnFn = spawn) {
  const launch = ordinaryCommand(executable, args);
  const child = spawnFn(launch.executable, launch.args, options);
  // Windows has no exec replacement. Keep ChildProcess.pid/stdin/exit bound to
  // the actual target, and fail closed if its ordinary priority cannot be set.
  // Linux keeps the scheduler-before-exec contract and therefore the same PID.
  if (process.platform === 'win32' && child.pid) {
    try {
      os.setPriority(child.pid, os.constants.priority.PRIORITY_NORMAL);
      if (os.getPriority(child.pid) !== os.constants.priority.PRIORITY_NORMAL) {
        throw new Error('Child process did not enter normal priority class');
      }
    } catch (error) {
      child.kill();
      throw error;
    }
  }
  return child;
}

module.exports = { ordinaryCommand, spawnOrdinary };
