// M07 有限真实 Chrome UI 验收。运行者先启动匹配 Server/Worker 与前端；本工具只启动自己的独立 Chrome。
// 所有作者/设备修改均走 UI；CDP Network 只读观察固定身份，未调用 store 或额外 HTTP 写入。
import { createHash } from 'node:crypto';
import { mkdtempSync, mkdirSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { writeFile } from 'node:fs/promises';
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
  sample: { type: 'string', default: 'WordEmphasis' },
} });
if (!values.output) throw new Error('请用 --output 指定全新证据目录。');
const samples = ['WordEmphasis', 'RepeatEmphasis', 'ClusterExit'];
if (!samples.includes(values.sample)) throw new Error('--sample 必须是 WordEmphasis、RepeatEmphasis 或 ClusterExit。');
const sample = values.sample;
const fullAuthor = sample === 'WordEmphasis';
const origin = new URL(values.url);
if (!['http:', 'https:'].includes(origin.protocol) || !['127.0.0.1', 'localhost', '[::1]'].includes(origin.hostname)
  || origin.username || origin.password || origin.search || origin.hash) {
  throw new Error('--url 必须是没有凭证和查询参数的 loopback 前端地址。');
}
const output = resolve(values.output);
mkdirSync(output, { recursive: false, mode: 0o700 });
const sha = (value) => createHash('sha256').update(value).digest('hex');
const save = (name, value) => writeFileSync(join(output, name), JSON.stringify(value, null, 2) + '\n');
const root = join(resolveBackendRepo(), 'samples', 'ScriptEffectsV2', sample);
const manifestBytes = readFileSync(join(root, 'manifest.json'));
const manifest = JSON.parse(manifestBytes.toString('utf8'));
if (manifest.packageVersion !== 3 || !manifest.sdkVersion.startsWith('2.')) throw new Error('测试素材必须是 package3/SDK2。');
const files = manifest.files.map((entry) => {
  const data = readFileSync(join(root, entry.path));
  if (sha(data) !== entry.sha256) throw new Error(`SDK2 素材哈希不匹配：${entry.path}`);
  return { name: entry.path, data };
});
const sourceFile = files.find((file) => manifest.files.some((item) => item.path === file.name && item.role === 'csharp'));
if (!sourceFile || files.filter((file) => manifest.files.some((item) => item.path === file.name && item.role === 'csharp')).length !== 1) {
  throw new Error(`本次有限 UI 场景要求 ${sample} 包只有一个 C# 文件。`);
}
if (manifest.exports.length !== 1 || manifest.exports[0].kind !== 'controller') throw new Error('本次有限 UI 场景要求单一 controller 导出。');
const entry = manifest.exports[0];
const parameterIds = entry.parameters ?? [];
if (parameterIds.some((id) => !manifest.parameters.some((parameter) => parameter.id === id))) throw new Error('导出参数与实际 manifest 不一致。');
const parameterDefinitions = manifest.parameters.filter((parameter) => parameterIds.includes(parameter.id));
const sourceText = sourceFile.data.toString('utf8');
const assets = mkdtempSync(join(tmpdir(), 'mako-author-v2-'));
const archive = zipFiles([{ name: 'manifest.json', data: manifestBytes }, ...files]);
const packagePath = join(assets, `M07${sample}V2.mkfx`);
writeFileSync(packagePath, archive);
save('fixture.json', { sample: `ScriptEffectsV2/${sample}`, packageVersion: manifest.packageVersion, sdkVersion: manifest.sdkVersion,
  packageId: manifest.packageId, exportId: entry.id, entryType: entry.entryType, sourcePath: sourceFile.name, parameters: parameterDefinitions,
  manifestSha256: sha(manifestBytes), archiveSha256: sha(archive), files: Object.fromEntries(files.map((file) => [file.name, sha(file.data)])) });

const checks = [];
const identities = [];
const displayed = [];
const runtimeErrors = [];
let browser;
let page;
let network;
let configurationId;
let draftId;
let previousDevice;
let stage = 'initialize';
let failure;
const sampleImage = `.template-sample [data-status=ready] img`;
const componentIds = `[...document.querySelectorAll('.component-list [data-configuration-id]')].map((el) => el.dataset.configurationId)`;
const inUse = `document.querySelector('[data-author-source-revision]')?.dataset.authorSourceRevision`;
const sourceShown = `document.querySelector('.draft-source')?.dataset.revision`;
const deviceLabels = { default: '跟随后端默认', cpu: 'CPU', gpu: 'GPU' };

function check(condition, label) {
  checks.push({ passed: !!condition, label });
  if (!condition) throw new Error(label);
  console.log(`✓ ${label}`);
}

async function chooseDevice(value) {
  await ui.click(page, 'button', '（渲染设备）', { partial: true, scope: '.app-bar' });
  await page.waitFor(`!!document.querySelector('.render-device-menu')`, '设备菜单');
  await ui.click(page, 'menuitem', deviceLabels[value], { partial: value === 'default', scope: '.render-device-menu' });
  await page.waitFor(`!document.querySelector('.render-device-menu')`, '设备菜单关闭');
}

async function recordDraft(label, expectedSource, expectedLatest) {
  await page.waitFor(`${inUse} === ${JSON.stringify(expectedSource)}`, label + ' 的在用来源');
  const draft = (await network.wait((record) => record.status < 300 && record.body?.draftId === draftId
    && record.body?.configuration?.sourceRevision === expectedSource
    && (!expectedLatest || record.body.currentSourceRevision === expectedLatest), label + ' 的草稿响应')).body;
  identities.push({ label, draftId, draftRevision: draft.draftRevision, latestSourceRevision: draft.currentSourceRevision,
    inUseSourceRevision: draft.configuration.sourceRevision, adoptedSourceRevision: draft.adoptedSourceRevision,
    lastCompilation: draft.lastCompilation, parametersJson: draft.configuration.parametersJson });
  save('author-identities.json', identities);
  return draft;
}

async function selectSourceFile() {
  // 完整包编辑器默认可能停在 manifest.json；源码编辑只改本样例唯一 C# 入口。
  await ui.click(page, `.source-file[data-path=${JSON.stringify(sourceFile.name)}]`, null);
  await page.waitFor(`document.querySelector('.source-file[aria-current=true]')?.dataset.path === ${JSON.stringify(sourceFile.name)}`, '切换到 C# 入口文件');
}

async function replaceSource(text) {
  await selectSourceFile();
  await page.waitFor(`!!document.querySelector('.cm-content')`, 'CodeMirror 源码编辑器');
  await page.eval(`document.querySelector('.cm-content').focus()`);
  for (const type of ['keyDown', 'keyUp']) await page.send('Input.dispatchKeyEvent', { type, key: 'a', code: 'KeyA', modifiers: 2, windowsVirtualKeyCode: 65 });
  await page.send('Input.insertText', { text });
  await page.waitFor(`!!document.querySelector('.draft-source[data-dirty]')`, '编辑器源码已修改');
}

async function saveSource(label, expectedText) {
  const before = network.mark();
  await ui.click(page, 'button', '保存修订', { scope: '.srcbar' });
  const saved = await network.wait((record) => record.method === 'PUT' && record.path === `/api/v1/drafts/${draftId}/package-files`
    && record.status === 200, label, before);
  // manifest 由服务派生文件 SHA，编辑器提交的是上次规范化原文，这里只核对 C# 入口字节。
  check(saved.input.textFiles.some((file) => file.path === sourceFile.name && file.textSha256 === sha(expectedText)),
    label + ' 从 UI 完整包提交本样例实际 C# 入口编辑后字节');
  const revision = saved.body.currentSourceRevision;
  const canonical = await network.wait((record) => record.method === 'GET' && record.path === `/api/v1/drafts/${draftId}/package-files`
    && record.status === 200 && record.body?.sourceRevision === revision, label + ' 保存后的规范完整包', before);
  check(canonical.body.files.some((file) => file.path === sourceFile.name && file.textSha256 === sha(expectedText)),
    label + ' 规范完整包保留编辑后的 C# 字节');
  await page.waitFor(`${sourceShown} === ${JSON.stringify(revision)} && !document.querySelector('.draft-source[data-dirty]')`, label + ' 的编辑器固定修订');
  return revision;
}

async function compileShown(label, source, succeeded) {
  const before = network.mark();
  await ui.click(page, 'button', '编译查看的修订', { scope: '.srcbar' });
  const accepted = await network.wait((record) => record.method === 'POST' && record.path === `/api/v1/drafts/${draftId}/compilation-jobs`
    && record.status === 202, label + ' 接受任务', before);
  const fixed = accepted.body.authorCompilationInput;
  check(accepted.input.sourceRevision === source && fixed.sourceRevision === source && fixed.draftId === draftId
    && fixed.draftRevision === accepted.input.expectedDraftRevision, label + ' 任务固定编辑器所看源码与草稿修订');
  const result = await network.wait((record) => record.path === `/api/v1/jobs/${accepted.body.jobId}/compilation-result`
    && record.status === 200, label + ' 实际编译结果', before);
  check(result.body.sourceRevision === source && result.body.draftRevision === fixed.draftRevision
    && result.body.succeeded === succeeded, label + ' 编译结果保持接受时身份与预期结果');
  const terminal = await network.wait((record) => record.path === `/api/v1/jobs/${accepted.body.jobId}`
    && record.body?.state === 'succeeded', label + ' 任务终态', before);
  check(terminal.body.authorCompilationInput.sourceRevision === source, label + ' 终态来源保持固定');
  const refreshed = await network.wait((record) => record.method === 'GET' && record.status === 200
    && record.path === `/api/v1/drafts/${draftId}` && record.body?.draftId === draftId
    && record.body.configuration && record.body.lastCompilation?.sourceRevision === source
    && record.body.lastCompilation.succeeded === succeeded, label + ' 编译后的真实草稿刷新', before);
  check(refreshed.body.draftRevision === fixed.draftRevision, label + ' 编译摘要刷新保持草稿修订');
  await page.waitFor(`document.querySelector('.source-compile')?.dataset.outcome === ${JSON.stringify(succeeded ? 'passed' : 'failed')}
    && !document.querySelector('.source-compile-button')?.disabled && !document.querySelector('.source-compile[data-stale]')`, label + ' UI 编译结果', 180_000);
  return result.body;
}

async function displayedFrame(label, source, expectedDraftRevision) {
  const response = await network.wait((record) => record.method === 'GET' && record.status === 200
    && record.body?.session?.source?.kind === 'authorDraft'
    && record.body.session.source.sourceId === draftId && record.body.session.source.authorSourceRevision === source
    && record.body.session.source.sourceRevision === expectedDraftRevision && record.body.session.latestFrame,
  label + ' 固定输入的真实预览状态');
  const session = response.body.session;
  const frame = session.latestFrame;
  const image = await page.waitFor(`(async () => {
    const img = document.querySelector(${JSON.stringify(sampleImage)});
    if (!img || !img.complete || img.naturalWidth === 0 || img.dataset.sha !== ${JSON.stringify(frame.content.sha256)}
      || img.dataset.time !== ${JSON.stringify(frame.timeUs)}) return null;
    img.scrollIntoView({ block: 'center', inline: 'nearest' });
    const src = img.currentSrc;
    await img.decode();
    // 只读取页面已显示的 Blob URL，不另发 API 请求或改变作者状态。
    const blobBytes = await (await fetch(src)).arrayBuffer();
    const blobDigest = await crypto.subtle.digest('SHA-256', blobBytes);
    const canvas = document.createElement('canvas'); canvas.width = img.naturalWidth; canvas.height = img.naturalHeight;
    const context = canvas.getContext('2d'); context.drawImage(img, 0, 0);
    const rgba = context.getImageData(0, 0, canvas.width, canvas.height).data;
    const digest = await crypto.subtle.digest('SHA-256', rgba);
    if (!img.isConnected || document.querySelector(${JSON.stringify(sampleImage)}) !== img || img.currentSrc !== src
      || img.dataset.sha !== ${JSON.stringify(frame.content.sha256)} || img.dataset.time !== ${JSON.stringify(frame.timeUs)}) return null;
    const rect = img.getBoundingClientRect();
    const center = { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 };
    const visible = rect.width > 0 && rect.height > 0 && center.x >= 0 && center.x < innerWidth
      && center.y >= 0 && center.y < innerHeight && document.elementFromPoint(center.x, center.y) === img;
    if (!visible) return null;
    return { sha256: img.dataset.sha, timeUs: img.dataset.time, width: canvas.width, height: canvas.height,
      blobSha256: [...new Uint8Array(blobDigest)].map((n) => n.toString(16).padStart(2, '0')).join(''),
      rgbaSha256: [...new Uint8Array(digest)].map((n) => n.toString(16).padStart(2, '0')).join(''),
      complete: img.complete, connected: img.isConnected, visible, objectUrl: img.currentSrc.startsWith('blob:') };
  })()`, label + ' 对应图片稳定加载并完成解码', 120_000);
  const bytes = await network.wait((record) => record.status === 200 && record.download?.sha256 === image.sha256
    && record.path === frame.content.contentUrl, label + ' 完整 PNG 下载');
  check(session.renderBackend === 'gpu' && frame.renderBackend === 'gpu'
    && frame.sourceRevision === session.source.sourceRevision && frame.previewGeneration === session.previewGeneration
    && bytes.download.sizeBytes === frame.content.sizeBytes && bytes.download.pngSignatureValid
    && bytes.download.width === frame.width && bytes.download.height === frame.height
    && image.blobSha256 === frame.content.sha256 && image.width === frame.width && image.height === frame.height
    && image.objectUrl && image.connected && image.visible,
  label + ' 实际已解码图片匹配 GPU 固定源码、代次和 immutable PNG');
  displayed.push({ label, source: session.source, previewId: session.previewId, previewGeneration: session.previewGeneration,
    frameId: frame.frameId, requestSeq: frame.requestSeq, png: bytes.download, image, renderBackend: session.renderBackend });
  save('displayed-frames.json', displayed);
  await writeFile(join(output, `${label}.png`), await page.screenshot());
  return displayed.at(-1);
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
  previousDevice = await page.eval(`localStorage.getItem('mako.renderBackend') ?? 'default'`);
  stage = fullAuthor ? 'cpu-import' : 'gpu-import';
  await chooseDevice(fullAuthor ? 'cpu' : 'gpu');
  await shell.goto(page, 'library');
  await ui.click(page, 'button', '效果组件', { scope: '.library-sections' });
  await page.waitFor(`!!document.querySelector('.component-list')`, '组件列表');
  const priorIds = await page.eval(componentIds);
  await ui.setFile(page, '导入效果包…', packagePath);
  await ui.waitDialog(page, '导入效果包');
  await page.waitFor(`!!document.querySelector('[role=dialog] .import-choices')`, 'SDK2 包预检');
  await ui.clickInDialog(page, '导入');
  await ui.waitDialogClosed(page, '导入效果包');
  await page.waitFor(`${componentIds}.some((id) => !${JSON.stringify(priorIds)}.includes(id))`, '创建本次导入组件');
  configurationId = await page.eval(`${componentIds}.find((id) => !${JSON.stringify(priorIds)}.includes(id))`);
  await ui.click(page, `[role=row]:has([data-configuration-id="${configurationId}"])`, null);
  await page.waitFor(`document.querySelector('.component-detail')?.dataset.configurationId === ${JSON.stringify(configurationId)}`, '本次组件详情');
  await ui.click(page, 'button', '编辑', { scope: '.component-detail' });
  await page.waitFor(`!!document.querySelector('.draft-config') && !!document.querySelector('[data-author-source-revision]')`, 'SDK2 草稿');
  draftId = await page.eval(`document.querySelector('.draft-detail').dataset.draftId`);
  const created = await network.wait((record) => record.method === 'POST' && record.path === '/api/v1/drafts' && record.status === 201
    && record.body?.draftId === draftId, '实际创建 SDK2 草稿');
  const sourceA = created.body.currentSourceRevision;
  check(created.body.packageVersion === 3 && created.body.sdkVersion.startsWith('2.')
    && created.body.configuration.sourceRevision === sourceA && created.body.configuration.packageId === manifest.packageId
    && created.body.configuration.exportId === entry.id, '导入草稿显示本样例真实 package3/SDK2、导出及初始在用来源 A');
  await page.waitFor(`${JSON.stringify(parameterIds)}.every((id) => [...document.querySelectorAll('.draft-config [data-param]')]
    .some((el) => el.dataset.param === id))`, '本样例实际参数控件加载');
  const renderedParameters = await page.eval(`[...document.querySelectorAll('.draft-config [data-param]')].map((el) => el.dataset.param)`);
  check(parameterIds.every((id) => renderedParameters.includes(id)), '配置 UI 使用本样例 manifest 声明的参数控件');
  await recordDraft('imported-A', sourceA, sourceA);
  if (fullAuthor) {
    const cpuCreate = await network.wait((record) => record.method === 'POST' && record.path === '/api/v1/preview-sessions'
      && record.input?.source?.sourceId === draftId && record.input.renderBackend === 'cpu' && record.status === 422,
    'CPU 作者预览具名拒绝');
    check(cpuCreate.body.code === 'commandNotSupported' && cpuCreate.body.field === 'renderBackend'
      && cpuCreate.body.failureCode === 'PackageBackendUnsupported', '真实 UI 的 CPU 试样请求具名拒绝');
    await page.waitFor(`document.querySelector('.template-sample')?.textContent.includes('PackageBackendUnsupported')`, 'CPU 拒绝文案');
    await ui.click(page, 'tab', '验证', { scope: '.draft-tabs' });
    const cpuValidationMark = network.mark();
    await ui.click(page, 'button', '开始验证', { scope: '.draft-validation' });
    const cpuValidation = await network.wait((record) => record.method === 'POST' && record.path === `/api/v1/drafts/${draftId}/validation-jobs`
      && record.status === 422, 'CPU 验证具名拒绝', cpuValidationMark);
    check(cpuValidation.body.code === 'commandNotSupported' && cpuValidation.body.failureCode === 'PackageBackendUnsupported'
      && cpuValidation.body.field === 'renderBackend'
      && cpuValidation.input.renderBackend === 'cpu' && !cpuValidation.body.jobId, 'CPU 正式验证在接受任务前拒绝');
    await page.waitFor(`document.querySelector('.draft-validation')?.textContent.includes('PackageBackendUnsupported')`, '验证显示 CPU 具名拒绝');
  }

  stage = 'gpu-initial-preview';
  await chooseDevice('gpu');
  const device = await network.wait((record) => record.path === '/api/v1/render-backends' && record.status === 200
    && record.body.backends?.some((item) => item.renderBackend === 'gpu' && item.available && item.api === 'vulkan'), 'Vulkan 设备检测');
  save('render-backends.json', device.body);
  await displayedFrame('initial-A', sourceA, created.body.draftRevision);
  await ui.click(page, 'tab', '源码', { scope: '.draft-tabs' });
  await page.waitFor(`${sourceShown} === ${JSON.stringify(sourceA)} && !!document.querySelector('.cm-content')`, '读取本样例实际源码');
  const loadedSource = await network.wait((record) => record.method === 'GET' && record.status === 200
    && record.path === `/api/v1/drafts/${draftId}/package-files` && record.body?.sourceRevision === sourceA,
  '实际完整包读取身份');
  check(loadedSource.body.files.length === files.length && loadedSource.body.files.some((file) => file.path === sourceFile.name
    && file.role === 'csharp' && file.editable && file.textSha256 === sha(sourceFile.data)), '源码 UI 读取本样例真实 manifest 对应 C# 内容');

  if (fullAuthor) {
    stage = 'save-and-failed-compile';
    const broken = sourceText + '\n#error M07 deliberate UI compilation rejection\n';
    await replaceSource(broken);
    const sourceX = await saveSource('保存非法源码 X', broken);
    const savedX = await recordDraft('saved-X-keeps-A', sourceA, sourceX);
    check(savedX.currentSourceRevision !== sourceA && savedX.adoptedSourceRevision === created.body.adoptedSourceRevision,
      '保存非法源码只推进最新修订，保持在用/采用来源');
    await compileShown('非法 X', sourceX, false);
    await recordDraft('failed-X-keeps-A', sourceA, sourceX);
    await ui.click(page, 'button', 'CS1029', { partial: true, scope: '.source-compile' });
    await page.waitFor(`!!document.querySelector('.cm-lintRange-error')`, '编译诊断标记');
    check((await page.eval(inUse)) === sourceA, '编译失败后 UI 在用来源仍为 A');
    await displayedFrame('failed-X-still-A', sourceA, savedX.draftRevision);
  }

  stage = 'success-and-explicit-adoption';
  const edited = sourceText + `\n// M07 ${sample} successful immutable UI source B\n`;
  await replaceSource(edited);
  const sourceB = await saveSource('保存正确源码 B', edited);
  const savedB = await recordDraft('saved-B-keeps-A', sourceA, sourceB);
  check(sourceB !== sourceA && savedB.adoptedSourceRevision === created.body.adoptedSourceRevision
    && savedB.configuration.parametersJson === created.body.configuration.parametersJson,
  '源码编辑保存只推进最新修订，保留本样例在用/采用来源和实际参数');
  await compileShown('正确 B', sourceB, true);
  const compiledB = await recordDraft('compiled-B-keeps-A', sourceA, sourceB);
  check(compiledB.lastCompilation?.sourceRevision === sourceB && compiledB.lastCompilation.succeeded,
    '成功编译 B 的真实摘要保持源码身份，在用来源仍为 A');
  const adoptMark = network.mark();
  await ui.click(page, 'button', '采用编译通过的修订', { partial: true, scope: '.source-compile' });
  await ui.waitDialog(page, '采用修订并配置', { partial: true });
  await ui.clickInDialog(page, '采用源码与配置');
  const adopted = await network.wait((record) => record.method === 'POST' && record.path === `/api/v1/drafts/${draftId}/adopt-source-revision`
    && record.status === 200, '显式采用 B', adoptMark);
  await ui.waitDialogClosed(page, '采用修订并配置', { partial: true });
  check(adopted.input.sourceRevision === sourceB && adopted.body.previousSourceRevision === sourceA
    && adopted.body.adoptedSourceRevision === sourceB, '显式采用按钮才提交 A→B 的来源切换');
  const adoptedDraft = await recordDraft('adopted-B', sourceB, sourceB);
  check(adoptedDraft.configuration.parametersJson === savedB.configuration.parametersJson, '显式采用保留本样例 manifest 参数配置');
  await displayedFrame('adopted-B', sourceB, adoptedDraft.draftRevision);
  const labels = await page.eval(`[...document.querySelectorAll('.source-revisions .source-revision-name')].map((el) => el.textContent).join(' ')`);
  check(['最新', '编译通过', '已采用', '在用'].every((label) => labels.includes(label)), '修订列表区分最新、编译通过、已采用与在用身份');

  if (fullAuthor) {
    stage = 'parameter-edit-undo-redo';
    const parameter = parameterDefinitions.find((item) => item.id === 'peakScale' && item.type === 'number');
    check(parameter && parameter.min <= 3 && parameter.max >= 3, 'WordEmphasis 完整链参数值在实际 manifest 允许范围内');
    const parameterScope = `.draft-config [data-param=${parameter.id}]`;
    await ui.click(page, 'tab', '配置', { scope: '.draft-tabs' });
    const parameterBefore = await ui.value(page, null, { scope: parameterScope });
    const parameterMark = network.mark();
    await ui.fill(page, null, '3', { scope: parameterScope, enter: true });
    const parameterSaved = await network.wait((record) => record.method === 'PUT' && record.path === `/api/v1/drafts/${draftId}/configuration`
      && record.status === 200 && JSON.parse(record.body.configuration.parametersJson)[parameter.id] === 3, '参数保存', parameterMark);
    await page.waitFor(`!document.querySelector('.draft-config[data-pending]')`, '参数提交结束');
    check(parameterSaved.input.expectedDraftRevision === adoptedDraft.draftRevision
      && parameterSaved.body.configuration.sourceRevision === sourceB, '参数编辑使用精确草稿 CAS 并保持采用来源 B');
    await displayedFrame('parameter-three-B', sourceB, parameterSaved.body.draftRevision);
    const undoMark = network.mark();
    await ui.click(page, 'button', '撤销配置修改');
    const undo = await network.wait((record) => record.method === 'PUT' && record.path === `/api/v1/drafts/${draftId}/configuration`
      && record.status === 200 && record.body.configuration.parametersJson === adoptedDraft.configuration.parametersJson, '参数撤销保存', undoMark);
    await page.waitFor(`!document.querySelector('.draft-config[data-pending]')`, '撤销参数提交结束');
    check((await ui.value(page, null, { scope: parameterScope })) === parameterBefore
      && undo.body.configuration.sourceRevision === sourceB && undo.input.expectedDraftRevision === parameterSaved.body.draftRevision,
    '参数撤销经过 UI 和新 CAS，保留采用来源 B');
    const redoMark = network.mark();
    await ui.click(page, 'button', '重做配置修改');
    const redo = await network.wait((record) => record.method === 'PUT' && record.path === `/api/v1/drafts/${draftId}/configuration`
      && record.status === 200 && JSON.parse(record.body.configuration.parametersJson)[parameter.id] === 3, '参数重做保存', redoMark);
    check(redo.body.configuration.sourceRevision === sourceB && redo.input.expectedDraftRevision === undo.body.draftRevision,
      '参数重做继续使用精确 CAS，来源 B 不被配置历史带回');
    await displayedFrame('parameter-redo-B', sourceB, redo.body.draftRevision);
    await recordDraft('parameter-redo-keeps-B', sourceB, sourceB);
  }

  stage = 'cleanup';
  await ui.click(page, 'button', '关闭草稿', { scope: '.draft-detail' });
  await ui.waitDialog(page, '关闭草稿', { partial: true });
  await ui.clickInDialog(page, '关闭');
  await page.waitFor(`!document.querySelector('.draft-detail')`, '关闭本次草稿');
  await network.wait((record) => record.method === 'DELETE' && record.path === `/api/v1/drafts/${draftId}` && record.status === 200, '删除本次草稿');
  draftId = undefined;
  await ui.click(page, `[role=row]:has([data-configuration-id="${configurationId}"])`, null);
  await page.waitFor(`document.querySelector('.component-detail')?.dataset.configurationId === ${JSON.stringify(configurationId)}`, '本次导入组件');
  await ui.menu(page, '组件操作', '删除…');
  await ui.clickInDialog(page, '删除');
  await page.waitFor(`!document.querySelector('.component-list [data-configuration-id="${configurationId}"]')`, '删除本次导入组件');
  configurationId = undefined;
  await chooseDevice(previousDevice);
  check(runtimeErrors.length === 0, '真实页面没有未处理异常');
  check(network.credentialUrls === 0, '浏览器请求 URL 没有凭证参数');
} catch (error) {
  failure = { sample, stage, errorType: error?.constructor?.name ?? 'Error', message: network?.safeText(error instanceof Error ? error.message : String(error))
    ?? '浏览器初始化失败', retainedDraftId: draftId, retainedConfigurationId: configurationId };
  process.exitCode = 1;
  if (page) await writeFile(join(output, 'failure.png'), await page.screenshot()).catch(() => undefined);
} finally {
  if (network) {
    await network.flush();
    network.write(output);
    network.listening = false;
  }
  if (browser) await browser.close().catch(() => { failure ??= { stage: 'browser-close', message: '本次 Chrome 关闭失败' }; process.exitCode = 1; });
  rmSync(assets, { recursive: true, force: true });
  if (failure) save('failure.json', failure);
  save('runtime-errors.json', runtimeErrors.map((message) => network?.safeText(message) ?? '<unavailable>'));
  const summary = () => ({ status: failure ? 'failed' : 'passed', sample, scenario: fullAuthor ? 'full-author' : 'edit-to-preview',
    sourcePath: sourceFile.name, packageId: manifest.packageId, exportId: entry.id, entryType: entry.entryType,
    checks, displayedFrames: displayed.length,
    retainedDraftId: draftId, retainedConfigurationId: configurationId,
    scope: `Finite real Chrome SDK2 ${sample} UI, source edit/save/compile/explicit adoption and actual decoded HTTP PNG identity. `
      + (fullAuthor ? 'Includes CPU named rejection, failed compilation and parameter undo/redo. '
        : 'Uses this sample manifest/source/parameter controls; no CPU rejection, failed compile or parameter edit/undo/redo in this narrower sample run. ')
      + 'Backend Vulkan is identified by real render-backend response and per-frame GPU echo; hardware DRM/process evidence belongs to companion HTTP probe. '
      + 'Identical content hashes prove pixel-content equality and do not uniquely identify a render invocation. '
      + 'No source-adoption undo/redo, full author validation, library reopen, shared texture, browser GPU acceleration, platform or performance acceptance.' });
  save('summary.json', summary());
  const files = (directory) => readdirSync(directory).flatMap((name) => { const path = join(directory, name); return statSync(path).isDirectory() ? files(path) : [path]; });
  let scanned = 0;
  let scanPassed = true;
  let removed = 0;
  for (const path of files(output)) {
    const data = readFileSync(path);
    if ([...(network?.secrets ?? [])].some((secret) => secret && data.includes(Buffer.from(secret)))) {
      process.exitCode = 1;
      scanPassed = false;
      // Do not print the path, credential or raw response in this failure.
      failure ??= { stage: 'credential-scan', message: '输出凭证扫描失败' };
      rmSync(path, { force: true });
      removed++;
    }
    scanned++;
  }
  if (failure) save('failure.json', failure);
  save('credential-scan.json', { scannedFiles: scanned, passed: scanPassed, removedContaminatedFiles: removed });
  save('summary.json', { ...summary(), credentialScanPassed: scanPassed });
  save('evidence-manifest.json', { files: Object.fromEntries(files(output).map((path) => [path.slice(output.length + 1), sha(readFileSync(path))])) });
  console.log(JSON.stringify({ status: failure ? 'failed' : 'passed', sample, assertions: checks.length, displayedFrames: displayed.length,
    retainedDraftId: draftId ?? null, retainedConfigurationId: configurationId ?? null }));
}
