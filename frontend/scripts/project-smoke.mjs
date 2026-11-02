// 工程闭环与导出的浏览器冒烟（W5 设计 §4 第 11 步）：用真实 Chrome 走一遍 W5 的主链路。
// 首页目录浏览与搜索 → 新建空工程 → 导入歌词、选择音频、改名 → 背景图片：适配、移除、撤销
// → 保存到…、关闭、首页单击选中看详情再打开 → 另存为（目标是已有工程时写明覆盖） → 导出歌词文件（评估、接受损失、下载）
// → 导出页：修复入口（输出时长）、正式验证、导出 MP4 并下载、再导出一次后取消 → 只读打开
// → 只有歌词、只有音频的新建 → 背景视频与播放策略、替换音频、移除音频与撤销 → 从备份恢复一份。
//
// 前提：pnpm dev:backend 与 pnpm dev 都在运行。
// 用法：node scripts/project-smoke.mjs [--url http://localhost:5173] [--screenshot out.png] [--headed]

import { execFileSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { parseArgs } from 'node:util';
import { deflateSync } from 'node:zlib';
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

// ── 素材：4 秒和 6 秒的 WAV、两行逐字打好轴的增强 LRC、一张 64×36 的 PNG、1 秒的 MP4（本机有 ffmpeg 时）──

function makeWav(seconds = 4, sampleRate = 22050) {
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
    buffer.writeInt16LE(Math.round(Math.sin(2 * Math.PI * 440 * t) * 8000 * Math.exp(-(t % 1) * 3)), 44 + i * 2);
  }
  return buffer;
}

function makePng(width = 64, height = 36) {
  const crcTable = Array.from({ length: 256 }, (_, n) => {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    return c >>> 0;
  });
  const crc = (bytes) => {
    let c = 0xffffffff;
    for (const b of bytes) c = crcTable[(c ^ b) & 0xff] ^ (c >>> 8);
    return (c ^ 0xffffffff) >>> 0;
  };
  const chunk = (type, data) => {
    const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
    const out = Buffer.alloc(body.length + 8);
    out.writeUInt32BE(data.length, 0);
    body.copy(out, 4);
    out.writeUInt32BE(crc(body), body.length + 4);
    return out;
  };
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header.set([8, 2, 0, 0, 0], 8);
  const rows = Buffer.alloc((width * 3 + 1) * height);
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) rows.set([40 + x * 3, 80, 160 + y * 2], y * (width * 3 + 1) + 1 + x * 3);
  }
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', header), chunk('IDAT', deflateSync(rows)), chunk('IEND', Buffer.alloc(0))]);
}

const assets = mkdtempSync(join(tmpdir(), 'mako-project-smoke-'));
const downloads = join(assets, 'downloads');
mkdirSync(downloads);
const wavPath = join(assets, '冒烟音频.wav');
const lrcPath = join(assets, '冒烟逐字.lrc');
const pngPath = join(assets, '冒烟背景.png');
const wav6Path = join(assets, '冒烟音频-6秒.wav');
const mp4Path = join(assets, '冒烟背景.mp4');
writeFileSync(wavPath, makeWav());
writeFileSync(wav6Path, makeWav(6));
writeFileSync(lrcPath, '[00:00.20]<00:00.20>朝<00:00.60>の<00:01.00>光<00:01.40>\n[00:01.60]<00:01.60>你<00:02.20>好<00:03.00>\n');
writeFileSync(pngPath, makePng());
let hasVideo = true;
try {
  execFileSync('ffmpeg', ['-loglevel', 'error', '-f', 'lavfi', '-i', 'testsrc=size=160x90:rate=10:duration=1', '-pix_fmt', 'yuv420p', mp4Path]);
} catch {
  hasVideo = false;
}
const stamp = Date.now();
const directory = `project-smoke-${stamp}`;
const projectName = `闭环冒烟 ${stamp}`;

// ── 页面操作 ──

const checks = [];
function check(ok, label) {
  checks.push({ ok: !!ok, label });
  console.log(`${ok ? '✓' : '✗'} ${label}`);
  if (!ok) process.exitCode = 1;
}

const revision = `(document.querySelector('.status-bar')?.textContent.match(/修订 (\\d+)/) ?? [])[1]`;

const setFile = ui.setFile;

async function gotoPage(page, value, ready) {
  await shell.goto(page, value);
  await page.waitFor(ready, `切到「${value}」页`);
}

/** 等下载目录里出现 name（Chrome 先写 .crdownload，完成后改名）。 */
async function waitDownload(name, timeoutMs = 30_000) {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    if (readdirSync(downloads).includes(name)) return statSync(join(downloads, name)).size;
    await new Promise((r) => setTimeout(r, 200));
  }
  throw new Error(`等待下载超时：${name}（目录里有 ${readdirSync(downloads).join('、') || '无'}）`);
}

/** 更多菜单“关闭工程”；有未保存的修改时选“不保存”。 */
async function closeCurrentProject(page) {
  await shell.closeProject(page);
  await page.waitFor(`!!document.querySelector('.home')`, '回到首页', 60_000);
}

/** 在首页用新建对话框建工程；audio、lyrics 可以都不给。 */
const newProject = (page, files) => shell.newProject(page, files);
const fileCheck = shell.fileCheck;

/** 设置面板里某一项（dt）的取值。 */
const settingsFact = (group, label) =>
  `[...document.querySelectorAll('[data-group="${group}"] .settings-facts > div')].find((d) => d.querySelector('dt')?.textContent === ${JSON.stringify(label)})?.querySelector('dd')?.textContent`;

async function snapshot(page, suffix) {
  if (!values.screenshot) return;
  await new Promise((resolve) => setTimeout(resolve, 700));
  await writeFile(values.screenshot.replace(/\.png$/, `-${suffix}.png`), await page.screenshot());
}

const lineCount = `document.querySelectorAll('.line-list [role=row]').length`;
const settingsOpen = `!!document.querySelector('.project-settings:not([data-exiting])')`;
const settingsClosed = `!document.querySelector('.project-settings')`;
/** 等下拉框显示 text（设置在修订变化之后才重新读回来）；超时返回 false。 */
const selectShows = (page, label, text, scope) =>
  page
    .waitFor(uiJs(`return __ui.find('select', ${JSON.stringify(label)}, false, ${JSON.stringify(scope)})?.textContent.trim() === ${JSON.stringify(text)};`), `下拉「${label}」显示「${text}」`, 10_000)
    .then(() => true, () => false);

async function closeSettings(page) {
  await ui.click(page, 'button', '关闭工程设置');
  await page.waitFor(settingsClosed, '关闭工程设置');
}
const check$ = (id) => `document.querySelector('[data-check="${id}"]')?.dataset.level`;

const browser = await launchChrome({ headed: values.headed });
let page;
const errors = [];
try {
  browser.cdp.on((message) => {
    if (message.method === 'Runtime.exceptionThrown') errors.push(message.params.exceptionDetails.exception?.description ?? 'exception');
    if (message.method === 'Runtime.consoleAPICalled' && message.params.type === 'error') errors.push(message.params.args.map((a) => a.value ?? a.description).join(' '));
  });
  await browser.cdp.send('Browser.setDownloadBehavior', { behavior: 'allow', downloadPath: downloads });

  page = await browser.openPage(`${ORIGIN}/`, { width: 1440, height: 900 });
  await page.send('DOM.enable');
  await page.waitFor(shell.connected, '连接 Server');

  // 1. 首页：工程目录浏览与搜索，新建空工程
  await page.waitFor(`!!document.querySelector('.directory-list') && !document.querySelector('.directory-progress')`, '工程目录读完', 30_000);
  check(await page.eval(`!!document.querySelector('.breadcrumb') && !!document.querySelector('.home-detail')`), '首页显示授权目录的路径导航与列表，右边是详情');
  await ui.fill(page, '搜索这一层', `不存在-${stamp}`);
  await page.waitFor(`document.querySelector('.directory-empty')?.textContent.includes('没有名称包含')`, '搜索没有结果');
  await ui.fill(page, '搜索这一层', '');
  check(true, '按名称搜索这一层，没有结果时写明');

  await ui.click(page, 'button', '新建工程');
  await ui.waitDialog(page, '新建工程');
  check(
    (await page.eval(`${fileCheck('audio')} === 'none' && ${fileCheck('lyrics')} === 'none'`)) && (await ui.exists(page, 'button', '下一步', { scope: '@dialog' })),
    '新建工程第一步：文件检查写明没有选音频、歌词',
  );
  await ui.fill(page, '工程名', projectName);
  await ui.clickInDialog(page, '下一步');
  await ui.clickInDialog(page, '新建');
  await page.waitFor(`!!document.querySelector('.no-audio-bar') && !!document.querySelector('.lp-empty')`, '空工程进入歌词页', 60_000);
  check((await page.eval(revision)) === '0', '不选音频和歌词也能新建：歌词页显示导入入口，底部显示“还没有音频”');
  await snapshot(page, 'empty');

  // 2. 导入歌词、选择音频、改名
  await ui.click(page, 'button', '导入歌词文件…', { scope: '.lp-empty' });
  await ui.waitDialog(page, '导入歌词文件');
  await ui.setFile(page, '选择歌词文件', lrcPath);
  await page.waitFor(`['ok', 'warn'].includes(document.querySelector('[role=dialog] .file-check [data-check="lyrics"]')?.dataset.state)`, '识别结果', 30_000);
  await ui.choose(page, '拆分文本单元', '不拆分：保持文件原有粒度', { scope: '@dialog' });
  await ui.clickInDialog(page, '导入');
  await ui.waitDialog(page, '导入报告：', { partial: true, timeout: 30_000 });
  await ui.clickInDialog(page, '关闭');
  await page.waitFor(`${lineCount} === 2`, '导入后 2 行');
  check(true, '从空态导入歌词文件（2 行）');

  await setFile(page, '选择音频…', wavPath);
  await page.waitFor(`!document.querySelector('.no-audio-bar') && !!document.querySelector('.transport-time')`, '选择音频后出现播放控制', 90_000);
  check(true, '从底部“选择音频…”换入音频，出现播放控制与时间轴');

  await ui.click(page, 'button', '工程设置');
  await page.waitFor(settingsOpen, '打开工程设置');
  await ui.fill(page, '名称', `${projectName} 改`, { scope: '.project-settings', enter: true });
  await page.waitFor(`document.querySelector('.project-name')?.textContent === ${JSON.stringify(`${projectName} 改`)}`, '改名生效', 20_000);
  check(await page.eval(`document.querySelector('.audio-file')?.textContent === '冒烟音频.wav'`), '设置里改名一条历史；音频显示本次上传的文件名');

  // 3. 背景图片：适配、移除、撤销
  const revisionOf = `Number(${revision})`;
  await setFile(page, '选择图片或视频…', pngPath);
  await page.waitFor(`!!document.querySelector('img.background-thumb')`, '背景缩略图', 60_000);
  const beforeFit = await page.eval(revisionOf);
  await ui.choose(page, '适配方式', '铺满', { scope: '.project-settings' });
  await page.waitFor(`${revisionOf} === ${beforeFit + 1}`, '改适配方式');
  await ui.click(page, 'button', '移除', { scope: '[data-group="background"]' });
  await page.waitFor(`!document.querySelector('img.background-thumb') && ${revisionOf} === ${beforeFit + 2}`, '移除背景媒体');
  // 面板是模态的，用面板标题栏里的撤销
  await ui.click(page, 'button', '撤销', { scope: '.project-settings' });
  await page.waitFor(`!!document.querySelector('img.background-thumb')`, '撤销后背景回来', 30_000);
  check(await selectShows(page, '适配方式', '铺满', '.project-settings'), '背景图片：选择、改适配（一条历史）、移除、在面板里撤销后恢复为“铺满”');
  await snapshot(page, 'settings');
  await closeSettings(page);

  // 4. 保存到…、关闭、从工程目录打开
  const saveRows = `!!document.querySelector('.save-browser .directory-row') && !document.querySelector('.save-browser .directory-progress')`;
  await ui.click(page, 'button', '保存工程');
  await ui.waitDialog(page, '保存到');
  await page.waitFor(saveRows, '“保存到…”对话框列出目录', 30_000);
  await ui.fill(page, '目录名', directory);
  await page.waitFor(`document.querySelector('.save-target')?.dataset.kind === 'new'`, '目标写明“会新建目录”');
  await ui.clickInDialog(page, '保存');
  await page.waitFor(`document.querySelector('.project-state')?.textContent === '已保存'`, '保存完成', 60_000);
  await shell.projectMenu(page, '关闭工程');
  await page.waitFor(`!!document.querySelector('.home')`, '回到首页', 60_000);
  const rowScope = (path) => `.directory-row[data-path=${JSON.stringify(path)}]`;
  const directoryRow = `document.querySelector('${rowScope(directory)} .directory-main')`;
  await page.waitFor(`!!(${directoryRow}) && !(${directoryRow}).disabled`, '工程目录列出刚保存的工程', 30_000);
  // 单击只选中，右边详情写出位置；用详情里的“打开”
  await ui.click(page, 'button', directory, { scope: rowScope(directory), partial: true });
  await page.waitFor(`document.querySelector('.home-detail h2')?.textContent === ${JSON.stringify(directory)} && document.querySelector('${rowScope(directory)}').classList.contains('is-highlight')`, '单击选中工程');
  check(await page.eval(`!!document.querySelector('.home') && ${lineCount} === 0`), '单击工程只选中：右边详情显示名称和位置，不打开');
  await ui.click(page, 'button', '打开', { scope: '.home-detail' });
  await page.waitFor(`${lineCount} === 2 && document.querySelector('.project-name')?.textContent === ${JSON.stringify(`${projectName} 改`)}`, '重开工程', 120_000);
  check(true, `保存到新目录 ${directory}、关闭、从首页详情打开（名称与歌词一致）`);

  // 4b. 另存为：选中别的工程时写明“覆盖”（不真的覆盖）；换成新目录名另存
  const savedAs = `${directory}-另存`;
  await shell.projectMenu(page, '另存为…');
  await ui.waitDialog(page, '另存为');
  await page.waitFor(saveRows, '另存为对话框列出目录', 30_000);
  const otherProject = await page.eval(
    `[...document.querySelectorAll('.save-browser .directory-row.is-project')].find((r) => r.dataset.path !== ${JSON.stringify(directory)})?.dataset.path ?? ''`,
  );
  if (otherProject) {
    await ui.click(page, 'button', otherProject, { scope: `.save-browser ${rowScope(otherProject)}`, partial: true });
    await page.waitFor(`document.querySelector('.save-target')?.dataset.kind === 'project'`, '目标写明“覆盖已有工程”');
    check(await ui.exists(page, 'button', '覆盖', { scope: '@dialog' }), '另存为：选中别的工程时写明会覆盖，按钮变为“覆盖”');
  } else {
    check(true, '跳过覆盖提示：授权目录里没有别的工程');
  }
  await ui.fill(page, '目录名', savedAs);
  await page.waitFor(`document.querySelector('.save-target')?.dataset.kind === 'new'`, '另存为的目标写明“会新建目录”');
  await ui.clickInDialog(page, '保存');
  await page.waitFor(`!document.querySelector('.save-location-dialog') && document.querySelector('.project-state')?.textContent === '已保存'`, '另存为完成', 60_000);
  check(true, `另存为到新目录 ${savedAs}`);

  // 5. 导出歌词文件
  await shell.projectMenu(page, '导出歌词文件…');
  await ui.waitDialog(page, '导出歌词文件');
  await page.waitFor(`!!document.querySelector('.lyrics-export-dialog .lyrics-export-form')`, '导出歌词对话框');
  await ui.clickInDialog(page, '评估');
  await page.waitFor(`!!document.querySelector('.lyrics-export-plan')`, '评估结果', 30_000);
  if (await ui.exists(page, 'checkbox', '接受这些损失', { scope: '@dialog' })) {
    check(await ui.read(page, 'button', '导出并下载', 'return __ui.disabled(el);', { scope: '@dialog' }), '有损失时不勾选“接受这些损失”不能导出');
    await ui.setChecked(page, 'checkbox', '接受这些损失', true, { scope: '@dialog' });
  }
  await ui.clickInDialog(page, '导出并下载');
  await page.waitFor(`!!document.querySelector('.lyrics-export-done')`, '歌词导出完成', 30_000);
  const reported = Number((await page.eval(`document.querySelector('.lyrics-export-done').textContent`)).match(/(\d+) 字节/)?.[1]);
  const lyricsSize = await waitDownload(`${projectName} 改.lrc`);
  check(lyricsSize === reported && reported > 0, `导出增强 LRC 并下载，文件大小与报告一致（${lyricsSize} 字节）`);
  await ui.clickInDialog(page, '完成');
  await ui.waitDialogClosed(page, '导出歌词文件');

  // 6. 导出页：修复入口、正式验证、导出 MP4 并下载、再导出一次后取消
  await shell.goto(page, '导出视频');
  await page.waitFor(`${check$('duration')} === 'block' && ${check$('timing')} === 'ok'`, '导出页：输出时长未定是阻断项', 30_000);
  check(await ui.read(page, 'button', '开始导出', 'return __ui.disabled(el);', { scope: '.export-go' }), '准备检查有阻断项时“开始导出”停用');
  await ui.click(page, 'button', '去设置', { scope: '[data-check="duration"]' });
  await page.waitFor(settingsOpen, '修复入口打开工程设置');
  await ui.click(page, 'button', '按音频时长', { scope: '[data-group="duration"]' });
  await page.waitFor(`${check$('duration')} === 'ok'`, '输出时长确定', 20_000);
  await closeSettings(page);
  await page.waitFor(`document.querySelectorAll('.export-thumbnail img').length === 2`, '首帧与末帧缩略图', 60_000);
  await ui.click(page, 'button', '末帧', { scope: '.export-stage' });
  await page.waitFor(`!!document.querySelector('.export-thumbnail:not([hidden]) img[alt^="末帧"]')`, '切到末帧');
  check(true, '修复入口“去设置”打开输出时长一组；确定后阻断解除，幕后区可切换首帧与末帧');

  await ui.fill(page, '检查帧数', '10', { enter: true, scope: '[aria-label="正式验证"]' });
  await ui.click(page, 'button', '开始验证', { scope: '[aria-label="正式验证"]' });
  await page.waitFor(`document.querySelector('[aria-label="正式验证"] .job-result[data-state]')?.textContent.includes('验证通过')`, '正式验证完成', 180_000);
  check(
    (await page.eval(`document.querySelector('[aria-label="正式验证"] .job-result').textContent`)).includes('普通 10 帧') && (await page.eval(check$('validation'))) === 'ok',
    '正式验证 10 帧通过，准备检查里显示“当前修订验证通过”',
  );

  await ui.click(page, 'button', '开始导出', { scope: '.export-go' });
  await page.waitFor(`!!document.querySelector('[aria-label="导出任务"] .job-progress')`, '导出进度');
  await page.waitFor(`document.querySelector('[aria-label="导出任务"] .job-result[data-state="succeeded"]')?.textContent.includes('导出完成')`, '导出完成', 300_000);
  const facts = await page.eval(`document.querySelector('[aria-label="导出任务"] .export-facts').textContent`);
  check(facts.includes('1920 × 1080') && facts.includes('120/120') && facts.includes('有（aac）'), `导出 MP4：报告与设置一致（${facts.slice(0, 80)}…）`);
  await ui.click(page, 'button', '下载', { scope: '[aria-label="导出任务"] .job-result' });
  const videoSize = await waitDownload(`${projectName} 改.mp4`, 60_000);
  check(videoSize > 1000, `下载成片（${videoSize} 字节，文件名用工程名）`);
  await snapshot(page, 'export');

  await ui.click(page, 'button', '重新导出', { scope: '.export-go' });
  await page.waitFor(`!!document.querySelector('[aria-label="导出任务"] .job-progress')`, '第二次导出开始');
  await ui.click(page, 'button', '取消', { scope: '[aria-label="导出任务"] .job-progress' });
  await page.waitFor(`document.querySelector('[aria-label="导出任务"] .job-result[data-state]')?.textContent.includes('已取消')`, '取消后的终态', 60_000);
  check(true, '导出中途取消，状态是“已取消”');
  await ui.click(page, 'button', '返回编辑', { scope: '.app-bar' });
  await page.waitFor(`${lineCount} === 2`, '返回歌词页');

  // 7. 只读打开：关闭工程（不保存导出页里定下的输出时长），从工程目录的更多操作里只读打开
  await closeCurrentProject(page);
  await page.waitFor(`!!(${directoryRow}) && !!document.querySelector('${rowScope(savedAs)}')`, '工程目录列出原工程和另存的工程', 30_000);
  await ui.menu(page, `${directory} 的更多操作`, '只读打开');
  await page.waitFor(`document.querySelector('.project-state')?.textContent === '只读' && ${lineCount} === 2`, '只读打开', 120_000);
  check(await page.eval(`document.querySelector('.project-name')?.textContent === ${JSON.stringify(`${projectName} 改`)}`), '从工程目录“只读打开”：顶栏显示“只读”，名称和歌词与保存时一致');
  await closeCurrentProject(page);

  // 8. 只有歌词、只有音频的新建
  await newProject(page, { lyrics: lrcPath, name: `${projectName} 只有歌词` });
  await page.waitFor(`${lineCount} === 2 && !!document.querySelector('.no-audio-bar') && !document.querySelector('.timeline')`, '只有歌词的工程', 60_000);
  check(true, '只选歌词新建：2 行，没有时间轴，底部显示“还没有音频”');
  await closeCurrentProject(page);

  await newProject(page, { audio: wavPath, name: `${projectName} 只有音频` });
  await page.waitFor(`!!document.querySelector('.lp-empty') && !!document.querySelector('.timeline') && !document.querySelector('.no-audio-bar')`, '只有音频的工程', 120_000);
  check(true, '只选音频新建：歌词页显示导入入口，有播放控制与时间轴');

  // 9. 背景视频与播放策略；替换音频；移除音频与撤销
  await ui.click(page, 'button', '工程设置');
  await page.waitFor(settingsOpen, '打开工程设置');
  if (hasVideo) {
    await setFile(page, '选择图片或视频…', mp4Path);
    await page.waitFor(`document.querySelector('[data-group="background"] .background-info')?.textContent.includes('视频')`, '背景视频', 90_000);
    await ui.wait(page, 'select', '播放', { scope: '.project-settings' });
    const beforePlayback = await page.eval(revisionOf);
    await ui.choose(page, '播放', '停在最后一帧', { scope: '.project-settings' });
    await page.waitFor(`${revisionOf} === ${beforePlayback + 1}`, '改播放策略');
    check(await selectShows(page, '播放', '停在最后一帧', '.project-settings'), '背景视频：选择后显示类型与大小，“停在最后一帧”一条历史');
  } else {
    check(true, '跳过背景视频：本机没有 ffmpeg，造不出测试视频');
  }

  const durationBefore = await page.eval(settingsFact('audio', '时长'));
  await setFile(page, '替换音频…', wav6Path);
  await page.waitFor(`${settingsFact('audio', '时长')} !== ${JSON.stringify(durationBefore)} && document.querySelector('.audio-file')?.textContent === '冒烟音频-6秒.wav'`, '替换音频', 120_000);
  const durationAfter = await page.eval(settingsFact('audio', '时长'));
  check(!!durationAfter && durationAfter !== durationBefore, `替换音频：时长 ${durationBefore} → ${durationAfter}，文件名随之更新`);

  await ui.click(page, 'button', '移除音频', { scope: '[data-group="audio"]' });
  await ui.waitDialog(page, '移除音频？');
  await ui.clickInDialog(page, '移除');
  await page.waitFor(`!!document.querySelector('.no-audio-bar') && !document.querySelector('.timeline')`, '移除音频后时间轴消失', 60_000);
  await ui.click(page, 'button', '撤销', { scope: '.project-settings' });
  await page.waitFor(`!document.querySelector('.no-audio-bar') && !!document.querySelector('.timeline') && ${settingsFact('audio', '时长')} === ${JSON.stringify(durationAfter)}`, '撤销后音频回来', 120_000);
  check(true, '移除音频（先确认）后时间轴消失、底部显示“还没有音频”；在面板里撤销后音频和时间轴回来');
  await closeSettings(page);
  await closeCurrentProject(page);

  // 10. 从备份恢复一份，进入歌词页
  await ui.click(page, 'button', '从备份恢复…');
  await ui.waitDialog(page, '从备份恢复');
  await page.waitFor(`!!document.querySelector('.recovery-item') || !!document.querySelector('.recovery-empty')`, '恢复对话框读完备份', 30_000);
  const candidates = await page.eval(`document.querySelectorAll('.recovery-item').length`);
  if (candidates > 0) {
    const recovered = await page.eval(`document.querySelector('.recovery-item .recovery-name').textContent`);
    await ui.click(page, 'row', recovered, { scope: '@dialog', partial: true });
    await page.waitFor(`document.querySelector('.recovery-item')?.getAttribute('aria-selected') === 'true'`, '选中最新的备份');
    await ui.clickInDialog(page, '恢复');
    await page.waitFor(`document.querySelector('.project-name')?.textContent === ${JSON.stringify(recovered)} && (!!document.querySelector('.line-list') || !!document.querySelector('.lp-empty'))`, '恢复后进入歌词页', 180_000);
    check(true, `“从备份恢复…”列出 ${candidates} 份备份；恢复最新的一份（${recovered}）并进入歌词页`);
    await closeCurrentProject(page);
  } else {
    check(true, '“从备份恢复…”写明没有可以恢复的备份（profile 是新的）');
    await ui.clickInDialog(page, '取消');
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
    const failure = join(tmpdir(), 'mako-project-smoke-failure.png');
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
