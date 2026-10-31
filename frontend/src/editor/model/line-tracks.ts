// 时间轴的翻译行轨与主歌词参考轨（W6 设计 §2.6）：每条轨画哪些行、画在哪里、能不能拖。纯函数，不依赖 React。

import type { LyricBranchSummary, LyricBranchTimingMode } from '../../protocol';
import { isTimed, type EditorLine } from './lyrics';

/** 轨上的一行。follow：跟随主歌词，画在关联主歌词行的位置（虚线框），不能拖。 */
export interface TrackLine {
  lineId: string;
  text: string;
  startUs: number;
  endUs: number;
  follow: boolean;
}

/** 行轨下面的一条行轨：看主歌词时每个翻译来源一条，看其他来源时是只读的主歌词参考轨。 */
export interface LineTrack {
  branchId: string;
  /** 轨道左端的小字。 */
  name: string;
  lines: readonly TrackLine[];
  /** 没有可画的行时写的说明。 */
  empty: string;
  /** 独立计时的翻译来源可以拖行的边界。 */
  editable: boolean;
}

/**
 * 某来源在时间轴上画的行：独立计时的画自己有时间的行；跟随主歌词的，已关联且主歌词行有时间的画在主歌词行的位置
 * （后端保留的原时间显示时不用，也不画）；不计时的不画。
 */
export function trackLines(mode: LyricBranchTimingMode, lines: readonly EditorLine[], primaryLines: readonly EditorLine[]): TrackLine[] {
  if (mode === 'none') return [];
  if (mode === 'independent') {
    return lines.flatMap((l) => (isTimed(l) ? [{ lineId: l.id, text: l.text, startUs: l.startUs, endUs: l.endUs, follow: false }] : []));
  }
  const primary = new Map(primaryLines.map((l) => [l.id, l]));
  return lines.flatMap((l) => {
    const p = l.primaryLineId ? primary.get(l.primaryLineId) : undefined;
    return p && isTimed(p) ? [{ lineId: l.id, text: l.text, startUs: p.startUs, endUs: p.endUs, follow: true }] : [];
  });
}

/** 歌词页时间轴行轨下面的轨：当前来源是主歌词时，每个角色为“翻译”的来源一条；否则一条只读的主歌词参考轨。 */
export function lineTracks(state: {
  branches?: readonly LyricBranchSummary[] | undefined;
  primaryBranchId?: string | undefined;
  branchId: string;
  linesByBranch: Readonly<Record<string, readonly EditorLine[]>>;
}): LineTrack[] {
  const { branches, primaryBranchId, branchId, linesByBranch } = state;
  if (!branches || !primaryBranchId) return [];
  const primaryLines = linesByBranch[primaryBranchId] ?? [];
  if (branchId !== primaryBranchId) {
    return [{ branchId: primaryBranchId, name: '主歌词', lines: trackLines('independent', primaryLines, []), empty: '主歌词还没有时间', editable: false }];
  }
  return branches
    .filter((b) => b.role === 'translation' && b.id !== primaryBranchId)
    .map((b) => ({
      branchId: b.id,
      name: b.name,
      lines: trackLines(b.timingMode, linesByBranch[b.id] ?? [], primaryLines),
      empty: b.timingMode === 'none' ? '这个来源不计时' : '这个来源还没有时间',
      editable: b.timingMode === 'independent',
    }));
}

/** 当前来源跟随主歌词时，行轨里已关联的行画在主歌词行的位置（行 ID → 时间）；其他时间策略为空。 */
export function followSpans(
  branch: LyricBranchSummary | undefined,
  lines: readonly EditorLine[],
  primaryLines: readonly EditorLine[],
): ReadonlyMap<string, { startUs: number; endUs: number }> {
  if (branch?.timingMode !== 'followPrimary') return new Map();
  return new Map(trackLines('followPrimary', lines, primaryLines).map((l) => [l.lineId, { startUs: l.startUs, endUs: l.endUs }]));
}
