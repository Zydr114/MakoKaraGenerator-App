'use strict';
// Standalone acceptance host. No product entry point, production UX or controls change.
if(!process.argv.includes('--run-real-gpu')){console.log('Prepared only. Supply --run-real-gpu --root <App> --output <prepared-output> --broker <matched-managed-addon> --driver <built-driver.dll>.');process.exit(0);}
const {app,BrowserWindow,ipcMain,sharedTexture,dialog,screen}=require('electron');
const fs=require('node:fs');
const path=require('node:path');
const http=require('node:http');
const {spawn}=require('node:child_process');
const {createHash,randomBytes,timingSafeEqual}=require('node:crypto');
const {createInterface}=require('node:readline');
const {createAdapter,createRouter}=require('./adapter-factory.cjs');
const option=name=>{const index=process.argv.indexOf(name);return index<0?undefined:process.argv[index+1];};
const root=fs.realpathSync(option('--root')??path.resolve(__dirname,'../../..'));
const output=path.resolve(option('--output')??'');
if(!output.startsWith(path.join(root,'.local')+path.sep))throw new Error('Output must be App .local');
const brokerPath=fs.realpathSync(option('--broker')??'');
if(!brokerPath.startsWith(path.join(root,'.local/backend-dist')+path.sep))throw new Error('Use matched App managed broker');
const worker=path.join(output,'dist/worker/MakoKaraGenerator.Backend.Worker.dll');
const driverFile=path.resolve(option('--driver')??'');
if(!fs.existsSync(worker)||!fs.existsSync(driverFile))throw new Error('Prepared isolated deployment and compiled driver required');
const sha=file=>createHash('sha256').update(fs.readFileSync(file)).digest('hex');
if(sha(brokerPath)!==sha(path.join(output,'dist/desktop/gpu_preview_broker.node')))throw new Error('Broker differs from isolated Worker deployment');
if(process.versions.electron!=='45.2.1')throw new Error('Use pinned App Electron 45.2.1');
app.setPath('userData',path.join(output,'dual-electron-profile'));
app.setPath('crashDumps',path.join(output,'dual-crash-dumps'));
app.commandLine.appendSwitch('disable-breakpad');
const record=(type,data)=>fs.appendFileSync(path.join(output,'dual-events.jsonl'),JSON.stringify({type,monotonicNs:process.hrtime.bigint().toString(),data})+'\n',{mode:0o600});
const pause=ms=>new Promise(resolve=>setTimeout(resolve,ms));
const invariant=(value,message)=>{if(!value)throw new Error(message);};
const identityKeys=['streamId','hostInstanceId','producerEpoch','inputGeneration','consumerEpoch','poolGeneration'];
const matches=(frame,identity)=>identityKeys.every(key=>frame[key]===identity[key]);
const own=new Map();
const proc=pid=>{try{const value=fs.readFileSync(`/proc/${pid}/stat`,'utf8'),fields=value.slice(value.lastIndexOf(')')+2).split(' ');return {pid,startTicks:fields[19],parentPid:Number(fields[1]),group:Number(fields[2]),state:fields[0]};}catch(error){if(error.code==='ENOENT'||error.code==='ESRCH')return {pid,exited:true};throw error;}};
function observeTree(pid){
  const all=fs.readdirSync('/proc').filter(name=>/^\d+$/.test(name)).map(name=>proc(Number(name))).filter(value=>!value.exited);
  const descendants=all.filter(value=>value.pid===pid);
  for(let index=0;index<descendants.length;index++)for(const value of all)
    if(value.parentPid===descendants[index].pid&&!descendants.some(old=>old.pid===value.pid))descendants.push(value);
  for(const value of descendants)own.set(`${value.pid}:${value.startTicks}`,value);
}
const html=`<!doctype html><meta charset="utf-8"><title>M04 real GPU consumer</title><style>html,body{margin:0;background:#18202a;color:white;font:14px sans-serif}canvas{width:100%;height:auto}</style><canvas id="mako-gpu-preview"></canvas><script>
let timer;window.m04Surface={async start(stream,input){await window.makoDesktopPreview.attachPreview(stream,input);const origin=performance.now();const update=()=>{const now=performance.now();return window.makoDesktopPreview.setPlaybackState({mode:'playing',mediaTimeUs:Math.round((now-origin)*1000),rate:1,targetFps:24,timelineResetId:stream+'-playing',samplePerformanceMs:now});};await update();timer=setInterval(()=>void update().catch(error=>{window.m04Failure=error.message;}),500);},async stop(){clearInterval(timer);await window.makoDesktopPreview.detachPreview();},read(){return window.makoDesktopPreview.readPreviewState();}};
</script>`;
const routes=new Map();let driver,driverExit,server,driverReady,readyResolve,readyReject,sequence=0,ending=false;
const pending=new Map(),windows=[],adapters=[],streams=[];
const sampleTimers=[];
let driverOriginal;
const request=(op,data={})=>new Promise((resolve,reject)=>{
  const id=++sequence,timer=setTimeout(()=>{pending.delete(id);reject(new Error(`Driver ${op} timed out`));},30000);
  pending.set(id,{resolve:value=>{clearTimeout(timer);resolve(value);},reject:error=>{clearTimeout(timer);reject(error);}});
  driver.stdin.write(JSON.stringify({id,op,...data})+'\n');
});
function readDriver(line){
  let value;try{value=JSON.parse(line);}catch{throw new Error('Unexpected private driver output');}
  if(value.ready){readyResolve(value);return;}
  const waiter=pending.get(value.id);if(!waiter)throw new Error('Uncorrelated private driver response');pending.delete(value.id);
  if(value.ok)waiter.resolve(value.data);else waiter.reject(new Error(`Driver rejected ${value.errorType}:${value.code??''}`));
}
async function waitFor(predicate,label,timeout=30000){const deadline=Date.now()+timeout;while(Date.now()<deadline){const result=await predicate();if(result)return result;await pause(50);}throw new Error(label+' deadline');}
async function snapshot(index){const value=await windows[index].webContents.executeJavaScript('window.m04Surface.read()');record('surface-snapshot',{surface:index,...value});return value;}
async function completed(index,since='0'){
  return waitFor(async()=>{const state=await snapshot(index);invariant(!state.sampleCursorExpired,'Completion sample cursor expired');const identity=state.activeStream;if(!identity)return null;
    const frames=state.samples.filter(frame=>frame.drawn===true&&matches(frame,identity)&&BigInt(frame.completedMonotonicNs)>BigInt(since));
    return frames.length?{identity,frames,state}:null;},`surface ${index} exact native completed drawn frame`);
}
async function poolRead(){observeTree(driver.pid);const state=await request('read');invariant(state.pool.total<=2&&state.pool.spare<=1,'Worker process quota exceeded');record('pool',state);return state;}
async function main(){
  if(fs.existsSync(path.join(output,'dual-events.jsonl')))throw new Error('Use new dual evidence output');
  record('manifest',{runClass:'real-dual-consumer-finite-not-performance',workerSha256:sha(worker),brokerSha256:sha(brokerPath),driverSha256:sha(driverFile),
    preloadSha256:sha(path.join(root,'desktop/gpu-preview/preload.cjs')),controlCadenceMs:500,consumerBoundary:'Two real independent sandbox webContents use production claim/adapter/native import/preload; exact native completion and ACK required',
    hostBoundary:'Scratch trusted Host/Proxy + real GpuPreviewStreamRegistry; temporary authenticated loopback claim endpoint; product single-consumer UX unchanged'});
  const stderr=fs.openSync(path.join(output,'dual-driver.stderr.log'),'wx',0o600);
  const args=[driverFile,'--run-real-gpu','--dual-consumer-driver','--worker',worker,'--output',output,'--main-pid',String(process.pid)];
  if(option('--snapshot'))args.push('--snapshot',path.resolve(option('--snapshot')));
  driverReady=new Promise((resolve,reject)=>{readyResolve=resolve;readyReject=reject;});
  driver=spawn('dotnet',args,{stdio:['pipe','pipe',stderr]});fs.closeSync(stderr);
  driverOriginal=proc(driver.pid);if(!driverOriginal.exited)own.set(`${driverOriginal.pid}:${driverOriginal.startTicks}`,driverOriginal);
  driverExit=new Promise(resolve=>driver.once('exit',(code,signal)=>{for(const waiter of pending.values())waiter.reject(new Error('Owned driver exited'));pending.clear();readyReject(new Error('Owned driver exited before ready'));resolve({code,signal});}));
  createInterface({input:driver.stdout}).on('line',readDriver);
  const ready=await Promise.race([driverReady,pause(30000).then(()=>{throw new Error('Driver ready deadline');})]);
  const host=ready.hostInstanceId;
  server=http.createServer(async(req,res)=>{
    try{
      if(req.method==='GET'&&req.url==='/'){res.writeHead(200,{'Content-Type':'text/html','Cache-Control':'no-store'});res.end(html);return;}
      const match=/^\/internal\/gpu-preview-streams\/([a-f0-9]{32})\/claim$/.exec(req.url??'');
      if(req.method!=='POST'||!match)throw new Error('Unknown private route');
      const chunks=[];let length=0;for await(const chunk of req){length+=chunk.length;if(length>4096)throw new Error('Claim too large');chunks.push(chunk);}
      const body=JSON.parse(Buffer.concat(chunks)),route=routes.get(body.consumerId),secret=req.headers['x-mako-desktop-secret'];
      if(!route||typeof secret!=='string'||secret.length!==64||!timingSafeEqual(Buffer.from(secret),Buffer.from(route.secret))||body.hostInstanceId!==host||body.streamId!==undefined)throw new Error('Claim authentication failed');
      const claim=await request('claim',{...body,streamId:match[1]});
      // Private capability response is not recorded or forwarded to renderers.
      res.writeHead(200,{'Content-Type':'application/json','Cache-Control':'no-store'});res.end(JSON.stringify(claim));
    }catch{res.writeHead(403,{'Content-Type':'application/json'});res.end('{"error":"claim rejected"}');}
  });
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const baseUrl=`http://127.0.0.1:${server.address().port}/`;
  await app.whenReady();const gpuInfo=await app.getGPUInfo('complete'),broker=require(brokerPath),router=createRouter(ipcMain),work=screen.getPrimaryDisplay().workArea;
  for(let index=0;index<2;index++){
    const consumerId=randomBytes(16).toString('hex'),secret=randomBytes(32).toString('hex');routes.set(consumerId,{secret});
    const config={root,pageUrl:baseUrl,baseUrl,hostInstanceId:host,consumerId,consumerSecret:secret};
    const adapter=createAdapter({root,config,electron:{app,dialog,sharedTexture},broker,router});adapters.push(adapter);
    const window=new BrowserWindow({x:work.x+Math.floor(index*work.width/2),y:work.y,width:Math.floor(work.width/2),height:Math.min(work.height,600),show:true,title:`M04 consumer ${index+1}`,
      webPreferences:{preload:path.join(root,'desktop/gpu-preview/preload.cjs'),sandbox:true,contextIsolation:true,nodeIntegration:false,backgroundThrottling:true}});
    windows.push(window);adapter.bind(window,gpuInfo);window.webContents.on('render-process-gone',()=>adapter.failStop('gpuRendererLost'));
    await window.loadURL(baseUrl);
    await waitFor(()=>window.webContents.executeJavaScript('!!window.m04Surface && !!window.makoDesktopPreview'),'Sandbox bridge ready');
    record('adapter-source',{surface:index,...adapter.provenance});
    const opened=await request('open',{consumerId,clientId:`m04-dual-${index}`});streams.push(opened);
    const proof=opened.opened.warmupBinding,prep=opened.preparation;
    invariant(proof?.originalDeviceBound===true&&proof.deviceInitializations===1&&proof.contextInitializations===1&&proof.preparationId===prep.preparationId&&proof.initializationId===prep.initializationId,'Original prepared device/context not reused');
    record('business-open',{surface:index,...opened});
    await window.webContents.executeJavaScript(`window.m04Surface.start(${JSON.stringify(opened.state.streamId)},${JSON.stringify(opened.state.inputGeneration)})`);
    const result=await completed(index);record('first-completed',{surface:index,identity:result.identity,frame:result.frames.at(-1)});
  }
  let sampling=false;
  sampleTimers.push(setInterval(()=>{if(sampling||ending)return;sampling=true;void poolRead().catch(error=>{record('observer-failed',{name:error.name,message:error.message});void finish(1);}).finally(()=>{sampling=false;});},100));
  const both=await poolRead();invariant(both.pool.bound===2&&both.pool.total===2&&both.pool.spare===0,'Two consumers must own exactly two active Workers');
  const before=both.workers.map(value=>value.ownedIdentity?.processId).sort();await request('verify');
  const after=await poolRead();invariant(JSON.stringify(before)===JSON.stringify(after.workers.map(value=>value.ownedIdentity?.processId).sort()),'Availability started third helper');
  const stopMark=process.hrtime.bigint().toString();await windows[1].webContents.executeJavaScript('window.m04Surface.stop()');
  const closed=await request('close',{streamId:streams[1].state.streamId});record('closed-second',closed);invariant(closed.terminationConfirmed===true,'Second original Worker not drained');
  await waitFor(async()=>{const value=await poolRead();return value.pool.bound===1&&value.pool.spare===1&&value.pool.total===2;},'One active plus full replacement spare');
  const remaining=await completed(0,stopMark),first=streams[0];
  const expectedFirst={streamId:first.state.streamId,hostInstanceId:first.hostInstanceId,producerEpoch:first.state.producerEpoch,
    inputGeneration:first.state.inputGeneration,consumerEpoch:'1',poolGeneration:first.state.poolGeneration};
  invariant(matches(remaining.identity,expectedFirst)&&matches(remaining.frames.at(-1),expectedFirst),'First consumer changed exact attachment identity');
  invariant(remaining.state.workerState?.failureCode==null,'First consumer failed after second close');record('first-still-completing',{identity:remaining.identity,frame:remaining.frames.at(-1)});
  await windows[0].webContents.executeJavaScript('window.m04Surface.stop()');
  record('closed-first',await request('close',{streamId:streams[0].state.streamId}));
  await poolRead();observeTree(driver.pid);await request('shutdown');
  const exit=await Promise.race([driverExit,pause(30000).then(()=>{throw new Error('Driver owned drain deadline');})]);
  invariant(exit.code===0&&exit.signal===null,'Driver shutdown failed');
  const remainingProcesses=[...own.values()].filter(value=>{const current=proc(value.pid);return !current.exited&&current.startTicks===value.startTicks;});
  invariant(remainingProcesses.length===0,'Owned Worker/process identity remains after shutdown');
  record('passed',{twoNativeCompletedDrawnConsumers:true,secondClosedAndReplenished:true,firstContinued:true,driverExit:exit,owned:[...own.values()],remaining:remainingProcesses});
}
app.on('child-process-gone',(_event,detail)=>{if(detail.type==='GPU'){record('gpu-loss',{type:detail.type});for(const adapter of adapters)adapter.failStop('gpuProcessLost');}});
main().then(()=>finish(0),error=>{record('failed',{name:error.name,message:error.message});void finish(1);});
async function finish(code){
  if(ending)return;ending=true;
  for(const timer of sampleTimers)clearInterval(timer);
  try{
    for(const adapter of adapters)try{await adapter.detach();}catch{}
    if(driver&&driver.exitCode===null){
      observeTree(driver.pid);try{await request('shutdown');}catch{}driver.stdin.end();
      await Promise.race([driverExit,pause(10000)]);
      if(driver.exitCode===null){const current=proc(driver.pid);if(!current.exited&&driverOriginal?.startTicks===current.startTicks)driver.kill('SIGTERM');code=1;}
    }
    server?.close();for(const window of windows)if(!window.isDestroyed())window.destroy();
    const remaining=[...own.values()].filter(value=>{const current=proc(value.pid);return !current.exited&&current.startTicks===value.startTicks;});
    record('final-drain',{code,remaining});if(remaining.length)code=1;
  }finally{app.exit(code);}
}
