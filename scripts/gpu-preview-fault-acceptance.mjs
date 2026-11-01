// Four finite faults in fresh named App-owned profiles; no production fault hooks are added.
import {spawn} from 'node:child_process';
import {open,mkdir,readFile,readdir,writeFile} from 'node:fs/promises';
import {resolve,join} from 'node:path';
import {parseArgs} from 'node:util';
import {createProject,connectPage,makeFixtures} from './gpu-preview-p3.mjs';
const {values}=parseArgs({options:{output:{type:'string'},prefix:{type:'string'},port:{type:'string',default:'5185'},'cdp-port':{type:'string',default:'9235'}}});
const root=process.cwd(),prefix=values.prefix??`fault-${Date.now()}`,output=resolve(values.output??`.local/gpu-preview-lifecycle/${prefix}`);
if(!/^[a-z0-9][a-z0-9-]{0,27}$/.test(prefix)||!output.startsWith(join(root,'.local')+'/'))throw new Error('Invalid fault acceptance prefix/output');
await mkdir(output,{recursive:true,mode:0o700});
const pause=ms=>new Promise(r=>setTimeout(r,ms));
const proc=async pid=>{try{const stat=await readFile(`/proc/${pid}/stat`,'utf8'),f=stat.slice(stat.lastIndexOf(')')+2).split(' ');return {pid,parentPid:Number(f[1]),startTicks:f[19],state:f[0]};}catch(e){if(e.code==='ENOENT')return {pid,exited:true};throw e;}};
const tree=async pid=>{const all=(await Promise.all((await readdir('/proc')).filter(x=>/^\d+$/.test(x)).map(x=>proc(Number(x))))).filter(x=>!x.exited);const result=all.filter(x=>x.pid===pid);for(let i=0;i<result.length;i++)result.push(...all.filter(x=>x.parentPid===result[i].pid&&!result.some(y=>y.pid===x.pid)));return result;};
async function command(args){const child=spawn(process.execPath,args,{cwd:root,stdio:['ignore','pipe','pipe']});let text='';child.stdout.on('data',b=>text+=b);child.stderr.on('data',b=>text+=b);const code=await new Promise((done,reject)=>{child.once('error',reject);child.once('exit',done)});if(code!==0)throw new Error(`Acceptance subprocess ${code}: ${text}`);return text;}
const fixture=await makeFixtures(output),summary={schemaVersion:1,startedAt:new Date().toISOString(),deployment:JSON.parse(await readFile('.local/backend-current.json','utf8')),cases:[]};
try{
  for(const external of [false,true])for(const kind of ['renderer','worker']){
    const name=`${prefix}-${external?'natural':'injected'}-${kind}`,directory=join(output,name),logPath=join(directory,'desktop.log');await mkdir(directory,{recursive:true});
    const log=await open(logPath,'w',0o600),child=spawn(process.execPath,['scripts/app.mjs','desktop','--session-name',name,'--port',values.port,'--cdp-port',values['cdp-port'],'--slow-consumer-ms','180'],{cwd:root,stdio:['ignore',log.fd,log.fd]});
    const exited=new Promise((done,reject)=>{child.once('error',reject);child.once('exit',(code,signal)=>done({code,signal}));});let cdp,owned=[];
    const entry={name,kind,external,launcherPid:child.pid,log:logPath};summary.cases.push(entry);
    try{
      const connection=await connectPage(Number(values['cdp-port']));cdp=connection.cdp;const page=connection.page;
      await createProject(page,fixture);await page.eval(`document.querySelector('[aria-label="播放"]')?.click()`);
      await page.waitFor('window.makoDesktopPreview.readPreviewState().then(s=>s.completedDrawnUnique>3)','Real production GPU frame');
      owned=await tree(child.pid);entry.owned=owned;cdp.ws.close();cdp=null;
      await command(['scripts/gpu-preview-lifecycle.mjs','--scenario',`${kind}-loss`,'--cdp-port',values['cdp-port'],'--output',join(directory,'probe'),...(external?['--external-loss','--session-name',name]:[])]);
      entry.launcherExit=await Promise.race([exited,pause(20000).then(()=>{throw new Error('Owned launcher did not exit');})]);
      entry.probe=JSON.parse(await readFile(join(directory,'probe/report.json'),'utf8'));
      entry.ownedAfter=await Promise.all(owned.map(x=>proc(x.pid)));entry.allOwnedExited=entry.ownedAfter.every((p,i)=>p.exited||p.startTicks!==owned[i].startTicks);
      const text=await readFile(logPath,'utf8'),messages=text.split('\n').filter(x=>x.startsWith('{')).map(x=>{try{return JSON.parse(x)}catch{return {}}});
      entry.loss=messages.find(x=>x.type==='desktop-injected-loss');entry.failStop=messages.find(x=>x.type==='desktop-fail-stop');
      entry.serverExit=Number(text.match(/backend exited (-?\d+)/)?.[1]??0);entry.serverAbort=/backend exited -6/.test(text);
      if(external)entry.observationToDetectionAckDelta=entry.failStop.counters.acknowledgedUnique-entry.probe.externalKill.acknowledgedAtObservation;
      else entry.exactLossToFailStopAckDelta=entry.failStop.counters.acknowledgedUnique-entry.loss.acknowledgedAtLoss;
      entry.passed=entry.probe.passed&&entry.allOwnedExited&&entry.failStop?.unknownOutstanding>=1&&!entry.serverAbort&&(external||entry.exactLossToFailStopAckDelta===0);
      if(!entry.passed)throw new Error(`Fault case failed: ${name}`);
      console.log(JSON.stringify({name,code:entry.failStop.code,unknown:entry.failStop.unknownOutstanding,serverExit:entry.serverExit,allOwnedExited:entry.allOwnedExited}));
    }finally{
      cdp?.ws.close();if(child.exitCode===null&&child.signalCode===null){const current=await proc(child.pid);if(owned.length===0||current.startTicks===owned.find(x=>x.pid===child.pid)?.startTicks)child.kill('SIGTERM');await exited;}await log.close();
      await writeFile(join(output,'report.json'),JSON.stringify(summary,null,2)+'\n');
    }
  }
}catch(error){summary.error=error.stack;process.exitCode=1;}finally{summary.finishedAt=new Date().toISOString();summary.passed=summary.cases.length===4&&summary.cases.every(x=>x.passed)&&!summary.error;await writeFile(join(output,'report.json'),JSON.stringify(summary,null,2)+'\n');console.log(JSON.stringify({output,passed:summary.passed,error:summary.error}));}
