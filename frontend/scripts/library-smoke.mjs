// 样式库完整模板的浏览器冒烟（W7 设计 §5 第 1～4、9 条）：用真实 Chrome 走一遍样式库的完整定义编辑。
// 复制“经典整行” → 字体：从目录选、字体检查逐字列出、目录里没有的字体标出
// → 材质：导入“自定义波纹材质”作未唱填充，试样焦点回显，改参数试样跟着变，越界时写范围并停用保存，移除在用的包被拦下，焦点切到已唱填充
// → 效果：导入“逐字强调”并加效果，放大比例 2、最大种子，试样跟着变
// → 试样：多行样本、换目标、拖时刻、试播 → 保存后重读与保存前一致（参数原文、最大种子）→ 列表缩略图
// → .mktpl：导出两个模板并核对，导入得到两个新 ID、定义一致，第三次传输写明要等几分钟 → 删除本次的模板。
//
// 前提：pnpm dev:backend 与 pnpm dev 都在运行；效果包取后端仓库 samples/ScriptEffectsV2（找不到时跳过并写明原因）。
// 个人库里只增删本次创建的模板。控件按 ARIA 角色和名称找（scripts/ui-query.mjs），区域用样式库自己的类名限定。
// 用法：node scripts/library-smoke.mjs [--url http://localhost:5173] [--screenshot out.png] [--headed]

import { mkdtempSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { parseArgs } from 'node:util';
import { launchChrome } from './cdp.mjs';
import { shell, ui, uiJs } from './ui-query.mjs';
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
  console.log('[skipped] 找不到后端仓库的 samples/ScriptEffectsV2（用 MAKO_BACKEND_REPO 指定后端仓库），没有效果包可用，跳过样式库冒烟。');
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

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const templateName = `document.querySelector('.template-name')?.textContent`;
const detailTags = `[...document.querySelectorAll('.template-detail .phead .tag')].map((t) => t.textContent)`;
const sampleImg = `document.querySelector('.template-sample [data-status=ready] img')`;
const sampleFocus = `document.querySelector('.template-sample')?.dataset.focus`;
// 试样标题（样本、目标、材质焦点、时刻）加画布说明（“未保存的修改，……”）
const sampleText = `((document.querySelector('.template-sample')?.dataset.title ?? '') + ' ' + (document.querySelector('.template-sample .canvas-hint')?.textContent ?? ''))`;
const saveDisabled = uiJs(`const b = __ui.find('button', '保存', false, '.template-actions'); return !!b && __ui.disabled(b);`);
const listIds = (name) => `[...document.querySelectorAll('.library-list [data-template-id]')].filter((i) => i.textContent.includes(${JSON.stringify(name)})).map((i) => i.dataset.templateId)`;
const dialogNote = `[...document.querySelectorAll('[role=dialog] .note.err')].map((n) => n.textContent).join(' ')`;

/** 按 CSS 选择器点（真实鼠标）：ui.click 的角色参数也接受选择器，名称不限。 */
const clickAt = (page, selector, opts = {}) => ui.click(page, selector, null, opts);
/** 区域里唯一的输入框改值并按 Enter 提交。 */
const commitIn = (page, scope, value) => ui.fill(page, null, value, { scope, enter: true });

/** 等试样画面换成和 before 不同的一帧，返回新帧的 SHA。 */
async function sampleChanged(page, before, label) {
  await page.waitFor(`!!${sampleImg} && ${sampleImg}.dataset.sha !== ${JSON.stringify(before)}`, label, 60_000);
  return page.eval(`${sampleImg}.dataset.sha`);
}
const sampleSha = (page) => page.eval(`${sampleImg}?.dataset.sha`);

async function selectTemplate(page, templateId, name) {
  await clickAt(page, `[role=row]:has([data-template-id="${templateId}"])`);
  await page.waitFor(`${templateName} === ${JSON.stringify(name)}`, `选中 ${name}`);
}

/** 高级页签里的完整定义；包引用只留包 ID 和修订（保存后会从 asset 引用变成 library 引用）。 */
async function definitionText(page) {
  await ui.click(page, 'tab', '高级');
  await page.waitFor(`!!document.querySelector('.template-json')`, '完整定义');
  const definition = JSON.parse(await page.eval(`document.querySelector('.template-json').textContent`));
  return { definition, comparable: JSON.stringify({ ...definition, packages: definition.packages.map((p) => `${p.packageId}@${p.packageRevision}`) }) };
}

async function copyTemplate(page, name) {
  await ui.click(page, 'button', '复制并编辑');
  await ui.waitDialog(page, '复制到我的模板');
  await ui.fill(page, '名称', name, { scope: '@dialog' });
  await ui.clickInDialog(page, '复制');
  await ui.waitDialogClosed(page, '复制到我的模板', { timeout: 30_000 });
  await page.waitFor(`${templateName} === ${JSON.stringify(name)}`, `复制为 ${name}`, 30_000);
}

/** 导入效果包：选文件，等检查结果，加入模板。 */
async function importPackage(page, path, packageId) {
  await ui.setFile(page, '导入效果包…', path);
  await page.waitFor(`!!document.querySelector('.package-pending[data-status=ready]')`, '包检查', 60_000);
  await ui.click(page, 'button', '加入模板', { scope: '.package-pending' });
  await page.waitFor(`!!document.querySelector('.package-item[data-package-id="${packageId}"]')`, '包已加入');
}

async function waitDownload(before) {
  for (let i = 0; i < 150; i++) {
    const fresh = readdirSync(downloads).filter((f) => f.startsWith('MakoKara-templates') && !f.endsWith('.crdownload') && !before.includes(f));
    if (fresh.length > 0) return join(downloads, fresh[0]);
    await sleep(200);
  }
  throw new Error('等待 .mktpl 下载超时');
}

/** 导出对话框：选“我的”库，只勾 ids，点导出。 */
async function exportTemplates(page, ids) {
  await ui.menu(page, '模板库的更多操作', '导出模板…');
  await ui.waitDialog(page, '导出模板');
  await ui.choose(page, '库', '我的', { scope: '@dialog' });
  await page.waitFor(`document.querySelectorAll('[role=dialog] .transfer-row').length >= ${ids.length}`, '导出列表');
  if (await page.eval(uiJs(`const b = __ui.find('button', '全不选', false, '@dialog'); return !!b && !__ui.disabled(b);`))) await ui.clickInDialog(page, '全不选');
  for (const id of ids) await ui.setChecked(page, 'checkbox', null, true, { scope: `[role=dialog] .transfer-row[data-template-id="${id}"]` });
  await ui.clickInDialog(page, `导出 ${ids.length} 个模板`);
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

  page = await browser.openPage(`${ORIGIN}/`, { width: 1440, height: 1000 });
  await page.send('DOM.enable');
  await browser.cdp.send('Browser.setDownloadBehavior', { behavior: 'allow', downloadPath: downloads });
  await page.waitFor(shell.connected, '连接 Server');
  await ui.click(page, 'button', '（渲染设备）', { partial: true, scope: '.app-bar' });
  await page.waitFor(`!!document.querySelector('.render-device-menu')`, '设备菜单');
  await ui.click(page, 'menuitem', 'GPU', { scope: '.render-device-menu' });
  await page.waitFor(`!document.querySelector('.render-device-menu')`, '设备菜单关闭');

  // 1. 复制“经典整行”
  await shell.goto(page, 'library');
  await page.waitFor(`!!document.querySelector('.library-list [data-template-id="builtin:line-classic"]')`, '样式库');
  await selectTemplate(page, 'builtin:line-classic', '经典整行');
  await copyTemplate(page, fullName);
  await page.waitFor(`!!document.querySelector('.font-family')`, '进入编辑');
  created.push(...(await page.eval(listIds(fullName))));
  const tabs = await page.eval(uiJs(`return __ui.all('tab', null, false, '.template-tabs').map((t) => __ui.text(t));`));
  check(created.length === 1 && tabs.join('、') === '外观、材质、效果、高级', `复制“经典整行”为“${fullName}”，进入编辑（${tabs.join('、')}四个页签）`);

  // 2. 字体：从目录选；字体检查逐字列出；目录里没有的字体标出
  await ui.fill(page, '字体', 'noto sans', { scope: '.font-family' });
  await page.waitFor(`document.querySelectorAll('.font-family .font-option').length > 0 || !!document.querySelector('.font-family .font-options-note')`, '字体目录', 30_000);
  let font = await page.eval(`document.querySelector('.font-family .font-option')?.dataset.family`);
  if (!font) {
    // 没有 Noto Sans 时取目录里的第一个。
    await ui.fill(page, '字体', '', { scope: '.font-family' });
    await page.waitFor(`document.querySelectorAll('.font-family .font-option').length > 0`, '字体目录', 30_000);
    font = await page.eval(`document.querySelector('.font-family .font-option')?.dataset.family`);
  }
  await ui.click(page, 'option', font, { scope: '.font-family' });
  await page.waitFor(uiJs(`return !document.querySelector('.font-family .font-options') && __ui.find('textbox', '字体', false, '.font-family')?.value === ${JSON.stringify(font)};`), '选中字体');
  await ui.click(page, 'button', '字体检查');
  await ui.fill(page, '再检查一段文字', '朝の光Ж');
  await ui.click(page, 'button', '检查', { scope: '.font-check' });
  await page.waitFor(`document.querySelector('.font-check')?.dataset.status === 'done'`, '字体检查', 30_000);
  const runs = await page.eval(`document.querySelectorAll('.font-runs tr').length`);
  const fontPicked = !(await page.eval(`!!document.querySelector('.font-family .font-missing')`));
  check(fontPicked && runs > 0, `字体：从目录选中“${font}”，没有“后端没有”标记；字体检查逐字列出实际字体（${runs} 行）`);
  await ui.fill(page, '字体', '不存在的字体XYZ', { scope: '.font-family', enter: true });
  await page.waitFor(`!!document.querySelector('.font-family .font-missing')`, '缺字体标记');
  const missing = await page.eval(`document.querySelector('.font-family .font-missing').textContent`);
  await ui.fill(page, '字体', font, { scope: '.font-family', enter: true });
  await page.waitFor(`!document.querySelector('.font-family .font-missing')`, '改回目录里的字体');
  check(!!missing, `目录里没有的字体照常保留并标出（${missing}）`);

  // 3. 材质：导入“自定义波纹材质”作未唱填充
  await ui.click(page, 'tab', '材质');
  await page.waitFor(`!!document.querySelector('.slot-material')`, '材质页签');
  await importPackage(page, inkPath, 'makokara.v2.custom-ink');
  await ui.click(page, 'button', '未唱填充', { scope: '.slot-buttons' });
  await ui.click(page, 'button', '效果包', { scope: '.slot-material .material-kind' });
  await page.waitFor(`document.querySelector('.slot-material')?.dataset.kind === 'package'`, '包材质');
  await page.waitFor(`${sampleFocus} === 'unsungFill' && ${sampleText}.includes('未保存的修改') && !!${sampleImg}`, '试样焦点', 60_000);
  const params = await page.eval(`[...document.querySelectorAll('.material-params [data-param]')].map((r) => r.dataset.param).join(',')`);
  check(params === 'frequencyHz,amplitude,colorA,colorB', `材质：导入 CustomInk 作未唱填充，参数控件 ${params}；试样后端回显焦点“未唱填充”`);

  const amplitude = '.material-params [data-param=amplitude]';
  let sha = await sampleSha(page);
  await commitIn(page, amplitude, '0.3');
  const afterAmplitude = await sampleChanged(page, sha, '改幅度后的试样');
  check(afterAmplitude !== sha, `改“混色波纹幅度”为 0.3，试样画面跟着变（${sha.slice(0, 12)} → ${afterAmplitude.slice(0, 12)}）`);

  await commitIn(page, amplitude, '0.9');
  await page.waitFor(`!!document.querySelector('${amplitude} .param-problem')`, '越界提示');
  const problem = await page.eval(`document.querySelector('${amplitude} .param-problem').textContent`);
  const blocked = (await page.eval(saveDisabled)) && (await page.eval(`!!document.querySelector('.template-problem')`));
  check(blocked && problem.includes('0.5'), `参数越界：控件旁写明范围（${problem}），“保存”停用`);
  await commitIn(page, amplitude, '0.3');
  await page.waitFor(`!document.querySelector('${amplitude} .param-problem') && !${saveDisabled}`, '改回 0.3');

  await clickAt(page, 'button', { scope: '.package-item[data-package-id="makokara.v2.custom-ink"]' });
  await page.waitFor(`!!document.querySelector('.package-notice')`, '移除被拦下');
  const notice = await page.eval(`document.querySelector('.package-notice').textContent`);
  check(notice.includes('未唱填充') && (await page.eval(`!!document.querySelector('.package-item[data-package-id="makokara.v2.custom-ink"]')`)), `移除还在用的包被拦下：${notice}`);

  await ui.click(page, 'button', '已唱填充', { scope: '.slot-buttons' });
  await page.waitFor(`${sampleFocus} === 'sungFill' && !!${sampleImg}`, '焦点已唱填充', 60_000);
  check((await page.eval(sampleText)).includes('材质焦点 已唱填充'), '材质焦点切到“已唱填充”，后端回显的焦点一致');
  await snapshot(page, 'material');

  // 4. 效果：导入“逐字强调”，放大比例 2，最大种子
  await ui.click(page, 'tab', '效果');
  await importPackage(page, emphasisPath, 'makokara.v2.word-emphasis');
  await ui.wait(page, 'button', '加效果', { scope: '.add-effect' });
  sha = await sampleSha(page);
  await ui.click(page, 'button', '加效果', { scope: '.add-effect' });
  await page.waitFor(`document.querySelectorAll('.recipe-item').length === 1 && !!document.querySelector('.recipe-params [data-param=peakScale] input')`, '效果已加');
  const added = await sampleChanged(page, sha, '加效果后的试样');
  await commitIn(page, '.recipe-params [data-param=peakScale]', '2');
  const scaled = await sampleChanged(page, added, '改放大比例后的试样');
  await ui.fill(page, '种子', MAX_SEED, { scope: '.recipe-fields', enter: true });
  const seedInput = `document.querySelector('.recipe-seed input')`;
  await page.waitFor(`${seedInput}?.value === ${JSON.stringify(MAX_SEED)} && ${seedInput}.getAttribute('aria-invalid') !== 'true'`, '种子');
  check(scaled !== added && added !== sha, `效果：加“逐字强调”后试样变化，放大比例改为 2 再变化；固定种子填最大值 ${MAX_SEED}`);

  // 5. 试样：多行样本、换目标、拖时刻、试播
  await ui.choose(page, '样本', '多行样本', { scope: '.template-sample' });
  await page.waitFor(`${sampleText}.includes('多行样本') && !!${sampleImg}`, '多行样本', 60_000);
  await ui.click(page, 'select', '目标', { scope: '.template-sample' });
  await ui.wait(page, 'option', '片段');
  const targets = await page.eval(uiJs(`return __ui.all('option').map((o) => __ui.text(o));`));
  await ui.click(page, 'option', '片段');
  await page.waitFor(`${sampleText}.includes('片段') && !!${sampleImg}`, '目标：片段', 60_000);
  await page.eval(uiJs(`const s = __ui.find('slider', null, false, '.sample-time'); __ui.setValue(s, '8000');`));
  await page.waitFor(`!!document.querySelector('.template-sample [data-status=ready] img[data-time="8000000"]')`, '拖到 8 秒', 60_000);
  await ui.click(page, 'button', '试播', { scope: '.template-sample' });
  const frames = new Set();
  for (let i = 0; i < 30; i++) {
    await sleep(100);
    frames.add(await page.eval(`${sampleImg}?.dataset.time`));
  }
  const playing = await ui.exists(page, 'button', '暂停试播', { scope: '.template-sample' });
  await ui.click(page, 'button', '暂停试播', { scope: '.template-sample' });
  check(targets.includes('标记') && frames.size >= 2 && playing, `试样：多行样本（目标 ${targets.join('、')}）换到片段、拖到 8 秒、试播时画面前进（${frames.size} 个时刻）`);
  await snapshot(page, 'workshop');

  // 6. 保存后重读与保存前一致
  const draft = await definitionText(page);
  await ui.click(page, 'button', '保存', { scope: '.template-actions' });
  await page.waitFor(`${detailTags}[0] === '我的模板' && !${detailTags}.includes('未保存') && !${sampleText}.includes('未保存的修改')`, '保存完成', 30_000);
  await selectTemplate(page, 'builtin:line-classic', '经典整行');
  await selectTemplate(page, created[0], fullName);
  const saved = await definitionText(page);
  const recipe = saved.definition.effectRecipes[0];
  check(
    saved.comparable === draft.comparable &&
      recipe?.parametersJson === '{"peakScale":2}' &&
      recipe?.fixedSeed === MAX_SEED &&
      saved.definition.style.unsung.fill.parametersJson === '{"amplitude":0.3}' &&
      saved.definition.packages.every((p) => p.kind === 'library'),
    `保存后重读，完整定义与保存前一致（参数原文 {"peakScale":2}、{"amplitude":0.3}，种子 ${recipe?.fixedSeed}）；包引用变成 library 引用`,
  );
  await page.waitFor(`!!document.querySelector('.library-list [data-template-id="${created[0]}"] .template-thumb[data-status=ready] img')`, '列表缩略图', 60_000);
  check(true, '列表里新模板的缩略图按工坊样本画出');

  // 7. .mktpl：导出两个模板，导入回来，第三次传输提示要等几分钟
  await selectTemplate(page, 'builtin:token-highlight', '逐字强调');
  await copyTemplate(page, plainName);
  created.push(...(await page.eval(listIds(plainName))));
  const originals = [...created];
  const plainSaved = await definitionText(page);

  await exportTemplates(page, originals);
  await ui.waitDialogClosed(page, '导出模板', { timeout: 60_000 });
  const archive = await waitDownload([]);
  const exportedToast = await ui.toastText(page);
  check(statSync(archive).size > 0 && exportedToast.includes('已导出 2 个模板') && exportedToast.includes('SHA-256 已核对'), `导出两个模板：下载 MakoKara-templates.mktpl（${statSync(archive).size} 字节），大小和 SHA-256 已核对`);

  await ui.click(page, 'button', '导入…', { scope: '.listfoot' });
  await ui.waitDialog(page, '导入模板');
  await ui.setFile(page, '选择 .mktpl 文件', archive);
  await page.waitFor(`document.querySelectorAll('[role=dialog] .transfer-row').length === 2`, '预检列表', 30_000);
  const listed = await page.eval(`document.querySelector('[role=dialog]').innerText`);
  await ui.clickInDialog(page, '导入 2 个模板');
  await ui.waitDialogClosed(page, '导入模板', { timeout: 60_000 });
  const fullIds = await page.eval(listIds(fullName));
  const plainIds = await page.eval(listIds(plainName));
  const imported = [...fullIds, ...plainIds].filter((id) => !originals.includes(id));
  created.push(...imported);
  const selected = await page.eval(templateName);
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
  await ui.waitDialogClosed(page, '导出模板', { timeout: 60_000 });
  await page.waitFor(`document.querySelector('.toast-region .toast-text')?.textContent.includes('已导出 1 个模板')`, '第二次导出', 30_000);
  await exportTemplates(page, [originals[1]]);
  await page.waitFor(`${dialogNote}.length > 0`, '第三次导出', 30_000);
  const quota = await page.eval(dialogNote);
  check(/请稍后再试，大约 \d 分钟后可以/.test(quota), `第三次传输写明要等几分钟：${quota}`);
  await snapshot(page, 'quota');
  await ui.clickInDialog(page, '取消');
  await ui.waitDialogClosed(page, '导出模板');

  // 8. 删除本次的模板
  for (const id of [...created]) {
    await clickAt(page, `[role=row]:has([data-template-id="${id}"])`);
    await page.waitFor(`[${JSON.stringify(fullName)}, ${JSON.stringify(plainName)}].includes(${templateName})`, `选中 ${id}`);
    await ui.menu(page, '模板操作', '删除…');
    await ui.clickInDialog(page, '删除');
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
