// W5 导出页（W5 设计 §2.3）：总帧数、歌词时间（按后端导出检查重算，G29）、准备检查、任务进度文字。

import { describe, expect, it } from 'vitest';
import { blocked, defaultValidationFrames, readinessChecks, timingProblems, totalFrames, type ReadinessInput, type TimingBranch } from '../../src/editor/model/export-readiness';
import type { EditorLine, EditorToken } from '../../src/editor/model/lyrics';
import { downloadName, failureText, progressFraction, progressText } from '../../src/editor/model/render-job';
import type { ExportCapabilities } from '../../src/protocol';

const token = (id: string, text: string, startUs?: number, endUs?: number): EditorToken => ({
  id,
  lineId: 'l',
  text,
  ...(startUs !== undefined ? { startUs } : {}),
  ...(endUs !== undefined ? { endUs } : {}),
});

const line = (id: string, tokens: EditorToken[], time?: [number, number], extra: Partial<EditorLine> = {}): EditorLine => ({
  branchId: 'b',
  id,
  text: tokens.map((t) => t.text).join('') || id,
  ...(time ? { startUs: time[0], endUs: time[1] } : {}),
  tokens,
  ruby: [],
  ...extra,
});

const primary: TimingBranch = { id: 'p', name: '主歌词', role: 'primary', timingMode: 'independent' };
const translation: TimingBranch = { id: 't', name: '翻译', role: 'translation', timingMode: 'followPrimary' };

describe('导出：帧数', () => {
  it('max(1, round(时长 × 帧率))，正式验证默认取总帧数与上限的较小者', () => {
    expect(totalFrames(4_000_000, { numerator: 30, denominator: 1 })).toBe(120);
    expect(totalFrames(1_000_000, { numerator: 30000, denominator: 1001 })).toBe(30);
    expect(totalFrames(10, { numerator: 30, denominator: 1 })).toBe(1);
    expect(defaultValidationFrames(120, 600)).toBe(120);
    expect(defaultValidationFrames(9000, 600)).toBe(600);
  });
});

describe('导出：歌词时间', () => {
  it('主歌词逐字：句或字缺时间、空字都算，指到第一个有问题的字', () => {
    const lines = {
      p: [
        line('l1', [token('a', '你', 0, 100), token('b', '好', 100, 200)], [0, 200]),
        line('l2', [token('c', '第', 300, 400), token('d', '二')], [300, 600]),
        line('l3', [token('e', '', 700, 800)]),
      ],
    };
    const problems = timingProblems([primary], lines);
    expect(problems.map((p) => [p.lineId, p.lineNumber, p.missingLineTime, p.missingTokens, p.emptyTexts, p.tokenId])).toEqual([
      ['l2', 2, false, 1, 0, 'd'],
      ['l3', 3, true, 0, 1, 'e'],
    ]);
  });

  it('翻译默认静态：没有时间的字不算；跟随主歌词且关联了主歌词行的句子可以没有时间', () => {
    const lines = {
      t: [
        line('t1', [token('x', 'hello')], undefined, { primaryLineId: 'l1' }),
        line('t2', [token('y', 'world')]),
        line('t3', [token('z', 'half', 100)], [0, 500]),
      ],
    };
    const problems = timingProblems([translation], lines);
    expect(problems.map((p) => [p.lineId, p.missingLineTime, p.missingTokens])).toEqual([
      ['t2', true, 0],
      ['t3', false, 1],
    ]);
    // 编排把翻译设成逐字时，字也要有时间。
    expect(timingProblems([{ ...translation, animationPolicy: 'Karaoke' }], lines).map((p) => p.lineId)).toEqual(['t1', 't2', 't3']);
  });
});

const caps = { enabled: true, formats: ['opaqueMp4', 'transparentProRes4444'], quality: 'standard' } as ExportCapabilities;

const input = (overrides: Partial<ReadinessInput> = {}): ReadinessInput => ({
  capabilities: caps,
  format: 'opaqueMp4',
  includeAudio: true,
  canvasWidth: 1920,
  canvasHeight: 1080,
  frameRate: { numerator: 30, denominator: 1 },
  outputDurationUs: 4_000_000,
  hasAudio: true,
  resources: [],
  hasBackgroundMedia: false,
  timing: [],
  branchName: (id) => id,
  validation: 'none',
  ...overrides,
});

const levels = (i: ReadinessInput) => Object.fromEntries(readinessChecks(i).map((c) => [c.id, c.level]));

describe('导出：准备检查', () => {
  it('都满足时没有阻断项；正式验证没做过只是提示', () => {
    const checks = readinessChecks(input());
    expect(blocked(checks)).toBe(false);
    expect(levels(input())).toMatchObject({ capabilities: 'ok', duration: 'ok', timing: 'ok', canvas: 'ok', audio: 'ok', validation: 'info' });
  });

  it('输出时长未定、MP4 奇数画布、超出部署限制都阻断，并给出修复入口', () => {
    const undetermined = input({ canvasWidth: 1281 });
    delete undetermined.outputDurationUs;
    const checks = readinessChecks(undetermined);
    expect(checks.find((c) => c.id === 'duration')).toMatchObject({ level: 'block', fix: { kind: 'settings', group: 'duration' } });
    expect(checks.find((c) => c.id === 'canvas')).toMatchObject({ level: 'block', fix: { kind: 'settings', group: 'canvas' } });
    expect(levels(input({ canvasWidth: 1281, format: 'transparentProRes4444' })).canvas).toBe('ok');
    expect(levels(input({ capabilities: { ...caps, maxFrames: 60 } })).limits).toBe('block');
  });

  it('能力不可用或不支持所选格式时阻断；没有音频只是提示', () => {
    expect(levels(input({ capabilities: { ...caps, enabled: false } })).capabilities).toBe('block');
    expect(levels(input({ capabilities: { ...caps, formats: ['opaqueMp4'] }, format: 'transparentProRes4444' })).capabilities).toBe('block');
    expect(levels(input({ hasAudio: false })).audio).toBe('info');
  });

  it('音轨短时补静音、长时截断，无音频时只导画面且不阻断', () => {
    const audio = readinessChecks(input()).find((check) => check.id === 'audio')!;
    expect(audio.level).toBe('ok');
    expect(audio.text).toContain('补静音');
    expect(audio.text).toContain('截断');
    const silent = readinessChecks(input({ hasAudio: false, includeAudio: true }));
    expect(silent.find((check) => check.id === 'audio')?.text).toContain('只导出画面');
    expect(blocked(silent)).toBe(false);
  });

  it('音频资源损坏仍阻断含音轨导出；选择纯画面时不阻断', () => {
    const audioResource = { resourceId: 'broken', kind: 'media' as const, roles: ['audio'], status: 'failed' as const };
    expect(levels(input({ audioResource })).audio).toBe('block');
    expect(levels(input({ audioResource, includeAudio: false })).audio).toBe('info');
  });

  it('歌词时间问题阻断，修复入口指向第一处', () => {
    const timing = timingProblems([primary], { p: [line('l2', [token('d', '二')], [0, 100])] });
    const check = readinessChecks(input({ timing })).find((c) => c.id === 'timing')!;
    expect(check).toMatchObject({ level: 'block', fix: { kind: 'lyrics', branchId: 'p', lineId: 'l2', tokenId: 'd' } });
    expect(check.text).toContain('1 个字没有时间');
  });
});

describe('导出：任务文字', () => {
  it('进度写阶段和帧数；请求取消后写“正在取消”', () => {
    expect(progressText({ state: 'running', cancellationRequested: false, progress: { stage: 'rendering', completedFrames: 30, totalFrames: 120 } })).toBe('渲染 · 30/120 帧');
    expect(progressText({ state: 'running', cancellationRequested: true })).toBe('正在取消……');
    expect(progressFraction({ stage: 'rendering', completedFrames: 30, totalFrames: 120 })).toBe(0.25);
    expect(progressFraction(undefined)).toBeUndefined();
  });

  it('失败原因查表，未知代码用后端的说明；文件名用工程名', () => {
    expect(failureText('exportInputRejected', 'x')).toContain('G29');
    expect(failureText('somethingElse', '后端说明')).toBe('后端说明');
    expect(downloadName(null, 'a/b', 'video/quicktime')).toBe('a-b.mov');
    expect(downloadName('MakoKara-1.mov', '歌名', 'video/mp4')).toBe('歌名.mov');
    expect(downloadName('MakoKara-1.mp4', '  ', 'video/mp4')).toBe('MakoKara-1.mp4');
  });

  it('音频损坏与格式不支持显示实际原因，不误称没有音频', () => {
    expect(failureText('WorkerExportAudioDecodeFailed', '独立导出执行器失败')).toContain('无法解码');
    expect(failureText('WorkerExportAudioUnsupported', '独立导出执行器失败')).toContain('不受支持');
    expect(failureText('exportAudioUnsupported', '后端说明')).not.toContain('工程没有音频');
    expect(failureText('WorkerExportAudioMismatch', '独立导出执行器失败')).toContain('音频');
  });
});
