// 标准日语拍（Mora）分拍器：
// - 拗音（きゃ/きゅ/きょ/しゃ/ちゅ/ふぁ/てぃ 等）合并为 1 拍；
// - 独立假名、拨音 ん/ン、促音 っ/ッ、长音 う/い/ー 默认各为独立 1 拍；
// - 开启 mergeLongAndNasalInKanji 且基础词含汉字时，汉字读音内尾随长音与拨音并入前一拍（如 郷=きょう、感=かん）。

const SMALL_YOON_KANA = new Set([
  'ゃ', 'ゅ', 'ょ', 'ぁ', 'ぃ', 'ぅ', 'ぇ', 'ぉ', 'ゎ',
  'ャ', 'ュ', 'ョ', 'ァ', 'ィ', 'ゥ', 'ェ', 'ォ', 'ヮ', 'ヵ', 'ヶ',
]);

// O-row and U-row kana tails that prolong with う / ウ.
const O_OR_U_VOWEL_TAILS = new Set([
  'お', 'こ', 'ご', 'そ', 'ぞ', 'と', 'ど', 'の', 'ほ', 'ぼ', 'ぽ', 'も', 'よ', 'ろ', 'を', 'ょ', 'ぉ',
  'う', 'く', 'ぐ', 'す', 'ず', 'つ', 'づ', 'ぬ', 'ふ', 'ぶ', 'ぷ', 'む', 'ゆ', 'る', 'ゅ', 'ぅ', 'ゔ',
  'オ', 'コ', 'ゴ', 'ソ', 'ゾ', 'ト', 'ド', 'ノ', 'ホ', 'ボ', 'ポ', 'モ', 'ヨ', 'ロ', 'ヲ', 'ョ', 'ォ',
  'ウ', 'ク', 'グ', 'ス', 'ズ', 'ツ', 'ヅ', 'ヌ', 'フ', 'ブ', 'プ', 'ム', 'ユ', 'ル', 'ュ', 'ゥ', 'ヴ',
]);

// E-row kana tails that prolong with い / イ.
const E_VOWEL_TAILS = new Set([
  'え', 'け', 'げ', 'せ', 'ぜ', 'て', 'で', 'ね', 'へ', 'べ', 'ぺ', 'め', 'れ', 'ぇ',
  'エ', 'ケ', 'ゲ', 'セ', 'ゼ', 'テ', 'デ', 'ネ', 'ヘ', 'ベ', 'ペ', 'メ', 'レ', 'ェ',
]);

export interface MoraSplitOptions {
  /** 开启「合并汉字内长音/拨音」开关（默认 false）。 */
  mergeLongAndNasalInKanji?: boolean;
  /** 当前读音是否附着在汉字词元上（默认 true，便于工具函数独立调用）。 */
  isKanjiBase?: boolean;
}

export function katakanaToHiragana(text: string): string {
  let out = '';
  for (const ch of text) {
    const cp = ch.codePointAt(0)!;
    // Standard Katakana ァ(0x30A1)..ヶ(0x30F6) -> Hiragana ぁ(0x3041)..ゖ(0x3096); keep ー(0x30FC) unchanged.
    if (cp >= 0x30a1 && cp <= 0x30f6) {
      out += String.fromCodePoint(cp - 0x60);
    } else {
      out += ch;
    }
  }
  return out;
}

function shouldMergeIntoPrevious(prevMora: string, current: string): boolean {
  if (!prevMora) return false;
  // Nasal ん/ン or prolonged sound mark ー always merges into previous mora when option is on.
  if (current === 'ん' || current === 'ン' || current === 'ー') {
    return true;
  }
  const lastChar = Array.from(prevMora).at(-1) ?? '';
  if ((current === 'う' || current === 'ウ') && O_OR_U_VOWEL_TAILS.has(lastChar)) {
    return true;
  }
  if ((current === 'い' || current === 'イ') && E_VOWEL_TAILS.has(lastChar)) {
    return true;
  }
  return false;
}

/**
 * 按标准日语拍（Mora）规则拆分读音为多拍片段数组。
 */
export function splitJapaneseMora(reading: string, options: MoraSplitOptions = {}): string[] {
  const trimmed = reading.trim();
  if (!trimmed) return [];

  const chars = Array.from(trimmed);
  const morae: string[] = [];

  for (const ch of chars) {
    if (SMALL_YOON_KANA.has(ch) && morae.length > 0) {
      // Combine yoon small kana with preceding kana (e.g. き+ょ => きょ, テ+ィ => ティ).
      // Note: sokuon っ/ッ is NOT in SMALL_YOON_KANA, so it stays an independent 1 beat.
      morae[morae.length - 1] += ch;
    } else {
      morae.push(ch);
    }
  }

  const merge = Boolean(options.mergeLongAndNasalInKanji) && (options.isKanjiBase ?? true);
  if (!merge || morae.length <= 1) {
    return morae;
  }

  const merged: string[] = [];
  for (const mora of morae) {
    if (merged.length > 0 && shouldMergeIntoPrevious(merged[merged.length - 1]!, mora)) {
      merged[merged.length - 1] += mora;
    } else {
      merged.push(mora);
    }
  }
  return merged;
}

const EXPLICIT_RUBY_SEPARATOR = /[・,，/]+/u;

/** 判断手工注音输入是否包含显式拍点分隔符（・ / , / ， / /）。 */
export function hasExplicitRubySeparator(input: string): boolean {
  return EXPLICIT_RUBY_SEPARATOR.test(input);
}

/** 按显式分隔符拆分手动注音输入（保留非空段，去除段首尾空白）。 */
export function splitExplicitRubySegments(input: string): string[] {
  return input
    .split(EXPLICIT_RUBY_SEPARATOR)
    .map((part) => part.trim())
    .filter((part) => part.length > 0);
}

/** 将多拍注音片段格式化为弹窗展示字符串（默认以中点 `・` 连接）。 */
export function formatRubySegments(segments: readonly string[], separator = '・'): string {
  return segments.join(separator);
}
