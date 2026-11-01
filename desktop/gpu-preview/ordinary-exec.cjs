'use strict';
// Acceptance only: adjust this new child before exec, never an existing PID.
const source = 'import os,sys\nos.sched_setscheduler(0,os.SCHED_OTHER,os.sched_param(0))\nos.setpriority(os.PRIO_PROCESS,0,0)\nassert os.sched_getscheduler(0)==os.SCHED_OTHER and os.getpriority(os.PRIO_PROCESS,0)==0\nos.execvp(sys.argv[1],sys.argv[1:])';
function ordinaryCommand(executable,args){return {executable:'python3',args:['-c',source,executable,...args]};}
module.exports={ordinaryCommand};
