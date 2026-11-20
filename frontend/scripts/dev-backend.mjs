// pnpm dev:backend：启动真实 Backend.Server 供开发页面连接，前台运行直到 Ctrl+C。
// 机制见 scripts/backend-launcher.mjs 与 docs/web-client-w0-design.md §4.1。
//
// 可选参数：--dist <匹配发布目录>  --profile <持久 profile 目录>
// 环境变量：MAKO_BACKEND_DIST。

import { parseArgs } from 'node:util';
import { startBackend } from './backend-launcher.mjs';

const { values } = parseArgs({
  options: {
    dist: { type: 'string' },
    profile: { type: 'string' },
  },
});

let backend;
try {
  backend = await startBackend({ dist: values.dist, profile: values.profile });
} catch (error) {
  console.error(`[backend] 启动失败：${error instanceof Error ? error.message : error}`);
  process.exit(1);
}

console.log('[backend] 现在可以运行 pnpm dev 并打开 http://localhost:5173 。按 Ctrl+C 停止。');

let stopping = false;
async function shutdown(signal) {
  if (stopping) return;
  stopping = true;
  console.log(`[backend] 收到 ${signal}，等待 Server 排水退出……`);
  const code = await backend.stop();
  console.log(`[backend] 已退出（${code}），启动信息已删除。`);
  process.exit(code ?? 0);
}
for (const signal of ['SIGINT', 'SIGTERM', 'SIGHUP']) process.on(signal, () => void shutdown(signal));

backend.exited.then((code) => {
  if (stopping) return;
  console.error(`[backend] 后端意外退出（${code}）。启动信息已删除。`);
  process.exit(code || 1);
});
