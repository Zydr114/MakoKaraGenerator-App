'use strict';
const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const {EventEmitter}=require('node:events');
const turn=()=>new Promise(done=>setImmediate(done));
async function fixture(options={}){
  const ipc=new EventEmitter(),pending=[],barrierTimeouts=[];let api,boot,receiver,draws=0,releases=0;
  ipc.invoke=async(name,...args)=>{
    if(name==='mako-gpu:config')return {mode:'production',performanceDiagnostics:!!options.diagnostics};
    if(name==='mako-gpu:attach')return options.attachResponse?.(...args);
    if(name==='mako-gpu:clock')return {mainMonotonicNs:BigInt(Math.round(performance.now()*1e6)).toString()};
    if(name==='mako-gpu:playback')return new Promise(resolve=>pending.push({args,resolve}));
    return undefined;
  };
  ipc.send=()=>{};
  const canvas={width:64,height:64,getContext:()=>({clearRect(){},drawImage(){draws++;const end=performance.now()+(options.drawDelayMs??0);while(performance.now()<end){}}})};
  vm.runInNewContext(fs.readFileSync(path.join(__dirname,'preload.cjs'),'utf8'),{
    require:name=>{assert.equal(name,'electron');return {contextBridge:{exposeInMainWorld(_name,value){api=value;}},ipcRenderer:ipc,sharedTexture:{setSharedTextureReceiver(value){receiver=value;}}};},
    window:{addEventListener(_name,callback){boot=callback;}},document:{getElementById:()=>canvas},performance,process:{sandboxed:true,contextIsolated:true,versions:{}},setTimeout:(callback,ms)=>{if(options.manualBarrierTimeout&&ms===1000){barrierTimeouts.push(callback);return 0;}return setTimeout(callback,ms);},clearTimeout,setInterval:()=>0,requestAnimationFrame:callback=>setImmediate(callback),
  });
  await boot();
  const identity={protocolVersion:1,streamId:'stream',hostInstanceId:'host',producerEpoch:'1',inputGeneration:'1',consumerEpoch:'1',poolGeneration:'1'};
  const emit=(name,value)=>ipc.emit(`mako-gpu:${name}`,{},value);
  if(!options.detachedStart)emit('attached',identity);
  const control=(mode='paused')=>({mode,mediaTimeUs:0,rate:1,targetFps:24,timelineResetId:'reset',samplePerformanceMs:performance.now()});
  const barrier=seq=>emit('barrier',{...identity,timelineEpoch:'1',timelineResetId:'reset',minControlSeq:seq});
  const frame=async (seq,extra={})=>receiver({importedSharedTexture:{getVideoFrame:()=>({timestamp:seq,displayWidth:64,displayHeight:64,close(){}}),release(){releases++;}}},{...identity,timelineEpoch:'1',timelineResetId:'reset',controlSeq:seq,frameSeq:seq,slotId:0,slotEpoch:String(seq),timeUs:seq,width:64,height:64,...extra});
  return {api,pending,emit,control,barrier,frame,identity,expireBarrierWait:()=>barrierTimeouts.splice(0).forEach(callback=>callback()),get draws(){return draws;},get releases(){return releases;}};
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
test('attached reset and repeated semantic state confirm the actual applied sequence without admitting older frames',async()=>{
  const f=await fixture();const first=f.api.setPlaybackState(f.control('playing'));await turn();f.pending.shift().resolve({controlSeq:1});await first;f.barrier(1);
  f.emit('attached',f.identity);
  const repeated=f.api.setPlaybackState(f.control('playing'));await turn();f.pending.shift().resolve({controlSeq:2});await repeated;
  f.emit('barrier',{...f.identity,timelineEpoch:'1',timelineResetId:'reset',minControlSeq:1,controlSeq:2});
  await f.frame(1);assert.equal(f.draws,0);
  await f.frame(2);assert.equal(f.draws,1);
});
test('explicit diagnostics attribute a slow draw to its exact lease while ordinary mode has no diagnostics',async()=>{
  const normal=await fixture();assert.equal((await normal.api.readPreviewState()).rendererPerformanceDiagnostics,undefined);
  const f=await fixture({diagnostics:true,drawDelayMs:8}),p=f.api.setPlaybackState(f.control());await turn();f.pending.shift().resolve({controlSeq:1});await p;f.barrier(1);await f.frame(1);
  const event=(await f.api.readPreviewState()).rendererPerformanceDiagnostics.events.find(e=>e.type==='renderer-frame');
  assert.equal(event.lease.frameSeq,1);assert.equal(event.lease.streamId,'stream');assert.ok(event.drawMs>=7);assert.equal(f.releases,1);
});

test('final paused frame waits for exact invoke response after its applied barrier',async()=>{
  const f=await fixture();
  const state=f.api.setPlaybackState(f.control('paused'));await turn();
  f.barrier(1);
  const delivered=f.frame(1);await turn();
  assert.equal(f.draws,0,'unknown hard sequence cannot display the frame');
  f.pending.shift().resolve({controlSeq:1});await state;await delivered;
  assert.equal(f.draws,1,'the sole paused target must be drawn after response confirmation');
  assert.equal(f.releases,1);
});
test('final paused frame waits for exact applied barrier after invoke response',async()=>{
  const f=await fixture();
  const state=f.api.setPlaybackState(f.control('paused'));await turn();
  f.pending.shift().resolve({controlSeq:1});await state;
  const delivered=f.frame(1);await turn();assert.equal(f.draws,0);
  f.barrier(1);await delivered;
  assert.equal(f.draws,1,'frame delivered before the barrier must be reconsidered after it');
  assert.equal(f.releases,1);
});
test('a pending paused frame cannot cross detach or a replacement attachment',async()=>{
  for(const replacement of [false,true]){
    const f=await fixture();const state=f.api.setPlaybackState(f.control());await turn();
    const delivered=f.frame(1);await turn();
    if(replacement)f.emit('attached',{...f.identity,inputGeneration:'2'});else f.emit('detach');
    f.pending.shift().resolve({controlSeq:1});await state;f.barrier(1);await delivered;
    assert.equal(f.draws,0);assert.equal(f.releases,1);
  }
});

test('a missing paused barrier releases its retained texture at the bounded deadline',async()=>{
  const f=await fixture({manualBarrierTimeout:true});const state=f.api.setPlaybackState(f.control());await turn();
  f.pending.shift().resolve({controlSeq:1});await state;
  const delivered=f.frame(1);await turn();assert.equal(f.releases,0);
  f.expireBarrierWait();await delivered;
  assert.equal(f.draws,0);assert.equal(f.releases,1);
  assert.equal((await f.api.readPreviewState()).rendererGate.lastReason,'waitingBarrierTimeout');
  f.barrier(1);assert.equal(f.draws,0,'a returned lease cannot be displayed later');
});

test('paused waiting never admits mismatched attachment identities',async()=>{
  for(const key of ['streamId','hostInstanceId','producerEpoch','inputGeneration','consumerEpoch','poolGeneration']){
    const f=await fixture();const state=f.api.setPlaybackState(f.control());await turn();
    await f.frame(1,{[key]:'stale'});
    f.pending.shift().resolve({controlSeq:1});await state;f.barrier(1);
    assert.equal(f.draws,0,key);assert.equal(f.releases,1,key);
  }
});
test('a newer playing intent wakes and retires the pending paused target',async()=>{
  const f=await fixture();const paused=f.api.setPlaybackState(f.control());await turn();
  const delivered=f.frame(1);await turn();assert.equal(f.releases,0);
  const playing=f.api.setPlaybackState(f.control('playing'));await turn();await delivered;
  assert.equal(f.draws,0);assert.equal(f.releases,1);
  f.pending[0].resolve({controlSeq:1});await paused;f.barrier(1);
  f.pending[1].resolve({controlSeq:2});await playing;f.barrier(2);await f.frame(2);
  assert.equal(f.draws,1);assert.equal(f.releases,2);
});

// 附着的返回值与 attached 事件分开送达，事件可能晚到：返回值先生效，晚到的同序号事件不能清掉首个暂停控制。
test('the attach response applies first and a late attached event cannot wipe the first paused control',async()=>{
  let response;const f=await fixture({detachedStart:true,attachResponse:()=>response});
  response={...f.identity,attachSeq:1};
  await f.api.attachPreview('stream','1');
  const state=f.api.setPlaybackState(f.control());await turn();
  f.emit('attached',{...f.identity,attachSeq:1});
  f.pending.shift().resolve({controlSeq:1});await state;f.barrier(1);await f.frame(1);
  assert.equal(f.draws,1,'the sole paused frame of a new attachment must be drawn');
  assert.equal((await f.api.readPreviewState()).rendererGate.hardControlSeq,1);
});
test('a detach event from a replaced attachment cannot detach the newer one',async()=>{
  let response;const f=await fixture({detachedStart:true,attachResponse:()=>response});
  f.emit('attached',{...f.identity,attachSeq:1});
  response={...f.identity,inputGeneration:'2',attachSeq:2};
  await f.api.attachPreview('stream','2');
  f.emit('detach',{attachSeq:1});
  const state=f.api.setPlaybackState(f.control());await turn();f.pending.shift().resolve({controlSeq:1});await state;
  f.barrier(1);await f.frame(1,{inputGeneration:'2'});
  assert.equal(f.draws,1);
});

test('SHM WebGL2 receiver enforces RGBA8+sRGB, deletes immutable texture on resize, toggles uSwizzleBgra, and sends lightweight lease ACKs',async()=>{
  const ipc=new EventEmitter(),pending=[],portReplies=[],glOps=[];
  let api,boot;
  ipc.invoke=async(name)=>{
    if(name==='mako-gpu:config')return {mode:'production',transport:'shm'};
    if(name==='mako-gpu:clock')return {mainMonotonicNs:BigInt(Math.round(performance.now()*1e6)).toString()};
    if(name==='mako-gpu:playback')return new Promise(resolve=>pending.push({resolve}));
    return undefined;
  };
  ipc.send=(channel,payload)=>{if(channel==='mako-gpu:error')throw new Error(`Unexpected preload error: ${payload}`);};
  let nextTexId=1;
  const fakeGl={
    VERTEX_SHADER:0x8b31,FRAGMENT_SHADER:0x8b30,COMPILE_STATUS:0x8b81,LINK_STATUS:0x8b82,
    BLEND:0x0be2,DEPTH_TEST:0x0b71,SCISSOR_TEST:0x0c11,
    UNPACK_PREMULTIPLY_ALPHA_WEBGL:0x9241,UNPACK_FLIP_Y_WEBGL:0x9240,UNPACK_COLORSPACE_CONVERSION_WEBGL:0x9243,
    UNPACK_ALIGNMENT:0x0cf5,UNPACK_ROW_LENGTH:0x0cf2,NONE:0,
    TEXTURE0:0x84c0,TEXTURE_2D:0x0de1,TEXTURE_MIN_FILTER:0x2801,TEXTURE_MAG_FILTER:0x2800,
    TEXTURE_WRAP_S:0x2802,TEXTURE_WRAP_T:0x2803,NEAREST:0x2600,CLAMP_TO_EDGE:0x812f,
    RGBA8:0x8058,RGBA:0x1908,UNSIGNED_BYTE:0x1401,TRIANGLES:0x0004,NO_ERROR:0,
    drawingBufferColorSpace:'uninitialized',
    createShader:()=>({}),shaderSource(){},compileShader(){},getShaderParameter:()=>true,deleteShader(){},
    createProgram:()=>({}),attachShader(){},linkProgram(){},getProgramParameter:()=>true,
    createVertexArray:()=>({}),disable(){},
    pixelStorei(param,val){glOps.push(['pixelStorei',param,val]);},
    getUniformLocation:(_p,name)=>name,
    createTexture:()=>({id:nextTexId++}),
    deleteTexture(tex){glOps.push(['deleteTexture',tex.id]);},
    activeTexture(){},bindTexture(){},texParameteri(){},
    texStorage2D(_t,_l,internalFormat,w,h){glOps.push(['texStorage2D',internalFormat,w,h]);},
    viewport(_x,_y,w,h){glOps.push(['viewport',w,h]);},
    texSubImage2D(_t,_l,_x,_y,w,h,_f,_ty,buf){glOps.push(['texSubImage2D',w,h,buf.byteLength]);},
    useProgram(){},
    uniform1i(loc,val){if(loc==='uSwizzleBgra'||loc==='uHeight')glOps.push(['uniform1i',loc,val]);},
    bindVertexArray(){},
    drawArrays(){glOps.push(['drawArrays']);},
    getError:()=>0,
  };
  const canvas={width:64,height:64,getContext(kind,attrs){assert.equal(kind,'webgl2');assert.equal(attrs.premultipliedAlpha,true);return fakeGl;},addEventListener(){}};
  vm.runInNewContext(fs.readFileSync(path.join(__dirname,'preload.cjs'),'utf8'),{
    require:name=>{assert.equal(name,'electron');return {contextBridge:{exposeInMainWorld(_n,v){api=v;}},ipcRenderer:ipc,sharedTexture:undefined};},
    window:{addEventListener(_n,cb){boot=cb;}},document:{getElementById:()=>canvas},Uint8Array,performance,process:{sandboxed:true,contextIsolated:true,versions:{}},setTimeout,clearTimeout,setInterval:()=>0,requestAnimationFrame:cb=>setImmediate(cb),
  });
  await boot();

  const identity={protocolVersion:1,streamId:'s1',hostInstanceId:'h1',producerEpoch:'1',inputGeneration:'1',consumerEpoch:'1',poolGeneration:'1'};
  const port={onmessage:null,started:false,start(){this.started=true;},postMessage(msg){assert.equal(msg.buffer,undefined,'ACK must never echo pixel buffer');portReplies.push(msg);}};
  ipc.emit('mako-gpu:shm-port',{ports:[port]},{...identity,attachSeq:1});
  assert.equal(port.started,true);

  const play=api.setPlaybackState({mode:'playing',mediaTimeUs:0,rate:1,targetFps:24,timelineResetId:'r1',samplePerformanceMs:performance.now()});
  await turn();pending.shift().resolve({controlSeq:1});await play;
  ipc.emit('mako-gpu:barrier',{},{...identity,timelineEpoch:'1',timelineResetId:'r1',minControlSeq:1});

  // Frame 1: 64x64 bgra8888 compact stride (256)
  port.onmessage({data:{type:'frame',metadata:{...identity,timelineEpoch:'1',timelineResetId:'r1',controlSeq:1,frameSeq:1,slotId:0,slotEpoch:'1',timeUs:100,width:64,height:64,pixelFormat:'bgra8888',strideBytes:256},buffer:new Uint8Array(64*64*4)}});
  await turn();
  assert.equal(fakeGl.drawingBufferColorSpace,'srgb');
  assert.ok(glOps.some(op=>op[0]==='pixelStorei'&&op[1]===fakeGl.UNPACK_FLIP_Y_WEBGL&&op[2]===false));
  assert.ok(glOps.some(op=>op[0]==='pixelStorei'&&op[1]===fakeGl.UNPACK_PREMULTIPLY_ALPHA_WEBGL&&op[2]===false));
  assert.ok(glOps.some(op=>op[0]==='texStorage2D'&&op[1]===fakeGl.RGBA8&&op[2]===64&&op[3]===64));
  assert.ok(glOps.some(op=>op[0]==='uniform1i'&&op[1]==='uSwizzleBgra'&&op[2]===1));

  // Frame 2: Same size 64x64 rgba8888 padded stride (320) -> NO deleteTexture/texStorage2D, uSwizzleBgra = 0, UNPACK_ROW_LENGTH = 80
  glOps.length=0;
  port.onmessage({data:{type:'frame',metadata:{...identity,timelineEpoch:'1',timelineResetId:'r1',controlSeq:1,frameSeq:2,slotId:1,slotEpoch:'1',timeUs:200,width:64,height:64,pixelFormat:'rgba8888',strideBytes:320},buffer:new Uint8Array(320*64)}});
  await turn();
  assert.ok(!glOps.some(op=>op[0]==='deleteTexture'));
  assert.ok(!glOps.some(op=>op[0]==='texStorage2D'));
  assert.ok(glOps.some(op=>op[0]==='pixelStorei'&&op[1]===fakeGl.UNPACK_ROW_LENGTH&&op[2]===80));
  assert.ok(glOps.some(op=>op[0]==='uniform1i'&&op[1]==='uSwizzleBgra'&&op[2]===0));

  // Frame 3: Resize to 128x72 -> MUST deleteTexture(1) BEFORE texStorage2D(128, 72)
  glOps.length=0;
  port.onmessage({data:{type:'frame',metadata:{...identity,timelineEpoch:'1',timelineResetId:'r1',controlSeq:1,frameSeq:3,slotId:2,slotEpoch:'1',timeUs:300,width:128,height:72,pixelFormat:'bgra8888',strideBytes:512},buffer:new Uint8Array(512*72)}});
  await turn();
  const delIdx=glOps.findIndex(op=>op[0]==='deleteTexture'&&op[1]===1);
  const allocIdx=glOps.findIndex(op=>op[0]==='texStorage2D'&&op[2]===128&&op[3]===72);
  assert.ok(delIdx>=0&&allocIdx>delIdx,'deleteTexture must precede replacement texStorage2D on resize');

  assert.equal(portReplies.length,3);
  assert.ok(portReplies.every(r=>r.type==='drawn'));

  const cfg=await api.previewConfig();
  assert.equal(cfg.transport,'shm');

  // Stale shm-port from an older attachSeq must be closed and ignored without clobbering active port
  let staleClosed=false;
  const stalePort={onmessage:null,start(){throw new Error('stale port must not start');},close(){staleClosed=true;}};
  ipc.emit('mako-gpu:shm-port',{ports:[stalePort]},{...identity,attachSeq:0});
  assert.equal(staleClosed,true);
});

test('SHM receiver keeps active port open across mako-gpu:detach so queued/paused frames still emit released ACKs immediately',async()=>{
  const ipc=new EventEmitter(),pending=[],portReplies=[];
  let api,boot,closed=false;
  ipc.invoke=async(name)=>{
    if(name==='mako-gpu:config')return {mode:'production',transport:'shm'};
    if(name==='mako-gpu:clock')return {mainMonotonicNs:BigInt(Math.round(performance.now()*1e6)).toString()};
    if(name==='mako-gpu:playback')return new Promise(resolve=>pending.push({resolve}));
    return undefined;
  };
  ipc.send=(channel,payload)=>{if(channel==='mako-gpu:error')throw new Error(`Unexpected preload error: ${payload}`);};
  const canvas={width:64,height:64,getContext:()=>null,addEventListener(){}};
  vm.runInNewContext(fs.readFileSync(path.join(__dirname,'preload.cjs'),'utf8'),{
    require:name=>{assert.equal(name,'electron');return {contextBridge:{exposeInMainWorld(_n,v){api=v;}},ipcRenderer:ipc,sharedTexture:undefined};},
    window:{addEventListener(_n,cb){boot=cb;}},document:{getElementById:()=>canvas},Uint8Array,performance,process:{sandboxed:true,contextIsolated:true,versions:{}},setTimeout,clearTimeout,setInterval:()=>0,requestAnimationFrame:cb=>setImmediate(cb),
  });
  await boot();

  const identity={protocolVersion:1,streamId:'s-detach',hostInstanceId:'h1',producerEpoch:'1',inputGeneration:'1',consumerEpoch:'1',poolGeneration:'1'};
  const port={onmessage:null,start(){},close(){closed=true;},postMessage(msg){portReplies.push(msg);}};
  ipc.emit('mako-gpu:shm-port',{ports:[port]},{...identity,attachSeq:1});

  const pauseReq=api.setPlaybackState({mode:'paused',mediaTimeUs:0,rate:1,targetFps:24,timelineResetId:'r1',samplePerformanceMs:performance.now()});
  await turn();pending.shift().resolve({controlSeq:1});await pauseReq;

  ipc.emit('mako-gpu:detach',{attachSeq:1});
  assert.equal(closed,false,'mako-gpu:detach must not prematurely close activeShmPort');

  port.onmessage({data:{type:'frame',metadata:{...identity,timelineEpoch:'1',timelineResetId:'r1',controlSeq:1,frameSeq:7,slotId:2,slotEpoch:'1',timeUs:500,width:64,height:64,pixelFormat:'bgra8888',strideBytes:256},buffer:new Uint8Array(64*64*4)}});
  await turn();
  assert.equal(portReplies.length,1);
  assert.deepStrictEqual({...portReplies[0]},{
    type:'released',
    streamId:'s-detach',hostInstanceId:'h1',producerEpoch:'1',inputGeneration:'1',
    consumerEpoch:'1',poolGeneration:'1',timelineEpoch:'1',slotId:2,slotEpoch:'1',frameSeq:7,
  });
});
