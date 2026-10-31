import { afterEach, describe, expect, it } from 'vitest';
import { MakoClient, MemoryIdentityStore, staticBootstrapProvider } from '../../src/client';
import { EditorSession, selectionHint } from '../../src/editor/session/editor-session';
import { micros } from '../../src/protocol';
import { createFakeFetch, installFakeBackend, json, problem, type FakeFetch } from '../support/fake-fetch';

const disposables: { dispose(): void }[] = [];
afterEach(() => {
  for (const item of disposables.splice(0)) item.dispose();
});

async function connect(fake: FakeFetch) {
  const client = await MakoClient.connect(staticBootstrapProvider({ baseUrl: 'http://127.0.0.1:4000', bootstrapToken: 'boot-token' }), {
    fetch: fake.fetch,
    identityStore: new MemoryIdentityStore(),
    environment: null,
  });
  disposables.push(client);
  return client;
}

const EDITABLE = ['project-sessions.lyrics.branches.read', 'project-sessions.lyrics.window.read', 'project-sessions.settings.read'];

/** 一个只有一行两个元素的假工程；setTimeRange 改元素时间并加修订。 */
function installFakeProject(fake: FakeFetch, options: { editable?: boolean } = {}) {
  const project = {
    revision: 0,
    tokens: [
      { id: 't1', text: '朝', start: undefined as number | undefined, end: undefined as number | undefined },
      { id: 't2', text: 'の', start: undefined as number | undefined, end: undefined as number | undefined },
    ],
    conflictNext: false,
    /** 大于 0 时，窗口读取先让修订前进一步再返回 409（模拟读取途中别处有修改）。 */
    windowConflicts: 0,
    /** 为 true 时窗口读取一直返回 409，修订不动。 */
    windowStuck: false,
    /** 为 true 时命令响应报告的修订比读模型领先一步（模拟读取期间又有命令返回）。 */
    reportAhead: false,
    /** 额外的空翻译来源。 */
    translation: false,
    /** 读窗口时调用（模拟读取途中用户操作）。 */
    onWindowRead: undefined as ((branchId: string | null) => void) | undefined,
    /** 每次 project.applyEdits 带的修改条数。 */
    editBatches: [] as number[],
    /** 第几次 project.applyEdits（从 1 起）被拒绝。 */
    rejectBatch: 0,
  };
  const time = (t: { start?: number | undefined; end?: number | undefined }) =>
    t.start === undefined ? {} : { startUs: String(t.start), endUs: String(t.end) };
  const summary = () => ({
    projectSessionId: 'p1',
    hostInstanceId: 'host-A',
    projectRevision: String(project.revision),
    name: '示例',
    audioAssetId: 'a1',
    lyricsLineCount: 1,
    audioDurationUs: 3_000_000,
    isDirty: project.revision > 0,
    canUndo: project.revision > 0,
    canRedo: false,
    protocolVersion: '1',
    hasPath: false,
    isReadOnly: false,
  });

  if (options.editable) {
    fake.route('GET', '/api/v1/capabilities', () =>
      json(200, { protocolVersion: '1', hostInstanceId: 'host-A', features: EDITABLE, assetMimeTypes: [], maxAssetBytes: 1, serverTimeUtc: '2026-10-16T10:00:00Z' }),
    );
  }
  fake.route('GET', '/api/v1/project-sessions/p1', () => json(200, summary()));
  fake.route('GET', '/api/v1/project-sessions/p1/lyrics/lines', () =>
    json(200, {
      projectSessionId: 'p1',
      projectRevision: String(project.revision),
      branchId: 'primary',
      lines: [{ branchId: 'primary', id: 'l1', text: '朝の', time: {}, tokens: project.tokens.map((t) => ({ id: t.id, text: t.text, time: time(t) })) }],
      protocolVersion: '1',
    }),
  );
  const checkRevision = (url: URL) => url.searchParams.get('expectedRevision') === String(project.revision);
  fake.route('GET', '/api/v1/project-sessions/p1/lyrics/branches', (req) =>
    checkRevision(req.url)
      ? json(200, {
          projectSessionId: 'p1',
          projectRevision: String(project.revision),
          primaryBranchId: 'primary',
          offset: 0,
          totalCount: project.translation ? 2 : 1,
          branches: [
            { id: 'primary', name: '主歌词', role: 'primary', timingMode: 'independent', lineCount: 1 },
            ...(project.translation ? [{ id: 'trans', name: '翻译', role: 'translation', timingMode: 'followPrimary', lineCount: 0 }] : []),
          ],
        })
      : problem(409, 'revisionConflict'),
  );
  fake.route('GET', '/api/v1/project-sessions/p1/settings', (req) =>
    checkRevision(req.url)
      ? json(200, { projectSessionId: 'p1', projectRevision: String(project.revision), frameRateNumerator: 24, frameRateDenominator: 1 })
      : problem(409, 'revisionConflict'),
  );
  fake.route('GET', '/api/v1/project-sessions/p1/lyrics/window', (req) => {
    project.onWindowRead?.(req.url.searchParams.get('branchId'));
    if (req.url.searchParams.get('branchId') === 'trans') {
      return json(200, { projectSessionId: 'p1', projectRevision: String(project.revision), branchId: 'trans', offset: 0, totalCount: 0, lines: [] });
    }
    if (project.windowConflicts > 0) {
      project.windowConflicts--;
      project.revision++;
      return problem(409, 'revisionConflict');
    }
    if (project.windowStuck) return problem(409, 'revisionConflict');
    return checkRevision(req.url)
      ? json(200, {
          projectSessionId: 'p1',
          projectRevision: String(project.revision),
          branchId: 'primary',
          offset: 0,
          totalCount: 1,
          lines: [
            {
              branchId: 'primary',
              id: 'l1',
              text: '朝の',
              time: {},
              timingStatus: 'missing',
              tokens: project.tokens.map((t) => ({ id: t.id, text: t.text, time: time(t), timingStatus: t.start === undefined ? 'missing' : 'edited' })),
              rubyAnnotations: [],
            },
          ],
        })
      : problem(409, 'revisionConflict');
  });
  fake.route('POST', '/api/v1/project-sessions/p1/commands', (req) => {
    const body = req.body as { expectedRevision: string; command: { type: string; target?: { tokenId: string }; startUs?: string; endUs?: string } };
    if (project.conflictNext || body.expectedRevision !== String(project.revision)) {
      project.conflictNext = false;
      return problem(409, 'revisionConflict');
    }
    const previous = project.revision;
    if (body.command.type === 'project.applyEdits') {
      project.editBatches.push((body.command as unknown as { edits: unknown[] }).edits.length);
      if (project.editBatches.length === project.rejectBatch) return problem(422, 'commandRejected', 'rejected', { failureCode: 'TargetStale' });
    }
    if (body.command.type === 'lyrics.setTimeRange') {
      const token = project.tokens.find((t) => t.id === body.command.target?.tokenId)!;
      if (token.start === Number(body.command.startUs) && token.end === Number(body.command.endUs)) {
        return json(200, { ...commandBody(previous, previous, false), changeSet: emptyChangeSet });
      }
      token.start = Number(body.command.startUs);
      token.end = Number(body.command.endUs);
    }
    project.revision++;
    if (body.command.type === 'lyrics.splitToken') {
      return json(200, {
        ...commandBody(previous, project.revision, true),
        tokenResult: { changedLineCount: 1, selection: { branchId: 'primary', lineId: 'l1', hasTokenSelection: true, tokenId: 't2' } },
      });
    }
    if (body.command.type === 'orchestration.setDefaultContainer') {
      return json(200, { ...commandBody(previous, project.revision, true), changeSet: { ...emptyChangeSet, areas: ['orchestration', 'renderInputs'] } });
    }
    if (project.reportAhead) return json(200, { ...commandBody(previous, project.revision + 1, true), changeSet: { ...emptyChangeSet, requiresFullRefresh: true } });
    return json(200, commandBody(previous, project.revision, true));
  });
  return project;
}

const emptyChangeSet = { areas: [], branchIds: [], lineIds: [], tokenIds: [], requiresFullRefresh: false };
const commandBody = (prev: number, next: number, changed: boolean) => ({
  projectSessionId: 'p1',
  previousRevision: String(prev),
  projectRevision: String(next),
  changed,
  isDirty: next > 0,
  canUndo: next > 0,
  canRedo: false,
  changeSet: changed
    ? { areas: ['lyricsTiming', 'timeline', 'renderInputs'], branchIds: ['primary'], lineIds: ['l1'], tokenIds: ['t1'], requiresFullRefresh: false }
    : emptyChangeSet,
  protocolVersion: '1',
});

async function settle(session: EditorSession) {
  for (let i = 0; i < 50 && (session.state.syncing || session.state.pendingCommands > 0); i++) await new Promise((r) => setTimeout(r, 1));
}

describe('EditorSession', () => {
  it('没有 S1 能力时用最小投影，来源固定 primary，没有时间状态', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    installFakeProject(fake);
    const session = await EditorSession.attach(await connect(fake), 'p1', { composition: { frameRateNumerator: 30 } });
    disposables.push(session);
    expect(session.state.readModel).toBe('minimal');
    expect(session.state.lines[0]!.tokens.map((t) => t.text)).toEqual(['朝', 'の']);
    expect(session.state.lines[0]!.tokens[0]!.status).toBeUndefined();
    expect(session.state.linesRevision).toBe('0');
    expect(session.frameRate).toEqual({ numerator: 30, denominator: 1 });
  });

  it('有 S1 能力时按同一修订读来源、设置与窗口，帧率取自设置', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    installFakeProject(fake, { editable: true });
    const session = await EditorSession.attach(await connect(fake), 'p1');
    disposables.push(session);
    expect(session.state.readModel).toBe('editable');
    expect(session.state.branches?.map((b) => b.id)).toEqual(['primary']);
    expect(session.state.lines[0]!.tokens[0]!.status).toBe('missing');
    expect(session.frameRate).toEqual({ numerator: 24, denominator: 1 });
    const reads = fake.calls.filter((c) => c.url.searchParams.has('expectedRevision'));
    expect(new Set(reads.map((c) => c.url.searchParams.get('expectedRevision')))).toEqual(new Set(['0']));
  });

  it('命令 changed 后重读，选择按稳定 ID 保留；no-op 不重读并提示', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    installFakeProject(fake, { editable: true });
    const session = await EditorSession.attach(await connect(fake), 'p1');
    disposables.push(session);
    session.select({ lineId: 'l1', tokenId: 't1' });
    const target = { branchId: 'primary', lineId: 'l1', tokenId: 't1' };
    const changed = await session.command({ type: 'lyrics.setTimeRange', target, startUs: micros(100), endUs: micros(200) });
    expect(changed?.changed).toBe(true);
    await settle(session);
    expect(session.state.linesRevision).toBe('1');
    expect(session.state.lines[0]!.tokens[0]).toMatchObject({ startUs: 100, endUs: 200, status: 'edited' });
    expect(session.state.selection).toEqual({ lineId: 'l1', tokenId: 't1' });
    expect(session.state.summary.canUndo).toBe(true);

    // 按 changeSet 只重读受影响的行：一次 limit=1 的窗口读取，不重读摘要、来源与设置。
    const afterCommand = fake.calls.slice(fake.calls.findIndex((c) => c.path.endsWith('/commands')) + 1);
    expect(afterCommand.filter((c) => c.method === 'GET').map((c) => `${c.url.pathname.split('/').pop()}?limit=${c.url.searchParams.get('limit')}`)).toEqual([
      'window?limit=1',
    ]);

    const windowReads = fake.calls.filter((c) => c.path.endsWith('/lyrics/window')).length;
    const noop = await session.command({ type: 'lyrics.setTimeRange', target, startUs: micros(100), endUs: micros(200) });
    expect(noop?.changed).toBe(false);
    await settle(session);
    expect(fake.calls.filter((c) => c.path.endsWith('/lyrics/window')).length).toBe(windowReads);
    expect(session.state.notice?.message).toContain('没有变化');
  });

  it('同一修订的命令响应与 project.changed 事件只触发一次重读；changeSet 要求整体刷新时整体重读', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    installFakeProject(fake, { editable: true });
    const client = await connect(fake);
    const session = await EditorSession.attach(client, 'p1');
    disposables.push(session);
    const target = { branchId: 'primary', lineId: 'l1', tokenId: 't1' };
    await session.command({ type: 'lyrics.setTimeRange', target, startUs: micros(100), endUs: micros(200) });
    await settle(session);
    const reads = () => fake.calls.filter((c) => c.method === 'GET' && c.path.startsWith('/api/v1/project-sessions/')).length;
    const before = reads();
    // 自己命令的回声事件：同一修订 1，不再读。
    (client as unknown as { emit(name: string, value: unknown): void }).emit('event', {
      type: 'project.changed',
      summary: { projectSessionId: 'p1', projectRevision: '1' },
      changeSet: { areas: ['lyricsTiming'], branchIds: ['primary'], lineIds: ['l1'], tokenIds: [], requiresFullRefresh: false },
    });
    await settle(session);
    expect(reads()).toBe(before);
    // 别处的修改（修订 9）且要求整体刷新：重读摘要、来源、设置与窗口。
    (client as unknown as { emit(name: string, value: unknown): void }).emit('event', {
      type: 'project.changed',
      summary: { projectSessionId: 'p1', projectRevision: '9' },
      changeSet: { areas: ['lyricsTiming'], branchIds: [], lineIds: [], tokenIds: [], requiresFullRefresh: true },
    });
    await settle(session);
    const paths = fake.calls.slice(-4).map((c) => c.url.pathname.split('/').slice(4).join('/'));
    expect(paths).toEqual(['p1', 'p1/lyrics/branches', 'p1/settings', 'p1/lyrics/window']);
  });

  it('只涉及编排或分配的变化只更新摘要、不重读歌词；混有其他区域时仍整体重读', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    installFakeProject(fake, { editable: true });
    const client = await connect(fake);
    const session = await EditorSession.attach(client, 'p1');
    disposables.push(session);
    const reads = () => fake.calls.filter((c) => c.method === 'GET' && c.path.startsWith('/api/v1/project-sessions/')).length;
    const before = reads();
    // 命令回执：摘要直接取自回执。
    await session.command({ type: 'orchestration.setDefaultContainer', branchId: 'primary', containerId: 'c2' });
    await settle(session);
    expect(reads()).toBe(before);
    expect(session.state.summary).toMatchObject({ projectRevision: '1', canUndo: true });
    // 事件：摘要取自事件。
    const emit = (value: unknown) => (client as unknown as { emit(name: string, value: unknown): void }).emit('event', value);
    const area = (areas: string[]) => ({ areas, branchIds: [], lineIds: [], tokenIds: [], requiresFullRefresh: false });
    emit({ type: 'project.changed', summary: { projectSessionId: 'p1', projectRevision: '5', isDirty: true }, changeSet: area(['presetApplications', 'renderInputs']) });
    await settle(session);
    expect(reads()).toBe(before);
    expect(session.state.summary.projectRevision).toBe('5');
    // 混有其他区域：整体重读。
    emit({ type: 'project.changed', summary: { projectSessionId: 'p1', projectRevision: '6' }, changeSet: area(['orchestration', 'project']) });
    await settle(session);
    expect(fake.calls.slice(-4).map((c) => c.url.pathname.split('/').slice(4).join('/'))).toEqual(['p1', 'p1/lyrics/branches', 'p1/settings', 'p1/lyrics/window']);
  });

  it('建议选择在刷新后应用；刷新完成前用户已改选择时不覆盖用户的选择', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    installFakeProject(fake, { editable: true });
    const session = await EditorSession.attach(await connect(fake), 'p1');
    disposables.push(session);
    const target = { branchId: 'primary', lineId: 'l1', tokenId: 't1' };

    session.select({ lineId: 'l1', tokenId: 't1' });
    await session.command({ type: 'lyrics.splitToken', target });
    await settle(session);
    expect(session.state.selection).toEqual({ lineId: 'l1', tokenId: 't2' });

    session.select({ lineId: 'l1', tokenId: 't1' });
    await session.command({ type: 'lyrics.splitToken', target });
    session.select({ lineId: 'l1' });
    await settle(session);
    expect(session.state.selection).toEqual({ lineId: 'l1' });
  });

  it('重读到的摘要比命令响应旧时，内容照常更新，修订与撤销状态不回退', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const project = installFakeProject(fake, { editable: true });
    const session = await EditorSession.attach(await connect(fake), 'p1');
    disposables.push(session);
    project.reportAhead = true;
    await session.command({ type: 'lyrics.setLineText', target: { branchId: 'primary', lineId: 'l1' }, text: '朝の' });
    await settle(session);
    expect(session.state.linesRevision).toBe('1');
    expect(session.state.summary.projectRevision).toBe('2');
  });

  it('整体重读途中用户换了来源：读完后保留用户选的来源', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const project = installFakeProject(fake, { editable: true });
    project.translation = true;
    const session = await EditorSession.attach(await connect(fake), 'p1');
    disposables.push(session);
    expect(session.state.branchId).toBe('primary');
    project.onWindowRead = (branchId) => {
      if (branchId !== 'primary') return;
      project.onWindowRead = undefined;
      session.selectBranch('trans');
    };
    await session.refresh();
    expect(session.state.branchId).toBe('trans');
    expect(session.state.lines).toEqual([]);
  });

  it('applyEdits 每 200 条一批、每批一次请求；某一批被拒绝就停下，返回已提交的条数与批数', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const project = installFakeProject(fake, { editable: true });
    const session = await EditorSession.attach(await connect(fake), 'p1');
    disposables.push(session);
    const edits = Array.from({ length: 450 }, (_, i) => ({ type: 'lyrics.removeLine' as const, target: { branchId: 'primary', lineId: `l${i}` } }));
    expect(await session.applyEdits(edits)).toEqual({ applied: 450, batches: 3, total: 3 });
    expect(project.editBatches).toEqual([200, 200, 50]);
    await settle(session);

    project.editBatches = [];
    project.rejectBatch = 2;
    expect(await session.applyEdits(edits)).toEqual({ applied: 200, batches: 1, total: 3 });
    expect(project.editBatches).toEqual([200, 200]);
    expect(session.state.notice?.level).toBe('error');
    expect(await session.applyEdits([])).toEqual({ applied: 0, batches: 0, total: 0 });
  });

  it('revisionConflict：提示工程已在别处修改并重读，返回 undefined', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const project = installFakeProject(fake);
    const session = await EditorSession.attach(await connect(fake), 'p1');
    disposables.push(session);
    project.conflictNext = true;
    const result = await session.command({ type: 'history.undo' });
    expect(result).toBeUndefined();
    await settle(session);
    expect(session.state.notice).toMatchObject({ level: 'warning', code: 'revisionConflict' });
  });

  it('读取途中修订变化（409）时从摘要重来；修订每次都前进就一直重试（连续快速撤销），同一修订再冲突才提示错误', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const project = installFakeProject(fake, { editable: true });
    project.windowConflicts = 1;
    const session = await EditorSession.attach(await connect(fake), 'p1');
    disposables.push(session);
    expect(session.state.linesRevision).toBe('1');
    expect(session.state.notice).toBeUndefined();

    project.windowConflicts = 10;
    await session.refresh();
    expect(session.state.linesRevision).toBe('11');
    expect(session.state.notice).toBeUndefined();

    project.windowStuck = true;
    const before = fake.calls.length;
    await session.refresh();
    expect(session.state.notice?.level).toBe('error');
    expect(fake.calls.slice(before).filter((c) => c.path === '/api/v1/project-sessions/p1')).toHaveLength(2);
  });
});

describe('selectionHint', () => {
  const base = {
    projectSessionId: 'p1',
    previousRevision: '1',
    projectRevision: '2',
    changed: true,
    isDirty: true,
    canUndo: true,
    canRedo: false,
    changeSet: { areas: [], branchIds: [], lineIds: [], tokenIds: [], requiresFullRefresh: true },
    protocolVersion: '1' as const,
  } as unknown as import('../../src/protocol').ProjectCommandResponse;

  it('正文／行操作：选中新行，或清空选择', () => {
    expect(selectionHint({ ...base, feedback: { selection: { branchId: 'b', hasSelection: true, lineId: 'l2' }, createdLineId: 'l2' } })).toEqual({
      branchId: 'b',
      selection: { lineId: 'l2' },
    });
    expect(selectionHint({ ...base, feedback: { selection: { branchId: 'b', hasSelection: false } } })).toEqual({ branchId: 'b', selection: null });
    expect(selectionHint({ ...base, feedback: { preservedTokenCount: 3, resetTokenCount: 1 } })).toBeUndefined();
  });

  it('文本单元结构：选中元素或只选行', () => {
    expect(
      selectionHint({ ...base, tokenResult: { changedLineCount: 1, selection: { branchId: 'b', lineId: 'l', hasTokenSelection: true, tokenId: 't9' } } }),
    ).toEqual({ branchId: 'b', selection: { lineId: 'l', tokenId: 't9' } });
    expect(
      selectionHint({ ...base, tokenResult: { changedLineCount: 1, selection: { branchId: 'b', lineId: 'l', hasTokenSelection: false } } }),
    ).toEqual({ branchId: 'b', selection: { lineId: 'l' } });
  });

  it('project.applyEdits：取最后一条带建议的结果', () => {
    const removed = (lineId: string) => ({ index: 0, type: 'lyrics.removeLine', changed: true, feedback: { selection: { branchId: 'b', hasSelection: true, lineId } } });
    expect(selectionHint({ ...base, editResults: [removed('l3'), removed('l1'), { index: 2, type: 'lyrics.linkPrimaryLine', changed: true }] } as never)).toEqual({
      branchId: 'b',
      selection: { lineId: 'l1' },
    });
    expect(selectionHint({ ...base, editResults: [{ index: 0, type: 'lyrics.linkPrimaryLine', changed: true }] } as never)).toBeUndefined();
  });

  it('注音片段：加、删时选中建议的片段或只留文本单元；改字、改时间时选中被改的片段', () => {
    const target = { branchId: 'b', lineId: 'l', tokenId: 't', rubyId: 'r' };
    expect(selectionHint({ ...base, rubyResult: { changedSegmentCount: 1, target, selectedSegmentId: 's2', createdSegmentId: 's2', selectionChanged: true } })).toEqual({
      branchId: 'b',
      selection: { lineId: 'l', tokenId: 't', segmentId: 's2' },
    });
    expect(selectionHint({ ...base, rubyResult: { changedSegmentCount: 1, target, selectionChanged: true } })).toEqual({ branchId: 'b', selection: { lineId: 'l', tokenId: 't' } });
    expect(selectionHint({ ...base, rubyResult: { changedSegmentCount: 1, target, selectedSegmentId: 's1', selectionChanged: false } })).toEqual({
      branchId: 'b',
      selection: { lineId: 'l', tokenId: 't', segmentId: 's1' },
    });
    expect(selectionHint({ ...base, rubyResult: { changedSegmentCount: 2, selectionChanged: false } })).toBeUndefined();
    // 按字拆开：几次加片段之后最后一条是改原片段的字，选中原片段。
    const add = (id: string) => ({ index: 0, type: 'lyrics.addRubySegment', changed: true, rubyResult: { changedSegmentCount: 1, target, selectedSegmentId: id, createdSegmentId: id, selectionChanged: true } });
    const setText = { index: 2, type: 'lyrics.setRubySegmentText', changed: true, rubyResult: { changedSegmentCount: 1, target, selectedSegmentId: 's1', selectionChanged: false } };
    expect(selectionHint({ ...base, editResults: [add('s3'), add('s2'), setText] } as never)?.selection).toEqual({ lineId: 'l', tokenId: 't', segmentId: 's1' });
  });

  it('导入与来源：切换到建议来源', () => {
    expect(selectionHint({ ...base, importResult: { lineCount: 3, selectedBranchId: 'tr', selectedLineId: 'tl1' } })).toEqual({
      branchId: 'tr',
      selection: { lineId: 'tl1' },
    });
    expect(selectionHint({ ...base, branchResult: { selectedBranchId: 'tr' } })).toEqual({ branchId: 'tr', selection: null });
  });
});
