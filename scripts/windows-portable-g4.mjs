import assert from 'node:assert/strict';
import { execFile, spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { mkdir, mkdtemp, readdir, stat, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { createServer } from 'node:net';
import { basename, join, resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { parseArgs, promisify } from 'node:util';
import { Cdp } from '../frontend/scripts/cdp.mjs';
import { shell, ui } from '../frontend/scripts/ui-query.mjs';
import { connectPage } from './gpu-preview-p3.mjs';
import { getProcessIdentity, getProcessTree, waitOwnedExit } from './process-identity.mjs';

const require = createRequire(import.meta.url);
const privateFs = require('../desktop/runtime/private-fs.cjs');
const run = promisify(execFile);
const root = resolve(import.meta.dirname, '..');
const { values } = parseArgs({ options: { zip: { type: 'string' }, exe: { type: 'string', default: 'MakoKaraGenerator.exe' } } });
assert.equal(process.platform, 'win32');
assert(values.zip && basename(values.exe) === values.exe);
const system = join(process.env.SystemRoot, 'System32');
const evidenceRoot = join(root, '.local', 'windows-portable-g4');
await mkdir(evidenceRoot, { recursive: true });
const evidence = await mkdtemp(join(evidenceRoot, 'g4-'));
const installation = join(evidence, 'Portable 安装 with spaces');
const localAppData = join(evidence, 'local-appdata');
const runtime = join(localAppData, 'makokara-generator');
const downloads = join(evidence, 'download');
for (const directory of [installation, localAppData, downloads]) await mkdir(directory);
const volume = privateFs.getWindowsVolumeInfo(installation);
assert.equal(volume.fileSystemName.toUpperCase(), 'NTFS', 'This smoke tests a real NTFS installation only');
await run(join(system, 'tar.exe'), ['-xf', resolve(values.zip), '-C', installation], { windowsHide: true });
const files = await readdir(installation, { recursive: true });
const candidates = files.filter((item) => basename(item) === values.exe);
assert.equal(candidates.length, 1, 'ZIP must carry exactly one requested executable');
const executable = join(installation, candidates[0]);
const appDirectory = resolve(executable, '..');
const packages = files.filter((item) => basename(item) === 'ffprobe.exe');
assert.equal(packages.length, 1, 'ZIP must carry its media tools');
const ffprobe = join(installation, packages[0]);
const ffmpeg = join(resolve(ffprobe, '..'), 'ffmpeg.exe');
assert(existsSync(ffmpeg));

const simulatedVolume = (target) => privateFs.isSamePath(target, installation) || privateFs.isInsideRoot(installation, target)
  ? { volumeRoot: installation, fileSystemFlags: 0, fileSystemName: 'exFAT' }
  : { volumeRoot: localAppData, fileSystemFlags: privateFs.FILE_PERSISTENT_ACLS, fileSystemName: 'NTFS' };
assert.throws(() => privateFs.assertPersistentAclVolume(installation, { getVolumeInfo: simulatedVolume }), /FILE_PERSISTENT_ACLS/);
const env = { ...process.env, LOCALAPPDATA: localAppData, PATH: system };
for (const key of Object.keys(env)) {
  if (/^(MAKO_|ELECTRON_|NODE_OPTIONS$)/i.test(key) || (/^path$/i.test(key) && key !== 'PATH')) delete env[key];
}
const chosenRuntime = privateFs.defaultRuntimeDataDir('makokara-generator', env);
assert.equal(chosenRuntime, runtime);
privateFs.assertPersistentAclVolume(chosenRuntime, { getVolumeInfo: simulatedVolume });

const rate = 16000;
const sampleCount = rate * 2;
const wave = Buffer.alloc(44 + sampleCount * 2);
wave.write('RIFF', 0); wave.writeUInt32LE(wave.length - 8, 4); wave.write('WAVEfmt ', 8);
wave.writeUInt32LE(16, 16); wave.writeUInt16LE(1, 20); wave.writeUInt16LE(1, 22);
wave.writeUInt32LE(rate, 24); wave.writeUInt32LE(rate * 2, 28); wave.writeUInt16LE(2, 32); wave.writeUInt16LE(16, 34);
wave.write('data', 36); wave.writeUInt32LE(sampleCount * 2, 40);
for (let index = 0; index < sampleCount; index++) wave.writeInt16LE(Math.round(8000 * Math.sin(2 * Math.PI * index * 440 / rate)), 44 + 2 * index);
const audio = join(evidence, 'tone.wav');
const lyrics = join(evidence, 'lyrics.lrc');
await writeFile(audio, wave);
await writeFile(lyrics, '[00:00.05]<00:00.05>P<00:00.40>4<00:00.80>\n[00:01.00]<00:01.00>G<00:01.40>4<00:01.90>\n');

const server = createServer();
await new Promise((done, reject) => server.once('error', reject).listen(0, '127.0.0.1', done));
const port = server.address().port;
await new Promise((done) => server.close(done));
const child = spawn(executable, [`--remote-debugging-port=${port}`, '--remote-debugging-address=127.0.0.1'], {
  cwd: appDirectory, env, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true,
});
const exit = new Promise((done, reject) => {
  child.once('error', reject);
  child.once('exit', (code) => done(code));
});
void exit.catch(() => {});
let backendExit;
const output = require('node:readline').createInterface({ input: child.stdout });
output.on('line', (line) => {
  const stopped = /^\[backend\] WEB_BACKEND_STOPPED (\{.*\})$/.exec(line);
  if (stopped) backendExit = JSON.parse(stopped[1]).exitCode;
});
child.stderr.resume();
const identity = await getProcessIdentity(child.pid);
let cdp, browserCdp, page;

async function quit() {
  if (child.exitCode !== null) return;
  if (browserCdp) void browserCdp.send('Browser.close').catch(() => {});
  else if (page) void page.eval('window.close()').catch(() => {});
}

try {
  ({ cdp, page } = await Promise.race([connectPage(port), exit.then((code) => { throw Error(`Portable exited early (${code})`); })]));
  const version = await fetch(`http://127.0.0.1:${port}/json/version`).then((response) => response.json());
  browserCdp = await Cdp.connect(version.webSocketDebuggerUrl);
  await browserCdp.send('Browser.setDownloadBehavior', { behavior: 'allow', downloadPath: downloads });
  await page.waitFor(shell.connected, 'Portable Server ready', 120_000);
  const initial = await page.eval('window.makoDesktopPreview.readPreviewState()');
  assert.equal(initial.mainPid, child.pid, 'CDP must connect to the extracted executable');
  const runs = join(runtime, 'profile', 'runs');
  const runDirectories = await readdir(runs);
  assert.equal(runDirectories.length, 1);
  assert(existsSync(join(runs, runDirectories[0], 'bootstrap.json')));
  assert(!existsSync(join(installation, 'makokara-generator')));
  await page.send('DOM.enable');
  await shell.newProject(page, { audio, lyrics, name: 'Portable G4' });
  await shell.goto(page, 'appearance');
  await page.waitFor(`(async () => {
    const state = await window.makoDesktopPreview.readPreviewState();
    return state.activeStream?.transport === 'shm' && state.activeStream?.api === 'vulkan' && state.drawn > 0;
  })()`, 'Renamed Electron host presents GPU SHM frames', 120_000);
  const preview = await page.eval('window.makoDesktopPreview.readPreviewState()');
  assert(preview.peakOutstanding <= 3);
  const owned = await getProcessTree(child.pid);
  assert(owned.some((entry) => entry.pid === child.pid && entry.startTicks === identity.startTicks));
  assert(owned.length > 2, 'Capture the backend and Electron children for exit checks');

  await shell.goto(page, '导出视频');
  await page.waitFor(`document.querySelector('[data-check="duration"]')?.dataset.level === 'block'`, 'Export page');
  await ui.click(page, 'button', '去设置', { scope: '[data-check="duration"]' });
  await page.waitFor(`!!document.querySelector('.project-settings:not([data-exiting])')`, 'Project settings');
  await ui.click(page, 'button', '按音频时长', { scope: '[data-group="duration"]' });
  await page.waitFor(`document.querySelector('[data-check="duration"]')?.dataset.level === 'ok'`, 'Duration set');
  await ui.click(page, 'button', '关闭工程设置');
  await ui.fill(page, '检查帧数', '10', { enter: true, scope: '[aria-label="正式验证"]' });
  await ui.click(page, 'button', '开始验证', { scope: '[aria-label="正式验证"]' });
  await page.waitFor(`document.querySelector('[aria-label="正式验证"] .job-result')?.textContent.includes('验证通过')`, 'Export validation', 180_000);
  const readiness = await page.eval(`Array.from(document.querySelectorAll('[data-check]'), ({ dataset }) => [dataset.check, dataset.level])`);
  assert.equal(Object.fromEntries(readiness).validation, 'ok');
  assert.deepEqual(readiness.filter(([, level]) => level === 'block'), [], 'Export readiness must have no blockers');
  await ui.click(page, 'button', '开始导出', { scope: '.export-go' });
  await page.waitFor(`!!document.querySelector('[aria-label="导出任务"] .job-result[data-state="succeeded"]')`, 'Portable export', 240_000);
  await ui.click(page, 'button', '下载', { scope: '[aria-label="导出任务"] .job-result' });
  let video;
  for (let attempt = 0; attempt < 120 && !video; attempt++) {
    video = (await readdir(downloads)).find((file) => file.endsWith('.mp4'));
    if (!video) await delay(250);
  }
  assert(video, 'Portable export must download an MP4');
  const path = join(downloads, video);
  const probe = await run(ffprobe, ['-v', 'error', '-count_frames', '-show_streams', '-of', 'json', path], { env, windowsHide: true });
  const streams = JSON.parse(probe.stdout).streams;
  const frames = Number(streams.find((stream) => stream.codec_name === 'h264')?.nb_read_frames);
  assert.equal(frames, 60);
  assert(streams.some((stream) => stream.codec_name === 'aac'));
  await run(ffmpeg, ['-v', 'error', '-xerror', '-i', path, '-f', 'null', '-'], { env, windowsHide: true });
  await ui.click(page, 'button', '返回编辑', { scope: '.app-bar' });
  await shell.closeProject(page);
  void page.eval('window.close()').catch(() => {});
  assert.equal(await Promise.race([exit, delay(45_000, 'timeout', { ref: false })]), 0);
  assert.equal(backendExit, 0);
  assert(!existsSync(join(runs, runDirectories[0], 'bootstrap.json')));
  for (const ownedIdentity of owned) assert(await waitOwnedExit(ownedIdentity), 'Captured process identity remained alive');
  const report = { executable: values.exe, fileSystem: 'NTFS', preview: 'GPU · SHM', drawn: preview.drawn, frames,
    mp4Bytes: (await stat(path)).size, fullyDecoded: true, backendExit, electronExit: 0, exitedIdentities: owned.length,
    credentialsRemoved: true, noAclBoundarySimulated: true, exFatVolumeTested: false };
  await writeFile(join(evidence, 'summary.json'), JSON.stringify(report, null, 2) + '\n');
  console.log(JSON.stringify(report));
} finally {
  await quit();
  if (child.exitCode === null && identity.startTicks && !await waitOwnedExit(identity, 5000)) {
    const current = await getProcessIdentity(identity.pid);
    if (current.startTicks === identity.startTicks) child.kill();
  }
  cdp?.ws.close();
  browserCdp?.ws.close();
}
