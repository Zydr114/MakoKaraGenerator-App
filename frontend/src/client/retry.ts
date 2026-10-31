// 重试决策表（设计 §3.3，按后端审查 F4 修订）。纯函数，单独做单元测试。

import { BackendError, NetworkError } from './errors';

export type RetryDecision =
  /** 用同一 requestId 和同一 deadline 重发（有副作用）；只读请求直接重发。 */
  | 'retry'
  /** 401 clientTokenExpired：single-flight resume 后用同一 requestId 重发。 */
  | 'resume'
  /**
   * 401 unauthorizedClient：凭证被回收／轮换，或 Server 已重启（认证先于 host 检查，重启后常先得 401）。
   * 由会话比较 health 的 hostInstanceId 决定是 hostRestarted 还是 needsRegistration；不自动无限重试。
   */
  | 'reauthenticate'
  /** 409 hostRestarted：清空本地身份，回到初始化。 */
  | 'hostRestarted'
  /** 有副作用请求遇到 internalError：结果可能已固定在回执里，转“结果未知”，由调用方重读权威状态。 */
  | 'unknownOutcome'
  /** 其他错误（含 revisionConflict、429、requestIdReuse）直接抛给调用方。 */
  | 'fail';

export function decideRetry(error: unknown, sideEffect: boolean): RetryDecision {
  if (error instanceof NetworkError) return 'retry';
  if (!(error instanceof BackendError)) return 'fail';
  if (error.status === 401 && error.code === 'clientTokenExpired') return 'resume';
  if (error.status === 401 && error.code === 'unauthorizedClient') return 'reauthenticate';
  if (error.code === 'hostRestarted') return 'hostRestarted';
  if (error.status === 500 || (error.status >= 500 && error.code === 'internalError')) {
    return sideEffect ? 'unknownOutcome' : 'retry';
  }
  return 'fail';
}

/** 网络重试退避：250ms 起翻倍，上限 2 秒。 */
export function retryDelayMs(attempt: number): number {
  return Math.min(250 * 2 ** attempt, 2000);
}

/** 用同一回执重试的截止余量：deadline 前 5 秒停止（设计 §3.3）。 */
export const RECEIPT_RETRY_MARGIN_MS = 5000;

/** 只读请求的最大重试次数。 */
export const MAX_READ_RETRIES = 3;
