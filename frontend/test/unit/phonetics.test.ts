import { describe, expect, it } from 'vitest';
import { gzipSync } from 'node:zlib';
import { clampPlaybackRate } from '../../src/editor/audio/audio-controller';
import {
  classifyToken,
  derivePhraseEndHint,
  hasPhraseEndHint,
  isLastContentToken,
} from '../../src/editor/model/token-semantics';
import {
  extractZipEntries,
  gunzipBytes,
  JapaneseDictStore,
  KUROMOJI_DICT_FILES,
  MemoryDictStorage,
  sha256Hex,
} from '../../src/editor/phonetics/dict-store';
import {
  buildLineFuriganaPlan,
  generateLineFurigana,
  stripOkurigana,
} from '../../src/editor/phonetics/japanese-furigana';
import { splitJapaneseMora } from '../../src/editor/phonetics/mora-splitter';
import { previewRomajiInput } from '../../src/editor/phonetics/romaji-preview';
import {
  applyTagOffsetUs,
  clampRewindMs,
  clampTagOffsetMs,
} from '../../src/editor/phonetics/timing-preferences';
import {
  exportUserDictionaryText,
  parseExternalUserDictionary,
} from '../../src/editor/phonetics/user-dictionary';

/** Build a valid minimal uncompressed (Stored) ZIP archive in memory for import tests. */
function makeStoredZip(files: Record<string, Uint8Array>): Uint8Array {
  const localParts: Uint8Array[] = [];
  const centralParts: Uint8Array[] = [];
  let offset = 0;
  const encoder = new TextEncoder();

  for (const [name, data] of Object.entries(files)) {
    const nameBytes = encoder.encode(name);
    const local = new Uint8Array(30 + nameBytes.length + data.length);
    const lv = new DataView(local.buffer);
    lv.setUint32(0, 0x04034b50, true);
    lv.setUint16(4, 20, true);
    lv.setUint16(8, 0, true); // method = stored
    lv.setUint32(18, data.length, true);
    lv.setUint32(22, data.length, true);
    lv.setUint16(26, nameBytes.length, true);
    local.set(nameBytes, 30);
    local.set(data, 30 + nameBytes.length);
    localParts.push(local);

    const central = new Uint8Array(46 + nameBytes.length);
    const cv = new DataView(central.buffer);
    cv.setUint32(0, 0x02014b50, true);
    cv.setUint16(4, 20, true);
    cv.setUint16(6, 20, true);
    cv.setUint16(10, 0, true);
    cv.setUint32(20, data.length, true);
    cv.setUint32(24, data.length, true);
    cv.setUint16(28, nameBytes.length, true);
    cv.setUint32(42, offset, true);
    central.set(nameBytes, 46);
    centralParts.push(central);

    offset += local.length;
  }

  const cdSize = centralParts.reduce((s, b) => s + b.length, 0);
  const eocd = new Uint8Array(22);
  const ev = new DataView(eocd.buffer);
  ev.setUint32(0, 0x06054b50, true);
  ev.setUint16(8, centralParts.length, true);
  ev.setUint16(10, centralParts.length, true);
  ev.setUint32(12, cdSize, true);
  ev.setUint32(16, offset, true);

  const total = offset + cdSize + eocd.length;
  const out = new Uint8Array(total);
  let cursor = 0;
  for (const part of [...localParts, ...centralParts, eocd]) {
    out.set(part, cursor);
    cursor += part.length;
  }
  return out;
}

describe('标准日语 Mora 分拍器（mora-splitter）', () => {
  it('默认标准拍：拗音合并为 1 拍，拨音/促音/长音各为独立 1 拍', () => {
    expect(splitJapaneseMora('おな')).toEqual(['お', 'な']);
    expect(splitJapaneseMora('きょう')).toEqual(['きょ', 'う']);
    expect(splitJapaneseMora('かん')).toEqual(['か', 'ん']);
    expect(splitJapaneseMora('ずっと')).toEqual(['ず', 'っ', 'と']);
    expect(splitJapaneseMora('ふたり')).toEqual(['ふ', 'た', 'り']);
    expect(splitJapaneseMora('しゃしん')).toEqual(['しゃ', 'し', 'ん']);
    expect(splitJapaneseMora('パーティー')).toEqual(['パ', 'ー', 'ティ', 'ー']);
    expect(splitJapaneseMora('ふぁんたじー')).toEqual(['ふぁ', 'ん', 'た', 'じ', 'ー']);
  });

  it('开启「合并汉字内长音/拨音」开关后正确合并尾随长音与拨音', () => {
    const opt = { mergeLongAndNasalInKanji: true, isKanjiBase: true };
    expect(splitJapaneseMora('きょう', opt)).toEqual(['きょう']);
    expect(splitJapaneseMora('かん', opt)).toEqual(['かん']);
    expect(splitJapaneseMora('せいしん', opt)).toEqual(['せい', 'しん']);
    expect(splitJapaneseMora('きゅう', opt)).toEqual(['きゅう']);
    expect(splitJapaneseMora('カー', opt)).toEqual(['カー']);
    // 非长音/拨音不被误合并
    expect(splitJapaneseMora('おな', opt)).toEqual(['お', 'な']);
    expect(splitJapaneseMora('あおい', opt)).toEqual(['あ', 'お', 'い']);
    // 促音仍然独立成拍
    expect(splitJapaneseMora('いっしょ', opt)).toEqual(['い', 'っ', 'しょ']);
  });

  it('非汉字词元即使开启合并开关也保持标准拍', () => {
    expect(splitJapaneseMora('きょう', { mergeLongAndNasalInKanji: true, isKanjiBase: false })).toEqual(['きょ', 'う']);
  });
});

describe('罗马音实时预览引擎（romaji-preview / wanakana）', () => {
  it('小写转平假名、大写转片假名并按 Mora 分拍，Shift+Enter 保留原文', () => {
    const lower = previewRomajiInput('kyou');
    expect(lower.hasLatin).toBe(true);
    expect(lower.kanaSegments).toEqual(['きょ', 'う']);
    expect(lower.previewLabel).toBe('きょ・う');
    expect(lower.rawSegments).toEqual(['kyou']);

    const upper = previewRomajiInput('SHIN');
    expect(upper.kanaSegments).toEqual(['シ', 'ン']);
    expect(upper.rawSegments).toEqual(['SHIN']);

    const pinyin = previewRomajiInput('ni,hao');
    expect(pinyin.rawSegments).toEqual(['ni', 'hao']);
  });

  it('支持显式分隔符与合并长音/拨音选项', () => {
    const manual = previewRomajiInput('fu,ta,ri');
    expect(manual.kanaSegments).toEqual(['ふ', 'た', 'り']);
    expect(manual.rawSegments).toEqual(['fu', 'ta', 'ri']);

    const merged = previewRomajiInput('kyou', { mergeLongAndNasalInKanji: true, isKanjiBase: true });
    expect(merged.kanaSegments).toEqual(['きょう']);
  });
});

describe('日语自动注音：送假名剥离、熟字训与用户词典最高优先级覆盖', () => {
  it('剥离首尾与中间送假名（見よう、思い出、取り戻す、お姉さん）', () => {
    expect(stripOkurigana('見よう', 'ミヨウ')).toEqual([
      { surface: '見', rubySegments: ['み'], isKanji: true, source: 'kuromoji' },
      { surface: 'よう', isKanji: false, source: 'none' },
    ]);

    expect(stripOkurigana('思い出', 'オモイデ')).toEqual([
      { surface: '思', rubySegments: ['お', 'も'], isKanji: true, source: 'kuromoji' },
      { surface: 'い', isKanji: false, source: 'none' },
      { surface: '出', rubySegments: ['で'], isKanji: true, source: 'kuromoji' },
    ]);

    expect(stripOkurigana('取り戻す', 'トリモドス')).toEqual([
      { surface: '取', rubySegments: ['と'], isKanji: true, source: 'kuromoji' },
      { surface: 'り', isKanji: false, source: 'none' },
      { surface: '戻', rubySegments: ['も', 'ど'], isKanji: true, source: 'kuromoji' },
      { surface: 'す', isKanji: false, source: 'none' },
    ]);

    expect(stripOkurigana('お姉さん', 'オネエサン')).toEqual([
      { surface: 'お', isKanji: false, source: 'none' },
      { surface: '姉', rubySegments: ['ね', 'え'], isKanji: true, source: 'kuromoji' },
      { surface: 'さん', isKanji: false, source: 'none' },
    ]);
  });

  it('熟字训与复合汉字词保持完整并按 Mora 分拍（二人 -> ふ・た・り）', () => {
    expect(stripOkurigana('二人', 'フタリ')).toEqual([
      { surface: '二人', rubySegments: ['ふ', 'た', 'り'], isKanji: true, source: 'kuromoji' },
    ]);
    expect(stripOkurigana('郷', 'キョウ', { mergeLongAndNasalInKanji: true })).toEqual([
      { surface: '郷', rubySegments: ['きょう'], isKanji: true, source: 'kuromoji' },
    ]);
  });

  it('用户词典最高优先级覆盖分词结果且支持 StrangeUtaGame / RL 导入导出', () => {
    const imported = parseExternalUserDictionary(`
      # comment
      本気\tま・じ\t当て字
      地球=ほ,し
    `);
    expect(imported).toHaveLength(2);
    const exported = exportUserDictionaryText(imported);
    expect(exported).toContain('本気\tま・じ');

    const mockTokenize = (segment: string) => [
      { surface_form: segment, reading: segment === 'で' ? 'デ' : 'ホンキ' },
    ];
    const spans = generateLineFurigana('本気で地球へ', mockTokenize, { userDictionary: imported });
    expect(spans.map((s) => s.surface).join('')).toBe('本気で地球へ');
    expect(spans[0]).toEqual({ surface: '本気', rubySegments: ['ま', 'じ'], isKanji: true, source: 'user-dict' });
    const earth = spans.find((s) => s.surface === '地球');
    expect(earth).toEqual({ surface: '地球', rubySegments: ['ほ', 'し'], isKanji: true, source: 'user-dict' });

    const plan = buildLineFuriganaPlan(spans);
    expect(plan.tokenTexts.join('')).toBe('本気で地球へ');
    expect(plan.rubyByTokenIndex.get(0)).toEqual(['ま', 'じ']);
  });
});

describe('按需词典缓存、SHA-256 校验与离线本地 .zip 导入（dict-store）', () => {
  it('按需下载校验 SHA-256 并在校验失败时拒绝落盘，成功后可解压读取', async () => {
    const storage = new MemoryDictStorage();
    const store = new JapaneseDictStore(storage);
    expect((await store.getStatus()).installed).toBe(false);

    const payloadMap = new Map<string, Uint8Array>();
    const manifestFiles: Record<string, { size: number; sha256: string }> = {};
    let totalBytes = 0;

    for (const file of KUROMOJI_DICT_FILES) {
      const gz = new Uint8Array(gzipSync(Buffer.from(`mock-${file}`, 'utf8')));
      payloadMap.set(file, gz);
      const hash = await sha256Hex(gz);
      manifestFiles[file] = { size: gz.byteLength, sha256: hash };
      totalBytes += gz.byteLength;
    }

    const manifest = {
      schemaVersion: 1,
      id: 'naist-jdic-0.6.3b',
      name: 'NAIST-jdic 0.6.3b',
      version: '0.6.3b-pruned-1',
      license: 'BSD-3-Clause',
      totalBytes,
      files: manifestFiles,
    };

    const mockFetch = (async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.endsWith('/manifest.json')) {
        return new Response(JSON.stringify(manifest), { status: 200 });
      }
      const name = url.split('/').pop()!;
      const bytes = payloadMap.get(name);
      return bytes ? new Response(new Uint8Array(bytes), { status: 200 }) : new Response(null, { status: 404 });
    }) as typeof fetch;

    const progressEvents: number[] = [];
    const status = await store.downloadFromUrl('https://mirror.example/naist', {
      fetchImpl: mockFetch,
      onProgress: (p) => progressEvents.push(p.percent),
    });

    expect(status.installed).toBe(true);
    expect(status.license).toBe('BSD-3-Clause');
    expect(progressEvents.at(-1)).toBe(100);

    const rawBase = await store.readDictFile('base.dat.gz');
    expect(rawBase).not.toBeNull();
    const decompressed = new TextDecoder().decode(await gunzipBytes(rawBase!));
    expect(decompressed).toBe('mock-base.dat.gz');

    await store.clearCache();
    expect((await store.getStatus()).installed).toBe(false);
  });

  it('支持离线导入本地 .zip 词典包', async () => {
    const zipEntries: Record<string, Uint8Array> = {};
    for (const file of KUROMOJI_DICT_FILES) {
      zipEntries[`dict/${file}`] = new Uint8Array(gzipSync(Buffer.from(`zip-${file}`, 'utf8')));
    }
    const zipBytes = makeStoredZip(zipEntries);
    const extracted = await extractZipEntries(zipBytes);
    expect(extracted.size).toBe(KUROMOJI_DICT_FILES.length);

    const store = new JapaneseDictStore(new MemoryDictStorage());
    const status = await store.importLocalFiles([{ name: 'naist-jdic-0.6.3b.zip', bytes: async () => zipBytes }]);
    expect(status.installed).toBe(true);
    const decompressed = new TextDecoder().decode(await gunzipBytes((await store.readDictFile('tid.dat.gz'))!));
    expect(decompressed).toBe('zip-tid.dat.gz');
  });
});

describe('语境化空格语义与打轴变速偏好工具', () => {
  it('前端 token-semantics 与后端 LyricTokenSemantics 规则完全一致', () => {
    expect(classifyToken(' ')).toBe('whitespace');
    expect(classifyToken('、')).toBe('punctuation');
    expect(classifyToken('ー')).toBe('content');
    expect(classifyToken('々')).toBe('content');

    const en = [{ text: 'Hello' }, { text: ' ' }, { text: 'world' }];
    expect(derivePhraseEndHint(en, 0)).toBe(false);
    expect(hasPhraseEndHint([{ text: 'Hello', phraseEndHint: true }, { text: ' ' }, { text: 'world' }], 0)).toBe(true);
    expect(isLastContentToken(en, 2)).toBe(true);

    const ja = [{ text: '夢' }, { text: ' ' }, { text: '光' }];
    expect(derivePhraseEndHint(ja, 0)).toBe(true);
    expect(hasPhraseEndHint([{ text: '夢', phraseEndHint: false }, { text: ' ' }, { text: '光' }], 0)).toBe(false);
  });

  it('保调变速与按键补偿钳位边界正确', () => {
    expect(clampPlaybackRate(0.1)).toBe(0.25);
    expect(clampPlaybackRate(0.85)).toBe(0.85);
    expect(clampPlaybackRate(3.5)).toBe(2.0);
    expect(clampTagOffsetMs(-999)).toBe(-500);
    expect(clampTagOffsetMs(80)).toBe(80);
    expect(clampRewindMs(50)).toBe(200);
    expect(applyTagOffsetUs(1_000_000, 50)).toBe(950_000);
    expect(applyTagOffsetUs(10_000, 100)).toBe(0);
  });
});
