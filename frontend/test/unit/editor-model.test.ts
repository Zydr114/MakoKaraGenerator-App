import { describe, expect, it } from 'vitest';
import {
  fromEditableLine,
  fromMinimalLine,
  lineAt,
  reconcileSelection,
  stepToken,
  tokenAt,
  type EditorLine,
} from '../../src/editor/model/lyrics';
import { batchHistoryText, editBatches } from '../../src/editor/model/edit-batches';
import {
  movedOrder,
  nudgedRange,
  removeRubyEdits,
  rubyBandSelected,
  rubyBands,
  rubyTextLocked,
  selectedSegment,
  splitBlocked,
  splitEdits,
  stampedEnd,
  stampedStart,
} from '../../src/editor/model/ruby-segments';
import { fillRatio, previewSegments, previewWindow, translationsByPrimary, windowKey } from '../../src/editor/model/word-preview';
import { currentSegments, graphemes, parseSegments } from '../../src/editor/model/token-segments';
import { autoPair, branchDescription, currentLinks, lineNumbers, linkChanges, roleOptions, timingModeOptions } from '../../src/editor/model/branches';
import { followSpans, lineTracks, trackLines } from '../../src/editor/model/line-tracks';
import {
  decoderLabel,
  fileStem,
  filterIssues,
  issueCounts,
  pastedLineCount,
  reportFacts,
  sourceWindow,
  splitSourceLines,
  worstSeverity,
} from '../../src/editor/model/lyrics-import';
import { analysisProgressText, formatColor, formatFrameRate, parseColor, parseDimension, parseFrameRate } from '../../src/editor/model/settings';
import { BASE_BUCKET_SAMPLES, buildPyramid, chooseLevel, columnPeaks, type PeakPyramid } from '../../src/editor/model/peaks';
import { BackendPeakSource, CHUNK_BUCKETS, type PeakWindowFetcher, type PeakWindowRequest } from '../../src/editor/model/backend-peaks';
import { formatTime, frameDurationUs, frameIndexAt, frameStartUs, parseTime, secondsToUs, snapToFrame, toMicros } from '../../src/editor/model/time';
import { clampStart, fitViewport, follow, hitEdges, hitSpan, linkedEdges, shrinkingFirst, tickStepUs, ticks, usAt, xOf, zoomAround } from '../../src/editor/model/timeline';
import { decodePeakWindow, micros, type LyricsImportIssue, type LyricsImportReport, type ProjectWaveformAvailable } from '../../src/protocol';

describe('时间换算', () => {
  it('formatTime：分秒毫秒，小时以上带小时，负数带符号', () => {
    expect(formatTime(0)).toBe('0:00.000');
    expect(formatTime(12_345_678)).toBe('0:12.346');
    expect(formatTime(225_000_000)).toBe('3:45.000');
    expect(formatTime(3_725_000_000)).toBe('1:02:05.000');
    expect(formatTime(-1_500_000)).toBe('-0:01.500');
    expect(formatTime(61_000_000, { millis: false })).toBe('1:01');
  });

  it('parseTime：接受 ss、ss.fff、m:ss.fff、h:mm:ss；拒绝越界与乱码', () => {
    expect(parseTime('12')).toBe(12_000_000);
    expect(parseTime('1.5')).toBe(1_500_000);
    expect(parseTime('0:01.234567')).toBe(1_234_567);
    expect(parseTime(' 3:45.000 ')).toBe(225_000_000);
    expect(parseTime('1:02:05')).toBe(3_725_000_000);
    expect(parseTime('-0:01.5')).toBe(-1_500_000);
    expect(parseTime('1:60')).toBeNull();
    expect(parseTime('1:60:00')).toBeNull();
    expect(parseTime('1.2345678')).toBeNull();
    expect(parseTime('abc')).toBeNull();
    expect(parseTime('')).toBeNull();
  });

  it('formatTime 与 parseTime 在毫秒精度往返', () => {
    for (const us of [0, 1_000, 999_000, 59_999_000, 61_001_000, 3_600_000_000]) {
      expect(parseTime(formatTime(us))).toBe(us);
    }
  });

  it('秒与微秒、Micros 字符串', () => {
    expect(secondsToUs(1.2345675)).toBe(1_234_568);
    expect(toMicros(1_500_000)).toBe(micros('1500000'));
    expect(() => toMicros(1.5)).toThrow(RangeError);
  });

  it('帧：30 fps 与 30000/1001 fps 的帧号、帧起点与吸附', () => {
    const fps30 = { numerator: 30, denominator: 1 };
    expect(frameDurationUs(fps30)).toBeCloseTo(33_333.33, 1);
    expect(frameIndexAt(1_000_000, fps30)).toBe(30);
    expect(frameStartUs(1, fps30)).toBe(33_334);
    expect(frameIndexAt(frameStartUs(1, fps30), fps30)).toBe(1);
    expect(snapToFrame(1_010_000, fps30)).toBe(1_000_000);
    expect(snapToFrame(1_020_000, fps30)).toBe(1_033_334);

    const ntsc = { numerator: 30000, denominator: 1001 };
    for (const frame of [0, 1, 29, 30, 1799, 107892]) {
      expect(frameIndexAt(frameStartUs(frame, ntsc), ntsc)).toBe(frame);
    }
  });
});

describe('时间轴几何', () => {
  const vp = { startUs: 1_000_000, usPerPx: 10_000, widthPx: 500 };

  it('像素与时间互换', () => {
    expect(xOf(vp, 2_000_000)).toBe(100);
    expect(usAt(vp, 100)).toBe(2_000_000);
  });

  it('以锚点为不动点缩放，并夹在缩放范围内', () => {
    const zoomed = zoomAround(vp, 0.5, 100);
    expect(zoomed.usPerPx).toBe(5_000);
    expect(usAt(zoomed, 100)).toBe(2_000_000);
    expect(zoomAround(vp, 1e-9, 0).usPerPx).toBe(100);
    expect(zoomAround(vp, 1e9, 0).usPerPx).toBe(1_000_000);
  });

  it('fit、clampStart、follow', () => {
    expect(fitViewport(10_000_000, 1000)).toEqual({ startUs: 0, usPerPx: 10_000, widthPx: 1000 });
    expect(clampStart({ ...vp, startUs: -10_000_000 }, 60_000_000).startUs).toBe(-2_500_000);
    expect(clampStart({ ...vp, startUs: 90_000_000 }, 60_000_000).startUs).toBe(57_500_000);
    expect(follow(vp, 3_000_000)).toBe(vp);
    expect(follow(vp, 7_000_000).startUs).toBe(7_000_000 - 500_000);
  });

  it('标尺刻度间隔不小于最小像素间距', () => {
    expect(tickStepUs(10_000, 80)).toBe(1_000_000);
    expect(tickStepUs(100, 80)).toBe(10_000);
    expect(ticks({ startUs: 900_000, usPerPx: 10_000, widthPx: 300 }, 1_000_000)).toEqual([1_000_000, 2_000_000, 3_000_000]);
  });

  it('边界命中：相邻元素的共享边界同时命中，end 排在 start 前', () => {
    const spans = [
      { id: 'a', startUs: 1_000_000, endUs: 2_000_000 },
      { id: 'b', startUs: 2_000_000, endUs: 3_000_000 },
    ];
    const hits = hitEdges(vp, spans, 101, 5);
    expect(hits.map((h) => `${h.id}.${h.edge}`)).toEqual(['a.end', 'b.start']);
    expect(hitEdges(vp, spans, 150, 5)).toEqual([]);
    expect(hitSpan(vp, spans, 150)?.id).toBe('b');
    expect(hitSpan(vp, spans, 250)).toBeUndefined();
  });

  it('linkedEdges：共用同一时间点的相邻边界一起拖；只是挨得近、时间不同的不联动', () => {
    const vp = { startUs: 0, usPerPx: 10_000, widthPx: 1000 };
    const spans = [
      { id: 'a', startUs: 0, endUs: 1_000_000 },
      { id: 'b', startUs: 1_000_000, endUs: 2_000_000 },
      { id: 'c', startUs: 2_020_000, endUs: 3_000_000 },
    ];
    expect(linkedEdges(vp, spans, 101).map((e) => `${e.span.id}.${e.edge}`)).toEqual(['a.end', 'b.start']);
    expect(linkedEdges(vp, spans, 201).map((e) => `${e.span.id}.${e.edge}`)).toEqual(['b.end']);
    expect(linkedEdges(vp, spans, 150)).toEqual([]);
  });

  it('shrinkingFirst：共用边界向右拖先提交后一个的起点，向左拖先提交前一个的终点', () => {
    const targets = [
      { id: 'a', edge: 'end' as const },
      { id: 'b', edge: 'start' as const },
    ];
    expect(shrinkingFirst(targets, 600_000, 700_000).map((t) => t.id)).toEqual(['b', 'a']);
    expect(shrinkingFirst(targets, 600_000, 500_000).map((t) => t.id)).toEqual(['a', 'b']);
  });
});

const lines: EditorLine[] = [
  {
    branchId: 'primary',
    id: 'l1',
    text: 'ab',
    startUs: 0,
    endUs: 2_000_000,
    tokens: [
      { id: 't1', lineId: 'l1', text: 'a', startUs: 0, endUs: 1_000_000 },
      { id: 't2', lineId: 'l1', text: 'b', startUs: 1_000_000, endUs: 2_000_000 },
    ],
    ruby: [],
  },
  { branchId: 'primary', id: 'l2', text: '', tokens: [], ruby: [] },
  { branchId: 'primary', id: 'l3', text: 'c', tokens: [{ id: 't3', lineId: 'l3', text: 'c' }], ruby: [] },
];

describe('歌词导航', () => {
  it('归一化两种读模型；未打轴为 undefined，零起点保留', () => {
    const minimal = fromMinimalLine({
      branchId: 'primary',
      id: 'l',
      text: 'x',
      time: { startUs: micros('0'), endUs: micros('5') },
      tokens: [{ id: 't', text: 'x', time: {} }],
    });
    expect(minimal).toMatchObject({ startUs: 0, endUs: 5 });
    expect(minimal.status).toBeUndefined();
    expect(minimal.tokens[0]).toEqual({ id: 't', lineId: 'l', text: 'x' });

    const editable = fromEditableLine({
      branchId: 'primary',
      id: 'l',
      text: 'x',
      time: {},
      timingStatus: 'missing',
      tokens: [{ id: 't', text: 'x', time: { startUs: micros('1'), endUs: micros('2') }, timingStatus: 'edited' }],
      rubyAnnotations: [{ id: 'r', baseTokenId: 't', text: 'えっくす', relativeScale: 0.5, gapPx: 2, alignment: 'center', segments: [] }],
    });
    expect(editable.status).toBe('missing');
    expect(editable.tokens[0]).toMatchObject({ startUs: 1, endUs: 2, status: 'edited' });
    expect(editable.ruby).toHaveLength(1);
  });

  it('跨行前进／后退，跳过没有元素的行', () => {
    expect(stepToken(lines, 't1', 1)?.id).toBe('t2');
    expect(stepToken(lines, 't2', 1)?.id).toBe('t3');
    expect(stepToken(lines, 't3', -1)?.id).toBe('t2');
    expect(stepToken(lines, 't3', 1)).toBeUndefined();
    expect(stepToken(lines, 't1', -1)).toBeUndefined();
    expect(stepToken(lines, 'gone', 1)).toBeUndefined();
  });

  it('按时间找行与元素；区间左闭右开', () => {
    expect(lineAt(lines, 1_500_000)?.id).toBe('l1');
    expect(lineAt(lines, 2_000_000)).toBeUndefined();
    expect(tokenAt(lines[0]!, 1_000_000)?.id).toBe('t2');
  });

  it('选择在新数据里失效时被收窄或清空', () => {
    expect(reconcileSelection(lines, { lineId: 'l1', tokenId: 't1' })).toEqual({ lineId: 'l1', tokenId: 't1' });
    expect(reconcileSelection(lines, { lineId: 'l1', tokenId: 'gone' })).toEqual({ lineId: 'l1' });
    expect(reconcileSelection(lines, { lineId: 'gone' })).toBeUndefined();
  });
});

describe('注音片段（W6 设计 §2.1、§2.4）', () => {
  const segment = (id: string, text: string, timed = false) => ({
    id,
    text,
    time: timed ? { startUs: micros(0), endUs: micros(100) } : {},
    timingStatus: timed ? ('edited' as const) : ('missing' as const),
  });
  const ruby = (segments: ReturnType<typeof segment>[]) => ({ id: 'r', baseTokenId: 't1', text: segments.map((s) => s.text).join(''), relativeScale: 0.5, gapPx: 2, alignment: 'center' as const, segments });
  const lineWith = (segments: ReturnType<typeof segment>[]): EditorLine => ({
    branchId: 'b',
    id: 'l',
    text: '朝日',
    tokens: [
      { id: 't1', lineId: 'l', text: '朝' },
      { id: 't2', lineId: 'l', text: '日' },
    ],
    ruby: [ruby(segments)],
  });
  const group = { branchId: 'b', lineId: 'l', tokenId: 't1', rubyId: 'r' };

  it('选择里的片段不存在时去掉片段、保留文本单元；所选片段只认所选单元的注音', () => {
    const line = lineWith([segment('s1', 'あ'), segment('s2', 'さ')]);
    expect(reconcileSelection([line], { lineId: 'l', tokenId: 't1', segmentId: 's2' })).toEqual({ lineId: 'l', tokenId: 't1', segmentId: 's2' });
    expect(reconcileSelection([line], { lineId: 'l', tokenId: 't1', segmentId: 'gone' })).toEqual({ lineId: 'l', tokenId: 't1' });
    expect(reconcileSelection([line], { lineId: 'l', tokenId: 't2', segmentId: 's2' })).toEqual({ lineId: 'l', tokenId: 't2' });
    expect(selectedSegment(line, { lineId: 'l', tokenId: 't1', segmentId: 's2' })?.index).toBe(1);
    expect(selectedSegment(line, { lineId: 'l', tokenId: 't2', segmentId: 's2' })).toBeUndefined();
  });

  it('整段注音只在没有注音或一个没有时间的片段时能直接改', () => {
    expect(rubyTextLocked(undefined)).toBeUndefined();
    expect(rubyTextLocked(ruby([segment('s1', 'あさ')]))).toBeUndefined();
    expect(rubyTextLocked(ruby([segment('s1', 'あ'), segment('s2', 'さ')]))).toMatch(/几个片段/);
    expect(rubyTextLocked(ruby([segment('s1', 'あさ', true)]))).toMatch(/已有时间/);
  });

  it('左右移给出完整的新顺序，到头时没有', () => {
    const r = ruby([segment('s1', 'あ'), segment('s2', 'さ'), segment('s3', 'ひ')]);
    expect(movedOrder(r, 's2', -1)).toEqual(['s2', 's1', 's3']);
    expect(movedOrder(r, 's2', 1)).toEqual(['s1', 's3', 's2']);
    expect(movedOrder(r, 's1', -1)).toBeUndefined();
    expect(movedOrder(r, 's3', 1)).toBeUndefined();
  });

  it('按字拆开：其余的字倒序加在原片段后面，最后把原片段改成第一个字；有时间的片段不拆', () => {
    const line = lineWith([segment('s1', 'あさ'), segment('s2', 'ひかり', true), segment('s3', 'に')]);
    const r = line.ruby[0]!;
    expect(splitBlocked(r)).toBeUndefined();
    expect(splitEdits(line, r)).toEqual([
      { type: 'lyrics.addRubySegment', target: group, text: 'さ', afterSegmentId: 's1' },
      { type: 'lyrics.setRubySegmentText', target: { ...group, segmentId: 's1' }, text: 'あ' },
    ]);
    const three = lineWith([segment('s1', 'きょう')]);
    expect(splitEdits(three, three.ruby[0]!).map((e) => ('text' in e ? e.text : ''))).toEqual(['う', 'ょ', 'き']);
    expect(splitBlocked(ruby([segment('s1', 'あさ', true)]))).toMatch(/有时间的片段不拆/);
    expect(splitBlocked(ruby([segment('s1', 'あ'), segment('s2', 'さ')]))).toMatch(/只有一个字/);
  });

  it('片段时间：±1 帧按帧吸附、不越过 0；起点取播放头保留晚于它的终点；终点取播放头要先有更早的起点', () => {
    const rate = { numerator: 30, denominator: 1 };
    expect(nudgedRange({ startUs: 200_000, endUs: 400_000 }, 1, rate)).toEqual({ startUs: 233_334, endUs: 433_334 });
    expect(nudgedRange({ startUs: 0, endUs: 400_000 }, -1, rate)).toBeUndefined();
    expect(stampedStart({ startUs: 0, endUs: 900_000 }, 500_000, rate)).toEqual({ startUs: 500_000, endUs: 900_000 });
    expect(stampedStart({ endUs: 300_000 }, 500_000, rate)).toEqual({ startUs: 500_000, endUs: 533_334 });
    expect(stampedStart({}, 510_000, rate)).toEqual({ startUs: 500_000, endUs: 533_334 });
    expect(stampedEnd({ startUs: 200_000 }, 700_000, rate)).toEqual({ startUs: 200_000, endUs: 700_000 });
    expect(stampedEnd({}, 700_000, rate)).toBe('先设起点');
    expect(stampedEnd({ startUs: 700_000 }, 690_000, rate)).toBe('播放头要晚于起点');
    expect(stampedEnd({ startUs: 200_000 }, 700_123, undefined)).toEqual({ startUs: 200_000, endUs: 700_123 });
  });

  it('注音轨：有时间的片段各画一块，整组没有时间时在文本单元的位置画虚线框；部分有时间时只画有时间的', () => {
    const timedLine: EditorLine = {
      ...lineWith([segment('s1', 'あ', true), segment('s2', 'さ')]),
      tokens: [
        { id: 't1', lineId: 'l', text: '朝', startUs: 0, endUs: 500_000 },
        { id: 't2', lineId: 'l', text: '日', startUs: 500_000, endUs: 900_000 },
      ],
    };
    expect(rubyBands([timedLine]).map((b) => [b.id, b.segmentId, b.follow, b.startUs, b.endUs])).toEqual([['s1', 's1', false, 0, 100]]);
    const following: EditorLine = { ...timedLine, ruby: [ruby([segment('s1', 'あ'), segment('s2', 'さ')])] };
    const [box] = rubyBands([following]);
    expect(box).toMatchObject({ id: 'follow:r', segmentId: 's1', text: 'あさ', startUs: 0, endUs: 500_000, follow: true });
    expect(rubyBands([lineWith([segment('s1', 'あ')])])).toEqual([]); // 文本单元也没有时间
    expect(rubyBandSelected(box!, { lineId: 'l', tokenId: 't1' })).toBe(true);
    expect(rubyBandSelected(box!, { lineId: 'l', tokenId: 't2' })).toBe(false);
    const [band] = rubyBands([timedLine]);
    expect(rubyBandSelected(band!, { lineId: 'l', tokenId: 't1', segmentId: 's1' })).toBe(true);
    expect(rubyBandSelected(band!, { lineId: 'l', tokenId: 't1' })).toBe(false);
  });

  it('删除注音：逐个删除全部片段', () => {
    const line = lineWith([segment('s1', 'あ'), segment('s2', 'さ')]);
    expect(removeRubyEdits(line, line.ruby[0]!)).toEqual([
      { type: 'lyrics.removeRubySegment', target: { ...group, segmentId: 's1' } },
      { type: 'lyrics.removeRubySegment', target: { ...group, segmentId: 's2' } },
    ]);
  });
});

describe('波形峰值金字塔', () => {
  const sampleRate = 48_000;
  // 1 秒：前半秒静音，后半秒 ±0.5 方波；第二声道在开头有一个 0.9 尖峰。
  const left = new Float32Array(sampleRate).map((_, i) => (i < sampleRate / 2 ? 0 : i % 2 ? 0.5 : -0.5));
  const right = new Float32Array(sampleRate);
  right[10] = 0.9;
  const pyramid = buildPyramid([left, right], sampleRate, 16);

  it('第 0 层每桶 64 样本，多声道取并集；逐级两两合并到不超过上限', () => {
    expect(pyramid.levels[0]!.bucketSamples).toBe(BASE_BUCKET_SAMPLES);
    expect(pyramid.levels[0]!.min.length).toBe(Math.ceil(sampleRate / 64));
    expect(pyramid.levels[0]!.max[0]).toBeCloseTo(0.9);
    expect(pyramid.levels.at(-1)!.min.length).toBeLessThanOrEqual(16);
    for (let k = 1; k < pyramid.levels.length; k++) {
      expect(pyramid.levels[k]!.bucketSamples).toBe(pyramid.levels[k - 1]!.bucketSamples * 2);
    }
  });

  it('按每列样本数选层', () => {
    expect(chooseLevel(pyramid, 10).bucketSamples).toBe(64);
    expect(chooseLevel(pyramid, 300).bucketSamples).toBe(128);
  });

  it('列峰值：静音段为 0，方波段为 ±0.5，音频之外为 NaN', () => {
    // 每列 200 ms，从 -200 ms 开始：[-200,0) [0,200) [200,400) [400,600) … [1000,1200)
    const peaks = columnPeaks(pyramid, -200_000, 200_000, 7);
    expect(Number.isNaN(peaks.min[0])).toBe(true);
    expect(peaks.max[1]).toBeCloseTo(0.9);
    expect([peaks.min[2], peaks.max[2]]).toEqual([0, 0]);
    expect([peaks.min[3], peaks.max[3]]).toEqual([-0.5, 0.5]);
    expect(peaks.min[5]).toBeCloseTo(-0.5);
    expect(Number.isNaN(peaks.max[6])).toBe(true);
  });
});

describe('后端波形（G11）', () => {
  // 100 秒 8 kHz：第 0 层 12500 桶，分两块；第 1 层 6250 桶，一块取完，是概览层。
  const sampleRate = 8000;
  const signal = new Float32Array(sampleRate * 100).map((_, i) => Math.sin(i / 7) * (0.2 + 0.7 * ((i / sampleRate) % 10) / 10));
  const pyramid = buildPyramid([signal], sampleRate);

  function metaOf(source: PeakPyramid): ProjectWaveformAvailable {
    return {
      projectSessionId: 'p',
      available: true,
      protocolVersion: '1',
      waveformId: 'wf',
      sourceSha256: '0'.repeat(64),
      profile: 'self-contained-native-waveform-v1',
      sampleRate: source.sampleRate,
      sampleCount: String(source.sampleCount),
      durationUs: micros(Math.round((source.sampleCount / source.sampleRate) * 1e6)),
      sourceChannels: 1,
      waveformChannels: 1,
      baseBucketSamples: 64,
      format: 'minmax-f32le-v1',
      // 后端层号与桶宽顺序无关：故意倒序给出。
      levels: source.levels.map((l, i) => ({ level: i, bucketSamples: String(l.bucketSamples), bucketCount: l.min.length })).reverse(),
    };
  }

  /** 假后端：请求先挂起，flush 时按金字塔切窗口返回。 */
  function fakeBackend(source: PeakPyramid, fail?: (request: PeakWindowRequest) => unknown) {
    const requests: PeakWindowRequest[] = [];
    const pending: Array<() => void> = [];
    const fetcher: PeakWindowFetcher = (request) => {
      requests.push(request);
      return new Promise((resolve, reject) =>
        pending.push(() => {
          const error = fail?.(request);
          if (error) return reject(error);
          const level = source.levels[request.level]!;
          const out = new Float32Array(request.count * 2);
          for (let i = 0; i < request.count; i++) {
            out[2 * i] = level.min[request.start + i]!;
            out[2 * i + 1] = level.max[request.start + i]!;
          }
          resolve(out);
        }),
      );
    };
    const flush = async () => {
      while (pending.length > 0) {
        for (const settle of pending.splice(0)) settle();
        await new Promise((r) => setTimeout(r, 0));
      }
    };
    return { fetcher, requests, flush };
  }

  const same = (a: Float32Array, b: Float32Array) => a.every((v, i) => (Number.isNaN(v) ? Number.isNaN(b[i]!) : v === b[i]));

  it('decodePeakWindow 按小端读 float32；长度不是 8 的倍数时报错', () => {
    const bytes = new ArrayBuffer(8);
    new DataView(bytes).setFloat32(0, -0.25, true);
    new DataView(bytes).setFloat32(4, 0.75, true);
    expect([...decodePeakWindow(bytes)]).toEqual([-0.25, 0.75]);
    expect(() => decodePeakWindow(new ArrayBuffer(12))).toThrow('不是 8 的倍数');
  });

  it('块都到齐后，任何视口的列峰值与同一金字塔的 columnPeaks 完全一致', async () => {
    const backend = fakeBackend(pyramid);
    const source = new BackendPeakSource(metaOf(pyramid), backend.fetcher, { maxInFlight: 4 });
    const viewports: Array<[number, number, number]> = [
      [-2_000_000, 120_000, 1000], // 整首，两侧超出
      [30_000_000, 12_000, 1000], // 12 秒视口
      [65_400_000, 400, 1200], // 帧级放大，跨第 0 层两块的边界（8192 × 64 / 8000 ≈ 65.5 秒）
      [99_990_000, 50, 800], // 结尾
    ];
    for (const [startUs, usPerPx, columns] of viewports) {
      for (let round = 0; round < 5; round++) {
        source.columns(startUs, usPerPx, columns);
        await backend.flush();
      }
      const expected = columnPeaks(pyramid, startUs, usPerPx, columns);
      const actual = source.columns(startUs, usPerPx, columns);
      expect(same(actual.min, expected.min) && same(actual.max, expected.max), `${startUs}/${usPerPx}`).toBe(true);
    }
    expect(backend.requests.every((r) => r.count >= 1 && r.count <= CHUNK_BUCKETS && r.start % CHUNK_BUCKETS === 0)).toBe(true);
  });

  it('按需加载：先取一块就能取完的最细一层（概览）；放大后只取视口所在块及两侧各一块；未到达的列用更粗一层顶上', async () => {
    const backend = fakeBackend(pyramid);
    const source = new BackendPeakSource(metaOf(pyramid), backend.fetcher);
    expect(backend.requests).toEqual([{ level: 1, start: 0, count: pyramid.levels[1]!.min.length }]);
    let changes = 0;
    source.subscribe(() => changes++);
    await backend.flush();
    expect(changes).toBe(1);

    // 帧级视口在 20 秒处：第 0 层，第 0 块；它是第一块，只多取右侧一块。
    const view = source.columns(20_000_000, 400, 1000);
    expect(backend.requests.slice(1)).toEqual([
      { level: 0, start: 0, count: CHUNK_BUCKETS },
      { level: 0, start: CHUNK_BUCKETS, count: pyramid.levels[0]!.min.length - CHUNK_BUCKETS },
    ]);
    // 第 0 层没到：用概览层顶上，每列都有值，且包住精确值。
    const exact = columnPeaks(pyramid, 20_000_000, 400, 1000);
    expect(view.min.every((v, i) => v <= exact.min[i]! && view.max[i]! >= exact.max[i]!)).toBe(true);
    await backend.flush();
    expect(source.loadedChunks).toBe(3);
    const loaded = source.columns(20_000_000, 400, 1000);
    expect(same(loaded.min, exact.min)).toBe(true);
    // 已加载、在途的块不重复请求。
    const before = backend.requests.length;
    source.columns(20_000_000, 400, 1000);
    expect(backend.requests.length).toBe(before);
  });

  it('同时在途不超过上限，待取队列只保留最近一次视口要的块', async () => {
    const backend = fakeBackend(pyramid);
    const source = new BackendPeakSource(metaOf(pyramid), backend.fetcher, { maxInFlight: 1 });
    source.columns(0, 400, 1000); // 想要第 0 层第 0、1 块，但名额被概览层占着
    source.columns(0, 80_000, 1000); // 换到第 2 层（每列 640 样本）：之前想要的第 0 层块不再请求
    await backend.flush();
    expect(backend.requests.every((r) => r.level !== 0)).toBe(true);
    expect(backend.requests.length).toBe(2);
  });

  it('块失败按退避重试，最多三次；波形失效（fatal）时交给 onLost，不再请求', async () => {
    let failing = true;
    const backend = fakeBackend(pyramid, (r) => (failing && r.level === 0 ? new Error('网络') : undefined));
    let now = 0;
    const scheduled: number[] = [];
    const source = new BackendPeakSource(metaOf(pyramid), backend.fetcher, { now: () => now, schedule: (_, ms) => scheduled.push(ms) });
    await backend.flush();
    source.columns(0, 400, 100);
    await backend.flush();
    // 视口所在块和右侧预取块都失败：各安排一次重画。
    expect(scheduled).toEqual([1000, 1000]);
    source.columns(0, 400, 100); // 还没到重试时间：不请求
    expect(backend.requests.filter((r) => r.level === 0)).toHaveLength(2);
    now = 1000;
    source.columns(0, 400, 100);
    await backend.flush();
    expect(scheduled).toEqual([1000, 1000, 2000, 2000]);
    now = 3000;
    source.columns(0, 400, 100);
    await backend.flush();
    // 第三次仍失败：不再安排重试，也不再请求。
    expect(scheduled).toHaveLength(4);
    now = 100_000;
    source.columns(0, 400, 100);
    expect(backend.requests.filter((r) => r.level === 0)).toHaveLength(6);

    failing = false;
    const recovered = new BackendPeakSource(metaOf(pyramid), backend.fetcher, { now: () => now, schedule: (_, ms) => scheduled.push(ms) });
    recovered.columns(0, 400, 100);
    await backend.flush();
    expect(recovered.loadedChunks).toBe(3);

    const lost: unknown[] = [];
    const fatal = new Error('404');
    const gone = fakeBackend(pyramid, () => fatal);
    const dead = new BackendPeakSource(metaOf(pyramid), gone.fetcher, { isFatal: (e) => e === fatal, onLost: (e) => lost.push(e) });
    await gone.flush();
    dead.columns(0, 400, 100);
    await gone.flush();
    expect(lost).toEqual([fatal]);
    expect(gone.requests).toHaveLength(1);
  });
});

describe('逐字预览', () => {
  const line = (id: string, startUs?: number, endUs?: number, tokens: Array<[string, number?, number?]> = [], primaryLineId?: string): EditorLine => ({
    branchId: 'b',
    id,
    text: tokens.map((t) => t[0]).join('') || id,
    ...(startUs !== undefined && endUs !== undefined ? { startUs, endUs } : {}),
    ...(primaryLineId ? { primaryLineId } : {}),
    tokens: tokens.map(([text, s, e], i) => ({ id: `${id}-${i}`, lineId: id, text, ...(s !== undefined && e !== undefined ? { startUs: s, endUs: e } : {}) })),
    ruby: [],
  });
  const lines = [line('a', 1_000_000, 2_000_000), line('u'), line('b', 3_000_000, 4_000_000), line('c', 5_000_000, 6_000_000)];

  it('previewWindow：行内取当前行，相邻行跳过未打轴的行', () => {
    const w = previewWindow(lines, 3_500_000);
    expect([w.previous?.id, w.current?.id, w.next?.id, w.active]).toEqual(['a', 'b', 'c', true]);
  });

  it('previewWindow：行间取下一句，开头之前取第一句，唱完取最后一句', () => {
    expect(previewWindow(lines, 2_500_000)).toMatchObject({ current: { id: 'b' }, active: false });
    expect(previewWindow(lines, 0)).toMatchObject({ current: { id: 'a' }, previous: undefined, active: false });
    expect(previewWindow(lines, 9_000_000)).toMatchObject({ current: { id: 'c' }, next: undefined, active: false });
    expect(previewWindow([line('u')], 0)).toEqual({ active: false });
  });

  it('windowKey：同一窗口相同，换行或进出行内时不同', () => {
    expect(windowKey(previewWindow(lines, 3_100_000))).toBe(windowKey(previewWindow(lines, 3_900_000)));
    expect(windowKey(previewWindow(lines, 3_100_000))).not.toBe(windowKey(previewWindow(lines, 2_900_000)));
  });

  it('previewSegments：元素之间的原文单独成段并跟随前一个元素；未定时元素整行唱完才填满', () => {
    const l = { ...line('x', 0, 2_000_000, [['Hello', 0, 500_000], ['world'], ['!', 1_500_000, 1_800_000]]), text: 'Hello, world !' } as EditorLine & { startUs: number; endUs: number };
    expect(previewSegments(l).map((s) => [s.text, s.token?.id ?? null, s.fillStartUs, s.fillEndUs])).toEqual([
      ['Hello', 'x-0', 0, 500_000],
      [', ', null, 500_000, 500_000],
      ['world', 'x-1', 2_000_000, 2_000_000],
      [' ', null, 2_000_000, 2_000_000],
      ['!', 'x-2', 1_500_000, 1_800_000],
    ]);
    const bare = line('n', 1_000_000, 3_000_000) as EditorLine & { startUs: number; endUs: number };
    expect(previewSegments(bare)).toEqual([{ text: 'n', fillStartUs: 1_000_000, fillEndUs: 3_000_000 }]);
  });

  it('previewSegments：注音按片段填充，有时间的用自己的区间，没有时间的跟随所注的元素（W6 设计 §2.5）', () => {
    const l = {
      ...line('r', 0, 1_000_000, [['朝', 200_000, 600_000], ['日', 600_000, 1_000_000]]),
      ruby: [
        {
          id: 'g',
          baseTokenId: 'r-0',
          text: 'あさ',
          relativeScale: 0.5,
          gapPx: 2,
          alignment: 'center' as const,
          segments: [
            { id: 's1', text: 'あ', time: { startUs: micros(200_000), endUs: micros(350_000) }, timingStatus: 'edited' as const },
            { id: 's2', text: 'さ', time: {}, timingStatus: 'missing' as const },
          ],
        },
      ],
    } as EditorLine & { startUs: number; endUs: number };
    const [asa, hi] = previewSegments(l);
    expect(asa!.ruby).toEqual([
      { text: 'あ', fillStartUs: 200_000, fillEndUs: 350_000, timed: true },
      { text: 'さ', fillStartUs: 200_000, fillEndUs: 600_000, timed: false },
    ]);
    expect(hi!.ruby).toBeUndefined();
  });

  it('previewSegments：元素文字在正文里找不到时照常成段，不丢元素', () => {
    const l = { ...line('m', 0, 1_000_000, [['甲', 0, 500_000], ['乙', 500_000, 1_000_000]]), text: '甲丙' } as EditorLine & { startUs: number; endUs: number };
    expect(previewSegments(l).map((s) => s.text)).toEqual(['甲', '乙', '丙']);
  });

  it('fillRatio：线性填充并夹在 0～1；起止相同时到点即填满', () => {
    expect(fillRatio(250_000, 0, 1_000_000)).toBe(0.25);
    expect(fillRatio(-1, 0, 1_000_000)).toBe(0);
    expect(fillRatio(5_000_000, 0, 1_000_000)).toBe(1);
    expect(fillRatio(1_999_999, 2_000_000, 2_000_000)).toBe(0);
    expect(fillRatio(2_000_000, 2_000_000, 2_000_000)).toBe(1);
  });

  it('translationsByPrimary：按主歌词行分组，跳过未关联的行', () => {
    const map = translationsByPrimary([line('t1', undefined, undefined, [], 'a'), line('t2'), line('t3', undefined, undefined, [], 'a')]);
    expect([...map.keys()]).toEqual(['a']);
    expect(map.get('a')!.map((l) => l.id)).toEqual(['t1', 't3']);
  });
});

describe('文本单元切分', () => {
  const tokens = (...texts: string[]) => texts.map((text, i) => ({ id: `t${i}`, lineId: 'l', text }));

  it('graphemes：组合字符与 emoji 算一个文本元素', () => {
    expect(graphemes('朝の')).toEqual(['朝', 'の']);
    expect(graphemes('éa')).toEqual(['é', 'a']);
    expect(graphemes('👍🏽x')).toEqual(['👍🏽', 'x']);
  });

  it('currentSegments：元素之间的原文并入前一段，开头的并入第一段，拼不回时整行一段', () => {
    expect(currentSegments({ text: 'Hello karaoke!', tokens: tokens('Hello', 'karaoke') })).toEqual(['Hello ', 'karaoke!']);
    expect(currentSegments({ text: '「朝の」', tokens: tokens('朝', 'の') })).toEqual(['「朝', 'の」']);
    expect(currentSegments({ text: '朝の', tokens: tokens('夜') })).toEqual(['朝の']);
    expect(currentSegments({ text: '朝の', tokens: [] })).toEqual(['朝の']);
    expect(currentSegments({ text: '', tokens: [] })).toEqual([]);
  });

  it('parseSegments：忽略空片段，必须拼回原文且落在文本元素边界', () => {
    expect(parseSegments('朝|の||光', '朝の光')).toEqual({ ok: true, segments: ['朝', 'の', '光'] });
    expect(parseSegments('|朝の光|', '朝の光')).toEqual({ ok: true, segments: ['朝の光'] });
    expect(parseSegments('', '')).toEqual({ ok: true, segments: [] });
    expect(parseSegments('朝|光', '朝の光')).toMatchObject({ ok: false });
    expect(parseSegments('e|́a', 'éa')).toEqual({ ok: false, error: '切分位置不能落在一个字的中间' });
    expect(parseSegments('a|b', 'a|b')).toMatchObject({ ok: false });
  });
});

describe('来源与翻译关联', () => {
  const line = (branchId: string, id: string, primaryLineId?: string): EditorLine => ({
    branchId,
    id,
    text: id,
    ...(primaryLineId ? { primaryLineId } : {}),
    tokens: [],
    ruby: [],
  });
  const primary = [line('p', 'p1'), line('p', 'p2')];
  const translation = [line('t', 't1', 'p2'), line('t', 't2'), line('t', 't3')];

  it('branchDescription：角色、语言、时间策略、行数，缺语言时省略', () => {
    expect(branchDescription({ id: 't', name: '中文', role: 'translation', language: 'zh', timingMode: 'followPrimary', lineCount: 3 })).toBe(
      '翻译 · zh · 跟随主歌词 · 3 行',
    );
    expect(branchDescription({ id: 'p', name: '主歌词', role: 'primary', timingMode: 'independent', lineCount: 0 })).toBe('主歌词 · 独立时间 · 0 行');
  });

  it('角色与时间策略选项：主来源只能是主歌词且不提供“跟随主歌词”，当前值总在列表里', () => {
    expect(roleOptions(true)).toEqual(['primary']);
    expect(roleOptions(false)).not.toContain('primary');
    expect(timingModeOptions(true, 'independent')).toEqual(['independent', 'none']);
    expect(timingModeOptions(true, 'followPrimary')).toContain('followPrimary');
    expect(timingModeOptions(false, 'none')).toEqual(['independent', 'followPrimary', 'none']);
  });

  it('autoPair：按顺序配对，主歌词行不够时多出的行不关联', () => {
    expect(autoPair(translation, primary)).toEqual({ t1: 'p1', t2: 'p2', t3: null });
  });

  it('linkChanges：只列出与当前关联不同的行，按行顺序；草稿里没有的行不算修改', () => {
    expect(linkChanges(translation, currentLinks(translation))).toEqual([]);
    expect(linkChanges(translation, autoPair(translation, primary))).toEqual([
      { lineId: 't1', primaryLineId: 'p1' },
      { lineId: 't2', primaryLineId: 'p2' },
    ]);
    expect(linkChanges(translation, { t1: null })).toEqual([{ lineId: 't1', primaryLineId: null }]);
  });

  it('lineNumbers：行号从 1 开始', () => {
    expect([...lineNumbers(primary)]).toEqual([
      ['p1', 1],
      ['p2', 2],
    ]);
  });
});

describe('时间轴翻译行轨与主歌词参考轨（W6 设计 §2.6）', () => {
  const line = (branchId: string, id: string, time?: [number, number], primaryLineId?: string): EditorLine => ({
    branchId,
    id,
    text: id,
    ...(time ? { startUs: time[0], endUs: time[1] } : {}),
    ...(primaryLineId ? { primaryLineId } : {}),
    tokens: [],
    ruby: [],
  });
  const primary = [line('p', 'p1', [0, 1000]), line('p', 'p2')];
  const follow = [line('f', 'f1', [5000, 6000], 'p1'), line('f', 'f2', undefined, 'p2'), line('f', 'f3')];
  const own = [line('i', 'i1', [100, 900], 'p1'), line('i', 'i2', undefined, 'p1')];
  const branch = (id: string, role: 'primary' | 'translation' | 'annotation', timingMode: 'independent' | 'followPrimary' | 'none') => ({ id, name: id, role, timingMode, lineCount: 0 });
  const branches = [branch('p', 'primary', 'independent'), branch('f', 'translation', 'followPrimary'), branch('i', 'translation', 'independent'), branch('a', 'annotation', 'independent'), branch('n', 'translation', 'none')];
  const linesByBranch = { p: primary, f: follow, i: own, a: [line('a', 'a1', [0, 10])], n: [line('n', 'n1', [0, 10])] };

  it('trackLines：独立计时画自己的时间；跟随主歌词画在有时间的主歌词行上，不用保留的原时间；不计时不画', () => {
    expect(trackLines('independent', own, primary)).toEqual([{ lineId: 'i1', text: 'i1', startUs: 100, endUs: 900, follow: false }]);
    expect(trackLines('followPrimary', follow, primary)).toEqual([{ lineId: 'f1', text: 'f1', startUs: 0, endUs: 1000, follow: true }]);
    expect(trackLines('none', own, primary)).toEqual([]);
  });

  it('lineTracks：看主歌词时每个“翻译”来源一条（注释不画，只有独立计时的能拖）；看其他来源时一条只读的主歌词参考轨', () => {
    const tracks = lineTracks({ branches, primaryBranchId: 'p', branchId: 'p', linesByBranch });
    expect(tracks.map((t) => [t.branchId, t.editable, t.lines.length, t.empty])).toEqual([
      ['f', false, 1, '这个来源还没有时间'],
      ['i', true, 1, '这个来源还没有时间'],
      ['n', false, 0, '这个来源不计时'],
    ]);
    const reference = lineTracks({ branches, primaryBranchId: 'p', branchId: 'a', linesByBranch });
    expect(reference).toEqual([{ branchId: 'p', name: '主歌词', lines: [{ lineId: 'p1', text: 'p1', startUs: 0, endUs: 1000, follow: false }], empty: '主歌词还没有时间', editable: false }]);
    expect(lineTracks({ branchId: 'p', linesByBranch })).toEqual([]);
  });

  it('followSpans：只有跟随主歌词的来源，已关联的行画在主歌词行的位置', () => {
    expect([...followSpans(branches[1], follow, primary)]).toEqual([['f1', { startUs: 0, endUs: 1000 }]]);
    expect(followSpans(branches[2], own, primary).size).toBe(0);
  });
});

describe('添加歌词与导入报告', () => {
  const issue = (severity: LyricsImportIssue['severity'], sourceLineNumber: number): LyricsImportIssue => ({
    kind: 'textWithoutTimestamp',
    severity,
    sourceLineNumber,
    rawText: 'x',
    message: 'm',
  });
  const report = (patch: Partial<LyricsImportReport> = {}): LyricsImportReport => ({
    importerId: 'lrc.plain',
    importerName: '普通 LRC（仅行级时间）',
    sourceFormat: 'LRC',
    sourceLineCount: 12,
    lineCount: 10,
    tokenCount: 40,
    timedTokenCount: 30,
    missingTokenCount: 10,
    estimatedTokenCount: 0,
    skippedLineCount: 2,
    mergedContinuationCount: 0,
    derivedLineEndCount: 9,
    fallbackLineEndCount: 1,
    offsetUs: micros('0'),
    encodingName: 'utf-8',
    hadByteOrderMark: true,
    newLineKind: 'carriageReturnLineFeed',
    issues: [],
    candidateNames: [],
    isFormatAmbiguous: false,
    discardedFeatures: [],
    splitLineCount: 0,
    splitCreatedTokenCount: 0,
    splitEstimatedTokenCount: 0,
    issueCount: 0,
    ...patch,
  });

  it('fileStem 去掉最后一个扩展名；点开头的文件名保持原样', () => {
    expect(fileStem('song.zh.lrc')).toBe('song.zh');
    expect(fileStem('歌词.txt')).toBe('歌词');
    expect(fileStem('.hidden')).toBe('.hidden');
    expect(fileStem('noext')).toBe('noext');
  });

  it('pastedLineCount 与后端一致：CRLF／CR／LF 都换行，空白行跳过', () => {
    expect(pastedLineCount('')).toBe(0);
    expect(pastedLineCount('  \n\t\n')).toBe(0);
    expect(pastedLineCount('a\r\nb\rc\n\n d ')).toBe(4);
  });

  it('问题按严重程度计数与筛选，筛选保持原顺序', () => {
    const issues = [issue('info', 3), issue('error', 1), issue('warning', 2), issue('error', 5)];
    expect(issueCounts(issues)).toEqual({ error: 2, warning: 1, info: 1 });
    expect(filterIssues(issues, 'error').map((i) => i.sourceLineNumber)).toEqual([1, 5]);
    expect(filterIssues(issues, 'all')).toHaveLength(4);
    expect(worstSeverity(undefined, 'info')).toBe('info');
    expect(worstSeverity('warning', 'error')).toBe('error');
    expect(worstSeverity('error', 'info')).toBe('error');
  });

  it('reportFacts 只列有内容的项；偏移按带符号的秒显示', () => {
    const facts = Object.fromEntries(reportFacts(report()).map((f) => [f.label, f.value]));
    expect(facts['编码']).toBe('utf-8（有 BOM） · 换行 CRLF');
    expect(facts['行数']).toBe('源文件 12 行 → 歌词 10 行，跳过 2 行');
    expect(facts['文本单元']).toBe('40 个：有时间 30，缺时间 10');
    expect(facts['行处理']).toBe('由下一行推出行尾 9，行尾用默认时长 1');
    expect(facts).not.toHaveProperty('时间偏移');
    expect(facts).not.toHaveProperty('重新切分');

    const shifted = Object.fromEntries(
      reportFacts(report({ offsetUs: micros('-1500000'), splitLineCount: 3, splitCreatedTokenCount: 7, discardedFeatures: ['音高'] })).map((f) => [f.label, f.value]),
    );
    expect(shifted['时间偏移']).toBe('−1.5 秒（文件里的 offset 已应用）');
    expect(shifted['重新切分']).toBe('3 行，新建 7 个文本单元');
    expect(shifted['丢弃的内容']).toBe('音高');
    expect(Object.fromEntries(reportFacts(report({ offsetUs: micros('250000') })).map((f) => [f.label, f.value]))['时间偏移']).toMatch(/^\+0\.25 秒/);
  });

  it('decoderLabel 把 .NET 编码名映射到浏览器解码器；不支持的返回 undefined', () => {
    expect(decoderLabel('utf-8')).toBe('utf-8');
    expect(decoderLabel('gb18030')).toBe('gb18030');
    expect(decoderLabel('shift_jis')).toBe('shift_jis');
    expect(decoderLabel('utf-16')).toBe('utf-16le');
    expect(decoderLabel('utf-16BE')).toBe('utf-16be');
    expect(decoderLabel('utf-32')).toBeUndefined();
    expect(decoderLabel('no-such-encoding')).toBeUndefined();
  });

  it('源文件按行号切行；很长时只取焦点附近一段', () => {
    expect(splitSourceLines('a\r\nb\rc\n')).toEqual(['a', 'b', 'c', '']);
    expect(sourceWindow(50, 10, 100)).toEqual([0, 50]);
    expect(sourceWindow(5000, undefined, 1000)).toEqual([0, 1000]);
    expect(sourceWindow(5000, 2000, 1000)).toEqual([1499, 2499]);
    expect(sourceWindow(5000, 4990, 1000)).toEqual([4000, 5000]);
    expect(sourceWindow(5000, 10, 1000)).toEqual([0, 1000]);
  });
});

describe('工程设置', () => {
  it('parseDimension 只接受正整数（奇数也可以）', () => {
    expect(parseDimension(' 1921 ')).toEqual({ ok: true, value: 1921 });
    expect(parseDimension('0').ok).toBe(false);
    expect(parseDimension('-5').ok).toBe(false);
    expect(parseDimension('12.5').ok).toBe(false);
    expect(parseDimension('').ok).toBe(false);
  });

  it('parseFrameRate 接受整数、最多三位小数和分数；小数约分', () => {
    expect(parseFrameRate('30')).toEqual({ ok: true, value: { numerator: 30, denominator: 1 } });
    expect(parseFrameRate('29.97')).toEqual({ ok: true, value: { numerator: 2997, denominator: 100 } });
    expect(parseFrameRate('23.976 fps')).toEqual({ ok: true, value: { numerator: 2997, denominator: 125 } });
    expect(parseFrameRate('30000/1001')).toEqual({ ok: true, value: { numerator: 30000, denominator: 1001 } });
    expect(parseFrameRate('0').ok).toBe(false);
    expect(parseFrameRate('30/0').ok).toBe(false);
    expect(parseFrameRate('29.9701').ok).toBe(false);
    expect(parseFrameRate('abc').ok).toBe(false);
  });

  it('formatFrameRate：整数直接显示，分数附原值', () => {
    expect(formatFrameRate({ numerator: 30, denominator: 1 })).toBe('30 fps');
    expect(formatFrameRate({ numerator: 30000, denominator: 1001 })).toBe('29.97 fps（30000/1001）');
    expect(formatFrameRate({ numerator: 25, denominator: 2 })).toBe('12.5 fps（25/2）');
  });

  it('颜色解析与显示：#RGB／#RRGGBB／#RRGGBBAA，不透明时省略 alpha', () => {
    expect(parseColor('#fff')).toEqual({ ok: true, value: { red: 255, green: 255, blue: 255, alpha: 255 } });
    expect(parseColor('102030')).toEqual({ ok: true, value: { red: 16, green: 32, blue: 48, alpha: 255 } });
    expect(parseColor('#10203080')).toEqual({ ok: true, value: { red: 16, green: 32, blue: 48, alpha: 128 } });
    expect(parseColor('#12345').ok).toBe(false);
    expect(formatColor({ red: 16, green: 32, blue: 48, alpha: 255 })).toBe('#102030');
    expect(formatColor({ red: 0, green: 0, blue: 0, alpha: 0 })).toBe('#00000000');
  });

  it('分析进度：有总采样数按采样算，解码中按已完成时长估算，阶段名翻译成中文', () => {
    expect(analysisProgressText(undefined, 1_000_000)).toEqual({ text: '排队中' });
    expect(analysisProgressText({ stage: 'decoding', completedSamples: '24000', timeUs: micros(500_000) }, 2_000_000)).toEqual({
      text: '解码并分析 25%',
      fraction: 0.25,
    });
    expect(analysisProgressText({ stage: 'prepared', completedSamples: '48000', totalSamples: '96000' }, 2_000_000)).toEqual({ text: '整理结果', fraction: 0.5 });
    expect(analysisProgressText({ stage: 'probing', completedSamples: '0' }, 2_000_000)).toEqual({ text: '读取音频信息' });
    expect(analysisProgressText({ stage: 'somethingNew', completedSamples: '0' }, 0)).toEqual({ text: 'somethingNew' });
  });
});

describe('批量修改分批（W6 设计 §2.2）', () => {
  it('按顺序每批最多 200 条；说明写一条历史或批数', () => {
    expect(editBatches([1, 2, 3, 4, 5], 2)).toEqual([[1, 2], [3, 4], [5]]);
    expect(editBatches([])).toEqual([]);
    expect(editBatches(Array.from({ length: 401 })).map((b) => b.length)).toEqual([200, 200, 1]);
    expect(batchHistoryText(3)).toBe('记一条历史，可以一次撤销');
    expect(batchHistoryText(200)).toBe('记一条历史，可以一次撤销');
    expect(batchHistoryText(401)).toBe('超过 200 条，将分 3 批提交，每批一条历史');
  });
});
