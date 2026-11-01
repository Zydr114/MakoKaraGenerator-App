// 有限布局检查：真实 Chromium 在几种窗口尺寸下打开编排页与外观页，确认预览画布完整显示、页面不需要滚动。
// 用法：node scripts/layout-check.mjs [页面地址] [--sizes 1920x1080,1440x900,1366x768]
// 需要 pnpm start 或 pnpm dev 已运行。只读布局尺寸并截图，不测性能。
import { mkdtemp, writeFile, rm, mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { launchChrome } from '../frontend/scripts/cdp.mjs';

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
const button = (text) => `[...document.querySelectorAll('mdui-button,mdui-button-icon')].find(b => (b.textContent.trim() === ${JSON.stringify(text)} || b.getAttribute('aria-label') === ${JSON.stringify(text)}) && !b.disabled)`;
async function click(text) { const expr = button(text); await page.waitFor(`!!(${expr})`, text); await page.eval(`(${expr}).click()`); }
async function file(label, path) { const { root } = await page.send('DOM.getDocument', { depth: -1 }); const { nodeId } = await page.send('DOM.querySelector', { nodeId: root.nodeId, selector: `input[type=file][aria-label="${label}"]` }); await page.send('DOM.setFileInputFiles', { nodeId, files: [path] }); }
async function resize({ width, height }) {
  await page.send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
  await new Promise((r) => setTimeout(r, 600));
}
async function goto(view) {
  await page.eval(`document.querySelector('mdui-navigation-rail-item[value=${view}]').click()`);
  await page.waitFor(`!!document.querySelector('.${view} .preview-frame')`, `${view} 预览区`);
}

/** 预览区与整页是否需要滚动、画布是否整块落在可见区域里。 */
const measure = (view) => `(() => {
  const r = (e) => { const b = e.getBoundingClientRect(); return { x: Math.round(b.x), y: Math.round(b.y), w: Math.round(b.width), h: Math.round(b.height) }; };
  const main = document.querySelector('.editor-main');
  const pane = document.querySelector('.${view}-preview');
  const frame = pane.querySelector('.preview-frame');
  const bottom = document.querySelector('.${view} .workspace-bottom');
  const p = pane.getBoundingClientRect(), f = frame.getBoundingClientRect();
  return {
    pane: r(pane), frame: r(frame), bottom: r(bottom),
    paneScrolls: pane.scrollHeight > pane.clientHeight + 1 || pane.scrollWidth > pane.clientWidth + 1,
    mainScrolls: main.scrollHeight > main.clientHeight + 1 || main.scrollWidth > main.clientWidth + 1,
    frameInside: f.top >= p.top - 1 && f.bottom <= p.bottom + 1 && f.left >= p.left - 1 && f.right <= p.right + 1 && f.bottom <= innerHeight + 1,
    frameShare: +(f.height / innerHeight).toFixed(3),
  };
})()`;

async function check(view, size, label) {
  const m = await page.eval(measure(view));
  const name = `${view}${label ? `-${label}` : ''}-${size.width}x${size.height}`;
  await writeFile(join(out, `${name}.png`), await page.screenshot());
  const pass = !m.paneScrolls && !m.mainScrolls && m.frameInside && m.frame.h >= 120;
  results.push({ name, pass, ...m });
  console.log(`${pass ? 'PASS' : 'FAIL'} ${name} 画布 ${m.frame.w}×${m.frame.h}（占窗口高 ${m.frameShare}）· 底部 ${m.bottom.h}px${m.paneScrolls ? ' · 预览区需要滚动' : ''}${m.mainScrolls ? ' · 整页需要滚动' : ''}${m.frameInside ? '' : ' · 画布超出可见区'}`);
}

try {
  browser.cdp.on((m) => { if (m.method === 'Runtime.exceptionThrown') errors.push(m.params.exceptionDetails.exception?.description ?? 'exception'); });
  page = await browser.openPage(url, sizes[0]);
  await page.send('DOM.enable');
  await page.waitFor(`document.querySelector('.connection')?.textContent === '已连接'`, '连接后端');
  await click('新建工程');
  await page.waitFor(`!!document.querySelector('mdui-dialog.new-project-dialog[open] input[aria-label="选择歌词"]')`, '新建对话框');
  await file('选择音频', wav); await file('选择歌词', lrc);
  await page.waitFor(`!!document.querySelector('.lyrics-plan-summary')`, '歌词识别');
  await click('下一步'); await click('新建');
  await page.waitFor(`document.querySelectorAll('.line-list mdui-list-item').length === 8`, '8 行歌词进入工作区', 120_000);

  for (const view of ['orchestration', 'appearance']) {
    await goto(view);
    await page.waitFor(`document.querySelector('.${view} .preview-frame img')?.naturalWidth > 0`, `${view} 预览帧`, 120_000);
    for (const size of sizes) {
      await resize(size);
      await check(view, size);
      if (view === 'orchestration') {
        // 片段列表展开时底部最高。
        await page.eval(`document.querySelector('.assignment-clips').click()`);
        await page.waitFor(`!!document.querySelector('.clip-panel')`, '片段列表');
        await new Promise((r) => setTimeout(r, 300));
        await check(view, size, 'clips');
        await page.eval(`document.querySelector('.assignment-clips').click()`);
        await page.waitFor(`!document.querySelector('.clip-panel')`, '收起片段列表');
      }
    }
    await resize(sizes[0]);
  }

  await click('更多');
  const item = `[...document.querySelectorAll('mdui-menu-item')].find(i => i.textContent.trim() === '关闭工程' && !i.disabled)`;
  await page.waitFor(`!!(${item})`, '关闭工程菜单');
  await page.eval(`(${item}).dispatchEvent(new MouseEvent('click',{bubbles:true,composed:true}))`);
  await page.waitFor(`!!(${button('不保存')}) || !document.querySelector('mdui-navigation-rail-item[value=lyrics]')`, '关闭或放弃保存');
  if (await page.eval(`!!(${button('不保存')})`)) await click('不保存');
  await page.waitFor(`!document.querySelector('mdui-navigation-rail-item[value=lyrics]')`, '工程已关闭');
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
