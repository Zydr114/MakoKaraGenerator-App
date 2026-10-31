// 作者草稿的验证页签（W8 设计 §2.7）：在一个工坊样本上按固定规格跑不透明和透明两个输出，读报告，列出问题。不依赖 React。
// 运行条件默认跟随试样画布当前的样本、目标和材质部位，也可以在页签里改；渲染设备按顶栏偏好。
// 编译不过时任务失败、0 帧，编译诊断在报告的 issues 里，诊断接口是空的（§0）；所以只读完整报告，问题和诊断都从报告里取。
// 最近一次验证的任务 ID 按草稿记在 sessionStorage，刷新后在任务保留期内能重读报告。

import { BackendError, ObservableValue } from '../../client';
import type {
  AuthorDraftSampleOptions,
  AuthorValidationInput,
  JobResponse,
  JobSummary,
  RenderBackend,
  TemplateMaterialSlot,
  TemplateWorkshopSampleId,
  ValidationDiagnostic,
  ValidationReport,
} from '../../protocol';
import { failureText, isTerminal } from '../model/render-job';
import { renderBackendErrorText } from '../preview/render-device';
import { errorText } from '../session/editor-session';
import type { FullDraft } from './author-draft-store';
import { withFreshDraft, type SourceLocation } from './draft-source';

export const DRAFT_VALIDATIONS_KEY = 'mako.author-draft-validations';

/** 作者验证的固定规格（§0）。 */
export const AUTHOR_VALIDATION_SPEC = '640×360、30 fps、12 秒，不透明和透明各 360 帧';

export interface ValidationConditions {
  sampleId: TemplateWorkshopSampleId;
  targetKey: string;
  /** 材质草稿必填；效果草稿不带。 */
  materialSlot?: TemplateMaterialSlot;
}

export interface DraftValidationState {
  /** 页签里改过的运行条件；没有时跟随试样画布。 */
  chosen?: ValidationConditions;
  /** 最近一次读到的任务（事件摘要合并进来）。 */
  job?: JobResponse;
  cancelling: boolean;
  /** 终态后的报告已读完（或读取失败）。 */
  settled: boolean;
  report?: ValidationReport;
  /** 失败、取消、结果未知或读取失败时的说明。 */
  error?: string;
  /** 发起时就被拒绝。 */
  submitError?: string;
  /** 正在重读上一次的任务（刷新后）。 */
  restoring?: boolean;
  /** 正在提交配置和验证请求，任务尚未返回。 */
  submitting?: boolean;
  /** 状态或报告读取中断，可重读同一个任务。 */
  retryable?: boolean;
}

export interface DraftValidationPort {
  current(): FullDraft | undefined;
  refresh(): Promise<FullDraft>;
  /** 验证前先提交配置页签里等待合并的改动；失败时不运行旧配置。 */
  prepare?(): Promise<void>;
  /** 202：作者验证任务。 */
  validate(draft: FullDraft, input: AuthorValidationInput): Promise<JobResponse>;
  job(jobId: string): Promise<JobResponse>;
  waitJob(jobId: string, onProgress: (job: JobResponse | JobSummary) => void, signal: AbortSignal): Promise<JobResponse>;
  cancelJob(jobId: string): Promise<unknown>;
  report(jobId: string): Promise<ValidationReport>;
  /** 按顶栏偏好；跟随后端默认时不带。 */
  renderBackend(): { renderBackend?: RenderBackend };
  /** 最近一次验证的任务 ID（sessionStorage）。 */
  lastJob(): string | undefined;
  rememberJob(jobId: string): void;
}

type StatePatch = { [K in keyof DraftValidationState]?: DraftValidationState[K] | undefined };

export class DraftValidationModel {
  readonly state$ = new ObservableValue<DraftValidationState>({ cancelling: false, settled: false });
  private readonly abort = new AbortController();
  private disposed = false;
  private restored = false;

  constructor(private readonly port: DraftValidationPort) {}

  get state(): DraftValidationState {
    return this.state$.get();
  }

  /** 正在发起、恢复、运行任务，或终态后报告还没读完。 */
  get running(): boolean {
    const job = this.state.job;
    return !!this.state.submitting || !!this.state.restoring || (!!this.state.retryable && !job) || (!!job && (!isTerminal(job.state) || !this.state.settled));
  }

  /** 在页签里改运行条件；undefined 表示改回跟随试样画布。 */
  choose(conditions: ValidationConditions | undefined): void {
    this.patch({ chosen: conditions });
  }

  /** 刷新后重读上一次的任务：还在跑就接着等，结束了就读报告；过了保留期（404）就当没验证过。只做一次。 */
  async restore(): Promise<void> {
    if (this.disposed || this.restored || this.state.job) return;
    this.restored = true;
    const jobId = this.port.lastJob();
    if (!jobId) return;
    await this.readJob(jobId);
  }

  /** 网络恢复后重读原任务，避免不确定是否仍在运行时另开一个任务。 */
  async retry(): Promise<void> {
    if (this.disposed || !this.state.retryable || this.state.restoring || this.state.submitting) return;
    const jobId = this.state.job?.jobId ?? this.port.lastJob();
    if (jobId) await this.readJob(jobId);
  }

  private async readJob(jobId: string): Promise<void> {
    this.patch({ restoring: true, retryable: undefined, error: undefined });
    let job: JobResponse;
    try {
      job = await this.port.job(jobId);
    } catch (error) {
      if (error instanceof BackendError && error.status === 404) this.patch({ job: undefined, report: undefined, cancelling: false, settled: false });
      else this.patch({ error: `读取验证任务失败：${errorText(error)}`, retryable: true });
      return;
    } finally {
      this.patch({ restoring: undefined });
    }
    if (this.disposed) {
      if (!isTerminal(job.state) && job.canCancel && !job.cancellationRequested) void this.port.cancelJob(job.jobId).catch(() => undefined);
      return;
    }
    this.patch({ job, cancelling: job.cancellationRequested, settled: false });
    await this.watch(job);
  }

  async start(conditions: ValidationConditions): Promise<void> {
    if (this.running || this.disposed) return;
    this.restored = true;
    this.patch({ submitting: true, submitError: undefined });
    let job: JobResponse;
    try {
      if (this.port.prepare) await this.port.prepare();
      const backend = this.port.renderBackend();
      job = await withFreshDraft(this.port, (draft) => {
        if (this.disposed) throw new Error('草稿已关闭。');
        const selected = validationConditions(draft.sampleOptions, conditions, undefined);
        if (!selected) throw new Error('这个草稿没有可用的试样目标，不能验证。');
        return this.port.validate(draft, { ...selected, ...backend });
      });
    } catch (error) {
      const device = renderBackendErrorText(error);
      const code = error instanceof BackendError ? (error.code ?? undefined) : undefined;
      this.patch({ submitError: device ? `${device}。可以在顶栏改用别的渲染设备后重新验证。` : failureText(code, errorText(error)) });
      return;
    } finally {
      this.patch({ submitting: undefined });
    }
    if (this.disposed) {
      if (!isTerminal(job.state) && job.canCancel && !job.cancellationRequested) void this.port.cancelJob(job.jobId).catch(() => undefined);
      return;
    }
    this.port.rememberJob(job.jobId);
    this.state$.set({ ...(this.state.chosen ? { chosen: this.state.chosen } : {}), job, cancelling: false, settled: false });
    await this.watch(job);
  }

  /** 请求取消；之后一直显示“正在取消”，直到真正的终态。 */
  async cancel(): Promise<void> {
    const job = this.state.job;
    if (this.disposed || !job || isTerminal(job.state) || !job.canCancel || this.state.cancelling) return;
    this.patch({ cancelling: true });
    try {
      await this.port.cancelJob(job.jobId);
    } catch (error) {
      if (this.state.job?.jobId === job.jobId && !isTerminal(this.state.job.state)) this.patch({ cancelling: false, error: `取消没有成功：${errorText(error)}` });
    }
  }

  dispose(): void {
    this.disposed = true;
    this.abort.abort();
    const job = this.state.job;
    if (job && !isTerminal(job.state) && job.canCancel && !this.state.cancelling && !job.cancellationRequested) void this.port.cancelJob(job.jobId).catch(() => undefined);
  }

  private async watch(accepted: JobResponse): Promise<void> {
    const jobId = accepted.jobId;
    const mine = () => !this.disposed && this.state.job?.jobId === jobId;
    let job = accepted;
    if (!isTerminal(job.state)) {
      try {
        job = await this.port.waitJob(
          jobId,
          (current) => {
            if (mine()) this.patch({ job: mergeJob(this.state.job!, current) });
          },
          this.abort.signal,
        );
      } catch (error) {
        if (mine()) this.patch({ settled: true, error: `读取任务状态失败：${errorText(error)}`, retryable: true });
        return;
      }
    }
    if (!mine()) return;
    this.patch({ job, cancelling: false });
    try {
      // 没通过时任务是 failed，但完整报告照样发布（validationResult 在）；执行错误、取消时没有报告。
      if (job.validationResult) {
        const report = await this.port.report(jobId);
        if (mine()) this.patch({ report });
      } else if (mine()) this.patch({ error: terminalText(job) });
    } catch (error) {
      if (mine()) this.patch({ error: `读取验证报告失败：${errorText(error)}`, retryable: true });
    } finally {
      if (mine()) this.patch({ settled: true });
    }
  }

  /** 值为 undefined 的键从状态中删除。 */
  private patch(patch: StatePatch): void {
    if (this.disposed) return;
    const next: Record<string, unknown> = { ...this.state };
    for (const [key, value] of Object.entries(patch)) {
      if (value === undefined) delete next[key];
      else next[key] = value;
    }
    this.state$.set(next as unknown as DraftValidationState);
  }
}

// ── 运行条件 ──

/**
 * 运行条件：页签里改过就用改过的（样本或目标已不在选项里时退回默认）；否则跟随试样画布；画布不在这个草稿上时用默认。
 * 材质草稿必须有部位，默认“未唱填充”；效果草稿不带部位。
 */
export function validationConditions(
  options: AuthorDraftSampleOptions,
  chosen: Partial<ValidationConditions> | undefined,
  canvas: Partial<ValidationConditions> | undefined,
): ValidationConditions | undefined {
  const wanted = chosen ?? canvas ?? {};
  const usable = (id: TemplateWorkshopSampleId | undefined) => options.samples.find((s) => s.sampleId === id && s.targets.length > 0);
  const sample = usable(wanted.sampleId) ?? usable(options.defaultSampleId) ?? options.samples.find((s) => s.targets.length > 0);
  if (!sample) return undefined;
  const targetKey = sample.targets.some((t) => t.key === wanted.targetKey) ? wanted.targetKey! : sample.targets[0]!.key;
  const slots = options.materialSlots;
  if (slots.length === 0) return { sampleId: sample.sampleId, targetKey };
  const materialSlot = wanted.materialSlot && slots.includes(wanted.materialSlot) ? wanted.materialSlot : slots.includes('unsungFill') ? 'unsungFill' : slots[0]!;
  return { sampleId: sample.sampleId, targetKey, materialSlot };
}

// ── 报告 ──

export interface ValidationIssueRow {
  key: string;
  transparent: boolean;
  timeUs: number;
  code: string;
  /** message 为空时取 diagnostic.message。 */
  message: string;
  isError: boolean;
  origin: 'compile' | 'runtime';
  /** 源码里的位置：问题自带的，或编译诊断的路径和行列。运行时异常没有行号。 */
  location?: SourceLocation;
  /** 脚本自己的问题（编译或运行），可以到源码页签看。 */
  script: boolean;
}

export const ORIGIN_TEXT: Record<ValidationIssueRow['origin'], string> = { compile: '编译', runtime: '运行' };

/** 脚本运行时失败的编码。这类问题不带 diagnostic（后端只给编译、图和材质问题附诊断），只能按编码认。 */
const SCRIPT_FAILURES = new Set(['ScriptExecutionFailed', 'ScriptDisposeFailed', 'WorkerPackageTimedOut', 'WorkerPackageRuntimeFailed']);

/** 报告里的问题：编译诊断按效果实例重复多次，按输出、编码、位置和消息只留一条；顺序同报告。 */
export function validationIssueRows(report: Pick<ValidationReport, 'issues'>): ValidationIssueRow[] {
  const rows: ValidationIssueRow[] = [];
  const seen = new Set<string>();
  for (const issue of report.issues) {
    const diagnostic = issue.diagnostic;
    const source = diagnostic?.source;
    const where = issue.location ?? (source?.path ? { path: source.path, line: source.line, column: source.column } : undefined) ?? diagnostic?.occurrence?.location;
    const message = issue.message || diagnostic?.message || '';
    const origin = diagnostic?.originKind === 'compileSource' ? 'compile' : 'runtime';
    const key = [issue.transparent, issue.timeUs, issue.code, where ? `${where.path}:${where.line}:${where.column}` : '', origin === 'compile' ? message : stripInstance(message)].join('\u0000');
    if (seen.has(key)) continue;
    seen.add(key);
    rows.push({
      key,
      transparent: issue.transparent,
      timeUs: Number(issue.timeUs),
      code: issue.code,
      message,
      isError: issue.isError,
      origin,
      ...(where ? { location: { path: where.path, line: where.line, column: where.column } } : {}),
      script: !!where || origin === 'compile' || SCRIPT_FAILURES.has(issue.code) || !!diagnostic?.occurrence?.package,
    });
  }
  return rows;
}

/** 报告的诊断里，问题列表没有覆盖到的那些（运行时异常在两处都有）。 */
export function extraDiagnostics(report: Pick<ValidationReport, 'issues' | 'diagnostics'>): ValidationDiagnostic[] {
  const covered = new Set(report.issues.map((i) => `${i.code}\u0000${i.message || i.diagnostic?.message || ''}`));
  return report.diagnostics.filter((d) => !covered.has(`${d.diagnostic.code}\u0000${d.diagnostic.message}`));
}

/** “通过 · 普通 360 帧、透明 360 帧”“失败 · 普通 61 帧、透明 0 帧 · 1 个问题”。 */
export function validationOutcomeText(report: Pick<ValidationReport, 'succeeded' | 'opaqueFrames' | 'transparentFrames' | 'issues'>): string {
  const frames = `普通 ${report.opaqueFrames} 帧、透明 ${report.transparentFrames} 帧`;
  const issues = validationIssueRows(report).length;
  return `${report.succeeded ? '通过' : '失败'} · ${frames}${issues ? ` · ${issues} 个问题` : ''}`;
}

/** 验证之后草稿又改过（配置、源码或采用）：结果对应旧版本。编译不改草稿修订。 */
export function validationStale(job: Pick<JobResponse, 'authorValidationInput'> | undefined, draft: Pick<FullDraft, 'draftRevision'>): boolean {
  const validated = job?.authorValidationInput?.draftRevision;
  return !!validated && validated !== draft.draftRevision;
}

/** 运行时异常的消息带效果实例 ID（“效果 fx:… （包 …）”），不同实例的同一个异常算一条。 */
function stripInstance(message: string): string {
  return message.replace(/fx:[0-9a-f]+/g, 'fx');
}

/** 事件摘要只带状态和进度：合并进已有的完整任务。 */
function mergeJob(job: JobResponse, current: JobResponse | JobSummary): JobResponse {
  if ('cancellationRequested' in current) return current;
  return {
    ...job,
    state: current.state,
    canCancel: current.canCancel,
    ...(current.progress ? { progress: current.progress } : {}),
    ...(current.failureCode ? { failureCode: current.failureCode } : {}),
  };
}

function terminalText(job: JobResponse): string {
  if (job.state === 'cancelled') return '已取消。';
  if (job.state === 'unknown') return '结果未知：后端没能确认这个任务的结果。';
  return failureText(job.failureCode ?? job.error?.failureCode ?? job.error?.code, job.error?.message);
}

// ── sessionStorage：{ clientId, drafts: { 草稿: 任务 ID } } ──

export function readValidationJob(clientId: string | undefined, draftId: string): string | undefined {
  try {
    const stored = JSON.parse(sessionStorage.getItem(DRAFT_VALIDATIONS_KEY) ?? 'null') as { clientId?: string; drafts?: Record<string, string> } | null;
    const jobId = stored && stored.clientId === clientId ? stored.drafts?.[draftId] : undefined;
    return typeof jobId === 'string' ? jobId : undefined;
  } catch {
    return undefined;
  }
}

/** 写入一个草稿的任务；只保留 keep 里的草稿（关掉的草稿顺带清掉）。 */
export function writeValidationJob(clientId: string | undefined, draftId: string, jobId: string, keep: readonly string[]): void {
  try {
    if (!clientId) return;
    const stored = JSON.parse(sessionStorage.getItem(DRAFT_VALIDATIONS_KEY) ?? 'null') as { clientId?: string; drafts?: Record<string, string> } | null;
    const drafts: Record<string, string> = {};
    if (stored?.clientId === clientId && stored.drafts) for (const id of keep) if (stored.drafts[id]) drafts[id] = stored.drafts[id];
    drafts[draftId] = jobId;
    sessionStorage.setItem(DRAFT_VALIDATIONS_KEY, JSON.stringify({ clientId, drafts }));
  } catch {
    // 存不下时刷新后读不回上一次的报告，重新验证即可。
  }
}
