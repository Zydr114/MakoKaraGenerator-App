// 开发辅助：用真实 Chrome 打开页面，等条件成立后截图，并输出页面里的未处理异常。
// 用法：node scripts/page-shot.mjs <url> <out.png> [--wait <JS 表达式>] [--width 1440] [--height 900] [--delay 毫秒]
// 需要 pnpm dev（和 pnpm dev:backend，如果页面要连 Server）。

import { writeFile } from 'node:fs/promises';
import { parseArgs } from 'node:util';
import { launchChrome } from './cdp.mjs';

const { values, positionals } = parseArgs({
  allowPositionals: true,
  options: {
    wait: { type: 'string' },
    width: { type: 'string', default: '1440' },
    height: { type: 'string', default: '900' },
    delay: { type: 'string', default: '300' },
  },
});
const [url, out] = positionals;
if (!url || !out) {
  console.error('用法：node scripts/page-shot.mjs <url> <out.png> [--wait 表达式] [--width] [--height] [--delay]');
  process.exit(2);
}

const browser = await launchChrome();
try {
  const errors = [];
  browser.cdp.on((message) => {
    if (message.method === 'Runtime.exceptionThrown') errors.push(message.params.exceptionDetails.exception?.description ?? 'exception');
    if (message.method === 'Runtime.consoleAPICalled' && message.params.type === 'error') {
      errors.push(message.params.args.map((a) => a.value ?? a.description).join(' '));
    }
  });
  const page = await browser.openPage(url, { width: Number(values.width), height: Number(values.height) });
  if (values.wait) await page.waitFor(values.wait, values.wait, 60_000);
  await new Promise((r) => setTimeout(r, Number(values.delay)));
  await writeFile(out, await page.screenshot());
  console.log(`截图：${out}`);
  if (errors.length) {
    console.log(`页面异常 ${errors.length} 条：`);
    for (const error of errors) console.log(`  ${error}`);
    process.exitCode = 1;
  }
} finally {
  await browser.close();
}
