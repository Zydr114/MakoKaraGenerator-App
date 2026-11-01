// Finite real HTTP guards on a matched App desktop deployment. This does not
// connect a native consumer, render frames, or establish presentation evidence.
import assert from 'node:assert/strict';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { randomUUID, createHash } from 'node:crypto';
import { resolve, join, sep, dirname } from 'node:path';
import { parseArgs } from 'node:util';
import { execFileSync } from 'node:child_process';

const root = resolve(import.meta.dirname, '..');
const { values } = parseArgs({ options: {
  bootstrap: { type: 'string' }, desktop: { type: 'string' }, output: { type: 'string' },
} });
if (!values.bootstrap || !values.desktop) throw new Error('Pass private --bootstrap and --desktop file paths; no credentials on the command line.');
const bootstrap = JSON.parse(await readFile(resolve(values.bootstrap), 'utf8'));
const desktop = JSON.parse(await readFile(resolve(values.desktop), 'utf8'));
const base = new URL(bootstrap.baseUrl);
assert.equal(base.hostname, '127.0.0.1');
assert.equal(desktop.hostInstanceId, bootstrap.hostInstanceId);
const output = resolve(values.output ?? join(root, '.local/gpu-preview-http-negative', new Date().toISOString().replaceAll(':','-')));
assert.ok(output.startsWith(join(root,'.local') + sep));
await mkdir(output, { recursive: true, mode: 0o700 });
const dist = dirname(dirname(desktop.brokerAddon));
assert.ok(dist.startsWith(join(root,'.local/backend-dist')+sep));
const deploymentBytes=await readFile(join(dist,'deployment.json'));
const nativeBytes=await readFile(join(dist,'gpu-preview-manifest.json'));
const deployment=JSON.parse(deploymentBytes),native=JSON.parse(nativeBytes);
const sha256=bytes=>createHash('sha256').update(bytes).digest('hex');
const sourceInputsHash=inputs=>sha256(JSON.stringify(Object.fromEntries(Object.entries(inputs).sort(([a],[b])=>a.localeCompare(b)))));
const publishedDeployment={dist:dist.slice(root.length+1),version:deployment.version,
  sourceCommit:deployment.sourceCommit,sourceDirty:deployment.sourceDirty,
  deploymentManifestSha256:sha256(deploymentBytes),nativeManifestSha256:sha256(nativeBytes),
  sourceInputsHashEncoding:'UTF-8 JSON object, keys sorted by localeCompare, no whitespace',
  sourceInputsSha256:sourceInputsHash(deployment.sourceInputs),sourceInputCount:Object.keys(deployment.sourceInputs).length,
  nativeSourceInputsSha256:sourceInputsHash(native.sourceInputs),nativeSourceInputCount:Object.keys(native.sourceInputs).length,
  nativeSourceInputs:native.sourceInputs,nativeArtifacts:native.files};
const checks = [];
const ownedWorkers = [];
const processIdentity = async pid => {
  try { const text=await readFile(`/proc/${pid}/stat`,'utf8'); const fields=text.slice(text.lastIndexOf(')')+2).split(' ');return {pid,startTicks:fields[19]}; } catch { return null; }
};
async function waitOwnedExit(identity) {
  const deadline=Date.now()+10_000;
  while(Date.now()<deadline) { const now=await processIdentity(identity.pid);if(!now||now.startTicks!==identity.startTicks)return true;await new Promise(done=>setTimeout(done,20)); }
  return false;
}
const metadata = () => ({ requestId: randomUUID().replaceAll('-',''), hostInstanceId: bootstrap.hostInstanceId,
  validUntilUtc: new Date(Date.now() + 90_000).toISOString() });
async function request(client, method, path, body, extra = {}) {
  const headers = { ...(client ? { Authorization: `Bearer ${client.accessToken}`, 'X-Mako-Client-Id': client.clientId,
    'X-Mako-Host-Instance-Id': bootstrap.hostInstanceId } : {}), ...(body === undefined ? {} : {'Content-Type':'application/json'}), ...extra };
  const response = await fetch(new URL(path,base), { method, headers,
    ...(body === undefined ? {} : { body: typeof body === 'string' ? body : JSON.stringify(body) }), signal: AbortSignal.timeout(45_000) });
  const text = await response.text();
  let data; try { data = text ? JSON.parse(text) : undefined; } catch { throw new Error(`HTTP ${response.status} did not return JSON`); }
  return { status: response.status, data };
}
function record(name, response, statuses, errorCode) {
  assert.ok(statuses.includes(response.status), `${name}: unexpected HTTP ${response.status}`);
  if (errorCode) assert.equal(response.data?.code, errorCode, `${name}: unexpected error code`);
  checks.push({ name, status: response.status, ...(response.status >= 400 ? {errorCode:response.data?.code} : {}) });
}
function noPrivateFields(data) {
  const text = JSON.stringify(data);
  assert.ok(!text.includes(desktop.consumerSecret), 'Public response exposed desktop secret');
  assert.ok(!/"(?:brokerEndpoint|brokerPath|handshake)"/.test(text), 'Public response exposed broker claim data');
}
async function register() {
  const response = await request(null,'POST','/api/v1/clients',{protocolVersion:'1'}, {Authorization:`Bearer ${bootstrap.bootstrapToken}`});
  assert.equal(response.status,201); return response.data;
}
let owner, other, project, stream;
let failure;
try {
  owner = await register(); other = await register();
  const created = await request(owner,'POST','/api/v1/project-sessions',{name:'GPU HTTP finite guards',metadata:metadata()});
  record('create-isolated-project',created,[201]); project=created.data;
  const source={kind:'project',sourceId:project.projectSessionId,sourceRevision:project.projectRevision};
  const create=()=>({source,size:{width:256,height:128},alphaMode:'straight',api:'vulkan',consumerId:desktop.consumerId,metadata:metadata()});
  record('foreign-source-create',await request(other,'POST','/api/v1/gpu-preview-streams',create()),[404], 'sessionNotFound');
  record('create-missing-required-fields',await request(owner,'POST','/api/v1/gpu-preview-streams',{source}),[400]);
  record('create-unknown-field',await request(owner,'POST','/api/v1/gpu-preview-streams',{...create(),unknown:true}),[400]);
  record('create-integer-enum',await request(owner,'POST','/api/v1/gpu-preview-streams',{...create(),alphaMode:1}),[400]);
  record('create-duplicate-field',await request(owner,'POST','/api/v1/gpu-preview-streams',`{"api":"vulkan",${JSON.stringify(create()).slice(1)}`),[400]);
  record('PNG-size-limit-remains-1280',await request(owner,'POST','/api/v1/preview-sessions',{source,size:{width:2560,height:1440},alphaMode:'straight',metadata:metadata()}),[400], 'invalidRequest');
  const exactCreate=create();
  const opened=await request(owner,'POST','/api/v1/gpu-preview-streams',exactCreate);
  record('real-worker-pool-open',opened,[201]); stream=opened.data; noPrivateFields(stream);
  assert.equal(stream.slotCount,3); assert.ok(stream.allocationBytes>0&&stream.allocationBytes<=64*1024**2);
  const path=`/api/v1/gpu-preview-streams/${stream.streamId}`;
  const replay=await request(owner,'POST','/api/v1/gpu-preview-streams',exactCreate);
  record('same-metadata-create-replay',replay,[201]);
  assert.deepEqual(replay.data,stream);
  checks.at(-1).streamIdUnchanged=true;checks.at(-1).producerEpochUnchanged=true;checks.at(-1).poolGenerationUnchanged=true;
  const firstClaimPath=`/internal/gpu-preview-streams/${stream.streamId}/claim`;
  const firstClaim=await request(null,'POST',firstClaimPath,{consumerId:desktop.consumerId,hostInstanceId:bootstrap.hostInstanceId,expectedInputGeneration:stream.inputGeneration},{'X-Mako-Desktop-Secret':desktop.consumerSecret});
  record('private-old-generation-worker-identity',firstClaim,[200]);
  const oldIdentity=await processIdentity(firstClaim.data.workerPid);assert.ok(oldIdentity);ownedWorkers.push(oldIdentity);
  checks.at(-1).workerPid=oldIdentity.pid;checks.at(-1).workerStartTicks=oldIdentity.startTicks;
  assert.equal(stream.producerEpoch,replay.data.producerEpoch);
  record('same-consumer-quota',await request(owner,'POST','/api/v1/gpu-preview-streams',create()),[429], 'sessionQuotaExceeded');
  record('foreign-stream-read',await request(other,'GET',path),[404], 'sessionNotFound');
  record('foreign-stream-resize',await request(other,'PUT',path+'/size',{expectedStreamRevision:stream.streamRevision,size:{width:256,height:128},metadata:metadata()}),[404], 'sessionNotFound');
  record('foreign-stream-close',await request(other,'DELETE',path,{expectedStreamRevision:stream.streamRevision,metadata:metadata()}),[404], 'sessionNotFound');
  record('stale-resize-CAS',await request(owner,'PUT',path+'/size',{expectedStreamRevision:'0',size:{width:512,height:256},metadata:metadata()}),[409], 'revisionConflict');
  record('stale-source-CAS',await request(owner,'PUT',path+'/source',{expectedStreamRevision:'0',source,metadata:metadata()}),[409], 'revisionConflict');
  record('stale-source-revision-preserves-old-generation',await request(owner,'PUT',path+'/source',{expectedStreamRevision:stream.streamRevision,source:{...source,sourceRevision:String(BigInt(source.sourceRevision)+1n)},metadata:metadata()}),[409], 'revisionConflict');
  record('stale-close-CAS',await request(owner,'DELETE',path,{expectedStreamRevision:'0',metadata:metadata()}),[409], 'revisionConflict');
  record('resize-unknown-field',await request(owner,'PUT',path+'/size',{expectedStreamRevision:stream.streamRevision,size:{width:256,height:128},unknown:true,metadata:metadata()}),[400]);
  record('resize-duplicate-nested-field',await request(owner,'PUT',path+'/size',`{"expectedStreamRevision":"${stream.streamRevision}","size":{"width":256,"width":512,"height":128},"metadata":${JSON.stringify(metadata())}}`),[400]);
  const unchanged=await request(owner,'GET',path); record('rejected-writes-preserve-generation',unchanged,[200]); noPrivateFields(unchanged.data);
  for(const key of ['streamRevision','inputGeneration','producerEpoch','poolGeneration'])assert.equal(unchanged.data[key],stream[key]);
  assert.equal(unchanged.data.size.width,256); assert.equal(unchanged.data.size.height,128);
  const beforeResize=stream;
  const resized=await request(owner,'PUT',path+'/size',{expectedStreamRevision:stream.streamRevision,size:{width:2560,height:1440},metadata:metadata()});
  record('real-1440p-resize-new-generation',resized,[200]);stream=resized.data;noPrivateFields(stream);
  assert.notEqual(stream.inputGeneration,beforeResize.inputGeneration);assert.notEqual(stream.producerEpoch,beforeResize.producerEpoch);
  assert.equal(stream.size.width,2560);assert.equal(stream.size.height,1440);assert.equal(stream.slotCount,3);assert.ok(stream.allocationBytes<=64*1024**2);
  assert.ok(await waitOwnedExit(oldIdentity),'Old owned Worker did not exit after resize');
  checks.at(-1).oldWorkerExited=true;checks.at(-1).allocationBytes=stream.allocationBytes;checks.at(-1).inputGeneration=stream.inputGeneration;
  const claimPath=`/internal/gpu-preview-streams/${stream.streamId}/claim`;
  const claim={consumerId:desktop.consumerId,hostInstanceId:bootstrap.hostInstanceId,expectedInputGeneration:stream.inputGeneration};
  const secret={'X-Mako-Desktop-Secret':desktop.consumerSecret};
  for(const [name,body,headers] of [
    ['claim-missing-secret',claim,{}],['claim-wrong-secret',claim,{'X-Mako-Desktop-Secret':'invalid'}],
    ['claim-wrong-consumer',{...claim,consumerId:'foreign'},secret],['claim-wrong-host',{...claim,hostInstanceId:'foreign'},secret],
    ['claim-stale-generation',{...claim,expectedInputGeneration:'0'},secret],
  ]){
    const denied=await request(null,'POST',claimPath,body,headers);record(name,denied,[401], 'unauthorizedClient');noPrivateFields(denied.data);
  }
  const claimed=await request(null,'POST',claimPath,claim,secret);record('private-claim-once',claimed,[200]);
  const newIdentity=await processIdentity(claimed.data.workerPid);assert.ok(newIdentity);ownedWorkers.push(newIdentity);
  checks.at(-1).workerPid=newIdentity.pid;checks.at(-1).workerStartTicks=newIdentity.startTicks;
  assert.equal(claimed.data.inputGeneration,stream.inputGeneration);assert.equal(claimed.data.poolGeneration,stream.poolGeneration);
  assert.equal(typeof claimed.data.brokerEndpoint,'string');assert.equal(typeof claimed.data.handshake,'string');
  const repeated=await request(null,'POST',claimPath,claim,secret);record('private-claim-replay',repeated,[401], 'unauthorizedClient');noPrivateFields(repeated.data);
  const closed=await request(owner,'DELETE',path,{expectedStreamRevision:stream.streamRevision,metadata:metadata()});
  record('close-unconnected-worker-pool',closed,[200]);assert.equal(closed.data.registrationState,'closed');stream=null;
  assert.ok(await waitOwnedExit(newIdentity),'Current owned Worker did not exit after close');checks.at(-1).workerExited=true;
} catch(error) { failure = error; }
finally {
  if(stream&&owner) {
    const response=await request(owner,'DELETE',`/api/v1/gpu-preview-streams/${stream.streamId}`,{expectedStreamRevision:stream.streamRevision,metadata:metadata()}).catch(()=>null);
    checks.push({name:'cleanup-stream',status:response?.status??null});
    if(response?.status!==200)failure??=new Error('Stream cleanup did not confirm closure');
  }
  if(project&&owner) {
    const closed=await request(owner,'POST',`/api/v1/project-sessions/${project.projectSessionId}/close`,{expectedRevision:project.projectRevision,policy:'discard',activeJobs:'cancelAndWait',metadata:metadata()}).catch(()=>null);
    checks.push({name:'cleanup-project-accepted',status:closed?.status??null});
    if(closed?.status===202) {
      const deadline=Date.now()+30_000;let terminal;
      do {
        const job=await request(owner,'GET',`/api/v1/jobs/${closed.data.jobId}`);
        if(['succeeded','failed','cancelled','unknown'].includes(job.data?.state)){terminal=job.data.state;break;}
        await new Promise(done=>setTimeout(done,20));
      }while(Date.now()<deadline);
      checks.push({name:'cleanup-project-terminal',state:terminal??'timeout'});
      if(terminal!=='succeeded')failure??=new Error('Project cleanup did not complete');
    } else failure??=new Error('Project cleanup was not accepted');
  }
  await writeFile(join(output,'result.json'),JSON.stringify({schemaVersion:1,publishedDeployment,probeSourceCommit:execFileSync('git',['rev-parse','HEAD'],{cwd:root,encoding:'utf8'}).trim(),
    completedAt:new Date().toISOString(),passed:!failure,scope:'HTTP guards and real unconnected Worker pool lifecycle; no frame/presentation claim',checks,ownedWorkers,
    ...(failure?{failure:{name:failure.name,message:failure.message}}:{})},null,2)+'\n');
}
if(failure)throw failure;
console.log(`PASS ${checks.length} finite HTTP guard/lifecycle checks; ${join(output,'result.json')}`);
