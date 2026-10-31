// 工程闭环与导出的浏览器冒烟（W5 设计 §4 第 11 步）：用真实 Chrome 走一遍 W5 的主链路。
// 首页目录浏览与搜索 → 新建空工程 → 导入歌词、选择音频、改名 → 背景图片：适配、移除、撤销
// → 保存到…、关闭、从工程目录打开 → 另存为（目标是已有工程时写明覆盖） → 导出歌词文件（评估、接受损失、下载）
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

const PRELUDE = `
  const buttons = () => [...document.querySelectorAll('mdui-button, mdui-button-icon')];
  const button = (text) => buttons().find((b) => b.textContent.trim() === text || b.getAttribute('aria-label') === text);
  const field = (label, scope = '') => [...document.querySelectorAll(scope + ' mdui-text-field')].find((f) => f.label === label);
  const revision = () => (document.querySelector('.status-bar')?.textContent.match(/修订 (\\d+)/) ?? [])[1];
`;
const js = (body) => `(() => { ${PRELUDE} ${body} })()`;
const click = `dispatchEvent(new MouseEvent('click', { bubbles: true, composed: true }))`;

async function clickButton(page, text) {
  await page.waitFor(js(`const b = button(${JSON.stringify(text)}); return !!b && !b.disabled;`), `按钮「${text}」可用`);
  await page.eval(js(`button(${JSON.stringify(text)}).click();`));
}

/** 点 scope 容器里文字为 text 的按钮（页面上有同名按钮时用）。 */
async function clickButtonIn(page, scope, text) {
  const target = `[...document.querySelectorAll(${JSON.stringify(`${scope} mdui-button`)})].find((b) => b.textContent.trim() === ${JSON.stringify(text)})`;
  await page.waitFor(`!!(${target}) && !(${target}).disabled`, `「${text}」可用`);
  await page.eval(`(${target}).click()`);
}

async function clickMenuItem(page, trigger, text) {
  await clickButton(page, trigger);
  const item = js(
    `const d = button(${JSON.stringify(trigger)})?.closest('mdui-dropdown'); return d?.open ? [...d.querySelectorAll('mdui-menu-item')].find((i) => i.textContent.trim() === ${JSON.stringify(text)} && !i.disabled) : undefined;`,
  );
  await page.waitFor(`!!${item}`, `菜单项「${text}」可用`);
  // mdui 组件覆盖了 click()，转给内部元素时不一定冒泡到 React；派发一个真实的点击事件。
  await page.eval(`${item}.${click}`);
}

async function clickDialogButton(page, text) {
  const target = `[...[...document.querySelectorAll('mdui-dialog')].find((d) => d.open)?.querySelectorAll('mdui-button') ?? []].find((b) => b.textContent.trim() === ${JSON.stringify(text)} && !b.disabled)`;
  await page.waitFor(`!!(${target})`, `对话框按钮「${text}」可用`);
  await page.eval(`(${target}).${click}`);
}

/** 同名输入框不止一个时（例如来源管理和工程设置都有“名称”）用 scope 限定。 */
async function typeInto(page, label, value, scope = '') {
  const target = `field(${JSON.stringify(label)}, ${JSON.stringify(scope)})`;
  await page.waitFor(js(`return !!${target};`), `输入框「${label}」`);
  await page.eval(js(`const f = ${target}; f.value = ${JSON.stringify(value)}; f.dispatchEvent(new Event('input', { bubbles: true, composed: true }));`));
}

async function pressEnter(page, label, scope = '') {
  await page.eval(js(`field(${JSON.stringify(label)}, ${JSON.stringify(scope)}).dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, composed: true }));`));
}

async function chooseOption(page, label, value) {
  // 改 value 后内部 mdui-menu 会发一次冒泡的 change，和用户点选一样。
  await page.eval(`[...document.querySelectorAll('mdui-select')].find((s) => s.label === ${JSON.stringify(label)}).value = ${JSON.stringify(value)}`);
}

async function setFile(page, ariaLabel, path) {
  const { root } = await page.send('DOM.getDocument', { depth: -1 });
  const { nodeId } = await page.send('DOM.querySelector', { nodeId: root.nodeId, selector: `input[type=file][aria-label="${ariaLabel}"]` });
  await page.send('DOM.setFileInputFiles', { nodeId, files: [path] });
}

async function gotoPage(page, value, ready) {
  await page.eval(`document.querySelector('mdui-navigation-rail-item[value=${value}]').click()`);
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
  await clickMenuItem(page, '更多', '关闭工程');
  const discard = `[...[...document.querySelectorAll('mdui-dialog')].find((d) => d.open)?.querySelectorAll('mdui-button') ?? []].find((b) => b.textContent.trim() === '不保存')`;
  await page.waitFor(`!!document.querySelector('.home') || !!(${discard})`, '关闭工程', 60_000);
  if (await page.eval(`!!(${discard})`)) await page.eval(`(${discard}).${click}`);
  await page.waitFor(`!!document.querySelector('.home')`, '回到首页', 60_000);
}

/** 在首页用新建对话框建工程；audio、lyrics 可以都不给。 */
async function newProject(page, { audio, lyrics, name }) {
  await clickButton(page, '新建工程');
  await page.waitFor(`!!document.querySelector('mdui-dialog.new-project-dialog[open] input[aria-label="选择歌词"]')`, '新建工程对话框');
  if (audio) {
    await setFile(page, '选择音频', audio);
    await page.waitFor(`document.querySelector('mdui-dialog.new-project-dialog').innerText.includes(${JSON.stringify(audio.split('/').at(-1))})`, '音频已选择');
  }
  if (lyrics) {
    await setFile(page, '选择歌词', lyrics);
    await page.waitFor(`!!document.querySelector('.lyrics-plan-summary')`, '歌词识别结果', 30_000);
  }
  await typeInto(page, '工程名', name);
  await clickDialogButton(page, '下一步');
  await clickDialogButton(page, '新建');
}

/** 设置面板里某一项（dt）的取值。 */
const settingsFact = (group, label) =>
  `[...document.querySelectorAll('[data-group="${group}"] .settings-facts > div')].find((d) => d.querySelector('dt')?.textContent === ${JSON.stringify(label)})?.querySelector('dd')?.textContent`;

async function snapshot(page, suffix) {
  if (!values.screenshot) return;
  await new Promise((resolve) => setTimeout(resolve, 700));
  await writeFile(values.screenshot.replace(/\.png$/, `-${suffix}.png`), await page.screenshot());
}

const lineCount = `document.querySelectorAll('.line-list mdui-list-item').length`;
const settingsOpen = `!!document.querySelector('mdui-navigation-drawer.settings-drawer')?.open`;
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
  await page.waitFor(`document.querySelector('.connection')?.textContent === '已连接'`, '连接 Server');

  // 1. 首页：工程目录浏览与搜索，新建空工程
  await page.waitFor(`!!document.querySelector('.directory-list') && !document.querySelector('.directory-progress')`, '工程目录读完', 30_000);
  check(await page.eval(`!!document.querySelector('.breadcrumb')`), '首页显示授权目录的路径导航与列表');
  await typeInto(page, '搜索这一层', `不存在-${stamp}`);
  await page.waitFor(`document.querySelector('.directory-empty')?.textContent.includes('没有名称包含')`, '搜索没有结果');
  await typeInto(page, '搜索这一层', '');
  check(true, '按名称搜索这一层，没有结果时写明');

  await clickButton(page, '新建工程');
  await page.waitFor(`!!document.querySelector('mdui-dialog.new-project-dialog[open]')`, '新建工程对话框');
  await typeInto(page, '工程名', projectName);
  await clickDialogButton(page, '下一步');
  await clickDialogButton(page, '新建');
  await page.waitFor(`!!document.querySelector('.no-audio-bar') && !!document.querySelector('.empty-state')`, '空工程进入歌词页', 60_000);
  check(await page.eval(js(`return revision() === '0';`)), '不选音频和歌词也能新建：歌词页显示导入入口，底部显示“还没有音频”');
  await snapshot(page, 'empty');

  // 2. 导入歌词、选择音频、改名
  await clickButtonIn(page, '.empty-state', '导入歌词文件…');
  await page.waitFor(`!!document.querySelector('mdui-dialog.import-dialog[open]')`, '导入对话框');
  await setFile(page, '选择文件', lrcPath);
  await chooseOption(page, '拆分文本单元', 'none');
  await clickDialogButton(page, '导入');
  await page.waitFor(`!!document.querySelector('mdui-dialog.report-dialog[open]')`, '导入报告', 30_000);
  await clickDialogButton(page, '关闭');
  await page.waitFor(`${lineCount} === 2`, '导入后 2 行');
  check(true, '从空态导入歌词文件（2 行）');

  await setFile(page, '选择音频…', wavPath);
  await page.waitFor(`!document.querySelector('.no-audio-bar') && !!document.querySelector('.transport-time')`, '选择音频后出现播放控制', 90_000);
  check(true, '从底部“选择音频…”换入音频，出现播放控制与时间轴');

  await page.eval(`document.querySelector('.settings-item').click()`);
  await page.waitFor(settingsOpen, '打开工程设置');
  await typeInto(page, '名称', `${projectName} 改`, '.settings-drawer');
  await pressEnter(page, '名称', '.settings-drawer');
  await page.waitFor(`document.querySelector('.project-name')?.textContent === ${JSON.stringify(`${projectName} 改`)}`, '改名生效', 20_000);
  check(await page.eval(`document.querySelector('.audio-file')?.textContent === '冒烟音频.wav'`), '设置里改名一条历史；音频显示本次上传的文件名');

  // 3. 背景图片：适配、移除、撤销
  const revisionOf = js(`return Number(revision());`);
  await setFile(page, '选择图片或视频…', pngPath);
  await page.waitFor(`!!document.querySelector('img.background-thumb')`, '背景缩略图', 60_000);
  const beforeFit = await page.eval(revisionOf);
  await chooseOption(page, '适配方式', 'cover');
  await page.waitFor(`${revisionOf} === ${beforeFit + 1}`, '改适配方式');
  await clickButtonIn(page, '[data-group="background"]', '移除');
  await page.waitFor(`!document.querySelector('img.background-thumb') && ${revisionOf} === ${beforeFit + 2}`, '移除背景媒体');
  await clickButton(page, '撤销');
  await page.waitFor(`!!document.querySelector('img.background-thumb')`, '撤销后背景回来', 30_000);
  check(
    await page.eval(`[...document.querySelectorAll('mdui-select')].find((s) => s.label === '适配方式')?.value === 'cover'`),
    '背景图片：选择、改适配（一条历史）、移除、撤销后恢复为“铺满”',
  );
  await snapshot(page, 'settings');
  await clickButton(page, '关闭工程设置');
  await page.waitFor(`!(${settingsOpen})`, '关闭工程设置');

  // 4. 保存到…、关闭、从工程目录打开
  await clickButton(page, '保存工程');
  await page.waitFor(`!!document.querySelector('mdui-dialog.save-location-dialog[open] .directory-row')`, '“保存到…”对话框', 30_000);
  await typeInto(page, '目录名', directory);
  await page.waitFor(`document.querySelector('.save-target')?.dataset.kind === 'new'`, '目标写明“会新建目录”');
  await clickDialogButton(page, '保存');
  await page.waitFor(`document.querySelector('.project-state')?.textContent === '已保存'`, '保存完成', 60_000);
  await clickMenuItem(page, '更多', '关闭工程');
  await page.waitFor(`!!document.querySelector('.home')`, '回到首页', 60_000);
  const directoryRow = `document.querySelector('.directory-row[data-path=${JSON.stringify(directory)}] .directory-main')`;
  await page.waitFor(`!!(${directoryRow}) && !(${directoryRow}).disabled`, '工程目录列出刚保存的工程', 30_000);
  await page.eval(`(${directoryRow}).click()`);
  await page.waitFor(`${lineCount} === 2 && document.querySelector('.project-name')?.textContent === ${JSON.stringify(`${projectName} 改`)}`, '重开工程', 120_000);
  check(true, `保存到新目录 ${directory}、关闭、从工程目录重开（名称与歌词一致）`);

  // 4b. 另存为：选中别的工程时写明“覆盖”（不真的覆盖）；换成新目录名另存
  const savedAs = `${directory}-另存`;
  await clickMenuItem(page, '更多', '另存为…');
  await page.waitFor(`!!document.querySelector('mdui-dialog.save-location-dialog[open] .directory-row')`, '另存为对话框', 30_000);
  const otherProject = `[...document.querySelectorAll('mdui-dialog.save-location-dialog .directory-row.is-project')].find((r) => r.dataset.path !== ${JSON.stringify(directory)})?.querySelector('.directory-main')`;
  const saveButton = `[...document.querySelectorAll('mdui-dialog.save-location-dialog mdui-button')].map((b) => b.textContent.trim())`;
  if (await page.eval(`!!(${otherProject})`)) {
    await page.eval(`(${otherProject}).click()`);
    await page.waitFor(`document.querySelector('.save-target')?.dataset.kind === 'project'`, '目标写明“覆盖已有工程”');
    check((await page.eval(saveButton)).includes('覆盖'), '另存为：选中别的工程时写明会覆盖，按钮变为“覆盖”');
  } else {
    check(true, '跳过覆盖提示：授权目录里没有别的工程');
  }
  await typeInto(page, '目录名', savedAs);
  await page.waitFor(`document.querySelector('.save-target')?.dataset.kind === 'new'`, '另存为的目标写明“会新建目录”');
  await clickDialogButton(page, '保存');
  await page.waitFor(`!document.querySelector('mdui-dialog.save-location-dialog[open]') && document.querySelector('.project-state')?.textContent === '已保存'`, '另存为完成', 60_000);
  check(true, `另存为到新目录 ${savedAs}`);

  // 5. 导出歌词文件
  await clickMenuItem(page, '更多', '导出歌词文件…');
  await page.waitFor(`!!document.querySelector('mdui-dialog.lyrics-export-dialog[open] .lyrics-export-form')`, '导出歌词对话框');
  await clickDialogButton(page, '评估');
  await page.waitFor(`!!document.querySelector('.lyrics-export-plan')`, '评估结果', 30_000);
  const needsAccept = await page.eval(`!!document.querySelector('.lyrics-export-plan mdui-checkbox')`);
  if (needsAccept) {
    const blockedBefore = await page.eval(`[...document.querySelectorAll('mdui-dialog.lyrics-export-dialog mdui-button')].find((b) => b.textContent.trim() === '导出并下载').disabled`);
    check(blockedBefore, '有损失时不勾选“接受这些损失”不能导出');
    await page.eval(`document.querySelector('.lyrics-export-plan mdui-checkbox').${click}`);
  }
  await clickDialogButton(page, '导出并下载');
  await page.waitFor(`!!document.querySelector('.lyrics-export-done')`, '歌词导出完成', 30_000);
  const reported = Number((await page.eval(`document.querySelector('.lyrics-export-done').textContent`)).match(/(\d+) 字节/)?.[1]);
  const lyricsSize = await waitDownload(`${projectName} 改.lrc`);
  check(lyricsSize === reported && reported > 0, `导出增强 LRC 并下载，文件大小与报告一致（${lyricsSize} 字节）`);
  await clickDialogButton(page, '完成');

  // 6. 导出页：修复入口、正式验证、导出 MP4 并下载、再导出一次后取消
  await clickButtonIn(page, '.app-bar', '导出视频');
  await page.waitFor(`${check$('duration')} === 'block' && ${check$('timing')} === 'ok'`, '导出页：输出时长未定是阻断项', 30_000);
  check(await page.eval(`[...document.querySelectorAll('[aria-label="导出"] mdui-button')].find((b) => b.textContent.includes('开始导出'))?.disabled === true`), '准备检查有阻断项时“开始导出”停用');
  await page.eval(`document.querySelector('[data-check="duration"] mdui-button').click()`);
  await page.waitFor(settingsOpen, '修复入口打开工程设置');
  await clickButtonIn(page, '[data-group="duration"]', '按音频时长');
  await page.waitFor(`${check$('duration')} === 'ok'`, '输出时长确定', 20_000);
  await clickButton(page, '关闭工程设置');
  await page.waitFor(`!(${settingsOpen})`, '关闭工程设置');
  await page.waitFor(`document.querySelectorAll('.export-thumbnail img').length === 2`, '首帧与末帧缩略图', 60_000);
  check(true, '修复入口“去设置”打开输出时长一组；确定后阻断解除，摘要显示首帧与末帧');

  await typeInto(page, '检查帧数', '10');
  await pressEnter(page, '检查帧数');
  await clickButtonIn(page, '[aria-label="正式验证"]', '开始验证');
  await page.waitFor(`document.querySelector('[aria-label="正式验证"] .job-result[data-state]')?.textContent.includes('验证通过')`, '正式验证完成', 180_000);
  check(
    (await page.eval(`document.querySelector('[aria-label="正式验证"] .job-result').textContent`)).includes('普通 10 帧') && (await page.eval(check$('validation'))) === 'ok',
    '正式验证 10 帧通过，准备检查里显示“当前修订验证通过”',
  );

  await clickButtonIn(page, '[aria-label="导出"]', '开始导出');
  await page.waitFor(`!!document.querySelector('[aria-label="导出"] .job-progress')`, '导出进度');
  await page.waitFor(`document.querySelector('[aria-label="导出"] .job-result[data-state="succeeded"]')?.textContent.includes('导出完成')`, '导出完成', 300_000);
  const facts = await page.eval(`document.querySelector('[aria-label="导出"] .export-facts').textContent`);
  check(facts.includes('1920 × 1080') && facts.includes('120/120') && facts.includes('有（aac）'), `导出 MP4：报告与设置一致（${facts.slice(0, 80)}…）`);
  await clickButtonIn(page, '[aria-label="导出"] .job-result', '下载');
  const videoSize = await waitDownload(`${projectName} 改.mp4`, 60_000);
  check(videoSize > 1000, `下载成片（${videoSize} 字节，文件名用工程名）`);
  await snapshot(page, 'export');

  await clickButtonIn(page, '[aria-label="导出"]', '重新导出');
  await page.waitFor(`!!document.querySelector('[aria-label="导出"] .job-progress')`, '第二次导出开始');
  await clickButtonIn(page, '[aria-label="导出"] .job-progress', '取消');
  await page.waitFor(`document.querySelector('[aria-label="导出"] .job-result[data-state]')?.textContent.includes('已取消')`, '取消后的终态', 60_000);
  check(true, '导出中途取消，状态是“已取消”');
  await clickButtonIn(page, '.export-header', '返回编辑');
  await page.waitFor(`${lineCount} === 2`, '返回歌词页');

  // 7. 只读打开：关闭工程（不保存导出页里定下的输出时长），从工程目录的更多操作里只读打开
  await closeCurrentProject(page);
  await page.waitFor(`!!(${directoryRow}) && !!document.querySelector('.directory-row[data-path=${JSON.stringify(savedAs)}]')`, '工程目录列出原工程和另存的工程', 30_000);
  await clickMenuItem(page, `${directory} 的更多操作`, '只读打开');
  await page.waitFor(`document.querySelector('.project-state')?.textContent === '只读' && ${lineCount} === 2`, '只读打开', 120_000);
  check(await page.eval(`document.querySelector('.project-name')?.textContent === ${JSON.stringify(`${projectName} 改`)}`), '从工程目录“只读打开”：顶栏显示“只读”，名称和歌词与保存时一致');
  await closeCurrentProject(page);

  // 8. 只有歌词、只有音频的新建
  await newProject(page, { lyrics: lrcPath, name: `${projectName} 只有歌词` });
  await page.waitFor(`${lineCount} === 2 && !!document.querySelector('.no-audio-bar') && !document.querySelector('.timeline')`, '只有歌词的工程', 60_000);
  check(true, '只选歌词新建：2 行，没有时间轴，底部显示“还没有音频”');
  await closeCurrentProject(page);

  await newProject(page, { audio: wavPath, name: `${projectName} 只有音频` });
  await page.waitFor(`!!document.querySelector('.empty-state') && !!document.querySelector('.timeline') && !document.querySelector('.no-audio-bar')`, '只有音频的工程', 120_000);
  check(true, '只选音频新建：歌词页显示导入入口，有播放控制与时间轴');

  // 9. 背景视频与播放策略；替换音频；移除音频与撤销
  await page.eval(`document.querySelector('.settings-item').click()`);
  await page.waitFor(settingsOpen, '打开工程设置');
  if (hasVideo) {
    await setFile(page, '选择图片或视频…', mp4Path);
    await page.waitFor(`document.querySelector('[data-group="background"] .background-info')?.textContent.includes('视频') && [...document.querySelectorAll('mdui-select')].some((s) => s.label === '播放')`, '背景视频', 90_000);
    const beforePlayback = await page.eval(revisionOf);
    await chooseOption(page, '播放', 'freezeLastFrame');
    await page.waitFor(`${revisionOf} === ${beforePlayback + 1}`, '改播放策略');
    check(
      await page.eval(`[...document.querySelectorAll('mdui-select')].find((s) => s.label === '播放')?.value === 'freezeLastFrame'`),
      '背景视频：选择后显示类型与大小，“停在最后一帧”一条历史',
    );
  } else {
    check(true, '跳过背景视频：本机没有 ffmpeg，造不出测试视频');
  }

  const durationBefore = await page.eval(settingsFact('audio', '时长'));
  await setFile(page, '替换音频…', wav6Path);
  await page.waitFor(`${settingsFact('audio', '时长')} !== ${JSON.stringify(durationBefore)} && document.querySelector('.audio-file')?.textContent === '冒烟音频-6秒.wav'`, '替换音频', 120_000);
  const durationAfter = await page.eval(settingsFact('audio', '时长'));
  check(!!durationAfter && durationAfter !== durationBefore, `替换音频：时长 ${durationBefore} → ${durationAfter}，文件名随之更新`);

  await clickButtonIn(page, '[data-group="audio"]', '移除音频');
  await clickDialogButton(page, '移除');
  await page.waitFor(`!!document.querySelector('.no-audio-bar') && !document.querySelector('.timeline')`, '移除音频后时间轴消失', 60_000);
  await clickButton(page, '撤销');
  await page.waitFor(`!document.querySelector('.no-audio-bar') && !!document.querySelector('.timeline') && ${settingsFact('audio', '时长')} === ${JSON.stringify(durationAfter)}`, '撤销后音频回来', 120_000);
  check(true, '移除音频（先确认）后时间轴消失、底部显示“还没有音频”；撤销后音频和时间轴回来');
  await clickButton(page, '关闭工程设置');
  await page.waitFor(`!(${settingsOpen})`, '关闭工程设置');
  await closeCurrentProject(page);

  // 10. 从备份恢复一份，进入歌词页
  await clickButton(page, '从备份恢复…');
  await page.waitFor(`!!document.querySelector('mdui-dialog.recovery-dialog[open]') && (!!document.querySelector('.recovery-item') || document.querySelector('.recovery-dialog')?.textContent.includes('没有可以恢复的备份'))`, '恢复对话框', 30_000);
  const candidates = await page.eval(`document.querySelectorAll('.recovery-item').length`);
  if (candidates > 0) {
    const recovered = await page.eval(`document.querySelector('.recovery-item .recovery-name').textContent`);
    await page.eval(`document.querySelector('.recovery-item').click()`);
    await clickDialogButton(page, '恢复');
    await page.waitFor(`document.querySelector('.project-name')?.textContent === ${JSON.stringify(recovered)} && (!!document.querySelector('.line-list') || !!document.querySelector('.empty-state'))`, '恢复后进入歌词页', 180_000);
    check(true, `“从备份恢复…”列出 ${candidates} 份备份；恢复最新的一份（${recovered}）并进入歌词页`);
    await closeCurrentProject(page);
  } else {
    check(true, '“从备份恢复…”写明没有可以恢复的备份（profile 是新的）');
    await clickDialogButton(page, '取消');
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
