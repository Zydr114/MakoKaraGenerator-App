import { describe, expect, it } from 'vitest';
import { checkBootstrapRequest, type BootstrapRequestFacts } from '../../scripts/vite-bootstrap-plugin';

const ALLOWED = ['localhost:5173', '127.0.0.1:5173'];
const ok: BootstrapRequestFacts = {
  method: 'GET',
  remoteAddress: '127.0.0.1',
  host: 'localhost:5173',
  origin: undefined,
  secFetchSite: 'same-origin',
};

describe('开发 bootstrap 路由的请求门禁', () => {
  it('允许本机同源页面的 fetch', () => {
    expect(checkBootstrapRequest(ok, ALLOWED)).toBeNull();
    expect(checkBootstrapRequest({ ...ok, remoteAddress: '::1', origin: 'http://localhost:5173' }, ALLOWED)).toBeNull();
    expect(checkBootstrapRequest({ ...ok, remoteAddress: '::ffff:127.0.0.1', host: '127.0.0.1:5173' }, ALLOWED)).toBeNull();
  });

  it.each([
    ['非 GET', { method: 'POST' }, 405],
    ['非回环连接', { remoteAddress: '192.168.1.5' }, 403],
    ['DNS rebinding 的 Host', { host: 'evil.example:5173' }, 403],
    ['跨源 Origin', { origin: 'http://evil.example' }, 403],
    ['跨站请求', { secFetchSite: 'cross-site' }, 403],
    ['地址栏直接打开', { secFetchSite: 'none' }, 403],
    ['缺少 Sec-Fetch-Site', { secFetchSite: undefined }, 403],
  ] as const)('拒绝%s', (_name, patch, status) => {
    expect(checkBootstrapRequest({ ...ok, ...patch }, ALLOWED)?.status).toBe(status);
  });
});
