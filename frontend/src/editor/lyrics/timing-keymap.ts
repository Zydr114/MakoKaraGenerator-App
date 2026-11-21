// 动态打轴画布纯逻辑模块：
// - 模式 M1 快捷键解析与默认键位表（Space / J / K / L / Backspace / G / Q / W / Z / X / F2 / 方向键 / []）；
// - 分支实义拍点序列（跳过空白与标点，自动展开多拍注音分段）；
// - 契约 B1 分段加权扫字进度与无时间空白/标点跟亮桥接；
// - 高速轮指（J/K 交替）单调时间戳保护。

import type { LyricBeatTarget, RubyAnnotation, TimingStatus } from '../../protocol';
import { isTimed, type EditorLine, type EditorToken, type Selection } from '../model/lyrics';
import { rubyOf, segmentTimed } from '../model/ruby-segments';
import { fromLyricTime } from '../model/time';
import { graphemes } from '../model/token-segments';
import { classifyToken, hasPhraseEndHint, isContentToken, isLastContentToken } from '../model/token-semantics';
import { applyTagOffsetUs, type TimingActionId } from '../phonetics/timing-preferences';

export type LyricsWorkMode = 'timing' | 'edit' | 'quickEdit';

export interface BeatItem {
  /** 拍点唯一标识（tokenId 或 tokenId:segmentId）。 */
  key: string;
  target: LyricBeatTarget;
  selection: Selection;
  lineId: string;
  lineIndex: number;
  tokenId: string;
  tokenIndex: number;
  rubyId?: string;
  segmentId?: string;
  segmentIndex?: number;
  segmentCount: number;
  text: string;
  baseText: string;
  startUs?: number;
  endUs?: number;
  status?: TimingStatus;
  /** 该拍是否为行末或带有句中停顿收尾提示 ○ 的最后一拍。 */
  isPhraseEnd: boolean;
}

export function beatKeyOf(target: LyricBeatTarget): string {
  return target.segmentId ? `${target.lineId}:${target.tokenId}:${target.segmentId}` : `${target.lineId}:${target.tokenId}`;
}

export function sameBeatTarget(a: LyricBeatTarget | null | undefined, b: LyricBeatTarget | null | undefined): boolean {
  if (!a || !b) return false;
  return (
    a.branchId === b.branchId &&
    a.lineId === b.lineId &&
    a.tokenId === b.tokenId &&
    (a.rubyId ?? undefined) === (b.rubyId ?? undefined) &&
    (a.segmentId ?? undefined) === (b.segmentId ?? undefined)
  );
}

/**
 * 收集当前分支全部可打轴拍点：
 * - 自动跳过独立空白与标点 Token（遵守 Phase 0 词元语义规则）；
 * - 含 >= 2 个注音分段的汉字/词元展开为逐拍 RubyTextSegment；
 * - 无注音或仅 1 段注音的实义词元作为单拍基础 Token。
 */
export function collectBranchBeats(lines: readonly EditorLine[]): BeatItem[] {
  const beats: BeatItem[] = [];
  for (let lineIndex = 0; lineIndex < lines.length; lineIndex++) {
    const line = lines[lineIndex]!;
    for (let tokenIndex = 0; tokenIndex < line.tokens.length; tokenIndex++) {
      const token = line.tokens[tokenIndex]!;
      if (!isContentToken(token)) continue;
      const phraseEnd = hasPhraseEndHint(line.tokens, tokenIndex) || isLastContentToken(line.tokens, tokenIndex);
      const ruby = rubyOf(line, token.id);
      if (ruby && ruby.segments.length >= 2) {
        const count = ruby.segments.length;
        for (let segIdx = 0; segIdx < count; segIdx++) {
          const seg = ruby.segments[segIdx]!;
          const time = fromLyricTime(seg.time);
          const target: LyricBeatTarget = {
            branchId: line.branchId,
            lineId: line.id,
            tokenId: token.id,
            rubyId: ruby.id,
            segmentId: seg.id,
          };
          beats.push({
            key: beatKeyOf(target),
            target,
            selection: { lineId: line.id, tokenId: token.id, segmentId: seg.id },
            lineId: line.id,
            lineIndex,
            tokenId: token.id,
            tokenIndex,
            rubyId: ruby.id,
            segmentId: seg.id,
            segmentIndex: segIdx,
            segmentCount: count,
            text: seg.text,
            baseText: token.text,
            ...(time.startUs !== undefined ? { startUs: time.startUs } : {}),
            ...(time.endUs !== undefined ? { endUs: time.endUs } : {}),
            status: seg.timingStatus,
            isPhraseEnd: phraseEnd && segIdx === count - 1,
          });
        }
      } else {
        const target: LyricBeatTarget = {
          branchId: line.branchId,
          lineId: line.id,
          tokenId: token.id,
        };
        beats.push({
          key: beatKeyOf(target),
          target,
          selection: { lineId: line.id, tokenId: token.id },
          lineId: line.id,
          lineIndex,
          tokenId: token.id,
          tokenIndex,
          segmentCount: 1,
          text: token.text,
          baseText: token.text,
          ...(token.startUs !== undefined ? { startUs: token.startUs } : {}),
          ...(token.endUs !== undefined ? { endUs: token.endUs } : {}),
          ...(token.status !== undefined ? { status: token.status } : {}),
          isPhraseEnd: phraseEnd,
        });
      }
    }
  }
  return beats;
}

/** 将当前选择解析到拍点列表中的下标（若只选中行则定位到该行首个实义拍点）。 */
export function findBeatIndexForSelection(beats: readonly BeatItem[], selection: Selection | undefined): number {
  if (beats.length === 0) return -1;
  if (!selection) return 0;
  if (selection.tokenId && selection.segmentId) {
    const exact = beats.findIndex(
      (b) => b.lineId === selection.lineId && b.tokenId === selection.tokenId && b.segmentId === selection.segmentId,
    );
    if (exact >= 0) return exact;
  }
  if (selection.tokenId) {
    const byToken = beats.findIndex((b) => b.lineId === selection.lineId && b.tokenId === selection.tokenId);
    if (byToken >= 0) return byToken;
  }
  const byLine = beats.findIndex((b) => b.lineId === selection.lineId);
  return byLine >= 0 ? byLine : 0;
}

/** 在实义词元之间跨行移动（自动跳过空白与标点）。 */
export function stepContentToken(
  lines: readonly EditorLine[],
  selection: Selection | undefined,
  delta: 1 | -1,
): Selection | undefined {
  const contentList: Array<{ lineId: string; tokenId: string }> = [];
  for (const line of lines) {
    for (const token of line.tokens) {
      if (isContentToken(token)) contentList.push({ lineId: line.id, tokenId: token.id });
    }
  }
  if (contentList.length === 0) return undefined;
  if (!selection) return contentList[0];

  if (selection.tokenId) {
    const idx = contentList.findIndex((item) => item.lineId === selection.lineId && item.tokenId === selection.tokenId);
    if (idx >= 0) {
      const next = contentList[idx + delta];
      return next ? { lineId: next.lineId, tokenId: next.tokenId } : undefined;
    }
  }
  const firstInLine = contentList.find((item) => item.lineId === selection.lineId);
  if (firstInLine) return firstInLine;
  return contentList[0];
}

/** 在同一行内按拍点移动，或在全分支拍点列表中前进/后退一拍。 */
export function stepBranchBeat(
  beats: readonly BeatItem[],
  selection: Selection | undefined,
  delta: 1 | -1,
): BeatItem | undefined {
  if (beats.length === 0) return undefined;
  const idx = findBeatIndexForSelection(beats, selection);
  if (idx < 0) return beats[0];
  return beats[idx + delta];
}

/**
 * 契约 B1 扫字进度计算（与后端 LyricSampler.cs 保持 1:1 一致）：
 * - 若基础词元挂载 >= 2 个已计时注音分段，按各分段字素数权重做分段插值，拍间空隙静止等待；
 * - 否则按基础词元 [startUs, endUs] 线性插值；未定时返回 0。
 */
export function computeTokenWipeProgress(
  token: Pick<EditorToken, 'startUs' | 'endUs'>,
  ruby: RubyAnnotation | undefined,
  playheadUs: number,
): number {
  if (ruby) {
    const timedSegs: Array<{ startUs: number; endUs: number; weight: number }> = [];
    for (const seg of ruby.segments) {
      if (!segmentTimed(seg)) continue;
      const range = fromLyricTime(seg.time);
      if (!isTimed(range)) continue;
      timedSegs.push({
        startUs: range.startUs,
        endUs: range.endUs,
        weight: Math.max(1, graphemes(seg.text).length),
      });
    }
    if (timedSegs.length >= 2) {
      const totalWeight = timedSegs.reduce((sum, s) => sum + s.weight, 0);
      if (playheadUs <= timedSegs[0]!.startUs) return 0;
      let accWeight = 0;
      for (let i = 0; i < timedSegs.length; i++) {
        const seg = timedSegs[i]!;
        const wStart = accWeight / totalWeight;
        const wEnd = (accWeight + seg.weight) / totalWeight;
        accWeight += seg.weight;
        if (playheadUs < seg.startUs) {
          // 拍间微小静默空隙：保持上一拍结束权重，不倒退也不提前冲过。
          return wStart;
        }
        if (playheadUs < seg.endUs) {
          const span = seg.endUs - seg.startUs;
          const local = span > 0 ? Math.min(1, Math.max(0, (playheadUs - seg.startUs) / span)) : 1;
          return wStart + (wEnd - wStart) * local;
        }
      }
      return 1;
    }
  }

  if (!isTimed(token)) return 0;
  const span = token.endUs - token.startUs;
  if (span <= 0) return playheadUs >= token.endUs ? 1 : 0;
  return Math.min(1, Math.max(0, (playheadUs - token.startUs) / span));
}

/**
 * 契约 Phase 0 无时间空白/标点跟亮桥接（绝不作用于未定时实义字）：
 * - 行首前导无时间空白/标点：绑定到后方首个已定时实义词元，t >= first.startUs 时亮起；
 * - 句中/行尾无时间空白/标点：绑定到紧邻前驱已定时实义词元，t >= prev.endUs 时亮起。
 */
export function computeUntimedBridgeFill(
  tokens: readonly EditorToken[],
  tokenIndex: number,
  playheadUs: number,
): number {
  const token = tokens[tokenIndex];
  if (!token) return 0;
  const kind = classifyToken(token.text);
  if (kind === 'content') return 0;
  if (isTimed(token)) {
    return computeTokenWipeProgress(token, undefined, playheadUs);
  }

  for (let i = tokenIndex - 1; i >= 0; i--) {
    const prev = tokens[i]!;
    if (classifyToken(prev.text) === 'content' && isTimed(prev)) {
      return playheadUs >= prev.endUs ? 1 : 0;
    }
  }
  for (let i = tokenIndex + 1; i < tokens.length; i++) {
    const next = tokens[i]!;
    if (classifyToken(next.text) === 'content' && isTimed(next)) {
      return playheadUs >= next.startUs ? 1 : 0;
    }
  }
  return 0;
}

/** 计算单个注音分段的扫字填充进度（未单独定时时跟随基础词元）。 */
export function computeRubySegmentWipeProgress(
  segmentTime: { startUs?: number; endUs?: number },
  fallbackTokenProgress: number,
  playheadUs: number,
): number {
  if (!isTimed(segmentTime)) return fallbackTokenProgress;
  const span = segmentTime.endUs - segmentTime.startUs;
  if (span <= 0) return playheadUs >= segmentTime.endUs ? 1 : 0;
  return Math.min(1, Math.max(0, (playheadUs - segmentTime.startUs) / span));
}

/**
 * 计算应用延迟补偿后的连打落点微秒，并在高速双指轮指时保证严格递增（至少 +1ms），
 * 防止两次极快击键命中同一个音频时钟刻度导致 endUs <= startUs 被拒。
 */
export function computeMonotonicStrikeUs(
  rawPlayheadUs: number,
  tagOffsetMs: number,
  lastStrikeUs: number | null,
): number {
  const adjusted = applyTagOffsetUs(rawPlayheadUs, tagOffsetMs);
  if (lastStrikeUs !== null && adjusted <= lastStrikeUs) {
    return lastStrikeUs + 1000;
  }
  return adjusted;
}

/** 模式 M1 默认快捷键绑定表。 */
export const DEFAULT_KEYMAP: Readonly<Record<TimingActionId, readonly string[]>> = {
  togglePlay: [' '],
  strikeBeat: ['j', 'k'],
  closeBeat: ['l'],
  undoAndRewind: ['backspace', 'g'],
  speedDown: ['q'],
  speedUp: ['w'],
  seekBack: ['z'],
  seekForward: ['x'],
  openRubyPopover: ['f2'],
  prevLine: ['arrowup'],
  nextLine: ['arrowdown'],
  prevToken: ['arrowleft'],
  nextToken: ['arrowright'],
  prevBeat: ['arrowleft'],
  nextBeat: ['arrowright'],
};

export interface KeyEventLike {
  key: string;
  code?: string;
  ctrlKey: boolean;
  metaKey: boolean;
  altKey: boolean;
  shiftKey: boolean;
  repeat: boolean;
}

export type ResolvedKeyIntent =
  | { kind: 'strikeBeat' }
  | { kind: 'closeBeat' }
  | { kind: 'togglePhraseEndHint' }
  | { kind: 'undoAndRewind' }
  | { kind: 'clearSelectedTiming' }
  | { kind: 'speedDown'; fine: boolean }
  | { kind: 'speedUp'; fine: boolean }
  | { kind: 'seekBack' }
  | { kind: 'seekForward' }
  | { kind: 'openRubyPopover' }
  | { kind: 'prevLine' }
  | { kind: 'nextLine' }
  | { kind: 'prevToken' }
  | { kind: 'nextToken' }
  | { kind: 'prevBeat' }
  | { kind: 'nextBeat' }
  | { kind: 'nudgeTime'; frameDelta: -1 | 1 }
  | { kind: 'splitRubyBeat' }
  | { kind: 'mergeRubyBeat' }
  | { kind: 'splitLine' }
  | { kind: 'mergeLines' }
  | { kind: 'inlineEditLine' }
  | { kind: 'openTokenMenu' };

function matchesAction(
  event: KeyEventLike,
  action: TimingActionId,
  overrides?: Partial<Record<TimingActionId, string[]>>,
): boolean {
  const custom = overrides?.[action];
  const bindings = custom && custom.length > 0 ? custom : DEFAULT_KEYMAP[action];
  const keyLower = event.key.toLowerCase();
  const codeLower = (event.code ?? '').toLowerCase();
  return bindings.some((b) => {
    const norm = b.toLowerCase();
    return norm === keyLower || (codeLower !== '' && norm === codeLower);
  });
}

/**
 * 解析模式 M1 键盘事件意图（不含全局 Space / Ctrl+Z / Ctrl+Y / Ctrl+S）。
 * 在 quickEdit 模式下返回 null，将所有按键留给多行编辑器。
 */
export function resolveTimingKeyAction(
  event: KeyEventLike,
  mode: LyricsWorkMode,
  overrides?: Partial<Record<TimingActionId, string[]>>,
): ResolvedKeyIntent | null {
  if (mode === 'quickEdit') return null;

  const mod = event.ctrlKey || event.metaKey;
  const keyLower = event.key.toLowerCase();

  // Ctrl/Cmd + Enter in edit mode: inline line text editing
  if (mod && event.key === 'Enter' && !event.altKey && mode === 'edit') {
    return { kind: 'inlineEditLine' };
  }
  if (mod) return null;

  // F2: open inline ruby popover in both timing & edit modes
  if (!event.altKey && matchesAction(event, 'openRubyPopover', overrides)) {
    return event.repeat ? null : { kind: 'openRubyPopover' };
  }

  // Alt + Arrows: beat step in timing mode, frame nudge in edit mode
  if (event.altKey && !event.shiftKey) {
    if (event.key === 'ArrowLeft') {
      return mode === 'timing' ? { kind: 'prevBeat' } : { kind: 'nudgeTime', frameDelta: -1 };
    }
    if (event.key === 'ArrowRight') {
      return mode === 'timing' ? { kind: 'nextBeat' } : { kind: 'nudgeTime', frameDelta: 1 };
    }
    if (mode === 'edit' && event.key === 'ArrowUp') {
      return { kind: 'nudgeTime', frameDelta: -1 };
    }
    if (mode === 'edit' && event.key === 'ArrowDown') {
      return { kind: 'nudgeTime', frameDelta: 1 };
    }
    return null;
  }

  // Q / W: pitch-preserved speed step (Shift for fine ±5%)
  if (matchesAction(event, 'speedDown', overrides)) {
    return { kind: 'speedDown', fine: event.shiftKey };
  }
  if (matchesAction(event, 'speedUp', overrides)) {
    return { kind: 'speedUp', fine: event.shiftKey };
  }

  if (event.shiftKey) {
    // Shift+Enter in edit mode: merge current line with previous line
    if (mode === 'edit' && event.key === 'Enter') {
      return event.repeat ? null : { kind: 'mergeLines' };
    }
    return null;
  }

  // Z / X: seek -2s / +3s
  if (matchesAction(event, 'seekBack', overrides)) {
    return { kind: 'seekBack' };
  }
  if (matchesAction(event, 'seekForward', overrides)) {
    return { kind: 'seekForward' };
  }

  // [ / ]: split / merge ruby beats on current token
  if (event.key === '[') {
    return event.repeat ? null : { kind: 'splitRubyBeat' };
  }
  if (event.key === ']') {
    return event.repeat ? null : { kind: 'mergeRubyBeat' };
  }

  // Arrows: line & content token navigation
  if (matchesAction(event, 'prevLine', overrides)) return { kind: 'prevLine' };
  if (matchesAction(event, 'nextLine', overrides)) return { kind: 'nextLine' };
  if (matchesAction(event, 'prevToken', overrides)) return { kind: 'prevToken' };
  if (matchesAction(event, 'nextToken', overrides)) return { kind: 'nextToken' };

  // Mode-specific primary actions
  if (mode === 'timing') {
    if (matchesAction(event, 'strikeBeat', overrides)) {
      return event.repeat ? null : { kind: 'strikeBeat' };
    }
    if (matchesAction(event, 'closeBeat', overrides)) {
      return event.repeat ? null : { kind: 'closeBeat' };
    }
    if (matchesAction(event, 'undoAndRewind', overrides)) {
      return event.repeat ? null : { kind: 'undoAndRewind' };
    }
  } else if (mode === 'edit') {
    if (matchesAction(event, 'closeBeat', overrides)) {
      return event.repeat ? null : { kind: 'togglePhraseEndHint' };
    }
    if (event.key === 'Enter') {
      return event.repeat ? null : { kind: 'splitLine' };
    }
    if (event.key === 'Delete' || event.key === 'Backspace') {
      return event.repeat ? null : { kind: 'clearSelectedTiming' };
    }
    if (keyLower === 'j') {
      return event.repeat ? null : { kind: 'openTokenMenu' };
    }
  }

  return null;
}
