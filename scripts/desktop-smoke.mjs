// 桌面版冒烟：在真实 Electron 窗口里走一遍 GPU 共享纹理预览的功能（只看对不对，不测帧率）。
// 默认自己启动一个独立会话（--session-name desktop-smoke，独立的后端 profile 与 Electron profile），用 CDP 操作页面，
// 最后关窗，检查启动器正常退出、私有启动文件已删除。--attach <CDP 端口> 时连到已经开着的验收会话，不关窗。
// 每帧有没有 CPU 回读由主进程逐帧把关（帧元数据里 cpuPixelReadbacks 不是 0 就整个退出），这里再汇总一次。
// 检查画面内容时会在页面里读 canvas 像素，这只是冒烟里的正确性取样，不属于正式预览路径。
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { mkdir, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { inflateSync } from 'node:zlib';
import { connectPage } from './gpu-preview-p3.mjs';
import { stopLauncherGracefully } from './process-identity.mjs';
import { shell, ui } from '../frontend/scripts/ui-query.mjs';

const require = createRequire(import.meta.url);
const { appRuntimePaths } = require('../desktop/runtime/app-paths.cjs');

const ROOT = resolve(import.meta.dirname, '..');
const { values } = parseArgs({ options: { attach: { type: 'string' }, port: { type: 'string', default: '5193' }, 'cdp-port': { type: 'string', default: '9251' } } });
const out = join(ROOT, '.local/desktop-smoke', new Date().toISOString().replaceAll(/[^0-9TZ]/g, ''));
await mkdir(out, { recursive: true, mode: 0o700 });

let passed = 0;
function check(ok, text) {
  if (!ok) throw new Error(`未通过：${text}`);
  passed++;
  console.log(`✓ ${text}`);
}
const wait = (ms) => new Promise((done) => setTimeout(done, ms));

// 6 秒 440 Hz 正弦（16 位单声道 16 kHz）与 3 行歌词。
async function fixtures() {
  const rate = 16000, seconds = 6, samples = rate * seconds;
  const header = Buffer.alloc(44), body = Buffer.alloc(samples * 2);
  header.write('RIFF'); header.writeUInt32LE(36 + body.length, 4); header.write('WAVEfmt ', 8); header.writeUInt32LE(16, 16); header.writeUInt16LE(1, 20); header.writeUInt16LE(1, 22);
  header.writeUInt32LE(rate, 24); header.writeUInt32LE(rate * 2, 28); header.writeUInt16LE(2, 32); header.writeUInt16LE(16, 34); header.write('data', 36); header.writeUInt32LE(body.length, 40);
  for (let i = 0; i < samples; i++) body.writeInt16LE(Math.round(Math.sin((2 * Math.PI * 440 * i) / rate) * 8000), i * 2);
  const audio = join(out, 'tone-6s.wav'), lyrics = join(out, 'lyrics.lrc');
  await writeFile(audio, Buffer.concat([header, body]));
  await writeFile(lyrics, '[00:00.00]桌面版 GPU 预览\n[00:02.00]共享纹理 低拷贝\n[00:04.00]最后一行\n');
  return { audio, lyrics };
}

/** 主进程汇总的预览状态（drawn 等是整个进程的累计数）。 */
const previewState = `(async () => {
  const s = await window.makoDesktopPreview.readPreviewState();
  return { drawn: s.drawn, completed: s.completedDrawnUnique, outstanding: s.outstanding, peakOutstanding: s.peakOutstanding,
    active: s.activeStream ? { streamId: s.activeStream.streamId, inputGeneration: s.activeStream.inputGeneration, size: s.activeStream.size, transport: s.activeStream.transport, api: s.activeStream.api } : null,
    readbacks: s.samples.reduce((n, x) => n + (x.counters?.cpuPixelReadbacks ?? 0), 0), samples: s.samples.length,
    last: s.samples.at(-1) ? { timeUs: s.samples.at(-1).timeUs, width: s.samples.at(-1).width, height: s.samples.at(-1).height, provider: s.samples.at(-1).provider, transport: s.samples.at(-1).transport, api: s.samples.at(-1).api, readbacks: s.samples.at(-1).counters?.cpuPixelReadbacks } : null };
})()`;
const readState = (page) => page.eval(previewState);
const title = `(document.querySelector('.preview-panel .preview-title .pane-title-text')?.textContent ?? '')`;
const gpuCanvas = `document.querySelector('canvas#mako-gpu-preview')`;
const playhead = `(document.querySelector('.transport-time span')?.textContent ?? '')`;

/** 极简 PNG 解码器（支持 8-bit RGB/RGBA），用于从 CDP 实际合成截图采样 WebGL2 / 2D 画布像素。 */
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

/** 采样实际展示的画布像素（同时兼容 2D 与 preserveDrawingBuffer=false 的 WebGL2 画布）。 */
async function probeCanvas(page) {
  const rect = await page.eval(`(() => {
    const c = ${gpuCanvas}; if (!c) return null;
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

/** 等连续预览面板画出新帧：drawn 比 before 多，并且有活动的流。 */
async function waitFrames(page, before, what, expectedPrefix = '成片 · GPU · ', extra = 1) {
  await page.waitFor(`${gpuCanvas} && ${title}.startsWith(${JSON.stringify(expectedPrefix)}) && ${title}.includes('修订 ')`, `${what}：连续预览面板出帧`, 120_000);
  const start = Date.now();
  for (;;) {
    const state = await readState(page);
    if (state.active && state.drawn >= before + extra) return state;
    if (Date.now() - start > 60_000) throw new Error(`等待超时：${what}（${JSON.stringify(state)}）`);
    await wait(200);
  }
}

async function chooseDevice(page, label, partial = false) {
  await ui.click(page, 'button', '（渲染设备）', { partial: true, scope: '.app-bar' });
  await page.waitFor(`!!document.querySelector('.render-device-menu')`, '渲染设备菜单');
  await ui.click(page, 'menuitem', label, { partial, scope: '.render-device-menu', what: `渲染设备「${label}」可选` });
  await page.waitFor(`!document.querySelector('.render-device-menu')`, '菜单关闭');
}

/** 窄屏 / 高 DPI 下右侧检查器会以覆盖面板浮在预览画布与「GPU 预览规格」上方；若已展开则收起以免遮挡。 */
async function collapseRightOverlay(page) {
  await page.eval(`document.querySelector('.work-rail.right.on[aria-expanded="true"]')?.click()`);
  await page.waitFor(`!document.querySelector('.work-rail.right.on[aria-expanded="true"]')`, '收起右侧覆盖面板', 5_000);
}

async function screenshot(page, name) {
  const { data } = await page.send('Page.captureScreenshot', { format: 'png' });
  await writeFile(join(out, `${name}.png`), Buffer.from(data, 'base64'));
}

let launcher, log = '';
async function launch() {
  launcher = spawn(
    process.execPath,
    [join(ROOT, 'scripts/app.mjs'), 'desktop', '--session-name', 'desktop-smoke', '--port', values.port, '--cdp-port', values['cdp-port']],
    {
      cwd: ROOT,
      env: { ...process.env, MAKO_APP_SHUTDOWN_STDIN: '1' },
      stdio: ['pipe', 'pipe', 'pipe'],
    },
  );
  for (const stream of [launcher.stdout, launcher.stderr]) stream.on('data', (chunk) => { log += chunk; });
  const start = Date.now();
  while (!log.includes('[app] 桌面页面')) {
    if (launcher.exitCode !== null) throw new Error(`桌面版没有启动（退出 ${launcher.exitCode}）：\n${log.slice(-2000)}`);
    if (Date.now() - start > 600_000) throw new Error('桌面版启动超时');
    await wait(200);
  }
}

async function main() {
  const files = await fixtures();
  if (!values.attach) await launch();
  const { cdp, page } = await connectPage(Number(values.attach ?? values['cdp-port']));
  try {
    await page.waitFor(shell.connected, '连接 Server', 120_000);
    check(await page.eval(`!!window.makoDesktopPreview && document.title === 'MakoKara Generator'`), '桌面窗口加载编辑器，页面里有 GPU 预览桥');
    if (await page.eval(shell.projectOpen)) await shell.closeProject(page);

    await shell.newProject(page, { ...files, name: '桌面版冒烟' });
    await page.waitFor(`document.querySelector('.transport .play') && !document.querySelector('.transport .play').disabled`, '音频就绪', 60_000);
    check(true, '新建工程（音频 6 秒、歌词 3 行），播放器就绪');

    // 外观页：跟随后端默认时，Linux 默认走 GPU · DMA-BUF（MAKO_PREVIEW_TRANSPORT=shm 时走 GPU · SHM）；Windows 默认走 GPU · SHM
    const envTransport = process.env.MAKO_PREVIEW_TRANSPORT?.trim().toLowerCase();
    const expectedGpuTransport = envTransport === 'shm' || envTransport === 'dma-buf'
      ? envTransport
      : (process.platform === 'linux' ? 'dma-buf' : 'shm');
    const expectedGpuPrefix = expectedGpuTransport === 'shm' ? '成片 · GPU · SHM · ' : '成片 · GPU · DMA-BUF · ';
    const expectedGpuReadbacks = expectedGpuTransport === 'shm' ? 1 : 0;
    const beforeAppearance = (await readState(page)).drawn;
    await shell.goto(page, 'appearance');
    await collapseRightOverlay(page);
    let state = await waitFrames(page, beforeAppearance, '外观页', expectedGpuPrefix);
    let probe = await probeCanvas(page);
    check(state.active.size.width === 1280 && state.active.transport === expectedGpuTransport && state.last.provider === 'runtime' && probe.width === 1280 && probe.colors > 2,
      `外观页默认走 GPU · ${expectedGpuTransport.toUpperCase()}：${state.active.size.width}×${state.active.size.height}，画面有内容（取样 ${probe.colors} 种颜色）`);
    check(state.last.readbacks === expectedGpuReadbacks && state.peakOutstanding <= 3, `单帧回读计数 ${state.last.readbacks}（预期 ${expectedGpuReadbacks}），在途帧最多 ${state.peakOutstanding} 张（上限 3）`);
    await screenshot(page, '1-appearance-gpu');

    // 播放 2 秒：帧持续到达，时刻往前走
    const beforePlay = await readState(page);
    await ui.click(page, 'button', '播放', { scope: '.transport' });
    await wait(2000);
    const playing = await readState(page);
    await ui.click(page, 'button', '暂停', { scope: '.transport' });
    const played = playing.drawn - beforePlay.drawn;
    check(played >= 24 && playing.last.timeUs > beforePlay.last.timeUs, `播放 2 秒画了 ${played} 帧，画面时刻从 ${beforePlay.last.timeUs} 到 ${playing.last.timeUs} 微秒`);

    // 暂停后拖动：画面停在播放头的时刻
    await wait(500);
    const beforeSeek = await page.eval(playhead);
    const point = await page.eval(`(() => { const r = document.querySelector('canvas[aria-label="时间轴"]').getBoundingClientRect(); return { x: r.x + r.width * 0.15, y: r.y + 8 }; })()`);
    for (const type of ['mousePressed', 'mouseReleased']) await page.send('Input.dispatchMouseEvent', { type, x: point.x, y: point.y, button: 'left', clickCount: 1 });
    await page.waitFor(`${playhead} !== ${JSON.stringify(beforeSeek)} && ${title}.endsWith(${playhead})`, '画面时刻等于播放头', 30_000);
    const seekText = await page.eval(playhead);
    check(seekText !== '' && (await page.eval(title)).endsWith(seekText), `暂停时拖动播放头到 ${seekText}，画面跟到同一时刻`);

    // 编辑：改背景色，流换成新修订的输入，画面跟着变
    const revisionBefore = await page.eval(`${title}.match(/修订 (\\d+)/)?.[1]`);
    const generationBefore = (await readState(page)).active.inputGeneration;
    await page.waitFor(`!!window.makoGpuPreviewAcceptance`, '验收入口');
    await page.eval(`window.makoGpuPreviewAcceptance.setBackground(200, 30, 40, 255)`);
    await page.waitFor(`(${title}.match(/修订 (\\d+)/)?.[1] ?? ${revisionBefore}) !== ${JSON.stringify(revisionBefore)}`, '画面换成新修订', 60_000);
    const bgDeadline = Date.now() + 30_000;
    for (;;) {
      probe = await probeCanvas(page);
      if (probe && Math.abs(probe.corner[0] - 200) <= 6 && Math.abs(probe.corner[1] - 30) <= 6 && Math.abs(probe.corner[2] - 40) <= 6) break;
      if (Date.now() > bgDeadline) throw new Error(`背景未变成新颜色：${JSON.stringify(probe)}`);
      await wait(150);
    }
    state = await readState(page);
    check(state.active.inputGeneration !== generationBefore, `改背景色后流换成新输入（修订 ${revisionBefore} → ${await page.eval(`${title}.match(/修订 (\\d+)/)?.[1]`)}），画面角上是新背景 rgb(${probe.corner.slice(0, 3).join(', ')})`);
    await screenshot(page, '2-appearance-edited');

    // 规格换成 1440p30：重建 2560×1440 的流
    const before1440 = (await readState(page)).drawn;
    await ui.choose(page, 'GPU 预览规格', '1440p · 30 fps');
    await page.waitFor(`${gpuCanvas}?.width === 2560`, '1440p 画面', 60_000);
    state = await waitFrames(page, before1440, '1440p', expectedGpuPrefix);
    check(state.active.size.width === 2560 && state.active.size.height === 1440 && state.last.width === 2560, '规格换成 1440p · 30 fps 后重建 2560×1440 的流并出帧');
    await ui.choose(page, 'GPU 预览规格', '720p · 24 fps');

    // 切页：编排页也走连续预览；歌词页没有预览，流关掉；回外观页重新出帧
    const beforeOrch = (await readState(page)).drawn;
    await shell.goto(page, 'orchestration');
    state = await waitFrames(page, beforeOrch, '编排页', expectedGpuPrefix);
    check(!!state.active, '编排页的成片预览也走连续预览流并出帧');
    await screenshot(page, '3-orchestration-gpu');
    await shell.goto(page, 'lyrics');
    await page.waitFor(`(async () => !(await window.makoDesktopPreview.readPreviewState()).activeStream)()`, '离开预览页后流关掉', 30_000);
    check(true, '切到歌词页（没有成片预览）后连续预览流关掉');
    const beforeBack = (await readState(page)).drawn;
    await shell.goto(page, 'appearance');
    await collapseRightOverlay(page);
    state = await waitFrames(page, beforeBack, '回到外观页', expectedGpuPrefix);
    check(!!state.active, '回到外观页重新连上连续预览流并出帧');

    // 明确选 CPU：切换至 CPU · SHM 连续预览（并经 key=transport 隔离 Canvas 上下文）；改回跟随默认恢复 GPU 流
    const beforeCpu = (await readState(page)).drawn;
    await chooseDevice(page, 'CPU');
    state = await waitFrames(page, beforeCpu, 'CPU · SHM 连续预览', '成片 · CPU · SHM · ');
    probe = await probeCanvas(page);
    check(state.active.api === 'cpu' && state.active.transport === 'shm' && state.last.readbacks === 1 && probe && probe.colors > 2,
      `渲染设备选 CPU 后切到 CPU · SHM 连续预览且无 Canvas 上下文冲突（取样 ${probe?.colors} 种颜色）`);
    await screenshot(page, '4-appearance-cpu-shm');
    const beforeGpu = (await readState(page)).drawn;
    await chooseDevice(page, '跟随后端默认', true);
    state = await waitFrames(page, beforeGpu, '改回跟随默认', expectedGpuPrefix);
    check(!!state.active && state.active.api === 'vulkan' && state.active.transport === expectedGpuTransport, `改回跟随后端默认后恢复 GPU · ${expectedGpuTransport.toUpperCase()} 连续预览`);

    // 关窗前的确认：工程没保存时页面会拦下 beforeunload（主进程据此弹窗问）
    check((await page.eval(`!window.dispatchEvent(new Event('beforeunload', { cancelable: true }))`)) === true, '工程有未保存的修改时，关窗前的 beforeunload 被拦下（会弹窗确认）');
    state = await readState(page);
    check(state.peakOutstanding <= 3, `全程 ${state.drawn} 帧在途帧最多 ${state.peakOutstanding} 张（上限 3）`);
    await shell.closeProject(page);
    check((await page.eval(`!window.dispatchEvent(new Event('beforeunload', { cancelable: true }))`)) === false, '关闭工程后关窗不再拦');
  } catch (error) {
    await screenshot(page, 'failure').catch(() => {});
    throw error;
  }
  if (values.attach) {
    cdp.ws.close();
    return;
  }

  // 关窗：启动器按顺序停掉 Electron、Web 与后端，正常退出，私有启动文件删掉
  void page.eval(`window.close()`).catch(() => {});
  let exitTimer;
  const code = await Promise.race([
    new Promise((done) => (launcher.exitCode !== null ? done(launcher.exitCode) : launcher.once('exit', done))),
    new Promise((done) => { exitTimer = setTimeout(() => done('timeout'), 60_000); }),
  ]);
  clearTimeout(exitTimer);
  cdp.ws.close();
  const runDir = appRuntimePaths(ROOT).run;
  check(code === 0 && log.includes('[app] 自有 Web 与后端进程已停止。'), '关窗后启动器停掉 Electron、Web 与后端，退出码 0');
  check(!existsSync(join(runDir, `desktop-${launcher.pid}.json`)) && !existsSync(join(runDir, `desktop-backend-${launcher.pid}.json`)), '本次的私有启动文件已删除');
}

try {
  await main();
  console.log(`\n${passed} 项通过（截图在 ${out}）`);
} catch (error) {
  console.error(`\n${error.message}`);
  if (log) await writeFile(join(out, 'launcher.log'), log);
  if (launcher && launcher.exitCode === null) stopLauncherGracefully(launcher);
  process.exitCode = 1;
}
