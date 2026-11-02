// 有限布局检查：真实 Chromium 在几种窗口尺寸下打开编排页与外观页，确认中栏的预览画布完整显示、中栏与整页不需要滚动；歌词页只截图。
// 用法：node scripts/layout-check.mjs [页面地址] [--sizes 1920x1080,1440x900,1366x768]
// 需要 pnpm start 或 pnpm dev 已运行。只读布局尺寸并截图，不测性能。
import { mkdtemp, writeFile, rm, mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { launchChrome } from '../frontend/scripts/cdp.mjs';
import { shell } from '../frontend/scripts/ui-query.mjs';

const { values, positionals } = parseArgs({ allowPositionals: true, options: { sizes: { type: 'string', default: '1920x1080,1440x900,1366x768' } } });
const url = positionals[0] ?? 'http://127.0.0.1:5173';
const sizes = values.sizes.split(',').map((s) => s.split('x').map(Number)).map(([width, height]) => ({ width, height }));
if (!sizes.length || sizes.some((s) => !Number.isInteger(s.width) || !Number.isInteger(s.height))) throw new Error('--sizes 形如 1920x1080,1366x768');
const out = resolve(import.meta.dirname, '../.local/layout', new Date().toISOString().replaceAll(/[^0-9TZ]/g, ''));
await mkdir(out, { recursive: true });

// 临时素材：8 行歌词、20 秒正弦音。
const assets = await mkdtemp(join(tmpdir(), 'mako-layout-'));
const lrc = join(assets, '布局检查.lrc');
const wav = join(assets, '布局检查.wav');
await writeFile(lrc, Array.from({ length: 8 }, (_, i) => `[00:${String(1 + i * 2).padStart(2, '0')}.00]布局检查第${i + 1}行 layout line\n`).join(''));
const seconds = 20, rate = 22050, samples = seconds * rate;
const b = Buffer.alloc(44 + samples * 2);
b.write('RIFF'); b.writeUInt32LE(b.length - 8, 4); b.write('WAVEfmt ', 8); b.writeUInt32LE(16, 16); b.writeUInt16LE(1, 20); b.writeUInt16LE(1, 22); b.writeUInt32LE(rate, 24); b.writeUInt32LE(rate * 2, 28); b.writeUInt16LE(2, 32); b.writeUInt16LE(16, 34); b.write('data', 36); b.writeUInt32LE(samples * 2, 40);
for (let i = 0; i < samples; i++) b.writeInt16LE(Math.round(Math.sin((i * 2 * Math.PI * 330) / rate) * 6000), 44 + i * 2);
await writeFile(wav, b);

const browser = await launchChrome();
const errors = [];
const results = [];
let page;
async function resize({ width, height }) {
  await page.send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
  await new Promise((r) => setTimeout(r, 600));
}

/** 中栏（画布）与整页是否需要滚动、画布是否整块落在中栏和窗口的可见区域里。 */
const measure = (view) => `(() => {
  const r = (e) => { const b = e.getBoundingClientRect(); return { x: Math.round(b.x), y: Math.round(b.y), w: Math.round(b.width), h: Math.round(b.height) }; };
  const root = document.querySelector('.work-layout.${view}');
  const center = root.querySelector('.work-center');
  const frame = center.querySelector('.preview-frame');
  const dock = root.querySelector('.work-dock');
  const page = document.scrollingElement;
  const c = center.getBoundingClientRect(), f = frame.getBoundingClientRect();
  return {
    center: r(center), frame: r(frame), dock: r(dock),
    centerScrolls: center.scrollHeight > center.clientHeight + 1 || center.scrollWidth > center.clientWidth + 1,
    pageScrolls: page.scrollHeight > page.clientHeight + 1 || page.scrollWidth > page.clientWidth + 1,
    frameInside: f.top >= c.top - 1 && f.bottom <= c.bottom + 1 && f.left >= c.left - 1 && f.right <= c.right + 1 && f.bottom <= innerHeight + 1,
    frameShare: +(f.height / innerHeight).toFixed(3),
  };
})()`;

async function check(view, size) {
  const m = await page.eval(measure(view));
  const name = `${view}-${size.width}x${size.height}`;
  await writeFile(join(out, `${name}.png`), await page.screenshot());
  const pass = !m.centerScrolls && !m.pageScrolls && m.frameInside && m.frame.h >= 120;
  results.push({ name, pass, ...m });
  console.log(`${pass ? 'PASS' : 'FAIL'} ${name} 画布 ${m.frame.w}×${m.frame.h}（占窗口高 ${m.frameShare}）· 底部 ${m.dock.h}px${m.centerScrolls ? ' · 中栏需要滚动' : ''}${m.pageScrolls ? ' · 整页需要滚动' : ''}${m.frameInside ? '' : ' · 画布超出可见区'}`);
}

try {
  browser.cdp.on((m) => { if (m.method === 'Runtime.exceptionThrown') errors.push(m.params.exceptionDetails.exception?.description ?? 'exception'); });
  page = await browser.openPage(url, sizes[0]);
  await page.send('DOM.enable');
  await page.waitFor(shell.connected, '连接后端');
  await shell.newProject(page, { audio: wav, lyrics: lrc });
  await page.waitFor(`document.querySelectorAll('.line-list [role=row]').length === 8`, '8 行歌词进入工作区', 120_000);

  // 歌词页与另两页共用播放条和时间轴：只截图对照，不判定。
  for (const size of sizes) {
    await resize(size);
    await writeFile(join(out, `lyrics-${size.width}x${size.height}.png`), await page.screenshot());
  }
  await resize(sizes[0]);

  for (const view of ['orchestration', 'appearance']) {
    await shell.goto(page, view);
    await page.waitFor(`document.querySelector('.work-layout.${view} .work-center .preview-frame img')?.naturalWidth > 0`, `${view} 预览帧`, 120_000);
    for (const size of sizes) {
      await resize(size);
      await check(view, size);
    }
    await resize(sizes[0]);
  }

  await shell.closeProject(page);
} catch (error) {
  if (page) await writeFile(join(out, 'failure.png'), await page.screenshot()).catch(() => {});
  throw error;
} finally {
  await writeFile(join(out, 'result.json'), JSON.stringify({ url, sizes, results, runtimeExceptions: errors }, null, 2) + '\n');
  await browser.close();
  await rm(assets, { recursive: true, force: true });
}
console.log(`截图与结果：${out}`);
if (errors.length) console.log(`页面异常 ${errors.length} 条：\n  ${errors.join('\n  ')}`);
if (errors.length || results.some((r) => !r.pass)) process.exitCode = 1;
