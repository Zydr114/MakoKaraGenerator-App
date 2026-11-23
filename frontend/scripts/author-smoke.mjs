// W8 的一条真实 Chrome 主路径；运行前启动 pnpm dev:backend 和 pnpm dev。
// 运行时打包现有曲线变体，所有操作走 UI；成功时删除本次的草稿、组件和模板，失败时保留供复查。
// 控件按 ARIA 角色和名称找（scripts/ui-query.mjs），区域用样式库自己的类名限定。
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { isDeepStrictEqual, parseArgs } from 'node:util';
import { launchChrome } from './cdp.mjs';
import { shell, ui } from './ui-query.mjs';
import { packSampleEffect, packVariantEffect, sampleEffectsDir } from './effect-packages.mjs';

const { values } = parseArgs({ options: { url: { type: 'string', default: 'http://localhost:5173' }, headed: { type: 'boolean' }, screenshot: { type: 'string' } } });
const samples = sampleEffectsDir();
if (!samples) {
  console.log('[skipped] 找不到 tests/Fixtures/ScriptEffectsV2，跳过作者冒烟。');
  process.exit(0);
}
const assets = mkdtempSync(join(tmpdir(), 'mako-author-smoke-'));
const packagePath = join(assets, 'WordEmphasisCurves.mkfx');
const inkPath = join(assets, 'CustomInk.mkfx');
writeFileSync(packagePath, packVariantEffect('WordEmphasisCurves'));
writeFileSync(inkPath, packSampleEffect('CustomInk'));
const original = readFileSync(join(samples, 'WordEmphasis', 'src', 'WordEmphasis.cs'), 'utf8');
const name = `冒烟作者-${Date.now().toString(36)}`;
const MAX_SEED = '18446744073709551615';
const errors = [];
let configurationId;
let materialId;
let templateId;
let page;
const browser = await launchChrome({ headed: values.headed });

const componentIds = `[...document.querySelectorAll('.component-list [data-configuration-id]')].map((i) => i.dataset.configurationId)`;
const templateName = `document.querySelector('.template-name')?.textContent`;
const detailTags = `[...document.querySelectorAll('.template-detail .phead .tag')].map((t) => t.textContent)`;
const sampleImg = `document.querySelector('.template-sample [data-status=ready] img')`;
/** 按 CSS 选择器点（真实鼠标）：ui.click 的角色参数也接受选择器，名称不限。 */
const clickAt = (selector, opts = {}) => ui.click(page, selector, null, opts);
const section = (label) => ui.click(page, 'button', label, { scope: '.library-sections' });

async function importComponent(path) {
  const before = await page.eval(componentIds);
  await ui.setFile(page, '导入效果包…', path);
  await ui.waitDialog(page, '导入效果包');
  await page.waitFor(`!!document.querySelector('[role=dialog] .import-choices')`, '效果包预检', 60_000);
  await ui.clickInDialog(page, '导入');
  await ui.waitDialogClosed(page, '导入效果包', { timeout: 60_000 });
  await page.waitFor(`${componentIds}.some((id) => !${JSON.stringify(before)}.includes(id))`, '导入组件');
  return page.eval(`${componentIds}.find((id) => !${JSON.stringify(before)}.includes(id))`);
}
const selectComponent = (id) => clickAt(`[role=row]:has([data-configuration-id="${id}"])`);
const selectTemplate = async (id, expected) => {
  await clickAt(`[role=row]:has([data-template-id="${id}"])`);
  await page.waitFor(`${templateName} === ${JSON.stringify(expected)}`, `选中 ${expected}`);
};

async function source(text) {
  await page.waitFor(`!!document.querySelector('.cm-content')`, '源码编辑器');
  // 源码页签列出包内全部可编辑文件，默认停在 manifest.json；先切到 C# 文件。
  await clickAt(`.source-file[data-path$=".cs"]`);
  await page.waitFor(`document.querySelector('.source-file[aria-current=true]')?.dataset.path?.endsWith('.cs')`, '切到 C# 文件');
  await page.eval(`document.querySelector('.cm-content').focus()`);
  for (const type of ['keyDown', 'keyUp']) await page.send('Input.dispatchKeyEvent', { type, key: 'a', code: 'KeyA', modifiers: 2, windowsVirtualKeyCode: 65 });
  await page.send('Input.insertText', { text });
  await ui.wait(page, 'button', '保存修订', { scope: '.srcbar' });
  await ui.click(page, 'button', '保存并编译', { scope: '.srcbar' });
}

/** 模板里“从效果组件选…”：在对话框里按组件 ID 选，再点提交。 */
async function pickComponent(id, submit) {
  await ui.click(page, 'button', '从效果组件选…');
  await ui.waitDialog(page, '从效果组件选');
  await ui.click(page, 'select', '效果组件', { scope: '@dialog' });
  await clickAt(`[role=option][data-key="${id}"]`);
  await page.waitFor(`!!document.querySelector('[role=dialog] .component-pick-detail[data-component-id="${id}"]')`, '选中组件');
  await ui.clickInDialog(page, submit);
  await ui.waitDialogClosed(page, '从效果组件选');
}

async function chooseGpuDevice() {
  await ui.click(page, 'button', '（渲染设备）', { partial: true, scope: '.app-bar' });
  await page.waitFor(`!!document.querySelector('.render-device-menu')`, '设备菜单');
  await ui.click(page, 'menuitem', 'GPU', { scope: '.render-device-menu' });
  await page.waitFor(`!document.querySelector('.render-device-menu')`, '设备菜单关闭');
}

function passed(message) {
  console.log(`✓ ${message}`);
}

try {
  browser.cdp.on((message) => {
    if (message.method === 'Runtime.exceptionThrown') errors.push(message.params.exceptionDetails.exception?.description ?? 'exception');
  });
  page = await browser.openPage(`${new URL(values.url).origin}/`, { width: 1440, height: 1000 });
  await page.send('DOM.enable');
  await page.waitFor(shell.connected, '连接 Server');
  await chooseGpuDevice();
  await shell.goto(page, 'library');
  await section('效果组件');
  await page.waitFor(`!!document.querySelector('.component-list')`, '读取组件库', 30_000);
  configurationId = await importComponent(packagePath);
  await selectComponent(configurationId);
  await page.waitFor(`document.querySelector('.component-detail')?.dataset.configurationId === ${JSON.stringify(configurationId)}`, '组件详情');
  await ui.click(page, 'button', '编辑', { scope: '.component-detail' });
  await page.waitFor(`!!document.querySelector('.draft-config')`, '作者草稿', 60_000);
  await ui.fill(page, '名称', name, { scope: '.draft-config', enter: true });
  await ui.fill(page, null, '3', { scope: '.draft-config [data-param=peakScale]', enter: true });
  await ui.fill(page, '随机种子', MAX_SEED, { scope: '.draft-config', enter: true });
  await ui.click(page, 'select', '类型', { scope: '.draft-config [data-param=shape]' });
  await clickAt('[role=option][data-key="cubicBezier"]');
  await page.waitFor(`document.querySelector('.draft-config [data-param=shape] .curve-field')?.dataset.curveKind === 'cubicBezier' && !document.querySelector('.draft-config[data-pending]')`, '曲线和配置提交');
  await page.waitFor(`!!${sampleImg}`, '草稿试样', 120_000);
  passed('导入曲线包→编辑草稿，比例 3、最大种子、贝塞尔曲线与试样');

  await ui.click(page, 'tab', '源码');
  await source(original.replace('source.Parameters.GetNumber', 'sourc.Parameters.GetNumber'));
  await page.waitFor(`document.querySelector('.source-compile')?.dataset.outcome === 'failed'`, '错误编译', 120_000);
  await ui.wait(page, 'button', 'CS0103', { partial: true, scope: '.source-compile' });
  await ui.click(page, 'button', 'CS0103', { partial: true, scope: '.source-compile' });
  await page.waitFor(`!!document.querySelector('.cm-lintRange-error') && document.activeElement?.classList.contains('cm-content')`, '跳到诊断并显示标记');
  await source(`${original}\n// ${name}\n`);
  await page.waitFor(`document.querySelector('.source-compile')?.dataset.outcome === 'passed' && !document.querySelector('.source-compile[data-stale]')`, '修正后编译', 120_000);
  await ui.click(page, 'button', '采用编译通过的修订', { partial: true, scope: '.source-compile' });
  await ui.waitDialog(page, '采用修订并配置', { partial: true });
  await ui.clickInDialog(page, '采用源码与配置');
  await ui.waitDialogClosed(page, '采用修订并配置', { partial: true });
  passed('保存错误源码→CS0103 行列和编辑器诊断→修正后编译通过并采用修订');

  await ui.click(page, 'tab', '验证');
  await ui.click(page, 'button', '开始验证');
  await page.waitFor(`document.querySelector('.validation-result')?.dataset.outcome === 'passed'`, '作者验证', 180_000);
  const report = await page.eval(`document.querySelector('.validation-result').innerText.replace(/\\s+/g, ' ')`);
  if (!report.includes('360')) throw new Error(`验证帧数不符：${report}`);
  passed(`作者验证：${report}`);
  if (values.screenshot) await writeFile(values.screenshot.replace(/\.png$/, '-validation.png'), await page.screenshot());
  await ui.click(page, 'button', '保存到组件库', { scope: '.draft-detail' });
  await page.waitFor(`document.querySelector('.draft-detail')?.dataset.saveState === 'saved'`, '组件已保存', 60_000);
  await ui.click(page, 'button', '关闭草稿', { scope: '.draft-detail' });
  await page.waitFor(`!document.querySelector('.draft-detail')`, '关闭草稿');
  materialId = await importComponent(inkPath);

  await section('模板');
  await selectTemplate('builtin:line-classic', '经典整行');
  await ui.click(page, 'button', '复制并编辑');
  await ui.waitDialog(page, '复制到我的模板');
  await ui.fill(page, '名称', name, { scope: '@dialog' });
  await ui.clickInDialog(page, '复制');
  await ui.waitDialogClosed(page, '复制到我的模板', { timeout: 30_000 });
  await page.waitFor(`${templateName} === ${JSON.stringify(name)} && !!document.querySelector('.template-tabs')`, '模板副本', 30_000);
  templateId = await page.eval(`[...document.querySelectorAll('.library-list [data-template-id]')].find((i) => i.textContent.includes(${JSON.stringify(name)})).dataset.templateId`);
  await ui.click(page, 'tab', '效果');
  await pickComponent(configurationId, '加入效果');
  await ui.click(page, 'tab', '材质');
  await ui.click(page, 'button', '未唱填充', { scope: '.slot-buttons' });
  await pickComponent(materialId, '使用材质');
  await page.waitFor(`document.querySelector('.slot-material')?.dataset.kind === 'package'`, '未唱填充已用组件材质');
  await ui.click(page, 'tab', '高级');
  await page.waitFor(`document.querySelector('.template-json')?.textContent.includes(${JSON.stringify(configurationId)})`, '模板含组件配方');
  const definition = await page.eval(`JSON.parse(document.querySelector('.template-json').textContent)`);
  const recipe = definition.effectRecipes.find((r) => r.configurationId === configurationId);
  if (recipe?.fixedSeed !== MAX_SEED || JSON.parse(recipe.parametersJson).shape?.kind !== 'cubicBezier') throw new Error('模板没有保留组件参数和种子');
  if (definition.style.unsung.fill.packageId !== 'makokara.v2.custom-ink') throw new Error('未唱填充没有采用 CustomInk 材质');
  await ui.click(page, 'button', '保存', { scope: '.template-actions' });
  await page.waitFor(`${detailTags}[0] === '我的模板' && !${detailTags}.includes('未保存')`, '保存模板', 30_000);
  await selectTemplate('builtin:line-classic', '经典整行');
  await selectTemplate(templateId, name);
  await ui.click(page, 'tab', '高级');
  await page.waitFor(`document.querySelector('.template-json')?.textContent.includes(${JSON.stringify(configurationId)})`, '保存后重读');
  const reread = await page.eval(`JSON.parse(document.querySelector('.template-json').textContent)`);
  if (!isDeepStrictEqual([reread.effectRecipes, reread.style.unsung.fill], [definition.effectRecipes, definition.style.unsung.fill])) throw new Error('保存重读改变了效果或材质');
  await page.waitFor(`!!${sampleImg}`, '模板试样', 120_000);
  const sha = await page.eval(`${sampleImg}.dataset.sha`);
  passed('模板分别从效果和材质组件选择，保存重读保留曲线、最大种子与 CustomInk 未唱填充');
  if (values.screenshot) await writeFile(values.screenshot, await page.screenshot());

  await section('效果组件');
  for (const id of [configurationId, materialId]) {
    await selectComponent(id);
    await page.waitFor(`document.querySelector('.component-detail')?.dataset.configurationId === ${JSON.stringify(id)}`, '选中本次组件');
    await ui.menu(page, '组件操作', '删除…');
    await ui.clickInDialog(page, '删除');
    await page.waitFor(`!document.querySelector('.component-list [data-configuration-id="${id}"]')`, '删除本次组件');
  }
  configurationId = materialId = undefined;
  await section('模板');
  await selectTemplate(templateId, name);
  await ui.click(page, 'tab', '效果');
  await page.waitFor(`document.querySelector('.effects-tab')?.textContent.includes('来自效果组件（已删除）')`, '已删除来源提示');
  await page.waitFor(`${sampleImg}?.dataset.sha === ${JSON.stringify(sha)}`, '删除组件后模板帧与删除前相同', 120_000);
  passed('删除来源组件后模板照常出相同试样，并标出组件已删除');
  await ui.menu(page, '模板操作', '删除…');
  await ui.clickInDialog(page, '删除');
  await page.waitFor(`!document.querySelector('.library-list [data-template-id="${templateId}"]')`, '删除本次模板');
  templateId = undefined;
  if (errors.length) throw new Error(errors.join(' | '));
  passed('清理本次组件与模板，页面没有未处理异常');
} catch (error) {
  process.exitCode = 1;
  console.error(`✗ ${error instanceof Error ? error.message : error}`);
  if (page) {
    const failure = join(tmpdir(), 'mako-author-smoke-failure.png');
    await writeFile(failure, await page.screenshot()).catch(() => undefined);
    console.error(`失败截图：${failure}`);
    console.error((await page.eval('document.body.innerText').catch(() => '')).slice(-2000));
  }
  console.error(`本次名称 ${name}；保留组件 ${configurationId ?? '无'}、材质 ${materialId ?? '无'}、模板 ${templateId ?? '无'} 供复查。`);
} finally {
  await browser.close();
  rmSync(assets, { recursive: true, force: true });
}
