// 连真实 Server 的 W0 主链路（设计 §7）：
// 上传 → 新建 → 读歌词 → setTimeRange → undo／redo；SSE 收到 project.changed、断线用 Last-Event-ID 续接、
// 游标缺口走 resync；静态预览一帧并校验 PNG；保存 → 关闭 → 重开；Server 重启后识别 hostRestarted 并重开持久工程。
// 测试按顺序共享状态。

import { createHash } from 'node:crypto';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { MakoClient, MemoryIdentityStore, pngDimensions } from '../../src/client';
import {
  CONTRACT_PRODUCT_VERSION,
  micros,
  type BackendCapabilities,
  type BackendEvent,
  type LyricLine,
  type ProjectSessionSummary,
  type Revision,
} from '../../src/protocol';
import { ContractRecorder, startTestBackend, type TestBackend } from '../support/backend';
import { SAMPLE_LRC, makeWav } from '../support/fixtures';

let backend: TestBackend;
let client: MakoClient;
let capabilities: BackendCapabilities;
const recorder = new ContractRecorder();
const events: BackendEvent[] = [];
const runId = `it-${Date.now().toString(36)}`;

const state = {
  project: undefined as ProjectSessionSummary | undefined,
  originalLines: [] as LyricLine[],
  revision: '0' as Revision,
  handleId: '',
  finalLines: [] as LyricLine[],
};

beforeAll(async () => {
  backend = await startTestBackend('workflow');
  client = await MakoClient.connect(backend.provider, {
    identityStore: new MemoryIdentityStore(),
    environment: null,
    onExchange: recorder.observer,
    jobPollIntervalMs: 1000,
  });
  client.on('event', (event) => events.push(event));
  if (process.env.MAKO_IT_VERBOSE) client.on('stream-log', (l) => console.log('[sse]', new Date().toISOString(), l.kind, l.message, l.cursor ?? ''));
  capabilities = await client.capabilities();
});

afterAll(async () => {
  client?.dispose();
  await backend?.stop();
});

async function until<T>(read: () => T | undefined, what: string, timeoutMs = 15_000): Promise<T> {
  const start = Date.now();
  for (;;) {
    const value = read();
    if (value !== undefined) return value;
    if (Date.now() - start > timeoutMs) throw new Error(`等待超时：${what}`);
    await new Promise((r) => setTimeout(r, 25));
  }
}

const projectId = () => state.project!.projectSessionId;
const changedEvent = (revision: string) =>
  events.find((e) => e.type === 'project.changed' && e.summary?.projectSessionId === projectId() && e.summary.projectRevision === revision);

describe('W0 主链路（真实 Server）', () => {
  it('部署具备音频准备能力（新建有音频工程的前提）', () => {
    expect(capabilities.productVersion).toBe(CONTRACT_PRODUCT_VERSION);
    expect(capabilities.audioPreparation?.enabled, capabilities.audioPreparation?.disabledReason).toBe(true);
  });

  it('上传音频与中文文件名歌词，SHA-256 与本地一致', async () => {
    const wav = makeWav();
    const audio = await client.assets.upload(new Blob([wav]), { fileName: `${runId} 音频.wav` });
    const lyrics = await client.assets.upload(new Blob([SAMPLE_LRC]), { fileName: '测试歌词.lrc' });
    expect(audio.sha256).toBe(createHash('sha256').update(wav).digest('hex'));
    expect(audio.contentType).toBe('audio/wav');
    expect(lyrics.fileName).toBe('测试歌词.lrc');
    expect(lyrics.sha256).toBe(createHash('sha256').update(SAMPLE_LRC).digest('hex'));

    // 鉴权读取刚上传的字节：完整读取、Range 读取与 If-None-Match 命中 304。
    const full = await client.assets.fetchContent(lyrics.assetId);
    expect(await full.blob?.text()).toBe(SAMPLE_LRC);
    const partial = await client.assets.fetchContent(lyrics.assetId, { range: 'bytes=0-9' });
    expect(partial.status).toBe(206);
    expect(partial.blob?.size).toBe(10);
    const cached = await client.assets.fetchContent(lyrics.assetId, { ifNoneMatch: full.etag! });
    expect(cached.status).toBe(304);
    expect(cached.blob).toBeNull();

    state.project = await client.projects.create({
      name: `W0 集成测试 ${runId}`,
      audioAssetId: audio.assetId,
      lyricsAssetId: lyrics.assetId,
      composition: { canvasWidth: 320, canvasHeight: 180, frameRateNumerator: 30, frameRateDenominator: 1, outputDurationUs: 2_000_000 },
    });
    expect(state.project).toMatchObject({ projectRevision: '0', isDirty: true, hasPath: false, lyricsLineCount: 2 });
    expect(state.project.audioDurationUs).toBeGreaterThan(1_900_000);
    await until(
      () => events.find((e) => e.type === 'project.opened' && e.summary?.projectSessionId === projectId()),
      'project.opened 事件',
    );
  });

  it('读歌词：未打轴为 {}，中文正文完整', async () => {
    const lyrics = await client.projects.lyrics(projectId(), 'primary');
    expect(lyrics.projectRevision).toBe('0');
    expect(lyrics.lines.map((l) => l.text)).toEqual(['你好，协议控制台', 'Second line']);
    state.originalLines = lyrics.lines;
  });

  it('setTimeRange → undo → redo：修订递增，时间投影精确，SSE 收到 project.changed', async () => {
    const line = state.originalLines[0]!;
    const set = await client.projects.setTimeRange(projectId(), { branchId: 'primary', lineId: line.id }, micros(100_000), micros(900_000));
    expect(set).toMatchObject({ previousRevision: '0', projectRevision: '1', changed: true, canUndo: true });
    expect(set.changeSet.lineIds).toContain(line.id);
    let now = await client.projects.lyrics(projectId());
    expect(now.lines[0]?.time).toEqual({ startUs: '100000', endUs: '900000' });

    const undo = await client.projects.undo(projectId());
    expect(undo.projectRevision).toBe('2');
    now = await client.projects.lyrics(projectId());
    expect(now.lines[0]?.time).toEqual(line.time);

    const redo = await client.projects.redo(projectId());
    expect(redo).toMatchObject({ projectRevision: '3', canRedo: false });
    now = await client.projects.lyrics(projectId());
    expect(now.lines[0]?.time).toEqual({ startUs: '100000', endUs: '900000' });

    const history = await client.projects.history(projectId());
    expect(history).toMatchObject({ projectRevision: '3', canUndo: true, canRedo: false });
    expect(client.projects.knownRevision(projectId())).toBe('3');
    await until(() => changedEvent('3'), 'project.changed（修订 3）');
    state.revision = '3' as Revision;
  });

  it('领域 no-op：同样的时间再设一次，修订不变', async () => {
    const line = state.originalLines[0]!;
    const same = await client.projects.setTimeRange(projectId(), { branchId: 'primary', lineId: line.id }, micros(100_000), micros(900_000));
    expect(same).toMatchObject({ changed: false, projectRevision: '3' });
    expect(client.projects.knownRevision(projectId())).toBe('3');
  });

  it('并发发出的命令被串行化，不产生 revisionConflict', async () => {
    const line = state.originalLines[1]!;
    const results = await Promise.all([
      client.projects.setTimeRange(projectId(), { branchId: 'primary', lineId: line.id }, micros(1_000_000), micros(1_500_000)),
      client.projects.undo(projectId()),
      client.projects.redo(projectId()),
    ]);
    expect(results.map((r) => r.projectRevision)).toEqual(['4', '5', '6']);
    state.revision = '6' as Revision;
  });

  it('SSE 断线期间的事件在重连时用 Last-Event-ID 补回', async () => {
    await until(() => changedEvent('6'), 'project.changed（修订 6）');
    const seqBefore = events.map((e) => BigInt(e.eventSeq));
    client.disconnectEvents();
    await client.projects.undo(projectId()); // 修订 7，断线期间发生
    expect(changedEvent('7')).toBeUndefined();
    client.connectEvents();
    await until(() => changedEvent('7'), '断线期间的 project.changed（修订 7）');
    await client.projects.redo(projectId()); // 修订 8
    await until(() => changedEvent('8'), 'project.changed（修订 8）');
    const seqs = events.map((e) => BigInt(e.eventSeq));
    expect(seqs.length).toBeGreaterThan(seqBefore.length);
    // 没有重复、严格递增。
    for (let i = 1; i < seqs.length; i++) expect(seqs[i]! > seqs[i - 1]!).toBe(true);
    state.revision = '8' as Revision;
  });

  it('人为制造游标缺口：服务端发 resync.required，客户端重读 client state 后续接', async () => {
    const resyncs: string[] = [];
    const off = client.on('resync', (r) => resyncs.push(r.state.eventCursor));
    client.reconnectEvents('999999999');
    await until(() => (resyncs.length ? true : undefined), 'resync');
    off();
    // 不等待 status=open：没有可重放事件时服务端要到第一次 heartbeat 才发响应头（设计 §9 G7）。
    await client.projects.undo(projectId()); // 修订 9
    await until(() => changedEvent('9'), 'resync 后的 project.changed（修订 9）');
    await client.projects.redo(projectId()); // 修订 10
    state.revision = '10' as Revision;
  });

  it('静态预览一帧：PNG 尺寸、大小与 SHA-256 与元数据一致', async (ctx) => {
    const runtime = await client.preview.runtimeInfo();
    if (!runtime.enabled) {
      console.warn(`[skipped] 预览不可用：${runtime.disabledReason?.code ?? runtime.status}`);
      ctx.skip();
    }
    const still = await client.preview.renderStill({
      projectSessionId: projectId(),
      sourceRevision: state.revision,
      timeUs: micros(500_000),
      size: { width: 320, height: 180 },
    });
    expect(still.verified).toBe(true);
    expect(still.frame).toMatchObject({ sourceRevision: state.revision, requestSeq: '1', width: 320, height: 180 });
    const bytes = new Uint8Array(await still.blob.arrayBuffer());
    expect(pngDimensions(bytes)).toEqual({ width: 320, height: 180 });
    expect(createHash('sha256').update(bytes).digest('hex')).toBe(still.frame.content.sha256);
    // 预览已关闭：GET 返回 404。
    await expect(client.preview.get(still.preview.previewId)).rejects.toMatchObject({ status: 404 });
  });

  it('保存到授权目录 → 关闭 → 重开：各自等到成功终态，内容一致', async () => {
    const roots = await client.files.roots();
    expect(roots.rootGrantIds).toContain('projects');
    const handle = await client.files.createHandle({ rootGrantId: 'projects', relativePath: runId, access: 'readWrite' });
    expect(handle.canWrite).toBe(true);
    state.handleId = handle.handleId;
    const before = await client.projects.lyrics(projectId());

    const saveJob = await client.projects.save(projectId(), { targetHandleId: handle.handleId });
    const saved = await client.jobs.waitForTerminal(saveJob.jobId);
    expect(saved).toMatchObject({ state: 'succeeded', kind: 'save', savedProjectRevision: state.revision });

    const summary = await client.projects.get(projectId());
    expect(summary).toMatchObject({ isDirty: false, hasPath: true, savedProjectRevision: state.revision });

    const closeJob = await client.projects.close(projectId(), { policy: 'requireClean', activeJobs: 'wait' });
    expect((await client.jobs.waitForTerminal(closeJob.jobId)).state).toBe('succeeded');
    await expect(client.projects.get(projectId())).rejects.toMatchObject({ code: 'sessionNotFound' });

    const openJob = await client.projects.open({ projectDirectoryHandleId: handle.handleId, access: 'readWrite' });
    const opened = await client.jobs.waitForTerminal(openJob.jobId);
    expect(opened.state).toBe('succeeded');
    const reopened = opened.result!;
    expect(reopened).toMatchObject({ isDirty: false, hasPath: true, isReadOnly: false });
    const after = await client.projects.lyrics(reopened.projectSessionId);
    expect(after.lines).toEqual(before.lines);
    state.project = reopened;
    state.finalLines = after.lines;
    const types = new Set(events.map((e) => e.type));
    for (const type of ['project.saved', 'project.closed', 'job.terminal']) expect(types).toContain(type);
  });

  it('Server 重启：旧地址不可达时识别 hostRestarted、清空身份；重新初始化后打开持久工程', async () => {
    const resets: string[] = [];
    client.on('reset', (r) => resets.push(r.reason));
    const oldHost = client.state$.get().hostInstanceId;
    // 先关闭工程释放目录写租约，再重启。
    const closeJob = await client.projects.close(projectId(), { policy: 'requireClean' });
    expect((await client.jobs.waitForTerminal(closeJob.jobId)).state).toBe('succeeded');

    // 先断开事件流：Server 0.75.1 的优雅关闭会等待仍打开的 SSE 最多 30 秒（设计 §9 G8）。
    // 带着事件流重启的场景由浏览器冒烟（scripts/browser-smoke.mjs --manage-backend）覆盖。
    client.disconnectEvents();
    await backend.restart();
    await client.heartbeat(); // 旧地址连不上 → 心跳失败 → 核对启动信息
    await until(() => (client.state$.get().state === 'hostRestarted' ? true : undefined), 'hostRestarted');
    expect(resets.length).toBeGreaterThan(0);
    expect(client.state$.get().clientId).toBeUndefined();

    await client.reinitialize('测试：Server 重启后重新初始化');
    const fresh = client.state$.get();
    expect(fresh).toMatchObject({ state: 'ready', origin: 'registered' });
    expect(fresh.hostInstanceId).not.toBe(oldHost);

    const handle = await client.files.createHandle({ rootGrantId: 'projects', relativePath: runId, access: 'readWrite' });
    const opened = await client.jobs.waitForTerminal((await client.projects.open({ projectDirectoryHandleId: handle.handleId })).jobId);
    expect(opened.state).toBe('succeeded');
    const lines = await client.projects.lyrics(opened.result!.projectSessionId);
    expect(lines.lines).toEqual(state.finalLines);
    const close = await client.projects.close(opened.result!.projectSessionId, { policy: 'requireClean' });
    expect((await client.jobs.waitForTerminal(close.jobId)).state).toBe('succeeded');
  });

  it('全部 JSON 响应符合冻结 OpenAPI 1.0.0', () => {
    expect(recorder.issues).toEqual([]);
    // 覆盖本链路调用的全部 JSON 路由（成功与错误响应）。
    expect(recorder.validated.size).toBeGreaterThanOrEqual(18);
  });
});
