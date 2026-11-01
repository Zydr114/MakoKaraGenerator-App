// Explicit-CDP finite acceptance; never use this script against the user's default session.
import {mkdir,writeFile,readFile,appendFile} from 'node:fs/promises';
import {resolve,join} from 'node:path';
import {parseArgs} from 'node:util';
import {Cdp,Page} from '../frontend/scripts/cdp.mjs';
const {values}=parseArgs({options:{'cdp-port':{type:'string',default:'9234'},scenario:{type:'string'},output:{type:'string'},seconds:{type:'string',default:'10'},mode:{type:'string',default:'playing'}}});
const scenarios=['hidden','slow','renderer-loss','worker-loss','trace'];
if(!scenarios.includes(values.scenario))throw new Error(`Expected scenario ${scenarios.join('|')}`);
const root=process.cwd(),output=resolve(values.output??join(root,'.local/gpu-preview-lifecycle',`${values.scenario}-${Date.now()}`));
if(!output.startsWith(join(root,'.local')+'/'))throw new Error('Evidence must stay in App .local');
await mkdir(output,{recursive:true,mode:0o700});
const targets=await fetch(`http://127.0.0.1:${Number(values['cdp-port'])}/json/list`).then(r=>r.json());
const target=targets.find(t=>t.type==='page'&&/^http:\/\/127\.0\.0\.1:/.test(t.url));
if(!target)throw new Error('No loopback desktop page');
const cdp=await Cdp.connect(target.webSocketDebuggerUrl),page=new Page(cdp);
cdp.ws.addEventListener('close',()=>{for(const p of cdp.pending.values())p.reject(new Error('Owned CDP disconnected'));cdp.pending.clear();});
const wait=ms=>new Promise(r=>setTimeout(r,ms));
const invariant=(value,message)=>{if(!value)throw new Error(message);};
let revision=0;
const sample=async()=>{const s=await page.eval(`window.makoDesktopPreview.readPreviewState(${revision}).then(s=>({...s,visibility:document.visibilityState}))`);revision=s.sampleRevision;await appendFile(join(output,'events.jsonl'),JSON.stringify({type:'snapshot',observedAt:new Date().toISOString(),snapshot:s})+'\n');return s;};
const proc=async pid=>{try{const stat=await readFile(`/proc/${pid}/stat`,'utf8');return {pid,startTicks:stat.slice(stat.lastIndexOf(')')+2).split(' ')[19],state:stat.slice(stat.lastIndexOf(')')+2).split(' ')[0]};}catch(error){if(error.code==='ENOENT')return {pid,exited:true};throw error;}};
const until=async(predicate,timeout=15000)=>{const deadline=Date.now()+timeout;let result;while(Date.now()<deadline){result=await predicate();if(result)return result;await wait(100);}throw new Error('Finite acceptance condition did not converge');};
const report={schemaVersion:1,scenario:values.scenario,startedAt:new Date().toISOString(),cdpPort:Number(values['cdp-port']),readback:'none',evidenceBoundary:'finite injected scenario; displayed FPS and Chromium internal copies remain unmeasured'};
try{
  invariant(['paused','playing'].includes(values.mode),'Expected paused or playing mode');
  await page.eval(values.mode==='paused'?`document.querySelector('[aria-label="暂停"]')?.click()`:`document.querySelector('[aria-label="播放"]')?.click()`);
  const before=await until(async()=>{const s=await sample();return s.activeStream&&s.mediaClock?.mode===values.mode&&(!values.scenario.endsWith('-loss')||s.outstanding>=1)?s:false;});report.before=before;invariant(before.activeStream&&before.completedDrawnUnique>0,'Requires a real, drawn production GPU stream');
  report.ownedIdentities=await Promise.all([...new Set([before.mainPid,before.workerPid,before.rendererPid,...before.processes.map(p=>p.pid)])].map(proc));
  if(values.scenario==='hidden'){
    await page.eval('window.makoDesktopPreview.setAcceptanceVisibility(false)');
    const hidden=await until(async()=>{const s=await sample();return s.visibility==='hidden'&&!s.activeStream&&s.outstanding===0?s:false;});
    report.hidden=hidden;report.oldWorkerAfterHide=await until(async()=>{const s=await proc(before.workerPid);return s.exited?s:false;});
    await wait(500);const quiet=await sample();invariant(quiet.imported===hidden.imported,'Hidden renderer continued importing');
    await page.eval('window.makoDesktopPreview.setAcceptanceVisibility(true)');
    const restored=await until(async()=>{const s=await sample();return s.visibility==='visible'&&s.activeStream&&s.activeStream.streamId!==before.activeStream.streamId&&s.completedDrawnUnique>hidden.completedDrawnUnique?s:false;},30000);
    report.restored=restored;report.passed=true;
  }else if(values.scenario==='slow'){
    const seconds=Number(values.seconds);invariant(Number.isFinite(seconds)&&seconds>=5&&seconds<=60,'Slow sample must be 5..60 seconds');
    const snapshots=[],deadline=Date.now()+seconds*1000;while(Date.now()<deadline){snapshots.push(await sample());await wait(100);}
    const last=snapshots.at(-1);invariant(last.completedDrawnUnique>before.completedDrawnUnique,'Slow consumer did not complete frames');
    invariant(snapshots.every(s=>s.outstanding<=3&&s.peakOutstanding<=3&&s.executingControls<=1&&s.pendingControls<=1),'Slow consumer exceeded bounded pool/mailbox');
    report.after=last;report.maximumOutstanding=Math.max(...snapshots.map(s=>s.outstanding));report.passed=true;
  }else if(values.scenario==='trace'){
    const categories=['viz','gpu','cc','benchmark','toplevel','disabled-by-default-viz.gpu_composite_time','disabled-by-default-devtools.timeline.frame'];
    let complete;const done=new Promise(r=>complete=r);cdp.on(m=>{if(m.method==='Tracing.tracingComplete')complete(m.params);});
    await cdp.send('Tracing.start',{categories:categories.join(','),transferMode:'ReturnAsStream'});
    await wait(10000);await cdp.send('Tracing.end');const result=await done;let trace='';
    for(;;){const part=await cdp.send('IO.read',{handle:result.stream});trace+=part.base64Encoded?Buffer.from(part.data,'base64').toString():part.data;if(part.eof)break;}
    await cdp.send('IO.close',{handle:result.stream});await writeFile(join(output,'chromium-trace.json'),trace);
    const parsed=JSON.parse(trace);report.trace={categories,durationSeconds:10,eventCount:parsed.traceEvents?.length??0,path:'chromium-trace.json',displayedFps:'unmeasured; raw trace retained without deriving a presentation rate'};report.after=await sample();report.passed=report.trace.eventCount>0&&report.after.completedDrawnUnique>=before.completedDrawnUnique+10;
  }else{
    try{await page.eval(`window.makoDesktopPreview.injectAcceptanceFailure(${JSON.stringify(values.scenario)})`);}catch(error){report.injectReturn=error.message;}
    report.ownedAfter=await until(async()=>{const states=await Promise.all(report.ownedIdentities.map(p=>proc(p.pid)));return states.every((s,index)=>s.exited||s.startTicks!==report.ownedIdentities[index].startTicks)?states:false;},30000);
    report.passed=true;report.evidenceBoundary+='; exact unknown-before-kill and ACK suppression require the owned main log';
  }
}catch(error){report.passed=false;report.error=error.stack;process.exitCode=1;}finally{
  report.finishedAt=new Date().toISOString();await writeFile(join(output,'report.json'),JSON.stringify(report,null,2)+'\n');cdp.ws.close();console.log(JSON.stringify({output,passed:report.passed,error:report.error}));
}
