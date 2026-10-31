import { describe, expect, it } from 'vitest';
import { BackendError, HttpTransport, NetworkError, redact, type ExchangeRecord } from '../../src/client';
import { createFakeFetch, json, problem } from '../support/fake-fetch';

function setup() {
  const fake = createFakeFetch();
  const records: ExchangeRecord[] = [];
  const transport = new HttpTransport({
    baseUrl: 'http://127.0.0.1:4000',
    fetch: fake.fetch,
    authHeaders: (kind) =>
      kind === 'access'
        ? { Authorization: 'Bearer secret-access', 'X-Mako-Client-Id': 'client-1', 'X-Mako-Host-Instance-Id': 'host-A' }
        : kind === 'bootstrap'
          ? { Authorization: 'Bearer secret-boot' }
          : {},
    observer: (record) => records.push(structuredClone(record)),
  });
  return { fake, transport, records };
}

describe('HttpTransport', () => {
  it('拼接 Server 地址并附加认证头；health 不带凭证', async () => {
    const { fake, transport } = setup();
    fake.route('GET', '/api/v1/capabilities', () => json(200, { ok: true }));
    fake.route('GET', '/api/v1/health/live', () => json(200, { status: 'live' }));
    await transport.request({ method: 'GET', path: '/api/v1/capabilities', auth: 'access' });
    await transport.request({ method: 'GET', path: '/api/v1/health/live', auth: 'none' });
    expect(fake.calls[0]?.url.href).toBe('http://127.0.0.1:4000/api/v1/capabilities');
    expect(fake.calls[0]?.headers.get('authorization')).toBe('Bearer secret-access');
    expect(fake.calls[0]?.headers.get('x-mako-client-id')).toBe('client-1');
    expect(fake.calls[1]?.headers.get('authorization')).toBeNull();
  });

  it('非 2xx 解析 BackendErrorResponse，保留 code／field／failureCode', async () => {
    const { fake, transport } = setup();
    fake.route('POST', '/x', () => problem(422, 'taskExecutionFailed', '失败', { field: 'audio', failureCode: 'WorkerAudioPreparationTimedOut' }));
    const error = await transport.request({ method: 'POST', path: '/x', auth: 'access', json: {} }).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(BackendError);
    expect(error).toMatchObject({ status: 422, code: 'taskExecutionFailed', field: 'audio', failureCode: 'WorkerAudioPreparationTimedOut' });
  });

  it('416 等无 JSON 正文的状态单独构造错误，不解析正文', async () => {
    const { fake, transport } = setup();
    fake.route('GET', '/bin', () => new Response(null, { status: 416, headers: { 'Content-Range': 'bytes */10' } }));
    const error = await transport.request({ method: 'GET', path: '/bin', auth: 'access', response: 'blob' }).catch((e: unknown) => e);
    expect(error).toMatchObject({ status: 416, code: null });
    const ok = await transport.request({ method: 'GET', path: '/bin', auth: 'access', response: 'blob', okStatuses: [416] });
    expect(ok).toMatchObject({ status: 416, data: null });
  });

  it('fetch 失败映射为 NetworkError；调用方中止保持 AbortError', async () => {
    const { fake, transport } = setup();
    fake.route('GET', '/down', () => {
      throw new TypeError('fetch failed');
    });
    await expect(transport.request({ method: 'GET', path: '/down', auth: 'access' })).rejects.toBeInstanceOf(NetworkError);
    const controller = new AbortController();
    controller.abort();
    await expect(transport.request({ method: 'GET', path: '/down', auth: 'access', signal: controller.signal })).rejects.toMatchObject({
      name: 'AbortError',
    });
  });

  it('超时映射为 NetworkError', async () => {
    const { fake, transport } = setup();
    fake.route('GET', '/slow', (req) => new Promise((_, reject) => req.signal?.addEventListener('abort', () => reject(req.signal?.reason))));
    await expect(transport.request({ method: 'GET', path: '/slow', auth: 'access', timeoutMs: 20 })).rejects.toBeInstanceOf(NetworkError);
  });

  it('拒绝把凭证发往其他 origin，拒绝 URL 中的凭证参数', async () => {
    const { transport } = setup();
    await expect(transport.request({ method: 'GET', path: 'http://evil.example/steal', auth: 'access' })).rejects.toThrow(/Server 以外/);
    await expect(transport.request({ method: 'GET', path: '/x', query: { access_token: 'a' }, auth: 'access' })).rejects.toThrow(/凭证参数/);
  });

  it('请求日志里凭证已打码', async () => {
    const { fake, transport, records } = setup();
    fake.route('POST', '/api/v1/clients', () =>
      json(201, { clientId: 'c', accessToken: 'secret-new-access', resumeToken: 'secret-resume' }),
    );
    await transport.request({ method: 'POST', path: '/api/v1/clients', auth: 'bootstrap', json: { resumeToken: 'secret-resume' } });
    const text = JSON.stringify(records);
    expect(text).not.toMatch(/secret-/);
    expect(records[0]?.requestHeaders.Authorization).toBe('Bearer ***');
    expect(records[0]?.responseBody).toEqual({ clientId: 'c', accessToken: '***', resumeToken: '***' });
  });

  it('redact 深拷贝、不修改原对象', () => {
    const original = { a: { bootstrapToken: 'x', keep: 1 }, list: [{ accessToken: 'y' }] };
    expect(redact(original)).toEqual({ a: { bootstrapToken: '***', keep: 1 }, list: [{ accessToken: '***' }] });
    expect(original.a.bootstrapToken).toBe('x');
  });
});
