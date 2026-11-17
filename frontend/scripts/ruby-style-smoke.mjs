// Ruby 设置的有限浏览器验收：使用隔离的 Server profile。
// 复制内置模板 → 编辑和保存两项 → 重选模板检查定义 → 新建含日文注音的 TTML 工程
// → 编排页选句，修改两项并检查静态结果和工程修订 → 恢复继承。
// 仅通过页面控件操作；不从页面模块对象读写工程。协议核验由独立 HTTP 验收负责。
// node scripts/ruby-style-smoke.mjs --url http://127.0.0.1:PORT [--screenshot /tmp/mako-ruby.png] [--headed]
// 待与相同源码构建的 Server/Worker 和 Web 启动后执行。
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { parseArgs } from 'node:util';
import { launchChrome } from './cdp.mjs';
import { shell, ui, uiJs } from './ui-query.mjs';

const { values } = parseArgs({ options: {
  url: { type: 'string', default: 'http://127.0.0.1:5173' },
  screenshot: { type: 'string', default: join(tmpdir(), 'mako-ruby.png') },
  headed: { type: 'boolean' },
} });
const assets = mkdtempSync(join(tmpdir(), 'mako-ruby-smoke-'));
const audio = join(assets, 'ruby-smoke.wav');
const lyrics = join(assets, 'ruby-smoke.ttml');
const samples = 22050 * 12;
const wav = Buffer.alloc(44 + samples * 2);
wav.write('RIFF', 0); wav.writeUInt32LE(36 + samples * 2, 4); wav.write('WAVEfmt ', 8);
wav.writeUInt32LE(16, 16); wav.writeUInt16LE(1, 20); wav.writeUInt16LE(1, 22);
wav.writeUInt32LE(22050, 24); wav.writeUInt32LE(44100, 28); wav.writeUInt16LE(2, 32);
wav.writeUInt16LE(16, 34); wav.write('data', 36); wav.writeUInt32LE(samples * 2, 40);
writeFileSync(audio, wav);
// 标准 TTML 注音容器，两个相邻的长注音，含字级时间。
writeFileSync(lyrics, `<?xml version="1.0" encoding="utf-8"?>
<tt xmlns="http://www.w3.org/ns/ttml" xmlns:tts="http://www.w3.org/ns/ttml#styling" xml:lang="ja">
  <body><div><p begin="0.5s" end="3s"><span begin="0s" end="1s"><span tts:ruby="container"><span tts:ruby="base">朝</span><span tts:ruby="textContainer"><span tts:ruby="text">あさひかがやき</span></span></span></span><span begin="1s" end="2.5s"><span tts:ruby="container"><span tts:ruby="base">光</span><span tts:ruby="textContainer"><span tts:ruby="text">ひかりきらめき</span></span></span></span></p><p begin="4s" end="6s"><span begin="0s" end="2s">風</span></p></div></body>
</tt>`);
const name = `注音样式冒烟-${Date.now().toString(36)}`;
const rev = `(document.querySelector('.status-bar')?.textContent.match(/修订 (\\d+)/) ?? [])[1]`;
const templateName = `document.querySelector('.template-name')?.textContent`;
const scope = '.line-inspector';
const readSelect = (label) => uiJs(`const el = __ui.find('select', ${JSON.stringify(label)}, false, ${JSON.stringify(scope)}); return el?.querySelector('.select-value')?.textContent.trim();`);
const readInput = (label) => uiJs(`return __ui.find('textbox', ${JSON.stringify(label)}, false, ${JSON.stringify(scope)})?.value;`);
function check(condition, message) {
  if (!condition) throw new Error(message);
  console.log(`✓ ${message}`);
}
async function screenshot(page, suffix) {
  const path = values.screenshot.replace(/\.png$/i, '') + `-${suffix}.png`;
  await writeFile(path, await page.screenshot());
  console.log(`截图：${path}`);
}
async function includeGroup(page, group) {
  const selector = `[data-group="${group}"] [role="switch"]`;
  await page.waitFor(`!!document.querySelector(${JSON.stringify(selector)})`, `${group} 属性开关`);
  if (!(await page.eval(`document.querySelector(${JSON.stringify(selector)}).checked`))) await ui.click(page, selector, null);
  await page.waitFor(`document.querySelector(${JSON.stringify(selector)})?.checked`, `${group} 属性已包含`);
}
async function readDefinition(page) {
  await ui.click(page, 'tab', '高级');
  await page.waitFor(`!!document.querySelector('.template-json')`, '完整模板定义');
  return JSON.parse(await page.eval(`document.querySelector('.template-json').textContent`));
}
async function change(page, label, option, expectedText) {
  const before = Number(await page.eval(rev));
  check(Number.isFinite(before), '读取工程修订');
  await ui.choose(page, label, option, { scope });
  await page.waitFor(`Number(${rev}) > ${before} && ${readSelect(label)} === ${JSON.stringify(option)} && !document.querySelector('.status-facts')?.textContent.includes('同步中')`, `${label} 已更新`, 30_000);
  if (expectedText) await page.waitFor(`(document.querySelector(${JSON.stringify(scope)})?.innerText ?? '').includes(${JSON.stringify(expectedText)})`, `${label} 静态结果`, 30_000);
}
async function fill(page, label, number, expectedText) {
  const before = Number(await page.eval(rev));
  await ui.fill(page, label, number, { scope, enter: true });
  await page.waitFor(`Number(${rev}) > ${before} && ${readInput(label)} === ${JSON.stringify(number)} && !document.querySelector('.status-facts')?.textContent.includes('同步中')`, `${label} 输入已提交`, 30_000);
  await page.waitFor(`(document.querySelector(${JSON.stringify(scope)})?.innerText ?? '').includes(${JSON.stringify(expectedText)})`, `${label} 静态结果`, 30_000);
}
async function clickLine(page) {
  if (await ui.exists(page, 'button', '关闭提示')) await ui.click(page, 'button', '关闭提示');
  await ui.click(page, 'button', '全曲');
  const point = await page.eval(`(() => { const r = document.querySelector('.orchestration .timeline-canvas canvas').getBoundingClientRect(); return { x: r.x + r.width * 1.5 / 12, y: r.y + 22 + 72 + 34 + 11 }; })()`);
  for (const type of ['mouseMoved', 'mousePressed', 'mouseReleased']) await page.send('Input.dispatchMouseEvent', { type, ...point, button: type === 'mouseMoved' ? 'none' : 'left', buttons: type === 'mousePressed' ? 1 : 0, clickCount: 1 });
  await page.waitFor(`document.querySelector('.line-inspector .line-text')?.textContent.includes('朝光')`, '选中第一句', 30_000);
}

let browser, page, createdId;
const errors = [];
try {
  browser = await launchChrome({ headed: values.headed });
  browser.cdp.on((message) => { if (message.method === 'Runtime.exceptionThrown') errors.push(message.params.exceptionDetails.exception?.description ?? 'exception'); });
  page = await browser.openPage(new URL(values.url).origin, { width: 1440, height: 900 });
  await page.send('DOM.enable');
  await page.waitFor(shell.connected, '连接 Server');
  await shell.newProject(page, { audio, lyrics, name });
  await page.waitFor(`document.querySelectorAll('.line-list .ll-text').length === 2`, '导入两行歌词', 120_000);
  await page.waitFor(`(document.querySelector('.tokens')?.textContent ?? '').includes('あさひかがやき')`, '第一句含日文注音', 30_000);
  await shell.goto(page, 'library');
  await page.waitFor(`!!document.querySelector('.library-list [data-template-id="builtin:line-classic"]')`, '内置模板');
  await ui.click(page, '[role=row]:has([data-template-id="builtin:line-classic"])', null);
  await page.waitFor(`${templateName} === '经典整行'`, '选择经典整行');
  await ui.click(page, 'button', '复制并编辑');
  await ui.waitDialog(page, '复制到我的模板');
  await ui.fill(page, '名称', name, { scope: '@dialog' });
  await ui.clickInDialog(page, '复制');
  await page.waitFor(`${templateName} === ${JSON.stringify(name)}`, '复制模板', 30_000);
  createdId = await page.eval(`[...document.querySelectorAll('.library-list [data-template-id]')].find(x => x.textContent.includes(${JSON.stringify(name)}))?.dataset.templateId`);
  check(!!createdId, '复制内置模板');
  await includeGroup(page, 'rubyScale');
  await includeGroup(page, 'rubyGap');
  await ui.choose(page, '注音字号比例模式', '自定义', { scope: '[data-group="rubyScale"]' });
  await ui.fill(page, '注音字号比例', '70', { scope: '[data-group="rubyScale"]', enter: true });
  await ui.choose(page, '注音间距模式', '自定义', { scope: '[data-group="rubyGap"]' });
  await ui.fill(page, '注音与正文间距', '5', { scope: '[data-group="rubyGap"]', enter: true });
  await screenshot(page, 'template');
  await ui.click(page, 'button', '保存', { scope: '.template-actions' });
  await page.waitFor(`![...document.querySelectorAll('.template-detail .phead .tag')].some(x => x.textContent === '未保存')`, '模板保存', 30_000);
  await ui.click(page, '[role=row]:has([data-template-id="builtin:line-classic"])', null);
  await ui.click(page, `[role=row]:has([data-template-id="${createdId}"])`, null);
  await page.waitFor(`${templateName} === ${JSON.stringify(name)}`, '重选模板');
  const definition = await readDefinition(page);
  check(definition.properties.includes('ruby.relativeScale') && definition.properties.includes('ruby.gapPx') && definition.style.ruby.relativeScale === 0.7 && definition.style.ruby.gapPx === 5, '模板保存后重选，两项数值和包含状态保持');
  await shell.goto(page, 'orchestration');
  await page.waitFor(`!!document.querySelector('.orchestration .timeline-canvas canvas')`, '编排时间轴', 60_000);
  await clickLine(page);
  await page.waitFor(`${readSelect('注音字号比例')} === '继承上级' && ${readSelect('注音与正文间距')} === '继承上级'`, '本句注音继承状态', 30_000);
  await change(page, '注音字号比例', '自定义');
  await fill(page, '注音字号比例', '75', '当前静态结果：75%');
  await change(page, '注音与正文间距', '自定义');
  await fill(page, '注音与正文间距', '8', '当前静态结果：8 px');
  check(true, '本句两项自定义生效并增加工程修订');
  await page.eval(`document.querySelector('.ruby-style-field')?.scrollIntoView({ block: 'center' })`);
  await screenshot(page, 'line');
  await change(page, '注音字号比例', '继承上级', '当前静态结果：跟随歌词中的注音设置');
  await change(page, '注音与正文间距', '继承上级', '当前静态结果：跟随歌词中的注音设置');
  check(errors.length === 0, `恢复继承，页面无未处理异常 ${errors.join(' | ')}`);
  await shell.goto(page, 'library');
  await page.waitFor(`!!document.querySelector('.library-list [data-template-id="${createdId}"]')`, '返回个人模板');
  await ui.click(page, `[role=row]:has([data-template-id="${createdId}"])`, null);
  await ui.menu(page, '模板操作', '删除…');
  await ui.clickInDialog(page, '删除');
  await page.waitFor(`!document.querySelector('.library-list [data-template-id="${createdId}"]')`, '删除临时模板');
  createdId = undefined;
} catch (error) {
  process.exitCode = 1;
  console.error(`✗ ${error.stack ?? error}`);
  if (page) {
    await screenshot(page, 'failure').catch(() => {});
    console.error((await page.eval(`document.body.innerText`).catch(() => '')).slice(0, 1600));
  }
  if (createdId) console.error(`隔离样式库可能保留临时模板 ${name} (${createdId})`);
} finally {
  if (browser) await browser.close();
  rmSync(assets, { recursive: true, force: true });
}
