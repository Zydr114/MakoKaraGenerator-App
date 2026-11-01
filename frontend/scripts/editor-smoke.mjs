// 编辑器浏览器冒烟（W1 设计 §6）：用真实 Chrome 走一遍编辑器主链路。
// 新建（上传生成的 WAV＋LRC）→ 选元素改时间（逐字预览跟随）→ 撤销／重做 → 播放中按 J 打点 → 微调／清除
// → 整行平移、定位到此行 → 时间轴波形读自后端（G11）、拖动边界（含相邻元素共用边界）→ 注音、正文、行插入／复制／批量删除
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

/** 页面里常用的查询，作为表达式前缀注入。 */
const PRELUDE = `
  const buttons = () => [...document.querySelectorAll('mdui-button, mdui-button-icon')];
  const button = (text) => buttons().find((b) => b.textContent.trim() === text || b.getAttribute('aria-label') === text);
  const field = (label) => [...document.querySelectorAll('mdui-text-field')].find((f) => f.label === label);
  const revision = () => (document.querySelector('.status-bar')?.textContent.match(/修订 (\\d+)/) ?? [])[1];
`;
const js = (body) => `(() => { ${PRELUDE} ${body} })()`;

async function clickButton(page, text) {
  await page.waitFor(js(`const b = button(${JSON.stringify(text)}); return !!b && !b.disabled;`), `按钮「${text}」可用`);
  await page.eval(js(`button(${JSON.stringify(text)}).click();`));
}

/** 打开 aria-label 为 trigger 的下拉菜单，等这个菜单打开后点其中文字为 text 的菜单项。 */
async function clickMenuItem(page, trigger, text) {
  await clickButton(page, trigger);
  const item = js(
    `const d = button(${JSON.stringify(trigger)})?.closest('mdui-dropdown'); return d?.open ? [...d.querySelectorAll('mdui-menu-item')].find((i) => i.textContent.trim() === ${JSON.stringify(text)} && !i.disabled) : undefined;`,
  );
  await page.waitFor(`!!${item}`, `菜单项「${text}」可用`);
  // mdui 组件覆盖了 click()，转给内部元素时不一定冒泡到 React；派发一个真实的点击事件。
  await page.eval(`${item}.dispatchEvent(new MouseEvent('click', { bubbles: true, composed: true }))`);
}

/** 点 scope 容器里文字为 text 的按钮（页面上有同名按钮时用）。 */
async function clickButtonIn(page, scope, text) {
  const target = `[...document.querySelectorAll(${JSON.stringify(`${scope} mdui-button`)})].find((b) => b.textContent.trim() === ${JSON.stringify(text)})`;
  await page.waitFor(`!!(${target}) && !(${target}).disabled`, `「${text}」可用`);
  await page.eval(`(${target}).click()`);
}

/** 点当前打开的对话框里文字为 text 的按钮。 */
async function clickDialogButton(page, text) {
  const target = `[...document.querySelectorAll('mdui-dialog')].find((d) => d.open)?.querySelector('mdui-button:not([disabled])') && [...[...document.querySelectorAll('mdui-dialog')].find((d) => d.open).querySelectorAll('mdui-button')].find((b) => b.textContent.trim() === ${JSON.stringify(text)} && !b.disabled)`;
  await page.waitFor(`!!(${target})`, `对话框按钮「${text}」可用`);
  await page.eval(`(${target}).dispatchEvent(new MouseEvent('click', { bubbles: true, composed: true }))`);
}

async function typeInto(page, label, value) {
  await page.waitFor(js(`return !!field(${JSON.stringify(label)});`), `输入框「${label}」`);
  await page.eval(
    js(`const f = field(${JSON.stringify(label)}); f.value = ${JSON.stringify(value)}; f.dispatchEvent(new Event('input', { bubbles: true, composed: true }));`),
  );
}

async function pressEnter(page, label) {
  await page.eval(js(`field(${JSON.stringify(label)}).dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, composed: true }));`));
}

/** 选中当前行的第 index 个元素；已选中时不再点（再点一次会回到整行）。 */
async function selectToken(page, index, text) {
  const title = `document.querySelector('.time-editor-title')?.textContent.includes('所选「${text}」')`;
  if (!(await page.eval(title))) await page.eval(`document.querySelectorAll('.token-chip')[${index}].click()`);
  await page.waitFor(title, `选中元素「${text}」`);
}

async function key(page, type, keyName, code, keyCode) {
  await page.send('Input.dispatchKeyEvent', { type, key: keyName, code, windowsVirtualKeyCode: keyCode, nativeVirtualKeyCode: keyCode });
}

const playheadText = `(document.querySelector('.transport-time span')?.textContent ?? '')`;
const tokenStatuses = js(`return [...document.querySelectorAll('.token-chip')].map((c) => [...c.classList].find((k) => k.startsWith('status-')));`);

/** 中间截图：等对话框、侧边面板的动画结束再截。 */
async function snapshot(page, suffix) {
  if (!values.screenshot) return;
  await new Promise((resolve) => setTimeout(resolve, 700));
  await writeFile(values.screenshot.replace(/\.png$/, `-${suffix}.png`), await page.screenshot());
}

async function setFile(page, ariaLabel, path) {
  const { root } = await page.send('DOM.getDocument', { depth: -1 });
  const { nodeId } = await page.send('DOM.querySelector', { nodeId: root.nodeId, selector: `input[type=file][aria-label="${ariaLabel}"]` });
  await page.send('DOM.setFileInputFiles', { nodeId, files: [path] });
}

/** 把标签为 label 的 mdui-select 改成 value，像用户选择一样发出 change。 */
async function chooseOption(page, label, value) {
  await page.eval(`(() => {
    const sel = [...document.querySelectorAll('mdui-select')].find((s) => s.label === ${JSON.stringify(label)});
    // 改 value 后内部 mdui-menu 会发一次冒泡的 change，和用户点选一样；不要再手动派发，否则命令会发两次。
    sel.value = ${JSON.stringify(value)};
  })()`);
}

const fieldValue = (label) => js(`return field(${JSON.stringify(label)})?.value ?? null;`);

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
  await page.waitFor(`document.querySelector('.connection')?.textContent === '已连接'`, '连接 Server');
  check(true, '编辑器页面经 bootstrap 路由连上 Server');

  // 1. 新建
  await clickButton(page, '新建工程');
  await page.waitFor(`!!document.querySelector('mdui-dialog.new-project-dialog[open] input[aria-label="选择歌词"]')`, '新建工程对话框');
  await setFile(page, '选择音频', wavPath);
  await setFile(page, '选择歌词', lrcPath);
  await page.waitFor(`!!document.querySelector('.lyrics-plan-summary')`, '歌词识别结果');
  await page.waitFor(`document.body.innerText.includes('冒烟音频.wav') && document.body.innerText.includes('冒烟歌词.lrc')`, '文件已选择');
  await typeInto(page, '工程名', `冒烟 ${directory}`);
  await clickButton(page, '下一步');
  await clickButton(page, '新建');
  await page.waitFor(`document.querySelectorAll('.line-list mdui-list-item').length === 4`, '工作区显示 4 行', 120_000);
  check(true, '上传中文文件名的 WAV 与 LRC，新建工程并进入工作区（4 行）');
  check(await page.eval(js(`return revision() === '0';`)), '新建后修订 0');
  const chips = await page.eval(`[...document.querySelectorAll('.token-chip .token-text')].map((n) => n.textContent)`);
  check(chips.join('') === '朝の光浴びて' && chips.length === 6, `第一行按“混合”规则逐字拆成 6 个元素（${chips.join('|')}）`);

  // 逐字预览：播放头在 0 秒，第一句（0.5 秒起）尚未开始
  const previewText = (role) => `document.querySelector('.wp-${role} .wp-text')?.textContent`;
  await page.waitFor(`${previewText('upcoming')} === '朝の光浴びて'`, '逐字预览显示即将开始的第一句', 30_000);
  check(true, '逐字预览：播放头在第一句之前时显示即将开始的第一句');

  // 2. 选第一个元素并输入时间
  await page.eval(`document.querySelector('.token-chip').click()`);
  await page.waitFor(`document.querySelector('.time-editor-title')?.textContent.includes('所选「朝」')`, '选中元素「朝」');
  await typeInto(page, '起点', '0:00.600');
  await typeInto(page, '终点', '0:00.900');
  await pressEnter(page, '终点');
  await page.waitFor(js(`return revision() === '1';`), '命令后修订 1');
  check((await page.eval(fieldValue('起点'))) === '0:00.600' && (await page.eval(fieldValue('终点'))) === '0:00.900', '回车提交 setTimeRange：读模型刷新后元素时间为 0.600–0.900');
  await page.waitFor(`!!document.querySelector('.wp-upcoming .wp-seg[data-s="600000"][data-e="900000"]')`, '逐字预览跟随编辑', 10_000);
  check(true, '逐字预览跟随编辑：「朝」的填充区间变为 0.600–0.900');
  if (values.screenshot) await writeFile(values.screenshot.replace(/\.png$/, '-preview.png'), await page.screenshot());

  // 3. 撤销／重做
  await clickButton(page, '撤销');
  await page.waitFor(js(`return revision() === '2';`), '撤销后修订 2');
  check((await page.eval(fieldValue('起点'))) === '', '撤销后元素回到未打轴');
  await clickButton(page, '重做');
  await page.waitFor(js(`return revision() === '3';`), '重做后修订 3');
  check((await page.eval(fieldValue('起点'))) === '0:00.600', '重做后元素时间恢复');

  // 4. 播放中打点：第 2 行（3.0–6.0 秒）的前两个元素，按住 J 记起点、松开记终点
  await page.eval(`document.querySelectorAll('.line-list mdui-list-item')[1].click()`);
  await page.waitFor(js(`return field('正文')?.value === '青い空見上げ';`), '选中第 2 行');
  await page.waitFor(`document.querySelector('.transport mdui-button-icon') && !document.querySelector('.transport mdui-button-icon').disabled`, '音频就绪', 60_000);
  check(true, '工程音频经鉴权读取后可以播放');
  await page.eval(`[...document.querySelectorAll('.switch-field')].find((l) => l.textContent.includes('打点模式')).querySelector('mdui-switch').click()`);
  await page.waitFor(`document.querySelector('.transport-help')?.textContent.includes('按住 J')`, '打点模式开启');
  const revisionBefore = Number(await page.eval(js(`return revision();`)));
  await key(page, 'keyDown', ' ', 'Space', 32);
  await key(page, 'keyUp', ' ', 'Space', 32);
  await page.waitFor(`${playheadText} >= '0:03.200'`, '播放到 3.2 秒', 20_000);
  for (let i = 0; i < 2; i++) {
    await key(page, 'keyDown', 'j', 'KeyJ', 74);
    await new Promise((r) => setTimeout(r, 250));
    await key(page, 'keyUp', 'j', 'KeyJ', 74);
    await new Promise((r) => setTimeout(r, 100));
  }
  await key(page, 'keyDown', ' ', 'Space', 32);
  await key(page, 'keyUp', ' ', 'Space', 32);
  await page.waitFor(js(`return Number(revision()) === ${revisionBefore + 4} && !document.querySelector('.status-bar')?.textContent.includes('同步中');`), '四条打点命令完成', 20_000);
  if (values.screenshot) await writeFile(values.screenshot.replace(/\.png$/, '-stamped.png'), await page.screenshot());
  const fills = await page.eval(`[...document.querySelectorAll('.wp-current .wp-seg')].map((s) => s.style.getPropertyValue('--fill'))`);
  check(
    (await page.eval(previewText('current'))) === '青い空見上げ' && fills[0] === '1' && fills[1] === '1' && fills[2] === '0',
    `暂停后逐字预览：当前行「青い空見上げ」，已打点的两个元素填满、未打轴的「空」未填（${fills.join(' ')}）`,
  );
  const statuses = await page.eval(tokenStatuses);
  check(statuses[0] === 'status-edited' && statuses[1] === 'status-edited' && statuses[2] === 'status-missing', `播放中打点两次：前两个元素已修改、第三个未打轴（${statuses.slice(0, 3).join(' ')}）`);
  check(await page.eval(`document.querySelector('.time-editor-title')?.textContent.includes('所选「空」')`), '每次松开 J 自动前进：打完两个元素后选中第三个「空」');
  await selectToken(page, 0, '青');
  const stampStart = await page.eval(fieldValue('起点'));
  check(stampStart >= '0:03.200' && stampStart < '0:04.500', `打点时间来自按键当时的播放头（「青」起点 ${stampStart}）`);

  // 5. 按帧微调与清除
  const beforeNudge = Number(await page.eval(js(`return revision();`)));
  await clickButton(page, '+1 帧');
  await page.waitFor(js(`return Number(revision()) === ${beforeNudge + 1};`), '微调完成');
  check((await page.eval(fieldValue('起点'))) !== stampStart, `按帧微调：起点 ${stampStart} → ${await page.eval(fieldValue('起点'))}`);
  await selectToken(page, 1, 'い');
  await clickButton(page, '清除时间');
  await page.waitFor(js(`return Number(revision()) === ${beforeNudge + 2};`), '清除完成');
  check((await page.eval(fieldValue('起点'))) === '', '清除元素时间后回到未打轴');

  // 6. 整行平移与时间轴拖动：第 1 行
  await page.eval(`document.querySelectorAll('.line-list mdui-list-item')[0].click()`);
  await page.waitFor(js(`return field('正文')?.value === '朝の光浴びて';`), '选中第 1 行');
  const beforeShift = Number(await page.eval(js(`return revision();`)));
  await typeInto(page, '整行平移（毫秒）', '-100');
  await clickButton(page, '平移');
  await page.waitFor(js(`return Number(revision()) === ${beforeShift + 1};`), '平移完成');
  check(
    await page.eval(`document.querySelector('.line-detail-header')?.textContent.includes('0:00.400 – 0:02.900')`),
    '整行平移 −100 毫秒：行 0.4–2.9，已定时元素一起移动',
  );
  await clickButton(page, '定位到此行');
  await page.waitFor(`${previewText('current')} === '朝の光浴びて' && document.querySelector('.word-preview .pane-title-text')?.textContent.includes('0:00.400')`, '定位到第 1 行', 10_000);
  check(true, '定位到此行：播放头到行起点 0:00.400，逐字预览显示该行为当前行');

  await clickButton(page, '全曲');
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
  const beforeDrag = Number(await page.eval(js(`return revision();`)));
  await dragTimeline(800_000, 1_100_000);
  await page.waitFor(js(`return Number(revision()) === ${beforeDrag + 1};`), '拖动终点提交');
  await selectToken(page, 0, '朝');
  await page.waitFor(js(`return field('终点')?.value === '0:01.100';`), '「朝」终点 1.1', 10_000).catch(() => undefined);
  check((await page.eval(fieldValue('终点'))) === '0:01.100', `拖动元素终点：「朝」0.5–${await page.eval(fieldValue('终点'))}`);

  // 「の」接在后面：1.1–1.4；再把共用边界 1.1 拖到 1.0，两边一次提交。
  await selectToken(page, 1, 'の');
  await typeInto(page, '起点', '0:01.100');
  await typeInto(page, '终点', '0:01.400');
  await pressEnter(page, '终点');
  await page.waitFor(js(`return Number(revision()) === ${beforeDrag + 2};`), '「の」设时间');
  await dragTimeline(1_100_000, 1_000_000);
  await page.waitFor(js(`return Number(revision()) === ${beforeDrag + 3} && !document.querySelector('.status-bar')?.textContent.includes('同步中');`), '拖动共用边界提交');
  // 拖动开始时会选中被拖的元素（这里是「朝」），分别选中两边读时间。
  await selectToken(page, 1, 'の');
  const noStart = await page.eval(fieldValue('起点'));
  await selectToken(page, 0, '朝');
  const asaEnd = await page.eval(fieldValue('终点'));
  check(asaEnd === '0:01.000' && noStart === '0:01.000', `拖动相邻元素的共用边界：一次命令同时改「朝」终点（${asaEnd}）与「の」起点（${noStart}）`);
  await clickButton(page, '撤销');
  await page.waitFor(js(`return Number(revision()) === ${beforeDrag + 4};`), '撤销拖动');
  await page.waitFor(js(`return field('终点')?.value === '0:01.100';`), '撤销后「朝」终点 1.1', 10_000).catch(() => undefined);
  check((await page.eval(fieldValue('终点'))) === '0:01.100', '一次撤销还原共用边界两侧');
  const savedAsaStart = await page.eval(fieldValue('起点'));

  // 6b. 注音、正文、行操作（S3a）
  const lineTexts = `[...document.querySelectorAll('.line-list .line-text')].map((n) => n.textContent)`;
  const toastShown = (text) => `[...document.querySelectorAll('mdui-snackbar')].some((s) => s.open && s.textContent.includes(${JSON.stringify(text)}))`;
  let before = Number(await page.eval(js(`return revision();`)));
  await typeInto(page, '整段注音', 'あさ');
  await pressEnter(page, '整段注音');
  await page.waitFor(`document.querySelector('.token-chip .token-ruby')?.textContent === 'あさ'`, '注音显示在「朝」上方', 10_000);
  check(Number(await page.eval(js(`return revision();`))) === before + 1, '注音：setRubyText 后「朝」上方显示「あさ」');

  await page.eval(`document.querySelectorAll('.line-list mdui-list-item')[3].click()`);
  await page.waitFor(js(`return field('正文')?.value === '你好，世界';`), '选中第 4 行');
  before = Number(await page.eval(js(`return revision();`)));
  await typeInto(page, '正文', '你好，新世界');
  await pressEnter(page, '正文');
  await page.waitFor(`${toastShown('正文已更新')}`, '正文更新提示', 10_000);
  check(
    (await page.eval(lineTexts))[3] === '你好，新世界' && (await page.eval(`[...document.querySelectorAll('mdui-snackbar')].find((s) => s.open)?.textContent`)).includes('保留'),
    '正文：Enter 提交 setLineText，提示保留／重新拆分的文本单元数',
  );
  await clickButton(page, '撤销');
  await page.waitFor(js(`return Number(revision()) === ${before + 2} && field('正文')?.value === '你好，世界';`), '撤销正文', 10_000);
  check(true, '撤销正文修改');

  await clickMenuItem(page, '第 4 行操作', '在下方插入');
  await page.waitFor(`${lineTexts}.length === 5`, '插入后 5 行', 10_000);
  await page.waitFor(js(`return field('正文')?.value === '' && document.querySelectorAll('.line-list mdui-list-item')[4].active;`), '选中新插入的空行', 10_000);
  check(true, '在下方插入：新空行出现在第 5 行并被选中（feedback.selection）');
  await clickMenuItem(page, '第 1 行操作', '复制本行');
  await page.waitFor(`${lineTexts}.length === 6`, '复制后 6 行', 10_000);
  check(JSON.stringify((await page.eval(lineTexts)).slice(0, 2)) === JSON.stringify(['朝の光浴びて', '朝の光浴びて']), '复制本行：副本紧接原行');

  // Ctrl 点击加选副本和空行，批量删除（一次 project.applyEdits，确认框，一条历史）
  before = Number(await page.eval(js(`return revision();`)));
  for (const index of [1, 5]) {
    await page.eval(`document.querySelectorAll('.line-list mdui-list-item')[${index}].dispatchEvent(new MouseEvent('click', { bubbles: true, ctrlKey: true }))`);
  }
  await page.waitFor(`document.querySelector('.bulk-bar')?.textContent.includes('已选 2 行')`, '多选 2 行');
  await clickButton(page, '删除');
  await page.waitFor(`[...document.querySelectorAll('mdui-dialog')].some((d) => d.open && d.headline === '删除 2 行？')`, '确认框');
  await page.eval(`[...document.querySelectorAll('mdui-dialog')].find((d) => d.open).querySelectorAll('mdui-button')[1].click()`);
  await page.waitFor(js(`return Number(revision()) === ${before + 1} && document.querySelectorAll('.line-list mdui-list-item').length === 4;`), '批量删除完成', 20_000);
  check(
    JSON.stringify(await page.eval(lineTexts)) === JSON.stringify(['朝の光浴びて', '青い空見上げ', 'Hello karaoke', '你好，世界']),
    'Ctrl 加选两行后批量删除：确认后一次删除（一条历史），回到原来 4 行',
  );
  await clickButton(page, '撤销');
  await page.waitFor(js(`return Number(revision()) === ${before + 2} && document.querySelectorAll('.line-list mdui-list-item').length === 6;`), '撤销批量删除', 10_000);
  await clickButton(page, '重做');
  await page.waitFor(js(`return Number(revision()) === ${before + 3} && document.querySelectorAll('.line-list mdui-list-item').length === 4;`), '重做批量删除', 10_000);
  check(true, '批量删除一次撤销两行都回来，重做再次删除');

  // 6c. 文本单元结构（S3c）：第 3 行「Hello karaoke」
  const chipTexts = `[...document.querySelectorAll('.token-chip .token-text')].map((n) => n.textContent)`;
  const dialogClosed = `![...document.querySelectorAll('mdui-dialog')].some((d) => d.open)`;
  await page.eval(`document.querySelectorAll('.line-list mdui-list-item')[2].click()`);
  await page.waitFor(js(`return field('正文')?.value === 'Hello karaoke';`), '选中第 3 行');
  await clickButton(page, '按规则重切本行…');
  await page.waitFor(js(`return field('切分')?.value === 'Hello |karaoke';`), '重切对话框带出当前切分', 10_000);
  await typeInto(page, '切分', 'Hel|lo |karaoke');
  await page.waitFor(`[...document.querySelectorAll('mdui-dialog')].find((d) => d.open)?.querySelectorAll('.segment-chip').length === 3`, '切分预览 3 段');
  await clickDialogButton(page, '重切');
  await page.waitFor(`${dialogClosed} && ${chipTexts}.length === 3 && ${toastShown('已重切本行')}`, '重切本行完成', 10_000);
  check(true, `按规则重切本行：用“|”切成 3 段（${(await page.eval(chipTexts)).join('|')}），对话框预览切分结果`);

  await selectToken(page, 0, 'Hel');
  before = Number(await page.eval(js(`return revision();`)));
  await clickButton(page, '与后一个合并');
  await page.waitFor(js(`return Number(revision()) === ${before + 1};`) + ` && ${chipTexts}.length === 2`, '合并完成', 10_000);
  await page.waitFor(`document.querySelector('.time-editor-title')?.textContent.includes('所选「Hello')`, '合并后选中合并结果', 10_000);
  check(true, '与后一个合并：「Hel」+「lo」，选中合并后的单元（tokenResult.selection）');

  await typeInto(page, '起点', '0:06.200');
  await typeInto(page, '终点', '0:06.800');
  await pressEnter(page, '终点');
  await page.waitFor(js(`return Number(revision()) === ${before + 2};`), '给 Hello 打上时间', 10_000);
  await clickButton(page, '拆开');
  await page.waitFor(`${chipTexts}.length === 3`, '拆开完成', 10_000);
  const splitChips = await page.eval(chipTexts);
  // 合并结果是「Hello 」（含尾随空格，6 个文本元素），对半拆成「Hel」「lo 」。
  check(splitChips[0] === 'Hel' && splitChips[1] === 'lo ', `拆开已打轴的单元：按文本元素数对半（${splitChips.join('|')}）`);

  // 把最后一个单元拖到最前，选“时间槽跟着新顺序重排”
  before = Number(await page.eval(js(`return revision();`)));
  await page.eval(`(() => {
    const chips = document.querySelectorAll('.token-chip');
    const dt = new DataTransfer();
    const fire = (el, type, x) => el.dispatchEvent(new DragEvent(type, { bubbles: true, cancelable: true, dataTransfer: dt, clientX: x, clientY: el.getBoundingClientRect().top + 5 }));
    const first = chips[0].getBoundingClientRect();
    fire(chips[2], 'dragstart', 0);
    fire(chips[0], 'dragover', first.left + 2);
    fire(chips[0], 'drop', first.left + 2);
    fire(chips[2], 'dragend', 0);
  })()`);
  await clickDialogButton(page, '时间槽跟着新顺序重排');
  await page.waitFor(js(`return Number(revision()) === ${before + 1};`), '重排完成', 10_000);
  await page.waitFor(`${chipTexts}[0] === 'karaoke'`, '重排后顺序', 10_000);
  check(true, `拖动排序：「karaoke」移到最前（${(await page.eval(chipTexts)).join('|')}），时间槽跟着新顺序重排`);

  // 整个来源按“逐字”重切，一次撤销整体还原
  const chipsBefore = (await page.eval(chipTexts)).length;
  before = Number(await page.eval(js(`return revision();`)));
  await clickMenuItem(page, '来源操作', '按规则重切整个来源…');
  await page.waitFor(`[...document.querySelectorAll('mdui-dialog')].some((d) => d.open && d.headline === '按规则重切整个来源')`, '整来源重切对话框');
  await page.eval(`(() => {
    const sel = [...document.querySelectorAll('mdui-dialog')].find((d) => d.open).querySelector('mdui-select');
    sel.value = 'perTextElement';
    sel.dispatchEvent(new Event('change', { bubbles: true }));
  })()`);
  await clickDialogButton(page, '重切');
  await page.waitFor(`${dialogClosed} && ${toastShown('已重切')}`, '整来源重切完成', 20_000);
  await page.waitFor(js(`return Number(revision()) === ${before + 1};`), '整来源一条历史', 10_000);
  const perLetter = (await page.eval(chipTexts)).length;
  await clickButton(page, '撤销');
  await page.waitFor(js(`return Number(revision()) === ${before + 2};`) + ` && ${chipTexts}.length === ${chipsBefore}`, '撤销整来源重切', 10_000);
  check(perLetter > chipsBefore, `按规则重切整个来源（逐字）：第 3 行 ${chipsBefore} → ${perLetter} 个单元，一次撤销整体还原`);
  await page.eval(`document.querySelectorAll('.line-list mdui-list-item')[0].click()`);
  await selectToken(page, 0, '朝');

  // 6d. 来源管理与翻译关联（S3b）
  const drawerOpen = `document.querySelector('.sources-drawer')?.open === true`;
  const branchItems = `[...document.querySelectorAll('.sources-drawer .branch-list mdui-list-item')]`;
  const lineMetas = `[...document.querySelectorAll('.line-list .line-meta')].map((n) => n.textContent)`;
  before = Number(await page.eval(js(`return revision();`)));
  await clickButton(page, '管理来源');
  await page.waitFor(drawerOpen, '管理来源面板打开');
  await clickButton(page, '添加翻译来源');
  await typeInto(page, '新来源名称', '中文');
  await typeInto(page, '新来源语言（可留空）', 'zh-Hans');
  await clickButton(page, '添加');
  await page.waitFor(js(`return Number(revision()) === ${before + 1};`) + ` && ${branchItems}.length === 2`, '添加翻译来源', 10_000);
  await page.waitFor(
    `document.querySelector('.empty-state')?.textContent.includes('还没有歌词') && ${js(`return field('名称')?.value === '中文';`)}`,
    '切到新来源并在面板里编辑它',
    10_000,
  );
  check(true, '添加翻译来源：切到新的空来源（branchResult.selectedBranchId），面板选中它');
  await typeInto(page, '名称', '中文翻译');
  await clickButton(page, '保存');
  await page.waitFor(
    `${branchItems}.some((i) => i.querySelector('span')?.textContent === '中文翻译' && i.querySelector('[slot=description]')?.textContent === '翻译 · zh-Hans · 跟随主歌词 · 0 行')`,
    '来源改名',
    10_000,
  );
  check(Number(await page.eval(js(`return revision();`))) === before + 2, '修改来源名称（configureBranch），列表显示角色、语言、时间策略');
  await page.eval(`${branchItems}[0].click()`);
  await page.waitFor(js(`return field('名称')?.value === '主歌词';`), '面板选中主歌词');
  check(
    await page.eval(
      js(
        `const remove = button('删除来源'); const role = [...document.querySelectorAll('.sources-drawer mdui-select')].find((s) => s.label === '角色'); return remove.disabled && role.disabled && document.querySelector('.sources-drawer').textContent.includes('主歌词不能删除');`,
      ),
    ),
    '主歌词的“删除来源”和“角色”禁用，并写明原因',
  );
  await snapshot(page, 'sources');
  await clickButton(page, '关闭管理来源');
  await page.waitFor(`!(${drawerOpen})`, '关闭管理来源面板');

  // 在翻译来源里建两行，再按顺序自动配对
  await clickButton(page, '新建一行');
  await page.waitFor(js(`return field('正文')?.value === '' && document.querySelectorAll('.line-list mdui-list-item').length === 1;`), '新建第一行', 10_000);
  await typeInto(page, '正文', '沐浴着晨光');
  await pressEnter(page, '正文');
  await page.waitFor(`${lineTexts}[0] === '沐浴着晨光'`, '第一行正文', 10_000);
  await clickMenuItem(page, '第 1 行操作', '在下方插入');
  await page.waitFor(js(`return field('正文')?.value === '' && document.querySelectorAll('.line-list mdui-list-item').length === 2;`), '插入第二行', 10_000);
  await typeInto(page, '正文', '仰望蓝天');
  await pressEnter(page, '正文');
  await page.waitFor(`${lineTexts}[1] === '仰望蓝天'`, '第二行正文', 10_000);
  before = Number(await page.eval(js(`return revision();`)));
  await clickMenuItem(page, '来源操作', '关联主歌词…');
  await page.waitFor(`[...document.querySelectorAll('mdui-dialog')].some((d) => d.open && d.headline === '关联主歌词：中文翻译')`, '翻译关联对话框');
  await clickDialogButton(page, '按顺序自动配对');
  await page.waitFor(`document.querySelectorAll('.link-row.changed').length === 2`, '自动配对后两行有修改');
  await snapshot(page, 'link');
  await clickDialogButton(page, '应用 2 处修改');
  await page.waitFor(`${dialogClosed} && ${toastShown('已修改 2 行关联')}`, '应用关联', 20_000);
  await page.waitFor(js(`return Number(revision()) === ${before + 1};`) + ` && ${lineMetas}.every((t) => t.includes('跟随 第'))`, '关联刷新', 10_000);
  const metas = await page.eval(lineMetas);
  check(
    metas[0].includes('跟随 第 1 行') && metas[0].includes('主歌词 1：朝の光浴びて') && metas[1].includes('跟随 第 2 行'),
    `按顺序自动配对：两处关联一次提交（一条历史），行列表显示“跟随 第 N 行”和关联的主歌词（${metas[0]}）`,
  );
  await page.waitFor(`document.querySelector('.translation-section')?.textContent.includes('跟随主歌词第 2 行显示')`, '行编辑区显示关联');
  check(true, '翻译行的行编辑区显示它关联的主歌词');

  // 回到主歌词：行列表、行编辑区和逐字预览都显示关联的翻译
  await page.eval(`(() => {
    const sel = [...document.querySelectorAll('mdui-select')].find((s) => s.label === '歌词来源');
    sel.value = sel.querySelector('mdui-menu-item').value;
    sel.dispatchEvent(new Event('change', { bubbles: true }));
  })()`);
  await page.waitFor(`${lineTexts}[0] === '朝の光浴びて'`, '切回主歌词', 10_000);
  await page.eval(`document.querySelectorAll('.line-list mdui-list-item')[0].click()`);
  await clickButton(page, '定位到此行');
  await page.waitFor(`document.querySelector('.wp-translation')?.textContent.includes('沐浴着晨光')`, '逐字预览显示翻译', 10_000);
  await page.waitFor(`${lineMetas}[0]?.includes('翻译：沐浴着晨光')`, '主歌词行下显示翻译', 10_000);
  await page.waitFor(`document.querySelector('.translation-section')?.textContent.includes('沐浴着晨光')`, '行编辑区列出关联的翻译', 10_000);
  check(true, '主歌词行下显示关联的翻译；行编辑区列出关联的翻译；逐字预览在当前行下显示翻译');
  await snapshot(page, 'translations');

  // 删除翻译来源（确认），撤销后来源和关联都回来
  before = Number(await page.eval(js(`return revision();`)));
  await clickButton(page, '管理来源');
  await page.waitFor(drawerOpen, '管理来源面板打开');
  await page.eval(`${branchItems}[1].click()`);
  await page.waitFor(js(`return field('名称')?.value === '中文翻译';`), '面板选中中文翻译');
  await clickButton(page, '删除来源');
  await page.waitFor(`[...document.querySelectorAll('mdui-dialog')].some((d) => d.open && d.headline === '删除来源「中文翻译」？')`, '删除来源确认框');
  await clickDialogButton(page, '删除');
  await page.waitFor(js(`return Number(revision()) === ${before + 1};`) + ` && ${branchItems}.length === 1`, '删除来源', 10_000);
  await clickButton(page, '关闭管理来源');
  await page.waitFor(`!(${drawerOpen}) && !document.querySelector('.wp-translation') && !${lineMetas}[0].includes('翻译')`, '翻译从各处消失', 10_000);
  await clickButton(page, '撤销');
  await page.waitFor(js(`return Number(revision()) === ${before + 2};`) + ` && ${lineMetas}[0].includes('翻译：沐浴着晨光')`, '撤销删除来源', 10_000);
  check(true, '删除翻译来源（先确认），翻译从行列表和预览消失；撤销后来源和关联恢复');

  // 6e. 添加歌词（S3d）：粘贴到新翻译来源、追加到现有来源末尾、导入文件为新来源（导入报告）、导入替换现有来源
  const openDialog = (headline) => `[...document.querySelectorAll('mdui-dialog')].some((d) => d.open && d.headline === ${JSON.stringify(headline)})`;
  const branchOptions = `([...document.querySelectorAll('mdui-select')].find((s) => s.label === '歌词来源')?.querySelectorAll('mdui-menu-item').length ?? 0)`;
  const selectedText = js(`return field('正文')?.value;`);
  before = Number(await page.eval(js(`return revision();`)));
  await clickMenuItem(page, '添加歌词', '粘贴文本…');
  await page.waitFor(openDialog('粘贴歌词'), '粘贴对话框');
  await typeInto(page, '歌词', 'Bathing in morning light\n\n  \nLooking up at the blue sky\n');
  await page.waitFor(js(`return field('歌词')?.helper === '将追加 2 行；空白行跳过';`), '粘贴行数预览');
  await chooseOption(page, '追加到', '__new__');
  await typeInto(page, '新来源名称', 'English');
  await snapshot(page, 'paste');
  await clickDialogButton(page, '追加 2 行');
  await page.waitFor(`${dialogClosed} && ${toastShown('已新建翻译来源「English」，2 行')}`, '粘贴到新翻译来源', 10_000);
  await page.waitFor(
    js(`return Number(revision()) === ${before + 1};`) + ` && ${branchOptions} === 3 && ${lineTexts}.join('|') === 'Bathing in morning light|Looking up at the blue sky' && ${selectedText} === 'Bathing in morning light'`,
    '切到新翻译来源并选中第一行',
    10_000,
  );
  check(true, '粘贴文本到新翻译来源（appendText newTranslation）：跳过空白行，切到新来源并选中新加入的第一行');
  await clickMenuItem(page, '添加歌词', '粘贴文本…');
  await page.waitFor(openDialog('粘贴歌词'), '粘贴对话框');
  await typeInto(page, '歌词', 'Walking the road with you');
  await clickDialogButton(page, '追加 1 行');
  await page.waitFor(
    js(`return Number(revision()) === ${before + 2};`) + ` && ${lineTexts}.length === 3 && ${selectedText} === 'Walking the road with you'`,
    '追加到现有来源末尾',
    10_000,
  );
  check(true, '粘贴文本追加到当前来源末尾（appendText existing），选中新加入的第一行');

  await clickMenuItem(page, '添加歌词', '导入文件…');
  await page.waitFor(openDialog('导入歌词文件'), '导入对话框');
  await setFile(page, '选择文件', importPath);
  await chooseOption(page, '导入到', '__new__');
  await page.waitFor(js(`return field('新来源名称')?.value === '冒烟导入';`), '新来源名称默认用文件名');
  await chooseOption(page, '拆分文本单元', 'none');
  await snapshot(page, 'import');
  await clickDialogButton(page, '导入');
  await page.waitFor(openDialog('导入报告：冒烟导入.lrc'), '导入后显示报告', 20_000);
  await page.waitFor(
    js(`return Number(revision()) === ${before + 3};`) + ` && ${branchOptions} === 4 && ${lineTexts}.includes('倒序的一行')`,
    '切到导入的新来源',
    10_000,
  );
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
  await page.eval(`${issueItems}.find((i) => i.textContent.includes('第 6 行')).click()`);
  await page.waitFor(`document.querySelector('.source-line.focused')?.textContent.includes('倒序的一行') && document.querySelector('.source-line.focused')?.dataset.line === '6'`, '定位到源文件第 6 行');
  check(true, '导入报告的问题清单按源文件行号定位：点“第 6 行”的警告，源文件对照高亮第 6 行');
  await snapshot(page, 'report');
  await clickDialogButton(page, '关闭');
  await page.waitFor(dialogClosed, '关闭导入报告');
  await clickMenuItem(page, '添加歌词', '查看上次导入报告');
  await page.waitFor(openDialog('导入报告：冒烟导入.lrc'), '重新打开上次导入报告');
  await clickDialogButton(page, '关闭');
  await page.waitFor(dialogClosed, '关闭导入报告');

  await clickMenuItem(page, '添加歌词', '导入文件…');
  await page.waitFor(openDialog('导入歌词文件'), '导入对话框');
  await setFile(page, '选择文件', importPath);
  await page.waitFor(`document.querySelector('.import-dialog')?.textContent.includes('「冒烟导入」现有的 3 行会被全部替换')`, '替换现有来源的说明');
  // 普通 LRC 本身逐字切分；“按词”把连续的中日文合成一个单元，三行都会被重切。
  await chooseOption(page, '拆分文本单元', 'latinWords');
  await clickDialogButton(page, '导入');
  await page.waitFor(openDialog('导入报告：冒烟导入.lrc'), '替换导入后显示报告', 20_000);
  await page.waitFor(js(`return Number(revision()) === ${before + 4};`) + ` && ${branchOptions} === 4 && ${lineTexts}.length === 3`, '替换现有来源', 10_000);
  const replacedFacts = await page.eval(`document.querySelector('.report-facts')?.textContent ?? ''`);
  check(replacedFacts.includes('重新切分3 行'), `导入文件替换现有来源（importAsset existing），按所选规则重新切分并在报告中说明（${replacedFacts.match(/重新切分[^文]*/)?.[0] ?? '无'}）`);
  await clickDialogButton(page, '关闭');
  await page.waitFor(dialogClosed, '关闭导入报告');
  for (let i = 1; i <= 4; i++) {
    await clickButton(page, '撤销');
    await page.waitFor(js(`return Number(revision()) === ${before + 4 + i};`), `撤销第 ${i} 步添加歌词`, 10_000);
  }
  await page.waitFor(`${branchOptions} === 2 && ${lineTexts}[0] === '朝の光浴びて'`, '撤销后回到主歌词', 10_000);
  check(true, '四次添加歌词各一条历史，逐步撤销后新来源消失，回到主歌词');

  // 6f. 工程设置：画布、帧率、背景色、输出时长、音频分析各一条历史；输出时长“未确定”有明确显示；逐步撤销
  const settingsOpen = `document.querySelector('.settings-drawer')?.open === true`;
  const selectValue = (label) => `[...document.querySelectorAll('.settings-drawer mdui-select')].find((s) => s.label === ${JSON.stringify(label)})?.value`;
  const durationText = `(document.querySelector('.settings-drawer .duration-value')?.textContent ?? '')`;
  const analysisText = `(document.querySelector('.settings-drawer .analysis-status')?.textContent ?? '')`;
  const atRevision = (n) => js(`return Number(revision()) === ${n};`);
  before = Number(await page.eval(js(`return revision();`)));
  await page.eval(`document.querySelector('.nav-rail .settings-item').dispatchEvent(new MouseEvent('click', { bubbles: true, composed: true }))`);
  await page.waitFor(settingsOpen, '工程设置面板打开');
  await page.waitFor(`${selectValue('尺寸')} === '1920x1080' && ${selectValue('帧率')} === '30/1'`, '显示当前画布与帧率', 10_000);
  const initialDuration = await page.eval(durationText);
  check(initialDuration === '未确定' && (await page.eval(`!!document.querySelector('.duration-value.undetermined')`)), '新工程的输出时长显示“未确定”，并提示导出前必须确定');
  await chooseOption(page, '尺寸', '1280x720');
  await page.waitFor(`${atRevision(before + 1)} && ${selectValue('尺寸')} === '1280x720'`, '改画布尺寸', 10_000);
  await chooseOption(page, '帧率', '25/1');
  await page.waitFor(`${atRevision(before + 2)} && ${selectValue('帧率')} === '25/1'`, '改帧率', 10_000);
  await typeInto(page, '底色', '#102030');
  await pressEnter(page, '底色');
  await page.waitFor(`${atRevision(before + 3)} && ${fieldValue('底色')} === '#102030' && document.querySelector('.color-swatch').value === '#102030'`, '改背景色', 10_000);
  const noticeText = await page.eval(`document.querySelector('.status-bar .notice')?.textContent ?? ''`);
  check(!noticeText.includes('没有变化'), `画布尺寸、帧率、背景色各一条历史（composition.setCanvasSize／setFrameRate／setBackgroundColor），面板显示新值，没有重复发出的空命令（提示：${noticeText || '无'}）`);
  await clickButtonIn(page, '.settings-drawer', '按音频时长');
  await page.waitFor(`${atRevision(before + 4)} && ${durationText} === '0:12.000'`, '按音频时长', 10_000);
  await typeInto(page, '自定义时长', '10.5');
  await pressEnter(page, '自定义时长');
  await page.waitFor(`${atRevision(before + 5)} && ${durationText} === '0:10.500'`, '自定义输出时长', 10_000);
  await clickButtonIn(page, '.settings-drawer', '设为未确定');
  await page.waitFor(`${atRevision(before + 6)} && ${durationText} === '未确定'`, '设为未确定', 10_000);
  check(true, '输出时长：按音频时长（0:12.000）→ 自定义 10.5 秒 → 设为未确定，各一条历史');
  await clickButtonIn(page, '.settings-drawer', '分析并采用');
  await page.waitFor(`${atRevision(before + 7)} && ${analysisText}.startsWith('已采用')`, '分析并采用', 90_000);
  check(true, `音频分析：建任务、等成功、audio.analysis.apply 采用（${await page.eval(analysisText)}）`);
  await snapshot(page, 'settings');
  await clickButtonIn(page, '.settings-drawer', '清除');
  await page.waitFor(`${atRevision(before + 8)} && ${analysisText} === '未分析'`, '清除音频分析', 10_000);
  for (let i = 1; i <= 8; i++) {
    await clickButton(page, '撤销');
    await page.waitFor(atRevision(before + 8 + i), `撤销第 ${i} 步设置`, 10_000);
  }
  await page.waitFor(
    `${selectValue('尺寸')} === '1920x1080' && ${selectValue('帧率')} === '30/1' && ${fieldValue('底色')} !== '#102030' && ${durationText} === '未确定' && ${analysisText} === '未分析'`,
    '撤销后设置回到原值',
    10_000,
  );
  check(true, '清除音频分析；八步设置逐步撤销后，画布、帧率、背景色、输出时长和音频分析都回到原值');
  await clickButton(page, '关闭工程设置');
  await page.waitFor(`!(${settingsOpen})`, '关闭工程设置');

  // 7. 刷新页面：经 resume 接回同一 client，首页列出已打开的工程，点击后回到同一修订
  const revisionBeforeReload = await page.eval(js(`return revision();`));
  await page.navigate(`${ORIGIN}/`);
  await page.waitFor(`document.querySelector('.connection')?.textContent === '已连接'`, '刷新后重新连接');
  const sessionCard = `[...document.querySelectorAll('.home .session-card')].find((c) => c.querySelector('.session-card-name')?.textContent.includes(${JSON.stringify(directory)}))`;
  await page.waitFor(`!!(${sessionCard})`, '首页“继续工作”列出已打开的工程', 30_000);
  await page.eval(`(${sessionCard}).click()`);
  await page.waitFor(js(`return revision() === ${JSON.stringify(revisionBeforeReload)} && document.querySelectorAll('.line-list mdui-list-item').length === 4;`), '接回工程', 60_000);
  check(true, `刷新页面后从首页接回同一工程（修订 ${revisionBeforeReload}），不重新打开`);
  await selectToken(page, 0, '朝');

  // 8. 保存到新目录
  await clickButton(page, '保存工程');
  // 列表加载完：有目录行，或者工程目录还是空的（新 profile）。
  await page.waitFor(`!!document.querySelector('mdui-dialog.save-location-dialog[open] :is(.directory-row, .directory-empty)')`, '“保存到…”对话框列出工程目录', 30_000);
  await typeInto(page, '目录名', directory);
  await page.waitFor(`document.querySelector('.save-target')?.dataset.kind === 'new'`, '目标写明“会新建目录”');
  await clickDialogButton(page, '保存');
  await page.waitFor(`document.querySelector('.project-state')?.textContent === '已保存'`, '保存完成', 60_000);
  check(true, `保存到授权目录下的新目录 ${directory}（job 成功终态）`);

  // 9. 关闭并从工程目录重开
  await clickMenuItem(page, '更多', '关闭工程');
  await page.waitFor(`!!document.querySelector('.home')`, '回到首页', 60_000);
  check(true, '关闭工程回到首页');
  const directoryRow = `document.querySelector('.directory-row[data-path=${JSON.stringify(directory)}] .directory-main')`;
  await page.waitFor(`!!(${directoryRow}) && !(${directoryRow}).disabled`, '工程目录列出刚保存的工程', 30_000);
  await page.eval(`(${directoryRow}).click()`);
  await page.waitFor(`document.querySelectorAll('.line-list mdui-list-item').length === 4`, '重开后显示 4 行', 120_000);
  await page.eval(`document.querySelector('.token-chip').click()`);
  await page.waitFor(`document.querySelector('.time-editor-title')?.textContent.includes('所选「朝」')`, '重开后选中元素「朝」');
  check(
    (await page.eval(fieldValue('起点'))) === savedAsaStart && (await page.eval(fieldValue('终点'))) === '0:01.100' && (await page.eval(`document.querySelector('.project-state')?.textContent`)) === '已保存',
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
