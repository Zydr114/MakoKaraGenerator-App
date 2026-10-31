// Production-editor P3 runner and offline analyzer. All pixel readbacks/screenshots
// belong in separate correctness runs; this runner records bounded telemetry only.
// Build: node scripts/app.mjs build:desktop
// First integration: node scripts/gpu-preview-p3.mjs --debug --provider runtime
// Finite real-content acceptance: node scripts/gpu-preview-p3.mjs --finite --provider runtime
// Quiet transport benchmark: node scripts/gpu-preview-p3.mjs --provider synthetic
// Reanalyze: node scripts/gpu-preview-p3.mjs --analyze .local/gpu-preview-p3/<run>
// --connect reuses an explicitly CDP-enabled App without owning its shutdown.
// Formal timing excludes fixture creation, correctness probes and pixel readbacks.
import { spawn } from 'node:child_process';
import { appendFile, mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import { resolve, dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { parseArgs } from 'node:util';
import { createHash } from 'node:crypto';
import { release, platform, arch } from 'node:os';
import { Cdp, Page } from '../frontend/scripts/cdp.mjs';
import { verifyDesktopDeployment } from './desktop-build.mjs';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const NS = 1_000_000_000n;
const KEYS = ['streamId','hostInstanceId','producerEpoch','inputGeneration','consumerEpoch','timelineEpoch','poolGeneration','slotId','slotEpoch','frameSeq'];
const pause = ms => new Promise(done => setTimeout(done, ms));
const invariant = (value, message) => { if (!value) throw new Error(message); };
const ns = value => { invariant(typeof value === 'string' && /^(0|[1-9]\d*)$/.test(value), 'Missing canonical monotonic event time'); return BigInt(value); };
export function leaseKey(frame) {
  invariant(KEYS.every(key => frame[key] !== undefined && frame[key] !== null), 'Incomplete frame identity');
  return JSON.stringify(KEYS.map(key => frame[key]));
}
export function percentile(values, fraction) {
  if (!values.length) return null;
  const sorted = [...values].sort((a,b) => a-b);
  return sorted[Math.max(0, Math.ceil(sorted.length*fraction)-1)];
}
function distribution(values) {
  const finite = values.filter(Number.isFinite);
  return {count:finite.length,min:finite.length?Math.min(...finite):null,p50:percentile(finite,.5),p95:percentile(finite,.95),p99:percentile(finite,.99),max:finite.length?Math.max(...finite):null};
}
const lowerBound = (values, target) => {
  let low=0, high=values.length;
  while(low<high){const middle=(low+high)>>>1;if(values[middle]<target)low=middle+1;else high=middle;}
  return low;
};
// A half-open [start,start+5s) count changes only at an event entering or leaving.
// Check every exact nanosecond boundary, including immediately after an event.
export function slidingWindows(times, start, end, window=5n*NS) {
  invariant(end>start && window>0n,'Invalid measurement interval');
  if(end-start<window)return {complete:false,reason:'Measurement shorter than sliding window'};
  const sorted=[...times].sort((a,b)=>a<b?-1:a>b?1:0);
  const last=end-window, candidates=new Set([start,last]);
  for(const time of sorted)for(const candidate of [time+1n,time-window+1n])
    if(candidate>=start&&candidate<=last)candidates.add(candidate);
  let minimum=Infinity, worst=start;
  for(const candidate of candidates){
    const count=lowerBound(sorted,candidate+window)-lowerBound(sorted,candidate);
    if(count<minimum){minimum=count;worst=candidate;}
  }
  return {complete:true,windowSeconds:Number(window)/1e9,minimumUniqueFrames:minimum,
    minimumFps:minimum/(Number(window)/1e9),worstStartMonotonicNs:String(worst),checkedEventBoundaries:candidates.size};
}
function regression(points) {
  if(points.length<2)return {count:points.length,slopePerSecond:null,delta:null,min:null,max:null};
  const origin=points[0][0], values=points.map(([t,y])=>[Number(t-origin)/1e9,y]);
  const meanX=values.reduce((sum,[x])=>sum+x,0)/values.length, meanY=values.reduce((sum,[,y])=>sum+y,0)/values.length;
  const denominator=values.reduce((sum,[x])=>sum+(x-meanX)**2,0);
  return {count:points.length,slopePerSecond:denominator?values.reduce((sum,[x,y])=>sum+(x-meanX)*(y-meanY),0)/denominator:null,
    delta:values.at(-1)[1]-values[0][1],min:Math.min(...values.map(([,y])=>y)),max:Math.max(...values.map(([,y])=>y)),first:values[0][1],last:values.at(-1)[1]};
}
export function analyze(records, manifest={}) {
  const frames=new Map(), snapshots=[], phases=[], actions=[], gaps=[];
  let previousAck, previousMainPid;
  for(const record of records){
    if(record.type==='phase')phases.push(record);
    if(record.type==='action')actions.push(record);
    if(record.type!=='snapshot')continue;
    const snapshot=record.snapshot;
    invariant(snapshot&&Array.isArray(snapshot.samples),'Missing bounded completion samples');
    const observed=ns(snapshot.nowMonotonicNs);
    if(snapshot.sampleCursorExpired)gaps.push(`Completion cursor expired at ${observed}`);
    if(previousMainPid!==undefined&&snapshot.mainPid!==previousMainPid)gaps.push('Electron main process changed during the run');
    previousMainPid=snapshot.mainPid;
    let added=0;
    const totalUncertainty=snapshot.clock?.totalUncertaintyNs;
    for(const sample of snapshot.samples){
      const key=leaseKey(sample), completed=ns(sample.completedMonotonicNs), previous=frames.get(key);
      invariant(completed<=observed,'Frame completion is after snapshot time');
      const enriched={...sample,...(sample.totalClockUncertaintyMs===undefined&&totalUncertainty!==undefined?{totalClockUncertaintyMs:Number(ns(totalUncertainty))/1e6}:{})};
      if(previous){
        invariant(previous.completedMonotonicNs===sample.completedMonotonicNs,'Completion timestamp changed for one lease');
        frames.set(key,{...previous,...enriched,drawn:previous.drawn===true||sample.drawn===true});
      }else{frames.set(key,enriched);added++;}
    }
    if(previousAck!==undefined){
      const delta=snapshot.acknowledgedUnique-previousAck;
      if(!Number.isSafeInteger(delta)||delta<0||delta>added)gaps.push(`Completion ring gap or reset at ${observed}`);
    }
    previousAck=snapshot.acknowledgedUnique;
    snapshots.push({...record,time:observed});
  }
  const phaseReports=phases.map(phase=>{
    const start=ns(phase.startMonotonicNs),end=ns(phase.endMonotonicNs),duration=Number(end-start)/1e9;
    const candidates=[...frames.values()].filter(frame=>ns(frame.completedMonotonicNs)>=start&&ns(frame.completedMonotonicNs)<end&&
      (phase.streamId===undefined||frame.streamId===phase.streamId)&&
      (phase.inputGeneration===undefined||frame.inputGeneration===phase.inputGeneration));
    const delivered=candidates.filter(frame=>frame.drawn===true).sort((a,b)=>ns(a.completedMonotonicNs)<ns(b.completedMonotonicNs)?-1:1);
    const times=delivered.map(frame=>ns(frame.completedMonotonicNs));
    const intervals=times.slice(1).map((value,index)=>Number(value-times[index])/1e6);
    const period=1000/phase.targetFps, expected=duration*phase.targetFps;
    const window=slidingWindows(times,start,end);
    const threshold=Math.ceil(phase.targetFps*5*.98);
    const sampleClocks=delivered.map(frame=>frame.totalClockUncertaintyMs);
    const unknownClock=sampleClocks.filter(value=>!Number.isFinite(value)).length;
    const badProvider=delivered.filter(frame=>frame.provider!==phase.provider).length;
    const readbacks=candidates.filter(frame=>frame.counters?.cpuPixelReadbacks!==0).length;
    const wrongSize=delivered.filter(frame=>frame.width!==phase.width||frame.height!==phase.height).length;
    const clocks=snapshots.filter(record=>record.snapshot.mediaClock).map(record=>record.snapshot.mediaClock);
    const drift=delivered.map(frame=>{
      if(Number.isFinite(frame.signedDriftUs))return frame.signedDriftUs/1000;
      const completion=ns(frame.completedMonotonicNs);
      const clock=clocks.filter(clock=>clock.timelineResetId===frame.timelineResetId&&ns(clock.mainSampleMonotonicNs)<=completion).at(-1);
      if(!clock)return null;
      const master=clock.mediaTimeUs+(clock.mode==='playing'?Number(completion-ns(clock.mainSampleMonotonicNs))/1000*clock.rate:0);
      return (frame.timeUs-master)/1000;
    }).filter(Number.isFinite);
    const age=drift.map(value=>-value);
    const streamSnapshots=snapshots.filter(record=>record.time>=start&&record.time<=end);
    const resourcePids=new Map();
    for(const record of streamSnapshots)for(const proc of record.processes??[]){
      const key=`${proc.pid}:${proc.startTicks}`,points=resourcePids.get(key)??{pid:proc.pid,startTicks:proc.startTicks,role:proc.role,fd:[],rss:[],gpu:[]};
      if(Number.isFinite(proc.fdCount))points.fd.push([record.time,proc.fdCount]);
      if(Number.isFinite(proc.rssBytes))points.rss.push([record.time,proc.rssBytes]);
      if(Number.isFinite(proc.gpuMemoryBytes))points.gpu.push([record.time,proc.gpuMemoryBytes]);
      resourcePids.set(key,points);
    }
    const gate={overall:delivered.length>=Math.ceil(expected*.99),sliding5s:window.complete&&window.minimumUniqueFrames>=threshold,
      intervalP95:percentile(intervals,.95)!==null&&percentile(intervals,.95)<=2*period,
      intervalP99:percentile(intervals,.99)!==null&&percentile(intervals,.99)<=3*period,
      noUnexplainedStall:intervals.every(value=>value<=250),clockRecorded:unknownClock===0&&delivered.length>0,
      exactProvider:badProvider===0&&delivered.length>0,exactSize:wrongSize===0&&delivered.length>0,
      productionFinalReadbacksZero:readbacks===0&&delivered.length>0,
      visibleWindow:streamSnapshots.length>0&&streamSnapshots.every(record=>record.snapshot.rendererVisibility==='visible'),
      poolBounded:streamSnapshots.length>0&&streamSnapshots.every(record=>record.snapshot.outstanding<=3&&record.snapshot.peakOutstanding<=3),
      mainControlsBounded:streamSnapshots.length>0&&streamSnapshots.every(record=>record.snapshot.executingControls<=1&&record.snapshot.pendingControls<=1)};
    return {name:phase.name,provider:phase.provider,width:phase.width,height:phase.height,targetFps:phase.targetFps,durationSeconds:duration,
      uniqueNativeCompletions:candidates.length,uniqueDrawnAndNativeCompleted:delivered.length,undrawnNativeCompletions:candidates.length-delivered.length,
      expectedFrames:expected,uniqueCompleteConsumptionFps:delivered.length/duration,displayedFps:'unmeasured',chromiumInternalCopies:'unmeasured',
      completeRatio:delivered.length/expected,sliding5s:{...window,requiredUniqueFrames:threshold},intervalMs:distribution(intervals),
      stallsOver250ms:intervals.filter(value=>value>250).length,renderMs:distribution(delivered.map(frame=>frame.renderMs)),
      brokerToCompletionMs:distribution(delivered.map(frame=>frame.brokerToCompletionMs)),readyToCompletionMs:distribution(delivered.map(frame=>frame.readyToCompletionMs)),
      totalClockUncertaintyMs:distribution(sampleClocks),signedDriftMs:distribution(drift),ageMs:distribution(age),absoluteDriftMs:distribution(drift.map(Math.abs)),
      workerFlowControl:{status:streamSnapshots.length&&streamSnapshots.every(record=>record.snapshot.workerState)?'recorded':'unmeasured',samples:streamSnapshots.filter(record=>record.snapshot.workerState).map(record=>({monotonicNs:String(record.time),...record.snapshot.workerState})),publicStates:streamSnapshots.filter(record=>record.snapshot.workerPublicState).map(record=>({monotonicNs:String(record.time),...record.snapshot.workerPublicState}))},
      freshness:drift.length===delivered.length&&delivered.length>0?{status:percentile(age,.95)<=2*period&&percentile(drift.map(Math.abs),.95)<=2*period?'candidate-met':'candidate-failed',candidateP95LimitMs:2*period,earlyFrameConstraint:'absolute signed drift p95 must also stay within two periods; negative age never makes an early frame pass',model:'latest calibrated real audio.currentTime sample at or before completion, extrapolated by rate within exact timelineResetId'}:{status:'unmeasured',reason:'Missing calibrated audio master comparison on some frames'},
      resourceTrends:[...resourcePids.values()].map(points=>({pid:points.pid,startTicks:points.startTicks,role:points.role,fd:regression(points.fd),rssBytes:regression(points.rss),gpuMemoryBytes:regression(points.gpu)})),
      gates:gate,throughputPassed:Object.values(gate).every(Boolean)};
  });
  const actionReports=actions.map(action=>({...action,elapsedMs:Number(ns(action.endMonotonicNs)-ns(action.startMonotonicNs))/1e6}));
  const interactions=records.filter(record=>record.type==='interaction').map(action=>{
    const start=ns(action.uiInvocationMonotonicNs);
    const matching=[...frames.values()].filter(frame=>frame.drawn===true&&ns(frame.completedMonotonicNs)>=start&&
      (action.streamId===undefined||frame.streamId===action.streamId)&&
      (action.inputGeneration===undefined||frame.inputGeneration===action.inputGeneration)&&
      (action.sourceRevision===undefined||frame.sourceRevision===action.sourceRevision)&&
      (action.timelineResetId===undefined||frame.timelineResetId===action.timelineResetId)&&
      (action.minimumControlSeq===undefined||frame.controlSeq>=action.minimumControlSeq)).sort((a,b)=>ns(a.completedMonotonicNs)<ns(b.completedMonotonicNs)?-1:1)[0];
    return {...action,firstMatchingCompletedLease:matching?leaseKey(matching):null,
      uiInvocationToMatchingCompletionMs:matching?Number(ns(matching.completedMonotonicNs)-start)/1e6:null,
      uiInvocationClockUncertaintyMs:action.uiInvocationClockUncertaintyMs??null,
      mainReceiveToWorkerAppliedMs:'unmeasured unless separate main-receive and worker-applied timestamps are recorded'};
  });
  const interactionGroups=Object.fromEntries([...new Set(interactions.map(action=>action.kind))].map(kind=>{
    const group=interactions.filter(action=>action.kind===kind),values=group.map(action=>action.uiInvocationToMatchingCompletionMs).filter(Number.isFinite);
    return [kind,{...distribution(values),missing:group.length-values.length,tailSampleCaveat:group.length<100?'fewer than 100 observations; p99 is the nearest-rank observed tail, not a population-tail estimate':null,origin:'CDP invocation bracket mapped to Electron main monotonic; include reported uncertainty',candidateMet:values.length===group.length&&percentile(values,.95)<=150&&percentile(values,.99)<=250}];
  }));
  const lifecycle={attachDetach:{required:20,observed:actionReports.filter(action=>action.kind==='attach-detach'&&action.newGenerationVerified===true&&action.oldWorkerExited===true).length},
    publicResizeSeek:{required:10,observed:actionReports.filter(action=>action.kind==='public-resize-seek'&&action.exactGenerationCompleted===true).length},
    uiResolutionRecreate:{observed:actionReports.filter(action=>action.kind==='ui-resolution-recreate').length}};
  const requirements={
    formalRunClass:manifest.runClass==='formal-p3',runFinished:typeof manifest.finishedAt==='string'&&manifest.runError===undefined,
    matchingDeployment:manifest.deployment?.verified===true,visibleRenderer:manifest.platformEvidence?.renderer?.visibility==='visible',
    runtimeWayland:manifest.platformEvidence?.waylandConfirmed===true,
    phaseMatrix:['720p24','1440p30','1440p30-lifetime'].every(name=>phaseReports.some(phase=>phase.name===name&&phase.provider===(manifest.provider==='synthetic'?'synthetic-gpu':manifest.provider)&&phase.durationSeconds>=(name.endsWith('lifetime')?600:180))),
    throughput:phaseReports.length===3&&phaseReports.every(phase=>phase.throughputPassed),
    freshness:phaseReports.length===3&&phaseReports.every(phase=>phase.freshness.status==='candidate-met'),
    interactions:['pause','seek','latest-input'].every(kind=>interactionGroups[kind]?.count>=20&&interactionGroups[kind]?.candidateMet===true),
    lifecycle:lifecycle.attachDetach.observed>=20&&lifecycle.publicResizeSeek.observed>=10,
    completeEventHistory:gaps.length===0};
  return {schemaVersion:1,sourceCommit:manifest.sourceCommit??null,generatedAt:new Date().toISOString(),runClass:manifest.runClass??'unspecified',
    identity:'stream/host/producer/input/consumer/timeline/pool/slot/slotEpoch/frameSeq',
    counting:'unique canvas-drawn identity intersected with actual native allReferencesReleased; measured at completion timestamp',
    phaseReports,eventGaps:[...new Set(gaps)],actions:actionReports,interactions,interactionGroups,requiredLifecycle:lifecycle,measuredRequirements:requirements,
    conclusion:manifest.runClass!=='formal-p3'?(manifest.runClass??'incomplete-or-failed'):Object.values(requirements).every(Boolean)?'measured-candidate-gates-met; separate correctness/trace/resource interpretation remains required':'incomplete-or-failed',
    p3Acceptance:'not established by timed runner alone; requires separate correctness/trace, Worker pool/control budgets, and lifetime lease/FD/cache review',
    limitations:['No screen scan/displayed FPS measurement','No Chromium internal copy count inferred','RSS/GPU-memory slopes need platform-cache interpretation, not an invented fixed MB threshold','Synthetic and runtime providers must be measured and reported separately']};
}

async function readProc(pid,role) {
  try{
    const directory=`/proc/${pid}`;
    const [identity,status,fds]=await Promise.all([readFile(`${directory}/stat`,'utf8'),readFile(`${directory}/status`,'utf8'),readdir(`${directory}/fd`)]);
    const fields=identity.slice(identity.lastIndexOf(')')+2).split(' '), startTicks=fields[19];
    const clients=new Map();
    for(const fd of fds){
      let info;try{info=await readFile(`${directory}/fdinfo/${fd}`,'utf8');}catch{continue;}
      const client=info.match(/^drm-client-id:\s*(\d+)$/m)?.[1];
      if(!client||clients.has(client))continue;
      const bytes=[...info.matchAll(/^drm-memory-(?:vram|gtt|system):\s*(\d+)\s*(KiB|kB|B)$/mg)].reduce((sum,match)=>sum+Number(match[1])*(match[2]==='B'?1:1024),0);
      clients.set(client,bytes);
    }
    return {pid,role,startTicks,rssBytes:Number(status.match(/^VmRSS:\s*(\d+) kB$/m)?.[1]??0)*1024,fdCount:fds.length,
      gpuMemoryBytes:clients.size?[...clients.values()].reduce((sum,value)=>sum+value,0):null,gpuMemorySource:clients.size?'per-process DRM fdinfo, deduplicated client-id':'unavailable'};
  }catch{return {pid,role,exited:true};}
}
async function processSnapshot(snapshot) {
  const pids=new Map();
  if(snapshot.mainPid)pids.set(snapshot.mainPid,'electron-main');
  if(snapshot.workerPid)pids.set(snapshot.workerPid,'worker');
  for(const proc of snapshot.processes??[])pids.set(proc.pid,proc.type);
  return Promise.all([...pids].map(([pid,role])=>readProc(pid,role)));
}
async function setFile(page,selector,path) {
  const {root}=await page.send('DOM.getDocument');
  const {nodeId}=await page.send('DOM.querySelector',{nodeId:root.nodeId,selector});
  invariant(nodeId,'Missing file input '+selector);
  await page.send('DOM.setFileInputFiles',{nodeId,files:[path]});
}
async function click(page,text,selector='mdui-button,button') {
  const search=`[...document.querySelectorAll(${JSON.stringify(selector)})].find(element=>element.textContent.trim()===${JSON.stringify(text)}&&!element.disabled)`;
  await page.waitFor(`!!(${search})`,`UI ${text}`,120_000);
  await page.eval(`(${search}).click()`);
}
async function seekTimeline(page,xOffset) {
  const point=await page.eval(`(()=>{const rect=document.querySelector('canvas[aria-label="时间轴"]').getBoundingClientRect();return {x:rect.x+Math.min(rect.width-2,${xOffset}),y:rect.y+8};})()`);
  await page.send('Input.dispatchMouseEvent',{type:'mousePressed',x:point.x,y:point.y,button:'left',clickCount:1});
  await page.send('Input.dispatchMouseEvent',{type:'mouseReleased',x:point.x,y:point.y,button:'left',clickCount:1});
}
async function makeFixtures(output) {
  const seconds=1800,rate=8000,bytes=seconds*rate*2,header=Buffer.alloc(44),audio=join(output,'silence-1800s.wav');
  header.write('RIFF');header.writeUInt32LE(36+bytes,4);header.write('WAVEfmt ',8);header.writeUInt32LE(16,16);header.writeUInt16LE(1,20);header.writeUInt16LE(1,22);header.writeUInt32LE(rate,24);header.writeUInt32LE(rate*2,28);header.writeUInt16LE(2,32);header.writeUInt16LE(16,34);header.write('data',36);header.writeUInt32LE(bytes,40);
  await writeFile(audio,Buffer.concat([header,Buffer.alloc(bytes)]));
  const lyrics=join(output,'production-lyrics.lrc');
  await writeFile(lyrics,Array.from({length:360},(_,index)=>`[${String(Math.floor(index*5/60)).padStart(2,'0')}:${String(index*5%60).padStart(2,'0')}.00]持续 GPU 预览 · 第 ${index+1} 行 · unique frame consumption`).join('\n'));
  return {audio,lyrics,audioSeconds:seconds,audioSha256:createHash('sha256').update(await readFile(audio)).digest('hex')};
}
async function commandOutput(executable,args) {
  const child=spawn(executable,args,{cwd:ROOT,stdio:['ignore','pipe','pipe']});let output='',error='';
  for await(const chunk of child.stdout)output+=chunk;
  for await(const chunk of child.stderr)error+=chunk;
  const code=await new Promise(done=>{if(child.exitCode!==null)done(child.exitCode);else child.once('exit',done);});
  invariant(code===0,`${executable} metadata command failed: ${error.slice(0,200)}`);return output.trim();
}
async function deploymentEvidence() {
  let current;try{current=JSON.parse(await readFile(join(ROOT,'.local/backend-current.json'),'utf8'));}catch{return {available:false};}
  const candidates=[join(current.dist,'server/MakoKaraGenerator.Backend.Server.dll'),join(current.dist,'worker/MakoKaraGenerator.Backend.Worker.dll'),
    join(current.dist,'worker/libSkiaSharp.so'),join(current.dist,'worker/libmako_gpu_preview.so'),join(current.dist,'desktop/gpu_preview_broker.node'),
    join(current.dist,'deployment.json'),join(current.dist,'gpu-preview-manifest.json'),join(ROOT,'desktop/node_modules/electron/package.json'),join(ROOT,'frontend/dist/index.html')];
  const files=[];
  for(const path of candidates){try{const bytes=await readFile(path);files.push({path:path.replace(ROOT+'/',''),size:bytes.length,sha256:createHash('sha256').update(bytes).digest('hex')});}catch{files.push({path:path.replace(ROOT+'/',''),missing:true});}}
  let verified=false,verificationError,nativeManifest;
  try{nativeManifest=await verifyDesktopDeployment(ROOT,current.dist);verified=true;}catch(error){verificationError=error.message;}
  return {available:true,verified,verificationError,dist:current.dist.replace(ROOT+'/',''),files,nativeManifest};
}
async function actualWaylandEvidence(page,cdp,snapshot) {
  let systemInfo=null,commandLine=null;
  let browserCdp;
  try{const version=await fetch(`http://127.0.0.1:${snapshot.cdpPort}/json/version`).then(response=>response.json());browserCdp=await Cdp.connect(version.webSocketDebuggerUrl);systemInfo=await browserCdp.send('SystemInfo.getInfo');}catch{}finally{browserCdp?.ws.close();}
  try{commandLine=await readFile(`/proc/${snapshot.mainPid}/cmdline`,'utf8').then(value=>value.split('\0').filter(Boolean));}catch{}
  const details=await page.eval(`({visibility:document.visibilityState,userAgent:navigator.userAgent,platform:navigator.platform})`);
  const backend=systemInfo?.gpu?.auxAttributes?.displayType??systemInfo?.gpu?.auxAttributes?.glImplementationParts??null;
  return {renderer:details,gpuAuxAttributes:systemInfo?.gpu?.auxAttributes??null,electronArguments:commandLine,
    actualOzoneBackend:backend??'unmeasured; environment variables alone are not proof',
    environmentObservation:{XDG_SESSION_TYPE:process.env.XDG_SESSION_TYPE??null,WAYLAND_DISPLAY:process.env.WAYLAND_DISPLAY??null},
    waylandConfirmed:typeof backend==='string'&&/wayland/i.test(backend)};
}
async function selectGpu(page) {
  await page.waitFor(`(()=>{const option=document.querySelector('.render-device-option[data-value="gpu"]')?.closest('mdui-menu-item');return option&&!option.disabled;})()`,'Available GPU render backend',120_000);
  await page.eval(`document.querySelector('.render-device-option[data-value="gpu"]').closest('mdui-menu-item').click()`);
}
async function createProject(page,fixture) {
  await click(page,'新建工程');
  await page.waitFor(`!!document.querySelector('input[aria-label="选择音频"]')`,'New project file inputs');
  await setFile(page,'input[aria-label="选择音频"]',fixture.audio);
  await setFile(page,'input[aria-label="选择歌词"]',fixture.lyrics);
  await page.eval(`(()=>{const field=document.querySelector('mdui-text-field[aria-label="工程名"]');field.value='P3 GPU production benchmark';field.dispatchEvent(new Event('input',{bubbles:true}));})()`);
  await click(page,'下一步','.new-project-dialog mdui-button');
  await click(page,'新建','.new-project-dialog mdui-button');
  await page.waitFor(`!!document.querySelector('mdui-navigation-rail-item[value="orchestration"]')`,'Project created',180_000);
  await page.eval(`document.querySelector('mdui-navigation-rail-item[value="orchestration"]').click()`);
  await selectGpu(page);
  await page.waitFor(`!!document.querySelector('select[aria-label="GPU 预览规格"]')`,'Production GPU preview',180_000);
  await page.waitFor(`!!document.querySelector('[aria-label="播放"]:not([disabled])')`,'Real audio playback ready',180_000);
}
async function connectPage(port) {
  const deadline=Date.now()+90_000;
  while(Date.now()<deadline){
    try{
      const targets=await fetch(`http://127.0.0.1:${port}/json/list`).then(response=>response.json());
      const target=targets.find(value=>value.type==='page'&&/^http:\/\/(127\.0\.0\.1|localhost):/.test(value.url));
      if(target){const cdp=await Cdp.connect(target.webSocketDebuggerUrl);const page=new Page(cdp,undefined);await page.send('Runtime.enable');await page.send('Page.enable');return {cdp,page};}
    }catch{}
    await pause(100);
  }
  throw new Error('Loopback Electron CDP page did not become ready');
}
async function run(values) {
  const output=resolve(values.output??join(ROOT,'.local/gpu-preview-p3',new Date().toISOString().replaceAll(':','-')));
  invariant(output.startsWith(join(ROOT,'.local')+'/'),'P3 output must stay in App .local');
  await mkdir(output,{recursive:true,mode:0o700});
  const manifest={schemaVersion:1,startedAt:new Date().toISOString(),platform:platform(),arch:arch(),kernel:release(),provider:values.provider,
    runClass:values.debug?'short-debug-not-performance-acceptance':values.finite?'finite-correctness-not-performance-acceptance':'formal-p3',
    warmupSeconds:values.debug||values.finite?2:10,measureSeconds:values.debug||values.finite?5:180,stabilitySeconds:values.debug||values.finite?5:600,readbacks:'none; no screenshots/pixel extraction in timed runner',
    phases:[{name:'720p24',width:1280,height:720,targetFps:24},{name:'1440p30',width:2560,height:1440,targetFps:30}],
    thresholds:{overallRatio:.99,sliding5sRatio:.98,intervalP95Periods:2,intervalP99Periods:3,maxUnexplainedStallMs:250,interactionP95Ms:150,interactionP99Ms:250},
    lifecycle:{attachDetach:20,publicResizeSeek:10,uiResolutionRecreate:10},command:process.argv};
  manifest.sourceCommit=await commandOutput('git',['rev-parse','HEAD']);
  manifest.dirtyPaths=(await commandOutput('git',['status','--short'])).split('\n').filter(Boolean);
  manifest.deployment=await deploymentEvidence();
  await writeFile(join(output,'manifest.json'),JSON.stringify(manifest,null,2)+'\n');
  if(values['dry-run']){console.log(JSON.stringify({output,plan:manifest}));return;}
  invariant(manifest.deployment.verified&&manifest.deployment.files.every(file=>!file.missing),'Matched desktop deployment is missing/stale: build:desktop before a real run');
  const records=[],record=async value=>{records.push(value);await appendFile(join(output,'events.jsonl'),JSON.stringify(value)+'\n');};
  let child,cdp,page;
  const seenSamples=new Map();
  const observedSamples=new Map();
  let sampleRevision=0;
  const sample=async()=>{
    const snapshot=await page.eval(`window.makoDesktopPreview.readPreviewState(${sampleRevision}).then(snapshot=>({...snapshot,rendererVisibility:document.visibilityState}))`);
    ns(snapshot.nowMonotonicNs);
    invariant(!snapshot.sampleCursorExpired,'Native completion telemetry cursor expired; evidence is incomplete');
    sampleRevision=snapshot.sampleRevision;
    // Persist each complete event once, plus actual late drawn updates; the bridge
    // returns a bounded history so repeated samples must never multiply counts.
    let workerPublicState;
    try{workerPublicState=await page.eval(`window.makoGpuPreviewAcceptance?.read()`);}catch{}
    const changed=[];
    for(const frame of snapshot.samples){const key=leaseKey(frame),serialized=JSON.stringify(frame);observedSamples.set(key,frame);if(seenSamples.get(key)!==serialized){seenSamples.set(key,serialized);changed.push(frame);}}
    await record({type:'snapshot',snapshot:{...snapshot,...(workerPublicState?{workerPublicState}:{}),samples:changed},processes:await processSnapshot(snapshot)});
    return {...snapshot,latestSample:[...observedSamples.values()].at(-1)};
  };
  const mainClockBracket=async()=>{
    const start=process.hrtime.bigint();
    const main=ns(await page.eval(`window.makoDesktopPreview.readPreviewState(${sampleRevision}).then(value=>value.nowMonotonicNs)`));
    const end=process.hrtime.bigint();
    return {offset:main-(start+end)/2n,uncertaintyMs:Number(end-start)/2e6};
  };
  const invokeInteraction=async(kind,action)=>{
    const before=await sample(),bracket=await mainClockBracket();
    const uncertaintyMs=bracket.uncertaintyMs;
    const invocation=process.hrtime.bigint()+bracket.offset;
    await action();
    const deadline=Date.now()+10_000;
    let after;
    do{after=await sample();if(after.mediaClock&&(kind==='pause'?after.mediaClock.mode==='paused':after.mediaClock.timelineResetId!==before.mediaClock?.timelineResetId))break;await pause(20);}while(Date.now()<deadline);
    invariant(after.mediaClock&&(kind==='pause'?after.mediaClock.mode==='paused':after.mediaClock.timelineResetId!==before.mediaClock?.timelineResetId),'Interaction media-clock transition did not apply');
    const minControl=after.mediaClock.controlSeq;
    invariant(Number.isSafeInteger(minControl),'Missing authoritative control sequence for interaction');
    await record({type:'interaction',kind,uiInvocationMonotonicNs:String(invocation),uiInvocationClockUncertaintyMs:uncertaintyMs,
      streamId:after.activeStream?.streamId,timelineResetId:after.mediaClock.timelineResetId,
      minimumControlSeq:minControl,controlObservationMonotonicNs:after.nowMonotonicNs});
    const expectedStream=after.activeStream?.streamId,expectedReset=after.mediaClock.timelineResetId;
    const matching=()=>[...observedSamples.values()].some(frame=>frame.drawn===true&&ns(frame.completedMonotonicNs)>=invocation&&
      frame.streamId===expectedStream&&frame.timelineResetId===expectedReset&&frame.controlSeq>=minControl);
    while(!matching()&&Date.now()<deadline){await pause(20);after=await sample();}
    invariant(matching(),'Interaction did not yield a matching drawn/native-completed frame');
    return after;
  };
  const waitFrames=async(previous,count=3,predicate=()=>true)=>{
    const deadline=Date.now()+30_000;
    while(Date.now()<deadline){const state=await sample();if(state.completedDrawnUnique>=previous+count&&predicate(state))return state;await pause(100);}
    throw new Error('Production unique drawn/native completion did not advance');
  };
  try{
    if(!values.connect){
      child=spawn(process.execPath,[join(ROOT,'scripts/app.mjs'),'desktop','--port',values.port,'--cdp-port',values['cdp-port']],
        {cwd:ROOT,env:{...process.env,...(values.provider==='synthetic'?{MAKO_BACKEND_GPU_PREVIEW_BENCHMARK:'1'}:{})},stdio:['ignore','ignore','inherit']});
      child.once('error',()=>{});
    }
    ({cdp,page}=await connectPage(Number(values['cdp-port'])));
    await page.waitFor(`typeof window.makoDesktopPreview?.readPreviewState==='function'`,'Desktop production bridge');
    manifest.platformEvidence=await actualWaylandEvidence(page,cdp,{...await sample(),cdpPort:values['cdp-port']});
    if(!values['reuse-project'])manifest.fixture=await makeFixtures(output).then(async fixture=>{await createProject(page,fixture);return fixture;});
    if(values['reuse-project'])await selectGpu(page);
    await writeFile(join(output,'manifest.json'),JSON.stringify(manifest,null,2)+'\n');
    await page.eval(`(()=>{window.__makoP3Events=[];window.makoDesktopPreview.onPreviewState(event=>{window.__makoP3Events.push({event,rendererPerformanceMs:performance.now()});if(window.__makoP3Events.length>4096)window.__makoP3Events.shift();});})()`);
    await page.eval(`document.querySelector('[aria-label="播放"]')?.click()`);
    await page.waitFor(`!!document.querySelector('[aria-label="暂停"]')`,'Audio master playing');
    for(const phase of manifest.phases){
      await page.eval(`(()=>{const select=document.querySelector('select[aria-label="GPU 预览规格"]');select.value=${JSON.stringify(phase.name)};select.dispatchEvent(new Event('change',{bubbles:true}));})()`);
      const before=await sample();await waitFrames(before.completedDrawnUnique,3,state=>state.latestSample?.width===phase.width&&state.latestSample?.height===phase.height&&state.mediaClock?.targetFps===phase.targetFps);
      const warmupUntil=Date.now()+manifest.warmupSeconds*1000;while(Date.now()<warmupUntil){await sample();await pause(1000);}
      const start=await sample();
      const until=Date.now()+manifest.measureSeconds*1000;while(Date.now()<until){await pause(Math.min(1000,until-Date.now()));await sample();}
      const end=await sample();await record({type:'phase',...phase,provider:values.provider==='synthetic'?'synthetic-gpu':'runtime',
        streamId:start.activeStream?.streamId,inputGeneration:start.activeStream?.inputGeneration,startMonotonicNs:start.nowMonotonicNs,endMonotonicNs:end.nowMonotonicNs});
      if(!values.debug&&!values.finite){const result=analyze(records,manifest).phaseReports.at(-1);invariant(result.throughputPassed&&result.freshness.status==='candidate-met',`Formal phase ${phase.name} failed throughput/freshness candidate gates; long matrix stopped`);}
    }
    // Explicit interactions are separate from the uninterrupted throughput phases.
    for(let index=0;index<(values.debug?1:20);index++){
      await invokeInteraction('pause',()=>page.eval(`document.querySelector('[aria-label="暂停"]').click()`));
      await invokeInteraction('seek',()=>seekTimeline(page,30+index*9));
      await page.eval(`document.querySelector('[aria-label="播放"]').click()`);
      const before=await sample();await waitFrames(before.completedDrawnUnique,2);
    }
    invariant(await page.eval(`typeof window.makoGpuPreviewAcceptance?.setBackground==='function'`),'Real project edit benchmark hook unavailable');
    for(let index=0;index<(values.debug?1:20);index++){
      await sample();
      const bracket=await mainClockBracket(),invocation=process.hrtime.bigint()+bracket.offset;
      const old=await page.eval('window.makoGpuPreviewAcceptance.current()');
      const edited=await page.eval(`window.makoGpuPreviewAcceptance.setBackground(${index%2?24:48},16,32,255)`);
      const deadline=Date.now()+30_000;let current;
      do{current=await page.eval('window.makoGpuPreviewAcceptance.current()');if(current?.source?.sourceRevision===edited.sourceRevision&&current.inputGeneration!==old.inputGeneration)break;await sample();await pause(20);}while(Date.now()<deadline);
      invariant(current?.streamId===old.streamId&&current.source.sourceRevision===edited.sourceRevision&&current.inputGeneration!==old.inputGeneration,'Real project input did not replace exact public generation');
      await record({type:'interaction',kind:'latest-input',uiInvocationMonotonicNs:String(invocation),uiInvocationClockUncertaintyMs:bracket.uncertaintyMs,
        streamId:current.streamId,inputGeneration:current.inputGeneration,expectedSourceRevision:edited.sourceRevision,previousInputGeneration:old.inputGeneration});
      const matching=()=>[...observedSamples.values()].some(frame=>frame.drawn===true&&ns(frame.completedMonotonicNs)>=invocation&&frame.streamId===current.streamId&&frame.inputGeneration===current.inputGeneration);
      while(!matching()&&Date.now()<deadline){await pause(20);await sample();}
      invariant(matching(),'Latest real input did not yield an exact-generation native-completed drawn frame');
    }
    // A resolution selector replacement is reported separately from public resize.
    for(let index=0;index<(values.debug?1:10);index++){
      const before=await sample(),quality=index%2?'1440p30':'720p24';
      await page.eval(`(()=>{const select=document.querySelector('select[aria-label="GPU 预览规格"]');select.value=${JSON.stringify(quality)};select.dispatchEvent(new Event('change',{bubbles:true}));})()`);
      const after=await waitFrames(before.completedDrawnUnique);
      await record({type:'action',kind:'ui-resolution-recreate',index,startMonotonicNs:before.nowMonotonicNs,endMonotonicNs:after.nowMonotonicNs,previousWorkerPid:before.workerPid,nextWorkerPid:after.workerPid});
    }
    for(let index=0;index<(values.debug?1:20);index++){
      const before=await sample();
      await page.eval(`document.querySelector('mdui-navigation-rail-item[value="library"]').click()`);
      await page.waitFor(`!document.querySelector('#mako-gpu-preview')`,'Preview detached by actual page navigation');
    await page.eval(`document.querySelector('mdui-navigation-rail-item[value="orchestration"]').click()`);
      const after=await waitFrames(before.completedDrawnUnique);
      const oldIdentity=before.latestSample,newIdentity=after.latestSample;
      invariant(oldIdentity?.streamId!==newIdentity?.streamId||oldIdentity?.inputGeneration!==newIdentity?.inputGeneration,'Attach cycle replayed the same claimed stream generation');
      await record({type:'action',kind:'attach-detach',index,startMonotonicNs:before.nowMonotonicNs,endMonotonicNs:after.nowMonotonicNs,
        previousStreamId:oldIdentity?.streamId,nextStreamId:newIdentity?.streamId,previousWorkerPid:before.workerPid,nextWorkerPid:after.workerPid,
        newGenerationVerified:true,oldWorkerExited:before.workerPid?(await readProc(before.workerPid,'retired-worker')).exited===true:false,
        previousWorkerAfter:before.workerPid?await readProc(before.workerPid,'retired-worker'):null});
    }
    // This hook must route through the existing public client and controller CAS;
    // it is deliberately never emulated by replaying a claimed old generation.
    invariant(await page.eval(`typeof window.makoGpuPreviewAcceptance?.resize==='function'`),'Public resize benchmark hook is unavailable; UI recreation does not substitute for protocol resize');
    for(let index=0;index<(values.debug?1:10);index++){
      const before=await sample();
      const old=await page.eval('window.makoGpuPreviewAcceptance.current()');
      const targetWidth=old.size.width===1280?2560:1280,targetHeight=targetWidth===1280?720:1440;
      const resized=await page.eval(`window.makoGpuPreviewAcceptance.resize(${targetWidth},${targetHeight})`);
      invariant(old.streamId===resized.streamId&&old.inputGeneration!==resized.inputGeneration,'Resize did not preserve stream and replace input generation');
      await seekTimeline(page,20+index*7);
      const after=await waitFrames(before.completedDrawnUnique,3,state=>state.latestSample?.streamId===resized.streamId&&state.latestSample?.inputGeneration===resized.inputGeneration);
      await record({type:'action',kind:'public-resize-seek',exactGenerationCompleted:true,index,startMonotonicNs:before.nowMonotonicNs,endMonotonicNs:after.nowMonotonicNs,
        streamId:resized.streamId,previousInputGeneration:old.inputGeneration,inputGeneration:resized.inputGeneration,previousPoolGeneration:old.poolGeneration,poolGeneration:resized.poolGeneration});
    }
    await page.eval(`(()=>{const select=document.querySelector('select[aria-label="GPU 预览规格"]');select.value='1440p30';select.dispatchEvent(new Event('change',{bubbles:true}));})()`);
    await page.waitFor(`!!window.makoGpuPreviewAcceptance?.current()`,'Current public stream before stability');
    const stabilityPublic=await page.eval('window.makoGpuPreviewAcceptance.current()');
    if(stabilityPublic.size.width!==2560||stabilityPublic.size.height!==1440)await page.eval(`window.makoGpuPreviewAcceptance.resize(2560,1440)`);
    await waitFrames((await sample()).completedDrawnUnique,3,state=>state.latestSample?.width===2560&&state.latestSample?.height===1440&&state.latestSample?.provider===(values.provider==='synthetic'?'synthetic-gpu':'runtime')&&state.mediaClock?.targetFps===30);
    const stableWarmup=Date.now()+manifest.warmupSeconds*1000;while(Date.now()<stableWarmup){await sample();await pause(1000);}
    const start=await sample(),until=Date.now()+manifest.stabilitySeconds*1000;
    while(Date.now()<until){await pause(Math.min(1000,until-Date.now()));await sample();}
    const end=await sample();await record({type:'phase',name:'1440p30-lifetime',width:2560,height:1440,targetFps:30,provider:values.provider==='synthetic'?'synthetic-gpu':'runtime',
      streamId:start.activeStream?.streamId,inputGeneration:start.activeStream?.inputGeneration,startMonotonicNs:start.nowMonotonicNs,endMonotonicNs:end.nowMonotonicNs});
    await page.eval(`document.querySelector('[aria-label="暂停"]')?.click()`);
    // Flush delayed drawn reports; original completion times are unchanged.
    for(let index=0;index<3;index++){await pause(1000);await sample();}
    await record({type:'renderer-observations',events:await page.eval('window.__makoP3Events')});
  }catch(error){manifest.runError=error.message;throw error;}finally{
    manifest.finishedAt=new Date().toISOString();
    await writeFile(join(output,'manifest.json'),JSON.stringify(manifest,null,2)+'\n');
    let report;
    try{
      report=analyze(records,manifest);
      await writeFile(join(output,'report.json'),JSON.stringify(report,null,2)+'\n');
    }finally{
      cdp?.ws.close();
      if(child&&child.exitCode===null&&child.signalCode===null){child.kill('SIGINT');await new Promise((done,reject)=>{const timer=setTimeout(()=>reject(new Error('Owned App launcher did not drain within 45 seconds')),45_000);child.once('exit',()=>{clearTimeout(timer);done();});});}
    }
    console.log(JSON.stringify({output,conclusion:report.conclusion}));
  }
}
async function main() {
  const {values}=parseArgs({options:{'cdp-port':{type:'string',default:'9223'},port:{type:'string',default:'5174'},output:{type:'string'},provider:{type:'string',default:'runtime'},
    connect:{type:'boolean'},'reuse-project':{type:'boolean'},'dry-run':{type:'boolean'},debug:{type:'boolean'},finite:{type:'boolean'},analyze:{type:'string'}}});
  invariant(['runtime','synthetic'].includes(values.provider),'provider must be runtime or synthetic');
  if(values.analyze){const directory=resolve(values.analyze);const records=(await readFile(join(directory,'events.jsonl'),'utf8')).trim().split('\n').filter(Boolean).map(line=>JSON.parse(line));
    const manifest=JSON.parse(await readFile(join(directory,'manifest.json'),'utf8'));const report=analyze(records,manifest);await writeFile(join(directory,'report.json'),JSON.stringify(report,null,2)+'\n');console.log(JSON.stringify(report));return;}
  for(const option of ['cdp-port','port'])invariant(/^\d+$/.test(values[option])&&Number(values[option])>=1024&&Number(values[option])<=65535,`Invalid ${option}`);
  await run(values);
}
if(process.argv[1]&&import.meta.url===pathToFileURL(resolve(process.argv[1])).href)main().catch(error=>{console.error(error.message);process.exitCode=1;});
