// 样式库与外观页的浏览器冒烟（W3 设计 §5，W4 设计 §9.5）：用真实 Chrome 走一遍 W3 的验收链路。
// 新建工程 → 样式库：内置只读、复制并编辑颜色、保存后重读一致、试样与缩略图（G17：改草稿试样跟着变、不写库）、改名
// → 歌词页与外观页切换时播放头和播放状态保持
// → 外观页：多选两行、选择器灰显不适用的模板、试看（G16：不写工程，帧与应用后相同）、一次应用一条历史（预览帧变化，
//   撤销后帧与应用前相同）、重做
// → 分配管理（G15）：目标浏览器标模板名、停用／启用、库里改模板工程不变、按库里当前版本更新、移除后撤销恢复
// → 文本单元应用逐字强调 → 删除库里的模板后工程里的分配照常（不能再更新）
// → 含效果的模板（W7 设计 §5 第 5～8 条）：样式库里做“波纹”（包材质）和“读来源”（读取输入“翻译”）两个模板，新建工程并造出片段和前奏提示组；
//   行上试看与应用、撤销后帧不变；文本单元上灰显；没选来源不能应用，选好后分配写明映射；
//   应用到默认容器（包括里面的句子）、片段（片段外的句子不变）、前奏提示组；时间轴片段轨点选同步；
//   外观来源写明分配和目标，点来源选中那个目标；在样式库中编辑副本，保存并返回试看，应用后撤销 → 删除本次的模板。
//
// 前提：pnpm dev:backend 与 pnpm dev 都在运行；效果包取后端仓库 samples/ScriptEffects（找不到时跳过含效果模板的部分并写明原因）。
// 个人库里只增删本次创建的模板。
// 用法：node scripts/appearance-smoke.mjs [--url http://localhost:5173] [--screenshot out.png] [--headed]

import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { parseArgs } from 'node:util';
import { launchChrome } from './cdp.mjs';
import { packSampleEffect, sampleEffectsDir } from './effect-packages.mjs';

const { values } = parseArgs({
  options: {
    url: { type: 'string', default: 'http://localhost:5173' },
    headed: { type: 'boolean' },
    screenshot: { type: 'string' },
  },
});
const ORIGIN = new URL(values.url).origin;

// ── 素材：12 秒 WAV 与四行 LRC（与 editor-smoke 相同） ──

function makeWav(seconds = 12, sampleRate = 22050) {
  const samples = seconds * sampleRate;
  const buffer = Buffer.alloc(44 + samples * 2);
  buffer.write('RIFF', 0, 'ascii');
  buffer.writeUInt32LE(36 + samples * 2, 4);
  buffer.write('WAVEfmt ', 8, 'ascii');
  buffer.writeUInt32LE(16, 16);
  buffer.writeUInt16LE(1, 20);
  buffer.writeUInt16LE(1, 22);
  buffer.writeUInt32LE(sampleRate, 24);
  buffer.writeUInt32LE(sampleRate * 2, 28);
  buffer.writeUInt16LE(2, 32);
  buffer.writeUInt16LE(16, 34);
  buffer.write('data', 36, 'ascii');
  buffer.writeUInt32LE(samples * 2, 40);
  for (let i = 0; i < samples; i++) {
    const t = i / sampleRate;
    const envelope = Math.exp(-(t % 1) * 4) * Math.min(1, i / 500, (samples - i) / 500);
    buffer.writeInt16LE(Math.round(Math.sin(2 * Math.PI * 330 * t) * 12000 * envelope), 44 + i * 2);
  }
  return buffer;
}

const assets = mkdtempSync(join(tmpdir(), 'mako-appearance-smoke-'));
const wavPath = join(assets, '外观冒烟.wav');
const lrcPath = join(assets, '外观冒烟.lrc');
writeFileSync(wavPath, makeWav());
writeFileSync(lrcPath, '[00:00.50]朝の光浴びて\n[00:03.00]青い空見上げ\n[00:06.00]Hello karaoke\n[00:09.00]你好，世界\n');
const stamp = Date.now().toString(36);
const copyName = `冒烟红-${stamp}`;
const renamed = `冒烟改名-${stamp}`;
const waveName = `冒烟波纹-${stamp}`;
const inputName = `冒烟读来源-${stamp}`;
const inkPath = join(assets, 'CustomInk.mkfx');
const emphasisPath = join(assets, 'WordEmphasis.mkfx');

// ── 页面操作 ──

const checks = [];
function check(ok, label) {
  checks.push({ ok: !!ok, label });
  console.log(`${ok ? '✓' : '✗'} ${label}`);
  if (!ok) process.exitCode = 1;
}

const PRELUDE = `
  const buttons = (scope = document) => [...scope.querySelectorAll('mdui-button, mdui-button-icon')];
  const button = (text, scope) => buttons(scope).find((b) => (b.textContent.trim() === text || b.getAttribute('aria-label') === text) && !b.disabled);
  const anyButton = (text, scope) => buttons(scope).find((b) => b.textContent.trim() === text || b.getAttribute('aria-label') === text);
  const field = (label) => [...document.querySelectorAll('mdui-text-field')].find((f) => f.label === label);
  const openDialog = () => [...document.querySelectorAll('mdui-dialog')].find((d) => d.open);
  const revision = () => (document.querySelector('.status-bar')?.textContent.match(/修订 (\\d+)/) ?? [])[1];
  const templateName = () => document.querySelector('.template-name')?.textContent;
  const lineRow = (i) => document.querySelectorAll('.target-row.level-2')[i];
  const appRow = () => document.querySelector('.application-row');
  const sampleSha = () => document.querySelector('.template-sample [data-status=ready] img')?.dataset.sha;
  const sampleTitle = () => document.querySelector('.template-sample .preview-title')?.textContent ?? '';
  const readyThumbs = (scope) => document.querySelectorAll(scope + ' .template-thumb[data-status=ready] img').length;
`;
const js = (body) => `(() => { ${PRELUDE} ${body} })()`;
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/** mdui 组件覆盖了 click()，转给内部元素时不一定冒泡到 React；菜单项和对话框按钮用真实鼠标事件。 */
async function realClick(page, expression) {
  const r = await page.eval(`(() => { const b = (${expression}).getBoundingClientRect(); return { x: b.x + b.width / 2, y: b.y + b.height / 2 }; })()`);
  for (const type of ['mousePressed', 'mouseReleased']) await page.send('Input.dispatchMouseEvent', { type, x: r.x, y: r.y, button: 'left', clickCount: 1 });
}

/** 点文字为 text 的按钮；scope 是容器的选择器表达式。 */
async function clickButton(page, text, scope = 'document') {
  const target = js(`return button(${JSON.stringify(text)}, ${scope});`);
  await page.waitFor(`!!${target}`, `按钮「${text}」可用`);
  await page.eval(`${target}.click()`);
}

async function clickDialogButton(page, text) {
  const target = js(`return openDialog() && button(${JSON.stringify(text)}, openDialog());`);
  await page.waitFor(`!!${target}`, `对话框按钮「${text}」可用`);
  await page.eval(`${target}.dispatchEvent(new MouseEvent('click', { bubbles: true, composed: true }))`);
}

async function menuItem(page, trigger, text) {
  await page.eval(`document.querySelector('[aria-label=${JSON.stringify(trigger)}]').click()`);
  const item = `[...document.querySelectorAll('mdui-menu-item')].find((i) => i.textContent.trim() === ${JSON.stringify(text)} && !i.disabled)`;
  await page.waitFor(`!!${item} && ${item}.getBoundingClientRect().height > 0`, `菜单项「${text}」`);
  await sleep(200);
  await realClick(page, item);
}

async function typeInto(page, label, value, { enter = false, inDialog = false } = {}) {
  const f = inDialog ? `openDialog()?.querySelector('mdui-text-field')` : `field(${JSON.stringify(label)})`;
  await page.waitFor(js(`return !!${f};`), `输入框「${label}」`);
  await page.eval(js(`const f = ${f}; f.value = ${JSON.stringify(value)}; f.dispatchEvent(new Event('input', { bubbles: true, composed: true }));`));
  if (enter) {
    await sleep(50);
    await page.eval(js(`${f}.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, composed: true }));`));
  }
}

async function key(page, keyName, code, keyCode, modifiers = 0) {
  for (const type of ['keyDown', 'keyUp']) await page.send('Input.dispatchKeyEvent', { type, key: keyName, code, windowsVirtualKeyCode: keyCode, modifiers });
}

async function gotoPage(page, value, ready) {
  await page.eval(`document.querySelector('mdui-navigation-rail-item[value=${value}]').click()`);
  await page.waitFor(ready, `进入 ${value}`);
}

async function setFile(page, ariaLabel, path) {
  const { root } = await page.send('DOM.getDocument', { depth: -1 });
  const { nodeId } = await page.send('DOM.querySelector', { nodeId: root.nodeId, selector: `input[type=file][aria-label="${ariaLabel}"]` });
  await page.send('DOM.setFileInputFiles', { nodeId, files: [path] });
}

const playheadText = `(document.querySelector('.transport-time span')?.textContent ?? '')`;
const currentRevision = (page) => page.eval(js(`return revision();`));

/** 等当前修订在播放头时刻的预览帧渲染好，返回 PNG 的 SHA-256 前 16 位。 */
async function frameHash(page, label) {
  const rev = await currentRevision(page);
  const title = `document.querySelector('.preview-title')?.textContent ?? ''`;
  // 标题里的时刻要与播放头一致：定位后不会拿到上一个时刻的帧。
  await page.waitFor(
    `!!${playheadText} && (${title}).includes('修订 ${rev} ·') && (${title}).includes(${playheadText}) && !(${title}).includes('渲染中') && !!document.querySelector('.preview-frame img')`,
    `${label}（修订 ${rev}）`,
    60_000,
  );
  await sleep(200);
  return page.eval(`(async () => {
    const bytes = await (await fetch(document.querySelector('.preview-frame img').src)).arrayBuffer();
    const hash = new Uint8Array(await crypto.subtle.digest('SHA-256', bytes));
    return [...hash].slice(0, 8).map((x) => x.toString(16).padStart(2, '0')).join('');
  })()`);
}

/** 在首页用新建对话框建工程（12 秒 WAV、四行 LRC）。 */
async function newProject(page) {
  await clickButton(page, '新建工程');
  await page.waitFor(`!!document.querySelector('mdui-dialog.new-project-dialog[open] input[aria-label="选择歌词"]')`, '新建工程对话框');
  await setFile(page, '选择音频', wavPath);
  await setFile(page, '选择歌词', lrcPath);
  await page.waitFor(`!!document.querySelector('.lyrics-plan-summary')`, '歌词识别结果');
  await page.waitFor(`document.body.innerText.includes('外观冒烟.wav')`, '文件已选择');
  await clickButton(page, '下一步');
  await clickButton(page, '新建');
  await page.waitFor(`document.querySelectorAll('.line-list mdui-list-item').length === 4`, '工作区显示 4 行', 120_000);
}

/** 更多菜单“关闭工程”；有未保存的修改时选“不保存”。在样式库里关闭时留在样式库，再从导航栏回首页。 */
async function closeProject(page) {
  await menuItem(page, '更多', '关闭工程');
  const closed = `!document.querySelector('mdui-navigation-rail-item[value=lyrics]')`;
  const discard = js(`return openDialog() && button('不保存', openDialog());`);
  await page.waitFor(`${closed} || !!${discard}`, '关闭工程', 60_000);
  if (await page.eval(`!!${discard}`)) await clickDialogButton(page, '不保存');
  await page.waitFor(closed, '工程已关闭', 60_000);
  await gotoPage(page, 'home', `!!document.querySelector('.home')`);
}

/** 文本框改值后按 Enter 提交（中间等一下，让 React 先收到 input）。 */
async function commit(page, expression, value) {
  await page.waitFor(`!!(${expression})`, `输入框 ${expression}`);
  await page.eval(`(() => { const f = ${expression}; f.value = ${JSON.stringify(value)}; f.dispatchEvent(new Event('input', { bubbles: true, composed: true })); })()`);
  await sleep(80);
  await page.eval(`(${expression}).dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, composed: true }))`);
}

async function choose(page, expression, value) {
  await page.eval(`(() => { const s = ${expression}; s.value = ${JSON.stringify(value)}; s.dispatchEvent(new Event('change')); })()`);
}

/** 样式库：把“经典整行”复制成 name 并进入编辑。 */
async function copyClassic(page, name) {
  await page.eval(`document.querySelector('[data-template-id="builtin:line-classic"]').click()`);
  await page.waitFor(js(`return templateName() === '经典整行' && !!button('复制并编辑');`), '内置详情');
  await clickButton(page, '复制并编辑');
  await typeInto(page, '名称', name, { inDialog: true });
  await clickDialogButton(page, '复制');
  await page.waitFor(js(`return templateName() === ${JSON.stringify(name)} && !!document.querySelector('.template-tabs');`), `复制为 ${name}`);
}

async function libraryTab(page, value) {
  await page.eval(`document.querySelector('mdui-tab[value=${value}]').click()`);
  await sleep(150);
}

async function importPackage(page, path, packageId) {
  await setFile(page, '导入效果包…', path);
  await page.waitFor(`!!document.querySelector('.package-pending[data-status=ready]')`, '效果包检查', 60_000);
  await page.eval(js(`button('加入模板', document.querySelector('.package-pending')).click();`));
  await page.waitFor(`!!document.querySelector('.package-item[data-package-id="${packageId}"]')`, `加入 ${packageId}`);
}

const dirty = `[...document.querySelectorAll('.tag')].some((t) => t.textContent.includes('有未保存的修改'))`;
async function saveTemplate(page) {
  await clickButton(page, '保存', `document.querySelector('.template-actions')`);
  await page.waitFor(`!${dirty}`, '保存完成', 30_000);
}

/** 目标浏览器里标签为 label 的一行（编排对象、行）。 */
const targetRow = (label) => `[...document.querySelectorAll('.target-row')].find((r) => r.querySelector('.target-label')?.textContent === ${JSON.stringify(label)})`;
async function selectRow(page, label, path) {
  await page.waitFor(`!!${targetRow(label)}`, `目标「${label}」`, 30_000);
  await page.eval(`${targetRow(label)}.click()`);
  await page.waitFor(`document.querySelector('.inspector-path')?.textContent.includes(${JSON.stringify(path)})`, `选中「${label}」`);
}

/** 点时间轴：seconds 秒处，y 是相对画布顶部的偏移（负数从底部算）。 */
async function clickTimeline(page, seconds, y = 10) {
  const r = await page.eval(`(() => { const r = document.querySelector('.timeline canvas').getBoundingClientRect(); return { x: r.x, y: r.y, w: r.width, h: r.height }; })()`);
  const span = Number(await page.eval(`document.querySelector('.appearance')?.innerText.match(/可见 (\\d+) 秒/)?.[1] ?? '12'`));
  for (const type of ['mousePressed', 'mouseReleased']) {
    await page.send('Input.dispatchMouseEvent', { type, x: r.x + r.w * (seconds / span), y: y >= 0 ? r.y + y : r.y + r.h + y, button: 'left', clickCount: 1 });
  }
}

async function seek(page, seconds) {
  await clickTimeline(page, seconds);
  await page.waitFor(
    `(() => { const m = ${playheadText}.match(/(\\d+):(\\d+\\.\\d+)/); return !!m && Math.abs(Number(m[1]) * 60 + Number(m[2]) - ${seconds}) < 0.15; })()`,
    `定位到 ${seconds} 秒`,
  );
}

/** 打开选择器，等 name 的卡片出来；返回卡片的表达式。 */
async function pickTemplate(page, name) {
  await page.eval(js(`(button('选择模板…') ?? button('换模板…')).click();`));
  const card = `[...document.querySelectorAll('.template-picker .template-card')].find((c) => c.querySelector('.template-card-name')?.textContent === ${JSON.stringify(name)})`;
  await page.waitFor(`!!${card}`, `选择器里的「${name}」`, 30_000);
  // 等试样选项读到，卡片的适用说明稳定下来。
  await sleep(800);
  return card;
}

async function applyTemplate(page, name, countText) {
  const card = await pickTemplate(page, name);
  await page.eval(`${card}.click()`);
  await page.waitFor(js(`return !!button(${JSON.stringify(`应用到 ${countText}`)}, openDialog());`), `应用按钮（${countText}）`, 60_000);
  const before = Number(await currentRevision(page));
  await clickDialogButton(page, `应用到 ${countText}`);
  await page.waitFor(js(`return !openDialog() && Number(revision()) === ${before + 1};`), `应用「${name}」`, 30_000);
}

async function undo(page) {
  const before = Number(await currentRevision(page));
  await page.eval(`document.activeElement?.blur()`);
  await key(page, 'z', 'KeyZ', 90, 2);
  await page.waitFor(js(`return Number(revision()) === ${before + 1};`), '撤销', 30_000);
}

/** 在样式库里删掉名称以 prefix 开头的个人模板。 */
async function deleteTemplates(page, prefix) {
  const named = `[...document.querySelectorAll('.library-list [data-template-id]')].find((c) => c.textContent.includes(${JSON.stringify(prefix)}))`;
  while (await page.eval(`!!${named}`)) {
    const id = await page.eval(`${named}.dataset.templateId`);
    await page.eval(`${named}.click()`);
    await page.waitFor(js(`return (templateName() ?? '').startsWith(${JSON.stringify(prefix)}) && !!document.querySelector('[aria-label="模板操作"]');`), `选中 ${prefix}`);
    await menuItem(page, '模板操作', '删除');
    await clickDialogButton(page, '删除');
    await page.waitFor(`!document.querySelector('.library-list [data-template-id="${id}"]')`, `删除 ${id}`);
  }
}

async function snapshot(page, suffix) {
  if (!values.screenshot) return;
  await sleep(700);
  await writeFile(values.screenshot.replace(/\.png$/, `-${suffix}.png`), await page.screenshot());
}

const browser = await launchChrome({ headed: values.headed });
let page;
const errors = [];
let copied = false;
try {
  browser.cdp.on((message) => {
    if (message.method === 'Runtime.exceptionThrown') errors.push(message.params.exceptionDetails.exception?.description ?? 'exception');
    if (message.method === 'Runtime.consoleAPICalled' && message.params.type === 'error') {
      errors.push(message.params.args.map((a) => a.value ?? a.description).join(' '));
    }
  });

  page = await browser.openPage(`${ORIGIN}/`, { width: 1440, height: 900 });
  await page.send('DOM.enable');
  // 数歌词窗口的读取次数（W4 设计 §2.5）：资源计时的缓冲区默认只有 250 条。
  await page.eval(`performance.setResourceTimingBufferSize(100000)`);
  await page.waitFor(`document.querySelector('.connection')?.textContent === '已连接'`, '连接 Server');

  // 1. 新建工程
  await newProject(page);
  check(true, '新建工程（4 行），导航栏出现“外观”“样式库”');

  // 2. 样式库：内置只读；复制并编辑；保存后重读一致；改名
  await gotoPage(page, 'library', `!!document.querySelector('[data-template-id="builtin:line-classic"]')`);
  await page.eval(`document.querySelector('[data-template-id="builtin:line-classic"]').click()`);
  await page.waitFor(js(`return templateName() === '经典整行' && !!button('复制并编辑');`), '内置详情');
  check(await page.eval(js(`return !button('保存') && !field('未唱颜色');`)), '内置模板只读：只有“复制并编辑”，没有可编辑的表单');
  await clickButton(page, '复制并编辑');
  await typeInto(page, '名称', copyName, { inDialog: true });
  await clickDialogButton(page, '复制');
  await page.waitFor(js(`return templateName() === ${JSON.stringify(copyName)} && !!field('未唱颜色');`), '复制后进入编辑');
  copied = true;
  await typeInto(page, '未唱颜色', '#20c040', { enter: true });
  await typeInto(page, '已唱颜色', '#ff2020', { enter: true });
  await clickButton(page, '保存');
  await page.waitFor(`[...document.querySelectorAll('.tag')].some((t) => t.textContent === '我的模板')`, '保存完成');
  // 回到内置再选回来：详情按新的库修订重读。
  await page.eval(`document.querySelector('[data-template-id="builtin:line-classic"]').click()`);
  await page.waitFor(js(`return templateName() === '经典整行';`), '切到内置');
  await page.eval(js(`[...document.querySelectorAll('[data-template-id]')].find((c) => c.textContent.includes(${JSON.stringify(copyName)})).click();`));
  await page.waitFor(js(`return templateName() === ${JSON.stringify(copyName)} && !!field('未唱颜色');`), '选回副本');
  const colors = await page.eval(js(`return [field('未唱颜色').value, field('已唱颜色').value];`));
  check(colors[0] === '#20c040' && colors[1] === '#ff2020', `复制内置模板并改颜色，保存后重读的定义与草稿一致（${colors.join('、')}）`);

  // 2b. 样式库试样（G17）：缩略图；草稿改动后试样跟着变，不写库；放弃修改后回到已保存的画面
  await page.waitFor(js(`return !!sampleSha() && readyThumbs('.library-list') >= 3;`), '试样与缩略图', 60_000);
  const savedSample = await page.eval(js(`return sampleSha();`));
  const libraryWrites = () => page.eval(`performance.getEntriesByType('resource').filter((e) => e.name.includes('/template-libraries/personal/commands')).length`);
  const writesBefore = await libraryWrites();
  await typeInto(page, '未唱颜色', '#2040c0', { enter: true });
  await page.waitFor(js(`return sampleTitle().includes('未保存的修改') && !!sampleSha() && sampleSha() !== ${JSON.stringify(savedSample)};`), '草稿试样', 60_000);
  const draftSample = await page.eval(js(`return sampleSha();`));
  const writesAfter = await libraryWrites();
  await clickButton(page, '放弃修改');
  await page.waitFor(js(`return !sampleTitle().includes('未保存的修改') && sampleSha() === ${JSON.stringify(savedSample)};`), '放弃修改后的试样', 60_000);
  check(
    writesAfter === writesBefore,
    `样式库试样：列表有缩略图；改草稿颜色后试样变化（${savedSample.slice(0, 16)} → ${draftSample.slice(0, 16)}），没有写库；放弃修改后回到已保存的画面`,
  );
  await menuItem(page, '模板操作', '改名');
  await typeInto(page, '名称', renamed, { inDialog: true });
  await clickDialogButton(page, '改名');
  await page.waitFor(js(`return templateName() === ${JSON.stringify(renamed)};`), '改名后');
  check(await page.eval(`document.querySelector('.library-list')?.textContent.includes(${JSON.stringify(renamed)})`), `改名：列表与详情都显示“${renamed}”`);
  await snapshot(page, 'library');

  // 3. 歌词页与外观页共用播放器：定位、播放，切换页面后播放头和播放状态保持
  await gotoPage(page, 'appearance', `!!document.querySelector('.appearance .timeline canvas')`);
  await clickButton(page, '全曲');
  const rect = await page.eval(`(() => { const r = document.querySelector('.timeline canvas').getBoundingClientRect(); return { x: r.x, y: r.y, w: r.width }; })()`);
  for (const type of ['mousePressed', 'mouseReleased']) await page.send('Input.dispatchMouseEvent', { type, x: rect.x + rect.w * (1.5 / 12), y: rect.y + 10, button: 'left', clickCount: 1 });
  await page.waitFor(`${playheadText}.startsWith('0:01.5')`, '定位到 1.5 秒');
  await gotoPage(page, 'lyrics', `!!document.querySelector('.line-list')`);
  const kept = await page.eval(playheadText);
  await clickButton(page, '播放');
  await sleep(400);
  await gotoPage(page, 'appearance', `!!document.querySelector('.appearance')`);
  await sleep(400);
  const playing = await page.eval(js(`return !!button('暂停');`));
  await clickButton(page, '暂停');
  check(kept.startsWith('0:01.5') && playing, `切换页面：播放头保持（${kept}），歌词页开始播放后到外观页仍在播放`);
  // 回到 1.5 秒，后面的预览帧都取这个时刻。播放时视口跟着播放头走，先回到“全曲”。
  await clickButton(page, '全曲');
  await sleep(200);
  for (const type of ['mousePressed', 'mouseReleased']) await page.send('Input.dispatchMouseEvent', { type, x: rect.x + rect.w * (1.5 / 12), y: rect.y + 10, button: 'left', clickCount: 1 });
  await page.waitFor(`${playheadText}.startsWith('0:01.5')`, '回到 1.5 秒');
  const h0 = await frameHash(page, '应用前的帧');

  // 4. 多选两行，选择器灰显不适用的模板，一次应用
  await page.eval(js(`lineRow(0).click();`));
  await page.eval(js(`lineRow(1).dispatchEvent(new MouseEvent('click', { bubbles: true, ctrlKey: true }));`));
  await page.waitFor(`document.querySelector('.inspector-path')?.textContent.includes('第 1、2 行')`, '选中两行');
  await clickButton(page, '选择模板…');
  const tokenCard = `document.querySelector('.template-card[data-template-id="builtin:token-highlight"]')`;
  await page.waitFor(`!!${tokenCard}`, '选择器卡片');
  const reason = await page.eval(`${tokenCard}.classList.contains('blocked') ? ${tokenCard}.querySelector('.template-card-reason')?.textContent : ''`);
  check(reason === '文本单元用途，只能用于文本单元', `不适用的模板灰显并写明原因：“逐字强调”用在行上（${reason}）`);
  const before = Number(await currentRevision(page));
  await page.eval(`[...document.querySelectorAll('.template-card')].find((c) => c.textContent.includes(${JSON.stringify(renamed)})).click()`);
  // 试看（G16）：当前修订、播放头时刻的候选帧，不写工程。
  await page.waitFor(js(`return !!document.querySelector('.picker-trial img[data-sha]') && readyThumbs('.template-picker') >= 2;`), '试看画面与卡片缩略图', 60_000);
  const trial = await page.eval(
    js(`return { title: document.querySelector('.picker-trial .preview-title').textContent, sha: document.querySelector('.picker-trial img').dataset.sha, revision: revision() };`),
  );
  await snapshot(page, 'picker');
  const windowReads = () => page.eval(`performance.getEntriesByType('resource').filter((e) => e.name.includes('/lyrics/window')).length`);
  const readsBefore = await windowReads();
  await clickDialogButton(page, '应用到 2 行');
  await page.waitFor(js(`return !openDialog() && document.querySelectorAll('.target-row.level-2 .target-template').length === 2;`), '应用后');
  const h1 = await frameHash(page, '应用后的帧');
  check(
    trial.title.includes(`试看 · ${renamed} → 2 行 · 0:01.500`) && trial.title.includes('没有写入工程') && Number(trial.revision) === before && trial.sha.slice(0, 16) === h1,
    `选择器试看：标明模板、目标与时刻，没有写入工程（修订仍是 ${trial.revision}）；与应用后成片预览同一时刻的帧相同（${h1}）；卡片有缩略图`,
  );
  const reread = (await windowReads()) - readsBefore;
  check(
    Number(await currentRevision(page)) === before + 1 && h1 !== h0 && reread === 0,
    `两行一次应用：修订 ${before} → ${before + 1}（一条历史），预览帧变化，目标浏览器两行标出模板名；只涉及分配，没有重读歌词（歌词窗口读取 ${reread} 次）`,
  );
  await snapshot(page, 'applied');
  await clickButton(page, '撤销', `document.querySelector('.applied-banner')`);
  await page.waitFor(js(`return !document.querySelector('.target-row.level-2 .target-template');`), '撤销后');
  const h2 = await frameHash(page, '撤销后的帧');
  check(h2 === h0, `撤销一次就整体还原，预览帧 SHA 与应用前相同（${h0}）`);
  await key(page, 'z', 'KeyZ', 90, 2 | 8);
  await page.waitFor(js(`return document.querySelectorAll('.target-row.level-2 .target-template').length === 2;`), '重做后');
  check((await frameHash(page, '重做后的帧')) === h1, 'Ctrl+Shift+Z 重做后两行的分配和画面都回来');

  // 5. 分配管理（G15）
  await page.eval(js(`lineRow(0).click();`));
  await page.waitFor(js(`return !!appRow() && appRow().textContent.includes(${JSON.stringify(renamed)});`), '检查器列出分配');
  const applicationId = await page.eval(js(`return appRow().dataset.applicationId;`));
  check(await page.eval(js(`return !!button('换模板…');`)), '单选第 1 行：检查器列出直接分配（名称、用途、属性摘要），按钮变成“换模板…”');
  await clickButton(page, '停用', `document.querySelector('.application-row')`);
  await page.waitFor(js(`return !!document.querySelector('.application-row.disabled');`), '停用后');
  const disabledHash = await frameHash(page, '停用后的帧');
  const disabledLabel = await page.eval(js(`return lineRow(0).querySelector('.target-template')?.textContent;`));
  check(disabledHash === h0 && disabledLabel?.endsWith('（已停用）'), `停用：画面回到应用前，目标浏览器注明“${disabledLabel}”`);
  await clickButton(page, '启用', `document.querySelector('.application-row')`);
  await page.waitFor(js(`return !!document.querySelector('.application-row:not(.disabled)');`), '启用后');
  check((await frameHash(page, '启用后的帧')) === h1, '启用：画面回到应用后');

  await gotoPage(page, 'library', `!!document.querySelector('.library-list')`);
  await page.eval(js(`[...document.querySelectorAll('[data-template-id]')].find((c) => c.textContent.includes(${JSON.stringify(renamed)})).click();`));
  await page.waitFor(js(`return templateName() === ${JSON.stringify(renamed)} && !!field('未唱颜色');`), '选中副本');
  await typeInto(page, '未唱颜色', '#ffd040', { enter: true });
  await clickButton(page, '保存');
  await page.waitFor(`[...document.querySelectorAll('.tag')].some((t) => t.textContent === '我的模板')`, '保存完成');
  await gotoPage(page, 'appearance', `!!document.querySelector('.appearance')`);
  check((await frameHash(page, '库修改后的帧')) === h1, '库与工程独立：在样式库改了已应用的模板，工程的预览帧不变');
  const beforeUpdate = Number(await currentRevision(page));
  await clickButton(page, '更新', `document.querySelector('.application-row')`);
  await page.waitFor(js(`return Number(revision()) === ${beforeUpdate + 1};`), '更新后');
  const h3 = await frameHash(page, '更新后的帧');
  check(h3 !== h1 && (await page.eval(js(`return appRow().dataset.applicationId;`))) === applicationId, '“更新”取库里的当前版本：一条历史，预览帧变化，分配 ID 不变');
  await clickButton(page, '更新', `document.querySelector('.application-row')`);
  await page.waitFor(`document.querySelector('.status-bar .notice')?.textContent.includes('已经是库里的当前版本')`, '无变化提示');
  check(Number(await currentRevision(page)) === beforeUpdate + 1, '再点“更新”：内容相同，没有新历史，提示“已经是库里的当前版本”');
  await snapshot(page, 'application');

  await clickButton(page, '移除', `document.querySelector('.application-row')`);
  await page.waitFor(js(`return !appRow() && !lineRow(0).querySelector('.target-template');`), '移除后');
  await key(page, 'z', 'KeyZ', 90, 2);
  await page.waitFor(js(`return !!appRow();`), '撤销移除');
  check((await page.eval(js(`return appRow().dataset.applicationId;`))) === applicationId, '移除分配后 Ctrl+Z 撤销，分配以原 ID 回来');

  // 6. 文本单元：经典整行不能用，逐字强调可以
  await page.eval(js(`lineRow(0).querySelector('.target-toggle').click();`));
  await page.waitFor(`document.querySelectorAll('.target-token').length > 0`, '展开文本单元');
  await page.eval(`document.querySelectorAll('.target-token')[0].click()`);
  await clickButton(page, '选择模板…');
  const lineCard = `document.querySelector('.template-card[data-template-id="builtin:line-classic"]')`;
  await page.waitFor(`!!${lineCard} && !!document.querySelector('.template-card[data-template-id="builtin:token-highlight"]:not(.blocked)')`, '选择器卡片');
  const lineReason = await page.eval(`${lineCard}.querySelector('.template-card-reason')?.textContent ?? ''`);
  await page.eval(`document.querySelector('.template-card[data-template-id="builtin:token-highlight"]').click()`);
  await clickDialogButton(page, '应用到 1 个文本单元');
  await page.waitFor(`!!document.querySelector('.target-token.has-template')`, '文本单元应用后');
  check(lineReason.startsWith('含文本单元不能用的属性'), `文本单元：“经典整行”灰显（${lineReason}），“逐字强调”应用后单元带模板标记`);

  // 7. 删除库里的模板：工程里的分配是固定副本，照常存在，只是不能再更新
  await gotoPage(page, 'library', `!!document.querySelector('.library-list')`);
  await page.eval(js(`[...document.querySelectorAll('[data-template-id]')].find((c) => c.textContent.includes(${JSON.stringify(renamed)})).click();`));
  await page.waitFor(js(`return templateName() === ${JSON.stringify(renamed)};`), '选中副本');
  await menuItem(page, '模板操作', '删除');
  await clickDialogButton(page, '删除');
  await page.waitFor(`!document.querySelector('.library-list')?.textContent.includes(${JSON.stringify(renamed)})`, '删除后');
  copied = false;
  await gotoPage(page, 'appearance', `!!document.querySelector('.appearance')`);
  await page.eval(js(`lineRow(0).click();`));
  await page.waitFor(js(`return !!appRow() && !!appRow().querySelector('.hint');`), '不能更新的说明');
  check(
    (await page.eval(js(`return appRow().textContent.includes(${JSON.stringify(renamed)}) && appRow().querySelector('.hint').textContent;`))) === '库里已经没有这个模板，不能更新。',
    '删除库里的模板后，工程里的分配照常显示，“更新”不可用并说明原因',
  );

  // 8. 含效果的模板（W7 设计 §5 第 5～8 条）
  if (!sampleEffectsDir()) {
    console.log('[skipped] 找不到后端仓库的 samples/ScriptEffects（用 MAKO_BACKEND_REPO 指定后端仓库），跳过含效果模板的部分。');
  } else {
    writeFileSync(inkPath, packSampleEffect('CustomInk'));
    writeFileSync(emphasisPath, packSampleEffect('WordEmphasis'));

    // 8a. 样式库：“波纹”把未唱填充换成包材质；“读来源”加一个读取输入“翻译”的逐字强调
    await gotoPage(page, 'library', `!!document.querySelector('[data-template-id="builtin:line-classic"]')`);
    await copyClassic(page, waveName);
    copied = true;
    await libraryTab(page, 'material');
    await importPackage(page, inkPath, 'makokara.custom-ink');
    await page.eval(`document.querySelector('.slot-buttons [data-slot=unsungFill]').click()`);
    await choose(page, `document.querySelector('.slot-material mdui-select')`, 'package');
    await page.waitFor(`document.querySelector('.slot-material')?.dataset.kind === 'package'`, '未唱填充换成包材质');
    await saveTemplate(page);
    await copyClassic(page, inputName);
    await libraryTab(page, 'effects');
    await importPackage(page, emphasisPath, 'makokara.word-emphasis');
    await clickButton(page, '加效果', `document.querySelector('.add-effect')`);
    await page.waitFor(`!!document.querySelector('.recipe-read-scope')`, '效果已加');
    await choose(page, `document.querySelector('.recipe-read-scope')`, 'branchInput');
    await commit(page, `document.querySelector('.recipe-branch-input')`, '翻译');
    await page.waitFor(`!document.querySelector('.template-problem')`, '填好输入名');
    await saveTemplate(page);
    const listed = await page.eval(`document.querySelector('.library-list').textContent`);
    check(listed.includes(waveName) && listed.includes(inputName), `样式库：“${waveName}”的未唱填充用包材质，“${inputName}”的逐字强调读取输入“翻译”，都已保存`);

    // 8b. 新工程：第 2～3 句套单行居中（得到片段“歌词段布局”），画面下建前奏提示组，给第 1 句加 0.4 秒前奏。
    // 这些编排操作已由 smoke:orchestration 走过界面，这里直接用页面里的客户端造出来。
    await closeProject(page);
    await newProject(page);
    const seeded = await page.eval(`(async () => {
      const url = performance.getEntriesByType('resource').map((e) => e.name).find((n) => n.includes('/src/editor/connection.ts'));
      const { client } = await import(url);
      const id = (await client.clientState()).sessions.at(-1).projectSessionId;
      const lines = (await client.projects.lyrics(id)).lines.map((l) => l.id);
      let r = await client.projects.command(id, { type: 'orchestration.applyBasicLayout', branchId: 'primary', preset: 'SingleCentered', selectedLineIds: [lines[1], lines[2]], layoutName: '单行居中', slotNames: ['槽位 1'], selectedSegmentName: '歌词段布局', retainedSegmentName: '保留原布局的歌词段' });
      const root = (await client.projects.orchestration(id, r.projectRevision)).programs.find((p) => p.branchId === 'primary').rootContainerId;
      r = await client.projects.command(id, { type: 'orchestration.createCountdown', parentId: root, name: null, count: 3 });
      const group = (await client.projects.orchestration(id, r.projectRevision)).nodes.find((n) => n.contentKind === 'countdownSequence');
      r = await client.projects.command(id, { type: 'orchestration.createLeadIn', branchId: 'primary', leadInId: 'lead-smoke', name: null, targetLineId: lines[0], indicatorContainerId: group.id, durationUs: '400000', enabled: true });
      return r.projectRevision;
    })()`);
    await gotoPage(page, 'appearance', js(`return document.querySelectorAll('.target-row.level-2').length === 4 && revision() === ${JSON.stringify(seeded)} && !!${targetRow('前奏提示')};`));
    await clickButton(page, '全曲');
    await sleep(200);

    // 8c. 行：含效果的模板试看与应用，一条历史，撤销后帧与应用前相同；外观来源写明分配和行
    await page.eval(js(`lineRow(0).click();`));
    await page.waitFor(`document.querySelector('.inspector-path')?.textContent.includes('第 1 行')`, '选中第 1 行');
    await seek(page, 1.5);
    const w0 = await frameHash(page, '应用前的帧');
    const waveCard = await pickTemplate(page, waveName);
    await page.eval(`${waveCard}.click()`);
    await page.waitFor(`!!document.querySelector('.picker-trial img[data-sha]')`, '试看画面', 60_000);
    const waveTrial = await page.eval(`document.querySelector('.picker-trial img').dataset.sha`);
    const wr = Number(await currentRevision(page));
    await clickDialogButton(page, '应用到 1 行');
    await page.waitFor(js(`return !openDialog() && Number(revision()) === ${wr + 1};`), '应用波纹');
    const w1 = await frameHash(page, '应用后的帧');
    check(w1 !== w0 && waveTrial.slice(0, 16) === w1, `含效果的模板应用到行：试看与应用后的帧相同（${w1}），一条历史（修订 ${wr} → ${wr + 1}），画面变化`);
    if (!(await page.eval(`!!document.querySelector('.style-sources .source-group')`))) await page.eval(`document.querySelector('.style-sources .section-toggle').click()`);
    // 组里各属性来源相同时合成一行：“未唱填充”组写一行“来自 分配“…”（第 1 行）”。
    const lineSource = `来自 分配“${waveName}”（第 1 行）`;
    const unsungGroup = `[...document.querySelectorAll('.style-sources .source-group')].find((g) => g.querySelector('dt')?.textContent === '未唱填充')`;
    await page.waitFor(`[...(${unsungGroup})?.querySelectorAll('.source-row') ?? []].some((r) => r.textContent.includes(${JSON.stringify(lineSource)}))`, '外观来源', 30_000);
    check(true, `外观来源：选中第 1 行，“未唱填充”写“${lineSource}”`);
    await undo(page);
    check((await frameHash(page, '撤销后的帧')) === w0, '撤销后帧与应用前相同');

    // 8d. 文本单元：灰显并写明原因
    await page.eval(js(`lineRow(0).querySelector('.target-toggle').click();`));
    await page.waitFor(`document.querySelectorAll('.target-token').length > 0`, '展开文本单元');
    await page.eval(`document.querySelector('.target-token').click()`);
    const tokenCard = await pickTemplate(page, waveName);
    const tokenReason = await page.eval(`${tokenCard}.classList.contains('blocked') ? ${tokenCard}.querySelector('.template-card-reason')?.textContent ?? '' : ''`);
    check(!!tokenReason, `文本单元上“${waveName}”灰显：${tokenReason}`);
    await clickDialogButton(page, '取消');
    await page.waitFor(js(`return !openDialog();`), '关闭选择器');

    // 8e. 来源输入：没选来源前不能应用；选好后应用，分配写明映射
    await page.eval(js(`lineRow(3).click();`));
    await page.waitFor(`document.querySelector('.inspector-path')?.textContent.includes('第 4 行')`, '选中第 4 行');
    const inputCard = await pickTemplate(page, inputName);
    await page.eval(`${inputCard}.click()`);
    await page.waitFor(`!!document.querySelector('.picker-inputs [data-input="翻译"] mdui-select')`, '来源输入');
    const unmapped = await page.eval(js(`return { text: document.querySelector('.picker-inputs').textContent, blocked: anyButton('应用到 1 行', openDialog())?.disabled === true };`));
    await choose(page, `document.querySelector('.picker-inputs [data-input="翻译"] mdui-select')`, 'primary');
    await page.waitFor(js(`return !!button('应用到 1 行', openDialog());`), '选好来源', 60_000);
    const ir = Number(await currentRevision(page));
    await clickDialogButton(page, '应用到 1 行');
    await page.waitFor(js(`return !openDialog() && Number(revision()) === ${ir + 1};`), '应用读来源');
    await page.waitFor(`(document.querySelector('.application-row')?.textContent ?? '').includes('读取：翻译 →')`, '分配写明映射');
    const mapping = await page.eval(`document.querySelector('.application-row').textContent.match(/读取：翻译 → [^·]*/)?.[0]`);
    check(unmapped.blocked && unmapped.text.includes('需要读取：翻译'), `需要来源输入的模板：没选来源前不能应用（${unmapped.text.slice(0, 40)}…），选好后应用，分配写明“${mapping?.trim()}”`);
    await undo(page);

    // 8f. 编排对象：默认容器（包括里面的句子）、片段、前奏提示组；时间轴片段轨点选同步
    await selectRow(page, '常规', '常规');
    await seek(page, 1.5);
    await applyTemplate(page, waveName, '这个容器');
    const c1 = await frameHash(page, '应用到默认容器后的帧');
    check(c1 !== w0 && (await page.eval(`${targetRow('常规')}.querySelector('.target-template')?.textContent`)) === waveName, `应用到默认容器（包括里面的句子）：第 1 句变化（${w0} → ${c1}），目标浏览器标出模板名`);
    await page.eval(js(`lineRow(0).click();`));
    const nodeSource = `分配“${waveName}”（画面 / 常规）`;
    await page.waitFor(`[...document.querySelectorAll('.style-sources button.source-row')].some((r) => r.textContent.includes(${JSON.stringify(nodeSource)}))`, '来源指向容器', 30_000);
    await page.eval(`[...document.querySelectorAll('.style-sources button.source-row')].find((r) => r.textContent.includes(${JSON.stringify(nodeSource)})).click()`);
    await page.waitFor(`${targetRow('常规')}?.classList.contains('selected')`, '点来源选中容器', 10_000);
    check(true, `外观来源：第 1 行写“来自 ${nodeSource}”，点它选中“常规”`);
    await undo(page);

    await selectRow(page, '歌词段布局', '歌词段布局');
    await seek(page, 4.5);
    const k0 = await frameHash(page, '第 2 句');
    await applyTemplate(page, waveName, '这个片段');
    const k1 = await frameHash(page, '应用到片段后的第 2 句');
    await seek(page, 1.5);
    const k2 = await frameHash(page, '应用到片段后的第 1 句');
    check(k1 !== k0 && k2 === w0, `应用到片段：片段里的第 2 句变化（${k0} → ${k1}），片段外的第 1 句不变`);

    await selectRow(page, '工程默认', '工程默认');
    await clickTimeline(page, 4.5, -10);
    await page.waitFor(`${targetRow('歌词段布局')}?.classList.contains('selected')`, '片段轨点选同步', 10_000);
    check(true, '在时间轴片段轨上点选片段，目标浏览器同步选中');

    await selectRow(page, '前奏提示', '前奏提示');
    await seek(page, 0.3);
    const g0 = await frameHash(page, '前奏 0.3 秒');
    await applyTemplate(page, waveName, '这个前奏提示组');
    const g1 = await frameHash(page, '应用到前奏提示组后');
    check(g1 !== g0 && (await page.eval(`document.querySelector('.appearance-inspector')?.innerText.includes('提示组外观 · 只读')`)), `应用到前奏提示组：标记外观变化（${g0} → ${g1}）；提示组自己的外观只读`);
    await snapshot(page, 'objects');

    // 8g. 在样式库中编辑副本：改描边，保存并返回试看，应用后撤销
    await page.eval(js(`lineRow(3).click();`));
    await page.waitFor(`document.querySelector('.inspector-path')?.textContent.includes('第 4 行')`, '选中第 4 行');
    await applyTemplate(page, waveName, '1 行');
    await clickButton(page, '在样式库中编辑副本');
    const copyOf = `${waveName}（副本）`;
    await page.waitFor(js(`return !!document.querySelector('.library-return') && templateName() === ${JSON.stringify(copyOf)};`), '跳到样式库编辑副本', 30_000);
    const bar = await page.eval(`document.querySelector('.library-return').innerText`);
    const stroke = await page.eval(js(`return !!field('宽度');`));
    if (stroke) await commit(page, `[...document.querySelectorAll('mdui-text-field')].find((f) => f.label === '宽度')`, '9');
    else await commit(page, `[...document.querySelectorAll('mdui-text-field')].find((f) => f.label === '已唱颜色')`, '#ff2020');
    await page.waitFor(dirty, '副本有修改');
    await clickButton(page, '保存并返回试看');
    await page.waitFor(js(`return !!document.querySelector('.appearance') && !!openDialog()?.classList.contains('template-picker');`), '回到外观页的选择器', 30_000);
    await page.waitFor(
      `!!document.querySelector('.picker-trial img') && (document.querySelector('.picker-trial .pane-title-text')?.textContent ?? '').includes(${JSON.stringify(copyOf)})`,
      '试看副本',
      60_000,
    );
    const er = Number(await currentRevision(page));
    await clickDialogButton(page, '应用到 1 行');
    await page.waitFor(js(`return !openDialog() && Number(revision()) === ${er + 1};`), '应用副本');
    const rowShows = (name) =>
      page.waitFor(js(`return lineRow(3).querySelector('.target-template')?.textContent === ${JSON.stringify(name)};`), `第 4 行标着 ${name}`, 10_000).then(
        () => true,
        () => false,
      );
    const appliedCopy = await rowShows(copyOf);
    await undo(page);
    const restored = await rowShows(waveName);
    if (!appliedCopy || !restored) console.log(`  返回栏：${bar}；应用后：${await page.eval(js(`return lineRow(3).querySelector('.target-template')?.textContent;`))}`);
    check(
      bar.includes('来自 外观') && bar.includes('第 4 行') && appliedCopy && restored,
      `编辑副本：返回栏写来源，改${stroke ? '描边宽度' : '已唱颜色'}后“保存并返回试看”，回到外观页试看副本；应用后第 4 行标着副本，撤销后回到“${waveName}”`,
    );

    // 8h. 删除本次的模板（工程里的分配是固定副本，不受影响）
    await gotoPage(page, 'library', `!!document.querySelector('.library-list')`);
    await deleteTemplates(page, waveName);
    await deleteTemplates(page, inputName);
    copied = false;
    check(!(await page.eval(`document.querySelector('.library-list').textContent.includes(${JSON.stringify(stamp)})`)), '删除本次创建的模板（波纹、读来源和副本）');
  }

  if (values.screenshot) {
    await writeFile(values.screenshot, await page.screenshot());
    console.log(`  截图已保存：${values.screenshot}`);
  }
  check(errors.length === 0, `页面没有未处理异常${errors.length ? `：${errors.join(' | ')}` : ''}`);
  console.log(`\n${checks.filter((c) => c.ok).length}/${checks.length} 项通过`);
} catch (error) {
  console.error(`✗ ${error instanceof Error ? error.message : error}`);
  process.exitCode = 1;
  if (page) {
    const failure = join(tmpdir(), 'mako-appearance-smoke-failure.png');
    await writeFile(failure, await page.screenshot()).catch(() => undefined);
    console.error(`  失败时的页面：${failure}`);
    const text = await page.eval(`document.body.innerText`).catch(() => '');
    console.error(`  页面文字：${String(text).slice(0, 600).replace(/\n+/g, ' ⏎ ')}`);
  }
  for (const message of errors) console.error(`  页面异常：${message}`);
  if (copied) console.error(`  个人库里可能留下了名称带 ${stamp} 的冒烟模板（${copyName}、${renamed}、${waveName}、${inputName}），请在样式库里删除。`);
} finally {
  await browser.close();
  rmSync(assets, { recursive: true, force: true });
}
