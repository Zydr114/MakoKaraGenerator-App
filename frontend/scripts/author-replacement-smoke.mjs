// 真实 Chrome 走作者草稿“替换包内图片”（合同 1.49.0）：导入 SDK v2 的 SpriteTrail→源码页签选 assets/spark.png→
// 换 GIF 被拒→换 32×24 PNG 看到尺寸提醒→保存修订（提交 replacedFiles 与所看修订）→编译、采用→试样帧变化→清理。
// 所有写操作都从界面发起，网络只旁观。需 pnpm dev:backend 与 pnpm dev；效果包取后端 tests/Fixtures/ScriptEffectsV2。
import { createHash } from 'node:crypto';
import { mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs';
import { writeFile } from 'node:fs/promises';
import { join, relative, resolve, sep } from 'node:path';
import { parseArgs } from 'node:util';
import { launchChrome } from './cdp.mjs';
import { resolveBackendRepo } from './backend-launcher.mjs';
import { zipFiles } from './effect-packages.mjs';
import { shell, ui } from './ui-query.mjs';
import { UiNetworkEvidence } from './ui-network-evidence.mjs';
import { makePng } from '../test/support/fixtures.ts';

const { values } = parseArgs({ options: { url: { type: 'string', default: 'http://127.0.0.1:5173' }, output: { type: 'string' }, headed: { type: 'boolean' } } });
const output = resolve(values.output ?? `.dev/smoke-author-replacement-${new Date().toISOString().replace(/[-:.]/g, '')}`);
mkdirSync(output, { recursive: false, mode: 0o700 });
const IMAGE = 'assets/spark.png';
const sha = (data) => createHash('sha256').update(data).digest('hex');
const files = { mkfx: join(output, 'SpriteTrail.mkfx'), png: join(output, 'orange-32x24.png'), gif: join(output, 'dot.gif') };
const png = makePng(32, 24, [255, 140, 0, 255]);
const sample = join(resolveBackendRepo(), 'tests', 'Fixtures', 'ScriptEffectsV2', 'SpriteTrail');
const list = (path) => statSync(path).isDirectory() ? readdirSync(path).sort().flatMap((entry) => list(join(path, entry))) : [path];
writeFileSync(files.mkfx, zipFiles(list(sample).map((path) => ({ name: relative(sample, path).split(sep).join('/'), data: readFileSync(path) }))));
writeFileSync(files.png, png);
writeFileSync(files.gif, Buffer.from('R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7', 'base64'));

const arg = JSON.stringify;
const checks = [], runtimeErrors = [];
const sampleSha = `document.querySelector('.template-sample [data-status=ready] img')?.dataset.sha`;
const shown = `document.querySelector('.draft-source')?.dataset.revision`;
const binaryText = `document.querySelector('.source-binary')?.textContent ?? ''`;
let browser, page, network, draftId, configurationId, failure, stage = 'initialize';
function check(condition, label) {
  checks.push({ passed: !!condition, label });
  if (!condition) throw new Error(label);
  console.log(`✓ ${label}`);
}

try {
  browser = await launchChrome({ headed: values.headed });
  page = await browser.openPage(undefined, { width: 1440, height: 1000 });
  network = new UiNetworkEvidence(page);
  browser.cdp.on((message) => { if (message.sessionId === page.sessionId && message.method === 'Runtime.exceptionThrown')
    runtimeErrors.push(message.params.exceptionDetails.exception?.description ?? message.params.exceptionDetails.text); });
  await network.enable();
  await page.send('DOM.enable');
  await page.navigate(new URL(values.url).origin + '/');
  await page.waitFor(shell.connected, '连接 Server');
  // SDK v2 试样只走 Vulkan；Chrome 每次用临时 profile，偏好不会留下。
  await ui.click(page, 'button', '（渲染设备）', { partial: true, scope: '.app-bar' });
  await ui.click(page, 'menuitem', 'GPU', { scope: '.render-device-menu' });
  await page.waitFor(`!document.querySelector('.render-device-menu')`, '设备菜单关闭');

  stage = 'import';
  await shell.goto(page, 'library');
  await ui.click(page, 'button', '效果组件', { scope: '.library-sections' });
  const ids = `[...document.querySelectorAll('.component-list [data-configuration-id]')].map((el) => el.dataset.configurationId)`;
  const prior = await page.eval(ids);
  await ui.setFile(page, '导入效果包…', files.mkfx);
  await ui.waitDialog(page, '导入效果包');
  await ui.clickInDialog(page, '导入');
  await ui.waitDialogClosed(page, '导入效果包');
  await page.waitFor(`${ids}.some((id) => !${arg(prior)}.includes(id))`, '新导入的组件');
  configurationId = await page.eval(`${ids}.find((id) => !${arg(prior)}.includes(id))`);
  await ui.click(page, `[role=row]:has([data-configuration-id=${arg(configurationId)}])`, null);
  await ui.click(page, 'button', '编辑', { scope: '.component-detail' });
  await page.waitFor(`!!document.querySelector('.draft-detail[data-draft-id]')`, '作者草稿');
  draftId = await page.eval(`document.querySelector('.draft-detail').dataset.draftId`);
  // 在用修订从界面实际收到的草稿响应里取。
  const inUse = (after = 0) => network.wait((record) => record.status < 300 && record.body?.draftId === draftId && record.body?.configuration, '草稿响应', after);
  const sourceA = (await inUse()).body.configuration.sourceRevision;
  const frameA = await page.waitFor(sampleSha, '初始试样帧', 120_000);
  await writeFile(join(output, '1-initial.png'), await page.screenshot());

  stage = 'replace';
  await ui.click(page, 'tab', '源码', { scope: '.draft-tabs' });
  await page.waitFor(`${shown} === ${arg(sourceA)}`, '源码页签读出在用修订');
  await ui.click(page, `.source-file[data-path=${arg(IMAGE)}]`, null);
  await page.waitFor(`document.querySelector('.source-binary')?.getAttribute('aria-label') === '包资源'`, '图片可替换');
  const original = await page.eval(binaryText);
  await ui.setFile(page, '替换…', files.gif);
  await page.waitFor(`(${binaryText}).includes('替换 spark.png 失败')`, '格式不同的说明', 30_000);
  check(!(await page.eval(`!!document.querySelector('.source-binary[data-replaced], .draft-source[data-dirty]')`)),
    'GIF 换 PNG 被拒，写明原因，不记待替换');
  await ui.setFile(page, '替换…', files.png);
  await page.waitFor(`!!document.querySelector('.source-binary[data-replaced] .source-replace-summary')`, '待保存的替换', 30_000);
  const summary = await page.eval(`document.querySelector('.source-replace-summary').textContent`);
  check(summary.includes('16×16') && summary.includes('32×24') && summary.includes(sha(png).slice(0, 12))
    && !!(await page.eval(`!!document.querySelector('.source-replace-warnings [data-code=imageSizeChanged]')`))
    && !!(await page.eval(`!!document.querySelector('.draft-source[data-dirty] .source-file[data-path=${arg(IMAGE)}][data-dirty]')`)),
  '待保存摘要写明 16×16→32×24、新摘要和尺寸提醒，文件标为已改');
  await writeFile(join(output, '2-pending.png'), await page.screenshot());

  stage = 'save';
  const before = network.mark();
  await ui.click(page, 'button', '保存修订', { scope: '.srcbar' });
  const saved = await network.wait((record) => record.method === 'PUT' && record.path === `/api/v1/drafts/${draftId}/package-files`
    && record.status === 200, '保存修订 PUT', before);
  const sourceB = saved.body.currentSourceRevision;
  check(saved.input.baseSourceRevision === sourceA && saved.input.replacedFiles?.length === 1 && saved.input.replacedFiles[0].path === IMAGE
    && sourceB !== sourceA && saved.body.configuration.sourceRevision === sourceA, '提交 replacedFiles 与所看修订，得到新修订，在用修订不变');
  await page.waitFor(`${shown} === ${arg(sourceB)} && !document.querySelector('.draft-source[data-dirty]')`, '读回新修订');
  const after = await page.eval(binaryText);
  check(after.includes(sha(png)) && after.includes(`${png.length} 字节`) && !after.includes('待保存') && after !== original,
    '新修订里图片是新字节，待替换已清掉');

  stage = 'compile-adopt';
  await ui.click(page, 'button', '编译查看的修订', { scope: '.srcbar' });
  await page.waitFor(`document.querySelector('.source-compile')?.dataset.outcome === 'passed'`, '编译通过', 180_000);
  const adoptMark = network.mark();
  await ui.click(page, 'button', '采用编译通过的修订', { partial: true, scope: '.source-compile' });
  await ui.waitDialog(page, '采用修订并配置', { partial: true });
  await ui.clickInDialog(page, '采用源码与配置');
  const adopted = await network.wait((record) => record.method === 'POST' && record.path === `/api/v1/drafts/${draftId}/adopt-source-revision`
    && record.status === 200, '采用新修订', adoptMark);
  check(adopted.input.sourceRevision === sourceB && adopted.body.adoptedSourceRevision === sourceB, '显式采用替换后的修订');
  const frameB = await page.waitFor(`(${sampleSha}) && (${sampleSha}) !== ${arg(frameA)} ? ${sampleSha} : null`, '试样帧更新', 120_000);
  check(frameB !== frameA, '采用后试样帧换成新图片渲染的结果');
  await writeFile(join(output, '3-adopted.png'), await page.screenshot());

  stage = 'cleanup';
  await ui.click(page, 'button', '关闭草稿', { scope: '.draft-detail' });
  await ui.waitDialog(page, '关闭草稿', { partial: true });
  await ui.clickInDialog(page, '关闭');
  await page.waitFor(`!document.querySelector('.draft-detail')`, '关闭草稿');
  draftId = undefined;
  await ui.click(page, `[role=row]:has([data-configuration-id=${arg(configurationId)}])`, null);
  await ui.menu(page, '组件操作', '删除…');
  await ui.clickInDialog(page, '删除');
  await page.waitFor(`!document.querySelector('.component-list [data-configuration-id=${arg(configurationId)}]')`, '删除组件');
  configurationId = undefined;
  check(runtimeErrors.length === 0, '页面没有未处理异常');
  check(network.credentialUrls === 0, '请求 URL 不带凭证');
} catch (error) {
  failure = { stage, message: network?.safeText(error instanceof Error ? error.message : String(error)) ?? String(error) };
  process.exitCode = 1;
  if (page) await writeFile(join(output, 'failure.png'), await page.screenshot()).catch(() => undefined);
} finally {
  if (network) { await network.flush(); network.write(output); network.listening = false; }
  if (browser) await browser.close().catch(() => undefined);
  const result = { status: failure ? 'failed' : 'passed', checks, failure, runtimeErrors: runtimeErrors.map((m) => network?.safeText(m) ?? m),
    retainedDraftId: draftId ?? null, retainedConfigurationId: configurationId ?? null };
  writeFileSync(join(output, 'summary.json'), JSON.stringify(result, null, 2) + '\n');
  console.log(JSON.stringify({ status: result.status, assertions: checks.length, output, ...(failure ? { failure } : {}) }));
}
