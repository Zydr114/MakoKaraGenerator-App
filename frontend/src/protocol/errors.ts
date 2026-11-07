// 对应 BackendContracts.cs 的 BackendErrorResponse / BackendErrorCode（OpenAPI 1.0.0 components.schemas）。
// 后端规范 §1.4：错误正文是 application/problem+json，但不是完整 RFC Problem Details。
// 程序分支只看 code / failureCode；message 是中文，只用于显示。

export const BACKEND_ERROR_CODES = [
  'invalidRequest',
  'unauthorizedClient',
  'unsupportedMediaType',
  'resourceEmpty',
  'resourceTooLarge',
  'resourceQuotaExceeded',
  'resourcePathNotAllowed',
  'resourceNotFound',
  'resourceUnauthorized',
  'lyricsImportFailed',
  'emptyLyrics',
  'noAudioStream',
  'audioPreparationFailed',
  'sessionNotFound',
  'hostRestarted',
  'sessionConflict',
  'revisionConflict',
  'commandNotSupported',
  'commandRejected',
  'requestExpired',
  'requestIdReuse',
  'requestQuotaExceeded',
  'clientQuotaExceeded',
  'clientTokenExpired',
  'sessionQuotaExceeded',
  'resourceExpired',
  'pathNotAllowed',
  'writeLeaseConflict',
  'readOnlySession',
  'sessionClosing',
  'dirtySession',
  'jobNotFound',
  'activeJobs',
  'projectLoadFailed',
  'operationNotCancellable',
  'internalError',
  'previewSessionNotFound',
  'previewGenerationConflict',
  'previewRequestOutOfOrder',
  'previewFrameNotFound',
  'previewFrameExpired',
  'diagnosticCursorExpired',
  'diagnosticLimitExceeded',
  'previewUnavailable',
  'validationFailed',
  'taskExecutionFailed',
  'taskUnavailable',
  'exportInputRejected',
  'exportAudioUnsupported',
  'exportArtifactNotFound',
  'exportArtifactExpired',
  'previewInspectionUnavailable',
] as const;

export type BackendErrorCode = (typeof BACKEND_ERROR_CODES)[number];

export interface BackendErrorResponse {
  /** 已知值见 BackendErrorCode；未来新增的 code 也可能出现，按未知处理。 */
  code: BackendErrorCode | (string & {});
  message: string;
  field?: string;
  protocolVersion: '1';
  failureCode?: string;
}

/**
 * 设计 §6 的四类展示约定，外加“结果未知”与“其他”。
 * - recoverable：页面可以自行恢复（重读、换代、重新请求帧）。
 * - userAction：需要用户处理（配额、文件、输入）。
 * - connection：连接身份问题（主机重启、凭证失效）。
 * - defect：客户端缺陷，应记录并修复。
 */
export type ErrorCategory = 'recoverable' | 'userAction' | 'connection' | 'defect' | 'other';

const CATEGORY: Partial<Record<BackendErrorCode, ErrorCategory>> = {
  previewFrameExpired: 'recoverable',
  previewFrameNotFound: 'recoverable',
  previewGenerationConflict: 'recoverable',
  previewRequestOutOfOrder: 'recoverable',
  revisionConflict: 'recoverable',
  diagnosticCursorExpired: 'recoverable',
  resourceExpired: 'recoverable',
  exportArtifactExpired: 'recoverable',
  previewInspectionUnavailable: 'recoverable',

  resourceQuotaExceeded: 'userAction',
  requestQuotaExceeded: 'userAction',
  clientQuotaExceeded: 'userAction',
  sessionQuotaExceeded: 'userAction',
  resourceTooLarge: 'userAction',
  resourceEmpty: 'userAction',
  unsupportedMediaType: 'userAction',
  lyricsImportFailed: 'userAction',
  emptyLyrics: 'userAction',
  noAudioStream: 'userAction',
  audioPreparationFailed: 'userAction',
  commandRejected: 'userAction',
  dirtySession: 'userAction',
  activeJobs: 'userAction',
  writeLeaseConflict: 'userAction',
  readOnlySession: 'userAction',
  pathNotAllowed: 'userAction',
  projectLoadFailed: 'userAction',

  hostRestarted: 'connection',
  unauthorizedClient: 'connection',
  clientTokenExpired: 'connection',
  sessionClosing: 'connection',

  requestIdReuse: 'defect',
  invalidRequest: 'defect',
  commandNotSupported: 'defect',
  requestExpired: 'defect',
};

export function categorizeErrorCode(code: string | null | undefined): ErrorCategory {
  if (!code) return 'other';
  return CATEGORY[code as BackendErrorCode] ?? 'other';
}

export function isBackendErrorResponse(value: unknown): value is BackendErrorResponse {
  return (
    typeof value === 'object' &&
    value !== null &&
    typeof (value as { code?: unknown }).code === 'string' &&
    typeof (value as { message?: unknown }).message === 'string'
  );
}
