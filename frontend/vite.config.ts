import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import { makoBootstrapPlugin } from './scripts/vite-bootstrap-plugin.ts';

// 开发服务器固定为 localhost:5173：dev-backend 会把这个 origin（及 127.0.0.1:5173）交给 Server 的 Origin 白名单。
// 页面直接跨源访问 Server，不使用 Vite proxy：开发期间就走真实的 CORS 预检与 Origin 校验，SSE 也不受代理缓冲影响。
// 开发用 bootstrap 插件只在 vite dev 中启用，见 docs/web-client-w0-design.md §4.2。
const appPort = Number(process.env.MAKO_APP_PORT ?? 5173);
const DEV_HOSTS = [`localhost:${appPort}`, `127.0.0.1:${appPort}`];

export default defineConfig({
  plugins: [
    react(),
    makoBootstrapPlugin({
      file: process.env.MAKO_APP_BOOTSTRAP_FILE ?? fileURLToPath(new URL('./.dev/backend.json', import.meta.url)),
      allowedHosts: DEV_HOSTS,
    }),
  ],
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
  },
});
