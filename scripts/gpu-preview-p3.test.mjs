import test from 'node:test';
import assert from 'node:assert/strict';
import { analyze, leaseKey, percentile, slidingWindows, ozoneArguments, parseSchedulerStat, auditPipelineScheduler, currentCompletedFrames, timedPlan, resolutionCycleQuality } from './gpu-preview-p3.mjs';
import { materialSlotPlan } from './gpu-preview-v2-content.mjs';

const N=1_000_000_000n;
const frame=(seq,time,extra={})=>({streamId:'stream',hostInstanceId:'host',producerEpoch:'1',inputGeneration:'1',consumerEpoch:'1',timelineEpoch:'1',poolGeneration:'1',slotId:seq%3,slotEpoch:String(seq),frameSeq:seq,
  completedMonotonicNs:String(time),drawn:true,width:1280,height:720,provider:'runtime',controlSeq:1,timelineResetId:'initial',timeUs:Number(time)/1000,
  counters:{cpuPixelReadbacks:0,gpuBlits:seq},clockUncertaintyMs:.2,totalClockUncertaintyMs:.3,renderMs:2,brokerToCompletionMs:1,readyToCompletionMs:3,...extra});
const snapshot=(time,samples,ack,extra={})=>({type:'snapshot',snapshot:{nowMonotonicNs:String(time),samples,acknowledgedUnique:ack,mainPid:1,outstanding:0,peakOutstanding:3,executingControls:0,pendingControls:1,...extra}});
const phase={type:'phase',name:'720p24',startMonotonicNs:'0',endMonotonicNs:String(10n*N),width:1280,height:720,targetFps:24,provider:'runtime',streamId:'stream',inputGeneration:'1'};

test('full lease identity includes generations and avoids string delimiter collision',()=>{
  assert.notEqual(leaseKey(frame(1,0n)),leaseKey(frame(1,0n,{inputGeneration:'2'})));
  assert.notEqual(leaseKey(frame(1,0n,{streamId:'a/b',hostInstanceId:'c'})),leaseKey(frame(1,0n,{streamId:'a',hostInstanceId:'b/c'})));
  assert.throws(()=>leaseKey({frameSeq:1}),/Incomplete/);
});
test('nearest rank percentile retains small-sample worst tails',()=>{
  assert.equal(percentile([1,2,100],.95),100);assert.equal(percentile([],.99),null);
});
test('sliding five seconds checks all event transitions, including noninteger gaps',()=>{
  const times=Array.from({length:240},(_,i)=>BigInt(Math.round((i+.5)*1e9/24)));
  const normal=slidingWindows(times,0n,10n*N);
  assert.equal(normal.minimumUniqueFrames,120);
  const removed=times.filter(time=>time<3_250_000_000n||time>3_550_000_000n);
  const failing=slidingWindows(removed,0n,10n*N);
  assert.ok(failing.minimumUniqueFrames<118);
  assert.ok(failing.checkedEventBoundaries>10);
});
test('late drawn update counts one native completion, and never ACK-only FPS',()=>{
  const first=frame(1,N,{drawn:false}),second=frame(2,2n*N);
  const report=analyze([snapshot(3n*N,[first,second],2),snapshot(4n*N,[{...first,drawn:true}],2),phase]);
  assert.equal(report.phaseReports[0].uniqueDrawnAndNativeCompleted,2);
  assert.equal(report.phaseReports[0].uniqueNativeCompletions,2);
  assert.deepEqual(report.eventGaps,[]);
  const ackOnly=analyze([snapshot(3n*N,[first],1),phase]);
  assert.equal(ackOnly.phaseReports[0].uniqueDrawnAndNativeCompleted,0);
  assert.equal(ackOnly.phaseReports[0].undrawnNativeCompletions,1);
});
test('ring overflow and changed completion timestamps invalidate evidence',()=>{
  const report=analyze([snapshot(N,[frame(1,N)],1),snapshot(3n*N,[frame(3,3n*N)],3),phase]);
  assert.equal(report.eventGaps.length,1);assert.equal(report.conclusion,'incomplete-or-failed');
  assert.throws(()=>analyze([snapshot(N,[frame(1,N)],1),snapshot(3n*N,[frame(1,2n*N)],1),phase]),/timestamp changed/);
});
test('phase filters stream generation, and wrong size/provider/readback remains a failure',()=>{
  const report=analyze([snapshot(3n*N,[frame(1,N,{inputGeneration:'2'}),frame(2,2n*N,{width:2560,provider:'synthetic-gpu',counters:{cpuPixelReadbacks:1}})],2),phase]);
  const result=report.phaseReports[0];
  assert.equal(result.uniqueNativeCompletions,1);
  assert.equal(result.gates.exactSize,false);assert.equal(result.gates.exactProvider,false);assert.equal(result.gates.productionFinalReadbacksZero,false);
});
test('calibrated audio master signed drift uses same timeline and actual completion time',()=>{
  const report=analyze([snapshot(2n*N,[frame(1,2n*N,{timeUs:1_990_000})],1,{mediaClock:{mode:'playing',rate:1,timelineResetId:'initial',mediaTimeUs:1_000_000,mainSampleMonotonicNs:String(N),uncertaintyNs:'300000'}}),phase]);
  assert.equal(report.phaseReports[0].signedDriftMs.p95,-10);
  assert.equal(report.phaseReports[0].ageMs.p95,10);
});
test('interaction requires exact stream/reset/min control and reports UI origin separately',()=>{
  const records=[snapshot(2n*N,[frame(1,1_010_000_000n,{controlSeq:1}),frame(2,1_100_000_000n,{controlSeq:2})],2),
    {type:'interaction',kind:'pause',uiInvocationMonotonicNs:String(N),streamId:'stream',timelineResetId:'initial',minimumControlSeq:2,uiInvocationClockUncertaintyMs:1},phase];
  const result=analyze(records);
  assert.equal(result.interactions[0].uiInvocationToMatchingCompletionMs,100);
  assert.equal(result.interactionGroups.pause.candidateMet,true);
  assert.match(result.interactions[0].mainReceiveToWorkerAppliedMs,/unmeasured/);
});

test('offline analysis preserves debug/finite classification and rejects short formal phases',()=>{
  const records=[snapshot(2n*N,[frame(1,N)],1),phase];
  assert.equal(analyze(records,{runClass:'short-debug-not-performance-acceptance'}).conclusion,'short-debug-not-performance-acceptance');
  assert.equal(analyze(records,{runClass:'finite-correctness-not-performance-acceptance'}).conclusion,'finite-correctness-not-performance-acceptance');
  const formal=analyze(records,{runClass:'formal-p3',provider:'runtime',finishedAt:'2026-11-01T00:00:00Z'});
  assert.equal(formal.measuredRequirements.phaseMatrix,false);
  assert.equal(formal.measuredRequirements.interactions,false);
  assert.equal(formal.conclusion,'incomplete-or-failed');
});
test('latest-input interaction excludes completed frames from the old exact generation',()=>{
  const report=analyze([snapshot(2n*N,[frame(1,1_010_000_000n),frame(2,1_120_000_000n,{inputGeneration:'2'})],2),
    {type:'interaction',kind:'latest-input',uiInvocationMonotonicNs:String(N),streamId:'stream',inputGeneration:'2'},phase]);
  assert.equal(report.interactions[0].uiInvocationToMatchingCompletionMs,120);
  assert.equal(report.interactionGroups['latest-input'].count,1);
  assert.match(report.interactionGroups['latest-input'].tailSampleCaveat,/fewer than 100/);
});
test('audio master from a different timeline cannot establish freshness',()=>{
  const report=analyze([snapshot(2n*N,[frame(1,2n*N)],1,{mediaClock:{mode:'playing',rate:1,timelineResetId:'other',mediaTimeUs:1_000_000,mainSampleMonotonicNs:String(N)}}),phase]);
  assert.equal(report.phaseReports[0].freshness.status,'unmeasured');
});
test('lifecycle counts require verified replacement and retired Worker drain',()=>{
  const report=analyze([{type:'action',kind:'attach-detach',startMonotonicNs:'0',endMonotonicNs:'1'},
    {type:'action',kind:'attach-detach',startMonotonicNs:'0',endMonotonicNs:'1',newGenerationVerified:true,oldWorkerExited:true},
    {type:'action',kind:'public-resize-seek',startMonotonicNs:'0',endMonotonicNs:'1'}]);
  assert.equal(report.requiredLifecycle.attachDetach.observed,1);
  assert.equal(report.requiredLifecycle.publicResizeSeek.observed,0);
});
test('an early frame cannot satisfy freshness solely through negative age',()=>{
  const report=analyze([snapshot(2n*N,[frame(1,2n*N,{timeUs:2_100_000})],1,{mediaClock:{mode:'playing',rate:1,timelineResetId:'initial',mediaTimeUs:1_000_000,mainSampleMonotonicNs:String(N)}}),phase]);
  const result=report.phaseReports[0];
  assert.equal(result.ageMs.p95,-100);
  assert.equal(result.absoluteDriftMs.p95,100);
  assert.equal(result.freshness.status,'candidate-failed');
});
test('freshness and interaction gates include their measured clock uncertainty',()=>{
  const report=analyze([snapshot(2n*N,[frame(1,2n*N,{timeUs:1_920_000,totalClockUncertaintyMs:5})],1,{mediaClock:{mode:'playing',rate:1,timelineResetId:'initial',mediaTimeUs:1_000_000,mainSampleMonotonicNs:String(N),uncertaintyNs:'5000000'}}),
    {type:'interaction',kind:'pause',uiInvocationMonotonicNs:'1851000000',streamId:'stream',minimumControlSeq:1,uiInvocationClockUncertaintyMs:2},phase]);
  assert.equal(report.phaseReports[0].absoluteDriftMs.p95,80);
  assert.equal(report.phaseReports[0].absoluteDriftUpperBoundMs.p95,85);
  assert.equal(report.phaseReports[0].freshness.status,'candidate-failed');
  assert.equal(report.interactionGroups.pause.p95,149);
  assert.equal(report.interactionGroups.pause.withClockUncertaintyUpperBoundMs.p95,151);
  assert.equal(report.interactionGroups.pause.candidateMet,false);
});
test('Worker pool/render/control gates require real independent telemetry and actual retained bytes',()=>{
  const good={executingRender:1,pendingControls:1,pendingRenderTargets:1,outstanding:2,slotCount:3,actualAllocationBytes:12_000_000,retainedAllocationBytes:12_000_000,peaks:{executingRender:1,pendingControls:1,pendingRenderTargets:1,outstanding:3}};
  const report=state=>analyze([2n,5n,8n,10n].map((t,index)=>snapshot(t*N,index===0?[frame(1,N)]:[],1,{workerState:state?{...state,streamId:'stream',inputGeneration:'1',mainObservedMonotonicNs:String((t===10n?10n:t-1n)*N)}:undefined})).concat(phase)).phaseReports[0];
  assert.equal(report(undefined).gates.workerPoolAndRenderControlBudgets,false);
  assert.equal(report(good).gates.workerPoolAndRenderControlBudgets,true);
  assert.equal(report({...good,retainedAllocationBytes:65*1024**2}).gates.workerPoolAndRenderControlBudgets,false);
  assert.equal(report({...good,peaks:{...good.peaks,executingRender:2}}).gates.workerPoolAndRenderControlBudgets,false);
});
test('selected audio mapping uncertainty remains in freshness despite smaller completion mapping',()=>{
  const report=analyze([snapshot(2n*N,[frame(1,2n*N,{timeUs:1_925_000,clockUncertaintyMs:1,totalClockUncertaintyMs:2})],1,{mediaClock:{mode:'playing',rate:1,timelineResetId:'initial',mediaTimeUs:1_000_000,mainSampleMonotonicNs:String(N),rendererUncertaintyNs:'10000000',workerUncertaintyNs:'1000000',uncertaintyNs:'11000000'}}),phase]);
  const result=report.phaseReports[0];
  assert.equal(result.absoluteDriftMs.p95,75);
  assert.equal(result.absoluteDriftUpperBoundMs.p95,86);
  assert.equal(result.freshness.status,'candidate-failed');
  assert.equal(result.gates.selectedAudioClockComponentsRecorded,true);
});
test('Ozone runtime evidence extracts normal argv and Chromium process titles only',()=>{
  assert.deepEqual(ozoneArguments('/app/electron\0--type=renderer\0--ozone-platform=wayland\0--field-trial=secret'),['--ozone-platform=wayland']);
  assert.deepEqual(ozoneArguments('/app/electron --type=gpu-process --ozone-platform=wayland --field-trial=secret\0'),['--ozone-platform=wayland']);
  assert.deepEqual(ozoneArguments('/app/electron\0--ozone-platform-hint=wayland\0'),['--ozone-platform-hint=wayland']);
  assert.deepEqual(ozoneArguments('/app/electron --unrelated=--ozone-platform=waylandish'),[]);
});
test('latest-input activation is an exact new-generation attachment event while UI cold start stays visible',()=>{
  const sample=frame(1,2n*N,{inputGeneration:'2',attachedMonotonicNs:'1990000000'});
  const report=analyze([snapshot(3n*N,[sample],1),{type:'interaction',kind:'latest-input',uiInvocationMonotonicNs:String(N),streamId:'stream',inputGeneration:'2',uiInvocationClockUncertaintyMs:1}]);
  assert.equal(report.interactions[0].uiInvocationToMatchingCompletionMs,1000);
  assert.equal(report.interactions[0].activeToMatchingCompletionMs,10);
  assert.equal(report.interactionGroups['latest-input'].candidateMet,false);
  assert.equal(report.activeInteractionGroups['latest-input'].candidateMet,true);
  assert.equal(analyze([snapshot(3n*N,[{...sample,attachedMonotonicNs:'999000000'}],1),{type:'interaction',kind:'latest-input',uiInvocationMonotonicNs:String(N),streamId:'stream',inputGeneration:'2'}]).interactions[0].activeToMatchingCompletionMs,null);
});
test('stale cached native Worker state cannot satisfy budget evidence across a phase',()=>{
  const state={streamId:'stream',inputGeneration:'1',mainObservedMonotonicNs:String(N),executingRender:1,pendingControls:0,pendingRenderTargets:0,outstanding:1,slotCount:3,actualAllocationBytes:12_000_000,retainedAllocationBytes:12_000_000,peaks:{executingRender:1,pendingControls:1,pendingRenderTargets:1,outstanding:3}};
  const records=[snapshot(2n*N,[frame(1,N)],1,{workerState:state}),snapshot(10n*N,[],1,{workerState:state}),phase];
  const report=analyze(records).phaseReports[0];
  assert.equal(report.gates.workerPoolAndRenderControlBudgets,false);
  assert.equal(report.workerFlowControl.freshness.uniqueUpdates,1);
  assert.equal(report.workerFlowControl.freshness.maxAgeMs,9000);
  assert.equal(report.workerFlowControl.freshness.covered,false);
});
test('frame-ready to completion reports Worker mapping uncertainty separately from UI latency',()=>{
  const report=analyze([snapshot(2n*N,[frame(1,N,{readyToCompletionMs:3,clockUncertaintyMs:2,totalClockUncertaintyMs:20})],1),phase]);
  assert.equal(report.phaseReports[0].readyToCompletionMs.p95,3);
  assert.equal(report.phaseReports[0].readyToCompletionUpperBoundMs.p95,5);
  assert.match(report.phaseReports[0].readyToCompletionBoundary,/actual native allReferencesReleased/);
});
test('scheduler proc stat parsing uses kernel field offsets even with spaces in comm',()=>{
  const fields=Array.from({length:45},()=> '0');fields[0]='S';fields[15]='20';fields[16]='0';fields[36]='7';fields[37]='0';fields[38]='5';
  const parsed=parseSchedulerStat('123 (Electron Renderer) '+fields.join(' '));
  assert.deepEqual({policy:parsed.policy,name:parsed.policyName,priority:parsed.priority,nice:parsed.nice,cpu:parsed.cpu},{policy:5,name:'SCHED_IDLE',priority:20,nice:0,cpu:7});
});


test('scheduler gate excludes observed tool Node priority but rejects actual pipeline IDLE and Worker nice drift',()=>{
  const pipeline=[{pid:1,startTicks:'10',role:'Browser',scheduler:{policy:0,nice:-8}},{pid:2,startTicks:'20',role:'GPU',scheduler:{policy:0,nice:-8}},{pid:3,startTicks:'30',role:'Tab',scheduler:{policy:0,nice:0}},{pid:4,startTicks:'40',role:'worker',scheduler:{policy:0,nice:0}}];
  const tools=[{role:'p3-runner',scheduler:{policy:5,nice:16}},{role:'owned-app-launcher',scheduler:{policy:5,nice:16}}];
  const gate=processes=>analyze([{...snapshot(2n*N,[frame(1,N)],1,{rendererPid:3,workerPid:4,device:{chromiumGpuPid:2}}),processes},phase],{schedulerBaseline:{explicitOrdinaryScheduling:true,initialOwnedProcesses:pipeline}});
  const observed=gate([...pipeline,...tools]);
  assert.equal(observed.phaseReports[0].gates.schedulerOtherObserved,true);
  assert.equal(observed.measuredRequirements.ordinarySchedulerBaseline,true);
  assert.equal(gate([...pipeline,{role:'Tab',scheduler:{policy:5,nice:0}},...tools]).phaseReports[0].gates.schedulerOtherObserved,false);
  assert.equal(gate(pipeline.map(proc=>proc.role==='worker'?{...proc,scheduler:{policy:0,nice:16}}:proc)).phaseReports[0].gates.schedulerOtherObserved,false);
  assert.equal(gate(tools).phaseReports[0].gates.schedulerOtherObserved,false);
});


test('every sampled declared pipeline identity is required even after an earlier valid observation',()=>{
  const record=()=>({snapshot:{mainPid:1,rendererPid:3,workerPid:4,device:{chromiumGpuPid:2}},processes:['Browser','GPU','Tab','worker'].map((role,index)=>({role,pid:index+1,startTicks:String(index+10),scheduler:{policy:0,nice:0}}))});
  assert.equal(auditPipelineScheduler([record(),record()]).passed,true);
  for(const role of ['Browser','GPU','Tab','worker']){
    const missing=record();missing.processes=missing.processes.filter(proc=>proc.role!==role);
    assert.equal(auditPipelineScheduler([record(),missing]).passed,false,`missing ${role}`);
    const exited=record();exited.processes.find(proc=>proc.role===role).exited=true;
    assert.equal(auditPipelineScheduler([record(),exited]).passed,false,`exited ${role}`);
    const changed=record();changed.processes.find(proc=>proc.role===role).startTicks='999';
    assert.equal(auditPipelineScheduler([record(),changed]).passed,false,`reused ${role} PID`);
    const unknown=record();delete unknown.processes.find(proc=>proc.role===role).startTicks;
    assert.equal(auditPipelineScheduler([record(),unknown]).passed,false,`unknown ${role} identity`);
  }
  const wrong=record();wrong.snapshot.rendererPid=99;assert.equal(auditPipelineScheduler([record(),wrong]).passed,false);
  const undeclared=record();delete undeclared.snapshot.device;assert.equal(auditPipelineScheduler([record(),undeclared]).passed,false);
  const duplicate=record();duplicate.processes.push({...duplicate.processes[0]});assert.equal(auditPipelineScheduler([duplicate]).passed,false);
  assert.equal(auditPipelineScheduler([]).passed,false);
});


test('current completions reject detached late frames and every stale stream identity',()=>{
  const good=frame(1,N),activeStream={...good};
  assert.deepEqual(currentCompletedFrames({activeStream:null},[good]),[]);
  assert.deepEqual(currentCompletedFrames({activeStream},[good]),[good]);
  assert.deepEqual(currentCompletedFrames({activeStream},[{...good,drawn:false}]),[]);
  for(const key of ['streamId','hostInstanceId','producerEpoch','inputGeneration','consumerEpoch','poolGeneration']){
    assert.deepEqual(currentCompletedFrames({activeStream},[{...good,[key]:'stale'}]),[],key);
    const missing={...activeStream};delete missing[key];
    assert.deepEqual(currentCompletedFrames({activeStream:missing},[good]),[],`missing active ${key}`);
  }
});
test('current completion cutoff uses native completion time and returns chronological frames',()=>{
  const early=frame(1,N),at=frame(2,2n*N),late=frame(3,3n*N);
  assert.deepEqual(currentCompletedFrames({activeStream:{...early}},[late,early,at],String(2n*N)),[at,late]);
  assert.deepEqual(currentCompletedFrames({activeStream:{...early}},[early],String(N+1n)),[]);
});
test('owned process drain requires successful identity cleanup evidence',()=>{
  const base={runClass:'formal-p3',connectionOwnership:'owned-isolated-session'};
  const measured=manifest=>analyze([],manifest).measuredRequirements.ownedProcessDrain;
  assert.equal(measured(base),false);
  assert.equal(measured({...base,shutdown:{launcherExitCode:0,remaining:[]}}),true);
  assert.equal(measured({...base,shutdown:{launcherExitCode:1,remaining:[]}}),false);
  assert.equal(measured({...base,shutdown:{launcherExitCode:0,remaining:[{pid:1,startTicks:'10'}]}}),false);
  assert.equal(measured({...base,shutdown:{launcherExitCode:0,remaining:[],error:'drain failed'}}),false);
  assert.equal(measured({...base,connectionOwnership:'existing-explicit-CDP-session',shutdown:{launcherExitCode:0,remaining:[]}}),false);
});
test('interaction matching respects all recorded active stream identities',()=>{
  for(const key of ['hostInstanceId','producerEpoch','consumerEpoch','poolGeneration']){
    const action={type:'interaction',kind:'latest-input',uiInvocationMonotonicNs:String(N),streamId:'stream',[key]:'2'};
    const report=analyze([snapshot(2n*N,[frame(1,1_010_000_000n),frame(2,1_120_000_000n,{[key]:'2'})],2),action]);
    assert.equal(report.interactions[0].uiInvocationToMatchingCompletionMs,120,key);
  }
});
test('timed plan keeps the formal matrix fixed and lets short runs pick phases and timing',()=>{
  const formal=timedPlan({});
  assert.deepEqual([formal.phases.map(phase=>phase.name),formal.measureSeconds,formal.stabilitySeconds],[['720p24','1440p30'],180,600]);
  assert.throws(()=>timedPlan({phases:'720p24'}),/only apply/);
  assert.throws(()=>timedPlan({'measure-seconds':'60'}),/only apply/);
  const g1=timedPlan({finite:true,phases:'720p24','measure-seconds':'60'});
  assert.deepEqual([g1.phases,g1.measureSeconds,g1.warmupSeconds],[[{name:'720p24',width:1280,height:720,targetFps:24}],60,2]);
  assert.equal(timedPlan({debug:true}).measureSeconds,5);
  for(const phases of ['','480p24','720p24,720p24'])assert.throws(()=>timedPlan({debug:true,phases}),/--phases/);
  for(const seconds of ['4','601','60.5','x'])assert.throws(()=>timedPlan({debug:true,'measure-seconds':seconds}),/--measure-seconds/);
});
test('material slots use the first and last material export across packages like the HTTP probe',()=>{
  const pkg=(packageId,ids)=>({packageId,packageName:packageId.toUpperCase(),materials:ids.map(id=>({id,name:`${id}-name`}))});
  assert.deepEqual(materialSlotPlan([pkg('fx',[])]),[]);
  const one=materialSlotPlan([pkg('ink',['custom-ink'])]);
  assert.deepEqual(one.map(item=>[item.slot,item.label,item.packageId,item.id]),[['unsungFill','未唱填充','ink','custom-ink'],['sungFill','已唱填充','ink','custom-ink']]);
  const many=materialSlotPlan([pkg('glass',['glass-media','glass-backdrop']),pkg('fx',[]),pkg('ink',['custom-ink'])]);
  assert.deepEqual(many.map(item=>`${item.packageName}/${item.name}`),['GLASS/glass-media-name','INK/custom-ink-name']);
});
test('resolution recreate always switches away from the last timed phase first',()=>{
  assert.deepEqual([0,1,2].map(index=>resolutionCycleQuality('1440p30',index)),['720p24','1440p30','720p24']);
  assert.deepEqual([0,1,2].map(index=>resolutionCycleQuality('720p24',index)),['1440p30','720p24','1440p30']);
});
