import {
  BackendError,
  ConnectionUnavailableError,
  NetworkError,
  OutcomeUnknownError,
  PreviewFailedError,
  UploadRejectedError,
  describeErrorCategory,
} from '../client';
import { microsToNumber, type LyricTime, type Micros } from '../protocol';

const CATEGORY_LABEL: Record<string, string> = {
  recoverable: '可恢复',
  userAction: '需要用户操作',
  connection: '连接问题',
  defect: '客户端缺陷',
  unknownOutcome: '结果未知',
  network: '网络问题',
  other: '其他错误',
};

export interface DisplayError {
  category: string;
  title: string;
  detail: string;
}

/** 按设计 §6 分类显示错误：程序只按 code 分类，后端中文 message 只作为详情。 */
export function describeError(error: unknown): DisplayError {
  const category = CATEGORY_LABEL[describeErrorCategory(error)] ?? '其他错误';
  if (error instanceof OutcomeUnknownError) {
    return { category, title: '请稍候，正在核对状态', detail: `请求 ${error.requestId} 的结果未知，已重新读取权威状态。` };
  }
  if (error instanceof BackendError) {
    const code = error.code ?? `HTTP ${error.status}`;
    const extra = [error.field && `field=${error.field}`, error.failureCode && `failureCode=${error.failureCode}`].filter(Boolean).join(' ');
    return { category, title: `${code}（HTTP ${error.status}）`, detail: `${error.message}${extra ? ` · ${extra}` : ''}` };
  }
  if (error instanceof PreviewFailedError) {
    return { category: '预览失败', title: error.renderFailureCode ?? error.failure?.code ?? 'failed', detail: error.message };
  }
  if (error instanceof UploadRejectedError) return { category: '需要用户操作', title: `上传预检：${error.reason}`, detail: error.message };
  if (error instanceof ConnectionUnavailableError) return { category, title: `连接状态 ${error.state}`, detail: error.message };
  if (error instanceof NetworkError) return { category, title: '网络请求失败', detail: error.message };
  return { category, title: error instanceof Error ? error.name : '错误', detail: error instanceof Error ? error.message : String(error) };
}

export function formatMicros(value: Micros | undefined): string {
  if (value === undefined) return '—';
  return `${(microsToNumber(value) / 1_000_000).toFixed(3)}s`;
}

export function formatTime(time: LyricTime): string {
  if (time.startUs === undefined && time.endUs === undefined) return '未打轴';
  return `${formatMicros(time.startUs)} → ${formatMicros(time.endUs)}`;
}

export function formatCountdown(ms: number | undefined): string {
  if (ms === undefined || !Number.isFinite(ms)) return '—';
  const seconds = Math.round(ms / 1000);
  return seconds <= 0 ? '已过期' : `${seconds}s`;
}

export function clockTime(ms: number): string {
  return new Date(ms).toLocaleTimeString('zh-CN', { hour12: false }) + '.' + String(ms % 1000).padStart(3, '0');
}

/** 游标很长，只显示尾部序号部分。 */
export function shortCursor(cursor: string | undefined): string {
  if (!cursor) return '—';
  const parts = cursor.split(':');
  return parts.length > 1 ? `…:${parts.at(-1)}` : cursor;
}

export function shortId(id: string | undefined): string {
  if (!id) return '—';
  return id.length > 18 ? `${id.slice(0, 14)}…${id.slice(-4)}` : id;
}
