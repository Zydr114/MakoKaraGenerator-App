'use strict';

// 受限同源词典下载代理（供 web-server.cjs、Electron 主进程与 Vite 开发服务器共用）：
// - 解决浏览器/渲染进程直接 fetch GitHub Releases 时的 CORS 拦截（Failed to fetch）；
// - 严格限制仅允许下载 NAIST-jdic 词典白名单文件名（manifest.json / 12 个 .dat.gz / LICENSE.naist-jdic.txt），防止 SSRF；
// - 支持 Electron net.fetch（自动遵循系统代理/PAC）与纯 Node.js（自动遵循 HTTPS_PROXY / HTTP_PROXY 并透传流式进度）。

const http = require('node:http');
const https = require('node:https');
const { once } = require('node:events');
const tls = require('node:tls');

const ALLOWED_FILENAMES = new Set([
  'manifest.json',
  'LICENSE.naist-jdic.txt',
  'base.dat.gz',
  'check.dat.gz',
  'tid.dat.gz',
  'tid_pos.dat.gz',
  'tid_map.dat.gz',
  'cc.dat.gz',
  'unk.dat.gz',
  'unk_pos.dat.gz',
  'unk_map.dat.gz',
  'unk_char.dat.gz',
  'unk_compat.dat.gz',
  'unk_invoke.dat.gz',
]);

const LOOPBACK_HOSTS = new Set(['127.0.0.1', 'localhost', '[::1]', '::1']);
const MAX_REDIRECTS = 8;
const REQUEST_TIMEOUT_MS = 45_000;
const USER_AGENT = 'MakoKaraGenerator-DictDownloader/1.0';

function isBlockedHost(hostname) {
  const host = hostname.toLowerCase();
  // Block link-local / cloud metadata IPv4 range (169.254.0.0/16) and 0.0.0.0.
  if (host === '0.0.0.0' || host.startsWith('169.254.')) return true;
  return false;
}

/** 校验初始词典下载目标 URL（必须是 https 或本机回环 http，且路径末段位于白名单）。 */
function validateDictProxyUrl(rawUrl) {
  if (typeof rawUrl !== 'string' || !rawUrl.trim()) {
    return { ok: false, status: 400, error: '缺少 url 参数。' };
  }
  let parsed;
  try {
    parsed = new URL(rawUrl);
  } catch {
    return { ok: false, status: 400, error: '无效的词典 URL。' };
  }
  const isLoopback = LOOPBACK_HOSTS.has(parsed.hostname.toLowerCase());
  if (parsed.protocol !== 'https:' && !(parsed.protocol === 'http:' && isLoopback)) {
    return { ok: false, status: 403, error: '仅允许通过 HTTPS（或本机回环 HTTP）下载词典。' };
  }
  if (isBlockedHost(parsed.hostname)) {
    return { ok: false, status: 403, error: '目标主机不在允许范围内。' };
  }
  const basename = parsed.pathname.split('/').pop() || '';
  if (!ALLOWED_FILENAMES.has(basename)) {
    return { ok: false, status: 403, error: `不允许通过词典代理下载文件：${basename || '(empty)'}` };
  }
  return { ok: true, url: parsed };
}

function validateRedirectUrl(rawUrl, baseUrl) {
  let parsed;
  try {
    parsed = new URL(rawUrl, baseUrl);
  } catch {
    return null;
  }
  const isLoopback = LOOPBACK_HOSTS.has(parsed.hostname.toLowerCase());
  if (parsed.protocol !== 'https:' && !(parsed.protocol === 'http:' && isLoopback)) return null;
  if (isBlockedHost(parsed.hostname)) return null;
  return parsed;
}

function resolveProxyUrl(targetUrl) {
  if (LOOPBACK_HOSTS.has(targetUrl.hostname.toLowerCase())) return null;
  const raw =
    targetUrl.protocol === 'https:'
      ? process.env.HTTPS_PROXY || process.env.https_proxy || process.env.HTTP_PROXY || process.env.http_proxy
      : process.env.HTTP_PROXY || process.env.http_proxy;
  if (!raw || !raw.trim()) return null;
  try {
    const proxy = new URL(raw.trim());
    if (proxy.protocol === 'http:' || proxy.protocol === 'https:') return proxy;
  } catch {
    // Ignore malformed proxy env var.
  }
  return null;
}

function openConnectTunnel(proxyUrl, targetHost, targetPort, signal) {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(new Error('请求已取消。'));
      return;
    }
    const headers = { Host: `${targetHost}:${targetPort}` };
    if (proxyUrl.username || proxyUrl.password) {
      const cred = Buffer.from(`${decodeURIComponent(proxyUrl.username)}:${decodeURIComponent(proxyUrl.password)}`).toString('base64');
      headers['Proxy-Authorization'] = `Basic ${cred}`;
    }
    const proxyClient = proxyUrl.protocol === 'https:' ? https : http;
    const req = proxyClient.request({
      host: proxyUrl.hostname,
      port: proxyUrl.port || (proxyUrl.protocol === 'https:' ? 443 : 80),
      method: 'CONNECT',
      path: `${targetHost}:${targetPort}`,
      headers,
      timeout: REQUEST_TIMEOUT_MS,
    });
    const onAbort = () => {
      req.destroy();
      reject(new Error('请求已取消。'));
    };
    signal?.addEventListener('abort', onAbort, { once: true });
    req.on('connect', (res, socket) => {
      signal?.removeEventListener('abort', onAbort);
      if (res.statusCode !== 200) {
        socket.destroy();
        reject(new Error(`代理 CONNECT 失败（HTTP ${res.statusCode}）。`));
        return;
      }
      resolve(socket);
    });
    req.on('timeout', () => {
      signal?.removeEventListener('abort', onAbort);
      req.destroy();
      reject(new Error('代理连接超时。'));
    });
    req.on('error', (err) => {
      signal?.removeEventListener('abort', onAbort);
      reject(err);
    });
    req.end();
  });
}

function nodeRequestOnce(targetUrl, signal) {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(new Error('请求已取消。'));
      return;
    }
    const proxyUrl = resolveProxyUrl(targetUrl);
    const headers = {
      'User-Agent': USER_AGENT,
      Accept: '*/*',
    };

    const launch = async () => {
      let req;
      if (proxyUrl && targetUrl.protocol === 'https:') {
        const targetPort = Number(targetUrl.port) || 443;
        const tunnelSocket = await openConnectTunnel(proxyUrl, targetUrl.hostname, targetPort, signal);
        const tlsSocket = tls.connect({
          socket: tunnelSocket,
          servername: targetUrl.hostname,
        });
        req = https.request({
          hostname: targetUrl.hostname,
          port: targetPort,
          path: `${targetUrl.pathname}${targetUrl.search}`,
          method: 'GET',
          headers,
          createConnection: () => tlsSocket,
          timeout: REQUEST_TIMEOUT_MS,
        });
      } else if (proxyUrl && targetUrl.protocol === 'http:') {
        const proxyClient = proxyUrl.protocol === 'https:' ? https : http;
        req = proxyClient.request({
          hostname: proxyUrl.hostname,
          port: proxyUrl.port || (proxyUrl.protocol === 'https:' ? 443 : 80),
          path: targetUrl.toString(),
          method: 'GET',
          headers: { ...headers, Host: targetUrl.host },
          timeout: REQUEST_TIMEOUT_MS,
        });
      } else {
        const client = targetUrl.protocol === 'https:' ? https : http;
        req = client.request(targetUrl, {
          method: 'GET',
          headers,
          timeout: REQUEST_TIMEOUT_MS,
        });
      }

      const onAbort = () => {
        req.destroy();
        reject(new Error('请求已取消。'));
      };
      signal?.addEventListener('abort', onAbort, { once: true });
      req.on('response', (upstreamRes) => {
        signal?.removeEventListener('abort', onAbort);
        resolve(upstreamRes);
      });
      req.on('timeout', () => {
        signal?.removeEventListener('abort', onAbort);
        req.destroy();
        reject(new Error('下载请求超时。'));
      });
      req.on('error', (err) => {
        signal?.removeEventListener('abort', onAbort);
        reject(err);
      });
      req.end();
    };

    launch().catch(reject);
  });
}

async function nodeFetchWithRedirects(initialUrl, signal) {
  let current = initialUrl;
  for (let hop = 0; hop <= MAX_REDIRECTS; hop++) {
    const upstreamRes = await nodeRequestOnce(current, signal);
    const status = upstreamRes.statusCode ?? 0;
    const loc = upstreamRes.headers.location;
    if (status >= 300 && status < 400 && loc) {
      upstreamRes.resume();
      const next = validateRedirectUrl(loc, current);
      if (!next) {
        throw new Error('上游重定向目标不安全或无效。');
      }
      current = next;
      continue;
    }
    return upstreamRes;
  }
  throw new Error('下载重定向次数过多。');
}

function sendJson(res, status, payload) {
  if (res.headersSent) return;
  res.writeHead(status, {
    'Content-Type': 'application/json; charset=utf-8',
    'Cache-Control': 'no-store',
    'X-Content-Type-Options': 'nosniff',
    'X-Mako-Dict-Proxy': '1',
  });
  res.end(JSON.stringify(payload));
}

/**
 * 处理 GET /__mako/dict-proxy?url=<encoded> 请求，将上游词典文件流式透传给同源前端。
 * @param {import('node:http').IncomingMessage} req
 * @param {import('node:http').ServerResponse} res
 * @param {{ fetchImpl?: typeof fetch }} [options]
 */
async function handleDictProxyRequest(req, res, options = {}) {
  res.setHeader('Cache-Control', 'no-store');
  res.setHeader('X-Content-Type-Options', 'nosniff');

  if (req.method !== 'GET') {
    sendJson(res, 405, { error: '只允许 GET 请求。' });
    return;
  }

  let parsedReqUrl;
  try {
    parsedReqUrl = new URL(req.url, 'http://127.0.0.1');
  } catch {
    sendJson(res, 400, { error: '请求路径无效。' });
    return;
  }

  const check = validateDictProxyUrl(parsedReqUrl.searchParams.get('url'));
  if (!check.ok) {
    sendJson(res, check.status, { error: check.error });
    return;
  }

  const abort = new AbortController();
  const onClose = () => {
    if (!res.writableEnded) abort.abort();
  };
  req.on('close', onClose);

  try {
    if (typeof options.fetchImpl === 'function') {
      const upstream = await options.fetchImpl(check.url.toString(), {
        method: 'GET',
        redirect: 'follow',
        headers: { 'User-Agent': USER_AGENT },
        signal: abort.signal,
      });
      const outHeaders = {
        'Content-Type': upstream.headers.get('content-type') || 'application/octet-stream',
        'Cache-Control': 'no-store',
        'X-Content-Type-Options': 'nosniff',
        'X-Mako-Dict-Proxy': '1',
      };
      const contentLength = upstream.headers.get('content-length');
      if (contentLength) outHeaders['Content-Length'] = contentLength;
      res.writeHead(upstream.status, outHeaders);
      if (upstream.body && typeof upstream.body.getReader === 'function') {
        const reader = upstream.body.getReader();
        try {
          while (true) {
            const { done, value } = await reader.read();
            if (done || res.writableEnded || abort.signal.aborted) break;
            if (value && value.byteLength > 0) {
              const ok = res.write(Buffer.from(value.buffer, value.byteOffset, value.byteLength));
              if (!ok && !res.writableEnded) {
                await once(res, 'drain');
              }
            }
          }
        } finally {
          reader.releaseLock();
          if (!res.writableEnded) res.end();
        }
      } else {
        const buf = Buffer.from(await upstream.arrayBuffer());
        res.end(buf);
      }
      return;
    }

    const upstreamRes = await nodeFetchWithRedirects(check.url, abort.signal);
    const outHeaders = {
      'Content-Type': upstreamRes.headers['content-type'] || 'application/octet-stream',
      'Cache-Control': 'no-store',
      'X-Content-Type-Options': 'nosniff',
      'X-Mako-Dict-Proxy': '1',
    };
    if (upstreamRes.headers['content-length']) {
      outHeaders['Content-Length'] = upstreamRes.headers['content-length'];
    }
    res.writeHead(upstreamRes.statusCode ?? 502, outHeaders);
    upstreamRes.on('error', () => {
      if (!res.writableEnded) res.end();
    });
    upstreamRes.pipe(res);
  } catch (error) {
    if (abort.signal.aborted) return;
    const message = error instanceof Error ? error.message : String(error);
    sendJson(res, 502, { error: `代理拉取词典失败：${message}` });
  } finally {
    req.off('close', onClose);
  }
}

module.exports = {
  ALLOWED_FILENAMES,
  validateDictProxyUrl,
  handleDictProxyRequest,
};
