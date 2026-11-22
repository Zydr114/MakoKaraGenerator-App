// 共享词元语义规则：与后端 MakoKaraGenerator.Core/Lyrics/LyricTokenSemantics.cs 1:1 对齐。
// 用于前端画布决定可打轴实义词元、跳过空白与标点，以及推导英/日邻接语境停顿提示 ○。

export type TokenKind = 'content' | 'whitespace' | 'punctuation';

export interface TokenLike {
  text: string;
  phraseEndHint?: boolean;
}

const WHITESPACE_ONLY = /^\s+$/u;
// CJK punctuation, ASCII punctuation, Unicode P* (excluding Japanese prolonged mark 'ー' U+30FC and iteration mark '々' U+3005 which are content!).
const PUNCTUATION_CHAR = /[\p{P}\p{S}]/u;

export function isCjkCodePoint(cp: number): boolean {
  return (
    (cp >= 0x3040 && cp <= 0x309f) || // Hiragana
    (cp >= 0x30a0 && cp <= 0x30ff) || // Katakana
    (cp >= 0x31f0 && cp <= 0x31ff) || // Katakana Phonetic Extensions
    (cp >= 0x3400 && cp <= 0x4dbf) || // CJK Unified Ideographs Extension A
    (cp >= 0x4e00 && cp <= 0x9fff) || // CJK Unified Ideographs
    (cp >= 0xf900 && cp <= 0xfaff) || // CJK Compatibility Ideographs
    (cp >= 0xff65 && cp <= 0xff9f) || // Halfwidth Katakana
    (cp >= 0xac00 && cp <= 0xd7af) || // Hangul Syllables
    (cp >= 0x1100 && cp <= 0x11ff) || // Hangul Jamo
    (cp >= 0x3130 && cp <= 0x318f) || // Hangul Compatibility Jamo
    cp === 0x3005 || // 々
    cp === 0x3006 || // 〆
    cp === 0x30fc // ー
  );
}

export function isContentKanaOrIdeographMark(cp: number): boolean {
  return cp === 0x30fc || cp === 0x3005 || cp === 0x3006 || cp === 0x309d || cp === 0x309e || cp === 0x30fd || cp === 0x30fe;
}

export function classifyToken(text: string): TokenKind {
  if (!text) return 'whitespace';
  if (WHITESPACE_ONLY.test(text)) return 'whitespace';
  let allPunct = true;
  for (const ch of text) {
    const cp = ch.codePointAt(0)!;
    if (isContentKanaOrIdeographMark(cp)) {
      allPunct = false;
      break;
    }
    if (!PUNCTUATION_CHAR.test(ch) && !/\s/u.test(ch)) {
      allPunct = false;
      break;
    }
  }
  return allPunct ? 'punctuation' : 'content';
}

export function isContentToken(token: TokenLike): boolean {
  return classifyToken(token.text) === 'content';
}

function endsWithCjk(text: string): boolean {
  let lastCp = -1;
  for (const ch of text) lastCp = ch.codePointAt(0)!;
  return lastCp >= 0 && isCjkCodePoint(lastCp);
}

function startsWithCjk(text: string): boolean {
  for (const ch of text) {
    const cp = ch.codePointAt(0)!;
    return isCjkCodePoint(cp);
  }
  return false;
}

/**
 * 推导实义词元按邻接语境是否具有句中停顿收尾提示 ○（与后端 LyricTokenSemantics.DerivePhraseEndHint 一致）。
 */
export function derivePhraseEndHint(tokens: readonly TokenLike[], index: number): boolean {
  const current = tokens[index];
  if (!current || classifyToken(current.text) !== 'content') return false;

  let nextContentIndex = -1;
  let hasWhitespaceGap = false;
  let hasPauseWhitespace = false;

  for (let i = index + 1; i < tokens.length; i++) {
    const kind = classifyToken(tokens[i]!.text);
    if (kind === 'content') {
      nextContentIndex = i;
      break;
    }
    if (kind === 'whitespace') {
      hasWhitespaceGap = true;
      const ws = tokens[i]!.text;
      // Fullwidth space U+3000 or consecutive spaces (>= 2 chars) or non-ASCII space => pause
      if (ws.length > 1 || ws !== ' ') {
        hasPauseWhitespace = true;
      }
    }
  }

  // Trailing whitespace/punctuation at line end does not produce a mid-line hint (line end has its own end-of-line cue).
  if (nextContentIndex < 0 || !hasWhitespaceGap) return false;
  if (hasPauseWhitespace) return true;

  // Single ASCII space U+0020: pause if adjacent to CJK, separator if between Latin/digit words.
  return endsWithCjk(current.text) || startsWithCjk(tokens[nextContentIndex]!.text);
}

/** 有效句中收尾提示：优先采用用户手动覆写 PhraseEndHint，否则按邻接语境推导。 */
export function hasPhraseEndHint(tokens: readonly TokenLike[], index: number): boolean {
  const current = tokens[index];
  if (!current || classifyToken(current.text) !== 'content') return false;
  if (current.phraseEndHint !== undefined) return current.phraseEndHint;
  return derivePhraseEndHint(tokens, index);
}

/** 判断指定下标是否为该行最后一个实义词元。 */
export function isLastContentToken(tokens: readonly TokenLike[], index: number): boolean {
  const current = tokens[index];
  if (!current || classifyToken(current.text) !== 'content') return false;
  for (let i = index + 1; i < tokens.length; i++) {
    if (classifyToken(tokens[i]!.text) === 'content') return false;
  }
  return true;
}

/** 判断文本是否包含至少一个汉字（CJK Unified / Extension A / Compatibility）。 */
export function containsKanji(text: string): boolean {
  for (const ch of text) {
    const cp = ch.codePointAt(0)!;
    if ((cp >= 0x4e00 && cp <= 0x9fff) || (cp >= 0x3400 && cp <= 0x4dbf) || (cp >= 0xf900 && cp <= 0xfaff)) {
      return true;
    }
  }
  return false;
}
