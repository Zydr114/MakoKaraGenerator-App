// W1 读模型与打轴命令的客户端方法（合同 1.1.0／1.2.x）：请求形状与分页逻辑。真实往返见集成测试。

import { afterEach, describe, expect, it } from 'vitest';
import { BackendError, MakoClient, MemoryIdentityStore, staticBootstrapProvider } from '../../src/client';
import { micros, revision, type EditableLyricLine } from '../../src/protocol';
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

const line = (i: number): EditableLyricLine => ({
  branchId: 'primary',
  id: `line-${i}`,
  text: `第${i}行`,
  time: {},
  timingStatus: 'missing',
  tokens: [],
  rubyAnnotations: [],
});

/** 假窗口：total 行，每页按请求的 limit 切，超过 budget 行时返回 413。 */
function windowRoute(fake: FakeFetch, total: number, budget = Infinity, rev = '7') {
  fake.route('GET', /\/lyrics\/window$/, (req) => {
    const offset = Number(req.url.searchParams.get('offset') ?? '0');
    const limit = Number(req.url.searchParams.get('limit') ?? '100');
    if (limit > budget) return problem(413, 'resourceTooLarge');
    const lines = Array.from({ length: Math.max(0, Math.min(limit, total - offset)) }, (_, k) => line(offset + k));
    const next = offset + lines.length;
    return json(200, {
      projectSessionId: 'p1',
      projectRevision: rev,
      branchId: 'primary',
      offset,
      totalCount: total,
      ...(next < total ? { nextOffset: next } : {}),
      lines,
    });
  });
}

describe('W1 读模型', () => {
  it('branches 与 settings 带 expectedRevision 查询参数', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    fake.route('GET', /\/lyrics\/branches$/, () =>
      json(200, { projectSessionId: 'p1', projectRevision: '3', primaryBranchId: 'primary', offset: 0, totalCount: 1, branches: [] }),
    );
    fake.route('GET', /\/settings$/, () => json(200, { projectSessionId: 'p1', projectRevision: '3' }));
    await client.projects.branches('p1', revision('3'));
    await client.projects.settings('p1', revision('3'));
    const branchCall = fake.calls.find((c) => c.path.endsWith('/lyrics/branches'))!;
    expect(branchCall.url.searchParams.get('expectedRevision')).toBe('3');
    expect(branchCall.url.searchParams.has('offset')).toBe(false);
    expect(fake.calls.find((c) => c.path.endsWith('/settings'))!.url.searchParams.get('expectedRevision')).toBe('3');
  });

  it('allLines 沿 nextOffset 读完整个来源', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    windowRoute(fake, 230);
    const lines = await client.projects.allLines('p1', 'primary', revision('7'));
    expect(lines.map((l) => l.id)).toEqual(Array.from({ length: 230 }, (_, i) => `line-${i}`));
    const offsets = fake.calls.filter((c) => c.path.endsWith('/lyrics/window')).map((c) => c.url.searchParams.get('offset'));
    expect(offsets).toEqual(['0', '100', '200']);
  });

  it('allLines 遇 413 把 limit 减半重试', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    windowRoute(fake, 60, 30);
    const lines = await client.projects.allLines('p1', 'primary', revision('7'));
    expect(lines).toHaveLength(60);
    const limits = fake.calls.filter((c) => c.path.endsWith('/lyrics/window')).map((c) => c.url.searchParams.get('limit'));
    expect(limits).toEqual(['100', '50', '25', '25', '25']);
  });

  it('allLines 单行仍超预算时抛出 resourceTooLarge', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    windowRoute(fake, 5, 0);
    await expect(client.projects.allLines('p1', 'primary', revision('7'))).rejects.toSatisfy(
      (error) => error instanceof BackendError && error.code === 'resourceTooLarge',
    );
  });

  it('allLines 修订过期时把 409 抛给调用方', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    fake.route('GET', /\/lyrics\/window$/, () => problem(409, 'revisionConflict'));
    await expect(client.projects.allLines('p1', 'primary', revision('7'))).rejects.toSatisfy(
      (error) => error instanceof BackendError && error.code === 'revisionConflict',
    );
  });
});

describe('W1 打轴命令', () => {
  it('打轴命令与旧命令同走队列：顶层 expectedRevision 与回执，command 只带本 type 的字段', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    fake.route('GET', /\/project-sessions\/p1$/, () =>
      json(200, {
        projectSessionId: 'p1',
        hostInstanceId: 'host-A',
        projectRevision: '4',
        name: 'n',
        audioAssetId: 'a',
        lyricsLineCount: 1,
        isDirty: false,
        canUndo: false,
        canRedo: false,
        protocolVersion: '1',
        hasPath: false,
        isReadOnly: false,
      }),
    );
    fake.route('POST', /\/commands$/, (req) => {
      const body = req.body as { expectedRevision: string };
      const next = String(Number(body.expectedRevision) + 1);
      return json(200, {
        projectSessionId: 'p1',
        previousRevision: body.expectedRevision,
        projectRevision: next,
        changed: true,
        isDirty: true,
        canUndo: true,
        canRedo: false,
        changeSet: { areas: ['lyricsTiming'], branchIds: ['primary'], lineIds: ['l1'], tokenIds: ['t1'], requiresFullRefresh: false },
        protocolVersion: '1',
      });
    });
    await client.projects.get('p1');
    const target = { branchId: 'primary', lineId: 'l1', tokenId: 't1' };
    await client.projects.command('p1', { type: 'lyrics.stampTokenStart', target, playheadUs: micros(1_000_000) });
    await client.projects.command('p1', { type: 'lyrics.nudgeTokenTime', target, frameDelta: -1 });
    const bodies = fake.calls.filter((c) => c.path.endsWith('/commands')).map((c) => c.body as Record<string, unknown>);
    expect(bodies.map((b) => b.expectedRevision)).toEqual(['4', '5']);
    expect(bodies[0]!.command).toEqual({ type: 'lyrics.stampTokenStart', target, playheadUs: '1000000' });
    expect(bodies[1]!.command).toEqual({ type: 'lyrics.nudgeTokenTime', target, frameDelta: -1 });
    expect(bodies[0]).toHaveProperty('requestId');
    expect(bodies[0]).toHaveProperty('validUntilUtc');
  });
});

describe('W2 音频分析', () => {
  it('analyze 走命令队列：正文只有精确修订与回执 metadata；analysisCapabilities 读能力', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    fake.route('GET', /\/project-sessions\/p1$/, () =>
      json(200, {
        projectSessionId: 'p1',
        hostInstanceId: 'host-A',
        projectRevision: '4',
        name: 'n',
        audioAssetId: 'a',
        lyricsLineCount: 1,
        isDirty: false,
        canUndo: false,
        canRedo: false,
        protocolVersion: '1',
        hasPath: false,
        isReadOnly: false,
      }),
    );
    fake.route('POST', /\/analysis-jobs$/, () =>
      json(202, { jobId: 'job-A', kind: 'analysis', state: 'queued', projectSessionId: 'p1', expiresAtUtc: '2026-10-16T13:00:00Z', cancellationRequested: false, canCancel: true }),
    );
    fake.route('GET', /\/analysis-capabilities$/, () => json(200, { enabled: false, audioProfiles: [], disabledReason: { code: 'taskUnavailable', message: '不可用' } }));
    await client.projects.get('p1');
    const job = await client.projects.analyze('p1');
    expect(job.jobId).toBe('job-A');
    const call = fake.calls.find((c) => c.path === '/api/v1/project-sessions/p1/analysis-jobs')!;
    const body = call.body as Record<string, unknown>;
    expect(Object.keys(body).sort()).toEqual(['expectedRevision', 'metadata']);
    expect(body.expectedRevision).toBe('4');
    expect(body.metadata).toHaveProperty('requestId');
    const capabilities = await client.analysisCapabilities();
    expect(capabilities.enabled).toBe(false);
    expect(capabilities.disabledReason?.code).toBe('taskUnavailable');
  });
});

describe('W2 后端波形（G11）', () => {
  it('waveform 读元数据；waveformPeaks 带全部四个查询参数，按小端解出 count 对 min／max', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    fake.route('GET', /\/project-sessions\/p1\/waveform$/, () =>
      json(200, { projectSessionId: 'p1', available: false, protocolVersion: '1', reason: 'cacheBudgetExceeded' }),
    );
    const bytes = new ArrayBuffer(16);
    const view = new DataView(bytes);
    [-0.5, 0.25, -1, 1].forEach((v, i) => view.setFloat32(i * 4, v, true));
    fake.route('GET', /\/waveform\/peaks$/, () => new Response(bytes, { status: 200, headers: { 'Content-Type': 'application/octet-stream' } }));

    const meta = await client.projects.waveform('p1');
    expect(meta).toMatchObject({ available: false, reason: 'cacheBudgetExceeded' });
    const peaks = await client.projects.waveformPeaks('p1', { waveformId: 'wf 1', level: 3, start: 8192, count: 2 });
    expect([...peaks]).toEqual([-0.5, 0.25, -1, 1]);
    const call = fake.calls.find((c) => c.path === '/api/v1/project-sessions/p1/waveform/peaks')!;
    expect(Object.fromEntries(call.url.searchParams)).toEqual({ waveformId: 'wf 1', level: '3', start: '8192', count: '2' });
  });

  it('窗口桶数与请求不符时报错；count 越界不发请求', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    fake.route('GET', /\/waveform\/peaks$/, () => new Response(new ArrayBuffer(8), { status: 200 }));
    await expect(client.projects.waveformPeaks('p1', { waveformId: 'w', level: 0, start: 0, count: 2 })).rejects.toThrow('应有 2 个桶');
    await expect(client.projects.waveformPeaks('p1', { waveformId: 'w', level: 0, start: 0, count: 8193 })).rejects.toThrow(RangeError);
    expect(fake.calls.filter((c) => c.path.endsWith('/waveform/peaks'))).toHaveLength(1);
  });
});

describe('W3 模板库与样式读取', () => {
  const summary = (i: number) => ({
    templateId: `t${i}`,
    name: `模板${i}`,
    kind: 'line',
    templateRevision: 'a'.repeat(64),
    properties: ['opacity'],
    hasEffectRecipes: false,
    hasPackageMaterials: false,
    hasPackages: false,
  });

  it('listAll 用首次读到的修订翻页；途中库被修改（409）就从头重读', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    let rev = 'r1';
    let conflictOnce = true;
    fake.route('GET', /\/template-libraries\/personal\/templates$/, (req) => {
      const expected = req.url.searchParams.get('expectedRevision');
      if (expected !== null && conflictOnce) {
        conflictOnce = false;
        rev = 'r2';
        return problem(409, 'revisionConflict');
      }
      if (expected !== null && expected !== rev) return problem(409, 'revisionConflict');
      const offset = Number(req.url.searchParams.get('offset') ?? '0');
      const templates = offset === 0 ? [summary(0), summary(1)] : [summary(2)];
      return json(200, { libraryId: 'personal', revision: rev, offset, totalCount: 3, templates, protocolVersion: '1', ...(offset === 0 ? { nextOffset: 2 } : {}) });
    });

    const all = await client.templates.listAll('personal');
    expect(all.revision).toBe('r2');
    expect(all.templates.map((t) => t.templateId)).toEqual(['t0', 't1', 't2']);
    const queries = fake.calls.filter((c) => c.path.endsWith('/templates')).map((c) => Object.fromEntries(c.url.searchParams));
    expect(queries).toEqual([
      { limit: '100' },
      { offset: '2', limit: '100', expectedRevision: 'r1' },
      { limit: '100' },
      { offset: '2', limit: '100', expectedRevision: 'r2' },
    ]);
  });

  it('库命令把 metadata 放在嵌套字段；style 把目标展开成查询参数', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    fake.route('POST', /\/template-libraries\/personal\/commands$/, () =>
      json(200, { libraryId: 'personal', previousRevision: 'missing', revision: 'b'.repeat(64), changed: true, templateId: 'new', protocolVersion: '1' }),
    );
    fake.route('GET', /\/project-sessions\/p1\/style$/, () => json(200, { projectSessionId: 'p1', projectRevision: '4' }));

    const response = await client.templates.command('personal', 'missing', { type: 'rename', templateId: 'x', name: '新名字' });
    expect(response.templateId).toBe('new');
    const body = fake.calls.find((c) => c.path.endsWith('/commands'))!.body as Record<string, unknown>;
    expect(Object.keys(body).sort()).toEqual(['command', 'expectedRevision', 'metadata']);
    expect(body.command).toEqual({ type: 'rename', templateId: 'x', name: '新名字' });
    expect(Object.keys(body.metadata as object).sort()).toEqual(['hostInstanceId', 'requestId', 'validUntilUtc']);

    await client.projects.style('p1', { kind: 'line', branchId: 'primary', lineId: 'l 1' }, revision('4'));
    const call = fake.calls.find((c) => c.path.endsWith('/style'))!;
    expect(Object.fromEntries(call.url.searchParams)).toEqual({ expectedRevision: '4', kind: 'line', branchId: 'primary', lineId: 'l 1' });
  });
});
