// ServerClock 与 ReceiptFactory（设计 §3.2）。
// 回执截止时间必须按服务端时间计算，不能用浏览器本地时间。

import type { RequestMetadata } from '../protocol';

export class ServerClock {
  private offsetMs = 0;
  private synced = false;
  private readonly localNow: () => number;

  constructor(localNow: () => number = Date.now) {
    this.localNow = localNow;
  }

  /** 收到 serverTimeUtc 时更新偏移量 offset = serverTime - 本地时间。 */
  observe(serverTimeUtc: string, localAtMs: number = this.localNow()): void {
    const serverMs = Date.parse(serverTimeUtc);
    if (!Number.isFinite(serverMs)) return;
    this.offsetMs = serverMs - localAtMs;
    this.synced = true;
  }

  /** 估计的当前服务端时间（毫秒）。 */
  now(): number {
    return this.localNow() + this.offsetMs;
  }

  get offset(): number {
    return this.offsetMs;
  }

  get isSynced(): boolean {
    return this.synced;
  }
}

export interface Receipt extends RequestMetadata {
  /** validUntilUtc 对应的服务端毫秒时间，用于判断还能不能用同一回执重试。 */
  readonly deadlineMs: number;
}

export interface ReceiptFactoryOptions {
  /** 默认重试窗口（秒），设计取 60。 */
  windowSeconds?: number;
  /** capabilities.maxCommandRetryWindowSeconds；窗口不得超过它。 */
  maxWindowSeconds?: () => number | undefined;
  randomId?: () => string;
}

export class ReceiptFactory {
  private readonly clock: ServerClock;
  private readonly hostInstanceId: () => string;
  private readonly windowSeconds: number;
  private readonly maxWindowSeconds: () => number | undefined;
  private readonly randomId: () => string;

  constructor(clock: ServerClock, hostInstanceId: () => string, options: ReceiptFactoryOptions = {}) {
    this.clock = clock;
    this.hostInstanceId = hostInstanceId;
    this.windowSeconds = options.windowSeconds ?? 60;
    this.maxWindowSeconds = options.maxWindowSeconds ?? (() => undefined);
    this.randomId = options.randomId ?? (() => crypto.randomUUID());
  }

  /** operation 作为 requestId 前缀，便于读日志，例如 cmd-<uuid>。 */
  create(operation: string): Receipt {
    const max = this.maxWindowSeconds();
    const seconds = max !== undefined && max > 0 ? Math.min(this.windowSeconds, max) : this.windowSeconds;
    const deadlineMs = Math.floor(this.clock.now()) + seconds * 1000;
    return {
      requestId: `${operation}-${this.randomId()}`.slice(0, 128),
      hostInstanceId: this.hostInstanceId(),
      validUntilUtc: new Date(deadlineMs).toISOString(),
      deadlineMs,
    };
  }
}

/** 去掉 deadlineMs，得到可以直接放进 JSON 的回执三元组。 */
export function toMetadata(receipt: Receipt): RequestMetadata {
  return {
    requestId: receipt.requestId,
    hostInstanceId: receipt.hostInstanceId,
    validUntilUtc: receipt.validUntilUtc,
  };
}
