// Vite 开发插件：在开发服务器上提供 GET /__mako/bootstrap，读取 pnpm dev:backend 写出的 .dev/backend.json。
// 设计 §4.2，按后端审查 F2 收紧：
// - 只在 vite dev（apply: 'serve'）中存在，生产构建里没有这个路由；
// - 只接受本机回环连接、精确的 Host（防 DNS rebinding）、同源 fetch（Sec-Fetch-Site: same-origin，
//   直接在地址栏打开会被拒绝，令牌不会显示在标签页里）；
// - 响应 no-store、nosniff，不发任何 CORS 头；只返回 baseUrl／hostInstanceId／bootstrapToken。

import { readFile } from 'node:fs/promises';
import type { IncomingMessage, ServerResponse } from 'node:http';
import type { Plugin } from 'vite';

export const BOOTSTRAP_ROUTE = '/__mako/bootstrap';

export interface BootstrapRequestFacts {
  method: string | undefined;
  remoteAddress: string | undefined;
  host: string | undefined;
  origin: string | undefined;
  secFetchSite: string | undefined;
}

const LOOPBACK = new Set(['127.0.0.1', '::1', '::ffff:127.0.0.1']);

/** 返回 null 表示允许；否则返回拒绝的状态码与原因。纯函数，便于单元测试。 */
export function checkBootstrapRequest(
  facts: BootstrapRequestFacts,
  allowedHosts: readonly string[],
): { status: number; reason: string } | null {
  if (facts.method !== 'GET') return { status: 405, reason: '只允许 GET' };
  if (!facts.remoteAddress || !LOOPBACK.has(facts.remoteAddress)) return { status: 403, reason: '只接受本机回环连接' };
  if (!facts.host || !allowedHosts.includes(facts.host)) return { status: 403, reason: 'Host 不在允许列表中' };
  if (facts.origin !== undefined && facts.origin !== `http://${facts.host}`) return { status: 403, reason: 'Origin 与页面不同源' };
  if (facts.secFetchSite !== 'same-origin') return { status: 403, reason: '只接受同源页面的 fetch 请求' };
  return null;
}

export interface BootstrapPluginOptions {
  /** .dev/backend.json 的绝对路径。 */
  file: string;
  /** 允许的 Host 头，例如 localhost:5173、127.0.0.1:5173。 */
  allowedHosts: readonly string[];
}

export function makoBootstrapPlugin(options: BootstrapPluginOptions): Plugin {
  return {
    name: 'mako-dev-bootstrap',
    apply: 'serve',
    configureServer(server) {
      server.middlewares.use(BOOTSTRAP_ROUTE, (req: IncomingMessage, res: ServerResponse) => {
        void handle(req, res, options);
      });
    },
  };
}

async function handle(req: IncomingMessage, res: ServerResponse, options: BootstrapPluginOptions): Promise<void> {
  res.setHeader('Cache-Control', 'no-store');
  res.setHeader('X-Content-Type-Options', 'nosniff');
  const header = (name: string) => {
    const value = req.headers[name];
    return Array.isArray(value) ? value[0] : value;
  };
  const rejection = checkBootstrapRequest(
    {
      method: req.method,
      remoteAddress: req.socket.remoteAddress,
      host: header('host'),
      origin: header('origin'),
      secFetchSite: header('sec-fetch-site'),
    },
    options.allowedHosts,
  );
  if (rejection) return send(res, rejection.status, { error: rejection.reason });

  let info: { baseUrl?: unknown; hostInstanceId?: unknown; bootstrapToken?: unknown };
  try {
    info = JSON.parse(await readFile(options.file, 'utf8'));
  } catch {
    return send(res, 503, { error: '后端未启动：请先运行 pnpm dev:backend。' });
  }
  if (typeof info.baseUrl !== 'string' || typeof info.bootstrapToken !== 'string') {
    return send(res, 503, { error: '启动信息文件格式不正确。' });
  }
  send(res, 200, { baseUrl: info.baseUrl, hostInstanceId: info.hostInstanceId, bootstrapToken: info.bootstrapToken });
}

function send(res: ServerResponse, status: number, body: unknown): void {
  res.statusCode = status;
  res.setHeader('Content-Type', 'application/json; charset=utf-8');
  res.end(JSON.stringify(body));
}
