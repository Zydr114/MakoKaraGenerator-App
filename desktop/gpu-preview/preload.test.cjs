'use strict';
const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const {EventEmitter}=require('node:events');
const turn=()=>new Promise(done=>setImmediate(done));
async function fixture(){
  const ipc=new EventEmitter(),pending=[];let api,boot,receiver,draws=0,releases=0;
  ipc.invoke=async(name,...args)=>{
    if(name==='mako-gpu:config')return {mode:'production'};
    if(name==='mako-gpu:clock')return {mainMonotonicNs:BigInt(Math.round(performance.now()*1e6)).toString()};
    if(name==='mako-gpu:playback')return new Promise(resolve=>pending.push({args,resolve}));
    return undefined;
  };
  ipc.send=()=>{};
  const canvas={width:64,height:64,getContext:()=>({clearRect(){},drawImage(){draws++;}})};
  vm.runInNewContext(fs.readFileSync(path.join(__dirname,'preload.cjs'),'utf8'),{
    require:name=>{assert.equal(name,'electron');return {contextBridge:{exposeInMainWorld(_name,value){api=value;}},ipcRenderer:ipc,sharedTexture:{setSharedTextureReceiver(value){receiver=value;}}};},
    window:{addEventListener(_name,callback){boot=callback;}},document:{getElementById:()=>canvas},performance,process:{sandboxed:true,contextIsolated:true,versions:{}},setTimeout,requestAnimationFrame:callback=>setImmediate(callback),
  });
  await boot();
  const identity={protocolVersion:1,streamId:'stream',hostInstanceId:'host',producerEpoch:'1',inputGeneration:'1',consumerEpoch:'1',poolGeneration:'1'};
  const emit=(name,value)=>ipc.emit(`mako-gpu:${name}`,{},value);
  emit('attached',identity);
  const control=(mode='paused')=>({mode,mediaTimeUs:0,rate:1,targetFps:24,timelineResetId:'reset',samplePerformanceMs:performance.now()});
  const barrier=seq=>emit('barrier',{...identity,timelineEpoch:'1',timelineResetId:'reset',minControlSeq:seq});
  const frame=async seq=>receiver({importedSharedTexture:{getVideoFrame:()=>({timestamp:seq,displayWidth:64,displayHeight:64,close(){}}),release(){releases++;}}},{...identity,timelineEpoch:'1',timelineResetId:'reset',controlSeq:seq,frameSeq:seq,slotId:0,slotEpoch:String(seq),timeUs:seq,width:64,height:64});
  return {api,pending,emit,control,barrier,frame,get draws(){return draws;},get releases(){return releases;}};
}
test('unknown hard barrier stays blocked across old applied events and same-reset mode changes',async()=>{
  const f=await fixture();
  const first=f.api.setPlaybackState(f.control());await turn();f.pending.shift().resolve({controlSeq:1});await first;f.barrier(1);await f.frame(1);assert.equal(f.draws,1);
  const playing=f.api.setPlaybackState(f.control('playing'));await turn();f.barrier(1);await f.frame(1);assert.equal(f.draws,1);
  f.pending.shift().resolve({controlSeq:2});await playing;await f.frame(1);assert.equal(f.draws,1);f.barrier(2);await f.frame(2);assert.equal(f.draws,2);
  const pause=f.api.setPlaybackState(f.control('paused'));const play=f.api.setPlaybackState(f.control('playing'));await turn();
  f.pending.shift().resolve({controlSeq:3});await pause;f.barrier(3);await f.frame(3);assert.equal(f.draws,2);
  f.pending.shift().resolve({controlSeq:4});await play;await f.frame(3);assert.equal(f.draws,2);f.barrier(4);await f.frame(4);assert.equal(f.draws,3);
  assert.equal(f.releases,7,'every stale frame still closes its imported reference');
});
test('detach before invoke response cannot resurrect or throw through the old desired object',async()=>{
  const f=await fixture();const response=f.api.setPlaybackState(f.control());await turn();f.emit('detach');f.pending.shift().resolve({controlSeq:1});await response;f.barrier(1);await f.frame(1);assert.equal(f.draws,0);assert.equal(f.releases,1);
});
test('ordinary clock correction cannot clear an unresolved hard barrier',async()=>{
  const f=await fixture();
  const first=f.api.setPlaybackState(f.control());await turn();f.pending.shift().resolve({controlSeq:1});await first;f.barrier(1);
  const hard=f.api.setPlaybackState(f.control('playing'));const correction=f.api.setPlaybackState(f.control('playing'));await turn();
  f.barrier(1);await f.frame(1);assert.equal(f.draws,0);
  f.pending[1].resolve({controlSeq:3});await correction;f.barrier(1);await f.frame(1);assert.equal(f.draws,0);
  f.pending[0].resolve({controlSeq:2});await hard;f.barrier(2);await f.frame(2);assert.equal(f.draws,1);
});
