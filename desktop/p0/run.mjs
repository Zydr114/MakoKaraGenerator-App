import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import electron from 'electron';
import { createHash } from 'node:crypto';

const root = path.resolve(fileURLToPath(new URL('../..', import.meta.url)));
const config = { mode: 'probe', scenario: 'normal', frames: 12, slots: 3, width: 1280, height: 720, slowMs: 180, deadlineMs: 30000, drawPlugin: '', gpuCopiesPerFrame: 0 };
const flags = new Set(['mode', 'scenario', 'frames', 'slots', 'width', 'height', 'slow-ms', 'deadline-ms', 'draw-plugin', 'gpu-copies-per-frame']);
for (let i = 2; i < process.argv.length; i += 2) {
  const key = process.argv[i]?.replace(/^--/, '');
  if (!flags.has(key) || !process.argv[i + 1]) throw new Error(`Unknown or incomplete option: ${process.argv[i]}`);
  const field = key.replace(/-([a-z])/g, (_, x) => x.toUpperCase());
  config[field] = typeof config[field] === 'number' ? Number(process.argv[i + 1]) : process.argv[i + 1];
}
if (!['probe', 'correctness', 'finite'].includes(config.mode)) throw new Error('mode must be probe, correctness or finite');
if (!['normal', 'slow', 'hidden', 'no-consumer', 'detach', 'close', 'renderer-crash', 'gpu-crash'].includes(config.scenario)) throw new Error('Invalid scenario');
for (const k of ['frames', 'slots', 'width', 'height', 'slowMs', 'deadlineMs']) {
  if (!Number.isSafeInteger(config[k]) || config[k] < 1) throw new Error(`Invalid ${k}`);
}
if (![1, 3].includes(config.slots) || config.frames > 300 || config.deadlineMs > 60000) throw new Error('P0 is finite: slots 1|3, frames <=300, deadline <=60000ms');
if (![0, 1].includes(config.gpuCopiesPerFrame)) throw new Error('gpu-copies-per-frame must explicitly be 0 or 1');
if (config.drawPlugin) {
  config.drawPlugin = fs.realpathSync(path.resolve(root, config.drawPlugin));
  if (!config.drawPlugin.startsWith(root + path.sep) || !fs.statSync(config.drawPlugin).isFile()) throw new Error('draw plugin must be a built file inside this App');
}
const runDir = path.join(root, '.local/gpu-preview-p0', new Date().toISOString().replace(/[:.]/g, '-') + '-' + process.pid);
fs.mkdirSync(runDir, { recursive: true, mode: 0o700 });
config.root = root;
config.runDir = runDir;
config.skiaColorSpace = process.env.MAKO_P0_SKIA_COLORSPACE ?? 'default-null';
config.artifactHashes = Object.fromEntries([
  electron,
  path.join(root, 'native/gpu-preview/build/gpu-preview-producer'),
  path.join(root, 'native/gpu-preview/build/gpu_preview_broker.node'),
  config.drawPlugin,
  ...(config.drawPlugin ? [path.join(path.dirname(config.drawPlugin),'libSkiaSharp.so')] : []),
].filter(file => file && fs.existsSync(file)).map(file => [path.relative(root,file),createHash('sha256').update(fs.readFileSync(file)).digest('hex')]));
fs.writeFileSync(path.join(runDir, 'config.json'), JSON.stringify(config, null, 2));
const child = spawn(electron, [path.join(root, 'desktop/p0/main.cjs'), '--p0-config', path.join(runDir, 'config.json')], {
  cwd: root,
  env: { ...process.env, ELECTRON_RUN_AS_NODE: undefined },
  stdio: ['ignore', 'pipe', 'pipe'],
});
const log = fs.createWriteStream(path.join(runDir, 'electron.log'));
child.stdout.on('data', data => { log.write(data); process.stdout.write(data); });
child.stderr.on('data', data => { log.write(data); process.stderr.write(data); });
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => child.kill(signal));
child.on('error', error => { console.error(error); log.end(); process.exitCode = 1; });
child.on('exit', async (code, signal) => {
  log.end();
  const resultPath = path.join(runDir, 'result.json');
  if (fs.existsSync(resultPath)) {
    const result = JSON.parse(fs.readFileSync(resultPath, 'utf8'));
    const identity = pid => {
      try { const stat=fs.readFileSync(`/proc/${pid}/stat`,'utf8'); const fields=stat.slice(stat.lastIndexOf(')')+2).split(' '); return { startTime:fields[19], state:fields[0] }; }
      catch { return null; }
    };
    let checks;
    const until = Date.now() + 3000;
    do {
      checks = (result.processes ?? []).map(item => {
        const current = identity(item.pid);
        return { pid:item.pid, type:item.type, original:item.identity, current, unknown:!item.identity, stillRunning:!!(current && item.identity && current.startTime === item.identity.startTime && current.state !== 'Z') };
      });
      if (!checks.some(x=>x.stillRunning)) break;
      await new Promise(resolve => setTimeout(resolve, 50));
    } while (Date.now() < until);
    fs.writeFileSync(path.join(runDir, 'process-drain.json'), JSON.stringify({ drained:checks.length > 0 && !checks.some(x=>x.stillRunning || x.unknown), unknown:checks.length === 0 || checks.some(x=>x.unknown), checks }, null, 2));
  }
  console.log(`P0 evidence: ${runDir}`);
  process.exitCode = code ?? (signal ? 1 : 0);
});
