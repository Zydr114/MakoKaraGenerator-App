// 只观察浏览器实际发出的请求。凭证仅在内存中用于脱敏/扫描，不保留请求头或原始 CDP 消息。
import { createHash } from 'node:crypto';
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';

const sha = (value) => createHash('sha256').update(value).digest('hex');
const PUBLIC_PATH = /^\/api\/v1\/(?:drafts(?:\/|$)|jobs\/|preview-sessions(?:\/|$)|preview-frames\/|render-backends$|script-configurations\/commands$|runtime-info$)/;

export class UiNetworkEvidence {
  constructor(page) {
    this.page = page;
    this.sequence = 0;
    this.requests = new Map();
    this.records = [];
    this.pngs = new Map();
    this.responseBytes = new Map();
    this.pending = new Set();
    this.secrets = new Set();
    this.credentialUrls = 0;
    this.captureErrors = [];
    this.listening = true;
    page.cdp.on((message) => {
      if (!this.listening || message.sessionId !== page.sessionId) return;
      const p = message.params;
      if (message.method === 'Fetch.requestPaused') {
        const task = this.captureResponse(p).catch(() => {
          this.captureErrors.push({ sequence: this.requests.get(p.networkId)?.sequence, error: 'interceptedResponseUnavailable' });
        });
        this.pending.add(task);
        void task.finally(() => this.pending.delete(task));
      } else if (message.method === 'Network.requestWillBeSent') this.request(p);
      else if (message.method === 'Network.responseReceived') {
        const entry = this.requests.get(p.requestId);
        if (entry) entry.response = { status: p.response.status, mimeType: p.response.mimeType };
      } else if (message.method === 'Network.loadingFinished') {
        const task = this.complete(p.requestId).catch(() => {
          this.captureErrors.push({ sequence: this.requests.get(p.requestId)?.sequence, error: 'responseBodyUnavailable' });
        });
        this.pending.add(task);
        void task.finally(() => this.pending.delete(task));
      } else if (message.method === 'Network.loadingFailed') this.requests.delete(p.requestId);
    });
  }

  async enable() {
    await this.page.send('Network.enable', { maxTotalBufferSize: 32 * 1024 * 1024, maxResourceBufferSize: 4 * 1024 * 1024 });
    // Blob responses may be absent from Network's body cache. Read the original
    // response while paused, then release it unchanged. No duplicate HTTP request.
    // This observation changes delivery timing and is not performance evidence.
    await this.page.send('Fetch.enable', { patterns: [{ urlPattern: '*/api/v1/preview-frames/*/content*', requestStage: 'Response' }] });
  }

  async captureResponse(p) {
    try {
      if (p.request.method === 'GET' && p.responseStatusCode === 200 && p.networkId) {
        const body = await this.page.send('Fetch.getResponseBody', { requestId: p.requestId });
        const bytes = Buffer.from(body.body, body.base64Encoded ? 'base64' : 'utf8');
        if (bytes.length > 4 * 1024 * 1024) throw new Error('Observed preview body exceeds evidence budget.');
        this.responseBytes.set(p.networkId, bytes);
      }
    } finally {
      await this.page.send('Fetch.continueResponse', { requestId: p.requestId });
    }
  }

  request(p) {
    const url = new URL(p.request.url);
    if ([...url.searchParams.keys()].some((key) => /token|credential|authorization|secret/i.test(key))) this.credentialUrls++;
    for (const [key, value] of Object.entries(p.request.headers ?? {})) {
      if (key.toLowerCase() === 'authorization') this.secrets.add(String(value).replace(/^Bearer\s+/i, ''));
    }
    let input;
    if (p.request.postData?.trimStart().startsWith('{')) {
      try {
        input = JSON.parse(p.request.postData);
        this.collectSecrets(input);
      } catch { /* binary uploads and non-JSON requests are not retained */ }
    }
    this.requests.set(p.requestId, { sequence: ++this.sequence, method: p.request.method, path: url.pathname,
      input: PUBLIC_PATH.test(url.pathname) ? this.publicValue(input) : undefined,
      capture: PUBLIC_PATH.test(url.pathname), credentialResponse: url.pathname === '/__mako/bootstrap' || url.pathname === '/api/v1/clients' });
  }

  collectSecrets(value) {
    if (!value || typeof value !== 'object') return;
    for (const [key, item] of Object.entries(value)) {
      if (/^(?:access|resume|bootstrap)Token$/i.test(key) && typeof item === 'string') this.secrets.add(item);
      else this.collectSecrets(item);
    }
  }

  safeText(value) {
    let text = String(value);
    for (const secret of [...this.secrets].filter(Boolean).sort((a, b) => b.length - a.length)) text = text.split(secret).join('<redacted>');
    return text;
  }

  publicValue(value) {
    if (Array.isArray(value)) return value.map((item) => this.publicValue(item));
    if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).map(([key, item]) => {
      if (/token|authorization|credential/i.test(key)) return [key, '<redacted>'];
      if (key === 'text' && typeof item === 'string') return ['textSha256', sha(item)];
      return [key, this.publicValue(item)];
    }));
    return typeof value === 'string' ? this.safeText(value) : value;
  }

  async complete(requestId) {
    const entry = this.requests.get(requestId);
    if (!entry?.response || (!entry.capture && !entry.credentialResponse)) {
      this.requests.delete(requestId);
      return;
    }
    const json = entry.response.mimeType?.includes('json');
    const png = entry.method === 'GET' && /^\/api\/v1\/preview-frames\/[^/]+\/content$/.test(entry.path) && entry.response.status === 200;
    if (!json && !png) {
      this.requests.delete(requestId);
      return;
    }
    let bytes = this.responseBytes.get(requestId);
    this.responseBytes.delete(requestId);
    if (!bytes) {
      const result = await this.page.send('Network.getResponseBody', { requestId });
      bytes = Buffer.from(result.body, result.base64Encoded ? 'base64' : 'utf8');
    }
    let body;
    if (json) {
      const parsed = JSON.parse(bytes.toString('utf8'));
      this.collectSecrets(parsed);
      if (entry.capture) body = this.publicValue(parsed);
    }
    if (entry.capture) {
      const record = { sequence: entry.sequence, method: entry.method, path: entry.path, input: entry.input,
        status: entry.response.status, body };
      if (png) {
        const hash = sha(bytes);
        this.pngs.set(hash, bytes);
        record.download = { sha256: hash, sizeBytes: bytes.length,
          width: bytes.length >= 24 ? bytes.readUInt32BE(16) : null, height: bytes.length >= 24 ? bytes.readUInt32BE(20) : null,
          pngSignatureValid: bytes.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10])) };
      }
      this.records.push(record);
    }
    this.requests.delete(requestId);
  }

  mark() { return this.sequence; }
  matching(predicate, after = 0) { return this.records.filter((record) => record.sequence > after && predicate(record)).sort((a, b) => a.sequence - b.sequence); }
  latest(predicate, after = 0) { return this.matching(predicate, after).at(-1); }
  draft(id) { return this.latest((record) => record.status < 300 && record.body?.draftId === id && record.body?.configuration)?.body; }

  async wait(predicate, what, after = 0, timeout = 120_000) {
    const deadline = Date.now() + timeout;
    while (Date.now() < deadline) {
      const result = this.latest(predicate, after);
      if (result) return result;
      await new Promise((resolve) => setTimeout(resolve, 100));
    }
    throw new Error(`等待网络证据超时：${what}`);
  }

  async flush() {
    await Promise.race([Promise.allSettled([...this.pending]), new Promise((resolve) => setTimeout(resolve, 10_000))]);
  }

  write(output) {
    const records = [...this.records].sort((a, b) => a.sequence - b.sequence).map((record) => this.publicValue(record));
    writeFileSync(join(output, 'network.json'), JSON.stringify({ records, credentialUrls: this.credentialUrls,
      captureErrors: this.captureErrors }, null, 2) + '\n');
    for (const [hash, bytes] of this.pngs) writeFileSync(join(output, `network-frame-${hash}.png`), bytes);
  }
}
