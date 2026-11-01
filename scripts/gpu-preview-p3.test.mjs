import test from 'node:test';
import assert from 'node:assert/strict';
import { analyze, leaseKey, percentile, slidingWindows, ozoneArguments } from './gpu-preview-p3.mjs';

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
  const report=state=>analyze([snapshot(2n*N,[frame(1,N)],1,{workerState:state}),phase]).phaseReports[0];
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
