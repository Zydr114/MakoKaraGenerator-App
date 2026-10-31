// 来源管理与翻译关联的纯函数（整体设计 §5.2、规范 §6.7）。
// 主来源不能删除、不能改角色，非主来源不能改成主来源；关联只能指向主来源里的行。

import type { LyricBranchRole, LyricBranchSummary, LyricBranchTimingMode } from '../../protocol';
import type { EditorLine } from './lyrics';

export const BRANCH_ROLE_TEXT: Record<LyricBranchRole, string> = { primary: '主歌词', translation: '翻译', annotation: '注音', custom: '自定义' };

export const TIMING_MODE_TEXT: Record<LyricBranchTimingMode, string> = {
  independent: '独立时间',
  followPrimary: '跟随主歌词',
  none: '不计时',
};

/** 来源一行摘要：角色 · 语言 · 时间策略 · 行数。 */
export function branchDescription(branch: LyricBranchSummary): string {
  return [BRANCH_ROLE_TEXT[branch.role], branch.language, TIMING_MODE_TEXT[branch.timingMode], `${branch.lineCount} 行`].filter(Boolean).join(' · ');
}

/** 设置面板里可选的角色：主来源只能是主歌词；其他来源不能改成主歌词。 */
export function roleOptions(isPrimary: boolean): LyricBranchRole[] {
  return isPrimary ? ['primary'] : ['translation', 'annotation', 'custom'];
}

/** 可选的时间策略：主来源“跟随主歌词”没有意义，不提供；当前值总在列表里。 */
export function timingModeOptions(isPrimary: boolean, current: LyricBranchTimingMode): LyricBranchTimingMode[] {
  const modes: LyricBranchTimingMode[] = isPrimary ? ['independent', 'none'] : ['independent', 'followPrimary', 'none'];
  return modes.includes(current) ? modes : [...modes, current];
}

/** 关联草稿：翻译行 ID → 主歌词行 ID，null 表示不关联。 */
export type LinkDraft = Readonly<Record<string, string | null>>;

export function currentLinks(lines: readonly EditorLine[]): LinkDraft {
  return Object.fromEntries(lines.map((l) => [l.id, l.primaryLineId ?? null]));
}

/** 按顺序自动配对：第 i 行关联主歌词第 i 行；主歌词行不够时多出的行不关联。 */
export function autoPair(lines: readonly EditorLine[], primaryLines: readonly EditorLine[]): LinkDraft {
  return Object.fromEntries(lines.map((l, i) => [l.id, primaryLines[i]?.id ?? null]));
}

/** 草稿相对当前关联的修改，按行顺序。 */
export function linkChanges(lines: readonly EditorLine[], draft: LinkDraft): Array<{ lineId: string; primaryLineId: string | null }> {
  return lines
    .filter((l) => l.id in draft && (draft[l.id] ?? null) !== (l.primaryLineId ?? null))
    .map((l) => ({ lineId: l.id, primaryLineId: draft[l.id] ?? null }));
}

/** 行 ID → 行号（从 1 开始）。 */
export function lineNumbers(lines: readonly EditorLine[]): Map<string, number> {
  return new Map(lines.map((l, i) => [l.id, i + 1]));
}
