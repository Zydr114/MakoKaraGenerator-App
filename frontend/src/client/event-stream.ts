// EventStream：用 fetch＋ReadableStream 读取 SSE（设计 §3.5）。
// 原生 EventSource 不能带 Authorization 头，所以不用它。
// - 每个 ClientSession 只有一条连接（服务端每 client 最多 2 条、全局 4 条）。
// - 重连用 Last-Event-ID 携带最近游标；初次连接用 client state 的 eventCursor。
// - 约 15 秒一次 `: heartbeat` 注释；45 秒没收到任何字节判定连接已死，主动中止并重连。
// - 429 按 1s、2s、4s…（上限 30s）退避。
// - resync.required：交给 onResync 重新读取 client state，用新游标重连。
// - client.expired：走 resume 后重连。
// 事件只是提示，订阅者需要权威数据时自己去 GET。

import {
  SSE_CLIENT_EXPIRED,
  SSE_RESYNC_REQUIRED,
  type BackendEvent,
  type ClientEventsResponse,
} from '../protocol';
import { BackendError, NetworkError } from './errors';
import { Emitter } from './observable';
import { decideRetry } from './retry';
import type { ClientSession } from './session';
import { SseParser } from './sse-parser';
import type { HttpTransport } from './transport';

export type EventStreamStatus = 'stopped' | 'connecting' | 'open' | 'waiting';

export interface EventStreamLog {
  kind: 'connect' | 'open' | 'close' | 'resync' | 'expired' | 'idle' | 'error' | 'backoff' | 'manual';
  message: string;
  cursor?: string;
}

export interface EventStreamEvents extends Record<string, unknown> {
  event: BackendEvent;
  status: EventStreamStatus;
  log: EventStreamLog;
}

export interface EventStreamOptions {
  transport: HttpTransport;
  session: ClientSession;
  /** 处理 resync.required：重新读取 client state 等权威状态，返回新的 eventCursor。 */
  onResync: (reason: string) => Promise<string>;
  idleTimeoutMs?: number;
}

type ReadOutcome =
  | { kind: 'resync' }
  | { kind: 'expired' }
  | { kind: 'ended' }
  | { kind: 'idle' }
  | { kind: 'aborted' }
  | { kind: 'error'; error: unknown };

const MAX_BACKOFF_MS = 30_000;

export class EventStream extends Emitter<EventStreamEvents> {
  private readonly transport: HttpTransport;
  private readonly session: ClientSession;
  private readonly onResync: (reason: string) => Promise<string>;
  private readonly idleTimeoutMs: number;

  private cursorValue: string | undefined;
  private statusValue: EventStreamStatus = 'stopped';
  private running = false;
  private current: AbortController | undefined;
  private wakeUp: (() => void) | undefined;
  private pendingCursorOverride: string | undefined;
  private reconnectCount = 0;
  private receivedCount = 0;

  constructor(options: EventStreamOptions) {
    super();
    this.transport = options.transport;
    this.session = options.session;
    this.onResync = options.onResync;
    this.idleTimeoutMs = options.idleTimeoutMs ?? 45_000;
  }

  get status(): EventStreamStatus {
    return this.statusValue;
  }

  get cursor(): string | undefined {
    return this.cursorValue;
  }

  get stats(): { reconnects: number; received: number } {
    return { reconnects: this.reconnectCount, received: this.receivedCount };
  }

  start(cursor: string | undefined): void {
    if (this.running) return;
    this.cursorValue = cursor;
    this.running = true;
    void this.loop();
  }

  stop(): void {
    if (!this.running) return;
    this.running = false;
    this.current?.abort();
    this.wakeUp?.();
    this.setStatus('stopped');
  }

  /**
   * 手动断开并立即重连（调试用）。cursorOverride 用于人为制造游标缺口：
   * 一个超前的游标会让服务端发 resync.required。
   */
  reconnect(cursorOverride?: string): void {
    if (!this.running) return;
    this.pendingCursorOverride = cursorOverride;
    this.emit('log', {
      kind: 'manual',
      message: cursorOverride ? `手动重连，使用游标 ${cursorOverride}` : '手动断开并重连',
    });
    this.current?.abort();
    this.wakeUp?.();
  }

  private async loop(): Promise<void> {
    let backoffMs = 0;
    while (this.running) {
      const controller = new AbortController();
      this.current = controller;
      const cursor = this.pendingCursorOverride ?? this.cursorValue;
      this.pendingCursorOverride = undefined;
      let outcome: ReadOutcome;
      try {
        this.setStatus('connecting');
        this.emit('log', { kind: 'connect', message: '连接事件流', ...(cursor ? { cursor } : {}) });
        const response = await this.transport.request<Response>({
          method: 'GET',
          path: '/api/v1/events',
          auth: 'access',
          headers: { Accept: 'text/event-stream', ...(cursor ? { 'Last-Event-ID': cursor } : {}) },
          response: 'stream',
          signal: controller.signal,
          // 后端 0.75.1 在没有可重放事件时，直到第一次 `: heartbeat`（约 15 秒）才发出响应头，
          // 所以响应头阶段也用空闲判死时间，而不是普通请求的超时（见设计 §9 G7）。
          timeoutMs: this.idleTimeoutMs,
          label: 'events',
        });
        this.setStatus('open');
        this.emit('log', { kind: 'open', message: '事件流已连接' });
        backoffMs = 0;
        outcome = await this.read(response.data, controller);
      } catch (error) {
        outcome = controller.signal.aborted ? { kind: 'aborted' } : { kind: 'error', error };
      }
      if (!this.running) break;
      this.reconnectCount++;

      switch (outcome.kind) {
        case 'resync': {
          try {
            this.cursorValue = await this.onResync('resync.required');
            this.emit('log', { kind: 'resync', message: '已重新读取 client state，用新游标重连', cursor: this.cursorValue });
          } catch (error) {
            backoffMs = nextBackoff(backoffMs);
            this.emit('log', { kind: 'error', message: `重同步失败：${describe(error)}` });
            await this.wait(backoffMs);
          }
          break;
        }
        case 'expired':
          this.emit('log', { kind: 'expired', message: '服务端报告访问令牌过期，resume 后重连' });
          await this.session.resume().catch(() => undefined);
          if (this.session.state !== 'ready') return this.halt();
          break;
        case 'idle':
          this.emit('log', { kind: 'idle', message: `${this.idleTimeoutMs / 1000} 秒未收到任何字节，判定连接已死，重连` });
          break;
        case 'aborted':
          break;
        case 'ended':
          backoffMs = 1000;
          this.emit('log', { kind: 'close', message: '服务端关闭了事件流，1 秒后重连' });
          await this.wait(backoffMs);
          break;
        case 'error': {
          const proceed = await this.handleError(outcome.error);
          if (!proceed) return this.halt();
          if (proceed === 'backoff') {
            backoffMs = nextBackoff(backoffMs);
            this.emit('log', { kind: 'backoff', message: `${describe(outcome.error)}；${backoffMs / 1000} 秒后重连` });
            await this.wait(backoffMs);
          }
          break;
        }
      }
    }
  }

  /** 返回 false 表示停止事件流；'now' 立即重连；'backoff' 退避后重连。 */
  private async handleError(error: unknown): Promise<false | 'now' | 'backoff'> {
    if (error instanceof BackendError && error.status === 429) return 'backoff';
    const decision = decideRetry(error, false);
    if (decision === 'resume') {
      await this.session.resume().catch(() => undefined);
      return this.session.state === 'ready' ? 'now' : false;
    }
    if (decision === 'reauthenticate') {
      const state = await this.session.handleUnauthorized();
      return state === 'ready' ? 'now' : state === 'refreshing' ? 'backoff' : false;
    }
    if (decision === 'hostRestarted') {
      this.session.markHostRestarted();
      return false;
    }
    if (error instanceof NetworkError || decision === 'retry') return 'backoff';
    this.emit('log', { kind: 'error', message: describe(error) });
    return 'backoff';
  }

  private halt(): void {
    this.running = false;
    this.setStatus('stopped');
    this.emit('log', { kind: 'close', message: `事件流停止（连接状态 ${this.session.state}）` });
  }

  private async read(response: Response, controller: AbortController): Promise<ReadOutcome> {
    const body = response.body;
    if (!body) return { kind: 'ended' };
    const reader = body.getReader();
    const decoder = new TextDecoder();
    const parser = new SseParser();
    let idle = false;
    let idleTimer: ReturnType<typeof setTimeout> | undefined;
    const resetIdle = () => {
      if (idleTimer !== undefined) clearTimeout(idleTimer);
      idleTimer = setTimeout(() => {
        idle = true;
        controller.abort();
      }, this.idleTimeoutMs);
    };
    // 不依赖 fetch 把中止传播到正文流：中止时显式取消 reader，让挂起的 read() 立即返回。
    const onAbort = () => void reader.cancel().catch(() => undefined);
    controller.signal.addEventListener('abort', onAbort, { once: true });
    resetIdle();
    try {
      for (;;) {
        const { value, done } = await reader.read();
        if (done) return idle ? { kind: 'idle' } : controller.signal.aborted ? { kind: 'aborted' } : { kind: 'ended' };
        resetIdle();
        const { messages } = parser.push(decoder.decode(value, { stream: true }));
        for (const message of messages) {
          if (message.event === SSE_RESYNC_REQUIRED) {
            const data = safeJson<ClientEventsResponse>(message.data);
            this.emit('log', {
              kind: 'resync',
              message: '收到 resync.required：游标缺口或超前，需要重新读取权威状态',
              ...(data?.eventCursor ? { cursor: data.eventCursor } : {}),
            });
            return { kind: 'resync' };
          }
          if (message.event === SSE_CLIENT_EXPIRED) return { kind: 'expired' };
          const event = safeJson<BackendEvent>(message.data);
          if (!event || typeof event.type !== 'string') continue;
          this.cursorValue = event.eventCursor ?? message.id ?? this.cursorValue;
          this.receivedCount++;
          this.emit('event', event);
        }
      }
    } catch (error) {
      if (idle) return { kind: 'idle' };
      if (controller.signal.aborted) return { kind: 'aborted' };
      return { kind: 'error', error: new NetworkError(`事件流读取中断：${describe(error)}`, { cause: error }) };
    } finally {
      if (idleTimer !== undefined) clearTimeout(idleTimer);
      controller.signal.removeEventListener('abort', onAbort);
      reader.cancel().catch(() => undefined);
    }
  }

  private wait(ms: number): Promise<void> {
    if (ms <= 0 || !this.running) return Promise.resolve();
    this.setStatus('waiting');
    return new Promise((resolve) => {
      const timer = setTimeout(done, ms);
      function done() {
        clearTimeout(timer);
        resolve();
      }
      this.wakeUp = () => {
        this.wakeUp = undefined;
        done();
      };
    });
  }

  private setStatus(status: EventStreamStatus): void {
    if (this.statusValue === status) return;
    this.statusValue = status;
    this.emit('status', status);
  }
}

function nextBackoff(current: number): number {
  return current <= 0 ? 1000 : Math.min(current * 2, MAX_BACKOFF_MS);
}

function safeJson<T>(text: string): T | undefined {
  try {
    return JSON.parse(text) as T;
  } catch {
    return undefined;
  }
}

function describe(error: unknown): string {
  if (error instanceof BackendError) return `${error.code ?? `HTTP ${error.status}`}：${error.message}`;
  return error instanceof Error ? error.message : String(error);
}
