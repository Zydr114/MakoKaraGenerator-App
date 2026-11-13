// 歌词页补全的浏览器冒烟（W6 设计 §4）：用真实 Chrome 走 W6 的入口。
// 用 MakoKara 工程 TTML 新建：识别结果写明会恢复全部来源，新工程里两个来源、注音片段都在
// → 导入文件：识别结果、预览、改选编码后预览变化、解析失败写明原因不能导入
// → 再导入同一个工程 TTML：先选来源，翻译来源不能导入到主歌词，作为新来源导入，名称和角色跟随所选来源，一次撤销
// → 注音片段：多片段时整段只读，左移右移、改字、加片段、删片段、删除注音，整段输入后按字拆开（一条历史），撤销重做
// → 片段时间：时间栏改起止、±1 帧、清除与撤销，起点／终点取播放头；逐字预览里注音按片段各自填充
// → 时间轴注音轨：点虚线框选第一个片段，点色块选片段，拖片段边界，共用边界的相邻片段一起拖（一条历史）与撤销；
//   元素轨的共用边界向右、向左拖与撤销
// → 时间轴翻译行轨：单击没有动作，拖独立计时的翻译行，双击切到翻译来源；看翻译时行轨里跟随主歌词的行可以点选，
//   主歌词参考轨双击切回主歌词。
//
// 前提：pnpm dev:backend 与 pnpm dev 都在运行。
// 用法：node scripts/lyrics-smoke.mjs [--url http://localhost:5173] [--screenshot out.png] [--headed]

import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { parseArgs } from 'node:util';
import { resolveBackendRepo } from './backend-launcher.mjs';
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

// ── 素材：8 秒 WAV；W5 导出的“TTML（整个工程）”：主歌词三行（「朝」有两段注音“あ”“さ”）、跟随主歌词的中文翻译三行、
//    独立计时的英文翻译两行 ──

function makeWav(seconds = 8, sampleRate = 22050) {
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
    buffer.writeInt16LE(Math.round(Math.sin(2 * Math.PI * 330 * t) * 10000 * Math.exp(-(t % 1) * 4)), 44 + i * 2);
  }
  return buffer;
}

// 工程 TTML 只接受当前歌词 schema（后端格式门禁），版本取自本仓库后端源码，不在这里写死。
const LYRICS_SCHEMA = /CurrentSchemaVersion\s*=\s*(\d+);/.exec(readFileSync(
  join(resolveBackendRepo(), 'src/MakoKaraGenerator.Core/Lyrics/LyricsDocument.cs'), 'utf8'))?.[1];
if (!LYRICS_SCHEMA) throw new Error('读不到后端歌词 schema 版本（LyricsDocument.CurrentSchemaVersion）。');
const TRANSLATION_ID = 'translation-1ae2eeb00b374e00a36a579eab6635d9';
const ENGLISH_ID = 'translation-2b7d0c1e9f8a4b3c8d6e5f4a3b2c1d0e';
const PROJECT_TTML = [
  '<?xml version="1.0" encoding="utf-8"?>',
  `<tt xmlns:tts="http://www.w3.org/ns/ttml#styling" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" xmlns:mkg="urn:makokara:ttml" mkg:profile="ttml.project" mkg:version="1" mkg:timing-profile="ttml2-media" mkg:primary-branch-id="primary" mkg:lyrics-schema="${LYRICS_SCHEMA}" xmlns="http://www.w3.org/ns/ttml">`,
  '<head><metadata format="ttml.project" /></head><body>',
  '<div mkg:branch-id="primary" mkg:branch-name="主歌词" mkg:branch-role="Primary" mkg:timing-mode="Independent">',
  '<p begin="0.2s" end="1.6s" mkg:line-id="line-0001" mkg:line-text="朝の光" mkg:timing-status="Imported" mkg:start-us="200000" mkg:end-us="1600000" itunes:key="line-0001">',
  '<span mkg:token-id="token-0001" mkg:timing-status="Imported" mkg:start-us="200000" mkg:end-us="600000"><span tts:ruby="container" mkg:ruby-id="ruby-0001" mkg:base-token-id="token-0001" mkg:scale="0.5" mkg:gap="2" mkg:alignment="Center">',
  '<span tts:ruby="base" begin="0s" end="0.4s">朝</span><span tts:ruby="textContainer">',
  '<span tts:ruby="text" mkg:segment-id="ruby-0001-segment-1" mkg:timing-status="Missing">あ</span><span tts:ruby="text" mkg:segment-id="ruby-0001-segment-2" mkg:timing-status="Missing">さ</span>',
  '</span></span></span>',
  '<span begin="0.4s" end="0.8s" mkg:token-id="token-0002" mkg:timing-status="Imported" mkg:start-us="600000" mkg:end-us="1000000">の</span>',
  '<span begin="0.8s" end="1.4s" mkg:token-id="token-0003" mkg:timing-status="Imported" mkg:start-us="1000000" mkg:end-us="1600000">光</span></p>',
  '<p begin="1.6s" end="3.2s" mkg:line-id="line-0002" mkg:line-text="風声" mkg:timing-status="Imported" mkg:start-us="1600000" mkg:end-us="3200000" itunes:key="line-0002">',
  '<span begin="0s" end="0.6s" mkg:token-id="token-0004" mkg:timing-status="Imported" mkg:start-us="1600000" mkg:end-us="2200000">風</span>',
  '<span begin="0.6s" end="1.6s" mkg:token-id="token-0005" mkg:timing-status="Imported" mkg:start-us="2200000" mkg:end-us="3200000">声</span></p>',
  '<p begin="3.2s" end="4.8s" mkg:line-id="line-0003" mkg:line-text="三行" mkg:timing-status="Imported" mkg:start-us="3200000" mkg:end-us="4800000" itunes:key="line-0003">',
  '<span begin="0s" end="0.4s" mkg:token-id="token-0006" mkg:timing-status="Imported" mkg:start-us="3200000" mkg:end-us="3600000">三</span>',
  '<span begin="0.4s" end="1.6s" mkg:token-id="token-0007" mkg:timing-status="Imported" mkg:start-us="3600000" mkg:end-us="4800000">行</span></p>',
  '</div>',
  `<div mkg:branch-id="${TRANSLATION_ID}" mkg:branch-name="中文" mkg:branch-role="Translation" mkg:timing-mode="FollowPrimary">`,
  '<p mkg:line-id="line-1000" mkg:line-text="晨光" mkg:primary-line-id="line-0001" mkg:timing-status="Missing" itunes:key="line-1000"><span mkg:token-id="token-1000" mkg:timing-status="Missing">晨</span><span mkg:token-id="token-1001" mkg:timing-status="Missing">光</span></p>',
  '<p mkg:line-id="line-1001" mkg:line-text="风声" mkg:primary-line-id="line-0002" mkg:timing-status="Missing" itunes:key="line-1001"><span mkg:token-id="token-1002" mkg:timing-status="Missing">风</span><span mkg:token-id="token-1003" mkg:timing-status="Missing">声</span></p>',
  '<p mkg:line-id="line-1002" mkg:line-text="三行" mkg:primary-line-id="line-0003" mkg:timing-status="Missing" itunes:key="line-1002"><span mkg:token-id="token-1004" mkg:timing-status="Missing">三</span><span mkg:token-id="token-1005" mkg:timing-status="Missing">行</span></p>',
  '</div>',
  `<div mkg:branch-id="${ENGLISH_ID}" mkg:branch-name="英文" mkg:branch-role="Translation" mkg:timing-mode="Independent">`,
  '<p begin="0.3s" end="1.5s" mkg:line-id="line-2000" mkg:line-text="Morning" mkg:primary-line-id="line-0001" mkg:timing-status="Imported" mkg:start-us="300000" mkg:end-us="1500000" itunes:key="line-2000"><span mkg:token-id="token-2000" mkg:timing-status="Missing">Morning</span></p>',
  '<p begin="1.7s" end="3s" mkg:line-id="line-2001" mkg:line-text="Wind" mkg:primary-line-id="line-0002" mkg:timing-status="Imported" mkg:start-us="1700000" mkg:end-us="3000000" itunes:key="line-2001"><span mkg:token-id="token-2001" mkg:timing-status="Missing">Wind</span></p>',
  '</div></body></tt>',
].join('');

const assets = mkdtempSync(join(tmpdir(), 'mako-lyrics-smoke-'));
const wavPath = join(assets, '歌词冒烟.wav');
const ttmlPath = join(assets, '工程歌词.ttml');
const lrcPath = join(assets, '编码试验.lrc');
const brokenPath = join(assets, '坏文件.ttml');
writeFileSync(wavPath, makeWav());
writeFileSync(ttmlPath, PROJECT_TTML);
writeFileSync(lrcPath, '[00:01.00]你好世界\n[00:02.00]第二行\n');
writeFileSync(brokenPath, '这不是 TTML');
const projectName = `歌词冒烟 ${Date.now()}`;

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

const lineTexts = `[...document.querySelectorAll('.line-list .ll-text')].map((n) => n.textContent)`;
const lineNotes = `[...document.querySelectorAll('.line-list .ll-sub')].map((n) => n.textContent)`;
const lineTitle = `document.querySelector('.lhead h2')?.textContent`;
const lineText = uiJs(`return __ui.find('textbox', '正文')?.value ?? null;`);
const branchName = `document.querySelector('.srcpick-name')?.textContent`;
const selectedToken = `document.querySelector('.tk-row .tk[aria-selected=true] .tk-t')?.textContent`;
const timeInputs = `[...document.querySelectorAll('.timebar .tb-time input')]`;
const dialogOpen = `!!document.querySelector('[role=dialog], [role=alertdialog]')`;
// 导入对话框右边的识别结果
const lyricsCheck = `document.querySelector('[role=dialog] .file-check [data-check="lyrics"]')`;
const planText = `(${lyricsCheck}?.textContent ?? '')`;
const previewTexts = `[...(${lyricsCheck}?.querySelectorAll('.found .plan-line-text') ?? [])].map((n) => n.textContent)`;
const shownIn = (label) => uiJs(`return __ui.find('select', ${JSON.stringify(label)}, false, '@dialog')?.textContent.trim();`);
const importDisabled = uiJs(`const b = __ui.find('button', '导入', false, '@dialog'); return !!b && __ui.disabled(b);`);

const undo = (page) => ui.click(page, 'button', '撤销', { scope: '.app-bar' });
const redo = (page) => ui.click(page, 'button', '重做', { scope: '.app-bar' });

async function escape(page) {
  await page.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
  await page.send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
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

async function selectRow(page, number, text) {
  await ui.click(page, 'row', `${number} ${text}`);
  await page.waitFor(`${lineTitle} === '第 ${number} 行'`, `选中第 ${number} 行`);
}

/** 选中当前行里文字为 text 的文本单元；已选中时不再点（再点一次会回到整行）。 */
async function selectToken(page, text) {
  if ((await page.eval(selectedToken)) !== text) await page.eval(`[...document.querySelectorAll('.tk-row .tk')].find((c) => c.querySelector('.tk-t').textContent === ${JSON.stringify(text)}).click()`);
  await page.waitFor(`${selectedToken} === ${JSON.stringify(text)}`, `选中文本单元「${text}」`);
}

/** 时间栏填起止并回车提交。 */
async function setTimes(page, start, end) {
  await page.eval(uiJs(`const el = ${timeInputs}[0]; el.focus(); __ui.setValue(el, ${JSON.stringify(start)});`));
  await page.eval(uiJs(`const el = ${timeInputs}[1]; el.focus(); __ui.setValue(el, ${JSON.stringify(end)}); __ui.key(el, 'Enter');`));
}

/** 中间截图：等对话框动画结束再截。 */
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
    if (message.method === 'Runtime.consoleAPICalled' && message.params.type === 'error') errors.push(message.params.args.map((a) => a.value ?? a.description).join(' '));
  });

  page = await browser.openPage(`${ORIGIN}/`, { width: 1440, height: 900 });
  await page.send('DOM.enable');
  await page.waitFor(`document.readyState === 'complete' && ${shell.connected}`, '连接 Server');
  const rev = async () => page.eval(REVISION);

  // 1. 用工程 TTML 新建：识别结果写明会恢复全部来源，不选来源
  // 新建对话框右边的文件检查：识别结果、提示和前几行
  await shell.startNewProject(page, { audio: wavPath, lyrics: ttmlPath, name: projectName });
  const createCheck = `document.querySelector('.file-check [data-check="lyrics"]')`;
  const createPlan = await page.eval(`${createCheck}.textContent`);
  check(
    createPlan.includes('这是 MakoKara 工程 TTML，新建时会恢复全部 3 个来源。') && !(await ui.exists(page, 'select', '导入哪个来源')),
    '新建工程：多来源的工程 TTML 写明“新建时会恢复全部 3 个来源”，不需要选来源',
  );
  const createTexts = await page.eval(`[...${createCheck}.querySelectorAll('.found .plan-line-text')].map((n) => n.textContent)`);
  const createRuby = await page.eval(`[...${createCheck}.querySelectorAll('.found .plan-line-ruby')].map((n) => n.textContent)`);
  check(createTexts[0] === '朝の光' && createRuby[0] === '朝（あ·さ）', `文件检查的预览写出注音片段（${createRuby[0]}）`);
  await snapshot(page, 'create');
  await shell.finishNewProject(page);
  await page.waitFor(`document.querySelector('.project-name')?.textContent === ${JSON.stringify(projectName)}`, '进入工程', 60_000);
  await page.waitFor(`${lineTexts}.length === 3`, '新工程有三行主歌词', 30_000);
  await page.waitFor(`${lineNotes}[0]?.includes('晨光')`, '主歌词行下显示关联的翻译', 10_000);
  const created = await branchNames(page);
  check(created.join('、') === '主歌词、中文、英文', `新工程恢复了主歌词、中文和英文翻译三个来源，翻译仍关联主歌词（${created.join('、')}）`);

  // 2. 导入文件：识别结果与预览；改选编码后预览变化；解析失败写明原因、不能导入
  await ui.menu(page, '添加歌词', '导入文件…');
  await ui.waitDialog(page, '导入歌词文件');
  await ui.setFile(page, '选择歌词文件', lrcPath);
  await page.waitFor(`${previewTexts}[0] === '你好世界'`, '识别结果的预览', 30_000);
  const lrcPlan = await page.eval(planText);
  check(lrcPlan.includes('识别为') && lrcPlan.includes('UTF-8') && lrcPlan.includes('2行'), `导入前先在右边显示识别结果和预览（${lrcPlan.slice(0, 40)}…）`);
  await ui.choose(page, '编码', 'GB18030（简体中文）', { scope: '@dialog' });
  await page.waitFor(`${planText}.includes('GB18030') && ${previewTexts}.length > 0 && ${previewTexts}[0] !== '你好世界'`, '改选编码后重新识别', 30_000);
  check(true, `改选编码为 GB18030：用同一个资源重新识别，预览随之变化（${(await page.eval(previewTexts))[0]}）`);
  await ui.setFile(page, '选择歌词文件', brokenPath);
  const parseError = `(${lyricsCheck}?.querySelector('.errline')?.textContent ?? '')`;
  await page.waitFor(`${parseError}.startsWith('文件解析失败：')`, '解析失败', 30_000);
  check(await page.eval(importDisabled), `解析失败时写明原因（${await page.eval(parseError)}），“导入”不能点`);

  // 3. 再导入同一个工程 TTML：先选来源；翻译来源不能导入到主歌词；作为新来源导入
  await ui.setFile(page, '选择歌词文件', ttmlPath);
  await page.waitFor(`!!${shownIn('导入哪个来源')} && ${previewTexts}[0] === '朝の光'`, '多来源要选来源', 30_000);
  check(
    (await page.eval(planText)).includes('有 3 个来源，一次导入一个') && (await page.eval(shownIn('导入哪个来源'))).startsWith('主歌词'),
    '导入工程 TTML：写明一次导入一个来源，默认选主歌词',
  );
  await ui.choose(page, '导入哪个来源', '中文（翻译，3 行）', { scope: '@dialog' });
  await page.waitFor(`${previewTexts}[0] === '晨光'`, '改选来源后预览换成翻译', 30_000);
  await page.waitFor(`document.querySelector('[role=dialog]')?.textContent.includes('不能导入到主歌词') && ${importDisabled}`, '不能导入到主歌词');
  check(true, '改选“中文”来源：预览换成翻译的行；目标是主歌词时写明不能导入，“导入”不能点');
  await ui.choose(page, '导入到', '作为新来源', { scope: '@dialog' });
  await page.waitFor(
    `${uiJs(`return __ui.find('textbox', '新来源名称', false, '@dialog')?.value;`)} === '中文' && ${shownIn('新来源的角色')} === '翻译'`,
    '新来源名称与角色跟随所选来源',
  );
  await snapshot(page, 'import-source');
  const before = await rev();
  await ui.clickInDialog(page, '导入');
  await ui.waitDialog(page, '导入报告：工程歌词.ttml', { timeout: 30_000 });
  await page.waitFor(`${atRevision(before + 1)} && ${lineTexts}[0] === '晨光'`, '切到导入的新来源', 10_000);
  await ui.clickInDialog(page, '关闭');
  await ui.waitDialogClosed(page, '导入报告：工程歌词.ttml');
  check((await branchNames(page)).length === 4, '作为新来源导入：名称和角色默认跟随所选来源，只导入了这个来源的 3 行，显示导入报告');
  await undo(page);
  await page.waitFor(atRevision(before + 2), '撤销导入', 10_000);
  check((await branchNames(page)).length === 3, '一次撤销整个导入');

  // 4. 注音片段：「朝」的注音是两段“あ”“さ”
  const chips = `[...document.querySelectorAll('.rb-segs .rb-seg > span:first-child')].map((n) => n.textContent)`;
  const selectedChip = `document.querySelector('.rb-segs .rb-seg.is-sel > span:first-child')?.textContent`;
  const chip = (text) => `[...document.querySelectorAll('.rb-segs .rb-seg')].find((c) => c.firstElementChild.textContent === ${JSON.stringify(text)})`;
  const clickChip = (text) => page.eval(`${chip(text)}.click()`);
  const rubyField = `__ui.find('textbox', '整段注音')`;
  await page.waitFor(`${lineTexts}[0] === '朝の光'`, '回到主歌词');
  await selectRow(page, 1, '朝の光');
  await page.waitFor(`document.querySelectorAll('.tk-row .tk').length === 3`, '第 1 行有 3 个文本单元');
  await selectToken(page, '朝');
  await page.waitFor(`JSON.stringify(${chips}) === '["あ","さ"]'`, '「朝」的注音片段', 10_000);
  check(
    await page.eval(uiJs(`const f = ${rubyField}; return f.disabled && f.closest('.tf').textContent.includes('几个片段');`)),
    '多片段的注音：整段输入框只读，写明请逐个片段修改',
  );
  let before2 = await rev();
  await clickChip('さ');
  await page.waitFor(`${selectedChip} === 'さ' && !!document.querySelector('.rb-edit')`, '选中片段「さ」');
  await ui.click(page, 'button', '左移', { scope: '.rb-edit' });
  await page.waitFor(`${atRevision(before2 + 1)} && JSON.stringify(${chips}) === '["さ","あ"]' && ${selectedChip} === 'さ'`, '左移', 10_000);
  await ui.click(page, 'button', '右移', { scope: '.rb-edit' });
  await page.waitFor(`${atRevision(before2 + 2)} && JSON.stringify(${chips}) === '["あ","さ"]' && ${selectedChip} === 'さ'`, '右移', 10_000);
  check(true, '片段左移、右移：顺序变化，仍选中这个片段');
  await ui.fill(page, '所选片段「さ」', 'ざ', { enter: true });
  await page.waitFor(`JSON.stringify(${chips}) === '["あ","ざ"]' && ${selectedChip} === 'ざ'`, '片段改字', 10_000);
  check(true, '片段改字（setRubySegmentText），仍选中它');
  await ui.click(page, 'button', '加片段', { scope: '.rb-segs' });
  await ui.waitDialog(page, '给「朝」加注音片段');
  await ui.fill(page, '片段文字', 'ひ', { scope: '@dialog' });
  await ui.clickInDialog(page, '加片段');
  await page.waitFor(`JSON.stringify(${chips}) === '["あ","ざ","ひ"]' && ${selectedChip} === 'ひ'`, '加片段', 10_000);
  check(true, '加片段：加在所选片段后面，选中新片段');
  await ui.click(page, 'button', '删除片段', { scope: '.rb-edit' });
  await page.waitFor(`JSON.stringify(${chips}) === '["あ","ざ"]' && ${selectedChip} === 'ざ'`, '删除片段', 10_000);
  check(true, '删除片段：选中相邻的片段');
  before2 = await rev();
  await ui.menu(page, '注音操作', '删除注音');
  await page.waitFor(`${atRevision(before2 + 1)} && !document.querySelector('.rb-seg') && ${uiJs(`const f = ${rubyField}; return !!f && !f.disabled && f.value === '';`)}`, '删除注音', 10_000);
  check(true, '删除注音：一次删除全部片段（一条历史），整段输入框可以输入');
  await ui.fill(page, '整段注音', 'あさ', { enter: true });
  await page.waitFor(`JSON.stringify(${chips}) === '["あさ"]'`, '整段输入注音', 10_000);
  check(await page.eval(uiJs(`return !${rubyField}.disabled;`)), '整段输入注音（setRubyText）：只有一个没有时间的片段时仍可直接改整段');
  before2 = await rev();
  await ui.menu(page, '注音操作', '按字拆开');
  await page.waitFor(`${atRevision(before2 + 1)} && JSON.stringify(${chips}) === '["あ","さ"]' && ${selectedChip} === 'あ'`, '按字拆开', 10_000);
  await snapshot(page, 'ruby');
  check(true, '按字拆开：“あさ”拆成“あ”“さ”，一条历史，选中原片段');
  await undo(page);
  await page.waitFor(`${atRevision(before2 + 2)} && JSON.stringify(${chips}) === '["あさ"]'`, '撤销按字拆开', 10_000);
  await redo(page);
  await page.waitFor(`${atRevision(before2 + 3)} && JSON.stringify(${chips}) === '["あ","さ"]'`, '重做按字拆开', 10_000);
  check(true, '按字拆开一次撤销、一次重做');

  // 5. 片段时间：时间栏与逐字预览
  const timebarOn = (text) => `document.querySelector('.timebar .lbl')?.textContent === '注音片段' && document.querySelector('.timebar b')?.textContent === ${JSON.stringify(text)}`;
  const selectChip = async (text) => {
    if ((await page.eval(selectedChip)) !== text) await clickChip(text);
    await page.waitFor(timebarOn(text), `时间栏切到片段「${text}」`);
  };
  const chipStatus = (text) => `${chip(text)}?.querySelector('.status-icon')?.getAttribute('aria-label')`;
  const rseg = (text) => `[...document.querySelectorAll('.wp-rseg')].find((n) => n.textContent === ${JSON.stringify(text)})`;
  const rsegRange = (text) => `(() => { const n = ${rseg(text)}; return n ? [n.classList.contains('timed'), Number(n.dataset.s), Number(n.dataset.e)] : null; })()`;
  // 时间轴用“全曲”视口（8 秒音频）：x 与时间按比例换算。看主歌词时从上到下：标尺 0～22、波形 22～94、注音 94～116、
  // 元素 116～150、行 150～172、中文翻译行轨 172～194、英文翻译行轨 194～216。
  await ui.click(page, 'button', '全曲');
  const geometry = await page.eval(`(() => { const r = document.querySelector('.timeline-canvas canvas').getBoundingClientRect(); return { left: r.left, top: r.top, width: r.width, height: r.height }; })()`);
  const xAt = (us) => geometry.left + (us * geometry.width) / 8_000_000;
  const RUBY_Y = geometry.top + 105;
  const mouse = (type, x, y) => page.send('Input.dispatchMouseEvent', { type, x, y, button: 'left', clickCount: 1 });
  const clickAt = async (x, y) => {
    await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y });
    await mouse('mousePressed', x, y);
    await mouse('mouseReleased', x, y);
  };
  check(geometry.height === 216, `时间轴有注音轨和两条翻译行轨：画布高 ${geometry.height}（标尺 22＋波形 72＋注音 22＋元素 34＋行 22＋翻译 22×2）`);
  await selectChip('さ');
  await clickAt(xAt(400_000), RUBY_Y);
  await page.waitFor(`${selectedChip} === 'あ'`, '点注音轨的虚线框', 10_000);
  check(true, '注音轨：整组没有时间时在「朝」的位置画虚线框，点它选中第一个片段');

  await selectChip('あ');
  check(
    JSON.stringify(await page.eval(rsegRange('あ'))) === JSON.stringify([false, 200_000, 600_000]),
    '逐字预览：没有时间的注音片段跟随「朝」的时间填充',
  );
  before2 = await rev();
  await setTimes(page, '0:00.200', '0:00.400');
  await page.waitFor(`${chipStatus('あ')} === '已修改' && JSON.stringify(${rsegRange('あ')}) === '[true,200000,400000]'`, '片段「あ」有了时间', 10_000);
  check(true, '时间栏：选中片段后改起止（setRubySegmentTime），片段显示已修改，逐字预览按片段自己的时间填充');
  await ui.click(page, 'button', '晚一帧', { scope: '.timebar' });
  await page.waitFor(`JSON.stringify(${rsegRange('あ')}) === '[true,233334,433334]'`, '片段晚一帧', 10_000);
  check(true, '片段晚一帧：起止一起移一帧（30 fps）');
  await ui.click(page, 'button', '清除', { scope: '.timebar' });
  await page.waitFor(`${chipStatus('あ')} === '未打轴' && JSON.stringify(${rsegRange('あ')}) === '[false,200000,600000]'`, '清除片段时间', 10_000);
  await undo(page);
  await page.waitFor(`${atRevision(before2 + 4)} && JSON.stringify(${rsegRange('あ')}) === '[true,233334,433334]'`, '撤销清除', 10_000);
  check(true, '清除片段时间（clearRubySegmentTime），一次撤销恢复');

  // 点标尺移动播放头，再取播放头
  const seekTo = (us) => clickAt(xAt(us), geometry.top + 11);
  await selectChip('さ');
  await seekTo(450_000);
  await ui.menu(page, '取播放头', '起点取播放头', { scope: '.timebar' });
  await page.waitFor(`${rseg('さ')}?.classList.contains('timed')`, '起点取播放头', 10_000);
  const [, stampStart, stampEnd] = await page.eval(rsegRange('さ'));
  check(
    // 帧起点向上取整到微秒，一帧是 33333 或 33334 微秒。
    stampStart >= 400_000 && stampStart <= 500_000 && [33_333, 33_334].includes(stampEnd - stampStart),
    `起点取播放头：起点 ${stampStart}，没有终点时终点为后一帧 ${stampEnd}`,
  );
  await seekTo(550_000);
  await ui.menu(page, '取播放头', '终点取播放头', { scope: '.timebar' });
  await page.waitFor(`${rsegRange('さ')}[2] > ${stampEnd}`, '终点取播放头', 10_000);
  const [, , stampedEnd] = await page.eval(rsegRange('さ'));
  check(stampedEnd >= 500_000 && stampedEnd <= 600_000, `终点取播放头：终点 ${stampedEnd}`);
  await snapshot(page, 'segment-time');
  // 0.3 秒：「朝」（0.2～0.6）填了约四分之一，“あ”（0.233～0.433）约三分之一，“さ”还没开始。
  await seekTo(300_000);
  const fillOf = (expr) => `Number((${expr})?.style.getPropertyValue('--fill'))`;
  await page.waitFor(`${fillOf(rseg('あ'))} > 0 && ${fillOf(rseg('あ'))} < 1`, '播放头到 0.3 秒', 10_000);
  const fills = await page.eval(`[${fillOf(`document.querySelector('.wp-seg[data-s="200000"]')`)}, ${fillOf(rseg('あ'))}, ${fillOf(rseg('さ'))}]`);
  check(
    fills[0] > 0.1 && fills[0] < 0.5 && fills[1] > 0.2 && fills[1] < 0.6 && fills[2] === 0,
    `逐字预览：注音片段各自填充（「朝」${fills[0].toFixed(2)}，“あ”${fills[1].toFixed(2)}，“さ”${fills[2]}）`,
  );

  // 6. 时间轴注音轨：现在“あ”是 0.233～0.433，“さ”从 stampStart 到 stampedEnd
  const rangeOf = async (text) => (await page.eval(rsegRange(text))).slice(1);
  const drag = async (fromUs, toUs) => {
    const [x0, x1] = [xAt(fromUs), xAt(toUs)];
    await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: x0, y: RUBY_Y });
    await mouse('mousePressed', x0, RUBY_Y);
    for (let i = 1; i <= 5; i++) await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: x0 + ((x1 - x0) * i) / 5, y: RUBY_Y, button: 'left' });
    await mouse('mouseReleased', x1, RUBY_Y);
  };
  await selectChip('さ');
  await clickAt(xAt(333_334), RUBY_Y);
  await page.waitFor(`${selectedChip} === 'あ' && ${timebarOn('あ')}`, '点注音轨的色块', 10_000);
  check(true, '注音轨：点片段色块选中这个片段，行编辑区和时间栏跟着切换');
  before2 = await rev();
  await drag(433_334, stampStart);
  await page.waitFor(`${atRevision(before2 + 1)} && JSON.stringify(${rsegRange('あ')}) === '[true,233334,${stampStart}]'`, '拖“あ”的终点', 10_000);
  check(JSON.stringify(await rangeOf('さ')) === JSON.stringify([stampStart, stampedEnd]), `拖片段终点：“あ”的终点到 ${stampStart}（按帧吸附），“さ”不动，一条历史`);
  const middle = 500_000;
  await drag(stampStart, middle);
  await page.waitFor(`${atRevision(before2 + 2)} && JSON.stringify(${rsegRange('あ')}) === '[true,233334,${middle}]'`, '拖共用边界', 10_000);
  check(JSON.stringify(await rangeOf('さ')) === JSON.stringify([middle, stampedEnd]), '拖共用边界：“あ”的终点和“さ”的起点一起移动，一条历史（setRubySegmentTimes）');
  await snapshot(page, 'ruby-track');
  await undo(page);
  await page.waitFor(`${atRevision(before2 + 3)} && JSON.stringify(${rsegRange('さ')}) === '[true,${stampStart},${stampedEnd}]'`, '撤销拖共用边界', 10_000);
  check(JSON.stringify(await rangeOf('あ')) === JSON.stringify([233_334, stampStart]), '一次撤销，两个片段一起还原');

  // 元素轨（116～150）：「朝」「の」共用 0.6 秒的边界，向右拖要先让「の」的起点让开（后端按顺序检查重叠）
  const TOKEN_Y = geometry.top + 133;
  const tokenRange = (text) =>
    `(() => { const n = [...document.querySelectorAll('.wp-seg')].find((s) => s.querySelector('.wp-base')?.textContent === ${JSON.stringify(text)}); return n ? [Number(n.dataset.s), Number(n.dataset.e)] : null; })()`;
  const dragToken = async (fromUs, toUs) => {
    const [x0, x1] = [xAt(fromUs), xAt(toUs)];
    await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: x0, y: TOKEN_Y });
    await mouse('mousePressed', x0, TOKEN_Y);
    for (let i = 1; i <= 5; i++) await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: x0 + ((x1 - x0) * i) / 5, y: TOKEN_Y, button: 'left' });
    await mouse('mouseReleased', x1, TOKEN_Y);
  };
  before2 = await rev();
  await dragToken(600_000, 700_000);
  await page.waitFor(`${atRevision(before2 + 1)} && JSON.stringify(${tokenRange('朝')}) === '[200000,700000]' && JSON.stringify(${tokenRange('の')}) === '[700000,1000000]'`, '向右拖元素的共用边界', 10_000);
  check(true, '元素轨：向右拖「朝」「の」的共用边界，两个元素一起移动，一条历史');
  await dragToken(700_000, 500_000);
  await page.waitFor(`${atRevision(before2 + 2)} && JSON.stringify(${tokenRange('朝')}) === '[200000,500000]' && JSON.stringify(${tokenRange('の')}) === '[500000,1000000]'`, '向左拖元素的共用边界', 10_000);
  check(true, '元素轨：再向左拖过原位置，两个元素一起移动');
  await undo(page);
  await page.waitFor(atRevision(before2 + 3), '撤销第二次拖动', 10_000);
  await undo(page);
  await page.waitFor(`${atRevision(before2 + 4)} && JSON.stringify(${tokenRange('朝')}) === '[200000,600000]'`, '撤销两次拖动', 10_000);
  check(JSON.stringify(await page.eval(tokenRange('の'))) === '[600000,1000000]', '两次撤销，元素回到原来的边界');

  // 7. 时间轴翻译行轨与主歌词参考轨
  const doubleClickAt = async (x, y) => {
    await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y });
    for (const clickCount of [1, 2]) {
      await page.send('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', clickCount });
      await page.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', clickCount });
    }
  };
  const [CHINESE_Y, ENGLISH_Y] = [geometry.top + 183, geometry.top + 205];
  before2 = await rev();
  await selectRow(page, 3, '三行');
  await clickAt(xAt(1_000_000), ENGLISH_Y);
  await sleep(300);
  check(
    (await page.eval(branchName)) === '主歌词' && (await page.eval(lineTitle)) === '第 3 行' && (await rev()) === before2,
    '翻译行轨：单击翻译行没有动作，仍在主歌词，选择不变',
  );
  const englishDrag = async (fromUs, toUs) => {
    const [x0, x1] = [xAt(fromUs), xAt(toUs)];
    await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: x0, y: ENGLISH_Y });
    await mouse('mousePressed', x0, ENGLISH_Y);
    for (let i = 1; i <= 5; i++) await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: x0 + ((x1 - x0) * i) / 5, y: ENGLISH_Y, button: 'left' });
    await mouse('mouseReleased', x1, ENGLISH_Y);
  };
  await englishDrag(1_500_000, 1_600_000);
  await page.waitFor(atRevision(before2 + 1), '拖英文翻译行的终点', 10_000);
  check((await page.eval(branchName)) === '主歌词' && (await page.eval(lineTitle)) === '第 3 行', '翻译行轨：拖独立计时的翻译行终点，提交一条历史，不切换来源');
  await snapshot(page, 'line-tracks');

  await doubleClickAt(xAt(2_400_000), CHINESE_Y);
  await page.waitFor(`${branchName} === '中文' && ${lineTitle} === '第 2 行' && ${lineText} === '风声'`, '双击中文翻译行轨', 10_000);
  check(true, '翻译行轨：双击跟随主歌词的虚线框，切到中文翻译并选中“风声”');
  // 画布变矮，顶边跟着移动：重新量。看翻译时从上到下：标尺、波形、元素 94～128、行 128～150、主歌词参考轨 150～172。
  const translationGeometry = await page.eval(`(() => { const r = document.querySelector('.timeline-canvas canvas').getBoundingClientRect(); return { top: r.top, height: r.height }; })()`);
  check(translationGeometry.height === 172, `看翻译时没有注音轨，行轨下面有主歌词参考轨：画布高 ${translationGeometry.height}（标尺 22＋波形 72＋元素 34＋行 22＋参考 22）`);
  await clickAt(xAt(900_000), translationGeometry.top + 139);
  await page.waitFor(`${lineTitle} === '第 1 行'`, '点行轨里跟随主歌词的虚线框', 10_000);
  check(true, '行轨：跟随主歌词的“晨光”画在主歌词行的位置，单击选中它');
  await snapshot(page, 'reference-track');
  await doubleClickAt(xAt(2_400_000), translationGeometry.top + 161);
  await page.waitFor(`${branchName} === '主歌词' && ${lineTitle} === '第 2 行'`, '双击主歌词参考轨', 10_000);
  check(true, '主歌词参考轨：双击切回主歌词并选中“風声”');

  await page.waitFor(`document.querySelector('.timeline-canvas canvas').getBoundingClientRect().height === 216`, '回到主歌词，画布恢复原高');
  await doubleClickAt(xAt(1_000_000), ENGLISH_Y);
  await page.waitFor(`${branchName} === '英文' && ${lineTitle} === '第 1 行'`, '双击英文翻译行轨', 10_000);
  const englishRange = `document.querySelector('.lhead .tnum')?.textContent`;
  check((await page.eval(englishRange)) === '0:00.300 – 0:01.600', `英文翻译第 1 行的时间是拖动后的 0.3～1.6 秒（${await page.eval(englishRange)}）`);
  await undo(page);
  await page.waitFor(`${englishRange} === '0:00.300 – 0:01.500'`, '撤销拖动翻译行', 10_000);
  check(true, '一次撤销，英文翻译行回到 0.3～1.5 秒');

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
    const failure = join(tmpdir(), 'mako-lyrics-smoke-failure.png');
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
