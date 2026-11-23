// 编排页状态（W4 设计 §2.4、§9.3、§10）：按最新修订读取编排结构，选中节点、片段、句子与歌词分配条的来源，
// 设置默认容器，快速布局的预演与应用（G18，范围模式 G19），片段的新建、改名、改目标容器、删除（G19-B，§11），
// 结构编辑：新建、复制、改名、移动、排序、删除、槽位增删（G20，§12），属性编辑与画布拖动（G21，§13），
// 前奏提示、提示组与标记、自动生成（G23，§14.2～§14.6），句子焦点、单句展示、本句微调与进入动画（G24，§14.7、§14.8），
// 片段的调整句子、拆分与合并（§15）。
// 不依赖 React。每个工程一个，切换页面时选择保留。

import { BackendError, ObservableValue, type MakoClient, type Unsubscribe } from '../../client';
import type {
  BasicLayoutCommandResult,
  CreateNodeKind,
  LeadInAttachmentMode,
  LeadInGenerationCommandResult,
  LeadInGenerationSettings,
  LineLocalTransform,
  OrchestrationClipMembersCommand,
  OrchestrationLeadIn,
  OrchestrationPropertiesCommand,
  ProjectCommand,
  ProjectCommandResponse,
  ProjectLinePresentationResponse,
  Revision,
} from '../../protocol';
import type { CountdownAnimationSpec, CountdownShapeSpec } from '../../protocol';
import {
  basicLayoutFields,
  buildOrchestration,
  clipFailureText,
  copyName,
  extendLineSelection,
  layoutFailureCodeText,
  layoutFailureText,
  lineRange,
  normalizeLineSelection,
  programOfNode,
  reorderPlan,
  structureFailureText,
  type LayoutPresetInfo,
  type OrchestrationModel,
} from '../model/orchestration';
import { clipMembersFailureText, type ClipMembersOperation } from '../model/orchestration-clip-members';
import {
  leadInFailureText,
  MODERN_COUNTDOWN_ANIMATION,
  MODERN_COUNTDOWN_SHAPE,
  updateLeadInCommand,
  type LeadInPatch,
} from '../model/orchestration-lead-ins';
import { propertyFailureText } from '../model/orchestration-properties';
import type { EditorSession } from '../session/editor-session';

export const ORCHESTRATION_READ = 'project-sessions.orchestration.read';
export const LINE_PRESENTATION_READ = 'project-sessions.orchestration.line-presentation.read';

export interface OrchestrationState {
  /** unsupported：后端没有开放编排读模型。 */
  status: 'unsupported' | 'loading' | 'ready' | 'error';
  /** 正在读或最近读到的修订。 */
  revision?: Revision;
  /** 读取期间和读取失败时保留上一份。 */
  model?: OrchestrationModel;
  error?: string;
  /** 结构树里选中的节点。 */
  selectedId?: string;
  /** 歌词分配条的来源；编排页的时间轴显示它的行。 */
  branchId: string;
  /** 选中的句子（分配条来源里，按来源顺序）：时间轴高亮，快速布局可以只套这几句（G19）。 */
  lineIds?: string[];
  /** 上次单独点选（或框选起点）的那句：扩展选择从它开始。 */
  lineAnchor?: string;
  /** 选中的片段：这时 lineIds 是它的句子，selectedId 是它的目标容器。 */
  selectedClipId?: string;
  /** 选中的前奏（分配条来源里的）：这时 lineIds 是它的目标句，selectedId 是它的提示组。 */
  selectedLeadInId?: string;
  /** 句子焦点：在时间轴上点选、框选句子，或在画布上双击句子；属性区显示句子（§14.7）。 */
  lineFocus?: boolean;
  /** 歌词分配下面显示的列表：片段或前奏提示，缺省是片段（切换页面时保留）。 */
  panel?: 'clips' | 'leadIns';
  /** 编排命令进行中。 */
  busy?: boolean;
  /** 快速布局对话框里最近一次预演。 */
  layoutPreview?: LayoutPreviewState;
  /** 有句子焦点、只选了一句时，这一句的单句展示（G24）。 */
  linePresentation?: LinePresentationState;
  /** 自动生成对话框里最近一次预演（G23）。 */
  generationPreview?: GenerationPreviewState;
}

/** 单句展示的读取：按（修订，来源，句子）；修订变了重读时保留上一份，换了句子时先清空。 */
export interface LinePresentationState {
  branchId: string;
  lineId: string;
  revision: Revision;
  status: 'loading' | 'ready' | 'error';
  response?: ProjectLinePresentationResponse;
  error?: string;
}

/** 前奏自动生成的预演：不改工程、不产生历史。 */
export interface GenerationPreviewState {
  branchId: string;
  settings: LeadInGenerationSettings;
  revision: Revision;
  status: 'loading' | 'ready' | 'error';
  result?: LeadInGenerationCommandResult;
  error?: string;
}

/** 快速布局的预演：不改工程、不产生历史。 */
export interface LayoutPreviewState {
  branchId: string;
  preset: LayoutPresetInfo['preset'];
  /** 范围模式的句子；整个来源时省略。 */
  lineIds?: string[];
  revision: Revision;
  status: 'loading' | 'ready' | 'error';
  result?: BasicLayoutCommandResult;
  error?: string;
}

export type OrchestrationResult = 'applied' | 'unchanged' | 'failed';

/** 新建片段的输入：连续、有时间的几句，目标容器，可选名称（空白等于不填）。 */
export interface NewClip {
  lineIds: readonly string[];
  containerId: string;
  name?: string;
}

/** 新建前奏的输入（§14.4）：名称空白等于不命名。 */
export interface NewLeadIn {
  targetLineId: string;
  indicatorContainerId: string;
  durationUs: string;
  name?: string;
}

/** 片段成员命令（§15）的结果：失败时带原因，由对话框就地显示。 */
export type ClipMembersResult = { result: 'applied' | 'unchanged' } | { result: 'failed'; error: string };

/** 句子命令：本句微调。 */
export type LineCommand = Extract<
  ProjectCommand,
  { type: 'orchestration.setLineLocalTransform' }
>;

/** 新建节点的输入（G20-B）：名称空白等于不命名；slotCount 只对轮换容器。 */
export interface NewNode {
  kind: CreateNodeKind;
  /** null 是画布根层。 */
  parentId: string | null;
  name: string;
  slotCount?: number;
}

/** 片段命令的失败原因按片段的对照表说明（W4 设计 §11.6）。 */
const CLIP_OPTIONS = { failureText: clipFailureText };
/** 结构命令的失败原因（W4 设计 §12.6）。 */
const STRUCTURE_OPTIONS = { failureText: structureFailureText };
/** 属性命令的失败原因（W4 设计 §13）。 */
const PROPERTY_OPTIONS = { failureText: propertyFailureText };
/** 前奏、附着与自动生成的失败原因（§14.3）。 */
const LEAD_IN_OPTIONS = { failureText: leadInFailureText };
/** 句子命令的失败原因（§14.7）。 */
const LINE_FAILURE_TEXT: Record<string, string> = {
  TargetStale: '这一句已经不在了，请重新选择。',
  PresentationInvalid: '结果没有通过画面结构校验，没有修改。',
};
const LINE_OPTIONS = { failureText: (code: string) => LINE_FAILURE_TEXT[code] };

/** 清空句子、片段、前奏的选择和句子焦点。 */
const NO_LINES = { lineIds: undefined, lineAnchor: undefined, selectedClipId: undefined, selectedLeadInId: undefined, lineFocus: undefined };
/** 选节点、片段、前奏时结束句子焦点。 */
const NO_FOCUS = { selectedClipId: undefined, selectedLeadInId: undefined, lineFocus: undefined };

export class OrchestrationStore {
  readonly state$: ObservableValue<OrchestrationState>;
  private readonly unsubscribe: Unsubscribe;
  private seq = 0;
  private previewSeq = 0;
  private generationSeq = 0;
  private presentationSeq = 0;
  /** 快速布局应用或新建片段后，读到这个修订时选中新的默认容器（整个来源）、包含 firstLineId 的片段（范围）或新片段 clipId。 */
  private follow: { branchId: string; revision: Revision; firstLineId?: string; clipId?: string } | undefined;
  /** 新建节点、前奏等以后，读到这个修订时再选中新对象。 */
  private afterRead: { revision: Revision; run: (model: OrchestrationModel) => void } | undefined;
  private readonly randomId: () => string;
  private disposed = false;

  constructor(
    private readonly client: MakoClient,
    private readonly session: EditorSession,
    options?: { randomId?: () => string },
  ) {
    this.randomId = options?.randomId ?? (() => crypto.randomUUID());
    const supported = session.supports(ORCHESTRATION_READ);
    this.state$ = new ObservableValue<OrchestrationState>({ status: supported ? 'loading' : 'unsupported', branchId: session.state.branchId });
    this.unsubscribe = supported ? session.state$.subscribe(() => this.sync()) : () => undefined;
    this.sync();
  }

  get state(): OrchestrationState {
    return this.state$.get();
  }

  /**
   * 选中节点，取消片段与前奏的选中、结束句子焦点；歌词分配条的来源跟着换到节点所在的来源（其他节点不换），
   * 换了来源时清空选中的句子。
   */
  select(id: string): void {
    const program = this.state.model ? programOfNode(this.state.model, id) : undefined;
    const other = !!program && program.branchId !== this.state.branchId;
    this.patch({ selectedId: id, ...NO_FOCUS, ...(other ? { branchId: program.branchId, ...NO_LINES } : {}) });
  }

  /** 换歌词分配条的来源；选中节点跟着换到它的默认容器，清空选中的句子和片段。 */
  setBranch(branchId: string): void {
    const program = this.state.model?.programByBranch.get(branchId);
    this.patch({ branchId, ...(branchId !== this.state.branchId ? NO_LINES : {}), ...(program ? { selectedId: program.defaultContainerId } : {}) });
  }

  /** 在时间轴上点一句；extend 时从上次点的那句扩展到这一句（含中间的句子）。取得句子焦点，取消片段与前奏的选中。 */
  clickLine(lineId: string, extend: boolean): void {
    const lines = this.session.linesOf(this.state.branchId);
    const anchor = extend ? this.state.lineAnchor : undefined;
    const ids = extend ? extendLineSelection(lines, anchor, lineId) : lineRange(lines, [lineId])?.lineIds ?? [];
    if (ids.length === 0) return;
    this.patch({ ...NO_FOCUS, lineIds: ids, lineAnchor: anchor !== undefined && ids.length > 1 ? anchor : lineId, lineFocus: true });
  }

  /** 时间轴框选：与拖过的时间相交的句子；additive 时加到已选的句子里。取得句子焦点，取消片段与前奏的选中。 */
  selectLines(lineIds: readonly string[], additive: boolean): void {
    const lines = this.session.linesOf(this.state.branchId);
    const ids = normalizeLineSelection(lines, additive ? [...(this.state.lineIds ?? []), ...lineIds] : lineIds);
    if (ids.length === 0) return;
    this.patch({ ...NO_FOCUS, lineIds: ids, lineAnchor: additive && this.state.lineAnchor ? this.state.lineAnchor : ids[0], lineFocus: true });
  }

  /** 在画布上双击句子（§14.8）：只选这一句并取得句子焦点；句子属于别的来源时分配条换过去。 */
  focusLine(branchId: string, lineId: string): void {
    if (!this.session.linesOf(branchId).some((l) => l.id === lineId)) return;
    this.patch({ ...NO_LINES, branchId, lineIds: [lineId], lineAnchor: lineId, lineFocus: true });
  }

  /** 歌词分配下面改显示片段列表或前奏列表。 */
  showPanel(panel: 'clips' | 'leadIns'): void {
    this.patch({ panel });
  }

  /** 清空选中的句子、片段与前奏。 */
  clearLines(): void {
    this.patch(NO_LINES);
  }

  /** 选中分配条来源的一个片段：时间轴高亮它的句子，结构树选中它的目标容器。 */
  selectClip(clipId: string): void {
    const clip = this.state.model?.programByBranch.get(this.state.branchId)?.clips?.find((c) => c.id === clipId);
    if (!clip) return;
    const ids = lineRange(this.session.linesOf(this.state.branchId), clip.lineIds)?.lineIds;
    this.patch({ ...NO_FOCUS, selectedClipId: clip.id, selectedId: clip.containerId, lineIds: ids, lineAnchor: ids?.[0] });
  }

  /** 选中分配条来源的一个前奏（§14.2）：时间轴高亮它的目标句，结构树选中它的提示组。 */
  selectLeadIn(leadInId: string): void {
    const leadIn = this.leadIn(this.state.branchId, leadInId);
    if (!leadIn) return;
    const ids = lineRange(this.session.linesOf(this.state.branchId), [leadIn.targetLineId])?.lineIds;
    this.patch({ ...NO_FOCUS, selectedLeadInId: leadIn.id, selectedId: leadIn.indicatorContainerId, lineIds: ids, lineAnchor: ids?.[0] });
  }

  /** 改来源的默认容器：一条历史；与当前相同时后端是 no-op。 */
  async setDefaultContainer(branchId: string, containerId: string): Promise<OrchestrationResult> {
    this.patch({ busy: true });
    try {
      const response = await this.session.command({ type: 'orchestration.setDefaultContainer', branchId, containerId });
      if (!response) return 'failed';
      return response.changed ? 'applied' : 'unchanged';
    } finally {
      this.patch({ busy: undefined });
    }
  }

  /**
   * 快速布局预演（G18）：直接经客户端发送，不经过会话，所以不会显示“没有变化”。只保留最新一次。
   * 给出 lineIds 时只预演这几句（范围模式，G19）。失败时按 failureCode 写明原因。
   */
  async previewLayout(branchId: string, preset: LayoutPresetInfo, lineIds?: readonly string[]): Promise<void> {
    const seq = ++this.previewSeq;
    const revision = this.session.latestRevision;
    const base = { branchId, preset: preset.preset, revision, ...(lineIds?.length ? { lineIds: [...lineIds] } : {}) };
    this.patch({ layoutPreview: { ...base, status: 'loading' } });
    try {
      const response = await this.client.projects.command(this.session.projectSessionId, {
        type: 'orchestration.previewBasicLayout',
        ...basicLayoutFields(branchId, preset, lineIds),
      });
      if (seq !== this.previewSeq) return;
      if (!response.basicLayoutResult) throw new Error('预演回执缺少 basicLayoutResult');
      this.patch({ layoutPreview: { ...base, status: 'ready', result: response.basicLayoutResult } });
    } catch (error) {
      if (seq !== this.previewSeq) return;
      const text = error instanceof BackendError ? layoutFailureText(error.failureCode, error.message, preset.label) : `预演失败：${error instanceof Error ? error.message : String(error)}`;
      this.patch({ layoutPreview: { ...base, status: 'error', error: text } });
    }
  }

  /** 关闭快速布局对话框。 */
  endLayoutPreview(): void {
    this.previewSeq++;
    this.patch({ layoutPreview: undefined });
  }

  /**
   * 快速布局应用（G18）：一条历史；与当前相同时后端是 no-op。成功后结构树选中来源的新默认容器；
   * 范围模式（G19）选中包含第一句的新片段和它的目标容器。
   */
  async applyLayout(branchId: string, preset: LayoutPresetInfo, lineIds?: readonly string[]): Promise<OrchestrationResult> {
    this.patch({ busy: true });
    try {
      const response = await this.session.command(
        { type: 'orchestration.applyBasicLayout', ...basicLayoutFields(branchId, preset, lineIds) },
        { failureText: (code) => layoutFailureCodeText(code, preset.label) },
      );
      if (!response) return 'failed';
      if (!response.changed) return 'unchanged';
      this.follow = { branchId, revision: response.projectRevision, ...(lineIds?.length ? { firstLineId: lineIds[0]! } : {}) };
      this.followDefault(this.state.model);
      return 'applied';
    } finally {
      this.patch({ busy: undefined });
    }
  }

  /**
   * 新建片段（G19-B）：一条历史。成功后读到新修订时选中新片段和它的目标容器。
   * 失败时返回原因（已按片段的对照表说明），由对话框就地显示。
   */
  async createClip(branchId: string, clip: NewClip): Promise<{ result: 'applied' } | { result: 'failed'; error: string }> {
    const clipId = `clip/${this.randomId()}`;
    const name = clip.name?.trim();
    this.patch({ busy: true });
    try {
      const response = await this.session.command(
        { type: 'orchestration.createClip', branchId, clipId, lineIds: [...clip.lineIds], containerId: clip.containerId, ...(name ? { name } : {}) },
        CLIP_OPTIONS,
      );
      if (!response) return { result: 'failed', error: this.session.state.notice?.message ?? '新建片段没有成功。' };
      this.follow = { branchId, revision: response.projectRevision, clipId: response.clipResult?.clipId ?? clipId };
      this.followDefault(this.state.model);
      return { result: 'applied' };
    } finally {
      this.patch({ busy: undefined });
    }
  }

  /** 片段改名：一条历史；空白表示清除名称。与原名相同时后端是 no-op。 */
  renameClip(branchId: string, clipId: string, name: string): Promise<OrchestrationResult> {
    const trimmed = name.trim();
    return this.clipCommand({ type: 'orchestration.renameClip', branchId, clipId, name: trimmed || null });
  }

  /** 改片段的目标容器：一条历史；成功后结构树选中新的目标容器。 */
  async setClipContainer(branchId: string, clipId: string, containerId: string): Promise<OrchestrationResult> {
    const result = await this.clipCommand({ type: 'orchestration.setClipContainer', branchId, clipId, containerId });
    if (result === 'applied' && this.state.selectedClipId === clipId) this.patch({ selectedId: containerId });
    return result;
  }

  /** 删除片段：一条历史，几句回到默认容器。成功后取消片段选中，选中的句子保留，结构树选中默认容器。 */
  async deleteClip(branchId: string, clipId: string): Promise<OrchestrationResult> {
    const result = await this.clipCommand({ type: 'orchestration.deleteClip', branchId, clipId });
    if (result === 'applied' && this.state.selectedClipId === clipId) {
      const program = this.state.model?.programByBranch.get(branchId);
      this.patch({ selectedClipId: undefined, ...(program ? { selectedId: program.defaultContainerId } : {}) });
    }
    return result;
  }

  /**
   * 调整片段的句子（§15.3）：一条历史，片段的身份、名称、目标容器不变；与现在相同时后端是 no-op。
   * 成功后读到新修订时选中片段的新句子。失败时返回原因，由对话框就地显示。
   */
  setClipMembers(branchId: string, clipId: string, lineIds: readonly string[]): Promise<ClipMembersResult> {
    return this.clipMembersCommand({ type: 'orchestration.setClipMembers', branchId, clipId, lineIds: [...lineIds] }, clipId);
  }

  /** 拆分片段（§15.4）：一条历史，从 splitBeforeLineId 开始成为新片段。成功后读到新修订时选中新片段。 */
  splitClip(branchId: string, clipId: string, splitBeforeLineId: string): Promise<ClipMembersResult> {
    const newClipId = `clip/${this.randomId()}`;
    return this.clipMembersCommand({ type: 'orchestration.splitClip', branchId, clipId, splitBeforeLineId, newClipId }, newClipId);
  }

  /** 合并两个相邻片段（§15.5）：一条历史，保留 targetClipId 的身份、名称与目标容器，选中的仍是它。 */
  mergeClips(branchId: string, targetClipId: string, otherClipId: string): Promise<ClipMembersResult> {
    return this.clipMembersCommand({ type: 'orchestration.mergeClips', branchId, clipIds: [targetClipId, otherClipId], targetClipId }, targetClipId);
  }

  /**
   * 新建节点（G20-B）：一条历史。成功后读到新修订时选中新节点。失败时返回原因（结构的对照表），由对话框就地显示。
   */
  async createNode(node: NewNode): Promise<{ result: 'applied'; nodeId: string } | { result: 'failed'; error: string }> {
    const name = node.name.trim();
    const response = await this.structureCommand({
      type: 'orchestration.createNode',
      kind: node.kind,
      parentId: node.parentId,
      name: name || null,
      slotCount: node.kind === 'slotSequence' ? (node.slotCount ?? 2) : null,
    });
    const nodeId = response?.structureResult?.createdNodeIds?.[0] ?? response?.structureResult?.nodeId;
    if (!response || !nodeId) return { result: 'failed', error: this.session.state.notice?.message ?? '新建没有成功。' };
    this.selectAfter(response.projectRevision, nodeId);
    return { result: 'applied', nodeId };
  }

  /** 同父复制整棵子树（G20-B）：副本名称“原名 副本”。成功后选中副本。 */
  async copyNode(id: string): Promise<OrchestrationResult> {
    const node = this.state.model?.byId.get(id);
    if (!node || !this.state.model) return 'failed';
    const response = await this.structureCommand({ type: 'orchestration.copySubtree', nodeId: id, name: copyName(this.state.model, node) });
    const nodeId = response?.structureResult?.createdNodeIds?.[0];
    if (!response) return 'failed';
    if (nodeId) this.selectAfter(response.projectRevision, nodeId);
    return response.changed ? 'applied' : 'unchanged';
  }

  /**
   * 新建提示组（G23，§14.4、§14.6）：在普通组 parentId 下新建有 count 个标记的倒计时容器，一条历史。
   * select 时读到新修订后在结构树里选中它。失败时返回原因（结构的对照表）。
   */
  async createCountdown(
    parentId: string,
    name: string,
    count: number,
    options?: { select?: boolean; modernDefaults?: boolean },
  ): Promise<{ result: 'applied'; containerId: string } | { result: 'failed'; error: string }> {
    const response = await this.structureCommand({ type: 'orchestration.createCountdown', parentId, name: name.trim() || null, count });
    const containerId = response?.leadInResult?.indicatorContainerId;
    if (!response || !containerId) return { result: 'failed', error: this.session.state.notice?.message ?? '新建提示组没有成功。' };
    let revision = response.projectRevision;
    if (options?.modernDefaults) {
      const presentation = await this.structureCommand(
        {
          type: 'orchestration.setCountdownPresentation',
          containerId,
          shape: MODERN_COUNTDOWN_SHAPE,
          animation: MODERN_COUNTDOWN_ANIMATION,
        },
        LEAD_IN_OPTIONS,
      );
      if (presentation) revision = presentation.projectRevision;
    }
    if (options?.select) this.selectAfter(revision, containerId);
    return { result: 'applied', containerId };
  }

  /**
   * 添加前奏（G23，§14.4）：ID 是 lead-in/ 加随机 UUID，一条历史。成功后读到新修订时选中新前奏。
   * 失败时返回原因（前奏的对照表），由对话框就地显示。
   */
  async createLeadIn(branchId: string, leadIn: NewLeadIn): Promise<{ result: 'applied'; leadInId: string } | { result: 'failed'; error: string }> {
    const leadInId = `lead-in/${this.randomId()}`;
    const response = await this.structureCommand(
      {
        type: 'orchestration.createLeadIn',
        branchId,
        leadInId,
        name: leadIn.name?.trim() || null,
        targetLineId: leadIn.targetLineId,
        indicatorContainerId: leadIn.indicatorContainerId,
        durationUs: leadIn.durationUs,
        enabled: true,
      },
      LEAD_IN_OPTIONS,
    );
    if (!response) return { result: 'failed', error: this.session.state.notice?.message ?? '添加前奏没有成功。' };
    const id = response.leadInResult?.leadInId ?? leadInId;
    this.whenRead(response.projectRevision, () => {
      if (this.state.branchId !== branchId) this.patch({ branchId, ...NO_LINES });
      this.selectLeadIn(id);
    });
    return { result: 'applied', leadInId: id };
  }

  /** 修改前奏（§14.3）：完整字段，只换改动的项；值没变时后端是 no-op。 */
  async updateLeadIn(branchId: string, leadIn: OrchestrationLeadIn, patch: LeadInPatch): Promise<OrchestrationResult> {
    return this.outcome(await this.structureCommand(updateLeadInCommand(branchId, leadIn, patch), LEAD_IN_OPTIONS));
  }

  /** 删除前奏，提示组保留；exclude 时目标句写进自动生成的排除列表。成功后取消前奏选中，选中的句子保留。 */
  async deleteLeadIn(branchId: string, leadInId: string, exclude = false): Promise<OrchestrationResult> {
    const type = exclude ? 'orchestration.deleteLeadInAndExclude' : 'orchestration.deleteLeadIn';
    const result = this.outcome(await this.structureCommand({ type, branchId, leadInId }, LEAD_IN_OPTIONS));
    if (result === 'applied' && this.state.selectedLeadInId === leadInId) this.patch({ selectedLeadInId: undefined });
    return result;
  }

  /** 改提示组的附着方式（§14.3）：带一个用这个提示组的前奏；同方式是 no-op。 */
  async setLeadInAttachment(branchId: string, leadInId: string, containerId: string, mode: LeadInAttachmentMode): Promise<OrchestrationResult> {
    return this.outcome(await this.structureCommand({ type: 'orchestration.setLeadInAttachment', branchId, leadInId, containerId, mode }, LEAD_IN_OPTIONS));
  }

  /** 设置提示组的图形形状与动画方式（1.54.0）：至少提供 shape 或 animation 其一，缺省一侧传 null 保持不变。 */
  async setCountdownPresentation(
    containerId: string,
    presentation: { shape?: CountdownShapeSpec | null; animation?: CountdownAnimationSpec | null },
  ): Promise<OrchestrationResult> {
    return this.outcome(
      await this.structureCommand(
        {
          type: 'orchestration.setCountdownPresentation',
          containerId,
          shape: presentation.shape ?? null,
          animation: presentation.animation ?? null,
        },
        LEAD_IN_OPTIONS,
      ),
    );
  }

  /** 给提示组加一个标记（§14.6）：一条历史。 */
  async addMark(containerId: string, text: string): Promise<OrchestrationResult> {
    return this.outcome(await this.structureCommand({ type: 'orchestration.addMark', containerId, text }));
  }

  /** 改标记的字：一条历史；同字是 no-op。 */
  async setMarkText(nodeId: string, text: string): Promise<OrchestrationResult> {
    return this.outcome(await this.structureCommand({ type: 'orchestration.setMarkText', nodeId, text }));
  }

  /** 均匀排列提示组的标记：已经均匀时后端是 no-op。 */
  async spaceMarks(containerId: string): Promise<OrchestrationResult> {
    return this.outcome(await this.structureCommand({ type: 'orchestration.spaceMarks', containerId }));
  }

  /**
   * 自动生成的预演（§14.5）：直接经客户端发送，不经过会话，不改工程、不产生历史。只保留最新一次。
   */
  async previewGeneration(branchId: string, settings: LeadInGenerationSettings): Promise<void> {
    const seq = ++this.generationSeq;
    const revision = this.session.latestRevision;
    const base = { branchId, settings, revision };
    this.patch({ generationPreview: { ...base, status: 'loading', ...(this.state.generationPreview?.result ? { result: this.state.generationPreview.result } : {}) } });
    try {
      const response = await this.client.projects.command(this.session.projectSessionId, { type: 'orchestration.previewLeadInGeneration', branchId, settings });
      if (seq !== this.generationSeq) return;
      if (!response.leadInGenerationResult) throw new Error('预演回执缺少 leadInGenerationResult');
      this.patch({ generationPreview: { ...base, status: 'ready', result: response.leadInGenerationResult } });
    } catch (error) {
      if (seq !== this.generationSeq) return;
      const reason = error instanceof BackendError ? (error.failureCode && leadInFailureText(error.failureCode)) || error.message : error instanceof Error ? error.message : String(error);
      this.patch({ generationPreview: { ...base, status: 'error', error: `预演失败：${reason}` } });
    }
  }

  /** 关闭自动生成对话框。 */
  endGenerationPreview(): void {
    this.generationSeq++;
    this.patch({ generationPreview: undefined });
  }

  /**
   * 应用自动生成（§14.5）：只应用 selectedLineIds 里的目标句，设置一并保存，一条历史。成功后展开前奏列表。
   * 失败时返回原因（前奏的对照表），由对话框就地显示。
   */
  async applyGeneration(
    branchId: string,
    settings: LeadInGenerationSettings,
    selectedLineIds: readonly string[],
  ): Promise<{ result: 'applied' | 'unchanged' } | { result: 'failed'; error: string }> {
    const response = await this.structureCommand(
      { type: 'orchestration.applyLeadInGeneration', branchId, settings, selectedLineIds: [...selectedLineIds] },
      LEAD_IN_OPTIONS,
    );
    if (!response) return { result: 'failed', error: this.session.state.notice?.message ?? '自动生成没有成功。' };
    if (!response.changed) return { result: 'unchanged' };
    this.patch({ panel: 'leadIns' });
    return { result: 'applied' };
  }

  /** 句子命令（§14.7、§14.8）：本句微调。一条历史；值没变时后端是 no-op。 */
  async setLine(command: LineCommand): Promise<OrchestrationResult> {
    return this.outcome(await this.structureCommand(command, LINE_OPTIONS));
  }

  /** 本句微调（null 清除）。 */
  setLineTransform(branchId: string, lineId: string, transform: LineLocalTransform | null): Promise<OrchestrationResult> {
    return this.setLine({ type: 'orchestration.setLineLocalTransform', branchId, lineId, transform });
  }

  /** 改名：空白表示清除名称；与原名相同时后端是 no-op。 */
  async renameNode(id: string, name: string): Promise<OrchestrationResult> {
    return this.outcome(await this.structureCommand({ type: 'orchestration.setNode', nodeId: id, name: name.trim() || null }));
  }

  /** 改父级：parentId 为 null 是画布根层；后端保持世界位置。选中保持不变。 */
  async moveNode(id: string, parentId: string | null): Promise<OrchestrationResult> {
    return this.outcome(await this.structureCommand({ type: 'orchestration.reparent', nodeId: id, parentId }));
  }

  /** 上移（-1）或下移（1）：槽位按轮换顺序，其他节点在同级里。不能移动时不发命令。 */
  async reorderNode(id: string, delta: -1 | 1): Promise<OrchestrationResult> {
    const plan = this.state.model ? reorderPlan(this.state.model, id, delta) : undefined;
    if (!plan || 'block' in plan) return 'failed';
    return this.outcome(await this.structureCommand(plan.command));
  }

  /** 删除节点及子树：一条历史。成功后选中原来的父级（没有父级时选中分配条来源的默认容器）。 */
  async removeNode(id: string): Promise<OrchestrationResult> {
    const parentId = this.state.model?.byId.get(id)?.parentId ?? undefined;
    const result = this.outcome(await this.structureCommand({ type: 'orchestration.removeNode', nodeId: id }));
    if (result === 'applied' && this.state.selectedId === id) {
      const fallback = this.state.model?.programByBranch.get(this.state.branchId)?.defaultContainerId;
      this.patch({ selectedId: parentId ?? fallback });
    }
    return result;
  }

  /** 给轮换容器追加一个空槽位：ID 是 slot/ 加随机 UUID，不命名（显示为“槽位 N”）。 */
  async addSlot(containerId: string): Promise<OrchestrationResult> {
    return this.outcome(await this.structureCommand({ type: 'orchestration.addSlot', containerId, slotId: `slot/${this.randomId()}`, name: null }));
  }

  /**
   * 属性命令（G21，W4 设计 §13.2～§13.5，画布拖动 §13.7）：位置与参考框、布局类型、布局设置、默认位置、槽位动画、动画策略。
   * 一条历史；值没变时后端是 no-op。命令由 orchestration-properties 组装。
   */
  async setProperties(command: OrchestrationPropertiesCommand): Promise<OrchestrationResult> {
    return this.outcome(await this.structureCommand(command, PROPERTY_OPTIONS));
  }

  /** 读取失败后重试。 */
  reload(): void {
    this.patch({ revision: undefined });
    this.sync();
  }

  dispose(): void {
    this.disposed = true;
    this.unsubscribe();
  }

  private async clipCommand(command: Parameters<EditorSession['command']>[0]): Promise<OrchestrationResult> {
    this.patch({ busy: true });
    try {
      const response = await this.session.command(command, CLIP_OPTIONS);
      if (!response) return 'failed';
      return response.changed ? 'applied' : 'unchanged';
    } finally {
      this.patch({ busy: undefined });
    }
  }

  private async clipMembersCommand(command: OrchestrationClipMembersCommand, selectClipId: string): Promise<ClipMembersResult> {
    this.patch({ busy: true });
    try {
      const operation = command.type.slice('orchestration.'.length) as ClipMembersOperation;
      const response = await this.session.command(command, { failureText: clipMembersFailureText(operation) });
      if (!response) return { result: 'failed', error: this.session.state.notice?.message ?? '片段没有修改成功。' };
      if (!response.changed) return { result: 'unchanged' };
      this.follow = { branchId: command.branchId, revision: response.projectRevision, clipId: selectClipId };
      this.followDefault(this.state.model);
      return { result: 'applied' };
    } finally {
      this.patch({ busy: undefined });
    }
  }

  private async structureCommand(command: ProjectCommand, options = STRUCTURE_OPTIONS): Promise<ProjectCommandResponse | undefined> {
    this.patch({ busy: true });
    try {
      return await this.session.command(command, options);
    } finally {
      this.patch({ busy: undefined });
    }
  }

  private outcome(response: ProjectCommandResponse | undefined): OrchestrationResult {
    if (!response) return 'failed';
    return response.changed ? 'applied' : 'unchanged';
  }

  /** 读到 revision 时选中 nodeId；已经读到了就立即选中。 */
  private selectAfter(revision: Revision, nodeId: string): void {
    this.whenRead(revision, (model) => {
      if (model.byId.has(nodeId)) this.select(nodeId);
    });
  }

  /** 读到 revision 的读模型后执行 run（只留最近一个）；已经读到了就立即执行。 */
  private whenRead(revision: Revision, run: (model: OrchestrationModel) => void): void {
    this.afterRead = { revision, run };
    this.runAfterRead(this.state.model);
  }

  private runAfterRead(model: OrchestrationModel | undefined): void {
    const pending = this.afterRead;
    if (!pending || !model || model.revision !== pending.revision) return;
    this.afterRead = undefined;
    pending.run(model);
  }

  private leadIn(branchId: string, leadInId: string): OrchestrationLeadIn | undefined {
    return this.state.model?.programByBranch.get(branchId)?.leadIns?.find((l) => l.id === leadInId);
  }

  /**
   * 有句子焦点、只选了一句时，按（修订，来源，句子）读单句展示（§14.7）。修订变了重读时保留上一份；
   * 换了句子时先清空；409 时等下一个修订。没有焦点或后端没开放时清掉。
   */
  private refreshPresentation(): void {
    const { lineFocus, lineIds, branchId, linePresentation: current } = this.state;
    const lineId = lineFocus && lineIds?.length === 1 ? lineIds[0]! : undefined;
    if (!lineId || this.state.status === 'unsupported' || !this.session.supports(LINE_PRESENTATION_READ)) {
      if (current) {
        this.presentationSeq++;
        this.patch({ linePresentation: undefined });
      }
      return;
    }
    const revision = this.session.latestRevision;
    const same = current?.branchId === branchId && current.lineId === lineId;
    if (same && current.revision === revision) return;
    void this.readPresentation(branchId, lineId, revision, same ? current.response : undefined);
  }

  private async readPresentation(branchId: string, lineId: string, revision: Revision, previous: ProjectLinePresentationResponse | undefined): Promise<void> {
    const seq = ++this.presentationSeq;
    const base = { branchId, lineId, revision };
    this.patch({ linePresentation: { ...base, status: 'loading', ...(previous ? { response: previous } : {}) } });
    try {
      const response = await this.client.projects.linePresentation(this.session.projectSessionId, revision, branchId, lineId);
      if (seq !== this.presentationSeq) return;
      this.patch({ linePresentation: { ...base, status: 'ready', response } });
    } catch (error) {
      if (seq !== this.presentationSeq) return;
      // 修订已过期：工程刷新后按新修订重读。
      if (error instanceof BackendError && error.code === 'revisionConflict') return;
      const reason = error instanceof BackendError && error.failureCode === 'TargetStale' ? LINE_FAILURE_TEXT.TargetStale : error instanceof Error ? error.message : String(error);
      this.patch({ linePresentation: { ...base, status: 'error', ...(previous ? { response: previous } : {}), error: `读取这一句失败：${reason}` } });
    }
  }

  private sync(): void {
    if (this.disposed || this.state.status === 'unsupported') return;
    // 来源被删掉时回到当前来源。
    const branches = this.session.state.branches;
    if (branches && !branches.some((b) => b.id === this.state.branchId)) this.patch({ branchId: this.session.state.branchId, ...NO_LINES });
    // 歌词改动后去掉已经不在的句子；一句也不剩时结束句子焦点。
    const { lineIds } = this.state;
    if (lineIds) {
      const kept = lineRange(this.session.linesOf(this.state.branchId), lineIds)?.lineIds;
      if (kept?.length !== lineIds.length) this.patch({ lineIds: kept, ...(kept ? {} : { lineAnchor: undefined, lineFocus: undefined }) });
    }
    const revision = this.session.latestRevision;
    if (this.state.revision !== revision) void this.read(revision);
    this.refreshPresentation();
  }

  private async read(revision: Revision): Promise<void> {
    const seq = ++this.seq;
    this.patch({ revision, status: 'loading', error: undefined });
    try {
      const response = await this.client.projects.orchestration(this.session.projectSessionId, revision);
      if (seq !== this.seq) return;
      const model = buildOrchestration(response);
      const { selectedId, branchId, selectedClipId, selectedLeadInId } = this.state;
      // 选中的节点不在了（或还没选过）：选当前来源的默认容器。片段、前奏不在了就取消它们的选中，选中的句子保留。
      const keep = selectedId !== undefined && model.byId.has(selectedId);
      const program = model.programByBranch.get(branchId);
      const clipKept = selectedClipId !== undefined && !!program?.clips?.some((c) => c.id === selectedClipId);
      const leadInKept = selectedLeadInId !== undefined && !!program?.leadIns?.some((l) => l.id === selectedLeadInId);
      this.patch({
        status: 'ready',
        model,
        selectedId: keep ? selectedId : program?.defaultContainerId,
        selectedClipId: clipKept ? selectedClipId : undefined,
        selectedLeadInId: leadInKept ? selectedLeadInId : undefined,
      });
      this.followDefault(model);
      this.runAfterRead(model);
    } catch (error) {
      if (seq !== this.seq) return;
      // 修订已过期：工程刷新后按新修订重读。
      if (error instanceof BackendError && error.code === 'revisionConflict') return;
      this.patch({ status: 'error', error: `读取编排结构失败：${error instanceof Error ? error.message : String(error)}` });
    }
  }

  /** 读到快速布局应用或新建片段后的修订时，分配条换到那个来源，选中它的新默认容器，范围模式选中包含第一句的片段，新建时选中新片段。 */
  private followDefault(model: OrchestrationModel | undefined): void {
    const follow = this.follow;
    if (!follow || !model || model.revision !== follow.revision) return;
    this.follow = undefined;
    const program = model.programByBranch.get(follow.branchId);
    if (!program) return;
    if (follow.branchId !== this.state.branchId) this.patch({ branchId: follow.branchId, ...NO_LINES });
    const clip = follow.clipId
      ? program.clips?.find((c) => c.id === follow.clipId)
      : follow.firstLineId
        ? program.clips?.find((c) => c.lineIds.includes(follow.firstLineId!))
        : undefined;
    if (clip) {
      const ids = lineRange(this.session.linesOf(follow.branchId), clip.lineIds)?.lineIds;
      this.patch({ ...NO_FOCUS, selectedClipId: clip.id, selectedId: clip.containerId, ...(follow.clipId ? { lineIds: ids, lineAnchor: ids?.[0] } : {}) });
    } else if (!follow.clipId) this.patch({ ...NO_FOCUS, selectedId: program.defaultContainerId });
  }

  private patch(partial: { [K in keyof OrchestrationState]?: OrchestrationState[K] | undefined }): void {
    if (this.disposed) return;
    const next: Record<string, unknown> = { ...this.state$.get() };
    for (const [k, v] of Object.entries(partial)) {
      if (v === undefined) delete next[k];
      else next[k] = v;
    }
    this.state$.set(next as unknown as OrchestrationState);
    if ('lineFocus' in partial || 'lineIds' in partial || 'branchId' in partial) this.refreshPresentation();
  }
}
