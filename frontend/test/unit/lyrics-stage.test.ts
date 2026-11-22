import { describe, expect, it } from 'vitest';
import type { EditorLine } from '../../src/editor/model/lyrics';
import { toMicros } from '../../src/editor/model/time';
import {
  computeFullTextDiff,
  parseInlineRubyLine,
  serializeBranchText,
  serializeLineWithInlineRuby,
} from '../../src/editor/lyrics/full-text-diff';
import {
  collectBranchBeats,
  computeMonotonicStrikeUs,
  computeTokenWipeProgress,
  computeUntimedBridgeFill,
  findBeatIndexForSelection,
  firstBeatSelectionForLine,
  lineHasAnyTiming,
  lineHasChildTiming,
  resolveTimingKeyAction,
  stepBranchBeat,
  stepContentToken,
} from '../../src/editor/lyrics/timing-keymap';

function makeLine(overrides: Partial<EditorLine> & { id: string; text: string }): EditorLine {
  return {
    branchId: 'primary',
    tokens: [],
    ruby: [],
    ...overrides,
  };
}

describe('Phase 3: 拍点序列与词元语义导航', () => {
  const sampleLines: EditorLine[] = [
    makeLine({
      id: 'L1',
      text: '「二人 で」',
      tokens: [
        { id: 'T0', lineId: 'L1', text: '「', status: 'missing' },
        { id: 'T1', lineId: 'L1', text: '二人', status: 'missing' },
        { id: 'T2', lineId: 'L1', text: ' ', status: 'missing' },
        { id: 'T3', lineId: 'L1', text: 'で', status: 'missing' },
        { id: 'T4', lineId: 'L1', text: '」', status: 'missing' },
      ],
      ruby: [
        {
          id: 'R1',
          baseTokenId: 'T1',
          text: 'ふたり',
          relativeScale: 0.5,
          gapPx: 2,
          alignment: 'center',
          segments: [
            { id: 'S1', text: 'ふ', time: {}, timingStatus: 'missing' },
            { id: 'S2', text: 'た', time: {}, timingStatus: 'missing' },
            { id: 'S3', text: 'り', time: {}, timingStatus: 'missing' },
          ],
        },
      ],
    }),
    makeLine({
      id: 'L2',
      text: 'Hello world',
      tokens: [
        { id: 'T5', lineId: 'L2', text: 'Hello', status: 'missing' },
        { id: 'T6', lineId: 'L2', text: ' ', status: 'missing' },
        { id: 'T7', lineId: 'L2', text: 'world', status: 'missing' },
      ],
    }),
  ];

  it('collectBranchBeats 自动跳过空白与标点，展开多拍注音，并准确标注停顿收尾提示', () => {
    const beats = collectBranchBeats(sampleLines);
    expect(beats.map((b) => `${b.tokenId}${b.segmentId ? `:${b.segmentId}` : ''}`)).toEqual([
      'T1:S1',
      'T1:S2',
      'T1:S3',
      'T3',
      'T5',
      'T7',
    ]);

    // CJK 后紧接空格 -> 二人 具有停顿提示 ○，且只挂在最后一个注音拍 S3 上！
    expect(beats[0]!.isPhraseEnd).toBe(false);
    expect(beats[1]!.isPhraseEnd).toBe(false);
    expect(beats[2]!.isPhraseEnd).toBe(true);
    // 行末实义词元 で -> isPhraseEnd = true
    expect(beats[3]!.isPhraseEnd).toBe(true);
    // 英文单词间单个半角空格 -> Hello 不是停顿提示，world 是行末
    expect(beats[4]!.isPhraseEnd).toBe(false);
    expect(beats[5]!.isPhraseEnd).toBe(true);
  });

  it('findBeatIndexForSelection、stepContentToken 与 stepBranchBeat 正确跨行与跳空白', () => {
    const beats = collectBranchBeats(sampleLines);
    expect(findBeatIndexForSelection(beats, { lineId: 'L1', tokenId: 'T1', segmentId: 'S2' })).toBe(1);
    expect(findBeatIndexForSelection(beats, { lineId: 'L1', tokenId: 'T1' })).toBe(0);
    expect(findBeatIndexForSelection(beats, { lineId: 'L2' })).toBe(4);

    // stepContentToken skips 「, space, 」, space
    expect(stepContentToken(sampleLines, { lineId: 'L1', tokenId: 'T1' }, 1)).toEqual({ lineId: 'L1', tokenId: 'T3' });
    expect(stepContentToken(sampleLines, { lineId: 'L1', tokenId: 'T3' }, 1)).toEqual({ lineId: 'L2', tokenId: 'T5' });
    expect(stepContentToken(sampleLines, { lineId: 'L2', tokenId: 'T5' }, 1)).toEqual({ lineId: 'L2', tokenId: 'T7' });
    expect(stepContentToken(sampleLines, { lineId: 'L2', tokenId: 'T5' }, -1)).toEqual({ lineId: 'L1', tokenId: 'T3' });

    expect(stepBranchBeat(beats, { lineId: 'L1', tokenId: 'T1', segmentId: 'S1' }, 1)?.segmentId).toBe('S2');
    expect(stepBranchBeat(beats, { lineId: 'L1', tokenId: 'T1', segmentId: 'S3' }, 1)?.tokenId).toBe('T3');
  });
});

describe('Phase 3: 契约 B1 分段扫字进度与无时间桥接', () => {
  it('多拍注音按字素数权重做分段插值，并在拍间空隙保持原位', () => {
    const token = { startUs: 1_000_000, endUs: 3_000_000 };
    const ruby = {
      id: 'R1',
      baseTokenId: 'T1',
      text: 'きょう',
      relativeScale: 0.5,
      gapPx: 2,
      alignment: 'center' as const,
      segments: [
        // きょ (2 graphemes, weight 2/3) in [1.0s, 1.8s]
        { id: 'S1', text: 'きょ', time: { startUs: toMicros(1_000_000), endUs: toMicros(1_800_000) }, timingStatus: 'edited' as const },
        // う (1 grapheme, weight 1/3) in [2.0s, 3.0s] (200ms inter-beat gap)
        { id: 'S2', text: 'う', time: { startUs: toMicros(2_000_000), endUs: toMicros(3_000_000) }, timingStatus: 'edited' as const },
      ],
    };

    expect(computeTokenWipeProgress(token, ruby, 500_000)).toBe(0);
    // Midpoint of first segment => half of 2/3 = 1/3
    expect(computeTokenWipeProgress(token, ruby, 1_400_000)).toBeCloseTo(1 / 3, 5);
    // Inside inter-beat silent gap [1.8s, 2.0s] => stays at 2/3
    expect(computeTokenWipeProgress(token, ruby, 1_900_000)).toBeCloseTo(2 / 3, 5);
    // Midpoint of second segment => 2/3 + 0.5 * 1/3 = 5/6
    expect(computeTokenWipeProgress(token, ruby, 2_500_000)).toBeCloseTo(5 / 6, 5);
    expect(computeTokenWipeProgress(token, ruby, 3_200_000)).toBe(1);
  });

  it('无时间空白与标点按规则跟亮，未定时实义字绝不跟亮', () => {
    const tokens = [
      { id: 'T0', lineId: 'L1', text: '「', status: 'missing' as const },
      { id: 'T1', lineId: 'L1', text: 'あ', startUs: 1_000_000, endUs: 1_500_000, status: 'edited' as const },
      { id: 'T2', lineId: 'L1', text: ' ', status: 'missing' as const },
      { id: 'T3', lineId: 'L1', text: 'い', status: 'missing' as const }, // untimed content!
      { id: 'T4', lineId: 'L1', text: '」', status: 'missing' as const },
    ];

    // Leading punct 「 bridges to first timed token T1.startUs (1.0s)
    expect(computeUntimedBridgeFill(tokens, 0, 900_000)).toBe(0);
    expect(computeUntimedBridgeFill(tokens, 0, 1_000_000)).toBe(1);

    // Mid space bridges to preceding timed token T1.endUs (1.5s)
    expect(computeUntimedBridgeFill(tokens, 2, 1_400_000)).toBe(0);
    expect(computeUntimedBridgeFill(tokens, 2, 1_500_000)).toBe(1);

    // Untimed content token NEVER bridges
    expect(computeUntimedBridgeFill(tokens, 3, 2_000_000)).toBe(0);
    expect(computeTokenWipeProgress(tokens[3]!, undefined, 2_000_000)).toBe(0);

    // Trailing punct 」 bridges to nearest preceding timed token T1.endUs (1.5s)
    expect(computeUntimedBridgeFill(tokens, 4, 1_500_000)).toBe(1);
  });
});

describe('Phase 3: 模式 M1 快捷键与高速轮指保护', () => {
  const key = (k: string, extra: Partial<Parameters<typeof resolveTimingKeyAction>[0]> = {}) => ({
    key: k,
    code: '',
    ctrlKey: false,
    metaKey: false,
    altKey: false,
    shiftKey: false,
    repeat: false,
    ...extra,
  });

  it('打轴模式下正确解析 J/K 连打、L 收尾、Backspace/G 纯撤回、Shift+Backspace 清本行、Q/W 变速、Z/X 寻轨、F2 注音', () => {
    expect(resolveTimingKeyAction(key('j'), 'timing')).toEqual({ kind: 'strikeBeat' });
    expect(resolveTimingKeyAction(key('K'), 'timing')).toEqual({ kind: 'strikeBeat' });
    expect(resolveTimingKeyAction(key('j', { repeat: true }), 'timing')).toBeNull();
    expect(resolveTimingKeyAction(key('l'), 'timing')).toEqual({ kind: 'closeBeat' });
    expect(resolveTimingKeyAction(key('Backspace'), 'timing')).toEqual({ kind: 'undoBeat' });
    expect(resolveTimingKeyAction(key('g'), 'timing')).toEqual({ kind: 'undoBeat' });
    expect(resolveTimingKeyAction(key('Backspace', { shiftKey: true }), 'timing')).toEqual({ kind: 'clearLineTiming' });
    expect(resolveTimingKeyAction(key('Backspace', { shiftKey: true, repeat: true }), 'timing')).toBeNull();
    expect(resolveTimingKeyAction(key('q'), 'timing')).toEqual({ kind: 'speedDown', fine: false });
    expect(resolveTimingKeyAction(key('W', { shiftKey: true }), 'timing')).toEqual({ kind: 'speedUp', fine: true });
    expect(resolveTimingKeyAction(key('z'), 'timing')).toEqual({ kind: 'seekBack' });
    expect(resolveTimingKeyAction(key('x'), 'timing')).toEqual({ kind: 'seekForward' });
    expect(resolveTimingKeyAction(key('F2'), 'timing')).toEqual({ kind: 'openRubyPopover' });
    expect(resolveTimingKeyAction(key('ArrowRight', { altKey: true }), 'timing')).toEqual({ kind: 'nextBeat' });
    expect(resolveTimingKeyAction(key('['), 'timing')).toEqual({ kind: 'splitRubyBeat' });
    expect(resolveTimingKeyAction(key(']'), 'timing')).toEqual({ kind: 'mergeRubyBeat' });
  });

  it('lineHasAnyTiming、lineHasChildTiming 与 firstBeatSelectionForLine 正确识别行/词元/注音时间', () => {
    const emptyLine = makeLine({ id: 'E1', text: 'abc', tokens: [{ id: 't0', lineId: 'E1', text: 'abc' }] });
    expect(lineHasAnyTiming(emptyLine)).toBe(false);
    expect(lineHasChildTiming(emptyLine)).toBe(false);

    const lineOnlyTimed = makeLine({
      id: 'E2',
      text: 'abc',
      startUs: 1_000_000,
      endUs: 2_000_000,
      tokens: [{ id: 't0', lineId: 'E2', text: 'abc' }],
    });
    expect(lineHasAnyTiming(lineOnlyTimed)).toBe(true);
    expect(lineHasChildTiming(lineOnlyTimed)).toBe(false);

    const rubyTimed = makeLine({
      id: 'E3',
      text: '二人',
      tokens: [{ id: 't0', lineId: 'E3', text: '二人' }],
      ruby: [
        {
          id: 'r0',
          baseTokenId: 't0',
          text: 'ふたり',
          relativeScale: 0.5,
          gapPx: 2,
          alignment: 'center',
          segments: [
            { id: 's0', text: 'ふ', time: { startUs: toMicros(1_000_000), endUs: toMicros(1_200_000) }, timingStatus: 'edited' },
            { id: 's1', text: 'た', time: {}, timingStatus: 'missing' },
          ],
        },
      ],
    });
    expect(lineHasAnyTiming(rubyTimed)).toBe(true);
    expect(lineHasChildTiming(rubyTimed)).toBe(true);
    expect(firstBeatSelectionForLine(rubyTimed)).toEqual({ lineId: 'E3', tokenId: 't0', segmentId: 's0' });
  });

  it('编辑模式下 L 翻转停顿、Enter 断行、Shift+Enter 合行、Ctrl+Enter 行内改词、Alt+方向键微调帧', () => {
    expect(resolveTimingKeyAction(key('l'), 'edit')).toEqual({ kind: 'togglePhraseEndHint' });
    expect(resolveTimingKeyAction(key('Enter'), 'edit')).toEqual({ kind: 'splitLine' });
    expect(resolveTimingKeyAction(key('Enter', { shiftKey: true }), 'edit')).toEqual({ kind: 'mergeLines' });
    expect(resolveTimingKeyAction(key('Enter', { ctrlKey: true }), 'edit')).toEqual({ kind: 'inlineEditLine' });
    expect(resolveTimingKeyAction(key('Delete'), 'edit')).toEqual({ kind: 'clearSelectedTiming' });
    expect(resolveTimingKeyAction(key('j'), 'edit')).toEqual({ kind: 'openTokenMenu' });
    expect(resolveTimingKeyAction(key('ArrowLeft', { altKey: true }), 'edit')).toEqual({ kind: 'nudgeTime', frameDelta: -1 });
    expect(resolveTimingKeyAction(key('ArrowDown', { altKey: true }), 'edit')).toEqual({ kind: 'nudgeTime', frameDelta: 1 });
  });

  it('全文快编模式完全让出所有按键，且支持自定义快捷键覆盖', () => {
    expect(resolveTimingKeyAction(key('j'), 'quickEdit')).toBeNull();
    expect(resolveTimingKeyAction(key('F2'), 'quickEdit')).toBeNull();

    expect(resolveTimingKeyAction(key('d'), 'timing', { strikeBeat: ['d', 'f'] })).toEqual({ kind: 'strikeBeat' });
    expect(resolveTimingKeyAction(key('j'), 'timing', { strikeBeat: ['d', 'f'] })).toBeNull();
  });

  it('computeMonotonicStrikeUs 应用延迟补偿并保证极快轮指严格递增', () => {
    // 50ms latency compensation => 1.000s becomes 0.950s
    const t1 = computeMonotonicStrikeUs(1_000_000, 50, null);
    expect(t1).toBe(950_000);
    // Second rapid tap at identical audio tick => bumped by +1ms
    const t2 = computeMonotonicStrikeUs(1_000_000, 50, t1);
    expect(t2).toBe(951_000);
    // Clamp non-negative near 0
    expect(computeMonotonicStrikeUs(20_000, 100, null)).toBe(0);
  });
});

describe('Phase 3: 全文快编 LCS Diff 与内联注音', () => {
  const existing: EditorLine[] = [
    makeLine({
      id: 'L1',
      text: '二人で歩こう',
      tokens: [
        { id: 'T1', lineId: 'L1', text: '二人' },
        { id: 'T2', lineId: 'L1', text: 'で' },
        { id: 'T3', lineId: 'L1', text: '歩' },
        { id: 'T4', lineId: 'L1', text: 'こう' },
      ],
      ruby: [
        {
          id: 'R1',
          baseTokenId: 'T1',
          text: 'ふたり',
          relativeScale: 0.5,
          gapPx: 2,
          alignment: 'center',
          segments: [
            { id: 'S1', text: 'ふ', time: {}, timingStatus: 'missing' },
            { id: 'S2', text: 'た', time: {}, timingStatus: 'missing' },
            { id: 'S3', text: 'り', time: {}, timingStatus: 'missing' },
          ],
        },
      ],
    }),
    makeLine({
      id: 'L2',
      text: '星降る夜に',
      tokens: [
        { id: 'T5', lineId: 'L2', text: '星' },
        { id: 'T6', lineId: 'L2', text: '降' },
        { id: 'T7', lineId: 'L2', text: 'る' },
        { id: 'T8', lineId: 'L2', text: '夜' },
        { id: 'T9', lineId: 'L2', text: 'に' },
      ],
    }),
    makeLine({
      id: 'L3',
      text: 'さよなら',
      tokens: [{ id: 'T10', lineId: 'L3', text: 'さよなら' }],
    }),
  ];

  it('行级 + 字素级 LCS 正确识别未变、修改、新增与删除行并保留 sourceLineId', () => {
    const draft = ['二人で歩こう', '星降る夜空に', '新しい行'].join('\n');
    const result = computeFullTextDiff(existing, draft);

    expect(result.stats.unchangedLines).toBe(1);
    expect(result.stats.modifiedLines).toBe(1);
    expect(result.stats.addedLines).toBe(1);
    expect(result.stats.removedLines).toBe(1);
    expect(result.stats.hasTextChanges).toBe(true);

    expect(result.diffLines).toEqual([
      { text: '二人で歩こう', sourceLineId: 'L1' },
      { text: '星降る夜空に', sourceLineId: 'L2' },
      { text: '新しい行' },
    ]);
    expect(result.removedLines.map((r) => r.lineId)).toEqual(['L3']);
  });

  it('内联注音双向序列化与解析准确无损', () => {
    expect(serializeLineWithInlineRuby(existing[0]!)).toBe('二人[ふ・た・り]で歩こう');
    expect(serializeBranchText(existing, false)).toBe('二人で歩こう\n星降る夜に\nさよなら');

    const parsed = parseInlineRubyLine('二人[ふ・た・り]で歩[ある]こう');
    expect(parsed.plainText).toBe('二人で歩こう');
    expect(parsed.spans).toEqual([
      { start: 0, length: 2, baseText: '二人', segments: ['ふ', 'た', 'り'] },
      { start: 3, length: 1, baseText: '歩', segments: ['あ', 'る'] },
    ]);
  });
});
