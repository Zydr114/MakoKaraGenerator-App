// Validation-only real editor/GPU background checks. No throughput claims.
// Owns one named isolated App session; never attaches to an existing user session.
import assert from 'node:assert/strict';
import {readFile,writeFile,mkdir} from 'node:fs/promises';
import {spawn,execFileSync} from 'node:child_process';
import {join,resolve,sep,dirname} from 'node:path';
import {randomUUID,createHash} from 'node:crypto';
import {parseArgs} from 'node:util';
import {connectPage,createProject} from './gpu-preview-p3.mjs';
import {shell,ui} from '../frontend/scripts/ui-query.mjs';

const ROOT=resolve(import.meta.dirname,'..');
const {values}=parseArgs({options:{output:{type:'string'},prepare:{type:'boolean'},port:{type:'string',default:'5191'},'cdp-port':{type:'string',default:'9241'}}});
const output=resolve(values.output??join(ROOT,'.local/gpu-preview-background',String(Date.now())));
assert.ok(output.startsWith(join(ROOT,'.local')+sep));
await mkdir(output,{recursive:true,mode:0o700});
const sha=bytes=>createHash('sha256').update(bytes).digest('hex');
const wait=ms=>new Promise(done=>setTimeout(done,ms));
const report={schemaVersion:1,startedAt:new Date().toISOString(),purpose:'finite real editor background correctness; validation-only CPU PNG and GPU canvas pixel readbacks; no performance claim',
  effectFixture:{inputSource:'BackgroundMedia',output:'line image graph shader samples background rgb within original coverage',counterfactual:'same workshop scene and graph package with no binding'},sourceCommit:execFileSync('git',['rev-parse','HEAD'],{cwd:ROOT,encoding:'utf8'}).trim(),checks:[],cases:[]};

// Deterministic four-quadrant PNG, 4s audio, and red->blue 2s video.
const image=join(output,'quadrants.png'),video=join(output,'two-colors.mov'),audio=join(output,'silence.wav'),lyrics=join(output,'lyrics.lrc');
execFileSync('python3',['-c',`import struct,zlib,sys
w,h=320,180
def chunk(t,b):return struct.pack('>I',len(b))+t+b+struct.pack('>I',zlib.crc32(t+b))
colors=[(240,30,50,255),(20,210,60,255),(30,60,230,255),(235,190,25,255)]
raw=b''.join(b'\\x00'+b''.join(bytes(colors[(y>=h//2)*2+(x>=w//2)]) for x in range(w)) for y in range(h))
open(sys.argv[1],'wb').write(b'\\x89PNG\\r\\n\\x1a\\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',w,h,8,6,0,0,0))+chunk(b'IDAT',zlib.compress(raw))+chunk(b'IEND',b''))`,image]);
execFileSync('ffmpeg',['-v','error','-y','-f','lavfi','-i','color=c=red:s=320x180:r=10:d=1','-f','lavfi','-i','color=c=blue:s=320x180:r=10:d=1','-filter_complex','[0:v][1:v]concat=n=2:v=1:a=0[v]','-map','[v]','-an','-c:v','mpeg4','-q:v','2',video]);
const header=Buffer.alloc(44),bytes=4*8000*2;header.write('RIFF');header.writeUInt32LE(36+bytes,4);header.write('WAVEfmt ',8);header.writeUInt32LE(16,16);header.writeUInt16LE(1,20);header.writeUInt16LE(1,22);header.writeUInt32LE(8000,24);header.writeUInt32LE(16000,28);header.writeUInt16LE(2,32);header.writeUInt16LE(16,34);header.write('data',36);header.writeUInt32LE(bytes,40);
await writeFile(audio,Buffer.concat([header,Buffer.alloc(bytes)]));await writeFile(lyrics,'[00:00.00]GPU background validation\n[00:03.00]Finite owned fixture\n');

// Same graph/controller semantics as SDK v2 GraphController fixture.
const effectSource=`using System.Collections.Generic;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;
public sealed class PackEffect : IPureKaraokeEffect {
 public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
 public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) {
  var binding = new GraphBindingValue(frame.Source.Resources.GetGraph("tint"),
   new ParameterValues(new[] { KeyValuePair.Create<string, ParameterValue>("amount", new ParameterValue.Number(0.5)) }),
   GraphUniformValues.Empty, GraphTransformValues.Empty);
  foreach (var line in frame.Presentations.LinesInBinding)
   output.BindGraph(line.Target, binding);
 }
}`;
async function packageArchive(name,enabled){
  const capture={includeRuby:true,includeStroke:true,includeShadow:true,includeDescendantGroups:true,includeOwnedOverlay:false,region:'fillAndStroke'};
  const graph={graphVersion:1,id:'probe',inputs:[{id:'original',source:'TargetColor',type:'color',availability:'required',backgroundRequirement:'none',space:'canvas',capture},
    {id:'background',source:'BackgroundMedia',type:'color',availability:'required',backgroundRequirement:'media',space:'canvas'}],parameters:['amount'],
    nodes:[{id:'probe',op:'RuntimeShader',program:'shaders/probe.program.json',children:{original:'input:original',background:'input:background'},uniforms:{amount:'parameter:amount'},outputType:'color',bounds:{expandPx:0,maxExpandPx:0,maxSampleOffsetPx:0},edge:'transparent'}],
    output:'node:probe',anchorMode:'ColorOnly',bounds:{space:'canvas',source:'targetRegion',maxExpandPx:0},composite:{mode:'sourceOver'},bypassInput:'input:original'};
  const files={'src/PackEffect.cs':enabled?effectSource:effectSource.replace(/foreach[\s\S]*?\);/,'// no binding: same workshop scene counterfactual'),
    'graphs/probe.graph.json':JSON.stringify(graph),'shaders/probe.program.json':JSON.stringify({programVersion:1,source:'shaders/probe.sksl',profile:'skia-raster-3.119.4-v1',uniforms:[{name:'amount',type:'float'}],children:[{name:'original',type:'color'},{name:'background',type:'color'}]}),
    'shaders/probe.sksl':'uniform float amount; uniform shader original; uniform shader background; half4 main(float2 p) { half4 c=original.eval(p); half4 b=background.eval(p); return half4(b.rgb*c.a,c.a); }'};
  const manifest={packageVersion:3,sdkVersion:'2.0.0',shaderProfile:'skia-ganesh-3.119.4-v2',packageId:'acceptance.background-'+name,name:'Owned background '+name,version:'2.0.0',
    files:Object.entries(files).map(([path,content])=>({path,role:path.endsWith('.cs')?'csharp':path.endsWith('.graph.json')?'graph':path.endsWith('.program.json')?'shaderProgram':'shader',sha256:sha(content)})),
    parameters:[{id:'amount',type:'number',default:.5,min:0,max:1,label:'Amount'}],
    requires:['image.graph'],lineExit:{durationUs:0},emitters:[],
    exports:[{id:'controller',kind:'controller',name:'Controller',entryType:'PackEffect',parameters:['amount'],bindingKinds:['project','branch','lines','tokens','clips','node'],outputs:[{id:'color',kind:'ImageGraph',target:'line',requires:[],dependsOn:[]}]},
    {id:'tint',kind:'graph',name:'Tint',graph:'graphs/probe.graph.json',parameters:['amount'],bindingKinds:['project','branch','lines','tokens','clips','node'],outputs:[{id:'color',kind:'ImageGraph',target:'line',requires:[],dependsOn:[],bypass:{kind:'graphInput',input:'input:original',mapping:'identity'}}]}]};
  const path=join(output,name+'.mkfx');
  execFileSync('python3',['-c','import json,sys,zipfile; x=json.load(sys.stdin); z=zipfile.ZipFile(x["path"],"w",zipfile.ZIP_DEFLATED); z.writestr("manifest.json",json.dumps(x["manifest"])); [z.writestr(k,v) for k,v in x["files"].items()]; z.close()'],{input:JSON.stringify({path,manifest,files})});
  return path;
}
const effectArchive=await packageArchive('effect',true),baselineArchive=await packageArchive('baseline',false);
report.fixtures={};for(const path of [image,video,audio,lyrics,effectArchive,baselineArchive])report.fixtures[path.slice(output.length+1)]={sha256:sha(await readFile(path))};
await writeFile(join(output,'fixture-manifest.json'),JSON.stringify(report.fixtures,null,2)+'\n');
if(values.prepare){console.log(JSON.stringify({output,prepared:true}));process.exit(0);}

let launcher,page,cdp,bootstrap,desktop,manualStream,client,heartbeat,draftIds=[];let launcherLog='';const owned=new Map();
async function identity(pid){try{const text=await readFile(`/proc/${pid}/stat`,'utf8');return {pid,startTicks:text.slice(text.lastIndexOf(')')+2).split(' ')[19]};}catch(error){if(error.code==='ENOENT')return null;throw error;}}
async function remember(pid){if(pid&&!owned.has(pid)){const x=await identity(pid);if(x)owned.set(pid,x);}}
async function until(fn,label,timeout=30000){const end=Date.now()+timeout;while(Date.now()<end){const x=await fn();if(x)return x;await wait(50);}throw new Error('Timed out: '+label);}
async function setFile(selector,path){const {root}=await page.send('DOM.getDocument');const {nodeId}=await page.send('DOM.querySelector',{nodeId:root.nodeId,selector});assert.ok(nodeId,selector);await page.send('DOM.setFileInputFiles',{nodeId,files:[path]});}
// 顶栏“工程设置”打开右侧面板；关闭后等面板退场。
async function openSettings(){await ui.click(page,'button','工程设置',{scope:'.app-bar'});await page.waitFor(`!!document.querySelector('.project-settings:not([data-exiting])')`,'project settings open');}
async function closeSettings(){await ui.click(page,'button','关闭工程设置');await page.waitFor(`!document.querySelector('.project-settings')`,'project settings closed');}
async function sample(){const s=await page.eval('window.makoDesktopPreview.readPreviewState()');for(const pid of [s.mainPid,s.rendererPid,s.workerPid,s.device?.chromiumGpuPid])await remember(pid);return s;}
async function rgbaCanvas(){return Buffer.from(await page.eval(`(()=>{const c=document.querySelector('#mako-gpu-preview');const b=c.getContext('2d').getImageData(0,0,c.width,c.height).data;let s='';for(let i=0;i<b.length;i+=8192)s+=String.fromCharCode(...b.subarray(i,i+8192));return btoa(s);})()`),'base64');}
function pixel(bytes,w,x,y){return [...bytes.subarray((y*w+x)*4,(y*w+x)*4+4)];}
function compare(a,b){assert.equal(a.length,b.length);let sum=0,max=0,changed=0;for(let i=0;i<a.length;i++){const d=Math.abs(a[i]-b[i]);sum+=d;max=Math.max(max,d);if(d>8)changed++;}return {mae:sum/a.length,max,channelsOver8:changed,ratioChannelsOver8:changed/a.length};}
function corners(bytes,w,h){return [[.1,.1],[.9,.1],[.1,.9],[.9,.9]].map(([x,y])=>pixel(bytes,w,Math.floor(x*w),Math.floor(y*h)));}
const metadata=()=>({requestId:randomUUID().replaceAll('-',''),hostInstanceId:bootstrap.hostInstanceId,validUntilUtc:new Date(Date.now()+90000).toISOString()});
async function nodeRequest(method,path,body,extra={}){
 const response=await fetch(new URL(path,bootstrap.baseUrl),{method,headers:{...(client?{Authorization:`Bearer ${client.accessToken}`,'X-Mako-Client-Id':client.clientId,'X-Mako-Host-Instance-Id':bootstrap.hostInstanceId}:{}),...(body===undefined?{}:{'Content-Type':body instanceof Uint8Array?'application/octet-stream':'application/json'}),...extra},...(body===undefined?{}:{body:body instanceof Uint8Array?body:JSON.stringify(body)}),signal:AbortSignal.timeout(60000)});
 const data=await response.json();assert.ok(response.ok,`${method} ${path} HTTP ${response.status} ${data.code??''} ${data.failureCode??''}`);return data;
}
async function uiRequest(method,path,body){return page.eval(`window.__backgroundApi(${JSON.stringify(method)},${JSON.stringify(path)},${JSON.stringify(body)})`);}
async function cpuFrame(source,size,alpha,timeUs,api=uiRequest){
 const p=await api('POST','/api/v1/preview-sessions',{source,size,alphaMode:alpha,renderBackend:'cpu',...(source.kind==='authorDraft'?{previewKind:'authorDraft',committable:false}:{}),metadata:metadata()});
 try{
  await api('POST',`/api/v1/preview-sessions/${p.previewId}/frame-requests`,{expectedSourceRevision:source.sourceRevision,previewGeneration:p.previewGeneration,requestSeq:'1',timeUs:String(timeUs),intent:'playback',metadata:metadata()});
  const state=await until(async()=>{const x=await api('GET',`/api/v1/preview-sessions/${p.previewId}`);if(x.failure)throw new Error('CPU preview failure '+x.failure.code);return x.session.latestFrame?x:false;},'CPU frame',60000);
  let encoded;
  if(api===uiRequest)encoded=await page.eval(`window.__backgroundBinary(${JSON.stringify(state.session.latestFrame.content.contentUrl)})`);
  else {const r=await fetch(new URL(state.session.latestFrame.content.contentUrl,bootstrap.baseUrl),{headers:{Authorization:`Bearer ${client.accessToken}`,'X-Mako-Client-Id':client.clientId,'X-Mako-Host-Instance-Id':bootstrap.hostInstanceId}});assert.ok(r.ok);encoded=Buffer.from(await r.arrayBuffer()).toString('base64');}
  const rgba=await page.eval(`(async()=>{const raw=Uint8Array.from(atob(${JSON.stringify(encoded)}),x=>x.charCodeAt(0));const im=await createImageBitmap(new Blob([raw],{type:'image/png'}));const c=new OffscreenCanvas(im.width,im.height);const ctx=c.getContext('2d');ctx.drawImage(im,0,0);const b=ctx.getImageData(0,0,c.width,c.height).data;let s='';for(let i=0;i<b.length;i+=8192)s+=String.fromCharCode(...b.subarray(i,i+8192));im.close();return btoa(s);})()`);
  return {bytes:Buffer.from(rgba,'base64'),frame:state.session.latestFrame,png:Buffer.from(encoded,'base64')};
 }finally{await api('POST',`/api/v1/preview-sessions/${p.previewId}/close`,{expectedPreviewGeneration:p.previewGeneration,expectedSourceRevision:source.sourceRevision,metadata:metadata()});}
}
async function playback(timeUs,reset){await page.eval(`window.makoDesktopPreview.setPlaybackState({mode:'paused',mediaTimeUs:${timeUs},rate:1,targetFps:24,timelineResetId:${JSON.stringify(reset)},samplePerformanceMs:performance.now()})`);}
async function exactFrame(stream,timeUs,reset){await until(async()=>{const s=await sample();return s.activeStream?.streamId===stream.streamId&&s.activeStream?.inputGeneration===stream.inputGeneration&&s.attachedMonotonicNs?s:false;},'matching attachment',60000);await playback(timeUs,reset);return until(async()=>{const s=await sample();const f=s.samples?.findLast(x=>x.streamId===stream.streamId&&x.inputGeneration===stream.inputGeneration&&x.timeUs===timeUs&&x.timelineResetId===reset&&x.drawn&&x.completedMonotonicNs);return f?{snapshot:s,frame:f}:false;},'exact native completion',60000);}
async function writeCase(name,gpu,cpu,size,details={}){
 const diff=compare(gpu,cpu.bytes);assert.ok(diff.mae<4,`${name}: CPU/GPU mean pixel difference ${diff.mae}`);
 await writeFile(join(output,name+'-gpu.rgba'),gpu);await writeFile(join(output,name+'-cpu.png'),cpu.png);
 const item={name,size,pixelDiff:diff,gpuCorners:corners(gpu,size.width,size.height),cpuCorners:corners(cpu.bytes,size.width,size.height),gpuSha256:sha(gpu),cpuPngSha256:sha(cpu.png),...details};report.cases.push(item);return item;
}
try{
 const name='background-acceptance-'+Date.now();report.sessionName=name;
 launcher=spawn(process.execPath,[join(ROOT,'scripts/app.mjs'),'desktop','--session-name',name,'--ordinary-scheduling','--port',values.port,'--cdp-port',values['cdp-port']],{cwd:ROOT,stdio:['ignore','pipe','pipe'],env:{...process.env,MAKO_BACKEND_GPU_PREVIEW_BENCHMARK:'0'}});
 launcher.stdout.on('data',x=>{launcherLog+=x;});launcher.stderr.on('data',x=>{launcherLog+=x;});launcher.on('exit',(code,signal)=>{report.launcherExit={code,signal};});
 await remember(launcher.pid);
 const bootPath=join(ROOT,'.local/run',`desktop-backend-${launcher.pid}.json`),desktopPath=join(ROOT,'.local/run',`desktop-${launcher.pid}.json`);
 await until(async()=>{try{bootstrap=JSON.parse(await readFile(bootPath,'utf8'));desktop=JSON.parse(await readFile(desktopPath,'utf8'));return desktop.ready;}catch{return false;}},'isolated desktop ready',90000);
 assert.equal(desktop.sessionName,name);assert.equal(new URL(bootstrap.baseUrl).hostname,'127.0.0.1');assert.equal(desktop.hostInstanceId,bootstrap.hostInstanceId);await remember(bootstrap.pid);await remember(desktop.mainPid);
 const dist=dirname(dirname(desktop.brokerAddon));report.deployment={dist:dist.slice(ROOT.length+1),manifestSha256:sha(await readFile(join(dist,'deployment.json')))};
 report.sourceHashes={};for(const p of ['frontend/dist/index.html','frontend/src/editor/preview/gpu-preview-controller.ts','desktop/main.cjs','desktop/gpu-preview/preload.cjs','scripts/gpu-preview-background-acceptance.mjs'])report.sourceHashes[p]=sha(await readFile(join(ROOT,p)));
 const html=await readFile(join(ROOT,'frontend/dist/index.html'),'utf8');for(const asset of html.matchAll(/(?:src|href)="(\/assets\/[^"]+)"/g)){const p='frontend/dist'+asset[1];report.sourceHashes[p]=sha(await readFile(join(ROOT,p)));}
 ({cdp,page}=await connectPage(Number(values['cdp-port'])));
 await page.waitFor(`location.origin===${JSON.stringify(desktop.pageUrl)}&&document.readyState==='complete'`,'owned page navigation complete');const target=await page.eval('location.origin');assert.equal(target,desktop.pageUrl);const initial=await sample();assert.equal(initial.mainPid,desktop.mainPid);assert.equal(initial.activeStream,null,'fresh isolated session must have no stream');
 await page.eval(`(()=>{const original=window.fetch.bind(window);window.__backgroundRequests=[];window.__backgroundAuth=null;window.fetch=async(input,init)=>{const url=new URL(input instanceof Request?input.url:String(input),location.href);const h=new Headers(init?.headers);if(h.has('X-Mako-Client-Id'))window.__backgroundAuth={base:url.origin,headers:Object.fromEntries(h)};const res=await original(input,init);if(url.pathname==='/api/v1/gpu-preview-streams'&&init?.method==='POST'){const body=JSON.parse(init.body);window.__backgroundRequests.push({alphaMode:body.alphaMode,source:body.source,size:body.size,response:await res.clone().json()});}return res;};window.__backgroundApi=async(method,path,body)=>{const a=window.__backgroundAuth;if(!a)throw Error('UI auth not captured');const h={...a.headers};delete h['content-type'];if(body!==undefined)h['Content-Type']='application/json';const r=await original(new URL(path,a.base),{method,headers:h,...(body===undefined?{}:{body:JSON.stringify(body)})});const d=await r.json();if(!r.ok)throw Error(method+' '+path+' HTTP '+r.status+' '+d.code);return d;};window.__backgroundBinary=async(path)=>{const a=window.__backgroundAuth;const r=await original(new URL(path,a.base),{headers:a.headers});if(!r.ok)throw Error('binary HTTP '+r.status);const b=new Uint8Array(await r.arrayBuffer());let s='';for(let i=0;i<b.length;i+=8192)s+=String.fromCharCode(...b.subarray(i,i+8192));return btoa(s);};})()`);
 await createProject(page,{audio,lyrics});
 await page.waitFor('window.makoGpuPreviewAcceptance?.current()?.alphaMode===\'opaque\'','production controller opaque');
 let stream=await page.eval('window.makoGpuPreviewAcceptance.current()');assert.equal(stream.alphaMode,'opaque');
 const creates=await page.eval('window.__backgroundRequests');assert.ok(creates.length&&creates.every(x=>x.alphaMode==='opaque'));report.checks.push({name:'actual-production-controller-create-opaque',requests:creates.map(x=>({alphaMode:x.alphaMode,source:x.source,size:x.size}))});
 await page.eval('window.makoGpuPreviewAcceptance.resize(320,180)');
 await openSettings();await page.waitFor(`!!document.querySelector('input[aria-label="选择图片或视频…"]')`,'background settings input');
 const canChoose=await page.eval(`(()=>{const i=document.querySelector('input[aria-label="选择图片或视频…"]');return !i.nextElementSibling.disabled;})()`);assert.equal(canChoose,true);report.checks.push({name:'real-background-settings-enabled',passed:true});
 await setFile('input[aria-label="选择图片或视频…"]',image);
 await page.waitFor(`!!document.querySelector('img.background-thumb')&&!!document.querySelector('input[aria-label="替换…"]')`,'image upload applied',180000);
 await closeSettings();
 stream=await until(async()=>{const x=await page.eval('window.makoGpuPreviewAcceptance.current()');return x&&x.source.sourceRevision!==stream.source.sourceRevision?x:false;},'image source generation');
 const imageDone=await exactFrame(stream,250000,'image-check');const imagePixels=await rgbaCanvas();const imageCpu=await cpuFrame(stream.source,stream.size,'opaque',250000);
 const imageCase=await writeCase('image-opaque-ui',imagePixels,imageCpu,stream.size,{nativeFrame:imageDone.frame,alphaMode:stream.alphaMode});
 const expected=[[240,30,50,255],[20,210,60,255],[30,60,230,255],[235,190,25,255]];imageCase.gpuCorners.forEach((p,i)=>p.forEach((v,c)=>assert.ok(Math.abs(v-expected[i][c])<=3,'image quadrant missing')));
 await openSettings();await setFile('input[aria-label="替换…"]',video);await page.waitFor(`document.querySelector('[data-group="background"] .background-info')?.textContent.includes('视频')`,'video upload applied',180000);await closeSettings();
 const imageRevision=stream.source.sourceRevision;stream=await until(async()=>{const x=await page.eval('window.makoGpuPreviewAcceptance.current()');return x&&x.source.sourceRevision!==imageRevision?x:false;},'video source generation');
 const videoPixels=[];
 for(const [timeUs,color] of [[250000,'red'],[1250000,'blue']]){const done=await exactFrame(stream,timeUs,'video-'+timeUs);const gpu=await rgbaCanvas();const cpu=await cpuFrame(stream.source,stream.size,'opaque',timeUs);const item=await writeCase('video-'+color+'-ui',gpu,cpu,stream.size,{nativeFrame:done.frame,alphaMode:stream.alphaMode});const corner=item.gpuCorners[0];assert.ok(color==='red'?corner[0]>220&&corner[2]<20:corner[2]>220&&corner[0]<20,'video expected time/color missing');videoPixels.push(gpu);}
 assert.ok(compare(...videoPixels).mae>50,'two video times must change actual pixels');report.checks.push({name:'video-exact-two-times-change',passed:true});
 // Leave the normal editor page before controlled manual streams, closing its controller.
 await shell.goto(page,'home');await until(async()=>!(await sample()).activeStream,'UI stream detached');await wait(250);
 await page.eval(`(()=>{const c=document.createElement('canvas');c.id='mako-gpu-preview';c.width=320;c.height=180;c.style.cssText='position:fixed;left:10px;top:10px;width:640px;height:360px;z-index:9999';document.body.appendChild(c);})()`);
 client=await nodeRequest('POST','/api/v1/clients',{protocolVersion:'1'},{Authorization:`Bearer ${bootstrap.bootstrapToken}`});heartbeat=setInterval(()=>{void nodeRequest('POST',`/api/v1/clients/${client.clientId}/heartbeat`,{}).catch(()=>{});},20000);
 // Transparent negative control on the SAME project video source/size/time.
 // UI client owns source; create using its auth, never borrow another client's project.
 manualStream=await uiRequest('POST','/api/v1/gpu-preview-streams',{source:stream.source,size:stream.size,alphaMode:'straight',api:'vulkan',consumerId:desktop.consumerId,metadata:metadata()});
 await page.eval(`window.makoDesktopPreview.attachPreview(${JSON.stringify(manualStream.streamId)},${JSON.stringify(manualStream.inputGeneration)})`);let done=await exactFrame(manualStream,250000,'video-transparent');let gpu=await rgbaCanvas();let cpu=await cpuFrame(stream.source,stream.size,'straight',250000);let item=await writeCase('video-transparent-control',gpu,cpu,stream.size,{nativeFrame:done.frame,alphaMode:'straight'});assert.ok(item.gpuCorners.every(p=>p[3]===0),'transparent video must omit media');assert.ok(compare(gpu,videoPixels[0]).mae>50,'old straight must fail normal background expectation');
 await page.eval('window.makoDesktopPreview.detachPreview()');await uiRequest('DELETE',`/api/v1/gpu-preview-streams/${manualStream.streamId}`,{expectedStreamRevision:manualStream.streamRevision,metadata:metadata()});manualStream=null;
 const effectResults={};
 for(const [kind,archive] of [['effect',effectArchive],['baseline',baselineArchive]]){
  const meta=metadata();const asset=await nodeRequest('POST','/api/v1/assets',await readFile(archive),{'X-Mako-Asset-Name':kind+'.mkfx','X-Mako-Request-Id':meta.requestId,'X-Mako-Valid-Until-Utc':meta.validUntilUtc});
  const draft=await nodeRequest('POST','/api/v1/drafts',{packageAssetId:asset.assetId,exportId:'controller',name:'Owned background '+kind,metadata:metadata()});draftIds.push(draft);
  const source={kind:'authorDraft',sourceId:draft.draftId,sourceRevision:draft.draftRevision,authorSourceRevision:draft.currentSourceRevision,templateSample:{sampleId:'workshop-single-line-v1',targetKey:'line',materialSlot:null}};
  for(const alpha of kind==='effect'?['opaque','straight']:['opaque']){
   const size={width:320,height:180};manualStream=await nodeRequest('POST','/api/v1/gpu-preview-streams',{source,size,alphaMode:alpha,api:'vulkan',consumerId:desktop.consumerId,metadata:metadata()});
   await page.eval(`window.makoDesktopPreview.attachPreview(${JSON.stringify(manualStream.streamId)},${JSON.stringify(manualStream.inputGeneration)})`);done=await exactFrame(manualStream,1000000,'effect-'+kind+'-'+alpha);gpu=await rgbaCanvas();cpu=await cpuFrame(source,size,alpha,1000000,nodeRequest);item=await writeCase('background-'+kind+'-'+alpha,gpu,cpu,size,{nativeFrame:done.frame,alphaMode:alpha,diagnostics:done.snapshot.workerState?.diagnostics});
   const authoritative=await until(async()=>{const s=await sample(),w=s.workerState;return w?.streamId===manualStream.streamId&&w?.inputGeneration===manualStream.inputGeneration&&w?.diagnostics&&BigInt(w.mainObservedMonotonicNs)>BigInt(done.frame.completedMonotonicNs)?s:false;},'fresh authoritative effect diagnostics');const summary=authoritative.workerState.diagnostics;item.diagnostics=summary;
   if(alpha==='straight'){assert.ok(item.gpuCorners.every(p=>p[3]===0));assert.ok(summary?.items?.some(x=>x.code==='TransparentBackgroundDependency'),'background graph transparent fallback diagnosis missing');}
   else assert.ok(!summary?.items?.some(x=>x.code==='TransparentBackgroundDependency'),'opaque background graph unexpectedly bypassed');
   effectResults[kind+'-'+alpha]=gpu;
   await page.eval('window.makoDesktopPreview.detachPreview()');await nodeRequest('DELETE',`/api/v1/gpu-preview-streams/${manualStream.streamId}`,{expectedStreamRevision:manualStream.streamRevision,metadata:metadata()});manualStream=null;
  }
 }
 const contribution=compare(effectResults['effect-opaque'],effectResults['baseline-opaque']);assert.ok(contribution.channelsOver8>100,'effect must make a visible pixel contribution relative to same scene no-binding');report.checks.push({name:'background-dependency-actual-visible-contribution',diff:contribution,passed:true});
 report.passed=true;

}catch(error){report.passed=false;report.failure={name:error.name,message:error.message};process.exitCode=1;}
finally{
 clearInterval(heartbeat);
 try{if(page&&manualStream){await page.eval('window.makoDesktopPreview.detachPreview()');/* Host shutdown owns final cleanup if API owner is ambiguous after a failure. */}}catch(error){report.detachCleanupFailure=error.message;}
 for(const draft of draftIds)try{await nodeRequest('DELETE',`/api/v1/drafts/${draft.draftId}`,{expectedDraftRevision:draft.draftRevision,metadata:metadata()});}catch(error){report.draftCleanupFailure=error.message;}
 cdp?.ws.close();
 if(launcher&&launcher.exitCode===null){try{const original=owned.get(launcher.pid),now=await identity(launcher.pid);assert.deepEqual(now,original,'Owned launcher identity changed');launcher.kill('SIGINT');await until(()=>launcher.exitCode!==null||launcher.signalCode!==null,'owned launcher exit',30000);}catch(error){report.cleanupFailure=error.message;report.passed=false;process.exitCode=1;}}
 report.ownedProcesses=[];for(const x of owned.values()){const current=await identity(x.pid);report.ownedProcesses.push({...x,sameIdentityStillPresent:!!current&&current.startTicks===x.startTicks});}
 if(report.ownedProcesses.some(x=>x.sameIdentityStillPresent)){report.passed=false;report.cleanupFailure='An observed owned process is still present';process.exitCode=1;}
 await writeFile(join(output,'owned-launcher.log'),launcherLog);
 report.finishedAt=new Date().toISOString();await writeFile(join(output,'report.json'),JSON.stringify(report,null,2)+'\n');console.log(JSON.stringify({output,passed:report.passed,failure:report.failure,caseCount:report.cases.length}));
}
