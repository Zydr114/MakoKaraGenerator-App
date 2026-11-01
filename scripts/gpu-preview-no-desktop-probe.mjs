import { spawn } from 'node:child_process';
import { mkdtemp, mkdir, readFile, writeFile } from 'node:fs/promises';
import { randomBytes } from 'node:crypto';
import { resolve, join } from 'node:path';

const dist = resolve(process.argv[2]);
const evidence = await mkdtemp(resolve('.local/no-desktop-http-'));
await mkdir(join(evidence, 'resources'));
await mkdir(join(evidence, 'state'));
const token = randomBytes(32).toString('hex');
const env = { ...process.env };
for (const key of Object.keys(env)) if (key.startsWith('MAKO_DESKTOP_') || key.startsWith('MAKO_BACKEND_')) delete env[key];
Object.assign(env, { MAKO_BACKEND_BOOTSTRAP_TOKEN: token, MAKO_BACKEND_RESOURCE_ROOT: join(evidence, 'resources'),
  MAKO_BACKEND_STATE_ROOT: join(evidence, 'state'), MAKO_BACKEND_WORKER_PATH: join(dist, 'worker/MakoKaraGenerator.Backend.Worker') });
const child = spawn(join(dist, 'server/MakoKaraGenerator.Backend.Server'), ['--urls', 'http://127.0.0.1:0'], { env });
let stdout = '', stderr = '';
child.stdout.on('data', chunk => { stdout += chunk; });
child.stderr.on('data', chunk => { stderr += chunk; });
const exit = new Promise(resolveExit => child.once('exit', (code, signal) => resolveExit({ code, signal })));
const observed = new Map();
async function observe(pid) {
  try {
    const stat = await readFile(`/proc/${pid}/stat`, 'utf8');
    const fields = stat.slice(stat.lastIndexOf(')') + 2).split(' ');
    observed.set(pid, fields[19]);
    const children = await readFile(`/proc/${pid}/task/${pid}/children`, 'utf8');
    for (const item of children.trim().split(/\s+/).filter(Boolean)) await observe(Number(item));
  } catch (error) { if (error.code !== 'ENOENT') throw error; }
}
const sampling = setInterval(() => { void observe(child.pid); }, 25);
let result;
try {
  let match;
  for (let i = 0; i < 300 && !(match = stdout.match(/BACKEND_READY (\S+) (\S+)/)); i++) {
    if (child.exitCode !== null) throw new Error('Server exited before readiness');
    await new Promise(resolveWait => setTimeout(resolveWait, 100));
  }
  if (!match) throw new Error('Server readiness timeout');
  const base = match[1];
  const registration = await fetch(base + '/api/v1/clients', { method: 'POST', headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' }, body: '{}' });
  if (registration.status !== 201) throw new Error(`registration: ${registration.status}`);
  const client = await registration.json();
  const headers = { authorization: `Bearer ${client.accessToken}`, 'x-mako-client-id': client.clientId, 'content-type': 'application/json' };
  const caps = await fetch(base + '/api/v1/capabilities', { headers });
  const capabilities = await caps.json();
  const publicRoute = await fetch(base + '/api/v1/gpu-preview-streams', { method: 'POST', headers, body: '{}' });
  const claim = await fetch(base + '/internal/gpu-preview-streams/test/claim', { method: 'POST', headers, body: '{}' });
  result = { productVersion: capabilities.productVersion, capabilityStatus: caps.status,
    gpuFeaturePresent: capabilities.features.includes('preview.gpu-streams'), gpuLimitsPresent: 'gpuPreview' in capabilities,
    publicRouteStatus: publicRoute.status, privateClaimStatus: claim.status };
  if (result.productVersion !== '0.109.0' || caps.status !== 200 || result.gpuFeaturePresent || result.gpuLimitsPresent ||
      publicRoute.status !== 404 || claim.status !== 404) throw new Error('Unexpected no-desktop HTTP behavior');
} finally {
  child.kill('SIGTERM');
  const ended = await Promise.race([exit, new Promise(resolveWait => setTimeout(() => resolveWait(null), 15000))]);
  clearInterval(sampling);
  if (!ended) { child.kill('SIGKILL'); await exit; throw new Error('Server did not drain'); }
  const processes = [];
  for (const [pid, startTicks] of observed) {
    let stillPresent = false;
    try {
      const stat = await readFile(`/proc/${pid}/stat`, 'utf8');
      stillPresent = stat.slice(stat.lastIndexOf(')') + 2).split(' ')[19] === startTicks;
    } catch (error) { if (error.code !== 'ENOENT') throw error; }
    processes.push({ pid, startTicks, stillPresent });
  }
  if (processes.some(process => process.stillPresent)) throw new Error('Owned process still exists');
  if (stdout.includes(token) || stderr.includes(token)) throw new Error('Credential appeared in server logs');
  await writeFile(join(evidence, 'result.json'), JSON.stringify({ dist, ...result, serverExit: ended, processes, credentialAbsentFromLogs: true }, null, 2) + '\n');
  console.log(JSON.stringify({ evidence, ...result, serverExit: ended }));
}
