import { afterEach, describe, expect, it } from 'vitest';
import {
  ClientSession,
  EventStream,
  HttpTransport,
  MemoryIdentityStore,
  ServerClock,
  type EventStreamLog,
} from '../../src/client';
import type { BackendEvent } from '../../src/protocol';
import { createFakeFetch, installFakeBackend, problem, sseResponse } from '../support/fake-fetch';

const streams: EventStream[] = [];
afterEach(() => {
  for (const stream of streams.splice(0)) stream.stop();
});

async function setup(options: { idleTimeoutMs?: number } = {}) {
  const fake = createFakeFetch();
  const backend = installFakeBackend(fake);
  let session!: ClientSession;
  const transport = new HttpTransport({ baseUrl: 'http://127.0.0.1:4000', fetch: fake.fetch, authHeaders: (k) => session.authHeaders(k) });
  session = new ClientSession({
    bootstrap: { baseUrl: 'http://127.0.0.1:4000', bootstrapToken: 'boot-token' },
    transport,
    clock: new ServerClock(),
    store: new MemoryIdentityStore(),
  });
  await session.initialize();
  session.dispose(); // 不需要心跳定时器
  const resyncs: string[] = [];
  const stream = new EventStream({
    transport,
    session,
    onResync: async () => {
      resyncs.push('resync');
      return 'host-A:client-1:42';
    },
    ...options,
  });
  streams.push(stream);
  const events: BackendEvent[] = [];
  const logs: EventStreamLog[] = [];
  stream.on('event', (e) => events.push(e));
  stream.on('log', (l) => logs.push(l));
  return { fake, backend, stream, events, logs, resyncs };
}

const eventText = (seq: number, type = 'project.changed') =>
  `id: ${seq}\nevent: ${type}\ndata: ${JSON.stringify({
    eventSeq: String(seq),
    eventCursor: `host-A:client-1:${seq}`,
    type,
    hostInstanceId: 'host-A',
    occurredAtUtc: '2026-10-16T10:00:00Z',
  })}\n\n`;

const until = async (condition: () => boolean, timeoutMs = 2000) => {
  const start = Date.now();
  while (!condition()) {
    if (Date.now() - start > timeoutMs) throw new Error('等待超时');
    await new Promise((r) => setTimeout(r, 5));
  }
};

describe('EventStream', () => {
  it('首次连接带初始游标，分发事件并记录最新游标', async () => {
    const { fake, stream, events } = await setup();
    const sse = sseResponse();
    fake.route('GET', '/api/v1/events', () => sse.response);
    stream.start('host-A:client-1:0');
    await until(() => stream.status === 'open');
    expect(fake.calls.at(-1)?.headers.get('last-event-id')).toBe('host-A:client-1:0');
    expect(fake.calls.at(-1)?.headers.get('authorization')).toMatch(/^Bearer /);
    sse.push(': heartbeat\n\n');
    sse.push(eventText(1));
    sse.push(eventText(2, 'job.changed').slice(0, 10));
    sse.push(eventText(2, 'job.changed').slice(10));
    await until(() => events.length === 2);
    expect(events.map((e) => e.type)).toEqual(['project.changed', 'job.changed']);
    expect(stream.cursor).toBe('host-A:client-1:2');
  });

  it('断线后用 Last-Event-ID 续接', async () => {
    const { fake, stream, events } = await setup();
    const first = sseResponse();
    const second = sseResponse();
    let n = 0;
    fake.route('GET', '/api/v1/events', () => (n++ === 0 ? first.response : second.response));
    stream.start('host-A:client-1:0');
    await until(() => stream.status === 'open');
    first.push(eventText(5));
    await until(() => events.length === 1);
    first.close();
    await until(() => n === 2 && stream.status === 'open', 3000);
    expect(fake.calls.at(-1)?.headers.get('last-event-id')).toBe('host-A:client-1:5');
  });

  it('resync.required：调用 onResync，用返回的新游标重连', async () => {
    const { fake, stream, resyncs } = await setup();
    let n = 0;
    fake.route('GET', '/api/v1/events', () => {
      const sse = sseResponse();
      if (n++ === 0) {
        sse.push('event: resync.required\ndata: {"eventCursor":"host-A:client-1:0","events":[],"resyncRequired":true}\n\n');
        sse.close();
      }
      return sse.response;
    });
    stream.start('999');
    await until(() => n === 2 && stream.status === 'open');
    expect(resyncs).toEqual(['resync']);
    expect(fake.calls.at(-1)?.headers.get('last-event-id')).toBe('host-A:client-1:42');
  });

  it('client.expired：resume 后重连', async () => {
    const { fake, stream, backend } = await setup();
    let n = 0;
    fake.route('GET', '/api/v1/events', () => {
      const sse = sseResponse();
      if (n++ === 0) {
        sse.push('event: client.expired\ndata: {"code":"clientTokenExpired","message":"x","protocolVersion":"1"}\n\n');
        sse.close();
      }
      return sse.response;
    });
    stream.start('c');
    await until(() => n === 2 && stream.status === 'open');
    expect(backend.resumes).toBe(1);
  });

  it('长时间没有任何字节：判定连接已死并重连', async () => {
    const { fake, stream, logs } = await setup({ idleTimeoutMs: 30 });
    let n = 0;
    fake.route('GET', '/api/v1/events', () => {
      n++;
      return sseResponse().response;
    });
    stream.start('c');
    await until(() => n >= 2);
    expect(logs.some((l) => l.kind === 'idle')).toBe(true);
  });

  it('429 时退避而不是立即重连', async () => {
    const { fake, stream, logs } = await setup();
    let n = 0;
    fake.route('GET', '/api/v1/events', () => {
      n++;
      return n === 1 ? problem(429, 'requestQuotaExceeded') : sseResponse().response;
    });
    stream.start('c');
    await until(() => logs.some((l) => l.kind === 'backoff'));
    expect(stream.status).toBe('waiting');
    expect(n).toBe(1);
  });

  it('手动重连可以注入游标以制造缺口', async () => {
    const { fake, stream } = await setup();
    let n = 0;
    fake.route('GET', '/api/v1/events', () => {
      n++;
      return sseResponse().response;
    });
    stream.start('host-A:client-1:0');
    await until(() => stream.status === 'open');
    stream.reconnect('999999');
    await until(() => n === 2);
    expect(fake.calls.at(-1)?.headers.get('last-event-id')).toBe('999999');
  });
});
