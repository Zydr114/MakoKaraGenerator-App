// M3 Windows 侧连续预览门禁套件：覆盖默认 GPU · SHM ↔ CPU · SHM 动态切换、
// 同一 <canvas key="shm"> 复用与像素校验、SHM 30 次 720p↔1440p 连续切尺寸（HandleCount 与 Commit Charge 核对）、
// 显式 MAKO_PREVIEW_TRANSPORT=dma-buf 零静默降级、以及消费者映射前精确 Worker 强杀清理。
// 凭证与私钥不写入任何报告文件；所有证据默认落在 .local/shm-preview-m3-windows/。
import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { existsSync } from 'node:fs';
import { mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { inflateSync } from 'node:zlib';
import { connectPage } from './gpu-preview-p3.mjs';
import { getProcessIdentity, getProcessMetrics, stopLauncherGracefully, waitOwnedExit } from './process-identity.mjs';
import { shell, ui } from '../frontend/scripts/ui-query.mjs';

if (process.platform !== 'win32') {
  throw new Error('scripts/shm-preview-m3-windows.mjs must run on Windows x64.');
}

const require = createRequire(import.meta.url);
const { startBackendProcess } = require('../desktop/runtime/backend-host.cjs');
const { appRuntimePaths } = require('../desktop/runtime/app-paths.cjs');
const privateFs = require('../desktop/runtime/private-fs.cjs');

const ROOT = resolve(import.meta.dirname, '..');
const { values } = parseArgs({
  options: {
    case: { type: 'string', default: 'all' },
    output: { type: 'string' },
    port: { type: 'string', default: '5195' },
    'cdp-port': { type: 'string', default: '9255' },
  },
});

const VALID_CASES = ['switch', 'shm-resize30', 'dmabuf-no-fallback', 'premap-kill', 'all'];
if (!VALID_CASES.includes(values.case)) {
  throw new Error(`Expected --case ${VALID_CASES.join('|')}`);
}
const selectedCases = values.case === 'all'
  ? ['switch', 'shm-resize30', 'dmabuf-no-fallback', 'premap-kill']
  : [values.case];

const outDir = resolve(
  values.output ?? join(ROOT, '.local/shm-preview-m3-windows', `${values.case}-${new Date().toISOString().replaceAll(/[^0-9TZ]/g, '')}`),
);
assert.ok(privateFs.isInsideRoot(join(ROOT, '.local'), outDir), 'Output must remain inside App .local/');
await mkdir(outDir, { recursive: true, mode: 0o700 });

const wait = (ms) => new Promise((done) => setTimeout(done, ms));
const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex');

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
  const audio = join(dir, 'm3-win-tone-6s.wav');
  const lyrics = join(dir, 'm3-win-lyrics.lrc');
  await writeFile(audio, Buffer.concat([header, body]));
  await writeFile(lyrics, '[00:00.00]M3 Windows 连续预览\n[00:02.00]Vulkan SHM 与 CPU SHM\n[00:04.00]零静默降级验证\n');
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

async function collapseRightOverlay(page) {
  await page.eval(`document.querySelector('.work-rail.right.on[aria-expanded="true"]')?.click()`);
  await page.waitFor(`!document.querySelector('.work-rail.right.on[aria-expanded="true"]')`, '收起右侧覆盖面板', 5_000);
}

async function launchDesktopSession(sessionName, port, cdpPort, transportEnv) {
  const env = { ...process.env, MAKO_APP_SHUTDOWN_STDIN: '1' };
  if (transportEnv === undefined) {
    delete env.MAKO_PREVIEW_TRANSPORT;
  } else {
    env.MAKO_PREVIEW_TRANSPORT = transportEnv;
  }
  const launcher = spawn(
    process.execPath,
    [join(ROOT, 'scripts/app.mjs'), 'desktop', '--session-name', sessionName, '--port', String(port), '--cdp-port', String(cdpPort)],
    { cwd: ROOT, env, stdio: ['pipe', 'pipe', 'pipe'] },
  );
  let log = '';
  for (const s of [launcher.stdout, launcher.stderr]) s.on('data', (chunk) => { log += chunk; });
  const start = Date.now();
  while (!log.includes('[app] 桌面页面')) {
    if (launcher.exitCode !== null) {
      throw new Error(`桌面启动失败（exit=${launcher.exitCode}）：${log.slice(-2000)}`);
    }
    if (Date.now() - start > 60_000) {
      stopLauncherGracefully(launcher);
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
  if (await page.eval(`localStorage.getItem('mako.renderBackend') !== null`)) {
    await page.eval(`localStorage.removeItem('mako.renderBackend'); location.reload();`);
    await page.waitFor(shell.connected, '重置设备偏好后重新连接 Server', 30_000);
  }

  async function closeAll() {
    try {
      if (await page.eval(shell.projectOpen).catch(() => false)) {
        await shell.closeProject(page).catch(() => {});
      }
    } catch {}
    try {
      await page.eval('window.close()').catch(() => {});
    } catch {}
    let timer;
    const exitCode = await Promise.race([
      new Promise((done) => (launcher.exitCode !== null ? done(launcher.exitCode) : launcher.once('exit', done))),
      new Promise((done) => { timer = setTimeout(() => done('timeout'), 30_000); }),
    ]);
    clearTimeout(timer);
    if (exitCode === 'timeout' && launcher.exitCode === null) {
      stopLauncherGracefully(launcher);
      await new Promise((done) => launcher.once('exit', done));
    }
    cdp.ws.close();
    const runDir = appRuntimePaths(ROOT).run;
    const launchFilesRemoved = !existsSync(join(runDir, `desktop-${launcher.pid}.json`))
      && !existsSync(join(runDir, `desktop-backend-${launcher.pid}.json`));
    return { exitCode, launchFilesRemoved, log };
  }

  return { launcher, cdp, page, runtimeExceptions, closeAll };
}

async function runSwitchCase(fixtures, port, cdpPort) {
  const session = await launchDesktopSession('m3-win-switch', port, cdpPort, undefined);
  let closedInfo;
  try {
    const { page, runtimeExceptions } = session;
    if (await page.eval(shell.projectOpen)) await shell.closeProject(page);
    await shell.newProject(page, { ...fixtures, name: 'M3 Windows 动态切换验证' });
    await page.waitFor(`document.querySelector('.transport .play') && !document.querySelector('.transport .play').disabled`, '音频就绪', 30_000);

    const beforeInit = (await readPreviewSummary(page)).completed;
    await shell.goto(page, 'appearance');
    await collapseRightOverlay(page);
    const gpuInitial = await waitForStreamFrame(page, beforeInit, '成片 · GPU · SHM · ', 'shm', 'vulkan');
    assert.equal(gpuInitial.last.counters?.cpuPixelReadbacks, 1);
    assert.ok(Number.isSafeInteger(gpuInitial.last.timings?.renderDurationUs) && gpuInitial.last.timings.renderDurationUs >= 0);
    assert.ok(Number.isSafeInteger(gpuInitial.last.timings?.portCaptureDurationUs) && gpuInitial.last.timings.portCaptureDurationUs >= 0);
    const gpuProbe = await probeCanvas(page);
    assert.ok(gpuProbe && gpuProbe.colors > 2, 'GPU SHM canvas probe empty');
    await page.eval(`void (window.__m3WinCanvasRef = document.querySelector('canvas#mako-gpu-preview'))`);

    // 切换至 CPU -> 必须走 CPU · SHM 且复用同一 <canvas key="shm"> WebGL2 节点无上下文冲突
    const beforeCpu = gpuInitial.completed;
    await chooseDevice(page, 'CPU');
    const cpuState = await waitForStreamFrame(page, beforeCpu, '成片 · CPU · SHM · ', 'shm', 'cpu');
    assert.equal(cpuState.last.counters?.cpuPixelReadbacks, 1);
    assert.ok(Number.isSafeInteger(cpuState.last.timings?.renderDurationUs) && cpuState.last.timings.renderDurationUs >= 0);
    assert.ok(Number.isSafeInteger(cpuState.last.timings?.portCaptureDurationUs) && cpuState.last.timings.portCaptureDurationUs >= 0);
    const cpuCanvas = await page.eval(`(() => {
      const c = document.querySelector('canvas#mako-gpu-preview');
      return { sameNode: !!c && c === window.__m3WinCanvasRef, transport: c?.dataset?.previewTransport, api: c?.dataset?.previewApi };
    })()`);
    assert.equal(cpuCanvas.sameNode, true, 'GPU·SHM -> CPU·SHM should reuse same <canvas key="shm"> node');
    assert.equal(cpuCanvas.transport, 'shm');
    assert.equal(cpuCanvas.api, 'cpu');

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

    // 切回跟随后端默认 -> 恢复 GPU · SHM 且复用同一 <canvas key="shm">
    const beforeBack = (await readPreviewSummary(page)).completed;
    await chooseDevice(page, '跟随后端默认', true);
    const gpuRecovered = await waitForStreamFrame(page, beforeBack, '成片 · GPU · SHM · ', 'shm', 'vulkan');
    assert.equal(gpuRecovered.last.counters?.cpuPixelReadbacks, 1);
    const gpuCanvasBack = await page.eval(`(() => {
      const c = document.querySelector('canvas#mako-gpu-preview');
      return { sameNode: !!c && c === window.__m3WinCanvasRef, transport: c?.dataset?.previewTransport, api: c?.dataset?.previewApi };
    })()`);
    assert.equal(gpuCanvasBack.sameNode, true, 'CPU·SHM -> GPU·SHM should reuse same <canvas key="shm"> node');
    const gpuRecoveredProbe = await probeCanvas(page);
    assert.ok(
      gpuRecoveredProbe && Math.abs(gpuRecoveredProbe.corner[0] - 40) <= 6 && Math.abs(gpuRecoveredProbe.corner[1] - 170) <= 6 && Math.abs(gpuRecoveredProbe.corner[2] - 90) <= 6,
      `Recovered GPU SHM corner mismatch: ${JSON.stringify(gpuRecoveredProbe)}`,
    );
    assert.deepEqual(runtimeExceptions, [], `Runtime exceptions: ${runtimeExceptions.join('; ')}`);

    closedInfo = await session.closeAll();
    assert.equal(closedInfo.exitCode, 0, `Launcher exit code ${closedInfo.exitCode}`);
    assert.equal(closedInfo.launchFilesRemoved, true, 'Private launch files remained after clean close');

    return {
      passed: true,
      gpuInitial: {
        size: gpuInitial.active.size,
        colors: gpuProbe.colors,
        readbacks: gpuInitial.last.counters.cpuPixelReadbacks,
        timings: gpuInitial.last.timings,
      },
      cpuShm: {
        reusedSameShmCanvas: cpuCanvas.sameNode,
        timings: cpuState.last.timings,
        readbacks: cpuState.last.counters.cpuPixelReadbacks,
        editedCornerRgba: cpuAfterEditProbe.corner,
        playbackFramesIn1500ms: cpuPlayFrames,
        peakOutstanding: duringPlay.peakOutstanding,
      },
      gpuRecovered: {
        reusedSameShmCanvas: gpuCanvasBack.sameNode,
        readbacks: gpuRecovered.last.counters.cpuPixelReadbacks,
        cornerRgba: gpuRecoveredProbe.corner,
      },
      launchFilesRemoved: closedInfo.launchFilesRemoved,
      launcherExitCode: closedInfo.exitCode,
    };
  } catch (error) {
    if (!closedInfo) await session.closeAll().catch(() => {});
    throw error;
  }
}

async function runShmResize30Case(fixtures, port, cdpPort) {
  const session = await launchDesktopSession('m3-win-resize30', port, cdpPort, undefined);
  let closedInfo;
  try {
    const { page, runtimeExceptions } = session;
    if (await page.eval(shell.projectOpen)) await shell.closeProject(page);
    await shell.newProject(page, { ...fixtures, name: 'M3 Windows SHM 30次切尺寸' });
    await page.waitFor(`document.querySelector('.transport .play') && !document.querySelector('.transport .play').disabled`, '音频就绪', 30_000);

    const beforeInit = (await readPreviewSummary(page)).completed;
    await shell.goto(page, 'appearance');
    await collapseRightOverlay(page);
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
    const baselineMain = await getProcessMetrics(warmed.mainPid);
    const baselineRenderer = await getProcessMetrics(warmed.rendererPid);

    let midMain = null;
    let midRenderer = null;
    let lastResizeState = warmed;
    for (let i = 1; i <= 30; i++) {
      const target = i % 2 === 1 ? { width: 2560, height: 1440 } : { width: 1280, height: 720 };
      lastResizeState = await doResizeAndWait(target.width, target.height, `resize-${i}`);
      assert.equal(lastResizeState.last.transport, 'shm');
      assert.equal(lastResizeState.last.api, 'vulkan');
      assert.ok(lastResizeState.peakOutstanding <= 3, `peakOutstanding > 3 at resize ${i}`);
      if (i === 15) {
        midMain = await getProcessMetrics(lastResizeState.mainPid);
        midRenderer = await getProcessMetrics(lastResizeState.rendererPid);
      }
    }

    const afterMain = await getProcessMetrics(lastResizeState.mainPid);
    const afterRenderer = await getProcessMetrics(lastResizeState.rendererPid);
    const sameCanvasAfter30 = await page.eval(`document.querySelector('canvas#mako-gpu-preview') === window.__m3ShmCanvasRef`);
    assert.equal(sameCanvasAfter30, true, 'SHM 30 resizes unexpectedly replaced canvas DOM node');

    const mainHandleDelta = afterMain.handleCount - baselineMain.handleCount;
    const rendererHandleDelta = afterRenderer.handleCount - baselineRenderer.handleCount;
    const mainHandleMidToEnd = afterMain.handleCount - midMain.handleCount;
    const rendererHandleMidToEnd = afterRenderer.handleCount - midRenderer.handleCount;
    const mainPrivateDeltaKiB = afterMain.privateKiB - baselineMain.privateKiB;
    const rendererPrivateDeltaKiB = afterRenderer.privateKiB - baselineRenderer.privateKiB;
    const mainWorkingSetDeltaKiB = afterMain.workingSetKiB - baselineMain.workingSetKiB;
    const rendererWorkingSetDeltaKiB = afterRenderer.workingSetKiB - baselineRenderer.workingSetKiB;

    assert.ok(mainHandleDelta <= 64 && mainHandleMidToEnd <= 32, `Main HandleCount grew too much: total=${mainHandleDelta}, midToEnd=${mainHandleMidToEnd}`);
    assert.ok(rendererHandleDelta <= 64 && rendererHandleMidToEnd <= 32, `Renderer HandleCount grew too much: total=${rendererHandleDelta}, midToEnd=${rendererHandleMidToEnd}`);
    assert.ok(mainPrivateDeltaKiB < 128 * 1024, `Main Commit Charge delta too high: ${mainPrivateDeltaKiB} KiB`);
    assert.ok(rendererPrivateDeltaKiB < 128 * 1024, `Renderer Commit Charge delta too high: ${rendererPrivateDeltaKiB} KiB`);

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
    assert.equal(closedInfo.launchFilesRemoved, true, 'Private launch files remained after clean close');

    return {
      passed: true,
      resizeCount: 30,
      sameCanvasAcross30Resizes: sameCanvasAfter30,
      sameCanvasAcrossGpuToCpuShm: sameCanvasOnCpu,
      baselineMain,
      midMain,
      afterMain,
      mainHandleDelta,
      mainHandleMidToEnd,
      mainPrivateDeltaKiB,
      mainWorkingSetDeltaKiB,
      baselineRenderer,
      midRenderer,
      afterRenderer,
      rendererHandleDelta,
      rendererHandleMidToEnd,
      rendererPrivateDeltaKiB,
      rendererWorkingSetDeltaKiB,
      lastGpuTimings: lastResizeState.last.timings,
      cpuShmTimings: cpuState.last.timings,
      cpuProbeColors: cpuProbe.colors,
      launchFilesRemoved: closedInfo.launchFilesRemoved,
      launcherExitCode: closedInfo.exitCode,
    };
  } catch (error) {
    if (!closedInfo) await session.closeAll().catch(() => {});
    throw error;
  }
}

async function runDmaBufNoFallbackCase() {
  const current = JSON.parse(await readFile(join(ROOT, '.local/backend-current.json'), 'utf8'));
  const profileDir = join(outDir, 'dmabuf-nofb-profile');
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
      MAKO_PREVIEW_TRANSPORT: 'dma-buf',
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
  let result = null;
  try {
    const reg = await apiReq(null, 'POST', '/api/v1/clients', { protocolVersion: '1' }, { Authorization: `Bearer ${backend.info.bootstrapToken}` });
    assert.equal(reg.status, 201);
    const client = reg.data;

    const caps = await apiReq(client, 'GET', '/api/v1/capabilities');
    assert.equal(caps.status, 200);
    assert.ok(Array.isArray(caps.data?.features), 'Capabilities response missing features array');
    const advertisesGpuStreams = caps.data.features.includes('preview.gpu-streams');
    assert.equal(advertisesGpuStreams, false, 'Windows with MAKO_PREVIEW_TRANSPORT=dma-buf must NOT advertise preview.gpu-streams');

    const projRes = await apiReq(client, 'POST', '/api/v1/project-sessions', { name: 'M3 Windows DMA-BUF Zero Fallback', metadata: metadata() });
    assert.equal(projRes.status, 201);
    const project = projRes.data;
    const source = { kind: 'project', sourceId: project.projectSessionId, sourceRevision: project.projectRevision };

    // 显式 dma-buf 下直接请求 vulkan 必须拒绝为 GpuPreviewUnsupportedPlatform（零静默回退 SHM）
    const vulkanAttempt = await apiReq(client, 'POST', '/api/v1/gpu-preview-streams', {
      source,
      size: { width: 640, height: 360 },
      alphaMode: 'opaque',
      api: 'vulkan',
      consumerId,
      metadata: metadata(),
    });
    assert.equal(vulkanAttempt.status, 503, `Expected 503 for Windows dma-buf vulkan, got ${JSON.stringify(vulkanAttempt)}`);
    assert.equal(vulkanAttempt.data?.code, 'previewUnavailable');
    assert.equal(vulkanAttempt.data?.failureCode, 'GpuPreviewUnsupportedPlatform');

    // 显式 dma-buf 下直接请求 cpu 必须拒绝为 PreviewTransportUnsupported（零静默回退 SHM）
    const cpuAttempt = await apiReq(client, 'POST', '/api/v1/gpu-preview-streams', {
      source,
      size: { width: 640, height: 360 },
      alphaMode: 'opaque',
      api: 'cpu',
      consumerId,
      metadata: metadata(),
    });
    assert.equal(cpuAttempt.status, 422, `Expected 422 for Windows dma-buf cpu, got ${JSON.stringify(cpuAttempt)}`);
    assert.equal(cpuAttempt.data?.code, 'commandNotSupported');
    assert.equal(cpuAttempt.data?.failureCode, 'PreviewTransportUnsupported');

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

    result = {
      passed: true,
      advertisesGpuStreams,
      vulkanAttempt: { status: vulkanAttempt.status, code: vulkanAttempt.data.code, failureCode: vulkanAttempt.data.failureCode },
      cpuAttempt: { status: cpuAttempt.status, code: cpuAttempt.data.code, failureCode: cpuAttempt.data.failureCode },
    };
  } catch (error) {
    innerError = error;
  } finally {
    const exitCode = await backend.stop();
    await writeFile(join(outDir, 'dmabuf-nofb-server.log'), backendLogs.join('\n') + '\n').catch(() => {});
    await rm(profileDir, { recursive: true, force: true }).catch(() => {});
    if (!innerError) {
      assert.equal(exitCode, 0, `Standalone backend did not exit 0: ${exitCode}`);
      result.backendExitCode = exitCode;
    }
  }
  if (innerError) throw innerError;
  return result;
}

async function runPremapKillCase() {
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

      const projRes = await apiReq(client, 'POST', '/api/v1/project-sessions', { name: `M3 Windows Pre-map Kill (${api})`, metadata: metadata() });
      assert.equal(projRes.status, 201);
      const project = projRes.data;
      const source = { kind: 'project', sourceId: project.projectSessionId, sourceRevision: project.projectRevision };

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

      const claimed = await apiReq(
        null,
        'POST',
        `/internal/gpu-preview-streams/${stream.streamId}/claim`,
        { consumerId, hostInstanceId, expectedInputGeneration: stream.inputGeneration },
        { 'X-Mako-Desktop-Secret': consumerSecret },
      );
      assert.equal(claimed.status, 200);
      assert.equal(claimed.data.transport, 'shm');
      assert.match(claimed.data.brokerEndpoint, /^\\\\\.\\pipe\\mkg-shm-[a-f0-9]{24,64}$/);
      const workerIdentity = await getProcessIdentity(claimed.data.workerPid);
      assert.ok(!workerIdentity.exited, `Owned worker ${claimed.data.workerPid} missing before kill`);

      // 向精确所属 Worker PID 发起强杀（消费者尚未 openReadOnly 映射）
      process.kill(workerIdentity.pid, 'SIGKILL');
      assert.equal(await waitOwnedExit(workerIdentity, 10_000), true, `Owned worker ${workerIdentity.pid} did not exit`);

      const failedState = await apiReq(client, 'GET', `/api/v1/gpu-preview-streams/${stream.streamId}`);
      assert.equal(failedState.status, 200);
      assert.equal(failedState.data.registrationState, 'failed');
      assert.equal(failedState.data.failureCode, 'GpuWorkerExited');

      const isolated = await apiReq(
        null,
        'POST',
        `/internal/gpu-preview-streams/${stream.streamId}/consumer-failed`,
        { consumerId, hostInstanceId, inputGeneration: stream.inputGeneration, producerEpoch: stream.producerEpoch },
        { 'X-Mako-Desktop-Secret': consumerSecret },
      );
      assert.equal(isolated.status, 200);
      assert.equal(isolated.data.terminated, true);

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
        namedPipeMatched: true,
        workerPid: workerIdentity.pid,
        registrationState: failedState.data.registrationState,
        failureCode: failedState.data.failureCode,
        consumerIsolationTerminated: isolated.data.terminated,
      });
    } catch (error) {
      innerError = error;
    } finally {
      const exitCode = await backend.stop();
      await writeFile(join(outDir, `premap-server-${api}.log`), backendLogs.join('\n') + '\n').catch(() => {});
      await rm(profileDir, { recursive: true, force: true }).catch(() => {});
      if (!innerError) {
        assert.equal(exitCode, 0, `Standalone backend (${api}) did not exit 0: ${exitCode}`);
        apiResults.at(-1).backendExitCode = exitCode;
      }
    }
    if (innerError) throw innerError;
  }

  return { passed: true, cases: apiResults };
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
    dist: currentDeployment.dist.slice(ROOT.length + 1).replaceAll('\\', '/'),
    version: deploymentMeta.version,
    rid: deploymentMeta.rid,
    sourceCommit: deploymentMeta.sourceCommit,
    sourceDirty: deploymentMeta.sourceDirty,
    nativeSourceDirty: nativeMeta.sourceDirty,
    deploymentManifestSha256: sha256(deploymentBytes),
    nativeManifestSha256: sha256(nativeBytes),
    nativeArtifacts: nativeMeta.files,
  },
  scope: 'M3 Windows x64 continuous preview gate (GPU·SHM, CPU·SHM, same <canvas key="shm"> reuse, 30x 720p<->1440p HandleCount/CommitCharge, explicit dma-buf zero-fallback, pre-map worker kill).',
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
    console.log(`[m3-windows] running case: ${name}`);
    if (name === 'switch') report.cases[name] = await runSwitchCase(fixtures, port, cdpPort);
    else if (name === 'shm-resize30') report.cases[name] = await runShmResize30Case(fixtures, port, cdpPort);
    else if (name === 'dmabuf-no-fallback') report.cases[name] = await runDmaBufNoFallbackCase();
    else if (name === 'premap-kill') report.cases[name] = await runPremapKillCase();
    console.log(`[m3-windows] PASS ${name}`);
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
