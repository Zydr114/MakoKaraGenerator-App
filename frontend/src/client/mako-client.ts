// MakoClient 门面（设计 §3.9）：按领域暴露方法，内部组合 transport／session／事件流／命令队列／任务等待。
// API 方法只覆盖规范中已实现的路由；没有的能力不在客户端里假装存在。
//
// 一次“连接”绑定一个 Server 进程（hostInstanceId）。Server 重启后旧身份全部失效：
// 状态变为 hostRestarted、清空队列并发出 reset，由页面调用 reinitialize() 重新取启动信息并登记。

import type { AuthorPackageFilesInput, AuthorPackageFilesResponse } from '../protocol/author-package-files';
import type { GpuPreviewCreateInput, GpuPreviewStreamState } from '../protocol';

import {
  DIRECTORY_PAGE_MAX,
  LYRIC_WINDOW_MAX_LIMIT,
  TEMPLATE_LIST_MAX_LIMIT,
  WAVEFORM_WINDOW_MAX_BUCKETS,
  assertSafeJsonInteger,
  decodePeakWindow,
  nextSequence,
  type AnalysisCapabilities,
  type AssetResponse,
  type BackendCapabilities,
  type BackendEvent,
  type ClientStateResponse,
  type ClosePreviewInput,
  type CloseProjectInput,
  type CreatePreviewInput,
  type PresetApplicationPreviewInput,
  type TemplatePreviewInput,
  type CreateProjectSessionInput,
  type EditableLyricLine,
  type ExportCapabilities,
  type ExportJobInput,
  type ExportResultReport,
  type FileHandleInput,
  type FileHandleResponse,
  type FileRootsResponse,
  type JobDiagnosticsPage,
  type JobResponse,
  type LyricBranchesPage,
  type LyricTimingTarget,
  type LyricWindowPage,
  type LyricsExportInput,
  type LyricsExportPlan,
  type LyricsExportPlanInput,
  type LyricsExportResponse,
  type LyricsImportPlan,
  type LyricsImportPlanInput,
  type LyricsLinesResponse,
  type BackendDiagnostic,
  type Micros,
  type OpenProjectInput,
  type PreviewAlphaMode,
  type PreviewDiagnosticsPage,
  type PreviewFrameAcceptedResponse,
  type PreviewFrameMetadata,
  type PreviewFrameRequestInput,
  type PreviewRenderBackendInput,
  type PreviewRuntimeInfo,
  type PreviewSessionResponse,
  type PreviewSourceRevision,
  type PreviewSessionStateResponse,
  type PreviewSize,
  type ProjectCommand,
  type ProjectCommandRequest,
  type ProjectCommandResponse,
  type ProjectDirectoryEntriesResponse,
  type ProjectDirectoryEntry,
  type ProjectHistoryResponse,
  type ProjectResource,
  type ProjectResourcesResponse,
  type ProjectSessionSummary,
  type ProjectSettings,
  type ProjectPresetApplicationsResponse,
  type ProjectGeometryReferenceResponse,
  type ProjectLineLocalTransformResponse,
  type ProjectLinePresentationResponse,
  type ProjectOrchestrationResponse,
  type ProjectStyleResponse,
  type RecoverProjectInput,
  type RecoveryCandidatesResponse,
  type StyleTarget,
  type EffectPackageInspectInput,
  type EffectPackageInspectResponse,
  type AuthorAdoptResponse,
  type AuthorCompilation,
  type AuthorDraft,
  type AuthorDraftConfigurationInput,
  type AuthorDraftCreateInput,
  type AuthorDraftDeleteResponse,
  type AuthorDraftListResponse,
  type AuthorDraftSampleSelection,
  type AuthorDraftSourcesResponse,
  type AuthorSourceFile,
  type AuthorSourceRevisionResponse,
  type AuthorValidationInput,
  type ScriptConfigurationCommand,
  type ScriptConfigurationCommandResponse,
  type ScriptConfigurationDetailResponse,
  type ScriptConfigurationListResponse,
  type FontInspectInput,
  type FontInspectResponse,
  type FontListResponse,
  type ProjectResolvedStyleResponse,
  type ResolvedStyleQuery,
  type TemplateArchiveExportResponse,
  type TemplateDetailResponse,
  type TemplateFullDetailResponse,
  type TemplateImportPreflightResponse,
  type TemplateImportResponse,
  type TemplatePreviewOptionsResponse,
  type TemplatePreviewSource,
  type TemplateLibrariesResponse,
  type TemplateLibraryCommand,
  type TemplateLibraryCommandResponse,
  type TemplateLibraryId,
  type TemplateListResponse,
  type TemplateSummary,
  type ProjectWaveform,
  type RenderBackend,
  type RenderBackendsResponse,
  type ReplacePreviewInput,
  type PreviewSource,
  type SceneMeasurementRequest,
  type SceneMeasurementResponse,
  type Revision,
  type SaveProjectInput,
  type Sequence,
  type ValidationCapabilities,
  type ValidationDiagnostic,
  type ValidationJobInput,
  type ValidationReport,
  type WaveformWindow,
} from '../protocol';
import {
  fetchBinary,
  inferContentType,
  preflightUpload,
  sha256Hex,
  type BinaryResult,
  type FetchBinaryOptions,
} from './binary';
import type { BootstrapInfo, BootstrapProvider } from './bootstrap';
import { ReceiptFactory, ServerClock, toMetadata, type Receipt } from './clock';
import { ProjectCommandQueue } from './command-queue';
import {
  BackendError,
  ConnectionUnavailableError,
  NetworkError,
  OutcomeUnknownError,
  PreviewFailedError,
  isAbortError,
} from './errors';
import { EventStream, type EventStreamLog, type EventStreamStatus } from './event-stream';
import { MemoryIdentityStore, WebStorageIdentityStore, type IdentityStore } from './identity-store';
import { JobWatcher, type WaitForJobOptions } from './job-watcher';
import { Emitter, ObservableValue, type Unsubscribe } from './observable';
import { MAX_READ_RETRIES, RECEIPT_RETRY_MARGIN_MS, decideRetry, retryDelayMs } from './retry';
import { ClientSession, type SessionSnapshot } from './session';
import { HttpTransport, type ExchangeObserver, type TransportRequest, type TransportResponse } from './transport';

export interface MakoSnapshot extends SessionSnapshot {
  baseUrl?: string;
  /** 服务端时间 − 本地时间（毫秒）。 */
  serverOffsetMs: number;
  events: { status: EventStreamStatus; cursor?: string; reconnects: number; received: number };
  productVersion?: string;
}

export interface MakoClientEvents extends Record<string, unknown> {
  /** SSE 业务事件（只是提示）。 */
  event: BackendEvent;
  /** 事件流连接日志：连接、断开、重连、resync。 */
  'stream-log': EventStreamLog;
  /** 事件流缺口后已重新读取 client state；订阅者应重读自己关心的权威状态。 */
  resync: { reason: string; state: ClientStateResponse };
  /** 旧连接身份全部作废（主机重启或重新初始化），页面应丢弃所有工程／预览／任务 ID。 */
  reset: { reason: string };
  log: string;
}

export interface BrowserEnvironment {
  onVisible?: (callback: () => void) => Unsubscribe;
  onPageHide?: (callback: () => void) => Unsubscribe;
  onPageShow?: (callback: (persisted: boolean) => void) => Unsubscribe;
}

export interface MakoClientOptions {
  /** 默认：有 sessionStorage 时用它，否则内存。 */
  identityStore?: IdentityStore;
  fetch?: typeof fetch;
  /** 每次 HTTP 往返（已打码）的观察者，供控制台和测试留档使用。 */
  onExchange?: ExchangeObserver;
  /** 默认在浏览器中自动接 visibilitychange／pagehide／pageshow；传 null 关闭。 */
  environment?: BrowserEnvironment | null;
  jobPollIntervalMs?: number;
  previewPollIntervalMs?: number;
  /** 事件流空闲判死时间，默认 45 秒。 */
  eventIdleTimeoutMs?: number;
}

interface Connection {
  bootstrap: BootstrapInfo;
  transport: HttpTransport;
  session: ClientSession;
  clock: ServerClock;
  receipts: ReceiptFactory;
  events: EventStream;
  capabilities: BackendCapabilities | undefined;
  disposers: Unsubscribe[];
}

export interface StillFrameResult {
  frame: PreviewFrameMetadata;
  blob: Blob;
  sha256: string;
  verified: boolean;
  preview: PreviewSessionResponse;
}

const UNUSABLE_STATES = new Set(['needsRegistration', 'hostRestarted', 'failed']);

/**
 * 服务端要同步准备媒体的命令（换音频、设背景媒体）可能要几十秒，单次请求的超时放宽。
 * 超时后仍按回执重试（同一 requestId），由服务端去重。
 */
const SLOW_COMMAND_TIMEOUT_MS: Partial<Record<ProjectCommand['type'], number>> = {
  'audio.replaceAsset': 180_000,
  'composition.setBackgroundAsset': 180_000,
  'lyrics.importAsset': 120_000,
};

/**
 * 一次性静态帧（renderStill／候选试看／模板试样）同时最多几个。后端每个 client 默认只保留 4 个预览会话，
 * 关闭又是异步释放的，要给页面上长期开着的预览留位置。
 */
const MAX_TRANSIENT_PREVIEWS = 2;
/** 创建预览会话遇到 429 sessionQuotaExceeded 时的退避（毫秒）：等关闭中的会话释放，之后仍满就报给调用方。 */
const PREVIEW_QUOTA_RETRY_MS = [200, 400, 800, 1600, 3200];

export class MakoClient extends Emitter<MakoClientEvents> {
  readonly state$: ObservableValue<MakoSnapshot>;

  private readonly provider: BootstrapProvider;
  private readonly options: MakoClientOptions;
  private readonly identityStore: IdentityStore;
  private readonly queue: ProjectCommandQueue;
  private readonly jobWatcher: JobWatcher;
  private conn: Connection | undefined;
  private environmentDisposers: Unsubscribe[] = [];
  private disposed = false;
  private probing: Promise<void> | undefined;
  private initializing: Promise<void> | undefined;
  private lastProbeAt = 0;
  private transientPreviews = 0;
  private readonly transientWaiters: (() => void)[] = [];

  private constructor(provider: BootstrapProvider, options: MakoClientOptions) {
    super();
    this.provider = provider;
    this.options = options;
    this.identityStore = options.identityStore ?? defaultIdentityStore();
    this.state$ = new ObservableValue<MakoSnapshot>({
      state: 'connecting',
      heartbeat: 'idle',
      resumeCount: 0,
      serverOffsetMs: 0,
      events: { status: 'stopped', reconnects: 0, received: 0 },
    });
    this.queue = new ProjectCommandQueue({
      readRevision: async (id) => (await this.projects.get(id)).projectRevision,
    });
    this.jobWatcher = new JobWatcher({
      getJob: (id, signal) => this.jobs.get(id, signal),
      onEvent: (handler) => this.on('event', handler),
      onResync: (handler) => this.on('resync', () => handler()),
      ...(options.jobPollIntervalMs !== undefined ? { pollIntervalMs: options.jobPollIntervalMs } : {}),
    });
    this.on('event', (event) => this.observeEvent(event));
  }

  /** 创建客户端但不连接；连接失败时页面仍能拿到对象显示状态并重试。 */
  static create(provider: BootstrapProvider, options: MakoClientOptions = {}): MakoClient {
    const client = new MakoClient(provider, options);
    client.attachEnvironment();
    return client;
  }

  static async connect(provider: BootstrapProvider, options: MakoClientOptions = {}): Promise<MakoClient> {
    const client = MakoClient.create(provider, options);
    await client.initialize();
    return client;
  }

  /**
   * 取启动信息 → health → resume 或新登记 → 读能力与 client state → 连接事件流。
   * 并发调用（例如自动重连与手动点击同时发生）共用同一次初始化，避免多登记一个 client。
   */
  initialize(): Promise<void> {
    this.initializing ??= this.doInitialize().finally(() => {
      this.initializing = undefined;
    });
    return this.initializing;
  }

  private async doInitialize(): Promise<void> {
    this.teardownConnection();
    const bootstrap = await this.provider.get().catch((error: unknown) => {
      this.state$.update((s) => ({ ...s, state: 'failed', reason: describe(error) }));
      throw error;
    });
    const clock = new ServerClock();
    let session: ClientSession | undefined;
    const transport = new HttpTransport({
      baseUrl: bootstrap.baseUrl,
      authHeaders: (kind) => session?.authHeaders(kind) ?? {},
      ...(this.options.fetch ? { fetch: this.options.fetch } : {}),
      ...(this.options.onExchange ? { observer: this.options.onExchange } : {}),
    });
    session = new ClientSession({
      bootstrap,
      transport,
      clock,
      store: this.identityStore,
      log: (message) => this.emit('log', message),
    });
    const conn: Connection = {
      bootstrap,
      transport,
      session,
      clock,
      receipts: new ReceiptFactory(clock, () => session.identity.hostInstanceId, {
        maxWindowSeconds: () => conn.capabilities?.maxCommandRetryWindowSeconds,
      }),
      events: new EventStream({
        transport,
        session,
        onResync: (reason) => this.resync(reason),
        ...(this.options.eventIdleTimeoutMs !== undefined ? { idleTimeoutMs: this.options.eventIdleTimeoutMs } : {}),
      }),
      capabilities: undefined,
      disposers: [],
    };
    this.conn = conn;
    conn.disposers.push(
      session.snapshot.subscribe(() => this.onSessionChange(conn)),
      conn.events.on('event', (event) => {
        this.refreshSnapshot(conn);
        this.emit('event', event);
      }),
      conn.events.on('log', (log) => {
        this.emit('stream-log', log);
        if (log.kind === 'backoff') void this.probeServer();
      }),
      conn.events.on('status', () => this.refreshSnapshot(conn)),
    );
    this.refreshSnapshot(conn);

    await session.initialize();
    await this.capabilities(true);
    const state = await this.clientState();
    for (const summary of state.sessions) this.queue.observeRevision(summary.projectSessionId, summary.projectRevision);
    if (this.conn === conn) conn.events.start(state.eventCursor);
  }

  /** 主机重启或需要重新登记后由页面调用：丢弃旧身份，重新取启动信息并连接。 */
  async reinitialize(reason = '重新初始化'): Promise<void> {
    if (this.initializing) return this.initializing;
    this.queue.reset(reason);
    this.emit('reset', { reason });
    await this.initialize();
  }

  /** needsRegistration：在同一 Server 上放弃旧身份，新登记一个 client。 */
  async registerNew(): Promise<void> {
    const conn = this.requireConn();
    this.queue.reset('重新登记');
    this.emit('reset', { reason: '重新登记' });
    await conn.session.registerNew();
    await this.capabilities(true);
    const state = await this.clientState();
    conn.events.stop();
    conn.events.start(state.eventCursor);
  }

  dispose(): void {
    this.disposed = true;
    for (const dispose of this.environmentDisposers) dispose();
    this.teardownConnection();
  }

  // ── 连接与状态 ─────────────────────────────────────────

  async capabilities(refresh = false): Promise<BackendCapabilities> {
    const conn = this.requireConn();
    if (conn.capabilities && !refresh) return conn.capabilities;
    const { data } = await this.send<BackendCapabilities>({ method: 'GET', path: '/api/v1/capabilities', auth: 'access', label: 'capabilities' });
    conn.clock.observe(data.serverTimeUtc);
    conn.capabilities = data;
    this.refreshSnapshot(conn);
    return data;
  }

  /** 音频分析能力（规范 §12.1）：是否可用、音频格式与时长上限。 */
  async analysisCapabilities(): Promise<AnalysisCapabilities> {
    const { data } = await this.send<AnalysisCapabilities>({ method: 'GET', path: '/api/v1/analysis-capabilities', auth: 'access', label: 'analysisCapabilities' });
    return data;
  }

  /** 正式验证能力（规范 §10.1）：是否可用、每种输出最多验证几帧、画布上限。 */
  async validationCapabilities(): Promise<ValidationCapabilities> {
    const { data } = await this.send<ValidationCapabilities>({ method: 'GET', path: '/api/v1/validation-capabilities', auth: 'access', label: 'validationCapabilities' });
    return data;
  }

  /** 导出能力（规范 §11.1）：可用的格式与质量；限制字段省略表示部署没有施加。 */
  async exportCapabilities(): Promise<ExportCapabilities> {
    const { data } = await this.send<ExportCapabilities>({ method: 'GET', path: '/api/v1/export-capabilities', auth: 'access', label: 'exportCapabilities' });
    return data;
  }

  /** 当前 profile 的恢复候选（规范 §8），没有分页。 */
  async recoveryCandidates(): Promise<RecoveryCandidatesResponse> {
    const { data } = await this.send<RecoveryCandidatesResponse>({ method: 'GET', path: '/api/v1/recovery-candidates', auth: 'access', label: 'recoveryCandidates' });
    return data;
  }

  async clientState(): Promise<ClientStateResponse> {
    const { clientId } = this.requireConn().session.identity;
    const { data } = await this.send<ClientStateResponse>({
      method: 'GET',
      path: `/api/v1/clients/${enc(clientId)}/state`,
      auth: 'access',
      label: 'clients.state',
    });
    data.sessions.forEach(checkSummary);
    return data;
  }

  heartbeat(): Promise<void> {
    return this.requireConn().session.heartbeat();
  }

  resume(): Promise<void> {
    return this.requireConn().session.resume();
  }

  /** 调试：断开事件流并立即重连；传入游标可人为制造缺口。 */
  reconnectEvents(cursorOverride?: string): void {
    this.requireConn().events.reconnect(cursorOverride);
  }

  /** 调试：断开事件流并保持断开（保留游标），用于验证断线期间的事件能否用 Last-Event-ID 续接。 */
  disconnectEvents(): void {
    this.requireConn().events.stop();
  }

  /** 从最近游标重新连接事件流。 */
  connectEvents(): void {
    const conn = this.requireConn();
    if (conn.session.state === 'ready') conn.events.start(conn.events.cursor);
  }

  /**
   * 网络层连不上 Server 时核对启动信息。Server 重启后监听端口是随机的，旧地址直接不可达，
   * 永远等不到 401／409；因此重新向可信启动器取 bootstrap，地址或 hostInstanceId 变化即判定主机已重启
   * （后端交接 §3.4：不能只等待 hostRestarted）。启动信息暂时不可用（Server 正在重启）时保持现状。
   */
  probeServer(): Promise<void> {
    const now = Date.now();
    if (this.probing || now - this.lastProbeAt < 3000) return this.probing ?? Promise.resolve();
    this.lastProbeAt = now;
    const conn = this.conn;
    this.probing = (async () => {
      if (!conn || conn.session.state === 'hostRestarted') return;
      let latest: BootstrapInfo;
      try {
        latest = await this.provider.get();
      } catch (error) {
        this.emit('log', `启动信息暂不可用：${describe(error)}`);
        return;
      }
      const knownHost = conn.session.snapshot.get().hostInstanceId;
      if (
        latest.baseUrl !== conn.bootstrap.baseUrl ||
        (latest.hostInstanceId !== undefined && knownHost !== undefined && latest.hostInstanceId !== knownHost)
      ) {
        conn.session.markHostRestarted(`Server 已重启（启动信息已变化：${latest.baseUrl}）`);
      }
    })().finally(() => {
      this.probing = undefined;
    });
    return this.probing;
  }

  get baseUrl(): string | undefined {
    return this.conn?.bootstrap.baseUrl;
  }

  // ── 资源 ──────────────────────────────────────────────

  readonly assets = {
    /**
     * 原始字节上传。文件名放在 ?fileName=（URLSearchParams 编码），中文名不写进请求头：
     * Fetch 的 Headers 只接受 ByteString（后端审查 F3）。回执放在请求头。
     */
    upload: async (
      file: Blob,
      options: { fileName: string; contentType?: string; signal?: AbortSignal },
    ): Promise<AssetResponse> => {
      const conn = this.requireConn();
      const capabilities = conn.capabilities ?? (await this.capabilities());
      const contentType = options.contentType ?? inferContentType(options.fileName, file.type, capabilities.assetMimeTypes);
      preflightUpload(file, options.fileName, contentType, capabilities);
      const receipt = conn.receipts.create('upload');
      const { data } = await this.send<AssetResponse>(
        {
          method: 'POST',
          path: '/api/v1/assets',
          query: { fileName: options.fileName },
          body: file,
          contentType,
          headers: {
            'X-Mako-Request-Id': receipt.requestId,
            'X-Mako-Host-Instance-Id': receipt.hostInstanceId,
            'X-Mako-Valid-Until-Utc': receipt.validUntilUtc,
          },
          auth: 'access',
          timeoutMs: 120_000,
          label: 'assets.upload',
          ...(options.signal ? { signal: options.signal } : {}),
        },
        receipt,
      );
      return data;
    },

    fetchContent: (assetId: string, options?: FetchBinaryOptions): Promise<BinaryResult> =>
      fetchBinary(this.sender, `/api/v1/assets/${enc(assetId)}/content`, options),

    /**
     * 歌词导入规划（规范 §6.12）：识别格式、编码与来源，返回报告和预览行。只读，不改任何工程，没有回执。
     * 解析失败也是 200，原因在 preparationError。
     */
    lyricsImportPlan: async (input: LyricsImportPlanInput, signal?: AbortSignal): Promise<LyricsImportPlan> => {
      const { data } = await this.send<LyricsImportPlan>({
        method: 'POST',
        path: '/api/v1/lyrics-import-plans',
        json: input,
        auth: 'access',
        label: 'assets.lyricsImportPlan',
        ...(signal ? { signal } : {}),
      });
      return data;
    },
  };

  // ── 工程 ──────────────────────────────────────────────

  readonly projects = {
    /** 同步新建，201 返回摘要（不是 202 job）。服务端会做真实音频准备，可能需要数十秒。 */
    create: async (input: CreateProjectSessionInput, signal?: AbortSignal): Promise<ProjectSessionSummary> => {
      assertSafeJsonInteger(input.composition?.outputDurationUs, 'composition.outputDurationUs');
      const receipt = this.requireConn().receipts.create('create');
      const { data } = await this.send<ProjectSessionSummary>(
        {
          method: 'POST',
          path: '/api/v1/project-sessions',
          json: { ...input, metadata: toMetadata(receipt) },
          auth: 'access',
          timeoutMs: 180_000,
          label: 'projects.create',
          ...(signal ? { signal } : {}),
        },
        receipt,
      );
      checkSummary(data);
      this.queue.observeRevision(data.projectSessionId, data.projectRevision);
      return data;
    },

    get: async (projectSessionId: string, signal?: AbortSignal): Promise<ProjectSessionSummary> => {
      const { data } = await this.send<ProjectSessionSummary>({
        method: 'GET',
        path: `/api/v1/project-sessions/${enc(projectSessionId)}`,
        auth: 'access',
        label: 'projects.get',
        ...(signal ? { signal } : {}),
      });
      checkSummary(data);
      this.queue.observeRevision(data.projectSessionId, data.projectRevision);
      return data;
    },

    history: async (projectSessionId: string): Promise<ProjectHistoryResponse> => {
      const { data } = await this.send<ProjectHistoryResponse>({
        method: 'GET',
        path: `/api/v1/project-sessions/${enc(projectSessionId)}/history`,
        auth: 'access',
        label: 'projects.history',
      });
      this.queue.observeRevision(projectSessionId, data.projectRevision);
      return data;
    },

    /** 读取一个来源的全部最小行／元素投影。没有来源列表入口，新建工程的主来源是 primary。 */
    lyrics: async (projectSessionId: string, branchId = 'primary'): Promise<LyricsLinesResponse> => {
      const { data } = await this.send<LyricsLinesResponse>({
        method: 'GET',
        path: `/api/v1/project-sessions/${enc(projectSessionId)}/lyrics/lines`,
        query: { branchId },
        auth: 'access',
        label: 'projects.lyrics',
      });
      this.queue.observeRevision(projectSessionId, data.projectRevision);
      return data;
    },

    // ── W1 读模型（合同 1.1.0，规范 §6.4） ──
    // 三个读取都必须带 expectedRevision；修订过期 409 revisionConflict，调用方从 summary 重来。

    /** 来源列表的一页。 */
    branches: async (
      projectSessionId: string,
      expectedRevision: Revision,
      page: { offset?: number; limit?: number } = {},
    ): Promise<LyricBranchesPage> => {
      const { data } = await this.send<LyricBranchesPage>({
        method: 'GET',
        path: `/api/v1/project-sessions/${enc(projectSessionId)}/lyrics/branches`,
        query: { expectedRevision, ...pageQuery(page) },
        auth: 'access',
        label: 'projects.branches',
      });
      this.queue.observeRevision(projectSessionId, data.projectRevision);
      return data;
    },

    /** 一个来源的一页可编辑行（每页最多 100 行）。 */
    lyricWindow: async (
      projectSessionId: string,
      branchId: string,
      expectedRevision: Revision,
      page: { offset?: number; limit?: number } = {},
    ): Promise<LyricWindowPage> => {
      const { data } = await this.send<LyricWindowPage>({
        method: 'GET',
        path: `/api/v1/project-sessions/${enc(projectSessionId)}/lyrics/window`,
        query: { branchId, expectedRevision, ...pageQuery(page) },
        auth: 'access',
        label: 'projects.lyricWindow',
      });
      this.queue.observeRevision(projectSessionId, data.projectRevision);
      return data;
    },

    /**
     * 同一修订下沿 nextOffset 读完一个来源的全部行。
     * 413 resourceTooLarge 时把 limit 减半重试；减到 1 仍超预算（单行过大）就抛出。
     */
    allLines: async (projectSessionId: string, branchId: string, expectedRevision: Revision): Promise<EditableLyricLine[]> => {
      const lines: EditableLyricLine[] = [];
      let offset = 0;
      let limit = LYRIC_WINDOW_MAX_LIMIT;
      for (;;) {
        let page: LyricWindowPage;
        try {
          page = await this.projects.lyricWindow(projectSessionId, branchId, expectedRevision, { offset, limit });
        } catch (error) {
          if (error instanceof BackendError && error.code === 'resourceTooLarge' && limit > 1) {
            limit = Math.max(1, Math.floor(limit / 2));
            continue;
          }
          throw error;
        }
        if (page.projectRevision !== expectedRevision) {
          throw new Error(`窗口修订 ${page.projectRevision} 与请求的 ${expectedRevision} 不一致。`);
        }
        lines.push(...page.lines);
        if (page.nextOffset === undefined || page.lines.length === 0) return lines;
        offset = page.nextOffset;
      }
    },

    /** 一个目标的四层静态样式（只读）；不含模板分配，不是最终帧外观。 */
    style: async (projectSessionId: string, target: StyleTarget, expectedRevision: Revision): Promise<ProjectStyleResponse> => {
      const { data } = await this.send<ProjectStyleResponse>({
        method: 'GET',
        path: `/api/v1/project-sessions/${enc(projectSessionId)}/style`,
        query: { expectedRevision, ...target },
        auth: 'access',
        label: 'projects.style',
      });
      this.queue.observeRevision(projectSessionId, data.projectRevision);
      return data;
    },

    /** 工程里的全部模板分配（G15）：失效目标也照常返回；不含库里是否有新版本的判断。 */
    presetApplications: async (projectSessionId: string, expectedRevision: Revision): Promise<ProjectPresetApplicationsResponse> => {
      const { data } = await this.send<ProjectPresetApplicationsResponse>({
        method: 'GET',
        path: `/api/v1/project-sessions/${enc(projectSessionId)}/preset-applications`,
        query: { expectedRevision },
        auth: 'access',
        label: 'projects.presetApplications',
      });
      this.queue.observeRevision(projectSessionId, data.projectRevision);
      return data;
    },

    /**
     * 一行或一个文字的最终静态样式与 21 个属性的来源（规范 §17.6）：叠加各层覆盖与模板分配，
     * 不运行动画、脚本和字体测量。clipId／nodeId 只核对归属。
     */
    resolvedStyle: async (projectSessionId: string, expectedRevision: Revision, query: ResolvedStyleQuery): Promise<ProjectResolvedStyleResponse> => {
      const { data } = await this.send<ProjectResolvedStyleResponse>({
        method: 'GET',
        path: `/api/v1/project-sessions/${enc(projectSessionId)}/resolved-style`,
        query: { expectedRevision, ...query },
        auth: 'access',
        label: 'projects.resolvedStyle',
      });
      this.queue.observeRevision(projectSessionId, data.projectRevision);
      return data;
    },

    /** 编排结构、每个来源的默认容器、片段与前奏：不含画面几何与最终外观。 */
    orchestration: async (projectSessionId: string, expectedRevision: Revision): Promise<ProjectOrchestrationResponse> => {
      const { data } = await this.send<ProjectOrchestrationResponse>({
        method: 'GET',
        path: `/api/v1/project-sessions/${enc(projectSessionId)}/orchestration`,
        query: { expectedRevision },
        auth: 'access',
        label: 'projects.orchestration',
      });
      this.queue.observeRevision(projectSessionId, data.projectRevision);
      return data;
    },

    /** 单节点的固定容器参考几何（G22-A）：画布坐标的四角与世界矩阵；不含文字测量与动画，不是最终像素。 */
    geometryReference: async (projectSessionId: string, expectedRevision: Revision, nodeId: string): Promise<ProjectGeometryReferenceResponse> => {
      const { data } = await this.send<ProjectGeometryReferenceResponse>({
        method: 'GET',
        path: `/api/v1/project-sessions/${enc(projectSessionId)}/orchestration/geometry`,
        query: { expectedRevision, nodeId },
        auth: 'access',
        label: 'projects.geometryReference',
      });
      this.queue.observeRevision(projectSessionId, data.projectRevision);
      return data;
    },

    /** 单句局部变换（G24）：transform 为 null 表示没有覆写。 */
    lineLocalTransform: async (
      projectSessionId: string,
      expectedRevision: Revision,
      branchId: string,
      lineId: string,
    ): Promise<ProjectLineLocalTransformResponse> => {
      const { data } = await this.send<ProjectLineLocalTransformResponse>({
        method: 'GET',
        path: `/api/v1/project-sessions/${enc(projectSessionId)}/orchestration/line-local-transform`,
        query: { expectedRevision, branchId, lineId },
        auth: 'access',
        label: 'projects.lineLocalTransform',
      });
      this.queue.observeRevision(projectSessionId, data.projectRevision);
      return data;
    },

    /** 单句展示（1.24.0）：本句的局部变换与进入动画覆盖、实际生效的进入动画和它的来源（本句、工程或没有）。 */
    linePresentation: async (
      projectSessionId: string,
      expectedRevision: Revision,
      branchId: string,
      lineId: string,
    ): Promise<ProjectLinePresentationResponse> => {
      const { data } = await this.send<ProjectLinePresentationResponse>({
        method: 'GET',
        path: `/api/v1/project-sessions/${enc(projectSessionId)}/orchestration/line-presentation`,
        query: { expectedRevision, branchId, lineId },
        auth: 'access',
        label: 'projects.linePresentation',
      });
      this.queue.observeRevision(projectSessionId, data.projectRevision);
      return data;
    },

    /**
     * 真实 Worker 同帧测量（1.24.0）：同一次采样的元素几何与 PNG。只读，没有 metadata、回执与历史；
     * 后端全局并发 2、每个客户端 1，单次最长 20 秒。
     */
    measureScene: async (projectSessionId: string, request: SceneMeasurementRequest): Promise<SceneMeasurementResponse> => {
      const { data } = await this.send<SceneMeasurementResponse>({
        method: 'POST',
        path: `/api/v1/project-sessions/${enc(projectSessionId)}/orchestration/measurements`,
        json: request,
        auth: 'access',
        label: 'projects.measureScene',
      });
      this.queue.observeRevision(projectSessionId, data.projectRevision);
      return data;
    },

    settings: async (projectSessionId: string, expectedRevision: Revision): Promise<ProjectSettings> => {
      const { data } = await this.send<ProjectSettings>({
        method: 'GET',
        path: `/api/v1/project-sessions/${enc(projectSessionId)}/settings`,
        query: { expectedRevision },
        auth: 'access',
        label: 'projects.settings',
      });
      this.queue.observeRevision(projectSessionId, data.projectRevision);
      return data;
    },

    /** 工程音频的波形元数据（G11）。不带修订，与歌词修订无关；不可用时 available:false 并给出原因。 */
    waveform: async (projectSessionId: string, signal?: AbortSignal): Promise<ProjectWaveform> => {
      const { data } = await this.send<ProjectWaveform>({
        method: 'GET',
        path: `/api/v1/project-sessions/${enc(projectSessionId)}/waveform`,
        auth: 'access',
        label: 'projects.waveform',
        ...(signal ? { signal } : {}),
      });
      return data;
    },

    /** 一段峰值窗口：返回 count 对 (min, max)，交错存放。waveformId 过期时 404 resourceNotFound。 */
    waveformPeaks: async (projectSessionId: string, window: WaveformWindow, signal?: AbortSignal): Promise<Float32Array> => {
      if (!Number.isInteger(window.count) || window.count < 1 || window.count > WAVEFORM_WINDOW_MAX_BUCKETS) {
        throw new RangeError(`峰值窗口 count 必须在 1..${WAVEFORM_WINDOW_MAX_BUCKETS}：${window.count}`);
      }
      const { data } = await this.send<Blob | null>({
        method: 'GET',
        path: `/api/v1/project-sessions/${enc(projectSessionId)}/waveform/peaks`,
        query: { waveformId: window.waveformId, level: String(window.level), start: String(window.start), count: String(window.count) },
        auth: 'access',
        response: 'blob',
        label: 'projects.waveformPeaks',
        ...(signal ? { signal } : {}),
      });
      const values = decodePeakWindow(await data!.arrayBuffer());
      if (values.length !== window.count * 2) throw new Error(`峰值窗口应有 ${window.count} 个桶，实际 ${values.length / 2} 个`);
      return values;
    },

    /** 经工程命令队列串行发出，expectedRevision 由队列填写。 */
    command: (projectSessionId: string, command: ProjectCommand): Promise<ProjectCommandResponse> =>
      this.queue.enqueue(
        projectSessionId,
        async (expectedRevision) => {
          const receipt = this.requireConn().receipts.create('cmd');
          const body: ProjectCommandRequest = { expectedRevision, command, ...toMetadata(receipt) };
          const timeoutMs = SLOW_COMMAND_TIMEOUT_MS[command.type];
          const { data } = await this.send<ProjectCommandResponse>(
            {
              method: 'POST',
              path: `/api/v1/project-sessions/${enc(projectSessionId)}/commands`,
              json: body,
              auth: 'access',
              label: `projects.command:${command.type}`,
              ...(timeoutMs ? { timeoutMs } : {}),
            },
            receipt,
          );
          return data;
        },
        (response) => (response.changed ? response.projectRevision : undefined),
      ),

    setTimeRange: (projectSessionId: string, target: LyricTimingTarget, startUs: Micros, endUs: Micros) =>
      this.projects.command(projectSessionId, { type: 'lyrics.setTimeRange', target, startUs, endUs }),

    undo: (projectSessionId: string) => this.projects.command(projectSessionId, { type: 'history.undo' }),

    redo: (projectSessionId: string) => this.projects.command(projectSessionId, { type: 'history.redo' }),

    /** 202：只表示已接受；用 jobs.waitForTerminal 等待结果。 */
    save: (projectSessionId: string, input: SaveProjectInput = {}): Promise<JobResponse> =>
      this.queue.enqueue(projectSessionId, (expectedRevision) =>
        this.postJob(`/api/v1/project-sessions/${enc(projectSessionId)}/save-jobs`, 'save', {
          expectedRevision,
          ...input,
        }),
      ),

    /** 202：关闭成功终态后会话才移除；保存→关闭→重开须分别等前一 job 成功。 */
    close: (projectSessionId: string, input: CloseProjectInput = {}): Promise<JobResponse> =>
      this.queue.enqueue(projectSessionId, (expectedRevision) =>
        this.postJob(`/api/v1/project-sessions/${enc(projectSessionId)}/close`, 'close', {
          expectedRevision,
          ...input,
        }),
      ),

    open: (input: OpenProjectInput): Promise<JobResponse> => this.postJob('/api/v1/project-sessions/open-jobs', 'open', { ...input }),

    /** 202：从恢复候选恢复一个工程（规范 §7）；成功终态的 result 是新会话的摘要。 */
    recover: (input: RecoverProjectInput): Promise<JobResponse> => this.postJob('/api/v1/project-sessions/recover-jobs', 'recover', { ...input }),

    /** 资源清单的一页（规范 §17.2）：精确修订，每页 1～100。 */
    resources: async (
      projectSessionId: string,
      expectedRevision: Revision,
      page: { offset?: number; limit?: number } = {},
    ): Promise<ProjectResourcesResponse> => {
      const { data } = await this.send<ProjectResourcesResponse>({
        method: 'GET',
        path: `/api/v1/project-sessions/${enc(projectSessionId)}/resources`,
        query: { expectedRevision, offset: String(page.offset ?? 0), limit: String(page.limit ?? 100) },
        auth: 'access',
        label: 'projects.resources',
      });
      this.queue.observeRevision(projectSessionId, data.projectRevision);
      return data;
    },

    /** 同一修订下的全部资源；不拼接不同修订（修订变了由服务端 409）。 */
    resourcesAll: async (projectSessionId: string, expectedRevision: Revision): Promise<ProjectResource[]> => {
      const all: ProjectResource[] = [];
      for (let offset: number | undefined = 0; offset !== undefined; ) {
        const page = await this.projects.resources(projectSessionId, expectedRevision, { offset, limit: 100 });
        all.push(...page.resources);
        offset = page.nextOffset !== undefined && page.nextOffset > offset ? page.nextOffset : undefined;
      }
      return all;
    },

    /** 歌词导出评估（规范 §17.3）：201 返回固定快照上的计划与损失；不推进工程历史。 */
    lyricsExportPlan: async (projectSessionId: string, expectedRevision: Revision, input: LyricsExportPlanInput): Promise<LyricsExportPlan> => {
      const receipt = this.requireConn().receipts.create('lyrics-plan');
      const { data } = await this.send<LyricsExportPlan>(
        {
          method: 'POST',
          path: `/api/v1/project-sessions/${enc(projectSessionId)}/lyrics-export-plans`,
          json: { expectedRevision, ...input, metadata: toMetadata(receipt) },
          auth: 'access',
          label: 'projects.lyricsExportPlan',
        },
        receipt,
      );
      return data;
    },

    /** 按计划导出歌词文件：修订必须与计划一致；有损失时必须 acceptLosses。 */
    lyricsExport: async (projectSessionId: string, expectedRevision: Revision, input: LyricsExportInput): Promise<LyricsExportResponse> => {
      const receipt = this.requireConn().receipts.create('lyrics-export');
      const { data } = await this.send<LyricsExportResponse>(
        {
          method: 'POST',
          path: `/api/v1/project-sessions/${enc(projectSessionId)}/lyrics-exports`,
          json: { expectedRevision, ...input, metadata: toMetadata(receipt) },
          auth: 'access',
          label: 'projects.lyricsExport',
        },
        receipt,
      );
      return data;
    },

    /** 202：正式验证工程当前修订（规范 §10.2）。之后的编辑不改变任务的输入。 */
    validate: (projectSessionId: string, input: ValidationJobInput): Promise<JobResponse> =>
      this.queue.enqueue(projectSessionId, (expectedRevision) =>
        this.postJob(`/api/v1/project-sessions/${enc(projectSessionId)}/validation-jobs`, 'validation', { expectedRevision, ...input }),
      ),

    /** 202：导出工程当前修订（规范 §11.2）。要求显式正输出时长。 */
    exportVideo: (projectSessionId: string, input: ExportJobInput): Promise<JobResponse> =>
      this.queue.enqueue(projectSessionId, (expectedRevision) =>
        this.postJob(`/api/v1/project-sessions/${enc(projectSessionId)}/export-jobs`, 'export', { expectedRevision, ...input }),
      ),

    /**
     * 202：离线分析工程当前的音频（规范 §12.2）。分析本身不改工程；成功后用 audio.analysis.apply 显式采用。
     * 走命令队列，expectedRevision 是发出时的精确修订。
     */
    analyze: (projectSessionId: string): Promise<JobResponse> =>
      this.queue.enqueue(projectSessionId, (expectedRevision) =>
        this.postJob(`/api/v1/project-sessions/${enc(projectSessionId)}/analysis-jobs`, 'analysis', { expectedRevision }),
      ),

    knownRevision: (projectSessionId: string): Revision | undefined => this.queue.knownRevision(projectSessionId),
  };

  // ── 目录授权 ───────────────────────────────────────────

  readonly files = {
    roots: async (): Promise<FileRootsResponse> =>
      (await this.send<FileRootsResponse>({ method: 'GET', path: '/api/v1/file-roots', auth: 'access', label: 'files.roots' })).data,

    /** 授权根下一层目录的一页（规范 §17.1）。relativePath 为空表示根。 */
    entries: async (
      rootGrantId: string,
      relativePath: string,
      page: { offset?: number; limit?: number } = {},
      signal?: AbortSignal,
    ): Promise<ProjectDirectoryEntriesResponse> => {
      const { data } = await this.send<ProjectDirectoryEntriesResponse>({
        method: 'GET',
        path: `/api/v1/file-roots/${enc(rootGrantId)}/entries`,
        query: { relativePath, offset: String(page.offset ?? 0), limit: String(page.limit ?? DIRECTORY_PAGE_MAX) },
        auth: 'access',
        label: 'files.entries',
        ...(signal ? { signal } : {}),
      });
      return data;
    },

    /**
     * 一层目录的全部条目。分页不是快照：翻页途中 totalCount 变了（目录被改动）就从第一页重读，最多重读两次，
     * 仍不一致时返回最后一次读到的完整结果。
     */
    listAll: async (rootGrantId: string, relativePath: string, signal?: AbortSignal): Promise<ProjectDirectoryEntry[]> => {
      for (let attempt = 0; ; attempt++) {
        const first = await this.files.entries(rootGrantId, relativePath, { offset: 0 }, signal);
        const all = [...first.entries];
        let consistent = true;
        let next = first.nextOffset;
        while (next !== undefined && next > 0 && all.length < first.totalCount) {
          const page = await this.files.entries(rootGrantId, relativePath, { offset: next }, signal);
          if (page.totalCount !== first.totalCount) {
            consistent = false;
            break;
          }
          all.push(...page.entries);
          next = page.nextOffset !== undefined && page.nextOffset > next ? page.nextOffset : undefined;
        }
        if (consistent || attempt >= 2) return all;
      }
    },

    createHandle: async (input: FileHandleInput): Promise<FileHandleResponse> => {
      const receipt = this.requireConn().receipts.create('handle');
      const { data } = await this.send<FileHandleResponse>(
        {
          method: 'POST',
          path: '/api/v1/file-handles',
          json: { ...input, metadata: toMetadata(receipt) },
          auth: 'access',
          label: 'files.createHandle',
        },
        receipt,
      );
      return data;
    },
  };

  // ── 任务 ──────────────────────────────────────────────

  readonly jobs = {
    get: async (jobId: string, signal?: AbortSignal): Promise<JobResponse> => {
      const { data } = await this.send<JobResponse>({
        method: 'GET',
        path: `/api/v1/jobs/${enc(jobId)}`,
        auth: 'access',
        label: 'jobs.get',
        ...(signal ? { signal } : {}),
      });
      if (data.result) checkSummary(data.result);
      return data;
    },

    /** 返回值只代表“已请求取消”，仍需等待终态。cancel 正文直接就是回执三元组。 */
    cancel: async (jobId: string): Promise<JobResponse> => {
      const receipt = this.requireConn().receipts.create('cancel');
      const { data } = await this.send<JobResponse>(
        { method: 'POST', path: `/api/v1/jobs/${enc(jobId)}/cancel`, json: toMetadata(receipt), auth: 'access', label: 'jobs.cancel' },
        receipt,
      );
      return data;
    },

    /** 正式验证的完整报告（原始 JSON）。没有登记报告时 404 jobNotFound。 */
    validationResult: async (jobId: string): Promise<ValidationReport> => {
      const { data } = await this.send<ValidationReport>({ method: 'GET', path: `/api/v1/jobs/${enc(jobId)}/validation-result`, auth: 'access', label: 'jobs.validationResult' });
      return data;
    },

    validationDiagnostics: async (jobId: string, cursor?: string): Promise<JobDiagnosticsPage<ValidationDiagnostic>> => {
      const { data } = await this.send<JobDiagnosticsPage<ValidationDiagnostic>>({
        method: 'GET',
        path: `/api/v1/jobs/${enc(jobId)}/validation-diagnostics`,
        ...(cursor ? { query: { cursor } } : {}),
        auth: 'access',
        label: 'jobs.validationDiagnostics',
      });
      return data;
    },

    /** 导出报告：成功报告或 kind=exportFailure 的失败报告；200 不代表导出成功。 */
    exportResult: async (jobId: string): Promise<ExportResultReport> => {
      const { data } = await this.send<ExportResultReport>({ method: 'GET', path: `/api/v1/jobs/${enc(jobId)}/export-result`, auth: 'access', label: 'jobs.exportResult' });
      return data;
    },

    exportDiagnostics: async (jobId: string, cursor?: string): Promise<JobDiagnosticsPage<BackendDiagnostic>> => {
      const { data } = await this.send<JobDiagnosticsPage<BackendDiagnostic>>({
        method: 'GET',
        path: `/api/v1/jobs/${enc(jobId)}/export-diagnostics`,
        ...(cursor ? { query: { cursor } } : {}),
        auth: 'access',
        label: 'jobs.exportDiagnostics',
      });
      return data;
    },

    /** 导出成片（认证读取，成片可能很大，超时放宽到 10 分钟）。 */
    exportContent: (jobId: string, options?: FetchBinaryOptions): Promise<BinaryResult> =>
      fetchBinary(this.sender, `/api/v1/jobs/${enc(jobId)}/export-content`, { timeoutMs: 600_000, ...options }),

    waitForTerminal: async (jobId: string, options?: WaitForJobOptions): Promise<JobResponse> => {
      const job = await this.jobWatcher.waitForTerminal(jobId, options);
      if (job.state === 'succeeded' && job.projectSessionId) {
        if (job.kind === 'close') this.queue.forget(job.projectSessionId);
        else if (job.result) this.queue.observeRevision(job.result.projectSessionId, job.result.projectRevision);
      }
      return job;
    },
  };

  /** 歌词导出的产物（规范 §17.3）：认证读取，保留 300 秒。 */
  readonly lyricsExports = {
    content: (exportId: string, options?: FetchBinaryOptions): Promise<BinaryResult> =>
      fetchBinary(this.sender, `/api/v1/lyrics-exports/${enc(exportId)}/content`, options),
  };

  // ── 预览 ──────────────────────────────────────────────

  /** 模板库（规范 §13）：与工程无关；库修订做 CAS，个人库没有入口时修订为 'missing'。 */
  readonly templates = {
    libraries: async (): Promise<TemplateLibrariesResponse> =>
      (await this.send<TemplateLibrariesResponse>({ method: 'GET', path: '/api/v1/template-libraries', auth: 'access', label: 'templates.libraries' })).data,

    /** 一页摘要。首次读取可省略 expectedRevision；翻页时必须带首次返回的修订。limit 1..100。 */
    list: async (
      libraryId: TemplateLibraryId,
      options: { offset?: number; limit?: number; expectedRevision?: string } = {},
    ): Promise<TemplateListResponse> => {
      const query: Record<string, string> = {};
      if (options.offset !== undefined) query.offset = String(options.offset);
      if (options.limit !== undefined) query.limit = String(options.limit);
      if (options.expectedRevision !== undefined) query.expectedRevision = options.expectedRevision;
      const { data } = await this.send<TemplateListResponse>({
        method: 'GET',
        path: `/api/v1/template-libraries/${enc(libraryId)}/templates`,
        query,
        auth: 'access',
        label: 'templates.list',
      });
      return data;
    },

    /** 同一修订下读完全部摘要；翻页途中库被修改（409）就从头再读，最多 3 次。 */
    listAll: async (libraryId: TemplateLibraryId): Promise<{ revision: string; templates: TemplateSummary[] }> => {
      for (let attempt = 1; ; attempt++) {
        try {
          const first = await this.templates.list(libraryId, { limit: TEMPLATE_LIST_MAX_LIMIT });
          const templates = [...first.templates];
          let next = first.nextOffset;
          while (next !== undefined) {
            const page = await this.templates.list(libraryId, { offset: next, limit: TEMPLATE_LIST_MAX_LIMIT, expectedRevision: first.revision });
            templates.push(...page.templates);
            next = page.templates.length > 0 ? page.nextOffset : undefined;
          }
          return { revision: first.revision, templates };
        } catch (error) {
          if (attempt < 3 && error instanceof BackendError && error.code === 'revisionConflict') continue;
          throw error;
        }
      }
    },

    detail: async (libraryId: TemplateLibraryId, templateId: string, expectedRevision: string): Promise<TemplateDetailResponse> =>
      (
        await this.send<TemplateDetailResponse>({
          method: 'GET',
          path: `/api/v1/template-libraries/${enc(libraryId)}/templates/detail`,
          query: { expectedRevision, templateId },
          auth: 'access',
          label: 'templates.detail',
        })
      ).data,

    /** 完整定义（规范 §24.1）：任何模板都给出定义；包是绑定这个库修订的 library 引用，库一改就要重读。 */
    fullDetail: async (libraryId: TemplateLibraryId, templateId: string, expectedRevision: string): Promise<TemplateFullDetailResponse> =>
      (
        await this.send<TemplateFullDetailResponse>({
          method: 'GET',
          path: `/api/v1/template-libraries/${enc(libraryId)}/templates/detail`,
          query: { expectedRevision, templateId, definitionFormat: 'full' },
          auth: 'access',
          label: 'templates.fullDetail',
        })
      ).data,

    /** 只对 personal。409 表示库已被修改：重读后再决定是否重发，不要盲目重放。 */
    command: async (libraryId: TemplateLibraryId, expectedRevision: string, command: TemplateLibraryCommand): Promise<TemplateLibraryCommandResponse> => {
      const receipt = this.requireConn().receipts.create('template');
      const { data } = await this.send<TemplateLibraryCommandResponse>(
        {
          method: 'POST',
          path: `/api/v1/template-libraries/${enc(libraryId)}/commands`,
          json: { expectedRevision, metadata: toMetadata(receipt), command },
          auth: 'access',
          label: `templates.command:${command.type}`,
        },
        receipt,
      );
      return data;
    },

    /** 工坊样本与能用的目标、三个材质槽（规范 §24.3）。只读，不建会话。 */
    previewOptions: async (source: TemplatePreviewSource, signal?: AbortSignal): Promise<TemplatePreviewOptionsResponse> =>
      (
        await this.send<TemplatePreviewOptionsResponse>({
          method: 'POST',
          path: '/api/v1/template-preview-options',
          json: { source },
          auth: 'access',
          label: 'templates.previewOptions',
          ...(signal ? { signal } : {}),
        })
      ).data,

    /** 把选中的模板打成 .mktpl（规范 §17.5）：固定库快照，之后库再改也不影响归档字节。templateIds 1..100。 */
    exportArchive: async (libraryId: TemplateLibraryId, expectedRevision: string, templateIds: string[]): Promise<TemplateArchiveExportResponse> => {
      const receipt = this.requireConn().receipts.create('template-transfer');
      const { data } = await this.send<TemplateArchiveExportResponse>(
        {
          method: 'POST',
          path: `/api/v1/template-libraries/${enc(libraryId)}/exports`,
          json: { expectedRevision, templateIds, metadata: toMetadata(receipt) },
          auth: 'access',
          label: 'templates.exportArchive',
        },
        receipt,
      );
      return data;
    },

    /** 读导出的归档（保留 300 秒）。 */
    archive: (exportId: string, options?: FetchBinaryOptions): Promise<BinaryResult> =>
      fetchBinary(this.sender, `/api/v1/template-transfers/${enc(exportId)}/archive`, options),

    /** 预检上传的 .mktpl：列出里面的模板，不写库。preflightId 只能用一次。 */
    preflightImport: async (libraryId: TemplateLibraryId, assetId: string): Promise<TemplateImportPreflightResponse> => {
      const receipt = this.requireConn().receipts.create('template-transfer');
      const { data } = await this.send<TemplateImportPreflightResponse>(
        {
          method: 'POST',
          path: `/api/v1/template-libraries/${enc(libraryId)}/imports/preflight`,
          json: { assetId, metadata: toMetadata(receipt) },
          auth: 'access',
          label: 'templates.preflightImport',
        },
        receipt,
      );
      return data;
    },

    /** 凭预检导入全部模板，一律生成新 ID；一次写库，失败不留下部分模板。 */
    importArchive: async (libraryId: TemplateLibraryId, expectedRevision: string, preflightId: string): Promise<TemplateImportResponse> => {
      const receipt = this.requireConn().receipts.create('template-transfer');
      const { data } = await this.send<TemplateImportResponse>(
        {
          method: 'POST',
          path: `/api/v1/template-libraries/${enc(libraryId)}/imports`,
          json: { expectedRevision, preflightId, metadata: toMetadata(receipt) },
          auth: 'access',
          label: 'templates.importArchive',
        },
        receipt,
      );
      return data;
    },
  };

  /** 效果包（规范 §24.2）：只读检查包结构与参数，不运行脚本。 */
  readonly effectPackages = {
    inspect: async (input: EffectPackageInspectInput, signal?: AbortSignal): Promise<EffectPackageInspectResponse> =>
      (
        await this.send<EffectPackageInspectResponse>({
          method: 'POST',
          path: '/api/v1/effect-packages/inspect',
          json: input,
          auth: 'access',
          label: 'effectPackages.inspect',
          ...(signal ? { signal } : {}),
        })
      ).data,
  };

  /**
   * 作者草稿（规范 §21）：只属于当前 client，换 client 或 Server 重启后读不到（G33）。
   * 有副作用的请求都带精确草稿修订；修订过期是 409，重读草稿后由调用方决定是否重发。
   */
  readonly drafts = {
    /** 201。每个 client 最多 16 个草稿（429 requestQuotaExceeded）。 */
    create: async (input: AuthorDraftCreateInput): Promise<AuthorDraft> => {
      const receipt = this.requireConn().receipts.create('draft');
      const { data } = await this.send<AuthorDraft>(
        { method: 'POST', path: '/api/v1/drafts', json: { ...input, metadata: toMetadata(receipt) }, auth: 'access', label: 'drafts.create' },
        receipt,
      );
      return data;
    },

    list: async (): Promise<AuthorDraftListResponse> =>
      (await this.send<AuthorDraftListResponse>({ method: 'GET', path: '/api/v1/drafts', auth: 'access', label: 'drafts.list' })).data,

    get: async (draftId: string, signal?: AbortSignal): Promise<AuthorDraft> =>
      (
        await this.send<AuthorDraft>({
          method: 'GET',
          path: `/api/v1/drafts/${enc(draftId)}`,
          auth: 'access',
          label: 'drafts.get',
          ...(signal ? { signal } : {}),
        })
      ).data,

    /** 草稿保留的某个源码修订的 C# 文件（两个修订都必填）；草稿修订过期 409，修订不存在或包里没有 C# 文件 404。 */
    sources: async (draftId: string, expectedDraftRevision: string, sourceRevision: string): Promise<AuthorDraftSourcesResponse> =>
      (
        await this.send<AuthorDraftSourcesResponse>({
          method: 'GET',
          path: `/api/v1/drafts/${enc(draftId)}/sources`,
          query: { expectedDraftRevision, sourceRevision },
          auth: 'access',
          label: 'drafts.sources',
        })
      ).data,

    /** 精确草稿修订下的完整 manifest、文本与只读二进制描述。 */
    packageFiles: async (draftId: string, expectedDraftRevision: string, sourceRevision: string): Promise<AuthorPackageFilesResponse> =>
      (await this.send<AuthorPackageFilesResponse>({
        method: 'GET', path: `/api/v1/drafts/${enc(draftId)}/package-files`,
        query: { expectedDraftRevision, sourceRevision }, auth: 'access', label: 'drafts.packageFiles',
      })).data,

    /** 原子保存完整包文本；SDK2 不自动采用或改变在用来源，服务重算文本摘要。 */
    savePackageFiles: async (draftId: string, expectedDraftRevision: string, input: AuthorPackageFilesInput): Promise<AuthorDraft> => {
      const receipt = this.requireConn().receipts.create('draft-package-files');
      const { data } = await this.send<AuthorDraft>({
        method: 'PUT', path: `/api/v1/drafts/${enc(draftId)}/package-files`,
        json: { expectedDraftRevision, ...input, metadata: toMetadata(receipt) },
        auth: 'access', label: 'drafts.savePackageFiles',
      }, receipt);
      return data;
    },

    /** 提交完整配置；内容相同是 no-op（草稿修订不变）。 */
    setConfiguration: async (draftId: string, expectedDraftRevision: string, configuration: AuthorDraftConfigurationInput): Promise<AuthorDraft> => {
      const receipt = this.requireConn().receipts.create('draft-configuration');
      const { data } = await this.send<AuthorDraft>(
        {
          method: 'PUT',
          path: `/api/v1/drafts/${enc(draftId)}/configuration`,
          json: { expectedDraftRevision, configuration, metadata: toMetadata(receipt) },
          auth: 'access',
          label: 'drafts.setConfiguration',
        },
        receipt,
      );
      return data;
    },

    /** 保存一个源码修订：必须是现有的全部且仅全部 C# 文件（G34）。保存后在用修订换成新修订。 */
    saveSources: async (draftId: string, expectedDraftRevision: string, files: AuthorSourceFile[]): Promise<AuthorSourceRevisionResponse> => {
      const receipt = this.requireConn().receipts.create('draft-sources');
      const { data } = await this.send<AuthorSourceRevisionResponse>(
        {
          method: 'POST',
          path: `/api/v1/drafts/${enc(draftId)}/source-revision-jobs`,
          json: { expectedDraftRevision, files, metadata: toMetadata(receipt) },
          auth: 'access',
          label: 'drafts.saveSources',
        },
        receipt,
      );
      return data;
    },

    /** 202：异步编译固定修订；省略 sourceRevision 取最新，结果用 compilationResult 读。 */
    compile: (draftId: string, expectedDraftRevision: string, sourceRevision?: string): Promise<JobResponse> =>
      this.postJob(`/api/v1/drafts/${enc(draftId)}/compilation-jobs`, 'author-compilation', { expectedDraftRevision, executionMode: 'async', ...(sourceRevision ? { sourceRevision } : {}) }),

    /** 最近一次编译；没编译过时 404。 */
    compilation: async (draftId: string): Promise<AuthorCompilation> =>
      (await this.send<AuthorCompilation>({ method: 'GET', path: `/api/v1/drafts/${enc(draftId)}/compilation`, auth: 'access', label: 'drafts.compilation' })).data,

    compilationResult: async (jobId: string): Promise<AuthorCompilation> =>
      (await this.send<AuthorCompilation>({ method: 'GET', path: `/api/v1/jobs/${enc(jobId)}/compilation-result`, auth: 'access', label: 'drafts.compilationResult' })).data,

    /** 202：作者验证，任务 kind 是 validation；报告与诊断用 jobs.validationResult／validationDiagnostics 读。 */
    validate: (draftId: string, expectedDraftRevision: string, input: AuthorValidationInput): Promise<JobResponse> =>
      this.postJob(`/api/v1/drafts/${enc(draftId)}/validation-jobs`, 'author-validation', { expectedDraftRevision, ...input }),

    /** 改用一个保留的修订；SDK2 C# 修订必须已有成功编译记录。 */
    adopt: async (draftId: string, expectedDraftRevision: string, sourceRevision: string): Promise<AuthorAdoptResponse> => {
      const receipt = this.requireConn().receipts.create('draft-adopt');
      const { data } = await this.send<AuthorAdoptResponse>(
        {
          method: 'POST',
          path: `/api/v1/drafts/${enc(draftId)}/adopt-source-revision`,
          json: { expectedDraftRevision, sourceRevision, metadata: toMetadata(receipt) },
          auth: 'access',
          label: 'drafts.adopt',
        },
        receipt,
      );
      return data;
    },

    delete: async (draftId: string, expectedDraftRevision: string): Promise<AuthorDraftDeleteResponse> => {
      const receipt = this.requireConn().receipts.create('draft-delete');
      const { data } = await this.send<AuthorDraftDeleteResponse>(
        {
          method: 'DELETE',
          path: `/api/v1/drafts/${enc(draftId)}`,
          json: { expectedDraftRevision, metadata: toMetadata(receipt) },
          auth: 'access',
          label: 'drafts.delete',
        },
        receipt,
      );
      return data;
    },
  };

  /** 个人配置库（界面叫效果组件库，规范 §21）：属于 profile；命令带库修订做 CAS，409 时重读再决定。 */
  readonly scriptConfigurations = {
    list: async (): Promise<ScriptConfigurationListResponse> =>
      (await this.send<ScriptConfigurationListResponse>({ method: 'GET', path: '/api/v1/script-configurations', auth: 'access', label: 'scriptConfigurations.list' })).data,

    /** 要带精确库修订（过期 409）。 */
    detail: async (configurationId: string, expectedRevision: string): Promise<ScriptConfigurationDetailResponse> =>
      (
        await this.send<ScriptConfigurationDetailResponse>({
          method: 'GET',
          path: `/api/v1/script-configurations/${enc(configurationId)}`,
          query: { expectedRevision },
          auth: 'access',
          label: 'scriptConfigurations.detail',
        })
      ).data,

    command: async (expectedRevision: string, command: ScriptConfigurationCommand): Promise<ScriptConfigurationCommandResponse> => {
      const receipt = this.requireConn().receipts.create('script-configuration');
      const { data } = await this.send<ScriptConfigurationCommandResponse>(
        {
          method: 'POST',
          path: '/api/v1/script-configurations/commands',
          json: { expectedRevision, metadata: toMetadata(receipt), command },
          auth: 'access',
          label: `scriptConfigurations.command:${command.type}`,
        },
        receipt,
      );
      return data;
    },
  };

  /** 渲染 Worker 的字体环境（规范 §6.12）。 */
  readonly fonts = {
    list: async (): Promise<FontListResponse> =>
      (await this.send<FontListResponse>({ method: 'GET', path: '/api/v1/fonts', auth: 'access', label: 'fonts.list' })).data,

    /** 一段文字在某字体下逐元素的实际字体、回退与缺字。 */
    inspect: async (input: FontInspectInput, signal?: AbortSignal): Promise<FontInspectResponse> =>
      (
        await this.send<FontInspectResponse>({
          method: 'POST',
          path: '/api/v1/fonts/inspect',
          json: input,
          auth: 'access',
          label: 'fonts.inspect',
          ...(signal ? { signal } : {}),
        })
      ).data,
  };

  readonly gpuPreview = {
    create: async (input: GpuPreviewCreateInput): Promise<GpuPreviewStreamState> => {
      const receipt = this.requireConn().receipts.create('preview');
      return (await this.send<GpuPreviewStreamState>({ method:'POST', path:'/api/v1/gpu-preview-streams', json:{...input,metadata:toMetadata(receipt)}, auth:'access', label:'gpuPreview.create' },receipt)).data;
    },
    read: async (streamId: string): Promise<GpuPreviewStreamState> =>
      (await this.send<GpuPreviewStreamState>({ method:'GET',path:`/api/v1/gpu-preview-streams/${enc(streamId)}`,auth:'access',label:'gpuPreview.read' })).data,
    replaceInput: async (streamId: string, input: { expectedStreamRevision:string;source:PreviewSource }): Promise<GpuPreviewStreamState> => {
      const receipt=this.requireConn().receipts.create('preview-input');
      return (await this.send<GpuPreviewStreamState>({ method:'PUT',path:`/api/v1/gpu-preview-streams/${enc(streamId)}/source`,json:{...input,metadata:toMetadata(receipt)},auth:'access',label:'gpuPreview.replaceInput' },receipt)).data;
    },
    resize: async (streamId: string, input: { expectedStreamRevision:string;size:PreviewSize }): Promise<GpuPreviewStreamState> => {
      const receipt=this.requireConn().receipts.create('preview-input');
      return (await this.send<GpuPreviewStreamState>({ method:'PUT',path:`/api/v1/gpu-preview-streams/${enc(streamId)}/size`,json:{...input,metadata:toMetadata(receipt)},auth:'access',label:'gpuPreview.resize' },receipt)).data;
    },
    close: async (streamId: string, expectedStreamRevision:string): Promise<GpuPreviewStreamState> => {
      const receipt=this.requireConn().receipts.create('preview');
      return (await this.send<GpuPreviewStreamState>({ method:'DELETE',path:`/api/v1/gpu-preview-streams/${enc(streamId)}`,json:{expectedStreamRevision,metadata:toMetadata(receipt)},auth:'access',label:'gpuPreview.close' },receipt)).data;
    },
  };

  readonly preview = {
    runtimeInfo: async (): Promise<PreviewRuntimeInfo> =>
      (await this.send<PreviewRuntimeInfo>({ method: 'GET', path: '/api/v1/runtime-info', auth: 'access', label: 'preview.runtimeInfo' })).data,

    /** 启动默认设备与 CPU、GPU 的检查结果（1.39.0，规范 §25.1）。每次调用都是 Worker 当时的真实检查。 */
    renderBackends: async (): Promise<RenderBackendsResponse> =>
      (await this.send<RenderBackendsResponse>({ method: 'GET', path: '/api/v1/render-backends', auth: 'access', label: 'preview.renderBackends' })).data,

    /**
     * 创建预览会话；会话数到上限（429 sessionQuotaExceeded）时短暂退避重试，三个 create 都一样。
     * signal 只停止退避重试，不中止已经发出的创建：服务端可能已经建好，调用方拿到回执后要自己关闭。
     */
    create: (input: CreatePreviewInput, signal?: AbortSignal): Promise<PreviewSessionResponse> =>
      this.withPreviewQuota(async () => {
        const receipt = this.requireConn().receipts.create('preview');
        const { data } = await this.send<PreviewSessionResponse>(
          {
            method: 'POST',
            path: '/api/v1/preview-sessions',
            json: { ...input, purpose: 'preview', metadata: toMetadata(receipt) },
            auth: 'access',
            label: 'preview.create',
          },
          receipt,
        );
        return data;
      }, signal),

    /** 模板候选试看（G16）：只读，返回 previewKind=presetCandidate 的预览会话；输入固定，工程或模板变了要重新创建。 */
    createPresetCandidate: (projectSessionId: string, input: PresetApplicationPreviewInput, signal?: AbortSignal): Promise<PreviewSessionResponse> =>
      this.withPreviewQuota(async () => {
        const receipt = this.requireConn().receipts.create('preview');
        const { data } = await this.send<PreviewSessionResponse>(
          {
            method: 'POST',
            path: `/api/v1/project-sessions/${enc(projectSessionId)}/preset-application-previews`,
            json: { ...input, metadata: toMetadata(receipt) },
            auth: 'access',
            label: 'preview.createPresetCandidate',
          },
          receipt,
        );
        return data;
      }, signal),

    /** 模板固定试样（G17）：脱离工程，在版本化样本上画一个模板；不写库、不改工程。 */
    createTemplatePreview: (input: TemplatePreviewInput, signal?: AbortSignal): Promise<PreviewSessionResponse> =>
      this.withPreviewQuota(async () => {
        const receipt = this.requireConn().receipts.create('preview');
        const { data } = await this.send<PreviewSessionResponse>(
          {
            method: 'POST',
            path: '/api/v1/template-preview-sessions',
            json: { ...input, metadata: toMetadata(receipt) },
            auth: 'access',
            label: 'preview.createTemplatePreview',
          },
          receipt,
        );
        return data;
      }, signal),

    /**
     * 作者草稿试样（规范 §21）：previewKind=authorDraft、committable=false。草稿改了以后用 replaceInput 换成新修订，
     * 不必重建；authorSourceRevision 必须等于草稿的在用修订。
     */
    createAuthorDraft: (
      input: {
        draftId: string;
        draftRevision: string;
        authorSourceRevision: string;
        sample: AuthorDraftSampleSelection;
        size: PreviewSize;
        alphaMode?: PreviewAlphaMode;
        renderBackend?: RenderBackend | null;
      },
      signal?: AbortSignal,
    ): Promise<PreviewSessionResponse> =>
      this.preview.create(
        {
          source: authorDraftSource(input.draftId, input.draftRevision, input.authorSourceRevision, input.sample),
          size: input.size,
          alphaMode: input.alphaMode ?? 'opaque',
          previewKind: 'authorDraft',
          committable: false,
          ...(input.renderBackend ? { renderBackend: input.renderBackend } : {}),
        },
        signal,
      ),

    /** 工程编辑后显式换输入；服务不会自动追随工程最新修订。 */
    replaceInput: async (previewId: string, input: ReplacePreviewInput): Promise<PreviewSessionResponse> => {
      const receipt = this.requireConn().receipts.create('preview-input');
      const { data } = await this.send<PreviewSessionResponse>(
        {
          method: 'PUT',
          path: `/api/v1/preview-sessions/${enc(previewId)}/input`,
          json: { ...input, metadata: toMetadata(receipt) },
          auth: 'access',
          label: 'preview.replaceInput',
        },
        receipt,
      );
      return data;
    },

    /**
     * 只给已有预览换设备（规范 §25.3）：相同设备是 no-op；不同设备成功后换新代次、旧帧作废，调用方采用返回的代次再取帧。
     * 失败时旧状态不变；previewGenerationConflict 要先 GET 重新同步。
     */
    setRenderBackend: async (previewId: string, input: PreviewRenderBackendInput): Promise<PreviewSessionResponse> => {
      const receipt = this.requireConn().receipts.create('preview-render-backend');
      const { data } = await this.send<PreviewSessionResponse>(
        {
          method: 'PUT',
          path: `/api/v1/preview-sessions/${enc(previewId)}/render-backend`,
          json: { ...input, metadata: toMetadata(receipt) },
          auth: 'access',
          label: 'preview.setRenderBackend',
        },
        receipt,
      );
      return data;
    },

    /** 202 只代表接受；seek 会返回新代次，调用方采用响应中的 previewGeneration。 */
    requestFrame: async (previewId: string, input: PreviewFrameRequestInput): Promise<PreviewFrameAcceptedResponse> => {
      const receipt = this.requireConn().receipts.create('frame');
      const { data } = await this.send<PreviewFrameAcceptedResponse>(
        {
          method: 'POST',
          path: `/api/v1/preview-sessions/${enc(previewId)}/frame-requests`,
          json: { ...input, metadata: toMetadata(receipt) },
          auth: 'access',
          label: 'preview.requestFrame',
        },
        receipt,
      );
      return data;
    },

    get: async (previewId: string, signal?: AbortSignal): Promise<PreviewSessionStateResponse> =>
      (
        await this.send<PreviewSessionStateResponse>({
          method: 'GET',
          path: `/api/v1/preview-sessions/${enc(previewId)}`,
          auth: 'access',
          label: 'preview.get',
          ...(signal ? { signal } : {}),
        })
      ).data,

    close: async (previewId: string, input: ClosePreviewInput): Promise<PreviewSessionStateResponse> => {
      const receipt = this.requireConn().receipts.create('preview-close');
      const { data } = await this.send<PreviewSessionStateResponse>(
        {
          method: 'POST',
          path: `/api/v1/preview-sessions/${enc(previewId)}/close`,
          json: { ...input, metadata: toMetadata(receipt) },
          auth: 'access',
          label: 'preview.close',
        },
        receipt,
      );
      return data;
    },

    /**
     * 尽力关闭（离开页面或丢弃会话时用），不抛错：代次或修订对不上（409，例如关闭时还有 seek 或换输入在途）
     * 就按 GET 读到的当前状态再关一次；会话已经没了算关上。
     */
    closeBestEffort: async (previewId: string, input: ClosePreviewInput): Promise<void> => {
      try {
        await this.preview.close(previewId, input);
      } catch (error) {
        if (!(error instanceof BackendError) || error.status !== 409) return;
        const current = await this.preview.get(previewId).catch(() => undefined);
        if (!current) return;
        await this.preview
          .close(previewId, { expectedPreviewGeneration: current.session.previewGeneration, expectedSourceRevision: current.session.source.sourceRevision })
          .catch(() => undefined);
      }
    },

    diagnostics: async (previewId: string, cursor?: string, limit?: number): Promise<PreviewDiagnosticsPage> =>
      (
        await this.send<PreviewDiagnosticsPage>({
          method: 'GET',
          path: `/api/v1/preview-sessions/${enc(previewId)}/diagnostics`,
          query: { cursor, limit: limit === undefined ? undefined : String(limit) },
          auth: 'access',
          label: 'preview.diagnostics',
        })
      ).data,

    /**
     * 等待匹配 previewId／generation／sourceRevision 且序号不早于 requestSeq 的帧。
     * 预览事件是有界 advisory，可能丢失，所以同时低频 GET 状态（GET 始终权威）。
     */
    waitForFrame: (
      previewId: string,
      expect: { generation: string; sourceRevision: PreviewSourceRevision; requestSeq: Sequence },
      options: { signal?: AbortSignal; timeoutMs?: number } = {},
    ): Promise<PreviewFrameMetadata> =>
      new Promise((resolve, reject) => {
        let settled = false;
        let checking = false;
        const pollMs = this.options.previewPollIntervalMs ?? 500;
        const cleanups: (() => void)[] = [];
        const finish = (fn: () => void) => {
          if (settled) return;
          settled = true;
          cleanups.forEach((c) => c());
          fn();
        };
        const check = async () => {
          if (settled || checking) return;
          checking = true;
          try {
            const state = await this.preview.get(previewId, options.signal);
            const frame = state.session.latestFrame;
            if (
              frame &&
              frame.previewGeneration === expect.generation &&
              frame.sourceRevision === expect.sourceRevision &&
              BigInt(frame.requestSeq) >= BigInt(expect.requestSeq)
            ) {
              finish(() => resolve(frame));
            } else if (state.session.state === 'failed') {
              finish(() => reject(new PreviewFailedError(state.failure, state.renderFailureCode)));
            } else if (state.session.state === 'closed' || state.session.state === 'closing') {
              finish(() => reject(new Error('预览会话已关闭。')));
            }
          } catch (error) {
            finish(() => reject(error));
          } finally {
            checking = false;
          }
        };
        cleanups.push(
          this.on('event', (event) => {
            if (event.preview?.previewId === previewId) void check();
          }),
          this.on('resync', () => void check()),
        );
        const timer = setInterval(() => void check(), pollMs);
        cleanups.push(() => clearInterval(timer));
        const timeout = setTimeout(
          () => finish(() => reject(new Error('等待预览帧超时。'))),
          options.timeoutMs ?? 60_000,
        );
        cleanups.push(() => clearTimeout(timeout));
        if (options.signal) {
          const onAbort = () => finish(() => reject(new DOMException('已中止', 'AbortError')));
          options.signal.addEventListener('abort', onAbort, { once: true });
          cleanups.push(() => options.signal?.removeEventListener('abort', onAbort));
        }
        void check();
      }),

    /** 鉴权读取 PNG 字节并核对 SHA-256 与大小。帧过期（410 previewFrameExpired）属于可恢复情况。 */
    fetchFrame: async (frame: PreviewFrameMetadata, signal?: AbortSignal): Promise<{ blob: Blob; sha256: string; verified: boolean }> => {
      const result = await fetchBinary(this.sender, frame.content.contentUrl, signal ? { signal } : {});
      if (!result.blob) throw new Error(`预览帧没有正文（HTTP ${result.status}）。`);
      const sha256 = await sha256Hex(result.blob);
      return { blob: result.blob, sha256, verified: sha256 === frame.content.sha256 && result.blob.size === frame.content.sizeBytes };
    },

    /**
     * 静态预览一帧：以精确修订创建预览 → 请求一帧 → 等待就绪 → 读取 PNG → 关闭预览。
     * 先读字节再关闭：关闭后帧可能 404。一次性帧同时最多 MAX_TRANSIENT_PREVIEWS 个，多出的排队。
     */
    renderStill: (input: {
      projectSessionId: string;
      sourceRevision: Revision;
      timeUs: Micros;
      size: PreviewSize;
      alphaMode?: PreviewAlphaMode;
      renderBackend?: RenderBackend | null;
      signal?: AbortSignal;
    }): Promise<StillFrameResult> =>
      this.transientStill(
        () =>
          this.preview.create({
            source: { kind: 'project', sourceId: input.projectSessionId, sourceRevision: input.sourceRevision },
            size: input.size,
            alphaMode: input.alphaMode ?? 'opaque',
            ...(input.renderBackend ? { renderBackend: input.renderBackend } : {}),
          }, input.signal),
        input.timeUs,
        input.signal,
      ),

    /** 模板候选试看一帧（G16）：创建候选 → 请求一帧 → 等待就绪 → 读取 PNG → 关闭。工程和模板库都不变。 */
    renderCandidateStill: async (
      projectSessionId: string,
      input: PresetApplicationPreviewInput & { timeUs: Micros; signal?: AbortSignal },
    ): Promise<StillFrameResult> => {
      const { timeUs, signal, ...request } = input;
      return this.transientStill(() => this.preview.createPresetCandidate(projectSessionId, request, signal), timeUs, signal);
    },

    /** 模板固定试样一帧（G17）：创建试样 → 请求一帧 → 等待就绪 → 读取 PNG → 关闭。 */
    renderTemplateStill: (input: TemplatePreviewInput & { timeUs: Micros; signal?: AbortSignal }): Promise<StillFrameResult> => {
      const { timeUs, signal, ...request } = input;
      return this.transientStill(() => this.preview.createTemplatePreview(request, signal), timeUs, signal);
    },
  };

  /** 一次性静态帧：先排队占一个名额，再创建会话、取一帧、关闭；名额在关闭请求返回后才让出。 */
  private async transientStill(create: () => Promise<PreviewSessionResponse>, timeUs: Micros, signal?: AbortSignal): Promise<StillFrameResult> {
    await this.acquireTransientPreview(signal);
    try {
      if (signal?.aborted) throw abortError();
      return await this.stillOf(await create(), timeUs, signal);
    } finally {
      this.releaseTransientPreview();
    }
  }

  private acquireTransientPreview(signal?: AbortSignal): Promise<void> {
    if (signal?.aborted) return Promise.reject(abortError());
    if (this.transientPreviews < MAX_TRANSIENT_PREVIEWS) {
      this.transientPreviews++;
      return Promise.resolve();
    }
    return new Promise((resolve, reject) => {
      const grant = () => {
        signal?.removeEventListener('abort', onAbort);
        resolve();
      };
      const onAbort = () => {
        const index = this.transientWaiters.indexOf(grant);
        if (index >= 0) this.transientWaiters.splice(index, 1);
        reject(abortError());
      };
      this.transientWaiters.push(grant);
      signal?.addEventListener('abort', onAbort, { once: true });
    });
  }

  /** 有人排队就把名额直接交给下一个，计数不变。 */
  private releaseTransientPreview(): void {
    const next = this.transientWaiters.shift();
    if (next) next();
    else this.transientPreviews--;
  }

  /** 名额满时退避重试；signal 中止后不再发新的创建（已经发出的照常等结果）。 */
  private async withPreviewQuota<T>(create: () => Promise<T>, signal?: AbortSignal): Promise<T> {
    for (let attempt = 0; ; attempt++) {
      if (signal?.aborted) throw abortError();
      try {
        return await create();
      } catch (error) {
        const delay = PREVIEW_QUOTA_RETRY_MS[attempt];
        if (delay === undefined || !(error instanceof BackendError && error.code === 'sessionQuotaExceeded')) throw error;
        await sleep(delay, signal);
      }
    }
  }

  /** 在刚建好的预览会话上取一帧，读完字节后关闭会话（关闭后帧可能 404）。 */
  private async stillOf(preview: PreviewSessionResponse, timeUs: Micros, signal?: AbortSignal): Promise<StillFrameResult> {
    const sourceRevision = preview.source.sourceRevision;
    try {
      const requestSeq = nextSequence(undefined);
      const accepted = await this.preview.requestFrame(preview.previewId, {
        expectedSourceRevision: sourceRevision,
        previewGeneration: preview.previewGeneration,
        requestSeq,
        timeUs,
        intent: 'playback',
      });
      const frame = await this.preview.waitForFrame(
        preview.previewId,
        { generation: accepted.previewGeneration, sourceRevision, requestSeq },
        signal ? { signal } : {},
      );
      const content = await this.preview.fetchFrame(frame, signal);
      return { frame, preview, ...content };
    } finally {
      await this.preview.closeBestEffort(preview.previewId, { expectedPreviewGeneration: preview.previewGeneration, expectedSourceRevision: sourceRevision });
    }
  }

  /** 鉴权读取任意二进制端点（相对路径用 baseUrl 拼接）。 */
  fetchBinary(url: string, options?: FetchBinaryOptions): Promise<BinaryResult> {
    return fetchBinary(this.sender, url, options);
  }

  // ── 内部：请求发送与重试 ───────────────────────────────

  private readonly sender = <T>(request: TransportRequest) => this.send<T>(request);

  /**
   * 发送请求并按决策表重试（设计 §3.3）。有 receipt 的是有副作用请求：
   * 重试一律复用同一 requestId 和同一 deadline，直到 deadline 前 5 秒，之后报告结果未知。
   */
  private async send<T>(request: TransportRequest, receipt?: Receipt): Promise<TransportResponse<T>> {
    const conn = this.requireConn();
    let resumed = false;
    for (let attempt = 0; ; attempt++) {
      if (UNUSABLE_STATES.has(conn.session.state)) {
        throw new ConnectionUnavailableError(conn.session.state);
      }
      try {
        return await conn.transport.request<T>(request);
      } catch (error) {
        if (isAbortError(error)) throw error;
        const decision = decideRetry(error, receipt !== undefined);
        if (decision === 'resume' && !resumed) {
          resumed = true;
          await conn.session.resume();
          continue;
        }
        if (decision === 'reauthenticate') {
          const state = await conn.session.handleUnauthorized();
          if (state === 'ready' && !resumed) {
            resumed = true;
            continue;
          }
          throw state === 'ready' ? error : new ConnectionUnavailableError(state, { cause: error });
        }
        if (decision === 'hostRestarted') {
          conn.session.markHostRestarted();
          throw new ConnectionUnavailableError('hostRestarted', { cause: error });
        }
        if (decision === 'unknownOutcome' && receipt) throw new OutcomeUnknownError(receipt.requestId, error);
        if (decision === 'retry') {
          const delay = retryDelayMs(attempt);
          if (receipt) {
            if (conn.clock.now() + delay >= receipt.deadlineMs - RECEIPT_RETRY_MARGIN_MS) {
              throw new OutcomeUnknownError(receipt.requestId, error);
            }
          } else if (attempt >= MAX_READ_RETRIES) {
            void this.probeServer();
            throw error;
          }
          if (error instanceof NetworkError && attempt >= 1) {
            // 连续连不上：核对启动信息，Server 已换地址时不再对旧地址空转。
            await this.probeServer();
            if (conn.session.state === 'hostRestarted') throw new ConnectionUnavailableError('hostRestarted', { cause: error });
          }
          await sleep(delay, request.signal);
          continue;
        }
        throw error;
      }
    }
  }

  private async postJob(path: string, operation: string, body: Record<string, unknown>): Promise<JobResponse> {
    const receipt = this.requireConn().receipts.create(operation);
    const { data } = await this.send<JobResponse>(
      { method: 'POST', path, json: { ...body, metadata: toMetadata(receipt) }, auth: 'access', label: `jobs.${operation}` },
      receipt,
    );
    return data;
  }

  /** 事件流 resync：重新 GET client state，通知订阅者重读，返回新游标。 */
  private async resync(reason: string): Promise<string> {
    const state = await this.clientState();
    for (const summary of state.sessions) this.queue.observeRevision(summary.projectSessionId, summary.projectRevision);
    this.emit('resync', { reason, state });
    return state.eventCursor;
  }

  private observeEvent(event: BackendEvent): void {
    const summary = event.summary;
    if (!summary || event.hostInstanceId !== this.conn?.session.snapshot.get().hostInstanceId) return;
    try {
      checkSummary(summary);
    } catch {
      return;
    }
    if (event.type === 'project.closed') this.queue.forget(summary.projectSessionId);
    else this.queue.observeRevision(summary.projectSessionId, summary.projectRevision);
  }

  private onSessionChange(conn: Connection): void {
    if (this.conn !== conn) return;
    const state = conn.session.state;
    if (state === 'ready' && conn.session.snapshot.get().heartbeat === 'failing') void this.probeServer();
    if (state === 'hostRestarted' || state === 'needsRegistration' || state === 'failed') {
      conn.events.stop();
      if (state === 'hostRestarted') {
        this.queue.reset('Server 已重启');
        this.emit('reset', { reason: conn.session.snapshot.get().reason ?? 'Server 已重启' });
      }
    }
    this.refreshSnapshot(conn);
  }

  private refreshSnapshot(conn: Connection): void {
    if (this.conn !== conn) return;
    const session = conn.session.snapshot.get();
    const cursor = conn.events.cursor;
    this.state$.set({
      ...session,
      baseUrl: conn.bootstrap.baseUrl,
      serverOffsetMs: conn.clock.offset,
      events: { status: conn.events.status, ...(cursor ? { cursor } : {}), ...conn.events.stats },
      ...(conn.capabilities ? { productVersion: conn.capabilities.productVersion } : {}),
    });
  }

  private requireConn(): Connection {
    if (!this.conn || this.disposed) throw new ConnectionUnavailableError(this.disposed ? 'disposed' : 'connecting');
    return this.conn;
  }

  private teardownConnection(): void {
    const conn = this.conn;
    if (!conn) return;
    this.conn = undefined;
    conn.events.stop();
    conn.session.dispose();
    for (const dispose of conn.disposers) dispose();
  }

  private attachEnvironment(): void {
    const env = this.options.environment === undefined ? browserEnvironment() : this.options.environment;
    if (!env) return;
    if (env.onVisible) this.environmentDisposers.push(env.onVisible(() => this.conn?.session.notifyVisible()));
    // 页面隐藏／卸载时主动断开事件流，释放服务端连接名额；bfcache 恢复时续接。
    if (env.onPageHide) this.environmentDisposers.push(env.onPageHide(() => this.conn?.events.stop()));
    if (env.onPageShow) {
      this.environmentDisposers.push(
        env.onPageShow((persisted) => {
          const conn = this.conn;
          if (!persisted || !conn || conn.session.state !== 'ready') return;
          conn.session.notifyVisible();
          conn.events.start(conn.events.cursor);
        }),
      );
    }
  }
}

function defaultIdentityStore(): IdentityStore {
  try {
    if (typeof sessionStorage !== 'undefined') return new WebStorageIdentityStore(sessionStorage);
  } catch {
    // 访问被禁止时退回内存。
  }
  return new MemoryIdentityStore();
}

function browserEnvironment(): BrowserEnvironment | null {
  if (typeof document === 'undefined' || typeof window === 'undefined') return null;
  return {
    onVisible: (callback) => {
      const handler = () => {
        if (document.visibilityState === 'visible') callback();
      };
      document.addEventListener('visibilitychange', handler);
      return () => document.removeEventListener('visibilitychange', handler);
    },
    onPageHide: (callback) => {
      window.addEventListener('pagehide', callback);
      return () => window.removeEventListener('pagehide', callback);
    },
    onPageShow: (callback) => {
      const handler = (event: PageTransitionEvent) => callback(event.persisted);
      window.addEventListener('pageshow', handler);
      return () => window.removeEventListener('pageshow', handler);
    },
  };
}

/** 两个仍为 JSON number 的 Int64 字段要求安全整数。 */
function checkSummary(summary: ProjectSessionSummary): void {
  assertSafeJsonInteger(summary.audioDurationUs, 'summary.audioDurationUs');
}

/** 作者草稿的预览来源；建会话与换输入共用。 */
export function authorDraftSource(draftId: string, draftRevision: string, authorSourceRevision: string, sample: AuthorDraftSampleSelection): PreviewSource {
  return { kind: 'authorDraft', sourceId: draftId, sourceRevision: draftRevision, authorSourceRevision, templateSample: sample };
}

function enc(id: string): string {
  return encodeURIComponent(id);
}

function describe(error: unknown): string {
  if (error instanceof BackendError) return `${error.code ?? `HTTP ${error.status}`}：${error.message}`;
  return error instanceof Error ? error.message : String(error);
}

function abortError(): DOMException {
  return new DOMException('已中止', 'AbortError');
}

function sleep(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) return reject(abortError());
    const timer = setTimeout(() => {
      signal?.removeEventListener('abort', onAbort);
      resolve();
    }, ms);
    const onAbort = () => {
      clearTimeout(timer);
      reject(abortError());
    };
    signal?.addEventListener('abort', onAbort, { once: true });
  });
}

function pageQuery(page: { offset?: number; limit?: number }): Record<string, string | undefined> {
  return {
    offset: page.offset === undefined ? undefined : String(page.offset),
    limit: page.limit === undefined ? undefined : String(page.limit),
  };
}
