// W5 客户端方法（合同 1.25.0／1.36.0 与规范 §8、§10、§11、§17）：目录浏览的分页重读、恢复、资源清单、
// 歌词导出与导入规划、正式验证与导出的请求形状、二进制文件名、慢命令的超时。真实往返见集成测试。

import { afterEach, describe, expect, it } from 'vitest';
import { MakoClient, MemoryIdentityStore, dispositionFileName, inferContentType, staticBootstrapProvider } from '../../src/client';
import { isExportFailureReport, revision, type ExportResultReport, type ProjectDirectoryEntry } from '../../src/protocol';
import { createFakeFetch, installFakeBackend, json, type FakeFetch } from '../support/fake-fetch';

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

const entry = (i: number): ProjectDirectoryEntry => ({ name: `song-${i}`, relativePath: `song-${i}`, isProject: i % 2 === 0 });

/** 假目录：total 项，每页按 limit 切；totals 依次给出每次请求时的目录大小（模拟目录被改动）。 */
function entriesRoute(fake: FakeFetch, totals: number[]) {
  let call = 0;
  fake.route('GET', /\/file-roots\/projects\/entries$/, (req) => {
    const total = totals[Math.min(call++, totals.length - 1)]!;
    const offset = Number(req.url.searchParams.get('offset') ?? '0');
    const limit = Number(req.url.searchParams.get('limit') ?? '100');
    const entries = Array.from({ length: Math.max(0, Math.min(limit, total - offset)) }, (_, k) => entry(offset + k));
    const next = offset + entries.length;
    return json(200, {
      rootGrantId: 'projects',
      relativePath: req.url.searchParams.get('relativePath') ?? '',
      offset,
      totalCount: total,
      ...(next < total ? { nextOffset: next } : {}),
      entries,
      protocolVersion: '1',
    });
  });
}

describe('目录浏览', () => {
  it('listAll 沿 nextOffset 读完一层，relativePath 原样放进查询', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    entriesRoute(fake, [230]);
    const all = await client.files.listAll('projects', 'albums/2026');
    expect(all.map((e) => e.name)).toEqual(Array.from({ length: 230 }, (_, i) => `song-${i}`));
    const calls = fake.calls.filter((c) => c.path.endsWith('/entries'));
    expect(calls.map((c) => c.url.searchParams.get('offset'))).toEqual(['0', '100', '200']);
    expect(calls.every((c) => c.url.searchParams.get('relativePath') === 'albums/2026')).toBe(true);
  });

  it('翻页途中目录变了就从第一页重读', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    // 第一轮：第一页时 150 项，第二页时变成 151 项 → 重读；第二轮两页都是 151 项。
    entriesRoute(fake, [150, 151, 151, 151]);
    const all = await client.files.listAll('projects', '');
    expect(all).toHaveLength(151);
    expect(fake.calls.filter((c) => c.path.endsWith('/entries')).map((c) => c.url.searchParams.get('offset'))).toEqual(['0', '100', '0', '100']);
  });
});

describe('恢复与资源', () => {
  it('恢复候选与恢复任务', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    fake.route('GET', /\/recovery-candidates$/, () =>
      json(200, { candidates: [{ recoveryId: 'r1', projectName: '我的歌', savedAtUtc: '2026-10-26T01:00:00Z', hasOriginalPath: true }], protocolVersion: '1' }),
    );
    fake.route('POST', /\/project-sessions\/recover-jobs$/, () =>
      json(202, { jobId: 'job-1', kind: 'recover', state: 'queued', expiresAtUtc: '2026-10-26T02:00:00Z', cancellationRequested: false, canCancel: false }),
    );
    const { candidates } = await client.recoveryCandidates();
    expect(candidates[0]!.projectName).toBe('我的歌');
    const job = await client.projects.recover({ recoveryId: 'r1' });
    expect(job.kind).toBe('recover');
    const body = fake.calls.find((c) => c.path.endsWith('/recover-jobs'))!.body as Record<string, unknown>;
    expect(body.recoveryId).toBe('r1');
    expect(body.metadata).toMatchObject({ requestId: expect.stringMatching(/^recover-/) });
  });

  it('resourcesAll 用同一修订翻页', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    fake.route('GET', /\/project-sessions\/p1\/resources$/, (req) => {
      const offset = Number(req.url.searchParams.get('offset'));
      const resources = offset === 0 ? [{ resourceId: 'audio', kind: 'media', roles: ['audio'], status: 'available' }] : [{ resourceId: 'bg', kind: 'media', roles: ['background'], status: 'available' }];
      return json(200, { projectSessionId: 'p1', projectRevision: '5', offset, totalCount: 2, ...(offset === 0 ? { nextOffset: 1 } : {}), resources, protocolVersion: '1' });
    });
    const all = await client.projects.resourcesAll('p1', revision('5'));
    expect(all.map((r) => r.resourceId)).toEqual(['audio', 'bg']);
    const calls = fake.calls.filter((c) => c.path.endsWith('/resources'));
    expect(calls.map((c) => [c.url.searchParams.get('expectedRevision'), c.url.searchParams.get('offset')])).toEqual([
      ['5', '0'],
      ['5', '1'],
    ]);
  });
});

describe('歌词导入规划与导出', () => {
  it('导入规划没有回执；导出评估与导出带顶层 expectedRevision 和嵌套回执', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    fake.route('POST', /\/lyrics-import-plans$/, () => json(200, { assetId: 'a1', fileName: 'x.lrc', previewLines: [] }));
    fake.route('POST', /\/project-sessions\/p1\/lyrics-export-plans$/, () => json(201, { planId: 'plan-1', requiresLossAcceptance: true }));
    fake.route('POST', /\/project-sessions\/p1\/lyrics-exports$/, () => json(201, { exportId: 'e1', contentUrl: '/api/v1/lyrics-exports/e1/content' }));
    fake.route('GET', /\/lyrics-exports\/e1\/content$/, () =>
      new Response('[00:01.00]你好', { status: 200, headers: { 'Content-Type': 'text/plain', 'Content-Disposition': 'attachment; filename="lyrics.lrc"' } }),
    );

    await client.assets.lyricsImportPlan({ assetId: 'a1', options: { encodingName: 'gb18030' } });
    const planBody = fake.calls.find((c) => c.path.endsWith('/lyrics-import-plans'))!.body as Record<string, unknown>;
    expect(planBody).toEqual({ assetId: 'a1', options: { encodingName: 'gb18030' } });

    await client.projects.lyricsExportPlan('p1', revision('9'), { format: 'lrc.enhanced', options: { newLine: '\r\n' }, branchId: 'primary' });
    const exportPlan = fake.calls.find((c) => c.path.endsWith('/lyrics-export-plans'))!.body as Record<string, unknown>;
    expect(exportPlan).toMatchObject({ expectedRevision: '9', format: 'lrc.enhanced', branchId: 'primary', options: { newLine: '\r\n' } });
    expect(exportPlan.metadata).toMatchObject({ requestId: expect.stringMatching(/^lyrics-plan-/) });

    await client.projects.lyricsExport('p1', revision('9'), { planId: 'plan-1', acceptLosses: true });
    expect(fake.calls.find((c) => c.path.endsWith('/lyrics-exports'))!.body).toMatchObject({ expectedRevision: '9', planId: 'plan-1', acceptLosses: true });

    const content = await client.lyricsExports.content('e1');
    expect(content.fileName).toBe('lyrics.lrc');
    expect(await content.blob!.text()).toBe('[00:01.00]你好');
  });
});

describe('正式验证与导出', () => {
  it('验证、导出经命令队列取 expectedRevision，报告与成片按任务读取', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    fake.route('GET', /\/project-sessions\/p1$/, () =>
      json(200, {
        projectSessionId: 'p1',
        hostInstanceId: 'host-A',
        projectRevision: '12',
        name: 'x',
        audioAssetId: '',
        lyricsLineCount: 0,
        isDirty: true,
        canUndo: false,
        canRedo: false,
        protocolVersion: '1',
        hasPath: false,
        isReadOnly: false,
      }),
    );
    const accepted = (kind: string) =>
      json(202, { jobId: `job-${kind}`, kind, state: 'queued', expiresAtUtc: '2026-10-26T02:00:00Z', cancellationRequested: false, canCancel: true });
    fake.route('POST', /\/validation-jobs$/, () => accepted('validation'));
    fake.route('POST', /\/export-jobs$/, () => accepted('export'));
    const failure: ExportResultReport = {
      kind: 'exportFailure',
      failureCode: 'WorkerRuntimeFailed',
      source: {} as never,
      input: {} as never,
      diagnostics: [],
    };
    fake.route('GET', /\/jobs\/job-export\/export-result$/, () => json(200, failure));
    fake.route('GET', /\/jobs\/job-export\/export-content$/, () =>
      new Response(new Uint8Array([0, 0, 0, 24]), {
        status: 200,
        headers: { 'Content-Type': 'video/mp4', 'Content-Disposition': "attachment; filename=MakoKara-x.mp4; filename*=UTF-8''%E6%88%91%E7%9A%84%E6%AD%8C.mp4" },
      }),
    );
    fake.route('GET', /\/jobs\/job-validation\/validation-diagnostics$/, () => json(200, { jobId: 'job-validation', reportSha256: 'a', diagnostics: [], nextCursor: 'c2' }));

    await client.projects.get('p1');
    await client.projects.validate('p1', { framesPerOutput: 30 });
    await client.projects.exportVideo('p1', { format: 'transparentProRes4444', quality: 'high', includeAudio: false });
    expect(fake.calls.find((c) => c.path.endsWith('/validation-jobs'))!.body).toMatchObject({ expectedRevision: '12', framesPerOutput: 30 });
    expect(fake.calls.find((c) => c.path.endsWith('/export-jobs'))!.body).toMatchObject({
      expectedRevision: '12',
      format: 'transparentProRes4444',
      quality: 'high',
      includeAudio: false,
      metadata: { requestId: expect.stringMatching(/^export-/) },
    });

    const report = await client.jobs.exportResult('job-export');
    expect(isExportFailureReport(report)).toBe(true);
    const content = await client.jobs.exportContent('job-export');
    expect(content.fileName).toBe('我的歌.mp4');
    expect(content.blob!.size).toBe(4);

    await client.jobs.validationDiagnostics('job-validation', 'c1');
    expect(fake.calls.find((c) => c.path.endsWith('/validation-diagnostics'))!.url.searchParams.get('cursor')).toBe('c1');
  });
});

describe('Content-Disposition 文件名', () => {
  it.each([
    ['attachment; filename="MakoKara-1a.mp4"', 'MakoKara-1a.mp4'],
    ['attachment; filename=plain.lrc', 'plain.lrc'],
    ["attachment; filename*=UTF-8''%E6%AD%8C.ttml", '歌.ttml'],
    ['attachment; filename="../../etc/passwd"', 'passwd'],
    ['attachment', null],
    [null, null],
  ])('%s → %s', (header, expected) => {
    expect(dispositionFileName(header)).toBe(expected);
  });
});

describe('上传类型推断', () => {
  const allowed = ['image/png', 'image/jpeg', 'video/mp4', 'video/quicktime', 'audio/mp4', 'application/ttml+xml', 'text/plain'];
  it.each([
    ['封面.PNG', '', 'image/png'],
    ['a.jpeg', '', 'image/jpeg'],
    ['clip.mp4', '', 'video/mp4'],
    ['clip.mov', '', 'video/quicktime'],
    ['song.m4a', '', 'audio/mp4'],
    ['lyrics.ttml', '', 'application/ttml+xml'],
    ['lyrics.ass', '', 'text/plain'],
    ['lyrics.ssa', '', 'text/plain'],
    ['lyrics.elrc', '', 'text/plain'],
    ['lyrics.srt', '', 'text/plain'],
    ['lyrics.vtt', '', 'text/plain'],
    ['lyrics.xml', '', 'application/ttml+xml'],
    ['clip.mp4', 'audio/mp4', 'audio/mp4'],
    ['x.webm', '', 'application/octet-stream'],
  ])('%s（%s）→ %s', (name, blobType, expected) => {
    expect(inferContentType(name, blobType, allowed)).toBe(expected);
  });
});
