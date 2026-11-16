'use strict';

const fs = require('node:fs');
const path = require('node:path');

// Acceptance only: adjust this new child before exec, never an existing PID.
const posixSource =
  'import os,sys\nos.sched_setscheduler(0,os.SCHED_OTHER,os.sched_param(0))\nos.setpriority(os.PRIO_PROCESS,0,0)\nassert os.sched_getscheduler(0)==os.SCHED_OTHER and os.getpriority(os.PRIO_PROCESS,0)==0\nos.execvp(sys.argv[1],sys.argv[1:])';

const windowsSource = [
  'import ctypes,ctypes.wintypes,subprocess,sys',
  "k=ctypes.WinDLL('kernel32',use_last_error=True)",
  'k.GetCurrentProcess.restype=ctypes.wintypes.HANDLE',
  'k.SetPriorityClass.argtypes=[ctypes.wintypes.HANDLE,ctypes.wintypes.DWORD]',
  'k.SetPriorityClass.restype=ctypes.wintypes.BOOL',
  'k.GetPriorityClass.argtypes=[ctypes.wintypes.HANDLE]',
  'k.GetPriorityClass.restype=ctypes.wintypes.DWORD',
  'h=k.GetCurrentProcess()',
  'assert k.SetPriorityClass(h,0x20),ctypes.WinError(ctypes.get_last_error())',
  'assert k.GetPriorityClass(h)==0x20',
  'sys.exit(subprocess.call(sys.argv[1:]))',
].join('\n');

function resolveWindowsPython() {
  const appsMarker = path.join('microsoft', 'windowsapps').toLowerCase();
  for (const dir of String(process.env.PATH ?? '').split(path.delimiter)) {
    if (!dir || !path.isAbsolute(dir)) continue;
    if (dir.toLowerCase().includes(appsMarker)) continue;
    const candidate = path.join(dir, 'python.exe');
    if (fs.existsSync(candidate)) return candidate;
  }
  return 'python';
}

function ordinaryCommand(executable, args) {
  if (process.platform === 'win32') {
    return { executable: resolveWindowsPython(), args: ['-c', windowsSource, executable, ...args] };
  }
  return { executable: 'python3', args: ['-c', posixSource, executable, ...args] };
}

module.exports = { ordinaryCommand };
