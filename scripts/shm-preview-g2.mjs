import { spawn, execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const root = path.resolve(fileURLToPath(new URL('..', import.meta.url)));
const desktopRequire = createRequire(path.join(root, 'desktop/package.json'));
const electronBinary = desktopRequire('electron');

const requestedSuite = process.argv[2] ?? 'all';
const validSuites = ['all', 'correctness-cpu', 'correctness-vulkan', 'stress1000', 'resize30', 'slow-backpressure'];
if (!validSuites.includes(requestedSuite)) {
  throw new Error(`Usage: node scripts/shm-preview-g2.mjs [${validSuites.join('|')}]`);
}

const shmAddon = path.join(root, 'native/shm-preview/build/shm_bridge.node');
const workerBinary = path.join(root, 'backend/src/MakoKaraGenerator.Backend.Worker/bin/Debug/net10.0/MakoKaraGenerator.Backend.Worker');
if (!fs.existsSync(shmAddon)) {
  execFileSync('bash', ['native/shm-preview/build.sh'], { cwd: root, stdio: 'inherit' });
}
if (!fs.existsSync(workerBinary)) {
  throw new Error(`Missing built Worker binary at ${workerBinary}; run dotnet build first.`);
}

function runSuite(name, suite, backendApi) {
  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  const runDir = path.join(root, '.local/shm-preview-g2', `${stamp}-${name}-${process.pid}`);
  fs.mkdirSync(runDir, { recursive: true, mode: 0o700 });
  const cfg = {
    root,
    runDir,
    suite,
    backendApi,
    shmAddon,
    workerBinary,
  };
  const cfgPath = path.join(runDir, 'config.json');
  fs.writeFileSync(cfgPath, JSON.stringify(cfg, null, 2));

  return new Promise((resolve, reject) => {
    const child = spawn(electronBinary, [path.join(root, 'desktop/g2/main.cjs'), '--g2-config', cfgPath], {
      cwd: root,
      env: { ...process.env, ELECTRON_RUN_AS_NODE: undefined },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    const log = fs.createWriteStream(path.join(runDir, 'electron.log'));
    child.stdout.on('data', (d) => { log.write(d); process.stdout.write(d); });
    child.stderr.on('data', (d) => { log.write(d); process.stderr.write(d); });
    child.on('error', (err) => { log.end(); reject(err); });
    child.on('exit', (code, signal) => {
      log.end();
      const resultFile = path.join(runDir, 'result.json');
      const result = fs.existsSync(resultFile) ? JSON.parse(fs.readFileSync(resultFile, 'utf8')) : null;
      if (code !== 0 || result?.status !== 'passed') {
        reject(new Error(`G2 suite '${name}' failed (exit=${code ?? signal}): ${result?.error ?? 'no result.json'}`));
        return;
      }
      console.log(`[G2 PASS] ${name} -> ${runDir}`);
      resolve({ name, runDir, result });
    });
  });
}

const matrix = [
  ['correctness-cpu', 'correctness', 'cpu'],
  ['correctness-vulkan', 'correctness', 'vulkan'],
  ['resize30', 'resize30', 'vulkan'],
  ['slow-backpressure', 'slow-backpressure', 'vulkan'],
  ['stress1000', 'stress1000', 'vulkan'],
].filter(([name]) => requestedSuite === 'all' || requestedSuite === name);

const summary = [];
for (const [name, suite, api] of matrix) {
  summary.push(await runSuite(name, suite, api));
}

const summaryPath = path.join(root, '.local/shm-preview-g2/latest-summary.json');
fs.mkdirSync(path.dirname(summaryPath), { recursive: true, mode: 0o700 });
fs.writeFileSync(summaryPath, JSON.stringify({
  completedAt: new Date().toISOString(),
  suites: summary.map((s) => ({ name: s.name, status: s.result.status, device: s.result.device, results: s.result.results, metrics: s.result.metrics })),
}, null, 2));
console.log(`All requested G2 suites passed. Summary: ${summaryPath}`);
