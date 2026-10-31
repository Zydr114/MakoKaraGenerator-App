'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { importAndTransferFrame } = require('./import-consumer.cjs');

function fixture(overrides={}) {
  const calls=[];
  let final;
  const options={
    packet:{ metadata:{}, fds:[10,11] },
    broker:{ closeFd:fd=>calls.push(`close:${fd}`) },
    sharedTexture:{
      importSharedTexture:config=>{final=config.allReferencesReleased;calls.push('import');return {release(){calls.push('release');final();}};},
      sendSharedTexture:async()=>calls.push('send'),
    },
    validate:()=>({width:64,height:64,planes:[{fdIndex:0,stride:256,offset:0,size:16384}],modifier:'0'}),
    timestamp:()=>1, targetFrame:()=>({}), shouldTransfer:()=>true,
    onImported:()=>calls.push('tracked'),
    onAllReferencesReleased:()=>calls.push('ack'),
    onAbandonedRelease:()=>calls.push('quarantined'),
    ...overrides,
  };
  return {options,calls};
}
test('received FDs close before transfer; final callback follows sole main release',async()=>{
  const {options,calls}=fixture(); await importAndTransferFrame(options);
  assert.deepEqual(calls,['import','tracked','close:10','close:11','send','release','ack']);
});
test('tracking hook failure releases the imported reference and cannot ACK',async()=>{
  const expected=new Error('tracking failed');const {options,calls}=fixture({onImported:()=>{throw expected;}});
  await assert.rejects(importAndTransferFrame(options),error=>error===expected);
  assert.equal(calls.filter(x=>x==='release').length,1);assert.ok(calls.includes('quarantined'));assert.ok(!calls.includes('ack'));assert.ok(calls.includes('close:10')&&calls.includes('close:11'));
});
test('validation failure closes every FD and preserves its error despite close failure',async()=>{
  const expected=new Error('invalid lease');const calls=[];const {options}=fixture({validate:()=>{throw expected;},broker:{closeFd:fd=>{calls.push(fd);throw new Error('close failed');}}});
  await assert.rejects(importAndTransferFrame(options),error=>error===expected);assert.deepEqual(calls,[10,11]);
});
test('transfer failure releases once, quarantines and retains the original failure',async()=>{
  const expected=new Error('renderer exited');const {options,calls}=fixture();options.sharedTexture.sendSharedTexture=async()=>{throw expected;};options.onFailure=()=>{throw new Error('secondary');};
  await assert.rejects(importAndTransferFrame(options),error=>error===expected);assert.equal(calls.filter(x=>x==='release').length,1);assert.ok(calls.includes('quarantined'));assert.ok(!calls.includes('ack'));
});
test('a release that calls back and then throws never ACKs',async()=>{
  const {options,calls}=fixture();
  const original=options.sharedTexture.importSharedTexture;
  options.sharedTexture.importSharedTexture=config=>{const ref=original(config);return {release(){ref.release();throw new Error('release failed');}};};
  await assert.rejects(importAndTransferFrame(options),/release failed/);
  assert.ok(!calls.includes('ack'));assert.ok(calls.includes('quarantined'));
});
test('completion observer exceptions cannot unwind into the native callback',async()=>{
  const {options}=fixture();let failures=0;
  options.onAllReferencesReleased=()=>{throw new Error('observer');};options.onFailure=()=>{failures++;};
  await importAndTransferFrame(options);assert.equal(failures,1);
});
