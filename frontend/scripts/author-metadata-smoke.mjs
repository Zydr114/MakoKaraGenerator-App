// Metadata-adoption Chrome acceptance. All mutations use normal UI.
// Real metadata delivery may be delayed unchanged. One logical post-success GET
// delivery fault is explicitly simulated; no private stores or fake writes.
import { mkdirSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { launchChrome } from './cdp.mjs';
import { shell, ui } from './ui-query.mjs';
import { UiNetworkEvidence } from './ui-network-evidence.mjs';
import { readVectorFixture, sha } from './author-package-fixture.mjs';

import { BrowserFetchGates } from './author-metadata-fetch-gates.mjs';
import { makeRevisionPlan, COUNTER, HOLD, COUNTER_LABEL, HOLD_LABEL, ABOVE_JS_INTEGER, MAX_I64, MAX_U64, TARGET_EXPORT_LABEL } from './author-metadata-revision-plan.mjs';

const { values } = parseArgs({ options: {
  url: { type: 'string', default: 'http://127.0.0.1:5173' }, package: { type: 'string' }, output: { type: 'string' },
  headed: { type: 'boolean' }, 'binary-path': { type: 'string' }, 'inspect-fixture': { type: 'boolean' },
} });
if (!values.package) throw new Error('请用 --package 指定本轮 HTTP 生成的 SVG vector .mkfx。');
const packagePath = resolve(values.package);
const fixture = readVectorFixture(packagePath, values['binary-path']);
const { manifest, svg, descriptor, binaries } = fixture;
const entry = manifest.exports[0];
// The finite schema scenario requires the original HTTP fixture to have no parameters.
makeRevisionPlan(fixture, manifest);
const fixtureEvidence = { packageId: manifest.packageId, packageVersion: manifest.packageVersion, sdkVersion: manifest.sdkVersion,
  exportId: entry.id, archiveSha256: fixture.archiveSha256, manifestSha256: sha(fixture.manifestBytes),
  svgPath: svg.path, descriptorPath: descriptor.path, initialColor: fixture.initialColor, repairedColor: fixture.repairedColor,
  files: [...fixture.byPath.values()].map((file) => ({ path: file.path, role: file.role, sizeBytes: file.data.length, sha256: sha(file.data), editable: file.editable })) };
if (values['inspect-fixture']) { console.log(JSON.stringify({ status: 'fixture-inspected-only', ...fixtureEvidence }, null, 2)); process.exit(0); }
if (!values.output) throw new Error('请用 --output 指定全新证据目录。');
const origin = new URL(values.url);
if (!['http:', 'https:'].includes(origin.protocol) || !['127.0.0.1', 'localhost', '[::1]'].includes(origin.hostname)
  || origin.username || origin.password || origin.search || origin.hash || origin.pathname !== '/')
  throw new Error('--url 必须是没有路径、凭证和查询参数的 loopback 前端地址。');
const output = resolve(values.output);
mkdirSync(output, { recursive: false, mode: 0o700 });
const save = (name, value) => writeFileSync(join(output, name), JSON.stringify(value, null, 2) + '\n');
save('fixture.json', fixtureEvidence);

const checks = [], identities = [], displayed = [], canonicalManifests = [], runtimeErrors = [];
const expectedTexts = new Map([...fixture.byPath.values()].filter((file) => file.editable).map((file) => [file.path, file.data.toString('utf8')]));
let browser, page, network, configurationId, draftId, previousDevice, failure;
let stage = 'initialize';
const componentIds = `[...document.querySelectorAll('.component-list [data-configuration-id]')].map((el) => el.dataset.configurationId)`;
const inUse = `document.querySelector('[data-author-source-revision]')?.dataset.authorSourceRevision`;
const sourceShown = `document.querySelector('.draft-source')?.dataset.revision`;
const sampleImage = '.template-sample [data-status=ready] img';
const dirty = `!!document.querySelector('.draft-source[data-dirty]')`;
const arg = JSON.stringify;
function check(condition, label) {
  checks.push({ passed: !!condition, label });
  if (!condition) throw new Error(label);
  console.log(`✓ ${label}`);
}
async function keys(key, code, keyCode, modifiers = 2) {
  for (const type of ['keyDown', 'keyUp']) await page.send('Input.dispatchKeyEvent', { type, key, code, modifiers, windowsVirtualKeyCode: keyCode });
}
async function selectFile(path) {
  await ui.click(page, `.source-file[data-path=${arg(path)}]`, null);
  await page.waitFor(`document.querySelector('.source-file[aria-current=true]')?.dataset.path === ${arg(path)}`, '切换实际文件');
}
async function editorText() {
  await page.waitFor(`!!document.querySelector('.cm-content')`, 'CodeMirror 编辑器');
  // Observe the actual UI Copy event after CodeMirror fills its clipboard data.
  // This reads the entire document even when CodeMirror virtualizes its DOM.
  await page.eval(`(() => {
    document.querySelector('.cm-content').focus();
    globalThis.__svgAuthorUiCopy = null;
    document.addEventListener('copy', (event) => {
      globalThis.__svgAuthorUiCopy = { text: event.clipboardData?.getData('text/plain'), handled: event.defaultPrevented };
    }, { once: true });
  })()`);
  await keys('a', 'KeyA', 65);
  await keys('c', 'KeyC', 67);
  const copied = await page.waitFor('globalThis.__svgAuthorUiCopy', '真实编辑器复制事件', 10_000);
  await page.eval('delete globalThis.__svgAuthorUiCopy');
  check(copied.handled && typeof copied.text === 'string', '从编辑器正常复制通道读取完整文本');
  return copied.text;
}
async function replaceText(text, expectDirty = true) {
  await page.waitFor(`!!document.querySelector('.cm-content')`, '可编辑包文本');
  await page.eval(`document.querySelector('.cm-content').focus()`);
  await keys('a', 'KeyA', 65);
  await page.send('Input.insertText', { text });
  await page.waitFor(expectDirty ? dirty : `!(${dirty})`, '编辑器 dirty 状态');
  check(await editorText() === text, '实际编辑器完整文本匹配输入');
}
async function chooseDevice(value) {
  await ui.click(page, 'button', '（渲染设备）', { partial: true, scope: '.app-bar' });
  await page.waitFor(`!!document.querySelector('.render-device-menu')`, '设备菜单');
  await ui.click(page, 'menuitem', { default: '跟随后端默认', cpu: 'CPU', gpu: 'GPU' }[value],
    { partial: value === 'default', scope: '.render-device-menu' });
  await page.waitFor(`!document.querySelector('.render-device-menu')`, '设备菜单关闭');
}
async function recordDraft(label, expectedSource, expectedLatest, after = 0) {
  await page.waitFor(`${inUse} === ${arg(expectedSource)}`, label + ' 在用来源');
  const draft = (await network.wait((record) => record.status < 300 && record.body?.draftId === draftId
    && record.body?.configuration?.sourceRevision === expectedSource && record.body.currentSourceRevision === expectedLatest,
  label + ' 固定草稿身份', after)).body;
  identities.push({ label, draftId, draftRevision: draft.draftRevision, latestSourceRevision: draft.currentSourceRevision,
    inUseSourceRevision: draft.configuration.sourceRevision, adoptedSourceRevision: draft.adoptedSourceRevision,
    lastCompilation: draft.lastCompilation, parametersJson: draft.configuration.parametersJson });
  save('author-identities.json', identities);
  return draft;
}
function checkPackage(body, label, texts = expectedTexts) {
  check(body.draftId === draftId && body.packageId === manifest.packageId && body.packageRevision === body.sourceRevision,
    label + ' 完整包响应身份匹配');
  const canonical = JSON.parse(body.manifestJson);
  check(body.files.length === fixture.byPath.size && canonical.files.length === fixture.byPath.size,
    label + ' 返回完整清单与包文件集合');
  for (const initial of fixture.byPath.values()) {
    const file = body.files.find((item) => item.path === initial.path);
    const listed = canonical.files.find((item) => item.path === initial.path);
    const expectedHash = initial.editable ? sha(texts.get(initial.path)) : sha(initial.data);
    check(file?.editable === initial.editable && file.role === initial.role && file.sha256 === expectedHash
      && file.sizeBytes === (initial.editable ? Buffer.byteLength(texts.get(initial.path)) : initial.data.length)
      && listed?.sha256 === expectedHash && (initial.editable ? file.textSha256 === expectedHash : file.textSha256 === undefined),
    label + ' 文件角色、固定字节与派生 SHA 一致：' + initial.path);
  }
  return canonical;
}
async function checkCanonical(body, label) {
  const canonical = checkPackage(body, label);
  await selectFile('manifest.json');
  check(await editorText() === body.manifestJson, label + ' 服务规范化 manifest 已同步至真实编辑器');
  await selectFile(svg.path);
  check(await editorText() === expectedTexts.get(svg.path), label + ' 切回 SVG 保留规范文本');
  await selectFile('manifest.json');
  check(await editorText() === body.manifestJson, label + ' 再切回 manifest 未恢复旧 SHA');
  canonicalManifests.push({ label, sourceRevision: body.sourceRevision, manifestSha256: sha(body.manifestJson),
    files: canonical.files.map(({ path, role, sha256 }) => ({ path, role, sha256 })) });
  save('canonical-manifests.json', canonicalManifests);
}
async function savePackage(label, expectedRevision, expectedManifest) {
  const before = network.mark();
  await ui.click(page, 'button', '保存修订', { scope: '.srcbar' });
  const saved = await network.wait((record) => record.method === 'PUT' && record.path === `/api/v1/drafts/${draftId}/package-files`
    && record.status === 200, label + ' UI 完整包 PUT', before);
  check(saved.input.expectedDraftRevision === expectedRevision && saved.input.manifestJson === expectedManifest,
    label + ' 提交当前草稿 CAS 与界面 manifest 原文');
  check(saved.input.textFiles.length === expectedTexts.size && [...expectedTexts].every(([path, text]) =>
    saved.input.textFiles.some((file) => file.path === path && file.textSha256 === sha(text))),
  label + ' 完整提交全部可编辑文本并排除只读二进制');
  const revision = saved.body.currentSourceRevision;
  const canonical = await network.wait((record) => record.method === 'GET' && record.path === `/api/v1/drafts/${draftId}/package-files`
    && record.status === 200 && record.body?.sourceRevision === revision, label + ' 保存后的 canonical GET', before);
  await page.waitFor(`${sourceShown} === ${arg(revision)} && !(${dirty}) && document.querySelector('.source-save')?.disabled === true`,
    label + ' 编辑器新固定修订');
  await checkCanonical(canonical.body, label);
  return saved.body;
}
async function compileShown(label, source, succeeded) {
  const before = network.mark();
  await ui.click(page, 'button', '编译查看的修订', { scope: '.srcbar' });
  const accepted = await network.wait((record) => record.method === 'POST' && record.path === `/api/v1/drafts/${draftId}/compilation-jobs`
    && record.status === 202, label + ' 接受编译任务', before);
  const fixed = accepted.body.authorCompilationInput;
  check(accepted.input.sourceRevision === source && fixed.sourceRevision === source && fixed.draftId === draftId
    && fixed.draftRevision === accepted.input.expectedDraftRevision, label + ' 编译任务固定源码和草稿修订');
  const result = (await network.wait((record) => record.path === `/api/v1/jobs/${accepted.body.jobId}/compilation-result`
    && record.status === 200, label + ' 实际编译报告', before)).body;
  check(result.sourceRevision === source && result.draftRevision === fixed.draftRevision && result.succeeded === succeeded,
    label + ' 报告对应接受时身份与结果');
  const terminal = await network.wait((record) => record.path === `/api/v1/jobs/${accepted.body.jobId}`
    && record.body?.state === 'succeeded', label + ' 任务完成', before);
  check(terminal.body.authorCompilationInput.sourceRevision === source, label + ' succeeded 任务终态仍保持源码身份');
  await network.wait((record) => record.method === 'GET' && record.status === 200 && record.path === `/api/v1/drafts/${draftId}`
    && record.body?.lastCompilation?.sourceRevision === source && record.body.lastCompilation.succeeded === succeeded,
  label + ' 编译摘要刷新', before);
  await page.waitFor(`document.querySelector('.source-compile')?.dataset.outcome === ${arg(succeeded ? 'passed' : 'failed')}
    && !document.querySelector('.source-compile-button')?.disabled && !document.querySelector('.source-compile[data-stale]')`, label + ' 界面编译结果');
  return result;
}
async function displayedFrame(label, source, draftRevision, expectedColor) {
  const response = await network.wait((record) => record.method === 'GET' && record.status === 200
    && record.body?.session?.source?.kind === 'authorDraft' && record.body.session.source.sourceId === draftId
    && record.body.session.source.authorSourceRevision === source && record.body.session.source.sourceRevision === draftRevision
    && record.body.session.latestFrame, label + ' 精确预览输入');
  const session = response.body.session, frame = session.latestFrame;
  const color = expectedColor.slice(1).match(/../g).map((part) => parseInt(part, 16));
  const image = await page.waitFor(`(async () => {
    const img = document.querySelector(${arg(sampleImage)});
    if (!img || !img.complete || !img.naturalWidth || img.dataset.sha !== ${arg(frame.content.sha256)}
      || img.dataset.time !== ${arg(String(frame.timeUs))}) return null;
    img.scrollIntoView({ block: 'center', inline: 'nearest' });
    const src = img.currentSrc;
    await img.decode();
    // Read only the blob URL already displayed by the actual UI, never an API URL.
    const bytes = await (await fetch(src)).arrayBuffer();
    const digest = async (data) => [...new Uint8Array(await crypto.subtle.digest('SHA-256', data))].map((n) => n.toString(16).padStart(2, '0')).join('');
    const canvas = document.createElement('canvas'); canvas.width = img.naturalWidth; canvas.height = img.naturalHeight;
    const ctx = canvas.getContext('2d'); ctx.drawImage(img, 0, 0);
    const rgba = ctx.getImageData(0, 0, canvas.width, canvas.height).data;
    const target = ${arg(color)}; let colorPixels = 0, alphaMin = 255, alphaMax = 0;
    for (let p = 0; p < rgba.length; p += 4) {
      alphaMin = Math.min(alphaMin, rgba[p + 3]); alphaMax = Math.max(alphaMax, rgba[p + 3]);
      if (target.every((value, c) => Math.abs(value - rgba[p + c]) <= 2) && rgba[p + 3] === 255) colorPixels++;
    }
    if (!img.isConnected || document.querySelector(${arg(sampleImage)}) !== img || img.currentSrc !== src
      || img.dataset.sha !== ${arg(frame.content.sha256)} || img.dataset.time !== ${arg(String(frame.timeUs))}) return null;
    const r = img.getBoundingClientRect(), x = r.left + r.width / 2, y = r.top + r.height / 2;
    const visible = r.width > 0 && r.height > 0 && x >= 0 && x < innerWidth && y >= 0 && y < innerHeight && document.elementFromPoint(x, y) === img;
    if (!visible) return null;
    return { sha256: img.dataset.sha, timeUs: img.dataset.time, blobSha256: await digest(bytes), rgbaSha256: await digest(rgba),
      width: canvas.width, height: canvas.height, objectUrl: src.startsWith('blob:'), connected: img.isConnected, visible,
      expectedColor: ${arg(expectedColor)}, colorPixels, alphaMin, alphaMax };
  })()`, label + ' 实际可见并解码的图片', 120_000);
  const download = await network.wait((record) => record.status === 200 && record.download?.sha256 === image.sha256
    && record.path === frame.content.contentUrl, label + ' 原始 PNG 字节');
  check(session.renderBackend === 'gpu' && frame.renderBackend === 'gpu' && session.alphaMode === 'opaque'
    && frame.sourceRevision === session.source.sourceRevision && frame.previewGeneration === session.previewGeneration
    && download.download.sizeBytes === frame.content.sizeBytes && download.download.pngSignatureValid
    && download.download.width === frame.width && download.download.height === frame.height
    && image.blobSha256 === frame.content.sha256 && image.width === frame.width && image.height === frame.height
    && image.objectUrl && image.connected && image.visible && image.alphaMin === 255 && image.colorPixels >= 100,
  label + ' 显示的 opaque PNG 匹配 GPU 来源、代次、SHA 与实际 SVG 颜色');
  displayed.push({ label, source: session.source, previewId: session.previewId, previewGeneration: session.previewGeneration,
    frameId: frame.frameId, requestSeq: frame.requestSeq, png: download.download, image, renderBackend: session.renderBackend, alphaMode: session.alphaMode });
  save('displayed-frames.json', displayed);
  await writeFile(join(output, label + '.png'), await page.screenshot());
  return displayed.at(-1);
}

const dialogScope = '.draft-adoption-dialog';
const normalScope = '.draft-detail .pbody[data-tab=config] .draft-config';
const originalName = '原始绘图配置';
const migratedName = '迁移绘图配置';
const metadataRequests = [];
const draftPath = (suffix) => `/api/v1/drafts/${draftId}${suffix}`;
let gates, faultEvents = [], savedVisualText, sourceDocKey;
const CONFIG_FIELDS = ['name', 'exportId', 'parametersJson', 'relativeTarget', 'readScope', 'branchInput', 'fixedSeed', 'overlayLayer'];
function editable(config) {
  return Object.fromEntries(CONFIG_FIELDS.map((key) => [key, config[key] ?? null]));
}
function fullConfig(config) {
  return { ...editable(config), packageId: config.packageId, packageRevision: config.packageRevision,
    sourceRevision: config.sourceRevision, kind: config.kind, bindingKinds: [...config.bindingKinds] };
}
function fullConfigEqual(left, right) { return JSON.stringify(fullConfig(left)) === JSON.stringify(fullConfig(right)); }
async function waitNoAuthorMutation(after, label, posts = 0) {
  // Deliberately exceed ordinary editor's 300ms merge and 500ms preview cadence.
  await new Promise((resolve) => setTimeout(resolve, 800));
  const mutations = network.matching((r) => r.path.startsWith(draftPath('')) && ['PUT', 'POST', 'DELETE'].includes(r.method), after);
  check(mutations.length === posts && mutations.every((r) => r.method === 'POST' && r.path === draftPath('/adopt-source-revision')), label);
}
async function metadataFor(source, revision, after = 0) {
  const record = await network.wait((r) => r.method === 'GET' && r.path === draftPath('/source-inspection') && r.status === 200
    && r.body.sourceRevision === source && r.body.draftRevision === revision, '精确作者 schema', after);
  check(record.body.draftId === draftId && record.body.packageId === manifest.packageId && record.body.packageRevision === source
    && !['reference', 'manifest', 'manifestJson'].some((key) => key in record.body), '真实 inspection 响应是窄身份投影');
  if (record.body.parameters.some((p) => p.id === COUNTER)) {
    const counter = record.body.parameters.find((p) => p.id === COUNTER);
    check(counter.defaultValueJson === ABOVE_JS_INTEGER && counter.maximumJson === MAX_I64,
      '后端 metadata 的 Int64 默认值/上界按原文到达');
  }
  return record.body;
}
async function waitConfiguration(expected, source, after = 0) {
  const record = await network.wait((r) => r.method === 'PUT' && r.path === draftPath('/configuration') && r.status === 200
    && r.body.configuration.sourceRevision === source && Object.entries(expected).every(([key, value]) => r.body.configuration[key] === value),
  '普通配置完整提交', after);
  await page.waitFor(`!document.querySelector(${arg(normalScope)})?.hasAttribute('data-pending')`, '普通配置提交完毕');
  return record.body;
}
async function latestDraft(source) {
  const record = await network.wait((r) => r.status < 300 && r.body?.draftId === draftId && r.body.configuration?.sourceRevision === source,
    '最新完整草稿');
  return record.body;
}
async function waitNormalSource(source) {
  await page.waitFor(`${inUse} === ${arg(source)}`, '在用源码来源');
}
async function openCandidate(source, replace = false) {
  await ui.click(page, `.source-revision[data-revision=${arg(source)}] .source-revision-adopt`, null);
  if (replace) {
    await ui.waitDialog(page, '准备另一个修订？');
    await ui.clickInDialog(page, '准备新候选');
  }
  await page.waitFor(`document.querySelector(${arg(dialogScope + ' [data-source-revision]')})?.dataset.sourceRevision === ${arg(source)}`, '目标采用候选');
}
async function confirmExportIfNeeded() {
  if (await ui.exists(page, 'button', '换导出', { scope: '@dialog' })) await ui.clickInDialog(page, '换导出');
}
async function setRawParameters(text) {
  const details = dialogScope + ' .draft-parameter-json';
  if (!(await page.eval(`document.querySelector(${arg(details)})?.open`))) await ui.click(page, details + ' > summary', null);
  await ui.fill(page, '完整参数 JSON', text, { scope: dialogScope });
}
async function submitCandidate(label, source, previousSource, target) {
  const before = await latestDraft(previousSource), mark = network.mark();
  await ui.click(page, 'button', '采用源码与配置', { scope: dialogScope });
  const adopted = await network.wait((r) => r.method === 'POST' && r.path === draftPath('/adopt-source-revision') && r.status === 200, label, mark);
  check(adopted.input.expectedDraftRevision === before.draftRevision && adopted.input.sourceRevision === source
    && adopted.body.previousSourceRevision === previousSource && adopted.body.adoptedSourceRevision === source
    && Object.keys(adopted.input.configuration).sort().join('|') === [...CONFIG_FIELDS].sort().join('|')
    && JSON.stringify(editable(adopted.input.configuration)) === JSON.stringify(editable(target)), label + ' 单 POST 使用 exact CAS 与完整八字段');
  const fresh = await recordDraft(label, source, before.currentSourceRevision, mark);
  check(JSON.stringify(editable(fresh.configuration)) === JSON.stringify(editable(target)) && fresh.draftRevision === adopted.body.draftRevision,
    label + ' 回执与精确 GET 同意完整配置');
  await page.waitFor(`!document.querySelector(${arg(dialogScope)})`, '确认采用后关闭候选');
  await metadataFor(source, fresh.draftRevision, mark);
  check(network.matching((r) => r.method === 'PUT' && r.path === draftPath('/configuration'), mark).length === 0,
    label + ' 没有普通配置队列补写');
  return fresh;
}
async function history(button, label, source, expected) {
  const before = network.draft(draftId), mark = network.mark();
  await ui.click(page, 'button', button, { scope: '.srcbar' });
  const response = await network.wait((r) => r.method === 'POST' && r.path === draftPath('/adopt-source-revision') && r.status === 200, label, mark);
  check(response.input.expectedDraftRevision === before.draftRevision && response.input.sourceRevision === source
    && JSON.stringify(editable(response.input.configuration)) === JSON.stringify(editable(expected)), label + ' 使用执行时 CAS 和完整快照');
  const fresh = await recordDraft(label, source, before.currentSourceRevision, mark);
  check(fullConfigEqual(fresh.configuration, expected), label + ' 恢复全部配置、类别、绑定和包/source 身份');
  await metadataFor(source, fresh.draftRevision, mark);
  return fresh;
}
async function assertDirtyBuffer(text, label) {
  await selectFile(svg.path);
  check(await page.eval(dirty) && await editorText() === text
    && await page.eval(`document.querySelector('.source-editor')?.dataset.doc`) === sourceDocKey, label + ' 原文与编辑器身份保留');
  await keys('z', 'KeyZ', 90);
  check(await editorText() === savedVisualText, label + ' 原本的真实 Ctrl+Z 历史仍可撤回到已保存文本');
  await keys('Z', 'KeyZ', 90, 10); // CDP: Control=2, Shift=8.
  check(await editorText() === text && await page.eval(dirty), label + ' 真实 Ctrl+Shift+Z 恢复 dirty 文本');
}

try {
  browser = await launchChrome({ headed: values.headed });
  page = await browser.openPage(undefined, { width: 1500, height: 1100 });
  network = new UiNetworkEvidence(page); gates = new BrowserFetchGates(page);
  browser.cdp.on((message) => {
    if (message.sessionId !== page.sessionId) return;
    if (message.method === 'Runtime.exceptionThrown') runtimeErrors.push(message.params.exceptionDetails.exception?.description ?? message.params.exceptionDetails.text);
    if (message.method === 'Network.requestWillBeSent') {
      const request = message.params.request, url = new URL(request.url);
      if (url.pathname.endsWith('/source-inspection')) metadataRequests.push({ method: request.method, path: url.pathname,
        query: [...url.searchParams], hasBody: !!request.postData });
    }
  });
  await network.enable(); await page.send('DOM.enable'); await page.navigate(origin.origin + '/');
  await page.waitFor(shell.connected, '连接匹配 Server'); await gates.install();
  previousDevice = await page.eval(`localStorage.getItem('mako.renderBackend') ?? 'default'`);
  check(['default', 'cpu', 'gpu'].includes(previousDevice), '独立 profile 设备值有效');
  await chooseDevice('gpu'); await shell.goto(page, 'library');
  await ui.click(page, 'button', '效果组件', { scope: '.library-sections' });
  await page.waitFor(`!!document.querySelector('.component-list')`, '组件列表');
  const priorIds = await page.eval(componentIds);
  stage = 'ui-import';
  await ui.setFile(page, '导入效果包…', packagePath); await ui.waitDialog(page, '导入效果包');
  await page.waitFor(`!!document.querySelector('[role=dialog] .import-choices')`, '真实包预检');
  await ui.clickInDialog(page, '导入'); await ui.waitDialogClosed(page, '导入效果包');
  await page.waitFor(`${componentIds}.some((id) => !${arg(priorIds)}.includes(id))`, '本次组件');
  configurationId = await page.eval(`${componentIds}.find((id) => !${arg(priorIds)}.includes(id))`);
  await ui.click(page, `[role=row]:has([data-configuration-id=${arg(configurationId)}])`, null);
  await ui.click(page, 'button', '编辑', { scope: '.component-detail' });
  await page.waitFor(`!!document.querySelector('.draft-config') && !!document.querySelector('[data-author-source-revision]')`, '作者草稿');
  draftId = await page.eval(`document.querySelector('.draft-detail').dataset.draftId`);
  const created = (await network.wait((r) => r.method === 'POST' && r.path === '/api/v1/drafts' && r.status === 201 && r.body?.draftId === draftId, '真实创建草稿')).body;
  const sourceA = created.currentSourceRevision;
  await metadataFor(sourceA, created.draftRevision);
  check(created.configuration.sourceRevision === sourceA && created.configuration.exportId === entry.id, '真实 HTTP fixture 通过 UI 固定为 A');
  const initial = await displayedFrame('initial-A', sourceA, created.draftRevision, fixture.initialColor);

  stage = 'ordinary-config-unblur';
  const beforeRaw = network.mark(), rawSeed = '1844674407370955161x';
  await ui.fill(page, '随机种子', rawSeed, { scope: normalScope });
  check(await ui.value(page, '随机种子', { scope: normalScope }) === rawSeed
    && await ui.read(page, 'textbox', '随机种子', 'return document.activeElement === el;', { scope: normalScope }), '普通未 blur 原文可见且焦点仍在输入框');
  await ui.click(page, 'tab', '源码', { scope: '.draft-tabs' });
  await ui.click(page, 'tab', '配置', { scope: '.draft-tabs' });
  check(await ui.value(page, '随机种子', { scope: normalScope }) === rawSeed, '普通非法原文跨页签保留');
  await waitNoAuthorMutation(beforeRaw, '普通未 blur 非法原文没有 PUT 或 adopt');
  await ui.fill(page, '随机种子', '', { scope: normalScope });
  await ui.fill(page, '名称', originalName, { scope: normalScope, enter: true });
  await ui.choose(page, '读取', '整个工程', { scope: normalScope });
  await ui.fill(page, '随机种子', '17', { scope: normalScope, enter: true });
  await ui.choose(page, '叠放层', '文字后面', { scope: normalScope });
  const configuredA = await waitConfiguration({ name: originalName, readScope: 'project', fixedSeed: '17', overlayLayer: 'behindText' }, sourceA, beforeRaw);
  await metadataFor(sourceA, configuredA.draftRevision);
  const fullA = structuredClone(configuredA.configuration);
  await ui.click(page, 'tab', '源码', { scope: '.draft-tabs' });
  await page.waitFor(`${sourceShown} === ${arg(sourceA)} && !!document.querySelector('.cm-content')`, '完整 A 编辑器');
  const loadedA = (await network.wait((r) => r.method === 'GET' && r.path === draftPath('/package-files') && r.status === 200 && r.body.sourceRevision === sourceA, '完整包 A')).body;
  await compileShown('compile-A-for-undo', sourceA, true);
  const afterCompileA = await latestDraft(sourceA);
  await metadataFor(sourceA, afterCompileA.draftRevision);

  stage = 'save-new-parameter-export-schema';
  const plan = makeRevisionPlan(fixture, JSON.parse(loadedA.manifestJson));
  save('revision-plan.json', { exportId: plan.exportId, manifestSha256: sha(plan.manifestText), svgSha256: sha(plan.svgText),
    parameterIds: [COUNTER, HOLD], defaultInt64Text: ABOVE_JS_INTEGER, maximumInt64Text: MAX_I64, colorA: plan.colorA, colorB: plan.colorB });
  await selectFile(svg.path); await replaceText(plan.svgText); expectedTexts.set(svg.path, plan.svgText);
  await selectFile('manifest.json'); await replaceText(plan.manifestText);
  const savedB = await savePackage('schema-B', afterCompileA.draftRevision, plan.manifestText);
  const sourceB = savedB.currentSourceRevision;
  check(sourceB !== sourceA && savedB.configuration.sourceRevision === sourceA && savedB.configuration.exportId === entry.id,
    '新 export/参数/SVG 保存为 B，完整在用配置仍为 A');
  await compileShown('compile-B', sourceB, true);
  await waitNormalSource(sourceA);

  stage = 'hold-real-old-candidate-metadata';
  await gates.delayNextMetadata(sourceB);
  await openCandidate(sourceB);
  const held = await gates.waitHeld();
  check(held.responseSourceRevision === sourceB && held.status === 200, '只延迟真实 B metadata 的交付，正文未替换');
  await ui.click(page, 'button', '取消并保留输入', { scope: dialogScope });
  await page.waitFor(`!document.querySelector(${arg(dialogScope)})`, '关闭加载中的旧候选');
  // Build C through the same UI while B's response is held. C changes immutable
  // source identity but intentionally keeps B's target schema and visual color.
  const canonicalB = (await network.wait((r) => r.method === 'GET' && r.path === draftPath('/package-files') && r.status === 200 && r.body.sourceRevision === sourceB, 'B canonical')).body;
  // Keep Int64 literals exact by replacing only the JSON version text.
  const manifestCText = canonicalB.manifestJson.replace('"version":"2.0.1"', '"version":"2.0.2"')
    .replace('"version": "2.0.1"', '"version": "2.0.2"') + '\n';
  check(manifestCText.includes('2.0.2') && manifestCText.includes(ABOVE_JS_INTEGER), 'C 清单保留 Int64 原文字面量');
  const svgC = plan.svgText + '\n<!-- distinct retained C; same visible blue -->\n';
  await selectFile(svg.path); await replaceText(svgC); expectedTexts.set(svg.path, svgC);
  await selectFile('manifest.json'); await replaceText(manifestCText);
  const currentB = await latestDraft(sourceA);
  const savedC = await savePackage('schema-C', currentB.draftRevision, manifestCText);
  const sourceC = savedC.currentSourceRevision;
  check(![sourceA, sourceB].includes(sourceC), 'C 是真实第三个不可变修订');
  await compileShown('compile-C', sourceC, true);
  const compiledC = await latestDraft(sourceA); await metadataFor(sourceA, compiledC.draftRevision);
  const unchangedA = await displayedFrame('compiled-C-still-A', sourceA, compiledC.draftRevision, fixture.initialColor);
  check(unchangedA.image.expectedColor === initial.image.expectedColor, '保存编译候选仍显示 A 的实际像素');
  await selectFile(svg.path); await replaceText(svgC + '\n<!-- unsaved source buffer survives adoption -->\n');
  const dirtyText = await editorText();
  savedVisualText = svgC; sourceDocKey = await page.eval(`document.querySelector('.source-editor')?.dataset.doc`);
  const candidateMark = network.mark();
  await openCandidate(sourceC, true);
  await metadataFor(sourceC, compiledC.draftRevision);
  await ui.choose(page, '导出', TARGET_EXPORT_LABEL, { scope: dialogScope });
  await confirmExportIfNeeded();
  await ui.fill(page, '名称', migratedName, { scope: dialogScope, enter: true });
  await ui.fill(page, COUNTER_LABEL, ABOVE_JS_INTEGER, { scope: dialogScope, enter: true });
  await ui.fill(page, HOLD_LABEL + '（毫秒）', '123.456', { scope: dialogScope, enter: true });
  await ui.choose(page, '读取', '绑定的来源', { scope: dialogScope });
  await ui.choose(page, '叠放层', '文字上面', { scope: dialogScope });
  const targetJson = `{"${COUNTER}":${ABOVE_JS_INTEGER},"${HOLD}":123456}`;
  await setRawParameters(targetJson);
  await ui.fill(page, '随机种子', rawSeed, { scope: dialogScope });
  check(await ui.read(page, 'textbox', '随机种子', 'return document.activeElement === el;', { scope: dialogScope }), '候选原文验证时输入仍未 blur');
  check(await ui.read(page, 'button', '采用源码与配置', 'return el.disabled;', { scope: dialogScope }), '候选未 blur 原文阻止采用');
  await ui.click(page, 'button', '取消并保留输入', { scope: dialogScope });
  await openCandidate(sourceC);
  check(await ui.value(page, '随机种子', { scope: dialogScope }) === rawSeed && await ui.value(page, '名称', { scope: dialogScope }) === migratedName,
    '取消重开保留候选名称和非法原文');
  await gates.release(held.id);
  await page.waitFor(`globalThis.__makoMetadataSmoke.events.some((e) => e.kind === 'real-metadata-released-unchanged' && e.id === ${arg(held.id)})`, '旧 B 原响应已经交付');
  // Let the released fetch body and model continuations settle before asserting
  // that the newer candidate survived. Checking only the gate event is too early.
  await waitNoAuthorMutation(candidateMark, '候选编辑、取消和迟到 metadata 没有普通 PUT/adopt');
  check(await page.eval(`document.querySelector(${arg(dialogScope + ' [data-source-revision]')})?.dataset.sourceRevision`) === sourceC
    && await ui.value(page, '随机种子', { scope: dialogScope }) === rawSeed
    && await ui.value(page, COUNTER_LABEL, { scope: dialogScope }) === ABOVE_JS_INTEGER,
    '迟到 B metadata 不能覆盖 C 的 schema、Int64 值和未提交原文');
  await ui.fill(page, '随机种子', MAX_U64, { scope: dialogScope, enter: true });
  const target = { name: migratedName, exportId: plan.exportId, parametersJson: targetJson, relativeTarget: 'application',
    readScope: 'boundSources', branchInput: null, fixedSeed: MAX_U64, overlayLayer: 'aboveText' };
  check(await ui.value(page, COUNTER_LABEL, { scope: dialogScope }) === ABOVE_JS_INTEGER, '目标 schema 的 Int64 控件显示原文');
  await writeFile(join(output, 'candidate-C.png'), await page.screenshot());
  await ui.click(page, 'button', '取消并保留输入', { scope: dialogScope });
  await ui.click(page, 'tab', '配置', { scope: '.draft-tabs' });
  check(!(await ui.exists(page, 'textbox', COUNTER_LABEL, { scope: normalScope }))
    && await page.eval(inUse) === sourceA, '候选配置预览不替换普通 A schema 或在用来源');
  await displayedFrame('candidate-C-still-A', sourceA, compiledC.draftRevision, fixture.initialColor);
  await ui.click(page, 'tab', '源码', { scope: '.draft-tabs' }); await openCandidate(sourceC);

  stage = 'real-server-rejection-retains-candidate';
  const rejectedName = '长'.repeat(257);
  await ui.fill(page, '名称', rejectedName, { scope: dialogScope, enter: true });
  const rejectMark = network.mark();
  await ui.click(page, 'button', '采用源码与配置', { scope: dialogScope });
  const rejected = await network.wait((r) => r.method === 'POST' && r.path === draftPath('/adopt-source-revision')
    && r.status === 400, '服务真实拒绝超长配置名称', rejectMark);
  check(rejected.body?.field === 'configuration.name' && rejected.body.failureCode === 'AuthorConfigurationInvalid', '真实字段拒绝来自后端而非 mock');
  await page.waitFor(`!!document.querySelector(${arg(dialogScope + ' [data-field=name]')})`, '候选字段错误');
  const rejectedFields = await page.eval(`[...document.querySelectorAll(${arg(dialogScope + ' .draft-config-problem')})].map((el) => ({ field: el.dataset.field, text: el.textContent }))`);
  check(rejectedFields.some((issue) => issue.field === 'name' && issue.text?.trim()), '真实后端拒绝显示在名称字段问题中');
  check(await ui.value(page, '名称', { scope: dialogScope }) === rejectedName
    && await ui.value(page, COUNTER_LABEL, { scope: dialogScope }) === ABOVE_JS_INTEGER
    && await page.eval(inUse) === sourceA, '后端拒绝保留名称、Int64 参数和 A 来源');
  await ui.click(page, 'button', '取消并保留输入', { scope: dialogScope });
  await openCandidate(sourceC);
  check(await ui.value(page, '名称', { scope: dialogScope }) === rejectedName
    && JSON.stringify(await page.eval(`[...document.querySelectorAll(${arg(dialogScope + ' .draft-config-problem')})].map((el) => ({ field: el.dataset.field, text: el.textContent }))`)) === JSON.stringify(rejectedFields),
    '后端错误后取消重开仍逐字保留候选和字段错误');
  save('candidate-rejection.json', { field: rejected.body.field, failureCode: rejected.body.failureCode, displayedFields: rejectedFields });
  await ui.fill(page, '名称', migratedName, { scope: dialogScope, enter: true });

  stage = 'atomic-full-adoption';
  const adoptedC = await submitCandidate('adopt-C', sourceC, sourceA, target);
  const fullC = structuredClone(adoptedC.configuration);
  check(fullConfig(adoptedC.configuration).parametersJson === targetJson, '完整采用保持参数 JSON 原文字节');
  const imageC = await displayedFrame('adopted-C', sourceC, adoptedC.draftRevision, fixture.repairedColor);
  check(imageC.image.rgbaSha256 !== initial.image.rgbaSha256, '采用后的实际可见 PNG 像素从 A 原色变为 C 新色');
  await ui.click(page, 'tab', '配置', { scope: '.draft-tabs' });
  await ui.wait(page, 'textbox', COUNTER_LABEL, { scope: normalScope });
  check(await ui.value(page, COUNTER_LABEL, { scope: normalScope }) === ABOVE_JS_INTEGER
    && await ui.value(page, HOLD_LABEL + '（毫秒）', { scope: normalScope }) === '123.456',
    '普通配置控件来自 C 新 schema，精确保留 integer/duration');
  // Exercise the schema-generated normal controls through real queued PUTs,
  // then use ordinary configuration undo to recover even the explicit default
  // JSON entry exactly. Typing that default again would intentionally omit it.
  const ordinaryControls = [];
  async function normalControl(label, before, parametersJson, action) {
    const mark = network.mark(); await action();
    const fresh = await waitConfiguration({ parametersJson }, sourceC, mark);
    const request = network.latest((r) => r.method === 'PUT' && r.path === draftPath('/configuration')
      && r.status === 200 && r.body?.draftRevision === fresh.draftRevision, mark);
    check(request?.input.expectedDraftRevision === before.draftRevision && fresh.configuration.sourceRevision === sourceC,
      label + ' 使用最新 CAS 与 C 的普通配置 PUT');
    check(network.matching((r) => r.method === 'POST' && r.path === draftPath('/adopt-source-revision'), mark).length === 0,
      label + ' 没有重新采用源码');
    await metadataFor(sourceC, fresh.draftRevision, mark);
    ordinaryControls.push({ label, beforeDraftRevision: before.draftRevision, draftRevision: fresh.draftRevision,
      configuration: fullConfig(fresh.configuration) });
    save('ordinary-schema-controls.json', ordinaryControls);
    return fresh;
  }
  const maxJson = `{"${COUNTER}":${MAX_I64},"${HOLD}":123456}`;
  const durationJson = `{"${COUNTER}":${MAX_I64},"${HOLD}":234567}`;
  const counterChanged = await normalControl('普通 Int64 控件', adoptedC, maxJson,
    () => ui.fill(page, COUNTER_LABEL, MAX_I64, { scope: normalScope, enter: true }));
  const durationChanged = await normalControl('普通 duration 控件', counterChanged, durationJson,
    () => ui.fill(page, HOLD_LABEL + '（毫秒）', '234.567', { scope: normalScope, enter: true }));
  const durationUndone = await normalControl('普通配置撤销 duration', durationChanged, maxJson,
    () => ui.click(page, 'button', '撤销配置修改', { scope: '.draft-detail .ptabs-row' }));
  const controlsRestored = await normalControl('普通配置撤销 Int64', durationUndone, targetJson,
    () => ui.click(page, 'button', '撤销配置修改', { scope: '.draft-detail .ptabs-row' }));
  check(fullConfigEqual(controlsRestored.configuration, fullC), '普通控件实际提交与撤销后完整恢复采用快照和参数 JSON 原文');
  await ui.click(page, 'tab', '源码', { scope: '.draft-tabs' });
  await assertDirtyBuffer(dirtyText, '完整采用保留 dirty source 与 CodeMirror 编辑历史');

  stage = 'full-history-undo-redo';
  const undone = await history('撤销源码采用', 'undo-to-A', sourceA, fullA);
  await displayedFrame('undo-A', sourceA, undone.draftRevision, fixture.initialColor);
  await ui.click(page, 'tab', '配置', { scope: '.draft-tabs' });
  check(!(await ui.exists(page, 'textbox', COUNTER_LABEL, { scope: normalScope })) && await ui.value(page, '名称', { scope: normalScope }) === originalName,
    'undo 恢复 A 的完整配置和旧 schema');
  await ui.click(page, 'tab', '源码', { scope: '.draft-tabs' });
  const redone = await history('重做源码采用', 'redo-to-C', sourceC, fullC);
  await displayedFrame('redo-C', sourceC, redone.draftRevision, fixture.repairedColor);
  await assertDirtyBuffer(dirtyText, 'undo/redo 不重载 dirty source');

  stage = 'post-success-get-delivery-outage';
  await gates.failPostAdoptRead(draftId);
  const outageBefore = network.draft(draftId);
  const outageMark = network.mark();
  await ui.click(page, 'button', '撤销源码采用', { scope: '.srcbar' });
  const outagePost = await network.wait((r) => r.method === 'POST' && r.path === draftPath('/adopt-source-revision') && r.status === 200,
    '故障前真实 undo POST 已成功', outageMark);
  const outageEvents = await gates.waitOutageConsumed();
  check(outagePost.input.expectedDraftRevision === outageBefore.draftRevision && outagePost.input.sourceRevision === sourceA
    && JSON.stringify(editable(outagePost.input.configuration)) === JSON.stringify(editable(fullA))
    && outagePost.body.adoptedSourceRevision === sourceA && outageEvents.length === 4
    && outageEvents.every((event) => event.realStatus === 200),
    '只对随后单个逻辑 GET 的初次和三次重试注入交付故障');
  await page.waitFor(`!document.querySelector('.source-save[data-pending]') && ${inUse} === ${arg(sourceC)}`, '页面保留最后确认 C');
  await ui.click(page, 'tab', '配置', { scope: '.draft-tabs' });
  await ui.wait(page, 'button', '重读采用结果并比较', { scope: normalScope });
  check(await ui.read(page, 'textbox', '名称', 'return el.disabled;', { scope: normalScope })
    && await ui.value(page, '名称', { scope: normalScope }) === migratedName, '未知结果保持 C 本地值并冻结普通编辑');
  await waitNoAuthorMutation(outageMark, '结果未知期间没有配置 PUT 或自动采用重发', 1);
  // Repeating a normal metadata read is deliberately not a recovery: there is
  // no ordinary reload button in this UI, so no private store method is invoked.
  const recoverMark = network.mark();
  await ui.click(page, 'button', '重读采用结果并比较', { scope: normalScope });
  const recovered = await recordDraft('explicit-real-recovery-A', sourceA, sourceC, recoverMark);
  await metadataFor(sourceA, recovered.draftRevision, recoverMark);
  await page.waitFor(`!document.querySelector(${arg(normalScope)})?.textContent.includes('普通配置保持冻结')`, '明确真实比较后解冻');
  check(fullConfigEqual(recovered.configuration, fullA), '恢复 GET 的真实完整配置证明前次 POST 已采用 A');
  check(!(await ui.exists(page, 'textbox', COUNTER_LABEL, { scope: normalScope })), '恢复后 ordinary schema 精确回到 A');
  await displayedFrame('recovered-A', sourceA, recovered.draftRevision, fixture.initialColor);
  await ui.click(page, 'tab', '源码', { scope: '.draft-tabs' });
  check(await ui.read(page, 'button', '撤销源码采用', 'return el.disabled;', { scope: '.srcbar' })
    && await ui.read(page, 'button', '重做源码采用', 'return el.disabled;', { scope: '.srcbar' }), '明确恢复清掉无法确认的旧采用历史');
  await assertDirtyBuffer(dirtyText, '故障和明确恢复保留 source 文本与编辑历史');

  stage = 'cleanup';
  const closeMark = network.mark();
  await ui.click(page, 'button', '关闭草稿', { scope: '.draft-detail' }); await ui.waitDialog(page, '关闭草稿', { partial: true });
  await ui.clickInDialog(page, '关闭'); await page.waitFor(`!document.querySelector('.draft-detail')`, '关闭本次草稿');
  await network.wait((r) => r.method === 'DELETE' && r.path === draftPath('') && r.status === 200, 'UI 删除本次草稿', closeMark);
  draftId = undefined;
  await ui.click(page, `[role=row]:has([data-configuration-id=${arg(configurationId)}])`, null);
  await page.waitFor(`document.querySelector('.component-detail')?.dataset.configurationId === ${arg(configurationId)}`, '本次组件');
  await ui.menu(page, '组件操作', '删除…'); await ui.clickInDialog(page, '删除');
  await page.waitFor(`!document.querySelector('.component-list [data-configuration-id=${arg(configurationId)}]')`, '删除本次组件'); configurationId = undefined;
  await chooseDevice(previousDevice);
  check(metadataRequests.some((r) => r.method === 'GET') && metadataRequests.every((r) => ['GET', 'OPTIONS'].includes(r.method) && !r.hasBody && r.query.length === 2
    && r.query.map(([key]) => key).sort().join('|') === 'expectedDraftRevision|sourceRevision'
    && r.query.every(([key, value]) => ['expectedDraftRevision', 'sourceRevision'].includes(key) && /^sha256:[a-f0-9]{64}$/.test(value))),
  '所有实际声明 GET 及浏览器 OPTIONS 预检都只有两个 exact revision 查询且无 body');
  check(runtimeErrors.length === 0 && network.credentialUrls === 0, '页面无未处理异常且 URL 无凭证参数');
} catch (error) {
  failure = { stage, errorType: error?.constructor?.name ?? 'Error', message: network?.safeText(error instanceof Error ? error.message : String(error)) ?? '浏览器初始化失败',
    retainedDraftId: draftId, retainedConfigurationId: configurationId };
  process.exitCode = 1;
  if (page) await writeFile(join(output, 'failure.png'), await page.screenshot()).catch(() => undefined);
} finally {
  if (gates) { faultEvents = await gates.restore().catch(() => faultEvents); }
  if (network) { await network.flush(); network.write(output); network.listening = false; }
  if (browser) await browser.close().catch(() => { failure ??= { stage: 'browser-close', message: '本次独立 Chrome 关闭失败' }; process.exitCode = 1; });
  save('runtime-errors.json', runtimeErrors.map((message) => network?.safeText(message) ?? '<unavailable>'));
  save('delivery-gates.json', { simulated: true, events: faultEvents,
    scope: 'One real metadata response delayed unchanged; exactly one post-success logical GET delivery failed across its 4 transport attempts. No fake mutation response or API write.' });
  save('metadata-requests.json', metadataRequests);
  const summary = () => ({ status: failure ? 'failed' : 'passed', checks, displayedFrames: displayed.length, retainedDraftId: draftId,
    retainedConfigurationId: configurationId, packageId: manifest.packageId,
    coverage: 'Real UI schema/export/SVG revision migration, compile, candidate field preview, no candidate writes, ordinary and candidate unblur preservation, late old candidate metadata, real field rejection preserved after reopening, atomic full configuration adoption, new schema controls with real ordinary PUT and undo, complete source/config undo/redo, dirty text/CodeMirror history, real displayed GPU PNG color, explicit recovery after narrowly injected delivery fault.',
    limits: 'Candidate preview is local configuration fields; product has no independent candidate renderer. Delivery timing is controlled; the post-adopt GET outage is simulated after real successful responses. No ordinary reload UI exists, no private store calls. No performance, transparent/export, browser GPU acceleration or full product acceptance claim.' });
  if (failure) save('failure.json', failure); save('summary.json', summary());
  const files = (directory) => readdirSync(directory).flatMap((name) => { const path = join(directory, name); return statSync(path).isDirectory() ? files(path) : [path]; });
  let scanned = 0, scanPassed = true, removed = 0;
  for (const path of files(output)) {
    const data = readFileSync(path);
    if ([...(network?.secrets ?? [])].some((secret) => secret && data.includes(Buffer.from(secret)))) {
      process.exitCode = 1; scanPassed = false; failure ??= { stage: 'credential-scan', message: '输出凭证扫描失败' };
      rmSync(path, { force: true }); removed++;
    }
    scanned++;
  }
  if (failure) save('failure.json', failure);
  save('credential-scan.json', { scannedFiles: scanned, passed: scanPassed, removedContaminatedFiles: removed });
  save('summary.json', { ...summary(), credentialScanPassed: scanPassed });
  save('evidence-manifest.json', { files: Object.fromEntries(files(output).map((path) => [path.slice(output.length + 1), sha(readFileSync(path))])) });
  console.log(JSON.stringify({ status: failure ? 'failed' : 'passed', assertions: checks.length, displayedFrames: displayed.length,
    retainedDraftId: draftId ?? null, retainedConfigurationId: configurationId ?? null }));
}
