// 真实 Electron 样式库升级回归；所有写入都在唯一 --session-name 的隔离 profile。
// pnpm smoke:desktop-library [--legacy-library PATH] [--port 5194] [--cdp-port 9252]
// 旧库只复制，不迁移；证据只记录文件数、聚合 hash、断言和新建测试模板截图。
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import { cp, lstat, mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import { createServer } from 'node:net';
import { join, resolve } from 'node:path';
import { isDeepStrictEqual, parseArgs } from 'node:util';
import { connectPage } from './gpu-preview-p3.mjs';
import { shell, ui } from '../frontend/scripts/ui-query.mjs';

const ROOT = resolve(import.meta.dirname, '..');
const { values } = parseArgs({ options: {
  'legacy-library': { type: 'string' }, port: { type: 'string', default: '5194' },
  'cdp-port': { type: 'string', default: '9252' },
} });
for (const key of ['port', 'cdp-port']) assert(Number.isInteger(Number(values[key])) && Number(values[key]) >= 1024 && Number(values[key]) <= 65535, '端口必须为 1024–65535');
assert(values.port !== values['cdp-port'], 'Web 和 CDP 端口必须不同');
const sessionName = `library-smoke-${Date.now().toString(36)}-${randomBytes(4).toString('hex')}`;
const base = join(ROOT, '.local/acceptance', sessionName);
const out = join(base, 'evidence');
const libraries = join(base, 'desktop-profile/state/libraries');
const legacy = join(libraries, 'templates');
const current = join(libraries, 'templates-v7/index.json');
const source = values['legacy-library'] ? resolve(values['legacy-library']) : null;
const testName = '桌面样式库回归';
const sleep = (ms) => new Promise((done) => setTimeout(done, ms));
const checks = [], runs = [];
let active, interrupted = false, baseline, safeScreenshot = false;
function check(ok, message) {
  assert(ok, message);
  checks.push(message);
  console.log(`✓ ${message}`);
}
async function writeJson(name, data) {
  await writeFile(join(out, name), JSON.stringify(data, null, 2) + '\n', { mode: 0o600 });
}
// 不跟随任何符号链接；完整相对路径参与 hash，但不打印私人文件名。
async function inventory(directory, prefix = '') {
  assert((await lstat(directory)).isDirectory() && !(await lstat(directory)).isSymbolicLink(), '旧库目录必须为普通目录');
  const files = [];
  for (const name of (await readdir(directory)).sort()) {
    const path = join(directory, name), relative = prefix + name, stat = await lstat(path);
    assert(!stat.isSymbolicLink(), '旧库不允许符号链接');
    if (stat.isDirectory()) files.push({ path: relative + '/', directory: true }, ...await inventory(path, relative + '/'));
    else {
      assert(stat.isFile(), '旧库只允许普通文件和目录');
      const bytes = await readFile(path);
      files.push({ path: relative, size: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex') });
    }
  }
  return files;
}
function digest(files) {
  return { fileCount: files.filter((file) => !file.directory).length, sha256: createHash('sha256').update(JSON.stringify(files)).digest('hex') };
}
async function verifyLegacy() {
  check(isDeepStrictEqual(await inventory(legacy), baseline), '隔离旧库所有文件字节及目录保持不变');
  if (source) check(isDeepStrictEqual(await inventory(source), baseline), '原始旧库所有文件字节及目录保持不变');
}
async function prepare() {
  await mkdir(out, { recursive: true, mode: 0o700 });
  await mkdir(libraries, { recursive: true, mode: 0o700 });
  if (source) {
    baseline = await inventory(source);
    await cp(source, legacy, { recursive: true, errorOnExist: true, force: false });
    check(isDeepStrictEqual(await inventory(legacy), baseline), '完整复制旧库到唯一隔离会话');
  } else {
    await mkdir(legacy, { mode: 0o700 });
    // 版本头隔离 fixture：单个无包静态模板，仅用于旧版本路径保护。
    // 实际旧库与其资源闭包的验收请传 --legacy-library，复制完整目录。
    await writeFile(join(legacy, 'index.json'), JSON.stringify({ schemaVersion: 6, packages: [], templates: [
      { id: 'legacy-smoke-fixture', name: '旧版回归样本', kind: 'Line', properties: ['fontSizePx'], style: { fontSizePx: 48 }, effectRecipes: [] },
    ] }, null, 2) + '\n', { mode: 0o600 });
    baseline = await inventory(legacy);
  }
  const index = JSON.parse(await readFile(join(legacy, 'index.json'), 'utf8'));
  check(index.schemaVersion === 6 && Array.isArray(index.templates) && index.templates.length > 0, '旧库为非空 schema6 样本');
  await writeJson('legacy-summary.json', { source: source ? 'external-copy' : 'generated-fixture', schemaVersion: 6, templateCount: index.templates.length, ...digest(baseline) });
}

// 只记录自己启动器及其后代的 PID/startTicks，不扫描或终止其他 App 会话。
async function identity(pid) {
  try {
    const stat = await readFile(`/proc/${pid}/stat`, 'utf8');
    return stat.slice(stat.lastIndexOf(')') + 2).split(' ')[19];
  } catch (error) { if (error.code === 'ENOENT' || error.code === 'ESRCH') return null; throw error; }
}
async function observe(run, pid = run.child.pid) {
  const ticks = await identity(pid);
  if (!ticks) return;
  if (run.owned.has(pid) && run.owned.get(pid) !== ticks) return;
  run.owned.set(pid, ticks);
  try {
    for (const child of (await readFile(`/proc/${pid}/task/${pid}/children`, 'utf8')).trim().split(/\s+/).filter(Boolean)) await observe(run, Number(child));
  } catch (error) { if (error.code !== 'ENOENT' && error.code !== 'ESRCH') throw error; }
}
async function signalOwned(run, pid, signal) {
  if (run.owned.get(pid) !== await identity(pid)) return;
  try { process.kill(pid, signal); } catch (error) { if (error.code !== 'ESRCH') throw error; }
}
async function drain(run, graceful = false) {
  if (!run) return;
  if (graceful && run.page) void run.page.eval('window.close()').catch(() => {});
  else if (run.child.exitCode === null && run.child.signalCode === null) await signalOwned(run, run.child.pid, 'SIGTERM');
  let ended = false;
  for (let i = 0; i < 600; i++) {
    ended = run.child.exitCode !== null || run.child.signalCode !== null;
    if (ended) break;
    await sleep(100);
  }
  if (!ended) {
    await signalOwned(run, run.child.pid, 'SIGTERM');
    for (let i = 0; i < 300 && run.child.exitCode === null && run.child.signalCode === null; i++) await sleep(100);
  }
  clearInterval(run.sampling);
  await run.observation;
  run.cdp?.ws.close();
  // 失败时也仅对本轮记录且身份仍相同的残留进程排水。
  for (const pid of [...run.owned.keys()].reverse()) await signalOwned(run, pid, 'SIGTERM');
  for (let i = 0; i < 150; i++) {
    if (!(await Promise.all([...run.owned].map(async ([pid, ticks]) => await identity(pid) === ticks))).some(Boolean)) break;
    await sleep(100);
  }
  for (const pid of [...run.owned.keys()].reverse()) await signalOwned(run, pid, 'SIGKILL');
  for (let i = 0; i < 50; i++) {
    if (!(await Promise.all([...run.owned].map(async ([pid, ticks]) => await identity(pid) === ticks))).some(Boolean)) break;
    await sleep(100);
  }
  const remaining = (await Promise.all([...run.owned].map(async ([pid, ticks]) => await identity(pid) === ticks))).filter(Boolean).length;
  run.drain = { exitCode: run.child.exitCode, signal: run.child.signalCode, observedProcessCount: run.owned.size, remainingProcessCount: remaining,
    cleanLauncherShutdown: run.log.includes('[app] 自有 Web 与后端进程已停止。') };
  if (active === run) active = null;
  check(!run.observationFailed, '本次启动器后代观察未发生错误');
  check(remaining === 0, '本次启动器及所有已观察后代完成排水');
  if (graceful) check(run.child.exitCode === 0 && run.drain.cleanLauncherShutdown, '关闭窗口后桌面启动器正常退出');
}
async function launch(number) {
  // 未占用检查避免连接到用户已有 CDP；连接后仍以启动配置中的 PID 验证身份。
  for (const port of [values.port, values['cdp-port']]) await new Promise((done, reject) => {
    const server = createServer();
    server.once('error', () => reject(new Error('验收端口已占用；请改用独立端口')));
    server.listen(Number(port), '127.0.0.1', () => server.close(done));
  });
  const run = { number, log: '', owned: new Map(), exceptions: 0, libraryHttpFailures: 0, loads: 0 };
  runs.push(run);
  run.child = spawn(process.execPath, [join(ROOT, 'scripts/app.mjs'), 'desktop', '--session-name', sessionName, '--port', values.port, '--cdp-port', values['cdp-port']],
    { cwd: ROOT, env: { ...process.env, AllowMissingPrunePackageData: 'true' }, stdio: ['ignore', 'pipe', 'pipe'] });
  active = run;
  run.child.on('error', () => { run.spawnFailed = true; });
  for (const stream of [run.child.stdout, run.child.stderr]) stream.on('data', (chunk) => { run.log += chunk; });
  run.observation = observe(run);
  run.sampling = setInterval(() => { run.observation = run.observation.then(() => observe(run)).catch(() => { run.observationFailed = true; }); }, 100);
  const deadline = Date.now() + 600_000;
  while (!run.log.includes('[app] 桌面页面')) {
    assert(!interrupted, '验收被中断');
    assert(!run.spawnFailed && run.child.exitCode === null && run.child.signalCode === null, '桌面启动失败；私有 profile 日志保留在隔离目录');
    assert(Date.now() < deadline, '桌面启动超时');
    await sleep(100);
  }
  ({ cdp: run.cdp, page: run.page } = await connectPage(Number(values['cdp-port'])));
  const launchConfig = JSON.parse(await readFile(join(ROOT, '.local/run', `desktop-${run.child.pid}.json`), 'utf8'));
  const mainPid = await run.page.eval('(async () => (await window.makoDesktopPreview.readPreviewState()).mainPid)()');
  assert(launchConfig.sessionName === sessionName && mainPid === launchConfig.mainPid, 'CDP 窗口必须属于本次隔离启动器');
  await observe(run);
  assert(run.owned.has(mainPid), '隔离 Electron 必须是本次启动器后代');
  run.cdp.on((event) => {
    if (event.method === 'Runtime.exceptionThrown') run.exceptions++;
    if (event.method === 'Page.loadEventFired') run.loads++;
    if (event.method === 'Network.responseReceived' && /\/api\/v1\/template-libraries\//.test(event.params.response.url) && event.params.response.status >= 400) run.libraryHttpFailures++;
  });
  await run.page.send('Network.enable');
  await run.page.send('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1000, deviceScaleFactor: 1, mobile: false });
  await run.page.waitFor(shell.connected, '隔离桌面连接 Server', 120_000);
  await run.page.eval(`(() => { window.__librarySmokeFailure = false; const check = () => { if (/读取[^\\n。]*模板库失败|模板库无法读取/.test(document.body.innerText)) window.__librarySmokeFailure = true; }; new MutationObserver(check).observe(document.body, { childList: true, subtree: true, characterData: true }); check(); })()`);
  await shell.goto(run.page, 'library');
  await run.page.waitFor(`!!document.querySelector('.library-list [data-template-id="builtin:line-classic"]')`, '样式库内置经典整行', 60_000);
  return run;
}
async function noFailures(run) {
  check(!await run.page.eval('window.__librarySmokeFailure') && !/读取[^\n。]*模板库失败|模板库无法读取/.test(run.log), '界面与启动日志无读取模板库失败');
  check(run.libraryHttpFailures === 0 && run.exceptions === 0, '模板库 HTTP 无失败且页面无未处理异常');
}
async function definition(page) {
  await ui.click(page, 'tab', '高级', { scope: '.template-tabs' });
  await page.waitFor(`!!document.querySelector('.template-json')`, '模板完整定义');
  return JSON.parse(await page.eval(`document.querySelector('.template-json').textContent`));
}
async function screenshot(page, name) {
  await ui.click(page, 'tab', '外观', { scope: '.template-tabs' });
  await page.waitFor(`document.querySelector('.template-sample [data-status=ready] img')?.naturalWidth > 0`, '辉光模板试样就绪', 120_000);
  await page.eval(`document.querySelector('[data-group="glow"]').scrollIntoView({ block: 'center' })`);
  await sleep(300);
  await writeFile(join(out, name), await page.screenshot(), { mode: 0o600 });
}
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => { interrupted = true; if (active) void signalOwned(active, active.child.pid, 'SIGTERM'); });
let failure;
try {
  await prepare();
  const first = await launch(1), page = first.page;
  await ui.click(page, '[role=row]:has([data-template-id="builtin:line-classic"])', null);
  await page.waitFor(`document.querySelector('.template-name')?.textContent === '经典整行'`, '选中经典整行');
  await ui.click(page, 'button', '复制并编辑');
  await ui.waitDialog(page, '复制到我的模板');
  await ui.fill(page, '名称', testName, { scope: '@dialog' });
  await ui.clickInDialog(page, '复制');
  await page.waitFor(`document.querySelector('.template-name')?.textContent === ${JSON.stringify(testName)}`, '复制到我的模板', 30_000);
  const id = await page.eval(`[...document.querySelectorAll('.library-list [data-template-id]')].find(el => el.textContent.includes(${JSON.stringify(testName)}))?.dataset.templateId`);
  check(typeof id === 'string' && !id.startsWith('builtin:'), '内置模板成功复制到当前个人库');
  const glowSwitch = '[data-group="glow"] [role="switch"]';
  await ui.wait(page, glowSwitch, null);
  if (!await page.eval(`document.querySelector(${JSON.stringify(glowSwitch)}).checked`)) await ui.click(page, glowSwitch, null);
  await ui.choose(page, '辉光开关', '有辉光');
  await ui.fill(page, '辉光颜色', '#ff20c0', { enter: true });
  await ui.fill(page, '扩张', '8', { scope: '[data-group="glow"]', enter: true });
  await ui.fill(page, '模糊', '18', { scope: '[data-group="glow"]', enter: true });
  const draft = await definition(page);
  check(isDeepStrictEqual(draft.style.glow?.color, { red: 255, green: 32, blue: 192, alpha: 255 }) && draft.style.glow?.spreadPx === 8 && draft.style.glow?.blurRadiusPx === 18, '辉光颜色、扩张和模糊写入草稿');
  await ui.click(page, 'button', '保存', { scope: '.template-actions' });
  await page.waitFor(`![...document.querySelectorAll('.template-detail .phead .tag')].some(el => el.textContent === '未保存')`, '辉光模板保存', 30_000);
  const saved = JSON.parse(await readFile(current, 'utf8'));
  check(saved.schemaVersion === 7 && saved.templates.length === 1 && saved.templates[0].id === id, '新写入 templates-v7 且不载入 schema6 旧模板');
  safeScreenshot = true;
  await screenshot(page, '1-saved-glow.png');
  await noFailures(first);
  await drain(first, true);
  await verifyLegacy();
  const second = await launch(2);
  await ui.click(second.page, `[role=row]:has([data-template-id=${JSON.stringify(id)}])`, null);
  await second.page.waitFor(`document.querySelector('.template-name')?.textContent === ${JSON.stringify(testName)}`, '同 profile 重启读回模板');
  const restored = await definition(second.page);
  check(isDeepStrictEqual(restored.style.glow, draft.style.glow) && ['glowSpec', 'glowColor', 'glowOpacity', 'glowBlurRadiusPx', 'glowSpreadPx'].every(key => restored.properties.includes(key)), '同 profile 重启后完整辉光与属性组保持');
  await screenshot(second.page, '2-restarted-glow.png');
  await noFailures(second);
  // 负例只破坏这个隔离会话的新库；始终恢复原始字节，再通过界面重试恢复。
  const currentBytes = await readFile(current);
  try {
    const damaged = JSON.parse(currentBytes);
    damaged.schemaVersion = 6;
    await writeFile(current, JSON.stringify(damaged), { mode: 0o600 });
    await sleep(300);
    const loadsBefore = second.loads;
    await second.page.send('Page.reload', { ignoreCache: true });
    const loadDeadline = Date.now() + 60_000;
    while (second.loads <= loadsBefore) { assert(Date.now() < loadDeadline, '负例页面刷新超时'); await sleep(100); }
    await second.page.waitFor(shell.connected, '负例刷新后连接 Server', 120_000);
    await shell.goto(second.page, 'library');
    await second.page.waitFor(`document.body.innerText.includes('格式不受支持或内容无效')`, '个人库错误说明', 60_000);
    await ui.click(second.page, '[role=row]:has([data-template-id="builtin:line-classic"])', null);
    await second.page.waitFor(`document.querySelector('.template-name')?.textContent === '经典整行'`, '个人库失败时内置仍可查看');
    check(await ui.read(second.page, 'button', '新建', 'return el.disabled || el.getAttribute("aria-disabled") === "true";', { scope: '.library-list' }) &&
      await ui.read(second.page, 'button', '复制并编辑', 'return el.disabled || el.getAttribute("aria-disabled") === "true";'), '个人库格式错误时内置可查看且新建、复制被禁用');
    await writeFile(join(out, '3-personal-error-builtin-available.png'), await second.page.screenshot(), { mode: 0o600 });
  } finally {
    await writeFile(current, currentBytes, { mode: 0o600 });
  }
  await ui.click(second.page, 'button', '重试', { scope: '.library-list' });
  await second.page.waitFor(`!document.body.innerText.includes('读取个人模板库失败') && !!document.querySelector('.library-list [data-template-id=' + ${JSON.stringify(JSON.stringify(id))} + ']')`, '还原新库后重试恢复', 60_000);
  await ui.click(second.page, `[role=row]:has([data-template-id=${JSON.stringify(id)}])`, null);
  check(isDeepStrictEqual((await definition(second.page)).style.glow, draft.style.glow), '还原隔离新库并重试后辉光仍保持');
  check((await readFile(current)).equals(currentBytes), '负例恢复隔离新库原始字节');
  check(second.exceptions === 0, '负例与恢复期间页面无未处理异常');
  second.expectedNegativeHttpFailures = second.libraryHttpFailures;
  await drain(second, true);
  await verifyLegacy();
} catch (error) {
  failure = error.message;
  if (safeScreenshot && active?.page && active.cdp?.ws.readyState === WebSocket.OPEN) await active.page.screenshot().then(bytes => writeFile(join(out, 'failure.png'), bytes, { mode: 0o600 })).catch(() => {});
  console.error(`✗ ${failure}`);
  process.exitCode = 1;
} finally {
  if (active) await drain(active).catch((error) => { failure ??= error.message; process.exitCode = 1; });
  if (baseline) await verifyLegacy().catch(() => { failure ??= '旧库保留检查失败'; process.exitCode = 1; });
  await writeJson('result.json', { passed: !failure, failure, checks, legacy: baseline ? digest(baseline) : null,
    runs: runs.map(run => ({ number: run.number, exceptions: run.exceptions, libraryHttpFailures: run.libraryHttpFailures, expectedNegativeHttpFailures: run.expectedNegativeHttpFailures ?? 0, observationFailed: !!run.observationFailed, drain: run.drain })) });
}
console.log(`证据：${out}`);
