'use strict';
const { contextBridge, ipcRenderer, sharedTexture } = require('electron');
let config;
let detached = false;
let attachment;
let barrier;
let desiredPlayback;
let waitingReset = false;
let hardBarrier = {seq:0};
const refreshBarrier = () => {
  waitingReset = !attachment || !barrier || !desiredPlayback || hardBarrier.seq === null ||
    barrier.timelineResetId !== desiredPlayback.timelineResetId || barrier.minControlSeq < hardBarrier.seq;
};
const identityKeys = ['streamId','hostInstanceId','producerEpoch','inputGeneration','consumerEpoch','timelineEpoch','poolGeneration','slotId','slotEpoch','frameSeq'];
const lease = metadata => Object.fromEntries(identityKeys.map(key=>[key,metadata[key]]));
let calibratedClock;
let calibrating;
async function rendererClock() {
  if(calibratedClock&&performance.now()-calibratedClock.at<30000)return calibratedClock;
  if(calibrating)return calibrating;
  calibrating=(async()=>{
    let best;
    for(let attempt=0;attempt<5;attempt++){
      const before=performance.now();
      const result=await ipcRenderer.invoke('mako-gpu:clock');
      const after=performance.now(),rtt=after-before;
      if(!Number.isFinite(rtt)||rtt<0||rtt>1000||typeof result?.mainMonotonicNs!=='string'||!/^\d+$/.test(result.mainMonotonicNs))continue;
      const current={offsetNs:BigInt(result.mainMonotonicNs)-BigInt(Math.round((before+after)*500000)),uncertaintyNs:BigInt(Math.ceil(rtt*500000)+1000),at:after};
      if(!best||current.uncertaintyNs<best.uncertaintyNs)best=current;
    }
    if(!best)throw new Error('Renderer clock calibration unavailable');
    calibratedClock=best;return best;
  })();
  try{return await calibrating;}finally{calibrating=undefined;}
}
ipcRenderer.on('mako-gpu:barrier', (_event,value) => {
  barrier = value;
  refreshBarrier();
});
ipcRenderer.on('mako-gpu:attached', (_event,value) => { detached=false;attachment=value;barrier=undefined;desiredPlayback=undefined;waitingReset=true; });
ipcRenderer.on('mako-gpu:detach', () => { detached=true;attachment=undefined;barrier=undefined;waitingReset=true; });
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const nextPaint = () => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));

contextBridge.exposeInMainWorld('makoDesktopPreview', Object.freeze({
  consumerId: () => ipcRenderer.invoke('mako-gpu:consumer-id'),
  acceptanceEnabled: () => ipcRenderer.invoke('mako-gpu:acceptance-enabled'),
  injectAcceptanceFailure: kind => ipcRenderer.invoke('mako-gpu:acceptance-failure',kind),
  attachPreview: (streamId,expectedInputGeneration) => ipcRenderer.invoke('mako-gpu:attach', streamId,expectedInputGeneration),
  setPlaybackState: async state => {
    const changed = !desiredPlayback || desiredPlayback.timelineResetId!==state.timelineResetId || desiredPlayback.mode!==state.mode || desiredPlayback.rate!==state.rate || desiredPlayback.targetFps!==state.targetFps;
    if(changed)hardBarrier={seq:null};
    const requestedBarrier=hardBarrier;
    const desired={ ...state };
    desiredPlayback = desired;
    refreshBarrier();
    const clock=await rendererClock();
    if(!Number.isFinite(state.samplePerformanceMs)||state.samplePerformanceMs<0||state.samplePerformanceMs>performance.now()+5||performance.now()-state.samplePerformanceMs>30000)throw new Error('Invalid audio clock sample');
    const sampleNs=BigInt(Math.round(state.samplePerformanceMs*1e6))+clock.offsetNs;
    return ipcRenderer.invoke('mako-gpu:playback', state,{senderMonotonicNs:sampleNs.toString(),uncertaintyNs:clock.uncertaintyNs.toString()}).then(result=>{
      if(hardBarrier===requestedBarrier && desiredPlayback){
        if(changed)requestedBarrier.seq=result.controlSeq;
        refreshBarrier();
      }
      return result;
    });
  },
  readPreviewState: (sinceRevision=0) => ipcRenderer.invoke('mako-gpu:read',sinceRevision),
  detachPreview: () => ipcRenderer.invoke('mako-gpu:detach'),
  onPreviewState: listener => {
    if (typeof listener !== 'function') throw new TypeError('listener required');
    const handler = (_event,state) => listener(state);
    ipcRenderer.on('mako-gpu:state',handler);
    return () => ipcRenderer.removeListener('mako-gpu:state',handler);
  },
}));

window.addEventListener('DOMContentLoaded', async () => {
  try {
    config = await ipcRenderer.invoke('mako-gpu:config');
    if (typeof sharedTexture?.setSharedTextureReceiver !== 'function') throw new Error('sandbox sharedTexture receiver unavailable');
    if (config.mode !== 'production') {
      const initialCanvas = document.getElementById('preview');
      initialCanvas.width = config.width;
      initialCanvas.height = config.height;
    }
    sharedTexture.setSharedTextureReceiver(async ({ importedSharedTexture }, metadata) => {
      let frame;
      try {
        frame = importedSharedTexture.getVideoFrame();
        const canvas = document.getElementById(config.mode === 'production' ? 'mako-gpu-preview' : 'preview');
        const current = config.mode !== 'production' || (attachment && barrier && !waitingReset &&
          ['streamId','hostInstanceId','producerEpoch','inputGeneration','consumerEpoch','poolGeneration'].every(key=>metadata[key]===attachment[key]) &&
          metadata.timelineEpoch===barrier.timelineEpoch && metadata.timelineResetId===desiredPlayback?.timelineResetId && metadata.controlSeq>=barrier.minControlSeq);
        if (!detached && canvas && current) {
          if (canvas.width !== metadata.width) canvas.width = metadata.width;
          if (canvas.height !== metadata.height) canvas.height = metadata.height;
          const context = canvas.getContext('2d', { alpha:true });
          if (!context) throw new Error('Canvas 2D context unavailable');
          context.clearRect(0, 0, canvas.width, canvas.height);
          context.drawImage(frame, 0, 0);
          const label = document.getElementById('state');
          if (label && config.mode !== 'production') label.textContent = ` frame ${metadata.frameSeq} / slot ${metadata.slotId}`;
          const observation = { ...lease(metadata), controlSeq:metadata.controlSeq, timelineResetId:metadata.timelineResetId, timeUs:metadata.timeUs, sourceRevision:metadata.sourceRevision, diagnostics:metadata.diagnostics, timestamp: frame.timestamp, width: frame.displayWidth, height: frame.displayHeight };
          // Readback is strictly limited to the separate correctness run.
          if (config.mode === 'correctness') {
            const sample = (x, y) => [...context.getImageData(Math.floor(x*canvas.width), Math.floor(y*canvas.height), 1, 1).data];
            const checks = [
              { name: 'top-left-red', x:.05, y:.15, expected:[255,0,0,255] },
              { name: 'top-right-green', x:.95, y:.15, expected:[0,255,0,255] },
              { name: 'bottom-left-blue', x:.05, y:.95, expected:[0,0,255,255] },
              { name: 'bottom-right-white', x:.95, y:.95, expected:[255,255,255,255] },
              { name: 'premultiplied-alpha', x:.375, y:.8, expected:[255,0,255,128] },
              { name: 'transparent', x:.625, y:.8, expected:[0,0,0,0] },
              ...Array.from({length:8}, (_, i) => ({ name:`gray-${i}`, x:(i+.5)/8, y:.5, expected:[Math.round(i/7*255),Math.round(i/7*255),Math.round(i/7*255),255] })),
            ];
            observation.samples = checks.map(check => ({ ...check, rgba:sample(check.x,check.y) }));
            observation.pixelChecksPassed = observation.samples.every(check => check.rgba.every((v,i) => Math.abs(v-check.expected[i]) <= 2));
            observation.decodedSequence = Array.from({length:16},(_,i)=>sample((i+.5)/16,.02)[0]>127 ? 2**i:0).reduce((a,b)=>a+b,0);
            if (!observation.pixelChecksPassed || observation.decodedSequence !== metadata.frameSeq || frame.timestamp !== metadata.frameSeq) throw new Error(`Correctness mismatch: ${JSON.stringify(observation)}`);
          }
          ipcRenderer.send('mako-gpu:drawn', observation);
          // Hidden documents do not advance rAF. Their draw still consumes VideoFrame.
          if (config.mode === 'correctness' && config.scenario !== 'hidden') await nextPaint();
          if (config.scenario === 'slow') await delay(config.slowMs);
        }
      } catch (error) {
        ipcRenderer.send('mako-gpu:error', String(error.stack || error));
      } finally {
        try { frame?.close(); } catch { ipcRenderer.send('mako-gpu:error','VideoFrame close failed'); }
        try { importedSharedTexture.release(); } catch { ipcRenderer.send('mako-gpu:error','Shared texture release failed'); }
        ipcRenderer.send('mako-gpu:renderer-release', lease(metadata));
      }
    });
    ipcRenderer.send('mako-gpu:ready', { sandboxed: process.sandboxed, contextIsolated: process.contextIsolated, versions: process.versions });
  } catch (error) { ipcRenderer.send('mako-gpu:error', String(error.stack || error)); }
});
