// 编辑器侧的歌词缓存形状与导航。
// 来源有两种：原最小投影（lyrics/lines，无时间状态与注音）和 S1 可编辑窗口（lyrics/window，合同 1.1.0）。
// 两者都归一成 EditorLine；没有的字段为 undefined，界面据此隐藏，不猜测。

import type { EditableLyricLine, LyricLine, RubyAnnotation, TimingStatus } from '../../protocol';
import { fromLyricTime } from './time';

export interface EditorToken {
  id: string;
  lineId: string;
  text: string;
  startUs?: number;
  endUs?: number;
  /** 最小投影没有时间状态。 */
  status?: TimingStatus;
  /** 句中停顿收尾提示覆写（undefined 表示按邻接语境自动推导）。 */
  phraseEndHint?: boolean;
}

export interface EditorLine {
  branchId: string;
  id: string;
  text: string;
  startUs?: number;
  endUs?: number;
  status?: TimingStatus;
  /** 翻译等来源的行关联到的主歌词行。 */
  primaryLineId?: string;
  tokens: EditorToken[];
  ruby: RubyAnnotation[];
}

export function fromMinimalLine(line: LyricLine): EditorLine {
  return {
    branchId: line.branchId,
    id: line.id,
    text: line.text,
    ...fromLyricTime(line.time),
    tokens: line.tokens.map((token) => ({ id: token.id, lineId: line.id, text: token.text, ...fromLyricTime(token.time) })),
    ruby: [],
  };
}

export function fromEditableLine(line: EditableLyricLine): EditorLine {
  return {
    branchId: line.branchId,
    id: line.id,
    text: line.text,
    ...fromLyricTime(line.time),
    status: line.timingStatus,
    ...(line.primaryLineId ? { primaryLineId: line.primaryLineId } : {}),
    tokens: line.tokens.map((token) => ({
      id: token.id,
      lineId: line.id,
      text: token.text,
      ...fromLyricTime(token.time),
      status: token.timingStatus,
      ...(token.phraseEndHint !== undefined ? { phraseEndHint: token.phraseEndHint } : {}),
    })),
    ruby: line.rubyAnnotations,
  };
}

export function isTimed(item: { startUs?: number; endUs?: number }): item is { startUs: number; endUs: number } {
  return item.startUs !== undefined && item.endUs !== undefined;
}

/** 选择：来源内的行，可选该行的一个元素；选了元素时还可以选它的一个注音片段（W6 设计 §2.1）。 */
export interface Selection {
  lineId: string;
  tokenId?: string;
  segmentId?: string;
}

export interface TokenPosition {
  lineIndex: number;
  tokenIndex: number;
}

/** 按稳定 ID 定位元素；找不到返回 undefined（选择已失效）。 */
export function locateToken(lines: readonly EditorLine[], tokenId: string): TokenPosition | undefined {
  for (let lineIndex = 0; lineIndex < lines.length; lineIndex++) {
    const tokenIndex = lines[lineIndex]!.tokens.findIndex((t) => t.id === tokenId);
    if (tokenIndex >= 0) return { lineIndex, tokenIndex };
  }
  return undefined;
}

/** 文档顺序中的下一个／上一个元素（跨行）；到头返回 undefined。没有元素的行被跳过。 */
export function stepToken(lines: readonly EditorLine[], tokenId: string, delta: 1 | -1): EditorToken | undefined {
  const at = locateToken(lines, tokenId);
  if (!at) return undefined;
  let { lineIndex, tokenIndex } = at;
  tokenIndex += delta;
  while (lineIndex >= 0 && lineIndex < lines.length) {
    const tokens = lines[lineIndex]!.tokens;
    if (tokenIndex >= 0 && tokenIndex < tokens.length) return tokens[tokenIndex];
    lineIndex += delta;
    const next = lines[lineIndex];
    if (!next) return undefined;
    tokenIndex = delta === 1 ? 0 : next.tokens.length - 1;
  }
  return undefined;
}

/** 时间点所在的行（取第一个包含它的已定时行）。 */
export function lineAt(lines: readonly EditorLine[], us: number): EditorLine | undefined {
  return lines.find((line) => isTimed(line) && us >= line.startUs && us < line.endUs);
}

/** 时间点所在的元素。 */
export function tokenAt(line: EditorLine, us: number): EditorToken | undefined {
  return line.tokens.find((token) => isTimed(token) && us >= token.startUs && us < token.endUs);
}

/** 选择在新数据中是否仍有效；失效的部分被去掉。 */
export function reconcileSelection(lines: readonly EditorLine[], selection: Selection | undefined): Selection | undefined {
  if (!selection) return undefined;
  const line = lines.find((l) => l.id === selection.lineId);
  if (!line) return undefined;
  if (selection.tokenId && !line.tokens.some((t) => t.id === selection.tokenId)) return { lineId: line.id };
  if (selection.segmentId) {
    const ruby = line.ruby.find((r) => r.baseTokenId === selection.tokenId);
    if (!selection.tokenId || !ruby?.segments.some((s) => s.id === selection.segmentId)) return { lineId: line.id, ...(selection.tokenId ? { tokenId: selection.tokenId } : {}) };
  }
  return selection;
}

export const TIMING_STATUS_TEXT: Record<TimingStatus, string> = {
  missing: '未打轴',
  imported: '导入',
  estimated: '估算',
  edited: '已修改',
  confirmed: '已确认',
  invalid: '无效',
};
