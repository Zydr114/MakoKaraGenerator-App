// 桌面版冒烟：在真实 Electron 窗口里走一遍 GPU 共享纹理预览的功能（只看对不对，不测帧率）。
// 默认自己启动一个独立会话（--session-name desktop-smoke，独立的后端 profile 与 Electron profile），用 CDP 操作页面，
// 最后关窗，检查启动器正常退出、私有启动文件已删除。--attach <CDP 端口> 时连到已经开着的验收会话，不关窗。
// 每帧有没有 CPU 回读由主进程逐帧把关（帧元数据里 cpuPixelReadbacks 不是 0 就整个退出），这里再汇总一次。
// 检查画面内容时会在页面里读 canvas 像素，这只是冒烟里的正确性取样，不属于正式预览路径。
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { connectPage } from './gpu-preview-p3.mjs';
import { shell, ui } from '../frontend/scripts/ui-query.mjs';

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
    active: s.activeStream ? { streamId: s.activeStream.streamId, inputGeneration: s.activeStream.inputGeneration, size: s.activeStream.size } : null,
    readbacks: s.samples.reduce((n, x) => n + (x.counters?.cpuPixelReadbacks ?? 0), 0), samples: s.samples.length,
    last: s.samples.at(-1) ? { timeUs: s.samples.at(-1).timeUs, width: s.samples.at(-1).width, height: s.samples.at(-1).height, provider: s.samples.at(-1).provider } : null };
})()`;
const readState = (page) => page.eval(previewState);
const title = `(document.querySelector('.preview-panel .preview-title .pane-title-text')?.textContent ?? '')`;
const gpuCanvas = `document.querySelector('canvas#mako-gpu-preview')`;
const playhead = `(document.querySelector('.transport-time span')?.textContent ?? '')`;

/** GPU 画面的像素取样：不同颜色数、四角颜色。 */
const canvasProbe = `(() => {
  const c = ${gpuCanvas}; if (!c) return null;
  const d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data;
  const colors = new Set(); for (let i = 0; i < d.length; i += 4 * 97) colors.add((d[i] << 16) | (d[i + 1] << 8) | d[i + 2]);
  const at = (x, y) => { const i = (Math.floor(y) * c.width + Math.floor(x)) * 4; return [d[i], d[i + 1], d[i + 2], d[i + 3]]; };
  return { width: c.width, height: c.height, colors: colors.size, corner: at(4, 4), center: at(c.width / 2, c.height / 2) };
})()`;

/** 等 GPU 面板画出新帧：drawn 比 before 多，并且有活动的流。 */
async function waitFrames(page, before, what, extra = 1) {
  await page.waitFor(`${gpuCanvas} && ${title}.startsWith('成片 · GPU · 修订')`, `${what}：GPU 面板出帧`, 120_000);
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

async function screenshot(page, name) {
  const { data } = await page.send('Page.captureScreenshot', { format: 'png' });
  await writeFile(join(out, `${name}.png`), Buffer.from(data, 'base64'));
}

let launcher, log = '';
async function launch() {
  launcher = spawn(process.execPath, [join(ROOT, 'scripts/app.mjs'), 'desktop', '--session-name', 'desktop-smoke', '--port', values.port, '--cdp-port', values['cdp-port']], { cwd: ROOT, stdio: ['ignore', 'pipe', 'pipe'] });
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

    // 外观页：跟随后端默认时，GPU 可用就走共享纹理
    await shell.goto(page, 'appearance');
    let state = await waitFrames(page, (await readState(page)).drawn, '外观页');
    let probe = await page.eval(canvasProbe);
    check(state.active.size.width === 1280 && state.last.provider === 'runtime' && probe.width === 1280 && probe.colors > 2,
      `外观页默认走 GPU 共享纹理：${state.active.size.width}×${state.active.size.height}，画面有内容（取样 ${probe.colors} 种颜色）`);
    check(state.readbacks === 0 && state.peakOutstanding <= 3, `每帧没有 CPU 像素回读，在途帧最多 ${state.peakOutstanding} 张（上限 3）`);
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
    const point = await page.eval(`(() => { const r = document.querySelector('canvas[aria-label="时间轴"]').getBoundingClientRect(); return { x: r.x + r.width * 0.15, y: r.y + 8 }; })()`);
    for (const type of ['mousePressed', 'mouseReleased']) await page.send('Input.dispatchMouseEvent', { type, x: point.x, y: point.y, button: 'left', clickCount: 1 });
    await page.waitFor(`${title}.endsWith(${playhead})`, '画面时刻等于播放头', 30_000);
    const seekText = await page.eval(playhead);
    check(seekText !== '' && (await page.eval(title)).endsWith(seekText), `暂停时拖动播放头到 ${seekText}，GPU 画面跟到同一时刻`);

    // 编辑：改背景色，流换成新修订的输入，画面跟着变
    const revisionBefore = await page.eval(`${title}.match(/修订 (\\d+)/)?.[1]`);
    const generationBefore = (await readState(page)).active.inputGeneration;
    await page.waitFor(`!!window.makoGpuPreviewAcceptance`, '验收入口');
    await page.eval(`window.makoGpuPreviewAcceptance.setBackground(200, 30, 40, 255)`);
    await page.waitFor(`(${title}.match(/修订 (\\d+)/)?.[1] ?? ${revisionBefore}) !== ${JSON.stringify(revisionBefore)}`, '画面换成新修订', 60_000);
    await page.waitFor(`(() => { const p = ${canvasProbe}; return p && Math.abs(p.corner[0] - 200) <= 3 && Math.abs(p.corner[1] - 30) <= 3 && Math.abs(p.corner[2] - 40) <= 3; })()`, '背景变成新颜色', 30_000);
    state = await readState(page);
    probe = await page.eval(canvasProbe);
    check(state.active.inputGeneration !== generationBefore, `改背景色后流换成新输入（修订 ${revisionBefore} → ${await page.eval(`${title}.match(/修订 (\\d+)/)?.[1]`)}），画面角上是新背景 rgb(${probe.corner.slice(0, 3).join(', ')})`);
    await screenshot(page, '2-appearance-edited');

    // 规格换成 1440p30：重建 2560×1440 的流
    const before1440 = (await readState(page)).drawn;
    await ui.choose(page, 'GPU 预览规格', '1440p · 30 fps');
    await page.waitFor(`${gpuCanvas}?.width === 2560`, '1440p 画面', 60_000);
    state = await waitFrames(page, before1440, '1440p');
    check(state.active.size.width === 2560 && state.active.size.height === 1440 && state.last.width === 2560, '规格换成 1440p · 30 fps 后重建 2560×1440 的流并出帧');
    await ui.choose(page, 'GPU 预览规格', '720p · 24 fps');

    // 切页：编排页也走 GPU；歌词页没有预览，流关掉；回外观页重新出帧
    await shell.goto(page, 'orchestration');
    state = await waitFrames(page, (await readState(page)).drawn, '编排页');
    check(!!state.active, '编排页的成片预览也走 GPU 共享纹理并出帧');
    await screenshot(page, '3-orchestration-gpu');
    await shell.goto(page, 'lyrics');
    await page.waitFor(`(async () => !(await window.makoDesktopPreview.readPreviewState()).activeStream)()`, '离开预览页后流关掉', 30_000);
    check(true, '切到歌词页（没有成片预览）后 GPU 流关掉');
    await shell.goto(page, 'appearance');
    state = await waitFrames(page, (await readState(page)).drawn, '回到外观页');
    check(!!state.active, '回到外观页重新连上 GPU 流并出帧');

    // 明确选 CPU：退回静态预览；改回跟随默认：又走 GPU
    await chooseDevice(page, 'CPU');
    await page.waitFor(`!!document.querySelector('.preview-frame img') && ${title}.startsWith('成片 · CPU')`, 'CPU 静态预览', 120_000);
    await page.waitFor(`(async () => !(await window.makoDesktopPreview.readPreviewState()).activeStream)()`, 'GPU 流关掉', 30_000);
    check(!(await page.eval(gpuCanvas)), '渲染设备选 CPU 后退回 PNG 静态预览，GPU 流关掉');
    await chooseDevice(page, '跟随后端默认', true);
    state = await waitFrames(page, (await readState(page)).drawn, '改回跟随默认');
    check(!!state.active, '改回跟随后端默认后又走 GPU 共享纹理');

    // 关窗前的确认：工程没保存时页面会拦下 beforeunload（主进程据此弹窗问）
    check((await page.eval(`!window.dispatchEvent(new Event('beforeunload', { cancelable: true }))`)) === true, '工程有未保存的修改时，关窗前的 beforeunload 被拦下（会弹窗确认）');
    state = await readState(page);
    check(state.readbacks === 0 && state.peakOutstanding <= 3, `全程 ${state.drawn} 帧都没有 CPU 像素回读，在途帧最多 ${state.peakOutstanding} 张`);
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
  const code = await Promise.race([
    new Promise((done) => (launcher.exitCode !== null ? done(launcher.exitCode) : launcher.once('exit', done))),
    wait(60_000).then(() => 'timeout'),
  ]);
  cdp.ws.close();
  check(code === 0 && log.includes('[app] 自有 Web 与后端进程已停止。'), '关窗后启动器停掉 Electron、Web 与后端，退出码 0');
  check(!existsSync(join(ROOT, `.local/run/desktop-${launcher.pid}.json`)) && !existsSync(join(ROOT, `.local/run/desktop-backend-${launcher.pid}.json`)), '本次的私有启动文件已删除');
}

try {
  await main();
  console.log(`\n${passed} 项通过（截图在 ${out}）`);
} catch (error) {
  console.error(`\n${error.message}`);
  if (log) await writeFile(join(out, 'launcher.log'), log);
  if (launcher && launcher.exitCode === null) launcher.kill('SIGTERM');
  process.exitCode = 1;
}
