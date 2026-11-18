'use strict';
const { contextBridge, ipcRenderer, sharedTexture } = require('electron');
let config;
let detached = false;
let attachment;
let barrier;
let desiredPlayback;
let waitingReset = false;
let hardBarrier = {seq:0};
let activeShmPort = null;
let diagnosticSequence=0;const diagnosticEvents=[];
function diagnostic(type,startMs,endMs,detail={}) {
  if(!config?.performanceDiagnostics)return;
  const map=ms=>calibratedClock?(BigInt(Math.round(ms*1e6))+calibratedClock.offsetNs).toString():null;
  diagnosticEvents.push({sequence:++diagnosticSequence,type,startPerformanceMs:startMs,endPerformanceMs:endMs,startMonotonicNs:map(startMs),endMonotonicNs:map(endMs),mappingUncertaintyNs:calibratedClock?.uncertaintyNs.toString(),durationMs:endMs-startMs,...detail});
  if(diagnosticEvents.length>512)diagnosticEvents.shift();
}
const drops={count:0,lastReason:null,lastFrame:null};
const barrierWaiters=new Set();
const attachmentKeys=['streamId','hostInstanceId','producerEpoch','inputGeneration','consumerEpoch','poolGeneration'];
const notifyBarrierWaiters=()=>{for(const wake of barrierWaiters)wake();};
const refreshBarrier = () => {
  waitingReset = !attachment || !barrier || !desiredPlayback || hardBarrier.seq === null ||
    barrier.timelineResetId !== desiredPlayback.timelineResetId || (barrier.controlSeq??barrier.minControlSeq) < hardBarrier.seq;
  notifyBarrierWaiters();
};
// A paused producer emits one final target. Its texture/SHM callback can arrive before
// the invoke response or applied IPC event, so retain that bounded existing lease
// until both gates are known. No extra control, frame production or CPU copy.
function waitForPausedBarrier(metadata) {
  const expectedAttachment=attachment,expectedBarrier=hardBarrier;
  const eligible=()=>!detached&&!!attachment&&attachment===expectedAttachment&&hardBarrier===expectedBarrier&&
    desiredPlayback?.mode==='paused'&&metadata.timelineResetId===desiredPlayback.timelineResetId&&
    attachmentKeys.every(key=>metadata[key]===attachment?.[key])&&
    (hardBarrier.seq===null||metadata.controlSeq>=hardBarrier.seq);
  if(!waitingReset||!eligible())return Promise.resolve(false);
  return new Promise(resolve=>{
    let settled=false;
    const finish=expired=>{if(settled)return;settled=true;clearTimeout(timer);barrierWaiters.delete(wake);resolve(expired);};
    const wake=()=>{if(!waitingReset||!eligible())finish(false);};
    const timer=setTimeout(()=>finish(true),1000);
    barrierWaiters.add(wake);wake();
  });
}
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
// The attach response and the attached event travel separately and the event can
// arrive after the page already sent its first control. Whichever arrives first
// applies; attachSeq rejects the later duplicate and detach events of replaced
// attachments, so a late reset cannot wipe the paused intent of the new stream.
let appliedAttachSeq=0;
function applyAttached(value) {
  if(Number.isSafeInteger(value?.attachSeq)){if(value.attachSeq<=appliedAttachSeq)return;appliedAttachSeq=value.attachSeq;}
  detached=false;attachment=value;barrier=undefined;desiredPlayback=undefined;waitingReset=true;notifyBarrierWaiters();
}
function closeActiveShmPort() {
  const port = activeShmPort;
  activeShmPort = null;
  if (port) {
    try { port.onmessage = null; } catch {}
    try { port.close?.(); } catch {}
  }
}
ipcRenderer.on('mako-gpu:attached', (_event,value) => applyAttached(value));
ipcRenderer.on('mako-gpu:detach', (_event,value) => {
  if(Number.isSafeInteger(value?.attachSeq)&&value.attachSeq<appliedAttachSeq)return;
  closeActiveShmPort();
  detached=true;attachment=undefined;barrier=undefined;waitingReset=true;notifyBarrierWaiters();
});
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const nextPaint = () => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));

// WeakMap<HTMLCanvasElement, GlContextState> keeps WebGL2 objects scoped to each DOM canvas node.
const glStates = new WeakMap();
const VERTEX_SHADER_SOURCE = `#version 300 es
void main() {
  float x = float((gl_VertexID & 1) << 2) - 1.0;
  float y = float((gl_VertexID & 2) << 1) - 1.0;
  gl_Position = vec4(x, y, 0.0, 1.0);
}
`;
const FRAGMENT_SHADER_SOURCE = `#version 300 es
precision highp float;
precision highp int;
uniform highp sampler2D uTex;
uniform int uHeight;
uniform int uSwizzleBgra;
out vec4 outColor;
void main() {
  ivec2 coord = ivec2(int(gl_FragCoord.x), uHeight - 1 - int(gl_FragCoord.y));
  vec4 texel = texelFetch(uTex, coord, 0);
  outColor = (uSwizzleBgra != 0) ? texel.bgra : texel;
}
`;

function compileShader(gl, type, source) {
  const shader = gl.createShader(type);
  if (!shader) throw new Error('WebGL2 createShader failed');
  gl.shaderSource(shader, source);
  gl.compileShader(shader);
  if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
    const info = gl.getShaderInfoLog(shader) || 'unknown shader error';
    gl.deleteShader(shader);
    throw new Error(`WebGL2 shader compile failed: ${info}`);
  }
  return shader;
}

function ensureWebGl2State(canvas, mode) {
  let state = glStates.get(canvas);
  if (state) {
    if (state.lost) throw new Error('WebGL2 context is lost');
    return state;
  }
  const gl = canvas.getContext('webgl2', {
    alpha: true,
    premultipliedAlpha: true,
    antialias: false,
    depth: false,
    stencil: false,
    preserveDrawingBuffer: mode === 'correctness',
  });
  if (!gl) throw new Error('WebGL2 context unavailable on preview canvas');
  if ('drawingBufferColorSpace' in gl) {
    gl.drawingBufferColorSpace = 'srgb';
  }
  const vs = compileShader(gl, gl.VERTEX_SHADER, VERTEX_SHADER_SOURCE);
  const fs = compileShader(gl, gl.FRAGMENT_SHADER, FRAGMENT_SHADER_SOURCE);
  const program = gl.createProgram();
  if (!program) {
    gl.deleteShader(vs);
    gl.deleteShader(fs);
    throw new Error('WebGL2 createProgram failed');
  }
  gl.attachShader(program, vs);
  gl.attachShader(program, fs);
  gl.linkProgram(program);
  gl.deleteShader(vs);
  gl.deleteShader(fs);
  if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
    const info = gl.getProgramInfoLog(program) || 'unknown link error';
    gl.deleteProgram(program);
    throw new Error(`WebGL2 program link failed: ${info}`);
  }
  const vao = gl.createVertexArray();
  if (!vao) {
    gl.deleteProgram(program);
    throw new Error('WebGL2 createVertexArray failed');
  }
  gl.disable(gl.BLEND);
  gl.disable(gl.DEPTH_TEST);
  gl.disable(gl.SCISSOR_TEST);
  gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false);
  gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
  gl.pixelStorei(gl.UNPACK_COLORSPACE_CONVERSION_WEBGL, gl.NONE);
  gl.pixelStorei(gl.UNPACK_ALIGNMENT, 4);

  state = {
    gl,
    program,
    vao,
    uTexLoc: gl.getUniformLocation(program, 'uTex'),
    uHeightLoc: gl.getUniformLocation(program, 'uHeight'),
    uSwizzleBgraLoc: gl.getUniformLocation(program, 'uSwizzleBgra'),
    texture: null,
    texWidth: 0,
    texHeight: 0,
    textureRecreations: 0,
    lost: false,
  };
  canvas.addEventListener?.('webglcontextlost', (event) => {
    event?.preventDefault?.();
    state.lost = true;
    ipcRenderer.send('mako-gpu:error', 'WebGL2 context lost');
  });
  glStates.set(canvas, state);
  return state;
}

async function consumeShmFrame(port, metadata, incomingBuffer) {
  let pixelBuffer = incomingBuffer;
  incomingBuffer = null;
  let ackType = 'released';
  const diagnosticStart = config?.performanceDiagnostics ? performance.now() : null;
  let diagnosticDrawStart, diagnosticDrawEnd;
  try {
    if (!metadata || typeof metadata !== 'object' || !(pixelBuffer instanceof Uint8Array)) {
      throw new Error('Invalid SHM frame payload');
    }
    const barrierExpired = config?.mode === 'production' && desiredPlayback?.mode === 'paused' && waitingReset
      ? await waitForPausedBarrier(metadata)
      : false;
    const canvas = document.getElementById(config?.mode === 'production' ? 'mako-gpu-preview' : 'preview');
    const current = config?.mode !== 'production' || (!barrierExpired && attachment && barrier && !waitingReset &&
      attachmentKeys.every(key => metadata[key] === attachment[key]) &&
      metadata.timelineEpoch === barrier.timelineEpoch &&
      metadata.timelineResetId === desiredPlayback?.timelineResetId &&
      metadata.controlSeq >= Math.max(barrier.minControlSeq, hardBarrier.seq ?? Infinity));
    if (detached || !canvas || !current) {
      pixelBuffer = null;
      drops.count = Math.min(Number.MAX_SAFE_INTEGER, drops.count + 1);
      drops.lastReason = detached ? 'detached' : !canvas ? 'canvasUnavailable' : barrierExpired ? 'waitingBarrierTimeout' : waitingReset ? 'waitingBarrier' : !attachment || !barrier ? 'missingAttachment' : 'staleIdentityOrControl';
      drops.lastFrame = { ...lease(metadata), controlSeq: metadata.controlSeq, timelineResetId: metadata.timelineResetId };
    } else {
      if (metadata.pixelFormat !== 'bgra8888' && metadata.pixelFormat !== 'rgba8888') {
        throw new Error(`Unsupported SHM pixelFormat: ${metadata.pixelFormat}`);
      }
      if (!Number.isSafeInteger(metadata.strideBytes) || metadata.strideBytes < metadata.width * 4 || metadata.strideBytes % 4 !== 0) {
        throw new Error(`Invalid SHM strideBytes: ${metadata.strideBytes}`);
      }
      const exactBytes = metadata.strideBytes * metadata.height;
      if (pixelBuffer.byteLength !== exactBytes) {
        throw new Error(`SHM pixelBuffer byteLength ${pixelBuffer.byteLength} !== exact ${exactBytes}`);
      }

      const state = ensureWebGl2State(canvas, config?.mode);
      const gl = state.gl;
      if (diagnosticStart !== null) diagnosticDrawStart = performance.now();

      // Immutable texStorage2D textures cannot be resized in place: deleteTexture before recreating!
      if (!state.texture || state.texWidth !== metadata.width || state.texHeight !== metadata.height) {
        if (state.texture) {
          gl.deleteTexture(state.texture);
          state.texture = null;
        }
        const tex = gl.createTexture();
        if (!tex) throw new Error('WebGL2 createTexture failed');
        gl.activeTexture(gl.TEXTURE0);
        gl.bindTexture(gl.TEXTURE_2D, tex);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        gl.texStorage2D(gl.TEXTURE_2D, 1, gl.RGBA8, metadata.width, metadata.height);
        state.texture = tex;
        state.texWidth = metadata.width;
        state.texHeight = metadata.height;
        state.textureRecreations++;
      }

      if (canvas.width !== metadata.width) canvas.width = metadata.width;
      if (canvas.height !== metadata.height) canvas.height = metadata.height;
      gl.viewport(0, 0, metadata.width, metadata.height);

      const rowLength = metadata.strideBytes === metadata.width * 4 ? 0 : (metadata.strideBytes / 4);
      gl.pixelStorei(gl.UNPACK_ROW_LENGTH, rowLength);
      gl.activeTexture(gl.TEXTURE0);
      gl.bindTexture(gl.TEXTURE_2D, state.texture);
      gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, metadata.width, metadata.height, gl.RGBA, gl.UNSIGNED_BYTE, pixelBuffer);
      // Immediately drop the PartitionAlloc backing store reference right after GPU upload!
      pixelBuffer = null;

      gl.useProgram(state.program);
      gl.uniform1i(state.uTexLoc, 0);
      gl.uniform1i(state.uHeightLoc, metadata.height);
      gl.uniform1i(state.uSwizzleBgraLoc, metadata.pixelFormat === 'bgra8888' ? 1 : 0);
      gl.bindVertexArray(state.vao);
      gl.drawArrays(gl.TRIANGLES, 0, 3);
      if (diagnosticStart !== null) diagnosticDrawEnd = performance.now();

      const glErr = gl.getError();
      if (glErr !== gl.NO_ERROR) {
        throw new Error(`WebGL2 error after SHM draw: 0x${glErr.toString(16)}`);
      }

      ackType = 'drawn';
      const label = document.getElementById('state');
      if (label && config?.mode !== 'production') label.textContent = ` frame ${metadata.frameSeq} / slot ${metadata.slotId}`;
      const observation = {
        ...lease(metadata),
        transport: 'shm',
        api: metadata.api,
        pixelFormat: metadata.pixelFormat,
        strideBytes: metadata.strideBytes,
        controlSeq: metadata.controlSeq,
        timelineResetId: metadata.timelineResetId,
        timeUs: metadata.timeUs,
        sourceRevision: metadata.sourceRevision,
        diagnostics: metadata.diagnostics,
        timings: metadata.timings,
        timestamp: metadata.timeUs,
        width: metadata.width,
        height: metadata.height,
        textureRecreations: state.textureRecreations,
      };

      if (config?.mode === 'correctness') {
        const scratch = new Uint8Array(4);
        const sampleTopDown = (nx, ny) => {
          const px = Math.floor(nx * metadata.width);
          const py = Math.floor(ny * metadata.height);
          gl.readPixels(px, metadata.height - 1 - py, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, scratch);
          return [scratch[0], scratch[1], scratch[2], scratch[3]];
        };
        const checks = [
          { name: 'top-left-red', x: 0.05, y: 0.15, expected: [255, 0, 0, 255] },
          { name: 'top-right-green', x: 0.95, y: 0.15, expected: [0, 255, 0, 255] },
          { name: 'bottom-left-blue', x: 0.05, y: 0.95, expected: [0, 0, 255, 255] },
          { name: 'bottom-right-white', x: 0.95, y: 0.95, expected: [255, 255, 255, 255] },
          { name: 'premultiplied-alpha', x: 0.375, y: 0.8, expected: [128, 0, 128, 128] },
          { name: 'transparent', x: 0.625, y: 0.8, expected: [0, 0, 0, 0] },
          ...Array.from({ length: 8 }, (_, i) => {
            const g = Math.floor((i * 255 + 3) / 7);
            return { name: `gray-${i}`, x: (i + 0.5) / 8, y: 0.5, expected: [g, g, g, 255] };
          }),
        ];
        observation.samples = checks.map((check) => ({ ...check, rgba: sampleTopDown(check.x, check.y) }));
        observation.pixelChecksPassed = observation.samples.every((check) =>
          check.rgba.every((v, idx) => Math.abs(v - check.expected[idx]) <= 1));
        observation.decodedSequence = Array.from({ length: 16 }, (_, i) =>
          sampleTopDown((i + 0.5) / 16, 0.02)[0] > 127 ? 2 ** i : 0).reduce((a, b) => a + b, 0);
        const expectedSeq16 = metadata.frameSeq & 0xffff;
        if (!observation.pixelChecksPassed || observation.decodedSequence !== expectedSeq16) {
          throw new Error(`SHM WebGL2 correctness mismatch (<=1 LSB required): ${JSON.stringify(observation)}`);
        }
      }

      ipcRenderer.send('mako-gpu:drawn', observation);
      if (config?.mode === 'correctness' && config?.scenario !== 'hidden') await nextPaint();
      if (config?.scenario === 'slow') await delay(config.slowMs);
    }
  } catch (error) {
    pixelBuffer = null;
    ipcRenderer.send('mako-gpu:error', String(error.stack || error));
  } finally {
    pixelBuffer = null;
    if (metadata && typeof metadata === 'object') {
      try { port.postMessage({ type: ackType, ...lease(metadata) }); } catch {}
      ipcRenderer.send('mako-gpu:renderer-release', lease(metadata));
      if (diagnosticStart !== null) {
        const end = performance.now();
        if (end - diagnosticStart >= 5) {
          diagnostic('renderer-shm-frame', diagnosticStart, end, {
            lease: lease(metadata),
            drawMs: diagnosticDrawEnd !== undefined && diagnosticDrawStart !== undefined ? diagnosticDrawEnd - diagnosticDrawStart : null,
          });
        }
      }
    }
  }
}

ipcRenderer.on('mako-gpu:shm-port', (event, info) => {
  try {
    closeActiveShmPort();
    const port = event?.ports?.[0];
    if (!port) throw new Error('Missing MessagePort in mako-gpu:shm-port');
    activeShmPort = port;
    if (info && typeof info === 'object' && info.streamId) {
      applyAttached(info);
    }
    port.onmessage = (msgEvent) => {
      const data = msgEvent?.data;
      if (!data || data.type !== 'frame') return;
      const metadata = data.metadata;
      const buffer = data.buffer;
      void consumeShmFrame(port, metadata, buffer);
    };
    port.start?.();
  } catch (error) {
    ipcRenderer.send('mako-gpu:error', String(error.stack || error));
  }
});

contextBridge.exposeInMainWorld('makoDesktopPreview', Object.freeze({
  consumerId: () => ipcRenderer.invoke('mako-gpu:consumer-id'),
  acceptanceEnabled: () => ipcRenderer.invoke('mako-gpu:acceptance-enabled'),
  injectAcceptanceFailure: kind => ipcRenderer.invoke('mako-gpu:acceptance-failure',kind),
  setAcceptanceVisibility: visible => ipcRenderer.invoke('mako-gpu:acceptance-visibility',visible),
  attachPreview: async (streamId,expectedInputGeneration) => {
    const value=await ipcRenderer.invoke('mako-gpu:attach', streamId,expectedInputGeneration);
    applyAttached(value);
    return value;
  },
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
  readPreviewState: async(sinceRevision=0) => ({...await ipcRenderer.invoke('mako-gpu:read',sinceRevision),...(config?.performanceDiagnostics?{rendererPerformanceDiagnostics:{enabled:true,sequence:diagnosticSequence,capacity:512,events:diagnosticEvents.slice()}}:{}),rendererGate:{...drops,detached,waitingReset,hardControlSeq:hardBarrier.seq,attachment,barrier}}),
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
    if(config.performanceDiagnostics){let previous=performance.now();setInterval(()=>{const end=performance.now(),start=previous+20;if(end-start>=20)diagnostic('renderer-event-loop-lag',start,end);previous=end;},20);}
    if (config.transport !== 'shm' && typeof sharedTexture?.setSharedTextureReceiver !== 'function') {
      throw new Error('sandbox sharedTexture receiver unavailable');
    }
    if (config.mode !== 'production') {
      const initialCanvas = document.getElementById('preview');
      if (initialCanvas) {
        initialCanvas.width = config.width;
        initialCanvas.height = config.height;
      }
    }
    if (typeof sharedTexture?.setSharedTextureReceiver === 'function') {
      sharedTexture.setSharedTextureReceiver(async ({ importedSharedTexture }, metadata) => {
        let frame;
        const diagnosticStart=config.performanceDiagnostics?performance.now():null;
        let diagnosticGetVideoEnd,diagnosticDrawStart,diagnosticDrawEnd,diagnosticFrameClosed;
        try {
          const barrierExpired=desiredPlayback?.mode==='paused'&&waitingReset?await waitForPausedBarrier(metadata):false;
          frame = importedSharedTexture.getVideoFrame();
          if(diagnosticStart!==null)diagnosticGetVideoEnd=performance.now();
          const canvas = document.getElementById(config.mode === 'production' ? 'mako-gpu-preview' : 'preview');
          const current = config.mode !== 'production' || (!barrierExpired && attachment && barrier && !waitingReset &&
            attachmentKeys.every(key=>metadata[key]===attachment[key]) &&
            metadata.timelineEpoch===barrier.timelineEpoch && metadata.timelineResetId===desiredPlayback?.timelineResetId && metadata.controlSeq>=Math.max(barrier.minControlSeq,hardBarrier.seq??Infinity));
          if(detached||!canvas||!current){
            drops.count=Math.min(Number.MAX_SAFE_INTEGER,drops.count+1);
            drops.lastReason=detached?'detached':!canvas?'canvasUnavailable':barrierExpired?'waitingBarrierTimeout':waitingReset?'waitingBarrier':!attachment||!barrier?'missingAttachment':'staleIdentityOrControl';
            drops.lastFrame={...lease(metadata),controlSeq:metadata.controlSeq,timelineResetId:metadata.timelineResetId};
          }
          if (!detached && canvas && current) {
            if (canvas.width !== metadata.width) canvas.width = metadata.width;
            if (canvas.height !== metadata.height) canvas.height = metadata.height;
            const context = canvas.getContext('2d', { alpha:true });
            if (!context) throw new Error('Canvas 2D context unavailable');
            if(diagnosticStart!==null)diagnosticDrawStart=performance.now();
            context.clearRect(0, 0, canvas.width, canvas.height);
            context.drawImage(frame, 0, 0);
            if(diagnosticStart!==null)diagnosticDrawEnd=performance.now();
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
          if(diagnosticStart!==null)diagnosticFrameClosed=performance.now();
          try { importedSharedTexture.release(); } catch { ipcRenderer.send('mako-gpu:error','Shared texture release failed'); }
          ipcRenderer.send('mako-gpu:renderer-release', lease(metadata));
          if(diagnosticStart!==null){const end=performance.now(),transferToReceiveMs=calibratedClock&&metadata.diagnosticTransferStartNs?Number(BigInt(Math.round(diagnosticStart*1e6))+calibratedClock.offsetNs-BigInt(metadata.diagnosticTransferStartNs))/1e6:null;
            if(end-diagnosticStart>=5||transferToReceiveMs>=10)diagnostic('renderer-frame',diagnosticStart,end,{lease:lease(metadata),getVideoFrameMs:diagnosticGetVideoEnd-diagnosticStart,drawMs:diagnosticDrawEnd-diagnosticDrawStart,frameCloseMs:diagnosticFrameClosed-(diagnosticDrawEnd??diagnosticGetVideoEnd),releaseMs:end-diagnosticFrameClosed,transferToReceiveMs,brokerReceivedMonotonicNs:metadata.diagnosticBrokerReceivedNs,transferStartMonotonicNs:metadata.diagnosticTransferStartNs});
          }
        }
      });
    }
    ipcRenderer.send('mako-gpu:ready', { sandboxed: process.sandboxed, contextIsolated: process.contextIsolated, versions: process.versions });
  } catch (error) { ipcRenderer.send('mako-gpu:error', String(error.stack || error)); }
});
