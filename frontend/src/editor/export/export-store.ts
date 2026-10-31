// 导出页状态（W5 设计 §2.4）：能力、格式选择、编排里各来源的动画策略（歌词时间检查用）、首末帧缩略图，
// 正式验证与导出任务各一个（进度、取消、终态后的报告与诊断、成片下载）。
// 不依赖 React。每个工程一个，由 App 持有；离开导出页时任务不停，回来仍能看到。

import { BackendError, ObservableValue, type MakoClient } from '../../client';
import {
  EXPORT_QUALITIES,
  isExportFailureReport,
  type BackendDiagnostic,
  type ExportCapabilities,
  type ExportFailureReport,
  type ExportFormat,
  type ExportQuality,
  type ExportReport,
  type JobResponse,
  type JobSummary,
  type PreviewSize,
  type Revision,
  type ValidationCapabilities,
  type ValidationReport,
} from '../../protocol';
import { FOLLOW_DEFAULT, renderBackendField, type RenderBackendRequest } from '../model/render-device';
import { failureText, isTerminal } from '../model/render-job';
import { toMicros } from '../model/time';
import { renderBackendErrorText } from '../preview/render-device';
import { errorText, type EditorSession } from '../session/editor-session';

export type RenderKind = 'validation' | 'export';

export interface JobTrack {
  kind: RenderKind;
  /** 最近一次读到的任务（事件摘要合并进来）。 */
  job: JobResponse;
  cancelling: boolean;
  /** 终态后的报告已读完（或读取失败）。事件摘要可能先带来终态，这之前报告还没读。 */
  settled: boolean;
  /** 终态后读取的报告。 */
  validationReport?: ValidationReport;
  exportReport?: ExportReport;
  failureReport?: ExportFailureReport;
  /** 失败、取消、结果未知时的说明。 */
  error?: string;
}

export interface Thumbnails {
  key: string;
  first?: string;
  last?: string;
  loading: boolean;
  error?: string;
}

export interface ExportState {
  /** 还没读到时为 undefined；读取失败时为 null。 */
  capabilities?: ExportCapabilities | null | undefined;
  validationCapabilities?: ValidationCapabilities | null | undefined;
  capabilitiesError?: string;
  format: ExportFormat;
  quality?: ExportQuality;
  includeAudio: boolean;
  /** 编排里各来源的动画策略（按修订读；后端没有编排读取时为空）。 */
  policies?: { revision: Revision; byBranch: Record<string, string | null | undefined> };
  thumbnails?: Thumbnails;
  /** 正式验证每种输出检查的帧数；未改时用默认值。 */
  validationFrames?: number;
  validation?: JobTrack;
  exportJob?: JobTrack;
  /** 发起任务时就被拒绝（例如 422）。 */
  submitError?: { kind: RenderKind; text: string } | undefined;
}

export const ORCHESTRATION_READ = 'project-sessions.orchestration.read';

export class ExportStore {
  readonly state$ = new ObservableValue<ExportState>({ format: 'opaqueMp4', includeAudio: true });
  private readonly abort = new AbortController();
  private thumbnailAbort: AbortController | undefined;
  private policiesRevision: Revision | undefined;

  constructor(
    private readonly client: MakoClient,
    private readonly session: EditorSession,
    /** 渲染设备偏好：缩略图、正式验证、导出都按它传 renderBackend（渲染设备设计 §2.2）。 */
    private readonly device: RenderBackendRequest = FOLLOW_DEFAULT,
  ) {
    void this.loadCapabilities();
  }

  get state(): ExportState {
    return this.state$.get();
  }

  private patch(next: Partial<ExportState>): void {
    if (this.abort.signal.aborted) return;
    this.state$.set({ ...this.state, ...next });
  }

  dispose(): void {
    this.abort.abort();
    this.thumbnailAbort?.abort();
    this.revokeThumbnails(this.state.thumbnails);
  }

  // ── 能力与选择 ──

  async loadCapabilities(): Promise<void> {
    this.patch({ capabilities: undefined, validationCapabilities: undefined });
    const [exportCaps, validationCaps] = await Promise.allSettled([this.client.exportCapabilities(), this.client.validationCapabilities()]);
    this.patch({
      capabilities: exportCaps.status === 'fulfilled' ? exportCaps.value : null,
      validationCapabilities: validationCaps.status === 'fulfilled' ? validationCaps.value : null,
      ...(exportCaps.status === 'rejected' ? { capabilitiesError: `读取导出能力失败：${errorText(exportCaps.reason)}` } : {}),
    });
    const caps = exportCaps.status === 'fulfilled' ? exportCaps.value : undefined;
    if (caps && this.state.quality === undefined) this.patch({ quality: isQuality(caps.quality) ? caps.quality : 'standard' });
  }

  setFormat(format: ExportFormat): void {
    this.patch({ format });
  }

  setQuality(quality: ExportQuality): void {
    this.patch({ quality });
  }

  setIncludeAudio(includeAudio: boolean): void {
    this.patch({ includeAudio });
  }

  setValidationFrames(frames: number): void {
    this.patch({ validationFrames: frames });
  }

  /** 读编排里各来源的动画策略：静态来源里没有时间的字不阻断导出（ProjectValidator）。每个修订读一次。 */
  async loadPolicies(revision: Revision): Promise<void> {
    if (!this.session.supports(ORCHESTRATION_READ) || this.policiesRevision === revision) return;
    this.policiesRevision = revision;
    try {
      const orchestration = await this.client.projects.orchestration(this.session.projectSessionId, revision);
      const byBranch: Record<string, string | null | undefined> = {};
      for (const program of orchestration.programs) byBranch[program.branchId] = program.animationPolicy;
      this.patch({ policies: { revision, byBranch } });
    } catch (error) {
      // 修订已变（409）时等下一次；其他错误按默认策略检查。
      if (this.policiesRevision === revision) this.policiesRevision = undefined;
      if (!(error instanceof BackendError && error.code === 'revisionConflict')) this.patch({ policies: { revision, byBranch: {} } });
    }
  }

  // ── 首帧与末帧缩略图 ──

  /** 按修订、尺寸、末帧时刻和渲染设备渲染两张静态帧；同一个 key 不重复渲染，换 key 时取消上一次。 */
  loadThumbnails(revision: Revision, size: PreviewSize, lastFrameUs: number): void {
    const backend = renderBackendField(this.device);
    const key = `${revision}|${size.width}x${size.height}|${lastFrameUs}|${backend.renderBackend ?? 'default'}`;
    if (this.state.thumbnails?.key === key) return;
    this.thumbnailAbort?.abort();
    const abort = new AbortController();
    this.thumbnailAbort = abort;
    const previous = this.state.thumbnails;
    this.patch({ thumbnails: { ...(previous?.first ? { first: previous.first } : {}), ...(previous?.last ? { last: previous.last } : {}), key, loading: true } });
    const still = (timeUs: number) =>
      this.client.preview.renderStill({
        projectSessionId: this.session.projectSessionId,
        sourceRevision: revision,
        timeUs: toMicros(timeUs),
        size,
        ...backend,
        signal: abort.signal,
      });
    void Promise.all([still(0), still(lastFrameUs)]).then(
      ([first, last]) => {
        if (abort.signal.aborted) return;
        this.revokeThumbnails(this.state.thumbnails);
        this.patch({ thumbnails: { key, first: URL.createObjectURL(first.blob), last: URL.createObjectURL(last.blob), loading: false } });
      },
      (error: unknown) => {
        if (abort.signal.aborted) return;
        this.patch({ thumbnails: { ...this.state.thumbnails!, loading: false, error: `缩略图没有渲染出来：${errorText(error)}` } });
      },
    );
  }

  private revokeThumbnails(thumbnails: Thumbnails | undefined): void {
    if (thumbnails?.first) URL.revokeObjectURL(thumbnails.first);
    if (thumbnails?.last) URL.revokeObjectURL(thumbnails.last);
  }

  // ── 正式验证与导出任务 ──

  running(kind: RenderKind): boolean {
    const track = kind === 'validation' ? this.state.validation : this.state.exportJob;
    return !!track && (!isTerminal(track.job.state) || !track.settled);
  }

  async startValidation(framesPerOutput: number): Promise<void> {
    await this.start('validation', () => this.client.projects.validate(this.session.projectSessionId, { framesPerOutput, ...renderBackendField(this.device) }));
  }

  async startExport(includeAudio: boolean): Promise<void> {
    const { format, quality } = this.state;
    await this.start('export', () =>
      this.client.projects.exportVideo(this.session.projectSessionId, { format, includeAudio, ...(quality ? { quality } : {}), ...renderBackendField(this.device) }),
    );
  }

  private async start(kind: RenderKind, create: () => Promise<JobResponse>): Promise<void> {
    if (this.running(kind)) return;
    this.patch({ submitError: undefined });
    let job: JobResponse;
    try {
      job = await create();
    } catch (error) {
      const code = error instanceof BackendError ? (error.code ?? undefined) : undefined;
      const device = renderBackendErrorText(error);
      this.patch({ submitError: { kind, text: device ? `${device}。可以在顶栏改用别的渲染设备后重新开始。` : failureText(code, errorText(error)) } });
      return;
    }
    this.setTrack(kind, { kind, job, cancelling: false, settled: false });
    await this.watch(kind, job.jobId);
  }

  private track(kind: RenderKind): JobTrack | undefined {
    return kind === 'validation' ? this.state.validation : this.state.exportJob;
  }

  private setTrack(kind: RenderKind, track: JobTrack): void {
    this.patch(kind === 'validation' ? { validation: track } : { exportJob: track });
  }

  private update(kind: RenderKind, jobId: string, patch: (track: JobTrack) => JobTrack): void {
    const track = this.track(kind);
    if (track?.job.jobId === jobId) this.setTrack(kind, patch(track));
  }

  private async watch(kind: RenderKind, jobId: string): Promise<void> {
    let job: JobResponse;
    try {
      job = await this.client.jobs.waitForTerminal(jobId, {
        signal: this.abort.signal,
        onProgress: (current) => this.update(kind, jobId, (t) => ({ ...t, job: mergeJob(t.job, current) })),
      });
    } catch (error) {
      if (this.abort.signal.aborted) return;
      this.update(kind, jobId, (t) => ({ ...t, settled: true, error: `读取任务状态失败：${errorText(error)}` }));
      return;
    }
    this.update(kind, jobId, (t) => ({ ...t, job, cancelling: false }));
    try {
      if (kind === 'validation') await this.finishValidation(job);
      else await this.finishExport(job);
    } catch (error) {
      if (this.abort.signal.aborted) return;
      this.update(kind, jobId, (t) => ({ ...t, error: `读取报告失败：${errorText(error)}` }));
    } finally {
      this.update(kind, jobId, (t) => ({ ...t, settled: true }));
    }
  }

  /** 验证没通过时任务是 failed，但完整报告照样发布（validationResult 在）；执行错误时没有报告。 */
  private async finishValidation(job: JobResponse): Promise<void> {
    if (job.validationResult) {
      const report = await this.client.jobs.validationResult(job.jobId);
      this.update('validation', job.jobId, (t) => ({ ...t, validationReport: report }));
      return;
    }
    this.update('validation', job.jobId, (t) => ({ ...t, error: terminalText(job) }));
  }

  private async finishExport(job: JobResponse): Promise<void> {
    if (job.state === 'succeeded' || job.failureReportRef) {
      const report = await this.client.jobs.exportResult(job.jobId);
      if (isExportFailureReport(report)) {
        this.update('export', job.jobId, (t) => ({ ...t, failureReport: report, error: terminalText(job, report.failureCode) }));
      } else {
        this.update('export', job.jobId, (t) => ({ ...t, exportReport: report }));
      }
      return;
    }
    this.update('export', job.jobId, (t) => ({ ...t, error: terminalText(job) }));
  }

  /** 请求取消；之后一直显示“正在取消”，直到真正的终态。 */
  async cancel(kind: RenderKind): Promise<void> {
    const track = this.track(kind);
    if (!track || isTerminal(track.job.state) || !track.job.canCancel) return;
    const jobId = track.job.jobId;
    this.update(kind, jobId, (t) => ({ ...t, cancelling: true }));
    try {
      await this.client.jobs.cancel(jobId);
    } catch (error) {
      this.update(kind, jobId, (t) => ({ ...t, cancelling: false, error: `取消没有成功：${errorText(error)}` }));
    }
  }

  /** 取成片（认证读取）。成片保留 1 小时，过期后 404／410。 */
  async download(): Promise<{ blob: Blob; fileName: string | null; contentType: string }> {
    const track = this.state.exportJob;
    if (!track?.exportReport || !track.job.exportResult) throw new Error('还没有可以下载的成片。');
    try {
      const result = await this.client.jobs.exportContent(track.job.jobId, { signal: this.abort.signal });
      if (!result.blob) throw new Error(`成片没有正文（HTTP ${result.status}）。`);
      return { blob: result.blob, fileName: result.fileName, contentType: track.job.exportResult.artifact.contentType };
    } catch (error) {
      if (error instanceof BackendError && (error.status === 404 || error.status === 410)) throw new Error('成片已过期（只保留 1 小时），请重新导出。');
      throw error;
    }
  }
}

function isQuality(value: string): value is ExportQuality {
  return (EXPORT_QUALITIES as readonly string[]).includes(value);
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

function terminalText(job: JobResponse, failureCode?: string): string {
  if (job.state === 'cancelled') return '已取消。';
  if (job.state === 'unknown') return '结果未知：后端没能确认这个任务的结果。';
  const code = failureCode ?? job.failureCode ?? job.error?.failureCode ?? job.error?.code;
  return failureText(code, job.error?.message);
}

export type { BackendDiagnostic };
