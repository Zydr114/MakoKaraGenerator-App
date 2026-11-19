// M3 Linux 侧连续预览门禁套件：覆盖多传输动态切换、Canvas key 隔离、
// SHM 30 次 720p↔1440p 连续切尺寸、显式 dma-buf 零静默降级、以及映射前 SIGKILL 清理。
// 凭证与私钥不写入任何报告文件；所有证据默认落在 .local/shm-preview-m3-linux/。
import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { existsSync } from 'node:fs';
import { mkdir, readdir, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { dirname, join, resolve, sep } from 'node:path';
import { parseArgs } from 'node:util';
import { inflateSync } from 'node:zlib';
import { connectPage } from './gpu-preview-p3.mjs';
import { shell, ui } from '../frontend/scripts/ui-query.mjs';

const require = createRequire(import.meta.url);
const { startBackendProcess } = require('../desktop/runtime/backend-host.cjs');

const ROOT = resolve(import.meta.dirname, '..');
const { values } = parseArgs({
  options: {
    case: { type: 'string', default: 'all' },
    output: { type: 'string' },
    port: { type: 'string', default: '5195' },
    'cdp-port': { type: 'string', default: '9255' },
  },
});

const VALID_CASES = ['switch', 'shm-resize30', 'dmabuf-no-fallback', 'premap-sigkill', 'all'];
if (!VALID_CASES.includes(values.case)) {
  throw new Error(`Expected --case ${VALID_CASES.join('|')}`);
}
const selectedCases = values.case === 'all'
  ? ['switch', 'shm-resize30', 'dmabuf-no-fallback', 'premap-sigkill']
  : [values.case];

const outDir = resolve(
  values.output ?? join(ROOT, '.local/shm-preview-m3-linux', `${values.case}-${new Date().toISOString().replaceAll(/[^0-9TZ]/g, '')}`),
);
assert.ok(outDir.startsWith(join(ROOT, '.local') + sep), 'Output must remain inside App .local/');
await mkdir(outDir, { recursive: true, mode: 0o700 });

const wait = (ms) => new Promise((done) => setTimeout(done, ms));
const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex');

async function scanTmpfsResidue() {
  const scanDir = async (dir) => {
    if (!dir || !existsSync(dir)) return [];
    try {
      const names = await readdir(dir);
      return names.filter((n) => n.startsWith('mkg-shm-')).map((n) => join(dir, n));
    } catch {
      return [];
    }
  };
  const devShm = await scanDir('/dev/shm');
  const xdgRuntime = process.env.XDG_RUNTIME_DIR && resolve(process.env.XDG_RUNTIME_DIR) !== '/dev/shm'
    ? await scanDir(process.env.XDG_RUNTIME_DIR)
    : [];
  return { devShm, xdgRuntime, total: devShm.length + xdgRuntime.length };
}

async function processIdentity(pid) {
  try {
    const text = await readFile(`/proc/${pid}/stat`, 'utf8');
    const fields = text.slice(text.lastIndexOf(')') + 2).split(' ');
    return { pid, startTicks: fields[19], state: fields[0] };
  } catch (error) {
    if (error.code === 'ENOENT') return null;
    throw error;
  }
}

async function waitOwnedExit(identity, timeoutMs = 15_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const now = await processIdentity(identity.pid);
    if (!now || now.startTicks !== identity.startTicks) return true;
    await wait(25);
  }
  return false;
}

async function procMemoryAndFds(pid) {
  const status = await readFile(`/proc/${pid}/status`, 'utf8');
  const readKiB = (key) => {
    const match = status.match(new RegExp(`^${key}:\\s+(\\d+)\\s+kB$`, 'm'));
    return match ? Number(match[1]) : null;
  };
  const fds = await readdir(`/proc/${pid}/fd`);
  return { pid, vmRssKiB: readKiB('VmRSS'), vmSizeKiB: readKiB('VmSize'), fdCount: fds.length };
}

function decodePng(buffer) {
  const sig = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
  if (!buffer.subarray(0, 8).equals(sig)) throw new Error('Invalid PNG signature');
  let offset = 8, width = 0, height = 0, colorType = 0;
  const idat = [];
  while (offset < buffer.length) {
    const length = buffer.readUInt32BE(offset);
    const type = buffer.toString('ascii', offset + 4, offset + 8);
    const data = buffer.subarray(offset + 8, offset + 8 + length);
    offset += 12 + length;
    if (type === 'IHDR') {
      width = data.readUInt32BE(0);
      height = data.readUInt32BE(4);
      if (data[8] !== 8) throw new Error(`Unsupported PNG bit depth ${data[8]}`);
      colorType = data[9];
    } else if (type === 'IDAT') {
      idat.push(data);
    } else if (type === 'IEND') {
      break;
    }
  }
  const bpp = colorType === 6 ? 4 : colorType === 2 ? 3 : 0;
  if (!bpp) throw new Error(`Unsupported PNG colorType ${colorType}`);
  const raw = inflateSync(Buffer.concat(idat));
  const stride = width * bpp;
  const pixels = Buffer.alloc(height * stride);
  const paeth = (a, b, c) => {
    const p = a + b - c, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c);
    return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
  };
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)];
    const rowIn = y * (stride + 1) + 1;
    const rowOut = y * stride;
    for (let x = 0; x < stride; x++) {
      const val = raw[rowIn + x];
      const a = x >= bpp ? pixels[rowOut + x - bpp] : 0;
      const b = y > 0 ? pixels[rowOut - stride + x] : 0;
      const c = x >= bpp && y > 0 ? pixels[rowOut - stride + x - bpp] : 0;
      let outVal = val;
      if (filter === 1) outVal = (val + a) & 0xff;
      else if (filter === 2) outVal = (val + b) & 0xff;
      else if (filter === 3) outVal = (val + ((a + b) >> 1)) & 0xff;
      else if (filter === 4) outVal = (val + paeth(a, b, c)) & 0xff;
      pixels[rowOut + x] = outVal;
    }
  }
  return { width, height, bpp, pixels };
}

async function probeCanvas(page) {
  const rect = await page.eval(`(() => {
    const c = document.querySelector('canvas#mako-gpu-preview');
    if (!c) return null;
    const r = c.getBoundingClientRect();
    return { width: c.width, height: c.height, x: r.x, y: r.y, cssW: r.width, cssH: r.height, transport: c.dataset.previewTransport, api: c.dataset.previewApi };
  })()`);
  if (!rect || rect.cssW < 4 || rect.cssH < 4) return null;
  const { data } = await page.send('Page.captureScreenshot', {
    format: 'png',
    clip: { x: rect.x, y: rect.y, width: rect.cssW, height: rect.cssH, scale: 1 },
  });
  const img = decodePng(Buffer.from(data, 'base64'));
  const colors = new Set();
  for (let i = 0; i < img.pixels.length; i += img.bpp * 37) {
    colors.add((img.pixels[i] << 16) | (img.pixels[i + 1] << 8) | img.pixels[i + 2]);
  }
  const at = (nx, ny) => {
    const x = Math.min(img.width - 1, Math.max(0, Math.floor(nx * img.width)));
    const y = Math.min(img.height - 1, Math.max(0, Math.floor(ny * img.height)));
    const idx = (y * img.width + x) * img.bpp;
    return [img.pixels[idx], img.pixels[idx + 1], img.pixels[idx + 2], img.bpp === 4 ? img.pixels[idx + 3] : 255];
  };
  return { width: rect.width, height: rect.height, transport: rect.transport, api: rect.api, colors: colors.size, corner: at(0.03, 0.03), center: at(0.5, 0.5) };
}

async function makeFixtures(dir) {
  const rate = 16000, seconds = 6, samples = rate * seconds;
  const header = Buffer.alloc(44), body = Buffer.alloc(samples * 2);
  header.write('RIFF'); header.writeUInt32LE(36 + body.length, 4); header.write('WAVEfmt ', 8); header.writeUInt32LE(16, 16); header.writeUInt16LE(1, 20); header.writeUInt16LE(1, 22);
  header.writeUInt32LE(rate, 24); header.writeUInt32LE(rate * 2, 28); header.writeUInt16LE(2, 32); header.writeUInt16LE(16, 34); header.write('data', 36); header.writeUInt32LE(body.length, 40);
  for (let i = 0; i < samples; i++) body.writeInt16LE(Math.round(Math.sin((2 * Math.PI * 440 * i) / rate) * 8000), i * 2);
  const audio = join(dir, 'm3-tone-6s.wav');
  const lyrics = join(dir, 'm3-lyrics.lrc');
  await writeFile(audio, Buffer.concat([header, body]));
  await writeFile(lyrics, '[00:00.00]M3 跨平台连续预览\n[00:02.00]Linux SHM 与 DMA-BUF\n[00:04.00]零静默降级验证\n');
  return { audio, lyrics };
}

const titleExpr = `(document.querySelector('.preview-panel .preview-title .pane-title-text')?.textContent ?? '')`;
const readPreviewSummary = (page) => page.eval(`(async () => {
  const s = await window.makoDesktopPreview.readPreviewState();
  const last = s.samples.at(-1);
  return {
    mainPid: s.mainPid,
    rendererPid: s.rendererPid,
    workerPid: s.workerPid,
    drawn: s.drawn,
    completed: s.completedDrawnUnique,
    outstanding: s.outstanding,
    peakOutstanding: s.peakOutstanding,
    previewDisabled: s.previewDisabled,
    previewIsolation: s.previewIsolation ?? null,
    active: s.activeStream ? {
      streamId: s.activeStream.streamId,
      inputGeneration: s.activeStream.inputGeneration,
      transport: s.activeStream.transport,
      api: s.activeStream.api,
      size: s.activeStream.size,
    } : null,
    last: last ? {
      frameSeq: last.frameSeq,
      width: last.width,
      height: last.height,
      transport: last.transport,
      api: last.api,
      provider: last.provider,
      timeUs: last.timeUs,
      counters: last.counters ?? null,
      timings: last.timings ?? null,
    } : null,
  };
})()`);

async function waitForStreamFrame(page, beforeCompleted, expectedPrefix, expectedTransport, expectedApi, timeoutMs = 45_000) {
  await page.waitFor(
    `document.querySelector('canvas#mako-gpu-preview') && ${titleExpr}.startsWith(${JSON.stringify(expectedPrefix)}) && ${titleExpr}.includes('修订 ')`,
    `等待 ${expectedPrefix} 出帧`,
    timeoutMs,
  );
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const state = await readPreviewSummary(page);
    if (
      state.active &&
      state.active.transport === expectedTransport &&
      state.active.api === expectedApi &&
      state.completed > beforeCompleted &&
      state.last?.transport === expectedTransport &&
      state.last?.api === expectedApi
    ) {
      return state;
    }
    await wait(120);
  }
  throw new Error(`等待 ${expectedPrefix} 帧完成超时`);
}

async function chooseDevice(page, label, partial = false) {
  await ui.click(page, 'button', '（渲染设备）', { partial: true, scope: '.app-bar' });
  await page.waitFor(`!!document.querySelector('.render-device-menu')`, '渲染设备菜单');
  await ui.click(page, 'menuitem', label, { partial, scope: '.render-device-menu', what: `渲染设备「${label}」` });
  await page.waitFor(`!document.querySelector('.render-device-menu')`, '渲染设备菜单关闭');
}

async function launchDesktopSession(sessionName, port, cdpPort, transportEnv) {
  const env = { ...process.env };
  if (transportEnv === undefined) {
    delete env.MAKO_PREVIEW_TRANSPORT;
  } else {
    env.MAKO_PREVIEW_TRANSPORT = transportEnv;
  }
  const launcher = spawn(
    process.execPath,
    [join(ROOT, 'scripts/app.mjs'), 'desktop', '--session-name', sessionName, '--port', String(port), '--cdp-port', String(cdpPort)],
    { cwd: ROOT, env, stdio: ['ignore', 'pipe', 'pipe'] },
  );
  let log = '';
  for (const s of [launcher.stdout, launcher.stderr]) s.on('data', (chunk) => { log += chunk; });
  const start = Date.now();
  while (!log.includes('[app] 桌面页面')) {
    if (launcher.exitCode !== null) {
      throw new Error(`桌面启动失败（exit=${launcher.exitCode}）：${log.slice(-2000)}`);
    }
    if (Date.now() - start > 45_000) {
      launcher.kill('SIGTERM');
      throw new Error(`桌面启动超时（${sessionName}）`);
    }
    await wait(120);
  }
  const { cdp, page } = await connectPage(cdpPort);
  const runtimeExceptions = [];
  cdp.on((msg) => {
    if (msg.method === 'Runtime.exceptionThrown') {
      runtimeExceptions.push(msg.params.exceptionDetails?.exception?.description ?? msg.params.exceptionDetails?.text ?? 'exception');
    }
  });
  await page.waitFor(shell.connected, '连接 Server', 30_000);

  async function closeAll() {
    try {
      if (await page.eval(shell.projectOpen).catch(() => false)) {
        await shell.closeProject(page).catch(() => {});
      }
    } catch {}
    try {
      await page.eval('window.close()').catch(() => {});
    } catch {}
    const exitCode = await Promise.race([
      new Promise((done) => (launcher.exitCode !== null ? done(launcher.exitCode) : launcher.once('exit', done))),
      wait(25_000).then(() => 'timeout'),
    ]);
    if (exitCode === 'timeout' && launcher.exitCode === null) {
      launcher.kill('SIGTERM');
      await new Promise((done) => launcher.once('exit', done));
    }
    cdp.ws.close();
    return { exitCode, log };
  }

  return { launcher, cdp, page, runtimeExceptions, closeAll };
}

async function runSwitchCase(fixtures, port, cdpPort) {
  const beforeTmpfs = await scanTmpfsResidue();
  assert.equal(beforeTmpfs.total, 0, `Start tmpfs not clean: ${JSON.stringify(beforeTmpfs)}`);
  const session = await launchDesktopSession('m3-switch', port, cdpPort, undefined);
  let closedInfo;
  try {
    const { page, runtimeExceptions } = session;
    if (await page.eval(shell.projectOpen)) await shell.closeProject(page);
    await shell.newProject(page, { ...fixtures, name: 'M3 动态切换验证' });
    await page.waitFor(`document.querySelector('.transport .play') && !document.querySelector('.transport .play').disabled`, '音频就绪', 30_000);

    const beforeInit = (await readPreviewSummary(page)).completed;
    await shell.goto(page, 'appearance');
    const gpuInitial = await waitForStreamFrame(page, beforeInit, '成片 · GPU · DMA-BUF · ', 'dma-buf', 'vulkan');
    assert.equal(gpuInitial.last.counters?.cpuPixelReadbacks, 0);
    const gpuProbe = await probeCanvas(page);
    assert.ok(gpuProbe && gpuProbe.colors > 2, 'GPU DMA-BUF canvas probe empty');
    await page.eval(`void (window.__m3GpuCanvasRef = document.querySelector('canvas#mako-gpu-preview'))`);

    // 切换至 CPU -> 必须切为 CPU · SHM 且 remount 全新 Canvas DOM 节点
    const beforeCpu = gpuInitial.completed;
    await chooseDevice(page, 'CPU');
    const cpuState = await waitForStreamFrame(page, beforeCpu, '成片 · CPU · SHM · ', 'shm', 'cpu');
    assert.equal(cpuState.last.counters?.cpuPixelReadbacks, 1);
    assert.ok(Number.isSafeInteger(cpuState.last.timings?.renderDurationUs) && cpuState.last.timings.renderDurationUs >= 0);
    assert.ok(Number.isSafeInteger(cpuState.last.timings?.portCaptureDurationUs) && cpuState.last.timings.portCaptureDurationUs >= 0);
    const cpuRemounted = await page.eval(`(() => {
      const c = document.querySelector('canvas#mako-gpu-preview');
      const changed = !!c && c !== window.__m3GpuCanvasRef;
      window.__m3CpuCanvasRef = c;
      return { changed, transport: c?.dataset?.previewTransport, api: c?.dataset?.previewApi };
    })()`);
    assert.equal(cpuRemounted.changed, true, '<canvas key={transport}> did not remount on dma-buf -> shm');
    assert.equal(cpuRemounted.transport, 'shm');
    assert.equal(cpuRemounted.api, 'cpu');

    // 编辑背景色，验证 CPU · SHM 流换源与真实呈现像素更新
    await page.waitFor(`!!window.makoGpuPreviewAcceptance`, '验收入口');
    const genBeforeEdit = cpuState.active.inputGeneration;
    await page.eval(`window.makoGpuPreviewAcceptance.setBackground(40, 170, 90, 255)`);
    let cpuAfterEditProbe = null;
    const editDeadline = Date.now() + 25_000;
    while (Date.now() < editDeadline) {
      const s = await readPreviewSummary(page);
      if (s.active?.inputGeneration !== genBeforeEdit) {
        const p = await probeCanvas(page);
        if (p && Math.abs(p.corner[0] - 40) <= 6 && Math.abs(p.corner[1] - 170) <= 6 && Math.abs(p.corner[2] - 90) <= 6) {
          cpuAfterEditProbe = p;
          break;
        }
      }
      await wait(120);
    }
    assert.ok(cpuAfterEditProbe, 'CPU · SHM canvas did not reflect edited green background');

    // CPU · SHM 播放 1.5s 验证连续出帧
    const beforePlay = (await readPreviewSummary(page)).completed;
    await ui.click(page, 'button', '播放', { scope: '.transport' });
    await wait(1500);
    const duringPlay = await readPreviewSummary(page);
    await ui.click(page, 'button', '暂停', { scope: '.transport' });
    const cpuPlayFrames = duringPlay.completed - beforePlay;
    assert.ok(cpuPlayFrames >= 10, `CPU SHM playback drew too few frames: ${cpuPlayFrames}`);
    assert.ok(duringPlay.peakOutstanding <= 3, `peakOutstanding exceeded 3: ${duringPlay.peakOutstanding}`);

    // 切回跟随后端默认 -> 恢复 GPU · DMA-BUF 并再次 remount Canvas DOM 节点
    const beforeBack = (await readPreviewSummary(page)).completed;
    await chooseDevice(page, '跟随后端默认', true);
    const gpuRecovered = await waitForStreamFrame(page, beforeBack, '成片 · GPU · DMA-BUF · ', 'dma-buf', 'vulkan');
    assert.equal(gpuRecovered.last.counters?.cpuPixelReadbacks, 0);
    const gpuRemounted = await page.eval(`(() => {
      const c = document.querySelector('canvas#mako-gpu-preview');
      return { changed: !!c && c !== window.__m3CpuCanvasRef, transport: c?.dataset?.previewTransport, api: c?.dataset?.previewApi };
    })()`);
    assert.equal(gpuRemounted.changed, true, '<canvas key={transport}> did not remount on shm -> dma-buf');
    const gpuRecoveredProbe = await probeCanvas(page);
    assert.ok(gpuRecoveredProbe && Math.abs(gpuRecoveredProbe.corner[0] - 40) <= 6 && Math.abs(gpuRecoveredProbe.corner[1] - 170) <= 6 && Math.abs(gpuRecoveredProbe.corner[2] - 90) <= 6,
      `Recovered GPU DMA-BUF corner mismatch: ${JSON.stringify(gpuRecoveredProbe)}`);
    assert.deepEqual(runtimeExceptions, [], `Runtime exceptions: ${runtimeExceptions.join('; ')}`);

    closedInfo = await session.closeAll();
    assert.equal(closedInfo.exitCode, 0, `Launcher exit code ${closedInfo.exitCode}`);
    const afterTmpfs = await scanTmpfsResidue();
    assert.equal(afterTmpfs.total, 0, `Tmpfs residue after switch: ${JSON.stringify(afterTmpfs)}`);

    return {
      passed: true,
      gpuInitial: { size: gpuInitial.active.size, colors: gpuProbe.colors, readbacks: gpuInitial.last.counters.cpuPixelReadbacks },
      cpuShm: {
        canvasRemounted: cpuRemounted.changed,
        timings: cpuState.last.timings,
        readbacks: cpuState.last.counters.cpuPixelReadbacks,
        editedCornerRgba: cpuAfterEditProbe.corner,
        playbackFramesIn1500ms: cpuPlayFrames,
        peakOutstanding: duringPlay.peakOutstanding,
      },
      gpuRecovered: {
        canvasRemounted: gpuRemounted.changed,
        readbacks: gpuRecovered.last.counters.cpuPixelReadbacks,
        cornerRgba: gpuRecoveredProbe.corner,
      },
      tmpfsResidueAfter: afterTmpfs,
      launcherExitCode: closedInfo.exitCode,
    };
  } catch (error) {
    if (!closedInfo) await session.closeAll().catch(() => {});
    throw error;
  }
}

async function runShmResize30Case(fixtures, port, cdpPort) {
  const beforeTmpfs = await scanTmpfsResidue();
  assert.equal(beforeTmpfs.total, 0, `Start tmpfs not clean: ${JSON.stringify(beforeTmpfs)}`);
  const session = await launchDesktopSession('m3-shm-resize30', port, cdpPort, 'shm');
  let closedInfo;
  try {
    const { page, runtimeExceptions } = session;
    if (await page.eval(shell.projectOpen)) await shell.closeProject(page);
    await shell.newProject(page, { ...fixtures, name: 'M3 SHM 30次切尺寸' });
    await page.waitFor(`document.querySelector('.transport .play') && !document.querySelector('.transport .play').disabled`, '音频就绪', 30_000);

    const beforeInit = (await readPreviewSummary(page)).completed;
    await shell.goto(page, 'appearance');
    const initial = await waitForStreamFrame(page, beforeInit, '成片 · GPU · SHM · ', 'shm', 'vulkan');
    assert.equal(initial.last.counters?.cpuPixelReadbacks, 1);
    await page.waitFor(`!!window.makoGpuPreviewAcceptance`, '验收入口');
    await page.eval(`void (window.__m3ShmCanvasRef = document.querySelector('canvas#mako-gpu-preview'))`);

    const doResizeAndWait = async (width, height, label) => {
      const before = (await readPreviewSummary(page)).completed;
      await page.eval(`window.makoGpuPreviewAcceptance.resize(${width}, ${height})`);
      const deadline = Date.now() + 25_000;
      while (Date.now() < deadline) {
        const s = await readPreviewSummary(page);
        if (s.previewDisabled) throw new Error(`Preview disabled during ${label}: ${JSON.stringify(s.previewIsolation)}`);
        if (s.completed > before && s.active?.size.width === width && s.active?.size.height === height && s.last?.width === width && s.last?.height === height) {
          return s;
        }
        await wait(40);
      }
      throw new Error(`Resize ${label} (${width}x${height}) timed out`);
    };

    // 2 次预热（1440p -> 720p），再采样稳态基线
    await doResizeAndWait(2560, 1440, 'warmup-1');
    const warmed = await doResizeAndWait(1280, 720, 'warmup-2');
    const baselineMain = await procMemoryAndFds(warmed.mainPid);
    const baselineRenderer = await procMemoryAndFds(warmed.rendererPid);

    let lastResizeState = warmed;
    for (let i = 1; i <= 30; i++) {
      const target = i % 2 === 1 ? { width: 2560, height: 1440 } : { width: 1280, height: 720 };
      lastResizeState = await doResizeAndWait(target.width, target.height, `resize-${i}`);
      assert.equal(lastResizeState.last.transport, 'shm');
      assert.equal(lastResizeState.last.api, 'vulkan');
      assert.ok(lastResizeState.peakOutstanding <= 3, `peakOutstanding > 3 at resize ${i}`);
    }

    const afterMain = await procMemoryAndFds(lastResizeState.mainPid);
    const afterRenderer = await procMemoryAndFds(lastResizeState.rendererPid);
    const sameCanvasAfter30 = await page.eval(`document.querySelector('canvas#mako-gpu-preview') === window.__m3ShmCanvasRef`);
    assert.equal(sameCanvasAfter30, true, 'SHM 30 resizes unexpectedly replaced canvas DOM node');

    const mainFdDelta = afterMain.fdCount - baselineMain.fdCount;
    const rendererFdDelta = afterRenderer.fdCount - baselineRenderer.fdCount;
    const mainRssDeltaKiB = afterMain.vmRssKiB - baselineMain.vmRssKiB;
    const rendererRssDeltaKiB = afterRenderer.vmRssKiB - baselineRenderer.vmRssKiB;
    assert.ok(mainFdDelta <= 8, `Main FD delta too high: ${mainFdDelta}`);
    assert.ok(rendererFdDelta <= 8, `Renderer FD delta too high: ${rendererFdDelta}`);
    assert.ok(mainRssDeltaKiB < 128 * 1024, `Main RSS delta too high: ${mainRssDeltaKiB} KiB`);
    assert.ok(rendererRssDeltaKiB < 128 * 1024, `Renderer RSS delta too high: ${rendererRssDeltaKiB} KiB`);

    // 同一 SHM 模式下切到 CPU · SHM，必须复用同一个 WebGL2 Canvas 节点且无报错
    const beforeCpu = lastResizeState.completed;
    await chooseDevice(page, 'CPU');
    const cpuState = await waitForStreamFrame(page, beforeCpu, '成片 · CPU · SHM · ', 'shm', 'cpu');
    const sameCanvasOnCpu = await page.eval(`document.querySelector('canvas#mako-gpu-preview') === window.__m3ShmCanvasRef`);
    assert.equal(sameCanvasOnCpu, true, 'GPU·SHM -> CPU·SHM should reuse same key="shm" canvas node');
    const cpuProbe = await probeCanvas(page);
    assert.ok(cpuProbe && cpuProbe.colors > 2, 'CPU·SHM canvas probe empty after 30 resizes');
    assert.deepEqual(runtimeExceptions, [], `Runtime exceptions: ${runtimeExceptions.join('; ')}`);

    closedInfo = await session.closeAll();
    assert.equal(closedInfo.exitCode, 0, `Launcher exit code ${closedInfo.exitCode}`);
    const afterTmpfs = await scanTmpfsResidue();
    assert.equal(afterTmpfs.total, 0, `Tmpfs residue after shm-resize30: ${JSON.stringify(afterTmpfs)}`);

    return {
      passed: true,
      resizeCount: 30,
      sameCanvasAcross30Resizes: sameCanvasAfter30,
      sameCanvasAcrossGpuToCpuShm: sameCanvasOnCpu,
      baselineMain,
      afterMain,
      mainFdDelta,
      mainRssDeltaKiB,
      baselineRenderer,
      afterRenderer,
      rendererFdDelta,
      rendererRssDeltaKiB,
      lastGpuTimings: lastResizeState.last.timings,
      cpuShmTimings: cpuState.last.timings,
      cpuProbeColors: cpuProbe.colors,
      tmpfsResidueAfter: afterTmpfs,
      launcherExitCode: closedInfo.exitCode,
    };
  } catch (error) {
    if (!closedInfo) await session.closeAll().catch(() => {});
    throw error;
  }
}

async function runDmaBufNoFallbackCase(fixtures, port, cdpPort) {
  const beforeTmpfs = await scanTmpfsResidue();
  assert.equal(beforeTmpfs.total, 0, `Start tmpfs not clean: ${JSON.stringify(beforeTmpfs)}`);
  const session = await launchDesktopSession('m3-dmabuf-nofb', port, cdpPort, 'dma-buf');
  let closedInfo;
  try {
    const { page, runtimeExceptions } = session;
    if (await page.eval(shell.projectOpen)) await shell.closeProject(page);
    await shell.newProject(page, { ...fixtures, name: 'M3 显式 DMA-BUF 零降级' });
    await page.waitFor(`document.querySelector('.transport .play') && !document.querySelector('.transport .play').disabled`, '音频就绪', 30_000);

    const beforeInit = (await readPreviewSummary(page)).completed;
    await shell.goto(page, 'appearance');
    const gpuInitial = await waitForStreamFrame(page, beforeInit, '成片 · GPU · DMA-BUF · ', 'dma-buf', 'vulkan');

    // 显式 MAKO_PREVIEW_TRANSPORT=dma-buf 下切至 CPU：禁止静默回退静态 <img> 或 SHM，必须明确显示 PreviewTransportUnsupported
    await chooseDevice(page, 'CPU');
    await page.waitFor(
      `(document.querySelector('.preview-panel')?.innerText ?? '').includes('PreviewTransportUnsupported')`,
      '显式 PreviewTransportUnsupported 报错',
      20_000,
    );
    const errorPanel = await page.eval(`(() => ({
      hasImgFallback: !!document.querySelector('.preview-frame img'),
      hasCanvas: !!document.querySelector('canvas#mako-gpu-preview'),
      errorText: document.querySelector('.preview-panel .notice-error')?.textContent ?? '',
    }))()`);
    assert.equal(errorPanel.hasImgFallback, false, 'Must NOT silently fall back to static <img> preview');
    assert.equal(errorPanel.hasCanvas, true, 'Must stay in continuous preview panel displaying explicit error');
    assert.ok(errorPanel.errorText.includes('PreviewTransportUnsupported'), `Unexpected error text: ${errorPanel.errorText}`);
    const tmpfsDuringError = await scanTmpfsResidue();
    assert.equal(tmpfsDuringError.total, 0, 'Must NOT create any SHM backing on dma-buf + cpu rejection');

    // 切回跟随后端默认 -> GPU · DMA-BUF 立即恢复正常
    const beforeRecover = (await readPreviewSummary(page)).completed;
    await chooseDevice(page, '跟随后端默认', true);
    const recovered = await waitForStreamFrame(page, beforeRecover, '成片 · GPU · DMA-BUF · ', 'dma-buf', 'vulkan');
    assert.equal(recovered.last.counters?.cpuPixelReadbacks, 0);
    assert.deepEqual(runtimeExceptions, [], `Runtime exceptions: ${runtimeExceptions.join('; ')}`);

    closedInfo = await session.closeAll();
    assert.equal(closedInfo.exitCode, 0, `Launcher exit code ${closedInfo.exitCode}`);
    const afterTmpfs = await scanTmpfsResidue();
    assert.equal(afterTmpfs.total, 0, `Tmpfs residue after dmabuf-no-fallback: ${JSON.stringify(afterTmpfs)}`);

    return {
      passed: true,
      initialTransport: gpuInitial.active.transport,
      cpuErrorPanel: errorPanel,
      tmpfsDuringError,
      recoveredTransport: recovered.active.transport,
      recoveredApi: recovered.active.api,
      tmpfsResidueAfter: afterTmpfs,
      launcherExitCode: closedInfo.exitCode,
    };
  } catch (error) {
    if (!closedInfo) await session.closeAll().catch(() => {});
    throw error;
  }
}

async function runPremapSigkillCase() {
  const beforeTmpfs = await scanTmpfsResidue();
  assert.equal(beforeTmpfs.total, 0, `Start tmpfs not clean: ${JSON.stringify(beforeTmpfs)}`);
  const current = JSON.parse(await readFile(join(ROOT, '.local/backend-current.json'), 'utf8'));
  const apiResults = [];

  for (const api of ['vulkan', 'cpu']) {
    const profileDir = join(outDir, `premap-profile-${api}`);
    await mkdir(profileDir, { recursive: true, mode: 0o700 });
    const consumerId = randomBytes(16).toString('hex');
    const consumerSecret = randomBytes(32).toString('hex');
    const backendLogs = [];
    const backend = await startBackendProcess({
      dist: current.dist,
      profile: profileDir,
      origins: ['http://127.0.0.1:5195'],
      env: {
        MAKO_DESKTOP_CONSUMER_ID: consumerId,
        MAKO_DESKTOP_CONSUMER_SECRET: consumerSecret,
        MAKO_DESKTOP_MAIN_PID: String(process.pid),
        MAKO_PREVIEW_TRANSPORT: 'shm',
      },
      log: (line) => { if (!line.includes('MAKO_PERFORMANCE_V1')) backendLogs.push(line); },
    });

    const base = new URL(backend.info.baseUrl);
    const hostInstanceId = backend.info.hostInstanceId;
    const metadata = () => ({
      requestId: randomUUID().replaceAll('-', ''),
      hostInstanceId,
      validUntilUtc: new Date(Date.now() + 90_000).toISOString(),
    });
    async function apiReq(client, method, path, body, extra = {}) {
      const headers = {
        ...(client ? {
          Authorization: `Bearer ${client.accessToken}`,
          'X-Mako-Client-Id': client.clientId,
          'X-Mako-Host-Instance-Id': hostInstanceId,
        } : {}),
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
        ...extra,
      };
      const res = await fetch(new URL(path, base), {
        method,
        headers,
        ...(body === undefined ? {} : { body: JSON.stringify(body) }),
        signal: AbortSignal.timeout(30_000),
      });
      const text = await res.text();
      return { status: res.status, data: text ? JSON.parse(text) : undefined };
    }

    let innerError = null;
    try {
      const reg = await apiReq(null, 'POST', '/api/v1/clients', { protocolVersion: '1' }, { Authorization: `Bearer ${backend.info.bootstrapToken}` });
      assert.equal(reg.status, 201);
      const client = reg.data;

      const projRes = await apiReq(client, 'POST', '/api/v1/project-sessions', { name: `M3 Pre-map SIGKILL (${api})`, metadata: metadata() });
      assert.equal(projRes.status, 201);
      const project = projRes.data;
      const source = { kind: 'project', sourceId: project.projectSessionId, sourceRevision: project.projectRevision };

      assert.equal((await scanTmpfsResidue()).total, 0, `Tmpfs dirty before ${api} pre-map SIGKILL`);
      const opened = await apiReq(client, 'POST', '/api/v1/gpu-preview-streams', {
        source,
        size: { width: 640, height: 360 },
        alphaMode: 'opaque',
        api,
        consumerId,
        metadata: metadata(),
      });
      assert.equal(opened.status, 201, `Create ${api} SHM stream failed: ${JSON.stringify(opened)}`);
      const stream = opened.data;
      assert.equal(stream.api, api);
      assert.equal(stream.transport, 'shm');

      // 消费者映射前，具名 SHM 文件必须已真实存在于 tmpfs 且权限为 0600
      const preKillTmpfs = await scanTmpfsResidue();
      assert.equal(preKillTmpfs.total, 1, `Expected exactly 1 unopened SHM file before kill (${api}): ${JSON.stringify(preKillTmpfs)}`);
      const shmPath = preKillTmpfs.devShm[0] ?? preKillTmpfs.xdgRuntime[0];
      const shmStat = await stat(shmPath);
      const shmModeOctal = (shmStat.mode & 0o777).toString(8).padStart(4, '0');
      assert.equal(shmModeOctal, '0600', `SHM file mode expected 0600, got ${shmModeOctal}`);

      // 仅调用私有 claim 获取精确 workerPid 与 brokerEndpoint，不调用 shm_bridge.openReadOnly！
      const claimed = await apiReq(
        null,
        'POST',
        `/internal/gpu-preview-streams/${stream.streamId}/claim`,
        { consumerId, hostInstanceId, expectedInputGeneration: stream.inputGeneration },
        { 'X-Mako-Desktop-Secret': consumerSecret },
      );
      assert.equal(claimed.status, 200);
      assert.equal(claimed.data.transport, 'shm');
      const brokerDir = dirname(claimed.data.brokerEndpoint);
      const workerIdentity = await processIdentity(claimed.data.workerPid);
      assert.ok(workerIdentity, `Owned worker ${claimed.data.workerPid} missing before SIGKILL`);

      // 向精确所属 Worker PID 发送 SIGKILL（模拟消费者尚未 mmap/unlink 前的强杀窗口）
      process.kill(workerIdentity.pid, 'SIGKILL');
      assert.equal(await waitOwnedExit(workerIdentity, 10_000), true, `Owned worker ${workerIdentity.pid} did not exit`);

      // 触发状态读取 -> MarkExited -> CleanupUnopenedShmIfDead() 必须立即回收未映射的具名 SHM
      const failedState = await apiReq(client, 'GET', `/api/v1/gpu-preview-streams/${stream.streamId}`);
      assert.equal(failedState.status, 200);
      assert.equal(failedState.data.registrationState, 'failed');
      assert.equal(failedState.data.failureCode, 'GpuWorkerExited');

      const afterReadTmpfs = await scanTmpfsResidue();
      assert.equal(afterReadTmpfs.total, 0, `Unopened SHM leaked after MarkExited (${api}): ${JSON.stringify(afterReadTmpfs)}`);

      // 可信桌面消费者按生产故障隔离协议上报 consumer-failed -> 确认 Worker 终止、清理 broker 目录且禁止重开配额
      const isolated = await apiReq(
        null,
        'POST',
        `/internal/gpu-preview-streams/${stream.streamId}/consumer-failed`,
        { consumerId, hostInstanceId, inputGeneration: stream.inputGeneration, producerEpoch: stream.producerEpoch },
        { 'X-Mako-Desktop-Secret': consumerSecret },
      );
      assert.equal(isolated.status, 200);
      assert.equal(isolated.data.terminated, true);
      assert.equal(existsSync(brokerDir), false, `Broker temp directory still exists after isolation: ${brokerDir}`);

      const closeProj = await apiReq(client, 'POST', `/api/v1/project-sessions/${project.projectSessionId}/close`, {
        expectedRevision: project.projectRevision,
        policy: 'discard',
        activeJobs: 'cancelAndWait',
        metadata: metadata(),
      });
      assert.equal(closeProj.status, 202);
      const jobDeadline = Date.now() + 20_000;
      while (Date.now() < jobDeadline) {
        const job = await apiReq(client, 'GET', `/api/v1/jobs/${closeProj.data.jobId}`);
        if (job.data?.state === 'succeeded') break;
        await wait(40);
      }

      apiResults.push({
        api,
        transport: stream.transport,
        shmModeOctal,
        workerPid: workerIdentity.pid,
        preKillShmCount: preKillTmpfs.total,
        registrationState: failedState.data.registrationState,
        failureCode: failedState.data.failureCode,
        postDetectTmpfsResidue: afterReadTmpfs.total,
        consumerIsolationTerminated: isolated.data.terminated,
        brokerDirRemovedAfterIsolation: !existsSync(brokerDir),
      });
    } catch (error) {
      innerError = error;
    } finally {
      const exitCode = await backend.stop();
      await writeFile(join(outDir, `premap-server-${api}.log`), backendLogs.join('\n') + '\n').catch(() => {});
      await rm(profileDir, { recursive: true, force: true }).catch(() => {});
      if (!innerError) {
        assert.equal(exitCode, 0, `Standalone backend (${api}) did not exit 0: ${exitCode} (tail: ${backendLogs.slice(-8).join(' | ')})`);
        apiResults.at(-1).backendExitCode = exitCode;
      }
    }
    if (innerError) throw innerError;
  }

  const afterTmpfs = await scanTmpfsResidue();
  assert.equal(afterTmpfs.total, 0, `Tmpfs residue after premap-sigkill: ${JSON.stringify(afterTmpfs)}`);
  return { passed: true, cases: apiResults, tmpfsResidueAfter: afterTmpfs };
}

const currentDeployment = JSON.parse(await readFile(join(ROOT, '.local/backend-current.json'), 'utf8'));
const deploymentBytes = await readFile(join(currentDeployment.dist, 'deployment.json'));
const deploymentMeta = JSON.parse(deploymentBytes);
const nativeBytes = await readFile(join(currentDeployment.dist, 'gpu-preview-manifest.json'));
const nativeMeta = JSON.parse(nativeBytes);

const report = {
  schemaVersion: 1,
  startedAt: new Date().toISOString(),
  probeSourceCommit: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: ROOT, encoding: 'utf8' }).trim(),
  publishedDeployment: {
    dist: currentDeployment.dist.slice(ROOT.length + 1),
    version: deploymentMeta.version,
    sourceCommit: deploymentMeta.sourceCommit,
    sourceDirty: deploymentMeta.sourceDirty,
    deploymentManifestSha256: sha256(deploymentBytes),
    nativeManifestSha256: sha256(nativeBytes),
    nativeArtifacts: nativeMeta.files,
  },
  scope: 'M3 Linux-side continuous preview gate (GPU·DMA-BUF, GPU·SHM, CPU·SHM, Canvas isolation, 30x resize, zero-fallback, pre-map SIGKILL). Windows M2/M3/M4 remain deferred to M4.',
  cases: {},
  passed: false,
};

const fixtures = await makeFixtures(outDir);
const basePort = Number(values.port);
const baseCdpPort = Number(values['cdp-port']);

try {
  for (let idx = 0; idx < selectedCases.length; idx++) {
    const name = selectedCases[idx];
    const port = basePort + idx;
    const cdpPort = baseCdpPort + idx;
    console.log(`[m3-linux] running case: ${name}`);
    if (name === 'switch') report.cases[name] = await runSwitchCase(fixtures, port, cdpPort);
    else if (name === 'shm-resize30') report.cases[name] = await runShmResize30Case(fixtures, port, cdpPort);
    else if (name === 'dmabuf-no-fallback') report.cases[name] = await runDmaBufNoFallbackCase(fixtures, port, cdpPort);
    else if (name === 'premap-sigkill') report.cases[name] = await runPremapSigkillCase();
    console.log(`[m3-linux] PASS ${name}`);
  }
  report.passed = true;
} catch (error) {
  report.passed = false;
  report.failure = { name: error.name, message: error.message, stack: error.stack };
  process.exitCode = 1;
} finally {
  report.finishedAt = new Date().toISOString();
  await writeFile(join(outDir, 'report.json'), JSON.stringify(report, null, 2) + '\n');
  console.log(JSON.stringify({ output: join(outDir, 'report.json'), passed: report.passed, failure: report.failure?.message ?? null }));
}
