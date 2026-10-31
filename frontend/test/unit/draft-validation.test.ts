// 作者草稿的验证页签（W8 设计 §2.7）：运行条件（改过的、跟随画布、默认）、问题的去重与位置、其余诊断、结论文字、需重新验证；
// 验证任务：提交（设备、409 重读）、进度、报告、没有报告时的原因、取消、刷新后重读上一次的任务。

import { describe, expect, it } from 'vitest';
import { BackendError } from '../../src/client';
import type { FullDraft } from '../../src/editor/library/author-draft-store';
import {
  DraftValidationModel,
  extraDiagnostics,
  validationConditions,
  validationIssueRows,
  validationOutcomeText,
  validationStale,
  type DraftValidationPort,
} from '../../src/editor/library/draft-validation';
import { micros, type AuthorDraftSampleOptions, type AuthorValidationInput, type JobResponse, type ValidationIssue, type ValidationReport } from '../../src/protocol';

const target = (key: string) => ({ key, binding: { kind: key, ids: [], includeDescendants: false } });

const EFFECT: AuthorDraftSampleOptions = {
  samples: [
    { sampleId: 'workshop-single-line-v1', multiLine: false, durationUs: '12000000', targets: [target('line'), target('token')] },
    { sampleId: 'workshop-multi-line-v1', multiLine: true, durationUs: '12000000', targets: [target('line')] },
  ],
  materialSlots: [],
  defaultSampleId: 'workshop-single-line-v1',
};
const MATERIAL: AuthorDraftSampleOptions = { ...EFFECT, materialSlots: ['sungFill', 'unsungFill', 'stroke'] };

function draft(patch: Partial<FullDraft> = {}): FullDraft {
  return {
    draftId: 'd1',
    draftRevision: 'r1',
    name: '逐字强调',
    packageId: 'demo.fx',
    baseSourceRevision: 'sha256:base',
    currentSourceRevision: 'sha256:base',
    exportId: 'word',
    canCompile: true,
    canAdopt: true,
    configuration: {
      name: '逐字强调',
      packageId: 'demo.fx',
      packageRevision: 'sha256:base',
      sourceRevision: 'sha256:base',
      exportId: 'word',
      parametersJson: '{}',
      relativeTarget: 'tokenFamily',
      readScope: 'boundSources',
      overlayLayer: 'package',
      kind: 'Effect',
      bindingKinds: ['lines', 'tokens'],
    },
    sampleOptions: EFFECT,
    protocolVersion: '1',
    ...patch,
  };
}

const compileIssue = (instance: string): ValidationIssue => ({
  transparent: false,
  timeUs: micros('0'),
  code: 'CS0103',
  message: null,
  isError: true,
  location: null,
  sampleFailureCode: null,
  diagnostic: {
    code: 'CS0103',
    severity: 'error',
    message: "The name 'contxt' does not exist in the current context",
    location: 'src/WordEmphasis.cs',
    originKind: 'compileSource',
    source: { packageId: 'demo.fx', revision: 'sha256:v2', instanceId: instance, code: 'CS0103', message: "The name 'contxt' does not exist", isError: true, path: 'src/WordEmphasis.cs', line: 10, column: 16 },
  },
});

/** 运行时异常的问题不带 diagnostic；诊断列表里另有一条同编码、同消息的。 */
const runtimeMessage = (instance: string) => `效果 ${instance}（包 demo.fx，修订 sha256:v2，导出 word） 在 Evaluate 失败：probe boom`;
const runtimeIssue = (transparent: boolean, instance = 'fx:aa11'): ValidationIssue => ({
  transparent,
  timeUs: micros('2008333'),
  code: 'ScriptExecutionFailed',
  message: runtimeMessage(instance),
  isError: true,
  location: null,
  sampleFailureCode: null,
});
const runtimeDiagnostic = (instance = 'fx:aa11') => ({
  transparent: false,
  diagnostic: {
    code: 'ScriptExecutionFailed',
    severity: 'error' as const,
    message: runtimeMessage(instance),
    originKind: 'runtime',
    occurrence: { epoch: 'e', sequence: '1', instanceId: instance, artifactHash: 'h', stage: 'Evaluate', timeUs: '2008333', package: { packageId: 'demo.fx', revision: 'sha256:v2', exportId: 'word' } },
  },
});

function report(patch: Partial<ValidationReport> = {}): ValidationReport {
  return { succeeded: true, opaqueFrames: 360, transparentFrames: 360, issues: [], diagnostics: [], renderBackend: 'cpu', ...patch };
}

describe('运行条件', () => {
  it('改过的优先，其次跟随画布，再次是默认样本和第一个目标', () => {
    const canvas = { sampleId: 'workshop-multi-line-v1' as const, targetKey: 'line' };
    expect(validationConditions(EFFECT, undefined, undefined)).toEqual({ sampleId: 'workshop-single-line-v1', targetKey: 'line' });
    expect(validationConditions(EFFECT, undefined, canvas)).toEqual(canvas);
    expect(validationConditions(EFFECT, { sampleId: 'workshop-single-line-v1', targetKey: 'token' }, canvas)).toEqual({ sampleId: 'workshop-single-line-v1', targetKey: 'token' });
    // 换样本后原来的目标不在：退回第一个目标。
    expect(validationConditions(EFFECT, { sampleId: 'workshop-multi-line-v1', targetKey: 'token' }, undefined)).toEqual({ sampleId: 'workshop-multi-line-v1', targetKey: 'line' });
  });

  it('没有目标的样本跳过；一个可用样本都没有时不能验证', () => {
    const options: AuthorDraftSampleOptions = { ...EFFECT, samples: [{ ...EFFECT.samples[0]!, targets: [] }, EFFECT.samples[1]!] };
    expect(validationConditions(options, { sampleId: 'workshop-single-line-v1', targetKey: 'line' }, undefined)?.sampleId).toBe('workshop-multi-line-v1');
    expect(validationConditions({ ...EFFECT, samples: [] }, undefined, undefined)).toBeUndefined();
  });

  it('材质草稿必须有部位，默认未唱填充；效果草稿不带部位', () => {
    expect(validationConditions(MATERIAL, undefined, undefined)?.materialSlot).toBe('unsungFill');
    expect(validationConditions(MATERIAL, undefined, { materialSlot: 'stroke' })?.materialSlot).toBe('stroke');
    expect(validationConditions(EFFECT, undefined, { materialSlot: 'stroke' })).not.toHaveProperty('materialSlot');
  });
});

describe('报告', () => {
  it('编译问题按效果实例重复：只留一条，位置取编译诊断，消息取诊断原文', () => {
    const rows = validationIssueRows(report({ succeeded: false, opaqueFrames: 0, transparentFrames: 0, issues: [compileIssue('fx:1'), compileIssue('fx:2')] }));
    expect(rows).toHaveLength(1);
    expect(rows[0]).toMatchObject({
      origin: 'compile',
      code: 'CS0103',
      message: "The name 'contxt' does not exist in the current context",
      location: { path: 'src/WordEmphasis.cs', line: 10, column: 16 },
      script: true,
    });
  });

  it('运行时异常没有行号：普通和透明各一条，不同实例的同一个异常算一条，仍可以到源码页签', () => {
    const rows = validationIssueRows(report({ succeeded: false, issues: [runtimeIssue(false), runtimeIssue(false, 'fx:bb22'), runtimeIssue(true)] }));
    expect(rows.map((r) => r.transparent)).toEqual([false, true]);
    expect(rows[0]).toMatchObject({ origin: 'runtime', timeUs: 2_008_333, script: true });
    expect(rows[0]!.message).toContain('probe boom');
    expect(rows[0]).not.toHaveProperty('location');
  });

  it('诊断里和问题重复的不再列出', () => {
    const other = { transparent: true, diagnostic: { code: 'FrameSlow', severity: 'warning' as const, message: '这一帧较慢' } };
    expect(extraDiagnostics(report({ issues: [runtimeIssue(false)], diagnostics: [runtimeDiagnostic(), other] }))).toEqual([other]);
  });

  it('结论写帧数和问题数', () => {
    expect(validationOutcomeText(report())).toBe('通过 · 普通 360 帧、透明 360 帧');
    expect(validationOutcomeText(report({ succeeded: false, opaqueFrames: 61, transparentFrames: 0, issues: [runtimeIssue(false)] }))).toBe('失败 · 普通 61 帧、透明 0 帧 · 1 个问题');
  });

  it('草稿修订变了才算需要重新验证', () => {
    const job = { authorValidationInput: { draftId: 'd1', draftRevision: 'r1', sourceRevision: 'sha256:base', sampleId: 'workshop-single-line-v1' as const, targetKey: 'line' } };
    expect(validationStale(job, draft())).toBe(false);
    expect(validationStale(job, draft({ draftRevision: 'r2' }))).toBe(true);
    expect(validationStale(undefined, draft())).toBe(false);
  });
});

function job(patch: Partial<JobResponse> = {}): JobResponse {
  return {
    jobId: 'j1',
    kind: 'validation',
    state: 'queued',
    expiresAtUtc: '2026-10-30T00:00:00Z',
    cancellationRequested: false,
    canCancel: true,
    authorValidationInput: { draftId: 'd1', draftRevision: 'r1', sourceRevision: 'sha256:base', sampleId: 'workshop-single-line-v1', targetKey: 'line' },
    ...patch,
  };
}

const RESULT = { passed: true, opaqueFrames: 360, transparentFrames: 360, report: { reportId: 'rp', sizeBytes: 1, sha256: 'x' } };

function fakePort(options: { terminal?: JobResponse; stale?: number; last?: string; stored?: JobResponse | 'missing'; reject?: unknown } = {}) {
  let current = draft();
  let stale = options.stale ?? 0;
  const calls: string[] = [];
  const remembered: string[] = [];
  const inputs: AuthorValidationInput[] = [];
  let finish: ((job: JobResponse) => void) | undefined;
  const port: DraftValidationPort = {
    current: () => current,
    refresh: async () => {
      calls.push('refresh');
      current = draft({ draftRevision: 'r2' });
      return current;
    },
    validate: async (sent, input) => {
      calls.push(`validate ${sent.draftRevision}`);
      if (options.reject) throw options.reject;
      if (stale > 0) {
        stale--;
        throw new BackendError(409, { code: 'revisionConflict', message: '草稿修订已过期' } as never);
      }
      inputs.push(input);
      return job();
    },
    job: async (jobId) => {
      calls.push(`job ${jobId}`);
      if (options.stored === 'missing') throw new BackendError(404, { code: 'jobNotFound', message: '没有这个任务' } as never);
      return options.stored ?? job();
    },
    waitJob: (jobId, onProgress) => {
      calls.push(`wait ${jobId}`);
      onProgress({ ...job(), state: 'running', progress: { stage: 'rendering', completedFrames: 30, totalFrames: 360 } });
      if (options.terminal) return Promise.resolve(options.terminal);
      return new Promise((resolve) => (finish = resolve));
    },
    cancelJob: async (jobId) => {
      calls.push(`cancel ${jobId}`);
    },
    report: async (jobId) => {
      calls.push(`report ${jobId}`);
      return report();
    },
    renderBackend: () => ({ renderBackend: 'gpu' }),
    lastJob: () => options.last,
    rememberJob: (jobId) => remembered.push(jobId),
  };
  return { port, calls, remembered, inputs, finish: (j: JobResponse) => finish?.(j) };
}

describe('验证任务', () => {
  it('配置提交完成前只发起一次验证；草稿关闭后晚到任务取消且不写回页面', async () => {
    const fake = fakePort();
    let prepared!: () => void;
    let accepted!: (job: JobResponse) => void;
    fake.port.prepare = () => new Promise<void>((resolve) => (prepared = resolve));
    fake.port.validate = () => new Promise<JobResponse>((resolve) => (accepted = resolve));
    const model = new DraftValidationModel(fake.port);
    const conditions = { sampleId: 'workshop-single-line-v1' as const, targetKey: 'line' };
    const started = model.start(conditions);
    await model.start(conditions);
    expect(model.state.submitting).toBe(true);
    prepared();
    await Promise.resolve();
    model.dispose();
    accepted(job());
    await started;
    expect(fake.calls).toEqual(['cancel j1']);
    expect(fake.remembered).toEqual([]);
    expect(model.state.job).toBeUndefined();
  });

  it('任务读取中断后重读原任务，避免重新运行验证', async () => {
    const fake = fakePort({ last: 'j1', stored: job({ state: 'succeeded', validationResult: RESULT }) });
    const getJob = fake.port.job;
    fake.port.job = async () => { throw new Error('暂时离线'); };
    const model = new DraftValidationModel(fake.port);
    await model.restore();
    expect(model.state.retryable).toBe(true);
    expect(model.running).toBe(true);
    fake.port.job = getJob;
    await model.retry();
    expect(fake.calls).toEqual(['job j1', 'report j1']);
    expect(model.state).toMatchObject({ settled: true, report: { succeeded: true } });
  });

  it('提交带上样本、目标和设备，记下任务；跑完读报告', async () => {
    const fake = fakePort({ terminal: job({ state: 'succeeded', validationResult: RESULT }) });
    const model = new DraftValidationModel(fake.port);
    await model.start({ sampleId: 'workshop-single-line-v1', targetKey: 'token' });
    expect(fake.inputs).toEqual([{ sampleId: 'workshop-single-line-v1', targetKey: 'token', renderBackend: 'gpu' }]);
    expect(fake.remembered).toEqual(['j1']);
    expect(fake.calls).toEqual(['validate r1', 'wait j1', 'report j1']);
    expect(model.state).toMatchObject({ settled: true, report: { succeeded: true } });
    expect(model.running).toBe(false);
  });

  it('草稿修订过期（409）时重读草稿再提交一次', async () => {
    const fake = fakePort({ stale: 1, terminal: job({ state: 'succeeded', validationResult: RESULT }) });
    await new DraftValidationModel(fake.port).start({ sampleId: 'workshop-single-line-v1', targetKey: 'line' });
    expect(fake.calls.slice(0, 3)).toEqual(['validate r1', 'refresh', 'validate r2']);
  });

  it('运行中显示进度，可以取消；取消后没有报告，写明已取消', async () => {
    const fake = fakePort();
    const model = new DraftValidationModel(fake.port);
    const started = model.start({ sampleId: 'workshop-single-line-v1', targetKey: 'line' });
    await Promise.resolve();
    await Promise.resolve();
    expect(model.running).toBe(true);
    expect(model.state.job?.progress).toEqual({ stage: 'rendering', completedFrames: 30, totalFrames: 360 });
    await model.cancel();
    expect(model.state.cancelling).toBe(true);
    fake.finish(job({ state: 'cancelled', canCancel: false }));
    await started;
    expect(fake.calls).toContain('cancel j1');
    expect(model.state).toMatchObject({ settled: true, cancelling: false, error: '已取消。' });
    expect(model.state.report).toBeUndefined();
  });

  it('设备不可用时提交就被拒绝，写明可以改设备', async () => {
    const reject = new BackendError(422, { code: 'renderBackendUnavailable', message: 'GPU 不可用', details: { renderBackend: 'gpu' } } as never);
    const model = new DraftValidationModel(fakePort({ reject }).port);
    await model.start({ sampleId: 'workshop-single-line-v1', targetKey: 'line' });
    expect(model.state.job).toBeUndefined();
    expect(model.state.submitError).toBeTruthy();
  });

  it('刷新后重读上一次的任务；过了保留期就当没验证过', async () => {
    const fake = fakePort({ last: 'j1', stored: job({ state: 'failed', validationResult: { ...RESULT, passed: false } }) });
    const model = new DraftValidationModel(fake.port);
    await model.restore();
    await model.restore();
    expect(fake.calls).toEqual(['job j1', 'report j1']);
    expect(model.state.settled).toBe(true);

    const gone = new DraftValidationModel(fakePort({ last: 'j1', stored: 'missing' }).port);
    await gone.restore();
    expect(gone.state).toEqual({ cancelling: false, settled: false });
  });
});
