// 片段成员调整、拆分与合并（W4 设计 §15）：原段说明、拆分点的选项与预填、前后可合并的片段及原因、
// 三条命令的失败原因与提示文字。调整句子的检查与新建片段共用 clipRangeBlock。无 DOM。

import type { OrchestrationClip, OrchestrationClipScheduleContext, OrchestrationProgram } from '../../protocol';
import { clipByLine, clipFailureText, clipName, clipsOf, lineRange, lineRangeText, nodeName, type OrchestrationModel } from './orchestration';

interface LineLike {
  id: string;
  text?: string;
  startUs?: number;
  endUs?: number;
}

export type ClipMembersOperation = 'setClipMembers' | 'splitClip' | 'mergeClips';

/** 片段的名称（clipsOf 排序后的序号）。 */
export function clipLabel(program: OrchestrationProgram, lines: readonly LineLike[], clip: OrchestrationClip): string {
  return clipName(clip, clipsOf(program, lines).indexOf(clip) + 1);
}

/** 有调度上下文时写明只能在原段里调整（§15.3）；没有时返回 undefined。 */
export function scheduleContextNote(clip: OrchestrationClip, lines: readonly LineLike[]): string | undefined {
  const context = clip.scheduleContext;
  if (!context) return undefined;
  const range = lineRange(lines, context.lineIds);
  return `这个片段保持原段${range ? `（${lineRangeText(range)}）` : ''}的槽位和显示时钟，句子只能在这一段里调整。`;
}

// ── 拆分（§15.4） ──

export interface SplitPoint {
  lineId: string;
  /** 句号，从 1 开始。 */
  number: number;
  label: string;
}

/** 只有一句时不能拆分。 */
export function splitBlock(clip: OrchestrationClip): string | undefined {
  return clip.lineIds.length < 2 ? '只有一句，不能拆分。' : undefined;
}

/** 拆分点的选项：片段里除第一句以外的句子，“第 k 句 · 文字”。 */
export function splitPoints(clip: OrchestrationClip, lines: readonly LineLike[]): SplitPoint[] {
  const index = new Map(lines.map((l, i) => [l.id, i]));
  return clip.lineIds.slice(1).flatMap((lineId) => {
    const i = index.get(lineId);
    if (i === undefined) return [];
    const text = lines[i]!.text?.trim();
    return [{ lineId, number: i + 1, label: text ? `第 ${i + 1} 句 · ${text}` : `第 ${i + 1} 句` }];
  });
}

/** 预填的拆分点：播放头所在的句子（在片段里、不是第一句时），否则中间那句。 */
export function defaultSplitPoint(clip: OrchestrationClip, lines: readonly LineLike[], playheadUs?: number): string | undefined {
  const points = splitPoints(clip, lines);
  if (points.length === 0) return undefined;
  if (playheadUs !== undefined) {
    const at = points.find((p) => {
      const line = lines[p.number - 1]!;
      return line.startUs !== undefined && line.endUs !== undefined && line.startUs <= playheadUs && playheadUs < line.endUs;
    });
    if (at) return at.lineId;
  }
  return clip.lineIds[Math.floor(clip.lineIds.length / 2)];
}

/** 从 splitLineId 拆开后两段的范围（拆分点之前、从拆分点开始）；拆分点无效时返回 undefined。 */
export function splitParts(clip: OrchestrationClip, lines: readonly LineLike[], splitLineId: string): { left: string; right: string } | undefined {
  const at = clip.lineIds.indexOf(splitLineId);
  if (at < 1) return undefined;
  const left = lineRange(lines, clip.lineIds.slice(0, at));
  const right = lineRange(lines, clip.lineIds.slice(at));
  return left && right ? { left: lineRangeText(left), right: lineRangeText(right) } : undefined;
}

/** 对话框里的拆分结果说明。 */
export function splitResultText(clip: OrchestrationClip, label: string, lines: readonly LineLike[], splitLineId: string): string | undefined {
  const parts = splitParts(clip, lines, splitLineId);
  if (!parts) return undefined;
  // 后端：原片段没有调度上下文时，两段的上下文都是原来的全部句子；有时沿用原来的。
  const scope = lineRange(lines, clip.scheduleContext?.lineIds ?? clip.lineIds);
  return (
    `${parts.left}留在“${label}”，${parts.right}成为新片段（名称相同，目标容器相同）。` +
    `拆开后两段都保持原段的槽位和显示时钟，句子只能在${scope ? lineRangeText(scope) : '原段'}里调整。`
  );
}

// ── 合并（§15.5） ──

const MERGE_CONTEXT = '两个片段来自不同的原段（调度上下文不同），不能合并。';

export type MergeSide = 'previous' | 'next';

export type MergeNeighbour = { clip: OrchestrationClip; label: string; block?: undefined } | { clip?: undefined; block: string };

/** 调度上下文按内容比较（与后端相同）；都没有时相同。 */
export function sameScheduleContext(a: OrchestrationClipScheduleContext | null | undefined, b: OrchestrationClipScheduleContext | null | undefined): boolean {
  if (!a || !b) return !a && !b;
  return (
    a.isDefaultSegment === b.isDefaultSegment &&
    (a.previousLineId ?? null) === (b.previousLineId ?? null) &&
    (a.nextLineId ?? null) === (b.nextLineId ?? null) &&
    a.lineIds.length === b.lineIds.length &&
    a.lineIds.every((id, i) => id === b.lineIds[i])
  );
}

/**
 * 前一个（或后一个）可合并的片段：最后一句紧挨着这个片段第一句的片段（后一个同理）。
 * 不能合并时返回原因：紧挨着的句子不在片段里、目标容器不同、调度上下文不同。外观分配的冲突由后端判断。
 */
export function mergeNeighbour(
  model: OrchestrationModel,
  program: OrchestrationProgram,
  lines: readonly LineLike[],
  clip: OrchestrationClip,
  side: MergeSide,
): MergeNeighbour {
  const where = side === 'previous' ? '前' : '后';
  const range = lineRange(lines, clip.lineIds);
  if (!range) return { block: '这个片段的句子已经不在了。' };
  const index = side === 'previous' ? range.first - 2 : range.last;
  const line = lines[index];
  if (!line) return { block: `${where}面没有句子。` };
  const neighbour = clipByLine(program).get(line.id);
  if (!neighbour) return { block: `${where}面紧挨着的第 ${index + 1} 句不在片段里。` };
  const label = clipLabel(program, lines, neighbour);
  if (neighbour.containerId !== clip.containerId) {
    const node = model.byId.get(neighbour.containerId);
    const container = node ? nodeName(model, node) : neighbour.containerId;
    return { block: `${where}一个片段“${label}”在“${container}”里，与这个片段的目标容器不同；先把它们改到同一个容器。` };
  }
  if (!sameScheduleContext(neighbour.scheduleContext, clip.scheduleContext)) return { block: MERGE_CONTEXT };
  return { clip: neighbour, label };
}

/** 合并后的范围（按来源顺序合成）。 */
export function mergedRangeText(lines: readonly LineLike[], a: OrchestrationClip, b: OrchestrationClip): string | undefined {
  const range = lineRange(lines, [...a.lineIds, ...b.lineIds]);
  return range ? lineRangeText(range) : undefined;
}

// ── 失败原因（§15.6）：在片段命令的对照表（§11.6）上按操作补充 ──

const MEMBERS_FAILURE_TEXT: Record<ClipMembersOperation, Record<string, string>> = {
  setClipMembers: {
    ScheduleContextConflict: '新加的句子不在这个片段的原段里：这个片段保持原段的槽位和显示时钟，只能在原段里调整。',
    ReferenceConflict: '这个片段的外观分配带有效果配方，不能跟着改变句子：先移除这个片段的外观分配再调整。',
  },
  splitClip: {
    InvalidRange: '拆分点必须是这个片段里除第一句以外的句子。',
    ClipScheduleConflict: '拆开会改变这几句的槽位或显示时钟，不能拆分。',
    ReferenceConflict: '这个片段的外观分配或效果引用不能完整拆开：先移除这个片段的外观分配再拆分。',
  },
  mergeClips: {
    ContainerInvalid: '合并的片段必须在同一个目标容器里。',
    ScheduleContextConflict: MERGE_CONTEXT,
    DiscontiguousRange: '合并后的句子必须按来源顺序连续：两个片段之间不能隔着别的句子。',
    ReferenceConflict: '两个片段的外观分配或效果引用不同，不能合并：先让它们一致，或删掉其中一个的分配。',
  },
};

/** 某个操作的失败原因；没有专门说明的按片段命令的对照表。 */
export function clipMembersFailureText(operation: ClipMembersOperation): (failureCode: string) => string | undefined {
  return (failureCode) => MEMBERS_FAILURE_TEXT[operation][failureCode] ?? clipFailureText(failureCode);
}
