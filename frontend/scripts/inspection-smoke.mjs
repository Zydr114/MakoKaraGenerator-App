// SDK v2 调试叠加层与成本视图的真实 Chrome 验收（检查投影设计 §7）。运行者先启动匹配 Server/Worker 与前端，
// 本工具只启动自己的独立 Chrome：经 UI 导入一个 SDK2 样例、打开作者草稿试样（GPU），开关叠加层与成本，
// 核对开启检查前后同一时刻的 PNG 相同、投影与帧身份一致、关闭后不再带 inspect，并截图。CDP Network 只读观察。
// node scripts/inspection-smoke.mjs --output <全新目录> [--url http://localhost:5173] [--sample SpriteTrail] [--headed]
import { createHash } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { launchChrome } from './cdp.mjs';
import { resolveBackendRepo } from './backend-launcher.mjs';
import { zipFiles } from './effect-packages.mjs';
import { shell, ui } from './ui-query.mjs';
import { UiNetworkEvidence } from './ui-network-evidence.mjs';

const { values } = parseArgs({ options: {
  url: { type: 'string', default: 'http://localhost:5173' }, output: { type: 'string' }, headed: { type: 'boolean' },
  sample: { type: 'string', default: 'SpriteTrail' },
} });
if (!values.output) throw new Error('请用 --output 指定全新证据目录。');
const origin = new URL(values.url);
if (!['127.0.0.1', 'localhost', '[::1]'].includes(origin.hostname) || origin.username || origin.password || origin.search) {
  throw new Error('--url 必须是没有凭证和查询参数的 loopback 前端地址。');
}
const output = resolve(values.output);
mkdirSync(output, { recursive: false, mode: 0o700 });
const sha = (value) => createHash('sha256').update(value).digest('hex');
const save = (name, value) => writeFileSync(join(output, name), JSON.stringify(value, null, 2) + '\n');

const root = join(resolveBackendRepo(), 'samples', 'ScriptEffectsV2', values.sample);
const manifestBytes = readFileSync(join(root, 'manifest.json'));
const manifest = JSON.parse(manifestBytes.toString('utf8'));
if (manifest.packageVersion !== 3 || !manifest.sdkVersion.startsWith('2.')) throw new Error('测试素材必须是 package3/SDK2。');
const files = manifest.files.map((entry) => ({ name: entry.path, data: readFileSync(join(root, entry.path)) }));
const packagePath = join(mkdtempSync(join(tmpdir(), 'mako-inspection-')), `Inspection${values.sample}.mkfx`);
writeFileSync(packagePath, zipFiles([{ name: 'manifest.json', data: manifestBytes }, ...files]));

const checks = [];
const runtimeErrors = [];
let browser;
let page;
let network;
let previousDevice;
let failure;
const componentIds = `[...document.querySelectorAll('.component-list [data-configuration-id]')].map((el) => el.dataset.configurationId)`;
const readyImage = `document.querySelector('.template-sample .preview-frame[data-status=ready] img')`;

function check(condition, label) {
  checks.push({ passed: !!condition, label });
  if (!condition) throw new Error(label);
  console.log(`✓ ${label}`);
}

async function chooseDevice(label) {
  await ui.click(page, 'button', '（渲染设备）', { partial: true, scope: '.app-bar' });
  await page.waitFor(`!!document.querySelector('.render-device-menu')`, '设备菜单');
  await ui.click(page, 'menuitem', label, { partial: true, scope: '.render-device-menu' });
  await page.waitFor(`!document.querySelector('.render-device-menu')`, '设备菜单关闭');
}

async function shot(name) {
  writeFileSync(join(output, `${name}.png`), await page.screenshot());
}

try {
  browser = await launchChrome({ headed: values.headed });
  page = await browser.openPage(undefined, { width: 1440, height: 1000 });
  network = new UiNetworkEvidence(page);
  browser.cdp.on((message) => {
    if (message.sessionId === page.sessionId && message.method === 'Runtime.exceptionThrown') {
      runtimeErrors.push(message.params.exceptionDetails.exception?.description ?? message.params.exceptionDetails.text);
    }
  });
  await network.enable();
  await page.send('DOM.enable');
  await page.navigate(origin.origin + '/');
  await page.waitFor(shell.connected, '连接 Server');
  previousDevice = await page.eval(`localStorage.getItem('mako.renderBackend')`);
  // 从默认状态开始：两个开关都关。
  await page.eval(`localStorage.removeItem('mako.sampleInspection')`);
  await chooseDevice('GPU');

  await shell.goto(page, 'library');
  await ui.click(page, 'button', '效果组件', { scope: '.library-sections' });
  await page.waitFor(`!!document.querySelector('.component-list')`, '组件列表');
  const priorIds = await page.eval(componentIds);
  await ui.setFile(page, '导入效果包…', packagePath);
  await ui.waitDialog(page, '导入效果包');
  await ui.clickInDialog(page, '导入');
  await ui.waitDialogClosed(page, '导入效果包');
  await page.waitFor(`${componentIds}.some((id) => !${JSON.stringify(priorIds)}.includes(id))`, '本次导入的组件');
  const configurationId = await page.eval(`${componentIds}.find((id) => !${JSON.stringify(priorIds)}.includes(id))`);
  await ui.click(page, `[role=row]:has([data-configuration-id="${configurationId}"])`, null);
  await ui.click(page, 'button', '编辑', { scope: '.component-detail' });
  await page.waitFor(`!!document.querySelector('.draft-detail')`, 'SDK2 草稿');

  // 1. 默认关闭：帧请求不带 inspect，没有叠加层与成本面板。
  await page.waitFor(`!!${readyImage}?.complete && ${readyImage}.naturalWidth > 0`, '作者草稿试样首帧', 180_000);
  const plainSha = await page.eval(`${readyImage}.dataset.sha`);
  const plainTime = await page.eval(`${readyImage}.dataset.time`);
  const firstRequest = await network.wait((r) => r.method === 'POST' && r.path.endsWith('/frame-requests') && r.status === 202, '首帧请求');
  check(firstRequest.input.inspect === undefined, '默认关闭时帧请求不带 inspect');
  check(await page.eval(`!document.querySelector('.inspection-overlay') && !document.querySelector('.sample-cost')`), '默认没有叠加层与成本面板');
  await shot('01-default-off');

  // 2. 打开叠加层：按当前时刻重取一帧并带 inspect，读到投影后画出叠加层。
  const beforeOverlay = network.mark();
  await ui.setChecked(page, 'switch', '叠加层', true);
  const inspectRequest = await network.wait((r) => r.method === 'POST' && r.path.endsWith('/frame-requests') && r.status === 202
    && r.input?.inspect === true, '带 inspect 的帧请求', beforeOverlay);
  check(inspectRequest.input.timeUs === plainTime, '打开叠加层后按当前时刻重取');
  const inspection = await network.wait((r) => r.method === 'GET' && /^\/api\/v1\/preview-frames\/[^/]+\/inspection$/.test(r.path) && r.status === 200,
    '读取检查投影', beforeOverlay);
  await page.waitFor(`Number(document.querySelector('.inspection-overlay')?.dataset.shapes) > 0`, '叠加层画出图形', 60_000);
  const overlayFrame = await page.eval(`document.querySelector('.inspection-overlay').dataset.frame`);
  const shown = await page.eval(`({ sha: ${readyImage}.dataset.sha, time: ${readyImage}.dataset.time })`);
  const body = inspection.body;
  check(body && overlayFrame === body.frameId && body.timeUs === plainTime && body.renderBackend === 'gpu', '叠加层画的是同一帧的投影（GPU）');
  check(shown.sha === plainSha && shown.time === plainTime, '开启检查前后同一时刻的 PNG 相同');
  check(body.inspection.effects.some((e) => e.sdkMajor === 2 && e.packageId === manifest.packageId), '效果实例对得上本次导入的 SDK2 包');
  // 证据记录会把名字含 token 的字段脱敏，几何数量从页面图例读。
  const legend = await page.eval(`Object.fromEntries([...document.querySelectorAll('.inspection-legend .legend-chip')].map((el) => [el.dataset.layer, Number(el.querySelector('.legend-count')?.textContent)]))`);
  check(Object.keys(legend).length === 5, '图例列出五类');
  check(body.inspection.prepared && legend.clusters > 0 && legend.effects > 0, '投影带字簇几何与效果实例');
  check(body.inspection.cursors.every((c) => ['unavailable', 'fromLine', 'fromToken', 'fromRubySegment'].includes(c.progressSource)), '播放头进度来源是合同枚举');
  check(!(await page.eval(`document.querySelector('.inspection-overlay').textContent`)).includes('undefined'), '叠加层文字没有 undefined');
  await shot('02-overlay');

  // 3. 图例隐藏字簇：图形变少，偏好记在本机。
  const allShapes = Number(await page.eval(`document.querySelector('.inspection-overlay').dataset.shapes`));
  await ui.click(page, '.legend-chip[data-layer=clusters]', null);
  await page.waitFor(`Number(document.querySelector('.inspection-overlay')?.dataset.shapes) < ${allShapes}`, '隐藏字簇后图形变少');
  check(JSON.parse(await page.eval(`localStorage.getItem('mako.sampleInspection')`)).hidden.includes('clusters'), '隐藏的类别记在本机');
  await ui.click(page, '.legend-chip[data-layer=clusters]', null);
  await page.waitFor(`Number(document.querySelector('.inspection-overlay')?.dataset.shapes) === ${allShapes}`, '恢复字簇');

  // 4. 打开成本：不重取（当前帧已有投影），面板分开写 Worker 与 Host。
  const beforeCost = network.mark();
  await ui.setChecked(page, 'switch', '成本', true);
  await page.waitFor(`document.querySelector('.sample-cost')?.dataset.frame === ${JSON.stringify(body.frameId)}`, '成本面板显示同一帧');
  const cost = await page.eval(`({
    phases: document.querySelectorAll('.sample-cost .cost-phase').length,
    host: document.querySelector('.sample-cost .cost-host')?.textContent ?? '',
    scripts: document.querySelectorAll('.sample-cost .cost-scripts tbody tr').length,
    head: document.querySelector('.sample-cost .cost-head')?.textContent ?? '',
  })`);
  check(cost.phases === 8 && cost.host.includes('渲染往返') && cost.host.includes('PNG 编码'), '成本面板写出 Worker 八个阶段与 Host 往返、编码');
  check(cost.scripts === body.inspection.cost.scripts.length, '脚本表行数与投影一致');
  await new Promise((r) => setTimeout(r, 500));
  check(network.matching((r) => r.method === 'POST' && r.path.endsWith('/frame-requests'), beforeCost).length === 0, '当前帧已有投影时打开成本不重取');
  await page.eval(`document.querySelector('.sample-cost')?.scrollIntoView({ block: 'nearest' })`);
  await shot('03-overlay-cost');
  save('inspection.json', { frameId: body.frameId, timeUs: body.timeUs, renderBackend: body.renderBackend, prepared: body.inspection.prepared,
    counts: { lines: body.inspection.lines.length, cursors: body.inspection.cursors.length,
      particles: body.inspection.particles.length, effects: body.inspection.effects.length },
    cost: body.inspection.cost, host: body.host, legend, panel: cost,
    approxBytes: Buffer.byteLength(JSON.stringify(body)) });

  // 5. 两个都关：叠加层与面板消失，之后的帧请求不带 inspect（拖时刻到另一处触发新帧）。
  await ui.setChecked(page, 'switch', '叠加层', false);
  await ui.setChecked(page, 'switch', '成本', false);
  check(await page.eval(`!document.querySelector('.inspection-overlay') && !document.querySelector('.sample-cost') && !document.querySelector('.inspection-legend')`), '关闭后叠加层、图例与成本面板都不显示');
  const beforeSeek = network.mark();
  await ui.click(page, '.sample-play', null);
  const later = await network.wait((r) => r.method === 'POST' && r.path.endsWith('/frame-requests') && r.status === 202, '关闭后的试播帧请求', beforeSeek);
  await ui.click(page, '.sample-play', null);
  check(later.input.inspect === undefined, '关闭后帧请求不再带 inspect');
  await shot('04-off-again');

  check(network.credentialUrls === 0, '请求 URL 不含凭证');
  check(runtimeErrors.length === 0, '页面没有未捕获异常');
} catch (error) {
  failure = error;
  console.error(`✗ ${error.message}`);
  if (page) await shot('failure').catch(() => {});
} finally {
  if (page && previousDevice !== undefined) {
    await page.eval(previousDevice === null ? `localStorage.removeItem('mako.renderBackend')` : `localStorage.setItem('mako.renderBackend', ${JSON.stringify(previousDevice)})`).catch(() => {});
    await page.eval(`localStorage.removeItem('mako.sampleInspection')`).catch(() => {});
  }
  save('checks.json', { sample: `ScriptEffectsV2/${values.sample}`, archiveSha256: sha(readFileSync(packagePath)), checks, runtimeErrors,
    failure: failure ? String(failure.message) : null });
  await browser?.close();
}
if (failure) process.exit(1);
