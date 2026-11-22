import { existsSync, readdirSync, statSync } from 'node:fs';
import { createRequire } from 'node:module';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import type { Plugin } from 'vite';
import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import { checkBootstrapRequest, makoBootstrapPlugin } from './scripts/vite-bootstrap-plugin.ts';

const requireFromConfig = createRequire(import.meta.url);
const dictProxy = requireFromConfig('../desktop/runtime/dict-proxy.cjs') as {
  handleDictProxyRequest: (req: unknown, res: unknown) => Promise<void>;
};

/** 开发服务器同源词典下载代理：与生产 web-server.cjs 保持一致，避免开发期拉取 GitHub Releases 触发浏览器 CORS 拦截。 */
function makoDictProxyPlugin(allowedHosts: readonly string[]): Plugin {
  return {
    name: 'mako-dev-dict-proxy',
    apply: 'serve',
    configureServer(server) {
      server.middlewares.use('/__mako/dict-proxy', (req, res) => {
        const header = (name: string) => {
          const v = req.headers[name];
          return Array.isArray(v) ? v[0] : v;
        };
        const rejected = checkBootstrapRequest(
          {
            method: req.method,
            remoteAddress: req.socket.remoteAddress,
            host: header('host'),
            origin: header('origin'),
            secFetchSite: header('sec-fetch-site'),
          },
          allowedHosts,
        );
        if (rejected) {
          res.statusCode = rejected.status;
          res.setHeader('Content-Type', 'application/json; charset=utf-8');
          res.end(JSON.stringify({ error: rejected.reason }));
          return;
        }
        void dictProxy.handleDictProxyRequest(req, res);
      });
    },
  };
}

/**
 * AGPL v3 合规门禁：禁止将 @patdx/kuromoji 自带的 IPADIC 词典资产（dict/*.dat.gz）
 * 打入 Web 构建产物或桌面安装包；运行时仅消费用户按需下载/导入的 BSD-3-Clause NAIST-jdic 词典。
 */
function excludeKuromojiIpadicPlugin(): Plugin {
  const FORBIDDEN_IMPORT = /@patdx\/kuromoji\/dict(\/|$)/i;
  const FORBIDDEN_OUTPUT = /(?:^|[/\\])(?:kuromoji[/\\]dict|ipadic|.*\.dat\.gz$)/i;
  let outDir = fileURLToPath(new URL('./dist', import.meta.url));

  function assertNoIpadicInDir(dir: string): void {
    if (!existsSync(dir)) return;
    for (const entry of readdirSync(dir)) {
      const full = join(dir, entry);
      if (statSync(full).isDirectory()) {
        assertNoIpadicInDir(full);
      } else if (FORBIDDEN_OUTPUT.test(full)) {
        throw new Error(`构建产物违规包含 IPADIC/词典二进制资产：${full}`);
      }
    }
  }

  return {
    name: 'mako-exclude-kuromoji-ipadic',
    configResolved(config) {
      outDir = join(config.root, config.build.outDir);
    },
    resolveId(source) {
      if (FORBIDDEN_IMPORT.test(source)) {
        throw new Error(`禁止静态引用 @patdx/kuromoji/dict（IPADIC）：${source}`);
      }
      return null;
    },
    closeBundle() {
      assertNoIpadicInDir(outDir);
    },
  };
}

// 开发服务器固定为 localhost:5173：dev-backend 会把这个 origin（及 127.0.0.1:5173）交给 Server 的 Origin 白名单。
// 页面直接跨源访问 Server，不使用 Vite proxy：开发期间就走真实的 CORS 预检与 Origin 校验，SSE 也不受代理缓冲影响。
// 开发用 bootstrap 插件只在 vite dev 中启用，见 docs/web-client-w0-design.md §4.2。
const appPort = Number(process.env.MAKO_APP_PORT ?? 5173);
const DEV_HOSTS = [`localhost:${appPort}`, `127.0.0.1:${appPort}`];

export default defineConfig({
  plugins: [
    react(),
    excludeKuromojiIpadicPlugin(),
    makoBootstrapPlugin({
      file: process.env.MAKO_APP_BOOTSTRAP_FILE ?? fileURLToPath(new URL('./.dev/backend.json', import.meta.url)),
      allowedHosts: DEV_HOSTS,
    }),
    makoDictProxyPlugin(DEV_HOSTS),
  ],
  optimizeDeps: {
    exclude: ['@patdx/kuromoji/dict'],
  },
  server: {
    host: 'localhost',
    port: appPort,
    strictPort: true,
  },
  // 两个页面：/ 是编辑器，/console.html 是 W0 协议控制台（调试工具）。
  build: {
    rolldownOptions: {
      input: {
        editor: fileURLToPath(new URL('./index.html', import.meta.url)),
        console: fileURLToPath(new URL('./console.html', import.meta.url)),
      },
    },
  },
  test: {
    environment: 'node',
    // material-color-utilities 0.4 的 ESM 里有不带扩展名的相对导入，Node 直接加载会失败，交给 Vite 处理。
    server: { deps: { inline: ['@material/material-color-utilities'] } },
  },
});
