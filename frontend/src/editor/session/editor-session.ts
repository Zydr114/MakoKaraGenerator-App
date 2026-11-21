// EditorSession（W1 设计 §4.3、W2 设计 §2.2）：一个已打开工程的读模型缓存、选择与命令发送。
// 不依赖 React；页面通过 state$ 订阅。工程内容以后端为准，这里只按修订整体替换缓存，不本地改写。
// 缓存全部来源的行（逐字预览与翻译关联需要），lines 是当前来源的视图。

import { BackendError, ObservableValue, OutcomeUnknownError, type MakoClient, type Unsubscribe } from '../../client';
import {
  isNewerRevision,
  type AnalysisProgress,
  type Composition,
  type ProjectChangeSet,
  type ProjectCommand,
  type LyricBranchSummary,
  type ProjectCommandResponse,
  type ProjectEditCommand,
  type ProjectSessionSummary,
  type ProjectStyleResponse,
  type ProjectResolvedStyleResponse,
  type ProjectSettings,
  type Revision,
} from '../../protocol';
import { editBatches } from '../model/edit-batches';
import { fromEditableLine, fromMinimalLine, reconcileSelection, type EditorLine, type Selection } from '../model/lyrics';
import type { FrameRate } from '../model/time';

/** 读模型来源：原最小投影（lyrics/lines），或 S1 可编辑窗口（按 capabilities 自动选择）。 */
export type ReadModelKind = 'minimal' | 'editable';

export interface EditorNotice {
  level: 'info' | 'warning' | 'error';
  message: string;
  /** 后端错误码或 failureCode，供界面就地提示。 */
  code?: string;
}

export interface EditorState {
  summary: ProjectSessionSummary;
  readModel: ReadModelKind;
  branchId: string;
  branches?: LyricBranchSummary[];
  /** 主来源（可编辑读模型才有）。 */
  primaryBranchId?: string;
  settings?: ProjectSettings;
  /** 当前来源的行（linesByBranch[branchId] 的视图）。 */
  lines: EditorLine[];
  /** 全部来源的行。 */
  linesByBranch: Readonly<Record<string, EditorLine[]>>;
  /** lines 对应的工程修订。 */
  linesRevision?: Revision;
  selection?: Selection;
  /** 正在重读读模型。 */
  syncing: boolean;
  /** 已发出、尚未返回的命令数。 */
  pendingCommands: number;
  notice?: EditorNotice;
}

/** 补丁：值为 undefined 的键会从状态中删除。 */
type EditorPatch = { [K in keyof EditorState]?: EditorState[K] | undefined };

/** 领域拒绝（422 commandRejected）的 failureCode 提示（规范 §6.5～§6.9）；未知原因显示后端 message。 */
const FAILURE_TEXT: Record<string, string> = {
  Untimed: '这个元素还没有时间，请先打起点。',
  InvalidRange: '终点必须晚于起点。',
  NudgeOutOfBounds: '微调超出范围：不能早于 0，也不能超过音频末尾。',
  BeyondContentDuration: '超出了音频时长。',
  DomainValidationFailed: '结果不符合规则（时间、行或展示设置校验未通过）。',
  TargetStale: '这个目标已经不存在或已被改变，请重新选择。',
  WorkspaceStale: '工程已被替换，请重新打开。',
  InvalidTarget: '目标不符合这个操作的要求。',
  IdentifierExhausted: '无法再分配新的编号。',
  PrimaryBranchProtected: '主歌词不能删除，也不能改变角色。',
  InvalidConfiguration: '来源名称、语言或时间策略不合法。',
  InvalidPrimaryLine: '只能关联到主歌词里的行。',
  InvalidPresentation: '这个设置与当前画面编排不兼容。',
  InvalidCanvasSize: '画布宽高必须是正整数。',
  InvalidFrameRate: '帧率必须是正数。',
  InvalidOutputDuration: '输出时长必须大于 0。',
  MediaDurationUnavailable: '音频时长不可用。',
  EmptyImport: '文件里没有可导入的歌词行。',
  EmptyText: '没有可追加的非空行。',
  unsupportedMediaType: '后端不接受这种文件类型。',
  resourceTooLarge: '文件太大，超出了后端的导入上限。',
  requestQuotaExceeded: '后端正忙，请稍后再试。',
  resourceUnauthorized: '这个文件不属于当前连接，请重新选择。',
  taskUnavailable: '后端的音频分析不可用。',
  resourceQuotaExceeded: '后端的分析结果空间已满，请稍后再试。',
};

export interface CommandOptions {
  /** 这条命令的 failureCode 说明；返回 undefined 时用通用说明。 */
  failureText?: (failureCode: string) => string | undefined;
}

/** 后端错误的中文说明：已知的 failureCode／错误码查表，其余用后端的 message。 */
export function errorText(error: unknown): string {
  const code = error instanceof BackendError ? (error.failureCode ?? error.code ?? undefined) : undefined;
  return (code && FAILURE_TEXT[code]) || (error instanceof Error ? error.message : String(error));
}

/** 分析任务失败的常见原因（规范 §12.2）。 */
const ANALYSIS_FAILURE_TEXT: Record<string, string> = {
  WorkerAnalysisTimedOut: '分析超时。',
  WorkerAnalysisProfileMismatch: '后端的分析组件版本不匹配，暂时不能分析。',
};

/** 时间命令的 DomainValidationFailed 有更具体的含义。 */
const TIMING_DOMAIN_TEXT = '时间越出了所在行，或与相邻元素重叠、倒序。';
/** applyEdits 的 413：回执预算装不下这批修改的反馈（W6 设计 §0），不是文件太大。 */
const BATCH_TOO_LARGE_TEXT = '一次修改的内容太多，超出了后端的回执预算，请分几次操作。';

/** 命令响应里建议的选择：等刷新完成（新 ID 进入缓存）后再应用。 */
interface SelectionHint {
  branchId?: string;
  /** null 表示清空选择。 */
  selection?: Selection | null;
}

/** 只影响时间的 changeSet 区域（规范 §6.5）；出现其他区域时整体重读。 */
const TIMING_AREAS = new Set(['lyricsTiming', 'lyricsRubyTiming', 'timeline', 'renderInputs']);
/** 一次完整读取最多试几次（每次都因为修订前进而冲突时）。 */
const READ_ATTEMPTS = 20;
/** 不进歌词读模型的区域（W4 设计 §2.5）：编排页、外观页按修订各自读取，这里只更新摘要。 */
const OWN_READ_AREAS = new Set(['orchestration', 'presetApplications']);

/** changeSet 只涉及编排或分配（可以带 renderInputs），且不涉及行。 */
function outsideLyrics(changeSet: ProjectChangeSet): boolean {
  if (changeSet.requiresFullRefresh || changeSet.lineIds.length > 0) return false;
  return changeSet.areas.some((a) => OWN_READ_AREAS.has(a)) && changeSet.areas.every((a) => OWN_READ_AREAS.has(a) || a === 'renderInputs');
}

const EDITABLE_FEATURES = [
  'project-sessions.lyrics.branches.read',
  'project-sessions.lyrics.window.read',
  'project-sessions.settings.read',
];

export class EditorSession {
  readonly state$: ObservableValue<EditorState>;
  readonly projectSessionId: string;
  private readonly client: MakoClient;
  /** 新建时由本页给出的画面设置；没有设置读取（S1）时用它确定帧率。 */
  private readonly createdComposition: Composition | undefined;
  /** 后端公布的 capabilities.features。 */
  private readonly features: ReadonlySet<string>;
  /** 刷新循环（单飞）；循环内按顺序读取，结果按修订单调应用。 */
  private refreshing: Promise<void> | undefined;
  /** 待整体重读。 */
  private fullRefresh = false;
  /** 待按行重读的行 ID（时间命令不改变行序，按缓存下标读 limit=1 的窗口）。 */
  private readonly staleLines = new Set<string>();
  /** 已经据以安排刷新的最新修订：同一修订的命令响应与 project.changed 事件只处理一次。 */
  private invalidatedRevision: Revision | undefined;
  private disposed = false;
  /** 用户每改一次选择加一；命令的建议选择只在发出命令后用户没再改过选择时应用。 */
  private selectionVersion = 0;
  private readonly unsubscribe: Unsubscribe;

  private constructor(client: MakoClient, summary: ProjectSessionSummary, features: readonly string[], composition?: Composition) {
    this.client = client;
    this.features = new Set(features);
    const readModel: ReadModelKind = EDITABLE_FEATURES.every((f) => this.features.has(f)) ? 'editable' : 'minimal';
    this.projectSessionId = summary.projectSessionId;
    this.createdComposition = composition;
    this.state$ = new ObservableValue<EditorState>({
      summary,
      readModel,
      branchId: 'primary',
      lines: [],
      linesByBranch: {},
      syncing: false,
      pendingCommands: 0,
    });
    // 事件只是提示：另一个标签页的修改或错过的事件都通过重读拿到权威状态。
    const offEvent = client.on('event', (event) => {
      if (event.type !== 'project.changed' || event.summary?.projectSessionId !== this.projectSessionId) return;
      void this.invalidate(event.summary.projectRevision, event.changeSet, event.summary);
    });
    const offResync = client.on('resync', () => void this.refresh());
    this.unsubscribe = () => {
      offEvent();
      offResync();
    };
  }

  /** 接上一个已在服务端打开的工程会话并读取读模型。 */
  static async attach(client: MakoClient, projectSessionId: string, options: { composition?: Composition } = {}): Promise<EditorSession> {
    const [summary, capabilities] = await Promise.all([client.projects.get(projectSessionId), client.capabilities()]);
    const session = new EditorSession(client, summary, capabilities.features, options.composition);
    await session.refresh();
    return session;
  }

  get state(): EditorState {
    return this.state$.get();
  }

  /** 后端是否开放某条命令（按 capabilities.features 的 project-sessions.commands.<type>）。 */
  supportsCommand(type: ProjectCommand['type']): boolean {
    return this.features.has(`project-sessions.commands.${type}`);
  }

  /** 后端是否公布某项 capabilities.features。 */
  supports(feature: string): boolean {
    return this.features.has(feature);
  }

  /** 已知的最新工程修订（命令队列只接受更大的修订）。 */
  get latestRevision(): Revision {
    return this.client.projects.knownRevision(this.projectSessionId) ?? this.state.summary.projectRevision;
  }

  /** 同一修订下读取句子的直接覆盖和包含模板的最终静态样式。 */
  async readLineStyle(branchId: string, lineId: string, revision: Revision): Promise<{ local: ProjectStyleResponse; resolved?: ProjectResolvedStyleResponse; resolvedError?: string }> {
    const [local, resolved] = await Promise.allSettled([
      this.client.projects.style(this.projectSessionId, { kind: 'line', branchId, lineId }, revision),
      this.client.projects.resolvedStyle(this.projectSessionId, revision, { branchId, lineId }),
    ]);
    if (local.status === 'rejected') throw local.reason;
    return resolved.status === 'fulfilled'
      ? { local: local.value, resolved: resolved.value }
      : { local: local.value, resolvedError: errorText(resolved.reason) };
  }

  get frameRate(): FrameRate | undefined {
    const settings = this.state.settings;
    if (settings) return { numerator: settings.frameRateNumerator, denominator: settings.frameRateDenominator };
    const composition = this.createdComposition;
    if (composition) return { numerator: composition.frameRateNumerator ?? 30, denominator: composition.frameRateDenominator ?? 1 };
    return undefined;
  }

  dispose(): void {
    this.disposed = true;
    this.unsubscribe();
  }

  /** 切换来源：全部来源已在缓存里，只切换视图并清空选择。 */
  selectBranch(branchId: string): void {
    if (branchId === this.state.branchId) return;
    this.selectionVersion++;
    this.patch({ branchId, selection: undefined, lines: this.state.linesByBranch[branchId] ?? [] });
  }

  /** 某来源的行（不存在时为空）。 */
  linesOf(branchId: string): EditorLine[] {
    return this.state.linesByBranch[branchId] ?? [];
  }

  select(selection: Selection | undefined): void {
    this.selectionVersion++;
    this.patch({ selection: reconcileSelection(this.state.lines, selection) });
  }

  notify(notice: EditorNotice): void {
    this.patch({ notice });
  }

  clearNotice(): void {
    if (this.state.notice) this.patch({ notice: undefined });
  }

  /** 整体重读摘要与读模型。与进行中的刷新合并。 */
  refresh(): Promise<void> {
    this.fullRefresh = true;
    return this.kick();
  }

  /**
   * 修订 revision 带来的变化：按 changeSet 只重读受影响的行；最小投影、结构变化、
   * requiresFullRefresh（含 changeSet 超过 16 KiB 时清空 ID 的情况）或缺少 changeSet 时整体重读。
   * 只涉及编排或分配时不重读歌词，只更新摘要：命令回执已经带了摘要，事件带 summary。
   */
  private invalidate(revision: Revision, changeSet: ProjectChangeSet | undefined, summary?: ProjectSessionSummary): Promise<void> {
    if (this.invalidatedRevision !== undefined && !isNewerRevision(revision, this.invalidatedRevision)) return this.refreshing ?? Promise.resolve();
    this.invalidatedRevision = revision;
    if (changeSet && this.state.readModel === 'editable' && outsideLyrics(changeSet)) {
      if (summary && !isNewerRevision(this.state.summary.projectRevision, summary.projectRevision)) this.patch({ summary: { ...this.state.summary, ...summary } });
      return this.refreshing ?? Promise.resolve();
    }
    if (!changeSet || changeSet.requiresFullRefresh || this.state.readModel === 'minimal') {
      this.fullRefresh = true;
    } else {
      for (const id of changeSet.lineIds) this.staleLines.add(id);
      // 时间变化只列出行和元素；其他区域（正文、结构、来源、设置）一律整体重读。
      if (changeSet.lineIds.length === 0 || changeSet.areas.some((a) => !TIMING_AREAS.has(a))) this.fullRefresh = true;
    }
    return this.kick();
  }

  private kick(): Promise<void> {
    if (this.refreshing) return this.refreshing;
    this.patch({ syncing: true });
    this.refreshing = (async () => {
      try {
        while (!this.disposed && (this.fullRefresh || this.staleLines.size > 0)) {
          if (this.fullRefresh) {
            this.fullRefresh = false;
            this.staleLines.clear();
            await this.readOnce();
          } else {
            await this.readStaleLines();
          }
        }
      } catch (error) {
        this.report(error, '读取工程失败');
      } finally {
        this.refreshing = undefined;
        if (!this.disposed) this.patch({ syncing: false });
      }
    })();
    return this.refreshing;
  }

  /** 以已知最新修订逐行重读；行找不到或修订已变（409）时改为整体重读。 */
  private async readStaleLines(): Promise<void> {
    const revision = this.client.projects.knownRevision(this.projectSessionId) ?? this.state.summary.projectRevision;
    const ids = [...this.staleLines];
    this.staleLines.clear();
    const updated = new Map<string, EditorLine>();
    for (const id of ids) {
      const found = this.locateLine(id);
      if (!found) {
        this.fullRefresh = true;
        return;
      }
      try {
        const page = await this.client.projects.lyricWindow(this.projectSessionId, found.branchId, revision, { offset: found.index, limit: 1 });
        const line = page.lines[0];
        if (!line || line.id !== id) {
          this.fullRefresh = true;
          return;
        }
        updated.set(id, fromEditableLine(line));
      } catch (error) {
        if (error instanceof BackendError && error.code === 'revisionConflict') {
          this.fullRefresh = true;
          return;
        }
        throw error;
      }
    }
    if (this.state.linesRevision !== undefined && isNewerRevision(this.state.linesRevision, revision)) return;
    const linesByBranch: Record<string, EditorLine[]> = {};
    for (const [branchId, lines] of Object.entries(this.state.linesByBranch)) {
      linesByBranch[branchId] = lines.map((line) => updated.get(line.id) ?? line);
    }
    this.apply({ linesByBranch, linesRevision: revision });
  }

  private locateLine(lineId: string): { branchId: string; index: number } | undefined {
    for (const [branchId, lines] of Object.entries(this.state.linesByBranch)) {
      const index = lines.findIndex((line) => line.id === lineId);
      if (index >= 0) return { branchId, index };
    }
    return undefined;
  }

  /**
   * 读一次完整读模型。读取途中修订变了（409）就从摘要重来：连续快速撤销时会连着冲突好几次，
   * 只要每次读到的修订都比上次冲突时新就继续；同一修订又冲突，或重试满 READ_ATTEMPTS 次，才放弃。
   */
  private async readOnce(): Promise<void> {
    let conflicted: Revision | undefined;
    for (let attempt = 1; ; attempt++) {
      let revision: Revision | undefined;
      try {
        const summary = await this.client.projects.get(this.projectSessionId);
        if (this.state.readModel === 'editable') {
          revision = summary.projectRevision;
          const [branchesPage, settings] = await Promise.all([
            this.client.projects.branches(this.projectSessionId, revision),
            this.client.projects.settings(this.projectSessionId, revision),
          ]);
          const linesByBranch: Record<string, EditorLine[]> = {};
          for (const branch of branchesPage.branches) {
            linesByBranch[branch.id] = (await this.client.projects.allLines(this.projectSessionId, branch.id, revision)).map(fromEditableLine);
          }
          // 读取期间用户可能换了来源：读完再按当时的来源决定，来源已不存在才回到主来源。
          const branchId = branchesPage.branches.some((b) => b.id === this.state.branchId) ? this.state.branchId : branchesPage.primaryBranchId;
          this.apply({ summary, branchId, branches: branchesPage.branches, primaryBranchId: branchesPage.primaryBranchId, settings, linesByBranch, linesRevision: revision });
        } else {
          const response = await this.client.projects.lyrics(this.projectSessionId, this.state.branchId);
          // 最小投影不带修订门禁：摘要与歌词之间若有新命令，以歌词响应自带的修订为准，摘要随后再读。
          const fresh = response.projectRevision === summary.projectRevision ? summary : await this.client.projects.get(this.projectSessionId);
          this.apply({ summary: fresh, linesByBranch: { [this.state.branchId]: response.lines.map(fromMinimalLine) }, linesRevision: response.projectRevision });
        }
        return;
      } catch (error) {
        const advanced = revision !== undefined && (conflicted === undefined || isNewerRevision(revision, conflicted));
        if (error instanceof BackendError && error.code === 'revisionConflict' && advanced && attempt < READ_ATTEMPTS) {
          conflicted = revision;
          continue;
        }
        throw error;
      }
    }
  }

  private apply(next: EditorPatch & { linesByBranch: Record<string, EditorLine[]> }): void {
    const branchId = next.branchId ?? this.state.branchId;
    const lines = next.linesByBranch[branchId] ?? [];
    // 读取期间又有命令返回时，摘要（修订、撤销状态）已经比这次读到的新；内容照常显示，摘要不回退。
    const stale = next.summary && isNewerRevision(this.state.summary.projectRevision, next.summary.projectRevision);
    this.patch({ ...next, ...(stale ? { summary: this.state.summary } : {}), lines, selection: reconcileSelection(lines, this.state.selection) });
  }

  /**
   * 发送一条工程命令。changed 时刷新读模型；no-op 只更新撤销状态。
   * revisionConflict：命令队列已清空待发命令，这里重读并提示，调用方保留自己的草稿。
   * 返回 undefined 表示命令被拒绝或结果未知（已在 notice 中说明）。
   * options.failureText：这条命令自己的 failureCode 说明（同一个代码在不同命令里含义不同时用），查不到时用通用说明。
   */
  async command(command: ProjectCommand, options?: CommandOptions): Promise<ProjectCommandResponse | undefined> {
    const selectionVersion = this.selectionVersion;
    this.patch({ pendingCommands: this.state.pendingCommands + 1 });
    try {
      const response = await this.client.projects.command(this.projectSessionId, command);
      this.patch({
        summary: {
          ...this.state.summary,
          projectRevision: response.projectRevision,
          isDirty: response.isDirty,
          canUndo: response.canUndo,
          canRedo: response.canRedo,
        },
      });
      if (response.changed) {
        const refreshed = this.invalidate(response.projectRevision, response.changeSet);
        const hint = selectionHint(response);
        // 刷新完成前用户已经选了别的，就不再用这条命令的建议覆盖用户的选择。
        if (hint) void refreshed.then(() => this.selectionVersion === selectionVersion && this.applyHint(hint));
      } else {
        this.patch({ notice: { level: 'info', message: '没有变化。' } });
      }
      return response;
    } catch (error) {
      if (error instanceof BackendError && error.code === 'revisionConflict') {
        this.patch({ notice: { level: 'warning', message: '工程已在别处修改，已重新读取。请检查后再操作。', code: error.code } });
      } else if (error instanceof OutcomeUnknownError) {
        this.patch({ notice: { level: 'warning', message: '请稍候，正在核对结果……' } });
      } else {
        this.report(error, '操作被拒绝', command.type, options?.failureText);
      }
      void this.refresh();
      return undefined;
    } finally {
      this.patch({ pendingCommands: this.state.pendingCommands - 1 });
    }
  }

  /**
   * 一条历史的批量修改（W6 设计 §2.2）：按 EDIT_BATCH_SIZE 分批，每批一次 project.applyEdits、一条历史。
   * 某一批被拒绝或结果未知就停下（原因已在 notice 里），返回已经提交的修改条数与批数。
   */
  async applyEdits(edits: readonly ProjectEditCommand[], options?: CommandOptions): Promise<{ applied: number; batches: number; total: number }> {
    const batches = editBatches(edits);
    let applied = 0;
    let done = 0;
    for (const batch of batches) {
      const response = await this.command({ type: 'project.applyEdits', edits: batch }, options);
      if (!response) break;
      applied += batch.length;
      done++;
    }
    return { applied, batches: done, total: batches.length };
  }

  /**
   * 分析音频并采用（规范 §12）：建分析任务 → 等成功终态 → audio.analysis.apply。
   * 分析本身不改工程（可以边分析边编辑），采用是一条历史。任务失败时抛出带中文说明的错误。
   */
  async analyzeAudio(onProgress: (progress: AnalysisProgress | undefined) => void, signal?: AbortSignal): Promise<ProjectCommandResponse | undefined> {
    const accepted = await this.client.projects.analyze(this.projectSessionId);
    const job = await this.client.jobs.waitForTerminal(accepted.jobId, {
      onProgress: (current) => onProgress(current.analysisProgress),
      ...(signal ? { signal } : {}),
    });
    if (job.state === 'cancelled') throw new Error('分析已取消。');
    if (job.state !== 'succeeded') {
      const code = job.failureCode ?? job.error?.failureCode ?? job.error?.code;
      throw new Error(code && ANALYSIS_FAILURE_TEXT[code] ? ANALYSIS_FAILURE_TEXT[code] : (job.error?.message ?? `分析没有成功（${job.state}）。`));
    }
    return this.command({ type: 'audio.analysis.apply', analysisJobId: job.jobId });
  }

  undo(): Promise<ProjectCommandResponse | undefined> {
    return this.command({ type: 'history.undo' });
  }

  redo(): Promise<ProjectCommandResponse | undefined> {
    return this.command({ type: 'history.redo' });
  }

  /** 摘要变化（例如保存 job 完成）后由调用方写回。 */
  updateSummary(summary: ProjectSessionSummary): void {
    this.patch({ summary });
  }

  private applyHint(hint: SelectionHint): void {
    if (this.disposed) return;
    // 建议选择不算用户操作：应用后恢复版本号，后续命令的建议仍然有效。
    const version = this.selectionVersion;
    if (hint.branchId && hint.branchId !== this.state.branchId && this.state.linesByBranch[hint.branchId]) this.selectBranch(hint.branchId);
    if (hint.selection === null) this.select(undefined);
    else if (hint.selection) this.select(hint.selection);
    this.selectionVersion = version;
  }

  private report(error: unknown, prefix: string, commandType?: string, failureText?: CommandOptions['failureText']): void {
    if (this.disposed) return;
    const code = error instanceof BackendError ? (error.failureCode ?? error.code ?? undefined) : undefined;
    const timing = commandType !== undefined && TIMING_COMMANDS.has(commandType);
    const custom = error instanceof BackendError && error.failureCode ? failureText?.(error.failureCode) : undefined;
    const message =
      custom ??
      (code === 'DomainValidationFailed' && timing
        ? TIMING_DOMAIN_TEXT
        : code === 'resourceTooLarge' && commandType === 'project.applyEdits'
          ? BATCH_TOO_LARGE_TEXT
          : errorText(error));
    this.patch({ notice: { level: 'error', message: `${prefix}：${message}`, ...(code ? { code } : {}) } });
  }

  private patch(partial: EditorPatch): void {
    if (this.disposed) return;
    const next = { ...this.state$.get() };
    for (const [key, value] of Object.entries(partial) as [keyof EditorState, unknown][]) {
      if (value === undefined) delete next[key];
      else (next as Record<string, unknown>)[key] = value;
    }
    this.state$.set(next);
  }
}

const TIMING_COMMANDS = new Set([
  'lyrics.setTimeRange',
  'lyrics.stampTokenStart',
  'lyrics.stampTokenEnd',
  'lyrics.clearTokenTime',
  'lyrics.nudgeTokenTime',
  'lyrics.shiftLine',
  'lyrics.setTokenTimes',
  'lyrics.strikeBeat',
  'lyrics.closeBeat',
]);

/** 从命令响应里取建议的来源与选择（规范 §6.6～§6.9）；project.applyEdits 取最后一条带建议的结果。 */
export function selectionHint(response: ProjectCommandResponse): SelectionHint | undefined {
  if (response.editResults) {
    for (let i = response.editResults.length - 1; i >= 0; i--) {
      const hint = resultHint(response.editResults[i]!);
      if (hint) return hint;
    }
    return undefined;
  }
  return resultHint(response);
}

type HintSource = Pick<ProjectCommandResponse, 'feedback' | 'tokenResult' | 'importResult' | 'branchResult' | 'rubyResult'>;

function resultHint(response: HintSource): SelectionHint | undefined {
  const content = response.feedback?.selection;
  if (content) return { branchId: content.branchId, selection: content.hasSelection && content.lineId ? { lineId: content.lineId } : null };
  const token = response.tokenResult?.selection;
  if (token) {
    return {
      branchId: token.branchId,
      selection: { lineId: token.lineId, ...(token.hasTokenSelection && token.tokenId ? { tokenId: token.tokenId } : {}) },
    };
  }
  // 注音片段（W6 设计 §2.1）：加、删片段时 selectionChanged，选中建议的片段或只保留文本单元；
  // 改字、改时间时不变，但带着被修改的片段，也选中它。
  const ruby = response.rubyResult;
  if (ruby?.target && (ruby.selectionChanged || ruby.selectedSegmentId)) {
    const { branchId, lineId, tokenId } = ruby.target;
    return { branchId, selection: { lineId, tokenId, ...(ruby.selectedSegmentId ? { segmentId: ruby.selectedSegmentId } : {}) } };
  }
  const imported = response.importResult;
  if (imported) return { branchId: imported.selectedBranchId, selection: { lineId: imported.selectedLineId } };
  if (response.branchResult) return { branchId: response.branchResult.selectedBranchId, selection: null };
  return undefined;
}
