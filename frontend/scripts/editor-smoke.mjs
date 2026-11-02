// 编辑器浏览器冒烟（W1 设计 §6）：用真实 Chrome 走一遍编辑器主链路。
// 新建（上传生成的 WAV＋LRC）→ 选元素改时间（逐字预览跟随）→ 撤销／重做 → 播放中按 J 打点 → 微调／清除
// → 整行平移、定位 → 时间轴波形读自后端（G11）、拖动边界（含相邻元素共用边界）→ 注音、正文、行插入／复制／批量删除
// → 文本单元重切本行、合并、拆开、拖动排序、整来源重切 → 来源管理与翻译关联 → 粘贴追加、导入文件与导入报告
// → 工程设置（画布、帧率、背景色、输出时长、音频分析）
// → 刷新后从“继续工作”接回 → 保存到新目录 → 关闭 → 从工程目录重开并核对时间。
//
// 前提：pnpm dev:backend 与 pnpm dev 都在运行。
// 用法：node scripts/editor-smoke.mjs [--url http://localhost:5173] [--screenshot out.png] [--headed]

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

// ── 素材：12 秒 WAV（每秒一个渐弱的音，波形上能看出节奏）与四行 LRC ──

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
    const beat = t % 1;
    const envelope = Math.exp(-beat * 4) * Math.min(1, i / 500, (samples - i) / 500);
    buffer.writeInt16LE(Math.round(Math.sin(2 * Math.PI * 330 * t) * 12000 * envelope), 44 + i * 2);
  }
  return buffer;
}

const LRC = '[00:00.50]朝の光浴びて\n[00:03.00]青い空見上げ\n[00:06.00]Hello karaoke\n[00:09.00]你好，世界\n';

const assets = mkdtempSync(join(tmpdir(), 'mako-editor-smoke-'));
const wavPath = join(assets, '冒烟音频.wav');
const lrcPath = join(assets, '冒烟歌词.lrc');
// 再次导入用：第 2 行在第一个时间之前、没有时间标签（提示，跳过），第 4 行只有时间（提示，跳过），第 6 行时间倒序（警告）。
const importPath = join(assets, '冒烟导入.lrc');
writeFileSync(wavPath, makeWav());
writeFileSync(lrcPath, LRC);
writeFileSync(importPath, '[ti:冒烟导入]\n开头没有时间的一行\n[00:01.00]第一行\n[00:02.00]\n[00:05.00]第三行\n[00:03.00]倒序的一行\n');
const directory = `smoke-${Date.now()}`;

// ── 页面操作 ──

const checks = [];
function check(ok, label) {
  checks.push({ ok: !!ok, label });
  console.log(`${ok ? '✓' : '✗'} ${label}`);
  if (!ok) process.exitCode = 1;
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const REVISION = `Number((document.querySelector('.status-bar')?.textContent.match(/修订 (\\d+)/) ?? [])[1])`;
const atRevision = (n) => `${REVISION} === ${n}`;
const revision = () => page.eval(REVISION);

// 行列表、文本单元、时间栏、行编辑区
const rowCount = `document.querySelectorAll('.line-list [role=row]').length`;
const lineTexts = `[...document.querySelectorAll('.line-list .ll-text')].map((n) => n.textContent)`;
const lineNotes = `[...document.querySelectorAll('.line-list .ll-sub')].map((n) => n.textContent)`;
const chipTexts = `[...document.querySelectorAll('.tk-row .tk .tk-t')].map((n) => n.textContent)`;
const selectedChip = `document.querySelector('.tk-row .tk[aria-selected=true] .tk-t')?.textContent`;
const lineTitle = `document.querySelector('.lhead h2')?.textContent`;
const lineText = uiJs(`return __ui.find('textbox', '正文')?.value ?? null;`);
/** 时间栏的起止（所选文本单元，没选时是本行）。 */
const timeInputs = `[...document.querySelectorAll('.timebar .tb-time input')]`;
const startTime = `(${timeInputs}[0]?.value ?? null)`;
const endTime = `(${timeInputs}[1]?.value ?? null)`;
const timebarStatus = `document.querySelector('.timebar .status-icon')?.getAttribute('aria-label')`;
const previewText = (role) => `document.querySelector('.wp-${role} .wp-text')?.textContent`;
const toastHas = (text) => `(document.querySelector('.toast-region .toast-text')?.textContent ?? '').includes(${JSON.stringify(text)})`;
const dialogOpen = `!!document.querySelector('[role=dialog], [role=alertdialog]')`;

/** 选中当前行里文字为 text 的文本单元；已选中时不再点（再点一次会回到整行）。 */
async function selectToken(page, text) {
  if ((await page.eval(selectedChip)) !== text) await page.eval(`[...document.querySelectorAll('.tk-row .tk')].find((c) => c.querySelector('.tk-t').textContent === ${JSON.stringify(text)}).click()`);
  await page.waitFor(`${selectedChip} === ${JSON.stringify(text)} && document.querySelector('.timebar b')?.textContent === ${JSON.stringify(text)}`, `选中文本单元「${text}」`);
}

/** 时间栏填起止并回车提交。 */
async function setTimes(page, start, end) {
  await page.eval(uiJs(`const el = ${timeInputs}[0]; el.focus(); __ui.setValue(el, ${JSON.stringify(start)});`));
  await page.eval(uiJs(`const el = ${timeInputs}[1]; el.focus(); __ui.setValue(el, ${JSON.stringify(end)}); __ui.key(el, 'Enter');`));
}

/** 点一行；等行编辑区换到这一行。 */
async function selectRow(page, number, text) {
  await ui.click(page, 'row', `${number} ${text}`.trim());
  await page.waitFor(`${lineTitle} === '第 ${number} 行'`, `选中第 ${number} 行`);
}

/** 顶栏的撤销、重做（工程设置面板、提示条里也有“撤销”）。 */
const undo = (page) => ui.click(page, 'button', '撤销', { scope: '.app-bar' });
const redo = (page) => ui.click(page, 'button', '重做', { scope: '.app-bar' });

async function key(page, type, keyName, code, keyCode) {
  await page.send('Input.dispatchKeyEvent', { type, key: keyName, code, windowsVirtualKeyCode: keyCode, nativeVirtualKeyCode: keyCode });
}

async function escape(page) {
  await key(page, 'keyDown', 'Escape', 'Escape', 27);
  await key(page, 'keyUp', 'Escape', 'Escape', 27);
}

/** 来源选择菜单里的来源名称（打开看一眼再关上）。 */
async function branchNames(page) {
  await ui.click(page, 'button', '歌词来源：', { partial: true });
  await ui.wait(page, 'menuitem', '管理来源…', { scope: '@menu' });
  const names = await page.eval(uiJs(`return [...document.querySelectorAll('[role=menu] [role=menuitemradio]')].map((el) => __ui.names(el).at(-1));`));
  await escape(page);
  await page.waitFor(`!document.querySelector('[role=menu]')`, '来源菜单关闭');
  return names;
}

const playheadText = `(document.querySelector('.transport-time span')?.textContent ?? '')`;

/** 中间截图：等对话框、侧边面板的动画结束再截。 */
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
  check(true, '编辑器页面经 bootstrap 路由连上 Server');

  // 1. 新建
  await shell.startNewProject(page, { audio: wavPath, lyrics: lrcPath, name: `冒烟 ${directory}` });
  await page.waitFor(`document.querySelector('.new-project-dialog').innerText.includes('冒烟音频.wav') && document.querySelector('.new-project-dialog').innerText.includes('冒烟歌词.lrc')`, '文件已选择');
  await shell.finishNewProject(page);
  await page.waitFor(`${rowCount} === 4 && ${chipTexts}.length > 0`, '工作区显示 4 行', 120_000);
  check(true, '上传中文文件名的 WAV 与 LRC，新建工程并进入工作区（4 行）');
  check((await revision()) === 0, '新建后修订 0');
  const chips = await page.eval(chipTexts);
  check(chips.join('') === '朝の光浴びて' && chips.length === 6, `第一行按“混合”规则逐字拆成 6 个元素（${chips.join('|')}）`);

  // 逐字预览：播放头在 0 秒，第一句（0.5 秒起）尚未开始
  await page.waitFor(`${previewText('upcoming')} === '朝の光浴びて'`, '逐字预览显示即将开始的第一句', 30_000);
  check(true, '逐字预览：播放头在第一句之前时显示即将开始的第一句');

  // 2. 选第一个元素并输入时间
  await selectToken(page, '朝');
  await setTimes(page, '0:00.600', '0:00.900');
  await page.waitFor(atRevision(1), '命令后修订 1');
  check((await page.eval(startTime)) === '0:00.600' && (await page.eval(endTime)) === '0:00.900', '回车提交 setTimeRange：读模型刷新后元素时间为 0.600–0.900');
  await page.waitFor(`!!document.querySelector('.wp-upcoming .wp-seg[data-s="600000"][data-e="900000"]')`, '逐字预览跟随编辑', 10_000);
  check(true, '逐字预览跟随编辑：「朝」的填充区间变为 0.600–0.900');
  await snapshot(page, 'preview');

  // 3. 撤销／重做
  await undo(page);
  await page.waitFor(atRevision(2), '撤销后修订 2');
  check((await page.eval(startTime)) === '', '撤销后元素回到未打轴');
  await redo(page);
  await page.waitFor(atRevision(3), '重做后修订 3');
  check((await page.eval(startTime)) === '0:00.600', '重做后元素时间恢复');

  // 4. 播放中打点：第 2 行（3.0–6.0 秒）的前两个元素，按住 J 记起点、松开记终点
  await selectRow(page, 2, '青い空見上げ');
  await page.waitFor(`${lineText} === '青い空見上げ'`, '第 2 行正文');
  await page.waitFor(`document.querySelector('.transport .play') && !document.querySelector('.transport .play').disabled`, '音频就绪', 60_000);
  check(true, '工程音频经鉴权读取后可以播放');
  await ui.setChecked(page, 'switch', '打点模式', true);
  // 真实点击会把焦点留在开关上，空格会拨开关而不是播放
  await page.eval(`document.activeElement?.blur()`);
  await page.waitFor(`document.querySelector('.transport-help')?.textContent.includes('按住 J')`, '打点模式开启');
  const revisionBefore = await revision();
  await key(page, 'keyDown', ' ', 'Space', 32);
  await key(page, 'keyUp', ' ', 'Space', 32);
  await page.waitFor(`${playheadText} >= '0:03.200'`, '播放到 3.2 秒', 20_000);
  for (let i = 0; i < 2; i++) {
    await key(page, 'keyDown', 'j', 'KeyJ', 74);
    await sleep(250);
    await key(page, 'keyUp', 'j', 'KeyJ', 74);
    await sleep(100);
  }
  await key(page, 'keyDown', ' ', 'Space', 32);
  await key(page, 'keyUp', ' ', 'Space', 32);
  await page.waitFor(`${atRevision(revisionBefore + 4)} && !document.querySelector('.status-bar')?.textContent.includes('同步中')`, '四条打点命令完成', 20_000);
  await snapshot(page, 'stamped');
  const fills = await page.eval(`[...document.querySelectorAll('.wp-current .wp-seg')].map((s) => s.style.getPropertyValue('--fill'))`);
  check(
    (await page.eval(previewText('current'))) === '青い空見上げ' && fills[0] === '1' && fills[1] === '1' && fills[2] === '0',
    `暂停后逐字预览：当前行「青い空見上げ」，已打点的两个元素填满、未打轴的「空」未填（${fills.join(' ')}）`,
  );
  const untimed = await page.eval(`[...document.querySelectorAll('.tk-row .tk')].map((c) => c.classList.contains('untimed'))`);
  check(untimed[0] === false && untimed[1] === false && untimed[2] === true, `播放中打点两次：前两个元素有了时间、第三个未打轴（${untimed.slice(0, 3).join(' ')}）`);
  check((await page.eval(selectedChip)) === '空', '每次松开 J 自动前进：打完两个元素后选中第三个「空」');
  await selectToken(page, '青');
  const stampStart = await page.eval(startTime);
  const stampStatus = await page.eval(timebarStatus);
  check(stampStart >= '0:03.200' && stampStart < '0:04.500' && stampStatus === '已修改', `打点时间来自按键当时的播放头（「青」起点 ${stampStart}，${stampStatus}）`);

  // 5. 按帧微调与清除
  const beforeNudge = await revision();
  await ui.click(page, 'button', '晚一帧', { scope: '.timebar' });
  await page.waitFor(atRevision(beforeNudge + 1), '微调完成');
  check((await page.eval(startTime)) !== stampStart, `按帧微调：起点 ${stampStart} → ${await page.eval(startTime)}`);
  await selectToken(page, 'い');
  await ui.click(page, 'button', '清除', { scope: '.timebar' });
  await page.waitFor(atRevision(beforeNudge + 2), '清除完成');
  check((await page.eval(startTime)) === '', '清除元素时间后回到未打轴');

  // 6. 整行平移与时间轴拖动：第 1 行
  await selectRow(page, 1, '朝の光浴びて');
  const beforeShift = await revision();
  await ui.click(page, 'button', '整行平移…');
  await ui.fill(page, '毫秒（可为负）', '-100', { enter: true });
  await page.waitFor(`${atRevision(beforeShift + 1)} && !document.querySelector('.shift-dialog')`, '平移完成');
  check(
    await page.eval(`document.querySelector('.lhead .tnum')?.textContent === '0:00.400 – 0:02.900'`),
    '整行平移 −100 毫秒（弹层输入）：行 0.4–2.9，已定时元素一起移动',
  );
  await ui.click(page, 'button', '定位', { scope: '.lhead' });
  await page.waitFor(`${previewText('current')} === '朝の光浴びて' && document.querySelector('.wp-clock')?.textContent.includes('0:00.400')`, '定位到第 1 行', 10_000);
  check(true, '定位：播放头到行起点 0:00.400，逐字预览显示该行为当前行');

  await ui.click(page, 'button', '全曲');
  const geometry = await page.eval(`(() => {
    const r = document.querySelector('.timeline-canvas canvas').getBoundingClientRect();
    return { left: r.left, top: r.top, width: r.width };
  })()`);
  // “全曲”视口：起点 0，每像素 = 时长 ÷ 宽度（12 秒音频）。元素轨在画布 y = 22 + 72 + 17 处。
  const xAt = (us) => geometry.left + (us * geometry.width) / 12_000_000;
  const tokenLaneY = geometry.top + 22 + 72 + 17;
  async function dragTimeline(fromUs, toUs) {
    const steps = 6;
    await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: xAt(fromUs), y: tokenLaneY });
    await page.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: xAt(fromUs), y: tokenLaneY, button: 'left', clickCount: 1 });
    for (let i = 1; i <= steps; i++) {
      const us = fromUs + ((toUs - fromUs) * i) / steps;
      await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: xAt(us), y: tokenLaneY, button: 'left', buttons: 1 });
    }
    await page.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: xAt(toUs), y: tokenLaneY, button: 'left', clickCount: 1 });
  }

  // 波形来自后端（G11）：画布标明来源；全曲视口下，振幅 ≥ 0.25 的位置只出现在每秒开头，能数出 12 拍。
  await page.waitFor(`document.querySelector('.timeline-canvas canvas')?.dataset.waveform === 'backend' && Number(document.querySelector('.timeline-canvas canvas').dataset.waveformChunks) >= 1`, '后端波形', 10_000);
  const beats = await page.eval(`(async () => {
    // “全曲”改了视口，下一帧才重画：等两帧再读像素。
    for (let i = 0; i < 2; i++) await new Promise((r) => requestAnimationFrame(r));
    const canvas = document.querySelector('.timeline-canvas canvas');
    const ratio = canvas.width / canvas.getBoundingClientRect().width;
    const ctx = canvas.getContext('2d');
    // 波形道 y 22–94，中线 58，半高 32：振幅 0.25 在 y = 50；y = 24 永远画不到，作背景参照。
    const row = (y) => ctx.getImageData(0, Math.round(y * ratio), canvas.width, 1).data;
    const probe = row(50);
    const background = row(24);
    let runs = 0;
    let inRun = false;
    for (let x = 0; x < canvas.width; x++) {
      const differs = [0, 1, 2].some((k) => Math.abs(probe[x * 4 + k] - background[x * 4 + k]) > 8);
      if (differs && !inRun) runs++;
      inRun = differs;
    }
    return runs;
  })()`);
  check(beats === 12, `时间轴波形读自后端（G11，按块懒加载）：全曲视口里数出 ${beats} 拍（应为 12）`);

  // 「朝」现在 0.5–0.8：把终点拖到 1.1 秒（30 fps 下正好在帧上）。
  const beforeDrag = await revision();
  await dragTimeline(800_000, 1_100_000);
  await page.waitFor(atRevision(beforeDrag + 1), '拖动终点提交');
  await selectToken(page, '朝');
  await page.waitFor(`${endTime} === '0:01.100'`, '「朝」终点 1.1', 10_000).catch(() => undefined);
  check((await page.eval(endTime)) === '0:01.100', `拖动元素终点：「朝」0.5–${await page.eval(endTime)}`);

  // 「の」接在后面：1.1–1.4；再把共用边界 1.1 拖到 1.0，两边一次提交。
  await selectToken(page, 'の');
  await setTimes(page, '0:01.100', '0:01.400');
  await page.waitFor(atRevision(beforeDrag + 2), '「の」设时间');
  await dragTimeline(1_100_000, 1_000_000);
  await page.waitFor(`${atRevision(beforeDrag + 3)} && !document.querySelector('.status-bar')?.textContent.includes('同步中')`, '拖动共用边界提交');
  // 拖动开始时会选中被拖的元素（这里是「朝」），分别选中两边读时间。
  await selectToken(page, 'の');
  const noStart = await page.eval(startTime);
  await selectToken(page, '朝');
  const asaEnd = await page.eval(endTime);
  check(asaEnd === '0:01.000' && noStart === '0:01.000', `拖动相邻元素的共用边界：一次命令同时改「朝」终点（${asaEnd}）与「の」起点（${noStart}）`);
  await undo(page);
  await page.waitFor(atRevision(beforeDrag + 4), '撤销拖动');
  await page.waitFor(`${endTime} === '0:01.100'`, '撤销后「朝」终点 1.1', 10_000).catch(() => undefined);
  check((await page.eval(endTime)) === '0:01.100', '一次撤销还原共用边界两侧');
  const savedAsaStart = await page.eval(startTime);

  // 6b. 注音、正文、行操作（S3a）
  let before = await revision();
  await ui.fill(page, '整段注音', 'あさ', { enter: true });
  await page.waitFor(`document.querySelector('.tk-row .tk .tk-r')?.textContent === 'あさ'`, '注音显示在「朝」上方', 10_000);
  check((await revision()) === before + 1, '注音：setRubyText 后「朝」上方显示「あさ」');

  await selectRow(page, 4, '你好，世界');
  before = await revision();
  await ui.fill(page, '正文', '你好，新世界', { enter: true });
  await page.waitFor(toastHas('正文已更新'), '正文更新提示', 10_000);
  check((await page.eval(lineTexts))[3] === '你好，新世界' && (await page.eval(toastHas('保留'))), '正文：Enter 提交 setLineText，提示保留／重新拆分的文本单元数');
  await undo(page);
  await page.waitFor(`${atRevision(before + 2)} && ${lineText} === '你好，世界'`, '撤销正文', 10_000);
  check(true, '撤销正文修改');

  await ui.menu(page, '行操作', '在下方插入', { scope: '.lhead' });
  await page.waitFor(`${lineTexts}.length === 5`, '插入后 5 行', 10_000);
  await page.waitFor(`${lineText} === '' && ${lineTitle} === '第 5 行'`, '选中新插入的空行', 10_000);
  check(true, '行操作菜单“在下方插入”：新空行出现在第 5 行并被选中（feedback.selection）');
  // 右键第 1 行：选中它并在指针处弹出行操作菜单
  await ui.click(page, 'row', '1 朝の光浴びて', { right: true });
  await ui.click(page, 'menuitem', '复制本行', { scope: '@menu' });
  await page.waitFor(`${lineTexts}.length === 6`, '复制后 6 行', 10_000);
  check(JSON.stringify((await page.eval(lineTexts)).slice(0, 2)) === JSON.stringify(['朝の光浴びて', '朝の光浴びて']), '右键菜单“复制本行”：副本紧接原行');

  // 打开多选，勾选副本和空行，批量删除（一次 project.applyEdits，确认框，一条历史）
  before = await revision();
  await ui.click(page, 'button', '多选');
  await ui.click(page, 'row', '2 朝の光浴びて');
  await ui.click(page, 'row', '6');
  await page.waitFor(`document.querySelector('.bulk-count')?.textContent === '已选 2 行'`, '多选 2 行');
  await ui.click(page, 'button', '删除', { scope: '.bulk-bar' });
  await ui.waitDialog(page, '删除 2 行？');
  await ui.clickInDialog(page, '删除');
  await page.waitFor(`${atRevision(before + 1)} && ${rowCount} === 4`, '批量删除完成', 20_000);
  check(
    JSON.stringify(await page.eval(lineTexts)) === JSON.stringify(['朝の光浴びて', '青い空見上げ', 'Hello karaoke', '你好，世界']),
    '多选勾选两行后批量删除：确认后一次删除（一条历史），回到原来 4 行',
  );
  await ui.click(page, 'button', '多选');
  await page.waitFor(`!document.querySelector('.bulk-bar')`, '关闭多选');
  await undo(page);
  await page.waitFor(`${atRevision(before + 2)} && ${rowCount} === 6`, '撤销批量删除', 10_000);
  await redo(page);
  await page.waitFor(`${atRevision(before + 3)} && ${rowCount} === 4`, '重做批量删除', 10_000);
  check(true, '批量删除一次撤销两行都回来，重做再次删除');

  // 6c. 文本单元结构（S3c）：第 3 行「Hello karaoke」
  await selectRow(page, 3, 'Hello karaoke');
  await ui.click(page, 'button', '按规则重切本行…');
  await ui.waitDialog(page, '按规则重切本行');
  await page.waitFor(uiJs(`return __ui.find('textbox', '切分', false, '@dialog')?.value === 'Hello |karaoke';`), '重切对话框带出当前切分', 10_000);
  await ui.fill(page, '切分', 'Hel|lo |karaoke', { scope: '@dialog' });
  await page.waitFor(`document.querySelectorAll('[role=dialog] .resplit-chip').length === 3`, '切分预览 3 段');
  await ui.clickInDialog(page, '重切');
  await page.waitFor(`!${dialogOpen} && ${chipTexts}.length === 3 && ${toastHas('已重切本行')}`, '重切本行完成', 10_000);
  check(true, `按规则重切本行：用“|”切成 3 段（${(await page.eval(chipTexts)).join('|')}），对话框预览切分结果`);

  await selectToken(page, 'Hel');
  before = await revision();
  await ui.click(page, 'button', '与后一个合并');
  await page.waitFor(`${atRevision(before + 1)} && ${chipTexts}.length === 2`, '合并完成', 10_000);
  await page.waitFor(`${selectedChip} === 'Hello '`, '合并后选中合并结果', 10_000);
  check(true, '与后一个合并：「Hel」+「lo」，选中合并后的单元（tokenResult.selection）');

  await setTimes(page, '0:06.200', '0:06.800');
  await page.waitFor(atRevision(before + 2), '给 Hello 打上时间', 10_000);
  await ui.click(page, 'button', '拆开');
  await page.waitFor(`${chipTexts}.length === 3`, '拆开完成', 10_000);
  const splitChips = await page.eval(chipTexts);
  // 合并结果是「Hello 」（含尾随空格，6 个文本元素），对半拆成「Hel」「lo 」。
  check(splitChips[0] === 'Hel' && splitChips[1] === 'lo ', `拆开已打轴的单元：按文本元素数对半（${splitChips.join('|')}）`);

  // 把最后一个单元拖到最前，选“时间槽跟着新顺序重排”
  before = await revision();
  await page.eval(`(() => {
    const chips = document.querySelectorAll('.tk-row .tk');
    const dt = new DataTransfer();
    const fire = (el, type, x) => el.dispatchEvent(new DragEvent(type, { bubbles: true, cancelable: true, dataTransfer: dt, clientX: x, clientY: el.getBoundingClientRect().top + 5 }));
    const first = chips[0].getBoundingClientRect();
    fire(chips[2], 'dragstart', 0);
    fire(chips[0], 'dragover', first.left + 2);
    fire(chips[0], 'drop', first.left + 2);
    fire(chips[2], 'dragend', 0);
  })()`);
  await ui.waitDialog(page, '调整文本单元顺序');
  await ui.clickInDialog(page, '时间槽跟着新顺序重排');
  await page.waitFor(atRevision(before + 1), '重排完成', 10_000);
  await page.waitFor(`${chipTexts}[0] === 'karaoke'`, '重排后顺序', 10_000);
  check(true, `拖动排序：「karaoke」移到最前（${(await page.eval(chipTexts)).join('|')}），时间槽跟着新顺序重排`);

  // 整个来源按“逐字”重切，一次撤销整体还原
  const chipsBefore = (await page.eval(chipTexts)).length;
  before = await revision();
  await ui.menu(page, '来源操作', '按规则重切整个来源…');
  await ui.waitDialog(page, '按规则重切整个来源');
  await ui.choose(page, '拆分规则', '逐字：英文也逐字母', { scope: '@dialog' });
  await ui.clickInDialog(page, '重切');
  await page.waitFor(`!${dialogOpen} && ${toastHas('已重切')}`, '整来源重切完成', 20_000);
  await page.waitFor(atRevision(before + 1), '整来源一条历史', 10_000);
  const perLetter = (await page.eval(chipTexts)).length;
  await undo(page);
  await page.waitFor(`${atRevision(before + 2)} && ${chipTexts}.length === ${chipsBefore}`, '撤销整来源重切', 10_000);
  check(perLetter > chipsBefore, `按规则重切整个来源（逐字）：第 3 行 ${chipsBefore} → ${perLetter} 个单元，一次撤销整体还原`);
  await selectRow(page, 1, '朝の光浴びて');
  await selectToken(page, '朝');

  // 6d. 来源管理与翻译关联（S3b）
  const branchRows = `[...document.querySelectorAll('.sources-sheet .branch-row')]`;
  const inSheet = { scope: '@dialog' };
  const sheetField = (label) => uiJs(`return __ui.find('textbox', ${JSON.stringify(label)}, false, '@dialog')?.value ?? null;`);
  before = await revision();
  await ui.menu(page, '歌词来源：', '管理来源…', { partial: true });
  await ui.waitDialog(page, '管理来源');
  await ui.click(page, 'button', '添加翻译来源', inSheet);
  await ui.fill(page, '新来源名称', '中文', inSheet);
  await ui.fill(page, '新来源语言（可留空）', 'zh-Hans', inSheet);
  await ui.click(page, 'button', '添加', inSheet);
  await page.waitFor(`${atRevision(before + 1)} && ${branchRows}.length === 2`, '添加翻译来源', 10_000);
  await page.waitFor(`document.querySelector('.lp-empty')?.textContent.includes('「中文」还没有歌词') && ${sheetField('名称')} === '中文'`, '切到新来源并在面板里编辑它', 10_000);
  check(true, '添加翻译来源：切到新的空来源（branchResult.selectedBranchId），面板选中它');
  await ui.fill(page, '名称', '中文翻译', inSheet);
  await ui.click(page, 'button', '保存', inSheet);
  await page.waitFor(
    `${branchRows}.some((r) => r.querySelector('.branch-name')?.textContent === '中文翻译' && r.querySelector('.faint')?.textContent === '翻译 · zh-Hans · 跟随主歌词 · 0 行')`,
    '来源改名',
    10_000,
  );
  check((await revision()) === before + 2, '修改来源名称（configureBranch），列表显示角色、语言、时间策略');
  await ui.click(page, 'row', '主歌词', inSheet);
  await page.waitFor(`${sheetField('名称')} === '主歌词'`, '面板选中主歌词');
  check(
    await page.eval(
      uiJs(
        `const remove = __ui.find('button', '删除来源', false, '@dialog'); const role = __ui.find('select', '角色', false, '@dialog'); return __ui.disabled(remove) && __ui.disabled(role) && document.querySelector('.sources-sheet').textContent.includes('主歌词不能删除');`,
      ),
    ),
    '主歌词的“删除来源”和“角色”禁用，并写明原因',
  );
  await snapshot(page, 'sources');
  await ui.click(page, 'button', '关闭管理来源');
  await ui.waitDialogClosed(page, '管理来源');

  // 在翻译来源里建两行，再按顺序自动配对
  await ui.click(page, 'button', '新建一行', { scope: '.lp-empty' });
  await page.waitFor(`${lineText} === '' && ${rowCount} === 1`, '新建第一行', 10_000);
  await ui.fill(page, '正文', '沐浴着晨光', { enter: true });
  await page.waitFor(`${lineTexts}[0] === '沐浴着晨光'`, '第一行正文', 10_000);
  await ui.menu(page, '行操作', '在下方插入', { scope: '.lhead' });
  await page.waitFor(`${lineText} === '' && ${rowCount} === 2`, '插入第二行', 10_000);
  await ui.fill(page, '正文', '仰望蓝天', { enter: true });
  await page.waitFor(`${lineTexts}[1] === '仰望蓝天'`, '第二行正文', 10_000);
  before = await revision();
  await ui.menu(page, '来源操作', '关联主歌词…');
  await ui.waitDialog(page, '关联主歌词：中文翻译');
  await ui.click(page, 'button', '按顺序自动配对', { scope: '@dialog' });
  await page.waitFor(`document.querySelectorAll('.link-row.changed').length === 2`, '自动配对后两行有修改');
  await snapshot(page, 'link');
  await ui.clickInDialog(page, '应用 2 处修改');
  await page.waitFor(`!${dialogOpen} && ${toastHas('已修改 2 行关联')}`, '应用关联', 20_000);
  await page.waitFor(`${atRevision(before + 1)} && ${lineNotes}.every((t) => t.includes('跟随 第'))`, '关联刷新', 10_000);
  const notes = await page.eval(lineNotes);
  check(
    notes[0].includes('跟随 第 1 行') && notes[0].includes('主歌词 1：朝の光浴びて') && notes[1].includes('跟随 第 2 行'),
    `按顺序自动配对：两处关联一次提交（一条历史），行列表显示“跟随 第 N 行”和关联的主歌词（${notes[0]}）`,
  );
  await page.waitFor(`document.querySelector('.fs.trans')?.textContent.includes('跟随主歌词第 2 行显示')`, '行编辑区显示关联');
  check(true, '翻译行的行编辑区显示它关联的主歌词');

  // 回到主歌词：行列表、行编辑区和逐字预览都显示关联的翻译
  await ui.menu(page, '歌词来源：', '主歌词', { partial: true });
  await page.waitFor(`${lineTexts}[0] === '朝の光浴びて'`, '切回主歌词', 10_000);
  await selectRow(page, 1, '朝の光浴びて');
  await ui.click(page, 'button', '定位', { scope: '.lhead' });
  await page.waitFor(`document.querySelector('.wp-translation')?.textContent.includes('沐浴着晨光')`, '逐字预览显示翻译', 10_000);
  await page.waitFor(`${lineNotes}[0]?.includes('沐浴着晨光')`, '主歌词行下显示翻译', 10_000);
  await page.waitFor(`document.querySelector('.fs.trans')?.textContent.includes('沐浴着晨光')`, '行编辑区列出关联的翻译', 10_000);
  check(true, '主歌词行下显示关联的翻译；行编辑区列出关联的翻译；逐字预览在当前行下显示翻译');
  await snapshot(page, 'translations');

  // 删除翻译来源（确认），撤销后来源和关联都回来
  before = await revision();
  await ui.menu(page, '歌词来源：', '管理来源…', { partial: true });
  await ui.waitDialog(page, '管理来源');
  await ui.click(page, 'row', '中文翻译', inSheet);
  await page.waitFor(`${sheetField('名称')} === '中文翻译'`, '面板选中中文翻译');
  await ui.click(page, 'button', '删除来源', inSheet);
  await ui.waitDialog(page, '删除来源「中文翻译」？');
  await ui.clickInDialog(page, '删除');
  await page.waitFor(`${atRevision(before + 1)} && ${branchRows}.length === 1`, '删除来源', 10_000);
  await ui.click(page, 'button', '关闭管理来源');
  await page.waitFor(`!${dialogOpen} && !document.querySelector('.wp-translation') && !${lineNotes}[0].includes('沐浴着晨光')`, '翻译从各处消失', 10_000);
  await undo(page);
  await page.waitFor(`${atRevision(before + 2)} && ${lineNotes}[0].includes('沐浴着晨光')`, '撤销删除来源', 10_000);
  check(true, '删除翻译来源（先确认），翻译从行列表和预览消失；撤销后来源和关联恢复');

  // 6e. 添加歌词（S3d）：粘贴到新翻译来源、追加到现有来源末尾、导入文件为新来源（导入报告）、导入替换现有来源
  before = await revision();
  await ui.menu(page, '添加歌词', '粘贴文本…');
  await ui.waitDialog(page, '粘贴歌词');
  await ui.fill(page, '歌词', 'Bathing in morning light\n\n  \nLooking up at the blue sky\n', { scope: '@dialog' });
  await page.waitFor(`document.querySelector('[role=dialog] .paste-text')?.textContent.includes('将追加 2 行；空白行跳过')`, '粘贴行数预览');
  await ui.choose(page, '追加到', '新建翻译来源', { scope: '@dialog' });
  await ui.fill(page, '新来源名称', 'English', { scope: '@dialog' });
  await snapshot(page, 'paste');
  await ui.clickInDialog(page, '追加 2 行');
  await page.waitFor(`!${dialogOpen} && ${toastHas('已新建翻译来源「English」，2 行')}`, '粘贴到新翻译来源', 10_000);
  await page.waitFor(
    `${atRevision(before + 1)} && ${lineTexts}.join('|') === 'Bathing in morning light|Looking up at the blue sky' && ${lineText} === 'Bathing in morning light'`,
    '切到新翻译来源并选中第一行',
    10_000,
  );
  const afterPaste = await branchNames(page);
  check(afterPaste.length === 3 && afterPaste.includes('English'), `粘贴文本到新翻译来源（appendText newTranslation）：跳过空白行，切到新来源并选中新加入的第一行（来源：${afterPaste.join('、')}）`);
  await ui.menu(page, '添加歌词', '粘贴文本…');
  await ui.waitDialog(page, '粘贴歌词');
  await ui.fill(page, '歌词', 'Walking the road with you', { scope: '@dialog' });
  await ui.clickInDialog(page, '追加 1 行');
  await page.waitFor(`${atRevision(before + 2)} && ${lineTexts}.length === 3 && ${lineText} === 'Walking the road with you'`, '追加到现有来源末尾', 10_000);
  check(true, '粘贴文本追加到当前来源末尾（appendText existing），选中新加入的第一行');

  const lyricsCheckState = `document.querySelector('[role=dialog] .file-check [data-check="lyrics"]')?.dataset.state`;
  await ui.menu(page, '添加歌词', '导入文件…');
  await ui.waitDialog(page, '导入歌词文件');
  await ui.setFile(page, '选择歌词文件', importPath);
  await page.waitFor(`['ok', 'warn'].includes(${lyricsCheckState})`, '识别结果', 30_000);
  await ui.choose(page, '导入到', '作为新来源', { scope: '@dialog' });
  await page.waitFor(uiJs(`return __ui.find('textbox', '新来源名称', false, '@dialog')?.value === '冒烟导入';`), '新来源名称默认用文件名');
  await ui.choose(page, '拆分文本单元', '不拆分：保持文件原有粒度', { scope: '@dialog' });
  await snapshot(page, 'import');
  await ui.clickInDialog(page, '导入');
  await ui.waitDialog(page, '导入报告：冒烟导入.lrc', { timeout: 20_000 });
  await page.waitFor(`${atRevision(before + 3)} && ${lineTexts}.includes('倒序的一行')`, '切到导入的新来源', 10_000);
  const facts = await page.eval(`document.querySelector('.report-facts')?.textContent ?? ''`);
  check(facts.includes('普通 LRC') && facts.includes('源文件 7 行 → 歌词 3 行，跳过 2 行'), `导入文件为新来源（importAsset newBranch），报告显示格式与行数（${facts.slice(0, 60)}…）`);
  const issueItems = `[...document.querySelectorAll('.report-dialog .issue-item')]`;
  await page.waitFor(
    `${issueItems}.some((i) => i.textContent.includes('第 2 行') && i.classList.contains('issue-info')) && ${issueItems}.some((i) => i.textContent.includes('第 6 行') && i.classList.contains('issue-warning'))`,
    '问题清单',
  );
  await page.waitFor(
    `document.querySelector('.source-line.focused')?.dataset.line === ${issueItems}.find((i) => i.classList.contains('located')).textContent.match(/第 (\\d+) 行/)[1]`,
    '默认定位到第一条有行号的问题',
  );
  await ui.click(page, '.report-dialog .issue-item', '第 6 行', { partial: true });
  await page.waitFor(`document.querySelector('.source-line.focused')?.textContent.includes('倒序的一行') && document.querySelector('.source-line.focused')?.dataset.line === '6'`, '定位到源文件第 6 行');
  check(true, '导入报告的问题清单按源文件行号定位：点“第 6 行”的警告，源文件对照高亮第 6 行');
  await snapshot(page, 'report');
  await ui.clickInDialog(page, '关闭');
  await ui.waitDialogClosed(page, '导入报告：冒烟导入.lrc');
  check((await branchNames(page)).length === 4, '导入后共 4 个来源');
  await ui.menu(page, '添加歌词', '查看上次导入报告');
  await ui.waitDialog(page, '导入报告：冒烟导入.lrc');
  await ui.clickInDialog(page, '关闭');
  await ui.waitDialogClosed(page, '导入报告：冒烟导入.lrc');

  await ui.menu(page, '添加歌词', '导入文件…');
  await ui.waitDialog(page, '导入歌词文件');
  await ui.setFile(page, '选择歌词文件', importPath);
  await page.waitFor(`['ok', 'warn'].includes(${lyricsCheckState})`, '识别结果', 30_000);
  await page.waitFor(`document.querySelector('.import-replace')?.textContent.includes('「冒烟导入」现有的 3 行会被全部替换')`, '替换现有来源的说明');
  // 普通 LRC 本身逐字切分；“按词”把连续的中日文合成一个单元，三行都会被重切。
  await ui.choose(page, '拆分文本单元', '按词：连续中日文算一个元素', { scope: '@dialog' });
  await ui.clickInDialog(page, '导入');
  await ui.waitDialog(page, '导入报告：冒烟导入.lrc', { timeout: 20_000 });
  await page.waitFor(`${atRevision(before + 4)} && ${lineTexts}.length === 3`, '替换现有来源', 10_000);
  const replacedFacts = await page.eval(`document.querySelector('.report-facts')?.textContent ?? ''`);
  check(replacedFacts.includes('重新切分3 行'), `导入文件替换现有来源（importAsset existing），按所选规则重新切分并在报告中说明（${replacedFacts.match(/重新切分[^文]*/)?.[0] ?? '无'}）`);
  await ui.clickInDialog(page, '关闭');
  await ui.waitDialogClosed(page, '导入报告：冒烟导入.lrc');
  for (let i = 1; i <= 4; i++) {
    await undo(page);
    await page.waitFor(atRevision(before + 4 + i), `撤销第 ${i} 步添加歌词`, 10_000);
  }
  await page.waitFor(`${lineTexts}[0] === '朝の光浴びて'`, '撤销后回到主歌词', 10_000);
  check((await branchNames(page)).length === 2, '四次添加歌词各一条历史，逐步撤销后新来源消失，回到主歌词');

  // 6f. 工程设置：画布、帧率、背景色、输出时长、音频分析各一条历史；输出时长“未确定”有明确显示；逐步撤销
  // 面板是模态的：下拉框显示的是选项文字，撤销用面板标题栏里的按钮
  const settingsOpen = `!!document.querySelector('.project-settings:not([data-exiting])')`;
  const inSettings = { scope: '.project-settings' };
  const selectShown = (label) => uiJs(`return __ui.find('select', ${JSON.stringify(label)}, false, '.project-settings')?.textContent.trim();`);
  const colorShown = uiJs(`return __ui.find('textbox', '底色', false, '.project-settings')?.value;`);
  const durationText = `(document.querySelector('.project-settings .duration-value')?.textContent ?? '')`;
  const analysisText = `(document.querySelector('.project-settings .analysis-status')?.textContent ?? '')`;
  before = await revision();
  await ui.click(page, 'button', '工程设置');
  await page.waitFor(settingsOpen, '工程设置面板打开');
  await page.waitFor(`${selectShown('尺寸')} === '1920 × 1080' && ${selectShown('帧率')} === '30 fps'`, '显示当前画布与帧率', 10_000);
  const initialDuration = await page.eval(durationText);
  check(initialDuration === '未确定' && (await page.eval(`!!document.querySelector('.duration-value.undetermined')`)), '新工程的输出时长显示“未确定”，并提示导出前必须确定');
  await ui.choose(page, '尺寸', '1280 × 720', inSettings);
  await page.waitFor(`${atRevision(before + 1)} && ${selectShown('尺寸')} === '1280 × 720'`, '改画布尺寸', 10_000);
  await ui.choose(page, '帧率', '25 fps', inSettings);
  await page.waitFor(`${atRevision(before + 2)} && ${selectShown('帧率')} === '25 fps'`, '改帧率', 10_000);
  await ui.fill(page, '底色', '#102030', { ...inSettings, enter: true });
  await page.waitFor(`${atRevision(before + 3)} && ${colorShown} === '#102030' && document.querySelector('.project-settings input[type=color]').value === '#102030'`, '改背景色', 10_000);
  const noticeText = await page.eval(`document.querySelector('.status-bar .notice')?.textContent ?? ''`);
  check(!noticeText.includes('没有变化'), `画布尺寸、帧率、背景色各一条历史（composition.setCanvasSize／setFrameRate／setBackgroundColor），面板显示新值，没有重复发出的空命令（提示：${noticeText || '无'}）`);
  await ui.click(page, 'button', '按音频时长', inSettings);
  await page.waitFor(`${atRevision(before + 4)} && ${durationText} === '0:12.000'`, '按音频时长', 10_000);
  await ui.fill(page, '自定义时长', '10.5', { ...inSettings, enter: true });
  await page.waitFor(`${atRevision(before + 5)} && ${durationText} === '0:10.500'`, '自定义输出时长', 10_000);
  await ui.click(page, 'button', '设为未确定', inSettings);
  await page.waitFor(`${atRevision(before + 6)} && ${durationText} === '未确定'`, '设为未确定', 10_000);
  check(true, '输出时长：按音频时长（0:12.000）→ 自定义 10.5 秒 → 设为未确定，各一条历史');
  await ui.click(page, 'button', '分析并采用', inSettings);
  await page.waitFor(`${atRevision(before + 7)} && ${analysisText}.startsWith('已采用')`, '分析并采用', 90_000);
  check(true, `音频分析：建任务、等成功、audio.analysis.apply 采用（${await page.eval(analysisText)}）`);
  await snapshot(page, 'settings');
  await ui.click(page, 'button', '清除', inSettings);
  await page.waitFor(`${atRevision(before + 8)} && ${analysisText} === '未分析'`, '清除音频分析', 10_000);
  for (let i = 1; i <= 8; i++) {
    await ui.click(page, 'button', '撤销', inSettings);
    await page.waitFor(atRevision(before + 8 + i), `撤销第 ${i} 步设置`, 10_000);
  }
  await page.waitFor(
    `${selectShown('尺寸')} === '1920 × 1080' && ${selectShown('帧率')} === '30 fps' && ${colorShown} !== '#102030' && ${durationText} === '未确定' && ${analysisText} === '未分析'`,
    '撤销后设置回到原值',
    10_000,
  );
  check(true, '清除音频分析；八步设置在面板里逐步撤销后，画布、帧率、背景色、输出时长和音频分析都回到原值');
  await ui.click(page, 'button', '关闭工程设置');
  await page.waitFor(`!document.querySelector('.project-settings')`, '关闭工程设置');

  // 6g. 界面主题（界面改版 §2.8）：应用设置里切深色、换主题色，立即生效；刷新后偏好还在
  const themeFacts = `JSON.stringify({ mode: document.documentElement.dataset.mode, primary: getComputedStyle(document.documentElement).getPropertyValue('--primary').trim() })`;
  const lightTheme = JSON.parse(await page.eval(themeFacts));
  await ui.click(page, 'button', '应用设置');
  await ui.click(page, 'button', '深色');
  await ui.fill(page, '当前', '#7A5AF8', { enter: true });
  await page.waitFor(`(() => { const t = ${themeFacts}; return JSON.parse(t).mode === 'dark' && JSON.parse(t).primary !== ${JSON.stringify(lightTheme.primary)}; })()`, '深色与新主题色生效', 10_000);
  const darkTheme = JSON.parse(await page.eval(themeFacts));
  check(true, `应用设置：切深色、主题色改为 #7A5AF8，立即生效（主色 ${lightTheme.primary} → ${darkTheme.primary}）`);
  await page.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
  await page.send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
  await page.waitFor(`!document.querySelector('[role=dialog]')`, '关闭应用设置');

  // 7. 刷新页面：经 resume 接回同一 client，首页列出已打开的工程，点击后回到同一修订
  const revisionBeforeReload = await revision();
  await page.navigate(`${ORIGIN}/`);
  await page.waitFor(shell.connected, '刷新后重新连接');
  const reloadedTheme = JSON.parse(await page.eval(themeFacts));
  check(
    reloadedTheme.mode === 'dark' && reloadedTheme.primary === darkTheme.primary,
    `刷新后主题偏好还在（${reloadedTheme.mode}，主色 ${reloadedTheme.primary}）`,
  );
  const sessionRow = `[...document.querySelectorAll('.home .session-row')].find((c) => c.querySelector('.session-name')?.textContent.includes(${JSON.stringify(directory)}))`;
  await page.waitFor(`!!(${sessionRow})`, '首页“正在编辑”列出已打开的工程', 30_000);
  const sessionScope = `.session-row[data-session-id="${await page.eval(`(${sessionRow}).dataset.sessionId`)}"]`;
  await ui.click(page, 'button', '接回', { scope: sessionScope });
  await page.waitFor(`${atRevision(revisionBeforeReload)} && ${rowCount} === 4 && ${chipTexts}.length > 0`, '接回工程', 60_000);
  check(true, `刷新页面后从首页接回同一工程（修订 ${revisionBeforeReload}），不重新打开`);
  await selectToken(page, '朝');

  // 8. 保存到新目录
  await ui.click(page, 'button', '保存工程');
  await ui.waitDialog(page, '保存到');
  // 列表加载完：有目录行，或者工程目录还是空的（新 profile）。
  await page.waitFor(`!!document.querySelector('.save-browser :is(.directory-row, .directory-empty)')`, '“保存到…”对话框列出工程目录', 30_000);
  await ui.fill(page, '目录名', directory);
  await page.waitFor(`document.querySelector('.save-target')?.dataset.kind === 'new'`, '目标写明“会新建目录”');
  await ui.clickInDialog(page, '保存');
  await page.waitFor(`document.querySelector('.project-state')?.textContent === '已保存'`, '保存完成', 60_000);
  check(true, `保存到授权目录下的新目录 ${directory}（job 成功终态）`);

  // 9. 关闭并从工程目录重开
  await shell.projectMenu(page, '关闭工程');
  await page.waitFor(`!!document.querySelector('.home')`, '回到首页', 60_000);
  check(true, '关闭工程回到首页');
  const directoryRow = `document.querySelector('.directory-row[data-path=${JSON.stringify(directory)}] .directory-main')`;
  await page.waitFor(`!!(${directoryRow}) && !(${directoryRow}).disabled`, '工程目录列出刚保存的工程', 30_000);
  // 单击只选中，双击打开
  await ui.click(page, 'button', directory, { scope: `.directory-row[data-path=${JSON.stringify(directory)}]`, partial: true, double: true });
  await page.waitFor(`${rowCount} === 4 && ${chipTexts}.length > 0`, '重开后显示 4 行', 120_000);
  await selectToken(page, '朝');
  check(
    (await page.eval(startTime)) === savedAsaStart && (await page.eval(endTime)) === '0:01.100' && (await page.eval(`document.querySelector('.project-state')?.textContent`)) === '已保存',
    `从工程目录重开：时间与保存状态一致（「朝」${savedAsaStart}–0:01.100）`,
  );

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
    const failure = join(tmpdir(), 'mako-editor-smoke-failure.png');
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
