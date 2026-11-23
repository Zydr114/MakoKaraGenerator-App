import {afterEach,beforeEach,expect,it,vi} from 'vitest';
import {BackendError, OutcomeUnknownError, type MakoClient} from '../../src/client';
import {revision,type DesktopPreviewBridge,type DesktopPreviewEvent,type GpuPlaybackState,type GpuPreviewCreateInput,type GpuPreviewStreamState,type PreviewStreamApi,type PreviewStreamTransport} from '../../src/protocol';
import {flushDesktopPreviewOperations, GpuPreviewController, suspendProjectPreviewForClose} from '../../src/editor/preview/gpu-preview-controller';

const deferred=()=>{let resolve!:()=>void;const promise=new Promise<void>(done=>{resolve=done;});return {promise,resolve};};
beforeEach(()=>vi.stubGlobal('window',{}));
afterEach(()=>{vi.restoreAllMocks();vi.unstubAllGlobals();});
function fixture(options:{createGate?:Promise<void>;replaceGate?:Promise<void>;resizeGate?:Promise<void>;onCreate?:()=>void;onReplace?:()=>void;createError?:()=>Error|undefined;replaceFails?:()=>boolean;resizeUnknown?:boolean;resizeError?:BackendError;acceptanceEnabled?:boolean;transportFor?:(api:PreviewStreamApi)=>PreviewStreamTransport}={}){
  const calls:string[]=[];let nextId=0;let attached=false;
  const creates:GpuPreviewCreateInput[]=[];
  const resizes:{expectedStreamRevision:string;size:GpuPreviewStreamState['size']}[]=[];
  const closes:{id:string;expectedStreamRevision:string}[]=[];
  const streams=new Map<string,GpuPreviewStreamState>();
  const listeners=new Set<(event:DesktopPreviewEvent)=>void>();
  const playbackStates:GpuPlaybackState[]=[];
  const state=(id:string,source:GpuPreviewStreamState['source'],generation='1',alphaMode:GpuPreviewStreamState['alphaMode']='opaque',api:PreviewStreamApi='vulkan'):GpuPreviewStreamState=>({streamId:id,streamRevision:generation,inputGeneration:generation,registrationState:'ready',source,size:{width:1280,height:720},alphaMode,textureAlphaMode:'premultiplied',api,transport:options.transportFor?.(api)??(api==='cpu'?'shm':'dma-buf'),producerEpoch:generation,poolGeneration:'1',slotCount:3,allocationBytes:4096,lastAppliedControlSeq:0});
  const client={gpuPreview:{
    create:async(input:GpuPreviewCreateInput)=>{calls.push(`create:${input.source.sourceId}`);creates.push(input);options.onCreate?.();await options.createGate;const err=options.createError?.();if(err)throw err;const next={...state(`s${++nextId}`,input.source,'1',input.alphaMode,input.api),size:input.size};streams.set(next.streamId,next);return next;},
    replaceInput:async(id:string,input:{source:GpuPreviewStreamState['source']})=>{calls.push(`replace:${id}`);options.onReplace?.();await options.replaceGate;if(options.replaceFails?.())throw new Error('replace failed');const prev=streams.get(id)!;const next={...state(id,input.source,'2',prev.alphaMode,prev.api),size:prev.size};streams.set(id,next);return next;},
    resize:async(id:string,input:typeof resizes[number])=>{
      calls.push(`resize:${id}`);resizes.push(input);await options.resizeGate;
      if(options.resizeError)throw options.resizeError;
      const prev=streams.get(id)!;
      const generation=String(Number(prev.inputGeneration)+1);
      const next={...prev,streamRevision:String(Number(prev.streamRevision)+1),inputGeneration:generation,producerEpoch:generation,size:input.size};
      streams.set(id,next);
      if(options.resizeUnknown)throw new OutcomeUnknownError('resize-1',new Error('dropped response'));
      return next;
    },
    list:async()=>{calls.push('list');return {streams:[...streams.values()],protocolVersion:'1'};},
    close:async(id:string,expectedStreamRevision:string)=>{calls.push(`close:${id}`);closes.push({id,expectedStreamRevision});streams.delete(id);},
  }} as unknown as MakoClient;
  const bridge={
    acceptanceEnabled:async()=>options.acceptanceEnabled??false,consumerId:async()=>'a'.repeat(32),
    attachPreview:async(id:string,generation:string)=>{calls.push(`attach:${id}:${generation}`);attached=true;},
    detachPreview:async()=>{calls.push('detach');attached=false;},
    setPlaybackState:async(state:GpuPlaybackState)=>{if(!attached)throw new Error('not attached');const now=performance.now();if(!Number.isFinite(state.samplePerformanceMs)||state.samplePerformanceMs<0||state.samplePerformanceMs>now+5||now-state.samplePerformanceMs>30000)throw new Error('Invalid audio clock sample');calls.push('playback');samples.push(state.samplePerformanceMs);playbackStates.push(state);return {controlSeq:1};},
    onPreviewState:(listener:(event:DesktopPreviewEvent)=>void)=>{listeners.add(listener);return()=>listeners.delete(listener);},
  } as unknown as DesktopPreviewBridge;
  const make=(id='p1',api:PreviewStreamApi='vulkan')=>new GpuPreviewController(client,id,{width:1280,height:720},bridge,undefined,api);
  const show=(controller:GpuPreviewController,rev='1',targetFps=24)=>controller.show(revision(rev),{mode:'paused',mediaTimeUs:0,rate:1,targetFps,timelineResetId:'reset',samplePerformanceMs:performance.now()});
  const samples:number[]=[];
  return {calls,creates,resizes,closes,streams,make,show,samples,playbackStates,emit:(event:DesktopPreviewEvent)=>listeners.forEach(listener=>listener(event))};
}
it('requests opaque composition preview and retains mode across revision replacement and resize',async()=>{
  const f=fixture({acceptanceEnabled:true});const controller=f.make();f.show(controller);
  try{
    await vi.waitFor(()=>expect(f.calls).toContain('playback'));
    expect(f.creates).toHaveLength(1);
    expect(f.creates[0]).toMatchObject({alphaMode:'opaque',api:'vulkan'});
    expect(f.streams.get('s1')?.alphaMode).toBe('opaque');
    f.show(controller,'2');
    await vi.waitFor(()=>expect(f.calls.filter(x=>x==='playback')).toHaveLength(2));
    expect(f.streams.get('s1')).toMatchObject({source:{sourceRevision:'2'},alphaMode:'opaque',inputGeneration:'2'});
    const resized=await window.makoGpuPreviewAcceptance!.resize(2560,1440);
    expect(resized).toMatchObject({alphaMode:'opaque',size:{width:2560,height:1440},inputGeneration:'3'});
    expect(f.creates).toHaveLength(1);
    expect(f.calls).toContain('attach:s1:3');
  }finally{
    controller.dispose();await vi.waitFor(()=>expect(f.calls).toContain('close:s1'));
  }
});
it.each(['vulkan','cpu'] as const)('resizes the same %s stream and resumes playback at the selected fps',async(api)=>{
  const f=fixture({transportFor:()=> 'shm'});const controller=f.make('p1',api);f.show(controller);
  try{
    await flushDesktopPreviewOperations();
    const size={width:2560,height:1440};
    controller.setSize(size);
    f.show(controller,'1',30);
    await flushDesktopPreviewOperations();
    expect(f.resizes).toEqual([{expectedStreamRevision:'1',size}]);
    expect(f.calls).toEqual(['create:p1','attach:s1:1','playback','detach','resize:s1','attach:s1:2','playback']);
    expect(f.playbackStates.map(state=>state.targetFps)).toEqual([24,30]);
    expect(f.streams.get('s1')).toMatchObject({size,api,transport:'shm',inputGeneration:'2'});
    f.emit({type:'drawn',streamId:'s1',producerEpoch:'1',inputGeneration:'1',timelineResetId:'reset',timeUs:500});
    expect(controller.state$.get().status).toBe('rendering');
    f.emit({type:'drawn',streamId:'s1',producerEpoch:'2',inputGeneration:'2',timelineResetId:'reset',timeUs:600});
    expect(controller.state$.get()).toMatchObject({status:'ready',frameTimeUs:600});
    controller.setSize({...size});
    await flushDesktopPreviewOperations();
    expect(f.resizes).toHaveLength(1);
    expect(f.playbackStates).toHaveLength(2);
  }finally{
    controller.dispose();await flushDesktopPreviewOperations();
  }
});
it('coalesces rapid size changes and only attaches the latest generation',async()=>{
  const gate=deferred();const f=fixture({resizeGate:gate.promise});const controller=f.make();f.show(controller);
  try{
    await flushDesktopPreviewOperations();
    const high={width:2560,height:1440}, low={width:1280,height:720};
    controller.setSize(high);
    f.show(controller,'1',30);
    await vi.waitFor(()=>expect(f.calls).toContain('resize:s1'));
    controller.setSize(low);
    controller.setSize(high);
    controller.setSize(low);
    f.show(controller,'1',24);
    gate.resolve();
    await flushDesktopPreviewOperations();
    expect(f.resizes).toEqual([{expectedStreamRevision:'1',size:high},{expectedStreamRevision:'2',size:low}]);
    expect(f.calls).toEqual(['create:p1','attach:s1:1','playback','detach','resize:s1','resize:s1','attach:s1:3','playback']);
    expect(f.playbackStates.map(state=>state.targetFps)).toEqual([24,24]);
  }finally{
    gate.resolve();controller.dispose();await flushDesktopPreviewOperations();
  }
});
it.each([false,true])('disposes during resize without reattaching (unknown outcome: %s)',async(resizeUnknown)=>{
  const gate=deferred();const f=fixture({resizeGate:gate.promise,resizeUnknown});const old=f.make();f.show(old);
  let next:GpuPreviewController|undefined;
  try{
    await flushDesktopPreviewOperations();
    old.setSize({width:2560,height:1440});
    await vi.waitFor(()=>expect(f.calls).toContain('resize:s1'));
    old.dispose();next=f.make('p2');f.show(next);
    gate.resolve();
    await flushDesktopPreviewOperations();
    expect(f.closes).toEqual([{id:'s1',expectedStreamRevision:'2'}]);
    expect(f.calls).toEqual(['create:p1','attach:s1:1','playback','detach','resize:s1',...(resizeUnknown?['list']:[]),'close:s1','create:p2','attach:s2:1','playback']);
  }finally{
    gate.resolve();if(next)next.dispose();else old.dispose();await flushDesktopPreviewOperations();
  }
});
it('reconciles an unknown resize before recovering at the latest desired size',async()=>{
  const gate=deferred();const f=fixture({resizeGate:gate.promise,resizeUnknown:true});const controller=f.make();f.show(controller);
  try{
    await flushDesktopPreviewOperations();
    controller.setSize({width:2560,height:1440});
    await vi.waitFor(()=>expect(f.calls).toContain('resize:s1'));
    const latest={width:1920,height:1080};
    controller.setSize(latest);
    f.show(controller,'1',30);
    gate.resolve();
    await flushDesktopPreviewOperations();
    expect(f.resizes).toHaveLength(1);
    expect(f.closes).toEqual([{id:'s1',expectedStreamRevision:'2'}]);
    expect(f.calls).toEqual(['create:p1','attach:s1:1','playback','detach','resize:s1','list','close:s1','create:p1','attach:s2:1','playback']);
    expect(f.creates.at(-1)?.size).toEqual(latest);
    expect(f.playbackStates.at(-1)?.targetFps).toBe(30);
  }finally{
    gate.resolve();controller.dispose();await flushDesktopPreviewOperations();
  }
});
it('does not retry or close a stream explicitly quarantined during resize',async()=>{
  const resizeError=new BackendError(503,{code:'previewUnavailable',message:'quarantined',failureCode:'GpuStreamOpenQuarantined',protocolVersion:'1'});
  const f=fixture({resizeError});const controller=f.make();f.show(controller);
  try{
    await flushDesktopPreviewOperations();
    controller.setSize({width:2560,height:1440});
    await flushDesktopPreviewOperations();
    expect(controller.state$.get().error).toContain('GpuStreamOpenQuarantined');
    controller.retry();
  }finally{
    controller.dispose();await flushDesktopPreviewOperations();
  }
  expect(f.calls).toEqual(['create:p1','attach:s1:1','playback','detach','resize:s1']);
});
it('unmount during create closes its eventual unclaimed stream before the next panel creates',async()=>{
  const gate=deferred();const f=fixture({createGate:gate.promise});const old=f.make();f.show(old);
  await vi.waitFor(()=>expect(f.calls).toEqual(['create:p1']));
  old.dispose();const next=f.make('p2');f.show(next);gate.resolve();
  await vi.waitFor(()=>expect(f.calls).toContain('playback'));
  expect(f.calls).toEqual(['create:p1','close:s1','create:p2','attach:s2:1','playback']);
  next.dispose();await vi.waitFor(()=>expect(f.calls).toContain('close:s2'));
});
it('unmount during replace closes the new generation without attaching it',async()=>{
  const gate=deferred();const f=fixture({replaceGate:gate.promise});const controller=f.make();f.show(controller);
  await vi.waitFor(()=>expect(f.calls).toContain('playback'));f.show(controller,'2');
  await vi.waitFor(()=>expect(f.calls).toContain('replace:s1'));controller.dispose();gate.resolve();
  await vi.waitFor(()=>expect(f.calls).toContain('close:s1'));
  expect(f.calls).toEqual(['create:p1','attach:s1:1','playback','detach','replace:s1','close:s1']);
});
it('retry after a stopped replacement fails closes the old stream and creates a fresh consumer generation',async()=>{
  let fail=true;const f=fixture({replaceFails:()=>fail});const controller=f.make();f.show(controller);
  await vi.waitFor(()=>expect(f.calls).toContain('playback'));f.show(controller,'2');
  await vi.waitFor(()=>expect(controller.state$.get().status).toBe('error'));fail=false;controller.retry();
  await vi.waitFor(()=>expect(f.calls.filter(x=>x==='playback')).toHaveLength(2));
  expect(f.calls).toContain('close:s1');expect(f.calls).toContain('attach:s2:1');
  expect(f.creates.map(input=>input.alphaMode)).toEqual(['opaque','opaque']);
  controller.dispose();await vi.waitFor(()=>expect(f.calls).toContain('close:s2'));
});
it('a retired stream failure cannot block a new controller or a new input generation',async()=>{
  const f=fixture();const old=f.make();f.show(old);await vi.waitFor(()=>expect(f.calls).toContain('playback'));
  old.dispose();const next=f.make('p2');f.show(next);
  f.emit({type:'failed',streamId:'s1',producerEpoch:'1',inputGeneration:'1',code:'retiredFailure'});
  await vi.waitFor(()=>expect(f.calls).toContain('attach:s2:1'));
  expect(next.state$.get().status).not.toBe('error');
  f.emit({type:'failed',streamId:'s2',producerEpoch:'1',inputGeneration:'9',code:'retiredGeneration'});
  expect(next.state$.get().status).not.toBe('error');
  f.emit({type:'failed',streamId:'s2',producerEpoch:'1',inputGeneration:'1',code:'currentFailure'});
  expect(next.state$.get()).toMatchObject({status:'error',error:'currentFailure'});
  next.dispose();await vi.waitFor(()=>expect(f.calls).toContain('close:s2'));
});
it('visibility enable followed by fresh audio sampling sends the new paused sample',async()=>{
  const f=fixture();const controller=f.make();f.show(controller);
  await vi.waitFor(()=>expect(f.samples).toHaveLength(1));
  controller.setEnabled(false);await vi.waitFor(()=>expect(f.calls).toContain('close:s1'));
  controller.setEnabled(true);
  const fresh=performance.now()+1;
  controller.show(revision('1'),{mode:'paused',mediaTimeUs:123456,rate:1,targetFps:24,timelineResetId:'reset',samplePerformanceMs:fresh});
  await vi.waitFor(()=>expect(f.samples).toHaveLength(2));
  expect(f.samples.at(-1)).toBe(fresh);
  expect(f.creates.map(input=>input.alphaMode)).toEqual(['opaque','opaque']);
  controller.dispose();await vi.waitFor(()=>expect(f.calls).toContain('close:s2'));
});
it('refreshes stale paused playback clock sample after a 31s create without error',async()=>{
  let currentNow=1_000;
  vi.spyOn(performance,'now').mockImplementation(()=>currentNow);
  const f=fixture({onCreate:()=>{currentNow+=31_000;}});
  const controller=f.make();
  controller.show(revision('1'),{mode:'paused',mediaTimeUs:420_000,rate:1,targetFps:24,timelineResetId:'reset',samplePerformanceMs:currentNow});
  await vi.waitFor(()=>expect(f.playbackStates).toHaveLength(1));
  expect(f.playbackStates[0]).toMatchObject({mode:'paused',mediaTimeUs:420_000,samplePerformanceMs:32_000});
  expect(controller.state$.get().status).not.toBe('error');
  controller.dispose();await vi.waitFor(()=>expect(f.calls).toContain('close:s1'));
});
it('extrapolates playing mediaTimeUs and refreshes samplePerformanceMs after 31s create and replaceInput',async()=>{
  let currentNow=5_000;
  vi.spyOn(performance,'now').mockImplementation(()=>currentNow);
  const f=fixture({onCreate:()=>{currentNow+=31_000;},onReplace:()=>{currentNow+=31_000;}});
  const controller=f.make();
  controller.show(revision('1'),{mode:'playing',mediaTimeUs:1_000_000,rate:1.5,targetFps:24,timelineResetId:'reset',samplePerformanceMs:currentNow});
  await vi.waitFor(()=>expect(f.playbackStates).toHaveLength(1));
  const afterCreate=f.playbackStates[0]!;
  expect(afterCreate.samplePerformanceMs).toBe(36_000);
  expect(afterCreate.mediaTimeUs).toBe(1_000_000+Math.round(31_000*1000*1.5));
  expect(Number.isSafeInteger(afterCreate.mediaTimeUs)&&afterCreate.mediaTimeUs>=0).toBe(true);
  controller.show(revision('2'),{mode:'playing',mediaTimeUs:2_000_000,rate:1.25,targetFps:24,timelineResetId:'reset',samplePerformanceMs:currentNow});
  await vi.waitFor(()=>expect(f.playbackStates).toHaveLength(2));
  const afterReplace=f.playbackStates[1]!;
  expect(afterReplace.samplePerformanceMs).toBe(67_000);
  expect(afterReplace.mediaTimeUs).toBe(2_000_000+Math.round(31_000*1000*1.25));
  expect(Number.isSafeInteger(afterReplace.mediaTimeUs)&&afterReplace.mediaTimeUs>=0).toBe(true);
  expect(controller.state$.get().status).not.toBe('error');
  controller.dispose();await vi.waitFor(()=>expect(f.calls).toContain('close:s1'));
});
it('creates cpu stream with shm transport and surfaces explicit PreviewTransportUnsupported without fallback',async()=>{
  const f=fixture();const cpuController=f.make('p1','cpu');f.show(cpuController);
  await vi.waitFor(()=>expect(f.calls).toContain('playback'));
  expect(f.creates[0]).toMatchObject({api:'cpu'});
  expect(cpuController.state$.get()).toMatchObject({api:'cpu',transport:'shm'});
  f.emit({type:'drawn',streamId:'s1',producerEpoch:'1',inputGeneration:'1',timelineResetId:'reset',timeUs:500});
  expect(cpuController.state$.get()).toMatchObject({status:'ready',api:'cpu',transport:'shm',frameTimeUs:500});
  cpuController.dispose();await vi.waitFor(()=>expect(f.calls).toContain('close:s1'));

  const blocked=fixture({createError:()=>new BackendError(422,{code:'commandNotSupported',message:'显式 DMA-BUF 不支持 CPU',field:'api',failureCode:'PreviewTransportUnsupported',protocolVersion:'1'})});
  const blockedController=blocked.make('p1','cpu');blocked.show(blockedController);
  await vi.waitFor(()=>expect(blockedController.state$.get().status).toBe('error'));
  expect(blockedController.state$.get().error).toContain('PreviewTransportUnsupported');
  blockedController.dispose();
});

it('reconciles authoritative orphan streams on OutcomeUnknownError and 409/404 close conflicts',async()=>{
  const calls:string[]=[];
  const baseSource={kind:'project' as const,sourceId:'p1',sourceRevision:revision('1')};
  let serverStream:GpuPreviewStreamState|undefined;
  let createCalls=0;
  let replaceCalls=0;
  let nextRev=1;
  let attached=false;
  const client={gpuPreview:{
    list:async()=>{calls.push('list');return {streams:serverStream?[serverStream]:[],protocolVersion:'1'};},
    read:async(id:string)=>{calls.push(`read:${id}`);if(!serverStream)throw new BackendError(404,{code:'sessionNotFound',message:'missing',protocolVersion:'1'});return serverStream;},
    create:async(input:GpuPreviewCreateInput)=>{
      createCalls++;calls.push(`create:${createCalls}`);
      serverStream={streamId:`s${createCalls}`,streamRevision:'1',inputGeneration:'1',registrationState:'ready',source:input.source,size:input.size,alphaMode:input.alphaMode,textureAlphaMode:'premultiplied',api:input.api,transport:'shm',producerEpoch:'1',poolGeneration:'1',slotCount:3,allocationBytes:4096,lastAppliedControlSeq:0};
      if(createCalls===1)throw new OutcomeUnknownError('preview-1',new Error('dropped'));
      return serverStream;
    },
    replaceInput:async(id:string,input:{expectedStreamRevision:string;source:GpuPreviewStreamState['source']})=>{
      replaceCalls++;calls.push(`replace:${id}`);
      // Simulate backend committing revision 2 before returning 500 -> OutcomeUnknownError.
      serverStream={...serverStream!,streamRevision:String(++nextRev),inputGeneration:'2',source:input.source};
      throw new OutcomeUnknownError('preview-2',new Error('timeout'));
    },
    resize:async()=>{throw new Error('unused');},
    close:async(id:string,expectedStreamRevision:string)=>{
      calls.push(`close:${id}:${expectedStreamRevision}`);
      if(!serverStream||serverStream.streamId!==id)throw new BackendError(404,{code:'sessionNotFound',message:'gone',protocolVersion:'1'});
      if(serverStream.streamRevision!==expectedStreamRevision)throw new BackendError(409,{code:'revisionConflict',message:'conflict',protocolVersion:'1'});
      serverStream=undefined;
      return {streamId:id,streamRevision:expectedStreamRevision,inputGeneration:'1',registrationState:'closed',source:baseSource,size:{width:1280,height:720},alphaMode:'opaque',textureAlphaMode:'premultiplied',api:'cpu',transport:'shm',producerEpoch:'1',poolGeneration:'1',slotCount:3,allocationBytes:4096,lastAppliedControlSeq:0};
    },
  }} as unknown as MakoClient;
  const bridge={
    acceptanceEnabled:async()=>false,consumerId:async()=>'a'.repeat(32),
    attachPreview:async(id:string,gen:string)=>{calls.push(`attach:${id}:${gen}`);attached=true;},
    detachPreview:async()=>{calls.push('detach');attached=false;},
    setPlaybackState:async()=>{if(!attached)throw new Error('not attached');calls.push('playback');return {controlSeq:1};},
    onPreviewState:()=>()=>{},
  } as unknown as DesktopPreviewBridge;
  const controller=new GpuPreviewController(client,'p1',{width:1280,height:720},bridge,undefined,'cpu');
  controller.show(revision('1'),{mode:'playing',mediaTimeUs:0,rate:1,targetFps:24,timelineResetId:'r1',samplePerformanceMs:performance.now()});
  await vi.waitFor(()=>expect(calls).toContain('attach:s2:1'));
  expect(calls).toEqual(['create:1','list','close:s1:1','create:2','attach:s2:1','playback']);

  // OutcomeUnknownError during playback replaceInput also reconciles & recovers cleanly.
  controller.show(revision('2'),{mode:'playing',mediaTimeUs:500_000,rate:1,targetFps:24,timelineResetId:'r1',samplePerformanceMs:performance.now()});
  await vi.waitFor(()=>expect(calls).toContain('attach:s3:1'));
  expect(replaceCalls).toBe(1);
  expect(controller.state$.get().status).not.toBe('error');

  // Suspends cleanly before project close and flushes desktop close.
  const resume=await suspendProjectPreviewForClose('p1');
  expect(calls.at(-1)).toBe('close:s3:1');
  expect(serverStream).toBeUndefined();
  resume();
  controller.dispose();
  await vi.waitFor(()=>expect(serverStream).toBeUndefined());
});

it('stops cleanly without looping when authority reports a quarantined stream',async()=>{
  const calls:string[]=[];
  const baseSource={kind:'project' as const,sourceId:'p1',sourceRevision:revision('1')};
  const quarantinedStream:GpuPreviewStreamState={
    streamId:'sq',streamRevision:'1',inputGeneration:'1',registrationState:'failed',source:baseSource,size:{width:1280,height:720},
    alphaMode:'opaque',textureAlphaMode:'premultiplied',api:'cpu',transport:'shm',producerEpoch:'1',poolGeneration:'1',slotCount:3,allocationBytes:4096,lastAppliedControlSeq:null,
    failureCode:'GpuConsumerCompletionUnknown',quarantined:true,
  };
  const client={gpuPreview:{
    list:async()=>{calls.push('list');return {streams:[quarantinedStream],protocolVersion:'1'};},
    read:async()=>{calls.push('read');return quarantinedStream;},
    create:async()=>{calls.push('create');throw new BackendError(429,{code:'sessionQuotaExceeded',message:'GPU 流配额已用完。',protocolVersion:'1'});},
    replaceInput:async()=>{throw new Error('unused');},
    resize:async()=>{throw new Error('unused');},
    close:async()=>{calls.push('close');throw new Error('must not close quarantined stream');},
  }} as unknown as MakoClient;
  const bridge={
    acceptanceEnabled:async()=>false,consumerId:async()=>'a'.repeat(32),
    attachPreview:async()=>{},detachPreview:async()=>{},
    setPlaybackState:async()=>({controlSeq:1}),onPreviewState:()=>()=>{},
  } as unknown as DesktopPreviewBridge;
  const controller=new GpuPreviewController(client,'p1',{width:1280,height:720},bridge,undefined,'cpu');
  controller.show(revision('1'),{mode:'paused',mediaTimeUs:0,rate:1,targetFps:24,timelineResetId:'r1',samplePerformanceMs:performance.now()});
  await vi.waitFor(()=>expect(controller.state$.get().status).toBe('error'));
  expect(controller.state$.get().error).toContain('GpuConsumerCompletionUnknown');
  controller.retry();
  await Promise.resolve();
  expect(calls).toEqual(['create','list']);
  controller.show(revision('2'),{mode:'paused',mediaTimeUs:0,rate:1,targetFps:24,timelineResetId:'r1',samplePerformanceMs:performance.now()});
  await flushDesktopPreviewOperations();
  expect(calls).toEqual(['create','list']);
  controller.dispose();
});

it('auto-recovers from a recoverable runtime failure when project revision changes and formats known codes',async()=>{
  const f=fixture();const controller=f.make('p1','cpu');f.show(controller,'1');
  try{
    await flushDesktopPreviewOperations();
    f.emit({type:'failed',streamId:'s1',producerEpoch:'1',inputGeneration:'1',code:'ImageEffectPartialFusion'});
    expect(controller.state$.get().status).toBe('error');
    expect(controller.state$.get().error).toContain('效果叠加范围冲突');
    expect(controller.state$.get().error).toContain('ImageEffectPartialFusion');

    // Same revision stays blocked to avoid crash loops.
    f.show(controller,'1');
    await flushDesktopPreviewOperations();
    expect(f.creates).toHaveLength(1);

    // Editing the project to a new revision reconciles the failed stream and recovers automatically.
    f.show(controller,'2');
    await flushDesktopPreviewOperations();
    expect(f.calls).toContain('list');
    expect(f.calls).toContain('close:s1');
    expect(f.creates).toHaveLength(2);
    expect(f.calls).toContain('attach:s2:1');
    expect(controller.state$.get().status).not.toBe('error');
  }finally{
    controller.dispose();await flushDesktopPreviewOperations();
  }
});
