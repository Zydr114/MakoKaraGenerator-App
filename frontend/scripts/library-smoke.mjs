// 样式库完整模板的浏览器冒烟（W7 设计 §5 第 1～4、9 条）：用真实 Chrome 走一遍样式库的完整定义编辑。
// 复制“经典整行” → 字体：从目录选、字体检查逐字列出、目录里没有的字体标出
// → 材质：导入“自定义波纹材质”作未唱填充，试样焦点回显，改参数试样跟着变，越界时写范围并停用保存，移除在用的包被拦下，焦点切到已唱填充
// → 效果：导入“逐字强调”并加效果，放大比例 2、最大种子，试样跟着变
// → 试样：多行样本、换目标、拖时刻、试播 → 保存后重读与保存前一致（参数原文、最大种子）→ 列表缩略图
// → .mktpl：导出两个模板并核对，导入得到两个新 ID、定义一致，第三次传输写明要等几分钟 → 删除本次的模板。
//
// 前提：pnpm dev:backend 与 pnpm dev 都在运行；效果包取后端仓库 samples/ScriptEffects（找不到时跳过并写明原因）。
// 个人库里只增删本次创建的模板。
// 用法：node scripts/library-smoke.mjs [--url http://localhost:5173] [--screenshot out.png] [--headed]

import { mkdtempSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
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

if (!sampleEffectsDir()) {
  console.log('[skipped] 找不到后端仓库的 samples/ScriptEffects（用 MAKO_BACKEND_REPO 指定后端仓库），没有效果包可用，跳过样式库冒烟。');
  process.exit(0);
}

const assets = mkdtempSync(join(tmpdir(), 'mako-library-smoke-'));
const downloads = join(assets, 'downloads');
const inkPath = join(assets, 'CustomInk.mkfx');
const emphasisPath = join(assets, 'WordEmphasis.mkfx');
writeFileSync(inkPath, packSampleEffect('CustomInk'));
writeFileSync(emphasisPath, packSampleEffect('WordEmphasis'));
const stamp = Date.now().toString(36);
const fullName = `冒烟完整-${stamp}`;
const plainName = `冒烟静态-${stamp}`;
const MAX_SEED = '18446744073709551615';

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
  const templateName = () => document.querySelector('.template-name')?.textContent;
  const sample = () => document.querySelector('.template-sample');
  const sampleImg = () => document.querySelector('.template-sample [data-status=ready] img');
  const sampleTitle = () => document.querySelector('.template-sample .preview-title')?.textContent ?? '';
  const saveButton = () => anyButton('保存', document.querySelector('.template-actions'));
  const listItems = () => [...document.querySelectorAll('.library-list [data-template-id]')];
  const toasts = () => [...document.querySelectorAll('mdui-snackbar')].filter((s) => s.open).map((s) => s.textContent).join(' | ');
`;
const js = (body) => `(() => { ${PRELUDE} ${body} })()`;
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/** mdui 组件覆盖了 click()，转给内部元素时不一定冒泡到 React；菜单项用真实鼠标事件。 */
async function realClick(page, expression) {
  const r = await page.eval(`(() => { const b = (${expression}).getBoundingClientRect(); return { x: b.x + b.width / 2, y: b.y + b.height / 2 }; })()`);
  for (const type of ['mousePressed', 'mouseReleased']) await page.send('Input.dispatchMouseEvent', { type, x: r.x, y: r.y, button: 'left', clickCount: 1 });
}

async function clickButton(page, text, scope = 'document') {
  const target = js(`return button(${JSON.stringify(text)}, ${scope});`);
  await page.waitFor(`!!${target}`, `按钮「${text}」可用`);
  await page.eval(`${target}.click()`);
}

async function clickDialogButton(page, text) {
  const target = js(`return openDialog() && button(${JSON.stringify(text)}, openDialog());`);
  await page.waitFor(`!!${target}`, `对话框按钮「${text}」可用`, 60_000);
  await page.eval(`${target}.dispatchEvent(new MouseEvent('click', { bubbles: true, composed: true }))`);
}

/** 打开菜单再点菜单项；trigger 是触发按钮的表达式。 */
async function menuItem(page, trigger, text) {
  await page.eval(`(${trigger}).click()`);
  const item = `[...document.querySelectorAll('mdui-menu-item')].find((i) => i.textContent.trim() === ${JSON.stringify(text)} && !i.disabled)`;
  await page.waitFor(`!!${item} && ${item}.getBoundingClientRect().height > 0`, `菜单项「${text}」`);
  await sleep(300);
  await realClick(page, item);
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

async function setFile(page, ariaLabel, path) {
  const { root } = await page.send('DOM.getDocument', { depth: -1 });
  const { nodeId } = await page.send('DOM.querySelector', { nodeId: root.nodeId, selector: `input[type=file][aria-label="${ariaLabel}"]` });
  await page.send('DOM.setFileInputFiles', { nodeId, files: [path] });
}

async function tab(page, value) {
  await page.eval(`document.querySelector('mdui-tab[value=${value}]').click()`);
  await sleep(150);
}

/** 等试样画面换成和 before 不同的一帧，返回新帧的 SHA。 */
async function sampleChanged(page, before, label) {
  await page.waitFor(js(`return !!sampleImg() && sampleImg().dataset.sha !== ${JSON.stringify(before)};`), label, 60_000);
  return page.eval(js(`return sampleImg().dataset.sha;`));
}
const sampleSha = (page) => page.eval(js(`return sampleImg()?.dataset.sha;`));

async function selectTemplate(page, templateId, name) {
  await page.eval(`document.querySelector('.library-list [data-template-id="${templateId}"]').click()`);
  await page.waitFor(js(`return templateName() === ${JSON.stringify(name)};`), `选中 ${name}`);
}

/** 高级页签里的完整定义；包引用只留包 ID 和修订（保存后会从 asset 引用变成 library 引用）。 */
async function definitionText(page) {
  await tab(page, 'advanced');
  await page.waitFor(`!!document.querySelector('.template-json')`, '完整定义');
  const definition = JSON.parse(await page.eval(`document.querySelector('.template-json').textContent`));
  return { definition, comparable: JSON.stringify({ ...definition, packages: definition.packages.map((p) => `${p.packageId}@${p.packageRevision}`) }) };
}

const idsNamed = (page, name) => page.eval(js(`return listItems().filter((i) => i.textContent.includes(${JSON.stringify(name)})).map((i) => i.dataset.templateId);`));

async function waitDownload(before) {
  for (let i = 0; i < 150; i++) {
    const fresh = readdirSync(downloads).filter((f) => f.startsWith('MakoKara-templates') && !f.endsWith('.crdownload') && !before.includes(f));
    if (fresh.length > 0) return join(downloads, fresh[0]);
    await sleep(200);
  }
  throw new Error('等待 .mktpl 下载超时');
}

async function openTransfer(page, text) {
  await menuItem(page, `document.querySelector('[aria-label="模板库的更多操作"]')`, text);
  await page.waitFor(js(`return !!openDialog()?.classList.contains('transfer-dialog');`), `对话框「${text}」`);
}

async function exportTemplates(page, ids) {
  await openTransfer(page, '导出模板…');
  await page.waitFor(js(`return openDialog().querySelectorAll('.transfer-row').length >= ${ids.length};`), '导出列表');
  await page.eval(js(`anyButton('全不选', openDialog())?.click();`));
  await page.waitFor(js(`return ![...openDialog().querySelectorAll('.transfer-row mdui-checkbox')].some((c) => c.checked);`), '全不选');
  for (const id of ids) await page.eval(`document.querySelector('mdui-dialog[open] .transfer-row[data-template-id="${id}"] mdui-checkbox').click()`);
  await clickDialogButton(page, `导出 ${ids.length} 个模板`);
}

async function snapshot(page, suffix) {
  if (!values.screenshot) return;
  await sleep(700);
  await writeFile(values.screenshot.replace(/\.png$/, `-${suffix}.png`), await page.screenshot());
}

const browser = await launchChrome({ headed: values.headed });
let page;
const errors = [];
/** 本次写进个人库的模板 ID，最后在页面上删掉。 */
const created = [];
try {
  browser.cdp.on((message) => {
    if (message.method === 'Runtime.exceptionThrown') errors.push(message.params.exceptionDetails.exception?.description ?? 'exception');
    if (message.method === 'Runtime.consoleAPICalled' && message.params.type === 'error') {
      errors.push(message.params.args.map((a) => a.value ?? a.description).join(' '));
    }
  });

  page = await browser.openPage(`${ORIGIN}/`, { width: 1440, height: 1200 });
  await page.send('DOM.enable');
  await browser.cdp.send('Browser.setDownloadBehavior', { behavior: 'allow', downloadPath: downloads });
  await page.eval(`performance.setResourceTimingBufferSize(100000)`);
  await page.waitFor(`document.querySelector('.connection')?.textContent === '已连接'`, '连接 Server');

  // 1. 复制“经典整行”
  await page.eval(`document.querySelector('mdui-navigation-rail-item[value=library]').click()`);
  await page.waitFor(`!!document.querySelector('.library-list [data-template-id="builtin:line-classic"]')`, '样式库');
  await selectTemplate(page, 'builtin:line-classic', '经典整行');
  await clickButton(page, '复制并编辑');
  await page.waitFor(js(`return !!openDialog()?.querySelector('mdui-text-field');`), '复制对话框');
  await page.eval(js(`const f = openDialog().querySelector('mdui-text-field'); f.value = ${JSON.stringify(fullName)}; f.dispatchEvent(new Event('input', { bubbles: true, composed: true }));`));
  await clickDialogButton(page, '复制');
  await page.waitFor(js(`return templateName() === ${JSON.stringify(fullName)} && !!document.querySelector('.font-family');`), '复制后进入编辑');
  created.push(...(await idsNamed(page, fullName)));
  check(created.length === 1, `复制“经典整行”为“${fullName}”，进入编辑（外观、材质、效果、高级四个页签）`);

  // 2. 字体：从目录选；字体检查逐字列出；目录里没有的字体标出
  const familyField = `document.querySelector('.font-family mdui-text-field')`;
  await page.eval(`${familyField}.focus()`);
  await page.waitFor(`document.querySelectorAll('.font-family .font-option').length > 0`, '字体目录', 30_000);
  await page.eval(`(() => { const t = ${familyField}; t.value = 'noto sans'; t.dispatchEvent(new Event('input', { bubbles: true, composed: true })); })()`);
  await sleep(150);
  let font = await page.eval(`document.querySelector('.font-family .font-option')?.dataset.family`);
  if (!font) {
    // 没有 Noto Sans 时取目录里的第一个。
    await page.eval(`(() => { const t = ${familyField}; t.value = ''; t.dispatchEvent(new Event('input', { bubbles: true, composed: true })); })()`);
    await sleep(150);
    font = await page.eval(`document.querySelector('.font-family .font-option')?.dataset.family`);
  }
  await page.eval(`[...document.querySelectorAll('.font-family .font-option')].find((o) => o.dataset.family === ${JSON.stringify(font)}).click()`);
  await page.waitFor(`!document.querySelector('.font-family .font-options') && ${familyField}.value === ${JSON.stringify(font)}`, '选中字体');
  await page.eval(`(() => { const t = document.querySelector('.font-check mdui-text-field'); t.value = '朝の光Ж'; t.dispatchEvent(new Event('input', { bubbles: true, composed: true })); })()`);
  await page.eval(`document.querySelector('.font-check mdui-button').click()`);
  await page.waitFor(`document.querySelector('.font-check')?.dataset.status === 'done'`, '字体检查', 30_000);
  const runs = await page.eval(`document.querySelectorAll('.font-runs tr').length`);
  const fontPicked = !(await page.eval(`!!document.querySelector('.font-family .font-missing')`));
  check(fontPicked && runs > 0, `字体：从目录选中“${font}”，没有“后端没有”标记；字体检查逐字列出实际字体（${runs} 行）`);
  await commit(page, familyField, '不存在的字体XYZ');
  await page.waitFor(`!!document.querySelector('.font-family .font-missing')`, '缺字体标记');
  const missing = await page.eval(`document.querySelector('.font-family .font-missing').textContent`);
  await commit(page, familyField, font);
  await page.waitFor(`!document.querySelector('.font-family .font-missing')`, '改回目录里的字体');
  check(!!missing, `目录里没有的字体照常保留并标出（${missing}）`);

  // 3. 材质：导入“自定义波纹材质”作未唱填充
  await tab(page, 'material');
  await page.waitFor(`!!document.querySelector('.slot-material')`, '材质页签');
  await setFile(page, '导入效果包…', inkPath);
  await page.waitFor(`!!document.querySelector('.package-pending[data-status=ready]')`, '包检查', 60_000);
  await page.eval(js(`button('加入模板', document.querySelector('.package-pending')).click();`));
  await page.waitFor(`!!document.querySelector('.package-item[data-package-id="makokara.custom-ink"]')`, '包已加入');
  await page.eval(`document.querySelector('.slot-buttons [data-slot=unsungFill]').click()`);
  await choose(page, `document.querySelector('.slot-material mdui-select')`, 'package');
  await page.waitFor(`document.querySelector('.slot-material')?.dataset.kind === 'package'`, '包材质');
  await page.waitFor(js(`return sample()?.dataset.focus === 'unsungFill' && sampleTitle().includes('未保存的修改') && !!sampleImg();`), '试样焦点', 60_000);
  const params = await page.eval(`[...document.querySelectorAll('.slot-material [data-param]')].map((r) => r.dataset.param).join(',')`);
  check(params === 'frequencyHz,amplitude,colorA,colorB', `材质：导入 CustomInk 作未唱填充，参数控件 ${params}；试样后端回显焦点“未唱填充”`);

  let sha = await sampleSha(page);
  await commit(page, `document.querySelector('[data-param=amplitude] mdui-text-field')`, '0.3');
  const afterAmplitude = await sampleChanged(page, sha, '改幅度后的试样');
  check(afterAmplitude !== sha, `改“混色波纹幅度”为 0.3，试样画面跟着变（${sha.slice(0, 12)} → ${afterAmplitude.slice(0, 12)}）`);

  await commit(page, `document.querySelector('[data-param=amplitude] mdui-text-field')`, '0.9');
  await page.waitFor(`!!document.querySelector('[data-param=amplitude] .param-problem')`, '越界提示');
  const problem = await page.eval(`document.querySelector('[data-param=amplitude] .param-problem').textContent`);
  const blocked = await page.eval(js(`return saveButton()?.disabled === true && !!document.querySelector('.template-problem');`));
  check(blocked && problem.includes('0.5'), `参数越界：控件旁写明范围（${problem}），“保存”停用`);
  await commit(page, `document.querySelector('[data-param=amplitude] mdui-text-field')`, '0.3');
  await page.waitFor(js(`return !document.querySelector('[data-param=amplitude] .param-problem') && saveButton()?.disabled === false;`), '改回 0.3');

  await page.eval(`document.querySelector('.package-item[data-package-id="makokara.custom-ink"] mdui-button-icon').click()`);
  await page.waitFor(`!!document.querySelector('.package-notice')`, '移除被拦下');
  const notice = await page.eval(`document.querySelector('.package-notice').textContent`);
  check(notice.includes('未唱填充') && (await page.eval(`!!document.querySelector('.package-item[data-package-id="makokara.custom-ink"]')`)), `移除还在用的包被拦下：${notice}`);

  await page.eval(`document.querySelector('.slot-buttons [data-slot=sungFill]').click()`);
  await page.waitFor(js(`return sample()?.dataset.focus === 'sungFill' && !!sampleImg();`), '焦点已唱填充', 60_000);
  check((await page.eval(js(`return sampleTitle();`))).includes('材质焦点 已唱填充'), '材质焦点切到“已唱填充”，后端回显的焦点一致');
  await snapshot(page, 'material');

  // 4. 效果：导入“逐字强调”，放大比例 2，最大种子
  await tab(page, 'effects');
  await setFile(page, '导入效果包…', emphasisPath);
  await page.waitFor(`!!document.querySelector('.package-pending[data-status=ready]')`, '包检查', 60_000);
  await page.eval(js(`button('加入模板', document.querySelector('.package-pending')).click();`));
  await page.waitFor(js(`return !!button('加效果', document.querySelector('.add-effect'));`), '可以加效果');
  sha = await sampleSha(page);
  await clickButton(page, '加效果', `document.querySelector('.add-effect')`);
  await page.waitFor(`document.querySelectorAll('.recipe-item').length === 1 && !!document.querySelector('.recipe-fields [data-param=peakScale] mdui-text-field')`, '效果已加');
  const added = await sampleChanged(page, sha, '加效果后的试样');
  await commit(page, `document.querySelector('.recipe-fields [data-param=peakScale] mdui-text-field')`, '2');
  const scaled = await sampleChanged(page, added, '改放大比例后的试样');
  await commit(page, `document.querySelector('.recipe-seed')`, MAX_SEED);
  await page.waitFor(`document.querySelector('.recipe-seed')?.value === ${JSON.stringify(MAX_SEED)} && !document.querySelector('.recipe-seed')?.invalid`, '种子');
  check(scaled !== added && added !== sha, `效果：加“逐字强调”后试样变化，放大比例改为 2 再变化；固定种子填最大值 ${MAX_SEED}`);

  // 5. 试样：多行样本、换目标、拖时刻、试播
  await choose(page, `document.querySelector('.sample-select')`, 'workshop-multi-line-v1');
  await page.waitFor(js(`return sampleTitle().includes('多行样本') && !!sampleImg();`), '多行样本', 60_000);
  const targets = await page.eval(`[...document.querySelectorAll('.sample-target mdui-menu-item')].map((i) => i.value)`);
  await choose(page, `document.querySelector('.sample-target')`, 'clip');
  await page.waitFor(js(`return sampleTitle().includes('片段') && !!sampleImg();`), '目标：片段', 60_000);
  await page.eval(`(() => { const s = document.querySelector('.sample-time'); s.value = 8000; s.dispatchEvent(new Event('input')); })()`);
  await page.waitFor(`!!document.querySelector('.template-sample [data-status=ready] img[data-time="8000000"]')`, '拖到 8 秒', 60_000);
  await page.eval(`document.querySelector('.sample-play').click()`);
  const frames = new Set();
  for (let i = 0; i < 30; i++) {
    await sleep(100);
    frames.add(await page.eval(js(`return sampleImg()?.dataset.time;`)));
  }
  const playing = await page.eval(`document.querySelector('.sample-play').getAttribute('aria-label')`);
  await page.eval(`document.querySelector('.sample-play').click()`);
  check(targets.includes('mark') && frames.size >= 2 && playing === '暂停试播', `试样：多行样本（目标 ${targets.join('、')}）换到片段、拖到 8 秒、试播时画面前进（${frames.size} 个时刻）`);
  await snapshot(page, 'workshop');

  // 6. 保存后重读与保存前一致
  const draft = await definitionText(page);
  await page.eval(js(`saveButton().click();`));
  await page.waitFor(js(`return document.querySelector('.chips .tag')?.textContent === '我的模板' && !sampleTitle().includes('未保存的修改');`), '保存完成', 30_000);
  await selectTemplate(page, 'builtin:line-classic', '经典整行');
  await selectTemplate(page, created[0], fullName);
  const saved = await definitionText(page);
  const recipe = saved.definition.effectRecipes[0];
  check(
    saved.comparable === draft.comparable &&
      recipe?.parametersJson === '{"peakScale":2}' &&
      recipe?.fixedSeed === MAX_SEED &&
      saved.definition.style.unsungFill.material.parametersJson === '{"amplitude":0.3}' &&
      saved.definition.packages.every((p) => p.kind === 'library'),
    `保存后重读，完整定义与保存前一致（参数原文 {"peakScale":2}、{"amplitude":0.3}，种子 ${recipe?.fixedSeed}）；包引用变成 library 引用`,
  );
  await page.waitFor(`!!document.querySelector('.library-list [data-template-id="${created[0]}"] .template-thumb[data-status=ready] img')`, '列表缩略图', 60_000);
  check(true, '列表里新模板的缩略图按工坊样本画出');

  // 7. .mktpl：导出两个模板，导入回来，第三次传输提示要等几分钟
  await selectTemplate(page, 'builtin:token-highlight', '逐字强调');
  await clickButton(page, '复制并编辑');
  await page.waitFor(js(`return !!openDialog()?.querySelector('mdui-text-field');`), '复制对话框');
  await page.eval(js(`const f = openDialog().querySelector('mdui-text-field'); f.value = ${JSON.stringify(plainName)}; f.dispatchEvent(new Event('input', { bubbles: true, composed: true }));`));
  await clickDialogButton(page, '复制');
  await page.waitFor(js(`return templateName() === ${JSON.stringify(plainName)};`), '第二个模板');
  created.push(...(await idsNamed(page, plainName)));
  const originals = [...created];
  const plainSaved = await definitionText(page);

  await exportTemplates(page, originals);
  await page.waitFor(js(`return !openDialog();`), '导出完成', 60_000);
  const archive = await waitDownload([]);
  const exportedToast = await page.eval(js(`return toasts();`));
  check(statSync(archive).size > 0 && exportedToast.includes('已导出 2 个模板') && exportedToast.includes('SHA-256 已核对'), `导出两个模板：下载 MakoKara-templates.mktpl（${statSync(archive).size} 字节），大小和 SHA-256 已核对`);

  await openTransfer(page, '导入模板…');
  await setFile(page, '选择 .mktpl 文件', archive);
  await page.waitFor(js(`return openDialog()?.querySelectorAll('.transfer-row').length === 2;`), '预检列表', 30_000);
  const listed = await page.eval(js(`return openDialog().innerText;`));
  await clickDialogButton(page, '导入 2 个模板');
  await page.waitFor(js(`return !openDialog();`), '导入完成', 60_000);
  const fullIds = await idsNamed(page, fullName);
  const plainIds = await idsNamed(page, plainName);
  const imported = [...fullIds, ...plainIds].filter((id) => !originals.includes(id));
  created.push(...imported);
  const selected = await page.eval(js(`return templateName();`));
  check(
    listed.includes('导入后全部生成新 ID') && listed.includes('含效果') && imported.length === 2 && selected === fullName,
    `导入：预检列出两个模板（含效果），导入后得到两个新 ID 的模板（${imported.join('、')}），选中第一个`,
  );
  const fullCopy = await definitionText(page);
  await selectTemplate(page, plainIds.find((id) => !originals.includes(id)), plainName);
  const plainCopy = await definitionText(page);
  check(fullCopy.comparable === saved.comparable && plainCopy.comparable === plainSaved.comparable, '导入的两个模板，完整定义与原模板一致（包引用除外）');

  // 导出那份还占着配额：再导出一次占满，第三次写明要等几分钟。
  await exportTemplates(page, [originals[1]]);
  await page.waitFor(js(`return !openDialog() && toasts().includes('已导出 1 个模板');`), '第二次导出', 60_000);
  await exportTemplates(page, [originals[1]]);
  await page.waitFor(js(`return !!openDialog()?.querySelector('.notice-error');`), '第三次导出', 30_000);
  const quota = await page.eval(js(`return openDialog().querySelector('.notice-error').textContent;`));
  check(/请稍后再试，大约 \d 分钟后可以/.test(quota), `第三次传输写明要等几分钟：${quota}`);
  await snapshot(page, 'quota');
  await page.eval(js(`anyButton('取消', openDialog())?.click();`));
  await page.waitFor(js(`return !openDialog();`), '关闭对话框');

  // 8. 删除本次的模板
  for (const id of [...created]) {
    await page.eval(`document.querySelector('.library-list [data-template-id="${id}"]').click()`);
    await page.waitFor(js(`return [${JSON.stringify(fullName)}, ${JSON.stringify(plainName)}].includes(templateName()) && !!anyButton('模板操作');`), `选中 ${id}`);
    await menuItem(page, `document.querySelector('[aria-label="模板操作"]')`, '删除');
    await clickDialogButton(page, '删除');
    await page.waitFor(`!document.querySelector('.library-list [data-template-id="${id}"]')`, '删除后');
    created.splice(created.indexOf(id), 1);
  }
  check(!(await page.eval(`document.querySelector('.library-list').textContent.includes(${JSON.stringify(stamp)})`)), '删除本次创建的 4 个模板');

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
    const failure = join(tmpdir(), 'mako-library-smoke-failure.png');
    await writeFile(failure, await page.screenshot()).catch(() => undefined);
    console.error(`  失败时的页面：${failure}`);
    const text = await page.eval(`document.body.innerText`).catch(() => '');
    console.error(`  页面文字：${String(text).slice(0, 600).replace(/\n+/g, ' ⏎ ')}`);
  }
  for (const message of errors) console.error(`  页面异常：${message}`);
  if (created.length > 0) console.error(`  个人库里留下了冒烟模板“${fullName}”“${plainName}”（${created.length} 个），请在样式库里删除。`);
} finally {
  await browser.close();
  rmSync(assets, { recursive: true, force: true });
}
