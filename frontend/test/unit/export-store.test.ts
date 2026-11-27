import { afterEach, describe, expect, it, vi } from 'vitest';
import { OutcomeUnknownError, type MakoClient } from '../../src/client';
import { ExportStore } from '../../src/editor/export/export-store';
import type { EditorSession } from '../../src/editor/session/editor-session';
import { micros, revision, type ExportReport, type IsoDateTime, type JobResponse } from '../../src/protocol';

const PROJECT_ID = 'session-arcadia';
const EXPIRES = '2026-11-26T15:00:00Z' as IsoDateTime;

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

function makeJob(overrides: Partial<JobResponse> = {}): JobResponse {
  return {
    jobId: 'job-1',
    kind: 'export',
    state: 'running',
    projectSessionId: PROJECT_ID,
    expiresAtUtc: EXPIRES,
    cancellationRequested: false,
    canCancel: true,
    ...overrides,
  };
}

function makeExportReport(): ExportReport {
  return {
    format: 'opaqueMp4',
    audioIncluded: true,
    expectedFrames: 30,
    decodedFrames: 30,
    framesWritten: 30,
    droppedFrames: 0,
    failedFrames: 0,
    width: 1920,
    height: 1080,
    frameRateNumerator: 30,
    frameRateDenominator: 1,
    derivedVideoDurationUs: micros(1_000_000),
    actualVideoDurationUs: micros(1_000_000),
    audioDurationUs: micros(1_000_000),
    videoCodec: 'h264',
    audioCodec: 'aac',
    pixelFormat: 'yuv420p',
    hasAlpha: false,
    mediaSize: 2048,
    sha256: 'a'.repeat(64),
    warnings: [],
    diagnostics: [],
    isVerified: true,
    actualAudioDurationUs: micros(1_000_000),
    strictlyVerified: true,
    requestedQuality: 'standard',
    actualQuality: 'standard',
  };
}

function makeSession(): EditorSession {
  return {
    projectSessionId: PROJECT_ID,
    supports: () => false,
  } as unknown as EditorSession;
}

function makeClient(overrides: {
  exportVideo?: ReturnType<typeof vi.fn>;
  clientState?: ReturnType<typeof vi.fn>;
  readJob?: ReturnType<typeof vi.fn>;
  waitForTerminal?: ReturnType<typeof vi.fn>;
  exportResult?: ReturnType<typeof vi.fn>;
  renderStills?: ReturnType<typeof vi.fn>;
} = {}) {
  const exportVideo = overrides.exportVideo ?? vi.fn();
  const clientState = overrides.clientState ?? vi.fn();
  const readJob = overrides.readJob ?? vi.fn();
  const waitForTerminal = overrides.waitForTerminal ?? vi.fn();
  const exportResult = overrides.exportResult ?? vi.fn();
  const renderStills = overrides.renderStills ?? vi.fn();
  const client = {
    exportCapabilities: vi.fn().mockResolvedValue({ enabled: true, formats: ['opaqueMp4'], qualities: ['standard'] }),
    validationCapabilities: vi.fn().mockResolvedValue({ enabled: true, maxFramesPerOutput: 30 }),
    clientState,
    projects: {
      exportVideo,
      validate: vi.fn(),
    },
    jobs: {
      get: readJob,
      waitForTerminal,
      exportResult,
      validationResult: vi.fn(),
      cancel: vi.fn(),
      exportContent: vi.fn(),
    },
    preview: {
      renderStills,
    },
  } as unknown as MakoClient;
  return { client, exportVideo, clientState, readJob, waitForTerminal, exportResult, renderStills };
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe('ExportStore 提交反馈与结果未知核对', () => {
  it('点击开始导出立即进入 submitting 状态并阻止重复并发提交', async () => {
    const createGate = deferred<JobResponse>();
    const terminalJob = makeJob({ jobId: 'job-export-1', state: 'succeeded' });
    const { client, exportVideo } = makeClient({
      exportVideo: vi.fn(() => createGate.promise),
      waitForTerminal: vi.fn().mockResolvedValue(terminalJob),
      exportResult: vi.fn().mockResolvedValue(makeExportReport()),
    });
    const store = new ExportStore(client, makeSession());

    const first = store.startExport(true);
    expect(store.state.submitting?.export).toBe('submitting');
    expect(store.running('export')).toBe(true);

    // 重复点击在提交未返回前必须被忽略
    await store.startExport(true);
    expect(exportVideo).toHaveBeenCalledTimes(1);

    createGate.resolve(makeJob({ jobId: 'job-export-1', state: 'queued' }));
    await first;

    expect(store.state.submitting).toBeUndefined();
    expect(store.state.exportJob?.job.jobId).toBe('job-export-1');
    expect(store.state.exportJob?.exportReport?.framesWritten).toBe(30);
    expect(store.state.exportJob?.settled).toBe(true);
    store.dispose();
  });

  it('提交返回 OutcomeUnknownError 时向权威状态核对并自动接管后端运行中的导出任务', async () => {
    const stateGate = deferred<{ clientId: string; hostInstanceId: string; dormant: boolean; leaseExpiresAtUtc: IsoDateTime; eventCursor: string; sessions: []; jobs: { jobId: string; kind: 'export'; state: 'running'; projectSessionId: string; canCancel: boolean; expiresAtUtc: IsoDateTime }[] }>();
    const activeJob = makeJob({ jobId: 'job-recovered-1', state: 'running' });
    const succeededJob = makeJob({ jobId: 'job-recovered-1', state: 'succeeded' });
    const { client, clientState, readJob, waitForTerminal } = makeClient({
      exportVideo: vi.fn().mockRejectedValue(new OutcomeUnknownError('export-req-1', new Error('timeout'))),
      clientState: vi.fn(() => stateGate.promise),
      readJob: vi.fn().mockResolvedValue(activeJob),
      waitForTerminal: vi.fn().mockResolvedValue(succeededJob),
      exportResult: vi.fn().mockResolvedValue(makeExportReport()),
    });
    const store = new ExportStore(client, makeSession());

    const run = store.startExport(true);
    await Promise.resolve();
    expect(store.state.submitting?.export).toBe('reconciling');
    expect(clientState).toHaveBeenCalledTimes(1);

    stateGate.resolve({
      clientId: 'c1',
      hostInstanceId: 'h1',
      dormant: false,
      leaseExpiresAtUtc: EXPIRES,
      eventCursor: '1',
      sessions: [],
      jobs: [{
        jobId: 'job-recovered-1',
        kind: 'export',
        state: 'running',
        projectSessionId: PROJECT_ID,
        canCancel: true,
        expiresAtUtc: EXPIRES,
      }],
    });
    await run;

    expect(readJob).toHaveBeenCalledWith('job-recovered-1', expect.any(AbortSignal));
    expect(waitForTerminal).toHaveBeenCalledWith('job-recovered-1', expect.any(Object));
    expect(store.state.submitting).toBeUndefined();
    expect(store.state.submitError).toBeUndefined();
    expect(store.state.exportJob?.job.jobId).toBe('job-recovered-1');
    expect(store.state.exportJob?.exportReport?.framesWritten).toBe(30);
    store.dispose();
  });

  it('提交返回 OutcomeUnknownError 且后端无进行中任务时清晰提示可安全重试', async () => {
    const { client, clientState, readJob } = makeClient({
      exportVideo: vi.fn().mockRejectedValue(new OutcomeUnknownError('export-req-2', new Error('timeout'))),
      clientState: vi.fn().mockResolvedValue({
        clientId: 'c1',
        hostInstanceId: 'h1',
        dormant: false,
        leaseExpiresAtUtc: EXPIRES,
        eventCursor: '1',
        sessions: [],
        jobs: [],
      }),
    });
    const store = new ExportStore(client, makeSession());

    await store.startExport(false);

    expect(clientState).toHaveBeenCalledTimes(1);
    expect(readJob).not.toHaveBeenCalled();
    expect(store.state.submitting).toBeUndefined();
    expect(store.state.submitError?.kind).toBe('export');
    expect(store.state.submitError?.text).toContain('已向后端核对权威状态');
    expect(store.state.submitError?.text).toContain('安全重试');
    store.dispose();
  });

  it('导出终态失败时保留具体 failureCode 说明而不是只显示泛化兜底文案', async () => {
    const failedJob = makeJob({
      jobId: 'job-fail-1',
      state: 'failed',
      failureCode: 'CaptureBudgetExceeded',
      error: { code: 'taskExecutionFailed', message: '独立导出执行器失败，未公开新的成片。', protocolVersion: '1' },
    });
    const { client } = makeClient({
      exportVideo: vi.fn().mockResolvedValue(makeJob({ jobId: 'job-fail-1', state: 'queued' })),
      waitForTerminal: vi.fn().mockResolvedValue(failedJob),
    });
    const store = new ExportStore(client, makeSession());

    await store.startExport(true);

    expect(store.state.exportJob?.error).toContain('CaptureBudgetExceeded');
    expect(store.state.exportJob?.error).toContain('预算限制');
    store.dispose();
  });

  it('首末帧缩略图通过单次 renderStills 复用预览会话而不是并发创建两个会话', async () => {
    const createObjectURL = vi.spyOn(URL, 'createObjectURL').mockReturnValueOnce('blob:first').mockReturnValueOnce('blob:last');
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
    const { client, renderStills } = makeClient({
      renderStills: vi.fn().mockResolvedValue([{ blob: new Blob(['a']) }, { blob: new Blob(['b']) }]),
    });
    const store = new ExportStore(client, makeSession());

    store.loadThumbnails(revision('3'), { width: 384, height: 216 }, 5_000_000);
    await vi.waitFor(() => expect(store.state.thumbnails?.loading).toBe(false));

    expect(renderStills).toHaveBeenCalledTimes(1);
    expect(renderStills).toHaveBeenCalledWith(expect.objectContaining({
      projectSessionId: PROJECT_ID,
      sourceRevision: revision('3'),
      timesUs: [micros(0), micros(5_000_000)],
      size: { width: 384, height: 216 },
    }));
    expect(createObjectURL).toHaveBeenCalledTimes(2);
    expect(store.state.thumbnails?.first).toBe('blob:first');
    expect(store.state.thumbnails?.last).toBe('blob:last');
    store.dispose();
  });
});
