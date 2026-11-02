// W8 的一条真实 Chrome 主路径；运行前启动 pnpm dev:backend 和 pnpm dev。
// 运行时打包现有曲线变体，所有操作走 UI；成功时删除本次的草稿、组件和模板，失败时保留供复查。
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { isDeepStrictEqual, parseArgs } from 'node:util';
import { launchChrome } from './cdp.mjs';
import { shell } from './ui-query.mjs';
import { packSampleEffect, packVariantEffect, sampleEffectsDir } from './effect-packages.mjs';

const { values } = parseArgs({ options: { url: { type: 'string', default: 'http://localhost:5173' }, headed: { type: 'boolean' }, screenshot: { type: 'string' } } });
const samples = sampleEffectsDir();
if (!samples) {
  console.log('[skipped] 找不到 samples/ScriptEffects（用 MAKO_BACKEND_REPO 指定），跳过作者冒烟。');
  process.exit(0);
}
const assets = mkdtempSync(join(tmpdir(), 'mako-author-smoke-'));
const packagePath = join(assets, 'WordEmphasisCurves.mkfx');
const inkPath = join(assets, 'CustomInk.mkfx');
writeFileSync(packagePath, packVariantEffect('WordEmphasisCurves'));
writeFileSync(inkPath, packSampleEffect('CustomInk'));
const original = readFileSync(join(samples, 'WordEmphasis', 'src', 'WordEmphasis.cs'), 'utf8');
const name = `冒烟作者-${Date.now().toString(36)}`;
const errors = [];
let configurationId;
let materialId;
let templateId;
let page;
const browser = await launchChrome({ headed: values.headed });
const js = (body) => `(() => {
  const button = (text, scope = document) => [...scope.querySelectorAll('mdui-button,mdui-button-icon')].find(b => !b.disabled && (b.textContent.trim() === text || b.getAttribute('aria-label') === text));
  const dialog = () => [...document.querySelectorAll('mdui-dialog')].find(d => d.open);
  ${body}
})()`;
const sleep = (ms) => new Promise(resolve => setTimeout(resolve, ms));
async function click(text, scope = 'document') {
  const expression = js(`return button(${JSON.stringify(text)}, ${scope});`);
  await page.waitFor(`!!${expression}`, `按钮 ${text}`);
  await page.eval(`${expression}.dispatchEvent(new MouseEvent('click', { bubbles: true, composed: true }))`);
}
async function commit(selector, value, enter = true) {
  await page.waitFor(`!!document.querySelector(${JSON.stringify(selector)})`, `输入框 ${selector}`);
  await page.eval(`(() => { const f = document.querySelector(${JSON.stringify(selector)}); f.value = ${JSON.stringify(value)}; f.dispatchEvent(new Event('input', { bubbles: true, composed: true })); })()`);
  await sleep(80);
  if (enter) await page.eval(`document.querySelector(${JSON.stringify(selector)}).dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, composed: true }))`);
}
async function choose(selector, value) {
  await page.eval(`(() => { const s = document.querySelector(${JSON.stringify(selector)}); s.value = ${JSON.stringify(value)}; s.dispatchEvent(new Event('change')); })()`);
}
async function tab(scope, value) {
  await page.eval(`document.querySelector('${scope} mdui-tab[value=${value}]').click()`);
}
async function importComponent(path) {
  const before = await page.eval(`[...document.querySelectorAll('.library-list [data-configuration-id]')].map(i => i.dataset.configurationId)`);
  const { root } = await page.send('DOM.getDocument', { depth: -1 });
  const { nodeId } = await page.send('DOM.querySelector', { nodeId: root.nodeId, selector: '.library-list input[type=file]' });
  await page.send('DOM.setFileInputFiles', { nodeId, files: [path] });
  await page.waitFor(`!!document.querySelector('.component-import .package-pending[data-status=ready]')`, '效果包预检');
  await click('导入', 'dialog()');
  await page.waitFor(js(`return !dialog() && [...document.querySelectorAll('.library-list [data-configuration-id]')].some(i => !${JSON.stringify(before)}.includes(i.dataset.configurationId));`), '导入组件');
  return page.eval(`[...document.querySelectorAll('.library-list [data-configuration-id]')].find(i => !${JSON.stringify(before)}.includes(i.dataset.configurationId)).dataset.configurationId`);
}
async function source(text) {
  await page.waitFor(`!!document.querySelector('.cm-content')`, '源码编辑器');
  await page.eval(`document.querySelector('.cm-content').focus()`);
  for (const type of ['keyDown', 'keyUp']) await page.send('Input.dispatchKeyEvent', { type, key: 'a', code: 'KeyA', modifiers: 2, windowsVirtualKeyCode: 65 });
  await page.send('Input.insertText', { text });
  await page.waitFor(`!document.querySelector('.source-save').disabled`, '源码改动');
  await click('保存并编译');
}
async function remove(menuLabel, itemText) {
  await click(menuLabel);
  const expression = `[...document.querySelectorAll('mdui-menu-item')].find(i => i.textContent.trim() === ${JSON.stringify(itemText)} && !i.disabled)`;
  await page.waitFor(`!!${expression} && ${expression}.getBoundingClientRect().height > 0`, itemText);
  await sleep(250);
  const point = await page.eval(`(() => { const r = ${expression}.getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 }; })()`);
  for (const type of ['mousePressed', 'mouseReleased']) await page.send('Input.dispatchMouseEvent', { type, ...point, button: 'left', clickCount: 1 });
  const confirm = js(`return dialog() && button('删除', dialog());`);
  await page.waitFor(`!!${confirm} && ${confirm}.getBoundingClientRect().height > 0`, '删除确认框可见');
  await sleep(300);
  const action = await page.eval(`(() => { const r = ${confirm}.getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 }; })()`);
  for (const type of ['mousePressed', 'mouseReleased']) await page.send('Input.dispatchMouseEvent', { type, ...action, button: 'left', clickCount: 1 });
}
function passed(message) { console.log(`✓ ${message}`); }

try {
  browser.cdp.on(message => {
    if (message.method === 'Runtime.exceptionThrown') errors.push(message.params.exceptionDetails.exception?.description ?? 'exception');
  });
  page = await browser.openPage(`${new URL(values.url).origin}/`, { width: 1440, height: 1200 });
  await page.send('DOM.enable');
  await page.waitFor(shell.connected, '连接 Server');
  await shell.goto(page, 'library');
  await page.waitFor(`!!document.querySelector('.library-sections mdui-tab[value=components]')`, '组件库');
  await tab('.library-sections', 'components');
  await page.waitFor(`!!document.querySelector('.component-empty, .library-list [data-configuration-id]')`, '读取组件库');
  configurationId = await importComponent(packagePath);
  await page.eval(`document.querySelector('.library-list [data-configuration-id="${configurationId}"]').click()`);
  await click('编辑');
  await page.waitFor(`!!document.querySelector('.draft-config')`, '作者草稿');
  await commit('.draft-config mdui-text-field[label="名称"]', name);
  await commit('.draft-config [data-param=peakScale] mdui-text-field', '3');
  await commit('.draft-config .recipe-seed', '18446744073709551615');
  await choose('.draft-config [data-param=shape] mdui-select[label="类型"]', 'cubicBezier');
  await page.waitFor(`document.querySelector('.draft-config [data-param=shape] .curve-field')?.dataset.curveKind === 'cubicBezier' && !document.querySelector('.draft-config[data-pending]')`, '曲线和配置提交');
  await page.waitFor(`!!document.querySelector('.template-sample [data-status=ready] img')`, '草稿试样', 120_000);
  passed('导入曲线包→编辑草稿，比例 3、最大种子、贝塞尔曲线与试样');

  await tab('.draft-tabs', 'source');
  await source(original.replace('context.Parameters.GetNumber', 'contxt.Parameters.GetNumber'));
  await page.waitFor(`document.querySelector('.source-compile')?.dataset.outcome === 'failed'`, '错误编译', 120_000);
  await page.waitFor(`[...document.querySelectorAll('.compile-row')].some(r => r.textContent.includes('CS0103'))`, 'CS0103 诊断');
  await page.eval(`[...document.querySelectorAll('.compile-row')].find(r => r.textContent.includes('CS0103')).click()`);
  await page.waitFor(`!!document.querySelector('.cm-lintRange-error') && document.activeElement?.classList.contains('cm-content')`, '跳到诊断并显示标记');
  await source(`${original}\n// ${name}\n`);
  await page.waitFor(`document.querySelector('.source-compile')?.dataset.outcome === 'passed' && !document.querySelector('.source-compile[data-stale]')`, '修正后编译', 120_000);
  passed('保存错误源码→CS0103 行列和编辑器诊断→修正后编译通过');

  await tab('.draft-tabs', 'validation');
  await click('开始验证');
  await page.waitFor(`document.querySelector('.validation-result')?.dataset.outcome === 'passed'`, '作者验证', 180_000);
  const report = await page.eval(`document.querySelector('.validation-outcome').textContent`);
  if (!report.includes('360')) throw new Error(`验证帧数不符：${report}`);
  passed(`作者验证：${report}`);
  if (values.screenshot) await writeFile(values.screenshot.replace(/\.png$/, '-validation.png'), await page.screenshot());
  const save = `document.querySelector('.draft-detail .template-actions mdui-button[variant=filled]')`;
  await page.waitFor(`!!${save} && !${save}.disabled`, '保存组件');
  await page.eval(`${save}.click()`);
  await page.waitFor(`document.querySelector('.draft-detail')?.dataset.saveState === 'saved'`, '组件已保存');
  await click('关闭', `document.querySelector('.draft-detail')`);
  await page.waitFor(`!document.querySelector('.draft-detail')`, '关闭草稿');
  materialId = await importComponent(inkPath);

  await tab('.library-sections', 'templates');
  await page.eval(`document.querySelector('.library-list [data-template-id="builtin:line-classic"]').click()`);
  await click('复制并编辑');
  await page.waitFor(js(`return !!dialog()?.querySelector('mdui-text-field');`), '复制模板');
  await commit('mdui-dialog[open] mdui-text-field', name, false);
  await click('复制', 'dialog()');
  await page.waitFor(`document.querySelector('.template-name')?.textContent === ${JSON.stringify(name)} && !!document.querySelector('.template-tabs')`, '模板副本');
  templateId = await page.eval(`[...document.querySelectorAll('.library-list [data-template-id]')].find(i => i.textContent.includes(${JSON.stringify(name)})).dataset.templateId`);
  await tab('.template-tabs', 'effects');
  await click('从效果组件选…');
  await page.waitFor(`!!document.querySelector('.component-picker-dialog mdui-menu-item[value="${configurationId}"]')`, '组件选择器');
  await choose('.component-picker-dialog mdui-select[label="效果组件"]', configurationId);
  await click('加入效果', 'dialog()');
  await page.waitFor(js(`return !dialog();`), '效果已加入');
  await tab('.template-tabs', 'material');
  await page.waitFor(`!!document.querySelector('.slot-buttons [data-slot=unsungFill]')`, '材质页签');
  await page.eval(`document.querySelector('.slot-buttons [data-slot=unsungFill]').click()`);
  await click('从效果组件选…');
  await page.waitFor(`!!document.querySelector('.component-picker-dialog mdui-menu-item[value="${materialId}"]')`, '材质组件选择器');
  await choose('.component-picker-dialog mdui-select[label="效果组件"]', materialId);
  await click('使用材质', 'dialog()');
  await page.waitFor(js(`return !dialog() && document.querySelector('.slot-material')?.dataset.kind === 'package';`), '未唱填充已用组件材质');
  await tab('.template-tabs', 'advanced');
  await page.waitFor(`document.querySelector('.template-json')?.textContent.includes(${JSON.stringify(configurationId)})`, '模板含组件配方');
  const definition = await page.eval(`JSON.parse(document.querySelector('.template-json').textContent)`);
  const recipe = definition.effectRecipes.find(r => r.configurationId === configurationId);
  if (recipe?.fixedSeed !== '18446744073709551615' || JSON.parse(recipe.parametersJson).shape?.kind !== 'cubicBezier') throw new Error('模板没有保留组件参数和种子');
  if (definition.style.unsungFill.material.packageId !== 'makokara.custom-ink') throw new Error('未唱填充没有采用 CustomInk 材质');
  await click('保存', `document.querySelector('.template-actions')`);
  await page.waitFor(`document.querySelector('.chips .tag')?.textContent === '我的模板'`, '保存模板');
  await page.eval(`document.querySelector('.library-list [data-template-id="builtin:line-classic"]').click()`);
  await page.waitFor(`document.querySelector('.template-name')?.textContent === '经典整行'`, '切离已保存模板');
  await page.eval(`document.querySelector('.library-list [data-template-id="${templateId}"]').click()`);
  await page.waitFor(`document.querySelector('.template-name')?.textContent === ${JSON.stringify(name)}`, '重开已保存模板');
  await tab('.template-tabs', 'advanced');
  await page.waitFor(`document.querySelector('.template-json')?.textContent.includes(${JSON.stringify(configurationId)})`, '保存后重读');
  const reread = await page.eval(`JSON.parse(document.querySelector('.template-json').textContent)`);
  if (!isDeepStrictEqual([reread.effectRecipes, reread.style.unsungFill.material], [definition.effectRecipes, definition.style.unsungFill.material])) throw new Error('保存重读改变了效果或材质');
  await page.waitFor(`!!document.querySelector('.template-sample [data-status=ready] img')`, '模板试样');
  const sha = await page.eval(`document.querySelector('.template-sample [data-status=ready] img').dataset.sha`);
  passed('模板分别从效果和材质组件选择，保存重读保留曲线、最大种子与 CustomInk 未唱填充');
  if (values.screenshot) await writeFile(values.screenshot, await page.screenshot());

  await tab('.library-sections', 'components');
  for (const id of [configurationId, materialId]) {
    await page.eval(`document.querySelector('.library-list [data-configuration-id="${id}"]').click()`);
    await page.waitFor(`document.querySelector('.component-detail')?.dataset.configurationId === ${JSON.stringify(id)}`, '选中本次组件');
    await remove('组件操作', '删除…');
    await page.waitFor(`!document.querySelector('.library-list [data-configuration-id="${id}"]')`, '删除本次组件');
  }
  configurationId = materialId = undefined;
  await tab('.library-sections', 'templates');
  await page.eval(`document.querySelector('.library-list [data-template-id="${templateId}"]').click()`);
  await page.waitFor(`document.querySelector('.template-name')?.textContent === ${JSON.stringify(name)}`, '组件删除后重开模板');
  await tab('.template-tabs', 'effects');
  await page.waitFor(`document.querySelector('.effects-tab')?.textContent.includes('来自效果组件（已删除）')`, '已删除来源提示');
  await page.waitFor(`document.querySelector('.template-sample [data-status=ready] img')?.dataset.sha === ${JSON.stringify(sha)}`, '删除组件后模板帧与删除前相同');
  passed('删除来源组件后模板照常出相同试样，并标出组件已删除');
  await remove('模板操作', '删除');
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
