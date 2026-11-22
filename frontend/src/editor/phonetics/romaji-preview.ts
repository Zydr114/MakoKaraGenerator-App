// 基于 wanakana（MIT）的罗马音实时预览引擎：
// - 小写拉丁字母实时预览为平假名，大写预览为片假名；
// - Enter 采纳假名并按显式分隔符或标准日语 Mora 规则分拍；
// - Shift+Enter 跳过假名转换、原样提交拉丁字母（兼顾英文音标与手动中文拼音注音）。

import { toKana } from 'wanakana';
import {
  formatRubySegments,
  hasExplicitRubySeparator,
  splitExplicitRubySegments,
  splitJapaneseMora,
  type MoraSplitOptions,
} from './mora-splitter';

const HAS_ASCII_LATIN = /[A-Za-z]/;

export interface RomajiPreviewResult {
  /** 原始输入中是否包含拉丁字母。 */
  hasLatin: boolean;
  /** 转换后的完整假名串（不含分隔符）。 */
  kanaText: string;
  /** 按 Enter 提交时的多拍注音分段（假名 + Mora 或显式分隔）。 */
  kanaSegments: string[];
  /** 按 Shift+Enter 提交时的原文分段（保留拉丁/拼音，仅按显式分隔符切分）。 */
  rawSegments: string[];
  /** 浮窗旁侧展示的实时预览文案（如 `きょ・う`），无需预览时为空字符串。 */
  previewLabel: string;
}

function convertSegmentToKana(part: string): string {
  if (!HAS_ASCII_LATIN.test(part)) return part;
  // wanakana.toKana: lowercase -> hiragana, UPPERCASE -> katakana, preserving existing kana.
  return toKana(part, { IMEMode: false });
}

export function previewRomajiInput(rawInput: string, options: MoraSplitOptions = {}): RomajiPreviewResult {
  const trimmed = rawInput.trim();
  if (!trimmed) {
    return {
      hasLatin: false,
      kanaText: '',
      kanaSegments: [],
      rawSegments: [],
      previewLabel: '',
    };
  }

  const hasLatin = HAS_ASCII_LATIN.test(trimmed);
  const explicit = hasExplicitRubySeparator(trimmed);

  // Raw segments for Shift+Enter (keep original Latin/pinyin text verbatim).
  const rawSegments = explicit ? splitExplicitRubySegments(trimmed) : [trimmed];

  // Kana segments for Enter.
  let kanaSegments: string[];
  if (explicit) {
    kanaSegments = splitExplicitRubySegments(trimmed)
      .map((part) => convertSegmentToKana(part))
      .filter((part) => part.length > 0);
  } else if (hasLatin) {
    const converted = convertSegmentToKana(trimmed);
    kanaSegments = splitJapaneseMora(converted, options);
  } else {
    kanaSegments = splitJapaneseMora(trimmed, options);
  }

  const kanaText = kanaSegments.join('');
  const previewLabel = hasLatin && kanaSegments.length > 0 ? formatRubySegments(kanaSegments) : '';

  return {
    hasLatin,
    kanaText,
    kanaSegments,
    rawSegments,
    previewLabel,
  };
}
