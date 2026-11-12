// 隔离 Server profile 上运行：复制模板、编辑辉光、保存重读并应用到新工程；
// 两态（合同 1.50.0）：已唱辉光跟随／单独设置，未唱描边层列表的添加、移动与删除。
// node scripts/glow-smoke.mjs --url http://127.0.0.1:PORT [--screenshot /tmp/glow.png] [--headed]
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { isDeepStrictEqual, parseArgs } from 'node:util';
import { launchChrome } from './cdp.mjs';
import { shell, ui, uiJs } from './ui-query.mjs';

const { values } = parseArgs({ options: { url: { type: 'string', default: 'http://127.0.0.1:5173' }, screenshot: { type: 'string', default: '/tmp/mako-glow.png' }, headed: { type: 'boolean' } } });
const assets = mkdtempSync(join(tmpdir(), 'mako-glow-'));
const audio = join(assets, 'smoke.wav');
const lyrics = join(assets, 'smoke.lrc');
const samples = 22050 * 12;
const wav = Buffer.alloc(44 + samples * 2);
wav.write('RIFF', 0); wav.writeUInt32LE(36 + samples * 2, 4); wav.write('WAVEfmt ', 8);
wav.writeUInt32LE(16, 16); wav.writeUInt16LE(1, 20); wav.writeUInt16LE(1, 22);
wav.writeUInt32LE(22050, 24); wav.writeUInt32LE(44100, 28); wav.writeUInt16LE(2, 32);
wav.writeUInt16LE(16, 34); wav.write('data', 36); wav.writeUInt32LE(samples * 2, 40);
writeFileSync(audio, wav);
writeFileSync(lyrics, '[00:00.50]朝の光浴びて\n[00:03.00]青い空見上げ\n[00:06.00]Hello karaoke\n[00:09.00]你好，世界\n');
const name = `辉光冒烟-${Date.now().toString(36)}`;
const templateName = `document.querySelector('.template-name')?.textContent`;
const sample = `document.querySelector('.template-sample [data-status=ready] img')?.dataset.sha`;
const revision = `(document.querySelector('.status-bar')?.textContent.match(/修订 (\\d+)/) ?? [])[1]`;
const playhead = `(document.querySelector('.transport-time span')?.textContent ?? '')`;
const properties = ['unsung.glow', 'unsung.glow.color', 'unsung.glow.opacity', 'unsung.glow.blurRadiusPx', 'unsung.glow.spreadPx'];
const click = (page, selector) => ui.click(page, selector, null);
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
function check(condition, message) { if (!condition) throw new Error(message); console.log(`✓ ${message}`); }
async function includeGroup(page, group, checked) {
  const selector = `[data-group="${group}"] [role="switch"]`;
  await page.waitFor(`!!document.querySelector(${JSON.stringify(selector)})`, `${group} 属性组开关`);
  if ((await page.eval(`document.querySelector(${JSON.stringify(selector)}).checked`)) !== checked) await click(page, selector);
  await page.waitFor(`document.querySelector(${JSON.stringify(selector)})?.checked === ${checked}`, `${group} 属性组状态`);
}
const includeGlow = (page, checked) => includeGroup(page, 'glow', checked);
async function definition(page) {
  await ui.click(page, 'tab', '高级');
  await page.waitFor(`!!document.querySelector('.template-json')`, '完整定义');
  return JSON.parse(await page.eval(`document.querySelector('.template-json').textContent`));
}
async function appearance(page) { await ui.click(page, 'tab', '外观', { scope: '.template-tabs' }); }
async function save(page) {
  await ui.click(page, 'button', '保存', { scope: '.template-actions' });
  await page.waitFor(`![...document.querySelectorAll('.template-detail .phead .tag')].some(x => x.textContent === '未保存')`, '模板保存', 30000);
}
async function frame(page) {
  const rev = await page.eval(revision);
  await page.waitFor(`!!${playhead} && (document.querySelector('.preview-title')?.textContent ?? '').includes('修订 ${rev} ·') && (document.querySelector('.preview-title')?.textContent ?? '').includes(${playhead}) && !(document.querySelector('.preview-title')?.textContent ?? '').includes('渲染中') && !!document.querySelector('.preview-frame img')`, '预览帧', 60000);
  return page.eval(`(async () => { const bytes = await (await fetch(document.querySelector('.preview-frame img').src)).arrayBuffer(); const hash = new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)); return [...hash].slice(0,8).map(x => x.toString(16).padStart(2,'0')).join(''); })()`);
}
async function shot(page, group, suffix) {
  await page.eval(`document.querySelector('[data-group="${group}"]').scrollIntoView({ block: 'center' })`);
  const path = values.screenshot.replace(/\.png$/i, '') + `-${suffix}.png`;
  await sleep(500); await writeFile(path, await page.screenshot()); console.log(`截图：${path}`);
}
async function strokeWidths(page) {
  return (await definition(page)).style.unsung.strokes.map((l) => l.widthPx);
}
async function screenshot(page, suffix) {
  await page.eval(`document.querySelector('[data-group="glow"]').scrollIntoView({ block: 'center' })`);
  await page.waitFor(uiJs(`const el = document.querySelector('[data-group="glow"]'); if (!el || !__ui.find('slider', null, false, '[data-group="glow"]')) return false; const r = el.getBoundingClientRect(); return r.top >= 0 && r.bottom <= innerHeight && r.left >= 0 && r.right <= innerWidth;`), `${suffix} 辉光控件可见`);
  const path = values.screenshot.replace(/\.png$/i, '') + `-${suffix}.png`;
  await sleep(500); await writeFile(path, await page.screenshot()); console.log(`截图：${path}`);
  const overflow = await page.eval(`({ viewport: document.documentElement.clientWidth, scroll: document.documentElement.scrollWidth })`);
  check(overflow.scroll <= overflow.viewport + 1, `${suffix} 页面无水平溢出 (${overflow.scroll}/${overflow.viewport})`);
}
async function theme(page, mode, label) {
  await ui.click(page, 'button', '应用设置');
  await ui.click(page, 'button', label);
  await page.waitFor(`document.documentElement.dataset.mode === ${JSON.stringify(mode)}`, `${label}主题`);
  for (const type of ['keyDown', 'keyUp']) await page.send('Input.dispatchKeyEvent', { type, key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
  await page.waitFor(`!document.querySelector('[role=dialog]')`, '关闭应用设置');
}

let browser, page, createdId;
const errors = [];
try {
  browser = await launchChrome({ headed: values.headed });
  browser.cdp.on((m) => { if (m.method === 'Runtime.exceptionThrown') errors.push(m.params.exceptionDetails.exception?.description ?? 'exception'); });
  page = await browser.openPage(new URL(values.url).origin, { width: 1440, height: 900 });
  await page.send('DOM.enable');
  await page.waitFor(shell.connected, '连接 Server');
  await shell.newProject(page, { audio, lyrics, name });
  await page.waitFor(`document.querySelectorAll('.line-list [role=row]').length === 4`, '四行歌词', 120000);
  await shell.goto(page, 'library');
  await page.waitFor(`!!document.querySelector('.library-list [data-template-id="builtin:line-classic"]')`, '样式库');
  await click(page, '[role=row]:has([data-template-id="builtin:line-classic"])');
  await page.waitFor(`${templateName} === '经典整行'`, '经典整行');
  await ui.click(page, 'button', '复制并编辑');
  await ui.waitDialog(page, '复制到我的模板');
  await ui.fill(page, '名称', name, { scope: '@dialog' });
  await ui.clickInDialog(page, '复制');
  await page.waitFor(`${templateName} === ${JSON.stringify(name)}`, '复制模板', 30000);
  createdId = await page.eval(`[...document.querySelectorAll('.library-list [data-template-id]')].find(x => x.textContent.includes(${JSON.stringify(name)}))?.dataset.templateId`);
  check(!!createdId, '复制内置模板');
  await page.waitFor(`!!${sample}`, '原始试样', 60000);
  const initial = await page.eval(sample);
  await includeGlow(page, true);
  await ui.choose(page, '未唱辉光开关', '有辉光');
  await ui.fill(page, '未唱辉光颜色', '#ff20c0', { enter: true });
  await ui.fill(page, '扩张', '8', { scope: '[data-group="glow"]', enter: true });
  await ui.fill(page, '模糊', '18', { scope: '[data-group="glow"]', enter: true });
  const sliderPoint = await page.eval(`(() => { const el = document.querySelector('[data-group="glow"] .slider'); el.scrollIntoView({block:'center'}); const r = el.getBoundingClientRect(); return {x:r.left+r.width*0.65, y:r.top+r.height/2}; })()`);
  for (const type of ['mouseMoved', 'mousePressed', 'mouseReleased']) await page.send('Input.dispatchMouseEvent', { type, ...sliderPoint, button: type === 'mouseMoved' ? 'none' : 'left', buttons: type === 'mousePressed' ? 1 : 0, clickCount: 1 });
  await page.waitFor(`!!${sample} && ${sample} !== ${JSON.stringify(initial)}`, '辉光草稿试样', 60000);
  check(true, '辉光草稿改变试样');
  let d = await definition(page);
  check(properties.every(p => d.properties.includes(p)) && isDeepStrictEqual(d.style.unsung.glow?.color, { red: 255, green: 32, blue: 192, alpha: 255 }) && d.style.unsung.glow?.blurRadiusPx === 18 && d.style.unsung.glow?.spreadPx === 8 && Math.abs(d.style.unsung.glow?.opacity - 0.65) < 0.011, '辉光属性、颜色、透明度、模糊和扩张进入草稿');
  const glow = d.style.unsung.glow;
  await appearance(page);
  await theme(page, 'light', '浅色');
  await screenshot(page, 'light-wide');
  await theme(page, 'dark', '深色');
  await page.send('Emulation.setDeviceMetricsOverride', { width: 800, height: 900, deviceScaleFactor: 1, mobile: false });
  await page.waitFor(`document.querySelector('.library')?.dataset.width === 'bothOverlay'`, '窄屏布局');
  await ui.click(page, 'button', '模板编辑', { scope: '.library' });
  await screenshot(page, 'dark-narrow');
  await page.send('Emulation.setDeviceMetricsOverride', { width: 1440, height: 900, deviceScaleFactor: 1, mobile: false });
  await page.waitFor(`document.querySelector('.library')?.dataset.width !== 'bothOverlay'`, '恢复宽屏布局');
  // 已唱辉光：单独设置从未唱取值起步，再改回跟随。
  await includeGroup(page, 'sungGlow', true);
  await ui.choose(page, '已唱辉光开关', '单独设置');
  d = await definition(page);
  check(d.properties.includes('sung.glow') && d.style.sung.glow.kind === 'set' && isDeepStrictEqual(d.style.sung.glow.value, glow), '已唱辉光单独设置从未唱取值起步');
  await appearance(page);
  await ui.choose(page, '已唱辉光开关', '跟随未唱');
  d = await definition(page);
  check(d.style.sung.glow.kind === 'follow', '已唱辉光改回跟随未唱');
  await appearance(page);
  await includeGroup(page, 'sungGlow', false);
  // 未唱描边层列表：加外圈沿用最外层，改宽度、向内移、删除；满 4 层不能再加。
  const beforeStroke = await page.eval(sample);
  await includeGroup(page, 'stroke', true);
  await ui.choose(page, '未唱描边开关', '有描边');
  let widths = await strokeWidths(page);
  await appearance(page);
  const n = widths.length;
  await ui.click(page, 'button', '加一层外圈', { scope: '[data-group="stroke"]' });
  await ui.fill(page, '宽度', '9', { scope: `[data-group="stroke"] [data-layer="${n}"]`, enter: true });
  widths = await strokeWidths(page);
  check(widths.length === n + 1 && widths[n] === 9, `加一层外圈并改宽度（${widths.join('/')} px）`);
  await appearance(page);
  if (n > 0) {
    await ui.click(page, 'button', `未唱描边第 ${n + 1} 层向内移`, { scope: '[data-group="stroke"]' });
    const moved = await strokeWidths(page);
    check(moved[n - 1] === 9 && moved[n] === widths[n - 1], `外圈向内移一层（${moved.join('/')} px）`);
    await appearance(page);
  }
  while ((await page.eval(`document.querySelectorAll('[data-group="stroke"] .stroke-layer').length`)) < 4) await ui.click(page, 'button', '加一层外圈', { scope: '[data-group="stroke"]' });
  check(await page.eval(`document.querySelector('[data-group="stroke"] .stroke-layer-actions button').disabled || document.querySelector('[data-group="stroke"] .stroke-layer-actions button').getAttribute('aria-disabled') === 'true' || document.querySelector('[data-group="stroke"] .stroke-layer-actions button').hasAttribute('data-disabled')`), '满 4 层后不能再加');
  await page.waitFor(`!!${sample} && ${sample} !== ${JSON.stringify(beforeStroke)}`, '多层描边试样', 60000);
  check(true, '多层描边改变试样');
  await shot(page, 'stroke', 'stroke-layers');
  await ui.click(page, 'button', '删除未唱描边第 4 层', { scope: '[data-group="stroke"]' });
  check((await strokeWidths(page)).length === 3, '删除一层描边');
  await appearance(page);
  await includeGroup(page, 'stroke', false);
  await save(page);
  await click(page, '[role=row]:has([data-template-id="builtin:line-classic"])');
  await click(page, `[role=row]:has([data-template-id="${createdId}"])`);
  await page.waitFor(`${templateName} === ${JSON.stringify(name)}`, '重选模板');
  d = await definition(page);
  check(isDeepStrictEqual(d.style.unsung.glow, glow) && properties.every(p => d.properties.includes(p)), '保存并重选后辉光定义保持');
  await appearance(page);
  await includeGlow(page, false);
  d = await definition(page);
  check(properties.every(p => !d.properties.includes(p)), '关闭属性组后辉光不写入模板');
  await appearance(page);
  await includeGlow(page, true);
  await ui.choose(page, '未唱辉光开关', '无辉光');
  d = await definition(page);
  check(properties.every(p => d.properties.includes(p)) && d.style.unsung.glow === null, '组内关闭保留属性并写入 null');
  await appearance(page);
  await ui.choose(page, '未唱辉光开关', '有辉光');
  await ui.fill(page, '未唱辉光颜色', '#ff20c0', { enter: true });
  await ui.fill(page, '扩张', '8', { scope: '[data-group="glow"]', enter: true });
  await ui.fill(page, '模糊', '18', { scope: '[data-group="glow"]', enter: true });
  await save(page);
  await shell.goto(page, 'appearance');
  await page.waitFor(`!!document.querySelector('.appearance .timeline-canvas canvas')`, '外观页');
  await ui.click(page, 'button', '全曲', { scope: '.appearance .timeline-toolbar' });
  if (await ui.exists(page, 'button', '关闭提示')) await ui.click(page, 'button', '关闭提示');
  const pos = await page.eval(`(() => { const r = document.querySelector('.appearance .timeline-canvas canvas').getBoundingClientRect(); const span = Number(document.querySelector('.appearance .timeline-span')?.textContent.match(/可见 ([\\d.]+) 秒/)?.[1] ?? '12'); return { x:r.x+r.width*1.5/span, y:r.y+10 }; })()`);
  for (const type of ['mousePressed', 'mouseReleased']) await page.send('Input.dispatchMouseEvent', { type, ...pos, button: 'left', clickCount: 1 });
  await page.waitFor(`${playhead}.startsWith('0:01.5')`, '定位到歌词');
  const before = await frame(page);
  await click(page, '.appearance-targets .at.line-row');
  await ui.click(page, 'button', '选择模板…', { scope: '.template-grp' });
  await ui.waitDialog(page, '选择模板');
  await page.waitFor(`!!document.querySelector('.template-picker .template-card[data-template-id="${createdId}"]')`, '辉光模板');
  await click(page, `.template-picker .template-card[data-template-id="${createdId}"]`);
  await ui.clickInDialog(page, '应用到 1 行', { timeout: 60000 });
  await ui.waitDialogClosed(page, '选择模板', { timeout: 30000 });
  const after = await frame(page);
  check(before !== after, '行应用辉光模板后预览变化');
  if (await ui.exists(page, 'button', '展开外观来源', { scope: '.style-sources' })) await ui.click(page, 'button', '展开外观来源', { scope: '.style-sources' });
  check((await page.eval(`document.querySelector('.appearance-inspector')?.innerText ?? ''`)).includes(name), '外观来源或分配摘要显示模板名称');
  await ui.click(page, 'button', '撤销', { scope: '.applied-banner' });
  check((await frame(page)) === before, '撤销恢复原预览');
  await shell.goto(page, 'library');
  await page.waitFor(`!!document.querySelector('.library-list [data-template-id="${createdId}"]')`, '返回模板');
  await click(page, `[role=row]:has([data-template-id="${createdId}"])`);
  await ui.menu(page, '模板操作', '删除…');
  await ui.clickInDialog(page, '删除');
  await page.waitFor(`!document.querySelector('.library-list [data-template-id="${createdId}"]')`, '删除模板');
  createdId = undefined;
  check(errors.length === 0, `页面无未处理异常 ${errors.join(' | ')}`);
} catch (error) {
  process.exitCode = 1;
  console.error(`✗ ${error.stack ?? error}`);
  if (page) {
    const failure = join(tmpdir(), 'mako-glow-smoke-failure.png');
    await writeFile(failure, await page.screenshot()).catch(() => {});
    console.error(`失败截图：${failure}`);
    console.error((await page.eval(`document.body.innerText`).catch(() => '')).slice(0, 1000));
  }
  if (createdId) console.error(`隔离样式库可能留下 ${name} (${createdId})`);
} finally {
  if (browser) await browser.close();
  rmSync(assets, { recursive: true, force: true });
}
