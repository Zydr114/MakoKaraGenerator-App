// 编排页的浏览器冒烟（W4 设计 §5、§9.5、§10.8、§11.8、§12.8、§13.10、§14.10、§15.8）：用真实 Chrome 走一遍 W4 第一批（骨架）、第二批（G18 快速布局）、
// 第三批（G19 范围快速布局与片段）、第四批（G19-B 片段编辑）、第五批（G20 结构编辑）、第六批（G21 属性、G22 画布）、
// 第七批（G23 前奏提示、G24 本句微调与进入动画）与第八批（片段成员）的验收链路。
// 新建工程 → 导航栏“编排”位于“歌词”和“外观”之间 → 结构树（画面 → 常规，默认容器）与可编辑的属性 → ↑↓ 移动选中
// → 歌词分配条（只有一个容器时不能改，未单独分配句数，片段列表为空、可以新建，前奏提示可用）
// → 编排页的成片预览帧（与外观页相同）→ 切换页面播放头保持
// → 快速布局：预演不改工程，套用双行居中（一条历史，新容器成为默认，帧变化）；改默认容器→撤销（R6 已修复）；再套同一预设没有变化；撤销后恢复
// → 范围快速布局：时间轴上选第 2～3 句，套单行居中（默认容器不变，片段轨与片段列表出现片段，属性区显示片段，帧变化），
//   列表与时间轴联动，再套一次没有变化
// → 片段编辑（G19-B）：改名→清除名称→撤销，改目标容器（帧变化）→撤销，删除（几句回到默认容器），已在片段里的句子不能新建并写明原因，
//   选两句新建片段（目标默认是另一个容器，帧变化），连续撤销回到范围套用后的样子 → 撤销范围套用后片段消失
// → 结构编辑（G20）：新建轮换容器（3 个槽位）并选中，改名，加槽位、下移槽位、删除槽位，复制，新建组并把副本移进去（帧不变），上移，
//   默认容器、来源的根、槽位不能做的操作写明原因（Delete 键只在状态栏说明），删除组，连续撤销回到原样
// → 画布（G22）：叠加层有容器的框和当前句子的墨迹框，点句子选中默认容器、点空白不改选择，拖动默认容器（X、Y 按位移变化）→撤销，
//   铺满画布的容器在空白处按下拖不动
// → 属性（G21）：改 Y 和参考框的宽，安全区不合法写明原因，动画改成静态（这时默认容器不能改成轮换）再改回，改成轮换（3 个槽位），
//   同时最多显示 1、出现时机，改回自动布局（槽位留作子节点），再改成轮换时复用，默认容器是轮换时动画不能改静态，
//   布局参考模式每个槽位一句代表文字、点文字选中槽位，连续撤销回到原样
// → 前奏提示（G23）：新建提示组，添加前奏（放不下时写明原因），时长、停用、启用、改名，附着方式切换再切回（画面不动），删除，
//   自动生成（预演不改工程、改设置重新预演、只应用勾选的句子、设置保存、删除并不再自动生成后是“已排除”），
//   标记改字、加、上移、均匀排列、删到只剩一个
// → 句子（G24）：时间轴上点一句，改 X 偏移→清除；开启工程默认进入动画（句子开头的帧变化），本句单独设置再关掉；
//   画布上单击文字仍选中容器、双击选中这一句、拖动改本句偏移；连续撤销回到原样
// → 片段成员（第八批）：第 2～3 句的片段调整成第 2～4 句（相同时不能调整），从第 3 句拆开（预填播放头所在句），
//   拆出的片段扩到原段外时写明原因，选中第 3～4 句的片段与前一个合并，和没有调度上下文的片段不能合并；连续撤销回到原样
// → 加一个翻译来源后编排结构按新修订重读，新来源有自己的程序（R4 已修复），它的快速布局和片段编辑不可用并写明原因（R7）→ 撤销后新来源消失。
//
// 前提：pnpm dev:backend 与 pnpm dev 都在运行。
// 用法：node scripts/orchestration-smoke.mjs [--url http://localhost:5173] [--screenshot out.png] [--headed]

import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { parseArgs } from 'node:util';
import { launchChrome } from './cdp.mjs';
import { shell } from './ui-query.mjs';

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

const assets = mkdtempSync(join(tmpdir(), 'mako-orchestration-smoke-'));
const wavPath = join(assets, '编排冒烟.wav');
const lrcPath = join(assets, '编排冒烟.lrc');
writeFileSync(wavPath, makeWav());
writeFileSync(lrcPath, '[00:00.50]朝の光浴びて\n[00:03.00]青い空見上げ\n[00:06.00]Hello karaoke\n[00:09.00]你好，世界\n');

// ── 页面操作 ──

const checks = [];
function check(ok, label) {
  checks.push({ ok: !!ok, label });
  console.log(`${ok ? '✓' : '✗'} ${label}`);
  if (!ok) process.exitCode = 1;
}

const PRELUDE = `
  // 先找页面里的 mdui 按钮，再找新控件（顶栏“保存工程”的文字也是“保存”）
  const buttons = (scope = document) => [...scope.querySelectorAll('mdui-button, mdui-button-icon'), ...scope.querySelectorAll('button')];
  const button = (text, scope) => buttons(scope).find((b) => (b.textContent.trim() === text || b.getAttribute('aria-label') === text) && !b.disabled);
  const anyButton = (text, scope) => buttons(scope).find((b) => b.textContent.trim() === text);
  const field = (label) => [...document.querySelectorAll('mdui-text-field')].find((f) => f.label === label);
  const select = (label) => [...document.querySelectorAll('mdui-select')].find((f) => f.label === label);
  // 歌词分配条用原生紧凑下拉框（工具栏不用 mdui 的 56px 输入框）。
  const assignmentSelect = (label) => document.querySelector('.assignment-bar select[aria-label="' + label + '"]');
  const revision = () => (document.querySelector('.status-bar')?.textContent.match(/修订 (\\d+)/) ?? [])[1];
  const rows = () => [...document.querySelectorAll('.orchestration .node-row')].map((r) => ({
    name: r.querySelector('.target-label').textContent,
    role: r.querySelector('.node-role').textContent,
    level: Number(r.getAttribute('aria-level')),
    isDefault: !!r.querySelector('.target-template'),
    selected: r.classList.contains('selected'),
  }));
  const sections = () => [...document.querySelectorAll('.orchestration .structure-section')].map((s) => ({
    title: s.querySelector('.structure-source .target-label').textContent,
    empty: s.querySelector('.structure-empty')?.textContent ?? '',
    rows: s.querySelectorAll('.node-row').length,
  }));
  const path = () => document.querySelector('.orchestration-inspector .inspector-path .pane-title-text')?.textContent;
  const inspector = () => document.querySelector('.orchestration-inspector')?.textContent ?? '';
  const sectionTitles = () => [...document.querySelectorAll('.orchestration-inspector .section-title')].map((h) => h.textContent);
  const value = (label) => [...document.querySelectorAll('.orchestration-inspector .override-row')].find((r) => r.querySelector('dt').textContent === label)?.querySelector('dd').textContent;
  const hint = () => document.querySelector('.assignment-hint')?.textContent ?? '';
  const layoutTag = () => document.querySelector('.assignment-layout')?.textContent ?? '';
  const sectionLayouts = () => [...document.querySelectorAll('.orchestration .structure-layout')].map((e) => e.textContent);
  const dialog = () => document.querySelector('mdui-dialog.quick-layout');
  const impact = () => dialog()?.querySelector('.layout-impact')?.textContent ?? '';
  const card = (preset) => dialog()?.querySelector('[data-preset="' + preset + '"]');
  const scopes = () => [...(dialog()?.querySelectorAll('.layout-scope [role=radio]') ?? [])].map((b) => ({ text: b.textContent, checked: b.getAttribute('aria-checked') === 'true', disabled: b.disabled }));
  const unassigned = () => document.querySelector('.assignment-unassigned')?.textContent ?? '';
  const rangeTag = () => document.querySelector('.assignment-range')?.textContent ?? '';
  const clipButton = () => document.querySelector('.assignment-clips');
  const clipRows = () => [...document.querySelectorAll('.clip-panel .clip-row')].map((r) => ({
    id: r.dataset.clipId,
    text: [...r.querySelectorAll('.target-label, .target-meta')].map((e) => e.textContent).join(' '),
    kept: !!r.querySelector('.target-template'),
    selected: r.getAttribute('aria-selected') === 'true',
  }));
  const snack = (text) => [...document.querySelectorAll('mdui-snackbar')].some((e) => e.open && e.textContent.includes(text));
  const clipDialog = () => document.querySelector('mdui-dialog.new-clip');
  const selectText = (s) => [...(s?.querySelectorAll('mdui-menu-item') ?? [])].find((i) => i.value === s.value)?.textContent.trim();
  const inspectorSelect = (label) => [...document.querySelectorAll('.orchestration-inspector mdui-select')].find((f) => f.label === label);
  const inspectorField = (label) => [...document.querySelectorAll('.orchestration-inspector mdui-text-field')].find((f) => f.label === label);
  const tool = (label) => document.querySelector('.structure-toolbar mdui-button-icon[aria-label="' + label + '"]');
  const toolTitle = (label) => tool(label)?.parentElement.title ?? '';
  const slotRows = () => [...document.querySelectorAll('.orchestration-inspector .slot-row')].map((r) => ({ id: r.dataset.slotId, name: r.querySelector('.slot-name').textContent }));
  const slotTool = (index, label) => document.querySelectorAll('.orchestration-inspector .slot-row')[index]?.querySelector('mdui-button-icon[aria-label="' + label + '"]');
  const nodeDialog = () => document.querySelector('mdui-dialog.new-node');
  const moveDialog = () => document.querySelector('mdui-dialog.move-node');
  const dialogField = (d, label) => [...(d?.querySelectorAll('mdui-text-field') ?? [])].find((f) => f.label === label);
  const dialogSelect = (d, label) => [...(d?.querySelectorAll('mdui-select') ?? [])].find((f) => f.label === label);
  const clickRow = (name) => [...document.querySelectorAll('.orchestration .node-row')].find((r) => r.querySelector('.target-label').textContent === name).click();
  const level2 = () => rows().filter((r) => r.level === 2).map((r) => r.name);
  const status = () => document.querySelector('.status-bar .notice')?.textContent ?? '';
  const overlay = () => {
    const svg = document.querySelector('.orchestration .canvas-overlay');
    return svg ? { frames: [...svg.querySelectorAll('.overlay-frame')].map((f) => f.dataset.nodeId), inks: svg.querySelectorAll('.overlay-ink').length, selected: !!svg.querySelector('.overlay-selected') } : undefined;
  };
  const inkCenter = (i = 0) => {
    const r = document.querySelectorAll('.orchestration .canvas-overlay .overlay-ink')[i]?.getBoundingClientRect();
    return r && { x: r.x + r.width / 2, y: r.y + r.height / 2 };
  };
  const canvasBox = () => {
    const r = document.querySelector('.orchestration .canvas-overlay')?.getBoundingClientRect();
    return r && { x: r.x, y: r.y, w: r.width, h: r.height };
  };
  const canvasMode = (text) => [...document.querySelectorAll('.canvas-mode')].find((b) => b.textContent.trim() === text);
  const previewTitle = () => document.querySelector('.orchestration .preview-title')?.textContent ?? '';
  const slotDialog = () => document.querySelector('mdui-dialog.slot-sequence-dialog');
  const switchOf = (label, scope = document) => [...scope.querySelectorAll('.switch-field')].find((l) => l.querySelector('span')?.textContent === label)?.querySelector('mdui-switch');
  const leadInDialog = () => document.querySelector('mdui-dialog.lead-in-dialog');
  const generateDialog = () => document.querySelector('mdui-dialog.generate-dialog');
  const leadInRows = () => [...document.querySelectorAll('.lead-in-panel .lead-in-row')].map((r) => ({
    id: r.dataset.leadInId,
    text: [...r.querySelectorAll('.target-label, .target-meta, .target-template, .tag')].map((e) => e.textContent).join(' '),
    off: r.classList.contains('disabled'),
  }));
  const genSummary = () => generateDialog()?.querySelector('.generate-summary')?.textContent ?? '';
  const genRows = () => [...(generateDialog()?.querySelectorAll('.generate-row') ?? [])].map((r) => ({ text: r.textContent, status: r.querySelector('.generate-status')?.textContent ?? '', checked: !!r.querySelector('mdui-checkbox')?.checked }));
  const markIds = () => [...document.querySelectorAll('.orchestration-inspector .mark-row')].map((r) => r.dataset.markId);
  const markTool = (i, css) => document.querySelectorAll('.orchestration-inspector .mark-row')[i]?.querySelector(css);
  const inspectorEl = (css) => document.querySelector('.orchestration-inspector ' + css);
  const effective = () => inspectorEl('.line-animation-effective')?.textContent ?? '';
  const lineInkCenter = () => {
    const r = document.querySelector('.orchestration .canvas-overlay .overlay-line')?.getBoundingClientRect();
    return r && { x: r.x + r.width / 2, y: r.y + r.height / 2 };
  };
`;
const js = (body) => `(() => { ${PRELUDE} ${body} })()`;
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function clickButton(page, text, scope = 'document') {
  const target = js(`return button(${JSON.stringify(text)}, ${scope});`);
  await page.waitFor(`!!${target}`, `按钮「${text}」可用`);
  await page.eval(`${target}.click()`);
}

async function typeInto(page, label, value) {
  const f = `field(${JSON.stringify(label)})`;
  await page.waitFor(js(`return !!${f};`), `输入框「${label}」`);
  await page.eval(js(`const f = ${f}; f.value = ${JSON.stringify(value)}; f.dispatchEvent(new Event('input', { bubbles: true, composed: true }));`));
}

async function key(page, keyName, code, keyCode, modifiers = 0) {
  for (const type of ['keyDown', 'keyUp']) await page.send('Input.dispatchKeyEvent', { type, key: keyName, code, windowsVirtualKeyCode: keyCode, modifiers });
}

async function gotoPage(page, value, ready) {
  await shell.goto(page, value);
  await page.waitFor(ready, `进入 ${value}`);
}

async function setFile(page, ariaLabel, path) {
  const { root } = await page.send('DOM.getDocument', { depth: -1 });
  const { nodeId } = await page.send('DOM.querySelector', { nodeId: root.nodeId, selector: `input[type=file][aria-label="${ariaLabel}"]` });
  await page.send('DOM.setFileInputFiles', { nodeId, files: [path] });
}

const playheadText = `(document.querySelector('.transport-time span')?.textContent ?? '')`;

/** 时间轴上的点击：t 秒处，lane 是 ruler／lines／clips／leadIns；modifiers 8 是 Shift。全曲视图下 12 秒铺满画布宽度。 */
async function clickTimeline(page, t, lane, modifiers = 0) {
  const rect = await page.eval(`(() => { const r = document.querySelector('.timeline canvas').getBoundingClientRect(); return { x: r.x, y: r.y, w: r.width }; })()`);
  const y = { ruler: 10, lines: 22 + 72 + 34 + 11, clips: 22 + 72 + 34 + 22 + 11, leadIns: 22 + 72 + 34 + 22 + 22 + 11 }[lane];
  for (const type of ['mousePressed', 'mouseReleased']) await page.send('Input.dispatchMouseEvent', { type, x: rect.x + rect.w * (t / 12), y: rect.y + y, button: 'left', clickCount: 1, modifiers });
}
const treeReady = js(`return rows().length > 0;`);

/** 底部的提示条会盖住时间轴（关闭动画期间也会），等它从页面上移除再点。 */
const noSnack = (page) => page.waitFor(`document.querySelectorAll('mdui-snackbar').length === 0`, '提示条消失', 10_000);

/** 属性区的提交式输入框：先输入，等 React 更新草稿，再按 Enter 提交。 */
async function commitInspectorField(page, label, value) {
  const f = `inspectorField(${JSON.stringify(label)})`;
  await page.waitFor(js(`return !!${f} && !${f}.disabled;`), `属性区输入框「${label}」可用`);
  await page.eval(js(`const f = ${f}; f.value = ${JSON.stringify(value)}; f.dispatchEvent(new Event('input', { bubbles: true, composed: true }));`));
  await sleep(100);
  await page.eval(js(`${f}.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, composed: true }));`));
}

/** 画布上的鼠标（CDP 的鼠标事件会产生 pointer 事件）。 */
async function mouse(page, type, x, y) {
  await page.send('Input.dispatchMouseEvent', { type, x, y, button: 'left', buttons: type === 'mouseReleased' ? 0 : 1, clickCount: 1 });
}

async function canvasClick(page, p) {
  await mouse(page, 'mousePressed', p.x, p.y);
  await mouse(page, 'mouseReleased', p.x, p.y);
}

/** 从 p 按下，分 6 步拖动 (dx, dy) 屏幕像素后松手。 */
async function canvasDrag(page, p, dx, dy) {
  await mouse(page, 'mousePressed', p.x, p.y);
  for (let i = 1; i <= 6; i++) {
    await mouse(page, 'mouseMoved', p.x + (dx * i) / 6, p.y + (dy * i) / 6);
    await sleep(30);
  }
  await mouse(page, 'mouseReleased', p.x + dx, p.y + dy);
}

/** 画布上双击（第二次按下带 clickCount 2，浏览器才发 dblclick）。 */
async function canvasDoubleClick(page, p) {
  for (const clickCount of [1, 2]) {
    await page.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: p.x, y: p.y, button: 'left', buttons: 1, clickCount });
    await page.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: p.x, y: p.y, button: 'left', buttons: 0, clickCount });
  }
}

/** 属性区里按 CSS 选择器找的提交式输入框（同名的输入框有好几个时用）。 */
async function commitBySelector(page, css, value) {
  const f = `document.querySelector('.orchestration-inspector ${css}')`;
  await page.waitFor(`!!${f} && !${f}.disabled`, `属性区输入框 ${css} 可用`);
  await page.eval(`(() => { const f = ${f}; f.value = ${JSON.stringify(value)}; f.dispatchEvent(new Event('input', { bubbles: true, composed: true })); })()`);
  await sleep(100);
  await page.eval(`${f}.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, composed: true }))`);
}

/** 对话框里的输入框：设值并发 input。 */
async function typeInDialog(page, dialog, label, value) {
  const f = `dialogField(${dialog}, ${JSON.stringify(label)})`;
  await page.waitFor(js(`return !!${f};`), `对话框输入框「${label}」`);
  await page.eval(js(`const f = ${f}; f.value = ${JSON.stringify(value)}; f.dispatchEvent(new Event('input', { bubbles: true, composed: true }));`));
  await sleep(100);
}

async function snapshot(page, suffix) {
  if (!values.screenshot) return;
  await sleep(700);
  await writeFile(values.screenshot.replace(/\.png$/, `-${suffix}.png`), await page.screenshot());
}

const browser = await launchChrome({ headed: values.headed });
let page;
const errors = [];
try {
  browser.cdp.on((message) => {
    if (message.method === 'Runtime.exceptionThrown') errors.push(message.params.exceptionDetails.exception?.description ?? 'exception');
    if (message.method === 'Runtime.consoleAPICalled' && message.params.type === 'error') {
      errors.push(message.params.args.map((a) => a.value ?? a.description).join(' '));
    }
  });

  page = await browser.openPage(`${ORIGIN}/`, { width: 1440, height: 900 });
  await page.send('DOM.enable');
  await page.waitFor(shell.connected, '连接 Server');

  // 1. 新建工程；导航栏顺序
  await clickButton(page, '新建工程');
  await page.waitFor(`!!document.querySelector('mdui-dialog.new-project-dialog[open] input[aria-label="选择歌词"]')`, '新建工程对话框');
  await setFile(page, '选择音频', wavPath);
  await setFile(page, '选择歌词', lrcPath);
  await page.waitFor(`!!document.querySelector('.lyrics-plan-summary')`, '歌词识别结果');
  await page.waitFor(`document.body.innerText.includes('编排冒烟.wav')`, '文件已选择');
  await clickButton(page, '下一步');
  await clickButton(page, '新建');
  await page.waitFor(`document.querySelectorAll('.line-list mdui-list-item').length === 4`, '工作区显示 4 行', 120_000);
  const nav = await page.eval(`[...document.querySelectorAll('.app-bar .pages .page-tab')].map((t) => t.textContent.trim()).join(',')`);
  check(nav === '歌词,编排,外观', `新建工程（4 行），顶栏页签“编排”位于“歌词”和“外观”之间（${nav}）`);

  // 2. 结构树与只读属性
  await gotoPage(page, 'orchestration', treeReady);
  const tree = await page.eval(js(`return { sections: sections(), rows: rows() };`));
  const [root, regular] = tree.rows;
  check(
    tree.sections.length === 1 &&
      tree.sections[0].title === '主歌词' &&
      tree.rows.length === 2 &&
      root.name === '画面' &&
      root.role === '组' &&
      root.level === 1 &&
      regular.name === '常规' &&
      regular.role === '自动布局' &&
      regular.level === 2 &&
      regular.isDefault &&
      !root.isDefault,
    `结构树：主歌词 → 画面（组）→ 常规（自动布局，标“默认”）（${tree.rows.map((r) => `${r.name}/${r.role}`).join('、')}）`,
  );
  const props = await page.eval(
    js(`const editable = (f) => !!f && !f.disabled;
      return { path: path(), titles: sectionTitles(), kind: selectText(inspectorSelect('类型')), x: inspectorField('X（%）')?.value,
        area: ['左', '上', '右', '下'].map((l) => inspectorField(l + '（%）')?.value).join('/'), maxWidth: inspectorField('最大宽度（%）')?.value,
        source: value('来源'), anim: selectText(inspectorSelect('动画')), selected: rows().find((r) => r.selected)?.name,
        editable: ['X', 'Y', '宽', '高', '左', '最大宽度'].every((l) => editable(inspectorField(l + '（%）'))) && ['锚点', '类型', '动画'].every((l) => editable(inspectorSelect(l))) };`),
  );
  check(
    props.selected === '常规' &&
      props.path === '主歌词 / 画面 / 常规' &&
      props.titles.join('、') === '位置与变换、参考框、内部布局、来源' &&
      props.kind === '自动布局' &&
      props.x === '50' &&
      props.area === '0/0/100/100' &&
      props.source === '主歌词' &&
      props.anim === '按来源角色' &&
      props.editable,
    `默认选中默认容器，属性可编辑（G21）：路径“${props.path}”，${props.titles.join('、')}；类型 ${props.kind}，X ${props.x}%，安全区 ${props.area}%，最大宽度 ${props.maxWidth}%，动画 ${props.anim}`,
  );
  await snapshot(page, 'tree');

  // 3. ↑↓ 在结构树里移动
  await key(page, 'ArrowUp', 'ArrowUp', 38);
  await page.waitFor(js(`return path() === '主歌词 / 画面';`), '↑ 选中画面');
  const groupTitles = await page.eval(js(`return sectionTitles().join('、');`));
  await key(page, 'ArrowDown', 'ArrowDown', 40);
  await page.waitFor(js(`return path() === '主歌词 / 画面 / 常规';`), '↓ 回到常规');
  check(groupTitles === '位置与变换、参考框、来源', `↑↓ 在结构树里移动：组没有“内部布局”一节（${groupTitles}）`);

  // 4. 歌词分配条与不可用的入口
  const bar = await page.eval(
    js(`return {
      source: assignmentSelect('来源')?.selectedOptions[0]?.textContent.trim(),
      container: assignmentSelect('默认容器')?.selectedOptions[0]?.textContent.trim(),
      containerDisabled: !!assignmentSelect('默认容器')?.disabled,
      hint: hint(),
      leadIns: (() => { const b = document.querySelector('.assignment-lead-ins'); return b && !b.disabled ? b.textContent.trim() : undefined; })(),
      clips: clipButton()?.textContent.trim(),
      clipsEnabled: !clipButton()?.disabled,
      unassigned: unassigned(),
      canvasHeight: document.querySelector('.timeline canvas').getBoundingClientRect().height,
      layout: !anyButton('快速布局…', document.querySelector('.assignment-bar'))?.disabled,
      layoutText: [layoutTag(), ...sectionLayouts()],
      layoutMode: (() => { const b = [...document.querySelectorAll('.canvas-mode')].find((m) => m.textContent === '布局参考'); return !!b && !b.disabled; })(),
    };`),
  );
  check(bar.source === tree.sections[0].title && bar.container === '常规 · 自动布局', `歌词分配条：来源是主歌词，默认容器是它的常规容器（${bar.source} → ${bar.container}）`);
  check(bar.containerDisabled && bar.hint.includes('只有一个歌词容器'), `只有一个容器时默认容器不能改，并写明原因（${bar.hint}）`);
  check(bar.leadIns === '前奏提示 · 0' && bar.layoutMode && bar.layout, `“快速布局…”“布局参考”模式和“${bar.leadIns}”可用`);
  check(
    bar.clips === '片段 · 0' && bar.clipsEnabled && bar.unassigned === '未单独分配 4 句' && bar.canvasHeight === 22 + 72 + 34 + 22 + 22 + 22,
    `分配条显示“${bar.unassigned}”，“${bar.clips}”可用；时间轴多了片段轨和前奏轨（画布高 ${bar.canvasHeight}）`,
  );
  await page.eval(`document.querySelector('.assignment-clips').click()`);
  await page.waitFor(`!!document.querySelector('.clip-panel')`, '展开片段列表');
  const emptyClips = await page.eval(
    js(`const panel = document.querySelector('.clip-panel'); const add = anyButton('＋ 新建片段', panel); return { title: panel.querySelector('.pane-title-text').textContent, add: !!add && !add.disabled, text: panel.textContent, empty: panel.querySelector('.clip-empty')?.textContent ?? '' };`),
  );
  check(
    emptyClips.title === '片段 · 0 个' && emptyClips.add && emptyClips.empty.includes('还没有片段') && !emptyClips.text.includes('G19-B'),
    `片段列表：“${emptyClips.title}”，写明还没有片段（${emptyClips.empty}）；“＋ 新建片段”可用，不再写 G19-B`,
  );
  check(bar.layoutText.join('、') === '布局：自动布局、布局：自动布局', `分配条和结构树的来源标题都显示当前布局（${bar.layoutText.join('、')}）`);

  // 5. 编排页的成片预览
  const hash = async (scope, rev = '0') => {
    const title = `document.querySelector('${scope} .preview-title')?.textContent ?? ''`;
    await page.waitFor(`(${title}).includes('修订 ${rev} ·') && !(${title}).includes('渲染中') && !!document.querySelector('${scope} .preview-frame img')`, `${scope} 的预览帧（修订 ${rev}）`, 60_000);
    await sleep(200);
    return page.eval(`(async () => {
      const bytes = await (await fetch(document.querySelector('${scope} .preview-frame img').src)).arrayBuffer();
      return [...new Uint8Array(await crypto.subtle.digest('SHA-256', bytes))].slice(0, 8).map((x) => x.toString(16).padStart(2, '0')).join('');
    })()`);
  };
  const orchestrationFrame = await hash('.orchestration');
  await gotoPage(page, 'appearance', `!!document.querySelector('.appearance')`);
  const appearanceFrame = await hash('.appearance');
  await gotoPage(page, 'orchestration', treeReady);
  check(orchestrationFrame === appearanceFrame, `编排页显示当前修订的成片预览帧，与外观页同一时刻的帧相同（${orchestrationFrame}）`);

  // 6. 切换页面播放头保持
  await clickButton(page, '全曲');
  const rect = await page.eval(`(() => { const r = document.querySelector('.timeline canvas').getBoundingClientRect(); return { x: r.x, y: r.y, w: r.width }; })()`);
  for (const type of ['mousePressed', 'mouseReleased']) await page.send('Input.dispatchMouseEvent', { type, x: rect.x + rect.w * (4.5 / 12), y: rect.y + 10, button: 'left', clickCount: 1 });
  await page.waitFor(`${playheadText}.startsWith('0:04.5')`, '定位到 4.5 秒');
  await gotoPage(page, 'lyrics', `!!document.querySelector('.line-list')`);
  const kept = await page.eval(playheadText);
  await gotoPage(page, 'orchestration', treeReady);
  const back = await page.eval(playheadText);
  check(kept.startsWith('0:04.5') && back.startsWith('0:04.5'), `切换页面：播放头保持（${kept} → ${back}）`);

  // 7. 快速布局（G18）：预演不改工程；套用一条历史，新容器成为默认，帧变化；再套同一预设没有变化；撤销后恢复
  const layoutStart = Number(await page.eval(js(`return revision();`)));
  const frameBeforeLayout = await hash('.orchestration', String(layoutStart));
  await clickButton(page, '快速布局…');
  await page.waitFor(js(`return dialog()?.open === true && !!card('DoubleCentered');`), '快速布局对话框');
  const opened = await page.eval(js(`return { cards: [...dialog().querySelectorAll('.layout-card .template-card-name')].map((e) => e.textContent), scope: dialog().querySelector('.layout-scope').textContent };`));
  await page.eval(js(`card('DoubleCentered').click();`));
  await page.waitFor(js(`return impact().startsWith('将影响');`), '预演结果', 10_000);
  const previewed = await page.eval(js(`return { impact: impact(), revision: revision() };`));
  check(
    opened.cards.join('、') === '单行居中、卡拉 OK 双行、双行居中、三行居中' && opened.scope.includes('整个来源（共 4 句）') && opened.scope.includes('在时间轴上选连续几句'),
    `快速布局对话框：四个预设，范围是整个来源（共 4 句），写明可以在时间轴上选几句（${opened.cards.join('、')}）`,
  );
  check(previewed.impact.includes('将影响 4 句') && previewed.impact.includes('2 个槽位') && Number(previewed.revision) === layoutStart, `预演写明影响且不改工程（修订仍是 ${previewed.revision}）：${previewed.impact}`);
  await snapshot(page, 'layout-dialog');
  await clickButton(page, '套用到 主歌词');
  await page.waitFor(js(`return Number(revision()) === ${layoutStart + 1} && !dialog()?.open && rows().length === 5;`), '套用双行居中', 15_000);
  await page.waitFor(js(`return rows().find((r) => r.selected)?.name === '双行居中';`), '选中新的默认容器', 10_000);
  const applied = await page.eval(
    js(`return { rows: rows(), layout: [layoutTag(), ...sectionLayouts()], containerDisabled: !!assignmentSelect('默认容器')?.disabled, hint: hint() };`),
  );
  const created = applied.rows.find((r) => r.name === '双行居中');
  check(
    !!created && created.role === '轮换·2' && created.isDefault && created.selected && applied.rows.filter((r) => r.name.startsWith('槽位 ')).length === 2 && !applied.rows.find((r) => r.name === '常规')?.isDefault,
    `套用双行居中：一条历史；结构树多出“双行居中”（轮换·2）和 2 个槽位，成为默认并被选中（${applied.rows.map((r) => r.name).join('、')}）`,
  );
  check(applied.layout.join('、') === '布局：双行居中、布局：双行居中', `分配条和来源标题显示“布局：双行居中”（${applied.layout.join('、')}）`);
  check(!applied.containerDisabled && !applied.hint.includes('R6'), `有两个容器后默认容器下拉框可用，不再写 R6（${applied.hint}）`);
  const frameAfterLayout = await hash('.orchestration', String(layoutStart + 1));
  check(frameAfterLayout !== frameBeforeLayout, `成片预览在同一时刻变化（${frameBeforeLayout} → ${frameAfterLayout}）`);
  await snapshot(page, 'layout-applied');

  // 7a. 改默认容器（R6 已修复）：一条历史，帧变化；撤销后恢复
  // 原生 select 只设值不会发 change，像用户选择一样手动发一次。
  await page.eval(js(`const s = assignmentSelect('默认容器'); s.value = [...s.options].find((o) => o.textContent.trim().startsWith('常规')).value; s.dispatchEvent(new Event('change', { bubbles: true }));`));
  await page.waitFor(js(`return Number(revision()) === ${layoutStart + 2} && rows().find((r) => r.name === '常规')?.isDefault;`), '默认容器改为常规', 10_000);
  const regularFrame = await hash('.orchestration', String(layoutStart + 2));
  const switched = await page.eval(js(`return [layoutTag(), ...sectionLayouts()];`));
  await key(page, 'z', 'KeyZ', 90, 2);
  await page.waitFor(js(`return Number(revision()) === ${layoutStart + 3} && rows().find((r) => r.name === '双行居中')?.isDefault;`), '撤销改默认容器', 10_000);
  const restoredFrame = await hash('.orchestration', String(layoutStart + 3));
  check(
    regularFrame === frameBeforeLayout && switched[0] === '布局：自动布局' && restoredFrame === frameAfterLayout,
    `改默认容器为“常规”：一条历史，帧回到自动布局的画面，布局文字随之变化（${switched[0]}）；撤销后默认容器回到“双行居中”，帧恢复（R6 已修复）`,
  );

  await clickButton(page, '快速布局…');
  await page.waitFor(js(`return dialog()?.open === true && !!card('DoubleCentered');`), '再次打开快速布局');
  await page.eval(js(`card('DoubleCentered').click();`));
  await page.waitFor(js(`return impact().includes('已经是这个布局');`), '同一预设的预演', 10_000);
  const again = await page.eval(
    js(`return { current: card('DoubleCentered').textContent.includes('当前'), submit: anyButton('套用到 主歌词', dialog())?.disabled, revision: revision() };`),
  );
  check(again.current && again.submit && Number(again.revision) === layoutStart + 3, '再套同一预设：卡片标“当前”，写明不会变化，套用按钮不可用，修订不变');
  await clickButton(page, '取消', 'dialog()');
  await page.waitFor(js(`return !dialog()?.open;`), '关闭对话框');

  await key(page, 'z', 'KeyZ', 90, 2);
  await page.waitFor(js(`return Number(revision()) === ${layoutStart + 4} && rows().length === 2;`), '撤销快速布局', 10_000);
  const undone = await page.eval(js(`return { rows: rows(), layout: [layoutTag(), ...sectionLayouts()] };`));
  const frameAfterUndo = await hash('.orchestration', String(layoutStart + 4));
  check(
    undone.rows.find((r) => r.name === '常规')?.isDefault && undone.layout.join('、') === '布局：自动布局、布局：自动布局' && frameAfterUndo === frameBeforeLayout,
    `Ctrl+Z 撤销：结构回到“常规”为默认，布局回到自动布局，帧与套用前相同（${frameAfterUndo}）`,
  );

  // 7b. 范围快速布局（G19）：时间轴上选第 2～3 句，只给这几句套单行居中
  const rangeStart = layoutStart + 4;
  // 切换页面后时间轴重新挂载、缩放回到缺省，先回到全曲视图。
  await clickButton(page, '全曲');
  await clickTimeline(page, 4.5, 'lines');
  await clickTimeline(page, 7.5, 'lines', 8);
  await page.waitFor(js(`return rangeTag().includes('已选第 2～3 句');`), '选中第 2～3 句');
  await clickButton(page, '快速布局…');
  await page.waitFor(js(`return dialog()?.open === true && !!card('SingleCentered') && scopes().length === 2;`), '范围快速布局对话框');
  const rangeScopes = await page.eval(js(`return scopes();`));
  await page.eval(js(`card('SingleCentered').click();`));
  await page.waitFor(js(`return impact().startsWith('将影响第');`), '范围预演', 10_000);
  const rangePreview = await page.eval(js(`return { impact: impact(), revision: revision(), submit: !!button('套用到第 2～3 句', dialog()) };`));
  check(
    rangeScopes[1]?.text === '选中的第 2～3 句（2 句）' && rangeScopes[1].checked && !rangeScopes[0].checked && rangePreview.submit,
    `选中几句后打开快速布局：范围默认是“${rangeScopes[1]?.text}”，套用按钮写“套用到第 2～3 句”`,
  );
  check(
    rangePreview.impact.includes('将影响第 2～3 句（2 句）') && rangePreview.impact.includes('默认容器不变') && Number(rangePreview.revision) === rangeStart,
    `范围预演不改工程（修订仍是 ${rangePreview.revision}）：${rangePreview.impact}`,
  );
  await snapshot(page, 'range-dialog');
  await clickButton(page, '套用到第 2～3 句');
  await page.waitFor(js(`return Number(revision()) === ${rangeStart + 1} && !dialog()?.open && rows().some((r) => r.name === '单行居中');`), '套用到第 2～3 句', 15_000);
  await page.waitFor(js(`return path() === '主歌词 / 片段 / 歌词段布局' && clipRows().length > 0;`), '选中新片段', 10_000);
  const ranged = await page.eval(
    js(`return {
      rows: rows(), clips: clipRows(), button: clipButton()?.textContent.trim(), unassigned: unassigned(), layout: layoutTag(),
      titles: sectionTitles(), range: value('范围'), container: selectText(inspectorSelect('目标容器')) ?? '',
      name: inspectorField('名称')?.value, remove: !!button('删除片段', document.querySelector('.orchestration-inspector')),
      pending: inspector().includes('G19-B'),
    };`),
  );
  const single = ranged.rows.find((r) => r.name === '单行居中');
  const selectedClip = ranged.clips.find((c) => c.selected);
  const keptClips = ranged.clips.filter((c) => c.kept);
  check(
    !!single && single.role === '轮换·1' && !single.isDefault && single.selected && ranged.rows.find((r) => r.name === '常规')?.isDefault && ranged.layout === '布局：自动布局',
    `范围套用：一条历史；结构树多出“单行居中”（轮换·1，被选中），默认容器仍是“常规”（${ranged.rows.map((r) => r.name).join('、')}）`,
  );
  check(
    !!selectedClip && selectedClip.text === '歌词段布局 第 2～3 句 → 单行居中' && keptClips.length > 0 && keptClips.every((c) => c.text.includes('保留原布局的歌词段') && c.text.endsWith('→ 常规')),
    `片段列表：选中“${selectedClip?.text}”，另有 ${keptClips.length} 个留在常规容器、标“保留原调度”的片段（${ranged.clips.map((c) => c.text).join('；')}）`,
  );
  check(
    ranged.button === `片段 · ${ranged.clips.length}` && ranged.unassigned === '未单独分配 0 句',
    `分配条：“${ranged.button}”，“${ranged.unassigned}”`,
  );
  check(
    ranged.titles.join('、') === '句子、目标容器' && ranged.range === '第 2～3 句（2 句）' && ranged.container.startsWith('单行居中') && ranged.name === '歌词段布局' && ranged.remove && !ranged.pending,
    `属性区显示片段：名称“${ranged.name}”，${ranged.titles.join('、')}，范围 ${ranged.range}，目标 ${ranged.container}；改名、改目标容器、删除可用（G19-B）`,
  );
  const frameAfterRange = await hash('.orchestration', String(rangeStart + 1));
  check(frameAfterRange !== frameBeforeLayout, `第 2 句的时刻成片预览变化（${frameBeforeLayout} → ${frameAfterRange}）`);
  await snapshot(page, 'range-applied');

  // 联动：点片段列表里留在常规的片段 → 属性区显示调度上下文，结构树选中常规；点片段轨上的“歌词段布局” → 选中它
  await page.eval(js(`document.querySelector('.clip-row[data-clip-id="${keptClips.at(-1).id}"]').click();`));
  await page.waitFor(js(`return path() === '主歌词 / 片段 / 保留原布局的歌词段';`), '选中保留的片段');
  const keptView = await page.eval(js(`return { titles: sectionTitles(), tree: rows().find((r) => r.selected)?.name, tag: rangeTag(), original: value('原段'), isDefault: value('默认段') };`));
  // 底部的提示条会盖住时间轴，等它消失再点。
  await page.waitFor(`document.querySelectorAll('mdui-snackbar').length === 0`, '提示条消失', 10_000);
  await clickTimeline(page, 4.5, 'clips');
  await page.waitFor(js(`return path() === '主歌词 / 片段 / 歌词段布局' && clipRows().find((c) => c.selected)?.id === ${JSON.stringify(selectedClip?.id)};`), '点片段轨选中片段');
  const bandView = await page.eval(js(`return { tree: rows().find((r) => r.selected)?.name, tag: rangeTag() };`));
  check(
    keptView.titles.includes('调度上下文') && keptView.tree === '常规' && keptView.original === '第 1～4 句' && keptView.isDefault.startsWith('是') && bandView.tree === '单行居中' && bandView.tag.includes('已选第 2～3 句'),
    `列表与片段轨联动：选中保留的片段时结构树选中常规、显示调度上下文（原段 ${keptView.original}，${keptView.tag}）；点片段轨选中“歌词段布局”，结构树选中单行居中（${bandView.tag}）`,
  );

  // 再套一次：后端是 no-op，修订不变；撤销后片段和容器都消失
  await clickButton(page, '快速布局…');
  await page.waitFor(js(`return dialog()?.open === true && scopes()[1]?.checked;`), '再次范围快速布局');
  await page.eval(js(`card('SingleCentered').click();`));
  await page.waitFor(js(`return impact().startsWith('将影响第');`), '再次范围预演', 10_000);
  await clickButton(page, '套用到第 2～3 句');
  await page.waitFor(js(`return !dialog()?.open;`), '再次套用后关闭对话框', 10_000);
  await sleep(300);
  const rangeAgain = await page.eval(js(`return { revision: revision(), snack: snack('已经是这个布局，没有变化') };`));
  check(Number(rangeAgain.revision) === rangeStart + 1 && rangeAgain.snack, '同一范围再套同一预设：提示“已经是这个布局，没有变化”，修订不变');

  // 7c. 片段编辑（G19-B）：在范围套用后的片段上改名、改目标容器、删除，再新建
  let rev = rangeStart + 1;
  const atRevision = (body) => js(`return Number(revision()) === ${rev} && (${body});`);
  const clipId = selectedClip?.id;
  await page.eval(js(`document.querySelector('.clip-row[data-clip-id="${clipId}"]').click();`));
  await page.waitFor(js(`return path() === '主歌词 / 片段 / 歌词段布局';`), '选中“歌词段布局”');
  await commitInspectorField(page, '名称', '副歌');
  rev++;
  await page.waitFor(atRevision(`clipRows().find((c) => c.id === '${clipId}')?.text === '副歌 第 2～3 句 → 单行居中'`), '改名为副歌', 10_000);
  const renamed = await page.eval(js(`return { path: path(), snack: snack('片段已改名为“副歌”') };`));
  await commitInspectorField(page, '名称', '');
  rev++;
  await page.waitFor(atRevision(`clipRows().find((c) => c.id === '${clipId}')?.text.startsWith('片段 2 第 2～3 句')`), '清除名称', 10_000);
  const cleared = await page.eval(js(`return { name: inspectorField('名称')?.value, placeholder: inspectorField('名称')?.placeholder, snack: snack('已清除片段名称') };`));
  await key(page, 'z', 'KeyZ', 90, 2);
  rev++;
  await page.waitFor(atRevision(`clipRows().find((c) => c.id === '${clipId}')?.text.startsWith('副歌') && inspectorField('名称')?.value === '副歌'`), '撤销清除名称', 10_000);
  check(
    renamed.path === '主歌词 / 片段 / 副歌' && renamed.snack && cleared.name === '' && cleared.placeholder === '片段 2' && cleared.snack,
    `改名：属性区输入“副歌”按 Enter 是一条历史，列表和路径跟着变；清空名称后列表显示“片段 2”；Ctrl+Z 恢复“副歌”`,
  );

  await page.eval(js(`const s = inspectorSelect('目标容器'); s.value = [...s.querySelectorAll('mdui-menu-item')].find((i) => i.textContent.trim().startsWith('常规')).value;`));
  rev++;
  await page.waitFor(atRevision(`clipRows().find((c) => c.id === '${clipId}')?.text === '副歌 第 2～3 句 → 常规'`), '改目标容器为常规', 10_000);
  const moved = await page.eval(js(`return { tree: rows().find((r) => r.selected)?.name, snack: snack('片段“副歌”已改到“常规”') };`));
  const frameMoved = await hash('.orchestration', String(rev));
  await key(page, 'z', 'KeyZ', 90, 2);
  rev++;
  await page.waitFor(atRevision(`clipRows().find((c) => c.id === '${clipId}')?.text === '副歌 第 2～3 句 → 单行居中'`), '撤销改目标容器', 10_000);
  const frameMoveUndone = await hash('.orchestration', String(rev));
  check(
    moved.tree === '常规' && moved.snack && frameMoved !== frameAfterRange && frameMoveUndone === frameAfterRange,
    `改目标容器为“常规”：一条历史，结构树跟着选中常规，第 2 句时刻的帧变化；Ctrl+Z 后回到单行居中，帧恢复`,
  );

  await page.eval(js(`document.querySelector('.clip-row[data-clip-id="${clipId}"]').click();`));
  await page.waitFor(js(`return path() === '主歌词 / 片段 / 副歌';`), '再次选中副歌');
  await clickButton(page, '删除片段', `document.querySelector('.orchestration-inspector')`);
  rev++;
  await page.waitFor(atRevision(`!clipRows().some((c) => c.id === '${clipId}')`), '删除片段', 10_000);
  const deleted = await page.eval(
    js(`return { unassigned: unassigned(), path: path(), tag: rangeTag(), snack: snack('已删除片段“副歌”，第 2～3 句回到默认容器'), newClip: !!button('为这几句新建片段', document.querySelector('.assignment-bar')) };`),
  );
  const frameDeleted = await hash('.orchestration', String(rev));
  check(
    deleted.unassigned === '未单独分配 2 句' && deleted.path === '主歌词 / 画面 / 常规' && deleted.tag.includes('已选第 2～3 句') && deleted.snack && deleted.newClip && frameDeleted !== frameAfterRange,
    `删除片段：一条历史，第 2～3 句回到默认容器（${deleted.unassigned}），结构树选中常规，选中的句子保留，“为这几句新建片段”可用；帧变化`,
  );

  // 已在片段里的句子不能新建：写明原因，按钮不可用
  await noSnack(page);
  await clickTimeline(page, 10.5, 'lines');
  await page.waitFor(js(`return rangeTag().includes('已选第 4 句');`), '选中第 4 句');
  await clickButton(page, '为这几句新建片段');
  await page.waitFor(js(`return clipDialog()?.open === true;`), '新建片段对话框');
  const blocked = await page.eval(
    js(`const d = clipDialog(); return { block: d.querySelector('.new-clip-block')?.textContent ?? '', submit: anyButton('创建片段', d)?.disabled };`),
  );
  check(
    blocked.block.includes('第 4 句已在片段“保留原布局的歌词段”里') && blocked.submit,
    `已在片段里的句子不能新建：对话框写明“${blocked.block}”，“创建片段”不可用`,
  );
  await clickButton(page, '取消', 'clipDialog()');
  await page.waitFor(js(`return !clipDialog()?.open;`), '关闭新建片段对话框');
  // 对话框的关闭动画期间遮罩仍会挡住点击。
  await sleep(600);

  // 选第 2～3 句新建片段：目标默认是另一个容器（单行居中）
  await clickTimeline(page, 4.5, 'lines');
  await clickTimeline(page, 7.5, 'lines', 8);
  await page.waitFor(js(`return rangeTag().includes('已选第 2～3 句');`), '再次选中第 2～3 句');
  await clickButton(page, '为这几句新建片段');
  await page.waitFor(js(`return clipDialog()?.open === true;`), '新建片段对话框');
  const form = await page.eval(
    js(`const d = clipDialog(); const f = (label) => [...d.querySelectorAll('mdui-text-field')].find((x) => x.label === label)?.value;
      return { first: f('从第'), last: f('到第'), target: selectText([...d.querySelectorAll('mdui-select')].find((x) => x.label === '目标容器')), block: d.querySelector('.new-clip-block')?.textContent ?? '', lines: d.querySelector('.new-clip-lines')?.textContent ?? '' };`),
  );
  await typeInto(page, '名称（可不填）', '副歌');
  await snapshot(page, 'clip-dialog');
  await clickButton(page, '创建片段', 'clipDialog()');
  rev++;
  await page.waitFor(atRevision(`!clipDialog()?.open && path() === '主歌词 / 片段 / 副歌'`), '创建片段', 10_000);
  const newClip = await page.eval(
    js(`return { row: clipRows().find((c) => c.selected)?.text, tree: rows().find((r) => r.selected)?.name, unassigned: unassigned(), snack: snack('已为第 2～3 句新建片段“副歌”') };`),
  );
  const frameCreated = await hash('.orchestration', String(rev));
  check(
    form.first === '2' && form.last === '3' && form.target?.startsWith('单行居中') && !form.block && form.lines.startsWith('2 句'),
    `新建片段对话框预填选中的第 ${form.first}～${form.last} 句（${form.lines}），目标默认是另一个容器“${form.target}”`,
  );
  check(
    newClip.row === '副歌 第 2～3 句 → 单行居中' && newClip.tree === '单行居中' && newClip.unassigned === '未单独分配 0 句' && newClip.snack && frameCreated !== frameDeleted,
    `创建片段：一条历史，列表选中“${newClip.row}”，结构树选中单行居中（${newClip.unassigned}），第 2 句时刻的帧变化`,
  );
  await snapshot(page, 'clip-created');

  // 连续撤销三次（新建、删除、改名）：回到范围套用后的样子
  for (let i = 0; i < 3; i++) {
    await key(page, 'z', 'KeyZ', 90, 2);
    rev++;
    await page.waitFor(js(`return Number(revision()) === ${rev};`), `撤销第 ${i + 1} 次`, 10_000);
  }
  await page.waitFor(js(`return clipRows().find((c) => c.id === '${clipId}')?.text === '歌词段布局 第 2～3 句 → 单行居中';`), '回到范围套用后', 10_000);
  const frameBack = await hash('.orchestration', String(rev));
  check(frameBack === frameAfterRange, 'Ctrl+Z 三次：新建的片段消失，删除的片段回来，名称回到“歌词段布局”，帧与范围套用后相同');

  await key(page, 'z', 'KeyZ', 90, 2);
  rev++;
  await page.waitFor(js(`return Number(revision()) === ${rev} && rows().length === 2 && clipRows().length === 0;`), '撤销范围快速布局', 10_000);
  const rangeUndone = await page.eval(js(`return { unassigned: unassigned(), button: clipButton()?.textContent.trim() };`));
  const frameAfterRangeUndo = await hash('.orchestration', String(rev));
  check(
    rangeUndone.unassigned === '未单独分配 4 句' && rangeUndone.button === '片段 · 0' && frameAfterRangeUndo === frameBeforeLayout,
    `Ctrl+Z 撤销范围套用：片段和容器都消失（${rangeUndone.button}，${rangeUndone.unassigned}），帧与套用前相同`,
  );

  // 7d. 结构编辑（G20）：新建轮换容器（3 个槽位）→改名→槽位增、排序、删→复制→新建组并把副本移进去（帧不变）→上移
  //     →默认容器、根、槽位不能做的操作写明原因→删除组→连续撤销回到原样
  await noSnack(page);
  const structureStart = rev;
  const frameBeforeStructure = await hash('.orchestration', String(rev));
  await page.eval(js(`clickRow('常规');`));
  await page.waitFor(js(`return path() === '主歌词 / 画面 / 常规';`), '选中常规');
  await page.eval(js(`tool('新建…').click();`));
  await page.waitFor(js(`return nodeDialog()?.open === true;`), '新建对话框打开');
  await sleep(300);
  const prefill = await page.eval(
    js(`const d = nodeDialog(); return { parent: selectText(dialogSelect(d, '放在')), kind: selectText(dialogSelect(d, '种类')), name: dialogField(d, '名称')?.value };`),
  );
  await page.eval(js(`dialogSelect(nodeDialog(), '种类').value = 'slotSequence';`));
  await page.waitFor(js(`return !!dialogField(nodeDialog(), '槽位数') && dialogField(nodeDialog(), '名称')?.value === '轮换';`), '换成轮换容器');
  await typeInDialog(page, 'nodeDialog()', '名称', '副歌轮换');
  await typeInDialog(page, 'nodeDialog()', '槽位数', '3');
  await clickButton(page, '新建', 'nodeDialog()');
  rev++;
  await page.waitFor(atRevision(`rows().find((r) => r.name === '副歌轮换')?.selected === true`), '新建轮换容器并选中', 10_000);
  const createdNode = await page.eval(
    js(`const all = rows(); const i = all.findIndex((r) => r.name === '副歌轮换');
      return { role: all[i].role, slots: all.slice(i + 1, i + 4).map((r) => r.name + '/' + r.level), path: path(), list: slotRows().length, snack: snack('已新建轮换容器“副歌轮换”（3 个槽位）') };`),
  );
  check(
    prefill.parent === '主歌词 / 画面' &&
      prefill.kind === '组' &&
      prefill.name === '新组' &&
      createdNode.role === '轮换·3' &&
      createdNode.slots.join('、') === '槽位 1/3、槽位 2/3、槽位 3/3' &&
      createdNode.path === '主歌词 / 画面 / 副歌轮换' &&
      createdNode.list === 3 &&
      createdNode.snack,
    `新建：预填放在“${prefill.parent}”、种类“${prefill.kind}”、名称“${prefill.name}”；换成轮换容器后名称跟着变；一条历史，结构树出现“副歌轮换”（${createdNode.role}，${createdNode.slots.join('、')}）并选中`,
  );
  await snapshot(page, 'structure-created');

  await noSnack(page);
  await commitInspectorField(page, '名称', '副歌');
  rev++;
  await page.waitFor(atRevision(`path() === '主歌词 / 画面 / 副歌'`), '改名为副歌', 10_000);
  check(await page.eval(js(`return rows().some((r) => r.name === '副歌' && r.selected) && snack('已改名为“副歌”');`)), '改名：属性区输入“副歌”按 Enter 是一条历史，结构树与路径跟着变');

  const slotsBefore = await page.eval(js(`return slotRows().map((r) => r.id);`));
  await noSnack(page);
  await clickButton(page, '＋ 加槽位', `document.querySelector('.orchestration-inspector')`);
  rev++;
  await page.waitFor(atRevision(`slotRows().length === 4 && rows().find((r) => r.name === '副歌')?.role === '轮换·4'`), '加槽位', 10_000);
  const added4 = await page.eval(js(`return { ids: slotRows().map((r) => r.id), snack: snack('已加槽位') };`));
  await page.eval(js(`slotTool(0, '下移').click();`));
  rev++;
  await page.waitFor(atRevision(`slotRows()[0]?.id === '${added4.ids[1]}'`), '下移第一个槽位', 10_000);
  const reordered = await page.eval(js(`return { ids: slotRows().map((r) => r.id), names: slotRows().map((r) => r.name), first: slotTool(0, '上移')?.parentElement.title ?? '' };`));
  await noSnack(page);
  await page.eval(js(`slotTool(3, '删除').click();`));
  rev++;
  await page.waitFor(atRevision(`slotRows().length === 3`), '删除最后一个槽位', 10_000);
  const removedSlot = await page.eval(js(`return { ids: slotRows().map((r) => r.id), snack: snack('已删除槽位 4') };`));
  check(
    added4.ids.length === 4 &&
      added4.ids[3].startsWith('slot/') &&
      added4.snack &&
      reordered.ids.join() === [added4.ids[1], added4.ids[0], ...added4.ids.slice(2)].join() &&
      reordered.names.join('、') === '槽位 1、槽位 2、槽位 3、槽位 4' &&
      reordered.first.includes('已经是第一个槽位') &&
      removedSlot.ids.join() === reordered.ids.slice(0, 3).join() &&
      removedSlot.snack,
    `槽位：加槽位（${added4.ids[3]}）、下移第 1 个（第一个的“上移”写明“已经是第一个槽位”）、删除第 4 个，各一条历史`,
  );

  await noSnack(page);
  await page.eval(js(`tool('复制').click();`));
  rev++;
  await page.waitFor(atRevision(`rows().find((r) => r.selected)?.name === '副歌 副本'`), '复制并选中副本', 10_000);
  const copied = await page.eval(js(`return { role: rows().find((r) => r.selected)?.role, ids: slotRows().map((r) => r.id), snack: snack('已复制“副歌”') };`));
  check(
    copied.role === '轮换·3' && copied.ids.length === 3 && copied.ids.every((id) => !removedSlot.ids.includes(id)) && copied.snack,
    `复制：一条历史，得到“副歌 副本”（${copied.role}），槽位是新的，并选中副本`,
  );

  await noSnack(page);
  const frameBeforeMove = await hash('.orchestration', String(rev));
  await page.eval(js(`tool('新建…').click();`));
  await page.waitFor(js(`return nodeDialog()?.open === true && dialogField(nodeDialog(), '名称')?.value === '新组';`), '新建对话框打开（组）');
  await typeInDialog(page, 'nodeDialog()', '名称', '角落');
  await clickButton(page, '新建', 'nodeDialog()');
  rev++;
  await page.waitFor(atRevision(`rows().find((r) => r.selected)?.name === '角落'`), '新建组并选中', 10_000);
  await page.waitFor(`!document.querySelector('mdui-dialog.new-node')?.open`, '新建对话框关闭');
  await sleep(600);
  await page.eval(js(`clickRow('副歌 副本');`));
  await page.eval(js(`tool('移到…').click();`));
  await page.waitFor(js(`return moveDialog()?.open === true;`), '移到对话框打开');
  await sleep(300);
  const moveOptions = await page.eval(js(`return [...dialogSelect(moveDialog(), '移到').querySelectorAll('mdui-menu-item')].map((i) => i.textContent.trim());`));
  await clickButton(page, '移动', 'moveDialog()');
  rev++;
  await page.waitFor(atRevision(`rows().find((r) => r.name === '副歌 副本')?.level === 3`), '移进角落', 10_000);
  const frameAfterMove = await hash('.orchestration', String(rev));
  const movedNode = await page.eval(js(`return { path: path(), snack: snack('已把“副歌 副本”移到“角落”') };`));
  check(
    moveOptions.join('、') === '主歌词 / 画面 / 角落、画布根层（不属于任何来源）' && movedNode.path === '主歌词 / 画面 / 角落 / 副歌 副本' && movedNode.snack && frameAfterMove === frameBeforeMove,
    `新建组“角落”，把副本移进去：可选 ${moveOptions.join('、')}；一条历史，路径“${movedNode.path}”，成片预览帧不变`,
  );
  await snapshot(page, 'structure-moved');

  await noSnack(page);
  await page.eval(js(`clickRow('副歌');`));
  const orderBefore = await page.eval(js(`return level2();`));
  await page.eval(js(`tool('上移').click();`));
  rev++;
  await page.waitFor(atRevision(`level2()[0] === '副歌'`), '上移副歌', 10_000);
  const orderAfter = await page.eval(js(`return { order: level2(), snack: snack('已上移“副歌”'), up: toolTitle('上移') };`));
  check(
    orderBefore.join('、') === '常规、副歌、角落' && orderAfter.order.join('、') === '副歌、常规、角落' && orderAfter.snack && orderAfter.up.includes('已经在最前'),
    `上移：一条历史，画面下的顺序 ${orderBefore.join('、')} → ${orderAfter.order.join('、')}；到最前后“上移”写明原因`,
  );

  await page.eval(js(`clickRow('常规');`));
  await page.waitFor(js(`return path() === '主歌词 / 画面 / 常规';`), '选中常规');
  const defaultBlocked = await page.eval(js(`return { disabled: !!tool('删除')?.disabled, title: toolTitle('删除') };`));
  await key(page, 'Delete', 'Delete', 46);
  await page.waitFor(js(`return status().startsWith('不能删除');`), '状态栏写明不能删除');
  const deleteKey = await page.eval(js(`return { status: status(), revision: Number(revision()) };`));
  await page.eval(js(`clickRow('画面');`));
  const rootBlocked = await page.eval(js(`return { copy: toolTitle('复制'), move: toolTitle('移到…'), remove: toolTitle('删除') };`));
  await page.eval(js(`clickRow('副歌');`));
  await page.waitFor(js(`return path() === '主歌词 / 画面 / 副歌' && slotRows().length === 3;`), '选中副歌');
  await page.eval(`document.querySelector('.orchestration-inspector .slot-name').click()`);
  await page.waitFor(js(`return path() === '主歌词 / 画面 / 副歌 / 槽位 1';`), '选中槽位 1');
  const slotBlocked = await page.eval(js(`return { move: toolTitle('移到…'), disabled: !!tool('移到…')?.disabled };`));
  check(
    defaultBlocked.disabled && defaultBlocked.title.includes('默认容器') && deleteKey.status.includes('默认容器') && deleteKey.revision === rev,
    `默认容器不能删：“删除”不可用（${defaultBlocked.title}）；按 Delete 只在状态栏写明原因，不发命令`,
  );
  check(
    rootBlocked.copy.includes('来源的根不能复制') && rootBlocked.move.includes('来源的根固定在画布根层') && rootBlocked.remove.includes('来源的根不能删除') && slotBlocked.disabled && slotBlocked.move.includes('槽位只能在它的轮换里调整顺序'),
    `来源的根不能复制、移动、删除；槽位不能移出轮换，各自写明原因（${slotBlocked.move}）`,
  );

  await noSnack(page);
  await page.eval(js(`clickRow('角落');`));
  await page.eval(js(`tool('删除').click();`));
  rev++;
  await page.waitFor(atRevision(`!rows().some((r) => r.name === '角落' || r.name === '副歌 副本')`), '删除角落', 10_000);
  const removedGroup = await page.eval(js(`return { path: path(), snack: snack('已删除“角落”及其中 4 个节点') };`));
  check(removedGroup.path === '主歌词 / 画面' && removedGroup.snack, `删除组：一条历史，连同其中的副本和槽位（4 个节点）一起删除，选中原来的父级“画面”`);

  const steps = rev - structureStart;
  for (let i = 0; i < steps; i++) {
    await key(page, 'z', 'KeyZ', 90, 2);
    rev++;
    await page.waitFor(js(`return Number(revision()) === ${rev};`), `结构编辑撤销第 ${i + 1} 次`, 10_000);
  }
  await page.waitFor(js(`return rows().length === 2;`), '结构回到画面和常规', 10_000);
  const frameAfterStructure = await hash('.orchestration', String(rev));
  await page.eval(js(`clickRow('常规');`));
  check(frameAfterStructure === frameBeforeStructure, `Ctrl+Z ${steps} 次：结构回到“画面 → 常规”，成片预览帧与开始时相同`);

  // 7e. 画布（G22）与属性编辑（G21）
  //     叠加层→点句子选中默认容器→点空白不改选择→拖动默认容器（一条历史、X/Y 变化、帧变化）→撤销→在铺满画布的框里空白处拖动不会动
  //     →改 Y 和参考框的宽→安全区不合法写明原因→改成轮换（3 个槽位）→同时最多显示 1、出现时机→改回自动布局（槽位留作子节点）
  //     →再改成轮换时预填并复用→布局参考：每个槽位一句代表文字，点文字选中槽位→动画策略改成静态再改回→连续撤销回到原样
  await noSnack(page);
  await page.eval(js(`clickRow('画面');`));
  await page.waitFor(js(`return path() === '主歌词 / 画面';`), '选中画面');
  await clickTimeline(page, 1.5, 'ruler');
  await page.waitFor(`${playheadText}.startsWith('0:01.5')`, '定位到 1.5 秒');
  // 预览换到新时刻之前，标题里还是旧时刻的帧。
  await page.waitFor(js(`return previewTitle().includes('· 0:01.500');`), '预览换到 1.5 秒', 30_000);
  const canvasFrame = await hash('.orchestration', String(rev));
  await page.waitFor(js(`return (overlay()?.inks ?? 0) > 0;`), '叠加层出现', 30_000);
  const drawn = await page.eval(js(`return overlay();`));
  check(
    drawn.frames.length >= 2 && drawn.inks >= 1 && drawn.selected,
    `叠加层（暂停时）：容器的参考框 ${drawn.frames.length} 个、当前句子的墨迹框 ${drawn.inks} 个，选中的“画面”高亮`,
  );

  await canvasClick(page, await page.eval(js(`return inkCenter();`)));
  await page.waitFor(js(`return path() === '主歌词 / 画面 / 常规';`), '点句子选中常规', 10_000);
  const box = await page.eval(js(`return canvasBox();`));
  await canvasClick(page, { x: box.x + 8, y: box.y + 8 });
  await sleep(300);
  const blank = await page.eval(js(`return { path: path(), selected: rows().find((r) => r.selected)?.name };`));
  check(blank.path === '主歌词 / 画面 / 常规' && blank.selected === '常规', '点选：点句子文字选中它所在的默认容器“常规”，结构树与属性区跟着变；点空白不改变选择');

  // 拖动：右 60、上 40 个屏幕像素，换算成父级参考里的比例
  await page.waitFor(js(`return (overlay()?.inks ?? 0) > 0 && overlay().selected;`), '拖动前叠加层在', 30_000);
  await canvasDrag(page, await page.eval(js(`return inkCenter();`)), 60, -40);
  rev++;
  await page.waitFor(atRevision(`snack('已移动“常规”')`), '拖动常规', 10_000);
  const dragged = await page.eval(js(`return { x: Number(inspectorField('X（%）').value), y: Number(inspectorField('Y（%）').value) };`));
  const expectX = 50 + ((60 * 1920) / box.w / 1920) * 100;
  const expectY = 50 - ((40 * 1080) / box.h / 1080) * 100;
  const draggedFrame = await hash('.orchestration', String(rev));
  check(
    Math.abs(dragged.x - expectX) < 0.5 && Math.abs(dragged.y - expectY) < 0.5 && draggedFrame !== canvasFrame,
    `拖动“常规”：一条历史，X、Y 按位移变化（${dragged.x}%、${dragged.y}%，预期约 ${expectX.toFixed(2)}%、${expectY.toFixed(2)}%），帧变化`,
  );
  await noSnack(page);
  await key(page, 'z', 'KeyZ', 90, 2);
  rev++;
  await page.waitFor(atRevision(`inspectorField('X（%）').value === '50' && inspectorField('Y（%）').value === '50'`), '撤销拖动', 10_000);
  const undoneFrame = await hash('.orchestration', String(rev));
  await page.waitFor(js(`return (overlay()?.inks ?? 0) > 0;`), '叠加层回来', 30_000);
  // 常规的框铺满画布：在空白处按下拖动不会挪走它
  await canvasDrag(page, { x: box.x + 20, y: box.y + 20 }, 60, 40);
  await sleep(800);
  const notMoved = await page.eval(js(`return { revision: Number(revision()), x: inspectorField('X（%）').value };`));
  check(
    undoneFrame === canvasFrame && notMoved.revision === rev && notMoved.x === '50',
    'Ctrl+Z 撤销拖动：回到原位，帧与拖动前相同；铺满画布的“常规”在空白处按下拖动不会被移动',
  );

  // 播放中隐藏叠加层并写明原因；暂停后回到 1.5 秒，叠加层回来
  await clickButton(page, '播放');
  await page.waitFor(js(`return !document.querySelector('.orchestration .canvas-overlay') && (document.querySelector('.canvas-status')?.textContent ?? '').includes('播放中不显示框');`), '播放中隐藏叠加层', 10_000);
  await clickButton(page, '暂停');
  await clickTimeline(page, 1.5, 'ruler');
  await page.waitFor(js(`return previewTitle().includes('· 0:01.500') && (overlay()?.inks ?? 0) > 0;`), '暂停后叠加层回来', 30_000);
  check(true, '播放中叠加层隐藏，画布下方写明“播放中不显示框”；暂停后叠加层回来');

  // 属性：Y 与参考框的宽
  const propertyStart = rev;
  await commitInspectorField(page, 'Y（%）', '30');
  rev++;
  await page.waitFor(atRevision(`inspectorField('Y（%）').value === '30' && snack('已调整“常规”的位置')`), '改 Y', 10_000);
  const yFrame = await hash('.orchestration', String(rev));
  await noSnack(page);
  await commitInspectorField(page, '宽（%）', '80');
  rev++;
  await page.waitFor(atRevision(`inspectorField('宽（%）').value === '80' && snack('已调整“常规”的参考框')`), '改参考框的宽', 10_000);
  check(yFrame !== canvasFrame, '改 Y（30%）、参考框的宽（80%）：各一条历史，提示可撤销，帧跟着变');

  await commitInspectorField(page, '右（%）', '0');
  await sleep(300);
  const badArea = await page.eval(js(`const f = inspectorField('右（%）'); return { invalid: f.invalid, message: f.validationMessage ?? '', revision: Number(revision()) };`));
  await page.eval(js(`inspectorField('右（%）').dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, composed: true }));`));
  await page.waitFor(js(`return inspectorField('右（%）').value === '100';`), 'Esc 恢复安全区右边');
  check(badArea.invalid && badArea.message.includes('右边要大于左边') && badArea.revision === rev, `安全区不合法时写明原因（${badArea.message}），修订不变；Esc 恢复`);

  // 最大宽度；默认位置设置（用当前位置作初值）→改 X→清除
  await noSnack(page);
  await commitInspectorField(page, '最大宽度（%）', '80');
  rev++;
  await page.waitFor(atRevision(`inspectorField('最大宽度（%）').value === '80' && snack('已调整最大宽度')`), '改最大宽度', 10_000);
  await noSnack(page);
  await page.eval(`document.querySelector('.orchestration-inspector .default-placement-set').click()`);
  rev++;
  await page.waitFor(atRevision(`!!document.querySelector('.orchestration-inspector .default-placement .prop-x') && snack('已设置默认位置')`), '设置默认位置', 10_000);
  const defaultSet = await page.eval(`document.querySelector('.orchestration-inspector .default-placement .prop-y').value`);
  await noSnack(page);
  await commitBySelector(page, '.default-placement .prop-x', '30');
  rev++;
  await page.waitFor(atRevision(`document.querySelector('.orchestration-inspector .default-placement .prop-x').value === '30' && snack('已调整默认位置')`), '改默认位置', 10_000);
  await noSnack(page);
  await page.eval(`document.querySelector('.orchestration-inspector .default-placement-clear').click()`);
  rev++;
  await page.waitFor(atRevision(`!document.querySelector('.orchestration-inspector .default-placement .prop-x') && !!document.querySelector('.orchestration-inspector .default-placement-unset')`), '清除默认位置', 10_000);
  check(defaultSet === '30', `最大宽度改成 80%；默认位置“设置”用当前位置作初值（Y ${defaultSet}%），改 X、清除，各一条历史`);

  // 动画策略：默认容器是自动布局、没有片段和前奏时能改成静态；静态时默认容器不能改成轮换并写明原因；再改回按来源角色
  await noSnack(page);
  await page.eval(js(`inspectorSelect('动画').value = 'Static';`));
  rev++;
  await page.waitFor(atRevision(`selectText(inspectorSelect('动画')) === '静态文本' && snack('动画已改为“静态文本”')`), '动画改成静态', 10_000);
  const staticKind = await page.eval(
    js(`const item = [...inspectorSelect('类型').querySelectorAll('mdui-menu-item')].find((i) => i.value === 'SlotSequence'); return { disabled: !!item?.disabled, hint: document.querySelector('.orchestration-inspector .kind-block')?.textContent ?? '' };`),
  );
  await noSnack(page);
  await page.eval(js(`inspectorSelect('动画').value = 'role';`));
  rev++;
  await page.waitFor(atRevision(`selectText(inspectorSelect('动画')) === '按来源角色' && !document.querySelector('.orchestration-inspector .kind-block')`), '动画改回按来源角色', 10_000);
  check(
    staticKind.disabled && staticKind.hint.includes('不能改成轮换'),
    `动画策略：能改成“静态文本”，再改回“按来源角色”，各一条历史；静态时默认容器的“轮换”不能选，写明原因（${staticKind.hint}）`,
  );

  // 布局类型：改成轮换（3 个槽位）
  await noSnack(page);
  await page.eval(js(`inspectorSelect('类型').value = 'SlotSequence';`));
  await page.waitFor(js(`return slotDialog()?.open === true;`), '改成轮换对话框打开');
  await sleep(300);
  const firstPrefill = await page.eval(js(`const f = dialogField(slotDialog(), '槽位数'); return { value: f.value, helper: f.helper };`));
  await typeInDialog(page, 'slotDialog()', '槽位数', '3');
  await clickButton(page, '改成轮换', 'slotDialog()');
  rev++;
  await page.waitFor(atRevision(`slotRows().length === 3 && sectionTitles().includes('槽位动画')`), '改成轮换', 10_000);
  const slotIds = await page.eval(js(`return slotRows().map((r) => r.id);`));
  const toSlots = await page.eval(js(`return { role: rows().find((r) => r.name === '常规')?.role, snack: snack('已改成轮换（3 个槽位）') };`));
  check(
    firstPrefill.value === '2' && firstPrefill.helper.includes('后端新建') && toSlots.role === '轮换·3' && toSlots.snack,
    `改成轮换：对话框预填 2（${firstPrefill.helper}），填 3 后一条历史，“常规”变成轮换·3，出现槽位列表和“槽位动画”`,
  );

  // 槽位动画：同时最多显示 1；出现时机改成“前一句开始后”，只显示延迟
  await noSnack(page);
  await commitInspectorField(page, '同时最多显示（句）', '1');
  rev++;
  await page.waitFor(atRevision(`inspectorField('同时最多显示（句）').value === '1' && snack('已调整槽位动画')`), '同时最多显示 1', 10_000);
  await noSnack(page);
  await page.eval(js(`inspectorSelect('出现时机').value = 'TimeAfterPreviousStart';`));
  rev++;
  await page.waitFor(atRevision(`!!inspectorField('延迟（秒）')`), '改出现时机', 10_000);
  const reveal = await page.eval(js(`return { progress: !!inspectorField('进度（%）'), lead: !!inspectorField('提前（秒）'), max: inspectorField('同时最多显示（句）').value };`));
  await noSnack(page);
  await commitInspectorField(page, '进入时长（秒）', '0.5');
  rev++;
  await page.waitFor(atRevision(`inspectorField('进入时长（秒）').value === '0.5'`), '改进入时长', 10_000);
  await commitBySelector(page, '.pose-enterFrom .pose-opacity', '20');
  rev++;
  await page.waitFor(atRevision(`document.querySelector('.orchestration-inspector .pose-enterFrom .pose-opacity').value === '20'`), '改进入起点的透明度', 10_000);
  check(
    !reveal.progress && !reveal.lead && reveal.max === '1',
    '槽位动画：同时最多显示改成 1、出现时机改成“前一句开始后”（只显示用到的“延迟”一项）、进入时长 0.5 秒、进入起点透明度 20%，各一条历史',
  );

  // 改回自动布局：槽位留作普通子节点；再改成轮换时预填并复用
  await noSnack(page);
  await page.eval(js(`inspectorSelect('类型').value = 'Auto';`));
  rev++;
  await page.waitFor(atRevision(`selectText(inspectorSelect('类型')) === '自动布局' && snack('已改成自动布局：3 个槽位留作普通子节点')`), '改回自动布局', 10_000);
  const leftover = await page.eval(js(`return rows().filter((r) => r.level === 3).map((r) => r.role);`));
  await noSnack(page);
  await page.eval(js(`inspectorSelect('类型').value = 'SlotSequence';`));
  await page.waitFor(js(`return slotDialog()?.open === true;`), '再次打开改成轮换对话框');
  await sleep(300);
  const reusePrefill = await page.eval(js(`const f = dialogField(slotDialog(), '槽位数'); return { value: f.value, helper: f.helper };`));
  await clickButton(page, '改成轮换', 'slotDialog()');
  rev++;
  await page.waitFor(atRevision(`slotRows().length === 3`), '再次改成轮换', 10_000);
  const reused = await page.eval(js(`return slotRows().map((r) => r.id);`));
  check(
    leftover.length === 3 && leftover.every((r) => r === '组') && reusePrefill.value === '3' && reusePrefill.helper.includes('现有的前 3 个子节点') && reused.join() === slotIds.join(),
    `改回自动布局：3 个槽位留作普通子节点（${leftover.join('、')}）；再改成轮换时预填 3、写明复用（${reusePrefill.helper}），槽位还是原来的节点`,
  );
  const slotPolicy = await page.eval(
    js(`return { disabled: [...inspectorSelect('动画').querySelectorAll('mdui-menu-item')].filter((i) => i.disabled).map((i) => i.value), hint: document.querySelector('.orchestration-inspector .policy-block')?.textContent ?? '' };`),
  );
  check(
    slotPolicy.disabled.join() === 'Static,FollowPrimary' && slotPolicy.hint.includes('默认容器是轮换'),
    `默认容器是轮换时，动画的“静态文本”“跟随主歌词”不能选，写明原因（${slotPolicy.hint}）`,
  );

  // 布局参考：每个槽位一句代表文字；点文字选中所在的槽位
  await page.eval(js(`canvasMode('布局参考').click();`));
  await page.waitFor(js(`return previewTitle().includes('布局参考 · 修订 ${rev}') && !!document.querySelector('.orchestration .preview-frame img') && (overlay()?.inks ?? 0) >= 3;`), '布局参考图', 30_000);
  const layoutView = await page.eval(js(`return overlay();`));
  await canvasClick(page, await page.eval(js(`return inkCenter(1);`)));
  await page.waitFor(js(`return path().startsWith('主歌词 / 画面 / 常规 / 槽位');`), '布局参考里点文字选中槽位', 10_000);
  const slotPath = await page.eval(js(`return path();`));
  check(
    slotIds.every((id) => layoutView.frames.includes(id)) && layoutView.inks >= 3,
    `布局参考：3 个槽位都有参考框，每个一句代表文字（墨迹框 ${layoutView.inks} 个）；点文字选中它所在的槽位（${slotPath}）`,
  );
  // 布局参考里拖动选中的槽位：一条历史，X 变化
  const slotX = await page.eval(js(`return inspectorField('X（%）').value;`));
  await page.waitFor(js(`return overlay()?.selected === true;`), '布局参考里选中槽位的框');
  await canvasDrag(page, await page.eval(js(`return inkCenter(1);`)), 40, 0);
  rev++;
  await page.waitFor(atRevision(`inspectorField('X（%）').value !== ${JSON.stringify(slotX)} && snack('已移动“')`), '布局参考里拖动槽位', 10_000);
  await page.waitFor(js(`return previewTitle().includes('布局参考 · 修订 ${rev}');`), '布局参考按新修订重测', 30_000);
  const slotMoved = await page.eval(js(`return inspectorField('X（%）').value;`));
  check(Number(slotMoved) > Number(slotX), `布局参考里拖动槽位：一条历史，X ${slotX}% → ${slotMoved}%，布局参考图按新修订重测`);
  await page.eval(js(`canvasMode('时间预览').click();`));
  await page.waitFor(js(`return previewTitle().startsWith('成片');`), '回到时间预览');

  const propertySteps = rev - propertyStart;
  for (let i = 0; i < propertySteps; i++) {
    await key(page, 'z', 'KeyZ', 90, 2);
    rev++;
    await page.waitFor(js(`return Number(revision()) === ${rev};`), `属性撤销第 ${i + 1} 次`, 10_000);
  }
  await page.waitFor(js(`return rows().length === 2 && selectText(inspectorSelect('类型')) === '自动布局';`), '属性回到原样', 10_000);
  const frameAfterProperties = await hash('.orchestration', String(rev));
  const restored = await page.eval(js(`return ['Y（%）', '宽（%）', '最大宽度（%）'].map((l) => inspectorField(l)?.value).join('/');`));
  check(
    frameAfterProperties === canvasFrame && restored === '50/100/90',
    `Ctrl+Z ${propertySteps} 次：属性回到原样（Y/宽/最大宽度 ${restored}），帧与开始时相同`,
  );

  // 7f. 前奏提示（G23）与句子（G24），第七批 §14.10
  //     新建提示组→添加前奏（3 秒放不下写明原因、修订不变；0.4 秒成功，列表与前奏轨出现，帧变化）→时长、停用、启用、改名
  //     →附着方式改成固定在父级再改回（画面不动）→删除→自动生成（默认 3 秒第 1 句跳过；改成 0.4 秒后新增；应用；设置保存）
  //     →删除并不再自动生成，再预演是“已排除”→标记：改字、加、上移、均匀排列、删到只剩一个
  //     →本句微调：时间轴上点第 2 句，改 X 偏移、清除→进入动画：开启工程默认，本句单独设置再关掉
  //     →画布：单击文字选中容器，双击选中这一句，拖动改本句偏移→连续撤销回到原样
  const timeLabel = (t) => `0:${t.toFixed(3).padStart(6, '0')}`;
  const frameAt = async (t) => {
    if (!(await page.eval(js(`return previewTitle().includes('· ${timeLabel(t)}');`)))) {
      await noSnack(page);
      await clickTimeline(page, t, 'ruler');
      await page.waitFor(js(`return previewTitle().includes('· ${timeLabel(t)}');`), `预览换到 ${t} 秒`, 30_000);
    }
    return hash('.orchestration', String(rev));
  };
  await noSnack(page);
  const leadStart = rev;
  const frameLeadBefore = await frameAt(0.3);

  // 新建前奏提示组（新建菜单）
  await page.eval(js(`clickRow('画面');`));
  await page.waitFor(js(`return path() === '主歌词 / 画面';`), '选中画面');
  await page.eval(js(`tool('新建…').click();`));
  await page.waitFor(js(`return nodeDialog()?.open === true;`), '新建对话框打开');
  await sleep(300);
  await page.eval(js(`dialogSelect(nodeDialog(), '种类').value = 'countdown';`));
  await page.waitFor(js(`return !!dialogField(nodeDialog(), '标记数') && dialogField(nodeDialog(), '名称')?.value === '前奏提示';`), '换成前奏提示组');
  const countdownPrefill = await page.eval(js(`const d = nodeDialog(); return { parent: selectText(dialogSelect(d, '放在')), marks: dialogField(d, '标记数').value };`));
  await clickButton(page, '新建', 'nodeDialog()');
  rev++;
  await page.waitFor(atRevision(`rows().find((r) => r.name === '前奏提示')?.selected === true`), '新建提示组并选中', 10_000);
  const countdown = await page.eval(
    js(`const all = rows(); const i = all.findIndex((r) => r.name === '前奏提示'); const marks = [];
      for (let j = i + 1; j < all.length && all[j].level > all[i].level; j++) marks.push(all[j].name);
      return { role: all[i].role, marks, path: path(), titles: sectionTitles(), snack: snack('已新建前奏提示组“前奏提示”（3 个标记）') };`),
  );
  check(
    countdownPrefill.parent === '主歌词 / 画面' && countdownPrefill.marks === '3' && countdown.marks.length === 3 && countdown.path === '主歌词 / 画面 / 前奏提示' &&
      countdown.titles.includes('前奏 · 0 个') && countdown.titles.includes('标记 · 3 个') && countdown.snack,
    `新建前奏提示组：放在“${countdownPrefill.parent}”、标记数预填 3，一条历史；结构树出现“前奏提示”（${countdown.role}）和三个标记（${countdown.marks.join('、')}），属性区有“前奏”“标记 · 3 个”两节`,
  );

  // 添加前奏：前奏提示页签→＋ 添加前奏；3 秒放不下（第 1 句 0.5 秒起唱），0.4 秒可以
  await noSnack(page);
  await page.eval(`document.querySelector('.assignment-lead-ins').click()`);
  await page.waitFor(`!!document.querySelector('.lead-in-panel')`, '前奏列表展开');
  const leadPanel = await page.eval(js(`return { tab: document.querySelector('.assignment-lead-ins').textContent.trim(), empty: !!document.querySelector('.lead-in-panel .clip-empty'), panels: document.querySelectorAll('.clip-panel').length };`));
  await page.eval(`document.querySelector('.lead-in-panel .lead-in-new').click()`);
  await page.waitFor(js(`return leadInDialog()?.open === true && !!dialogSelect(leadInDialog(), '提示组');`), '添加前奏对话框');
  await sleep(300);
  const leadPrefill = await page.eval(js(`const d = leadInDialog(); return { target: selectText(dialogSelect(d, '目标句')), indicator: selectText(dialogSelect(d, '提示组')), seconds: dialogField(d, '时长（秒）').value };`));
  if (!leadPrefill.target?.startsWith('第 1 句')) {
    await page.eval(js(`const s = dialogSelect(leadInDialog(), '目标句'); s.value = [...s.querySelectorAll('mdui-menu-item')].find((i) => i.textContent.trim().startsWith('第 1 句')).value;`));
    await sleep(200);
  }
  await clickButton(page, '添加前奏', 'leadInDialog()');
  await page.waitFor(js(`return !!leadInDialog()?.querySelector('.structure-dialog-error');`), '放不下时写明原因', 10_000);
  const conflict = await page.eval(js(`return { error: leadInDialog().querySelector('.structure-dialog-error').textContent, open: leadInDialog().open, revision: Number(revision()) };`));
  check(
    leadPanel.tab === '前奏提示 · 0' && leadPanel.empty && leadPanel.panels === 1 && leadPrefill.indicator === '前奏提示' && leadPrefill.seconds === '3',
    `前奏提示页签：列表为空，与片段列表只展开一个；添加前奏对话框预填“${leadPrefill.target}”、提示组“${leadPrefill.indicator}”、${leadPrefill.seconds} 秒`,
  );
  check(conflict.open && conflict.error.includes('放不下') && conflict.revision === rev, `3 秒放不下：对话框不关，写明原因（${conflict.error}），修订不变`);
  await typeInDialog(page, 'leadInDialog()', '时长（秒）', '0.4');
  await clickButton(page, '添加前奏', 'leadInDialog()');
  rev++;
  await page.waitFor(atRevision(`leadInRows().length === 1 && snack('已为第 1 句添加前奏') && path() === '主歌词 / 前奏提示 / 第 1 句'`), '添加前奏', 10_000);
  const addedLeadIn = await page.eval(js(`return { row: leadInRows()[0], tab: document.querySelector('.assignment-lead-ins').textContent.trim() };`));
  const frameLeadAfter = await frameAt(0.3);
  // 前奏轨：先选别的，再点前奏轨上的这一段
  await page.eval(js(`clickRow('画面');`));
  await page.waitFor(js(`return path() === '主歌词 / 画面';`), '选中画面');
  await noSnack(page);
  await clickTimeline(page, 0.3, 'leadIns');
  await page.waitFor(js(`return path() === '主歌词 / 前奏提示 / 第 1 句';`), '点前奏轨选中前奏', 10_000);
  check(
    addedLeadIn.row.text.includes('第 1 句') && addedLeadIn.row.text.includes('0:00.100～0:00.500') && addedLeadIn.row.text.includes('0.4 秒') && addedLeadIn.row.text.includes('手工') &&
      addedLeadIn.tab === '前奏提示 · 1' && frameLeadAfter !== frameLeadBefore,
    `添加前奏（0.4 秒）：一条历史，选中新前奏；列表一行（${addedLeadIn.row.text}），点前奏轨上的一段也选中它；0.3 秒处的帧变化`,
  );

  // 前奏属性：时长、停用、启用、改名
  await noSnack(page);
  await commitBySelector(page, '.lead-in-duration', '0.3');
  rev++;
  await page.waitFor(atRevision(`snack('已调整前奏') && leadInRows()[0]?.text.includes('0.3 秒') && leadInRows()[0].text.includes('0:00.200～0:00.500')`), '改前奏时长', 10_000);
  await noSnack(page);
  await page.eval(js(`switchOf('启用', document.querySelector('.orchestration-inspector')).click();`));
  rev++;
  await page.waitFor(atRevision(`snack('已停用前奏') && leadInRows()[0]?.off && leadInRows()[0].text.includes('已停用')`), '停用前奏', 10_000);
  await noSnack(page);
  await page.eval(js(`switchOf('启用', document.querySelector('.orchestration-inspector')).click();`));
  rev++;
  await page.waitFor(atRevision(`snack('已启用前奏') && leadInRows()[0] && !leadInRows()[0].off`), '启用前奏', 10_000);
  await noSnack(page);
  await commitBySelector(page, '.lead-in-name', '开场');
  rev++;
  await page.waitFor(atRevision(`snack('前奏已改名为“开场”') && leadInRows()[0]?.text.includes('“开场”')`), '前奏改名', 10_000);
  check(true, '前奏属性：时长改成 0.3 秒（时间 0:00.200～0:00.500）、停用（列表标“已停用”）、启用、改名“开场”，各一条历史');

  // 附着方式：固定在父级，再改回跟着文字；画面不动
  const frameAttached = await frameAt(0.3);
  await noSnack(page);
  await page.eval(`document.querySelector('.orchestration-inspector .lead-in-attachment').value = 'FixedParent'`);
  rev++;
  await page.waitFor(atRevision(`snack('提示组已改为固定在父级') && inspectorEl('.lead-in-attachment').value === 'FixedParent'`), '改成固定在父级', 10_000);
  const frameFixed = await frameAt(0.3);
  await noSnack(page);
  await page.eval(`document.querySelector('.orchestration-inspector .lead-in-attachment').value = 'CueTargetText'`);
  rev++;
  await page.waitFor(atRevision(`snack('提示组已改为跟着目标句的文字') && inspectorEl('.lead-in-attachment').value === 'CueTargetText'`), '改回跟着文字', 10_000);
  const frameReattached = await frameAt(0.3);
  check(
    frameFixed === frameAttached && frameReattached === frameAttached,
    `附着方式：改成“固定在父级”再改回，各一条历史；位置由后端换算，0.3 秒处的帧都不变（${frameAttached}）`,
  );

  // 删除（手工前奏没有“删除并不再自动生成”）
  const noExclude = await page.eval(`!document.querySelector('.orchestration-inspector .lead-in-exclude')`);
  await noSnack(page);
  await page.eval(`document.querySelector('.orchestration-inspector .lead-in-delete').click()`);
  rev++;
  await page.waitFor(atRevision(`snack('已删除第 1 句的前奏') && leadInRows().length === 0 && rows().some((r) => r.name === '前奏提示')`), '删除前奏', 10_000);
  check(noExclude, '删除前奏：一条历史，提示组保留；手工前奏没有“删除并不再自动生成”');

  // 自动生成：默认 3 秒时第 1 句起唱前不够；改成 0.4 秒后新增
  await noSnack(page);
  await page.eval(`document.querySelector('.lead-in-panel .lead-in-generate').click()`);
  await page.waitFor(js(`return generateDialog()?.open === true && genSummary() !== '' && !genSummary().includes('正在');`), '自动生成对话框与预演', 15_000);
  const gen1 = await page.eval(
    js(`const d = generateDialog(); return { summary: genSummary(), rows: genRows(), gap: dialogField(d, '间奏阈值（秒）').value, duration: dialogField(d, '提示时长（秒）').value, revision: Number(revision()) };`),
  );
  check(
    gen1.gap === '8' && gen1.duration === '3' && gen1.summary === '跳过 4' && gen1.rows[0]?.text.includes('起唱前的时间不够一个提示时长') && gen1.rows[1]?.text.includes('不是开头') && gen1.revision === rev,
    `自动生成：打开即预演（默认阈值 ${gen1.gap} 秒、时长 ${gen1.duration} 秒），${gen1.summary}；第 1 句起唱前不够、其余不是开头也没有长间奏；修订不变`,
  );
  await typeInDialog(page, 'generateDialog()', '提示时长（秒）', '0.4');
  await page.eval(js(`dialogField(generateDialog(), '提示时长（秒）').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, composed: true }));`));
  await page.waitFor(js(`return genSummary().startsWith('新增 1') && !genSummary().includes('正在');`), '改时长后重新预演', 15_000);
  const gen2 = await page.eval(js(`return { summary: genSummary(), rows: genRows(), revision: Number(revision()), submit: !!button('应用（1 句）', generateDialog()) };`));
  check(
    gen2.summary === '新增 1 · 跳过 3' && gen2.rows[0]?.status === '新增' && gen2.rows[0].checked && !gen2.rows[1]?.checked && gen2.submit && gen2.revision === rev,
    `改时长（0.4 秒）后重新预演：${gen2.summary}，第 1 句默认勾上，按钮写“应用（1 句）”，修订不变`,
  );
  await clickButton(page, '应用（1 句）', 'generateDialog()');
  rev++;
  await page.waitFor(atRevision(`snack('已应用自动生成：新增 1') && leadInRows().length === 1 && generateDialog()?.open !== true`), '应用自动生成', 10_000);
  const generated = await page.eval(js(`return leadInRows()[0];`));
  await noSnack(page);
  await page.eval(`document.querySelector('.lead-in-panel .lead-in-generate').click()`);
  await page.waitFor(js(`return generateDialog()?.open === true && genSummary() !== '' && !genSummary().includes('正在');`), '再打开自动生成', 15_000);
  const saved = await page.eval(js(`return dialogField(generateDialog(), '提示时长（秒）').value;`));
  await clickButton(page, '取消', 'generateDialog()');
  await page.waitFor(js(`return generateDialog()?.open !== true;`), '关闭自动生成');
  check(
    generated.text.includes('第 1 句') && generated.text.includes('自动') && generated.text.includes('0.4 秒') && saved === '0.4',
    `应用：一条历史，只加勾选的第 1 句（${generated.text}）；再打开时提示时长是保存下来的 ${saved} 秒`,
  );

  // 删除并不再自动生成：再预演这一句是“已排除”
  await page.eval(`document.querySelector('.lead-in-panel .lead-in-row').click()`);
  await page.waitFor(js(`return !!inspectorEl('.lead-in-exclude');`), '选中自动前奏');
  await page.eval(`document.querySelector('.orchestration-inspector .lead-in-exclude').click()`);
  rev++;
  await page.waitFor(atRevision(`snack('已删除第 1 句的前奏，自动生成不会再加这一句') && leadInRows().length === 0`), '删除并不再自动生成', 10_000);
  await noSnack(page);
  await page.eval(`document.querySelector('.lead-in-panel .lead-in-generate').click()`);
  await page.waitFor(js(`return generateDialog()?.open === true && genSummary() !== '' && !genSummary().includes('正在');`), '排除后再预演', 15_000);
  const gen3 = await page.eval(
    js(`return { rows: genRows(), excluded: generateDialog().querySelector('.generate-excluded').textContent, unexclude: !!generateDialog().querySelector('.generate-unexclude'), revision: Number(revision()) };`),
  );
  await clickButton(page, '取消', 'generateDialog()');
  await page.waitFor(js(`return generateDialog()?.open !== true;`), '关闭自动生成');
  check(
    gen3.rows[0]?.text.includes('已排除') && gen3.excluded.includes('已排除 1 句') && gen3.unexclude && gen3.revision === rev,
    `删除并不再自动生成：一条历史；再预演第 1 句是“已排除”（有“取消排除”），设置里写“已排除 1 句”`,
  );
  await snapshot(page, 'lead-ins');

  // 标记：改字、加、上移、均匀排列、删到只剩一个
  await page.eval(js(`clickRow('前奏提示');`));
  await page.waitFor(js(`return path() === '主歌词 / 画面 / 前奏提示' && markIds().length === 3;`), '选中提示组');
  await noSnack(page);
  await commitBySelector(page, '.mark-text', '3');
  rev++;
  await page.waitFor(atRevision(`snack('标记已改为“3”')`), '改标记的字', 10_000);
  await noSnack(page);
  await page.eval(`document.querySelector('.orchestration-inspector .mark-add').click()`);
  rev++;
  await page.waitFor(atRevision(`snack('已加标记') && markIds().length === 4 && sectionTitles().includes('标记 · 4 个')`), '加标记', 10_000);
  const marksBefore = await page.eval(js(`return markIds();`));
  await noSnack(page);
  await page.eval(js(`markTool(1, '.mark-up').click();`));
  rev++;
  await page.waitFor(atRevision(`markIds()[0] === '${marksBefore[1]}' && markIds()[1] === '${marksBefore[0]}'`), '上移标记', 10_000);
  await noSnack(page);
  await page.eval(`document.querySelector('.orchestration-inspector .mark-space').click()`);
  await page.waitFor(js(`return snack('已均匀排列标记') || snack('已经是均匀排列');`), '均匀排列', 10_000);
  const spaced = await page.eval(js(`return snack('已均匀排列标记');`));
  if (spaced) rev++;
  await page.waitFor(js(`return Number(revision()) === ${rev};`), '均匀排列后的修订');
  for (let n = 4; n > 1; n--) {
    await noSnack(page);
    await page.eval(js(`markTool(${n - 1}, '.mark-remove').click();`));
    rev++;
    await page.waitFor(atRevision(`markIds().length === ${n - 1}`), `删除标记（剩 ${n - 1} 个）`, 10_000);
  }
  const lastMark = await page.eval(js(`const b = markTool(0, '.mark-remove'); return { disabled: !!b?.disabled, title: b?.parentElement.title ?? '', titles: sectionTitles() };`));
  check(
    spaced && lastMark.disabled && lastMark.title.includes('提示组至少要有一个标记') && lastMark.titles.includes('标记 · 1 个'),
    `标记：改字、加标记、上移（顺序跟着变）、均匀排列、删除，各一条历史；只剩一个时不能删（${lastMark.title}）`,
  );

  // 本句微调：时间轴上点第 2 句
  const frameLine = await frameAt(4.5);
  await noSnack(page);
  await clickTimeline(page, 4.5, 'lines');
  await page.waitFor(js(`return path() === '主歌词 / 第 2 句' && !!inspectorEl('.line-x') && !inspectorEl('.line-x').disabled;`), '属性区显示第 2 句', 10_000);
  const lineView = await page.eval(
    js(`return { text: inspectorEl('.line-inspector-text')?.textContent, shown: value('显示在'), time: value('时间'), effective: effective(), add: !!inspectorEl('.line-lead-in-add'), clear: !!inspectorEl('.line-transform-clear')?.disabled };`),
  );
  check(
    lineView.text === '青い空見上げ' && lineView.time.startsWith('0:03.000') && lineView.effective.includes('没有进入动画') && lineView.add && lineView.clear,
    `时间轴上点第 2 句：属性区显示这一句（${lineView.text}，${lineView.time}，显示在：${lineView.shown}），没有微调时“清除”不可用，可以为它添加前奏`,
  );
  await commitBySelector(page, '.line-x', '10');
  rev++;
  await page.waitFor(atRevision(`snack('已调整本句微调') && inspectorEl('.line-x').value === '10'`), '改 X 偏移', 10_000);
  const frameShifted = await frameAt(4.5);
  await noSnack(page);
  await page.eval(`document.querySelector('.orchestration-inspector .line-transform-clear').click()`);
  rev++;
  await page.waitFor(atRevision(`snack('已清除本句微调') && inspectorEl('.line-x').value === '0'`), '清除本句微调', 10_000);
  const frameCleared = await frameAt(4.5);
  check(frameShifted !== frameLine && frameCleared === frameLine, '本句微调：X 偏移改成 10% 一条历史，帧变化；清除后一条历史，帧回到原样');

  // 进入动画：第 2 句 3.0 秒起唱，看 3.1 秒的帧
  const frameEnter = await frameAt(3.1);
  await noSnack(page);
  await page.eval(js(`switchOf('开启', document.querySelector('.orchestration-inspector')).click();`));
  rev++;
  await page.waitFor(atRevision(`snack('已开启工程默认进入动画') && effective().includes('用工程默认') && !!inspectorEl('.project-animation')`), '开启工程默认进入动画', 10_000);
  const frameEnterOn = await frameAt(3.1);
  const projectEffective = await page.eval(js(`return effective();`));
  await noSnack(page);
  await page.eval(js(`switchOf('本句单独设置', document.querySelector('.orchestration-inspector')).click();`));
  rev++;
  await page.waitFor(atRevision(`snack('已为这一句单独设置进入动画') && effective().includes('本句单独设置') && !!inspectorEl('.line-animation')`), '本句单独设置', 10_000);
  await noSnack(page);
  await page.eval(js(`switchOf('本句单独设置', document.querySelector('.orchestration-inspector')).click();`));
  rev++;
  await page.waitFor(atRevision(`snack('已清除本句进入动画，改用工程默认') && effective().includes('用工程默认') && !inspectorEl('.line-animation')`), '清除本句进入动画', 10_000);
  check(
    frameEnterOn !== frameEnter && projectEffective.startsWith('0.4 秒'),
    `进入动画：开启工程默认一条历史，第 2 句写明“${projectEffective}”，3.1 秒（起唱后 0.1 秒）的帧变化；本句单独设置再关掉，各一条历史`,
  );

  // 画布：单击文字仍选中容器；双击选中这一句；拖动改本句偏移
  await frameAt(4.5);
  await page.eval(js(`clickRow('画面');`));
  await page.waitFor(js(`return path() === '主歌词 / 画面' && (overlay()?.inks ?? 0) > 0;`), '选中画面，叠加层在', 30_000);
  await canvasClick(page, await page.eval(js(`return inkCenter();`)));
  await page.waitFor(js(`return path() === '主歌词 / 画面 / 常规';`), '单击文字选中常规', 10_000);
  await canvasDoubleClick(page, await page.eval(js(`return inkCenter();`)));
  await page.waitFor(js(`return path() === '主歌词 / 第 2 句' && !!document.querySelector('.orchestration .canvas-overlay .overlay-line');`), '双击选中第 2 句', 10_000);
  const focusedOverlay = await page.eval(js(`return { selected: overlay().selected, hint: document.querySelector('.orchestration').textContent.includes('双击文字选中这一句') };`));
  await page.waitFor(js(`return !!inspectorEl('.line-x') && !inspectorEl('.line-x').disabled;`), '第 2 句的展示读到');
  const lineBox = await page.eval(js(`return canvasBox();`));
  await canvasDrag(page, await page.eval(js(`return lineInkCenter();`)), 60, 0);
  rev++;
  await page.waitFor(atRevision(`snack('已移动第 2 句')`), '拖动第 2 句', 10_000);
  const lineDragged = await page.eval(js(`return { x: Number(inspectorEl('.line-x').value), y: Number(inspectorEl('.line-y').value), path: path(), rows: rows().find((r) => r.selected)?.name };`));
  const expectLineX = (60 / lineBox.w) * 100;
  check(
    !focusedOverlay.selected && focusedOverlay.hint && Math.abs(lineDragged.x - expectLineX) < 0.5 && Math.abs(lineDragged.y) < 0.5 && lineDragged.path === '主歌词 / 第 2 句',
    `画布：单击文字仍选中“常规”；双击选中第 2 句（只画这一句的墨迹，不画容器的选中框）；拖动一条历史，本句 X 偏移 ${lineDragged.x}%（预期约 ${expectLineX.toFixed(2)}%）`,
  );
  await snapshot(page, 'line');

  // 连续撤销回到开始时的修订与帧
  const leadSteps = rev - leadStart;
  await noSnack(page);
  for (let i = 0; i < leadSteps; i++) {
    await key(page, 'z', 'KeyZ', 90, 2);
    rev++;
    await page.waitFor(js(`return Number(revision()) === ${rev};`), `第七批撤销第 ${i + 1} 次`, 10_000);
  }
  await page.waitFor(js(`return rows().length === 2 && document.querySelector('.assignment-lead-ins').textContent.trim() === '前奏提示 · 0';`), '第七批回到原样', 10_000);
  const frameLeadUndone = await frameAt(0.3);
  const frameLineUndone = await frameAt(4.5);
  check(
    frameLeadUndone === frameLeadBefore && frameLineUndone === frameLine,
    `Ctrl+Z ${leadSteps} 次：提示组、前奏与句子的改动都撤销，0.3 秒与 4.5 秒的帧与开始时相同`,
  );
  // 后面的步骤按第一批的选择与片段列表检查
  await page.eval(js(`clickRow('常规');`));
  await page.waitFor(js(`return path() === '主歌词 / 画面 / 常规';`), '选中常规');
  await page.eval(`document.querySelector('.assignment-clips').click()`);
  await page.waitFor(`!!document.querySelector('.clip-panel') && !document.querySelector('.lead-in-panel')`, '回到片段列表');

  // 7g. 片段成员（第八批 §15.8）：第 2～3 句新建片段→调整成第 2～4 句（和现在相同时不能调整）→从第 3 句拆开（预填播放头所在句）
  //     →拆出的片段扩到第 1 句时写明“不在原段里”→选中第 3～4 句的片段与前一个合并→和没有调度上下文的片段不能合并→连续撤销
  const memberStart = rev;
  const membersDialog = `document.querySelector('mdui-dialog.clip-members')`;
  const splitDialog = `document.querySelector('mdui-dialog.split-clip')`;
  const inspectorButton = (cls) => `inspectorEl('mdui-button.${cls}')`;
  const reasons = `[...document.querySelectorAll('.orchestration-inspector .clip-member-reasons li')].map((li) => li.textContent)`;
  await noSnack(page);
  await clickTimeline(page, 4.5, 'lines');
  await clickTimeline(page, 7.5, 'lines', 8);
  await page.waitFor(js(`return rangeTag().includes('已选第 2～3 句');`), '第八批：选中第 2～3 句');
  await clickButton(page, '为这几句新建片段');
  await page.waitFor(js(`return clipDialog()?.open === true;`), '新建片段对话框');
  await typeInto(page, '名称（可不填）', '副歌');
  await clickButton(page, '创建片段', 'clipDialog()');
  rev++;
  await page.waitFor(atRevision(`!clipDialog()?.open && path() === '主歌词 / 片段 / 副歌'`), '第八批：新建片段', 10_000);
  await sleep(600);
  const fresh = await page.eval(
    js(`return { adjust: !${inspectorButton('clip-adjust')}.disabled, split: !${inspectorButton('clip-split')}.disabled, previous: ${inspectorButton('clip-merge-previous')}.disabled, next: ${inspectorButton('clip-merge-next')}.title, reasons: ${reasons} };`),
  );
  check(
    fresh.adjust && fresh.split && fresh.previous && fresh.next === '后面紧挨着的第 4 句不在片段里。' && fresh.reasons.includes('与前一个片段合并：前面紧挨着的第 1 句不在片段里。'),
    `片段属性区“句子”一节：调整句子、拆分可用；前后都没有片段时合并不可用，原因写在悬停说明和下面（${fresh.reasons.join('；')}）`,
  );

  // 调整句子：预填现在的范围，相同时不能调整；改成第 2～4 句
  const clipA = await page.eval(js(`return clipRows().find((c) => c.selected)?.id;`));
  await page.eval(js(`${inspectorButton('clip-adjust')}.click();`));
  await page.waitFor(js(`return ${membersDialog}?.open === true;`), '调整句子对话框');
  const sameForm = await page.eval(
    js(`const d = ${membersDialog}; return { first: dialogField(d, '从第')?.value, last: dialogField(d, '到第')?.value, block: d.querySelector('.clip-members-block')?.textContent ?? '', submit: anyButton('调整', d)?.disabled };`),
  );
  await typeInDialog(page, membersDialog, '到第', '4');
  const grownForm = await page.eval(js(`const d = ${membersDialog}; return { block: d.querySelector('.clip-members-block')?.textContent ?? '', lines: d.querySelector('.new-clip-lines')?.textContent ?? '', submit: anyButton('调整', d)?.disabled };`));
  check(
    sameForm.first === '2' && sameForm.last === '3' && sameForm.block === '和现在的句子相同。' && sameForm.submit && !grownForm.block && grownForm.lines.startsWith('3 句') && !grownForm.submit,
    `调整句子对话框预填第 ${sameForm.first}～${sameForm.last} 句，和现在相同时写明“${sameForm.block}”且“调整”不可用；改到第 4 句后可以调整（${grownForm.lines}）`,
  );
  await clickButton(page, '调整', membersDialog);
  rev++;
  await page.waitFor(atRevision(`!${membersDialog}?.open && clipRows().find((c) => c.id === '${clipA}')?.text.startsWith('副歌 第 2～4 句')`), '调整成第 2～4 句', 10_000);
  const grown = await page.eval(js(`return { range: value('范围'), snack: snack('片段“副歌”改为第 2～4 句'), rows: clipRows().length, selected: clipRows().find((c) => c.selected)?.id };`));
  check(
    grown.range === '第 2～4 句（3 句）' && grown.snack && grown.rows === 1 && grown.selected === clipA,
    `调整句子：一条历史，片段列表与属性区变成“${grown.range}”，仍选中这个片段，提示可撤销`,
  );
  await sleep(600);

  // 拆分：播放头在第 4 句时预填第 4 句；改成从第 3 句拆开
  await noSnack(page);
  await clickTimeline(page, 10.5, 'ruler');
  await page.waitFor(js(`return previewTitle().includes('· ${timeLabel(10.5)}');`), '播放头到第 4 句', 30_000);
  await page.eval(js(`${inspectorButton('clip-split')}.click();`));
  await page.waitFor(js(`return ${splitDialog}?.open === true;`), '拆分对话框');
  const splitSelect = `dialogSelect(${splitDialog}, '从哪一句开始拆出')`;
  const prefilled = await page.eval(js(`return { text: selectText(${splitSelect}), options: ${splitSelect}.querySelectorAll('mdui-menu-item').length };`));
  await page.eval(js(`const s = ${splitSelect}; s.value = [...s.querySelectorAll('mdui-menu-item')].find((i) => i.textContent.trim().startsWith('第 3 句')).value;`));
  await page.waitFor(js(`return (${splitDialog}.querySelector('.split-clip-result')?.textContent ?? '').startsWith('第 2 句留在“副歌”，第 3～4 句成为新片段');`), '拆分点改成第 3 句');
  const splitText = await page.eval(js(`return ${splitDialog}.querySelector('.split-clip-result').textContent;`));
  check(
    prefilled.text?.startsWith('第 4 句') && prefilled.options === 2 && splitText.includes('句子只能在第 2～4 句里调整'),
    `拆分对话框：拆分点是除第一句以外的 ${prefilled.options} 句，预填播放头所在的“${prefilled.text}”；改成第 3 句后写明“${splitText}”`,
  );
  await snapshot(page, 'split-dialog');
  await clickButton(page, '拆分', splitDialog);
  rev++;
  await page.waitFor(atRevision(`!${splitDialog}?.open && clipRows().length === 2`), '拆分片段', 10_000);
  const splitDone = await page.eval(
    js(`return { rows: clipRows(), path: path(), snack: snack('已把片段“副歌”拆成第 2 句和第 3～4 句'), context: value('原段'), range: value('范围') };`),
  );
  const clipB = splitDone.rows.find((c) => c.selected)?.id;
  check(
    splitDone.rows[0].id === clipA && splitDone.rows[0].text.startsWith('副歌 第 2 句') && splitDone.rows[1].text.startsWith('副歌 第 3～4 句') && clipB && clipB !== clipA &&
      splitDone.range === '第 3～4 句（2 句）' && splitDone.context === '第 2～4 句' && splitDone.snack,
    `拆分：一条历史，变成第 2 句和第 3～4 句两个同名片段，选中新片段；它的调度上下文是原段${splitDone.context}`,
  );
  await snapshot(page, 'split');
  await sleep(600);

  // 调度上下文：留在原片段的第 2 句扩到第 1 句时写明“不在原段里”
  await page.eval(js(`document.querySelector('.clip-row[data-clip-id="${clipA}"]').click();`));
  await page.waitFor(js(`return clipRows().find((c) => c.selected)?.id === '${clipA}' && value('范围') === '第 2 句';`), '选中第 2 句的片段');
  await page.eval(js(`${inspectorButton('clip-adjust')}.click();`));
  await page.waitFor(js(`return ${membersDialog}?.open === true;`), '调整句子对话框');
  await typeInDialog(page, membersDialog, '从第', '1');
  const outside = await page.eval(
    js(`const d = ${membersDialog}; return { note: d.querySelector('.clip-members-context')?.textContent ?? '', block: d.querySelector('.clip-members-block')?.textContent ?? '', submit: anyButton('调整', d)?.disabled };`),
  );
  check(
    outside.note.includes('原段（第 2～4 句）') && outside.block.startsWith('第 1 句不在原段（第 2～4 句）里') && outside.submit,
    `拆分后的片段扩到第 1 句：对话框写明“${outside.block}”，“调整”不可用`,
  );
  await snapshot(page, 'members-dialog');
  await clickButton(page, '取消', membersDialog);
  await page.waitFor(js(`return !${membersDialog}?.open;`), '关闭调整句子对话框');
  await sleep(600);

  // 合并：选中第 3～4 句的片段，与前一个合并，保留它的身份
  await page.eval(js(`document.querySelector('.clip-row[data-clip-id="${clipB}"]').click();`));
  await page.waitFor(js(`return clipRows().find((c) => c.selected)?.id === '${clipB}' && !${inspectorButton('clip-merge-previous')}.disabled;`), '选中第 3～4 句的片段');
  await noSnack(page);
  await page.eval(js(`${inspectorButton('clip-merge-previous')}.click();`));
  rev++;
  await page.waitFor(atRevision(`clipRows().length === 1`), '合并片段', 10_000);
  const merged = await page.eval(js(`return { rows: clipRows(), range: value('范围'), snack: snack('已把片段“副歌”合并进“副歌”（第 2～4 句）') };`));
  check(
    merged.rows[0].id === clipB && merged.rows[0].selected && merged.rows[0].text.startsWith('副歌 第 2～4 句') && merged.range === '第 2～4 句（3 句）' && merged.snack,
    `合并：一条历史，回到一个第 2～4 句的片段，身份是选中的那个，仍选中它`,
  );
  await sleep(600);

  // 和没有调度上下文的片段（第 1 句新建）不能合并
  await noSnack(page);
  await clickTimeline(page, 1.5, 'lines');
  await page.waitFor(js(`return rangeTag().includes('已选第 1 句');`), '选中第 1 句');
  await clickButton(page, '为这几句新建片段');
  await page.waitFor(js(`return clipDialog()?.open === true;`), '新建片段对话框');
  await clickButton(page, '创建片段', 'clipDialog()');
  rev++;
  await page.waitFor(atRevision(`!clipDialog()?.open && clipRows().length === 2`), '新建第 1 句的片段', 10_000);
  await sleep(600);
  await page.eval(js(`document.querySelector('.clip-row[data-clip-id="${clipB}"]').click();`));
  await page.waitFor(js(`return clipRows().find((c) => c.selected)?.id === '${clipB}';`), '再选中第 2～4 句的片段');
  const contextBlock = await page.eval(js(`return { disabled: ${inspectorButton('clip-merge-previous')}.disabled, title: ${inspectorButton('clip-merge-previous')}.title, reasons: ${reasons} };`));
  check(
    contextBlock.disabled && contextBlock.title === '两个片段来自不同的原段（调度上下文不同），不能合并。' && contextBlock.reasons.includes(`与前一个片段合并：${contextBlock.title}`),
    `与没有调度上下文的片段相邻时不能合并，写明“${contextBlock.title}”`,
  );
  await snapshot(page, 'members');

  // 连续撤销回到开始
  const memberSteps = rev - memberStart;
  await noSnack(page);
  for (let i = 0; i < memberSteps; i++) {
    await key(page, 'z', 'KeyZ', 90, 2);
    rev++;
    await page.waitFor(js(`return Number(revision()) === ${rev};`), `第八批撤销第 ${i + 1} 次`, 10_000);
  }
  await page.waitFor(js(`return clipRows().length === 0 && unassigned() === '未单独分配 4 句';`), '第八批回到原样', 10_000);
  check(memberSteps === 5, `Ctrl+Z ${memberSteps} 次（新建、调整、拆分、合并、新建）：片段都消失，4 句都回到默认容器`);
  await page.eval(js(`clickRow('常规');`));
  await page.waitFor(js(`return path() === '主歌词 / 画面 / 常规';`), '选中常规');

  // 8. 加一个翻译来源：编排按新修订重读，新来源有自己的程序（R4 已修复）；它的快速布局不可用并写明原因（R7）
  await gotoPage(page, 'lyrics', `!!document.querySelector('.line-list')`);
  const before = Number(await page.eval(js(`return revision();`)));
  await clickButton(page, '管理来源');
  await page.waitFor(`document.querySelector('.sources-drawer')?.open === true`, '管理来源面板打开');
  await clickButton(page, '添加翻译来源');
  await typeInto(page, '新来源名称', '中文');
  await clickButton(page, '添加');
  await page.waitFor(js(`return Number(revision()) === ${before + 1};`), '添加翻译来源', 10_000);
  await page.eval(`document.querySelector('.sources-drawer').open = false`);
  await gotoPage(page, 'orchestration', treeReady);
  await page.waitFor(js(`return sections().length === 2;`), '结构树出现新来源', 10_000);
  const added = await page.eval(js(`return { sections: sections(), path: path() };`));
  const translation = added.sections.find((s) => s.title === '中文');
  check(
    !!translation && translation.rows === 2 && !translation.empty && added.path === '主歌词 / 画面 / 常规',
    `加翻译来源后结构按新修订重读：新来源“中文”有自己的画面和常规容器（R4 已修复）；选中的节点保留（${added.path}）`,
  );
  await page.eval(js(`const s = assignmentSelect('来源'); s.value = [...s.options].find((o) => o.textContent.trim() === '中文').value; s.dispatchEvent(new Event('change', { bubbles: true }));`));
  await page.waitFor(js(`return path() === '中文 / 画面 / 常规';`), '分配条切到新来源');
  const r7 = await page.eval(
    js(`const b = anyButton('快速布局…', document.querySelector('.assignment-bar')); const add = anyButton('＋ 新建片段', document.querySelector('.clip-panel'));
      return { disabled: !!b?.disabled, title: b?.title ?? '', hint: hint(), revision: revision(), clip: !!add?.disabled && add.title.includes('不能建片段'), clipHint: document.querySelector('.clip-panel-header .hint')?.textContent ?? '' };`),
  );
  check(
    r7.disabled && r7.title.includes('只支持独立计时') && r7.hint.includes('只支持独立计时') && Number(r7.revision) === before + 1,
    `翻译来源的“快速布局…”不可用，并写明原因（R7）：${r7.hint}`,
  );
  check(r7.clip && r7.clipHint.includes('不能建片段'), `翻译来源的“＋ 新建片段”不可用，并写明原因：${r7.clipHint}`);
  await snapshot(page, 'translation');

  // 9. 撤销：新来源消失，分配条回到主歌词
  await key(page, 'z', 'KeyZ', 90, 2);
  // 撤销本身也是一个新修订。
  await page.waitFor(js(`return Number(revision()) === ${before + 2} && sections().length === 1;`), '撤销后', 10_000);
  await page.waitFor(js(`return assignmentSelect('来源')?.value !== undefined && hint().includes('只有一个歌词容器');`), '分配条回到主歌词', 10_000);
  check(true, 'Ctrl+Z 撤销：新来源从结构树消失，歌词分配条回到主歌词');

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
    const failure = join(tmpdir(), 'mako-orchestration-smoke-failure.png');
    await writeFile(failure, await page.screenshot()).catch(() => undefined);
    console.error(`  失败时的页面：${failure}`);
    const text = await page.eval(`document.body.innerText`).catch(() => '');
    console.error(`  页面文字：${String(text).slice(0, 600).replace(/\n+/g, ' ⏎ ')}`);
  }
  for (const message of errors) console.error(`  页面异常：${message}`);
} finally {
  await browser.close();
  rmSync(assets, { recursive: true, force: true });
}
