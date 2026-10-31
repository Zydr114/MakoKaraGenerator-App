// 目录授权、目录浏览、恢复、持久化任务入口与 JobResponse。
// 对应 BackendLifecycleContracts.cs、BackendContracts.cs（JobResponse、JobSummaryDto）；规范 §7、§8、§17.1。
// analysis 的进度与结果摘要（规范 §12）W2 起有类型；validation／export 的专用字段（§10、§11）W5 起有类型，见 render-jobs.ts。

import type { AuthorCompilationJobInput, AuthorValidationJobInput } from './author';
import type { BackendDiagnostic, IsoDateTime } from './common';
import type { BackendErrorResponse } from './errors';
import type { Micros, Revision } from './int64';
import type { ProjectSessionSummary } from './project';
import type { ExportJobInputSnapshot, ExportJobResult, JobReportReference, RenderJobInput, ValidationJobResult } from './render-jobs';

export interface FileRootsResponse {
  rootGrantIds: string[];
  protocolVersion: '1';
}

export type FileAccess = 'read' | 'readWrite';

/**
 * 授权根下一层目录的一页（规范 §17.1，合同 1.25.0）。只列直接子目录，按名称 Ordinal 排序；
 * isProject 只表示有工程入口文件，不加载工程。每页 1～100，单层扫描上限 4096（超过时 429）。
 * 分页不是快照：目录变化时从第一页重读。
 */
export interface ProjectDirectoryEntriesResponse {
  rootGrantId: string;
  /** 空字符串表示根本身。 */
  relativePath: string;
  offset: number;
  totalCount: number;
  /** 还有下一页时才有。 */
  nextOffset?: number;
  entries: ProjectDirectoryEntry[];
  protocolVersion: '1';
}

export interface ProjectDirectoryEntry {
  name: string;
  /** 相对授权根的路径，可直接用于 file-handles。 */
  relativePath: string;
  isProject: boolean;
}

export const DIRECTORY_PAGE_MAX = 100;

/** 当前 profile 的恢复候选（规范 §8），没有分页。 */
export interface RecoveryCandidate {
  recoveryId: string;
  projectName: string;
  savedAtUtc: IsoDateTime;
  hasOriginalPath: boolean;
}

export interface RecoveryCandidatesResponse {
  candidates: RecoveryCandidate[];
  protocolVersion: '1';
}

/** 只提交服务返回的 recoveryId，不接受任意备份路径。 */
export interface RecoverProjectInput {
  recoveryId: string;
}

export interface FileHandleInput {
  rootGrantId: string;
  relativePath: string;
  access?: FileAccess;
}

export interface FileHandleResponse {
  handleId: string;
  rootGrantId: string;
  relativePath: string;
  canWrite: boolean;
}

export interface OpenProjectInput {
  projectDirectoryHandleId: string;
  access?: FileAccess;
}

export interface SaveProjectInput {
  /** 已有路径的工程可以省略；新工程必须给可写句柄。 */
  targetHandleId?: string | null;
}

export type ClosePolicy = 'requireClean' | 'save' | 'discard';
export type CloseActiveJobs = 'reject' | 'wait' | 'cancelAndWait';

export interface CloseProjectInput {
  policy?: ClosePolicy;
  targetHandleId?: string | null;
  activeJobs?: CloseActiveJobs;
}

/** author-compilation 是作者草稿的异步编译（合同 1.38.0）。 */
export const JOB_KINDS = ['open', 'save', 'close', 'recover', 'validation', 'export', 'analysis', 'author-compilation'] as const;
export type JobKind = (typeof JOB_KINDS)[number];

export const JOB_STATES = ['queued', 'running', 'committing', 'succeeded', 'failed', 'cancelled', 'unknown'] as const;
export type JobState = (typeof JOB_STATES)[number];

/** 终态。unknown 原样返回，不当作失败也不当作成功（设计 §3.7）。 */
export const TERMINAL_JOB_STATES: ReadonlySet<JobState> = new Set(['succeeded', 'failed', 'cancelled', 'unknown']);

export function isTerminalJobState(state: string): state is JobState {
  return TERMINAL_JOB_STATES.has(state as JobState);
}

export interface JobProgress {
  stage: string;
  completedFrames: number;
  totalFrames: number;
  timeUs?: string;
  transparent?: boolean;
}

/** client state 与事件里的任务摘要。 */
export interface JobSummary {
  jobId: string;
  kind: JobKind;
  state: JobState;
  projectSessionId?: string;
  canCancel: boolean;
  expiresAtUtc: IsoDateTime;
  errorCode?: string;
  progress?: JobProgress;
  failureCode?: string;
  analysisProgress?: AnalysisProgress;
}

export interface JobResponse {
  jobId: string;
  kind: JobKind;
  state: JobState;
  projectSessionId?: string;
  /** open／recover／save／close 成功时的工程摘要。 */
  result?: ProjectSessionSummary;
  savedProjectRevision?: Revision;
  currentProjectRevision?: Revision;
  isDirty?: boolean;
  error?: BackendErrorResponse;
  expiresAtUtc: IsoDateTime;
  cancellationRequested: boolean;
  canCancel: boolean;
  diagnostics?: BackendDiagnostic[];
  progress?: JobProgress;
  failureCode?: string;
  // 以下为 validation／export 专用投影（规范 §10、§11）。
  /** 接受时固定的来源与帧计划；sourceRevision 是接受时的修订，不随之后的编辑变化。 */
  input?: RenderJobInput;
  validationResult?: ValidationJobResult;
  exportInput?: ExportJobInputSnapshot;
  exportResult?: ExportJobResult;
  /** 导出失败且有运行时诊断时的失败报告引用；与 exportResult 不会同时出现。 */
  failureReportRef?: JobReportReference;
  analysisInput?: Record<string, unknown>;
  analysisProgress?: AnalysisProgress;
  analysisResult?: AnalysisJobResult;
  // 作者草稿（规范 §21）：编译结果从 /jobs/{id}/compilation-result 读；作者验证仍是 validation 任务，报告读法同正式验证。
  authorCompilationInput?: AuthorCompilationJobInput;
  authorCompilationResult?: JobReportReference;
  authorValidationInput?: AuthorValidationJobInput;
}

// ── 独立离线音频分析（规范 §12） ──

/** GET /api/v1/analysis-capabilities。enabled=false 时 disabledReason 说明原因，创建任务返回 503。 */
export interface AnalysisCapabilities {
  enabled: boolean;
  sourceProfile: string;
  sourceProfiles?: string[];
  algorithm: string;
  audioProfiles: string[];
  maxAudioBytes: number;
  maxSourceSampleRate: number;
  maxSourceDurationUs: Micros;
  sampleRate: number;
  hopSamples: number;
  maxSamples: string;
  maxPoints: number;
  maxResultBytes: number;
  maxResultStorageBytes: number;
  operationTimeoutSeconds: number;
  cancellationGraceSeconds: number;
  maxRunning: number;
  maxRunningPerClient: number;
  maxRunningPerProject: number;
  maxQueued: number;
  maxQueuedPerClient: number;
  guardAvailable: boolean;
  toolStartupVerified: boolean;
  disabledReason?: BackendErrorResponse;
}

/** 采样数是 48kHz 单声道采样计数；totalSamples 只在解码完成（prepared）后才有。 */
export interface AnalysisProgress {
  stage: string;
  completedSamples: string;
  totalSamples?: string;
  timeUs?: Micros;
}

/** 成功的分析任务的结果摘要；完整结果另行读取，采用用 audio.analysis.apply。 */
export interface AnalysisJobResult {
  algorithm: string;
  sampleRate: number;
  hopSamples: number;
  originUs: Micros;
  sourceHash: string;
  pcmHash: string;
  sampleCount: string;
  durationUs: Micros;
  revision: string;
  contentHash: string;
  pointCount: number;
  report: Record<string, unknown>;
}
