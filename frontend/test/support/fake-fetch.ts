// 单元测试用的假 fetch：按方法与路径正则路由到处理函数，并记录每次调用。

export interface FakeRequest {
  method: string;
  url: URL;
  path: string;
  headers: Headers;
  body: unknown;
  rawBody: BodyInit | null | undefined;
  signal: AbortSignal | undefined;
}

export type FakeHandler = (req: FakeRequest, match: RegExpMatchArray) => Response | Promise<Response>;

export interface FakeFetch {
  fetch: typeof fetch;
  calls: FakeRequest[];
  route(method: string, path: RegExp | string, handler: FakeHandler): void;
}

export function createFakeFetch(): FakeFetch {
  const routes: { method: string; pattern: RegExp; handler: FakeHandler }[] = [];
  const calls: FakeRequest[] = [];
  const fetchImpl = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const url = new URL(input instanceof Request ? input.url : String(input));
    const method = (init?.method ?? 'GET').toUpperCase();
    const headers = new Headers(init?.headers);
    let body: unknown = init?.body;
    if (typeof init?.body === 'string' && headers.get('content-type')?.includes('json')) body = JSON.parse(init.body);
    const req: FakeRequest = { method, url, path: url.pathname, headers, body, rawBody: init?.body, signal: init?.signal ?? undefined };
    calls.push(req);
    if (init?.signal?.aborted) throw new DOMException('aborted', 'AbortError');
    for (const route of routes) {
      if (route.method !== method) continue;
      const match = url.pathname.match(route.pattern);
      if (match) return route.handler(req, match);
    }
    return problem(404, 'invalidRequest', `no fake route for ${method} ${url.pathname}`);
  };
  return {
    fetch: fetchImpl as typeof fetch,
    calls,
    route(method, path, handler) {
      const pattern = typeof path === 'string' ? new RegExp(`^${path.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`) : path;
      routes.unshift({ method, pattern, handler });
    },
  };
}

export function json(status: number, body: unknown, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } });
}

export function problem(status: number, code: string, message = code, extra: Record<string, unknown> = {}): Response {
  return new Response(JSON.stringify({ code, message, protocolVersion: '1', ...extra }), {
    status,
    headers: { 'Content-Type': 'application/problem+json' },
  });
}

export function networkFailure(): never {
  throw new TypeError('fetch failed');
}

/** 一个可以按块推送文本的 SSE 响应。 */
export function sseResponse(): { response: Response; push: (text: string) => void; close: () => void } {
  let controller!: ReadableStreamDefaultController<Uint8Array>;
  const stream = new ReadableStream<Uint8Array>({
    start(c) {
      controller = c;
    },
  });
  const encoder = new TextEncoder();
  return {
    response: new Response(stream, { status: 200, headers: { 'Content-Type': 'text/event-stream' } }),
    push: (text) => controller.enqueue(encoder.encode(text)),
    close: () => {
      try {
        controller.close();
      } catch {
        // 已关闭。
      }
    },
  };
}

/** 一个最小的“后端”：登记、resume、心跳、health、capabilities、client state。 */
export interface FakeBackendState {
  host: string;
  clientId: string;
  accessToken: string;
  resumeToken: string;
  registrations: number;
  resumes: number;
  heartbeats: number;
  now: () => Date;
}

export function installFakeBackend(fake: FakeFetch, overrides: Partial<FakeBackendState> = {}): FakeBackendState {
  const state: FakeBackendState = {
    host: 'host-A',
    clientId: 'client-1',
    accessToken: 'access-0',
    resumeToken: 'resume-1',
    registrations: 0,
    resumes: 0,
    heartbeats: 0,
    now: () => new Date('2026-10-16T10:00:00Z'),
    ...overrides,
  };
  const expiry = () => {
    const now = state.now();
    return {
      tokenExpiresAtUtc: new Date(now.getTime() + 120_000).toISOString(),
      leaseExpiresAtUtc: new Date(now.getTime() + 300_000).toISOString(),
      serverTimeUtc: now.toISOString(),
    };
  };
  fake.route('GET', '/api/v1/health/live', () => json(200, { status: 'live', hostInstanceId: state.host, protocolVersion: '1' }));
  fake.route('POST', '/api/v1/clients', (req) => {
    if (req.headers.get('authorization') !== 'Bearer boot-token') return problem(401, 'unauthorizedClient');
    const body = req.body as { resumeClientId?: string; resumeToken?: string };
    if (body.resumeClientId) {
      if (body.resumeClientId !== state.clientId || body.resumeToken !== state.resumeToken) {
        return problem(401, 'unauthorizedClient');
      }
      state.resumes++;
    } else {
      state.registrations++;
      state.clientId = `client-${state.registrations}`;
      state.resumeToken = `resume-${state.registrations}`;
    }
    state.accessToken = `access-${state.registrations}-${state.resumes}`;
    return json(201, {
      clientId: state.clientId,
      hostInstanceId: state.host,
      accessToken: state.accessToken,
      resumeToken: state.resumeToken,
      ...expiry(),
    });
  });
  const authed = (req: FakeRequest) => req.headers.get('authorization') === `Bearer ${state.accessToken}`;
  fake.route('POST', /^\/api\/v1\/clients\/([^/]+)\/heartbeat$/, (req) => {
    if (!authed(req)) return problem(401, 'clientTokenExpired');
    state.heartbeats++;
    return json(200, { clientId: state.clientId, hostInstanceId: state.host, ...expiry() });
  });
  fake.route('GET', '/api/v1/capabilities', (req) =>
    authed(req)
      ? json(200, {
          protocolVersion: '1',
          hostInstanceId: state.host,
          productVersion: '0.75.1',
          maxAssetBytes: 1024,
          assetMimeTypes: ['application/octet-stream', 'text/plain', 'audio/wav'],
          features: [],
          serverTimeUtc: state.now().toISOString(),
          maxCommandRetryWindowSeconds: 600,
        })
      : problem(401, 'clientTokenExpired'),
  );
  fake.route('GET', /^\/api\/v1\/clients\/([^/]+)\/state$/, (req) =>
    authed(req)
      ? json(200, {
          clientId: state.clientId,
          hostInstanceId: state.host,
          dormant: false,
          leaseExpiresAtUtc: expiry().leaseExpiresAtUtc,
          eventCursor: `${state.host}:${state.clientId}:0`,
          sessions: [],
          jobs: [],
        })
      : problem(401, 'clientTokenExpired'),
  );
  // 默认事件流：保持打开、不发送任何内容。
  fake.route('GET', '/api/v1/events', () => sseResponse().response);
  return state;
}
