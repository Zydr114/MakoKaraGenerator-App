import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  ConnectionUnavailableError,
  MakoClient,
  MemoryIdentityStore,
  OutcomeUnknownError,
  UploadRejectedError,
  staticBootstrapProvider,
} from '../../src/client';
import { preflightUpload } from '../../src/client/binary';
import { generation, micros, revision } from '../../src/protocol';
import { createFakeFetch, installFakeBackend, json, problem, type FakeFetch } from '../support/fake-fetch';

const clients: MakoClient[] = [];
afterEach(() => {
  for (const client of clients.splice(0)) client.dispose();
});

async function connect(fake: FakeFetch) {
  const client = await MakoClient.connect(staticBootstrapProvider({ baseUrl: 'http://127.0.0.1:4000', bootstrapToken: 'boot-token' }), {
    fetch: fake.fetch,
    identityStore: new MemoryIdentityStore(),
    environment: null,
  });
  clients.push(client);
  return client;
}

const summary = (rev: string) => ({
  projectSessionId: 'p1',
  hostInstanceId: 'host-A',
  projectRevision: rev,
  name: 'n',
  audioAssetId: 'a',
  lyricsLineCount: 1,
  audioDurationUs: 2_000_000,
  isDirty: true,
  canUndo: false,
  canRedo: false,
  protocolVersion: '1',
  hasPath: false,
  isReadOnly: false,
});

const commandResponse = (prev: string, next: string, changed = true) => ({
  projectSessionId: 'p1',
  previousRevision: prev,
  projectRevision: next,
  changed,
  isDirty: true,
  canUndo: true,
  canRedo: false,
  changeSet: { areas: [], branchIds: [], lineIds: [], tokenIds: [], requiresFullRefresh: false },
  protocolVersion: '1',
});

describe('MakoClient 请求重试', () => {
  it('网络错误：用同一 requestId 和同一 deadline 重试', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    let attempts = 0;
    fake.route('POST', '/api/v1/project-sessions', () => {
      if (attempts++ === 0) throw new TypeError('fetch failed');
      return json(201, summary('0'));
    });
    await client.projects.create({ name: 'n', audioAssetId: 'a', lyricsAssetId: 'l' });
    const sent = fake.calls.filter((c) => c.path === '/api/v1/project-sessions');
    expect(sent).toHaveLength(2);
    const [a, b] = sent.map((c) => (c.body as { metadata: unknown }).metadata);
    expect(a).toEqual(b);
  });

  it('401 clientTokenExpired：resume 后用同一 requestId 重试', async () => {
    const fake = createFakeFetch();
    const backend = installFakeBackend(fake);
    const client = await connect(fake);
    client.projects.knownRevision('p1');
    let attempts = 0;
    fake.route('POST', /\/commands$/, (req) => {
      if (attempts++ === 0) return problem(401, 'clientTokenExpired');
      expect(req.headers.get('authorization')).toBe(`Bearer ${backend.accessToken}`);
      return json(200, commandResponse('0', '1'));
    });
    fake.route('GET', '/api/v1/project-sessions/p1', () => json(200, summary('0')));
    await client.projects.undo('p1');
    const sent = fake.calls.filter((c) => c.path.endsWith('/commands')).map((c) => (c.body as { requestId: string }).requestId);
    expect(sent).toHaveLength(2);
    expect(sent[0]).toBe(sent[1]);
    expect(backend.resumes).toBe(1);
    expect(client.projects.knownRevision('p1')).toBe('1');
  });

  it('有副作用请求遇到 500 internalError：报告结果未知，不换 requestId 重做', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    fake.route('POST', '/api/v1/file-handles', () => problem(500, 'internalError'));
    const error = await client.files.createHandle({ rootGrantId: 'projects', relativePath: 'x' }).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(OutcomeUnknownError);
    expect(fake.calls.filter((c) => c.path === '/api/v1/file-handles')).toHaveLength(1);
  });

  it('409 hostRestarted：连接进入 hostRestarted，之后的请求直接拒绝并发出 reset', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    const resets: string[] = [];
    client.on('reset', (r) => resets.push(r.reason));
    fake.route('GET', '/api/v1/file-roots', () => problem(409, 'hostRestarted'));
    await expect(client.files.roots()).rejects.toBeInstanceOf(ConnectionUnavailableError);
    expect(client.state$.get().state).toBe('hostRestarted');
    expect(resets).toHaveLength(1);
    await expect(client.capabilities(true)).rejects.toBeInstanceOf(ConnectionUnavailableError);
  });

  it('Server 重启后旧凭证先得 401 unauthorizedClient：通过 health 识别 hostRestarted', async () => {
    const fake = createFakeFetch();
    const backend = installFakeBackend(fake);
    const client = await connect(fake);
    backend.host = 'host-B';
    backend.accessToken = 'new-process-token';
    fake.route('GET', '/api/v1/file-roots', () => problem(401, 'unauthorizedClient'));
    await expect(client.files.roots()).rejects.toBeInstanceOf(ConnectionUnavailableError);
    expect(client.state$.get().state).toBe('hostRestarted');
  });

  it('Server 重启换了端口：旧地址连不上时核对启动信息，识别 hostRestarted', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    let info = { baseUrl: 'http://127.0.0.1:4000', bootstrapToken: 'boot-token' };
    const client = await MakoClient.connect({ get: async () => info }, { fetch: fake.fetch, identityStore: new MemoryIdentityStore(), environment: null });
    clients.push(client);
    info = { baseUrl: 'http://127.0.0.1:4001', bootstrapToken: 'new-token' };
    fake.route('GET', '/api/v1/file-roots', () => {
      throw new TypeError('fetch failed');
    });
    await expect(client.files.roots()).rejects.toBeInstanceOf(ConnectionUnavailableError);
    expect(client.state$.get().state).toBe('hostRestarted');
  });

  it('命令 no-op（changed:false）不推进已知修订', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    fake.route('GET', '/api/v1/project-sessions/p1', () => json(200, summary('3')));
    fake.route('POST', /\/commands$/, () => json(200, commandResponse('3', '3', false)));
    await client.projects.setTimeRange('p1', { branchId: 'primary', lineId: 'l1' }, micros(0), micros(1));
    expect(client.projects.knownRevision('p1')).toBe('3');
    const body = fake.calls.find((c) => c.path.endsWith('/commands'))?.body as Record<string, unknown>;
    expect(body).toMatchObject({ expectedRevision: '3', command: { type: 'lyrics.setTimeRange', startUs: '0', endUs: '1' } });
    expect(Object.keys(body).sort()).toEqual(['command', 'expectedRevision', 'hostInstanceId', 'requestId', 'validUntilUtc']);
  });

  it('摘要中不安全的 JSON 整数被拒绝', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    fake.route('GET', '/api/v1/project-sessions/p1', () =>
      new Response(JSON.stringify(summary('0')).replace('2000000', '9007199254740993'), { status: 200, headers: { 'Content-Type': 'application/json' } }),
    );
    await expect(client.projects.get('p1')).rejects.toThrow(/audioDurationUs/);
  });
});

describe('MakoClient 初始化', () => {
  it('并发初始化共用同一次，只登记一个 client', async () => {
    const fake = createFakeFetch();
    const backend = installFakeBackend(fake);
    const client = MakoClient.create(staticBootstrapProvider({ baseUrl: 'http://127.0.0.1:4000', bootstrapToken: 'boot-token' }), {
      fetch: fake.fetch,
      identityStore: new MemoryIdentityStore(),
      environment: null,
    });
    clients.push(client);
    await Promise.all([client.initialize(), client.reinitialize('并发'), client.initialize()]);
    expect(backend.registrations).toBe(1);
    expect(client.state$.get().state).toBe('ready');
  });
});

describe('MakoClient 预览会话配额', () => {
  const still = (signal?: AbortSignal) => ({
    source: { kind: 'savedTemplate' as const, libraryId: 'builtin' as const, libraryRevision: '1', templateId: 't', templateRevision: '1' },
    sampleId: 'default-v1' as const,
    size: { width: 320, height: 180 },
    timeUs: micros('0'),
    ...(signal ? { signal } : {}),
  });

  it('创建遇到 429 sessionQuotaExceeded：退避后重试', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    let attempts = 0;
    fake.route('POST', '/api/v1/preview-sessions', () =>
      attempts++ === 0 ? problem(429, 'sessionQuotaExceeded') : json(201, { previewId: 'v1' }),
    );
    const created = await client.preview.create({ source: { kind: 'project', sourceId: 'p1', sourceRevision: revision('0') }, size: { width: 320, height: 180 } });
    expect(created.previewId).toBe('v1');
    expect(attempts).toBe(2);
  });

  it('中止后不再重试创建', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    fake.route('POST', '/api/v1/preview-sessions', () => problem(429, 'sessionQuotaExceeded'));
    const abort = new AbortController();
    const created = client.preview.create({ source: { kind: 'project', sourceId: 'p1', sourceRevision: revision('0') }, size: { width: 320, height: 180 } }, abort.signal);
    await vi.waitFor(() => expect(fake.calls.filter((c) => c.path === '/api/v1/preview-sessions')).toHaveLength(1));
    abort.abort();
    await expect(created).rejects.toMatchObject({ name: 'AbortError' });
    expect(fake.calls.filter((c) => c.path === '/api/v1/preview-sessions')).toHaveLength(1);
  });

  it('尽力关闭：代次对不上时按当前状态再关一次', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    const closes: unknown[] = [];
    fake.route('POST', '/api/v1/preview-sessions/v1/close', (req) => {
      const body = req.body as { expectedPreviewGeneration: string };
      closes.push(body.expectedPreviewGeneration);
      return body.expectedPreviewGeneration === '3' ? json(200, { session: { state: 'closed' } }) : problem(409, 'previewGenerationConflict');
    });
    fake.route('GET', '/api/v1/preview-sessions/v1', () => json(200, { session: { previewId: 'v1', previewGeneration: '3', source: { sourceRevision: '7' } } }));
    await client.preview.closeBestEffort('v1', { expectedPreviewGeneration: generation('2'), expectedSourceRevision: revision('7') });
    expect(closes).toEqual(['2', '3']);
  });

  it('一次性帧同时最多两个，多的排队；排队时中止的不占名额', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    const pending: ((response: Response) => void)[] = [];
    fake.route('POST', '/api/v1/template-preview-sessions', () => new Promise<Response>((resolve) => pending.push(resolve)));
    const creates = () => fake.calls.filter((c) => c.path === '/api/v1/template-preview-sessions').length;

    const abort = new AbortController();
    const results = [still(), still(), still(abort.signal), still()].map((input) => client.preview.renderTemplateStill(input).catch((e: unknown) => e));
    await vi.waitFor(() => expect(creates()).toBe(2));
    abort.abort();
    expect(((await results[2]) as Error).name).toBe('AbortError');

    pending[0]!(problem(422, 'commandNotSupported'));
    expect(await results[0]).toMatchObject({ code: 'commandNotSupported' });
    await vi.waitFor(() => expect(creates()).toBe(3));
    pending[1]!(problem(422, 'commandNotSupported'));
    pending[2]!(problem(422, 'commandNotSupported'));
    await Promise.all(results);
    expect(creates()).toBe(3);
  });
});

describe('MakoClient 渲染设备', () => {
  it('读检查结果；给已有预览换设备时带代次与回执', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    fake.route('GET', '/api/v1/render-backends', () => json(200, { defaultBackend: 'cpu', backends: [] }));
    fake.route('PUT', '/api/v1/preview-sessions/v1/render-backend', () => json(200, { previewId: 'v1', previewGeneration: '4', renderBackend: 'gpu' }));

    expect((await client.preview.renderBackends()).defaultBackend).toBe('cpu');
    const switched = await client.preview.setRenderBackend('v1', { expectedPreviewGeneration: generation('3'), renderBackend: 'gpu' });
    expect(switched.previewGeneration).toBe('4');
    const put = fake.calls.find((c) => c.method === 'PUT')!;
    expect(put.body).toMatchObject({ expectedPreviewGeneration: '3', renderBackend: 'gpu' });
    expect((put.body as { metadata: { requestId: string } }).metadata.requestId).toMatch(/^preview-render-backend-/);
  });

  it('一次性静态帧把选择带进创建请求；没选就不带', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    fake.route('POST', '/api/v1/preview-sessions', () => problem(422, 'commandNotSupported'));
    const still = (renderBackend?: 'gpu') =>
      client.preview
        .renderStill({ projectSessionId: 'p1', sourceRevision: revision('1'), timeUs: micros('0'), size: { width: 320, height: 180 }, ...(renderBackend ? { renderBackend } : {}) })
        .catch(() => undefined);
    await still('gpu');
    await still();
    const bodies = fake.calls.filter((c) => c.path === '/api/v1/preview-sessions').map((c) => c.body as Record<string, unknown>);
    expect(bodies[0]!.renderBackend).toBe('gpu');
    expect('renderBackend' in bodies[1]!).toBe(false);
  });
});

describe('MakoClient 上传', () => {
  it('中文文件名放在 fileName 查询参数里，回执放在请求头里', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    fake.route('POST', '/api/v1/assets', (req) =>
      json(201, { assetId: 'asset-1', sha256: 'a'.repeat(64), size: 3, contentType: req.headers.get('content-type'), fileName: req.url.searchParams.get('fileName') }),
    );
    const asset = await client.assets.upload(new Blob(['abc']), { fileName: '歌词 テスト.lrc' });
    const call = fake.calls.find((c) => c.path === '/api/v1/assets')!;
    expect(asset.fileName).toBe('歌词 テスト.lrc');
    expect(call.url.search).toContain('fileName=');
    expect(call.headers.get('content-type')).toBe('text/plain');
    expect(call.headers.get('x-mako-asset-name')).toBeNull();
    expect(call.headers.get('x-mako-request-id')).toMatch(/^upload-/);
    expect(call.headers.get('x-mako-host-instance-id')).toBe('host-A');
    expect(Date.parse(call.headers.get('x-mako-valid-until-utc')!)).toBeGreaterThan(0);
  });

  it('上传前按 capabilities 预检大小与类型', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    await expect(client.assets.upload(new Blob([new Uint8Array(2048)]), { fileName: 'big.wav' })).rejects.toMatchObject({
      reason: 'tooLarge',
    });
    await expect(client.assets.upload(new Blob([]), { fileName: 'empty.lrc' })).rejects.toBeInstanceOf(UploadRejectedError);
    await expect(client.assets.upload(new Blob(['x']), { fileName: 'a.mp3', contentType: 'audio/mpeg' })).rejects.toMatchObject({
      reason: 'unsupportedType',
    });
    expect(fake.calls.filter((c) => c.path === '/api/v1/assets')).toHaveLength(0);
  });

  it('部署没设单个资源上限（null）时不按大小拒绝', () => {
    const capabilities = { maxAssetBytes: null, assetMimeTypes: ['audio/wav'] } as unknown as Parameters<typeof preflightUpload>[3];
    expect(() => preflightUpload(new Blob([new Uint8Array(2048)]), 'big.wav', 'audio/wav', capabilities)).not.toThrow();
  });
});
