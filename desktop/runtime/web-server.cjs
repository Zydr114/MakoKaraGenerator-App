'use strict';

const { createReadStream } = require('node:fs');
const fs = require('node:fs');
const fsp = require('node:fs/promises');
const { createServer } = require('node:http');
const path = require('node:path');
const { handleDictProxyRequest } = require('./dict-proxy.cjs');
const privateFs = require('./private-fs.cjs');

const MIME_TYPES = Object.freeze({
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.webp': 'image/webp',
  '.ico': 'image/x-icon',
  '.json': 'application/json; charset=utf-8',
  '.woff2': 'font/woff2',
  '.wasm': 'application/wasm',
});

const LOOPBACK_ADDRS = new Set(['127.0.0.1', '::1', '::ffff:127.0.0.1']);

async function startStaticWebServer(options) {
  const distRoot = await fsp.realpath(path.resolve(options.distRoot));
  const requestedPort = options.port ?? 5180;
  const host = options.host ?? '127.0.0.1';
  const allowFallbackPort = options.allowFallbackPort ?? false;
  const getBootstrap = options.getBootstrap;
  const dictFetch = options.dictFetch;

  let actualPort = requestedPort;
  let allowedHosts = new Set([`127.0.0.1:${actualPort}`, `localhost:${actualPort}`]);

  const server = createServer((req, res) => {
    void (async () => {
      res.setHeader('X-Content-Type-Options', 'nosniff');
      if (!allowedHosts.has(req.headers.host) || !LOOPBACK_ADDRS.has(req.socket.remoteAddress)) {
        res.writeHead(403).end();
        return;
      }
      const pathname = new URL(req.url, `http://127.0.0.1:${actualPort}`).pathname;
      if (pathname === '/__mako/bootstrap') {
        res.setHeader('Cache-Control', 'no-store');
        const sameOrigin = req.headers.origin === undefined || req.headers.origin === `http://${req.headers.host}`;
        if (req.method !== 'GET' || req.headers['sec-fetch-site'] !== 'same-origin' || !sameOrigin) {
          res.writeHead(403).end();
          return;
        }
        const info = await getBootstrap();
        if (!info) {
          res.writeHead(503, { 'Content-Type': 'application/json; charset=utf-8' });
          res.end(JSON.stringify({ error: '后端尚未就绪' }));
          return;
        }
        res.setHeader('Content-Type', 'application/json; charset=utf-8');
        res.end(JSON.stringify({
          baseUrl: info.baseUrl,
          hostInstanceId: info.hostInstanceId,
          bootstrapToken: info.bootstrapToken,
        }));
        return;
      }
      if (pathname === '/__mako/dict-proxy') {
        res.setHeader('Cache-Control', 'no-store');
        const sameOrigin = req.headers.origin === undefined || req.headers.origin === `http://${req.headers.host}`;
        if (req.method !== 'GET' || req.headers['sec-fetch-site'] !== 'same-origin' || !sameOrigin) {
          res.writeHead(403).end();
          return;
        }
        await handleDictProxyRequest(req, res, { fetchImpl: dictFetch });
        return;
      }
      if (req.method !== 'GET' && req.method !== 'HEAD') {
        res.writeHead(405).end();
        return;
      }
      let target;
      try {
        target = path.resolve(distRoot, '.' + decodeURIComponent(pathname));
      } catch {
        res.writeHead(400).end();
        return;
      }
      if (!privateFs.isInsideRoot(distRoot, target) && !privateFs.isSamePath(target, distRoot)) {
        res.writeHead(403).end();
        return;
      }
      if (!path.extname(target)) {
        target = path.join(distRoot, 'index.html');
      }
      let realTarget;
      let info;
      try {
        realTarget = await fsp.realpath(target);
        if (!privateFs.isInsideRoot(distRoot, realTarget) && !privateFs.isSamePath(realTarget, distRoot)) {
          res.writeHead(403).end();
          return;
        }
        info = await fsp.stat(realTarget);
      } catch {
        res.writeHead(404).end();
        return;
      }
      if (!info.isFile()) {
        res.writeHead(404).end();
        return;
      }
      res.setHeader('Content-Type', MIME_TYPES[path.extname(realTarget)] ?? 'application/octet-stream');
      res.setHeader('Cache-Control', 'no-cache');
      res.setHeader('Content-Length', info.size);
      if (req.method === 'HEAD') {
        res.end();
      } else {
        createReadStream(realTarget)
          .on('error', () => {
            if (!res.headersSent) res.writeHead(500);
            res.end();
          })
          .pipe(res);
      }
    })().catch(() => {
      if (!res.headersSent) res.writeHead(500);
      res.end();
    });
  });

  const listen = (portToBind) =>
    new Promise((resolveListen, rejectListen) => {
      const onError = (error) => {
        server.off('listening', onListening);
        rejectListen(error);
      };
      const onListening = () => {
        server.off('error', onError);
        resolveListen();
      };
      server.once('error', onError);
      server.once('listening', onListening);
      server.listen(portToBind, host);
    });

  try {
    await listen(requestedPort);
  } catch (error) {
    if (allowFallbackPort && requestedPort !== 0 && error?.code === 'EADDRINUSE') {
      console.warn(`[web] 端口 ${requestedPort} 已被占用，改用随机 loopback 端口。`);
      await listen(0);
    } else {
      throw error;
    }
  }

  const address = server.address();
  if (!address || typeof address === 'string') {
    throw new Error('Static web server failed to bind a TCP port.');
  }
  actualPort = address.port;
  allowedHosts = new Set([`127.0.0.1:${actualPort}`, `localhost:${actualPort}`]);
  const origins = [`http://127.0.0.1:${actualPort}`, `http://localhost:${actualPort}`];

  const close = () =>
    new Promise((done) => {
      server.close(() => done());
      server.closeAllConnections?.();
    });

  return { server, port: actualPort, origins, close };
}

module.exports = { startStaticWebServer };
