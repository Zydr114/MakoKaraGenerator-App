// HttpTransport：对 fetch 的唯一封装（设计 §3.1）。其他模块不直接调用 fetch。
// - 拼接 Server 实际监听地址（不是页面 origin）；拒绝把凭证发往其他 origin。
// - 统一附加认证头；凭证永不进入 URL。
// - 非 2xx 解析 BackendErrorResponse 抛 BackendError；fetch 失败抛 NetworkError。

import { isBackendErrorResponse, type BackendErrorResponse } from '../protocol';
import { BackendError, NetworkError, isAbortError } from './errors';
import { assertNoCredentialInUrl, redact, redactHeaders } from './redact';

export type AuthKind = 'access' | 'bootstrap' | 'none';
export type ResponseKind = 'json' | 'blob' | 'stream' | 'none';
export type HttpMethod = 'GET' | 'POST' | 'PUT' | 'DELETE' | 'HEAD';

export interface TransportRequest {
  method: HttpMethod;
  /** 以 / 开头的路径，或与 baseUrl 同源的绝对 URL（例如 contentUrl）。 */
  path: string;
  query?: Record<string, string | undefined>;
  json?: unknown;
  body?: Blob | ArrayBuffer | Uint8Array;
  contentType?: string;
  headers?: Record<string, string>;
  auth: AuthKind;
  response?: ResponseKind;
  /** 除 2xx 以外按成功处理的状态（二进制读取的 304、416）。 */
  okStatuses?: readonly number[];
  signal?: AbortSignal;
  /** 单次请求超时（毫秒），不含 stream 的正文读取；默认 30 秒。 */
  timeoutMs?: number;
  /** 日志标签，例如 projects.command。 */
  label?: string;
}

export interface TransportResponse<T> {
  status: number;
  headers: Headers;
  data: T;
}

export interface ExchangeRecord {
  id: number;
  label: string | undefined;
  method: HttpMethod;
  url: string;
  requestHeaders: Record<string, string>;
  requestBody?: unknown;
  status?: number;
  responseBody?: unknown;
  error?: string;
  startedAt: number;
  durationMs: number;
}

export type ExchangeObserver = (record: ExchangeRecord) => void;

export interface HttpTransportOptions {
  baseUrl: string;
  /** 返回当前认证头；resume 轮换令牌后，重试请求自动用新值。 */
  authHeaders: (kind: AuthKind) => Record<string, string>;
  fetch?: typeof fetch;
  observer?: ExchangeObserver;
  defaultTimeoutMs?: number;
}

let exchangeCounter = 0;

export class HttpTransport {
  readonly baseUrl: string;
  private readonly origin: string;
  private readonly authHeaders: (kind: AuthKind) => Record<string, string>;
  private readonly fetchImpl: typeof fetch;
  private readonly observer: ExchangeObserver | undefined;
  private readonly defaultTimeoutMs: number;

  constructor(options: HttpTransportOptions) {
    const base = new URL(options.baseUrl);
    this.baseUrl = base.origin;
    this.origin = base.origin;
    this.authHeaders = options.authHeaders;
    this.fetchImpl = options.fetch ?? ((input, init) => globalThis.fetch(input, init));
    this.observer = options.observer;
    this.defaultTimeoutMs = options.defaultTimeoutMs ?? 30_000;
  }

  resolveUrl(path: string, query?: Record<string, string | undefined>): URL {
    const url = new URL(path, this.baseUrl);
    if (url.origin !== this.origin) {
      throw new Error(`拒绝向 Server 以外的地址发送请求：${url.origin}`);
    }
    for (const [key, value] of Object.entries(query ?? {})) {
      if (value !== undefined) url.searchParams.set(key, value);
    }
    assertNoCredentialInUrl(url);
    return url;
  }

  async request<T>(req: TransportRequest): Promise<TransportResponse<T>> {
    const url = this.resolveUrl(req.path, req.query);
    const headers: Record<string, string> = { ...this.authHeaders(req.auth), ...req.headers };
    let body: BodyInit | null = null;
    if (req.json !== undefined) {
      headers['Content-Type'] = 'application/json';
      body = JSON.stringify(req.json);
    } else if (req.body !== undefined) {
      if (req.contentType) headers['Content-Type'] = req.contentType;
      body = req.body as BodyInit;
    }
    const responseKind = req.response ?? 'json';
    const timeoutMs = req.timeoutMs ?? this.defaultTimeoutMs;
    const timeout = new AbortController();
    const timer = setTimeout(() => timeout.abort(new NetworkError(`请求超时（${timeoutMs} ms）`)), timeoutMs);
    const signal = req.signal ? AbortSignal.any([req.signal, timeout.signal]) : timeout.signal;

    const record: ExchangeRecord = {
      id: ++exchangeCounter,
      label: req.label,
      method: req.method,
      url: url.pathname + url.search,
      requestHeaders: redactHeaders(headers),
      requestBody: describeBody(req),
      startedAt: Date.now(),
      durationMs: 0,
    };
    const finish = (patch: Partial<ExchangeRecord>) => {
      if (!this.observer) return;
      Object.assign(record, patch, { durationMs: Date.now() - record.startedAt });
      try {
        this.observer(record);
      } catch {
        // 日志观察者失败不影响请求。
      }
    };

    let response: Response;
    try {
      response = await this.fetchImpl(url, { method: req.method, headers, body, signal, cache: 'no-store' });
    } catch (error) {
      clearTimeout(timer);
      const mapped = mapFetchError(error, req.signal, timeout.signal);
      finish({ error: mapped.message });
      throw mapped;
    }

    try {
      const ok = response.ok || (req.okStatuses?.includes(response.status) ?? false);
      if (!ok) {
        const errorBody = await readErrorBody(response, req.method);
        const error = new BackendError(response.status, errorBody);
        finish({ status: response.status, responseBody: errorBody ?? null, error: error.message });
        throw error;
      }
      if (responseKind === 'stream') {
        // 流的生命周期交给调用方；超时只覆盖响应头阶段。
        clearTimeout(timer);
        finish({ status: response.status, responseBody: '<event-stream>' });
        return { status: response.status, headers: response.headers, data: response as T };
      }
      let data: unknown = null;
      const hasBody = req.method !== 'HEAD' && response.status !== 204 && response.status !== 304 && response.status !== 416;
      if (hasBody && responseKind === 'json') data = await response.json();
      else if (hasBody && responseKind === 'blob') data = await response.blob();
      else await response.body?.cancel();
      finish({
        status: response.status,
        responseBody: responseKind === 'json' ? redact(data) : data instanceof Blob ? describeBlob(data) : null,
      });
      return { status: response.status, headers: response.headers, data: data as T };
    } catch (error) {
      if (error instanceof BackendError) throw error;
      const mapped = mapFetchError(error, req.signal, timeout.signal);
      finish({ status: response.status, error: mapped.message });
      throw mapped;
    } finally {
      clearTimeout(timer);
    }
  }
}

async function readErrorBody(response: Response, method: HttpMethod): Promise<BackendErrorResponse | undefined> {
  // 416／HEAD／304 没有 JSON 正文，单独构造错误，不尝试解析（规范 §1.4）。
  const type = response.headers.get('content-type') ?? '';
  if (method === 'HEAD' || !type.includes('json')) {
    await response.body?.cancel().catch(() => undefined);
    return undefined;
  }
  try {
    const parsed: unknown = await response.json();
    return isBackendErrorResponse(parsed) ? parsed : undefined;
  } catch {
    return undefined;
  }
}

function mapFetchError(error: unknown, callerSignal: AbortSignal | undefined, timeoutSignal: AbortSignal): Error {
  if (callerSignal?.aborted) {
    return isAbortError(error) ? (error as Error) : new DOMException('请求已中止', 'AbortError');
  }
  if (timeoutSignal.aborted) {
    return timeoutSignal.reason instanceof Error ? timeoutSignal.reason : new NetworkError('请求超时');
  }
  if (error instanceof NetworkError) return error;
  return new NetworkError(`网络请求失败：${error instanceof Error ? error.message : String(error)}`, { cause: error });
}

function describeBody(req: TransportRequest): unknown {
  if (req.json !== undefined) return redact(req.json);
  if (req.body instanceof Blob) return describeBlob(req.body);
  if (req.body !== undefined) return `<binary ${req.body.byteLength} bytes>`;
  return undefined;
}

function describeBlob(blob: Blob): string {
  return `<binary ${blob.size} bytes${blob.type ? `, ${blob.type}` : ''}>`;
}
