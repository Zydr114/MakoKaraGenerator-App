// Finite real author Error acceptance, using an owned draft and existing production bridge.
// Start a dedicated --session-name author-error-* desktop; never attach to a user project.
import assert from 'node:assert/strict';
import {readFile,mkdir,writeFile,appendFile} from 'node:fs/promises';
import {resolve,join,dirname,sep} from 'node:path';
import {createHash,randomUUID} from 'node:crypto';
import {execFileSync} from 'node:child_process';
import {parseArgs} from 'node:util';
import {Cdp,Page} from '../frontend/scripts/cdp.mjs';

const {values}=parseArgs({options:{bootstrap:{type:'string'},desktop:{type:'string'},'cdp-port':{type:'string',default:'9244'},output:{type:'string'},prepare:{type:'boolean'}}});
const root=resolve(import.meta.dirname,'..'),output=resolve(values.output??join(root,'.local/gpu-preview-author-error',String(Date.now())));
assert.ok(output.startsWith(join(root,'.local')+sep));
await mkdir(output,{recursive:true,mode:0o700});
const source=`using MakoKaraGenerator.Scripting;
public sealed class AcceptanceAuthorError : EffectBase
{
    public override void Evaluate(FrameContext frame, FrameWriter output)
    {
        if (frame.TimeUs >= 1000000)
            frame.Diagnostics.Report(DiagnosticSeverity.Error, "AcceptanceAuthorError", "finite owned author Error", new SourceLocation("src/AcceptanceAuthorError.cs", 7, 13));
    }
}
`;
const sha256=bytes=>createHash('sha256').update(bytes).digest('hex');
const manifest={packageVersion:2,sdkVersion:'1.0.0',shaderProfile:'skia-raster-3.119.4-v1',
  packageId:'acceptance.author-error',name:'Owned author Error acceptance',version:'1.0.0',
  files:[{path:'src/AcceptanceAuthorError.cs',role:'csharp',sha256:sha256(source)}],parameters:[],
  exports:[{id:'author-error',kind:'controller',name:'Owned author Error',entryType:'AcceptanceAuthorError',parameters:[],bindingKinds:['tokens'],
    outputs:[{id:'declared',kind:'VisualTransform',target:'tokenFamily',requires:[],dependsOn:[]}]}]};
const archive=join(output,'author-error.mkfx');
await writeFile(join(output,'AcceptanceAuthorError.cs'),source);
await writeFile(join(output,'package-manifest.json'),JSON.stringify(manifest,null,2)+'\n');
execFileSync('python3',['-c','import json,sys,zipfile; x=json.load(sys.stdin); z=zipfile.ZipFile(x["path"],"w",zipfile.ZIP_DEFLATED); z.writestr("manifest.json",json.dumps(x["manifest"])); z.writestr("src/AcceptanceAuthorError.cs",x["source"]); z.close()'],
  {input:JSON.stringify({path:archive,manifest,source})});
if(values.prepare){console.log(`Prepared owned author Error fixture: ${archive}`);process.exit(0);}
assert.ok(values.bootstrap&&values.desktop,'Pass private bootstrap and desktop file paths');
const bootstrap=JSON.parse(await readFile(resolve(values.bootstrap),'utf8')),desktop=JSON.parse(await readFile(resolve(values.desktop),'utf8'));
assert.match(desktop.sessionName??'',/^author-error-/,'Use a dedicated author-error-* acceptance session');
const base=new URL(bootstrap.baseUrl);assert.equal(base.hostname,'127.0.0.1');assert.equal(desktop.hostInstanceId,bootstrap.hostInstanceId);
const report={schemaVersion:1,startedAt:new Date().toISOString(),probeSourceCommit:execFileSync('git',['rev-parse','HEAD'],{cwd:root,encoding:'utf8'}).trim(),
  sessionName:desktop.sessionName,scope:'real author Evaluate Error, exact production lease completion, stream drain and App survival; no timing or display claim',
  readbacks:'none',fixture:{sourceSha256:sha256(source),archiveSha256:sha256(await readFile(archive)),errorCode:'AcceptanceAuthorError',failureAtTimeUs:1000000},checks:[]};
const dist=dirname(dirname(desktop.brokerAddon)),deploymentBytes=await readFile(join(dist,'deployment.json'));
const deployment=JSON.parse(deploymentBytes);report.publishedDeployment={dist:dist.slice(root.length+1),sourceCommit:deployment.sourceCommit,sourceDirty:deployment.sourceDirty,manifestSha256:sha256(deploymentBytes)};
const metadata=()=>({requestId:randomUUID().replaceAll('-',''),hostInstanceId:bootstrap.hostInstanceId,validUntilUtc:new Date(Date.now()+90000).toISOString()});
let client,draft,stream,cdp,page;
const wait=ms=>new Promise(done=>setTimeout(done,ms));
async function request(method,path,body,extra={}){
  const response=await fetch(new URL(path,base),{method,headers:{...(client?{Authorization:`Bearer ${client.accessToken}`,'X-Mako-Client-Id':client.clientId,'X-Mako-Host-Instance-Id':bootstrap.hostInstanceId}:{}),
    ...(body===undefined?{}:{'Content-Type':body instanceof Uint8Array?'application/octet-stream':'application/json'}),...extra},
    ...(body===undefined?{}:{body:body instanceof Uint8Array?body:JSON.stringify(body)}),signal:AbortSignal.timeout(45000)});
  const data=await response.json();assert.ok(response.ok,`${method} ${path}: HTTP ${response.status} ${data.code??''}`);return data;
}
async function until(predicate,timeout=15000){const deadline=Date.now()+timeout;while(Date.now()<deadline){const value=await predicate();if(value)return value;await wait(25);}throw new Error('Owned author Error acceptance condition timed out');}
const processIdentity=async pid=>{try{const stat=await readFile(`/proc/${pid}/stat`,'utf8');return {pid,startTicks:stat.slice(stat.lastIndexOf(')')+2).split(' ')[19]};}catch(error){if(error.code==='ENOENT')return {pid,exited:true};throw error;}};
async function sample(){const state=await page.eval('window.makoDesktopPreview.readPreviewState()');await appendFile(join(output,'events.jsonl'),JSON.stringify({type:'snapshot',snapshot:state})+'\n');return state;}
async function playback(timeUs,reset){return page.eval(`window.makoDesktopPreview.setPlaybackState({mode:'paused',mediaTimeUs:${timeUs},rate:1,targetFps:24,timelineResetId:${JSON.stringify(reset)},samplePerformanceMs:performance.now()})`);}
async function closeStream(){if(!stream)return;await page.eval('window.makoDesktopPreview.detachPreview()');const closed=await request('DELETE',`/api/v1/gpu-preview-streams/${stream.streamId}`,{expectedStreamRevision:stream.streamRevision,metadata:metadata()});assert.equal(closed.registrationState,'closed');stream=null;}
try{
  const targets=await fetch(`http://127.0.0.1:${Number(values['cdp-port'])}/json/list`).then(r=>r.json());
  const target=targets.find(t=>t.type==='page'&&t.url.startsWith(desktop.pageUrl)&&new URL(t.url).origin===new URL(desktop.pageUrl).origin);assert.ok(target,'Dedicated desktop page identity mismatch');
  cdp=await Cdp.connect(target.webSocketDebuggerUrl);page=new Page(cdp);
  cdp.ws.addEventListener('close',()=>{for(const pending of cdp.pending.values())pending.reject(new Error('Owned CDP disconnected'));cdp.pending.clear();});
  const initial=await sample();assert.equal(initial.mainPid,desktop.mainPid);assert.equal(initial.activeStream,null,'Acceptance session must start without a user stream');
  report.ownedProcesses=await Promise.all([initial.mainPid,initial.rendererPid,bootstrap.pid].map(processIdentity));
  await page.eval(`(()=>{const canvas=document.createElement('canvas');canvas.id='mako-gpu-preview';document.body.appendChild(canvas);window.__authorErrorEvents=[];window.makoDesktopPreview.onPreviewState(event=>window.__authorErrorEvents.push(event));})()`);
  client=await request('POST','/api/v1/clients',{protocolVersion:'1'},{Authorization:`Bearer ${bootstrap.bootstrapToken}`});
  const uploadMeta=metadata();
  const asset=await request('POST','/api/v1/assets',await readFile(archive),{'X-Mako-Asset-Name':'author-error.mkfx',
    'X-Mako-Request-Id':uploadMeta.requestId,'X-Mako-Valid-Until-Utc':uploadMeta.validUntilUtc});
  draft=await request('POST','/api/v1/drafts',{packageAssetId:asset.assetId,exportId:'author-error',name:'Owned finite author Error',metadata:metadata()});
  report.draft={draftId:draft.draftId,draftRevision:draft.draftRevision,sourceRevision:draft.currentSourceRevision};
  const sourceDto={kind:'authorDraft',sourceId:draft.draftId,sourceRevision:draft.draftRevision,authorSourceRevision:draft.currentSourceRevision,
    templateSample:{sampleId:'workshop-single-line-v1',targetKey:'token',materialSlot:null}};
  const createStream=()=>request('POST','/api/v1/gpu-preview-streams',{source:sourceDto,size:{width:640,height:360},alphaMode:'opaque',api:'vulkan',consumerId:desktop.consumerId,metadata:metadata()});
  stream=await createStream();report.checks.push({name:'real-author-runtime-pool-opened',streamId:stream.streamId,allocationBytes:stream.allocationBytes});
  await page.eval(`window.makoDesktopPreview.attachPreview(${JSON.stringify(stream.streamId)},${JSON.stringify(stream.inputGeneration)})`);
  await playback(0,'author-error-before');
  const first=await until(async()=>{const s=await sample();return s.completedDrawnUnique>initial.completedDrawnUnique?s:false;});
  report.successfulBeforeError=first;assert.equal(first.samples.at(-1).provider,'runtime');
  await playback(1000000,'author-error-trigger');
  report.failureEvent=await until(async()=>{const events=await page.eval('window.__authorErrorEvents');return events.find(e=>e.type==='failed'&&e.code==='WorkerRuntimeDiagnosticError');});
  assert.ok(report.failureEvent.diagnostics.errorCount>=1);assert.ok(report.failureEvent.diagnostics.items.some(item=>item.code==='AcceptanceAuthorError'));
  report.afterFailure=await sample();assert.equal(report.afterFailure.outstanding,0);assert.equal(report.afterFailure.rendererGate.detached,true);
  const publicFailure=await request('GET',`/api/v1/gpu-preview-streams/${stream.streamId}`);
  report.publicFailure=publicFailure;assert.equal(publicFailure.workerState.state,'failed');assert.equal(publicFailure.workerState.drained,true);
  assert.equal(publicFailure.workerState.failureCode,'WorkerRuntimeDiagnosticError');assert.ok(publicFailure.workerState.diagnostics.errorCount>=1);
  report.checks.push({name:'real-author-error-drained-with-diagnostics',passed:true});
  await closeStream();
  // A fresh healthy time on the same authored source proves the App can continue using GPU preview.
  stream=await createStream();await page.eval(`window.makoDesktopPreview.attachPreview(${JSON.stringify(stream.streamId)},${JSON.stringify(stream.inputGeneration)})`);
  await playback(0,'author-error-recovered');
  report.recovered=await until(async()=>{const s=await sample();return s.completedDrawnUnique>first.completedDrawnUnique?s:false;});
  report.checks.push({name:'fresh-healthy-runtime-stream-after-error',passed:true,streamId:stream.streamId});
  report.survivingProcesses=await Promise.all(report.ownedProcesses.map(p=>processIdentity(p.pid)));
  assert.deepEqual(report.survivingProcesses,report.ownedProcesses);report.passed=true;
}catch(error){report.passed=false;report.failure={name:error.name,message:error.message};process.exitCode=1;}
finally{
  try{if(page&&stream)await closeStream();}catch(error){report.cleanupFailure=error.message;report.passed=false;process.exitCode=1;}
  try{if(draft)await request('DELETE',`/api/v1/drafts/${draft.draftId}`,{expectedDraftRevision:draft.draftRevision,metadata:metadata()});}catch(error){report.draftCleanupFailure=error.message;report.passed=false;process.exitCode=1;}
  if(page)report.events=await page.eval('window.__authorErrorEvents').catch(()=>[]);
  report.finishedAt=new Date().toISOString();await writeFile(join(output,'report.json'),JSON.stringify(report,null,2)+'\n');cdp?.ws.close();
  console.log(JSON.stringify({output,passed:report.passed,failure:report.failure}));
}
