import { afterEach, describe, expect, it, vi } from 'vitest';
import { ClientSession, HttpTransport, MemoryIdentityStore, ServerClock, WebStorageIdentityStore } from '../../src/client';
import { createFakeFetch, installFakeBackend, json } from '../support/fake-fetch';

function setup(store = new MemoryIdentityStore()) {
  const fake = createFakeFetch();
  const backend = installFakeBackend(fake);
  let session!: ClientSession;
  const transport = new HttpTransport({
    baseUrl: 'http://127.0.0.1:4000',
    fetch: fake.fetch,
    authHeaders: (kind) => session.authHeaders(kind),
  });
  session = new ClientSession({
    bootstrap: { baseUrl: 'http://127.0.0.1:4000', bootstrapToken: 'boot-token' },
    transport,
    clock: new ServerClock(),
    store,
  });
  return { fake, backend, session, store };
}

afterEach(() => {
  vi.useRealTimers();
});

describe('ClientSession', () => {
  it('首次初始化新登记，并把 clientId／resumeToken 存入身份存储', async () => {
    const { session, backend, store } = setup();
    await session.initialize();
    expect(session.state).toBe('ready');
    expect(backend.registrations).toBe(1);
    expect(store.load('host-A')).toEqual({ clientId: 'client-1', resumeToken: 'resume-1' });
    expect(session.snapshot.get()).toMatchObject({ clientId: 'client-1', origin: 'registered', hostInstanceId: 'host-A' });
    session.dispose();
  });

  it('刷新页面：同一 host 下有已存身份时 resume，不新增登记', async () => {
    const first = setup();
    await first.session.initialize();
    first.session.dispose();

    const store = first.store;
    const second = setup(store);
    second.backend.clientId = 'client-1';
    second.backend.resumeToken = 'resume-1';
    second.backend.registrations = 1;
    await second.session.initialize();
    expect(second.backend.registrations).toBe(1);
    expect(second.backend.resumes).toBe(1);
    expect(second.session.snapshot.get()).toMatchObject({ clientId: 'client-1', origin: 'resumed' });
    second.session.dispose();
  });

  it('已存身份无法恢复（被回收）时改为新登记', async () => {
    const store = new MemoryIdentityStore();
    store.save('host-A', { clientId: 'client-old', resumeToken: 'gone' });
    const { session, backend } = setup(store);
    await session.initialize();
    expect(backend.registrations).toBe(1);
    expect(session.snapshot.get().clientId).toBe('client-1');
    session.dispose();
  });

  it('并发 resume 只发起一次（single-flight）', async () => {
    const { session, fake } = setup();
    await session.initialize();
    const before = fake.calls.filter((c) => c.path === '/api/v1/clients').length;
    await Promise.all([session.resume(), session.resume(), session.resume()]);
    expect(fake.calls.filter((c) => c.path === '/api/v1/clients').length - before).toBe(1);
    expect(session.state).toBe('ready');
    session.dispose();
  });

  it('401 unauthorizedClient 且 health 显示新 host → hostRestarted，清空身份', async () => {
    const { session, backend, store } = setup();
    await session.initialize();
    backend.host = 'host-B';
    expect(await session.handleUnauthorized()).toBe('hostRestarted');
    expect(store.load('host-A')).toBeNull();
    expect(session.authHeaders('access')).toEqual({});
    session.dispose();
  });

  it('401 unauthorizedClient 且同一 host：先尝试一次 resume；resume 也被拒 → needsRegistration', async () => {
    const { session, backend } = setup();
    await session.initialize();
    backend.resumeToken = 'rotated-elsewhere';
    expect(await session.handleUnauthorized()).toBe('needsRegistration');
    session.dispose();
  });

  it('心跳间隔取 min(令牌, 租约) 剩余的 1/3，按服务端返回值计算', async () => {
    vi.useFakeTimers();
    const { session, backend } = setup();
    await session.initialize();
    expect(session.snapshot.get().heartbeatIntervalMs).toBe(40_000);
    await vi.advanceTimersByTimeAsync(40_000);
    expect(backend.heartbeats).toBe(1);
    expect(session.snapshot.get().heartbeat).toBe('ok');
    session.dispose();
  });

  it('心跳遇到 clientTokenExpired → resume 后继续', async () => {
    const { session, backend, fake } = setup();
    await session.initialize();
    fake.route('POST', /heartbeat$/, (req) => {
      if (req.headers.get('authorization') !== `Bearer ${backend.accessToken}` || backend.resumes === 0) {
        return new Response(JSON.stringify({ code: 'clientTokenExpired', message: 'x', protocolVersion: '1' }), {
          status: 401,
          headers: { 'Content-Type': 'application/problem+json' },
        });
      }
      return json(200, {
        clientId: backend.clientId,
        hostInstanceId: backend.host,
        tokenExpiresAtUtc: '2026-10-16T10:02:00Z',
        leaseExpiresAtUtc: '2026-10-16T10:05:00Z',
        serverTimeUtc: '2026-10-16T10:00:00Z',
      });
    });
    await session.heartbeat();
    expect(backend.resumes).toBe(1);
    expect(session.state).toBe('ready');
    session.dispose();
  });

  it('bootstrap 的 hostInstanceId 与 Server 不一致时初始化失败', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    let session!: ClientSession;
    const transport = new HttpTransport({ baseUrl: 'http://127.0.0.1:4000', fetch: fake.fetch, authHeaders: (k) => session.authHeaders(k) });
    session = new ClientSession({
      bootstrap: { baseUrl: 'http://127.0.0.1:4000', bootstrapToken: 'boot-token', hostInstanceId: 'host-stale' },
      transport,
      clock: new ServerClock(),
      store: new MemoryIdentityStore(),
    });
    await expect(session.initialize()).rejects.toThrow(/不一致/);
    expect(session.state).toBe('failed');
  });
});

describe('WebStorageIdentityStore', () => {
  it('只保存 clientId 与 resumeToken；其他 host 的旧身份被丢弃', () => {
    const map = new Map<string, string>();
    const storage = {
      getItem: (k: string) => map.get(k) ?? null,
      setItem: (k: string, v: string) => void map.set(k, v),
      removeItem: (k: string) => void map.delete(k),
    };
    const store = new WebStorageIdentityStore(storage);
    store.save('host-A', { clientId: 'c', resumeToken: 'r' });
    expect(JSON.parse(map.get('mako.client-identity')!)).toEqual({ hostInstanceId: 'host-A', clientId: 'c', resumeToken: 'r' });
    expect(store.load('host-A')).toEqual({ clientId: 'c', resumeToken: 'r' });
    expect(store.load('host-B')).toBeNull();
    expect(map.size).toBe(0);
  });
});
