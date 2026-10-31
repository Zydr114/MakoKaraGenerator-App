// 字体选择与字体检查（W7 设计 §2.5）：在后端字体目录里搜索、标出目录里没有的字体、把逐字检查结果归成连续的段。不依赖 React。

import type { FontElementDiagnostic } from '../../protocol';

/** 字体检查默认带上的文字：工坊样本里的歌词与注音（后端 TemplateSampleScene.Workshop）。 */
export const FONT_CHECK_SAMPLE = '模板预览 mó 喜欢喜欢 光在远方';
/** 后端限制：字体名最多 256 个字符，回退字体最多 32 个，检查文字最多 100000 个 UTF-16 单元。 */
export const MAX_FAMILY_LENGTH = 256;
export const MAX_FALLBACKS = 32;
export const MAX_CHECK_TEXT = 100_000;

/** 搜索字体目录：不区分大小写，开头匹配的排在前面；没有输入时按目录顺序。最多 limit 个。 */
export function searchFamilies(families: readonly string[], query: string, limit = 50): { items: string[]; total: number } {
  const q = query.trim().toLowerCase();
  if (!q) return { items: families.slice(0, limit), total: families.length };
  const prefix: string[] = [];
  const inside: string[] = [];
  for (const family of families) {
    const name = family.toLowerCase();
    if (name.startsWith(q)) prefix.push(family);
    else if (name.includes(q)) inside.push(family);
  }
  const matched = [...prefix, ...inside];
  return { items: matched.slice(0, limit), total: matched.length };
}

/** 目录里有没有这个字体（不区分大小写，与后端按名称找字体一致）。 */
export function hasFamily(families: readonly string[], name: string): boolean {
  const n = name.trim().toLowerCase();
  return families.some((f) => f.toLowerCase() === n);
}

/** 目录里的写法（大小写以目录为准）；没有时原样返回。 */
export function catalogName(families: readonly string[], name: string): string {
  const n = name.trim().toLowerCase();
  return families.find((f) => f.toLowerCase() === n) ?? name.trim();
}

export interface FontRun {
  text: string;
  family: string;
  isFallback: boolean;
  hasMissingGlyph: boolean;
}

/**
 * 检查结果归成段。后端只列出回退或缺字的元素（Skia 字体检查），其余元素用主字体：
 * 序号相邻、实际字体、是否回退、是否缺字都相同的元素合在一起；只有空白的段不列。
 */
export function fontRuns(elements: readonly FontElementDiagnostic[]): FontRun[] {
  const runs: (FontRun & { lastIndex: number })[] = [];
  for (const e of [...elements].sort((a, b) => a.elementIndex - b.elementIndex)) {
    const last = runs.at(-1);
    if (last && last.lastIndex + 1 === e.elementIndex && last.family === e.actualFamily && last.isFallback === e.isFallback && last.hasMissingGlyph === e.hasMissingGlyph) {
      last.text += e.text;
      last.lastIndex = e.elementIndex;
    } else runs.push({ text: e.text, family: e.actualFamily, isFallback: e.isFallback, hasMissingGlyph: e.hasMissingGlyph, lastIndex: e.elementIndex });
  }
  return runs.filter((r) => r.text.trim() !== '').map(({ lastIndex: _lastIndex, ...run }) => run);
}

export interface MarkedSegment {
  text: string;
  status: 'main' | 'fallback' | 'missing';
  /** 回退或缺字时的实际字体。 */
  family?: string;
}

/** 把检查过的整段文字按结果标出：列出的元素按回退或缺字，其余是主字体；相邻同类合并。 */
export function markText(text: string, elements: readonly FontElementDiagnostic[]): MarkedSegment[] {
  const segments: MarkedSegment[] = [];
  const push = (part: string, status: MarkedSegment['status'], family?: string) => {
    if (!part) return;
    const last = segments.at(-1);
    if (last && last.status === status && last.family === family) last.text += part;
    else segments.push({ text: part, status, ...(family !== undefined ? { family } : {}) });
  };
  let at = 0;
  for (const e of [...elements].sort((a, b) => a.utf16Start - b.utf16Start)) {
    if (e.utf16Start < at) continue;
    push(text.slice(at, e.utf16Start), 'main');
    push(text.slice(e.utf16Start, e.utf16Start + e.text.length), e.hasMissingGlyph ? 'missing' : 'fallback', e.actualFamily);
    at = e.utf16Start + e.text.length;
  }
  push(text.slice(at), 'main');
  return segments;
}

/** 检查用的文字：样本加上用户输入的一段，超过上限时截断。 */
export function fontCheckText(extra: string): string {
  const text = extra.trim() ? `${FONT_CHECK_SAMPLE} ${extra.trim()}` : FONT_CHECK_SAMPLE;
  return text.slice(0, MAX_CHECK_TEXT);
}
