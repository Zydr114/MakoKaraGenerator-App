'use strict';
const { app, BrowserWindow, ipcMain, sharedTexture } = require('electron');
const fs = require('node:fs');
const path = require('node:path');
const { createHash } = require('node:crypto');
const { readLaunchConfig } = require('./gpu-preview/launch-contract.cjs');
const { importAndTransferFrame } = require('./gpu-preview/import-consumer.cjs');
const { validateWorkerMessage } = require('./gpu-preview/wire.cjs');
const { prepareDesktopSession } = require('./gpu-preview/session-paths.cjs');

const root = fs.realpathSync(path.join(__dirname,'..'));
const configFile = process.argv[process.argv.indexOf('--launch-config')+1];
let config = readLaunchConfig(configFile,root,process.pid);
if (process.versions.electron !== '45.2.1') throw new Error('Desktop requires the pinned Electron runtime');
const sessionPaths=prepareDesktopSession(root,config.sessionName);
app.setPath('userData',sessionPaths.userData);
app.setPath('crashDumps',sessionPaths.crashDumps);
app.commandLine.appendSwitch('disable-breakpad');
const addon=fs.realpathSync(config.brokerAddon);
if(addon!==config.brokerAddon||!addon.startsWith(path.join(root,'.local/backend-dist')+path.sep)||createHash('sha256').update(fs.readFileSync(addon)).digest('hex')!==config.brokerAddonSha256)throw new Error('Desktop broker payload mismatch');
const broker = require(addon);
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
let window, stream, gpuInfo, ending=false, lost=false, attachBusy=false;
const events=[];
const record=(type,detail={})=>{ events.push({type,ms:performance.now(),...detail}); if(events.length>256) events.shift(); };
const safeState = value => {
  if (window && !window.isDestroyed()) window.webContents.send('mako-gpu:state',value);
};
function failStop(code='gpuConsumerFailure') {
  if(ending)return;
  lost=true; ending=true;
  record('fail-stop',{code});
  // A lost consumer makes GPU completion unknown. Disconnect without ACKing or
  // reusing any lease, then terminate this whole Electron instance.
  if(stream?.connection) {try{broker.close(stream.connection);}catch{}stream.connection=null;}
  clearInterval(stream?.poll);
  try { safeState({type:'failed',code}); console.error(JSON.stringify({type:'desktop-fail-stop',code,unknownOutstanding:stream?.imports.size??0,counters:sTelemetry})); }
  finally { app.exit(1); }
}
function authorize(event) {
  if(!window || event.sender!==window.webContents || event.senderFrame!==window.webContents.mainFrame || new URL(event.senderFrame.url).origin!==new URL(config.pageUrl).origin) throw new Error('Unexpected desktop caller');
}
function send(s,message) {
  if(lost || !s.connection)throw new Error('Desktop stream disconnected');
  broker.send(s.connection,JSON.stringify(message));
}
const controlIdentity=s=>({protocolVersion:1,streamId:s.streamId,hostInstanceId:config.hostInstanceId,producerEpoch:s.producerEpoch,inputGeneration:s.inputGeneration??s.expectedInputGeneration,consumerEpoch:'1'});
function physicalDevice(m) {
  const pci = gpuInfo?.gpuDevice.find(x=>x.active&&x.vendorId===m.gpu?.vendorId&&x.deviceId===m.gpu?.deviceId);
  const gpu=app.getAppMetrics().find(x=>x.type==='GPU');
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
  // GPU process loss/replacement is a global child-process-gone fail-stop. The
  // hot path compares the established device without rescanning process metrics.
  else if(['vendorId','deviceId','drmRenderMajor','drmRenderMinor','deviceUUID'].some(k=>m.gpu?.[k]!==s.device[k]))throw new Error('GPU device changed');
  s.lastFrame=m.frameSeq;s.slotEpochs.set(m.slotId,BigInt(m.slotEpoch));
  return m;
}
async function consume(s,packet) {
  await importAndTransferFrame({packet,broker,sharedTexture,validate:p=>validateFrame(s,p),timestamp:m=>m.timeUs,
    targetFrame:()=>window.webContents.mainFrame,
    onImported:item=>{item.receivedNs=packet.receivedNs;s.imports.set(leaseKey(item.metadata),item);sTelemetry.imported++;sTelemetry.peakOutstanding=Math.max(sTelemetry.peakOutstanding,s.imports.size);},
    shouldTransfer:m=>{
      const current=!s.stopping&&stream===s&&!window.isDestroyed()&&window.isVisible()&&s.desired&&m.timelineResetId===s.desired.timelineResetId&&m.controlSeq>=s.barrierSeq;
      if(!current)sTelemetry.rejectedStale++;
      return current;
    },
    onTransferred:()=>{sTelemetry.transferred++;},
    onFailure:()=>failStop('gpuImportOrReleaseFailed'),
    onAbandonedRelease:()=>failStop('gpuCompletionUnknown'),
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
        const sample={attachedMonotonicNs:s.attachedMonotonicNs,controlApplied:s.controlApplied,sampleRevision:++sampleRevision,...lease(m),timeUs:m.timeUs,controlSeq:m.controlSeq,timelineResetId:m.timelineResetId,provider:m.provider,width:m.width,height:m.height,counters:m.counters,drawn:!!item.drawn,completedMonotonicNs:completedNs.toString(),renderMs:Number(BigInt(m.producerReadyMonotonicNs)-BigInt(m.renderStartMonotonicNs))/1e6,brokerToCompletionMs:Number(completedNs-item.receivedNs)/1e6,
          ...(s.clock?{readyToBrokerMs:Number(item.receivedNs-(BigInt(m.producerReadyMonotonicNs)-BigInt(s.clock.workerMinusSenderNs)))/1e6,readyToCompletionMs:Number(completedNs-(BigInt(m.producerReadyMonotonicNs)-BigInt(s.clock.workerMinusSenderNs)))/1e6,clockUncertaintyMs:Number(BigInt(s.clock.uncertaintyNs))/1e6,totalClockUncertaintyMs:Number(BigInt(s.clock.uncertaintyNs)+BigInt(s.rendererUncertaintyNs??'0'))/1e6}:{})};
        // Retain bounded identity/observation data only, never an imported GPU
        // reference across frames after its exact completion has been ACKed.
        samples.push(sample);completed.set(key,{item:{metadata:m,drawn:item.drawn},sample});
        if(completed.size>256)completed.delete(completed.keys().next().value);
        if(samples.length>4096)samples.shift();
        completeStop(s);
      }catch{failStop('gpuFinalReleaseFailed');}
    },
  });
}
function completeStop(s) {
  if(s.stopped&&s.imports.size===0&&s.connection){clearInterval(s.poll);broker.close(s.connection);s.connection=null;s.resolveStop?.();}
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
  if(p.peerPid!==s.workerPid)throw new Error('Worker peer PID mismatch');
  validateWorkerMessage(m,s,config);
  if(m.type==='frame'){void consume(s,p).catch(()=>failStop('gpuFrameFailed'));return;}
  for(const fd of p.fds)broker.closeFd(fd);
  if(p.fds.length)throw new Error('Control packet carried handles');
  if(m.type==='hello-accepted'){
    if(s.hello||m.protocolVersion!==1||m.streamId!==s.streamId||m.hostInstanceId!==config.hostInstanceId||m.producerEpoch!==s.producerEpoch||m.consumerId!==config.consumerId||m.consumerEpoch!=='1'||m.inputGeneration!==s.expectedInputGeneration||m.poolGeneration!==s.poolGeneration||m.echoSenderMonotonicNs!==s.helloNs)throw new Error('Invalid Worker handshake');
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
  if(m.type==='stopped'){if(m.drained!==true||s.imports.size!==0)throw new Error('Worker stopped before exact lease drain');s.stopped=true;completeStop(s);return;}
  if(m.type==='failed'){
    const summary=diagnostics(m.diagnostics);
    if(m.drained===true&&s.imports.size===0){
      s.stopping=true;s.stopped=true;s.pending=null;s.executing=null;
      window.webContents.send('mako-gpu:detach');
      completeStop(s);
      safeState({type:'failed',...controlIdentity(s),code:m.code??m.failureCode??'gpuRenderFailed',diagnostics:summary});
      return;
    }
    safeState({type:'failed',...controlIdentity(s),code:'gpuWorkerFailed',diagnostics:summary});
    throw new Error('Worker GPU completion unknown');
  }
  throw new Error('Unknown Worker GPU packet');
}
async function detach() {
  const s=stream;
  if(!s)return;
  if(s.stopped&&!s.connection){if(stream===s)stream=null;return;}
  if(s.stopping)return s.stopPromise;
  s.stopping=true;window?.webContents.send('mako-gpu:detach');
  s.stopPromise=new Promise((done,reject)=>{s.resolveStop=done;const timeout=setTimeout(()=>reject(new Error('GPU drain deadline')),10000);s.clearStop=()=>clearTimeout(timeout);});
  send(s,{type:'stop',...controlIdentity(s)});
  try{await s.stopPromise;}catch(error){failStop('gpuDrainTimeout');throw error;}finally{s.clearStop();if(stream===s)stream=null;}
  safeState({type:'detached'});
}
async function attach(streamId,expectedInputGeneration) {
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
    if(!positive(claim.producerEpoch)||!positive(claim.poolGeneration)||claim.inputGeneration!==expectedInputGeneration||!claim.size||!Number.isInteger(claim.size.width)||!Number.isInteger(claim.size.height)||claim.size.width<64||claim.size.height<64||claim.size.width>2560||claim.size.height>1440||!Number.isSafeInteger(claim.workerPid)||claim.workerPid<1||typeof claim.brokerEndpoint!=='string'||!path.isAbsolute(claim.brokerEndpoint)||typeof claim.handshake!=='string'||claim.handshake.length>512)throw new Error('Invalid desktop claim');
    const s={streamId,workerPid:claim.workerPid,producerEpoch:claim.producerEpoch,poolGeneration:claim.poolGeneration,size:claim.size,expectedInputGeneration,imports:new Map(),slotEpochs:new Map(),lastFrame:0,controlSeq:0,barrierSeq:1};
    stream=s;
    const until=Date.now()+5000;
    stage='connectBroker';
    while(!s.connection){try{const st=fs.lstatSync(claim.brokerEndpoint),parent=fs.lstatSync(path.dirname(claim.brokerEndpoint));if(!st.isSocket()||st.uid!==process.getuid()||!parent.isDirectory()||parent.uid!==process.getuid()||(parent.mode&0o777)!==0o700||fs.realpathSync(path.dirname(claim.brokerEndpoint))!==path.dirname(claim.brokerEndpoint))throw new Error('Invalid broker ownership');s.connection=broker.connect(claim.brokerEndpoint);}catch(error){if(Date.now()>until)throw error;await delay(20);}}
    const hello=new Promise(done=>{s.resolveHello=done;});
    s.helloNs=process.hrtime.bigint().toString();
    stage='hello';
    send(s,{type:'hello',protocolVersion:1,consumerId:config.consumerId,consumerEpoch:'1',streamId,hostInstanceId:config.hostInstanceId,producerEpoch:s.producerEpoch,handshake:claim.handshake,senderMonotonicNs:s.helloNs});
    s.poll=setInterval(()=>{
      try{for(let count=0;count<16&&s.connection;count++){const p=broker.receive(s.connection);if(!p)break;packet(s,p);}}
      catch(error){
        const known=['Worker response identity mismatch','Worker pool identity mismatch','Invalid Worker hello','Invalid calibrated clock','Worker stopped before exact lease drain','Unexpected applied control','peer disconnected; outstanding leases unknown','recvmsg failed','closed connection','Worker GPU completion unknown'];
        const cause=known.includes(error?.message)?error.message:'invalidWorkerPacket';
        console.error(JSON.stringify({type:'desktop-broker-error',stage:s.lastPacketType??'receive',cause,stopping:!!s.stopping,stopped:!!s.stopped,outstanding:s.imports.size}));
        failStop('gpuBrokerProtocolFailed');
      }
    },2);
    await Promise.race([hello,delay(5000).then(()=>{if(!s.hello)throw new Error('GPU handshake deadline');})]);
    s.attachedMonotonicNs=process.hrtime.bigint().toString();
    window.webContents.send('mako-gpu:attached',{...controlIdentity(s),inputGeneration:s.inputGeneration,poolGeneration:s.poolGeneration});
    return {streamId,inputGeneration:s.inputGeneration,producerEpoch:s.producerEpoch};
  }catch{
    const s=stream;
    if(s?.imports.size){failStop('gpuAttachFailed');}
    else if(s){clearInterval(s.poll);if(s.connection)try{broker.close(s.connection);}catch{}if(stream===s)stream=null;}
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
for(const [name,handler] of Object.entries({config:()=>({mode:'production',...(config.slowConsumerMs?{scenario:'slow',slowMs:config.slowConsumerMs}:{})}),'consumer-id':()=>config.consumerId,'acceptance-enabled':()=>app.commandLine.hasSwitch('remote-debugging-port'),clock:()=>({mainMonotonicNs:process.hrtime.bigint().toString()}),attach,playback,read:(sinceRevision=0)=>{if(!Number.isSafeInteger(sinceRevision)||sinceRevision<0)throw new Error('Invalid sample cursor');if(stream?.connection&&!stream.stopping&&!stream.stopped)send(stream,{type:'read-state',...controlIdentity(stream)});return {...sTelemetry,nowMonotonicNs:process.hrtime.bigint().toString(),clock:stream?.clock?{...stream.clock,rendererUncertaintyNs:stream.rendererUncertaintyNs,totalUncertaintyNs:(BigInt(stream.clock.uncertaintyNs)+BigInt(stream.rendererUncertaintyNs??'0')).toString()}:undefined,mediaClock:stream?.mediaClock,workerState:stream?.workerState,controlApplied:stream?.controlApplied,attachedMonotonicNs:stream?.attachedMonotonicNs,activeStream:stream?{...controlIdentity(stream),poolGeneration:stream.poolGeneration,size:stream.size}:null,executingControls:stream?.executing?1:0,pendingControls:stream?.pending?1:0,outstanding:stream?.imports.size??0,sampleRevision,sampleCursorExpired:samples.length>0&&sinceRevision>0&&sinceRevision<Math.min(...samples.map(x=>x.sampleRevision))-1,samples:samples.filter(x=>x.sampleRevision>sinceRevision),mainPid:process.pid,workerPid:stream?.workerPid,rendererPid:window?.webContents.getOSProcessId(),processes:app.getAppMetrics().map(x=>({pid:x.pid,type:x.type,cpu:x.cpu,memory:x.memory}))};},detach}))ipcMain.handle(`mako-gpu:${name}`,(event,...args)=>{authorize(event);return handler(...args);});
ipcMain.handle('mako-gpu:acceptance-failure',(event,kind)=>{
  authorize(event);
  if(!app.commandLine.hasSwitch('remote-debugging-port')||!['renderer-loss','worker-loss'].includes(kind))throw new Error('Acceptance fault injection unavailable');
  const pid=kind==='renderer-loss'?window.webContents.getOSProcessId():stream?.workerPid;
  if(!pid)throw new Error('No active owned fault target');
  lost=true;record('injected-consumer-loss',{kind,pid});
  process.kill(pid,'SIGKILL');failStop(kind==='renderer-loss'?'injectedRendererLoss':'injectedWorkerLoss');
});
ipcMain.handle('mako-gpu:acceptance-visibility',(event,visible)=>{
  authorize(event);
  if(!app.commandLine.hasSwitch('remote-debugging-port')||typeof visible!=='boolean')throw new Error('Acceptance visibility unavailable');
  if(visible)window.show();else window.hide();
});
ipcMain.on('mako-gpu:error',event=>{authorize(event);failStop('gpuRendererReceiverFailed');});
ipcMain.on('mako-gpu:drawn',(event,data)=>{authorize(event);const key=leaseKey(data),done=completed.get(key),item=stream?.imports.get(key)??done?.item;if(item&&!item.drawn&&data.timestamp===item.metadata.timeUs){item.drawn=true;sTelemetry.drawn++;if(done){done.sample.drawn=true;done.sample.sampleRevision=++sampleRevision;sTelemetry.completedDrawnUnique++;sTelemetry.completedWithoutDraw--;}safeState({type:'drawn',...data});}});
ipcMain.on('mako-gpu:renderer-release',(event,data)=>{authorize(event);if(stream?.imports.has(leaseKey(data)))record('renderer-release-requested',{frameSeq:data.frameSeq});});
async function close() {
  if(ending)return;
  try{await detach();ending=true;app.exit(0);}catch{failStop('gpuShutdownFailed');}
}
for(const signal of ['SIGINT','SIGTERM'])process.on(signal,()=>void close());
app.on('before-quit',event=>{if(!ending){event.preventDefault();void close();}});
app.on('child-process-gone',(_event,details)=>{if(details.type==='GPU')failStop('gpuProcessLost');});
process.on('uncaughtException',()=>failStop('desktopUnhandledFailure'));
process.on('unhandledRejection',()=>failStop('desktopUnhandledFailure'));
(async()=>{
  const deadline=Date.now()+45000;
  while(!config.ready){if(Date.now()>deadline)throw new Error('Desktop launch deadline');await delay(50);config=readLaunchConfig(configFile,root,process.pid);}
  await app.whenReady();
  window=new BrowserWindow({width:1440,height:1000,webPreferences:{preload:path.join(__dirname,'gpu-preview/preload.cjs'),sandbox:true,contextIsolation:true,nodeIntegration:false,backgroundThrottling:true}});
  window.webContents.setWindowOpenHandler(()=>({action:'deny'}));
  // v1 keeps one renderer epoch for the whole process. SPA history changes do
  // not emit will-navigate; full document navigation/reload is prohibited.
  window.webContents.on('will-navigate',event=>event.preventDefault());
  window.webContents.on('will-frame-navigate',event=>event.preventDefault());
  window.webContents.on('before-input-event',(event,input)=>{if(input.key==='F5'||((input.control||input.meta)&&input.key.toLowerCase()==='r'))event.preventDefault();});
  window.webContents.on('render-process-gone',()=>failStop('gpuRendererLost'));
  window.on('close',event=>{if(!ending){event.preventDefault();void close();}});
  window.on('hide',()=>{if(stream)void detach().catch(()=>{});});
  gpuInfo=await app.getGPUInfo('complete');
  await window.loadURL(config.pageUrl);
})().catch(()=>failStop('desktopStartupFailed'));
