// 逐字预览（W2 设计 §2.4）：按播放头找出上一行、当前行、下一行，并把行切成带填充区间的段落。
// 只看文字和时间，不含任何样式；不依赖 React。

import { isTimed, type EditorLine, type EditorToken } from './lyrics';
import { fromLyricTime } from './time';

export type TimedLine = EditorLine & { startUs: number; endUs: number };

export interface PreviewWindow {
  previous?: TimedLine | undefined;
  current?: TimedLine | undefined;
  next?: TimedLine | undefined;
  /** 播放头在当前行之内；为 false 时当前行是即将开始的下一句。 */
  active: boolean;
}

/**
 * 播放头所在的行；不在任何行内时取下一句（未开始），全部唱完时取最后一句。
 * 只看已定时的行，上一行和下一行按文档顺序取已定时的相邻行。
 */
export function previewWindow(lines: readonly EditorLine[], us: number): PreviewWindow {
  const timed = lines.filter((line): line is TimedLine => isTimed(line));
  if (timed.length === 0) return { active: false };
  let index = timed.findIndex((line) => us >= line.startUs && us < line.endUs);
  const active = index >= 0;
  if (!active) {
    let upcoming = -1;
    timed.forEach((line, i) => {
      if (line.startUs > us && (upcoming < 0 || line.startUs < timed[upcoming]!.startUs)) upcoming = i;
    });
    index = upcoming >= 0 ? upcoming : timed.length - 1;
  }
  return { previous: timed[index - 1], current: timed[index], next: timed[index + 1], active };
}

/** 窗口的身份：三行的 ID 加是否在行内；不变时不必重新渲染。 */
export function windowKey(window: PreviewWindow): string {
  return [window.previous?.id, window.current?.id, window.next?.id, window.active].join('|');
}

/** 注音的一个片段（W6 设计 §2.5）：有时间的按自己的区间填充，没有时间的跟随所注的元素。 */
export interface PreviewRubySegment {
  text: string;
  fillStartUs: number;
  fillEndUs: number;
  timed: boolean;
}

/** 预览里的一段文字：一个元素，或元素之间不属于任何元素的原文（空格、标点）。 */
export interface PreviewSegment {
  text: string;
  token?: EditorToken;
  /** 填充区间：播放头从起点走到终点时填充比例从 0 到 1；起止相同表示到点即填满。 */
  fillStartUs: number;
  fillEndUs: number;
  /** 元素的注音片段；没有注音时省略。 */
  ruby?: PreviewRubySegment[];
}

function rubySegments(line: EditorLine, tokenId: string, fillStartUs: number, fillEndUs: number): PreviewRubySegment[] {
  return line.ruby
    .filter((r) => r.baseTokenId === tokenId)
    .flatMap((r) => (r.segments.length > 0 ? r.segments : [{ text: r.text, time: {} }]))
    .map((segment) => {
      const time = fromLyricTime(segment.time);
      return isTimed(time)
        ? { text: segment.text, fillStartUs: time.startUs, fillEndUs: time.endUs, timed: true }
        : { text: segment.text, fillStartUs, fillEndUs, timed: false };
    });
}

/**
 * 按行正文切出预览段落。行正文是权威原文，元素不一定覆盖空格和标点，
 * 所以按顺序在正文里找元素文字，中间的原文单独成段，跟随前一个元素填满。
 * 未定时元素没有自己的时间，整行唱完才填满。没有元素的行整行按行时间填充。
 */
export function previewSegments(line: TimedLine): PreviewSegment[] {
  if (line.tokens.length === 0) return [{ text: line.text, fillStartUs: line.startUs, fillEndUs: line.endUs }];
  const segments: PreviewSegment[] = [];
  let cursor = 0;
  let lastEndUs = line.startUs;
  const gap = (text: string) => text && segments.push({ text, fillStartUs: lastEndUs, fillEndUs: lastEndUs });
  for (const token of line.tokens) {
    const at = token.text ? line.text.indexOf(token.text, cursor) : -1;
    if (at >= 0) {
      gap(line.text.slice(cursor, at));
      cursor = at + token.text.length;
    }
    const [fillStartUs, fillEndUs] = isTimed(token) ? [token.startUs, token.endUs] : [line.endUs, line.endUs];
    const ruby = rubySegments(line, token.id, fillStartUs, fillEndUs);
    segments.push({ text: token.text, token, fillStartUs, fillEndUs, ...(ruby.length > 0 ? { ruby } : {}) });
    lastEndUs = fillEndUs;
  }
  gap(line.text.slice(cursor));
  return segments;
}

/** 填充比例（0～1）。 */
export function fillRatio(us: number, startUs: number, endUs: number): number {
  if (endUs <= startUs) return us >= endUs ? 1 : 0;
  return Math.min(1, Math.max(0, (us - startUs) / (endUs - startUs)));
}

/** 按主歌词行 ID 索引翻译行（只收翻译来源里有关联的行）。 */
export function translationsByPrimary(translationLines: Iterable<EditorLine>): Map<string, EditorLine[]> {
  const result = new Map<string, EditorLine[]>();
  for (const line of translationLines) {
    if (!line.primaryLineId) continue;
    const list = result.get(line.primaryLineId);
    if (list) list.push(line);
    else result.set(line.primaryLineId, [line]);
  }
  return result;
}
