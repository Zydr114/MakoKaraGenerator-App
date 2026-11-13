// Production-editor P3 runner and offline analyzer. All pixel readbacks/screenshots
// belong in separate correctness runs; this runner records bounded telemetry only.
// Build: node scripts/app.mjs build:desktop
// First integration: node scripts/gpu-preview-p3.mjs --debug --provider runtime
// Finite real-content acceptance: node scripts/gpu-preview-p3.mjs --finite --provider runtime
// Quiet transport benchmark: node scripts/gpu-preview-p3.mjs --provider synthetic
// Reanalyze: node scripts/gpu-preview-p3.mjs --analyze .local/gpu-preview-p3/<run>
// SDK2 content: add --v2-samples t01|<names> [--v2-lines 72] to --finite/--debug; lyrics shrink to N lines every 5 s
//   names come from backend/samples/ScriptEffectsV2 or backend/tests/Fixtures/ScriptEffectsV2Perf; --v2-params blurPx=8 rewrites package parameter defaults when packing.
//   --v2-style twoState|bands|strokes|feather edits the template's style through the editor before saving; it can stand alone without --v2-samples.
// Background image: --background-image <png> uploads it through project settings before SDK2 content (default: solid colour only).
// and a UI-built SDK2 line template is applied to all of them before any timed phase.
// G1 targets: add --phases 720p24 --measure-seconds 60 to --finite/--debug (phase subset and per-phase timing; formal runs keep the full matrix).
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
import { shell, ui } from '../frontend/scripts/ui-query.mjs';
import { verifyDesktopDeployment } from './desktop-build.mjs';
import { applyV2Template, v2ParamOverrides, v2SampleNames, v2StyleName } from './gpu-preview-v2-content.mjs';

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
const STREAM_KEYS=['streamId','hostInstanceId','producerEpoch','inputGeneration','consumerEpoch','poolGeneration'];
export function currentCompletedFrames(snapshot,frames,since='0') {
  const active=snapshot.activeStream;
  if(!active)return [];
  return [...frames].filter(frame=>frame.drawn===true&&STREAM_KEYS.every(key=>active[key]!==undefined&&frame[key]===active[key])&&
    ns(frame.completedMonotonicNs)>=ns(since)).sort((a,b)=>ns(a.completedMonotonicNs)<ns(b.completedMonotonicNs)?-1:1);
}
function replacedStream(previous,current) {
  return !!previous&&!!current&&(previous.streamId!==current.streamId||previous.inputGeneration!==current.inputGeneration);
}
export function ozoneArguments(commandLine) {
  return [...new Set([...commandLine.matchAll(/(?:^|[\0\s])(--ozone-platform(?:-hint)?=(?:wayland|x11))(?=[\0\s]|$)/g)].map(match=>match[1]))];
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
export function auditPipelineScheduler(snapshots) {
  const issues=[],identities=new Map();
  for(const [index,record] of snapshots.entries()){
    const state=record.snapshot??{},processes=record.processes??[];
    const expected=[['Browser',state.mainPid],['Tab',state.rendererPid],['worker',state.workerPid],['GPU',state.device?.chromiumGpuPid]];
    for(const [role,pid] of expected){
      const matching=processes.filter(proc=>proc.pid===pid&&proc.role===role);
      const proc=matching[0];
      if(!Number.isSafeInteger(pid)||pid<=0||matching.length!==1||proc.exited||typeof proc.startTicks!=='string'||!/^\d+$/.test(proc.startTicks)){
        issues.push({snapshot:index,role,pid:pid??null,reason:'missing/exited/duplicate exact process identity'});continue;
      }
      const key=String(pid),previous=identities.get(key);
      if(previous!==undefined&&previous!==proc.startTicks)issues.push({snapshot:index,role,pid,reason:'startTicks changed for same PID'});
      identities.set(key,proc.startTicks);
    }
    for(const proc of processes.filter(proc=>!['p3-runner','owned-app-launcher'].includes(proc.role))){
      if(proc.exited||proc.scheduler?.policy!==0||(proc.role==='worker'&&proc.scheduler?.nice!==0))issues.push({snapshot:index,role:proc.role,pid:proc.pid,reason:'pipeline scheduler unavailable or outside ordinary policy/Worker nice'});
    }
  }
  return {snapshotCount:snapshots.length,passed:snapshots.length>0&&issues.length===0,issues,
    boundary:'Each sampled main/renderer/Worker/GPU exact PID plus stable startTicks and role is required; all observed pipeline policy0, Worker nice0. Runner/launcher are observed only; asynchronous brackets do not prove unsampled instants.'};
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
      const enriched={...sample,clockUncertaintyProvenance:sample.clockUncertaintyProvenance??(sample.totalClockUncertaintyMs!==undefined?'native-completion-sample':'legacy-observation-snapshot'),...(sample.totalClockUncertaintyMs===undefined&&totalUncertainty!==undefined?{totalClockUncertaintyMs:Number(ns(totalUncertainty))/1e6}:{})};
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
    const legacyClock=delivered.filter(frame=>frame.clockUncertaintyProvenance!=='native-completion-sample').length;
    const badProvider=delivered.filter(frame=>frame.provider!==phase.provider).length;
    const readbacks=candidates.filter(frame=>frame.counters?.cpuPixelReadbacks!==0).length;
    const wrongSize=delivered.filter(frame=>frame.width!==phase.width||frame.height!==phase.height).length;
    const clocks=snapshots.filter(record=>record.snapshot.mediaClock).map(record=>record.snapshot.mediaClock);
    const driftObservations=delivered.map(frame=>{
      const completion=ns(frame.completedMonotonicNs);
      const clock=clocks.filter(clock=>clock.timelineResetId===frame.timelineResetId&&ns(clock.mainSampleMonotonicNs)<=completion).at(-1);
      if(!clock)return {driftMs:null,uncertaintyMs:null,clockComponentsRecorded:false};
      const master=clock.mediaTimeUs+(clock.mode==='playing'?Number(completion-ns(clock.mainSampleMonotonicNs))/1000*clock.rate:0);
      const driftMs=Number.isFinite(frame.signedDriftUs)?frame.signedDriftUs/1000:(frame.timeUs-master)/1000;
      const split=clock.rendererUncertaintyNs!==undefined&&Number.isFinite(frame.clockUncertaintyMs);
      // Renderer->main belongs to the selected audio sample; Worker->main belongs
      // to this completed frame. The full completion bound also protects a later
      // mapping expansion. A worker component is never summed twice.
      const selectedBound=split?frame.clockUncertaintyMs+Number(ns(clock.rendererUncertaintyNs))/1e6:
        clock.uncertaintyNs===undefined?null:Number(ns(clock.uncertaintyNs))/1e6;
      return {driftMs,uncertaintyMs:Number.isFinite(selectedBound)&&Number.isFinite(frame.totalClockUncertaintyMs)?Math.max(frame.totalClockUncertaintyMs,selectedBound):null,clockComponentsRecorded:split};
    });
    const drift=driftObservations.map(value=>value.driftMs).filter(Number.isFinite);
    const age=drift.map(value=>-value);
    const freshnessUpperBound=driftObservations.map(value=>Number.isFinite(value.driftMs)&&Number.isFinite(value.uncertaintyMs)?Math.abs(value.driftMs)+value.uncertaintyMs:null).filter(Number.isFinite);
    const streamSnapshots=snapshots.filter(record=>record.time>=start&&record.time<=end);
    const resourcePids=new Map();
    for(const record of streamSnapshots)for(const proc of record.processes??[]){
      const key=`${proc.pid}:${proc.startTicks}`,points=resourcePids.get(key)??{pid:proc.pid,startTicks:proc.startTicks,role:proc.role,fd:[],rss:[],gpu:[]};
      const observedTime=record.processObservationBracket?(ns(record.processObservationBracket.startMonotonicNs)+ns(record.processObservationBracket.endMonotonicNs))/2n:record.time;
      if(Number.isFinite(proc.fdCount))points.fd.push([observedTime,proc.fdCount]);
      if(Number.isFinite(proc.rssBytes))points.rss.push([observedTime,proc.rssBytes]);
      if(Number.isFinite(proc.gpuMemoryBytes))points.gpu.push([observedTime,proc.gpuMemoryBytes]);
      resourcePids.set(key,points);
    }
    const schedulerCoverage=auditPipelineScheduler(streamSnapshots);
    const schedulerObserved=schedulerCoverage.passed;
    const workerStates=streamSnapshots.filter(record=>record.snapshot.workerState).map(record=>record.snapshot.workerState);
    const workerObserved=streamSnapshots.filter(record=>record.snapshot.workerState).map(record=>({time:record.time,state:record.snapshot.workerState}));
    const workerIdentity=workerObserved.length>0&&workerObserved.every(({state})=>state.streamId===phase.streamId&&state.inputGeneration===phase.inputGeneration);
    const workerTimeValid=workerObserved.length>0&&workerObserved.every(({time,state})=>typeof state.mainObservedMonotonicNs==='string'&&/^(0|[1-9]\d*)$/.test(state.mainObservedMonotonicNs)&&BigInt(state.mainObservedMonotonicNs)<=time);
    const workerUpdates=workerTimeValid?[...new Set(workerObserved.map(({state})=>state.mainObservedMonotonicNs))].map(BigInt).sort((a,b)=>a<b?-1:a>b?1:0):[];
    const workerAges=workerTimeValid?workerObserved.map(({time,state})=>Number(time-ns(state.mainObservedMonotonicNs))/1e6):[];
    const workerGaps=workerUpdates.slice(1).map((value,index)=>Number(value-workerUpdates[index])/1e6);
    const workerFreshness={uniqueUpdates:workerUpdates.length,maxAgeMs:workerAges.length?Math.max(...workerAges):null,maxGapMs:workerGaps.length?Math.max(...workerGaps):null,policy:'1Hz state requests; unique main-observed native state updates, max age/gap 3000ms, phase endpoint coverage within 2000ms',identityMatches:workerIdentity,covered:workerUpdates.length>0&&workerUpdates[0]<=start+2n*NS&&workerUpdates.at(-1)>=end-2n*NS};
    const workerTelemetryCurrent=workerIdentity&&workerTimeValid&&workerFreshness.covered&&workerAges.every(age=>age<=3000)&&workerGaps.every(gap=>gap<=3000);
    const workerBudgetGate=workerTelemetryCurrent&&workerStates.every(state=>state.slotCount===3&&Number.isSafeInteger(state.actualAllocationBytes)&&state.actualAllocationBytes>0&&state.actualAllocationBytes<=64*1024**2&&Number.isSafeInteger(state.retainedAllocationBytes)&&state.retainedAllocationBytes>=state.actualAllocationBytes&&state.retainedAllocationBytes<=64*1024**2&&state.executingRender<=1&&state.pendingControls<=1&&state.pendingRenderTargets<=1&&state.outstanding<=3&&state.peaks?.executingRender<=1&&state.peaks?.pendingControls<=1&&state.peaks?.pendingRenderTargets<=1&&state.peaks?.outstanding<=3);
    const gate={overall:delivered.length>=Math.ceil(expected*.99),sliding5s:window.complete&&window.minimumUniqueFrames>=threshold,
      intervalP95:percentile(intervals,.95)!==null&&percentile(intervals,.95)<=2*period,
      intervalP99:percentile(intervals,.99)!==null&&percentile(intervals,.99)<=3*period,
      noUnexplainedStall:intervals.every(value=>value<=250),clockRecorded:unknownClock===0&&delivered.length>0,contemporaneousClockRecorded:legacyClock===0&&delivered.length>0,
      exactProvider:badProvider===0&&delivered.length>0,exactSize:wrongSize===0&&delivered.length>0,
      productionFinalReadbacksZero:readbacks===0&&delivered.length>0,
      schedulerOtherObserved:schedulerObserved,
      workerPoolAndRenderControlBudgets:workerBudgetGate,selectedAudioClockComponentsRecorded:driftObservations.length>0&&driftObservations.every(value=>value.clockComponentsRecorded),
      visibleWindow:streamSnapshots.length>0&&streamSnapshots.every(record=>record.snapshot.rendererVisibility==='visible'),
      poolBounded:streamSnapshots.length>0&&streamSnapshots.every(record=>record.snapshot.outstanding<=3&&record.snapshot.peakOutstanding<=3),
      mainControlsBounded:streamSnapshots.length>0&&streamSnapshots.every(record=>record.snapshot.executingControls<=1&&record.snapshot.pendingControls<=1)};
    return {name:phase.name,provider:phase.provider,width:phase.width,height:phase.height,targetFps:phase.targetFps,durationSeconds:duration,
      uniqueNativeCompletions:candidates.length,uniqueDrawnAndNativeCompleted:delivered.length,undrawnNativeCompletions:candidates.length-delivered.length,
      expectedFrames:expected,uniqueCompleteConsumptionFps:delivered.length/duration,displayedFps:'unmeasured',chromiumInternalCopies:'unmeasured',
      completeRatio:delivered.length/expected,sliding5s:{...window,requiredUniqueFrames:threshold},intervalMs:distribution(intervals),
      stallsOver250ms:intervals.filter(value=>value>250).length,renderMs:distribution(delivered.map(frame=>frame.renderMs)),
      brokerToCompletionMs:distribution(delivered.map(frame=>frame.brokerToCompletionMs)),readyToBrokerMs:distribution(delivered.map(frame=>frame.readyToBrokerMs)),readyToCompletionMs:distribution(delivered.map(frame=>frame.readyToCompletionMs)),
      readyToCompletionUpperBoundMs:distribution(delivered.map(frame=>Number.isFinite(frame.readyToCompletionMs)&&Number.isFinite(frame.clockUncertaintyMs)?frame.readyToCompletionMs+frame.clockUncertaintyMs:null)),
      readyToCompletionBoundary:'Worker frame-ready → actual native allReferencesReleased; includes scheduling/import/consumer lifetime and Worker-to-main clock uncertainty, no screen scan or pure GPU-copy inference',
      totalClockUncertaintyMs:distribution(sampleClocks),signedDriftMs:distribution(drift),ageMs:distribution(age),absoluteDriftMs:distribution(drift.map(Math.abs)),absoluteDriftUpperBoundMs:distribution(freshnessUpperBound),
      schedulerCoverage,
      displayGeometryObservations:streamSnapshots.map(record=>({monotonicNs:String(record.time),...record.snapshot.displayGeometry})),
      workerFlowControl:{freshness:workerFreshness,status:streamSnapshots.length&&streamSnapshots.every(record=>record.snapshot.workerState)?'recorded':'unmeasured',samples:streamSnapshots.filter(record=>record.snapshot.workerState).map(record=>({monotonicNs:String(record.time),...record.snapshot.workerState})),publicStates:streamSnapshots.filter(record=>record.snapshot.workerPublicState).map(record=>({monotonicNs:String(record.time),...record.snapshot.workerPublicState}))},
      freshness:drift.length===delivered.length&&delivered.length>0?{status:percentile(age,.95)<=2*period&&percentile(freshnessUpperBound,.95)!==null&&percentile(freshnessUpperBound,.95)<=2*period&&unknownClock===0?'candidate-met':'candidate-failed',candidateP95LimitMs:2*period,uncertaintyPolicy:'p95(abs drift + max(frame total uncertainty, frame Worker mapping + selected audio renderer mapping)); selected audio mapping components are required for formal evidence, no repeated Worker term',earlyFrameConstraint:'absolute signed drift p95 must also stay within two periods; negative age never makes an early frame pass',model:'latest calibrated real audio.currentTime sample at or before completion, extrapolated by rate within exact timelineResetId'}:{status:'unmeasured',reason:'Missing calibrated audio master comparison on some frames'},
      resourceObservationBrackets:streamSnapshots.map(record=>record.processObservationBracket??{sampling:'legacy approximate /proc inventory after snapshot; exact bracket unavailable'}),
      resourceTrends:[...resourcePids.values()].map(points=>({pid:points.pid,startTicks:points.startTicks,role:points.role,fd:regression(points.fd),rssBytes:regression(points.rss),gpuMemoryBytes:regression(points.gpu)})),
      gates:gate,throughputPassed:Object.values(gate).every(Boolean)};
  });
  const actionReports=actions.map(action=>({...action,elapsedMs:Number(ns(action.endMonotonicNs)-ns(action.startMonotonicNs))/1e6}));
  const interactions=records.filter(record=>record.type==='interaction').map(action=>{
    const start=ns(action.uiInvocationMonotonicNs);
    const matching=[...frames.values()].filter(frame=>frame.drawn===true&&ns(frame.completedMonotonicNs)>=start&&
      STREAM_KEYS.every(key=>action[key]===undefined||frame[key]===action[key])&&
      (action.sourceRevision===undefined||frame.sourceRevision===action.sourceRevision)&&
      (action.timelineResetId===undefined||frame.timelineResetId===action.timelineResetId)&&
      (action.minimumControlSeq===undefined||frame.controlSeq>=action.minimumControlSeq)).sort((a,b)=>ns(a.completedMonotonicNs)<ns(b.completedMonotonicNs)?-1:1)[0];
    const applied=matching?.controlApplied;
    const validApplied=applied&&matching&&applied.streamId===matching.streamId&&applied.inputGeneration===matching.inputGeneration&&applied.timelineResetId===matching.timelineResetId&&applied.controlSeq<=matching.controlSeq&&(action.minimumControlSeq===undefined||applied.controlSeq>=action.minimumControlSeq)&&applied.mainObservedMonotonicNs!==undefined;
    const active=action.kind==='latest-input'?matching?.attachedMonotonicNs:validApplied?applied.mainObservedMonotonicNs:undefined;
    const activeValid=active!==undefined&&ns(active)>=start&&matching&&ns(active)<=ns(matching.completedMonotonicNs);
    return {...action,firstMatchingCompletedLease:matching?leaseKey(matching):null,
      uiInvocationToMatchingCompletionMs:matching?Number(ns(matching.completedMonotonicNs)-start)/1e6:null,
      uiInvocationToMatchingCompletionUpperBoundMs:matching&&Number.isFinite(action.uiInvocationClockUncertaintyMs)?Number(ns(matching.completedMonotonicNs)-start)/1e6+action.uiInvocationClockUncertaintyMs:null,
      uiInvocationClockUncertaintyMs:action.uiInvocationClockUncertaintyMs??null,
      activationOrigin:action.kind==='latest-input'?'exact attached hello-ready event for the new source/input generation':'exact main observation of matching Worker control-applied event',
      activeToMatchingCompletionMs:activeValid?Number(ns(matching.completedMonotonicNs)-ns(active))/1e6:null,
      activeMonotonicNs:activeValid?active:null,
      mainReceiveToWorkerAppliedObservedMs:validApplied&&applied.mainReceivedMonotonicNs!==undefined?Number(ns(applied.mainObservedMonotonicNs)-ns(applied.mainReceivedMonotonicNs))/1e6:null,
      mainReceiveToWorkerAppliedMs:'unmeasured pure Worker apply instant; separately report main receive to observed Worker applied ack'};
  });
  const interactionGroups=Object.fromEntries([...new Set(interactions.map(action=>action.kind))].map(kind=>{
    const group=interactions.filter(action=>action.kind===kind),values=group.map(action=>action.uiInvocationToMatchingCompletionMs).filter(Number.isFinite),upper=group.map(action=>action.uiInvocationToMatchingCompletionUpperBoundMs).filter(Number.isFinite);
    return [kind,{...distribution(values),withClockUncertaintyUpperBoundMs:distribution(upper),missing:group.length-values.length,tailSampleCaveat:group.length<100?'fewer than 100 observations; p99 is the nearest-rank observed tail, not a population-tail estimate':null,origin:'CDP invocation bracket mapped to Electron main monotonic; include reported uncertainty',candidateMet:values.length===group.length&&upper.length===group.length&&percentile(upper,.95)<=150&&percentile(upper,.99)<=250}];
  }));
  const activeInteractionGroups=Object.fromEntries([...new Set(interactions.map(action=>action.kind))].map(kind=>{
    const group=interactions.filter(action=>action.kind===kind),values=group.map(action=>action.activeToMatchingCompletionMs).filter(Number.isFinite);
    return [kind,{...distribution(values),missing:group.length-values.length,origin:kind==='latest-input'?'new input native hello-ready attachment event → first matching completed frame':'main-observed matching Worker control-applied ack → first matching completed frame',candidateMet:values.length===group.length&&percentile(values,.95)<=150&&percentile(values,.99)<=250}];
  }));
  const lifecycle={attachDetach:{required:20,observed:actionReports.filter(action=>action.kind==='attach-detach'&&action.newGenerationVerified===true&&action.oldWorkerExited===true).length},
    publicResizeSeek:{required:10,observed:actionReports.filter(action=>action.kind==='public-resize-seek'&&action.exactGenerationCompleted===true).length},
    uiResolutionRecreate:{observed:actionReports.filter(action=>action.kind==='ui-resolution-recreate').length}};
  const requirements={
    formalRunClass:manifest.runClass==='formal-p3',runFinished:typeof manifest.finishedAt==='string'&&manifest.runError===undefined,
    ownedProcessDrain:manifest.connectionOwnership==='owned-isolated-session'&&manifest.shutdown?.launcherExitCode===0&&
      manifest.shutdown?.error===undefined&&manifest.shutdown?.remaining?.length===0,
    matchingDeployment:manifest.deployment?.verified===true,visibleRenderer:manifest.platformEvidence?.renderer?.visibility==='visible',
    runtimeWayland:manifest.platformEvidence?.waylandConfirmed===true,
    ordinarySchedulerBaseline:manifest.schedulerBaseline?.explicitOrdinaryScheduling===true&&manifest.schedulerBaseline?.initialOwnedProcesses?.length>0&&manifest.schedulerBaseline.initialOwnedProcesses.every(proc=>!proc.exited&&proc.scheduler?.policy===0&&(proc.role!=='worker'||proc.scheduler?.nice===0)),
    phaseMatrix:['720p24','1440p30','1440p30-lifetime'].every(name=>phaseReports.some(phase=>phase.name===name&&phase.provider===(manifest.provider==='synthetic'?'synthetic-gpu':manifest.provider)&&phase.durationSeconds>=(name.endsWith('lifetime')?600:180))),
    throughput:phaseReports.length===3&&phaseReports.every(phase=>phase.throughputPassed),
    freshness:phaseReports.length===3&&phaseReports.every(phase=>phase.freshness.status==='candidate-met'),
    interactions:['pause','seek','latest-input'].every(kind=>activeInteractionGroups[kind]?.count>=20&&activeInteractionGroups[kind]?.candidateMet===true),
    lifecycle:lifecycle.attachDetach.observed>=20&&lifecycle.publicResizeSeek.observed>=10,
    completeEventHistory:gaps.length===0};
  return {schemaVersion:1,sourceCommit:manifest.sourceCommit??null,generatedAt:new Date().toISOString(),runClass:manifest.runClass??'unspecified',
    identity:'stream/host/producer/input/consumer/timeline/pool/slot/slotEpoch/frameSeq',
    counting:'unique canvas-drawn identity intersected with actual native allReferencesReleased; measured at completion timestamp',
    phaseReports,eventGaps:[...new Set(gaps)],actions:actionReports,interactions,interactionGroups,activeInteractionGroups,requiredLifecycle:lifecycle,measuredRequirements:requirements,
    conclusion:manifest.runClass!=='formal-p3'?(manifest.runClass??'incomplete-or-failed'):Object.values(requirements).every(Boolean)?'measured-candidate-gates-met; separate correctness/trace/resource interpretation remains required':'incomplete-or-failed',
    p3Acceptance:'not established by timed runner alone; requires separate correctness/trace and lifetime lease/FD/cache review',
    limitations:['No screen scan/displayed FPS measurement','No Chromium internal copy count inferred','RSS/GPU-memory slopes need platform-cache interpretation, not an invented fixed MB threshold','Synthetic and runtime providers must be measured and reported separately']};
}

const SCHEDULER_POLICY_NAMES={0:'SCHED_OTHER',1:'SCHED_FIFO',2:'SCHED_RR',3:'SCHED_BATCH',5:'SCHED_IDLE',6:'SCHED_DEADLINE'};
export function parseSchedulerStat(identity) {
  const fields=identity.slice(identity.lastIndexOf(')')+2).split(' ');
  const policy=Number(fields[38]);
  return {policy,policyName:SCHEDULER_POLICY_NAMES[policy]??'unknown',priority:Number(fields[15]),nice:Number(fields[16]),rtPriority:Number(fields[37]),cpu:Number(fields[36]),source:'/proc/pid/stat fields 18/19/39/40/41'};
}
async function schedulerKernelState() {
  try{const enabled=(await readFile('/proc/sys/kernel/sched_schedstats','utf8')).trim()==='1';return {schedSchedstatsEnabled:enabled,waitTimeInterpretation:enabled?'schedstat runqueue-wait counter is enabled':'disabled: zero schedstat wait does not prove no runqueue wait'};}catch{return {schedSchedstatsEnabled:null,waitTimeInterpretation:'kernel schedstats state unavailable'};}
}
async function readProc(pid,role) {
  try{
    const directory=`/proc/${pid}`;
    const [identity,status,fds]=await Promise.all([readFile(`${directory}/stat`,'utf8'),readFile(`${directory}/status`,'utf8'),readdir(`${directory}/fd`)]);
    const fields=identity.slice(identity.lastIndexOf(')')+2).split(' '), startTicks=fields[19];
    const scheduler=parseSchedulerStat(identity);
    try{const values=(await readFile(`${directory}/schedstat`,'utf8')).trim().split(/\s+/);scheduler.schedstat={cpuRuntimeNs:values[0],runqueueWaitNs:values[1],timeslices:values[2],waitValidity:'interpret with manifest kernel schedstats flag; zero when disabled is unmeasured wait'};}catch{}
    const clients=new Map();
    for(const fd of fds){
      let info;try{info=await readFile(`${directory}/fdinfo/${fd}`,'utf8');}catch{continue;}
      const client=info.match(/^drm-client-id:\s*(\d+)$/m)?.[1];
      if(!client||clients.has(client))continue;
      const bytes=[...info.matchAll(/^drm-memory-(?:vram|gtt|system):\s*(\d+)\s*(KiB|kB|B)$/mg)].reduce((sum,match)=>sum+Number(match[1])*(match[2]==='B'?1:1024),0);
      clients.set(client,bytes);
    }
    return {pid,role,startTicks,scheduler,rssBytes:Number(status.match(/^VmRSS:\s*(\d+) kB$/m)?.[1]??0)*1024,fdCount:fds.length,
      gpuMemoryBytes:clients.size?[...clients.values()].reduce((sum,value)=>sum+value,0):null,gpuMemorySource:clients.size?'per-process DRM fdinfo, deduplicated client-id':'unavailable'};
  }catch{return {pid,role,exited:true};}
}
async function concurrentAppEvidence(ownMainPid) {
  const result=[];
  for(const pidText of await readdir('/proc')){
    if(!/^\d+$/.test(pidText))continue;
    const pid=Number(pidText);if(pid===ownMainPid)continue;
    try{
      const args=(await readFile(`/proc/${pid}/cmdline`,'utf8')).split('\0').filter(Boolean);
      if(!args[0]?.startsWith(join(ROOT,'desktop/node_modules/electron/dist/electron'))||!args.join(' ').includes(join(ROOT,'desktop/main.cjs')))continue;
      result.push({...await readProc(pid,'concurrent-App-Electron-main'),observedOnly:true,cmdlineClassification:'same App desktop main; arguments omitted to preserve private handles'});
    }catch{}
  }
  return {observation:'Other App instances are preserved; no pause/close/kill is performed for timing',observedAt:new Date().toISOString(),instances:result};
}
async function processSnapshot(snapshot) {
  const pids=new Map();
  if(snapshot.mainPid)pids.set(snapshot.mainPid,'electron-main');
  if(snapshot.workerPid)pids.set(snapshot.workerPid,'worker');
  for(const proc of snapshot.processes??[])pids.set(proc.pid,proc.type);
  return Promise.all([...pids].map(([pid,role])=>readProc(pid,role)));
}
async function seekTimeline(page,xOffset) {
  const point=await page.eval(`(()=>{const rect=document.querySelector('canvas[aria-label="时间轴"]').getBoundingClientRect();return {x:rect.x+Math.min(rect.width-2,${xOffset}),y:rect.y+8};})()`);
  await page.send('Input.dispatchMouseEvent',{type:'mousePressed',x:point.x,y:point.y,button:'left',clickCount:1});
  await page.send('Input.dispatchMouseEvent',{type:'mouseReleased',x:point.x,y:point.y,button:'left',clickCount:1});
}
export async function makeFixtures(output,lineCount=360) {
  const seconds=1800,rate=8000,bytes=seconds*rate*2,header=Buffer.alloc(44),audio=join(output,'silence-1800s.wav');
  header.write('RIFF');header.writeUInt32LE(36+bytes,4);header.write('WAVEfmt ',8);header.writeUInt32LE(16,16);header.writeUInt16LE(1,20);header.writeUInt16LE(1,22);header.writeUInt32LE(rate,24);header.writeUInt32LE(rate*2,28);header.writeUInt16LE(2,32);header.writeUInt16LE(16,34);header.write('data',36);header.writeUInt32LE(bytes,40);
  await writeFile(audio,Buffer.concat([header,Buffer.alloc(bytes)]));
  const lyrics=join(output,'production-lyrics.lrc');
  await writeFile(lyrics,Array.from({length:lineCount},(_,index)=>`[${String(Math.floor(index*5/60)).padStart(2,'0')}:${String(index*5%60).padStart(2,'0')}.00]持续 GPU 预览 · 第 ${index+1} 行 · unique frame consumption`).join('\n'));
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
  try{for(const name of await readdir(join(ROOT,'frontend/dist/assets')))if(/\.(js|css)$/.test(name))candidates.push(join(ROOT,'frontend/dist/assets',name));}catch{}
  const files=[];
  for(const path of candidates){try{const bytes=await readFile(path);files.push({path:path.replace(ROOT+'/',''),size:bytes.length,sha256:createHash('sha256').update(bytes).digest('hex')});}catch{files.push({path:path.replace(ROOT+'/',''),missing:true});}}
  let verified=false,verificationError,nativeManifest;
  try{nativeManifest=await verifyDesktopDeployment(ROOT,current.dist);verified=true;}catch(error){verificationError=error.message;}
  return {available:true,verified,verificationError,sourceBoundary:{managedAndNative:'matched published dist; verifyDesktopDeployment checks payload inventory',desktopJs:'main/preload are App disk source loaded at process startup; hashes recorded separately from managed dist',frontend:'built index plus JS/CSS assets served by App launcher; source files do not substitute for these bundles'},dist:current.dist.replace(ROOT+'/',''),files,nativeManifest};
}
async function actualWaylandEvidence(page,cdp,snapshot) {
  let systemInfo=null,commandLine=null;
  let browserCdp;
  try{const version=await fetch(`http://127.0.0.1:${snapshot.cdpPort}/json/version`).then(response=>response.json());browserCdp=await Cdp.connect(version.webSocketDebuggerUrl);systemInfo=await browserCdp.send('SystemInfo.getInfo');}catch{}finally{browserCdp?.ws.close();}
  try{commandLine=await readFile(`/proc/${snapshot.mainPid}/cmdline`,'utf8').then(value=>value.split('\0').filter(Boolean));}catch{}
  const details=await page.eval(`({visibility:document.visibilityState,userAgent:navigator.userAgent,platform:navigator.platform})`);
  const backend=systemInfo?.gpu?.auxAttributes?.displayType??systemInfo?.gpu?.auxAttributes?.glImplementationParts??null;
  const ownedProcesses=[];
  for(const proc of [{pid:snapshot.rendererPid,type:'renderer'},...(snapshot.processes??[]).filter(proc=>proc.type==='GPU')]){
    if(!proc.pid)continue;
    try{const args=(await readFile(`/proc/${proc.pid}/cmdline`,'utf8')).split('\0');const ozone=ozoneArguments(args.join('\0'));ownedProcesses.push({pid:proc.pid,type:proc.type,ozonePlatformArguments:ozone,source:'owned /proc cmdline argv or Chromium process-title token; unrelated arguments omitted'});}catch{}
  }
  const actualWaylandProcesses=ownedProcesses.length>=2&&ownedProcesses.every(proc=>proc.ozonePlatformArguments.includes('--ozone-platform=wayland'));
  return {renderer:details,gpuAuxAttributes:systemInfo?.gpu?.auxAttributes??null,electronOzoneArguments:commandLine?ozoneArguments(commandLine.join('\0')):null,ownedProcesses,waylandRuntimeProof:actualWaylandProcesses?'owned Electron GPU and renderer process both run --ozone-platform=wayland':null,
    actualOzoneBackend:backend??'unmeasured; environment variables alone are not proof',
    environmentObservation:{XDG_SESSION_TYPE:process.env.XDG_SESSION_TYPE??null,WAYLAND_DISPLAY:process.env.WAYLAND_DISPLAY??null},
    waylandConfirmed:actualWaylandProcesses||(typeof backend==='string'&&/wayland/i.test(backend))};
}
async function selectGpu(page) {
  // 顶栏状态胶囊的渲染设备菜单；GPU 不可用时那一项禁用，ui.click 会一直等到超时
  await ui.click(page,'button','（渲染设备）',{partial:true,scope:'.app-bar'});
  await ui.click(page,'menuitem','GPU',{scope:'.render-device-menu',what:'Available GPU render backend',timeout:120_000});
  await page.waitFor(`!document.querySelector('.render-device-menu')`,'Render device menu closed');
}
const QUALITY_LABEL={'720p24':'720p · 24 fps','1440p30':'1440p · 30 fps'};
/** 成片预览标题栏的“GPU 预览规格”下拉框；name 是 720p24 或 1440p30。 */
async function setQuality(page,name) {
  await ui.choose(page,'GPU 预览规格',QUALITY_LABEL[name]);
}
/** 工程设置里经「选择图片或视频…」上传背景图片（只读取该文件），等缩略图出现后关闭面板；在计时与 SDK2 内容之前。 */
async function setBackgroundImage(page,path) {
  const bytes=await readFile(path);
  await ui.click(page,'button','工程设置',{scope:'.app-bar'});
  await page.waitFor(`!!document.querySelector('.project-settings:not([data-exiting])')`,'Project settings open');
  await ui.setFile(page,'选择图片或视频…',path);
  await page.waitFor(`!!document.querySelector('img.background-thumb')`,'Background image thumbnail',60_000);
  await ui.click(page,'button','关闭工程设置');
  await page.waitFor(`!document.querySelector('.project-settings')`,'Project settings closed');
  return {kind:'image',path,sizeBytes:bytes.length,sha256:createHash('sha256').update(bytes).digest('hex'),setBefore:'SDK2 content and all timed phases'};
}
export async function createProject(page,fixture) {
  await shell.newProject(page,{audio:fixture.audio,lyrics:fixture.lyrics,name:'P3 GPU production benchmark'});
  await shell.goto(page,'orchestration');
  await selectGpu(page);
  await ui.wait(page,'select','GPU 预览规格',{what:'Production GPU preview',timeout:180_000});
  await page.waitFor(`!!document.querySelector('[aria-label="播放"]:not([disabled])')`,'Real audio playback ready',180_000);
}
export async function connectPage(port) {
  const deadline=Date.now()+90_000;
  while(Date.now()<deadline){
    try{
      const targets=await fetch(`http://127.0.0.1:${port}/json/list`).then(response=>response.json());
      const target=targets.find(value=>value.type==='page'&&/^http:\/\/(127\.0\.0\.1|localhost):/.test(value.url));
      if(target){const cdp=await Cdp.connect(target.webSocketDebuggerUrl);
        cdp.ws.addEventListener('close',()=>{for(const pending of cdp.pending.values())pending.reject(new Error('Owned Electron CDP connection closed during acceptance'));cdp.pending.clear();});
        const page=new Page(cdp,undefined);await page.send('Runtime.enable');await page.send('Page.enable');return {cdp,page};}
    }catch{}
    await pause(100);
  }
  throw new Error('Loopback Electron CDP page did not become ready');
}
const PHASES={'720p24':{name:'720p24',width:1280,height:720,targetFps:24},'1440p30':{name:'1440p30',width:2560,height:1440,targetFps:30}};
/** 计时计划：正式运行固定两档 180 s；debug／finite 可用 --phases 选子集、--measure-seconds 改每档计时（5..600 s）。 */
export function timedPlan(values) {
  const short=!!(values.debug||values.finite);
  invariant(short||(values.phases===undefined&&values['measure-seconds']===undefined),'--phases/--measure-seconds only apply to --debug/--finite runs');
  const names=values.phases===undefined?Object.keys(PHASES):values.phases.split(',');
  invariant(names.length>0&&names.every(name=>Object.hasOwn(PHASES,name))&&new Set(names).size===names.length,'--phases must be a comma list of 720p24,1440p30');
  const measureSeconds=values['measure-seconds']===undefined?(short?5:180):Number(values['measure-seconds']);
  invariant(Number.isSafeInteger(measureSeconds)&&measureSeconds>=5&&measureSeconds<=600,'--measure-seconds must be 5..600');
  return {warmupSeconds:short?2:10,measureSeconds,stabilitySeconds:short?5:600,phases:names.map(name=>PHASES[name])};
}
/** 分辨率重建第 index 次要切到的档位：从计时阶段最后一档出发交替，第一次必须真的换档。 */
export function resolutionCycleQuality(lastPhase,index){
  invariant(Object.hasOwn(PHASES,lastPhase),'Unknown phase');
  return (index%2===0)===(lastPhase==='1440p30')?'720p24':'1440p30';
}
async function run(values) {
  invariant(!values['correctness-snapshot']||values.debug||values.finite,'Correctness screenshot must stay outside formal timed runs');
  const v2Style=values['v2-style']?v2StyleName(values['v2-style']):null;
  const v2Names=values['v2-samples']?v2SampleNames(values['v2-samples']):(v2Style?[]:null);
  const v2Lines=v2Names?Number(values['v2-lines']??'72'):null;
  invariant(!v2Names||(Number.isSafeInteger(v2Lines)&&v2Lines>=2&&v2Lines<=360),'--v2-lines must be 2..360');
  invariant(!v2Names||((values.debug||values.finite)&&!values['reuse-project']&&values.provider==='runtime'),'--v2-samples needs --debug/--finite, a new project and the runtime provider');
  const v2Params=v2ParamOverrides(values['v2-params']);
  invariant(!values['v2-params']||values['v2-samples'],'--v2-params needs --v2-samples');
  const backgroundImage=values['background-image']?resolve(values['background-image']):null;
  invariant(!backgroundImage||(/\.png$/i.test(backgroundImage)&&!values['reuse-project']&&(values.debug||values.finite)),'--background-image needs a .png, a new project and --debug/--finite');
  const sessionName=values.connect?null:(values['session-name']??`p3-${new Date().toISOString().replaceAll(/[^0-9TZ]/g,'').toLowerCase()}`);
  if(sessionName!==null)invariant(/^[a-z0-9][a-z0-9-]{0,63}$/.test(sessionName),'Invalid isolated desktop session name');
  const output=resolve(values.output??join(ROOT,'.local/gpu-preview-p3',new Date().toISOString().replaceAll(':','-')));
  invariant(output.startsWith(join(ROOT,'.local')+'/'),'P3 output must stay in App .local');
  await mkdir(output,{recursive:true,mode:0o700});
  const manifest={schemaVersion:1,startedAt:new Date().toISOString(),platform:platform(),arch:arch(),kernel:release(),provider:values.provider,sessionName,connectionOwnership:values.connect?'existing-explicit-CDP-session':'owned-isolated-session',
    runClass:values.debug?'short-debug-not-performance-acceptance':values.finite?'finite-correctness-not-performance-acceptance':'formal-p3',
    ...timedPlan(values),readbacks:values['correctness-snapshot']?'one validation-only screenshot before warmup; no screenshots/pixel extraction in timed phases':'none; no screenshots/pixel extraction in timed runner',
    thresholds:{overallRatio:.99,sliding5sRatio:.98,intervalP95Periods:2,intervalP99Periods:3,maxUnexplainedStallMs:250,interactionP95Ms:150,interactionP99Ms:250},
    lifecycle:{attachDetach:20,publicResizeSeek:10,uiResolutionRecreate:10},command:process.argv};
  manifest.schedulerBaseline={kernel:await schedulerKernelState(),runner:await readProc(process.pid,'p3-runner'),runnerParent:await readProc(process.ppid,'runner-parent'),explicitOrdinaryScheduling:values['ordinary-scheduling']===true,requiredFormalPolicy:'Explicit acceptance-only wrapper for newly spawned App/host: actual Electron main/renderer/GPU/utility and Worker SCHED_OTHER (0), Worker nice=0; Chromium may adjust its own nice. Runner/launcher actual scheduler is recorded continuously but excluded from pipeline gate; their external policy changes are an observation of the probe environment'};
  manifest.sourceCommit=await commandOutput('git',['rev-parse','HEAD']);
  manifest.dirtyPaths=(await commandOutput('git',['status','--short'])).split('\n').filter(Boolean);
  manifest.deployment=await deploymentEvidence();
  manifest.sourceSnapshotCurrentOnDisk={};
  for(const file of ['scripts/gpu-preview-p3.mjs','scripts/app.mjs','desktop/gpu-preview/ordinary-exec.cjs','desktop/main.cjs','desktop/gpu-preview/preload.cjs','frontend/src/editor/preview/PreviewPanel.tsx','frontend/src/editor/preview/gpu-preview-controller.ts']){
    manifest.sourceSnapshotCurrentOnDisk[file]=createHash('sha256').update(await readFile(join(ROOT,file))).digest('hex');
  }
  manifest.sourceSnapshotBoundary='Hashes identify current disk source, deployment hashes identify managed/native payload; a connected Electron may have loaded an earlier disk source version before connection';
  await writeFile(join(output,'manifest.json'),JSON.stringify(manifest,null,2)+'\n');
  if(values['dry-run']){console.log(JSON.stringify({output,plan:manifest}));return;}
  invariant(manifest.deployment.verified&&manifest.deployment.files.every(file=>!file.missing),'Matched desktop deployment is missing/stale: build:desktop before a real run');
  const records=[],record=async value=>{records.push(value);await appendFile(join(output,'events.jsonl'),JSON.stringify(value)+'\n');};
  let child,cdp,page;
  const ownedProcesses=new Map();
  const rememberOwned=proc=>{if(!values.connect&&!proc.exited&&typeof proc.startTicks==='string')ownedProcesses.set(`${proc.pid}:${proc.startTicks}`,{pid:proc.pid,startTicks:proc.startTicks,role:proc.role});};
  const observeOwnedTree=async(pid,seen=new Set())=>{
    if(seen.has(pid))return;seen.add(pid);
    const proc=await readProc(pid,pid===child?.pid?'owned-app-launcher':'owned-app-descendant');
    if(proc.exited)return;rememberOwned(proc);
    for(const task of await readdir(`/proc/${pid}/task`).catch(()=>[])){
      const children=await readFile(`/proc/${pid}/task/${task}/children`,'utf8').catch(()=>'');
      for(const text of children.trim().split(/\s+/).filter(Boolean))await observeOwnedTree(Number(text),seen);
    }
  };
  const seenSamples=new Map();
  const observedSamples=new Map();
  let sampleRevision=0,lastWorkerPublicRead=0,workerPublicState,workerPublicReadMonotonicNs;
  const sample=async()=>{
    const captureNodeStart=process.hrtime.bigint();
    const snapshot=await page.eval(`window.makoDesktopPreview.readPreviewState(${sampleRevision}).then(snapshot=>{const canvas=document.getElementById('mako-gpu-preview'),rect=canvas?.getBoundingClientRect();return {...snapshot,rendererVisibility:document.visibilityState,displayGeometry:{viewport:{width:innerWidth,height:innerHeight,devicePixelRatio},canvas:canvas?{backingWidth:canvas.width,backingHeight:canvas.height,cssWidth:rect.width,cssHeight:rect.height,cssX:rect.x,cssY:rect.y}:null}};})`);
    const captureNodeEnd=process.hrtime.bigint();
    ns(snapshot.nowMonotonicNs);
    const mainOffset=ns(snapshot.nowMonotonicNs)-(captureNodeStart+captureNodeEnd)/2n;
    const observationClockUncertaintyNs=(captureNodeEnd-captureNodeStart)/2n;
    invariant(!snapshot.sampleCursorExpired,'Native completion telemetry cursor expired; evidence is incomplete');
    sampleRevision=snapshot.sampleRevision;
    // Persist each complete event once, plus actual late drawn updates; the bridge
    // returns a bounded history so repeated samples must never multiply counts.
    if(Date.now()-lastWorkerPublicRead>=1000){
      lastWorkerPublicRead=Date.now();
      try{const started=process.hrtime.bigint()+mainOffset;workerPublicState=await page.eval(`window.makoGpuPreviewAcceptance?.read()`);workerPublicReadMonotonicNs={start:String(started),end:String(process.hrtime.bigint()+mainOffset),mappingUncertaintyNs:String(observationClockUncertaintyNs)};}catch{}
    }
    const changed=[];
    for(const frame of snapshot.samples){const key=leaseKey(frame),serialized=JSON.stringify(frame);observedSamples.set(key,frame);if(seenSamples.get(key)!==serialized){seenSamples.set(key,serialized);changed.push(frame);}}
    const procStart=process.hrtime.bigint()+mainOffset,processes=await processSnapshot(snapshot);
    for(const proc of processes)rememberOwned(proc);
    processes.push(await readProc(process.pid,'p3-runner'));
    if(child?.pid)processes.push(await readProc(child.pid,'owned-app-launcher'));
    const procEnd=process.hrtime.bigint()+mainOffset;
    await record({type:'snapshot',snapshot:{...snapshot,...(workerPublicState?{workerPublicState,workerPublicReadMonotonicNs}:{}),samples:changed},processes,processObservationBracket:{startMonotonicNs:String(procStart),endMonotonicNs:String(procEnd),mappingUncertaintyNs:String(observationClockUncertaintyNs),sampling:'approximate asynchronous /proc inventory over this bracket; not atomic with main snapshot'}});
    return {...snapshot,latestSample:currentCompletedFrames(snapshot,observedSamples.values()).at(-1)};
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
      ...Object.fromEntries(STREAM_KEYS.map(key=>[key,after.activeStream?.[key]])),timelineResetId:after.mediaClock.timelineResetId,
      minimumControlSeq:minControl,controlObservationMonotonicNs:after.nowMonotonicNs});
    const expected=after.activeStream,expectedReset=after.mediaClock.timelineResetId;
    const matching=()=>currentCompletedFrames({activeStream:expected},observedSamples.values(),String(invocation))
      .some(frame=>frame.timelineResetId===expectedReset&&frame.controlSeq>=minControl);
    while(!matching()&&Date.now()<deadline){await pause(20);after=await sample();}
    invariant(matching(),'Interaction did not yield a matching drawn/native-completed frame');
    return after;
  };
  // SDK2 重内容下每次换源/重建都要在新 Worker 里完整重开 Runtime 输入，等待上限放宽到 120 秒。
  const streamDeadlineMs=v2Names?120_000:30_000;
  const waitCurrentFrames=async({since='0',count=3,previous,expectedSize,predicate=()=>true}={})=>{
    const deadline=Date.now()+streamDeadlineMs;
    while(Date.now()<deadline){
      const state=await sample(),active=state.activeStream;
      const current=await page.eval('window.makoGpuPreviewAcceptance?.current()??null');
      const exact=active&&current&&['streamId','producerEpoch','inputGeneration','poolGeneration'].every(key=>active[key]===current[key])&&
        active.size?.width===current.size?.width&&active.size?.height===current.size?.height;
      // 渲染端已收到当前流的 failed 事件时立即带失败码与诊断摘要结束，避免空等到期限、也避免进程随后 fail-stop 后丢掉原因。
      const failed=await page.eval(`(window.__makoP3Events??[]).map(value=>value.event).filter(event=>event?.type==='failed').at(-1)??null`);
      if(failed&&(!current||failed.streamId===undefined||failed.streamId===current.streamId))
        throw new Error(`GPU stream failed: ${JSON.stringify(failed)}`);
      const completed=currentCompletedFrames(state,observedSamples.values(),since);
      if(exact&&Number.isSafeInteger(state.workerPid)&&state.workerPid>0&&(!previous||replacedStream(previous,active))&&
        (!expectedSize||active.size.width===expectedSize.width&&active.size.height===expectedSize.height)&&
        completed.length>=count&&predicate({...state,latestSample:completed.at(-1)}))return {...state,latestSample:completed.at(-1),verifiedCompletedFrames:completed.length};
      await pause(100);
    }
    throw new Error('Current public/native stream did not yield enough exact-generation drawn/native-completed frames');
  };
  const waitOwnedExit=async(original)=>{
    const deadline=Date.now()+30_000;
    let current;
    do{current=await readProc(original.pid,'retired-worker');if(current.exited||current.startTicks!==original.startTicks)return current;await pause(100);}while(Date.now()<deadline);
    throw new Error('Retired exact Worker PID/startTicks did not exit');
  };
  try{
    if(!values.connect){
      child=spawn(process.execPath,[join(ROOT,'scripts/app.mjs'),'desktop','--port',values.port,'--cdp-port',values['cdp-port'],'--session-name',sessionName,...(values['ordinary-scheduling']?['--ordinary-scheduling']:[])],
        {cwd:ROOT,env:{...process.env,...(values.provider==='synthetic'?{MAKO_BACKEND_GPU_PREVIEW_BENCHMARK:'1'}:{})},stdio:['ignore','ignore','inherit']});
      child.once('error',()=>{});
      manifest.schedulerBaseline.launcher=await readProc(child.pid,'owned-app-launcher');
    }
    ({cdp,page}=await connectPage(Number(values['cdp-port'])));
    await page.waitFor(`typeof window.makoDesktopPreview?.readPreviewState==='function'`,'Desktop production bridge');
    const initial=await sample();
    if(child?.pid)await observeOwnedTree(child.pid);
    manifest.platformEvidence=await actualWaylandEvidence(page,cdp,{...initial,cdpPort:values['cdp-port']});
    manifest.schedulerBaseline.initialOwnedProcesses=await processSnapshot(initial);
    manifest.concurrentAppObservation=await concurrentAppEvidence(initial.mainPid);
    if(!values['reuse-project'])manifest.fixture=await makeFixtures(output,v2Lines??360).then(async fixture=>{await createProject(page,fixture);return fixture;});
    if(!values['reuse-project'])manifest.background=backgroundImage?await setBackgroundImage(page,backgroundImage):{kind:'solid'};
    if(v2Names){
      // SDK2 内容在计时前经界面建模板并应用到全部行；之后回到编排页重新等 GPU 预览就绪。
      manifest.v2Content=await applyV2Template(page,output,v2Names,v2Lines,v2Params,v2Style);
      const appliedAt=Date.now();
      await shell.goto(page,'orchestration');
      await ui.wait(page,'select','GPU 预览规格',{what:'Production GPU preview',timeout:180_000});
      await page.waitFor(`!!document.querySelector('[aria-label="播放"]:not([disabled])')`,'Real audio playback ready',180_000);
      // 应用后到 GPU 流首个精确代次完成帧的墙钟时间，含换源、回编排页重建流与 Runtime 输入重开。
      await waitCurrentFrames({count:1});
      manifest.v2Content.streamReadyAfterApplyMs=Date.now()-appliedAt;
    }
    if(values['reuse-project']){invariant(await page.eval(shell.projectOpen),'--reuse-project requires an already-open project in the live App');await shell.goto(page,'orchestration');await selectGpu(page);}
    await writeFile(join(output,'manifest.json'),JSON.stringify(manifest,null,2)+'\n');
    await page.eval(`(()=>{window.__makoP3Events=[];window.makoDesktopPreview.onPreviewState(event=>{window.__makoP3Events.push({event,rendererPerformanceMs:performance.now()});if(window.__makoP3Events.length>4096)window.__makoP3Events.shift();});})()`);
    await ui.click(page,'button','播放',{scope:'.transport'});
    await page.waitFor(`!!document.querySelector('[aria-label="暂停"]')`,'Audio master playing');
    for(const phase of manifest.phases){
      await setQuality(page,phase.name);
      const before=await sample();await waitCurrentFrames({since:before.nowMonotonicNs,expectedSize:phase,predicate:state=>state.mediaClock?.targetFps===phase.targetFps});
      if(values['correctness-snapshot']&&phase.name==='720p24'){
        const image=await page.screenshot();
        await writeFile(join(output,'correctness-before-timing.png'),image);
        await record({type:'correctness-observation',readback:'validation-only CDP screenshot before warmup/timed phase; excluded from zero-readback performance evidence',snapshot:await sample(),path:'correctness-before-timing.png'});
      }
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
      const before=await sample();await waitCurrentFrames({since:before.nowMonotonicNs,count:2});
    }
    invariant(await page.eval(`typeof window.makoGpuPreviewAcceptance?.setBackground==='function'`),'Real project edit benchmark hook unavailable');
    for(let index=0;index<(values.debug?1:20);index++){
      await sample();
      const bracket=await mainClockBracket(),invocation=process.hrtime.bigint()+bracket.offset;
      const old=await page.eval('window.makoGpuPreviewAcceptance.current()');
      const edited=await page.eval(`window.makoGpuPreviewAcceptance.setBackground(${index%2?24:48},16,32,255)`);
      const deadline=Date.now()+streamDeadlineMs;let current;
      do{current=await page.eval('window.makoGpuPreviewAcceptance.current()');if(current?.source?.sourceRevision===edited.sourceRevision&&current.inputGeneration!==old.inputGeneration)break;await sample();await pause(20);}while(Date.now()<deadline);
      invariant(current?.streamId===old.streamId&&current.source.sourceRevision===edited.sourceRevision&&current.inputGeneration!==old.inputGeneration,'Real project input did not replace exact public generation');
      const after=await waitCurrentFrames({since:String(invocation),count:1,predicate:state=>state.activeStream.streamId===current.streamId&&state.activeStream.inputGeneration===current.inputGeneration});
      await record({type:'interaction',kind:'latest-input',uiInvocationMonotonicNs:String(invocation),uiInvocationClockUncertaintyMs:bracket.uncertaintyMs,
        ...Object.fromEntries(STREAM_KEYS.map(key=>[key,after.activeStream[key]])),expectedSourceRevision:edited.sourceRevision,previousInputGeneration:old.inputGeneration,controllerResponseTimings:await page.eval('window.makoGpuPreviewAcceptance.timings?.()??[]')});
    }
    // A resolution selector replacement is reported separately from public resize.
    for(let index=0;index<(values.debug?1:10);index++){
      const before=await waitCurrentFrames({count:1}),quality=resolutionCycleQuality(manifest.phases.at(-1).name,index);
      const expectedSize=quality==='720p24'?{width:1280,height:720}:{width:2560,height:1440};
      const oldWorker=await readProc(before.workerPid,'retired-worker');
      invariant(!oldWorker.exited&&typeof oldWorker.startTicks==='string','Resolution cycle requires a live exact previous Worker identity');
      await setQuality(page,quality);
      const after=await waitCurrentFrames({since:before.nowMonotonicNs,previous:before.activeStream,expectedSize});
      const previousWorkerAfter=await waitOwnedExit(oldWorker);
      await record({type:'action',kind:'ui-resolution-recreate',index,startMonotonicNs:before.nowMonotonicNs,endMonotonicNs:after.nowMonotonicNs,
        previousStreamId:before.activeStream.streamId,nextStreamId:after.activeStream.streamId,previousInputGeneration:before.activeStream.inputGeneration,inputGeneration:after.activeStream.inputGeneration,
        previousWorkerPid:before.workerPid,previousWorkerStartTicks:oldWorker.startTicks,nextWorkerPid:after.workerPid,exactGenerationCompleted:true,oldWorkerExited:true,previousWorkerAfter});
    }
    for(let index=0;index<(values.debug?1:20);index++){
      const before=await waitCurrentFrames({count:1}),oldIdentity=before.activeStream;
      const oldWorker=await readProc(before.workerPid,'retired-worker');
      invariant(!oldWorker.exited&&typeof oldWorker.startTicks==='string','Attach cycle requires a live exact previous Worker identity');
      await shell.goto(page,'library');
      await page.waitFor(`!document.querySelector('#mako-gpu-preview')`,'Preview detached by actual page navigation');
      const detachDeadline=Date.now()+30_000;
      let detached;
      do{detached=await sample();if(detached.activeStream===null)break;await pause(100);}while(Date.now()<detachDeadline);
      invariant(detached.activeStream===null,'Page navigation did not detach the native active stream');
      const previousWorkerAfter=await waitOwnedExit(oldWorker);
      await shell.goto(page,'orchestration');
      const after=await waitCurrentFrames({since:before.nowMonotonicNs,previous:oldIdentity}),newIdentity=after.activeStream;
      invariant(replacedStream(oldIdentity,newIdentity),'Attach cycle replayed the same claimed stream generation');
      await record({type:'action',kind:'attach-detach',index,startMonotonicNs:before.nowMonotonicNs,endMonotonicNs:after.nowMonotonicNs,
        previousStreamId:oldIdentity.streamId,nextStreamId:newIdentity.streamId,previousInputGeneration:oldIdentity.inputGeneration,inputGeneration:newIdentity.inputGeneration,
        previousWorkerPid:before.workerPid,previousWorkerStartTicks:oldWorker.startTicks,nextWorkerPid:after.workerPid,
        newGenerationVerified:true,oldWorkerExited:true,previousWorkerAfter,verifiedCompletedFrames:after.verifiedCompletedFrames});
    }
    // This hook must route through the existing public client and controller CAS;
    // it is deliberately never emulated by replaying a claimed old generation.
    invariant(await page.eval(`typeof window.makoGpuPreviewAcceptance?.resize==='function'`),'Public resize benchmark hook is unavailable; UI recreation does not substitute for protocol resize');
    for(let index=0;index<(values.debug?1:10);index++){
      const before=await waitCurrentFrames({count:1});
      const old=await page.eval('window.makoGpuPreviewAcceptance.current()');
      const targetWidth=old.size.width===1280?2560:1280,targetHeight=targetWidth===1280?720:1440;
      const resized=await page.eval(`window.makoGpuPreviewAcceptance.resize(${targetWidth},${targetHeight})`);
      invariant(old.streamId===resized.streamId&&old.inputGeneration!==resized.inputGeneration,'Resize did not preserve stream and replace input generation');
      await seekTimeline(page,20+index*7);
      const after=await waitCurrentFrames({since:before.nowMonotonicNs,previous:before.activeStream,expectedSize:{width:targetWidth,height:targetHeight},
        predicate:state=>state.latestSample?.streamId===resized.streamId&&state.latestSample?.inputGeneration===resized.inputGeneration&&state.latestSample?.timelineResetId!==before.mediaClock?.timelineResetId});
      await record({type:'action',kind:'public-resize-seek',exactGenerationCompleted:true,index,startMonotonicNs:before.nowMonotonicNs,endMonotonicNs:after.nowMonotonicNs,
        streamId:resized.streamId,previousInputGeneration:old.inputGeneration,inputGeneration:resized.inputGeneration,previousPoolGeneration:old.poolGeneration,poolGeneration:resized.poolGeneration});
    }
    await setQuality(page,'1440p30');
    await page.waitFor(`!!window.makoGpuPreviewAcceptance?.current()`,'Current public stream before stability');
    const stabilityPublic=await page.eval('window.makoGpuPreviewAcceptance.current()');
    if(stabilityPublic.size.width!==2560||stabilityPublic.size.height!==1440)await page.eval(`window.makoGpuPreviewAcceptance.resize(2560,1440)`);
    await waitCurrentFrames({since:(await sample()).nowMonotonicNs,expectedSize:{width:2560,height:1440},predicate:state=>state.latestSample?.provider===(values.provider==='synthetic'?'synthetic-gpu':'runtime')&&state.mediaClock?.targetFps===30});
    const stableWarmup=Date.now()+manifest.warmupSeconds*1000;while(Date.now()<stableWarmup){await sample();await pause(1000);}
    const start=await sample(),until=Date.now()+manifest.stabilitySeconds*1000;
    while(Date.now()<until){await pause(Math.min(1000,until-Date.now()));await sample();}
    const end=await sample();await record({type:'phase',name:'1440p30-lifetime',width:2560,height:1440,targetFps:30,provider:values.provider==='synthetic'?'synthetic-gpu':'runtime',
      streamId:start.activeStream?.streamId,inputGeneration:start.activeStream?.inputGeneration,startMonotonicNs:start.nowMonotonicNs,endMonotonicNs:end.nowMonotonicNs});
    await page.eval(`document.querySelector('[aria-label="暂停"]')?.click()`);
    // Flush delayed drawn reports; original completion times are unchanged.
    for(let index=0;index<3;index++){await pause(1000);await sample();}
    await record({type:'renderer-observations',events:await page.eval('window.__makoP3Events')});
  }catch(error){
    manifest.runError=error.message;
    // 失败时留下渲染端收到的 failed 事件（含 Worker 失败码与诊断摘要），页面已断开时跳过。
    try{manifest.runFailureEvents=await Promise.race([page.eval(`(window.__makoP3Events??[]).filter(value=>value.event?.type==='failed').slice(-8)`),pause(5000).then(()=>'timeout')]);}catch{}
    throw error;
  }finally{
    manifest.finishedAt=new Date().toISOString();
    await writeFile(join(output,'manifest.json'),JSON.stringify(manifest,null,2)+'\n');
    let report;
    try{
      report=analyze(records,manifest);
      await writeFile(join(output,'report.json'),JSON.stringify(report,null,2)+'\n');
    }finally{
      cdp?.ws.close();
      const shutdown={ownership:values.connect?'existing-explicit-CDP-session':'owned-isolated-session',signalSent:false,observedIdentities:[],remaining:[]};
      try{
        if(child){
          if(child.exitCode===null&&child.signalCode===null){
            await observeOwnedTree(child.pid);
            const original=manifest.schedulerBaseline.launcher,current=await readProc(child.pid,'owned-app-launcher');
            invariant(original&&!current.exited&&current.startTicks===original.startTicks,'Owned App launcher process identity changed before shutdown');
            await new Promise((done,reject)=>{
              const timer=setTimeout(()=>reject(new Error('Owned App launcher did not drain within 45 seconds')),45_000);
              child.once('exit',()=>{clearTimeout(timer);done();});
              shutdown.signalSent=child.kill('SIGINT');
            });
          }
          shutdown.launcherExitCode=child.exitCode;shutdown.launcherSignal=child.signalCode;
          invariant(child.exitCode===0,'Owned App launcher did not exit normally with code 0');
          const deadline=Date.now()+10_000;
          do{
            shutdown.remaining=[];
            for(const original of ownedProcesses.values()){
              const current=await readProc(original.pid,original.role);
              if(!current.exited&&current.startTicks===original.startTicks)shutdown.remaining.push(original);
            }
            if(shutdown.remaining.length===0)break;
            await pause(100);
          }while(Date.now()<deadline);
          invariant(shutdown.remaining.length===0,'Observed owned App process identities survived launcher shutdown');
        }
      }catch(error){shutdown.error=error.message;throw error;}finally{
        shutdown.observedIdentities=[...ownedProcesses.values()];
        shutdown.boundary='Only the spawned launcher with matching PID/startTicks is signaled; all observed descendants and pipeline identities are checked after exit. Connected sessions receive no signal. Unsampled short-lived descendants are not proven by process sampling.';
        await writeFile(join(output,'shutdown.json'),JSON.stringify(shutdown,null,2)+'\n');
        manifest.shutdown=shutdown;
        await writeFile(join(output,'manifest.json'),JSON.stringify(manifest,null,2)+'\n');
        if(report){report=analyze(records,manifest);await writeFile(join(output,'report.json'),JSON.stringify(report,null,2)+'\n');}
      }
    }
    console.log(JSON.stringify({output,conclusion:report.conclusion}));
  }
}
async function main() {
  const {values}=parseArgs({options:{'cdp-port':{type:'string',default:'9223'},port:{type:'string',default:'5174'},output:{type:'string'},provider:{type:'string',default:'runtime'},
    connect:{type:'boolean'},'ordinary-scheduling':{type:'boolean'},'session-name':{type:'string'},'reuse-project':{type:'boolean'},'dry-run':{type:'boolean'},debug:{type:'boolean'},finite:{type:'boolean'},'correctness-snapshot':{type:'boolean'},analyze:{type:'string'},'v2-samples':{type:'string'},'v2-lines':{type:'string'},'v2-params':{type:'string'},'v2-style':{type:'string'},'background-image':{type:'string'},phases:{type:'string'},'measure-seconds':{type:'string'}}});
  invariant(['runtime','synthetic'].includes(values.provider),'provider must be runtime or synthetic');
  if(values.analyze){const directory=resolve(values.analyze);const records=(await readFile(join(directory,'events.jsonl'),'utf8')).trim().split('\n').filter(Boolean).map(line=>JSON.parse(line));
    const manifest=JSON.parse(await readFile(join(directory,'manifest.json'),'utf8'));const report=analyze(records,manifest);
    const analyzerSha256=createHash('sha256').update(await readFile(fileURLToPath(import.meta.url))).digest('hex');
    report.analysisSource={sourceCommit:await commandOutput('git',['rev-parse','HEAD']),analyzerSha256,boundary:'Offline reanalysis; original manifest, events and report.json are preserved'};
    const output=join(directory,`report-analysis-${analyzerSha256.slice(0,12)}.json`);await writeFile(output,JSON.stringify(report,null,2)+'\n');console.log(JSON.stringify({output,report}));return;}
  for(const option of ['cdp-port','port'])invariant(/^\d+$/.test(values[option])&&Number(values[option])>=1024&&Number(values[option])<=65535,`Invalid ${option}`);
  if(!values.debug&&!values.finite&&!values.connect)values['ordinary-scheduling']=true;
  invariant(!values.connect||!values['ordinary-scheduling'],'--ordinary-scheduling requires a newly owned isolated session');
  timedPlan(values);
  await run(values);
}
if(process.argv[1]&&import.meta.url===pathToFileURL(resolve(process.argv[1])).href)main().catch(error=>{console.error(error.message);process.exitCode=1;});
