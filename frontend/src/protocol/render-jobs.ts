// 固定工程正式验证与导出（规范 §10、§11）：能力、请求、任务里的固定输入与结果摘要、完整报告、诊断分页。
// 对应 BackendContracts.cs（ValidationTaskCapabilitiesDto、ExportTaskCapabilitiesDto、ValidationJobRequest、ExportJobRequest、
// BackendJobInputDto、BackendExportInputDto、ValidationJobResultDto、ExportJobResultDto、ValidationReportDto、ExportReportDto、ExportFailureReportDto）。

import type { BackendDiagnostic } from './common';
import type { BackendErrorResponse } from './errors';
import type { Micros, Revision } from './int64';
import type { RenderBackend } from './preview';

// ── 能力 ──

/** GET /api/v1/validation-capabilities。enabled=false 时 disabledReason 说明原因，创建任务返回 503。 */
export interface ValidationCapabilities {
  enabled: boolean;
  sourceProfile: string;
  sourceProfiles?: string[];
  /** 每种输出（普通、透明）最多验证的帧数。 */
  maxFramesPerOutput: number;
  maxCanvasWidth: number;
  maxCanvasHeight: number;
  maxRawFrameBytes: number;
  operationTimeoutSeconds: number;
  maxReportBytes: number;
  maxIssues: number;
  maxOccurrences: number;
  maxRunning: number;
  maxRunningPerClient: number;
  maxRunningPerProject: number;
  maxQueued: number;
  maxQueuedPerClient: number;
  maxReportStorageBytes: number;
  maxDiagnosticsPageSize: number;
  disabledReason?: BackendErrorResponse;
}

export const EXPORT_FORMATS = ['opaqueMp4', 'transparentProRes4444'] as const;
export type ExportFormat = (typeof EXPORT_FORMATS)[number];

export const EXPORT_QUALITIES = ['draft', 'standard', 'high'] as const;
export type ExportQuality = (typeof EXPORT_QUALITIES)[number];

/**
 * GET /api/v1/export-capabilities。限制类字段省略（或 null）表示部署没有施加这一维度的限制。
 * 支持一种格式不代表另一种也可用；以 formats 为准。
 */
export interface ExportCapabilities {
  enabled: boolean;
  sourceProfile: string;
  sourceProfiles?: string[];
  formats: string[];
  audioProfiles: string[];
  /** 省略 quality 时的默认档位。 */
  quality: string;
  qualities?: string[];
  renderQuality: string;
  maxFrames?: number | null;
  maxDurationUs?: Micros | null;
  maxFrameRate?: number | null;
  maxCanvasWidth?: number | null;
  maxCanvasHeight?: number | null;
  maxRawFrameBytes?: number | null;
  operationTimeoutSeconds?: number | null;
  cancellationGraceSeconds: number;
  maxMediaBytes?: number | null;
  maxAudioBytes?: number | null;
  maxReportBytes: number;
  maxOccurrences: number;
  maxArtifactStorageBytes?: number | null;
  maxRunning: number;
  maxRunningPerClient: number;
  maxRunningPerProject: number;
  maxQueued: number;
  maxQueuedPerClient: number;
  outputFileGuard: string;
  maxDiagnosticsPageSize: number;
  executionProfile: string;
  executionProfiles?: string[];
  maxCompressedAudioDurationUs?: Micros | null;
  compressedAudioDurationBasis: string;
  disabledReason?: BackendErrorResponse;
}

// ── 请求 ──

/** framesPerOutput 为 1～maxFramesPerOutput；计划从 0 时刻起逐帧取样，最后一帧不能超过输出时长。 */
export interface ValidationJobInput {
  framesPerOutput: number;
  /** 省略或 null 用启动默认；任务登记时固定。 */
  renderBackend?: RenderBackend | null;
}

export interface ExportJobInput {
  /** 省略为 opaqueMp4。 */
  format?: ExportFormat;
  /** 省略为 standard。 */
  quality?: ExportQuality;
  /** 省略为 true；工程没有音频时必须为 false。 */
  includeAudio?: boolean;
  /** 省略或 null 用启动默认；任务登记时固定。 */
  renderBackend?: RenderBackend | null;
}

// ── 任务里的固定输入与结果摘要 ──

/** validation 与 export 共用的固定来源与帧计划；export 里 framesPerOutput 是这一种输出的完整帧数。 */
export interface RenderJobInput {
  sourceKind: string;
  sourceId: string;
  sourceRevision: Revision;
  sourceProfile: string;
  sourceSha256: string;
  framesPerOutput: number;
  canvasWidth: number;
  canvasHeight: number;
  frameRateNumerator: number;
  frameRateDenominator: number;
  outputDurationUs?: Micros;
  /** 任务固定的设备（1.39.0）；没有这个字段或为 null 表示当时没记录，不能当成 CPU。 */
  renderBackend?: RenderBackend | null;
}

export interface ExportJobInputSnapshot {
  format: string;
  includeAudio: boolean;
  quality: ExportQuality;
  audio?: { assetId: string; sizeBytes: number; sha256: string; audioProfile?: string } | null;
  executionProfile?: string;
  taskProfile?: string;
  baseSourceProfile?: string;
  renderBackend?: RenderBackend | null;
}

export interface JobReportReference {
  reportId: string;
  sizeBytes: number;
  sha256: string;
}

export interface ValidationJobResult {
  passed: boolean;
  opaqueFrames: number;
  transparentFrames: number;
  report: JobReportReference;
}

export interface ExportArtifactReference {
  artifactId: string;
  /** video/mp4 或 video/quicktime。 */
  contentType: string;
  sizeBytes: number;
  sha256: string;
}

export interface ExportJobResult {
  artifact: ExportArtifactReference;
  report: JobReportReference;
}

// ── 完整报告 ──

/** 正式验证的完整报告（原始 JSON 字节，可能保留 null 字段）。 */
export interface ValidationReport {
  succeeded: boolean;
  opaqueFrames: number;
  transparentFrames: number;
  issues: ValidationIssue[];
  diagnostics: ValidationDiagnostic[];
  /** 缺失或 null 表示没记录。 */
  renderBackend?: RenderBackend | null;
}

export interface ValidationIssue {
  transparent: boolean;
  timeUs: Micros;
  code: string;
  message: string | null;
  isError: boolean;
  location: { path: string; line: number; column: number } | null;
  sampleFailureCode: string | null;
  diagnostic?: BackendDiagnostic;
}

export interface ValidationDiagnostic {
  transparent: boolean;
  diagnostic: BackendDiagnostic;
}

/** 导出成功的完整报告。发布要求 strictlyVerified 为 true；isVerified 是原导出器按名义时长的旧标记。 */
export interface ExportReport {
  format: string;
  audioIncluded: boolean;
  expectedFrames: number;
  decodedFrames: number;
  framesWritten: number;
  droppedFrames: number;
  failedFrames: number;
  width: number;
  height: number;
  frameRateNumerator: number;
  frameRateDenominator: number;
  derivedVideoDurationUs: Micros;
  actualVideoDurationUs: Micros | null;
  /** 源音频的实际时长；纯视频为 null。 */
  audioDurationUs: Micros | null;
  videoCodec: string | null;
  audioCodec: string | null;
  pixelFormat: string | null;
  hasAlpha: boolean;
  mediaSize: number;
  sha256: string;
  warnings: string[];
  diagnostics: BackendDiagnostic[];
  isVerified: boolean;
  /** 成片音轨的时长；纯视频为 null。 */
  actualAudioDurationUs: Micros | null;
  strictlyVerified: boolean;
  requestedQuality: ExportQuality;
  actualQuality: ExportQuality;
  /** 缺失或 null 表示没记录。 */
  renderBackend?: RenderBackend | null;
}

/** 导出失败且有运行时诊断时的分型失败报告；没有成片。 */
export interface ExportFailureReport {
  kind: 'exportFailure';
  failureCode: string;
  source: RenderJobInput;
  input: ExportJobInputSnapshot;
  diagnostics: BackendDiagnostic[];
}

/** export-result 的 200 不代表导出成功：按 kind 区分。 */
export type ExportResultReport = ExportReport | ExportFailureReport;

export function isExportFailureReport(report: ExportResultReport): report is ExportFailureReport {
  return (report as { kind?: string }).kind === 'exportFailure';
}

/** validation-diagnostics、export-diagnostics 的一页；nextCursor 存在才继续。 */
export interface JobDiagnosticsPage<T> {
  jobId: string;
  reportSha256: string;
  diagnostics: T[];
  cursor?: string;
  nextCursor?: string;
}
