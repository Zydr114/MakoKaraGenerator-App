// 编排页的浏览器冒烟（W4 设计 §5、§9.5、§10.8、§11.8、§12.8、§13.10、§14.10、§15.8；界面改版批 5）：用真实 Chrome 走一遍 W4 第一批（骨架）、
// 第二批（G18 快速布局）、第三批（G19 范围快速布局与片段）、第四批（G19-B 片段编辑）、第五批（G20 结构编辑）、第六批（G21 属性、G22 画布）、
// 第七批（G23 前奏提示、G24 本句微调与进入动画）与第八批（片段成员）的验收链路。
// 新建工程 → 顶栏“编排”位于“歌词”和“外观”之间 → 结构树（画面 → 常规，默认容器）与可编辑的属性 → ↑↓ 移动选中
// → 歌词分配（只有一个容器时不能改，未单独分配句数，片段列表为空、可以新建，前奏列表可用）
// → 编排页的成片预览帧（与外观页相同）→ 切换页面播放头保持
// → 快速布局：预演不改工程，套用双行居中（一条历史，新容器成为默认，帧变化）；改默认容器→撤销（R6 已修复）；再套同一预设没有变化；撤销后恢复
// → 范围快速布局：时间轴上选第 2～3 句，套单行居中（默认容器不变，片段轨与片段列表出现片段，属性区显示片段，帧变化），
//   列表与时间轴联动，再套一次没有变化
// → 片段编辑（G19-B）：改名→清除名称→撤销，改目标容器（帧变化）→撤销，删除（几句回到默认容器），已在片段里的句子不能新建并写明原因，
//   选两句新建片段（目标默认是另一个容器，帧变化），连续撤销回到范围套用后的样子 → 撤销范围套用后片段消失
// → 结构编辑（G20）：新建轮换容器（3 个槽位）并选中，改名，加槽位、下移槽位、删除槽位，复制，新建组并把副本移进去（帧不变），上移，
//   默认容器、来源的根、槽位不能做的操作在“结构操作”菜单里写明原因（Delete 键只在状态栏说明），删除组，连续撤销回到原样
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
// 控件按 ARIA 角色和可访问名称找（scripts/ui-query.mjs）；结构树的行、片段与前奏列表、槽位与标记按 data-* 找，检查器的路径在 .ihead 的 data-path 上。
// 前提：pnpm dev:backend 与 pnpm dev 都在运行。
// 用法：node scripts/orchestration-smoke.mjs [--url http://localhost:5173] [--screenshot out.png] [--headed]

import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { parseArgs } from 'node:util';
import { launchChrome } from './cdp.mjs';
import { shell, ui, uiJs } from './ui-query.mjs';

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

// 页面里的读取函数（可以用 ui-query 的 __ui）。
const PRELUDE = `
  const sq = (t) => (t ?? '').replace(/\\s+/g, ' ').trim();
  const revision = () => (document.querySelector('.status-bar')?.textContent.match(/修订 (\\d+)/) ?? [])[1];
  // 结构树：每行是一个节点（.tree-head 是来源的标题行）
  const rows = () => [...document.querySelectorAll('.orchestration .structure-tree .tree-row')].map((r) => ({
    id: r.querySelector('.node')?.dataset.nodeId,
    name: r.querySelector('.node-name')?.textContent ?? '',
    role: r.querySelector('.node > .role')?.textContent ?? '',
    level: Number(r.getAttribute('aria-level')),
    isDefault: [...r.querySelectorAll('.node-badge')].some((b) => b.textContent.trim() === '默认'),
    selected: r.getAttribute('aria-selected') === 'true',
  }));
  const sections = () => [...document.querySelectorAll('.orchestration .structure-tree .tree-section')].map((s) => ({
    title: s.querySelector('.sec-title')?.textContent ?? '',
    empty: s.querySelector('.sec-empty')?.textContent ?? '',
    rows: s.querySelectorAll('.tree-row').length,
  }));
  const level2 = () => rows().filter((r) => r.level === 2).map((r) => r.name);
  // 检查器
  const insp = () => document.querySelector('.orchestration .orch-insp');
  const path = () => insp()?.querySelector('.ihead')?.dataset.path;
  const groupTitles = () => [...(insp()?.querySelectorAll('.grp-title') ?? [])].map((h) => h.textContent);
  const value = (label) => [...(insp()?.querySelectorAll('dl.info-list > div') ?? [])].find((r) => r.querySelector('dt')?.textContent === label)?.querySelector('dd')?.textContent;
  const field = (label, scope = '.orch-insp') => __ui.find('textbox', label, false, scope);
  const fieldValue = (label, scope) => field(label, scope)?.value;
  const help = (el) => el?.closest('.tf')?.querySelector('.help')?.textContent ?? '';
  const selectText = (label, scope = '.orch-insp') => __ui.find('select', label, false, scope)?.querySelector('.select-value')?.textContent.trim();
  const selectEnabled = (label, scope = '.orch-insp') => { const el = __ui.find('select', label, false, scope); return !!el && !__ui.disabled(el); };
  const button = (name, scope) => __ui.find('button', name, false, scope);
  const enabled = (name, scope) => { const b = button(name, scope); return !!b && !__ui.disabled(b); };
  const toolTitle = (label) => button(label)?.closest('.tool-wrap')?.title ?? '';
  // 分段按钮：选中的一段、某一段是否可用
  const seg = (label) => sq(document.querySelector('[role=radiogroup][aria-label="' + label + '"] [aria-checked=true]')?.textContent);
  const segEnabled = (label, option) => { const b = [...document.querySelectorAll('[role=radiogroup][aria-label="' + label + '"] [role=radio]')].find((x) => sq(x.textContent) === option); return !!b && !__ui.disabled(b); };
  // 左栏下部的歌词分配
  const assign = (label) => selectText(label, '.assign');
  const hint = () => document.querySelector('.assign-hint')?.textContent ?? '';
  const layoutTag = () => document.querySelector('.assign-layout')?.textContent ?? '';
  const sectionLayouts = () => [...document.querySelectorAll('.structure-tree .sec-layout')].map((e) => e.textContent);
  const unassigned = () => document.querySelector('.assign-unassigned')?.textContent ?? '';
  const rangeTag = () => document.querySelector('.assign-range .tag')?.textContent ?? '';
  const panels = () => [...document.querySelectorAll('.assign-panels [role=radio]')].map((b) => ({ text: sq(b.textContent), checked: b.getAttribute('aria-checked') === 'true', enabled: !__ui.disabled(b) }));
  // 列表为空时 React Aria 也渲染一行放空态，只取有片段（前奏）的行
  const clipRows = () => [...document.querySelectorAll('.clip-list [role=row]')].filter((r) => r.querySelector('.clip')).map((r) => {
    const c = r.querySelector('.clip');
    const name = c?.querySelector('.clip-name')?.textContent ?? '';
    const range = c?.querySelector(':scope > .tnum')?.textContent ?? '';
    const target = sq(c?.querySelector('.clip-text .faint')?.textContent).replace(' · 保留原调度', '');
    return { id: c?.dataset.clipId, name, range, target, text: name + ' ' + range + ' ' + target, kept: (c?.textContent ?? '').includes('保留原调度'), selected: r.getAttribute('aria-selected') === 'true' };
  });
  const leadInRows = () => [...document.querySelectorAll('.lead-in-list [role=row]')].filter((r) => r.querySelector('.lead-in')).map((r) => ({ id: r.querySelector('.lead-in')?.dataset.leadInId, text: sq(r.textContent), off: r.classList.contains('is-off') }));
  const slotRows = () => [...(insp()?.querySelectorAll('.slot[data-slot-id]') ?? [])].map((r) => ({ id: r.dataset.slotId, name: r.querySelector('.slot-name').textContent }));
  const markIds = () => [...(insp()?.querySelectorAll('.mark-row') ?? [])].map((r) => r.dataset.markId);
  const effective = () => insp()?.querySelector('.line-animation-effective')?.textContent ?? '';
  const reasons = () => [...(insp()?.querySelectorAll('.clip-member-reasons li') ?? [])].map((li) => li.textContent);
  const toast = () => document.querySelector('.toast-region .toast-text')?.textContent ?? '';
  const snack = (text) => toast().includes(text);
  const status = () => document.querySelector('.status-bar .notice')?.textContent ?? '';
  // 最上层的对话框
  const dlg = () => __ui.all('dialog', null).at(-1);
  const inDialog = (css) => dlg()?.querySelector(css)?.textContent ?? '';
  const dialogError = () => inDialog('.note.err');
  const genSummary = () => inDialog('.generate-summary');
  const genRows = () => [...(dlg()?.querySelectorAll('.generate-row') ?? [])].map((r) => ({ text: sq(r.textContent), status: r.querySelector('.generate-status')?.textContent ?? '', checked: !!r.querySelector('input[type=checkbox]')?.checked }));
  // 画布
  const overlay = () => {
    const svg = document.querySelector('.orchestration .canvas-overlay');
    return svg ? { frames: [...svg.querySelectorAll('.overlay-frame')].map((f) => f.dataset.nodeId), inks: svg.querySelectorAll('.overlay-ink').length, selected: !!svg.querySelector('.overlay-selected') } : undefined;
  };
  const center = (el) => { const r = el?.getBoundingClientRect(); return r && { x: r.x + r.width / 2, y: r.y + r.height / 2 }; };
  const inkCenter = (i = 0) => center(document.querySelectorAll('.orchestration .canvas-overlay .overlay-ink')[i]);
  const lineInkCenter = () => center(document.querySelector('.orchestration .canvas-overlay .overlay-line'));
  const canvasBox = () => {
    const r = document.querySelector('.orchestration .canvas-overlay')?.getBoundingClientRect();
    return r && { x: r.x, y: r.y, w: r.width, h: r.height };
  };
  const previewTitle = () => document.querySelector('.orchestration .preview-title')?.textContent ?? '';
`;
const js = (body) => uiJs(`${PRELUDE} ${body}`);
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function key(page, keyName, code, keyCode, modifiers = 0) {
  for (const type of ['keyDown', 'keyUp']) await page.send('Input.dispatchKeyEvent', { type, key: keyName, code, windowsVirtualKeyCode: keyCode, modifiers });
}
const escape = (page) => key(page, 'Escape', 'Escape', 27);
/** 焦点放回 body：留在输入框里时 Ctrl+Z 会让给输入框。 */
const blur = (page) => page.eval(`document.activeElement?.blur()`);
async function undo(page) {
  await blur(page);
  await key(page, 'z', 'KeyZ', 90, 2);
}

async function gotoPage(page, value, ready) {
  await shell.goto(page, value);
  await page.waitFor(ready, `进入 ${value}`);
}

/** 底部的提示条可能盖住时间轴：有就点掉。 */
async function dismissToast(page) {
  if (await ui.exists(page, 'button', '关闭提示')) await ui.click(page, 'button', '关闭提示', { timeout: 3_000 }).catch(() => undefined);
  await page.waitFor(`!document.querySelector('.toast-region .toast')`, '提示条消失', 10_000);
}

const playheadText = `(document.querySelector('.transport-time span')?.textContent ?? '')`;

/** 时间轴上的点击：t 秒处，lane 是 ruler／lines／clips／leadIns；modifiers 8 是 Shift。全曲视图下 12 秒铺满画布宽度。 */
async function clickTimeline(page, t, lane, modifiers = 0) {
  await dismissToast(page);
  const rect = await page.eval(`(() => { const r = document.querySelector('.timeline-canvas canvas').getBoundingClientRect(); return { x: r.x, y: r.y, w: r.width }; })()`);
  const y = { ruler: 10, lines: 22 + 72 + 34 + 11, clips: 22 + 72 + 34 + 22 + 11, leadIns: 22 + 72 + 34 + 22 + 22 + 11 }[lane];
  for (const type of ['mousePressed', 'mouseReleased']) await page.send('Input.dispatchMouseEvent', { type, x: rect.x + rect.w * (t / 12), y: rect.y + y, button: 'left', clickCount: 1, modifiers });
}
const treeReady = js(`return rows().length > 0;`);

/** 提交式输入框：填值按回车，再把焦点放回 body。scope 是 CSS 选择器或 '@dialog'。 */
async function commit(page, label, value, scope = '.orch-insp') {
  await ui.fill(page, label, value, { scope, enter: true });
  await blur(page);
}

/** 结构树里点一个节点（按名称）。 */
const clickRow = (page, name) => ui.click(page, 'treeitem', name, { scope: '.structure-tree' });
const clickClip = (page, id) => ui.click(page, `.clip-list [data-clip-id="${id}"]`, null);

/** 打开下拉框读选项（文字与是否可选），再收起。 */
async function selectOptions(page, label, scope) {
  await ui.click(page, 'select', label, { scope });
  await page.waitFor(`!!document.querySelector('.popover [role=listbox] [role=option]')`, `下拉「${label}」展开`);
  const options = await page.eval(uiJs(`return [...document.querySelectorAll('.popover [role=listbox] [role=option]')].map((o) => ({ text: __ui.text(o), disabled: o.getAttribute('aria-disabled') === 'true' }));`));
  await escape(page);
  await page.waitFor(`!document.querySelector('.popover [role=listbox]')`, `下拉「${label}」收起`);
  return options;
}

/** 画面结构的“结构操作”菜单：各项是否可用、不可用的原因；读完关上。 */
async function structureMenu(page) {
  await ui.click(page, 'button', '结构操作');
  await ui.wait(page, 'menuitem', '删除', { scope: '@menu', enabled: false });
  const items = await page.eval(
    uiJs(`return Object.fromEntries([...document.querySelectorAll('[role=menu] [role=menuitem]')].map((el) => [__ui.text(el), { disabled: el.getAttribute('aria-disabled') === 'true', why: el.querySelector('.menu-desc')?.textContent ?? '' }]));`),
  );
  await escape(page);
  await page.waitFor(`!document.querySelector('[role=menu]')`, '结构操作菜单关闭');
  return items;
}
const structureAction = (page, item) => ui.menu(page, '结构操作', item);

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

  // 1. 新建工程；顶栏页签顺序
  await shell.newProject(page, { audio: wavPath, lyrics: lrcPath });
  await page.waitFor(`document.querySelectorAll('.line-list .ll-text').length === 4`, '工作区显示 4 行', 120_000);
  const nav = await page.eval(`[...document.querySelectorAll('.app-bar .pages .page-label')].map((t) => t.textContent.trim()).join(',')`);
  check(nav === '歌词,编排,外观', `新建工程（4 行），顶栏页签“编排”位于“歌词”和“外观”之间（${nav}）`);

  // 2. 结构树与属性
  await gotoPage(page, 'orchestration', treeReady);
  await page.waitFor(js(`return path() === '主歌词 / 画面 / 常规';`), '默认选中常规');
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
    js(`const editable = (l) => { const f = field(l); return !!f && !__ui.disabled(f); };
      return { path: path(), titles: groupTitles(), kind: seg('内部布局类型'), x: fieldValue('X', '.node-placement'),
        area: ['左', '上', '右', '下'].map((l) => fieldValue(l, '.safe-area')).join('/'), maxWidth: fieldValue('最大宽度'),
        source: value('来源'), anim: selectText('动画'), selected: rows().find((r) => r.selected)?.name,
        editable: ['X', 'Y', '宽', '高', '左', '最大宽度'].every(editable) && ['锚点', '动画'].every((l) => selectEnabled(l)) && segEnabled('内部布局类型', '轮换') };`),
  );
  check(
    props.selected === '常规' &&
      props.path === '主歌词 / 画面 / 常规' &&
      props.titles.join('、') === '节点、位置与变换、参考框、内部布局、来源' &&
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
  const groupOnly = await page.eval(js(`return groupTitles().join('、');`));
  await key(page, 'ArrowDown', 'ArrowDown', 40);
  await page.waitFor(js(`return path() === '主歌词 / 画面 / 常规';`), '↓ 回到常规');
  check(groupOnly === '节点、位置与变换、参考框、来源', `↑↓ 在结构树里移动：组没有“内部布局”一节（${groupOnly}）`);

  // 4. 歌词分配与入口
  const bar = await page.eval(
    js(`return {
      source: assign('来源'),
      container: assign('默认容器'),
      containerDisabled: !selectEnabled('默认容器', '.assign'),
      hint: hint(),
      panels: panels(),
      unassigned: unassigned(),
      canvasHeight: document.querySelector('.timeline-canvas canvas').getBoundingClientRect().height,
      layout: enabled('快速布局…'),
      layoutText: [layoutTag(), ...sectionLayouts()],
      layoutMode: segEnabled('画布模式', '布局参考'),
      empty: document.querySelector('.clip-list .list-empty')?.textContent ?? '',
      add: enabled('新建片段', '.clip-list'),
    };`),
  );
  const [clipPanel, leadInPanel] = bar.panels;
  check(bar.source === tree.sections[0].title && bar.container === '常规 · 自动布局', `歌词分配：来源是主歌词，默认容器是它的常规容器（${bar.source} → ${bar.container}）`);
  check(bar.containerDisabled && bar.hint.includes('只有一个歌词容器'), `只有一个容器时默认容器不能改，并写明原因（${bar.hint}）`);
  check(leadInPanel?.text === '前奏 0' && leadInPanel.enabled && bar.layoutMode && bar.layout, `“快速布局…”“布局参考”模式和“${leadInPanel?.text}”列表可用`);
  check(
    clipPanel?.text === '片段 0' && clipPanel.checked && bar.unassigned === '未单独分配 4 句' && bar.canvasHeight === 22 + 72 + 34 + 22 + 22 + 22,
    `歌词分配显示“${bar.unassigned}”，下面是“${clipPanel?.text}”列表；时间轴多了片段轨和前奏轨（画布高 ${bar.canvasHeight}）`,
  );
  check(bar.empty.includes('还没有片段') && bar.add, `片段列表写明还没有片段（${bar.empty}）；“新建片段”可用`);
  check(bar.layoutText.join('、') === '布局：自动布局、布局：自动布局', `歌词分配和结构树的来源标题都显示当前布局（${bar.layoutText.join('、')}）`);

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
  await ui.click(page, 'button', '全曲');
  await clickTimeline(page, 4.5, 'ruler');
  await page.waitFor(`${playheadText}.startsWith('0:04.5')`, '定位到 4.5 秒');
  await gotoPage(page, 'lyrics', `!!document.querySelector('.line-list')`);
  const kept = await page.eval(playheadText);
  await gotoPage(page, 'orchestration', treeReady);
  const back = await page.eval(playheadText);
  check(kept.startsWith('0:04.5') && back.startsWith('0:04.5'), `切换页面：播放头保持（${kept} → ${back}）`);

  // 7. 快速布局（G18）：预演不改工程；套用一条历史，新容器成为默认，帧变化；再套同一预设没有变化；撤销后恢复
  const layoutStart = Number(await page.eval(js(`return revision();`)));
  const frameBeforeLayout = await hash('.orchestration', String(layoutStart));
  const layoutDialog = '快速布局 · 主歌词';
  const impact = js(`return inDialog('.layout-impact');`);
  const presetCards = js(`return [...(dlg()?.querySelectorAll('.layout-presets .radio') ?? [])].map((r) => ({ name: r.querySelector('.radio-label')?.textContent, text: sq(r.textContent) }));`);
  const scopes = js(
    `return [...(dlg()?.querySelectorAll('.layout-scope .radio') ?? [])].map((r) => ({ text: r.querySelector('.radio-label')?.textContent, checked: !!r.querySelector('input')?.checked, disabled: !!r.querySelector('input')?.disabled }));`,
  );
  await ui.click(page, 'button', '快速布局…');
  await ui.waitDialog(page, layoutDialog);
  const openedCards = (await page.eval(presetCards)).map((c) => c.name);
  const openedScopes = await page.eval(scopes);
  const openedText = await page.eval(js(`return dlg()?.textContent ?? '';`));
  await ui.click(page, 'radio', '双行居中', { scope: '@dialog' });
  await page.waitFor(js(`return inDialog('.layout-impact').startsWith('将影响');`), '预演结果', 10_000);
  const previewed = { impact: await page.eval(impact), revision: await page.eval(js(`return revision();`)) };
  check(
    openedCards.join('、') === '单行居中、卡拉 OK 双行、双行居中、三行居中' && openedScopes[0]?.text === '整个来源（共 4 句）' && openedText.includes('在时间轴上选连续几句'),
    `快速布局对话框：四个预设，范围是整个来源（共 4 句），写明可以在时间轴上选几句（${openedCards.join('、')}）`,
  );
  check(previewed.impact.includes('将影响 4 句') && previewed.impact.includes('2 个槽位') && Number(previewed.revision) === layoutStart, `预演写明影响且不改工程（修订仍是 ${previewed.revision}）：${previewed.impact}`);
  await snapshot(page, 'layout-dialog');
  await ui.clickInDialog(page, '套用到 主歌词');
  await page.waitFor(js(`return Number(revision()) === ${layoutStart + 1} && !dlg() && rows().length === 5;`), '套用双行居中', 15_000);
  await page.waitFor(js(`return rows().find((r) => r.selected)?.name === '双行居中';`), '选中新的默认容器', 10_000);
  const applied = await page.eval(js(`return { rows: rows(), layout: [layoutTag(), ...sectionLayouts()], containerDisabled: !selectEnabled('默认容器', '.assign'), hint: hint(), toast: toast() };`));
  const created = applied.rows.find((r) => r.name === '双行居中');
  check(
    !!created && created.role === '轮换·2' && created.isDefault && created.selected && applied.rows.filter((r) => r.name.startsWith('槽位 ')).length === 2 && !applied.rows.find((r) => r.name === '常规')?.isDefault,
    `套用双行居中：一条历史；结构树多出“双行居中”（轮换·2）和 2 个槽位，成为默认并被选中（${applied.rows.map((r) => r.name).join('、')}）；提示“${applied.toast}”`,
  );
  check(applied.layout.join('、') === '布局：双行居中、布局：双行居中', `歌词分配和来源标题显示“布局：双行居中”（${applied.layout.join('、')}）`);
  check(!applied.containerDisabled && !applied.hint.includes('R6'), `有两个容器后默认容器下拉框可用，不再写 R6（${applied.hint}）`);
  const frameAfterLayout = await hash('.orchestration', String(layoutStart + 1));
  check(frameAfterLayout !== frameBeforeLayout, `成片预览在同一时刻变化（${frameBeforeLayout} → ${frameAfterLayout}）`);
  await snapshot(page, 'layout-applied');

  // 7a. 改默认容器（R6 已修复）：一条历史，帧变化；撤销后恢复
  await ui.choose(page, '默认容器', '常规 · 自动布局', { scope: '.assign' });
  await page.waitFor(js(`return Number(revision()) === ${layoutStart + 2} && rows().find((r) => r.name === '常规')?.isDefault;`), '默认容器改为常规', 10_000);
  const regularFrame = await hash('.orchestration', String(layoutStart + 2));
  const switched = await page.eval(js(`return [layoutTag(), ...sectionLayouts()];`));
  await undo(page);
  await page.waitFor(js(`return Number(revision()) === ${layoutStart + 3} && rows().find((r) => r.name === '双行居中')?.isDefault;`), '撤销改默认容器', 10_000);
  const restoredFrame = await hash('.orchestration', String(layoutStart + 3));
  check(
    regularFrame === frameBeforeLayout && switched[0] === '布局：自动布局' && restoredFrame === frameAfterLayout,
    `改默认容器为“常规”：一条历史，帧回到自动布局的画面，布局文字随之变化（${switched[0]}）；撤销后默认容器回到“双行居中”，帧恢复（R6 已修复）`,
  );

  await ui.click(page, 'button', '快速布局…');
  await ui.waitDialog(page, layoutDialog);
  await ui.click(page, 'radio', '双行居中', { scope: '@dialog' });
  await page.waitFor(js(`return inDialog('.layout-impact').includes('已经是这个布局');`), '同一预设的预演', 10_000);
  const again = await page.eval(
    js(`return { current: sq([...dlg().querySelectorAll('.layout-presets .radio')].find((r) => r.querySelector('.radio-label')?.textContent === '双行居中')?.textContent).includes('当前'), submit: !enabled('套用到 主歌词', '@dialog'), revision: revision() };`),
  );
  check(again.current && again.submit && Number(again.revision) === layoutStart + 3, '再套同一预设：卡片标“当前”，写明不会变化，套用按钮不可用，修订不变');
  await ui.clickInDialog(page, '取消');
  await ui.waitDialogClosed(page, layoutDialog);

  await undo(page);
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
  await ui.click(page, 'button', '全曲');
  await clickTimeline(page, 4.5, 'lines');
  await clickTimeline(page, 7.5, 'lines', 8);
  await page.waitFor(js(`return rangeTag().includes('已选第 2～3 句');`), '选中第 2～3 句');
  await ui.click(page, 'button', '快速布局…');
  await ui.waitDialog(page, layoutDialog);
  await page.waitFor(js(`return dlg()?.querySelectorAll('.layout-scope .radio').length === 2;`), '范围快速布局对话框');
  const rangeScopes = await page.eval(scopes);
  await ui.click(page, 'radio', '单行居中', { scope: '@dialog' });
  await page.waitFor(js(`return inDialog('.layout-impact').startsWith('将影响第');`), '范围预演', 10_000);
  const rangePreview = await page.eval(js(`return { impact: inDialog('.layout-impact'), revision: revision(), submit: enabled('套用到第 2～3 句', '@dialog') };`));
  check(
    rangeScopes[1]?.text === '选中的第 2～3 句（2 句）' && rangeScopes[1].checked && !rangeScopes[0].checked && rangePreview.submit,
    `选中几句后打开快速布局：范围默认是“${rangeScopes[1]?.text}”，套用按钮写“套用到第 2～3 句”`,
  );
  check(
    rangePreview.impact.includes('将影响第 2～3 句（2 句）') && rangePreview.impact.includes('默认容器不变') && Number(rangePreview.revision) === rangeStart,
    `范围预演不改工程（修订仍是 ${rangePreview.revision}）：${rangePreview.impact}`,
  );
  await snapshot(page, 'range-dialog');
  await ui.clickInDialog(page, '套用到第 2～3 句');
  await page.waitFor(js(`return Number(revision()) === ${rangeStart + 1} && !dlg() && rows().some((r) => r.name === '单行居中');`), '套用到第 2～3 句', 15_000);
  await page.waitFor(js(`return path() === '主歌词 / 片段 / 歌词段布局' && clipRows().length > 0;`), '选中新片段', 10_000);
  const ranged = await page.eval(
    js(`return {
      rows: rows(), clips: clipRows(), panel: panels()[0]?.text, unassigned: unassigned(), layout: layoutTag(),
      titles: groupTitles(), range: value('范围'), container: selectText('目标容器') ?? '',
      name: fieldValue('名称'), remove: enabled('删除片段', '.orch-insp'), pending: (insp()?.textContent ?? '').includes('G19-B'),
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
    !!selectedClip && selectedClip.text === '歌词段布局 第 2～3 句 → 单行居中' && keptClips.length > 0 && keptClips.every((c) => c.name === '保留原布局的歌词段' && c.target === '→ 常规'),
    `片段列表：选中“${selectedClip?.text}”，另有 ${keptClips.length} 个留在常规容器、标“保留原调度”的片段（${ranged.clips.map((c) => c.text).join('；')}）`,
  );
  check(ranged.panel === `片段 ${ranged.clips.length}` && ranged.unassigned === '未单独分配 0 句', `歌词分配：“${ranged.panel}”，“${ranged.unassigned}”`);
  check(
    ranged.titles.join('、') === '片段、句子、目标容器' && ranged.range === '第 2～3 句（2 句）' && ranged.container.startsWith('单行居中') && ranged.name === '歌词段布局' && ranged.remove && !ranged.pending,
    `属性区显示片段：名称“${ranged.name}”，${ranged.titles.join('、')}，范围 ${ranged.range}，目标 ${ranged.container}；改名、改目标容器、删除可用（G19-B）`,
  );
  const frameAfterRange = await hash('.orchestration', String(rangeStart + 1));
  check(frameAfterRange !== frameBeforeLayout, `第 2 句的时刻成片预览变化（${frameBeforeLayout} → ${frameAfterRange}）`);
  await snapshot(page, 'range-applied');

  // 联动：点片段列表里留在常规的片段 → 属性区显示调度上下文，结构树选中常规；点片段轨上的“歌词段布局” → 选中它
  await clickClip(page, keptClips.at(-1).id);
  await page.waitFor(js(`return path() === '主歌词 / 片段 / 保留原布局的歌词段';`), '选中保留的片段');
  const keptView = await page.eval(js(`return { titles: groupTitles(), tree: rows().find((r) => r.selected)?.name, tag: rangeTag(), original: value('原段'), isDefault: value('默认段') };`));
  await clickTimeline(page, 4.5, 'clips');
  await page.waitFor(js(`return path() === '主歌词 / 片段 / 歌词段布局' && clipRows().find((c) => c.selected)?.id === ${JSON.stringify(selectedClip?.id)};`), '点片段轨选中片段');
  const bandView = await page.eval(js(`return { tree: rows().find((r) => r.selected)?.name, tag: rangeTag() };`));
  check(
    keptView.titles.includes('调度上下文') && keptView.tree === '常规' && keptView.original === '第 1～4 句' && keptView.isDefault?.startsWith('是') && bandView.tree === '单行居中' && bandView.tag.includes('已选第 2～3 句'),
    `列表与片段轨联动：选中保留的片段时结构树选中常规、显示调度上下文（原段 ${keptView.original}，${keptView.tag}）；点片段轨选中“歌词段布局”，结构树选中单行居中（${bandView.tag}）`,
  );

  // 再套一次：后端是 no-op，修订不变；撤销后片段和容器都消失
  await ui.click(page, 'button', '快速布局…');
  await ui.waitDialog(page, layoutDialog);
  await page.waitFor(js(`return !!dlg()?.querySelectorAll('.layout-scope .radio input')[1]?.checked;`), '再次范围快速布局');
  await ui.click(page, 'radio', '单行居中', { scope: '@dialog' });
  await page.waitFor(js(`return inDialog('.layout-impact').startsWith('将影响第');`), '再次范围预演', 10_000);
  await ui.clickInDialog(page, '套用到第 2～3 句');
  await ui.waitDialogClosed(page, layoutDialog, { timeout: 10_000 });
  await page.waitFor(js(`return snack('已经是这个布局，没有变化');`), '提示没有变化', 10_000);
  const rangeAgain = await page.eval(js(`return revision();`));
  check(Number(rangeAgain) === rangeStart + 1, '同一范围再套同一预设：提示“已经是这个布局，没有变化”，修订不变');

  // 7c. 片段编辑（G19-B）：在范围套用后的片段上改名、改目标容器、删除，再新建
  let rev = rangeStart + 1;
  const atRevision = (body) => js(`return Number(revision()) === ${rev} && (${body});`);
  const clipId = selectedClip?.id;
  await clickClip(page, clipId);
  await page.waitFor(js(`return path() === '主歌词 / 片段 / 歌词段布局';`), '选中“歌词段布局”');
  await commit(page, '名称', '副歌');
  rev++;
  await page.waitFor(atRevision(`clipRows().find((c) => c.id === '${clipId}')?.text === '副歌 第 2～3 句 → 单行居中'`), '改名为副歌', 10_000);
  const renamed = await page.eval(js(`return { path: path(), snack: snack('片段已改名为“副歌”') };`));
  await commit(page, '名称', '');
  rev++;
  await page.waitFor(atRevision(`clipRows().find((c) => c.id === '${clipId}')?.text.startsWith('片段 2 第 2～3 句')`), '清除名称', 10_000);
  const cleared = await page.eval(js(`const f = field('名称'); return { name: f?.value, placeholder: f?.placeholder, snack: snack('已清除片段名称') };`));
  await undo(page);
  rev++;
  await page.waitFor(atRevision(`clipRows().find((c) => c.id === '${clipId}')?.text.startsWith('副歌') && fieldValue('名称') === '副歌'`), '撤销清除名称', 10_000);
  check(
    renamed.path === '主歌词 / 片段 / 副歌' && renamed.snack && cleared.name === '' && cleared.placeholder === '片段 2' && cleared.snack,
    `改名：属性区输入“副歌”按 Enter 是一条历史，列表和路径跟着变；清空名称后列表显示“片段 2”；Ctrl+Z 恢复“副歌”`,
  );

  await ui.choose(page, '目标容器', '常规 · 自动布局（默认容器）', { scope: '.orch-insp' });
  rev++;
  await page.waitFor(atRevision(`clipRows().find((c) => c.id === '${clipId}')?.text === '副歌 第 2～3 句 → 常规'`), '改目标容器为常规', 10_000);
  const moved = await page.eval(js(`return { tree: rows().find((r) => r.selected)?.name, snack: snack('片段“副歌”已改到“常规”') };`));
  const frameMoved = await hash('.orchestration', String(rev));
  await undo(page);
  rev++;
  await page.waitFor(atRevision(`clipRows().find((c) => c.id === '${clipId}')?.text === '副歌 第 2～3 句 → 单行居中'`), '撤销改目标容器', 10_000);
  const frameMoveUndone = await hash('.orchestration', String(rev));
  check(
    moved.tree === '常规' && moved.snack && frameMoved !== frameAfterRange && frameMoveUndone === frameAfterRange,
    `改目标容器为“常规”：一条历史，结构树跟着选中常规，第 2 句时刻的帧变化；Ctrl+Z 后回到单行居中，帧恢复`,
  );

  await clickClip(page, clipId);
  await page.waitFor(js(`return path() === '主歌词 / 片段 / 副歌';`), '再次选中副歌');
  await ui.click(page, 'button', '删除片段', { scope: '.orch-insp' });
  rev++;
  await page.waitFor(atRevision(`!clipRows().some((c) => c.id === '${clipId}')`), '删除片段', 10_000);
  const deleted = await page.eval(
    js(`return { unassigned: unassigned(), path: path(), tag: rangeTag(), snack: snack('已删除片段“副歌”，第 2～3 句回到默认容器'), newClip: enabled('为这几句新建片段', '.assign-range') };`),
  );
  const frameDeleted = await hash('.orchestration', String(rev));
  check(
    deleted.unassigned === '未单独分配 2 句' && deleted.path === '主歌词 / 画面 / 常规' && deleted.tag.includes('已选第 2～3 句') && deleted.snack && deleted.newClip && frameDeleted !== frameAfterRange,
    `删除片段：一条历史，第 2～3 句回到默认容器（${deleted.unassigned}），结构树选中常规，选中的句子保留，“为这几句新建片段”可用；帧变化`,
  );

  // 已在片段里的句子不能新建：写明原因，按钮不可用
  await clickTimeline(page, 10.5, 'lines');
  await page.waitFor(js(`return rangeTag().includes('已选第 4 句');`), '选中第 4 句');
  await ui.click(page, 'button', '为这几句新建片段', { scope: '.assign-range' });
  await ui.waitDialog(page, '新建片段 · 主歌词');
  const blocked = await page.eval(js(`return { block: inDialog('.new-clip-block'), submit: !enabled('创建片段', '@dialog') };`));
  check(blocked.block.includes('第 4 句已在片段“保留原布局的歌词段”里') && blocked.submit, `已在片段里的句子不能新建：对话框写明“${blocked.block}”，“创建片段”不可用`);
  await ui.clickInDialog(page, '取消');
  await ui.waitDialogClosed(page, '新建片段 · 主歌词');

  // 选第 2～3 句新建片段：目标默认是另一个容器（单行居中）
  await clickTimeline(page, 4.5, 'lines');
  await clickTimeline(page, 7.5, 'lines', 8);
  await page.waitFor(js(`return rangeTag().includes('已选第 2～3 句');`), '再次选中第 2～3 句');
  await ui.click(page, 'button', '为这几句新建片段', { scope: '.assign-range' });
  await ui.waitDialog(page, '新建片段 · 主歌词');
  const form = await page.eval(
    js(`return { first: fieldValue('起句', '@dialog'), last: fieldValue('止句', '@dialog'), target: selectText('目标容器', '@dialog'), block: inDialog('.new-clip-block'), lines: inDialog('.clip-range-lines') };`),
  );
  await ui.fill(page, '名称', '副歌', { scope: '@dialog' });
  await snapshot(page, 'clip-dialog');
  await ui.clickInDialog(page, '创建片段');
  rev++;
  await page.waitFor(atRevision(`!dlg() && path() === '主歌词 / 片段 / 副歌'`), '创建片段', 10_000);
  const newClip = await page.eval(js(`return { row: clipRows().find((c) => c.selected)?.text, tree: rows().find((r) => r.selected)?.name, unassigned: unassigned(), snack: snack('已为第 2～3 句新建片段“副歌”') };`));
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
    await undo(page);
    rev++;
    await page.waitFor(js(`return Number(revision()) === ${rev};`), `撤销第 ${i + 1} 次`, 10_000);
  }
  await page.waitFor(js(`return clipRows().find((c) => c.id === '${clipId}')?.text === '歌词段布局 第 2～3 句 → 单行居中';`), '回到范围套用后', 10_000);
  const frameBack = await hash('.orchestration', String(rev));
  check(frameBack === frameAfterRange, 'Ctrl+Z 三次：新建的片段消失，删除的片段回来，名称回到“歌词段布局”，帧与范围套用后相同');

  await undo(page);
  rev++;
  await page.waitFor(js(`return Number(revision()) === ${rev} && rows().length === 2 && clipRows().length === 0;`), '撤销范围快速布局', 10_000);
  const rangeUndone = await page.eval(js(`return { unassigned: unassigned(), panel: panels()[0]?.text };`));
  const frameAfterRangeUndo = await hash('.orchestration', String(rev));
  check(
    rangeUndone.unassigned === '未单独分配 4 句' && rangeUndone.panel === '片段 0' && frameAfterRangeUndo === frameBeforeLayout,
    `Ctrl+Z 撤销范围套用：片段和容器都消失（${rangeUndone.panel}，${rangeUndone.unassigned}），帧与套用前相同`,
  );

  // 7d. 结构编辑（G20）：新建轮换容器（3 个槽位）→改名→槽位增、排序、删→复制→新建组并把副本移进去（帧不变）→上移
  //     →默认容器、根、槽位不能做的操作写明原因→删除组→连续撤销回到原样
  const structureStart = rev;
  const frameBeforeStructure = await hash('.orchestration', String(rev));
  await clickRow(page, '常规');
  await page.waitFor(js(`return path() === '主歌词 / 画面 / 常规';`), '选中常规');
  await ui.click(page, 'button', '新建…');
  await ui.waitDialog(page, '新建');
  const prefill = await page.eval(js(`return { parent: selectText('放在', '@dialog'), kind: selectText('种类', '@dialog'), name: fieldValue('名称', '@dialog') };`));
  await ui.choose(page, '种类', '轮换容器', { scope: '@dialog' });
  await page.waitFor(js(`return !!field('槽位数', '@dialog') && fieldValue('名称', '@dialog') === '轮换';`), '换成轮换容器');
  await ui.fill(page, '名称', '副歌轮换', { scope: '@dialog' });
  await ui.fill(page, '槽位数', '3', { scope: '@dialog' });
  await ui.clickInDialog(page, '新建');
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

  await commit(page, '名称', '副歌');
  rev++;
  await page.waitFor(atRevision(`path() === '主歌词 / 画面 / 副歌'`), '改名为副歌', 10_000);
  check(await page.eval(js(`return rows().some((r) => r.name === '副歌' && r.selected) && snack('已改名为“副歌”');`)), '改名：属性区输入“副歌”按 Enter 是一条历史，结构树与路径跟着变');

  await ui.click(page, 'button', '加槽位', { scope: '.orch-insp' });
  rev++;
  await page.waitFor(atRevision(`slotRows().length === 4 && rows().find((r) => r.name === '副歌')?.role === '轮换·4'`), '加槽位', 10_000);
  const added4 = await page.eval(js(`return { ids: slotRows().map((r) => r.id), snack: snack('已加槽位') };`));
  await ui.click(page, 'button', '下移槽位 1', { scope: '.orch-insp' });
  rev++;
  await page.waitFor(atRevision(`slotRows()[0]?.id === '${added4.ids[1]}'`), '下移第一个槽位', 10_000);
  const reordered = await page.eval(js(`return { ids: slotRows().map((r) => r.id), names: slotRows().map((r) => r.name), first: toolTitle('上移槽位 1') };`));
  await ui.click(page, 'button', '删除槽位 4', { scope: '.orch-insp' });
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

  await ui.click(page, 'button', '复制', { scope: '.orch-left .pane-head' });
  rev++;
  await page.waitFor(atRevision(`rows().find((r) => r.selected)?.name === '副歌 副本'`), '复制并选中副本', 10_000);
  const copied = await page.eval(js(`return { role: rows().find((r) => r.selected)?.role, ids: slotRows().map((r) => r.id), snack: snack('已复制“副歌”') };`));
  check(
    copied.role === '轮换·3' && copied.ids.length === 3 && copied.ids.every((id) => !removedSlot.ids.includes(id)) && copied.snack,
    `复制：一条历史，得到“副歌 副本”（${copied.role}），槽位是新的，并选中副本`,
  );

  const frameBeforeMove = await hash('.orchestration', String(rev));
  await ui.click(page, 'button', '新建…');
  await ui.waitDialog(page, '新建');
  await page.waitFor(js(`return fieldValue('名称', '@dialog') === '新组';`), '新建对话框打开（组）');
  await ui.fill(page, '名称', '角落', { scope: '@dialog' });
  await ui.clickInDialog(page, '新建');
  rev++;
  await page.waitFor(atRevision(`rows().find((r) => r.selected)?.name === '角落'`), '新建组并选中', 10_000);
  await ui.waitDialogClosed(page, '新建');
  await clickRow(page, '副歌 副本');
  await page.waitFor(js(`return path() === '主歌词 / 画面 / 副歌 副本';`), '选中副本');
  await structureAction(page, '移到…');
  await ui.waitDialog(page, '移动“副歌 副本”');
  const moveOptions = (await selectOptions(page, '移到', '@dialog')).map((o) => o.text);
  await ui.clickInDialog(page, '移动');
  rev++;
  await page.waitFor(atRevision(`rows().find((r) => r.name === '副歌 副本')?.level === 3`), '移进角落', 10_000);
  const frameAfterMove = await hash('.orchestration', String(rev));
  const movedNode = await page.eval(js(`return { path: path(), snack: snack('已把“副歌 副本”移到“角落”') };`));
  check(
    moveOptions.join('、') === '主歌词 / 画面 / 角落、画布根层（不属于任何来源）' && movedNode.path === '主歌词 / 画面 / 角落 / 副歌 副本' && movedNode.snack && frameAfterMove === frameBeforeMove,
    `新建组“角落”，把副本移进去：可选 ${moveOptions.join('、')}；一条历史，路径“${movedNode.path}”，成片预览帧不变`,
  );
  await snapshot(page, 'structure-moved');

  await clickRow(page, '副歌');
  await page.waitFor(js(`return path() === '主歌词 / 画面 / 副歌';`), '选中副歌');
  const orderBefore = await page.eval(js(`return level2();`));
  await structureAction(page, '上移');
  rev++;
  await page.waitFor(atRevision(`level2()[0] === '副歌'`), '上移副歌', 10_000);
  const orderAfter = await page.eval(js(`return { order: level2(), snack: snack('已上移“副歌”') };`));
  const upMenu = await structureMenu(page);
  check(
    orderBefore.join('、') === '常规、副歌、角落' && orderAfter.order.join('、') === '副歌、常规、角落' && orderAfter.snack && upMenu['上移']?.disabled && upMenu['上移'].why.includes('已经在最前'),
    `上移：一条历史，画面下的顺序 ${orderBefore.join('、')} → ${orderAfter.order.join('、')}；到最前后菜单里的“上移”不可用并写明原因（${upMenu['上移']?.why}）`,
  );

  await clickRow(page, '常规');
  await page.waitFor(js(`return path() === '主歌词 / 画面 / 常规';`), '选中常规');
  const defaultMenu = await structureMenu(page);
  await blur(page);
  await key(page, 'Delete', 'Delete', 46);
  await page.waitFor(js(`return status().startsWith('不能删除');`), '状态栏写明不能删除');
  const deleteKey = await page.eval(js(`return { status: status(), revision: Number(revision()) };`));
  await clickRow(page, '画面');
  await page.waitFor(js(`return path() === '主歌词 / 画面';`), '选中画面');
  const rootMenu = await structureMenu(page);
  const rootCopy = await page.eval(js(`return enabled('复制', '.orch-left .pane-head');`));
  await clickRow(page, '副歌');
  await page.waitFor(js(`return path() === '主歌词 / 画面 / 副歌' && slotRows().length === 3;`), '选中副歌');
  await ui.click(page, '.orch-insp .slot-name', '槽位 1');
  await page.waitFor(js(`return path() === '主歌词 / 画面 / 副歌 / 槽位 1';`), '选中槽位 1');
  const slotMenu = await structureMenu(page);
  check(
    defaultMenu['删除']?.disabled && defaultMenu['删除'].why.includes('默认容器') && deleteKey.status.includes('默认容器') && deleteKey.revision === rev,
    `默认容器不能删：菜单里的“删除”不可用（${defaultMenu['删除']?.why}）；按 Delete 只在状态栏写明原因，不发命令`,
  );
  check(
    !rootCopy &&
      rootMenu['复制']?.why.includes('来源的根不能复制') &&
      rootMenu['移到…']?.why.includes('来源的根固定在画布根层') &&
      rootMenu['删除']?.why.includes('来源的根不能删除') &&
      slotMenu['移到…']?.disabled &&
      slotMenu['移到…'].why.includes('槽位只能在它的轮换里调整顺序'),
    `来源的根不能复制（复制按钮也不可用）、移动、删除；槽位不能移出轮换，各自写明原因（${slotMenu['移到…']?.why}）`,
  );

  await clickRow(page, '角落');
  await page.waitFor(js(`return path() === '主歌词 / 画面 / 角落';`), '选中角落');
  await structureAction(page, '删除');
  rev++;
  await page.waitFor(atRevision(`!rows().some((r) => r.name === '角落' || r.name === '副歌 副本')`), '删除角落', 10_000);
  const removedGroup = await page.eval(js(`return { path: path(), snack: snack('已删除“角落”及其中 4 个节点') };`));
  check(removedGroup.path === '主歌词 / 画面' && removedGroup.snack, `删除组：一条历史，连同其中的副本和槽位（4 个节点）一起删除，选中原来的父级“画面”`);

  const steps = rev - structureStart;
  for (let i = 0; i < steps; i++) {
    await undo(page);
    rev++;
    await page.waitFor(js(`return Number(revision()) === ${rev};`), `结构编辑撤销第 ${i + 1} 次`, 10_000);
  }
  await page.waitFor(js(`return rows().length === 2;`), '结构回到画面和常规', 10_000);
  const frameAfterStructure = await hash('.orchestration', String(rev));
  await clickRow(page, '常规');
  check(frameAfterStructure === frameBeforeStructure, `Ctrl+Z ${steps} 次：结构回到“画面 → 常规”，成片预览帧与开始时相同`);

  // 7e. 画布（G22）与属性编辑（G21）
  //     叠加层→点句子选中默认容器→点空白不改选择→拖动默认容器（一条历史、X/Y 变化、帧变化）→撤销→在铺满画布的框里空白处拖动不会动
  //     →改 Y 和参考框的宽→安全区不合法写明原因→改成轮换（3 个槽位）→同时最多显示 1、出现时机→改回自动布局（槽位留作子节点）
  //     →再改成轮换时预填并复用→布局参考：每个槽位一句代表文字，点文字选中槽位→动画策略改成静态再改回→连续撤销回到原样
  await clickRow(page, '画面');
  await page.waitFor(js(`return path() === '主歌词 / 画面';`), '选中画面');
  await clickTimeline(page, 1.5, 'ruler');
  await page.waitFor(`${playheadText}.startsWith('0:01.5')`, '定位到 1.5 秒');
  // 预览换到新时刻之前，标题里还是旧时刻的帧。
  await page.waitFor(js(`return previewTitle().includes('· 0:01.500');`), '预览换到 1.5 秒', 30_000);
  const canvasFrame = await hash('.orchestration', String(rev));
  await page.waitFor(js(`return (overlay()?.inks ?? 0) > 0;`), '叠加层出现', 30_000);
  const drawn = await page.eval(js(`return overlay();`));
  check(drawn.frames.length >= 2 && drawn.inks >= 1 && drawn.selected, `叠加层（暂停时）：容器的参考框 ${drawn.frames.length} 个、当前句子的墨迹框 ${drawn.inks} 个，选中的“画面”高亮`);

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
  const dragged = await page.eval(js(`return { x: Number(fieldValue('X', '.node-placement')), y: Number(fieldValue('Y', '.node-placement')) };`));
  const expectX = 50 + ((60 * 1920) / box.w / 1920) * 100;
  const expectY = 50 - ((40 * 1080) / box.h / 1080) * 100;
  const draggedFrame = await hash('.orchestration', String(rev));
  check(
    Math.abs(dragged.x - expectX) < 0.5 && Math.abs(dragged.y - expectY) < 0.5 && draggedFrame !== canvasFrame,
    `拖动“常规”：一条历史，X、Y 按位移变化（${dragged.x}%、${dragged.y}%，预期约 ${expectX.toFixed(2)}%、${expectY.toFixed(2)}%），帧变化`,
  );
  await undo(page);
  rev++;
  await page.waitFor(atRevision(`fieldValue('X', '.node-placement') === '50' && fieldValue('Y', '.node-placement') === '50'`), '撤销拖动', 10_000);
  const undoneFrame = await hash('.orchestration', String(rev));
  await page.waitFor(js(`return (overlay()?.inks ?? 0) > 0;`), '叠加层回来', 30_000);
  // 常规的框铺满画布：在空白处按下拖动不会挪走它
  await canvasDrag(page, { x: box.x + 20, y: box.y + 20 }, 60, 40);
  await sleep(800);
  const notMoved = await page.eval(js(`return { revision: Number(revision()), x: fieldValue('X', '.node-placement') };`));
  check(undoneFrame === canvasFrame && notMoved.revision === rev && notMoved.x === '50', 'Ctrl+Z 撤销拖动：回到原位，帧与拖动前相同；铺满画布的“常规”在空白处按下拖动不会被移动');

  // 播放中隐藏叠加层并写明原因；暂停后回到 1.5 秒，叠加层回来
  await ui.click(page, 'button', '播放');
  await page.waitFor(js(`return !document.querySelector('.orchestration .canvas-overlay') && (document.querySelector('.canvas-status')?.textContent ?? '').includes('播放中不显示框');`), '播放中隐藏叠加层', 10_000);
  await ui.click(page, 'button', '暂停');
  await clickTimeline(page, 1.5, 'ruler');
  await page.waitFor(js(`return previewTitle().includes('· 0:01.500') && (overlay()?.inks ?? 0) > 0;`), '暂停后叠加层回来', 30_000);
  check(true, '播放中叠加层隐藏，画布下方写明“播放中不显示框”；暂停后叠加层回来');

  // 属性：Y 与参考框的宽
  const propertyStart = rev;
  await commit(page, 'Y', '30', '.node-placement');
  rev++;
  await page.waitFor(atRevision(`fieldValue('Y', '.node-placement') === '30' && snack('已调整“常规”的位置')`), '改 Y', 10_000);
  const yFrame = await hash('.orchestration', String(rev));
  await commit(page, '宽', '80');
  rev++;
  await page.waitFor(atRevision(`fieldValue('宽') === '80' && snack('已调整“常规”的参考框')`), '改参考框的宽', 10_000);
  check(yFrame !== canvasFrame, '改 Y（30%）、参考框的宽（80%）：各一条历史，提示可撤销，帧跟着变');

  await ui.fill(page, '右', '0', { scope: '.safe-area', enter: true });
  await sleep(300);
  const badArea = await page.eval(js(`const f = field('右', '.safe-area'); return { invalid: f.getAttribute('aria-invalid') === 'true', message: f.closest('.tf')?.querySelector('.err')?.textContent ?? '', revision: Number(revision()) };`));
  await page.eval(js(`__ui.key(field('右', '.safe-area'), 'Escape');`));
  await page.waitFor(js(`return fieldValue('右', '.safe-area') === '100';`), 'Esc 恢复安全区右边');
  await blur(page);
  check(badArea.invalid && badArea.message.includes('右边要大于左边') && badArea.revision === rev, `安全区不合法时写明原因（${badArea.message}），修订不变；Esc 恢复`);

  // 最大宽度；默认位置设置（用当前位置作初值）→改 X→清除
  await commit(page, '最大宽度', '80');
  rev++;
  await page.waitFor(atRevision(`fieldValue('最大宽度') === '80' && snack('已调整最大宽度')`), '改最大宽度', 10_000);
  await ui.click(page, 'button', '设置默认位置', { scope: '.orch-insp' });
  rev++;
  await page.waitFor(atRevision(`!!field('X', '.default-placement') && snack('已设置默认位置')`), '设置默认位置', 10_000);
  const defaultSet = await page.eval(js(`return fieldValue('Y', '.default-placement');`));
  await commit(page, 'X', '30', '.default-placement');
  rev++;
  await page.waitFor(atRevision(`fieldValue('X', '.default-placement') === '30' && snack('已调整默认位置')`), '改默认位置', 10_000);
  await ui.click(page, 'button', '清除默认位置', { scope: '.orch-insp' });
  rev++;
  await page.waitFor(atRevision(`!field('X', '.default-placement') && !!insp().querySelector('.default-placement-unset')`), '清除默认位置', 10_000);
  check(defaultSet === '30', `最大宽度改成 80%；默认位置“设置”用当前位置作初值（Y ${defaultSet}%），改 X、清除，各一条历史`);

  // 动画策略：默认容器是自动布局、没有片段和前奏时能改成静态；静态时默认容器不能改成轮换并写明原因；再改回按来源角色
  await ui.choose(page, '动画', '静态文本', { scope: '.orch-insp' });
  rev++;
  await page.waitFor(atRevision(`selectText('动画') === '静态文本' && snack('动画已改为“静态文本”')`), '动画改成静态', 10_000);
  const staticKind = await page.eval(js(`return { disabled: !segEnabled('内部布局类型', '轮换'), hint: insp().querySelector('.kind-block')?.textContent ?? '' };`));
  await ui.choose(page, '动画', '按来源角色', { scope: '.orch-insp' });
  rev++;
  await page.waitFor(atRevision(`selectText('动画') === '按来源角色' && !insp().querySelector('.kind-block')`), '动画改回按来源角色', 10_000);
  check(
    staticKind.disabled && staticKind.hint.includes('不能改成轮换'),
    `动画策略：能改成“静态文本”，再改回“按来源角色”，各一条历史；静态时默认容器的“轮换”不能选，写明原因（${staticKind.hint}）`,
  );

  // 布局类型：改成轮换（3 个槽位）
  const slotDialog = '把“常规”改成轮换';
  await ui.click(page, 'button', '轮换', { scope: '[aria-label="内部布局类型"]' });
  await ui.waitDialog(page, slotDialog);
  const firstPrefill = await page.eval(js(`const f = field('槽位数', '@dialog'); return { value: f.value, helper: help(f) };`));
  await ui.fill(page, '槽位数', '3', { scope: '@dialog' });
  await ui.clickInDialog(page, '改成轮换');
  rev++;
  await page.waitFor(atRevision(`slotRows().length === 3 && groupTitles().includes('调度') && groupTitles().includes('动画')`), '改成轮换', 10_000);
  const slotIds = await page.eval(js(`return slotRows().map((r) => r.id);`));
  const toSlots = await page.eval(js(`return { role: rows().find((r) => r.name === '常规')?.role, snack: snack('已改成轮换（3 个槽位）') };`));
  check(
    firstPrefill.value === '2' && firstPrefill.helper.includes('后端新建') && toSlots.role === '轮换·3' && toSlots.snack,
    `改成轮换：对话框预填 2（${firstPrefill.helper}），填 3 后一条历史，“常规”变成轮换·3，出现槽位列表和“调度”“动画”两组`,
  );

  // 槽位动画：同时最多显示 1；出现时机改成“前一句开始后”，只显示延迟
  await commit(page, '同时最多显示', '1');
  rev++;
  await page.waitFor(atRevision(`fieldValue('同时最多显示') === '1' && snack('已调整槽位动画')`), '同时最多显示 1', 10_000);
  await ui.choose(page, '出现', '前一句开始后', { scope: '.orch-insp' });
  rev++;
  await page.waitFor(atRevision(`!!field('延迟')`), '改出现时机', 10_000);
  const reveal = await page.eval(js(`return { progress: !!field('进度'), lead: !!field('提前'), max: fieldValue('同时最多显示') };`));
  await commit(page, '进入', '0.5', '.slot-animation');
  rev++;
  await page.waitFor(atRevision(`fieldValue('进入', '.slot-animation') === '0.5'`), '改进入时长', 10_000);
  await page.eval(`document.querySelector('.orch-insp .slot-poses').open = true`);
  await commit(page, '透明度', '20', '.pose-enterFrom');
  rev++;
  await page.waitFor(atRevision(`fieldValue('透明度', '.pose-enterFrom') === '20'`), '改进入起点的透明度', 10_000);
  check(
    !reveal.progress && !reveal.lead && reveal.max === '1',
    '槽位动画：同时最多显示改成 1、出现时机改成“前一句开始后”（只显示用到的“延迟”一项）、进入时长 0.5 秒、进入起点透明度 20%，各一条历史',
  );

  // 改回自动布局：槽位留作普通子节点；再改成轮换时预填并复用
  await ui.click(page, 'button', '自动布局', { scope: '[aria-label="内部布局类型"]' });
  rev++;
  await page.waitFor(atRevision(`seg('内部布局类型') === '自动布局' && snack('已改成自动布局：3 个槽位留作普通子节点')`), '改回自动布局', 10_000);
  const leftover = await page.eval(js(`return rows().filter((r) => r.level === 3).map((r) => r.role);`));
  await ui.click(page, 'button', '轮换', { scope: '[aria-label="内部布局类型"]' });
  await ui.waitDialog(page, slotDialog);
  const reusePrefill = await page.eval(js(`const f = field('槽位数', '@dialog'); return { value: f.value, helper: help(f) };`));
  await ui.clickInDialog(page, '改成轮换');
  rev++;
  await page.waitFor(atRevision(`slotRows().length === 3`), '再次改成轮换', 10_000);
  const reused = await page.eval(js(`return slotRows().map((r) => r.id);`));
  check(
    leftover.length === 3 && leftover.every((r) => r === '组') && reusePrefill.value === '3' && reusePrefill.helper.includes('现有的前 3 个子节点') && reused.join() === slotIds.join(),
    `改回自动布局：3 个槽位留作普通子节点（${leftover.join('、')}）；再改成轮换时预填 3、写明复用（${reusePrefill.helper}），槽位还是原来的节点`,
  );
  const policyOptions = await selectOptions(page, '动画', '.orch-insp');
  const policyHint = await page.eval(js(`return insp().querySelector('.policy-block')?.textContent ?? '';`));
  const policyDisabled = policyOptions.filter((o) => o.disabled).map((o) => o.text);
  check(
    policyDisabled.join('、') === '静态文本、跟随主歌词' && policyHint.includes('默认容器是轮换'),
    `默认容器是轮换时，动画的“静态文本”“跟随主歌词”不能选，写明原因（${policyHint}）`,
  );

  // 布局参考：每个槽位一句代表文字；点文字选中所在的槽位
  await ui.click(page, 'button', '布局参考', { scope: '.canvasbar' });
  await page.waitFor(js(`return previewTitle().includes('布局参考 · 修订 ${rev}') && !!document.querySelector('.orchestration .preview-frame img') && (overlay()?.inks ?? 0) >= 3;`), '布局参考图', 30_000);
  const layoutView = await page.eval(js(`return overlay();`));
  await canvasClick(page, await page.eval(js(`return inkCenter(1);`)));
  await page.waitFor(js(`return (path() ?? '').startsWith('主歌词 / 画面 / 常规 / 槽位');`), '布局参考里点文字选中槽位', 10_000);
  const slotPath = await page.eval(js(`return path();`));
  check(
    slotIds.every((id) => layoutView.frames.includes(id)) && layoutView.inks >= 3,
    `布局参考：3 个槽位都有参考框，每个一句代表文字（墨迹框 ${layoutView.inks} 个）；点文字选中它所在的槽位（${slotPath}）`,
  );
  // 布局参考里拖动选中的槽位：一条历史，X 变化
  const slotX = await page.eval(js(`return fieldValue('X', '.node-placement');`));
  await page.waitFor(js(`return overlay()?.selected === true;`), '布局参考里选中槽位的框');
  await canvasDrag(page, await page.eval(js(`return inkCenter(1);`)), 40, 0);
  rev++;
  await page.waitFor(atRevision(`fieldValue('X', '.node-placement') !== ${JSON.stringify(slotX)} && snack('已移动“')`), '布局参考里拖动槽位', 10_000);
  await page.waitFor(js(`return previewTitle().includes('布局参考 · 修订 ${rev}');`), '布局参考按新修订重测', 30_000);
  const slotMoved = await page.eval(js(`return fieldValue('X', '.node-placement');`));
  check(Number(slotMoved) > Number(slotX), `布局参考里拖动槽位：一条历史，X ${slotX}% → ${slotMoved}%，布局参考图按新修订重测`);
  await ui.click(page, 'button', '时间预览', { scope: '.canvasbar' });
  await page.waitFor(js(`return previewTitle().startsWith('成片');`), '回到时间预览');

  const propertySteps = rev - propertyStart;
  for (let i = 0; i < propertySteps; i++) {
    await undo(page);
    rev++;
    await page.waitFor(js(`return Number(revision()) === ${rev};`), `属性撤销第 ${i + 1} 次`, 10_000);
  }
  await clickRow(page, '常规');
  await page.waitFor(js(`return rows().length === 2 && path() === '主歌词 / 画面 / 常规' && seg('内部布局类型') === '自动布局';`), '属性回到原样', 10_000);
  const frameAfterProperties = await hash('.orchestration', String(rev));
  const restored = await page.eval(js(`return [fieldValue('Y', '.node-placement'), fieldValue('宽'), fieldValue('最大宽度')].join('/');`));
  check(frameAfterProperties === canvasFrame && restored === '50/100/90', `Ctrl+Z ${propertySteps} 次：属性回到原样（Y/宽/最大宽度 ${restored}），帧与开始时相同`);

  // 7f. 前奏提示（G23）与句子（G24），第七批 §14.10
  //     新建提示组→添加前奏（3 秒放不下写明原因、修订不变；0.4 秒成功，列表与前奏轨出现，帧变化）→时长、停用、启用、改名
  //     →附着方式改成固定在父级再改回（画面不动）→删除→自动生成（默认 3 秒第 1 句跳过；改成 0.4 秒后新增；应用；设置保存）
  //     →删除并不再自动生成，再预演是“已排除”→标记：改字、加、上移、均匀排列、删到只剩一个
  //     →本句微调：时间轴上点第 2 句，改 X 偏移、清除→进入动画：开启工程默认，本句单独设置再关掉
  //     →画布：单击文字选中容器，双击选中这一句，拖动改本句偏移→连续撤销回到原样
  const timeLabel = (t) => `0:${t.toFixed(3).padStart(6, '0')}`;
  const frameAt = async (t) => {
    if (!(await page.eval(js(`return previewTitle().includes('· ${timeLabel(t)}');`)))) {
      await clickTimeline(page, t, 'ruler');
      await page.waitFor(js(`return previewTitle().includes('· ${timeLabel(t)}');`), `预览换到 ${t} 秒`, 30_000);
    }
    return hash('.orchestration', String(rev));
  };
  const leadStart = rev;
  const frameLeadBefore = await frameAt(0.3);

  // 新建前奏提示组（新建对话框）
  await clickRow(page, '画面');
  await page.waitFor(js(`return path() === '主歌词 / 画面';`), '选中画面');
  await ui.click(page, 'button', '新建…');
  await ui.waitDialog(page, '新建');
  await ui.choose(page, '种类', '前奏提示组', { scope: '@dialog' });
  await page.waitFor(js(`return !!field('标记数', '@dialog') && fieldValue('名称', '@dialog') === '前奏提示';`), '换成前奏提示组');
  const countdownPrefill = await page.eval(js(`return { parent: selectText('放在', '@dialog'), marks: fieldValue('标记数', '@dialog') };`));
  await ui.clickInDialog(page, '新建');
  rev++;
  await page.waitFor(atRevision(`rows().find((r) => r.name === '前奏提示')?.selected === true`), '新建提示组并选中', 10_000);
  const countdown = await page.eval(
    js(`const all = rows(); const i = all.findIndex((r) => r.name === '前奏提示'); const marks = [];
      for (let j = i + 1; j < all.length && all[j].level > all[i].level; j++) marks.push(all[j].name);
      return { role: all[i].role, marks, path: path(), titles: groupTitles(), snack: snack('已新建前奏提示组“前奏提示”（3 个标记）') };`),
  );
  check(
    countdownPrefill.parent === '主歌词 / 画面' &&
      countdownPrefill.marks === '3' &&
      countdown.marks.length === 3 &&
      countdown.path === '主歌词 / 画面 / 前奏提示' &&
      countdown.titles.includes('前奏 · 0 个') &&
      countdown.titles.includes('标记 · 3 个') &&
      countdown.snack,
    `新建前奏提示组：放在“${countdownPrefill.parent}”、标记数预填 3，一条历史；结构树出现“前奏提示”（${countdown.role}）和三个标记（${countdown.marks.join('、')}），属性区有“前奏”“标记 · 3 个”两组`,
  );

  // 添加前奏：歌词分配下面换成前奏列表→添加前奏；3 秒放不下（第 1 句 0.5 秒起唱），0.4 秒可以
  await ui.click(page, 'button', '前奏 0', { scope: '.assign-panels' });
  await page.waitFor(`!!document.querySelector('.lead-in-list') && !document.querySelector('.clip-list')`, '前奏列表展开');
  const leadPanel = await page.eval(js(`return { panels: panels(), empty: document.querySelector('.lead-in-list .list-empty')?.textContent ?? '' };`));
  await ui.click(page, 'button', '添加前奏', { scope: '.lead-in-list' });
  await ui.waitDialog(page, '添加前奏');
  const leadPrefill = await page.eval(js(`return { target: selectText('目标句', '@dialog'), indicator: selectText('提示组', '@dialog'), seconds: fieldValue('时长', '@dialog') };`));
  if (!leadPrefill.target?.startsWith('第 1 句')) {
    const first = (await selectOptions(page, '目标句', '@dialog')).find((o) => o.text.startsWith('第 1 句'));
    await ui.choose(page, '目标句', first.text, { scope: '@dialog' });
  }
  await ui.clickInDialog(page, '添加前奏');
  await page.waitFor(js(`return dialogError() !== '';`), '放不下时写明原因', 10_000);
  const conflict = await page.eval(js(`return { error: dialogError(), open: !!dlg(), revision: Number(revision()) };`));
  check(
    leadPanel.panels[1]?.text === '前奏 0' && leadPanel.panels[1].checked && leadPanel.empty.includes('还没有前奏提示') && leadPrefill.indicator === '前奏提示' && leadPrefill.seconds === '3',
    `前奏列表：为空，与片段列表只显示一个；添加前奏对话框预填“${leadPrefill.target}”、提示组“${leadPrefill.indicator}”、${leadPrefill.seconds} 秒`,
  );
  check(conflict.open && conflict.error.includes('放不下') && conflict.revision === rev, `3 秒放不下：对话框不关，写明原因（${conflict.error}），修订不变`);
  await ui.fill(page, '时长', '0.4', { scope: '@dialog' });
  await ui.clickInDialog(page, '添加前奏');
  rev++;
  await page.waitFor(atRevision(`leadInRows().length === 1 && snack('已为第 1 句添加前奏') && path() === '主歌词 / 前奏提示 / 第 1 句'`), '添加前奏', 10_000);
  const addedLeadIn = await page.eval(js(`return { row: leadInRows()[0], panel: panels()[1]?.text };`));
  const frameLeadAfter = await frameAt(0.3);
  // 前奏轨：先选别的，再点前奏轨上的这一段
  await clickRow(page, '画面');
  await page.waitFor(js(`return path() === '主歌词 / 画面';`), '选中画面');
  await clickTimeline(page, 0.3, 'leadIns');
  await page.waitFor(js(`return path() === '主歌词 / 前奏提示 / 第 1 句';`), '点前奏轨选中前奏', 10_000);
  check(
    addedLeadIn.row.text.includes('第 1 句') &&
      addedLeadIn.row.text.includes('0:00.100～0:00.500') &&
      addedLeadIn.row.text.includes('0.4 秒') &&
      addedLeadIn.row.text.includes('手工') &&
      addedLeadIn.panel === '前奏 1' &&
      frameLeadAfter !== frameLeadBefore,
    `添加前奏（0.4 秒）：一条历史，选中新前奏；列表一行（${addedLeadIn.row.text}），点前奏轨上的一段也选中它；0.3 秒处的帧变化`,
  );

  // 前奏属性：时长、停用、启用、改名
  await commit(page, '时长', '0.3', '.lead-in-inspector');
  rev++;
  await page.waitFor(atRevision(`snack('已调整前奏') && leadInRows()[0]?.text.includes('0.3 秒') && leadInRows()[0].text.includes('0:00.200～0:00.500')`), '改前奏时长', 10_000);
  await ui.setChecked(page, 'switch', '启用', false, { scope: '.lead-in-inspector' });
  rev++;
  await page.waitFor(atRevision(`snack('已停用前奏') && leadInRows()[0]?.off && leadInRows()[0].text.includes('已停用')`), '停用前奏', 10_000);
  await ui.setChecked(page, 'switch', '启用', true, { scope: '.lead-in-inspector' });
  rev++;
  await page.waitFor(atRevision(`snack('已启用前奏') && leadInRows()[0] && !leadInRows()[0].off`), '启用前奏', 10_000);
  await commit(page, '名称', '开场', '.lead-in-inspector');
  rev++;
  await page.waitFor(atRevision(`snack('前奏已改名为“开场”') && leadInRows()[0]?.text.includes('“开场”')`), '前奏改名', 10_000);
  check(true, '前奏属性：时长改成 0.3 秒（时间 0:00.200～0:00.500）、停用（列表标“已停用”）、启用、改名“开场”，各一条历史');

  // 附着方式：固定在父级，再改回跟着文字；画面不动
  const frameAttached = await frameAt(0.3);
  await ui.choose(page, '提示组放在哪里', '固定在父级', { scope: '.lead-in-inspector' });
  rev++;
  await page.waitFor(atRevision(`snack('提示组已改为固定在父级') && selectText('提示组放在哪里') === '固定在父级'`), '改成固定在父级', 10_000);
  const frameFixed = await frameAt(0.3);
  await ui.choose(page, '提示组放在哪里', '跟着目标句的文字', { scope: '.lead-in-inspector' });
  rev++;
  await page.waitFor(atRevision(`snack('提示组已改为跟着目标句的文字') && selectText('提示组放在哪里') === '跟着目标句的文字'`), '改回跟着文字', 10_000);
  const frameReattached = await frameAt(0.3);
  check(frameFixed === frameAttached && frameReattached === frameAttached, `附着方式：改成“固定在父级”再改回，各一条历史；位置由后端换算，0.3 秒处的帧都不变（${frameAttached}）`);

  // 删除（手工前奏没有“删除并不再自动生成”）
  const noExclude = !(await ui.exists(page, 'button', '删除并不再自动生成', { scope: '.orch-insp' }));
  await ui.click(page, 'button', '删除前奏', { scope: '.orch-insp' });
  rev++;
  await page.waitFor(atRevision(`snack('已删除第 1 句的前奏') && leadInRows().length === 0 && rows().some((r) => r.name === '前奏提示')`), '删除前奏', 10_000);
  check(noExclude, '删除前奏：一条历史，提示组保留；手工前奏没有“删除并不再自动生成”');

  // 自动生成：默认 3 秒时第 1 句起唱前不够；改成 0.4 秒后新增
  const previewDone = js(`return !!dlg() && genSummary() !== '' && !genSummary().includes('正在');`);
  await ui.click(page, 'button', '自动生成…', { scope: '.lead-in-list' });
  await ui.waitDialog(page, '自动生成前奏');
  await page.waitFor(previewDone, '自动生成的预演', 15_000);
  const gen1 = await page.eval(js(`return { summary: genSummary(), rows: genRows(), gap: fieldValue('间奏阈值', '@dialog'), duration: fieldValue('提示时长', '@dialog'), revision: Number(revision()) };`));
  check(
    gen1.gap === '8' && gen1.duration === '3' && gen1.summary === '跳过 4' && gen1.rows[0]?.text.includes('起唱前的时间不够一个提示时长') && gen1.rows[1]?.text.includes('不是开头') && gen1.revision === rev,
    `自动生成：打开即预演（默认阈值 ${gen1.gap} 秒、时长 ${gen1.duration} 秒），${gen1.summary}；第 1 句起唱前不够、其余不是开头也没有长间奏；修订不变`,
  );
  await ui.fill(page, '提示时长', '0.4', { scope: '@dialog', enter: true });
  await page.waitFor(js(`return genSummary().startsWith('新增 1') && !genSummary().includes('正在');`), '改时长后重新预演', 15_000);
  const gen2 = await page.eval(js(`return { summary: genSummary(), rows: genRows(), revision: Number(revision()), submit: enabled('应用（1 句）', '@dialog') };`));
  check(
    gen2.summary === '新增 1 · 跳过 3' && gen2.rows[0]?.status === '新增' && gen2.rows[0].checked && !gen2.rows[1]?.checked && gen2.submit && gen2.revision === rev,
    `改时长（0.4 秒）后重新预演：${gen2.summary}，第 1 句默认勾上，按钮写“应用（1 句）”，修订不变`,
  );
  await ui.clickInDialog(page, '应用（1 句）');
  rev++;
  await page.waitFor(atRevision(`snack('已应用自动生成：新增 1') && leadInRows().length === 1 && !dlg()`), '应用自动生成', 10_000);
  const generated = await page.eval(js(`return leadInRows()[0];`));
  await ui.click(page, 'button', '自动生成…', { scope: '.lead-in-list' });
  await ui.waitDialog(page, '自动生成前奏');
  await page.waitFor(previewDone, '再打开自动生成', 15_000);
  const saved = await page.eval(js(`return fieldValue('提示时长', '@dialog');`));
  await ui.clickInDialog(page, '取消');
  await ui.waitDialogClosed(page, '自动生成前奏');
  check(
    generated.text.includes('第 1 句') && generated.text.includes('自动') && generated.text.includes('0.4 秒') && saved === '0.4',
    `应用：一条历史，只加勾选的第 1 句（${generated.text}）；再打开时提示时长是保存下来的 ${saved} 秒`,
  );

  // 删除并不再自动生成：再预演这一句是“已排除”
  await ui.click(page, '.lead-in-list .lead-in', null);
  await ui.wait(page, 'button', '删除并不再自动生成', { scope: '.orch-insp' });
  await ui.click(page, 'button', '删除并不再自动生成', { scope: '.orch-insp' });
  rev++;
  await page.waitFor(atRevision(`snack('已删除第 1 句的前奏，自动生成不会再加这一句') && leadInRows().length === 0`), '删除并不再自动生成', 10_000);
  await ui.click(page, 'button', '自动生成…', { scope: '.lead-in-list' });
  await ui.waitDialog(page, '自动生成前奏');
  await page.waitFor(previewDone, '排除后再预演', 15_000);
  const gen3 = await page.eval(js(`return { rows: genRows(), excluded: inDialog('.generate-excluded'), unexclude: !!button('取消排除', '@dialog'), revision: Number(revision()) };`));
  await ui.clickInDialog(page, '取消');
  await ui.waitDialogClosed(page, '自动生成前奏');
  check(
    gen3.rows[0]?.text.includes('已排除') && gen3.excluded.includes('已排除 1 句') && gen3.unexclude && gen3.revision === rev,
    `删除并不再自动生成：一条历史；再预演第 1 句是“已排除”（有“取消排除”），设置里写“已排除 1 句”`,
  );
  await snapshot(page, 'lead-ins');

  // 标记：改字、加、上移、均匀排列、删到只剩一个
  await clickRow(page, '前奏提示');
  await page.waitFor(js(`return path() === '主歌词 / 画面 / 前奏提示' && markIds().length === 3;`), '选中提示组');
  await commit(page, '标记 1', '3');
  rev++;
  await page.waitFor(atRevision(`snack('标记已改为“3”')`), '改标记的字', 10_000);
  await ui.click(page, 'button', '加标记', { scope: '.orch-insp' });
  rev++;
  await page.waitFor(atRevision(`snack('已加标记') && markIds().length === 4 && groupTitles().includes('标记 · 4 个')`), '加标记', 10_000);
  const marksBefore = await page.eval(js(`return markIds();`));
  await ui.click(page, 'button', '上移标记 2', { scope: '.orch-insp' });
  rev++;
  await page.waitFor(atRevision(`markIds()[0] === '${marksBefore[1]}' && markIds()[1] === '${marksBefore[0]}'`), '上移标记', 10_000);
  await dismissToast(page);
  await ui.click(page, 'button', '均匀排列', { scope: '.orch-insp' });
  await page.waitFor(js(`return snack('已均匀排列标记') || snack('已经是均匀排列');`), '均匀排列', 10_000);
  const spaced = await page.eval(js(`return snack('已均匀排列标记');`));
  if (spaced) rev++;
  await page.waitFor(js(`return Number(revision()) === ${rev};`), '均匀排列后的修订');
  for (let n = 4; n > 1; n--) {
    await ui.click(page, 'button', `删除标记 ${n}`, { scope: '.orch-insp' });
    rev++;
    await page.waitFor(atRevision(`markIds().length === ${n - 1}`), `删除标记（剩 ${n - 1} 个）`, 10_000);
  }
  const lastMark = await page.eval(js(`return { disabled: !enabled('删除标记 1', '.orch-insp'), title: toolTitle('删除标记 1'), titles: groupTitles() };`));
  check(
    spaced && lastMark.disabled && lastMark.title.includes('提示组至少要有一个标记') && lastMark.titles.includes('标记 · 1 个'),
    `标记：改字、加标记、上移（顺序跟着变）、均匀排列、删除，各一条历史；只剩一个时不能删（${lastMark.title}）`,
  );

  // 本句微调：时间轴上点第 2 句
  const frameLine = await frameAt(4.5);
  await clickTimeline(page, 4.5, 'lines');
  await page.waitFor(js(`const f = field('X 偏移', '.line-transform'); return path() === '主歌词 / 第 2 句' && !!f && !__ui.disabled(f);`), '属性区显示第 2 句', 10_000);
  const lineView = await page.eval(
    js(`return { text: insp().querySelector('.line-text')?.textContent, shown: value('显示在'), time: value('时间'), effective: effective(), add: enabled('为这一句添加前奏…', '.orch-insp'), clear: !enabled('清除本句微调', '.orch-insp') };`),
  );
  check(
    lineView.text === '青い空見上げ' && lineView.time.startsWith('0:03.000') && lineView.effective.includes('没有进入动画') && lineView.add && lineView.clear,
    `时间轴上点第 2 句：属性区显示这一句（${lineView.text}，${lineView.time}，显示在：${lineView.shown}），没有微调时“清除本句微调”不可用，可以为它添加前奏`,
  );
  await commit(page, 'X 偏移', '10', '.line-transform');
  rev++;
  await page.waitFor(atRevision(`snack('已调整本句微调') && fieldValue('X 偏移', '.line-transform') === '10'`), '改 X 偏移', 10_000);
  const frameShifted = await frameAt(4.5);
  await ui.click(page, 'button', '清除本句微调', { scope: '.orch-insp' });
  rev++;
  await page.waitFor(atRevision(`snack('已清除本句微调') && fieldValue('X 偏移', '.line-transform') === '0'`), '清除本句微调', 10_000);
  const frameCleared = await frameAt(4.5);
  check(frameShifted !== frameLine && frameCleared === frameLine, '本句微调：X 偏移改成 10% 一条历史，帧变化；清除后一条历史，帧回到原样');

  // 进入动画：第 2 句 3.0 秒起唱，看 3.1 秒的帧
  const frameEnter = await frameAt(3.1);
  await ui.setChecked(page, 'switch', '开启工程默认进入动画', true, { scope: '.orch-insp' });
  rev++;
  await page.waitFor(atRevision(`snack('已开启工程默认进入动画') && effective().includes('用工程默认') && !!insp().querySelector('.project-animation')`), '开启工程默认进入动画', 10_000);
  const frameEnterOn = await frameAt(3.1);
  const projectEffective = await page.eval(js(`return effective();`));
  await ui.setChecked(page, 'switch', '本句单独设置', true, { scope: '.orch-insp' });
  rev++;
  await page.waitFor(atRevision(`snack('已为这一句单独设置进入动画') && effective().includes('本句单独设置') && !!insp().querySelector('.line-animation')`), '本句单独设置', 10_000);
  await ui.setChecked(page, 'switch', '本句单独设置', false, { scope: '.orch-insp' });
  rev++;
  await page.waitFor(atRevision(`snack('已清除本句进入动画，改用工程默认') && effective().includes('用工程默认') && !insp().querySelector('.line-animation')`), '清除本句进入动画', 10_000);
  check(
    frameEnterOn !== frameEnter && projectEffective.startsWith('0.4 秒'),
    `进入动画：开启工程默认一条历史，第 2 句写明“${projectEffective}”，3.1 秒（起唱后 0.1 秒）的帧变化；本句单独设置再关掉，各一条历史`,
  );

  // 画布：单击文字仍选中容器；双击选中这一句；拖动改本句偏移
  await frameAt(4.5);
  await clickRow(page, '画面');
  await page.waitFor(js(`return path() === '主歌词 / 画面' && (overlay()?.inks ?? 0) > 0;`), '选中画面，叠加层在', 30_000);
  await canvasClick(page, await page.eval(js(`return inkCenter();`)));
  await page.waitFor(js(`return path() === '主歌词 / 画面 / 常规';`), '单击文字选中常规', 10_000);
  await canvasDoubleClick(page, await page.eval(js(`return inkCenter();`)));
  await page.waitFor(js(`return path() === '主歌词 / 第 2 句' && !!document.querySelector('.orchestration .canvas-overlay .overlay-line');`), '双击选中第 2 句', 10_000);
  const focusedOverlay = await page.eval(js(`return { selected: overlay().selected, hint: document.querySelector('.orchestration').textContent.includes('双击文字选中这一句') };`));
  await page.waitFor(js(`const f = field('X 偏移', '.line-transform'); return !!f && !__ui.disabled(f);`), '第 2 句的展示读到');
  const lineBox = await page.eval(js(`return canvasBox();`));
  await canvasDrag(page, await page.eval(js(`return lineInkCenter();`)), 60, 0);
  rev++;
  await page.waitFor(atRevision(`snack('已移动第 2 句')`), '拖动第 2 句', 10_000);
  const lineDragged = await page.eval(js(`return { x: Number(fieldValue('X 偏移', '.line-transform')), y: Number(fieldValue('Y 偏移', '.line-transform')), path: path() };`));
  const expectLineX = (60 / lineBox.w) * 100;
  check(
    !focusedOverlay.selected && focusedOverlay.hint && Math.abs(lineDragged.x - expectLineX) < 0.5 && Math.abs(lineDragged.y) < 0.5 && lineDragged.path === '主歌词 / 第 2 句',
    `画布：单击文字仍选中“常规”；双击选中第 2 句（只画这一句的墨迹，不画容器的选中框）；拖动一条历史，本句 X 偏移 ${lineDragged.x}%（预期约 ${expectLineX.toFixed(2)}%）`,
  );
  await snapshot(page, 'line');

  // 连续撤销回到开始时的修订与帧
  const leadSteps = rev - leadStart;
  for (let i = 0; i < leadSteps; i++) {
    await undo(page);
    rev++;
    await page.waitFor(js(`return Number(revision()) === ${rev};`), `第七批撤销第 ${i + 1} 次`, 10_000);
  }
  await page.waitFor(js(`return rows().length === 2 && panels()[1]?.text === '前奏 0';`), '第七批回到原样', 10_000);
  const frameLeadUndone = await frameAt(0.3);
  const frameLineUndone = await frameAt(4.5);
  check(frameLeadUndone === frameLeadBefore && frameLineUndone === frameLine, `Ctrl+Z ${leadSteps} 次：提示组、前奏与句子的改动都撤销，0.3 秒与 4.5 秒的帧与开始时相同`);
  // 后面的步骤按第一批的选择与片段列表检查
  await clickRow(page, '常规');
  await page.waitFor(js(`return path() === '主歌词 / 画面 / 常规';`), '选中常规');
  await ui.click(page, 'button', '片段 0', { scope: '.assign-panels' });
  await page.waitFor(`!!document.querySelector('.clip-list') && !document.querySelector('.lead-in-list')`, '回到片段列表');

  // 7g. 片段成员（第八批 §15.8）：第 2～3 句新建片段→调整成第 2～4 句（和现在相同时不能调整）→从第 3 句拆开（预填播放头所在句）
  //     →拆出的片段扩到第 1 句时写明“不在原段里”→选中第 3～4 句的片段与前一个合并→和没有调度上下文的片段不能合并→连续撤销
  const memberStart = rev;
  await clickTimeline(page, 4.5, 'lines');
  await clickTimeline(page, 7.5, 'lines', 8);
  await page.waitFor(js(`return rangeTag().includes('已选第 2～3 句');`), '第八批：选中第 2～3 句');
  await ui.click(page, 'button', '为这几句新建片段', { scope: '.assign-range' });
  await ui.waitDialog(page, '新建片段 · 主歌词');
  await ui.fill(page, '名称', '副歌', { scope: '@dialog' });
  await ui.clickInDialog(page, '创建片段');
  rev++;
  await page.waitFor(atRevision(`!dlg() && path() === '主歌词 / 片段 / 副歌'`), '第八批：新建片段', 10_000);
  const fresh = await page.eval(
    js(`return { adjust: enabled('调整句子…', '.orch-insp'), split: enabled('拆分…', '.orch-insp'), previous: !enabled('与前一个片段合并', '.orch-insp'), next: !enabled('与后一个片段合并', '.orch-insp'), reasons: reasons() };`),
  );
  check(
    fresh.adjust &&
      fresh.split &&
      fresh.previous &&
      fresh.next &&
      fresh.reasons.includes('与前一个片段合并：前面紧挨着的第 1 句不在片段里。') &&
      fresh.reasons.includes('与后一个片段合并：后面紧挨着的第 4 句不在片段里。'),
    `片段属性区“句子”一组：调整句子、拆分可用；前后都没有片段时合并不可用，原因写在下面（${fresh.reasons.join('；')}）`,
  );

  // 调整句子：预填现在的范围，相同时不能调整；改成第 2～4 句
  const clipA = await page.eval(js(`return clipRows().find((c) => c.selected)?.id;`));
  const membersDialog = '调整片段的句子 · 副歌';
  await ui.click(page, 'button', '调整句子…', { scope: '.orch-insp' });
  await ui.waitDialog(page, membersDialog);
  const sameForm = await page.eval(js(`return { first: fieldValue('起句', '@dialog'), last: fieldValue('止句', '@dialog'), block: inDialog('.clip-members-block'), submit: !enabled('调整', '@dialog') };`));
  await ui.fill(page, '止句', '4', { scope: '@dialog' });
  const grownForm = await page.eval(js(`return { block: inDialog('.clip-members-block'), lines: inDialog('.clip-range-lines'), submit: !enabled('调整', '@dialog') };`));
  check(
    sameForm.first === '2' && sameForm.last === '3' && sameForm.block === '和现在的句子相同。' && sameForm.submit && !grownForm.block && grownForm.lines.startsWith('3 句') && !grownForm.submit,
    `调整句子对话框预填第 ${sameForm.first}～${sameForm.last} 句，和现在相同时写明“${sameForm.block}”且“调整”不可用；改到第 4 句后可以调整（${grownForm.lines}）`,
  );
  await ui.clickInDialog(page, '调整');
  rev++;
  await page.waitFor(atRevision(`!dlg() && clipRows().find((c) => c.id === '${clipA}')?.text.startsWith('副歌 第 2～4 句')`), '调整成第 2～4 句', 10_000);
  const grown = await page.eval(js(`return { range: value('范围'), snack: snack('片段“副歌”改为第 2～4 句'), rows: clipRows().length, selected: clipRows().find((c) => c.selected)?.id };`));
  check(
    grown.range === '第 2～4 句（3 句）' && grown.snack && grown.rows === 1 && grown.selected === clipA,
    `调整句子：一条历史，片段列表与属性区变成“${grown.range}”，仍选中这个片段，提示可撤销`,
  );

  // 拆分：播放头在第 4 句时预填第 4 句；改成从第 3 句拆开
  await clickTimeline(page, 10.5, 'ruler');
  await page.waitFor(js(`return previewTitle().includes('· ${timeLabel(10.5)}');`), '播放头到第 4 句', 30_000);
  const splitDialog = '拆分片段 · 副歌';
  await ui.click(page, 'button', '拆分…', { scope: '.orch-insp' });
  await ui.waitDialog(page, splitDialog);
  const prefilledSplit = await page.eval(js(`return selectText('从哪一句开始拆出', '@dialog');`));
  const splitOptions = await selectOptions(page, '从哪一句开始拆出', '@dialog');
  await ui.choose(page, '从哪一句开始拆出', splitOptions.find((o) => o.text.startsWith('第 3 句')).text, { scope: '@dialog' });
  await page.waitFor(js(`return inDialog('.split-clip-result').startsWith('第 2 句留在“副歌”，第 3～4 句成为新片段');`), '拆分点改成第 3 句');
  const splitText = await page.eval(js(`return inDialog('.split-clip-result');`));
  check(
    prefilledSplit?.startsWith('第 4 句') && splitOptions.length === 2 && splitText.includes('句子只能在第 2～4 句里调整'),
    `拆分对话框：拆分点是除第一句以外的 ${splitOptions.length} 句，预填播放头所在的“${prefilledSplit}”；改成第 3 句后写明“${splitText}”`,
  );
  await snapshot(page, 'split-dialog');
  await ui.clickInDialog(page, '拆分');
  rev++;
  await page.waitFor(atRevision(`!dlg() && clipRows().length === 2`), '拆分片段', 10_000);
  const splitDone = await page.eval(js(`return { rows: clipRows(), path: path(), snack: snack('已把片段“副歌”拆成第 2 句和第 3～4 句'), context: value('原段'), range: value('范围') };`));
  const clipB = splitDone.rows.find((c) => c.selected)?.id;
  check(
    splitDone.rows[0].id === clipA &&
      splitDone.rows[0].text.startsWith('副歌 第 2 句') &&
      splitDone.rows[1].text.startsWith('副歌 第 3～4 句') &&
      clipB &&
      clipB !== clipA &&
      splitDone.range === '第 3～4 句（2 句）' &&
      splitDone.context === '第 2～4 句' &&
      splitDone.snack,
    `拆分：一条历史，变成第 2 句和第 3～4 句两个同名片段，选中新片段；它的调度上下文是原段${splitDone.context}`,
  );
  await snapshot(page, 'split');

  // 调度上下文：留在原片段的第 2 句扩到第 1 句时写明“不在原段里”
  await clickClip(page, clipA);
  await page.waitFor(js(`return clipRows().find((c) => c.selected)?.id === '${clipA}' && value('范围') === '第 2 句';`), '选中第 2 句的片段');
  await ui.click(page, 'button', '调整句子…', { scope: '.orch-insp' });
  await ui.waitDialog(page, membersDialog);
  await ui.fill(page, '起句', '1', { scope: '@dialog' });
  const outside = await page.eval(js(`return { note: inDialog('.clip-members-context'), block: inDialog('.clip-members-block'), submit: !enabled('调整', '@dialog') };`));
  check(
    outside.note.includes('原段（第 2～4 句）') && outside.block.startsWith('第 1 句不在原段（第 2～4 句）里') && outside.submit,
    `拆分后的片段扩到第 1 句：对话框写明“${outside.block}”，“调整”不可用`,
  );
  await snapshot(page, 'members-dialog');
  await ui.clickInDialog(page, '取消');
  await ui.waitDialogClosed(page, membersDialog);

  // 合并：选中第 3～4 句的片段，与前一个合并，保留它的身份
  await clickClip(page, clipB);
  await page.waitFor(js(`return clipRows().find((c) => c.selected)?.id === '${clipB}' && enabled('与前一个片段合并', '.orch-insp');`), '选中第 3～4 句的片段');
  await ui.click(page, 'button', '与前一个片段合并', { scope: '.orch-insp' });
  rev++;
  await page.waitFor(atRevision(`clipRows().length === 1`), '合并片段', 10_000);
  const merged = await page.eval(js(`return { rows: clipRows(), range: value('范围'), snack: snack('已把片段“副歌”合并进“副歌”（第 2～4 句）') };`));
  check(
    merged.rows[0].id === clipB && merged.rows[0].selected && merged.rows[0].text.startsWith('副歌 第 2～4 句') && merged.range === '第 2～4 句（3 句）' && merged.snack,
    `合并：一条历史，回到一个第 2～4 句的片段，身份是选中的那个，仍选中它`,
  );

  // 和没有调度上下文的片段（第 1 句新建）不能合并
  await clickTimeline(page, 1.5, 'lines');
  await page.waitFor(js(`return rangeTag().includes('已选第 1 句');`), '选中第 1 句');
  await ui.click(page, 'button', '为这几句新建片段', { scope: '.assign-range' });
  await ui.waitDialog(page, '新建片段 · 主歌词');
  await ui.clickInDialog(page, '创建片段');
  rev++;
  await page.waitFor(atRevision(`!dlg() && clipRows().length === 2`), '新建第 1 句的片段', 10_000);
  await clickClip(page, clipB);
  await page.waitFor(js(`return clipRows().find((c) => c.selected)?.id === '${clipB}';`), '再选中第 2～4 句的片段');
  const contextBlock = await page.eval(js(`return { disabled: !enabled('与前一个片段合并', '.orch-insp'), reasons: reasons() };`));
  const contextReason = '与前一个片段合并：两个片段来自不同的原段（调度上下文不同），不能合并。';
  check(contextBlock.disabled && contextBlock.reasons.includes(contextReason), `与没有调度上下文的片段相邻时不能合并，写明“${contextReason}”`);
  await snapshot(page, 'members');

  // 连续撤销回到开始
  const memberSteps = rev - memberStart;
  for (let i = 0; i < memberSteps; i++) {
    await undo(page);
    rev++;
    await page.waitFor(js(`return Number(revision()) === ${rev};`), `第八批撤销第 ${i + 1} 次`, 10_000);
  }
  await page.waitFor(js(`return clipRows().length === 0 && unassigned() === '未单独分配 4 句';`), '第八批回到原样', 10_000);
  check(memberSteps === 5, `Ctrl+Z ${memberSteps} 次（新建、调整、拆分、合并、新建）：片段都消失，4 句都回到默认容器`);
  await clickRow(page, '常规');
  await page.waitFor(js(`return path() === '主歌词 / 画面 / 常规';`), '选中常规');

  // 8. 加一个翻译来源：编排按新修订重读，新来源有自己的程序（R4 已修复）；它的快速布局不可用并写明原因（R7）
  await gotoPage(page, 'lyrics', `!!document.querySelector('.line-list')`);
  const before = Number(await page.eval(js(`return revision();`)));
  await ui.menu(page, '歌词来源：', '管理来源…', { partial: true });
  await ui.waitDialog(page, '管理来源');
  await ui.click(page, 'button', '添加翻译来源', { scope: '@dialog' });
  await ui.fill(page, '新来源名称', '中文', { scope: '@dialog' });
  await ui.click(page, 'button', '添加', { scope: '@dialog' });
  await page.waitFor(js(`return Number(revision()) === ${before + 1};`), '添加翻译来源', 10_000);
  await ui.click(page, 'button', '关闭管理来源');
  await ui.waitDialogClosed(page, '管理来源');
  await gotoPage(page, 'orchestration', treeReady);
  await page.waitFor(js(`return sections().length === 2;`), '结构树出现新来源', 10_000);
  const added = await page.eval(js(`return { sections: sections(), path: path() };`));
  const translation = added.sections.find((s) => s.title === '中文');
  check(
    !!translation && translation.rows === 2 && !translation.empty && added.path === '主歌词 / 画面 / 常规',
    `加翻译来源后结构按新修订重读：新来源“中文”有自己的画面和常规容器（R4 已修复）；选中的节点保留（${added.path}）`,
  );
  await ui.choose(page, '来源', '中文', { scope: '.assign' });
  await page.waitFor(js(`return path() === '中文 / 画面 / 常规';`), '歌词分配切到新来源');
  const r7 = await page.eval(
    js(`return { disabled: !enabled('快速布局…'), title: button('快速布局…')?.closest('.tool-wrap')?.title ?? '', hint: hint(), revision: revision(),
      clip: !enabled('新建片段', '.clip-list'), clipHint: document.querySelector('.clip-list > .note')?.textContent ?? '' };`),
  );
  check(
    r7.disabled && r7.title.includes('只支持独立计时') && r7.hint.includes('只支持独立计时') && Number(r7.revision) === before + 1,
    `翻译来源的“快速布局…”不可用，并写明原因（R7）：${r7.hint}`,
  );
  check(r7.clip && r7.clipHint.includes('不能建片段'), `翻译来源的“新建片段”不可用，并写明原因：${r7.clipHint}`);
  await snapshot(page, 'translation');

  // 9. 撤销：新来源消失，歌词分配回到主歌词
  await undo(page);
  // 撤销本身也是一个新修订。
  await page.waitFor(js(`return Number(revision()) === ${before + 2} && sections().length === 1;`), '撤销后', 10_000);
  await page.waitFor(js(`return assign('来源') === '主歌词' && hint().includes('只有一个歌词容器');`), '歌词分配回到主歌词', 10_000);
  check(true, 'Ctrl+Z 撤销：新来源从结构树消失，歌词分配回到主歌词');

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
