// 外观页的目标选择（W3 设计 §2.6，W7 设计 §2.7）：工程默认、来源、行、文本单元四类与分配命令的目标同形；
// W7 加了编排对象：片段（同一来源的多选合成一个 clips 目标）和节点（容器、组、槽位、前奏提示组、标记）。
// 多选只在同一层级内进行；工程默认和节点一次只选一个。

import {
  TOKEN_TEMPLATE_PROPERTIES,
  type LyricBranchSummary,
  type OrchestrationNode,
  type PresetApplicationTarget,
  type StyleTarget,
  type TemplatePreviewOptionsResponse,
  type TemplateSummary,
} from '../../protocol';
import type { EditorLine } from './lyrics';
import { clipName, clipsOf, nodePath, nodeRole, type OrchestrationModel } from './orchestration';
import { groupIncluded, TEMPLATE_GROUPS } from './template-groups';

/** 片段目标：发送时同一来源的片段合成一个 clips 目标（commandTargets）。 */
export interface ClipTarget {
  kind: 'clip';
  branchId: string;
  clipId: string;
}

/** 节点目标：includeDescendants 是应用范围（检查器的开关），不属于目标身份。 */
export interface NodeTarget {
  kind: 'node';
  nodeId: string;
  includeDescendants: boolean;
}

export type AppearanceTarget = StyleTarget | ClipTarget | NodeTarget;

export const PROJECT_TARGET: StyleTarget = { kind: 'project' };

/** 分配命令一次最多的目标数（合同 §13.3）。 */
export const MAX_TARGETS = 1000;

export const TARGET_KIND_TEXT: Record<AppearanceTarget['kind'], string> = { project: '工程默认', branch: '来源', line: '行', token: '文本单元', clip: '片段', node: '编排对象' };

export function targetKey(target: AppearanceTarget): string {
  switch (target.kind) {
    case 'project':
      return 'project';
    case 'branch':
      return `branch:${target.branchId}`;
    case 'line':
      return `line:${target.branchId}/${target.lineId}`;
    case 'token':
      return `token:${target.branchId}/${target.lineId}/${target.tokenId}`;
    case 'clip':
      return `clip:${target.clipId}`;
    case 'node':
      return `node:${target.nodeId}`;
  }
}

export function isStyleTarget(target: AppearanceTarget): target is StyleTarget {
  return target.kind !== 'clip' && target.kind !== 'node';
}

export function lineTarget(line: EditorLine): StyleTarget {
  return { kind: 'line', branchId: line.branchId, lineId: line.id };
}

export function tokenTarget(line: EditorLine, tokenId: string): StyleTarget {
  return { kind: 'token', branchId: line.branchId, lineId: line.id, tokenId };
}

export function clipTarget(branchId: string, clipId: string): ClipTarget {
  return { kind: 'clip', branchId, clipId };
}

/**
 * 点选一个目标。additive（Ctrl／⌘）时在同一层级内加选或取消，最后一个不能取消；片段只能和同一来源的片段一起选。
 * 层级不同、工程默认、节点或不加选时只选它一个。
 */
export function pickTarget(current: readonly AppearanceTarget[], target: AppearanceTarget, additive: boolean): AppearanceTarget[] {
  const head = current[0];
  const sameLevel = head?.kind === target.kind && (target.kind !== 'clip' || (head as ClipTarget).branchId === target.branchId);
  if (!additive || target.kind === 'project' || target.kind === 'node' || !sameLevel) return [target];
  const key = targetKey(target);
  const rest = current.filter((t) => targetKey(t) !== key);
  if (rest.length < current.length) return rest.length > 0 ? rest : [...current];
  return current.length >= MAX_TARGETS ? [...current] : [...current, target];
}

/** Shift 连选：同一列表里从锚点到目标的一段；锚点不在列表里时只选目标。 */
export function rangeTargets<T extends AppearanceTarget>(siblings: readonly T[], anchorKey: string | undefined, target: T): T[] {
  const keys = siblings.map(targetKey);
  const from = anchorKey ? keys.indexOf(anchorKey) : -1;
  const to = keys.indexOf(targetKey(target));
  if (from < 0 || to < 0) return [target];
  const [a, b] = from < to ? [from, to] : [to, from];
  return siblings.slice(a, Math.min(b + 1, a + MAX_TARGETS));
}

/**
 * 修订变化后去掉已不存在的目标；全部失效时回到工程默认。没有变化时返回原数组。
 * model 是编排结构；还没读到时片段和节点目标先保留。
 */
export function reconcileTargets(
  targets: readonly AppearanceTarget[],
  branchIds: readonly string[],
  linesByBranch: Readonly<Record<string, readonly EditorLine[]>>,
  model?: OrchestrationModel,
): readonly AppearanceTarget[] {
  const exists = (t: AppearanceTarget): boolean => {
    if (t.kind === 'project') return true;
    if (t.kind === 'node') return !model || model.byId.has(t.nodeId);
    if (!branchIds.includes(t.branchId)) return false;
    if (t.kind === 'branch') return true;
    if (t.kind === 'clip') return !model || !!model.programByBranch.get(t.branchId)?.clips?.some((c) => c.id === t.clipId);
    const line = linesByBranch[t.branchId]?.find((l) => l.id === t.lineId);
    return !!line && (t.kind === 'line' || line.tokens.some((k) => k.id === t.tokenId));
  };
  const kept = targets.filter(exists);
  if (kept.length === targets.length) return targets;
  return kept.length > 0 ? kept : [PROJECT_TARGET];
}

/** 分配命令和候选试看的目标：同一来源的片段合成一个 clips 目标（放在第一个片段的位置），其余原样。 */
export function commandTargets(targets: readonly AppearanceTarget[]): PresetApplicationTarget[] {
  const result: PresetApplicationTarget[] = [];
  const clips = new Map<string, string[]>();
  for (const t of targets) {
    if (t.kind !== 'clip') {
      result.push(t);
      continue;
    }
    const ids = clips.get(t.branchId);
    if (ids) {
      ids.push(t.clipId);
      continue;
    }
    const list = [t.clipId];
    clips.set(t.branchId, list);
    result.push({ kind: 'clips', branchId: t.branchId, clipIds: list });
  }
  return result;
}

/** 试看用的目标签名：节点的应用范围也算在内，换了范围就是另一次试看。 */
export function targetsSignature(targets: readonly AppearanceTarget[]): string {
  return targets.map((t) => (t.kind === 'node' ? `${targetKey(t)}:${t.includeDescendants ? 'all' : 'self'}` : targetKey(t))).join('|');
}

/** 来源的显示名：有名称用名称，否则用 ID。 */
export function branchName(branchId: string, branches: readonly LyricBranchSummary[] | undefined): string {
  return branches?.find((b) => b.id === branchId)?.name || branchId;
}

/** 编排节点是什么：前奏提示组、标记、槽位、容器、组；其他内容节点写“内容”。 */
export function nodeKindText(model: OrchestrationModel, node: OrchestrationNode): string {
  if (node.contentKind === 'countdownSequence') return '前奏提示组';
  if (node.contentKind === 'textMark') return '标记';
  return { slot: '槽位', container: '容器', group: '组', content: '内容' }[nodeRole(model, node)];
}

/** 来源里片段的显示名（按 clipsOf 的序号命名没有名称的片段）。 */
export function clipLabelOf(model: OrchestrationModel | undefined, lines: readonly EditorLine[], branchId: string, clipId: string): string {
  const clips = clipsOf(model?.programByBranch.get(branchId), lines);
  const index = clips.findIndex((c) => c.id === clipId);
  if (!model) return '片段';
  return index < 0 ? '已删除的片段' : clipName(clips[index]!, index + 1);
}

/**
 * 目标路径（整体设计 §5.4 检查器顶部），例如“主歌词 / 第 2、4～6 行”“主歌词 / 第 3 行 / 「光」「の」”
 * “主歌词 / 画面 / 常规”“主歌词 / 片段 1、副歌”。不同来源用“；”分开。
 */
export function describeTargets(
  targets: readonly AppearanceTarget[],
  branches: readonly LyricBranchSummary[] | undefined,
  linesByBranch: Readonly<Record<string, readonly EditorLine[]>>,
  model?: OrchestrationModel,
): string {
  const first = targets[0];
  if (!first) return '未选择';
  if (first.kind === 'project') return '工程默认';
  if (first.kind === 'branch') return targets.map((t) => (t.kind === 'branch' ? branchName(t.branchId, branches) : '')).join('、');
  if (first.kind === 'node') {
    if (!model?.byId.has(first.nodeId)) return '编排对象';
    return nodePath(model, first.nodeId, (id) => branches?.find((b) => b.id === id)?.name || undefined).join(' / ');
  }
  if (first.kind === 'clip') {
    const lines = linesByBranch[first.branchId] ?? [];
    const names = targets.flatMap((t) => (t.kind === 'clip' ? [clipLabelOf(model, lines, t.branchId, t.clipId)] : []));
    return `${branchName(first.branchId, branches)} / ${names.join('、')}`;
  }

  // 按来源（文本单元再按行）分组，保持首次出现的顺序。
  const groups = new Map<string, { branchId: string; lineId?: string; items: StyleTarget[] }>();
  for (const t of targets) {
    if (t.kind !== 'line' && t.kind !== 'token') continue;
    const key = t.kind === 'line' ? t.branchId : `${t.branchId}/${t.lineId}`;
    const group = groups.get(key) ?? { branchId: t.branchId, ...(t.kind === 'token' ? { lineId: t.lineId } : {}), items: [] };
    group.items.push(t);
    groups.set(key, group);
  }
  return [...groups.values()]
    .map((g) => {
      const lines = linesByBranch[g.branchId] ?? [];
      const name = branchName(g.branchId, branches);
      if (g.lineId === undefined) {
        const numbers = g.items.map((t) => (t.kind === 'line' ? lines.findIndex((l) => l.id === t.lineId) + 1 : 0)).filter((n) => n > 0);
        return `${name} / 第 ${compressNumbers(numbers)} 行`;
      }
      const index = lines.findIndex((l) => l.id === g.lineId);
      const line = lines[index];
      const texts = g.items.map((t) => (t.kind === 'token' ? `「${line?.tokens.find((k) => k.id === t.tokenId)?.text ?? '?'}」` : ''));
      return `${name} / 第 ${index + 1} 行 / ${texts.join('')}`;
    })
    .join('；');
}

/** 行号压缩成区间：[2,4,5,6] → “2、4～6”。 */
export function compressNumbers(numbers: readonly number[]): string {
  const sorted = [...new Set(numbers)].sort((a, b) => a - b);
  const parts: string[] = [];
  for (let i = 0; i < sorted.length; i++) {
    const start = sorted[i]!;
    let end = start;
    while (sorted[i + 1] === end + 1) end = sorted[++i]!;
    parts.push(end - start >= 2 ? `${start}～${end}` : end > start ? `${start}、${end}` : `${start}`);
  }
  return parts.join('、');
}

/** 应用按钮上的目标数量：“工程默认”“2 行”“3 个文本单元”“2 个片段”“这个前奏提示组”。 */
export function targetCountText(targets: readonly AppearanceTarget[], model?: OrchestrationModel): string {
  const n = targets.length;
  const first = targets[0];
  switch (first?.kind) {
    case undefined:
    case 'project':
      return '工程默认';
    case 'branch':
      return n === 1 ? '这个来源' : `${n} 个来源`;
    case 'line':
      return `${n} 行`;
    case 'token':
      return `${n} 个文本单元`;
    case 'clip':
      return n === 1 ? '这个片段' : `${n} 个片段`;
    case 'node': {
      const node = model?.byId.get(first.nodeId);
      return model && node ? `这个${nodeKindText(model, node)}` : '这个编排对象';
    }
  }
}

/** 试样选项里的目标键（W7 设计 §2.7）：节点分成容器或组（group）和标记（mark）。 */
export type SampleTargetKey = 'project' | 'branch' | 'line' | 'token' | 'clip' | 'group' | 'mark';

export const SAMPLE_KEY_TEXT: Record<SampleTargetKey, string> = { project: '工程默认', branch: '来源', line: '行', token: '文本单元', clip: '片段', group: '容器或组', mark: '标记' };

/** 目标在试样选项里对应的目标键；节点要看编排结构，还没读到时按容器或组。 */
export function sampleKeyOf(target: AppearanceTarget | undefined, model?: OrchestrationModel): SampleTargetKey {
  if (!target) return 'project';
  if (target.kind === 'node') return model?.byId.get(target.nodeId)?.contentKind === 'textMark' ? 'mark' : 'group';
  return target.kind;
}

/** 试样选项里所有样本的目标键（去重，按出现顺序）：后端按模板判断过能不能用。 */
export function sampleTargetKeys(options: Pick<TemplatePreviewOptionsResponse, 'samples'>): string[] {
  return [...new Set(options.samples.flatMap((s) => s.targets.map((t) => t.key)))];
}

/**
 * 模板不能用于这类目标的原因；能用时返回 undefined。
 * targetKeys 是后端试样选项给出的目标键（W7 设计 §2.7），给了就以它为准；没有时（还没读到或读失败）按本地规则判断
 * （W3 设计 §2.6，对照后端 PresetApplicationTargets.CanApply）。本地规则看不出配方能不能用，最终以后端的拒绝为准。
 */
export function applicability(summary: TemplateSummary, key: SampleTargetKey, targetKeys?: readonly string[]): string | undefined {
  const local = localApplicability(summary, key);
  if (!targetKeys) return local;
  if (targetKeys.includes(key)) return undefined;
  if (local) return local;
  if (key === 'token' && summary.hasEffectRecipes) return '含作用于整行的效果，不能用于文本单元';
  return `这个模板不能用于${SAMPLE_KEY_TEXT[key]}`;
}

function localApplicability(summary: TemplateSummary, key: SampleTargetKey): string | undefined {
  if (summary.kind === 'token' && key !== 'token') return '文本单元用途，只能用于文本单元';
  if (key === 'token') {
    const allowed = new Set(TOKEN_TEMPLATE_PROPERTIES);
    const groups = TEMPLATE_GROUPS.filter((g) => groupIncluded(g, summary.properties) && g.properties.some((p) => summary.properties.includes(p) && !allowed.has(p)));
    if (groups.length > 0) return `含文本单元不能用的属性：${groups.map((g) => g.label).join('、')}`;
  }
  return undefined;
}
