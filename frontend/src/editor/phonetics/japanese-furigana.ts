// 日语自动注音引擎：
// - 本地用户读音词典最高优先级覆盖（最长匹配）；
// - 送假名剥离（見よう -> 見[み] + よう、思い出 -> 思[おも] + い + 出[で]、取り戻す -> 取[と] + り + 戻[も・ど] + す）；
// - 熟字训与复合汉字词识别（二人 -> 二人[ふ・た・り]、大人 -> 大人[お・と・な]）；
// - 默认按标准日语 Mora 分拍，支持「合并汉字内长音/拨音」开关；
// - 通过 Web Worker 懒加载 @patdx/kuromoji，主线程零阻塞。

import { containsKanji } from '../model/token-semantics';
import { katakanaToHiragana, splitJapaneseMora, type MoraSplitOptions } from './mora-splitter';
import { normalizeUserEntries, type UserDictionaryEntry } from './user-dictionary';
import type { FuriganaWorkerMorpheme, FuriganaWorkerRequest, FuriganaWorkerResponse } from './japanese-furigana.worker';

export interface FuriganaSpan {
  surface: string;
  /** 无注音时省略（如假名、送假名、英数、空白标点）。 */
  rubySegments?: string[];
  isKanji: boolean;
  source: 'user-dict' | 'kuromoji' | 'none';
}

export interface FuriganaOptions extends MoraSplitOptions {
  /** 用户读音词典（最高优先级覆盖）。 */
  userDictionary?: readonly UserDictionaryEntry[];
}

function isKanaChar(ch: string): boolean {
  const cp = ch.codePointAt(0)!;
  return (cp >= 0x3040 && cp <= 0x309f) || (cp >= 0x30a0 && cp <= 0x30ff) || cp === 0x30fc;
}

function isKanjiOrIteration(ch: string): boolean {
  const cp = ch.codePointAt(0)!;
  return (
    (cp >= 0x4e00 && cp <= 0x9fff) ||
    (cp >= 0x3400 && cp <= 0x4dbf) ||
    (cp >= 0xf900 && cp <= 0xfaff) ||
    cp === 0x3005 || // 々
    cp === 0x3006 // 〆
  );
}

interface SurfaceRun {
  text: string;
  isKana: boolean;
  hiragana: string;
}

function splitIntoKanaAndNonKanaRuns(surface: string): SurfaceRun[] {
  const chars = Array.from(surface);
  const runs: SurfaceRun[] = [];
  for (const ch of chars) {
    const kana = isKanaChar(ch);
    const hira = kana ? katakanaToHiragana(ch) : '';
    const prev = runs[runs.length - 1];
    if (prev && prev.isKana === kana) {
      prev.text += ch;
      if (kana) prev.hiragana += hira;
    } else {
      runs.push({ text: ch, isKana: kana, hiragana: hira });
    }
  }
  return runs;
}

/**
 * 送假名剥离与汉字读音对齐：
 * - 纯假名或无汉字词直接原样返回（不加注音）；
 * - 首尾与中间夹杂的假名（如 `お姉さん`、`見よう`、`思い出`、`取り戻す`）自动剥离并仅给汉字段标注读音；
 * - 复合汉字／熟字训（如 `二人` -> `ふ・た・り`）保留为完整词块并按 Mora 分拍。
 */
export function stripOkurigana(
  surface: string,
  rawReading: string | undefined,
  options: MoraSplitOptions = {},
): FuriganaSpan[] {
  if (!surface) return [];
  if (!containsKanji(surface) || !rawReading || rawReading === '*') {
    return [{ surface, isKanji: containsKanji(surface), source: 'none' }];
  }

  const reading = katakanaToHiragana(rawReading.trim());
  if (!reading || reading === katakanaToHiragana(surface)) {
    return [{ surface, isKanji: containsKanji(surface), source: 'none' }];
  }

  const runs = splitIntoKanaAndNonKanaRuns(surface);
  // Pure kanji / kanji+々 block (e.g. 二人, 今日, 郷, 人々)
  if (runs.length === 1) {
    const segments = splitJapaneseMora(reading, { ...options, isKanjiBase: true });
    return [
      {
        surface,
        ...(segments.length > 0 ? { rubySegments: segments } : {}),
        isKanji: true,
        source: 'kuromoji',
      },
    ];
  }

  // Match kana anchors greedily while ensuring each kanji run receives >= 1 reading character.
  let readCursor = 0;
  let readEnd = reading.length;
  const result: FuriganaSpan[] = [];

  // Strip leading kana run if reading starts with it (e.g. お姉さん -> お + 姉さん)
  let startRun = 0;
  if (runs[0]!.isKana && reading.startsWith(runs[0]!.hiragana)) {
    result.push({ surface: runs[0]!.text, isKanji: false, source: 'none' });
    readCursor += runs[0]!.hiragana.length;
    startRun = 1;
  }

  // Strip trailing kana run if reading ends with it (e.g. 見よう -> 見 + よう)
  let endRun = runs.length - 1;
  let trailingSpan: FuriganaSpan | undefined;
  if (endRun > startRun && runs[endRun]!.isKana && reading.slice(readCursor).endsWith(runs[endRun]!.hiragana)) {
    trailingSpan = { surface: runs[endRun]!.text, isKanji: false, source: 'none' };
    readEnd -= runs[endRun]!.hiragana.length;
    endRun--;
  }

  // Now align middle runs [startRun..endRun] against reading[readCursor..readEnd].
  const middleRuns = runs.slice(startRun, endRun + 1);
  const middleReading = reading.slice(readCursor, readEnd);

  if (middleRuns.length === 1) {
    const run = middleRuns[0]!;
    if (containsKanji(run.text) && middleReading.length > 0) {
      const segments = splitJapaneseMora(middleReading, { ...options, isKanjiBase: true });
      result.push({
        surface: run.text,
        ...(segments.length > 0 ? { rubySegments: segments } : {}),
        isKanji: true,
        source: 'kuromoji',
      });
    } else {
      result.push({ surface: run.text, isKanji: containsKanji(run.text), source: 'none' });
    }
  } else {
    // Handle interleaved kanji + kana + kanji (e.g. 思い出, 取り戻す, 売り切れ).
    let pos = 0;
    let aligned = true;
    const middleSpans: FuriganaSpan[] = [];

    for (let i = 0; i < middleRuns.length; i++) {
      const run = middleRuns[i]!;
      if (run.isKana) {
        if (!middleReading.startsWith(run.hiragana, pos)) {
          aligned = false;
          break;
        }
        middleSpans.push({ surface: run.text, isKanji: false, source: 'none' });
        pos += run.hiragana.length;
      } else {
        // Find next kana run anchor (if any) to bound this kanji run's reading.
        const nextRun = middleRuns[i + 1];
        if (!nextRun) {
          const slice = middleReading.slice(pos);
          if (!slice) {
            aligned = false;
            break;
          }
          const segments = splitJapaneseMora(slice, { ...options, isKanjiBase: true });
          middleSpans.push({
            surface: run.text,
            ...(segments.length > 0 ? { rubySegments: segments } : {}),
            isKanji: containsKanji(run.text),
            source: 'kuromoji',
          });
          pos = middleReading.length;
        } else {
          // Search for next kana run starting at least 1 char ahead so kanji gets >= 1 kana.
          const anchorIdx = middleReading.indexOf(nextRun.hiragana, pos + 1);
          if (anchorIdx < 0) {
            aligned = false;
            break;
          }
          const slice = middleReading.slice(pos, anchorIdx);
          const segments = splitJapaneseMora(slice, { ...options, isKanjiBase: true });
          middleSpans.push({
            surface: run.text,
            ...(segments.length > 0 ? { rubySegments: segments } : {}),
            isKanji: containsKanji(run.text),
            source: 'kuromoji',
          });
          pos = anchorIdx;
        }
      }
    }

    if (aligned && pos === middleReading.length) {
      result.push(...middleSpans);
    } else {
      // Fallback: attach full remaining reading to the whole middle surface so no characters are ever dropped.
      const wholeSurface = middleRuns.map((r) => r.text).join('');
      const segments = splitJapaneseMora(middleReading, { ...options, isKanjiBase: true });
      result.push({
        surface: wholeSurface,
        ...(segments.length > 0 ? { rubySegments: segments } : {}),
        isKanji: containsKanji(wholeSurface),
        source: 'kuromoji',
      });
    }
  }

  if (trailingSpan) result.push(trailingSpan);
  return result;
}

interface PartitionedSegment {
  text: string;
  userEntry?: UserDictionaryEntry;
}

/** 用本地用户词典执行最长优先匹配，将整行拆分为“用户词典命中片段”与“待分词片段”。 */
export function partitionByUserDictionary(
  text: string,
  userEntries: readonly UserDictionaryEntry[],
): PartitionedSegment[] {
  const sorted = normalizeUserEntries(userEntries);
  if (sorted.length === 0 || !text) {
    return text ? [{ text }] : [];
  }

  const segments: PartitionedSegment[] = [];
  let cursor = 0;
  let plainStart = 0;

  while (cursor < text.length) {
    let matched: UserDictionaryEntry | undefined;
    for (const entry of sorted) {
      if (text.startsWith(entry.surface, cursor)) {
        matched = entry;
        break;
      }
    }
    if (matched) {
      if (cursor > plainStart) {
        segments.push({ text: text.slice(plainStart, cursor) });
      }
      segments.push({ text: matched.surface, userEntry: matched });
      cursor += matched.surface.length;
      plainStart = cursor;
    } else {
      cursor++;
    }
  }

  if (plainStart < text.length) {
    segments.push({ text: text.slice(plainStart) });
  }
  return segments;
}

/**
 * 纯函数版本：给定分词结果与用户词典，生成一行的完整注音分片列表。
 * 保证所有 span.surface 依次拼接严格等于原始 lineText！
 */
export function generateLineFurigana(
  lineText: string,
  tokenize: (text: string) => readonly FuriganaWorkerMorpheme[],
  options: FuriganaOptions = {},
): FuriganaSpan[] {
  if (!lineText) return [];
  const partitioned = partitionByUserDictionary(lineText, options.userDictionary ?? []);
  const spans: FuriganaSpan[] = [];

  for (const part of partitioned) {
    if (part.userEntry) {
      spans.push({
        surface: part.text,
        rubySegments: [...part.userEntry.segments],
        isKanji: containsKanji(part.text) || Array.from(part.text).some(isKanjiOrIteration),
        source: 'user-dict',
      });
      continue;
    }

    const morphemes = tokenize(part.text);
    for (const m of morphemes) {
      const subSpans = stripOkurigana(m.surface_form, m.reading, options);
      spans.push(...subSpans);
    }
  }

  return spans;
}

/**
 * 将一行生成的 FuriganaSpan 展开为行重切用的 Token 段列表 + 对应的多拍注音映射：
 * - 带注音的汉字/复合词元保持完整（如 `二人` 保持单个 Token 并挂载 `['ふ','た','り']`）；
 * - 不带注音的片段保留原字素/英文词/独立空白与标点边界，不破坏已有非汉字切分。
 */
export interface LineFuriganaPlan {
  /** 建议切分后的 Token 文本段（严格拼接等于原行正文）。 */
  tokenTexts: string[];
  /** 每个需要挂载注音的 token 下标及其多拍注音分段。 */
  rubyByTokenIndex: Map<number, string[]>;
}

const MIXED_CHUNK_RE = /(\s+|[A-Za-z0-9]+(?:['’][A-Za-z0-9]+)*|[\p{P}\p{S}]|.)/gu;

export function buildLineFuriganaPlan(spans: readonly FuriganaSpan[]): LineFuriganaPlan {
  const tokenTexts: string[] = [];
  const rubyByTokenIndex = new Map<number, string[]>();

  for (const span of spans) {
    if (span.rubySegments && span.rubySegments.length > 0) {
      const idx = tokenTexts.length;
      tokenTexts.push(span.surface);
      rubyByTokenIndex.set(idx, [...span.rubySegments]);
    } else {
      // Split non-ruby text into clean Mixed-style tokens (spaces, Latin words, single punct/kana).
      const matches = span.surface.match(MIXED_CHUNK_RE);
      if (matches) {
        for (const chunk of matches) tokenTexts.push(chunk);
      } else if (span.surface) {
        tokenTexts.push(span.surface);
      }
    }
  }

  return { tokenTexts, rubyByTokenIndex };
}

/**
 * 浏览器/桌面端异步日语注音服务：懒启动专用 Web Worker，按行批量生成注音方案。
 */
export class JapaneseFuriganaService {
  private worker: Worker | undefined;
  private nextId = 1;
  private readonly pending = new Map<number, { resolve: (res: FuriganaWorkerResponse) => void; reject: (err: Error) => void }>();

  private ensureWorker(): Worker {
    if (!this.worker) {
      this.worker = new Worker(new URL('./japanese-furigana.worker.ts', import.meta.url), { type: 'module' });
      this.worker.addEventListener('message', (event: MessageEvent<FuriganaWorkerResponse>) => {
        const res = event.data;
        const entry = this.pending.get(res.id);
        if (!entry) return;
        this.pending.delete(res.id);
        if (res.ok) entry.resolve(res);
        else entry.reject(new Error(res.error ?? '日语分词 Worker 执行失败。'));
      });
      this.worker.addEventListener('error', (event) => {
        const err = new Error(event.message || '日语分词 Worker 发生未知错误。');
        for (const entry of this.pending.values()) entry.reject(err);
        this.pending.clear();
        this.dispose();
      });
    }
    return this.worker;
  }

  private request(payload: Omit<FuriganaWorkerRequest, 'id'>): Promise<FuriganaWorkerResponse> {
    const worker = this.ensureWorker();
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      worker.postMessage({ ...payload, id } satisfies FuriganaWorkerRequest);
    });
  }

  async warmup(): Promise<void> {
    await this.request({ type: 'init' });
  }

  async reset(): Promise<void> {
    if (!this.worker) return;
    await this.request({ type: 'reset' });
  }

  async analyzeLines(lines: readonly string[], options: FuriganaOptions = {}): Promise<FuriganaSpan[][]> {
    const userDict = options.userDictionary ?? [];
    const partitionedPerLine = lines.map((line) => partitionByUserDictionary(line, userDict));
    const textsToTokenize: string[] = [];
    for (const parts of partitionedPerLine) {
      for (const part of parts) {
        if (!part.userEntry && part.text) textsToTokenize.push(part.text);
      }
    }

    let morphemeList: FuriganaWorkerMorpheme[][] = [];
    if (textsToTokenize.length > 0) {
      const res = await this.request({ type: 'tokenize', texts: textsToTokenize });
      morphemeList = res.tokensList ?? [];
    }

    let cursor = 0;
    return partitionedPerLine.map((parts) => {
      const spans: FuriganaSpan[] = [];
      for (const part of parts) {
        if (part.userEntry) {
          spans.push({
            surface: part.text,
            rubySegments: [...part.userEntry.segments],
            isKanji: containsKanji(part.text) || Array.from(part.text).some(isKanjiOrIteration),
            source: 'user-dict',
          });
        } else if (part.text) {
          const morphemes = morphemeList[cursor++] ?? [];
          for (const m of morphemes) {
            spans.push(...stripOkurigana(m.surface_form, m.reading, options));
          }
        }
      }
      return spans;
    });
  }

  dispose(): void {
    if (this.worker) {
      this.worker.terminate();
      this.worker = undefined;
    }
    this.pending.clear();
  }
}

export const defaultJapaneseFuriganaService = new JapaneseFuriganaService();
