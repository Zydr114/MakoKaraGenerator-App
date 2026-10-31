// W5 新建工程与恢复的页面模型（W5 设计 §2.3、§2.6）：四种组合、默认名称、识别结果、改选候选、恢复候选的排序与时间。
// 导入规划的识别结果由新建工程和导入文件两个对话框共用（W6 设计 §2.3）。

import { describe, expect, it } from 'vitest';
import {
  AUTO,
  chosen,
  encodingChoices,
  formatChoices,
  needsFormatChoice,
  planErrorText,
  planNotes,
  planSummary,
  previewRows,
  primaryImportBlock,
  sourceChoices,
} from '../../src/editor/model/lyrics-import';
import { defaultProjectName, newProjectKind } from '../../src/editor/model/new-project';
import { candidateDescription, formatSavedAt, sortCandidates } from '../../src/editor/model/recovery';
import { micros, type EditableLyricLine, type LyricsImportPlan, type LyricsImportReport } from '../../src/protocol';

function plan(overrides: Partial<LyricsImportPlan> = {}, report: Partial<LyricsImportReport> = {}): LyricsImportPlan {
  return {
    assetId: 'a1',
    fileName: 'x.lrc',
    byteLength: 10,
    detectedImporterId: 'lrc.enhanced',
    detectionReason: '文件里含逐字时间标签',
    isFormatAmbiguous: false,
    formatCandidates: [{ importerId: 'lrc.enhanced', name: '增强 LRC（含逐字时间）', sourceFormat: '', reason: '' }],
    encodingName: 'utf-8',
    hadByteOrderMark: false,
    isEncodingAmbiguous: false,
    encodingCandidates: [
      { name: 'utf-8', reason: '' },
      { name: 'gb18030', reason: '' },
    ],
    sourceBranches: [{ id: 'primary', name: '主歌词', role: 'primary', lineCount: 12 }],
    selectedSourceBranchId: 'primary',
    report: { importerName: '增强 LRC（含逐字时间）', encodingName: 'utf-8', lineCount: 12, tokenCount: 100, timedTokenCount: 98, skippedLineCount: 0, issues: [], ...report } as LyricsImportReport,
    previewLines: [],
    protocolVersion: '1',
    ...overrides,
  };
}

describe('新建工程', () => {
  it('四种组合与默认名称', () => {
    expect([newProjectKind(true, true), newProjectKind(false, true), newProjectKind(true, false), newProjectKind(false, false)]).toEqual(['full', 'lyricsOnly', 'audioOnly', 'empty']);
    expect(defaultProjectName('夜に駆ける.wav', 'x.lrc')).toBe('夜に駆ける');
    expect(defaultProjectName(undefined, '歌词.lrc')).toBe('歌词');
    expect(defaultProjectName()).toBe('新工程');
  });

  it('识别结果一行写明格式、编码、行数和逐字时间', () => {
    expect(planSummary(plan())).toBe('识别为：增强 LRC（含逐字时间） · UTF-8 · 12 行 · 逐字时间 98/100');
    expect(planSummary(plan({}, { importerName: '普通 LRC', encodingName: 'gb18030', timedTokenCount: 0 }))).toBe('识别为：普通 LRC · GB18030（简体中文） · 12 行 · 没有逐字时间');
  });

  it('格式不确定、多个来源、跳过的行都提示；解析失败给原因', () => {
    const notes = planNotes(
      plan(
        {
          isFormatAmbiguous: true,
          formatCandidates: [
            { importerId: 'text.plain', name: '纯文本', sourceFormat: '', reason: '' },
            { importerId: 'lrc.plain', name: '普通 LRC', sourceFormat: '', reason: '' },
          ],
          sourceBranches: [
            { id: 'primary', name: '主歌词', role: 'primary', lineCount: 3 },
            { id: 'b2', name: '翻译', role: 'translation', lineCount: 3 },
          ],
        },
        { skippedLineCount: 2 },
      ),
      'create',
    );
    expect(notes).toHaveLength(3);
    expect(notes[1]).toBe('这是 MakoKara 工程 TTML，新建时会恢复全部 2 个来源。');
    const failed = plan({ preparationError: { code: 'lyricsImportFailed', message: 'x', protocolVersion: '1', failureCode: 'UnsupportedLyricsFormat' }, formatCandidates: [] });
    expect(planErrorText(failed)).toMatch(/无法识别/);
    expect(planSummary(failed)).toBeUndefined();
  });

  it('候选第一项是“自动识别”；解析失败时可以改选全部导入器', () => {
    expect(formatChoices(plan()).map((c) => c.value)).toEqual([AUTO, 'lrc.enhanced']);
    expect(formatChoices(plan({ formatCandidates: [] })).length).toBeGreaterThan(5);
    expect(encodingChoices(plan()).map((c) => c.label)).toEqual(['自动识别', 'UTF-8', 'GB18030（简体中文）']);
    expect([chosen(AUTO), chosen('gb18030')]).toEqual([undefined, 'gb18030']);
    expect(needsFormatChoice(plan())).toBe(false);
    expect(needsFormatChoice(plan({ isEncodingAmbiguous: true }))).toBe(true);
    expect(needsFormatChoice(plan({ formatCandidates: [] }))).toBe(true);
  });
});

describe('导入文件的识别结果（W6 设计 §2.3）', () => {
  const sources = {
    sourceBranches: [
      { id: 'primary', name: '主歌词', role: 'primary', lineCount: 3 },
      { id: 'tr', name: '中文', role: 'translation', lineCount: 2 },
    ],
  };
  const line = (id: string, text: string, extra: Partial<EditableLyricLine> = {}): EditableLyricLine => ({
    branchId: 'primary',
    id,
    text,
    time: {},
    timingStatus: 'missing',
    tokens: [],
    rubyAnnotations: [],
    ...extra,
  });
  const token = (id: string, text: string, timed: boolean) => ({
    id,
    text,
    time: timed ? { startUs: micros(0), endUs: micros(100) } : {},
    timingStatus: timed ? ('imported' as const) : ('missing' as const),
  });

  it('多来源的工程 TTML：导入时写明一次导入一个，来源候选写出角色和行数', () => {
    expect(planNotes(plan(sources), 'import')).toEqual(['这是 MakoKara 工程 TTML，有 2 个来源，一次导入一个。请在下面选择要导入的来源。']);
    expect(sourceChoices(plan(sources)).map((c) => c.label)).toEqual(['主歌词（主歌词，3 行）', '中文（翻译，2 行）']);
    expect(planSummary(plan({ ...sources }, { lineCount: 5 }))).toBe('识别为：增强 LRC（含逐字时间） · UTF-8 · 2 个来源共 5 行 · 逐字时间 98/100');
  });

  it('预览写出注音与时间：多片段注音用“·”隔开', () => {
    const rows = previewRows(
      plan({
        previewLines: [
          line('l1', '朝の光', {
            tokens: [token('t1', '朝', true), token('t2', 'の', true), token('t3', '光', true)],
            rubyAnnotations: [
              { id: 'r1', baseTokenId: 't1', text: 'あさ', relativeScale: 0.5, gapPx: 0, alignment: 'center', segments: [{ id: 's1', text: 'あ', time: {}, timingStatus: 'missing' }, { id: 's2', text: 'さ', time: {}, timingStatus: 'missing' }] },
              { id: 'r2', baseTokenId: 't3', text: 'ひかり', relativeScale: 0.5, gapPx: 0, alignment: 'center', segments: [{ id: 's3', text: 'ひかり', time: {}, timingStatus: 'missing' }] },
            ],
          }),
          line('l2', '風声', { tokens: [token('t4', '風', true), token('t5', '声', false)] }),
          line('l3', 'line', { time: { startUs: micros(0), endUs: micros(1) }, tokens: [token('t6', 'line', false)] }),
          line('l4', 'none', { tokens: [token('t7', 'none', false)] }),
          line('l5', 'cut'),
        ],
      }),
      4,
    );
    expect(rows).toEqual([
      { id: 'l1', text: '朝の光', ruby: '朝（あ·さ） 光（ひかり）', timing: '逐字时间' },
      { id: 'l2', text: '風声', timing: '逐字时间 1/2' },
      { id: 'l3', text: 'line', timing: '行时间' },
      { id: 'l4', text: 'none', timing: '没有时间' },
    ]);
  });

  it('关联主歌词的行不能导入到主歌词；空文件和解析出错写明原因', () => {
    const linked = plan({ previewLines: [line('l1', '晨光', { primaryLineId: 'p1' })] });
    expect(primaryImportBlock(linked, true)).toMatch(/不能导入到主歌词/);
    expect(primaryImportBlock(linked, false)).toBeUndefined();
    expect(primaryImportBlock(plan({ previewLines: [line('l1', 'x')] }), true)).toBeUndefined();
    const empty = plan({ preparationError: { code: 'emptyLyrics', message: 'x', protocolVersion: '1', failureCode: 'EmptyImport' } });
    expect(planErrorText(empty)).toBe('文件里没有可以导入的歌词行。');
    const broken = plan(
      { preparationError: { code: 'emptyLyrics', message: 'x', protocolVersion: '1', failureCode: 'EmptyImport' } },
      { issues: [{ kind: 'malformedTag', severity: 'error', sourceLineNumber: 0, rawText: '', message: 'TTML 无法安全映射：根元素必须为 TTML tt。' }] },
    );
    expect(planErrorText(broken)).toBe('文件解析失败：TTML 无法安全映射：根元素必须为 TTML tt。');
  });
});

describe('恢复候选', () => {
  const now = new Date(2026, 9, 26, 20, 0);
  const at = (d: Date) => d.toISOString();

  it('按保存时间倒序，按工程名搜索', () => {
    const list = [
      { recoveryId: 'a', projectName: '旧歌', savedAtUtc: at(new Date(2026, 9, 1, 8, 0)), hasOriginalPath: true },
      { recoveryId: 'b', projectName: 'New Song', savedAtUtc: at(new Date(2026, 9, 26, 14, 20)), hasOriginalPath: false },
    ];
    expect(sortCandidates(list).map((c) => c.recoveryId)).toEqual(['b', 'a']);
    expect(sortCandidates(list, 'song').map((c) => c.recoveryId)).toEqual(['b']);
  });

  it('时间写成今天、昨天、月日，不是今年加年份', () => {
    expect(formatSavedAt(at(new Date(2026, 9, 26, 14, 20)), now)).toBe('今天 14:20');
    expect(formatSavedAt(at(new Date(2026, 9, 25, 9, 5)), now)).toBe('昨天 09:05');
    expect(formatSavedAt(at(new Date(2026, 9, 17, 18, 26)), now)).toBe('10月17日 18:26');
    expect(formatSavedAt(at(new Date(2025, 0, 2, 3, 4)), now)).toBe('2025年1月2日 03:04');
    expect(candidateDescription({ recoveryId: 'x', projectName: 'p', savedAtUtc: at(new Date(2026, 9, 26, 14, 20)), hasOriginalPath: true }, now)).toBe('今天 14:20 · 记得原来的保存位置');
  });
});
