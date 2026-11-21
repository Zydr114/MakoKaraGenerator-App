// 「全文快编」纯 Diff 引擎：
// - 支持纯文本与可选内联注音语法（如 `二人[ふ・た・り]`）双向序列化；
// - 执行行级精确 LCS + 字素级相似度对齐，准确生成 `lyrics.applyTextDiff` 所需的 `TextDiffLine[]`；
// - 实时计算预览统计（未变/修改/新增/删除行数，预计保留/重置词元数）。

import type { TextDiffLine } from '../../protocol';
import type { EditorLine } from '../model/lyrics';
import { rubyOf } from '../model/ruby-segments';
import { graphemes } from '../model/token-segments';
import { hasExplicitRubySeparator, splitExplicitRubySegments, splitJapaneseMora, type MoraSplitOptions } from '../phonetics/mora-splitter';

export type DiffLineStatus = 'unchanged' | 'modified' | 'added';

export interface InlineRubySpan {
  /** 在纯文本中的 UTF-16 起始偏移。 */
  start: number;
  /** 在纯文本中的 UTF-16 长度。 */
  length: number;
  /** 基础文本。 */
  baseText: string;
  /** 分拍注音列表。 */
  segments: string[];
}

export interface ParsedInlineRubyLine {
  plainText: string;
  spans: InlineRubySpan[];
}

export interface DiffPreviewRow {
  newLineIndex: number;
  text: string;
  status: DiffLineStatus;
  sourceLineId?: string;
  previousText?: string;
  inlineRubySpans: InlineRubySpan[];
}

export interface FullTextDiffResult {
  rows: DiffPreviewRow[];
  diffLines: TextDiffLine[];
  removedLines: Array<{ lineId: string; lineIndex: number; text: string }>;
  stats: {
    totalLines: number;
    unchangedLines: number;
    modifiedLines: number;
    addedLines: number;
    removedLines: number;
    estimatedPreservedTokens: number;
    estimatedResetTokens: number;
    hasTextChanges: boolean;
  };
}

/** 将整条分支序列化为纯文本（或带 `[ふ・た・り]` 内联注音语法的文本）。 */
export function serializeBranchText(lines: readonly EditorLine[], inlineRuby: boolean): string {
  return lines.map((line) => (inlineRuby ? serializeLineWithInlineRuby(line) : line.text)).join('\n');
}

export function serializeLineWithInlineRuby(line: EditorLine): string {
  if (line.tokens.length === 0 || line.ruby.length === 0) return line.text;
  // Verify tokens reconstruct line.text cleanly before emitting inline markers.
  if (line.tokens.map((t) => t.text).join('') !== line.text) return line.text;
  let out = '';
  for (const token of line.tokens) {
    out += token.text;
    const ruby = rubyOf(line, token.id);
    if (ruby && ruby.segments.length > 0) {
      out += `[${ruby.segments.map((s) => s.text).join('・')}]`;
    }
  }
  return out;
}

/**
 * 解析单行内联注音语法：`基础词[拍1・拍2]`（基础词取紧邻 `[` 之前的连续汉字块，或单个字素）。
 * 如果方括号不成对或内容为空，原样保留为普通正文字符。
 */
export function parseInlineRubyLine(rawLine: string, moraOptions: MoraSplitOptions = {}): ParsedInlineRubyLine {
  if (!rawLine.includes('[')) {
    return { plainText: rawLine, spans: [] };
  }

  let plainText = '';
  const spans: InlineRubySpan[] = [];
  let cursor = 0;

  while (cursor < rawLine.length) {
    const open = rawLine.indexOf('[', cursor);
    if (open < 0) {
      plainText += rawLine.slice(cursor);
      break;
    }
    const close = rawLine.indexOf(']', open + 1);
    if (close < 0) {
      plainText += rawLine.slice(cursor);
      break;
    }

    const before = rawLine.slice(cursor, open);
    const inside = rawLine.slice(open + 1, close).trim();
    if (!inside) {
      plainText += rawLine.slice(cursor, close + 1);
      cursor = close + 1;
      continue;
    }

    plainText += before;
    // Identify base span at the tail of plainText: prefer contiguous CJK kanji run, else last grapheme.
    const baseMatch = /([\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff々〆]+)$/u.exec(plainText);
    let baseText = '';
    if (baseMatch) {
      baseText = baseMatch[1]!;
    } else if (plainText.length > 0) {
      const gs = graphemes(plainText);
      baseText = gs[gs.length - 1] ?? '';
    }

    if (baseText) {
      const segments = hasExplicitRubySeparator(inside)
        ? splitExplicitRubySegments(inside)
        : splitJapaneseMora(inside, { ...moraOptions, isKanjiBase: true });
      if (segments.length > 0) {
        spans.push({
          start: plainText.length - baseText.length,
          length: baseText.length,
          baseText,
          segments,
        });
      }
    } else {
      // Nothing preceding `[` — preserve literally.
      plainText += rawLine.slice(open, close + 1);
    }
    cursor = close + 1;
  }

  return { plainText, spans };
}

/** 计算两个字素序列的 LCS 长度（O(m*n)，带长度上限防超长行卡顿）。 */
export function graphemeLcsLength(a: readonly string[], b: readonly string[]): number {
  const m = a.length;
  const n = b.length;
  if (m === 0 || n === 0) return 0;
  if (m * n > 40_000) {
    // Fallback bag overlap for pathological ultra-long lines.
    const counts = new Map<string, number>();
    for (const ch of a) counts.set(ch, (counts.get(ch) ?? 0) + 1);
    let overlap = 0;
    for (const ch of b) {
      const rem = counts.get(ch) ?? 0;
      if (rem > 0) {
        counts.set(ch, rem - 1);
        overlap++;
      }
    }
    return overlap;
  }

  let prev = new Uint16Array(n + 1);
  let curr = new Uint16Array(n + 1);
  for (let i = 1; i <= m; i++) {
    const ai = a[i - 1]!;
    for (let j = 1; j <= n; j++) {
      curr[j] = ai === b[j - 1]! ? prev[j - 1]! + 1 : Math.max(prev[j]!, curr[j - 1]!);
    }
    [prev, curr] = [curr, prev];
    curr.fill(0);
  }
  return prev[n]!;
}

/** 精确字符串数组 LCS，返回严格匹配的 (oldIdx, newIdx) 锚点对。 */
function exactLineLcs(oldTexts: readonly string[], newTexts: readonly string[]): Array<[number, number]> {
  const m = oldTexts.length;
  const n = newTexts.length;
  if (m === 0 || n === 0) return [];
  const dp: Uint16Array[] = Array.from({ length: m + 1 }, () => new Uint16Array(n + 1));
  for (let i = m - 1; i >= 0; i--) {
    const row = dp[i]!;
    const nextRow = dp[i + 1]!;
    const oldText = oldTexts[i]!;
    for (let j = n - 1; j >= 0; j--) {
      row[j] = oldText === newTexts[j]! ? nextRow[j + 1]! + 1 : Math.max(nextRow[j]!, row[j + 1]!);
    }
  }
  const anchors: Array<[number, number]> = [];
  let i = 0;
  let j = 0;
  while (i < m && j < n) {
    if (oldTexts[i] === newTexts[j]) {
      anchors.push([i, j]);
      i++;
      j++;
    } else if (dp[i + 1]![j]! >= dp[i]![j + 1]!) {
      i++;
    } else {
      j++;
    }
  }
  return anchors;
}

/**
 * 计算全文快编 Diff：
 * 1. 先按行正文精确 LCS 锚定未改动行；
 * 2. 在相邻锚点之间的变更区间内，按字素 LCS 相似度贪心/顺序配对修改行，保留 `sourceLineId`；
 * 3. 估算保留/重置的词元数，并输出 `lyrics.applyTextDiff` 载荷。
 */
export function computeFullTextDiff(
  existingLines: readonly EditorLine[],
  draftText: string,
  options: { inlineRuby?: boolean; moraOptions?: MoraSplitOptions } = {},
): FullTextDiffResult {
  const normalized = draftText.replace(/\r\n?/g, '\n');
  const rawNewLines = normalized === '' ? [] : normalized.split('\n');
  const parsedNew = rawNewLines.map((raw) =>
    options.inlineRuby ? parseInlineRubyLine(raw, options.moraOptions) : { plainText: raw, spans: [] },
  );

  const oldTexts = existingLines.map((l) => l.text);
  const newTexts = parsedNew.map((p) => p.plainText);
  const anchors = exactLineLcs(oldTexts, newTexts);

  // Pair each new line index to an existing line index (or -1 if added).
  const matchedOldForNew = new Int32Array(newTexts.length).fill(-1);
  const matchedOldSet = new Set<number>();

  const boundaries: Array<[number, number]> = [...anchors, [oldTexts.length, newTexts.length]];
  let prevOld = 0;
  let prevNew = 0;

  for (const [anchorOld, anchorNew] of boundaries) {
    // Gap [prevOld..anchorOld) vs [prevNew..anchorNew): pair modified lines by grapheme similarity in order.
    const oldGap: number[] = [];
    for (let oi = prevOld; oi < anchorOld; oi++) oldGap.push(oi);
    const newGap: number[] = [];
    for (let ni = prevNew; ni < anchorNew; ni++) newGap.push(ni);

    if (oldGap.length > 0 && newGap.length > 0) {
      const oldGraphemes = oldGap.map((oi) => graphemes(oldTexts[oi]!));
      const newGraphemes = newGap.map((ni) => graphemes(newTexts[ni]!));
      let oldCursor = 0;
      for (let gni = 0; gni < newGap.length && oldCursor < oldGap.length; gni++) {
        let bestOffset = -1;
        let bestScore = 0;
        const ng = newGraphemes[gni]!;
        for (let goi = oldCursor; goi < oldGap.length; goi++) {
          const og = oldGraphemes[goi]!;
          const maxLen = Math.max(og.length, ng.length);
          if (maxLen === 0) continue;
          const score = graphemeLcsLength(og, ng) / maxLen;
          if (score > bestScore) {
            bestScore = score;
            bestOffset = goi;
          }
        }
        // Pair when >= 25% grapheme overlap OR 1-to-1 single-line in-place replacement.
        if (bestOffset >= 0 && (bestScore >= 0.25 || (oldGap.length === 1 && newGap.length === 1))) {
          const oi = oldGap[bestOffset]!;
          const ni = newGap[gni]!;
          matchedOldForNew[ni] = oi;
          matchedOldSet.add(oi);
          oldCursor = bestOffset + 1;
        }
      }
    }

    if (anchorOld < oldTexts.length && anchorNew < newTexts.length) {
      matchedOldForNew[anchorNew] = anchorOld;
      matchedOldSet.add(anchorOld);
    }
    prevOld = anchorOld + 1;
    prevNew = anchorNew + 1;
  }

  let unchangedLines = 0;
  let modifiedLines = 0;
  let addedLines = 0;
  let estimatedPreservedTokens = 0;
  let estimatedResetTokens = 0;

  const rows: DiffPreviewRow[] = [];
  const diffLines: TextDiffLine[] = [];

  for (let ni = 0; ni < newTexts.length; ni++) {
    const text = newTexts[ni]!;
    const oi = matchedOldForNew[ni]!;
    const spans = parsedNew[ni]!.spans;

    if (oi >= 0) {
      const oldLine = existingLines[oi]!;
      const unchanged = oldLine.text === text;
      if (unchanged) {
        unchangedLines++;
        estimatedPreservedTokens += oldLine.tokens.length;
      } else {
        modifiedLines++;
        const og = graphemes(oldLine.text);
        const ng = graphemes(text);
        const common = graphemeLcsLength(og, ng);
        const ratio = og.length > 0 ? common / og.length : 0;
        const kept = Math.round(oldLine.tokens.length * ratio);
        estimatedPreservedTokens += kept;
        estimatedResetTokens += Math.max(0, ng.length - common);
      }
      rows.push({
        newLineIndex: ni,
        text,
        status: unchanged ? 'unchanged' : 'modified',
        sourceLineId: oldLine.id,
        ...(unchanged ? {} : { previousText: oldLine.text }),
        inlineRubySpans: spans,
      });
      diffLines.push({ text, sourceLineId: oldLine.id });
    } else {
      addedLines++;
      estimatedResetTokens += graphemes(text).length;
      rows.push({
        newLineIndex: ni,
        text,
        status: 'added',
        inlineRubySpans: spans,
      });
      diffLines.push({ text });
    }
  }

  const removedLines: Array<{ lineId: string; lineIndex: number; text: string }> = [];
  for (let oi = 0; oi < existingLines.length; oi++) {
    if (!matchedOldSet.has(oi)) {
      removedLines.push({ lineId: existingLines[oi]!.id, lineIndex: oi, text: existingLines[oi]!.text });
    }
  }

  const hasTextChanges =
    modifiedLines > 0 ||
    addedLines > 0 ||
    removedLines.length > 0 ||
    existingLines.length !== newTexts.length ||
    existingLines.some((line, idx) => line.id !== rows[idx]?.sourceLineId);

  return {
    rows,
    diffLines,
    removedLines,
    stats: {
      totalLines: newTexts.length,
      unchangedLines,
      modifiedLines,
      addedLines,
      removedLines: removedLines.length,
      estimatedPreservedTokens,
      estimatedResetTokens,
      hasTextChanges,
    },
  };
}
