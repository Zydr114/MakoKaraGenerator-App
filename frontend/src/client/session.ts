// ClientSession：登记、恢复与心跳（设计 §3.4）。
//
// 服务端事实（规范 §3、交接 §2.3）：
// - 新登记生成 clientId／accessToken／resumeToken；resumeToken 不轮换。
// - resume 需要 bootstrap 凭证＋resumeToken，成功后轮换 accessToken，旧值立即失效。
// - 心跳延长 accessToken（默认 120 秒）与租约（默认 300 秒），不返回新令牌。
// - 0.75.1 起登记配额不足时回收过期空闲 client；被回收后 access 与 resume 均 401。
// - Server 重启后旧 access 常先得 401 unauthorizedClient（认证先于 host 检查），不能只等 409 hostRestarted。

import { PROTOCOL_VERSION, type ClientHeartbeatResponse, type ClientRegistrationResponse, type HealthResponse } from '../protocol';
import type { BootstrapInfo } from './bootstrap';
import type { ServerClock } from './clock';
import { BackendError, ConnectionUnavailableError } from './errors';
import type { IdentityStore } from './identity-store';
import { ObservableValue } from './observable';
import { decideRetry } from './retry';
import type { AuthKind, HttpTransport } from './transport';

export type ConnectionState = 'connecting' | 'ready' | 'refreshing' | 'needsRegistration' | 'hostRestarted' | 'failed';

export interface SessionSnapshot {
  state: ConnectionState;
  hostInstanceId?: string;
  clientId?: string;
  /** 以下均为服务端时间（毫秒）。 */
  tokenExpiresAtMs?: number;
  leaseExpiresAtMs?: number;
  heartbeat: 'idle' | 'ok' | 'failing';
  lastHeartbeatAtMs?: number;
  heartbeatIntervalMs?: number;
  /** 本次身份来自新登记还是 resume。 */
  origin?: 'registered' | 'resumed';
  /** 本会话累计 resume 次数（含启动时恢复）。 */
  resumeCount: number;
  reason?: string;
}

export interface ClientSessionOptions {
  bootstrap: BootstrapInfo;
  transport: HttpTransport;
  clock: ServerClock;
  store: IdentityStore;
  log?: (message: string) => void;
}

const MIN_HEARTBEAT_MS = 5_000;
const FAILING_HEARTBEAT_RETRY_MS = 5_000;
/** unauthorizedClient 后自动 resume 的冷却期：避免复制标签页共享身份时互相轮换令牌、无限循环。 */
const UNAUTHORIZED_RESUME_COOLDOWN_MS = 60_000;

export class ClientSession {
  readonly snapshot = new ObservableValue<SessionSnapshot>({ state: 'connecting', heartbeat: 'idle', resumeCount: 0 });

  private readonly bootstrap: BootstrapInfo;
  private readonly transport: HttpTransport;
  private readonly clock: ServerClock;
  private readonly store: IdentityStore;
  private readonly log: (message: string) => void;

  private hostInstanceId: string | undefined;
  private clientId: string | undefined;
  private accessToken: string | undefined;
  private resumeToken: string | undefined;
  private heartbeatTimer: ReturnType<typeof setTimeout> | undefined;
  private resumeFlight: Promise<void> | undefined;
  private unauthorizedFlight: Promise<ConnectionState> | undefined;
  private lastUnauthorizedResumeAt = Number.NEGATIVE_INFINITY;
  private disposed = false;

  constructor(options: ClientSessionOptions) {
    this.bootstrap = options.bootstrap;
    this.transport = options.transport;
    this.clock = options.clock;
    this.store = options.store;
    this.log = options.log ?? (() => undefined);
  }

  get state(): ConnectionState {
    return this.snapshot.get().state;
  }

  get identity(): { hostInstanceId: string; clientId: string } {
    if (!this.hostInstanceId || !this.clientId) throw new ConnectionUnavailableError(this.state);
    return { hostInstanceId: this.hostInstanceId, clientId: this.clientId };
  }

  /** 供 HttpTransport 使用的认证头。 */
  authHeaders(kind: AuthKind): Record<string, string> {
    if (kind === 'none') return {};
    if (kind === 'bootstrap') return { Authorization: `Bearer ${this.bootstrap.bootstrapToken}` };
    if (!this.accessToken || !this.clientId || !this.hostInstanceId) return {};
    return {
      Authorization: `Bearer ${this.accessToken}`,
      'X-Mako-Client-Id': this.clientId,
      'X-Mako-Host-Instance-Id': this.hostInstanceId,
    };
  }

  /** 初始化：health 校验 → 有同 host 的已存身份则 resume，失败才新登记。 */
  async initialize(): Promise<void> {
    this.patch({ state: 'connecting' });
    try {
      const health = await this.health();
      if (this.bootstrap.hostInstanceId && health.hostInstanceId !== this.bootstrap.hostInstanceId) {
        throw new Error('启动信息中的 hostInstanceId 与 Server 不一致：Server 可能已重启，请重新获取启动信息。');
      }
      this.hostInstanceId = health.hostInstanceId;
      this.patch({ hostInstanceId: health.hostInstanceId });

      const stored = this.store.load(health.hostInstanceId);
      if (stored) {
        try {
          await this.register({ resumeClientId: stored.clientId, resumeToken: stored.resumeToken }, 'resumed');
          this.log(`已恢复 client ${stored.clientId}，未新增登记。`);
          return;
        } catch (error) {
          if (!(error instanceof BackendError) || (error.status !== 401 && error.status !== 400)) throw error;
          this.log(`保存的身份无法恢复（${error.code ?? error.status}），改为新登记。`);
          this.store.clear();
        }
      }
      await this.register({}, 'registered');
      this.log(`新登记 client ${this.clientId}。`);
    } catch (error) {
      this.patch({ state: 'failed', reason: errorText(error) });
      throw error;
    }
  }

  /** needsRegistration 状态下由用户触发：放弃旧身份，新登记一个 client。 */
  async registerNew(): Promise<void> {
    this.store.clear();
    this.clearCredentials();
    this.patch({ state: 'connecting', reason: undefined });
    try {
      await this.register({}, 'registered');
      this.log(`重新登记 client ${this.clientId}。`);
    } catch (error) {
      this.patch({ state: 'needsRegistration', reason: errorText(error) });
      throw error;
    }
  }

  /** 恢复访问令牌。并发调用只发起一次（single-flight），其他调用等待同一结果。 */
  resume(): Promise<void> {
    this.resumeFlight ??= this.doResume().finally(() => {
      this.resumeFlight = undefined;
    });
    return this.resumeFlight;
  }

  private async doResume(): Promise<void> {
    if (!this.clientId || !this.resumeToken) throw new ConnectionUnavailableError(this.state);
    const previous = this.state;
    this.patch({ state: 'refreshing' });
    try {
      await this.register({ resumeClientId: this.clientId, resumeToken: this.resumeToken }, 'resumed');
      this.log(`令牌已通过 resume 轮换（client ${this.clientId}）。`);
    } catch (error) {
      if (error instanceof BackendError && error.status === 401) {
        // resume 被拒：要么 Server 已重启，要么身份已被回收。用 health 区分。
        await this.classifyLostIdentity('resume 被拒绝');
      } else if (this.state === 'refreshing') {
        this.patch({ state: previous === 'refreshing' ? 'ready' : previous });
      }
      throw error;
    }
  }

  /**
   * 处理 401 unauthorizedClient。single-flight。返回处理后的状态：
   * - host 变了 → hostRestarted；
   * - 同一 host → 在冷却期外尝试一次 resume（G1 回收检查期间可能临时 401），失败则 needsRegistration；
   * - Server 不可达 → 状态不变，由调用方按网络错误处理。
   */
  handleUnauthorized(): Promise<ConnectionState> {
    this.unauthorizedFlight ??= this.doHandleUnauthorized().finally(() => {
      this.unauthorizedFlight = undefined;
    });
    return this.unauthorizedFlight;
  }

  private async doHandleUnauthorized(): Promise<ConnectionState> {
    if (this.state !== 'ready' && this.state !== 'refreshing') return this.state;
    let health: HealthResponse;
    try {
      health = await this.health();
    } catch {
      return this.state;
    }
    if (health.hostInstanceId !== this.hostInstanceId) {
      this.markHostRestarted(`Server 已重启（新 host ${health.hostInstanceId}）`);
      return this.state;
    }
    const now = Date.now();
    if (now - this.lastUnauthorizedResumeAt >= UNAUTHORIZED_RESUME_COOLDOWN_MS) {
      this.lastUnauthorizedResumeAt = now;
      try {
        await this.resume();
        return this.state;
      } catch {
        return this.state;
      }
    }
    this.toNeedsRegistration('凭证已失效或在别处被轮换（例如复制的标签页共享了身份）');
    return this.state;
  }

  private async classifyLostIdentity(context: string): Promise<void> {
    try {
      const health = await this.health();
      if (health.hostInstanceId !== this.hostInstanceId) {
        this.markHostRestarted(`Server 已重启（新 host ${health.hostInstanceId}）`);
        return;
      }
    } catch {
      // Server 不可达时也无法 resume；按需要重新登记处理，由页面决定何时重试。
    }
    this.toNeedsRegistration(`${context}：身份已被回收或凭证失效`);
  }

  /** 服务已重启：清空所有本地身份，回到初始化流程。 */
  markHostRestarted(reason = 'Server 已重启'): void {
    if (this.state === 'hostRestarted') return;
    this.stopHeartbeat();
    this.store.clear();
    this.clearCredentials();
    this.log(reason);
    this.patch({ state: 'hostRestarted', reason, clientId: undefined, tokenExpiresAtMs: undefined, leaseExpiresAtMs: undefined, heartbeat: 'idle' });
  }

  private toNeedsRegistration(reason: string): void {
    this.stopHeartbeat();
    this.store.clear();
    this.clearCredentials();
    this.log(reason);
    this.patch({ state: 'needsRegistration', reason, heartbeat: 'idle' });
  }

  async heartbeat(): Promise<void> {
    if (this.state !== 'ready' || !this.clientId) return;
    this.stopHeartbeat();
    try {
      const response = await this.transport.request<ClientHeartbeatResponse>({
        method: 'POST',
        path: `/api/v1/clients/${encodeURIComponent(this.clientId)}/heartbeat`,
        auth: 'access',
        label: 'clients.heartbeat',
      });
      this.applyExpiry(response.data);
      this.patch({ heartbeat: 'ok', lastHeartbeatAtMs: this.clock.now() });
      this.scheduleHeartbeat();
    } catch (error) {
      if (this.disposed) return;
      const decision = decideRetry(error, false);
      if (decision === 'resume') {
        await this.resume().then(
          () => this.heartbeat(),
          () => undefined,
        );
      } else if (decision === 'reauthenticate') {
        await this.handleUnauthorized();
      } else if (decision === 'hostRestarted') {
        this.markHostRestarted();
      } else {
        this.patch({ heartbeat: 'failing', reason: errorText(error) });
        this.scheduleHeartbeat(FAILING_HEARTBEAT_RETRY_MS);
      }
    }
  }

  /** 页面回到前台：令牌将过期则直接 resume，否则立即补一次心跳。 */
  notifyVisible(): void {
    if (this.state !== 'ready') return;
    const expires = this.snapshot.get().tokenExpiresAtMs;
    if (expires !== undefined && expires - this.clock.now() < MIN_HEARTBEAT_MS) {
      void this.resume().catch(() => undefined);
    } else {
      void this.heartbeat();
    }
  }

  dispose(): void {
    this.disposed = true;
    this.stopHeartbeat();
  }

  // ── 内部 ──────────────────────────────────────────────

  private async health(): Promise<HealthResponse> {
    const response = await this.transport.request<HealthResponse>({
      method: 'GET',
      path: '/api/v1/health/live',
      auth: 'none',
      label: 'health',
      timeoutMs: 10_000,
    });
    return response.data;
  }

  private async register(
    resume: { resumeClientId?: string; resumeToken?: string },
    origin: 'registered' | 'resumed',
  ): Promise<void> {
    const { data } = await this.transport.request<ClientRegistrationResponse>({
      method: 'POST',
      path: '/api/v1/clients',
      auth: 'bootstrap',
      json: { protocolVersion: PROTOCOL_VERSION, ...resume },
      label: origin === 'resumed' ? 'clients.resume' : 'clients.register',
    });
    if (this.hostInstanceId && data.hostInstanceId !== this.hostInstanceId) {
      throw new Error(`登记响应的 hostInstanceId 与 health 不一致：${data.hostInstanceId}`);
    }
    this.hostInstanceId = data.hostInstanceId;
    this.clientId = data.clientId;
    this.accessToken = data.accessToken;
    this.resumeToken = data.resumeToken;
    this.store.save(data.hostInstanceId, { clientId: data.clientId, resumeToken: data.resumeToken });
    this.applyExpiry(data);
    const previous = this.snapshot.get();
    this.patch({
      state: 'ready',
      reason: undefined,
      hostInstanceId: data.hostInstanceId,
      clientId: data.clientId,
      origin: previous.clientId === data.clientId && previous.origin ? previous.origin : origin,
      resumeCount: previous.resumeCount + (origin === 'resumed' ? 1 : 0),
    });
    this.scheduleHeartbeat();
  }

  private applyExpiry(data: { tokenExpiresAtUtc: string; leaseExpiresAtUtc: string; serverTimeUtc: string }): void {
    this.clock.observe(data.serverTimeUtc);
    const server = Date.parse(data.serverTimeUtc);
    const token = Date.parse(data.tokenExpiresAtUtc);
    const lease = Date.parse(data.leaseExpiresAtUtc);
    // 心跳间隔取 min(令牌剩余, 租约剩余) / 3，按服务端返回的过期时间计算而不是写死。
    const interval = Math.max(MIN_HEARTBEAT_MS, Math.min(token - server, lease - server) / 3);
    this.patch({ tokenExpiresAtMs: token, leaseExpiresAtMs: lease, heartbeatIntervalMs: interval });
  }

  private scheduleHeartbeat(delayMs?: number): void {
    this.stopHeartbeat();
    if (this.disposed || this.state !== 'ready') return;
    const delay = delayMs ?? this.snapshot.get().heartbeatIntervalMs ?? 30_000;
    this.heartbeatTimer = setTimeout(() => void this.heartbeat(), delay);
  }

  private stopHeartbeat(): void {
    if (this.heartbeatTimer !== undefined) clearTimeout(this.heartbeatTimer);
    this.heartbeatTimer = undefined;
  }

  private clearCredentials(): void {
    this.clientId = undefined;
    this.accessToken = undefined;
    this.resumeToken = undefined;
  }

  private patch(patch: { [K in keyof SessionSnapshot]?: SessionSnapshot[K] | undefined }): void {
    const next = { ...this.snapshot.get(), ...patch };
    for (const key of Object.keys(next) as (keyof SessionSnapshot)[]) {
      if (next[key] === undefined) delete next[key];
    }
    this.snapshot.set(next as SessionSnapshot);
  }
}

function errorText(error: unknown): string {
  if (error instanceof BackendError) return `${error.code ?? `HTTP ${error.status}`}：${error.message}`;
  return error instanceof Error ? error.message : String(error);
}
