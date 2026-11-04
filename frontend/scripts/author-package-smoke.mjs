// Finite real Chrome UI author acceptance. All author/device writes come from UI.
// Network only observes original responses; no store access or extra API requests.
import { mkdirSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { launchChrome } from './cdp.mjs';
import { shell, ui } from './ui-query.mjs';
import { UiNetworkEvidence } from './ui-network-evidence.mjs';
import { readVectorFixture, sha } from './author-package-fixture.mjs';

const { values } = parseArgs({ options: {
  url: { type: 'string', default: 'http://127.0.0.1:5173' }, package: { type: 'string' }, output: { type: 'string' },
  headed: { type: 'boolean' }, 'binary-path': { type: 'string' }, 'inspect-fixture': { type: 'boolean' },
} });
if (!values.package) throw new Error('请用 --package 指定本轮 HTTP 生成的 SVG vector .mkfx。');
const packagePath = resolve(values.package);
const fixture = readVectorFixture(packagePath, values['binary-path']);
const { manifest, svg, descriptor, binaries } = fixture;
const entry = manifest.exports[0];
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

try {
  browser = await launchChrome({ headed: values.headed });
  page = await browser.openPage(undefined, { width: 1440, height: 1000 });
  network = new UiNetworkEvidence(page);
  browser.cdp.on((message) => { if (message.sessionId === page.sessionId && message.method === 'Runtime.exceptionThrown')
    runtimeErrors.push(message.params.exceptionDetails.exception?.description ?? message.params.exceptionDetails.text); });
  await network.enable();
  await page.send('DOM.enable');
  await page.navigate(origin.origin + '/');
  await page.waitFor(shell.connected, '连接匹配 Server');
  previousDevice = await page.eval(`localStorage.getItem('mako.renderBackend') ?? 'default'`);
  if (!['default', 'cpu', 'gpu'].includes(previousDevice)) throw new Error('独立 profile 中渲染设备值无效。');
  await chooseDevice('gpu');
  await shell.goto(page, 'library');
  await ui.click(page, 'button', '效果组件', { scope: '.library-sections' });
  await page.waitFor(`!!document.querySelector('.component-list')`, '组件列表');
  const priorIds = await page.eval(componentIds);
  stage = 'import';
  await ui.setFile(page, '导入效果包…', packagePath);
  await ui.waitDialog(page, '导入效果包');
  await page.waitFor(`!!document.querySelector('[role=dialog] .import-choices')`, 'HTTP 素材包预检');
  await ui.clickInDialog(page, '导入');
  await ui.waitDialogClosed(page, '导入效果包');
  await page.waitFor(`${componentIds}.some((id) => !${arg(priorIds)}.includes(id))`, '本次导入组件');
  configurationId = await page.eval(`${componentIds}.find((id) => !${arg(priorIds)}.includes(id))`);
  await ui.click(page, `[role=row]:has([data-configuration-id=${arg(configurationId)}])`, null);
  await page.waitFor(`document.querySelector('.component-detail')?.dataset.configurationId === ${arg(configurationId)}`, '本次组件详情');
  await ui.click(page, 'button', '编辑', { scope: '.component-detail' });
  await page.waitFor(`!!document.querySelector('.draft-config') && !!document.querySelector('[data-author-source-revision]')`, '作者草稿');
  draftId = await page.eval(`document.querySelector('.draft-detail').dataset.draftId`);
  const created = (await network.wait((record) => record.method === 'POST' && record.path === '/api/v1/drafts'
    && record.status === 201 && record.body?.draftId === draftId, '实际创建草稿')).body;
  const sourceA = created.currentSourceRevision;
  check(created.configuration.sourceRevision === sourceA && created.configuration.packageId === manifest.packageId
    && created.configuration.exportId === entry.id, 'UI 导入真实素材并固定初始来源 A');
  const backend = await network.wait((record) => record.path === '/api/v1/render-backends' && record.status === 200
    && record.body.backends?.some((item) => item.renderBackend === 'gpu' && item.available && item.api === 'vulkan'), '可用 Vulkan 后端');
  save('render-backends.json', backend.body);
  await recordDraft('imported-A', sourceA, sourceA);
  const initialImage = await displayedFrame('initial-A', sourceA, created.draftRevision, fixture.initialColor);

  stage = 'read-complete-package';
  await ui.click(page, 'tab', '源码', { scope: '.draft-tabs' });
  await page.waitFor(`${sourceShown} === ${arg(sourceA)} && !!document.querySelector('.cm-content')`, '实际包编辑器');
  const loaded = (await network.wait((record) => record.method === 'GET' && record.path === `/api/v1/drafts/${draftId}/package-files`
    && record.status === 200 && record.body?.sourceRevision === sourceA, '完整包读取')).body;
  await checkCanonical(loaded, 'initial-canonical');
  for (const file of binaries) {
    await selectFile(file.path);
    const detail = await page.eval(`document.querySelector('.source-binary')?.textContent ?? ''`);
    check(detail.includes(file.path) && detail.includes(file.role) && detail.includes(String(file.data.length)) && detail.includes(sha(file.data))
      && !(await page.eval(`!!document.querySelector('.source-editor-frame .cm-content')`)), '二进制资源只显示路径、角色、大小和 SHA：' + file.path);
  }

  stage = 'dirty-files-and-configuration-cas';
  const badSvg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'><use href='#missing'/></svg>";
  await selectFile(svg.path); await replaceText(badSvg); expectedTexts.set(svg.path, badSvg);
  const changedDescriptor = structuredClone(fixture.descriptorJson);
  changedDescriptor.viewport.width += 20;
  const descriptorText = JSON.stringify(changedDescriptor, null, 2) + '\n';
  await selectFile(descriptor.path); await replaceText(descriptorText); expectedTexts.set(descriptor.path, descriptorText);
  await selectFile('manifest.json');
  const submittedManifestX = loaded.manifestJson + '\n';
  await replaceText(submittedManifestX);
  await selectFile(svg.path);
  check(await editorText() === badSvg && await page.eval(dirty), '跨 SVG、descriptor、manifest 切换保留 dirty 文本');
  await ui.click(page, 'tab', '配置', { scope: '.draft-tabs' });
  const configMark = network.mark();
  await ui.fill(page, '名称', 'SVG UI exact CAS', { scope: '.draft-config', enter: true });
  const configured = (await network.wait((record) => record.method === 'PUT' && record.path === `/api/v1/drafts/${draftId}/configuration`
    && record.status === 200 && record.body.configuration.name === 'SVG UI exact CAS', '真实配置提交', configMark));
  check(configured.input.expectedDraftRevision === created.draftRevision && configured.body.configuration.sourceRevision === sourceA,
    '配置提交使用初始 CAS 并保留在用来源 A');
  await page.waitFor(`!document.querySelector('.draft-config[data-pending]')`, '配置提交结束');
  await ui.click(page, 'tab', '源码', { scope: '.draft-tabs' });
  await selectFile(svg.path);
  check(await editorText() === badSvg && await page.eval(dirty), '配置页往返后 dirty SVG 缓冲完整保留');
  const savedX = await savePackage('invalid-X', configured.body.draftRevision, submittedManifestX);
  const sourceX = savedX.currentSourceRevision;
  check(sourceX !== sourceA && savedX.configuration.sourceRevision === sourceA, '无效 SVG 可保存新修订 X，试样来源保持 A');
  await recordDraft('saved-X-keeps-A', sourceA, sourceX);

  stage = 'named-svg-compile-failure';
  const badResult = await compileShown('invalid-X', sourceX, false);
  check(badResult.artifactHash === 'none' && badResult.diagnostics.some((item) => item.isError
    && item.code === 'PackageSvgReferenceMissing' && item.path === svg.path && item.line === 0 && item.column === 0),
  '真实编译报告是缺失引用具名错误和 SVG 路径，未发布 C# artifact');
  await ui.click(page, 'button', 'PackageSvgReferenceMissing', { partial: true, scope: '.source-compile' });
  await page.waitFor(`document.querySelector('.source-file[aria-current=true]')?.dataset.path === ${arg(svg.path)}`, '资源诊断定位 SVG');
  const location = await page.eval(`[...document.querySelectorAll('.compile-row')].find((el) => el.textContent.includes('PackageSvgReferenceMissing'))?.querySelector('.compile-location')?.textContent`);
  check(location === svg.path && !(await page.eval(`!!document.querySelector('.cm-lintRange-error')`)), '资源级诊断只显示路径并切文件，不伪造 0:0 编辑器标记');
  await recordDraft('failed-X-keeps-A', sourceA, sourceX);
  await displayedFrame('failed-X-still-A', sourceA, savedX.draftRevision, fixture.initialColor);

  stage = 'dirty-revision-cancel';
  const unsaved = badSvg + '\n<!-- unsaved buffer must survive cancel -->';
  await replaceText(unsaved);
  await selectFile(descriptor.path); await selectFile(svg.path);
  await ui.click(page, `.source-revision[data-revision=${arg(sourceA)}] .source-revision-view`, null);
  await ui.waitDialog(page, '查看别的修订？');
  await ui.clickInDialog(page, '取消');
  await ui.waitDialogClosed(page, '查看别的修订？');
  check(await page.eval(sourceShown) === sourceX && await page.eval(dirty) && await editorText() === unsaved,
    '取消切修订后仍为 X，dirty 缓冲逐字保留');
  await replaceText(badSvg, false);

  stage = 'repair-compile-explicit-adoption';
  await replaceText(fixture.repairedSvg); expectedTexts.set(svg.path, fixture.repairedSvg);
  await selectFile('manifest.json');
  const submittedManifestB = await editorText();
  const savedB = await savePackage('repaired-B', savedX.draftRevision, submittedManifestB);
  const sourceB = savedB.currentSourceRevision;
  check(![sourceA, sourceX].includes(sourceB) && savedB.configuration.sourceRevision === sourceA,
    '修复生成第三个不可变来源 B，保存仍保持在用 A');
  const resultB = await compileShown('repaired-B', sourceB, true);
  check(resultB.artifactHash !== 'none' && !resultB.diagnostics.some((item) => item.isError), '修复后实际编译成功并产生 artifact');
  await recordDraft('compiled-B-keeps-A', sourceA, sourceB);
  await displayedFrame('compiled-B-still-A', sourceA, savedB.draftRevision, fixture.initialColor);
  const adoptMark = network.mark();
  await ui.click(page, 'button', '采用编译通过的修订', { partial: true, scope: '.source-compile' });
  const adopted = await network.wait((record) => record.method === 'POST' && record.path === `/api/v1/drafts/${draftId}/adopt-source-revision`
    && record.status === 200, '显式采用 B', adoptMark);
  check(adopted.input.expectedDraftRevision === savedB.draftRevision && adopted.input.sourceRevision === sourceB
    && adopted.body.previousSourceRevision === sourceA && adopted.body.adoptedSourceRevision === sourceB,
  '只有显式采用按钮才以精确 CAS 把在用来源 A 切换到 B');
  const adoptedDraft = await recordDraft('adopted-B', sourceB, sourceB, adoptMark);
  check(adoptedDraft.configuration.parametersJson === configured.body.configuration.parametersJson, '采用保持 UI 已提交配置参数');
  const imageB = await displayedFrame('adopted-B', sourceB, adoptedDraft.draftRevision, fixture.repairedColor);
  check(imageB.image.rgbaSha256 !== initialImage.image.rgbaSha256, '实际显示的修复 SVG 像素已变化');
  const labels = await page.eval(`[...document.querySelectorAll('.source-revision-name')].map((el) => el.textContent).join(' ')`);
  check(['最新', '编译通过', '已采用', '在用'].every((label) => labels.includes(label)), '修订列表同时表达最新、编译通过、已采用和在用');

  stage = 'clean-revision-switch';
  await ui.click(page, `.source-revision[data-revision=${arg(sourceA)}] .source-revision-view`, null);
  await page.waitFor(`${sourceShown} === ${arg(sourceA)} && !(${dirty})`, '只查看原修订 A');
  await selectFile(svg.path);
  check(await editorText() === svg.data.toString('utf8') && await page.eval(inUse) === sourceB, '查看历史原文不改变在用 B');
  await ui.click(page, `.source-revision[data-revision=${arg(sourceB)}] .source-revision-view`, null);
  await page.waitFor(`${sourceShown} === ${arg(sourceB)} && !(${dirty})`, '返回修订 B');
  await selectFile(svg.path);
  check(await editorText() === fixture.repairedSvg, '跨修订返回精确修复 SVG');

  stage = 'cleanup';
  const closeMark = network.mark();
  await ui.click(page, 'button', '关闭草稿', { scope: '.draft-detail' });
  await ui.waitDialog(page, '关闭草稿', { partial: true });
  await ui.clickInDialog(page, '关闭');
  await page.waitFor(`!document.querySelector('.draft-detail')`, '关闭本次草稿');
  await network.wait((record) => record.method === 'DELETE' && record.path === `/api/v1/drafts/${draftId}` && record.status === 200, '删除本次草稿', closeMark);
  draftId = undefined;
  await ui.click(page, `[role=row]:has([data-configuration-id=${arg(configurationId)}])`, null);
  await page.waitFor(`document.querySelector('.component-detail')?.dataset.configurationId === ${arg(configurationId)}`, '本次组件');
  await ui.menu(page, '组件操作', '删除…');
  await ui.clickInDialog(page, '删除');
  await page.waitFor(`!document.querySelector('.component-list [data-configuration-id=${arg(configurationId)}]')`, '删除本次组件');
  configurationId = undefined;
  await chooseDevice(previousDevice);
  check(runtimeErrors.length === 0, '真实页面没有未处理异常');
  check(network.credentialUrls === 0, '所有浏览器请求 URL 没有凭证参数');
} catch (error) {
  failure = { stage, errorType: error?.constructor?.name ?? 'Error',
    message: network?.safeText(error instanceof Error ? error.message : String(error)) ?? '浏览器初始化失败',
    retainedDraftId: draftId, retainedConfigurationId: configurationId };
  process.exitCode = 1;
  if (page) await writeFile(join(output, 'failure.png'), await page.screenshot()).catch(() => undefined);
} finally {
  if (network) { await network.flush(); network.write(output); network.listening = false; }
  if (browser) await browser.close().catch(() => { failure ??= { stage: 'browser-close', message: '本次独立 Chrome 关闭失败' }; process.exitCode = 1; });
  save('runtime-errors.json', runtimeErrors.map((message) => network?.safeText(message) ?? '<unavailable>'));
  const summary = () => ({ status: failure ? 'failed' : 'passed', checks, displayedFrames: displayed.length,
    retainedDraftId: draftId, retainedConfigurationId: configurationId, packageId: manifest.packageId,
    readonlyBinary: { status: binaries.length ? 'included' : 'not-in-supplied-fixture', paths: binaries.map((file) => file.path) },
    coverage: 'Real Chrome UI import, complete package read/PUT, SVG/descriptor/manifest edit, exact configuration CAS, dirty file/tab buffers, cancelled dirty revision switch, canonical manifest SHA sync, named SVG compile failure, repair, explicit adoption, visible decoded opaque GPU PNG and clean history view.',
    limits: 'Transparent PNG and stale package CAS 409 belong to companion HTTP acceptance. This UI has no same-draft second editor/recovery entry. Response observation pauses original PNG delivery unchanged and is not performance evidence. No browser GPU acceleration, hardware/process proof, full product/export/platform/performance acceptance.' });
  if (failure) save('failure.json', failure);
  save('summary.json', summary());
  const files = (directory) => readdirSync(directory).flatMap((name) => { const path = join(directory, name); return statSync(path).isDirectory() ? files(path) : [path]; });
  let scanned = 0, scanPassed = true, removed = 0;
  for (const path of files(output)) {
    const data = readFileSync(path);
    if ([...(network?.secrets ?? [])].some((secret) => secret && data.includes(Buffer.from(secret)))) {
      process.exitCode = 1; scanPassed = false;
      failure ??= { stage: 'credential-scan', message: '输出凭证扫描失败' };
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
