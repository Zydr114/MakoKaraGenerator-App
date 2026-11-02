// 渲染设备的浏览器冒烟（渲染设备设计 §4 第 5 步）：用真实 Chrome 走一遍 CPU／GPU 偏好。
// 顶栏状态胶囊的菜单列出跟随默认、CPU、GPU 与设备摘要 → 新建工程 → 外观页成片预览是 CPU 帧
// → 选 GPU：已开的预览不重开就换成 GPU 帧 → 编排页预览与测量、样式库缩略图在 GPU 下照常
// → 导出页：缩略图、正式验证与导出都用 GPU，卡片和报告写明设备 → 改回跟随默认，预览回到 CPU 帧
// → 偏好在刷新后还在。GPU 不可用时（可以用 VK_ICD_FILENAMES=/nonexistent 启动后端来模拟），改为确认 GPU 一项禁用并写明原因，
// 偏好已经是 GPU 时预览与正式验证明确失败、不改用 CPU，在顶栏改用 CPU 后恢复。
//
// 前提：pnpm dev:backend 与 pnpm dev 都在运行。
// 用法：node scripts/render-device-smoke.mjs [--url http://localhost:5173] [--screenshot out.png] [--headed]

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

// ── 素材：4 秒的 WAV、两行逐字打好轴的增强 LRC ──

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

const assets = mkdtempSync(join(tmpdir(), 'mako-render-device-smoke-'));
const wavPath = join(assets, '设备冒烟.wav');
const lrcPath = join(assets, '设备冒烟.lrc');
writeFileSync(wavPath, makeWav());
writeFileSync(lrcPath, '[00:00.20]<00:00.20>朝<00:00.60>の<00:01.00>光<00:01.40>\n[00:01.60]<00:01.60>你<00:02.20>好<00:03.00>\n');
const projectName = `设备冒烟 ${Date.now()}`;

// ── 页面操作 ──

const checks = [];
function check(ok, label) {
  checks.push({ ok: !!ok, label });
  console.log(`${ok ? '✓' : '✗'} ${label}`);
  if (!ok) process.exitCode = 1;
}

/** 正式验证：检查帧数填 5（回车提交），再点开始验证。 */
async function startValidation(page) {
  await ui.fill(page, '检查帧数', '5', { enter: true, scope: '[aria-label="正式验证"]' });
  await ui.click(page, 'button', '开始验证', { scope: '[aria-label="正式验证"]' });
}

async function gotoPage(page, value, ready) {
  await shell.goto(page, value);
  await page.waitFor(ready, `切到「${value}」页`);
}

// 顶栏状态胶囊（界面改版 §2.5）：写“已连接，CPU”；点开是渲染设备菜单，菜单只在打开时渲染
const deviceText = `((document.querySelector('.status-chip .status-text')?.textContent ?? '').split('，')[1] ?? '')`;
const menuOpen = `!!document.querySelector('.render-device-menu')`;
const DEVICE_LABEL = { default: '跟随后端默认', cpu: 'CPU', gpu: 'GPU' };
const deviceItem = (value) => `__ui.find('menuitem', ${JSON.stringify(DEVICE_LABEL[value])}, ${value === 'default'}, '.render-device-menu')`;

async function openDeviceMenu(page) {
  if (await page.eval(menuOpen)) return;
  await ui.click(page, 'button', '（渲染设备）', { partial: true, scope: '.app-bar' });
  await page.waitFor(menuOpen, '打开渲染设备菜单');
}

async function closeDeviceMenu(page) {
  if (!(await page.eval(menuOpen))) return;
  await page.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
  await page.send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
  await page.waitFor(`!(${menuOpen})`, '关闭渲染设备菜单');
}

/** 打开菜单等检测完，读出三项的文字、设备摘要、是否选中、是否禁用，再关上。 */
async function readDevices(page, what) {
  await openDeviceMenu(page);
  await page.waitFor(uiJs(`const d = ${deviceItem('gpu')}?.querySelector('.menu-desc')?.textContent; return !!d && d !== '还没有检测';`), what, 60_000);
  const devices = await page.eval(
    uiJs(`return JSON.stringify(Object.fromEntries(${JSON.stringify(Object.keys(DEVICE_LABEL))}.map((value) => {
      const el = { default: ${deviceItem('default')}, cpu: ${deviceItem('cpu')}, gpu: ${deviceItem('gpu')} }[value];
      return [value, { label: __ui.text(el), detail: el.querySelector('.menu-desc')?.textContent ?? '', checked: el.getAttribute('aria-checked') === 'true', disabled: __ui.disabled(el) }];
    })));`),
  );
  await closeDeviceMenu(page);
  return JSON.parse(devices);
}

async function chooseDevice(page, value) {
  await openDeviceMenu(page);
  await ui.click(page, 'menuitem', DEVICE_LABEL[value], { partial: value === 'default', scope: '.render-device-menu', what: `渲染设备「${value}」可选` });
  await page.waitFor(`!(${menuOpen})`, '菜单关闭');
}

const previewTitle = `document.querySelector('.preview-panel .preview-title')?.textContent ?? ''`;

async function closeCurrentProject(page) {
  await shell.closeProject(page);
  await page.waitFor(`!!document.querySelector('.home')`, '回到首页', 60_000);
}

const settingsOpen = `!!document.querySelector('.project-settings:not([data-exiting])')`;
const check$ = (id) => `document.querySelector('[data-check="${id}"]')?.dataset.level`;

async function createProject(page) {
  await shell.newProject(page, { audio: wavPath, lyrics: lrcPath, name: projectName });
  await page.waitFor(`document.querySelector('.project-name')?.textContent === ${JSON.stringify(projectName)}`, '进入工程', 60_000);
}

/** 进导出页，用修复入口把输出时长定为音频时长。 */
async function openExportReady(page) {
  await shell.goto(page, '导出视频');
  await page.waitFor(`${check$('duration')} === 'block'`, '导出页', 30_000);
  await ui.click(page, 'button', '去设置', { scope: '[data-check="duration"]' });
  await page.waitFor(settingsOpen, '修复入口打开工程设置');
  await ui.click(page, 'button', '按音频时长', { scope: '[data-group="duration"]' });
  await page.waitFor(`${check$('duration')} === 'ok'`, '输出时长确定', 20_000);
  await ui.click(page, 'button', '关闭工程设置');
  await page.waitFor(`!document.querySelector('.project-settings')`, '关闭工程设置');
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
  // 从“跟随后端默认”开始。
  await page.eval(`localStorage.removeItem('mako.renderBackend'); location.reload()`);
  await page.waitFor(`document.readyState === 'complete' && ${shell.connected}`, '连接 Server');

  // 1. 状态胶囊的菜单：检测结果与三项
  const devices = await readDevices(page, '检测渲染设备');
  const gpuDetail = devices.gpu.detail;
  const gpuAvailable = !gpuDetail.startsWith('不可用');
  check(
    (await page.eval(deviceText)) === 'CPU' && devices.default.label === '跟随后端默认（CPU）' && devices.default.checked,
    '状态胶囊写“已连接，CPU”；菜单里选中“跟随后端默认（CPU）”',
  );
  check(!!devices.cpu.detail, `CPU 一项有设备摘要（${devices.cpu.detail}）`);
  if (!gpuAvailable) {
    // GPU 不可用（例如用 VK_ICD_FILENAMES=/nonexistent 启动后端）：菜单禁用 GPU；偏好已经是 GPU 时，预览与任务明确失败，不悄悄用 CPU。
    check(devices.gpu.disabled, `GPU 不可用：这一项禁用并写明原因（${gpuDetail}）`);
    await page.eval(`localStorage.setItem('mako.renderBackend', 'gpu'); location.reload()`);
    await page.waitFor(`document.readyState === 'complete' && ${shell.connected}`, '刷新后重新连接');
    const reloaded = await readDevices(page, '刷新后检测渲染设备');
    check(
      (await page.eval(deviceText)) === 'GPU（不可用）' && !reloaded.gpu.disabled && reloaded.gpu.checked,
      '偏好已经是 GPU：不自动改，胶囊写“GPU（不可用）”，菜单里这一项仍可见可选',
    );

    await createProject(page);
    await gotoPage(page, 'appearance', `!!document.querySelector('.preview-panel')`);
    await page.waitFor(`!!document.querySelector('.preview-panel .preview-retry')`, '外观页预览因设备停住', 60_000);
    const blocked = await page.eval(`document.querySelector('.preview-panel .preview-frame').textContent`);
    check(
      blocked.includes('渲染设备不可用') && blocked.includes('改用别的渲染设备') && (await page.eval(`!document.querySelector('.preview-panel .preview-frame img')`)),
      `成片预览创建失败：写明设备原因与“重试”，没有用 CPU 出帧（${blocked.slice(0, 60)}…）`,
    );

    await openExportReady(page);
    await startValidation(page);
    const validationText = `(document.querySelector('[aria-label="正式验证"] .note.err')?.textContent ?? document.querySelector('[aria-label="正式验证"] .job-result[data-state]')?.textContent ?? '')`;
    await page.waitFor(`${validationText} !== ''`, '正式验证失败', 120_000);
    const failed = await page.eval(validationText);
    check(!failed.includes('验证通过') && /渲染设备|RenderBackend/.test(failed), `正式验证用 GPU 明确失败，不改用 CPU（${failed.slice(0, 80)}）`);

    await chooseDevice(page, 'cpu');
    await ui.click(page, 'button', '返回编辑', { scope: '.app-bar' });
    await gotoPage(page, 'appearance', `!!document.querySelector('.preview-panel')`);
    await page.waitFor(`(${previewTitle}).startsWith('成片 · CPU · 修订')`, '改用 CPU 后出帧', 60_000);
    check(true, '在顶栏改用 CPU 后，成片预览恢复出帧（标题写 CPU）');
    await chooseDevice(page, 'default');
    await closeCurrentProject(page);
  } else {
    check(gpuDetail.includes('Vulkan'), `GPU 一项有设备摘要与 Vulkan 版本（${gpuDetail}）`);

    // 2. 新建工程，外观页的成片预览是 CPU 帧
    await createProject(page);
    await gotoPage(page, 'appearance', `!!document.querySelector('.preview-panel')`);
    await page.waitFor(`(${previewTitle}).startsWith('成片 · CPU · 修订')`, '外观页 CPU 帧', 60_000);
    check(true, `外观页成片预览标题写明设备（${await page.eval(previewTitle)}）`);

    // 3. 选 GPU：已开的预览换设备
    await chooseDevice(page, 'gpu');
    await page.waitFor(`(${previewTitle}).startsWith('成片 · GPU · 修订')`, '外观页 GPU 帧', 60_000);
    check(
      (await page.eval(deviceText)) === 'GPU' && (await page.eval(`localStorage.getItem('mako.renderBackend')`)) === 'gpu',
      '选 GPU：胶囊改成“GPU”并记在本机；已开的成片预览不用重开就换成 GPU 帧',
    );

    // 外观页的候选试看（一次性渲染）
    await ui.click(page, '.appearance-targets .at.line-row', null);
    await page.waitFor(`(document.querySelector('.appearance-inspector .ihead')?.dataset.path ?? '').includes('第 1 行')`, '选中第 1 行');
    await ui.click(page, 'button', '选择模板…', { scope: '.template-grp' });
    await ui.waitDialog(page, '选择模板');
    await ui.click(page, '.template-picker .template-card[data-template-id="builtin:line-classic"]', null);
    await page.waitFor(`!!document.querySelector('.picker-trial img[data-sha]') || !!document.querySelector('.picker-trial .preview-overlay.err')`, '试看画面', 60_000);
    check(await page.eval(`!!document.querySelector('.picker-trial img[data-sha]')`), '外观页选择器的候选试看在 GPU 下出图');
    await ui.clickInDialog(page, '取消');
    await ui.waitDialogClosed(page, '选择模板');

    // 4. 编排页的预览与测量、样式库缩略图
    await gotoPage(page, 'orchestration', `!!document.querySelector('.orchestration .preview-panel')`);
    await page.waitFor(`(${previewTitle}).startsWith('成片 · GPU · 修订')`, '编排页 GPU 帧', 60_000);
    await page.waitFor(`!!document.querySelector('.orchestration .canvas-overlay') || !!document.querySelector('.orchestration .canvas-status.err')`, '编排页同帧测量', 60_000);
    check(
      (await page.eval(`!!document.querySelector('.orchestration .canvas-overlay') && !document.querySelector('.orchestration .canvas-status.err')`)),
      '编排页：成片预览是 GPU 帧，同帧测量在 GPU 下出叠加层',
    );
    await gotoPage(page, 'library', `!!document.querySelector('.library')`);
    await page.waitFor(`document.querySelectorAll('.library .template-thumb[data-status=ready] img').length > 0`, '样式库缩略图', 60_000);
    check(
      (await page.eval(`document.querySelectorAll('.library .template-thumb[data-status=error]').length`)) === 0,
      `样式库缩略图在 GPU 下照常出图（${await page.eval(`document.querySelectorAll('.library .template-thumb[data-status=ready] img').length`)} 张）`,
    );

    // 5. 导出页：缩略图、正式验证、导出
    await openExportReady(page);
    await page.waitFor(`document.querySelectorAll('.export-thumbnail img').length === 2`, '首帧与末帧缩略图', 60_000);
    check(true, '导出页首帧、末帧缩略图在 GPU 下出图');

    await startValidation(page);
    await page.waitFor(`document.querySelector('[aria-label="正式验证"] .job-result[data-state]')?.textContent.includes('验证通过')`, '正式验证完成', 180_000);
    check(
      (await page.eval(`document.querySelector('[aria-label="正式验证"] .job-device')?.textContent`)) === '渲染设备：GPU',
      '正式验证通过，卡片写“渲染设备：GPU”（任务固定的设备）',
    );
    await ui.click(page, 'button', '开始导出', { scope: '.export-go' });
    await page.waitFor(`document.querySelector('[aria-label="导出任务"] .job-result[data-state="succeeded"]')?.textContent.includes('导出完成')`, '导出完成', 300_000);
    const facts = await page.eval(`document.querySelector('[aria-label="导出任务"] .export-facts').textContent`);
    check(
      facts.includes('设备GPU') && (await page.eval(`document.querySelector('[aria-label="导出任务"] .job-device')?.textContent`)) === '渲染设备：GPU',
      '导出完成，卡片和报告摘要都写 GPU',
    );

    // 6. 改回跟随默认：预览回到 CPU 帧；正在显示的任务结果仍写原来的设备
    await chooseDevice(page, 'default');
    check(
      (await page.eval(`document.querySelector('[aria-label="正式验证"] .job-device')?.textContent`)) === '渲染设备：GPU',
      '改回跟随默认后，已完成任务的卡片仍写它自己的设备（GPU）',
    );
    await ui.click(page, 'button', '返回编辑', { scope: '.app-bar' });
    await gotoPage(page, 'appearance', `!!document.querySelector('.preview-panel')`);
    await page.waitFor(`(${previewTitle}).startsWith('成片 · CPU · 修订')`, '外观页回到 CPU 帧', 60_000);
    check((await page.eval(`localStorage.getItem('mako.renderBackend')`)) === null, '改回跟随默认：本机偏好删掉，外观页预览回到 CPU 帧');

    // 7. 偏好在刷新后还在
    await chooseDevice(page, 'gpu');
    await closeCurrentProject(page);
    await page.eval(`location.reload()`);
    await page.waitFor(`document.readyState === 'complete' && ${shell.connected}`, '刷新后重新连接');
    const reloaded = await readDevices(page, '刷新后检测渲染设备');
    check((await page.eval(deviceText)) === 'GPU' && reloaded.gpu.checked, '刷新页面后偏好还是 GPU');
    await chooseDevice(page, 'default');
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
    const failure = join(tmpdir(), 'mako-render-device-smoke-failure.png');
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
