'use strict';
const {test}=require('node:test');const assert=require('node:assert/strict');
const fixture=require('../../backend/tests/MakoKaraGenerator.Backend.Worker.Tests/Fixtures/GpuStreamWorkerWire.json');
const {validateWorkerMessage}=require('./wire.cjs');
const stream={streamId:'stream',producerEpoch:'1',expectedInputGeneration:'1',poolGeneration:'2',helloNs:'1000000',size:{width:256,height:128}};
const config={hostInstanceId:'host',consumerId:'fixture'};
test('production main accepts the same serialized fixture as the real Worker',()=>{
  for(const message of Object.values(fixture))assert.doesNotThrow(()=>validateWorkerMessage(message,stream,config));
});
test('old generation, wrong consumer and unnegotiated size are rejected',()=>{
  for(const [name,patch] of [['frame',{inputGeneration:'2'}],['state',{poolGeneration:'3'}],['controlApplied',{consumerEpoch:'2'}],['helloAccepted',{consumerId:'other'}],['helloAccepted',{width:1280}],['stopped',{hostInstanceId:'old'}]])assert.throws(()=>validateWorkerMessage({...fixture[name],...patch},stream,config));
});
