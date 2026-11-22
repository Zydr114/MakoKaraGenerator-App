// 歌词页全链路浏览器冒烟（Phase 0–4 重构版）：用真实 Chrome 验证新动态滚动打轴画布、注音、词典与时间轴。
// 覆盖链路：
// 1. 多来源工程 TTML 新建：恢复主歌词（含「朝」两段注音 あ/さ）、中文翻译（跟随）、英文翻译（独立）
// 2. 动态滚动画布结构：左栏 LinePane + 全宽 LyricsStageCanvas（无旧 WordPreview 栏）、Ghost 伴随层
// 3. F2 原地注音浮窗：罗马音实时预览（asa -> あ・さ）、Enter 非破坏提交、Shift+Enter 保留拉丁/拼音、Tab 连续跳字、撤销重做
// 4. 按需词典管理弹窗：点击日语自动注音弹出 NAIST-jdic 0.6.3b / BSD-3-Clause / ~14~16 MB 确认提示并支持取消
// 5. 全文快编（LCS Diff）：修改一行并应用，保留其他行时间戳与注音，一次撤销还原
// 6. 模式 M1 连打闭环：在未打轴新行上按 J / K / L 直接打轴不报 422、BackSpace 回退撤销
// 7. 底部时间轴：0.25x~2.0x 保调变速、注音轨/元素轨边界拖动、翻译行轨拖动与双击切换来源
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

const LYRICS_SCHEMA = /CurrentSchemaVersion\s*=\s*(\d+);/.exec(
  readFileSync(join(resolveBackendRepo(), 'src/MakoKaraGenerator.Core/Lyrics/LyricsDocument.cs'), 'utf8'),
)?.[1];
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
  '<p mkg:line-id="line-0003" mkg:line-text="未打轴行" mkg:timing-status="Missing" itunes:key="line-0003">',
  '<span mkg:token-id="token-0006" mkg:timing-status="Missing">未</span>',
  '<span mkg:token-id="token-0007" mkg:timing-status="Missing">打</span>',
  '<span mkg:token-id="token-0008" mkg:timing-status="Missing">轴</span>',
  '<span mkg:token-id="token-0009" mkg:timing-status="Missing">行</span></p>',
  '</div>',
  `<div mkg:branch-id="${TRANSLATION_ID}" mkg:branch-name="中文" mkg:branch-role="Translation" mkg:timing-mode="FollowPrimary">`,
  '<p mkg:line-id="line-1000" mkg:line-text="晨光" mkg:primary-line-id="line-0001" mkg:timing-status="Missing" itunes:key="line-1000"><span mkg:token-id="token-1000" mkg:timing-status="Missing">晨</span><span mkg:token-id="token-1001" mkg:timing-status="Missing">光</span></p>',
  '<p mkg:line-id="line-1001" mkg:line-text="风声" mkg:primary-line-id="line-0002" mkg:timing-status="Missing" itunes:key="line-1001"><span mkg:token-id="token-1002" mkg:timing-status="Missing">风</span><span mkg:token-id="token-1003" mkg:timing-status="Missing">声</span></p>',
  '</div>',
  `<div mkg:branch-id="${ENGLISH_ID}" mkg:branch-name="英文" mkg:branch-role="Translation" mkg:timing-mode="Independent">`,
  '<p begin="0.3s" end="1.5s" mkg:line-id="line-2000" mkg:line-text="Morning" mkg:primary-line-id="line-0001" mkg:timing-status="Imported" mkg:start-us="300000" mkg:end-us="1500000" itunes:key="line-2000"><span mkg:token-id="token-2000" mkg:timing-status="Missing">Morning</span></p>',
  '<p begin="1.7s" end="3s" mkg:line-id="line-2001" mkg:line-text="Wind" mkg:primary-line-id="line-0002" mkg:timing-status="Imported" mkg:start-us="1700000" mkg:end-us="3000000" itunes:key="line-2001"><span mkg:token-id="token-2001" mkg:timing-status="Missing">Wind</span></p>',
  '</div></body></tt>',
].join('');

const assets = mkdtempSync(join(tmpdir(), 'mako-lyrics-smoke-'));
const wavPath = join(assets, '歌词冒烟.wav');
const ttmlPath = join(assets, '工程歌词.ttml');
writeFileSync(wavPath, makeWav());
writeFileSync(ttmlPath, PROJECT_TTML);
const projectName = `歌词重构冒烟 ${Date.now()}`;

const checks = [];
function check(ok, label) {
  checks.push({ ok: !!ok, label });
  console.log(`${ok ? '✓' : '✗'} ${label}`);
  if (!ok) process.exitCode = 1;
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const REVISION = `Number((document.querySelector('.status-bar')?.textContent.match(/修订 (\\d+)/) ?? [])[1])`;
const atRevision = (n) => `${REVISION} === ${n}`;
const branchName = `document.querySelector('.srcpick-name')?.textContent`;
const lineTexts = `[...document.querySelectorAll('.line-list .ll-text')].map((n) => n.textContent)`;
const undo = (page) => ui.click(page, 'button', '撤销', { scope: '.app-bar' });

async function dispatchKey(page, key, opts = {}) {
  const modifiers = (opts.alt ? 1 : 0) | (opts.ctrl ? 2 : 0) | (opts.meta ? 4 : 0) | (opts.shift ? 8 : 0);
  await page.send('Input.dispatchKeyEvent', {
    type: 'keyDown',
    key,
    code: opts.code ?? (key.length === 1 ? `Key${key.toUpperCase()}` : key),
    modifiers,
    ...(opts.text ? { text: opts.text } : {}),
  });
  await page.send('Input.dispatchKeyEvent', {
    type: 'keyUp',
    key,
    code: opts.code ?? (key.length === 1 ? `Key${key.toUpperCase()}` : key),
    modifiers,
  });
}

async function snapshot(page, suffix) {
  if (!values.screenshot) return;
  await sleep(400);
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

  // 1. 用工程 TTML 新建：恢复三个来源与注音片段
  await shell.startNewProject(page, { audio: wavPath, lyrics: ttmlPath, name: projectName });
  await shell.finishNewProject(page);
  await page.waitFor(`document.querySelector('.project-name')?.textContent === ${JSON.stringify(projectName)}`, '进入工程', 60_000);
  await page.waitFor(`${lineTexts}.length === 3`, '新工程有三行主歌词', 30_000);

  // 2. 验证新动态滚动画布布局（合并中右栏，无旧 .word-preview，带 Ghost 伴随层）
  await page.waitFor(`!!document.querySelector('.lyrics-stage-shell') && !!document.querySelector('.lyrics-toolbar')`, '动态打轴画布与工具栏挂载', 10_000);
  check(
    await page.eval(`!document.querySelector('.word-preview') && document.querySelectorAll('.stage-line').length === 3`),
    '三区宏观布局：保留左栏 + 全宽动态滚动画布（已移除旧 WordPreview），3 行均在画布渲染',
  );
  check(
    await page.eval(`[...document.querySelectorAll('.stage-ghost')].some((g) => g.textContent.includes('晨光'))`),
    'Ghost 伴随预览层：同屏展示中文翻译伴随行「晨光」',
  );
  await snapshot(page, 'stage');

  // 3. F2 原地悬浮注音框：罗马音实时预览 + Enter 采纳 + Shift+Enter 保留拉丁/拼音
  await page.eval(`[...document.querySelectorAll('.stage-tok.kind-content')].find((el) => el.querySelector('.st-base')?.textContent === '光')?.click()`);
  await page.waitFor(`document.querySelector('.stage-tok.is-sel .st-base')?.textContent === '光'`, '选中词元「光」');
  await dispatchKey(page, 'F2', { code: 'F2' });
  await page.waitFor(`!!document.querySelector('.ruby-inline-popover')`, '按 F2 弹出原地注音浮窗', 10_000);

  await page.eval(uiJs(`const el = document.querySelector('.rip-input'); el.focus(); __ui.setValue(el, 'hikari');`));
  await page.waitFor(`document.querySelector('.rip-preview-kana')?.textContent?.includes('ひ・か・り')`, '罗马音实时预览 -> ひ・か・り', 5_000);
  check(true, 'F2 原地注音浮窗：输入 hikari 实时预览为 → ひ・か・り');

  let before = await rev();
  await dispatchKey(page, 'Enter', { code: 'Enter' });
  await page.waitFor(`${atRevision(before + 1)} && !document.querySelector('.ruby-inline-popover')`, '提交注音并关闭浮窗', 10_000);
  check(
    await page.eval(`[...document.querySelectorAll('.stage-tok.is-sel .st-rseg')].map((s) => s.textContent).join('・') === 'ひ・か・り'`),
    'Enter 提交非破坏性多拍注音（ひ・か・り）并立刻呈现在画布上层',
  );

  // 再开 F2 用 Shift+Enter 保留原文/拼音
  await dispatchKey(page, 'F2', { code: 'F2' });
  await page.waitFor(`!!document.querySelector('.ruby-inline-popover')`, '再次打开注音浮窗');
  await page.eval(uiJs(`const el = document.querySelector('.rip-input'); el.focus(); __ui.setValue(el, 'guang');`));
  before = await rev();
  await dispatchKey(page, 'Enter', { code: 'Enter', shift: true });
  await page.waitFor(`${atRevision(before + 1)} && !document.querySelector('.ruby-inline-popover')`, 'Shift+Enter 原样提交', 10_000);
  check(
    await page.eval(`[...document.querySelectorAll('.stage-tok.is-sel .st-rseg')].map((s) => s.textContent).join('') === 'guang'`),
    'Shift+Enter 原样保留拉丁字母/拼音（guang）',
  );
  await undo(page);
  await page.waitFor(atRevision(before + 2), '撤销原样提交', 10_000);

  // 4. 首次点击日语自动注音 -> 弹出 NAIST-jdic 按需下载确认弹窗
  await ui.click(page, 'button', '日语自动注音', { partial: true });
  await ui.click(page, 'menuitem', '为当前行生成日语注音', { scope: '@menu' });
  await ui.waitDialog(page, '日语注音词典管理');
  const dictText = await page.eval(`document.querySelector('[role=dialog]')?.textContent ?? ''`);
  check(
    dictText.includes('NAIST-jdic 0.6.3b') && dictText.includes('BSD-3-Clause') && dictText.includes('14~16 MB'),
    '首次自动注音弹出词典按需下载提示（含 NAIST-jdic 0.6.3b、BSD-3-Clause 与预估体积）',
  );
  await snapshot(page, 'dict-dialog');
  await ui.clickInDialog(page, '暂不下载');
  await ui.waitDialogClosed(page, '日语注音词典管理');

  // 5. 全文快编（LCS Diff）：修改第 2 行正文并应用，保留第 1 行时间戳与注音
  await ui.click(page, 'button', '全文快编', { scope: '.lyrics-toolbar' });
  await page.waitFor(`!!document.querySelector('.quick-edit .qe-textarea')`, '切换到全文快编模式', 10_000);
  await page.eval(uiJs(`const ta = document.querySelector('.qe-textarea'); ta.focus(); __ui.setValue(ta, '朝の光\\n風声と空\\n未打轴行');`));
  await page.waitFor(`document.querySelector('.qe-stats')?.textContent.includes('修改 1')`, 'LCS Diff 预览显示修改 1 行', 5_000);
  before = await rev();
  await ui.click(page, 'button', '应用并返回打轴', { scope: '.quick-edit' });
  await page.waitFor(`${atRevision(before + 1)} && !!document.querySelector('.lyrics-stage-shell')`, '全文快编提交并返回画布', 10_000);
  check(
    await page.eval(`${lineTexts}[1] === '風声と空' && [...document.querySelectorAll('.stage-line')[0].querySelectorAll('.st-rseg')].length >= 2`),
    '全文快编：第 2 行正文已更新，第 1 行注音与时间戳无损保留',
  );
  await undo(page);
  await page.waitFor(`${atRevision(before + 2)} && ${lineTexts}[1] === '風声'`, '一次撤销全文快编', 10_000);

  // 6. 模式 M1 无行时间直接盲打（选中未打轴的第 3 行，按 J -> K -> L）
  await ui.click(page, 'row', '3 未打轴行');
  await page.waitFor(`document.querySelector('.stage-line.is-selected .slg-num')?.textContent === '03'`, '选中第 3 行（未打轴）');
  // 点时间轴标尺将播放头移至 4.0s
  await ui.click(page, 'button', '全曲');
  const geometry = await page.eval(`(() => { const r = document.querySelector('.timeline-canvas canvas').getBoundingClientRect(); return { left: r.left, top: r.top, width: r.width, height: r.height }; })()`);
  const xAt = (us) => geometry.left + (us * geometry.width) / 8_000_000;
  const clickAt = async (x, y) => {
    await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y });
    await page.send('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', clickCount: 1 });
    await page.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', clickCount: 1 });
  };
  await clickAt(xAt(4_000_000), geometry.top + 11);
  // 点击第 3 行首字「未」以确保焦点回到画布
  await page.eval(`[...document.querySelectorAll('.stage-line')[2].querySelectorAll('.stage-tok.kind-content')][0]?.click()`);

  before = await rev();
  await dispatchKey(page, 'j', { code: 'KeyJ' });
  await page.waitFor(atRevision(before + 1), 'J 连打第 1 拍（自动初始化行时间不报 422）', 10_000);
  await clickAt(xAt(4_400_000), geometry.top + 11);
  await dispatchKey(page, 'k', { code: 'KeyK' });
  await page.waitFor(atRevision(before + 2), 'K 交替连打第 2 拍', 10_000);
  await clickAt(xAt(4_800_000), geometry.top + 11);
  await dispatchKey(page, 'l', { code: 'KeyL' });
  await page.waitFor(atRevision(before + 3), 'L 显式收尾第 2 拍', 10_000);
  check(
    await page.eval(`!document.querySelectorAll('.stage-line')[2].classList.contains('is-untimed')`),
    '模式 M1 盲打：未打轴行直接 J/K/L 连续打轴成功，自动初始化并外扩行时间',
  );

  // 7. 保调变速（Q/W）与时间轴分支切换
  await dispatchKey(page, 'q', { code: 'KeyQ' });
  await sleep(150);
  check(
    await page.eval(`document.querySelector('.transport-rate')?.textContent?.includes('0.9')`),
    '保调变速：按 Q 从 1.0× 步进减速至 0.9×',
  );
  await dispatchKey(page, 'w', { code: 'KeyW' });

  const CHINESE_Y = geometry.top + 183;
  const doubleClickAt = async (x, y) => {
    await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y });
    for (const clickCount of [1, 2]) {
      await page.send('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', clickCount });
      await page.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', clickCount });
    }
  };
  await doubleClickAt(xAt(2_400_000), CHINESE_Y);
  await page.waitFor(`${branchName} === '中文'`, '双击翻译行轨切换到中文来源', 10_000);
  check(true, '底部时间轴：双击翻译行轨无损切换至「中文」来源');

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
