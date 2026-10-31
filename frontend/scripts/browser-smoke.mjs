// 浏览器冒烟：用真实 Chrome（DevTools 协议，无额外依赖）像用户一样点完控制台的 W0 步骤，
// 并检查 Node 测试覆盖不到的浏览器路径：CORS／Origin、开发 bootstrap 路由门禁、刷新后 resume。
// 不替代设计 §7 要求的人工检查，只是可重复的辅助验证。
//
// 前提：pnpm dev 已在运行；pnpm dev:backend 已在运行，或者传 --manage-backend 由本脚本启动并在最后
// 重启一次 Server，检查页面识别 hostRestarted、自动重新初始化并能重开持久工程（W0 出口 5）。
// 用法：node scripts/browser-smoke.mjs [--url http://localhost:5173]（开发服务器 origin，控制台在 /console.html） [--manage-backend] [--screenshot out.png] [--headed]
// Chrome 路径：CHROME_PATH，缺省时在 ~/.cache/ms-playwright 下查找。

import { parseArgs } from 'node:util';
import { writeFile } from 'node:fs/promises';
import { startBackend } from './backend-launcher.mjs';
import { launchChrome } from './cdp.mjs';

const { values } = parseArgs({
  options: {
    url: { type: 'string', default: 'http://localhost:5173' },
    headed: { type: 'boolean' },
    'manage-backend': { type: 'boolean' },
    screenshot: { type: 'string' },
  },
});
const ORIGIN = new URL(values.url).origin;
const PAGE_URL = `${ORIGIN}/console.html`;

// ── 控制台操作 ─────────────────────────────────────────

const ui = {
  chip: `document.querySelector('.chip')?.textContent ?? ''`,
  busy: `!!document.querySelector('.busy')`,
  error: `document.querySelector('.error-box')?.innerText ?? ''`,
  clientId: `[...document.querySelectorAll('.status-grid > div')].find(d => d.querySelector('dt')?.textContent === 'clientId')?.querySelector('dd')?.getAttribute('title') ?? ''`,
  clientOrigin: `[...document.querySelectorAll('.status-grid > div')].find(d => d.querySelector('dt')?.textContent === 'clientId')?.textContent ?? ''`,
};

async function click(page, text) {
  await page.waitFor(
    `[...document.querySelectorAll('button')].some(b => b.textContent.includes(${JSON.stringify(text)}) && !b.disabled)`,
    `按钮「${text}」可用`,
  );
  await page.eval(`[...document.querySelectorAll('button')].find(b => b.textContent.includes(${JSON.stringify(text)}) && !b.disabled).click()`);
  await new Promise((r) => setTimeout(r, 50));
  await page.waitFor(`!(${ui.busy})`, `「${text}」完成`, 120_000);
  const error = await page.eval(ui.error);
  if (error && !error.includes('no-op')) throw new Error(`「${text}」出错：${error}`);
}

const checks = [];
function check(ok, label) {
  checks.push({ ok: !!ok, label });
  console.log(`${ok ? '✓' : '✗'} ${label}`);
  if (!ok) process.exitCode = 1;
}

// ── 主流程 ────────────────────────────────────────────

const quiet = () => undefined;
let backend = values['manage-backend'] ? await startBackend({ log: quiet }) : undefined;

const browser = await launchChrome({ headed: values.headed });

try {
  const { cdp } = browser;
  const openPage = (url) => browser.openPage(url);

  const consoleErrors = [];
  cdp.on((message) => {
    if (message.method === 'Runtime.exceptionThrown') consoleErrors.push(message.params.exceptionDetails.exception?.description ?? 'exception');
    if (message.method === 'Runtime.consoleAPICalled' && message.params.type === 'error') {
      consoleErrors.push(message.params.args.map((a) => a.value ?? a.description).join(' '));
    }
  });

  // 1. 打开控制台并连接
  const page = await openPage(PAGE_URL);
  await page.waitFor(`(${ui.chip}) === '已连接'`, '连接 Server');
  check(true, '控制台在浏览器中通过 bootstrap 路由连上 Server（跨源 CORS 预检通过）');
  const firstClient = await page.eval(ui.clientId);
  await page.waitFor(`document.body.innerText.includes('事件流已连接') || document.body.innerText.includes('连接事件流')`, '事件流开始连接');

  // 2. W0 步骤
  await click(page, '生成示例素材');
  await click(page, '上传两个文件');
  check(await page.eval(`document.body.innerText.includes('示例歌词.lrc')`), '上传音频与中文文件名歌词');
  await click(page, '新建');
  check(await page.eval(`document.body.innerText.includes('修订 0')`), '新建工程（修订 0）');
  await click(page, '读取 primary 来源');
  check(await page.eval(`document.querySelectorAll('table.lines tbody tr').length === 3`), '读歌词：3 行');
  await click(page, 'setTimeRange');
  check(await page.eval(`document.querySelector('table.lines tbody tr')?.innerText.includes('0.500s → 1.800s')`), 'setTimeRange 后时间投影为 0.500s → 1.800s');
  await click(page, '撤销');
  check(await page.eval(`!document.querySelector('table.lines tbody tr')?.innerText.includes('0.500s → 1.800s')`), '撤销后第一行恢复为导入时的时间');
  await click(page, '重做');
  check(await page.eval(`document.body.innerText.includes('修订 3')`), '撤销／重做后修订为 3');
  await page.waitFor(`document.querySelectorAll('.event-entry.kind-event').length >= 3`, 'SSE 事件到达');
  check(true, 'SSE 在浏览器中收到 project.changed 等事件');

  await click(page, '渲染修订');
  const img = await page.waitFor(
    `(() => { const i = document.querySelector('.preview img'); return i && i.complete && i.naturalWidth ? { w: i.naturalWidth, h: i.naturalHeight } : null })()`,
    '预览图片解码',
  );
  check(img.w === 640 && img.h === 360, `静态预览 PNG 在浏览器中解码为 ${img.w}×${img.h}`);
  check(await page.eval(`document.querySelector('.preview figcaption')?.innerText.includes('✓ 一致')`), '预览帧 SHA-256 与元数据一致');

  await click(page, '保存');
  check(await page.eval(`document.body.innerText.includes('已保存')`), '保存到授权目录（job 成功终态）');
  await click(page, '关闭（requireClean）');
  check(await page.eval(`document.querySelector('.job')?.innerText.includes('succeeded')`), '关闭 job 成功');
  await click(page, '重开');
  check(await page.eval(`document.querySelectorAll('table.lines tbody tr').length === 3 && document.body.innerText.includes('已保存')`), '重开后歌词与保存状态一致');

  if (values.screenshot) {
    const { data } = await page.send('Page.captureScreenshot', { format: 'png' });
    await writeFile(values.screenshot, Buffer.from(data, 'base64'));
    console.log(`  截图已保存：${values.screenshot}`);
  }

  // 3. 游标缺口 → resync
  await click(page, '制造游标缺口');
  await page.waitFor(`document.body.innerText.includes('resync 完成')`, 'resync 完成');
  check(true, '人为游标缺口走完 resync');

  // 4. 刷新页面：resume 同一 client
  const savedPath = await page.eval(`document.querySelector('input[aria-label="相对目录"]').value`);
  await page.send('Page.reload');
  await page.waitFor(`document.readyState === 'complete'`, '刷新');
  await page.waitFor(`(${ui.chip}) === '已连接'`, '刷新后重新连接');
  const afterReload = await page.eval(ui.clientId);
  check(afterReload === firstClient && (await page.eval(ui.clientOrigin)).includes('resume'), `刷新后通过 resume 恢复同一 client（${afterReload}）`);

  // 5. bootstrap 路由门禁：直接在地址栏打开会被拒绝
  const direct = await openPage(`${ORIGIN}/__mako/bootstrap`);
  const directBody = await direct.eval('document.body.innerText');
  check(directBody.includes('只接受同源页面') && !directBody.includes('bootstrapToken'), '地址栏直接打开 /__mako/bootstrap 被拒绝，不显示令牌');

  // 6. Origin 校验：不在白名单的页面（opaque origin）读取 Server 被拒绝
  const baseUrl = await page.eval(`[...document.querySelectorAll('.status-grid dd')].map(d => d.textContent).find(t => t.startsWith('http://127.0.0.1:'))`);
  const foreign = await openPage('about:blank');
  const foreignResult = await foreign.eval(`fetch(${JSON.stringify(baseUrl + '/api/v1/health/live')}).then(r => 'status ' + r.status, e => 'blocked: ' + e.name)`);
  check(foreignResult.startsWith('blocked'), `非白名单 Origin 的页面读取 Server 被拒绝（${foreignResult}）`);

  // 7. Server 重启：页面识别 hostRestarted，自动重新初始化，并能从持久目录重开工程
  if (backend) {
    const hostOf = `[...document.querySelectorAll('.status-grid > div')].find(d => d.querySelector('dt')?.textContent === 'hostInstanceId')?.querySelector('dd')?.getAttribute('title') ?? ''`;
    const oldHost = await page.eval(hostOf);
    await backend.stop();
    backend = await startBackend({ log: quiet });
    await page.waitFor(`document.body.innerText.includes('连接身份作废')`, '页面识别 Server 重启', 60_000);
    await page.waitFor(`(${ui.chip}) === '已连接' && (${hostOf}) === ${JSON.stringify(backend.info.hostInstanceId)}`, '自动重新初始化到新 Server', 60_000);
    const newClient = await page.eval(ui.clientId);
    check(newClient !== firstClient && (await page.eval(hostOf)) !== oldHost, `Server 重启后页面自动重新初始化：新 host、新 client，不残留旧身份`);
    check(await page.eval(`document.querySelectorAll('table.lines tbody tr').length === 0`), '旧工程 ID 已随 reset 丢弃');
    // 页面刷新过，目录输入回到默认值：填回保存时的目录（React 受控输入需用原生 setter 并派发 input 事件）。
    await page.eval(`(() => {
      const input = document.querySelector('input[aria-label="相对目录"]');
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, ${JSON.stringify(savedPath)});
      input.dispatchEvent(new Event('input', { bubbles: true }));
    })()`);
    await click(page, '重开');
    check(await page.eval(`document.querySelectorAll('table.lines tbody tr').length === 3 && document.body.innerText.includes('已保存')`), '新 Server 上从授权目录重开持久工程');
  }

  check(consoleErrors.length === 0, `控制台页面没有未处理异常${consoleErrors.length ? `：${consoleErrors.join(' | ')}` : ''}`);
  console.log(`\n${checks.filter((c) => c.ok).length}/${checks.length} 项通过`);
} catch (error) {
  console.error(`✗ ${error instanceof Error ? error.message : error}`);
  process.exitCode = 1;
} finally {
  if (backend) await backend.stop();
  await browser.close();
}
