// 客户端库抛出的结构化错误。库只抛错误，不负责文案（设计 §6）。

import { categorizeErrorCode, type BackendErrorResponse, type ErrorCategory } from '../protocol';

/** 后端返回的非 2xx 响应。code 为 null 表示没有 JSON 错误正文（如 416）。 */
export class BackendError extends Error {
  override readonly name = 'BackendError';
  readonly status: number;
  readonly code: string | null;
  readonly field: string | undefined;
  readonly failureCode: string | undefined;
  readonly body: BackendErrorResponse | undefined;

  constructor(status: number, body: BackendErrorResponse | undefined, fallbackMessage?: string) {
    super(body?.message ?? fallbackMessage ?? `HTTP ${status}`);
    this.status = status;
    this.code = body?.code ?? null;
    this.field = body?.field;
    this.failureCode = body?.failureCode;
    this.body = body;
  }

  get category(): ErrorCategory {
    return categorizeErrorCode(this.code);
  }
}

/** fetch 本身失败或响应流中断：请求可能到达也可能没到达服务端。 */
export class NetworkError extends Error {
  override readonly name = 'NetworkError';
  constructor(message: string, options?: { cause?: unknown }) {
    super(message, options);
  }
}

/**
 * 有副作用请求的结果未知：重试窗口用完，或服务端返回了可能已固定在回执里的 internalError。
 * 调用方应提示“请稍候，正在核对状态”并重新读取权威状态，不能当作失败，也不能换 requestId 重做（设计 §3.3、§6）。
 */
export class OutcomeUnknownError extends Error {
  override readonly name = 'OutcomeUnknownError';
  readonly requestId: string;
  constructor(requestId: string, cause: unknown) {
    super(`请求 ${requestId} 的结果未知，需要重新读取权威状态。`, { cause });
    this.requestId = requestId;
  }
}

/** 当前连接状态不允许发请求（需要重新登记、主机已重启或初始化失败）。 */
export class ConnectionUnavailableError extends Error {
  override readonly name = 'ConnectionUnavailableError';
  readonly state: string;
  constructor(state: string, options?: { cause?: unknown }) {
    super(`连接状态为 ${state}，不能发送请求。`, options);
    this.state = state;
  }
}

/** 工程队列因修订冲突被清空，这条命令没有发出。 */
export class CommandDiscardedError extends Error {
  override readonly name = 'CommandDiscardedError';
  constructor(cause: unknown) {
    super('同一工程上前一条命令发生修订冲突，未发出的命令已丢弃；请重新读取工程状态。', { cause });
  }
}

/** 预览渲染失败（状态为 failed），带服务端的稳定失败编号。 */
export class PreviewFailedError extends Error {
  override readonly name = 'PreviewFailedError';
  readonly failure: BackendErrorResponse | undefined;
  readonly renderFailureCode: string | undefined;
  constructor(failure: BackendErrorResponse | undefined, renderFailureCode: string | undefined) {
    super(failure?.message ?? `预览失败${renderFailureCode ? `（${renderFailureCode}）` : ''}`);
    this.failure = failure;
    this.renderFailureCode = renderFailureCode;
  }
}

export function isAbortError(error: unknown): boolean {
  return error instanceof Error && error.name === 'AbortError';
}

export function isBackendError(error: unknown, code?: string): error is BackendError {
  return error instanceof BackendError && (code === undefined || error.code === code);
}

/** 把任何错误转成展示用分类；结果未知单独一类。 */
export function describeErrorCategory(error: unknown): ErrorCategory | 'unknownOutcome' | 'network' {
  if (error instanceof OutcomeUnknownError) return 'unknownOutcome';
  if (error instanceof NetworkError) return 'network';
  if (error instanceof ConnectionUnavailableError) return 'connection';
  if (error instanceof BackendError) return error.category;
  return 'other';
}
