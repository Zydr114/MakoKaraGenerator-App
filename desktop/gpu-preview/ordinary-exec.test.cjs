'use strict';
const {test}=require('node:test');const assert=require('node:assert/strict');const {execFileSync}=require('node:child_process');
const {ordinaryCommand}=require('./ordinary-exec.cjs');
test('explicit acceptance wrapper verifies its new child scheduler before exec without a shell',()=>{
  const marker='space and $ literal';
  const launch=ordinaryCommand('python3',['-c','import os,json,sys;print(json.dumps({"policy":os.sched_getscheduler(0),"nice":os.getpriority(os.PRIO_PROCESS,0),"argument":sys.argv[1]}))',marker]);
  const value=JSON.parse(execFileSync(launch.executable,launch.args,{encoding:'utf8'}));assert.deepEqual(value,{policy:0,nice:0,argument:marker});
});
