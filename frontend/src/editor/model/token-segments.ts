// 文本单元结构编辑的前端校验（规范 §6.8）：拆开要求至少两个 Unicode 文本元素；
// 按规则重切本行时，用户用“|”标出切分位置，片段必须精确拼回原正文且落在文本元素边界上。

import type { EditorLine } from './lyrics';

const segmenter = new Intl.Segmenter(undefined, { granularity: 'grapheme' });

/** Unicode 文本元素（用户看到的“一个字”）。 */
export function graphemes(text: string): string[] {
  return Array.from(segmenter.segment(text), (s) => s.segment);
}

/** 切分符号。 */
export const SEPARATOR = '|';

/**
 * 当前的切分：按文本单元顺序在正文里找，元素之间的原文（空格、标点）并入前一段，开头的并入第一段。
 * 元素拼不回正文时只返回整行一段，不猜测。
 */
export function currentSegments(line: Pick<EditorLine, 'text' | 'tokens'>): string[] {
  if (line.text === '') return [];
  const segments: string[] = [];
  let cursor = 0;
  for (const token of line.tokens) {
    const at = token.text ? line.text.indexOf(token.text, cursor) : -1;
    if (at < 0) return [line.text];
    const end = at + token.text.length;
    if (segments.length === 0) {
      segments.push(line.text.slice(0, end));
    } else {
      segments[segments.length - 1] += line.text.slice(cursor, at);
      segments.push(token.text);
    }
    cursor = end;
  }
  if (segments.length === 0) return [line.text];
  segments[segments.length - 1] += line.text.slice(cursor);
  return segments;
}

export type ParsedSegments = { ok: true; segments: string[] } | { ok: false; error: string };

/** 解析带“|”的输入：空片段忽略；去掉“|”后必须与正文完全一致，切分位置必须在文本元素边界上。 */
export function parseSegments(input: string, original: string): ParsedSegments {
  if (original.includes(SEPARATOR)) return { ok: false, error: `正文里含有“${SEPARATOR}”，不能用它标出切分位置` };
  const segments = input.split(SEPARATOR).filter((s) => s !== '');
  if (segments.join('') !== original) return { ok: false, error: `去掉“${SEPARATOR}”后必须与正文完全一致` };
  const boundaries = new Set<number>();
  let offset = 0;
  for (const g of graphemes(original)) boundaries.add((offset += g.length));
  offset = 0;
  for (const segment of segments.slice(0, -1)) {
    offset += segment.length;
    if (!boundaries.has(offset)) return { ok: false, error: '切分位置不能落在一个字的中间' };
  }
  return { ok: true, segments };
}
