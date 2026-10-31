// 编排结构（W4 设计 §2.3）：把读模型的扁平节点表整理成每个来源一棵树，并给出角色、名称和路径。
// 节点顺序沿用读模型数组的顺序（读模型没有单独的排序字段）；身份只用稳定 ID。

import type {
  BasicLayoutCommandResult,
  BasicLayoutFields,
  BasicLayoutPreset,
  CreateNodeKind,
  LineEnterAnimation,
  LyricBranchTimingMode,
  OrchestrationClip,
  OrchestrationLeadInCommand,
  OrchestrationNode,
  OrchestrationProgram,
  OrchestrationStructureCommand,
  ProjectOrchestrationResponse,
  Revision,
} from '../../protocol';

export type NodeRole = 'group' | 'container' | 'slot' | 'content';

export interface OrchestrationModel {
  revision: Revision;
  byId: ReadonlyMap<string, OrchestrationNode>;
  /** 父 ID → 子节点。 */
  children: ReadonlyMap<string, readonly OrchestrationNode[]>;
  programs: readonly OrchestrationProgram[];
  programByBranch: ReadonlyMap<string, OrchestrationProgram>;
  /** 槽位 ID → 所属容器与序号（从 1 开始，按轮换顺序）。 */
  slotOf: ReadonlyMap<string, { containerId: string; index: number }>;
  /** 前奏标记 ID → 所属提示组与序号（从 1 开始，按标记顺序，G23）。 */
  markOf: ReadonlyMap<string, { containerId: string; index: number }>;
  /** 从任何来源的根都走不到的节点里，最上层的那些（它们的子树一并显示）。 */
  orphans: readonly OrchestrationNode[];
  /** 工程默认的进入动画（G24），null 是关闭。 */
  projectEnterAnimation: LineEnterAnimation | null;
}

export function buildOrchestration(response: ProjectOrchestrationResponse): OrchestrationModel {
  const byId = new Map(response.nodes.map((n) => [n.id, n]));
  const children = new Map<string, OrchestrationNode[]>();
  for (const node of response.nodes) {
    if (!node.parentId) continue;
    children.set(node.parentId, [...(children.get(node.parentId) ?? []), node]);
  }
  const slotOf = new Map<string, { containerId: string; index: number }>();
  for (const node of response.nodes) node.layout?.slotIds.forEach((id, i) => slotOf.set(id, { containerId: node.id, index: i + 1 }));
  const markOf = new Map<string, { containerId: string; index: number }>();
  for (const node of response.nodes) node.content?.markNodeIds?.forEach((id, i) => markOf.set(id, { containerId: node.id, index: i + 1 }));

  // 从各来源的根往下走；父子关系有环时靠 reachable 截断。
  const reachable = new Set<string>();
  const visit = (id: string) => {
    if (reachable.has(id) || !byId.has(id)) return;
    reachable.add(id);
    for (const child of children.get(id) ?? []) visit(child.id);
  };
  for (const program of response.programs) visit(program.rootContainerId);
  // 孤立子树的根：父级缺失的节点；孤立节点之间成环时取环上第一个。
  const orphans: OrchestrationNode[] = [];
  for (const node of response.nodes) {
    if (reachable.has(node.id) || (node.parentId && byId.has(node.parentId))) continue;
    orphans.push(node);
    visit(node.id);
  }
  for (const node of response.nodes) {
    if (reachable.has(node.id)) continue;
    orphans.push(node);
    visit(node.id);
  }

  return {
    revision: response.projectRevision,
    byId,
    children,
    programs: response.programs,
    programByBranch: new Map(response.programs.map((p) => [p.branchId, p])),
    slotOf,
    markOf,
    orphans,
    projectEnterAnimation: response.projectEnterAnimation ?? null,
  };
}

export function nodeRole(model: OrchestrationModel, node: OrchestrationNode): NodeRole {
  if (model.slotOf.has(node.id)) return 'slot';
  if (node.layout) return 'container';
  if (node.contentKind) return 'content';
  return 'group';
}

const LAYOUT_TEXT: Record<string, string> = { Auto: '自动布局', SlotSequence: '轮换' };

/** 角色徽标：组、自动布局、轮换·N、槽位；前奏提示组写“前奏·N”，标记写“标记”，其他内容节点写它的类型；未知的布局类型原样显示。 */
export function roleText(model: OrchestrationModel, node: OrchestrationNode): string {
  switch (nodeRole(model, node)) {
    case 'slot':
      return '槽位';
    case 'content':
      if (node.contentKind === 'countdownSequence') return `前奏·${node.content?.markNodeIds?.length ?? 0}`;
      return node.contentKind === 'textMark' ? '标记' : node.contentKind!;
    case 'group':
      return '组';
    case 'container': {
      const kind = node.layout!.kind;
      return kind === 'SlotSequence' ? `轮换·${node.layout!.slotIds.length}` : (LAYOUT_TEXT[kind] ?? kind);
    }
  }
}

export function layoutText(kind: string): string {
  return LAYOUT_TEXT[kind] ?? kind;
}

/**
 * 名称：有名称就用；没有时按角色命名（来源的根“画面”，默认容器“常规”，槽位“槽位 N”）。
 * 后端给每个来源建的常规容器 ID 固定为“根/regular”，快速布局换了默认容器之后它仍叫“常规”。
 */
export function nodeName(model: OrchestrationModel, node: OrchestrationNode): string {
  if (node.name) return node.name;
  if (model.programs.some((p) => p.rootContainerId === node.id)) return '画面';
  if (model.programs.some((p) => p.defaultContainerId === node.id || `${p.rootContainerId}/regular` === node.id)) return '常规';
  const slot = model.slotOf.get(node.id);
  if (slot) return `槽位 ${slot.index}`;
  const mark = model.markOf.get(node.id);
  if (mark) return `标记 ${mark.index}`;
  return { group: '未命名组', container: '未命名容器', content: '未命名内容', slot: '槽位' }[nodeRole(model, node)];
}

/** 节点所在的来源程序：沿父级走到顶，顶上是哪个程序的根。 */
export function programOfNode(model: OrchestrationModel, id: string): OrchestrationProgram | undefined {
  const top = ancestors(model, id).at(-1);
  return top ? model.programs.find((p) => p.rootContainerId === top.id) : undefined;
}

/** 自己和祖先，从自己到顶。 */
export function ancestors(model: OrchestrationModel, id: string): OrchestrationNode[] {
  const chain: OrchestrationNode[] = [];
  const seen = new Set<string>();
  for (let node = model.byId.get(id); node && !seen.has(node.id); node = node.parentId ? model.byId.get(node.parentId) : undefined) {
    seen.add(node.id);
    chain.push(node);
  }
  return chain;
}

/** 对象路径：“主歌词 / 画面 / 常规”；不属于任何来源的写“其他节点”。 */
export function nodePath(model: OrchestrationModel, id: string, branchName: (branchId: string) => string | undefined): string[] {
  const chain = ancestors(model, id).reverse();
  const program = programOfNode(model, id);
  const head = program ? (branchName(program.branchId) ?? program.branchId) : '其他节点';
  return [head, ...chain.map((n) => nodeName(model, n))];
}

/** 来源根下的全部歌词容器（深度优先，按读模型顺序），供默认容器选择。 */
export function containersOf(model: OrchestrationModel, program: OrchestrationProgram): OrchestrationNode[] {
  const result: OrchestrationNode[] = [];
  const seen = new Set<string>();
  const walk = (node: OrchestrationNode | undefined) => {
    if (!node || seen.has(node.id)) return;
    seen.add(node.id);
    if (node.layout && !model.slotOf.has(node.id)) result.push(node);
    for (const child of model.children.get(node.id) ?? []) walk(child);
  };
  walk(model.byId.get(program.rootContainerId));
  return result;
}

/**
 * 结构树里的子节点顺序：轮换容器的槽位按轮换顺序、提示组的标记按标记顺序排在前面，其余子节点按读模型顺序在后（W4 设计 §12.4）。
 * 命令里的同级清单仍用读模型的原顺序（model.children）。
 */
export function orderedChildren(model: OrchestrationModel, id: string): OrchestrationNode[] {
  const raw = model.children.get(id) ?? [];
  const node = model.byId.get(id);
  const first = node?.layout?.slotIds ?? node?.content?.markNodeIds ?? [];
  if (first.length === 0) return [...raw];
  const ordered = first.flatMap((child) => raw.filter((n) => n.id === child));
  return [...ordered, ...raw.filter((n) => !first.includes(n.id))];
}

export interface StructureRow {
  node: OrchestrationNode;
  /** 0 是来源的根（或孤立子树的根）。 */
  depth: number;
}

/**
 * 结构树的一节：一个来源（branch），读模型里有、来源列表里没有的程序（unlisted），或“其他节点”（orphans）。
 * 来源没有程序时 rows 为空（R4）。
 */
export interface StructureSection {
  kind: 'branch' | 'unlisted' | 'orphans';
  key: string;
  title: string;
  branchId?: string;
  program?: OrchestrationProgram;
  rows: StructureRow[];
}

/** 结构树按来源分节：先按来源列表的顺序，再是来源列表里没有的程序，最后是其他节点。每个节点只出现一次。 */
export function structureSections(model: OrchestrationModel, branches: readonly { id: string; name: string }[]): StructureSection[] {
  const seen = new Set<string>();
  const rowsOf = (roots: readonly (OrchestrationNode | undefined)[]): StructureRow[] => {
    const rows: StructureRow[] = [];
    const walk = (node: OrchestrationNode, depth: number) => {
      if (seen.has(node.id)) return;
      seen.add(node.id);
      rows.push({ node, depth });
      for (const child of orderedChildren(model, node.id)) walk(child, depth + 1);
    };
    for (const root of roots) if (root) walk(root, 0);
    return rows;
  };
  const sections: StructureSection[] = [];
  const listed = new Set(branches.map((b) => b.id));
  for (const branch of branches) {
    const program = model.programByBranch.get(branch.id);
    sections.push({
      kind: 'branch',
      key: `branch:${branch.id}`,
      title: branch.name || branch.id,
      branchId: branch.id,
      ...(program ? { program } : {}),
      rows: program ? rowsOf([model.byId.get(program.rootContainerId)]) : [],
    });
  }
  for (const program of model.programs) {
    if (listed.has(program.branchId)) continue;
    sections.push({ kind: 'unlisted', key: `program:${program.id}`, title: program.branchId, branchId: program.branchId, program, rows: rowsOf([model.byId.get(program.rootContainerId)]) });
  }
  const orphans = rowsOf(model.orphans);
  if (orphans.length > 0) sections.push({ kind: 'orphans', key: 'orphans', title: '其他节点', rows: orphans });
  return sections;
}

/** 以这个节点为默认容器的来源。 */
export function defaultFor(model: OrchestrationModel, id: string): OrchestrationProgram[] {
  return model.programs.filter((p) => p.defaultContainerId === id);
}

const ANCHOR_TEXT: Record<string, string> = {
  Center: '中心',
  TopLeft: '左上角',
  TopCenter: '顶部中点',
  BottomRight: '右下角',
  LeftCenter: '左侧中点',
  RightCenter: '右侧中点',
};
const ANIMATION_POLICY_TEXT: Record<string, string> = { Karaoke: '逐字（卡拉 OK）', Static: '静态文本', FollowPrimary: '跟随主歌词' };

export const anchorText = (anchor: string): string => ANCHOR_TEXT[anchor] ?? anchor;
export const animationPolicyText = (policy: string | null | undefined): string => (policy ? (ANIMATION_POLICY_TEXT[policy] ?? policy) : '未单独设置');

/** 比例写成百分数：0.5 → “50%”，最多两位小数。 */
export function percent(ratio: number): string {
  return `${Number((ratio * 100).toFixed(2))}%`;
}

// ── 快速布局（G18，W4 设计 §9.3） ──

export interface LayoutPresetInfo {
  preset: BasicLayoutPreset;
  label: string;
  /** 后端按预设精确要求的槽位数。 */
  slots: number;
}

/** 四个预设，顺序即对话框里卡片的顺序；名称沿用旧版界面。 */
export const LAYOUT_PRESETS: readonly LayoutPresetInfo[] = [
  { preset: 'SingleCentered', label: '单行居中', slots: 1 },
  { preset: 'KaraokeDouble', label: '卡拉 OK 双行', slots: 2 },
  { preset: 'DoubleCentered', label: '双行居中', slots: 2 },
  { preset: 'TripleCentered', label: '三行居中', slots: 3 },
];

/** 预设的中文名；未知的原样显示。 */
export function presetLabel(preset: string): string {
  return LAYOUT_PRESETS.find((p) => p.preset === preset)?.label ?? preset;
}

export const SELECTED_SEGMENT_NAME = '歌词段布局';
export const RETAINED_SEGMENT_NAME = '保留原布局的歌词段';

/**
 * 快速布局命令的字段：容器用预设的中文名，槽位“槽位 1～N”，片段名沿用旧版界面。
 * 给出 selectedLineIds（按来源顺序连续的几句）时是范围模式（G19），否则是整个来源。
 */
export function basicLayoutFields(branchId: string, preset: LayoutPresetInfo, selectedLineIds?: readonly string[]): BasicLayoutFields {
  return {
    branchId,
    preset: preset.preset,
    ...(selectedLineIds && selectedLineIds.length > 0 ? { selectedLineIds: [...selectedLineIds] } : {}),
    layoutName: preset.label,
    slotNames: Array.from({ length: preset.slots }, (_, i) => `槽位 ${i + 1}`),
    selectedSegmentName: SELECTED_SEGMENT_NAME,
    retainedSegmentName: RETAINED_SEGMENT_NAME,
  };
}

/** 来源当前的布局：识别出预设时写预设名；识别不出时按默认容器的布局类型（自动布局），再不行写“自定义”。 */
export function currentLayoutText(model: OrchestrationModel, program: OrchestrationProgram): string {
  if (program.recognizedLayoutPreset) return `布局：${presetLabel(program.recognizedLayoutPreset)}`;
  const kind = model.byId.get(program.defaultContainerId)?.layout?.kind;
  return kind === 'Auto' ? `布局：${layoutText(kind)}` : '布局：自定义';
}

export const QUICK_LAYOUT_UNSUPPORTED = '快速布局只支持独立计时、逐字（卡拉 OK）动画的歌词来源；翻译这类跟随主歌词的来源不能套用。';

/**
 * 来源能不能套快速布局（合同 1.18.0：只支持独立计时、逐字动画的来源，否则 UnsupportedSource）。
 * 只看来源摘要的计时方式和读模型里明确设置的动画；动画没有设置时不猜，交给后端判断。不能套用时返回原因。
 */
export function quickLayoutBlock(branch: { timingMode: LyricBranchTimingMode } | undefined, program: OrchestrationProgram): string | undefined {
  if (branch && branch.timingMode !== 'independent') return QUICK_LAYOUT_UNSUPPORTED;
  if (program.animationPolicy && program.animationPolicy !== 'Karaoke') return QUICK_LAYOUT_UNSUPPORTED;
  return undefined;
}

/** 快速布局失败的原因（BasicLayoutErrorCode），文字沿用旧版界面。 */
const LAYOUT_FAILURE_TEXT: Record<string, (label: string) => string> = {
  InvalidPreset: () => '请选择有效的布局预设。',
  SourceMissing: () => '歌词来源已失效。',
  UnsupportedSource: () => QUICK_LAYOUT_UNSUPPORTED,
  InvalidRange: () => '请选择有效的连续歌词段。',
  DiscontiguousRange: () => '请选择连续的歌词行；不连续的对象请在高级编排中分别设置。',
  PresentationInvalid: () => '这个来源的显示结构没有通过校验，不能套用快速布局。',
  RecipeConflict: (label) => `预设“${label}”包含无法安全拆分的范围效果或相对成员，已取消整次操作；不会复制实例或改变种子。`,
  ReferenceConflict: () => '无法安全维护片段引用，已取消整次操作。',
  ScheduleConflict: () => '无法保留未选歌词的原槽位和显示时钟，已取消整次操作；请在高级编排中检查调度规则。',
  ScheduleFailure: () => '无法为歌词重新排定显示时钟，已取消整次操作。',
};

/** 失败原因加上后端消息；不认识的代码只显示后端消息。 */
export function layoutFailureText(failureCode: string | null | undefined, message: string, label: string): string {
  const known = failureCode ? LAYOUT_FAILURE_TEXT[failureCode]?.(label) : undefined;
  return known ? `${known}（${message}）` : message;
}

/**
 * 应用确定不会变化：预演的 changed 总是 false，不能用来判断，只能按读模型推断。
 * - 整个来源：默认容器已识别为这个预设；
 * - 范围：默认容器已识别为这个预设，并且这几句都没有单独分配（都在默认容器里）。
 * 其他情况（例如这几句已经在某个片段里）读模型看不出来，要等应用回执的 changed:false。
 */
export function layoutIsNoOp(program: OrchestrationProgram, preset: LayoutPresetInfo, selectedLineIds?: readonly string[]): boolean {
  if (program.recognizedLayoutPreset !== preset.preset) return false;
  if (!selectedLineIds || selectedLineIds.length === 0) return true;
  const assigned = clipByLine(program);
  return selectedLineIds.every((id) => !assigned.has(id));
}

/** 预演结果一段话：影响哪些句、新建什么。确定不会变化时说明不会变化。 */
export function layoutImpactText(result: BasicLayoutCommandResult, preset: LayoutPresetInfo, noOp: boolean, range?: LineRange): string {
  if (noOp) return '已经是这个布局，应用不会有变化。';
  const parts = range
    ? [
        `将影响${lineRangeText(range)}${range.count > 1 ? `（${range.count} 句）` : ''}；新建“${preset.label}”容器和 ${preset.slots} 个槽位，只接${range.count > 1 ? '这几句' : '这一句'}（新片段“${SELECTED_SEGMENT_NAME}”）；默认容器不变，其余句子保持原来的调度。`,
      ]
    : [`将影响 ${result.affectedLines} 句；新建“${preset.label}”容器和 ${preset.slots} 个槽位，并设为默认容器；原有容器保留。`];
  if (result.existingClips > 0) parts.push(`涉及 ${result.existingClips} 个已有片段${result.splitClips > 0 ? `，其中 ${result.splitClips} 个需要拆分` : ''}。`);
  return parts.join('');
}

// ── 片段与选中的句子（G19，W4 设计 §10） ──

interface LineLike {
  id: string;
  startUs?: number;
  endUs?: number;
}

/** 选中的几句：按来源顺序，句号从 1 开始。 */
export interface LineRange {
  lineIds: string[];
  first: number;
  last: number;
  count: number;
  /** 按来源顺序连续（快速布局的范围模式要求连续）。 */
  contiguous: boolean;
}

/** 这些句子在来源里的范围；不在来源里的 ID 去掉，一句都不剩时返回 undefined。 */
export function lineRange(lines: readonly LineLike[], ids: Iterable<string>): LineRange | undefined {
  const wanted = new Set(ids);
  const numbers: number[] = [];
  const lineIds: string[] = [];
  lines.forEach((line, i) => {
    if (!wanted.has(line.id)) return;
    numbers.push(i + 1);
    lineIds.push(line.id);
  });
  if (numbers.length === 0) return undefined;
  const first = numbers[0]!;
  const last = numbers.at(-1)!;
  return { lineIds, first, last, count: numbers.length, contiguous: last - first + 1 === numbers.length };
}

/** “第 5 句”“第 2～3 句”；不连续时“第 2～6 句中的 3 句”。 */
export function lineRangeText(range: LineRange): string {
  if (range.count === 1) return `第 ${range.first} 句`;
  return range.contiguous ? `第 ${range.first}～${range.last} 句` : `第 ${range.first}～${range.last} 句中的 ${range.count} 句`;
}

/**
 * 时间轴上框选或加选得到的句子，整理成按来源顺序的 ID：去掉不在来源里的，并补上夹在选中句子之间、
 * 没有时间的句子（它们在时间轴上点不到）。有时间的句子不补，所以仍可能不连续。
 */
export function normalizeLineSelection(lines: readonly LineLike[], ids: Iterable<string>): string[] {
  const wanted = new Set(ids);
  const indices = lines.flatMap((line, i) => (wanted.has(line.id) ? [i] : []));
  if (indices.length === 0) return [];
  const first = indices[0]!;
  const last = indices.at(-1)!;
  return lines.slice(first, last + 1).flatMap((line) => (wanted.has(line.id) || line.startUs === undefined || line.endUs === undefined ? [line.id] : []));
}

/** 从 anchor 到 id（含两端）的连续几句；anchor 不在来源里时只有 id。 */
export function extendLineSelection(lines: readonly LineLike[], anchorId: string | undefined, id: string): string[] {
  const to = lines.findIndex((l) => l.id === id);
  if (to < 0) return [];
  const from = anchorId === undefined ? -1 : lines.findIndex((l) => l.id === anchorId);
  if (from < 0) return [id];
  return lines.slice(Math.min(from, to), Math.max(from, to) + 1).map((l) => l.id);
}

/** 来源的片段，按第一句在来源里的位置排序；句子都不在来源里的排在最后，保持读模型顺序。 */
export function clipsOf(program: OrchestrationProgram | undefined, lines: readonly LineLike[]): OrchestrationClip[] {
  const clips = program?.clips ?? [];
  const index = new Map(lines.map((l, i) => [l.id, i]));
  const firstOf = (clip: OrchestrationClip) => Math.min(...clip.lineIds.map((id) => index.get(id) ?? Infinity));
  return clips
    .map((clip, order) => ({ clip, order, first: firstOf(clip) }))
    .sort((a, b) => a.first - b.first || a.order - b.order)
    .map((item) => item.clip);
}

/** 片段名称：有名称就用，否则“片段 N”（N 是 clipsOf 排序后的序号，从 1 开始）。 */
export function clipName(clip: OrchestrationClip, number: number): string {
  return clip.name || `片段 ${number}`;
}

/** 句子 ID → 所在的片段。 */
export function clipByLine(program: OrchestrationProgram | undefined): Map<string, OrchestrationClip> {
  const result = new Map<string, OrchestrationClip>();
  for (const clip of program?.clips ?? []) for (const id of clip.lineIds) if (!result.has(id)) result.set(id, clip);
  return result;
}

/** 没有单独分配（不属于任何片段）的句数：这些句子放在默认容器里。 */
export function unassignedCount(program: OrchestrationProgram | undefined, lines: readonly LineLike[]): number {
  const assigned = clipByLine(program);
  return lines.filter((l) => !assigned.has(l.id)).length;
}

/** 片段的时间范围：第一句有时间的开始到最后一句的结束；一句都没有时间时返回 undefined。 */
export function clipTimeSpan(clip: OrchestrationClip, lines: readonly LineLike[]): { startUs: number; endUs: number } | undefined {
  const members = new Set(clip.lineIds);
  let startUs = Infinity;
  let endUs = -Infinity;
  for (const line of lines) {
    if (!members.has(line.id) || line.startUs === undefined || line.endUs === undefined) continue;
    startUs = Math.min(startUs, line.startUs);
    endUs = Math.max(endUs, line.endUs);
  }
  return startUs < endUs ? { startUs, endUs } : undefined;
}

// ── 片段编辑（G19-B，合同 1.19.0，W4 设计 §11） ──

export const CLIP_UNSUPPORTED = '片段只支持独立计时、逐字（卡拉 OK）动画的歌词来源；翻译这类跟随主歌词的来源不能建片段。';

/** 来源能不能编辑片段：判断与快速布局相同（后端同样返回 UnsupportedSource）。不能时返回原因。 */
export function clipEditBlock(branch: { timingMode: LyricBranchTimingMode } | undefined, program: OrchestrationProgram): string | undefined {
  return quickLayoutBlock(branch, program) ? CLIP_UNSUPPORTED : undefined;
}

/** 片段命令失败的原因（四条命令共用，W4 设计 §11.6）。 */
const CLIP_FAILURE_TEXT: Record<string, string> = {
  SourceMissing: '歌词来源已失效。',
  UnsupportedSource: CLIP_UNSUPPORTED,
  ClipExists: '片段编号重复，请重试。',
  TargetStale: '这个片段或这几句已经不在了，请重新选择。',
  InvalidRange: '片段只能包含这个来源里有时间、不重复的句子。',
  DiscontiguousRange: '片段的句子必须按来源顺序连续。',
  ContainerInvalid: '目标必须是这个来源根下的歌词容器。',
  ClipScheduleConflict: '这几句的时间和别的片段或片段外的句子重叠：每句只能属于一个片段，片段的时间跨度里不能夹着别的句子。',
  TargetConflict: '结果没有通过画面结构校验，没有修改。',
};

export function clipFailureText(failureCode: string): string | undefined {
  return CLIP_FAILURE_TEXT[failureCode];
}

/** 快速布局应用失败的原因，不带后端消息（状态栏用）。 */
export function layoutFailureCodeText(failureCode: string, label: string): string | undefined {
  return LAYOUT_FAILURE_TEXT[failureCode]?.(label);
}

/** 按句号（从 1 开始，按来源顺序）取第 first～last 句；范围无效时返回 undefined。 */
export function linesBetween(lines: readonly LineLike[], first: number, last: number): string[] | undefined {
  if (!Number.isInteger(first) || !Number.isInteger(last) || first < 1 || last < first || last > lines.length) return undefined;
  return lines.slice(first - 1, last).map((l) => l.id);
}

/**
 * 新建片段前的检查（W4 设计 §11.2），返回第一条不满足的原因；都满足时返回 undefined。只是提前提示，结果以后端为准。
 * - 句子要按来源顺序连续、都有时间；
 * - 每句只能属于一个片段；
 * - 片段的时间跨度（第一句的开始到最后一句的结束）里不能有片段外、有时间的句子。
 */
export function clipCreateBlock(program: OrchestrationProgram, lines: readonly LineLike[], lineIds: readonly string[] | undefined): string | undefined {
  return clipRangeBlock(program, lines, lineIds);
}

/**
 * 片段句子的检查：新建（self 省略）与调整句子（§15.3，self 是要调整的片段）共用。
 * 调整时 self 自己的句子不算已占用；和现在相同时写明；有调度上下文时，每句都必须在原段里（与后端相同）。
 */
export function clipRangeBlock(program: OrchestrationProgram, lines: readonly LineLike[], lineIds: readonly string[] | undefined, self?: OrchestrationClip): string | undefined {
  const range = lineIds && lineIds.length > 0 ? lineRange(lines, lineIds) : undefined;
  if (!range || range.count !== lineIds!.length) return '请选择这个来源里的句子。';
  if (!range.contiguous) return '片段需要连续的歌词段。';
  if (self && self.lineIds.length === range.count && self.lineIds.every((id, i) => id === range.lineIds[i])) return '和现在的句子相同。';
  const members = new Set(range.lineIds);
  const number = (id: string) => lines.findIndex((l) => l.id === id) + 1;
  const untimed = range.lineIds.find((id) => {
    const line = lines[number(id) - 1]!;
    return line.startUs === undefined || line.endUs === undefined;
  });
  if (untimed) return `第 ${number(untimed)} 句没有时间；片段只能包含有时间的句子。`;
  const assigned = clipByLine(program);
  const clips = clipsOf(program, lines);
  const taken = range.lineIds.find((id) => {
    const clip = assigned.get(id);
    return clip !== undefined && clip.id !== self?.id;
  });
  if (taken) {
    const clip = assigned.get(taken)!;
    return `第 ${number(taken)} 句已在片段“${clipName(clip, clips.indexOf(clip) + 1)}”里；每句只能属于一个片段，先把那个片段缩小或删除。`;
  }
  const context = self?.scheduleContext;
  if (context) {
    const allowed = new Set(context.lineIds);
    const outside = range.lineIds.find((id) => !allowed.has(id));
    if (outside) {
      const original = lineRange(lines, context.lineIds);
      return `第 ${number(outside)} 句不在原段${original ? `（${lineRangeText(original)}）` : ''}里：这个片段保持原段的槽位和显示时钟，句子只能在原段里调整。`;
    }
  }
  const startUs = lines[range.first - 1]!.startUs!;
  const endUs = lines[range.last - 1]!.endUs!;
  if (endUs <= startUs) return `第 ${range.last} 句的结束不晚于第 ${range.first} 句的开始，这几句的时间顺序与来源顺序不一致。`;
  const inside = lines.findIndex((l) => !members.has(l.id) && l.startUs !== undefined && l.endUs !== undefined && l.startUs < endUs && startUs < l.endUs);
  if (inside >= 0) return `第 ${inside + 1} 句（片段外）的时间夹在这几句中间；片段的时间跨度里不能有别的句子。`;
  return undefined;
}

/** 新建片段默认的目标容器：来源根下第一个不是默认容器的歌词容器；只有默认容器时选它。 */
export function defaultClipContainer(model: OrchestrationModel, program: OrchestrationProgram): string | undefined {
  const containers = containersOf(model, program);
  return (containers.find((n) => n.id !== program.defaultContainerId) ?? containers[0])?.id;
}

// ── 结构编辑（G20／G20-B，合同 1.23.0，W4 设计 §12） ──

/** 普通组：没有布局、没有内容、不是槽位。新建和移动只放进普通组或画布根层。 */
export function isPlainGroup(model: OrchestrationModel, node: OrchestrationNode): boolean {
  return !node.layout && !node.contentKind && !model.slotOf.has(node.id);
}

export function isProgramRoot(model: OrchestrationModel, id: string): boolean {
  return model.programs.some((p) => p.rootContainerId === id);
}

/** 自己和全部后代（深度优先）；父子关系有环时截断。 */
export function subtreeIds(model: OrchestrationModel, id: string): string[] {
  const result: string[] = [];
  const seen = new Set<string>();
  const walk = (nodeId: string) => {
    if (seen.has(nodeId) || !model.byId.has(nodeId)) return;
    seen.add(nodeId);
    result.push(nodeId);
    for (const child of model.children.get(nodeId) ?? []) walk(child.id);
  };
  walk(id);
  return result;
}

/** 节点被谁用着：来源的根、默认容器、片段的目标容器、前奏的提示容器。 */
export interface NodeReference {
  kind: 'root' | 'default' | 'clips' | 'leadIns';
  program: OrchestrationProgram;
  /** clips／leadIns 的个数。 */
  count?: number;
}

export function referencesOf(model: OrchestrationModel, id: string): NodeReference[] {
  const refs: NodeReference[] = [];
  for (const program of model.programs) {
    if (program.rootContainerId === id) refs.push({ kind: 'root', program });
    if (program.defaultContainerId === id) refs.push({ kind: 'default', program });
    const clips = (program.clips ?? []).filter((c) => c.containerId === id).length;
    if (clips > 0) refs.push({ kind: 'clips', program, count: clips });
    const leadIns = (program.leadIns ?? []).filter((l) => l.indicatorContainerId === id).length;
    if (leadIns > 0) refs.push({ kind: 'leadIns', program, count: leadIns });
  }
  return refs;
}

type BranchName = (branchId: string) => string | undefined;

function referenceText(ref: NodeReference, branchName: BranchName): string {
  const source = branchName(ref.program.branchId) ?? ref.program.branchId;
  switch (ref.kind) {
    case 'root':
      return `${source}的根`;
    case 'default':
      return `${source}的默认容器`;
    case 'clips':
      return `${source}的 ${ref.count} 个片段的目标容器`;
    case 'leadIns':
      return `${source}的 ${ref.count} 个前奏的提示容器`;
  }
}

/** 删除前的检查（W4 设计 §12.4）：不能删时返回原因。只是提前提示，结果以后端为准。 */
export function removeBlock(model: OrchestrationModel, id: string, branchName: BranchName): string | undefined {
  const node = model.byId.get(id);
  if (!node) return '这个节点已经不在了。';
  if (isProgramRoot(model, id)) return '来源的根不能删除。';
  for (const ref of referencesOf(model, id)) {
    const source = branchName(ref.program.branchId) ?? ref.program.branchId;
    if (ref.kind === 'default') return `它是${source}的默认容器：先把默认容器改到别的容器。`;
    if (ref.kind === 'clips') return `有 ${ref.count} 个片段以它为目标容器：先删除这些片段或改目标容器。`;
    if (ref.kind === 'leadIns') return `有 ${ref.count} 个前奏用它提示：先删除这些前奏。`;
  }
  for (const descendant of subtreeIds(model, id).slice(1)) {
    const ref = referencesOf(model, descendant)[0];
    if (ref) return `子树里的“${nodeName(model, model.byId.get(descendant)!)}”是${referenceText(ref, branchName)}，不能删除。`;
  }
  const slot = model.slotOf.get(id);
  if (slot && model.byId.get(slot.containerId)?.layout?.slotIds.length === 1) return '轮换至少要有一个槽位。';
  const mark = model.markOf.get(id);
  if (mark && model.byId.get(mark.containerId)?.content?.markNodeIds?.length === 1) return '提示组至少要有一个标记。';
  return undefined;
}

/** 复制前的检查：来源的根不能复制。 */
export function copyBlock(model: OrchestrationModel, id: string): string | undefined {
  if (!model.byId.has(id)) return '这个节点已经不在了。';
  return isProgramRoot(model, id) ? '来源的根不能复制：副本会成为不属于任何来源的节点。' : undefined;
}

/** 副本的名称：“原名 副本”；没有名称的槽位和标记仍不命名（按序号显示）。 */
export function copyName(model: OrchestrationModel, node: OrchestrationNode): string | null {
  if (node.name) return `${node.name} 副本`;
  if (model.slotOf.has(node.id) || model.markOf.has(node.id)) return null;
  return `${nodeName(model, node)} 副本`;
}

/** 移动（改父级）前的检查：来源的根、槽位、标记不能移动。 */
export function moveBlock(model: OrchestrationModel, id: string): string | undefined {
  if (!model.byId.has(id)) return '这个节点已经不在了。';
  if (isProgramRoot(model, id)) return '来源的根固定在画布根层。';
  if (model.slotOf.has(id)) return '槽位只能在它的轮换里调整顺序。';
  if (model.markOf.has(id)) return '标记只能留在它的提示组里。';
  return undefined;
}

/**
 * 可以移到哪里：candidates（按结构树顺序）里的普通组，去掉自己和后代、当前父级；子树里有某个来源的默认容器、片段目标或前奏提示容器时，
 * 只能留在那个来源的根下。canvas 表示能不能移到画布根层。
 */
export function moveTargets(model: OrchestrationModel, id: string, candidates: readonly OrchestrationNode[]): { groups: OrchestrationNode[]; canvas: boolean } {
  const node = model.byId.get(id);
  if (!node || moveBlock(model, id)) return { groups: [], canvas: false };
  const subtree = new Set(subtreeIds(model, id));
  const roots = new Set<string>();
  for (const member of subtree) for (const ref of referencesOf(model, member)) roots.add(ref.program.rootContainerId);
  const under = (target: string) => [...roots].every((root) => ancestors(model, target).some((n) => n.id === root));
  const groups = candidates.filter((n) => isPlainGroup(model, n) && !subtree.has(n.id) && n.id !== node.parentId && under(n.id));
  return { groups, canvas: roots.size === 0 && !!node.parentId };
}

/**
 * 上移、下移（W4 设计 §12.4、§14.6）：槽位按轮换顺序（reorderSlots），标记按提示组里的顺序（reorderMarks），
 * 其他节点在同级里（reorderChildren，完整的同级清单）。轮换容器下的非槽位子节点只和非槽位的兄弟交换；
 * 画布根层只和其他不属于来源的节点交换。不能移动时返回原因。
 */
export function reorderPlan(
  model: OrchestrationModel,
  id: string,
  delta: -1 | 1,
): { command: OrchestrationStructureCommand | OrchestrationLeadInCommand } | { block: string } {
  const node = model.byId.get(id);
  if (!node) return { block: '这个节点已经不在了。' };
  if (isProgramRoot(model, id)) return { block: '来源之间的上下层次不在这里调整。' };
  const swap = (ids: readonly string[], subset: readonly string[]): string[] | undefined => {
    const k = subset.indexOf(id);
    const other = subset[k + delta];
    if (k < 0 || other === undefined) return undefined;
    return ids.map((x) => (x === id ? other : x === other ? id : x));
  };
  const slot = model.slotOf.get(id);
  if (slot) {
    const slotIds = model.byId.get(slot.containerId)?.layout?.slotIds ?? [];
    const next = swap(slotIds, slotIds);
    return next ? { command: { type: 'orchestration.reorderSlots', containerId: slot.containerId, orderedIds: next } } : { block: delta < 0 ? '已经是第一个槽位。' : '已经是最后一个槽位。' };
  }
  const mark = model.markOf.get(id);
  if (mark) {
    const markIds = model.byId.get(mark.containerId)?.content?.markNodeIds ?? [];
    const next = swap(markIds, markIds);
    return next ? { command: { type: 'orchestration.reorderMarks', containerId: mark.containerId, orderedIds: next } } : { block: delta < 0 ? '已经是第一个标记。' : '已经是最后一个标记。' };
  }
  const parent = node.parentId ? model.byId.get(node.parentId) : undefined;
  const siblings = parent ? (model.children.get(parent.id) ?? []).map((n) => n.id) : [...model.byId.values()].filter((n) => !n.parentId).map((n) => n.id);
  const subset = parent
    ? siblings.filter((x) => !model.slotOf.has(x) && !model.markOf.has(x))
    : siblings.filter((x) => !isProgramRoot(model, x));
  const next = swap(siblings, subset);
  return next ? { command: { type: 'orchestration.reorderChildren', parentId: parent?.id ?? null, orderedIds: next } } : { block: delta < 0 ? '已经在最前。' : '已经在最后。' };
}

/** 新建的预填父级：选中的节点是普通组就用它，否则用它最近的普通组祖先；都没有时用来源的根。null 是画布根层。 */
export function insertionParent(model: OrchestrationModel, selectedId: string | undefined, program: OrchestrationProgram | undefined): string | null {
  const group = selectedId ? ancestors(model, selectedId).find((n) => isPlainGroup(model, n)) : undefined;
  if (group) return group.id;
  return program && model.byId.has(program.rootContainerId) ? program.rootContainerId : null;
}

export const NODE_KIND_TEXT: Record<CreateNodeKind, string> = { group: '组', auto: '自动布局容器', slotSequence: '轮换容器' };
export const NODE_KIND_NAME: Record<CreateNodeKind, string> = { group: '新组', auto: '新容器', slotSequence: '轮换' };

/** 结构命令失败的原因（W4 设计 §12.6）。 */
const STRUCTURE_FAILURE_TEXT: Record<string, string> = {
  TargetStale: '这个节点已经不在了，请重新选择。',
  IdentityConflict: '编号重复，请重试。',
  ContainerInvalid: '只能放在普通组下（不能放进歌词容器、槽位或内容节点）。',
  PresentationInvalid: '结果没有通过画面结构校验，没有修改。',
};

export function structureFailureText(failureCode: string): string | undefined {
  return STRUCTURE_FAILURE_TEXT[failureCode];
}
