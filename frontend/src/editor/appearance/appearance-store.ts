// 外观页状态（W3 设计 §2.6、§2.7）：目标选择、单个目标的本层直接覆盖（GET .../style，只读）、预览能力、
// 工程里的模板分配（G15 读模型）与应用、停用、移除、更新，以及模板选择器里的试看（G16，W4 设计 §9.1）。
// W7 起目标还可以是编排对象（片段、节点，W7 设计 §2.7）：编排结构由外观页从编排状态传进来（setOrchestration）。
// 不依赖 React。每个工程一个，切换页面时选择保留；“当前行”与歌词页同步（整体设计 §3.3）。

import { BackendError, ObservableValue, type MakoClient, type Unsubscribe } from '../../client';
import {
  micros,
  type CanonicalStyle,
  type PresetApplication,
  type PreviewSize,
  type ProjectCommand,
  type ProjectResolvedStyleResponse,
  type ProjectStyleResponse,
  type Revision,
  type SceneTextAppearance,
  type StaticMaterial,
  type StyleTarget,
  type TemplateLibraryId,
  type TemplateSummary,
} from '../../protocol';
import {
  commandTargets,
  isStyleTarget,
  PROJECT_TARGET,
  reconcileTargets,
  targetCountText,
  targetKey,
  targetsSignature,
  type AppearanceTarget,
} from '../model/appearance-targets';
import type { OrchestrationModel } from '../model/orchestration';
import { leadInsUsing } from '../model/orchestration-lead-ins';
import { indexApplications, type ApplicationIndex } from '../model/preset-applications';
import { FOLLOW_DEFAULT, renderBackendField, type RenderBackendRequest } from '../model/render-device';
import { lyricStyleToCountdownAppearance } from '../model/style-overrides';
import type { EditorSession } from '../session/editor-session';

/** 预设预览需要的 Worker 输入（合同 §9）：工程含模板分配时 Host 自动选它。 */
export const PRESETS_PREVIEW_PROFILE = 'static-lyrics-scene-presets-v1';
/** 模板候选试看（G16）。 */
export const CANDIDATE_PREVIEW = 'project-sessions.preset-application-previews.create';
/** 外观来源（合同 1.37.0）。 */
export const RESOLVED_STYLE_READ = 'project-sessions.resolved-style.read';

export interface InspectState {
  key: string;
  revision: Revision;
  status: 'loading' | 'ready' | 'error';
  response?: ProjectStyleResponse;
  error?: string;
}

/** 外观来源：单选行或文本单元、并且展开了“外观来源”时读取；同一目标换修订时保留上一次的结果。 */
export interface ResolvedState {
  key: string;
  revision: Revision;
  status: 'loading' | 'ready' | 'error';
  response?: ProjectResolvedStyleResponse;
  error?: string;
}

/** 工程里的全部分配，按工程修订读取；同一修订重读时保留上一次的结果。 */
export interface ApplicationsState {
  revision: Revision;
  status: 'loading' | 'ready' | 'error';
  index?: ApplicationIndex;
  error?: string;
}

export interface AppearanceState {
  targets: readonly AppearanceTarget[];
  /** Shift 连选的锚点。 */
  anchorKey?: string;
  /** 只在选中一个目标时读取。 */
  inspect?: InspectState;
  /** 检查器的“外观来源”展开着（切换目标时保持）。 */
  sourcesOpen?: boolean;
  /** 外观来源（W7 设计 §2.7）。 */
  resolved?: ResolvedState;
  /** 后端开放分配读取时才有。 */
  applications?: ApplicationsState;
  /** 后端预览能否渲染含模板分配的工程；未知时为 undefined。 */
  presetsPreview?: boolean;
  /** 分配命令（应用、停用、移除、更新）进行中。 */
  applying?: boolean;
  /** 最近一次应用；工程修订仍是应用后的修订时可以一键撤销。 */
  lastApplied?: { templateName: string; targetText: string; revision: Revision };
  /** 模板选择器里正在试看的模板。 */
  trial?: TrialState;
}

/** 试看的一帧：按工程修订、播放头和目标生成，不写工程。 */
export interface TrialState {
  /** 库、模板、模板修订、目标；换了任何一项就是另一次试看。 */
  key: string;
  status: 'loading' | 'ready' | 'error';
  templateName: string;
  targetText: string;
  revision: Revision;
  timeUs: number;
  frame?: Blob;
  sha256?: string;
  error?: string;
}

interface TrialRequest {
  key: string;
  libraryId: TemplateLibraryId;
  libraryRevision: string;
  template: TemplateSummary;
  targets: readonly AppearanceTarget[];
  branchInputs: Record<string, string> | undefined;
  timeUs: number;
  size: PreviewSize;
}

/** 应用的结果：stale 表示模板库在读取之后又改了，后端拒绝了这份快照。 */
export type ApplyResult = 'applied' | 'unchanged' | 'stale' | 'failed';

const STALE_TEXT = '模板库已更新，已重新读取。请确认后再应用。';

export class AppearanceStore {
  readonly state$ = new ObservableValue<AppearanceState>({ targets: [PROJECT_TARGET] });
  private readonly unsubscribe: Unsubscribe;
  private inspectSeq = 0;
  private resolvedSeq = 0;
  private applicationsSeq = 0;
  private trialRequest: TrialRequest | undefined;
  private trialAbort: AbortController | undefined;
  /** 编排结构：片段、节点目标的有效性和名称；还没读到时为 undefined。 */
  private orchestration: OrchestrationModel | undefined;
  private disposed = false;

  constructor(
    private readonly client: MakoClient,
    private readonly session: EditorSession,
    /** 渲染设备偏好：候选试看按它传 renderBackend（渲染设备设计 §2.2）。 */
    private readonly device: RenderBackendRequest = FOLLOW_DEFAULT,
  ) {
    this.unsubscribe = session.state$.subscribe(() => this.sync());
    void client.preview.runtimeInfo().then(
      (info) => this.patch({ presetsPreview: info.enabled && (info.sourceProfiles ?? []).includes(PRESETS_PREVIEW_PROFILE) }),
      () => undefined,
    );
    this.sync();
  }

  get state(): AppearanceState {
    return this.state$.get();
  }

  /** 编排结构（外观页从编排状态传进来）：读到新修订时去掉已不存在的片段和节点目标。 */
  setOrchestration(model: OrchestrationModel | undefined): void {
    if (model === this.orchestration) return;
    this.orchestration = model;
    this.sync();
  }

  get orchestrationModel(): OrchestrationModel | undefined {
    return this.orchestration;
  }

  /**
   * 换目标。单选行或文本单元时同步“当前行”：时间轴切到它的来源并选中它。
   * 选片段时时间轴切到片段的来源（片段轨显示它）。
   */
  setTargets(targets: readonly AppearanceTarget[], anchorKey?: string): void {
    if (targets.length === 0) return;
    this.patch({ targets, anchorKey: anchorKey ?? targetKey(targets.at(-1)!) });
    const only = targets.length === 1 ? targets[0]! : undefined;
    if (only && (only.kind === 'line' || only.kind === 'token')) {
      this.session.selectBranch(only.branchId);
      this.session.select({ lineId: only.lineId, ...(only.kind === 'token' ? { tokenId: only.tokenId } : {}) });
    } else if (targets[0]?.kind === 'clip') this.session.selectBranch(targets[0].branchId);
    this.sync();
  }

  /** 从样式库返回时恢复原目标（W7 设计 §2.7）：有目标已经不存在时不改选择，返回 false。 */
  restoreTargets(targets: readonly AppearanceTarget[]): boolean {
    const { linesByBranch, branches, branchId } = this.session.state;
    const branchIds = branches?.map((b) => b.id) ?? [branchId];
    if (targets.length === 0 || reconcileTargets(targets, branchIds, linesByBranch, this.orchestration) !== targets) return false;
    this.setTargets(targets);
    return true;
  }

  /** 节点目标的应用范围（检查器的“包括里面的句子”）：只改下一次试看和应用，不改已有的分配。 */
  setIncludeDescendants(includeDescendants: boolean): void {
    const { targets } = this.state;
    const only = targets.length === 1 ? targets[0] : undefined;
    if (only?.kind !== 'node' || only.includeDescendants === includeDescendants) return;
    this.patch({ targets: [{ ...only, includeDescendants }] });
  }

  /** 进入页面时：目标是行或文本单元、而“当前行”在别处被换掉了，就改选当前行。 */
  followCurrentLine(): void {
    const { targets } = this.state;
    const selection = this.session.state.selection;
    const kind = targets[0]?.kind;
    if (!selection || (kind !== 'line' && kind !== 'token')) return;
    if (targets.some((t) => (t.kind === 'line' || t.kind === 'token') && t.lineId === selection.lineId)) return;
    const line = this.session.state.lines.find((l) => l.id === selection.lineId);
    if (line) this.patch({ targets: [{ kind: 'line', branchId: line.branchId, lineId: line.id }], anchorKey: undefined });
    this.sync();
  }

  /**
   * 把模板应用到当前全部目标：一条 presetApplications.apply，整批成功或整批拒绝，一条历史（W3 设计 §2.6）。
   * libraryRevision 是读到这个模板时的库修订；后端按它固定模板快照，对不上时 404（字段 command.templateRevision）。
   * branchInputs 要恰好覆盖模板声明的来源输入名（W7 设计 §2.7）；模板没有输入时省略。
   */
  async apply(libraryId: TemplateLibraryId, libraryRevision: string, template: TemplateSummary, branchInputs?: Record<string, string>): Promise<ApplyResult> {
    const targets = this.state.targets;
    const result = await this.run({
      type: 'presetApplications.apply',
      templateLibraryId: libraryId,
      templateId: template.templateId,
      templateRevision: libraryRevision,
      targets: commandTargets(targets),
      ...(branchInputs ? { branchInputs } : {}),
    });
    if (result.status === 'applied') this.patch({ lastApplied: { templateName: template.name, targetText: targetCountText(targets, this.orchestration), revision: result.revision } });
    return result.status;
  }

  /**
   * 用库里当前的版本更新一个分配（同一模板身份，保留分配 ID 与启用状态）。不带 branchInputs：后端沿用原分配的来源输入映射。
   * 内容相同时后端不产生修订和历史。库修订对不上时与 apply 相同，返回 stale。
   */
  async update(application: PresetApplication, libraryId: TemplateLibraryId, libraryRevision: string): Promise<ApplyResult> {
    const result = await this.run({
      type: 'presetApplications.update',
      applicationId: application.applicationId,
      templateLibraryId: libraryId,
      templateId: application.snapshot.templateId,
      templateRevision: libraryRevision,
    });
    if (result.status === 'unchanged') this.session.notify({ level: 'info', message: `“${application.snapshot.name}”已经是库里的当前版本。` });
    return result.status;
  }

  /** 停用或重新启用一个分配；停用的分配留在工程里，不起作用。 */
  async setEnabled(application: PresetApplication, enabled: boolean): Promise<boolean> {
    return (await this.run({ type: 'presetApplications.setEnabled', applicationId: application.applicationId, enabled })).status !== 'failed';
  }

  /** 一次移除多个分配（最多 1000 个），一条历史；失效目标上的分配也能移除。 */
  async remove(applicationIds: readonly string[]): Promise<boolean> {
    if (applicationIds.length === 0) return true;
    return (await this.run({ type: 'presetApplications.remove', applicationIds: [...applicationIds] })).status !== 'failed';
  }

  /** 直接设置前奏提示组的完整双态外观（与歌词样式解耦，不走模板分配）。 */
  async setCountdownAppearance(containerId: string, appearance: SceneTextAppearance<StaticMaterial>): Promise<ApplyResult> {
    return (await this.run({ type: 'orchestration.setCountdownAppearance', containerId, appearance })).status;
  }

  /** 按当前目标歌词行（或所在来源）的解析样式，一键同步前奏提示组的双态配色与字体（保留原有字号与字距）。 */
  async syncCountdownWithLyricStyle(containerId: string): Promise<ApplyResult> {
    const node = this.orchestration?.byId.get(containerId);
    const current = node?.content?.appearance ?? null;
    const firstUse = this.orchestration ? leadInsUsing(this.orchestration, containerId)[0] : undefined;
    const state = this.session.state;
    const branchId = firstUse?.program.branchId ?? state.branchId;
    const lineId =
      firstUse?.leadIn.targetLineId ??
      (branchId === state.branchId ? state.selection?.lineId : undefined) ??
      state.linesByBranch[branchId]?.[0]?.id;
    const revision = this.session.latestRevision;
    let style: CanonicalStyle | undefined;
    this.patch({ applying: true });
    try {
      if (this.canReadSources && branchId && lineId && this.client.projects?.resolvedStyle) {
        try {
          const resolved = await this.client.projects.resolvedStyle(this.session.projectSessionId, revision, { branchId, lineId });
          style = resolved.style;
        } catch {
          // 读取单句解析样式失败时回退到静态层级样式。
        }
      }
      if (!style && this.client.projects?.style) {
        const response = await this.client.projects.style(
          this.session.projectSessionId,
          branchId ? { kind: 'branch', branchId } : PROJECT_TARGET,
          revision,
        );
        style = response.staticHierarchy;
      }
      if (!style) {
        this.session.notify({ level: 'error', message: '无法读取当前歌词样式，不能同步配色。' });
        return 'failed';
      }
    } catch (error) {
      this.session.notify({
        level: 'error',
        message: `读取当前歌词样式失败：${error instanceof Error ? error.message : String(error)}`,
      });
      return 'failed';
    } finally {
      this.patch({ applying: undefined });
    }
    return await this.setCountdownAppearance(containerId, lyricStyleToCountdownAppearance(style, current));
  }

  private async run(command: ProjectCommand): Promise<{ status: 'applied'; revision: Revision } | { status: Exclude<ApplyResult, 'applied'> }> {
    this.patch({ applying: true });
    try {
      const response = await this.session.command(command);
      if (!response) {
        if (this.session.state.notice?.code !== 'resourceNotFound' || command.type === 'presetApplications.remove' || command.type === 'presetApplications.setEnabled') {
          return { status: 'failed' };
        }
        this.session.notify({ level: 'warning', message: STALE_TEXT, code: 'resourceNotFound' });
        return { status: 'stale' };
      }
      return response.changed ? { status: 'applied', revision: response.projectRevision } : { status: 'unchanged' };
    } finally {
      this.patch({ applying: undefined });
    }
  }

  /** 能否试看：后端开放候选试看，且预览支持含模板分配的工程。 */
  get canTrial(): boolean {
    return this.session.supports(CANDIDATE_PREVIEW) && this.state.presetsPreview === true;
  }

  /**
   * 试看模板（G16）：在当前修订、timeUs 时刻，把模板临时套到当前全部目标上渲染一帧；不写工程、不产生历史。
   * 只保留最新一次：换模板时中止上一次。工程修订变了（含 409）时按新修订重新生成。
   */
  trial(libraryId: TemplateLibraryId, libraryRevision: string, template: TemplateSummary, timeUs: number, size: PreviewSize, branchInputs?: Record<string, string>): void {
    const targets = this.state.targets;
    const key = [libraryId, libraryRevision, template.templateId, template.templateRevision, targetsSignature(targets), JSON.stringify(branchInputs ?? {})].join('|');
    const at = Math.max(0, Math.round(timeUs));
    const current = this.trialRequest;
    if (current && current.key === key && current.timeUs === at) return;
    this.trialRequest = { key, libraryId, libraryRevision, template, targets, branchInputs, timeUs: at, size };
    void this.runTrial();
  }

  /** 结束试看（关闭选择器或应用之后）。 */
  endTrial(): void {
    this.trialRequest = undefined;
    this.trialAbort?.abort();
    this.trialAbort = undefined;
    if (this.state.trial) this.patch({ trial: undefined });
  }

  private async runTrial(): Promise<void> {
    const request = this.trialRequest;
    if (!request || this.disposed) return;
    const revision = this.session.latestRevision;
    const previous = this.state.trial;
    if (previous && previous.key === request.key && previous.revision === revision && previous.timeUs === request.timeUs && previous.status !== 'error') return;
    this.trialAbort?.abort();
    const abort = new AbortController();
    this.trialAbort = abort;
    const base = { key: request.key, templateName: request.template.name, targetText: targetCountText(request.targets, this.orchestration), revision, timeUs: request.timeUs };
    this.patch({ trial: { ...base, status: 'loading' } });
    try {
      const still = await this.client.preview.renderCandidateStill(this.session.projectSessionId, {
        expectedProjectRevision: revision,
        templateLibraryId: request.libraryId,
        templateLibraryRevision: request.libraryRevision,
        templateId: request.template.templateId,
        templateRevision: request.template.templateRevision,
        targets: commandTargets(request.targets),
        ...(request.branchInputs ? { branchInputs: request.branchInputs } : {}),
        size: request.size,
        ...renderBackendField(this.device),
        timeUs: micros(request.timeUs),
        signal: abort.signal,
      });
      if (abort.signal.aborted || this.trialRequest !== request) return;
      this.patch({ trial: { ...base, status: 'ready', frame: still.blob, sha256: still.sha256 } });
    } catch (error) {
      if (abort.signal.aborted || this.trialRequest !== request) return;
      // 修订已过期：工程刷新后 sync 按新修订重来。
      if (error instanceof BackendError && error.code === 'revisionConflict') return;
      this.patch({ trial: { ...base, status: 'error', error: trialErrorText(error) } });
    } finally {
      if (this.trialAbort === abort) this.trialAbort = undefined;
    }
  }

  /** 最近一次应用之后工程没有再变，才能用它的“撤销”。 */
  get canUndoApply(): boolean {
    const last = this.state.lastApplied;
    return !!last && last.revision === this.session.state.summary.projectRevision && this.session.state.summary.canUndo;
  }

  async undoApply(): Promise<void> {
    if (!this.canUndoApply) return;
    this.patch({ lastApplied: undefined });
    await this.session.undo();
  }

  /** 展开或收起“外观来源”：展开时读取当前目标的外观来源。 */
  setSourcesOpen(open: boolean): void {
    if (!!this.state.sourcesOpen === open) return;
    this.patch({ sourcesOpen: open || undefined });
    this.sync();
  }

  /** 重新读取外观来源（例如读取失败后）。 */
  reloadResolved(): void {
    this.patch({ resolved: undefined });
    this.sync();
  }

  get canReadSources(): boolean {
    return this.session.supports(RESOLVED_STYLE_READ);
  }

  /** 重新读取当前目标的样式（例如读取失败后）。 */
  reloadInspect(): void {
    this.patch({ inspect: undefined });
    this.sync();
  }

  dispose(): void {
    this.endTrial();
    this.disposed = true;
    this.unsubscribe();
  }

  /** 重新读取分配（例如读取失败后）。 */
  reloadApplications(): void {
    this.patch({ applications: undefined });
    this.sync();
  }

  /** 工程变化后：去掉失效目标；按最新修订读取分配；单选行、文本单元等时读取样式。 */
  private sync(): void {
    if (this.disposed) return;
    const { linesByBranch, branches, branchId } = this.session.state;
    const branchIds = branches?.map((b) => b.id) ?? [branchId];
    const targets = reconcileTargets(this.state.targets, branchIds, linesByBranch, this.orchestration);
    if (targets !== this.state.targets) this.patch({ targets, anchorKey: undefined });
    // 应用进行中不重试：应用成功后选择器就关了。
    if (this.trialRequest && !this.state.applying && this.state.trial?.revision !== this.session.latestRevision) void this.runTrial();

    if (this.session.supports('project-sessions.preset-applications.read')) {
      const revision = this.session.latestRevision;
      if (this.state.applications?.revision !== revision) void this.readApplications(revision);
    }

    this.syncResolved(targets);

    // 样式读取只接受四类目标；编排对象的直接外观在编排结构里（W7 设计 §2.8）。
    const only = targets.length === 1 && isStyleTarget(targets[0]!) ? targets[0] : undefined;
    if (!only || !this.session.supports('project-sessions.style.read')) {
      if (this.state.inspect) this.patch({ inspect: undefined });
      return;
    }
    const key = targetKey(only);
    const revision = this.session.latestRevision;
    const current = this.state.inspect;
    if (current && current.key === key && current.revision === revision) return;
    void this.inspect(only, key, revision);
  }

  /** 外观来源只读单个行或文本单元（规范 §17.6）；没展开时不读。 */
  private syncResolved(targets: readonly AppearanceTarget[]): void {
    const only = targets.length === 1 ? targets[0] : undefined;
    if (!this.state.sourcesOpen || !only || (only.kind !== 'line' && only.kind !== 'token') || !this.canReadSources) {
      if (this.state.resolved) this.patch({ resolved: undefined });
      return;
    }
    const key = targetKey(only);
    const revision = this.session.latestRevision;
    const current = this.state.resolved;
    if (current && current.key === key && current.revision === revision) return;
    void this.readResolved(only, key, revision);
  }

  private async readResolved(target: Extract<StyleTarget, { kind: 'line' | 'token' }>, key: string, revision: Revision): Promise<void> {
    const seq = ++this.resolvedSeq;
    const previous = this.state.resolved;
    this.patch({ resolved: { key, revision, status: 'loading', ...(previous?.key === key && previous.response ? { response: previous.response } : {}) } });
    try {
      const response = await this.client.projects.resolvedStyle(this.session.projectSessionId, revision, {
        branchId: target.branchId,
        lineId: target.lineId,
        ...(target.kind === 'token' ? { tokenId: target.tokenId } : {}),
      });
      if (seq === this.resolvedSeq) this.patch({ resolved: { key, revision, status: 'ready', response } });
    } catch (error) {
      if (seq !== this.resolvedSeq) return;
      if (error instanceof BackendError && error.code === 'revisionConflict') return;
      const message =
        error instanceof BackendError && error.failureCode === 'TargetStale' ? '这个目标已经不存在。' : `读取外观来源失败：${error instanceof Error ? error.message : String(error)}`;
      this.patch({ resolved: { key, revision, status: 'error', error: message } });
    }
  }

  private async readApplications(revision: Revision): Promise<void> {
    const seq = ++this.applicationsSeq;
    const previous = this.state.applications;
    this.patch({ applications: { revision, status: 'loading', ...(previous?.index ? { index: previous.index } : {}) } });
    try {
      const response = await this.client.projects.presetApplications(this.session.projectSessionId, revision);
      if (seq === this.applicationsSeq) this.patch({ applications: { revision, status: 'ready', index: indexApplications(response.applications) } });
    } catch (error) {
      if (seq !== this.applicationsSeq) return;
      if (error instanceof BackendError && error.code === 'revisionConflict') return;
      const message = `读取模板分配失败：${error instanceof Error ? error.message : String(error)}`;
      this.patch({ applications: { revision, status: 'error', error: message, ...(previous?.index ? { index: previous.index } : {}) } });
    }
  }

  private async inspect(target: StyleTarget, key: string, revision: Revision): Promise<void> {
    const seq = ++this.inspectSeq;
    const previous = this.state.inspect;
    // 同一目标换修订时保留上一次的结果，避免闪烁。
    this.patch({ inspect: { key, revision, status: 'loading', ...(previous?.key === key && previous.response ? { response: previous.response } : {}) } });
    try {
      const response = await this.client.projects.style(this.session.projectSessionId, target, revision);
      if (seq === this.inspectSeq) this.patch({ inspect: { key, revision, status: 'ready', response } });
    } catch (error) {
      if (seq !== this.inspectSeq) return;
      // 修订已过期：工程刷新后 sync 会按新修订重读。
      if (error instanceof BackendError && error.code === 'revisionConflict') return;
      const message =
        error instanceof BackendError && error.failureCode === 'TargetStale' ? '这个目标已经不存在。' : `读取样式失败：${error instanceof Error ? error.message : String(error)}`;
      this.patch({ inspect: { key, revision, status: 'error', error: message } });
    }
  }

  private patch(partial: { [K in keyof AppearanceState]?: AppearanceState[K] | undefined }): void {
    if (this.disposed) return;
    const next: Record<string, unknown> = { ...this.state$.get() };
    for (const [k, v] of Object.entries(partial)) {
      if (v === undefined) delete next[k];
      else next[k] = v;
    }
    this.state$.set(next as unknown as AppearanceState);
  }
}

function trialErrorText(error: unknown): string {
  if (error instanceof BackendError) {
    if (error.code === 'resourceNotFound') return '模板库已更新，请关闭后重新选择。';
    if (error.code === 'commandRejected' || error.code === 'commandNotSupported') return `不能试看：${error.message}`;
  }
  return `试看失败：${error instanceof Error ? error.message : String(error)}`;
}
