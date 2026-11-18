'use strict';
const {test}=require('node:test');const assert=require('node:assert/strict');
const fixture=require('../../backend/tests/MakoKaraGenerator.Backend.Worker.Tests/Fixtures/GpuStreamWorkerWire.json');
const {validateWorkerMessage,workerFailureSummary}=require('./wire.cjs');
const stream={streamId:'stream',producerEpoch:'1',expectedInputGeneration:'1',poolGeneration:'2',helloNs:'1000000',size:{width:256,height:128}};
const config={hostInstanceId:'host',consumerId:'fixture'};
test('production main accepts the same serialized DMA-BUF and SHM fixtures as the real Worker',()=>{
  for(const message of Object.values(fixture))assert.doesNotThrow(()=>validateWorkerMessage(message,stream,config));
});
test('old generation, wrong consumer, unnegotiated size, invalid transport/format/timings are rejected',()=>{
  for(const [name,patch] of [
    ['frame',{inputGeneration:'2'}],
    ['frame',{timings:{renderDurationUs:-1,portCaptureDurationUs:0}}],
    ['shmFrame',{timings:{renderDurationUs:10,portCaptureDurationUs:-5}}],
    ['state',{poolGeneration:'3'}],
    ['controlApplied',{consumerEpoch:'2'}],
    ['helloAccepted',{consumerId:'other'}],
    ['helloAccepted',{width:1280}],
    ['shmHelloAccepted',{transport:'unknown'}],
    ['shmHelloAccepted',{pixelFormat:'rgb565'}],
    ['shmHelloAccepted',{strideBytes:1023}],
    ['shmHelloAccepted',{shmName:''}],
    ['stopped',{hostInstanceId:'old'}],
  ])assert.throws(()=>validateWorkerMessage({...fixture[name],...patch},stream,config));
});
test('hard Worker failure keeps code, root type, site and engine text but drops malformed fields',()=>{
  const failed={...fixture.failed,code:'ImageSurfaceBudgetExceeded',failureType:'ImageSurfaceBudgetException',failureSite:'ImageSurfaceBudgetSession.<Reserve>g__Check|5_0',failureDetail:'材质捕获：会话字节 当前 1 + 申请 2 / 3。',drained:false};
  assert.deepEqual(workerFailureSummary(failed),{code:'ImageSurfaceBudgetExceeded',type:'ImageSurfaceBudgetException',site:'ImageSurfaceBudgetSession.<Reserve>g__Check|5_0',detail:'材质捕获：会话字节 当前 1 + 申请 2 / 3。',drained:false});
  assert.deepEqual(workerFailureSummary({code:'bad code',failureType:'x'.repeat(129),failureSite:42,failureDetail:'line\nbreak',drained:true}),{code:undefined,type:undefined,site:undefined,detail:undefined,drained:true});
});
