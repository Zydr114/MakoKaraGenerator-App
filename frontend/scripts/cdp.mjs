// 最小 Chrome DevTools 协议客户端：启动 Chrome、打开页面、执行脚本、截图。无第三方依赖。
// browser-smoke.mjs 与 page-shot.mjs 共用。Chrome 路径：CHROME_PATH，缺省在 ~/.cache/ms-playwright 下查找。

import { spawn } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { join } from 'node:path';

export function findChrome() {
  if (process.env.CHROME_PATH) return process.env.CHROME_PATH;
  const root = join(homedir(), '.cache/ms-playwright');
  if (existsSync(root)) {
    for (const dir of readdirSync(root).filter((d) => d.startsWith('chromium-')).sort().reverse()) {
      const candidate = join(root, dir, 'chrome-linux64/chrome');
      if (existsSync(candidate)) return candidate;
    }
  }
  throw new Error('找不到 Chrome：请设置 CHROME_PATH。');
}

export class Cdp {
  constructor(ws) {
    this.ws = ws;
    this.nextId = 1;
    this.pending = new Map();
    this.listeners = [];
    // 连接关闭后仍在等待或新发出的请求必须立即失败；否则 Node 事件循环清空时调用方会静默以 0 退出。
    ws.addEventListener('close', () => {
      for (const { reject } of this.pending.values()) reject(new Error('CDP 连接已关闭'));
      this.pending.clear();
    });
    ws.addEventListener('message', (event) => {
      const message = JSON.parse(event.data);
      if (message.id && this.pending.has(message.id)) {
        const { resolve, reject } = this.pending.get(message.id);
        this.pending.delete(message.id);
        if (message.error) reject(new Error(`${message.error.message}`));
        else resolve(message.result);
      } else if (message.method) {
        for (const listener of this.listeners) listener(message);
      }
    });
  }
  static async connect(url) {
    const ws = new WebSocket(url);
    await new Promise((resolve, reject) => {
      ws.addEventListener('open', resolve, { once: true });
      ws.addEventListener('error', reject, { once: true });
    });
    return new Cdp(ws);
  }
  send(method, params = {}, sessionId) {
    if (this.ws.readyState !== WebSocket.OPEN) return Promise.reject(new Error('CDP 连接已关闭'));
    const id = this.nextId++;
    this.ws.send(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }));
    return new Promise((resolve, reject) => this.pending.set(id, { resolve, reject }));
  }
  on(listener) {
    this.listeners.push(listener);
  }
}

export class Page {
  constructor(cdp, sessionId) {
    this.cdp = cdp;
    this.sessionId = sessionId;
  }
  send(method, params) {
    return this.cdp.send(method, params, this.sessionId);
  }
  async eval(expression) {
    const result = await this.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
    if (result.exceptionDetails) throw new Error(`页面脚本异常：${result.exceptionDetails.exception?.description ?? result.exceptionDetails.text}`);
    return result.result.value;
  }
  async navigate(url) {
    await this.send('Page.navigate', { url });
    await this.waitFor(`document.readyState === 'complete'`, '页面加载');
  }
  async waitFor(expression, what, timeoutMs = 60_000) {
    const start = Date.now();
    for (;;) {
      const value = await this.eval(expression).catch(() => undefined);
      if (value) return value;
      if (Date.now() - start > timeoutMs) throw new Error(`等待超时：${what}`);
      await new Promise((r) => setTimeout(r, 100));
    }
  }

  async screenshot() {
    const { data } = await this.send('Page.captureScreenshot', { format: 'png' });
    return Buffer.from(data, 'base64');
  }
}

/** 启动一个独立 profile 的 Chrome；close() 结束进程并删除 profile。 */
export async function launchChrome({ headed = false } = {}) {
  const profile = mkdtempSync(join(tmpdir(), 'mako-chrome-'));
  const chrome = spawn(
    findChrome(),
    [
      ...(headed ? [] : ['--headless=new']),
      '--remote-debugging-port=0',
      `--user-data-dir=${profile}`,
      '--no-first-run',
      '--no-default-browser-check',
      '--disable-gpu',
      '--autoplay-policy=no-user-gesture-required',
      'about:blank',
    ],
    { stdio: ['ignore', 'ignore', 'pipe'] },
  );
  let chromeLog = '';
  chrome.stderr.on('data', (chunk) => (chromeLog += chunk));
  const close = async () => {
    if (chrome.exitCode === null) {
      chrome.kill('SIGTERM');
      await new Promise((r) => chrome.once('exit', r));
    }
    rmSync(profile, { recursive: true, force: true });
  };

  try {
    const portFile = join(profile, 'DevToolsActivePort');
    for (let i = 0; i < 100 && !existsSync(portFile); i++) await new Promise((r) => setTimeout(r, 100));
    if (!existsSync(portFile)) throw new Error(`Chrome 没有启动：\n${chromeLog.slice(-2000)}`);
    const [port, path] = readFileSync(portFile, 'utf8').trim().split('\n');
    const cdp = await Cdp.connect(`ws://127.0.0.1:${port}${path}`);

    async function openPage(url, { width = 1600, height = 1000 } = {}) {
      const { targetId } = await cdp.send('Target.createTarget', { url: 'about:blank' });
      const { sessionId } = await cdp.send('Target.attachToTarget', { targetId, flatten: true });
      const page = new Page(cdp, sessionId);
      await page.send('Page.enable');
      await page.send('Runtime.enable');
      await page.send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
      if (url) await page.navigate(url);
      return page;
    }

    return {
      cdp,
      openPage,
      close: async () => {
        cdp.ws.close();
        await close();
      },
    };
  } catch (error) {
    await close();
    throw error;
  }
}
