'use strict';
const { app, BrowserWindow, Menu, MessageChannelMain, dialog, ipcMain, net, screen, sharedTexture } = require('electron');
const fs = require('node:fs');
const path = require('node:path');
const { createHash, randomBytes } = require('node:crypto');
const { readLaunchConfig } = require('./gpu-preview/launch-contract.cjs');
const { importAndTransferFrame } = require('./gpu-preview/import-consumer.cjs');
const { connectLocalStream } = require('./gpu-preview/local-stream-client.cjs');
const { createShmStreamConsumer } = require('./gpu-preview/shm-consumer.cjs');
const { isolateGpuPreviewStream } = require('./gpu-preview/failure-isolation.cjs');
const { validateWorkerMessage, workerFailureSummary } = require('./gpu-preview/wire.cjs');
const { prepareDesktopSession } = require('./gpu-preview/session-paths.cjs');
const { prepareStandaloneSession } = require('./runtime/standalone-paths.cjs');
const privateFs = require('./runtime/private-fs.cjs');
const { compatibleProductVersion, startBackendProcess, validateDesktopDeployment } = require('./runtime/backend-host.cjs');
const { startStaticWebServer } = require('./runtime/web-server.cjs');

if (process.versions.electron !== '45.2.1') throw new Error('Desktop requires the pinned Electron runtime');
const windowsPortable = process.platform === 'win32' && (
  app.isPackaged || privateFs.isSamePath(__dirname, path.join(process.resourcesPath, 'app'))
);
const launchConfigFlagIndex = process.argv.indexOf('--launch-config');
if (windowsPortable && launchConfigFlagIndex !== -1) throw new Error('Windows Portable does not accept --launch-config');
const standaloneMode = launchConfigFlagIndex === -1;

let root;
let configFile;
let config;
let sessionPaths;
let standaloneResources;
let broker;
let shmBridge;
let webServer;
let backendProcess;
let singleInstanceGranted = true;

function resolveDefaultTransport() {
  const raw = process.env.MAKO_PREVIEW_TRANSPORT?.trim().toLowerCase();
  if (raw) {
    if (raw !== 'shm' && raw !== 'dma-buf') throw new Error(`Unsupported MAKO_PREVIEW_TRANSPORT: ${raw}`);
    return raw;
  }
  return process.platform === 'linux' ? 'dma-buf' : 'shm';
}

function resolveStandaloneResources() {
  const resourcesDir = process.resourcesPath;
  const packagedFrontend = path.join(resourcesDir, 'frontend-dist');
  const packagedBackend = path.join(resourcesDir, 'backend-dist');
  let frontendDist;
  let backendDist;
  if (fs.existsSync(path.join(packagedFrontend, 'index.html')) && fs.existsSync(path.join(packagedBackend, 'deployment.json'))) {
    frontendDist = packagedFrontend;
    backendDist = packagedBackend;
  } else {
    const repoRoot = fs.realpathSync(path.join(__dirname, '..'));
    frontendDist = path.join(repoRoot, 'frontend/dist');
    const pointer = path.join(repoRoot, '.local/backend-current.json');
    if (!fs.existsSync(path.join(frontendDist, 'index.html')) || !fs.existsSync(pointer)) {
      throw new Error('缺少桌面应用构建产物（frontend-dist 或 backend-dist）。');
    }
    backendDist = JSON.parse(fs.readFileSync(pointer, 'utf8')).dist;
  }
  const validated = validateDesktopDeployment(backendDist, '45.2.1');
  if (!compatibleProductVersion(validated.deployment.version)) {
    throw new Error(`桌面后端部署版本 ${validated.deployment.version} 与当前应用不兼容。`);
  }
  const brokerAddonSha256 = validated.gpuManifest.files['desktop/gpu_preview_broker.node'];
  const brokerAddon = brokerAddonSha256 ? path.join(validated.dist, 'desktop', 'gpu_preview_broker.node') : null;
  if (brokerAddon && !privateFs.isSamePath(fs.realpathSync(brokerAddon), brokerAddon)) {
    throw new Error('Desktop broker payload mismatch');
  }
  const shmAddonSha256 = validated.gpuManifest.files['desktop/shm_bridge.node'];
  const shmAddon = shmAddonSha256 ? path.join(validated.dist, 'desktop', 'shm_bridge.node') : null;
  if (shmAddon && !privateFs.isSamePath(fs.realpathSync(shmAddon), shmAddon)) {
    throw new Error('Desktop SHM bridge payload mismatch');
  }
  return { frontendDist, backendDist: validated.dist, brokerAddon, brokerAddonSha256, shmAddon, shmAddonSha256 };
}

if (standaloneMode) {
  sessionPaths = prepareStandaloneSession('makokara-generator');
  app.setPath('userData', sessionPaths.userData);
  app.setPath('crashDumps', sessionPaths.crashDumps);
  app.commandLine.appendSwitch('disable-breakpad');
  if (typeof app.setDesktopName === 'function') app.setDesktopName('makokara-generator.desktop');
  singleInstanceGranted = app.requestSingleInstanceLock();
  if (!singleInstanceGranted) {
    app.exit(0);
  } else {
    if (!windowsPortable) {
      standaloneResources = resolveStandaloneResources();
      if (standaloneResources.brokerAddon) broker = require(standaloneResources.brokerAddon);
      if (standaloneResources.shmAddon) shmBridge = require(standaloneResources.shmAddon);
    }
    config = { ready: false };
  }
} else {
  root = fs.realpathSync(path.join(__dirname, '..'));
  configFile = process.argv[launchConfigFlagIndex + 1];
  config = readLaunchConfig(configFile, root, process.pid);
  sessionPaths = prepareDesktopSession(root, config.sessionName);
  app.setPath('userData', sessionPaths.userData);
  app.setPath('crashDumps', sessionPaths.crashDumps);
  app.commandLine.appendSwitch('disable-breakpad');
  if (config.brokerAddon) {
    const addon = fs.realpathSync(config.brokerAddon);
    if (
      !privateFs.isSamePath(addon, config.brokerAddon) ||
      !privateFs.isInsideRoot(path.join(root, '.local/backend-dist'), addon) ||
      createHash('sha256').update(fs.readFileSync(addon)).digest('hex') !== config.brokerAddonSha256
    ) {
      throw new Error('Desktop broker payload mismatch');
    }
    broker = require(addon);
  }
  if (config.shmAddon) {
    const shmAddon = fs.realpathSync(config.shmAddon);
    if (
      !privateFs.isSamePath(shmAddon, config.shmAddon) ||
      !privateFs.isInsideRoot(path.join(root, '.local/backend-dist'), shmAddon) ||
      createHash('sha256').update(fs.readFileSync(shmAddon)).digest('hex') !== config.shmAddonSha256
    ) {
      throw new Error('Desktop SHM bridge payload mismatch');
    }
    shmBridge = require(shmAddon);
  }
}

const performanceDiagnostics = config?.performanceDiagnostics ? require('./gpu-preview/performance-diagnostics.cjs').createPerformanceDiagnostics() : null;
const measured = (type, action, metadata, threshold) => performanceDiagnostics ? performanceDiagnostics.measure(type, action, metadata, threshold) : action();
const decimal = value => typeof value === 'string' && /^(0|[1-9]\d*)$/.test(value) && BigInt(value)<=0xffffffffffffffffn;
const positive = value => decimal(value) && BigInt(value)>0n;
const identityKeys = ['streamId','hostInstanceId','producerEpoch','inputGeneration','consumerEpoch','timelineEpoch','poolGeneration','slotId','slotEpoch','frameSeq'];
const lease = m => Object.fromEntries(identityKeys.map(k=>[k,m[k]]));
const leaseKey = m => identityKeys.map(k=>m[k]).join('/');
function diagnostics(value) {
  if(!value)return undefined;
  if(JSON.stringify(value).length>16384||!['total','warningCount','errorCount'].every(k=>Number.isSafeInteger(value[k])&&value[k]>=0)||!Array.isArray(value.items)||value.items.length>4)throw new Error('Invalid GPU diagnostics summary');
  return value;
}
const delay = ms => new Promise(done=>setTimeout(done,ms));
let window, stream, gpuInfo, ending=false, lost=false, attachBusy=false, attachSeq=0, previewIsolation;
const events=[];
const record=(type,detail={})=>{ events.push({type,ms:performance.now(),...detail}); if(events.length>256) events.shift(); };
const safeState = value => {
  if (window && !window.isDestroyed()) window.webContents.send('mako-gpu:state',value);
};
const streamOutstanding = s => !s ? 0 : (s.transport === 'shm' ? (s.shmConsumer?.getOutstandingCount() ?? 0) : s.imports.size);
function closeStreamConnection(s, conn = s?.connection) {
  if (!s) return;
  try { s.shmConsumer?.close(); } catch {}
  s.shmConsumer = null;
  if (s.pendingRendererPort) {
    try { s.pendingRendererPort.close?.(); } catch {}
    s.pendingRendererPort = null;
  }
  if (conn) {
    if (s.transport === 'shm') { try { conn.close(); } catch {} }
    else { try { broker.close(conn); } catch {} }
  }
  s.connection = null;
}
function isolatePreview(code='gpuConsumerFailure',s=stream) {
  if(ending||s?.isolated)return;
  lost=true;
  const isolation={code,unknownOutstanding:streamOutstanding(s),workerTerminationConfirmed:false};
  previewIsolation=isolation;record('preview-isolated',isolation);
  // Keep the editor and the bounded unknown consumer references alive. The host
  // retires its owned Worker; no replacement pool is allowed in this process.
  const termination=s?isolateGpuPreviewStream(s,{
    closeConnection:(conn)=>closeStreamConnection(s,conn),
    detachRenderer:()=>window?.webContents.send('mako-gpu:detach',{attachSeq:s.attachSeq}),
    abortWorker:async()=>{
      const response=await fetch(new URL(`/internal/gpu-preview-streams/${encodeURIComponent(s.streamId)}/consumer-failed`,config.baseUrl),{
        method:'POST',headers:{'Content-Type':'application/json','X-Mako-Desktop-Secret':config.consumerSecret},
        body:JSON.stringify({consumerId:config.consumerId,hostInstanceId:config.hostInstanceId,inputGeneration:s.inputGeneration??s.expectedInputGeneration,producerEpoch:s.producerEpoch}),
        signal:AbortSignal.timeout(10000)});
      return response.ok&&(await response.json()).terminated===true;
    },
  }):Promise.resolve(false);
  try{safeState({type:'failed',...(s?controlIdentity(s):{}),code:'GPU 预览已停用，编辑和保存仍可继续；重启应用后可重试预览。',failureCode:code,previewDisabled:true,workerCode:s?.workerFailure?.code});}catch{}
  console.error(JSON.stringify({type:'desktop-preview-isolated',...isolation,workerFailure:s?.workerFailure,counters:sTelemetry}));
  void termination.then(terminated=>{
    isolation.workerTerminationConfirmed=terminated;
    record('preview-worker-termination',{confirmed:terminated});
  });
}
let standaloneStopPromise = null;
function shutdownOwnedRuntime(timeoutMs = 15000) {
  standaloneStopPromise ??= (async () => {
    const tasks = [];
    if (webServer) tasks.push(webServer.close().catch(() => {}));
    if (backendProcess) tasks.push(backendProcess.stop().catch(() => {}));
    if (tasks.length === 0) return;
    await Promise.race([Promise.all(tasks), delay(timeoutMs)]);
  })();
  return standaloneStopPromise;
}
function failStop(code='desktopUnhandledFailure') {
  if(ending)return;
  ending=true;lost=true;
  console.error(JSON.stringify({type:'desktop-fail-stop',code}));
  void shutdownOwnedRuntime(5000).finally(()=>app.exit(1));
}
function authorize(event) {
  if(!window || event.sender!==window.webContents || event.senderFrame!==window.webContents.mainFrame || new URL(event.senderFrame.url).origin!==new URL(config.pageUrl).origin) throw new Error('Unexpected desktop caller');
}
function send(s,message) {
  if(lost || !s.connection)throw new Error('Desktop stream disconnected');
  if(s.transport==='shm')s.connection.send(message);
  else broker.send(s.connection,JSON.stringify(message));
}
const controlIdentity=s=>({protocolVersion:1,streamId:s.streamId,hostInstanceId:config.hostInstanceId,producerEpoch:s.producerEpoch,inputGeneration:s.inputGeneration??s.expectedInputGeneration,consumerEpoch:'1'});
function physicalDevice(m) {
  const pci = gpuInfo?.gpuDevice.find(x=>x.active&&x.vendorId===m.gpu?.vendorId&&x.deviceId===m.gpu?.deviceId);
  const gpu=measured('main-getAppMetrics',()=>app.getAppMetrics()).find(x=>x.type==='GPU');
  if(!pci||!gpu)throw new Error('GPU device identity unavailable');
  const directory=`/proc/${gpu.pid}/fd`;
  const matched=fs.readdirSync(directory).some(fd=>{
    try {
      const node=fs.readlinkSync(path.join(directory,fd));
      if(!node.startsWith('/dev/dri/render'))return false;
      const dev=fs.statSync(node,{bigint:true}).rdev;
      return Number(((dev>>8n)&0xfffn)|((dev>>32n)&~0xfffn))===m.gpu.drmRenderMajor && Number((dev&0xffn)|((dev>>12n)&~0xffn))===m.gpu.drmRenderMinor;
    }catch{return false;}
  });
  if(!matched)throw new Error('Producer and Chromium physical GPU differ');
  sTelemetry.device={vendorId:m.gpu.vendorId,deviceId:m.gpu.deviceId,drmRenderMajor:m.gpu.drmRenderMajor,drmRenderMinor:m.gpu.drmRenderMinor,chromiumGpuPid:gpu.pid};
}
const samples=[];
let sampleRevision=0;
const completed=new Map();
const sTelemetry={imported:0,transferred:0,drawn:0,acknowledgedUnique:0,completedDrawnUnique:0,completedWithoutDraw:0,rejectedStale:0,peakOutstanding:0,device:null,displayedFps:'unmeasured',chromiumInternalCopies:'unmeasured'};
function validateFrame(s,packet) {
  const m=packet.metadata;
  if(diagnostics(m.diagnostics)?.errorCount)throw new Error('GPU error diagnostics cannot publish a successful frame');
  if(m.type!=='frame'||m.protocolVersion!==1||packet.peerPid!==s.workerPid||m.streamId!==s.streamId||m.hostInstanceId!==config.hostInstanceId||m.producerEpoch!==s.producerEpoch||m.consumerEpoch!=='1')throw new Error('Frame producer identity mismatch');
  for(const k of ['producerEpoch','inputGeneration','timelineEpoch','poolGeneration','slotEpoch'])if(!positive(m[k]))throw new Error('Invalid frame epoch');
  if(m.inputGeneration!==s.inputGeneration||m.poolGeneration!==s.poolGeneration)throw new Error('Unexpected frame generation');
  if(!Number.isSafeInteger(m.frameSeq)||m.frameSeq<=s.lastFrame||!Number.isSafeInteger(m.controlSeq)||m.controlSeq<1||!Number.isSafeInteger(m.timeUs)||m.timeUs<0||typeof m.timelineResetId!=='string'||!decimal(m.renderStartMonotonicNs)||!decimal(m.producerReadyMonotonicNs))throw new Error('Invalid frame time or sequence');
  if(!Number.isSafeInteger(m.slotId)||m.slotId<0||m.slotId>=3||BigInt(m.slotEpoch)<=(s.slotEpochs.get(m.slotId)??0n)||[...s.imports.values()].some(i=>i.metadata.slotId===m.slotId)||s.imports.size>=3)throw new Error('GPU pool bound or lease violated');
  if(m.width!==s.size.width||m.height!==s.size.height||m.format!=='BGRA_8888'||m.fourcc!=='AR24'||m.origin!=='top-left'||m.alphaMode!=='premultiplied'||m.colorSpace!=='srgb'||m.sync!=='queue-idle-foreign-release'||!['runtime','synthetic-gpu'].includes(m.provider)||m.counters?.cpuPixelReadbacks!==0)throw new Error('Unsupported GPU texture contract');
  if(m.modifier!=='0'||!Number.isSafeInteger(m.allocationSize)||m.allocationSize<1||m.allocationSize*3>64*1024**2||!Array.isArray(m.planes)||m.planes.length!==1||packet.fds.length!==1)throw new Error('Invalid native pixmap descriptor');
  for(const p of m.planes)if(p.fdIndex!==0||![p.stride,p.offset,p.size].every(Number.isSafeInteger)||p.stride<m.width*4||p.offset<0||p.size<p.offset+p.stride*m.height||p.size>m.allocationSize)throw new Error('Invalid native pixmap bounds');
  if(!s.device){physicalDevice(m);s.device={...m.gpu,pid:sTelemetry.device.chromiumGpuPid};}
  // GPU process loss/replacement disables the process preview consumer. The
  // hot path compares the established device without rescanning process metrics.
  else if(['vendorId','deviceId','drmRenderMajor','drmRenderMinor','deviceUUID'].some(k=>m.gpu?.[k]!==s.device[k]))throw new Error('GPU device changed');
  if(performanceDiagnostics)s.lastFrameMetadata=lease(m);s.lastFrame=m.frameSeq;s.slotEpochs.set(m.slotId,BigInt(m.slotEpoch));
  return m;
}
async function consume(s,packet) {
  const texture=performanceDiagnostics?{importSharedTexture:options=>measured('main-importSharedTexture',()=>sharedTexture.importSharedTexture(options),packet.metadata),sendSharedTexture:(options,metadata)=>{metadata.diagnosticTransferStartNs=process.hrtime.bigint().toString();return sharedTexture.sendSharedTexture(options,metadata);}}:sharedTexture;
  await importAndTransferFrame({packet,broker,sharedTexture:texture,validate:p=>{const m=validateFrame(s,p);return performanceDiagnostics?{...m,diagnosticBrokerReceivedNs:packet.receivedNs.toString()}:m;},timestamp:m=>m.timeUs,
    targetFrame:()=>window.webContents.mainFrame,
    onImported:item=>{item.receivedNs=packet.receivedNs;s.imports.set(leaseKey(item.metadata),item);sTelemetry.imported++;sTelemetry.peakOutstanding=Math.max(sTelemetry.peakOutstanding,s.imports.size);},
    shouldTransfer:m=>{
      const current=!s.stopping&&stream===s&&!window.isDestroyed()&&window.isVisible()&&s.desired&&m.timelineResetId===s.desired.timelineResetId&&m.controlSeq>=s.barrierSeq;
      if(!current)sTelemetry.rejectedStale++;
      return current;
    },
    onTransferred:()=>{sTelemetry.transferred++;},
    onFailure:()=>isolatePreview('gpuImportOrReleaseFailed',s),
    onAbandonedRelease:()=>isolatePreview('gpuCompletionUnknown',s),
    onAllReferencesReleased:m=>{
      // No exception can leave this native completion boundary.
      try{
        if(lost||ending||!s.connection)return;
        const key=leaseKey(m);
        const item=s.imports.get(key);
        if(!item)throw new Error('Duplicate GPU final release');
        send(s,{type:'release',protocolVersion:1,...lease(m)});
        s.imports.delete(key);sTelemetry.acknowledgedUnique++;
        if(item.drawn)sTelemetry.completedDrawnUnique++;else sTelemetry.completedWithoutDraw++;
        const completedNs=process.hrtime.bigint();
        const sample={attachedMonotonicNs:s.attachedMonotonicNs,controlApplied:s.controlApplied,sampleRevision:++sampleRevision,...lease(m),transport:'dma-buf',api:'vulkan',timeUs:m.timeUs,controlSeq:m.controlSeq,timelineResetId:m.timelineResetId,provider:m.provider,width:m.width,height:m.height,timings:m.timings,counters:m.counters,drawn:!!item.drawn,completedMonotonicNs:completedNs.toString(),renderMs:Number(BigInt(m.producerReadyMonotonicNs)-BigInt(m.renderStartMonotonicNs))/1e6,brokerToCompletionMs:Number(completedNs-item.receivedNs)/1e6,
          ...(s.clock?{readyToBrokerMs:Number(item.receivedNs-(BigInt(m.producerReadyMonotonicNs)-BigInt(s.clock.workerMinusSenderNs)))/1e6,readyToCompletionMs:Number(completedNs-(BigInt(m.producerReadyMonotonicNs)-BigInt(s.clock.workerMinusSenderNs)))/1e6,clockUncertaintyMs:Number(BigInt(s.clock.uncertaintyNs))/1e6,totalClockUncertaintyMs:Number(BigInt(s.clock.uncertaintyNs)+BigInt(s.rendererUncertaintyNs??'0'))/1e6}:{})};
        // Retain bounded identity/observation data only, never an imported GPU
        // reference across frames after its exact completion has been ACKed.
        samples.push(sample);completed.set(key,{item:{metadata:m,drawn:item.drawn},sample});
        if(completed.size>256)completed.delete(completed.keys().next().value);
        if(samples.length>4096)samples.shift();
        completeStop(s);
      }catch{isolatePreview('gpuFinalReleaseFailed',s);}
    },
  });
}
function completeStop(s) {
  if(s.stopped&&streamOutstanding(s)===0&&s.connection){clearInterval(s.poll);closeStreamConnection(s);s.resolveStop?.();}
}
function flushControl(s) {
  if(s.stopping||s.executing||!s.pending||!s.hello||(s.pending.mode==='playing'&&!s.clock))return;
  const value=s.pending;s.pending=null;s.executing=value;
  const {senderMonotonicNs,rendererUncertaintyNs,mainReceivedMonotonicNs,...state}=value;
  send(s,{type:'set-playback-state',...controlIdentity(s),...state,clockSample:{mediaTimeUs:value.mediaTimeUs,senderMonotonicNs}});
}
function packet(s,p) {
  const m=p.metadata;
  s.lastPacketType=['hello-accepted','frame','clock-ping','clock-calibrated','control-applied','state','stopped','failed'].includes(m.type)?m.type:'unknown';
  p.receivedNs=process.hrtime.bigint();
  if(s.transport!=='shm'&&p.peerPid!==s.workerPid)throw new Error('Worker peer PID mismatch');
  validateWorkerMessage(m,s,config);
  if(m.type==='frame'){
    if(s.transport==='shm'){
      try{
        if(!s.shmConsumer)throw new Error('SHM consumer not initialized before frame');
        const current=!s.stopping&&stream===s&&!window.isDestroyed()&&window.isVisible()&&s.desired&&m.timelineResetId===s.desired.timelineResetId&&m.controlSeq>=s.barrierSeq;
        if(current)s.lastFrameReceivedNs.set(leaseKey(m),p.receivedNs);
        s.shmConsumer.consumeFrame(m);
        if(current){sTelemetry.imported++;sTelemetry.transferred++;sTelemetry.peakOutstanding=Math.max(sTelemetry.peakOutstanding,s.shmConsumer.getPeakOutstanding());}
        else sTelemetry.rejectedStale++;
      }catch{isolatePreview('gpuFrameFailed',s);}
      return;
    }
    void consume(s,p).catch(()=>isolatePreview('gpuFrameFailed',s));return;
  }
  if(s.transport!=='shm'){for(const fd of p.fds)broker.closeFd(fd);}
  if(p.fds.length)throw new Error('Control packet carried handles');
  if(m.type==='hello-accepted'){
    if(s.hello||m.protocolVersion!==1||m.streamId!==s.streamId||m.hostInstanceId!==config.hostInstanceId||m.producerEpoch!==s.producerEpoch||m.consumerId!==config.consumerId||m.consumerEpoch!=='1'||m.inputGeneration!==s.expectedInputGeneration||m.poolGeneration!==s.poolGeneration||m.echoSenderMonotonicNs!==s.helloNs)throw new Error('Invalid Worker handshake');
    s.api=m.api??'vulkan';
    if(s.transport==='shm'){
      if(m.transport!=='shm')throw new Error('Worker transport mismatch: expected shm');
      const {port1,port2}=new MessageChannelMain();
      s.shmConsumer=createShmStreamConsumer({
        shmBridge,
        shmName:m.shmName,
        allocationBytes:m.allocationBytes,
        size:s.size,
        strideBytes:m.strideBytes,
        pixelFormat:m.pixelFormat,
        streamIdentity:{streamId:s.streamId},
        hostConfig:{hostInstanceId:config.hostInstanceId,producerEpoch:s.producerEpoch,inputGeneration:m.inputGeneration,poolGeneration:m.poolGeneration},
        port:port1,
        shouldTransfer:meta=>!s.stopping&&stream===s&&!window.isDestroyed()&&window.isVisible()&&s.desired&&meta.timelineResetId===s.desired.timelineResetId&&meta.controlSeq>=s.barrierSeq,
        sendRelease:rel=>{if(lost||ending||!s.connection)return;send(s,rel);sTelemetry.acknowledgedUnique++;completeStop(s);},
        onFrameCompleted:({metadata:meta,drawn,timings,counters})=>{
          const key=leaseKey(meta),receivedNs=s.lastFrameReceivedNs.get(key)??process.hrtime.bigint();
          s.lastFrameReceivedNs.delete(key);
          s.shmDrawnKeys.delete(key);
          if(drawn)sTelemetry.completedDrawnUnique++;else sTelemetry.completedWithoutDraw++;
          const completedNs=process.hrtime.bigint();
          const sample={attachedMonotonicNs:s.attachedMonotonicNs,controlApplied:s.controlApplied,sampleRevision:++sampleRevision,...lease(meta),transport:'shm',api:meta.api,pixelFormat:meta.pixelFormat,strideBytes:meta.strideBytes,timeUs:meta.timeUs,controlSeq:meta.controlSeq,timelineResetId:meta.timelineResetId,provider:meta.provider,width:meta.width,height:meta.height,timings,counters,drawn:!!drawn,completedMonotonicNs:completedNs.toString(),renderMs:Number(BigInt(meta.producerReadyMonotonicNs)-BigInt(meta.renderStartMonotonicNs))/1e6,brokerToCompletionMs:Number(completedNs-receivedNs)/1e6,
            ...(s.clock?{readyToBrokerMs:Number(receivedNs-(BigInt(meta.producerReadyMonotonicNs)-BigInt(s.clock.workerMinusSenderNs)))/1e6,readyToCompletionMs:Number(completedNs-(BigInt(meta.producerReadyMonotonicNs)-BigInt(s.clock.workerMinusSenderNs)))/1e6,clockUncertaintyMs:Number(BigInt(s.clock.uncertaintyNs))/1e6,totalClockUncertaintyMs:Number(BigInt(s.clock.uncertaintyNs)+BigInt(s.rendererUncertaintyNs??'0'))/1e6}:{})};
          samples.push(sample);completed.set(key,{item:{metadata:meta,drawn:!!drawn},sample});
          if(completed.size>256)completed.delete(completed.keys().next().value);
          if(samples.length>4096)samples.shift();
          completeStop(s);
        },
        onError:()=>isolatePreview('gpuFinalReleaseFailed',s),
      });
      s.pendingRendererPort=port2;
      sTelemetry.device={api:m.api,name:m.device?.name,vendorId:m.device?.vendorId,deviceId:m.device?.deviceId};
    }
    s.hello=true;s.inputGeneration=m.inputGeneration;s.poolGeneration=m.poolGeneration;s.resolveHello();flushControl(s);return;
  }
  if(m.type==='clock-ping'){if(!decimal(m.workerMonotonicNs))throw new Error('Invalid clock ping');const receive=process.hrtime.bigint().toString();send(s,{type:'clock-pong',echoWorkerMonotonicNs:m.workerMonotonicNs,senderReceiveMonotonicNs:receive,senderMonotonicNs:process.hrtime.bigint().toString()});return;}
  if(m.type==='clock-calibrated'){if(!/^-?(0|[1-9]\d*)$/.test(m.workerMinusSenderNs)||!decimal(m.uncertaintyNs))throw new Error('Invalid clock calibration');s.clock={workerMinusSenderNs:m.workerMinusSenderNs,uncertaintyNs:m.uncertaintyNs};safeState({type:'clock-calibrated',...s.clock});flushControl(s);return;}
  if(m.protocolVersion!==1||['streamId','hostInstanceId','producerEpoch','inputGeneration','consumerEpoch'].some(k=>m[k]!==controlIdentity(s)[k]))throw new Error('Control response identity mismatch');
  if(m.type==='control-applied'){
    if(!s.executing||m.controlSeq!==s.executing.controlSeq||!positive(m.timelineEpoch))throw new Error('Unexpected applied control');
    const applied=s.executing;s.executing=null;
    window.webContents.send('mako-gpu:barrier',{...controlIdentity(s),inputGeneration:s.inputGeneration,poolGeneration:s.poolGeneration,timelineEpoch:m.timelineEpoch,timelineResetId:applied.timelineResetId,minControlSeq:s.barrierSeq,controlSeq:m.controlSeq});
    s.controlApplied={...controlIdentity(s),controlSeq:m.controlSeq,timelineResetId:applied.timelineResetId,mainObservedMonotonicNs:process.hrtime.bigint().toString(),mainReceivedMonotonicNs:applied.mainReceivedMonotonicNs};
    safeState({...m,timelineResetId:applied.timelineResetId});flushControl(s);return;
  }
  if(m.type==='state'){diagnostics(m.diagnostics);s.workerState={...m,mainObservedMonotonicNs:process.hrtime.bigint().toString()};safeState(m);return;}
  if(m.type==='stopped'){if(m.drained!==true||streamOutstanding(s)!==0)throw new Error('Worker stopped before exact lease drain');s.stopped=true;completeStop(s);return;}
  if(m.type==='failed'){
    // 先记下 Worker 自带的失败身份，诊断摘要无效或完成未知时日志仍能给出真实原因。
    s.workerFailure=workerFailureSummary(m);
    const summary=diagnostics(m.diagnostics);
    if(m.drained===true&&streamOutstanding(s)===0){
      s.stopping=true;s.stopped=true;s.pending=null;s.executing=null;
      window.webContents.send('mako-gpu:detach',{attachSeq:s.attachSeq});
      completeStop(s);
      safeState({type:'failed',...controlIdentity(s),code:m.code??m.failureCode??'gpuRenderFailed',diagnostics:summary});
      return;
    }
    safeState({type:'failed',...controlIdentity(s),code:'gpuWorkerFailed',workerCode:s.workerFailure.code,diagnostics:summary});
    throw new Error('Worker GPU completion unknown');
  }
  throw new Error('Unknown Worker GPU packet');
}
async function detach() {
  const s=stream;
  if(!s)return;
  if(s.isolated){await s.isolationPromise;return;}
  if(s.stopped&&!s.connection){if(stream===s)stream=null;return;}
  if(s.stopping){await s.stopPromise;if(s.isolated)await s.isolationPromise;return;}
  s.stopping=true;window?.webContents.send('mako-gpu:detach',{attachSeq:s.attachSeq});
  s.stopPromise=new Promise((done,reject)=>{s.resolveStop=done;const timeout=setTimeout(()=>reject(new Error('GPU drain deadline')),10000);s.clearStop=()=>clearTimeout(timeout);});
  try{send(s,{type:'stop',...controlIdentity(s)});await s.stopPromise;}catch{isolatePreview('gpuDrainTimeout',s);}
  finally{s.clearStop();if(s.isolated)await s.isolationPromise;else if(stream===s)stream=null;}
  safeState({type:'detached'});
}
async function attach(streamId,expectedInputGeneration) {
  if(lost)throw new Error('GPU preview disabled after an unsafe completion; restart the app to retry');
  if(typeof streamId!=='string'||!/^[a-zA-Z0-9_-]{1,128}$/.test(streamId)||!positive(expectedInputGeneration)||attachBusy)throw new Error('Invalid desktop stream attachment');
  attachBusy=true;
  let stage='drainPrevious';
  try{
    await detach();
    stage='claim';
    const response=await fetch(new URL(`/internal/gpu-preview-streams/${encodeURIComponent(streamId)}/claim`,config.baseUrl),{method:'POST',headers:{'Content-Type':'application/json','X-Mako-Desktop-Secret':config.consumerSecret},body:JSON.stringify({consumerId:config.consumerId,hostInstanceId:config.hostInstanceId,expectedInputGeneration}),signal:AbortSignal.timeout(10000)});
    if(!response.ok){stage=`claimHttp${response.status}`;throw new Error('Desktop stream claim rejected');}
    stage='validateClaim';
    const claim=await response.json();
    const transport=claim.transport??config.transport??resolveDefaultTransport();
    if(!['dma-buf','shm'].includes(transport)||!positive(claim.producerEpoch)||!positive(claim.poolGeneration)||claim.inputGeneration!==expectedInputGeneration||!claim.size||!Number.isInteger(claim.size.width)||!Number.isInteger(claim.size.height)||claim.size.width<64||claim.size.height<64||claim.size.width>2560||claim.size.height>1440||!Number.isSafeInteger(claim.workerPid)||claim.workerPid<1||typeof claim.brokerEndpoint!=='string'||claim.brokerEndpoint.length===0||claim.brokerEndpoint.length>512||claim.brokerEndpoint.includes('\0')||(transport==='dma-buf'&&!path.isAbsolute(claim.brokerEndpoint))||typeof claim.handshake!=='string'||claim.handshake.length>512)throw new Error('Invalid desktop claim');
    if(transport==='shm'&&!shmBridge)throw new Error('Desktop SHM bridge addon unavailable');
    if(transport==='dma-buf'&&!broker)throw new Error('Desktop DMA-BUF broker addon unavailable');
    const s={streamId,transport,api:'vulkan',workerPid:claim.workerPid,producerEpoch:claim.producerEpoch,poolGeneration:claim.poolGeneration,size:claim.size,expectedInputGeneration,imports:new Map(),lastFrameReceivedNs:new Map(),shmDrawnKeys:new Set(),slotEpochs:new Map(),lastFrame:0,controlSeq:0,barrierSeq:1};
    stream=s;
    const until=Date.now()+5000;
    stage='connectBroker';
    if(transport==='shm'){
      if(process.platform==='linux'){
        while(true){
          try{
            const st=fs.lstatSync(claim.brokerEndpoint),parent=fs.lstatSync(path.dirname(claim.brokerEndpoint));
            if(!st.isSocket()||st.uid!==process.getuid()||!parent.isDirectory()||parent.uid!==process.getuid()||(parent.mode&0o777)!==0o700||fs.realpathSync(path.dirname(claim.brokerEndpoint))!==path.dirname(claim.brokerEndpoint))throw new Error('Invalid SHM broker ownership');
            break;
          }catch(error){if(Date.now()>until)throw error;await delay(20);}
        }
      }else if(process.platform==='win32'){
        if(!/^\\\\\.\\pipe\\mkg-shm-[a-f0-9]{24,64}$/.test(claim.brokerEndpoint))throw new Error('Invalid Windows SHM broker endpoint');
      }
      s.connection=await connectLocalStream(claim.brokerEndpoint,{timeoutMs:Math.max(100,until-Date.now()),retryDelayMs:20});
    }else{
      while(!s.connection){try{const st=fs.lstatSync(claim.brokerEndpoint),parent=fs.lstatSync(path.dirname(claim.brokerEndpoint));if(!st.isSocket()||st.uid!==process.getuid()||!parent.isDirectory()||parent.uid!==process.getuid()||(parent.mode&0o777)!==0o700||fs.realpathSync(path.dirname(claim.brokerEndpoint))!==path.dirname(claim.brokerEndpoint))throw new Error('Invalid broker ownership');s.connection=broker.connect(claim.brokerEndpoint);}catch(error){if(Date.now()>until)throw error;await delay(20);}}
    }
    const hello=new Promise((done,reject)=>{s.resolveHello=done;s.rejectHello=reject;});
    s.helloNs=process.hrtime.bigint().toString();
    stage='hello';
    send(s,{type:'hello',protocolVersion:1,consumerId:config.consumerId,consumerEpoch:'1',streamId,hostInstanceId:config.hostInstanceId,producerEpoch:s.producerEpoch,handshake:claim.handshake,senderMonotonicNs:s.helloNs});
    s.poll=setInterval(()=>{
      const pollStarted=performanceDiagnostics?process.hrtime.bigint():null;
      try{for(let count=0;count<16&&s.connection;count++){const p=s.transport==='shm'?s.connection.tryReceive():broker.receive(s.connection);if(!p)break;packet(s,p);}}
      catch(error){
        const known=['Worker response identity mismatch','Worker pool identity mismatch','Invalid Worker hello','Invalid calibrated clock','Worker stopped before exact lease drain','Unexpected applied control','peer disconnected; outstanding leases unknown','recvmsg failed','closed connection','Worker GPU completion unknown'];
        const cause=known.includes(error?.message)?error.message:'invalidWorkerPacket';
        console.error(JSON.stringify({type:'desktop-broker-error',stage:s.lastPacketType??'receive',cause,stopping:!!s.stopping,stopped:!!s.stopped,outstanding:streamOutstanding(s),worker:s.workerFailure}));
        isolatePreview('gpuBrokerProtocolFailed',s);
      }
      finally{if(pollStarted!==null){const end=process.hrtime.bigint();if(end-pollStarted>=10000000n)performanceDiagnostics.add('main-poll',pollStarted,end,{lease:performanceDiagnostics.identity(s.lastFrameMetadata)});}}
    },2);
    await Promise.race([hello,delay(5000).then(()=>{if(!s.hello)throw new Error('GPU handshake deadline');})]);
    s.attachedMonotonicNs=process.hrtime.bigint().toString();
    // 返回值与事件内容相同：渲染端谁先到用谁，attachSeq 丢掉晚到的重复事件（preload.cjs）。
    s.attachSeq=++attachSeq;
    const attached={...controlIdentity(s),transport:s.transport,api:s.api,inputGeneration:s.inputGeneration,poolGeneration:s.poolGeneration,attachSeq:s.attachSeq};
    if(s.transport==='shm'&&s.pendingRendererPort){
      const port2=s.pendingRendererPort;s.pendingRendererPort=null;
      window.webContents.postMessage('mako-gpu:shm-port',attached,[port2]);
    }else{
      window.webContents.send('mako-gpu:attached',attached);
    }
    return attached;
  }catch{
    const s=stream;
    if(s)isolatePreview('gpuAttachFailed',s);
    console.error(JSON.stringify({type:'desktop-attach-failed',stage}));
    throw new Error(`GPU attachment failed (${stage})`);
  }finally{attachBusy=false;}
}
function playback(value,rendererClock) {
  if(!stream||stream.stopping)throw new Error('No attached GPU preview');
  if(!value||!['paused','playing'].includes(value.mode)||!Number.isSafeInteger(value.mediaTimeUs)||value.mediaTimeUs<0||!Number.isFinite(value.rate)||value.rate<=0||value.rate>4||!Number.isInteger(value.targetFps)||value.targetFps<1||value.targetFps>30||typeof value.timelineResetId!=='string'||value.timelineResetId.length<1||value.timelineResetId.length>128||!Number.isFinite(value.samplePerformanceMs)||Object.keys(value).some(k=>!['mode','mediaTimeUs','rate','targetFps','timelineResetId','samplePerformanceMs'].includes(k))||!decimal(rendererClock?.senderMonotonicNs)||!decimal(rendererClock?.uncertaintyNs))throw new Error('Invalid bounded playback state');
  const now=process.hrtime.bigint(),sampleNs=BigInt(rendererClock.senderMonotonicNs),uncertaintyNs=BigInt(rendererClock.uncertaintyNs);
  if(uncertaintyNs>500000000n||sampleNs>now+uncertaintyNs+5000000n||now-sampleNs>30000000000n)throw new Error('Audio clock sample out of bounds');
  const s=stream;const controlSeq=++s.controlSeq;
  if(!Number.isSafeInteger(controlSeq))throw new Error('GPU control sequence exhausted');
  if(!s.desired||s.desired.timelineResetId!==value.timelineResetId||s.desired.mode!==value.mode||s.desired.rate!==value.rate||s.desired.targetFps!==value.targetFps)s.barrierSeq=controlSeq;
  const {samplePerformanceMs,...state}=value;
  s.rendererUncertaintyNs=rendererClock.uncertaintyNs;
  s.mediaClock={...state,controlSeq,mainSampleMonotonicNs:rendererClock.senderMonotonicNs,rendererUncertaintyNs:rendererClock.uncertaintyNs,workerUncertaintyNs:s.clock?.uncertaintyNs,uncertaintyNs:(uncertaintyNs+BigInt(s.clock?.uncertaintyNs??'0')).toString()};
  s.desired={...state};s.pending={...state,controlSeq,senderMonotonicNs:rendererClock.senderMonotonicNs,rendererUncertaintyNs:rendererClock.uncertaintyNs,mainReceivedMonotonicNs:now.toString()};flushControl(s);
  return {controlSeq};
}
for(const [name,handler] of Object.entries({config:()=>({mode:'production',transport:config.transport??resolveDefaultTransport(),...(performanceDiagnostics?{performanceDiagnostics:true}:{}),...(config.slowConsumerMs?{scenario:'slow',slowMs:config.slowConsumerMs}:{})}),'consumer-id':()=>{if(lost)throw new Error('GPU 预览已停用，请重启应用后重试；编辑和保存仍可继续。');return config.consumerId;},'acceptance-enabled':()=>app.commandLine.hasSwitch('remote-debugging-port'),clock:()=>({mainMonotonicNs:process.hrtime.bigint().toString()}),attach,playback,read:(sinceRevision=0)=>{if(!Number.isSafeInteger(sinceRevision)||sinceRevision<0)throw new Error('Invalid sample cursor');if(stream?.connection&&!stream.stopping&&!stream.stopped)send(stream,{type:'read-state',...controlIdentity(stream)});return {...sTelemetry,previewDisabled:lost,previewIsolation,nowMonotonicNs:process.hrtime.bigint().toString(),clock:stream?.clock?{...stream.clock,rendererUncertaintyNs:stream.rendererUncertaintyNs,totalUncertaintyNs:(BigInt(stream.clock.uncertaintyNs)+BigInt(stream.rendererUncertaintyNs??'0')).toString()}:undefined,mediaClock:stream?.mediaClock,workerState:stream?.workerState,controlApplied:stream?.controlApplied,attachedMonotonicNs:stream?.attachedMonotonicNs,activeStream:stream?{...controlIdentity(stream),transport:stream.transport,api:stream.api,poolGeneration:stream.poolGeneration,size:stream.size}:null,executingControls:stream?.executing?1:0,pendingControls:stream?.pending?1:0,outstanding:streamOutstanding(stream),sampleRevision,sampleCursorExpired:samples.length>0&&sinceRevision>0&&sinceRevision<Math.min(...samples.map(x=>x.sampleRevision))-1,samples:samples.filter(x=>x.sampleRevision>sinceRevision),mainPid:process.pid,workerPid:stream?.workerPid,rendererPid:window?.webContents.getOSProcessId(),performanceDiagnostics:performanceDiagnostics?.snapshot(),processes:measured('main-getAppMetrics',()=>app.getAppMetrics()).map(x=>({pid:x.pid,type:x.type,cpu:x.cpu,memory:x.memory}))};},detach}))ipcMain.handle(`mako-gpu:${name}`,(event,...args)=>{authorize(event);return name==='read'?measured('main-read',()=>handler(...args),undefined,10):handler(...args);});
ipcMain.handle('mako-gpu:acceptance-failure',(event,kind)=>{
  authorize(event);
  if(!app.commandLine.hasSwitch('remote-debugging-port')||!['renderer-loss','worker-loss'].includes(kind))throw new Error('Acceptance fault injection unavailable');
  const pid=kind==='renderer-loss'?window.webContents.getOSProcessId():stream?.workerPid;
  if(!pid)throw new Error('No active owned fault target');
  lost=true;record('injected-consumer-loss',{kind,pid});
  console.error(JSON.stringify({type:'desktop-injected-loss',kind,pid,unknownOutstanding:streamOutstanding(stream),acknowledgedAtLoss:sTelemetry.acknowledgedUnique}));
  process.kill(pid,'SIGKILL');isolatePreview(kind==='renderer-loss'?'injectedRendererLoss':'injectedWorkerLoss');
});
ipcMain.handle('mako-gpu:acceptance-visibility',(event,visible)=>{
  authorize(event);
  if(!app.commandLine.hasSwitch('remote-debugging-port')||typeof visible!=='boolean')throw new Error('Acceptance visibility unavailable');
  if(visible)window.show();else window.hide();
});
ipcMain.on('mako-gpu:error',event=>{authorize(event);isolatePreview('gpuRendererReceiverFailed');});
ipcMain.on('mako-gpu:drawn',(event,data)=>{authorize(event);const key=leaseKey(data);if(stream?.transport==='shm'){if(data.streamId===stream.streamId&&data.producerEpoch===stream.producerEpoch&&data.inputGeneration===stream.inputGeneration&&!stream.shmDrawnKeys.has(key)){stream.shmDrawnKeys.add(key);if(stream.shmDrawnKeys.size>64)stream.shmDrawnKeys.delete(stream.shmDrawnKeys.values().next().value);sTelemetry.drawn++;}safeState({type:'drawn',...data});return;}const done=completed.get(key),item=stream?.imports.get(key)??done?.item;if(item&&!item.drawn&&data.timestamp===item.metadata.timeUs){item.drawn=true;sTelemetry.drawn++;if(done){done.sample.drawn=true;done.sample.sampleRevision=++sampleRevision;sTelemetry.completedDrawnUnique++;sTelemetry.completedWithoutDraw--;}safeState({type:'drawn',...data});}});
ipcMain.on('mako-gpu:renderer-release',(event,data)=>{authorize(event);if(stream?.imports.has(leaseKey(data)))record('renderer-release-requested',{frameSeq:data.frameSeq});});
let closingProcess = false;
async function close() {
  if(ending||closingProcess)return;
  closingProcess=true;
  try{await detach();}catch{isolatePreview('gpuShutdownFailed');}
  ending=true;
  await shutdownOwnedRuntime();
  app.exit(0);
}
// 关窗会连后端一起停掉。先在页面里派发一次 beforeunload：编辑器在工程、样式库模板、作者草稿没保存时会拦下，
// 这时问一下再退出。页面 3 秒没回应（卡住）就当作没拦，照常退出。
let closing=false;
async function requestClose() {
  if(ending||closing)return;
  closing=true;
  try{
    const blocked=await Promise.race([window.webContents.executeJavaScript("!window.dispatchEvent(new Event('beforeunload',{cancelable:true}))",true).catch(()=>false),delay(3000).then(()=>false)]);
    if(blocked===true){
      const {response}=await dialog.showMessageBox(window,{type:'warning',buttons:['退出','取消'],defaultId:1,cancelId:1,noLink:true,title:'MakoKara Generator',
        message:'还有没保存的修改',detail:'工程、样式库模板或作者草稿有改动还没保存，现在退出会丢掉这些改动。'});
      if(response!==0)return;
    }
    await close();
  }finally{closing=false;}
}
for(const signal of ['SIGINT','SIGTERM','SIGHUP'])process.on(signal,()=>void close());
app.on('before-quit',event=>{if(!ending){event.preventDefault();void close();}});
app.on('child-process-gone',(_event,details)=>{if(details.type==='GPU')isolatePreview('gpuProcessLost');});
if(standaloneMode&&singleInstanceGranted){
  app.on('second-instance',()=>{if(window&&!window.isDestroyed()){if(window.isMinimized())window.restore();if(!window.isVisible())window.show();window.focus();}});
}
process.on('uncaughtException',()=>failStop('desktopUnhandledFailure'));
process.on('unhandledRejection',()=>failStop('desktopUnhandledFailure'));
if(singleInstanceGranted)(async()=>{
  if (standaloneMode) {
    let portableBackendOptions;
    if (windowsPortable) {
      const { resolveWindowsPortableResources, windowsPortableBackendOptions } = require('./runtime/windows-portable.cjs');
      standaloneResources = resolveWindowsPortableResources(process.resourcesPath);
      shmBridge = require(standaloneResources.shmAddon);
      portableBackendOptions = windowsPortableBackendOptions(standaloneResources, sessionPaths);
      app.setPath('temp', portableBackendOptions.cwd);
    }
    webServer = await startStaticWebServer({
      distRoot: standaloneResources.frontendDist,
      port: 5180,
      allowFallbackPort: true,
      getBootstrap: () => backendProcess?.info,
      dictFetch: (url, init) => net.fetch(url, init),
    });
    const consumerId = randomBytes(16).toString('hex');
    const consumerSecret = randomBytes(32).toString('hex');
    backendProcess = await startBackendProcess({
      ...portableBackendOptions,
      dist: standaloneResources.backendDist,
      profile: sessionPaths.profile,
      origins: webServer.origins,
      env: {
        ...portableBackendOptions?.env,
        MAKO_DESKTOP_CONSUMER_ID: consumerId,
        MAKO_DESKTOP_CONSUMER_SECRET: consumerSecret,
        MAKO_DESKTOP_MAIN_PID: String(process.pid),
      },
      log: (line) => console.log(line),
    });
    backendProcess.exited.then((code) => { if (!ending && !closingProcess) failStop(`desktopBackendExited:${code}`); });
    config = {
      consumerId,
      consumerSecret,
      pageUrl: webServer.origins[0],
      baseUrl: backendProcess.info.baseUrl,
      hostInstanceId: backendProcess.info.hostInstanceId,
      brokerAddon: standaloneResources.brokerAddon,
      brokerAddonSha256: standaloneResources.brokerAddonSha256,
      shmAddon: standaloneResources.shmAddon,
      shmAddonSha256: standaloneResources.shmAddonSha256,
      ready: true,
    };
  } else {
    const deadline=Date.now()+45000;
    while(!config.ready){if(Date.now()>deadline)throw new Error('Desktop launch deadline');await delay(50);config=readLaunchConfig(configFile,root,process.pid);}
  }
  await app.whenReady();
  // 编辑器自带顶栏，不要 Electron 默认菜单；窗口按工作区收小，最小 1024 宽（界面改版的最窄断点）。
  // 页面画好再显示，避免先闪一下白底。
  Menu.setApplicationMenu(null);
  const work=screen.getPrimaryDisplay().workAreaSize;
  const iconPng = fs.existsSync(path.join(__dirname, 'assets/icon.png')) ? path.join(__dirname, 'assets/icon.png') : path.join(__dirname, 'assets/icon-128.png');
  window=new BrowserWindow({width:Math.min(1440,work.width),height:Math.min(1000,work.height),minWidth:1024,minHeight:640,title:'MakoKara Generator',icon:iconPng,show:false,
    webPreferences:{preload:path.join(__dirname,'gpu-preview/preload.cjs'),sandbox:true,contextIsolation:true,nodeIntegration:false,backgroundThrottling:true}});
  window.once('ready-to-show',()=>{if(!ending&&!window.isDestroyed())window.show();});
  window.webContents.setWindowOpenHandler(()=>({action:'deny'}));
  // v1 keeps one renderer epoch for the whole process. SPA history changes do
  // not emit will-navigate; full document navigation/reload is prohibited.
  window.webContents.on('will-navigate',event=>event.preventDefault());
  window.webContents.on('will-frame-navigate',event=>event.preventDefault());
  window.webContents.on('before-input-event',(event,input)=>{if(input.key==='F5'||((input.control||input.meta)&&input.key.toLowerCase()==='r'))event.preventDefault();});
  window.webContents.on('render-process-gone',()=>isolatePreview('gpuRendererLost'));
  window.on('close',event=>{if(!ending){event.preventDefault();void requestClose();}});
  window.on('hide',()=>{if(stream)void detach().catch(()=>{});});
  gpuInfo=await app.getGPUInfo('complete');
  await window.loadURL(config.pageUrl);
  if(!ending&&!window.isDestroyed()&&!window.isVisible())window.show();
})().catch((error)=>{
  console.error(error?.stack||String(error));
  if(standaloneMode&&(windowsPortable||app.isReady())){try{dialog.showErrorBox('MakoKara Generator 启动失败',error?.message??String(error));}catch{}}
  failStop('desktopStartupFailed');
});
