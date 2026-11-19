// Finite faults in fresh named App-owned profiles; no production fault hooks are added.
import {spawn} from 'node:child_process';
import {open,mkdir,readFile,writeFile} from 'node:fs/promises';
import {createRequire} from 'node:module';
import {resolve,join} from 'node:path';
import {parseArgs} from 'node:util';
import {fileURLToPath} from 'node:url';
import {createHash} from 'node:crypto';
import {createProject,connectPage,makeFixtures} from './gpu-preview-p3.mjs';
import {getProcessIdentity,getProcessIdentities,getProcessTree,stopLauncherGracefully} from './process-identity.mjs';
const require=createRequire(import.meta.url);
const privateFs=require('../desktop/runtime/private-fs.cjs');
export function analyzeFaultCase(entry,text) {
  const lines=text.split(/\r?\n/);
  const messages=lines.filter(x=>x.startsWith('{')).map(x=>{try{return JSON.parse(x)}catch{return {}}});
  const loss=messages.find(x=>x.type==='desktop-injected-loss'),isolation=messages.find(x=>x.type==='desktop-preview-isolated');
  const stopped=lines.filter(line=>line.startsWith('[backend] WEB_BACKEND_STOPPED '))
    .map(line=>{try{return JSON.parse(line.slice('[backend] WEB_BACKEND_STOPPED '.length));}catch{return null;}})
    .filter(message=>Number.isSafeInteger(message?.exitCode)).at(-1);
  const exitLine=text.match(/backend exited (-?\d+)/),serverExit=stopped?.exitCode??(exitLine?Number(exitLine[1]):null);
  const baseline=entry.external?entry.probe?.externalKill?.acknowledgedAtObservation:loss?.acknowledgedAtLoss;
  const finalAck=isolation?.counters?.acknowledgedUnique;
  const ackDelta=Number.isSafeInteger(baseline)&&Number.isSafeInteger(finalAck)?finalAck-baseline:null;
  const gates={probePassed:entry.probe?.passed===true,allOwnedExitedAfterExplicitClose:entry.allOwnedExited===true,
    unknownOutstanding:Number.isSafeInteger(isolation?.unknownOutstanding)&&isolation.unknownOutstanding>=1,
    explicitServerExit:stopped!==undefined||exitLine!==null,expectedServerExit:serverExit===0,
    expectedLauncherExit:entry.launcherExit?.code===0&&entry.launcherExit?.signal===null,
    zeroAcknowledgements:ackDelta===0};
  return {loss,isolation,serverExit,serverAbort:serverExit===-6,
    ...(entry.external?{observationToDetectionAckDelta:ackDelta}:{exactLossToIsolationAckDelta:ackDelta}),gates,passed:Object.values(gates).every(Boolean)};
}
async function run() {
const {values}=parseArgs({options:{output:{type:'string'},prefix:{type:'string'},port:{type:'string',default:'5185'},'cdp-port':{type:'string',default:'9235'},analyze:{type:'string'},'run-source-commit':{type:'string'},kind:{type:'string',default:'all'},'loss-mode':{type:'string',default:'all'}}});
if(values.analyze){
  const source=resolve(values.analyze),original=JSON.parse(await readFile(source,'utf8'));
  const output=resolve(values.output??`${source}.analyzed.json`);
  if(!privateFs.isInsideRoot(join(process.cwd(),'.local'),output)||privateFs.isSamePath(output,source))throw new Error('Offline analysis must use a separate App .local output');
  const cases=[];
  for(const entry of original.cases){const text=await readFile(entry.log,'utf8');cases.push({name:entry.name,external:entry.external,log:entry.log,logSha256:createHash('sha256').update(text).digest('hex'),...analyzeFaultCase(entry,text)});}
  const report={schemaVersion:1,originalRunSourceCommit:values['run-source-commit'],originalReport:source,originalReportSha256:createHash('sha256').update(await readFile(source)).digest('hex'),
    analyzerSource:import.meta.url,analyzerSha256:createHash('sha256').update(await readFile(fileURLToPath(import.meta.url))).digest('hex'),analyzedAt:new Date().toISOString(),
    boundary:'Original run and source hashes are unchanged. External ACK delta is from the before-kill observation to detection, not an exact loss-time baseline.',cases,passed:cases.length>0&&cases.every(x=>x.passed)};
  await writeFile(output,JSON.stringify(report,null,2)+'\n');console.log(JSON.stringify({output,passed:report.passed}));if(!report.passed)process.exitCode=1;return;
}
if(!['all','renderer','worker'].includes(values.kind)||!['all','injected','external'].includes(values['loss-mode']))throw new Error('Invalid fault selection');
const root=process.cwd(),prefix=values.prefix??`fault-${Date.now()}`,output=resolve(values.output??`.local/gpu-preview-lifecycle/${prefix}`);
if(!/^[a-z0-9][a-z0-9-]{0,27}$/.test(prefix)||!privateFs.isInsideRoot(join(root,'.local'),output))throw new Error('Invalid fault acceptance prefix/output');
await mkdir(output,{recursive:true,mode:0o700});
const pause=ms=>new Promise(r=>setTimeout(r,ms));
const proc=pid=>getProcessIdentity(pid);
const tree=pid=>getProcessTree(pid);
async function command(args){const child=spawn(process.execPath,args,{cwd:root,stdio:['ignore','pipe','pipe']});let text='';child.stdout.on('data',b=>text+=b);child.stderr.on('data',b=>text+=b);const code=await new Promise((done,reject)=>{child.once('error',reject);child.once('exit',done)});if(code!==0)throw new Error(`Acceptance subprocess ${code}: ${text}`);return text;}
const fixture=await makeFixtures(output),summary={schemaVersion:1,startedAt:new Date().toISOString(),deployment:JSON.parse(await readFile('.local/backend-current.json','utf8')),cases:[]};
try{
  for(const external of (values['loss-mode']==='all'?[false,true]:[values['loss-mode']==='external']))for(const kind of (values.kind==='all'?['renderer','worker']:[values.kind])){
    const name=`${prefix}-${external?'natural':'injected'}-${kind}`,directory=join(output,name),logPath=join(directory,'desktop.log');await mkdir(directory,{recursive:true});
    const log=await open(logPath,'w',0o600),child=spawn(process.execPath,['scripts/app.mjs','desktop','--session-name',name,'--port',values.port,'--cdp-port',values['cdp-port'],'--slow-consumer-ms','180'],{cwd:root,env:{...process.env,MAKO_APP_SHUTDOWN_STDIN:'1'},stdio:['pipe',log.fd,log.fd]});
    const exited=new Promise((done,reject)=>{child.once('error',reject);child.once('exit',(code,signal)=>done({code,signal}));});let cdp,owned=[];
    const entry={name,kind,external,launcherPid:child.pid,log:logPath};summary.cases.push(entry);
    try{
      const connection=await connectPage(Number(values['cdp-port']));cdp=connection.cdp;const page=connection.page;
      await createProject(page,fixture);await page.eval(`document.querySelector('[aria-label="播放"]')?.click()`);
      await page.waitFor('window.makoDesktopPreview.readPreviewState().then(s=>s.completedDrawnUnique>3)','Real production GPU frame');
      owned=await tree(child.pid);entry.owned=owned;cdp.ws.close();cdp=null;
      await command(['scripts/gpu-preview-lifecycle.mjs','--scenario',`${kind}-loss`,'--cdp-port',values['cdp-port'],'--output',join(directory,'probe'),...(external?['--external-loss','--session-name',name]:[])]);
      entry.probe=JSON.parse(await readFile(join(directory,'probe/report.json'),'utf8'));
      if(child.exitCode!==null||child.signalCode!==null)throw new Error('Application exited before explicit acceptance close');
      entry.explicitClose=true;stopLauncherGracefully(child);
      entry.launcherExit=await Promise.race([exited,pause(30000).then(()=>{throw new Error('Owned launcher did not exit after explicit close');})]);
      const exitDeadline=Date.now()+8000;
      for(;;){
        const byPid=new Map((await getProcessIdentities(owned.map(x=>x.pid))).map(p=>[p.pid,p]));
        entry.ownedAfter=owned.map(x=>byPid.get(x.pid)??{pid:x.pid,exited:true});
        entry.allOwnedExited=entry.ownedAfter.every((p,i)=>p.exited||p.startTicks!==owned[i].startTicks);
        if(entry.allOwnedExited||Date.now()>=exitDeadline)break;
        await pause(150);
      }
      Object.assign(entry,analyzeFaultCase(entry,await readFile(logPath,'utf8')));
      if(!entry.passed)throw new Error(`Fault case failed: ${name}`);
      console.log(JSON.stringify({name,code:entry.isolation.code,unknown:entry.isolation.unknownOutstanding,serverExit:entry.serverExit,allOwnedExited:entry.allOwnedExited}));
    }finally{
      cdp?.ws.close();if(child.exitCode===null&&child.signalCode===null){const current=await proc(child.pid);if(owned.length===0||current.startTicks===owned.find(x=>x.pid===child.pid)?.startTicks)stopLauncherGracefully(child);await exited;}await log.close();
      await writeFile(join(output,'report.json'),JSON.stringify(summary,null,2)+'\n');
    }
  }
}catch(error){summary.error=error.stack;process.exitCode=1;}finally{summary.finishedAt=new Date().toISOString();summary.passed=summary.cases.length>0&&summary.cases.every(x=>x.passed)&&!summary.error;await writeFile(join(output,'report.json'),JSON.stringify(summary,null,2)+'\n');console.log(JSON.stringify({output,passed:summary.passed,error:summary.error}));}

}
if(process.argv[1]&&resolve(process.argv[1])===fileURLToPath(import.meta.url))await run();
