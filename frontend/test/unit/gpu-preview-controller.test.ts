import {afterEach,beforeEach,expect,it,vi} from 'vitest';
import type {MakoClient} from '../../src/client';
import {revision,type DesktopPreviewBridge,type GpuPreviewCreateInput,type GpuPreviewStreamState} from '../../src/protocol';
import {GpuPreviewController} from '../../src/editor/preview/gpu-preview-controller';

const deferred=()=>{let resolve!:()=>void;const promise=new Promise<void>(done=>{resolve=done;});return {promise,resolve};};
beforeEach(()=>vi.stubGlobal('window',{}));
afterEach(()=>vi.unstubAllGlobals());
function fixture(options:{createGate?:Promise<void>;replaceGate?:Promise<void>;replaceFails?:()=>boolean}={}){
  const calls:string[]=[];let nextId=0;let attached=false;
  const state=(id:string,source:GpuPreviewStreamState['source'],generation='1'):GpuPreviewStreamState=>({streamId:id,streamRevision:generation,inputGeneration:generation,registrationState:'ready',source,size:{width:1280,height:720},alphaMode:'straight',textureAlphaMode:'premultiplied',api:'vulkan',producerEpoch:generation,poolGeneration:'1',slotCount:3,allocationBytes:4096,lastAppliedControlSeq:0});
  const client={gpuPreview:{
    create:async(input:GpuPreviewCreateInput)=>{calls.push(`create:${input.source.sourceId}`);await options.createGate;return state(`s${++nextId}`,input.source);},
    replaceInput:async(id:string,input:{source:GpuPreviewStreamState['source']})=>{calls.push(`replace:${id}`);await options.replaceGate;if(options.replaceFails?.())throw new Error('replace failed');return state(id,input.source,'2');},
    close:async(id:string)=>{calls.push(`close:${id}`);},
  }} as unknown as MakoClient;
  const bridge={
    acceptanceEnabled:async()=>false,consumerId:async()=>'a'.repeat(32),
    attachPreview:async(id:string,generation:string)=>{calls.push(`attach:${id}:${generation}`);attached=true;},
    detachPreview:async()=>{calls.push('detach');attached=false;},
    setPlaybackState:async()=>{if(!attached)throw new Error('not attached');calls.push('playback');return {controlSeq:1};},
    onPreviewState:()=>()=>{},
  } as unknown as DesktopPreviewBridge;
  const make=(id='p1')=>new GpuPreviewController(client,id,{width:1280,height:720},bridge);
  const show=(controller:GpuPreviewController,rev='1')=>controller.show(revision(rev),{mode:'paused',mediaTimeUs:0,rate:1,targetFps:24,timelineResetId:'reset',samplePerformanceMs:performance.now()});
  return {calls,make,show};
}
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
  controller.dispose();await vi.waitFor(()=>expect(f.calls).toContain('close:s2'));
});
