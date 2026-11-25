// 前奏提示与句子属性的模型（W4 设计 §14）：前奏列表、提示组、命令组装、自动生成的初值与候选，本句微调与进入动画。
import { describe, expect, it } from 'vitest';
import { buildOrchestration } from '../../src/editor/model/orchestration';
import {
  appliedText,
  candidateReasonText,
  candidateSelectable,
  candidateSummary,
  countdownAnimationOf,
  countdownShapeOf,
  countdownsOf,
  DEFAULT_COUNTDOWN_ANIMATION,
  DEFAULT_COUNTDOWN_SHAPE,
  DEFAULT_CUSTOM_SVG,
  defaultLeadInTarget,
  defaultSelection,
  generationSettingsBlock,
  initialGenerationSettings,
  inlineSvgBlock,
  leadInEditBlock,
  leadInFailureText,
  leadInsOf,
  leadInSpan,
  leadInsUsing,
  lineContainerText,
  lineDragTransform,
  lineLabel,
  lineTransformCommand,
  MODERN_COUNTDOWN_ANIMATION,
  MODERN_COUNTDOWN_SHAPE,
  MAX_COUNTDOWN_SVG_BYTES,
  markTextBlock,
  newLeadInBlock,
  preferredAttachmentLeadIn,
  updateLeadInCommand,
} from '../../src/editor/model/orchestration-lead-ins';
import type { GeometryReference, LeadInGenerationCandidate, OrchestrationLeadIn, OrchestrationNode, OrchestrationProgram, ProjectOrchestrationResponse } from '../../src/protocol';
import { revision } from '../../src/protocol';

const node = (id: string, extra: Partial<OrchestrationNode> = {}): OrchestrationNode => ({
  id,
  placement: { x: 0.5, y: 0.5, anchor: 'Center', rotationDegrees: 0, scale: 1 },
  referenceRect: { widthRatio: 1, heightRatio: 1 },
  ...extra,
});
const layout = { kind: 'Auto', safeArea: { left: 0, top: 0, right: 1, bottom: 1 }, maxWidthRatio: 1, slotIds: [], slotAnimation: null };
const leadIn = (id: string, targetLineId: string, extra: Partial<OrchestrationLeadIn> = {}): OrchestrationLeadIn => ({
  id,
  enabled: true,
  targetLineId,
  indicatorContainerId: 'node/cd',
  durationUs: '2000000',
  origin: 'Manual',
  ...extra,
});

function scene(program: Partial<OrchestrationProgram> = {}): ProjectOrchestrationResponse {
  return {
    projectSessionId: 'p',
    projectRevision: revision('7'),
    nodes: [
      node('root'),
      node('root/regular', { parentId: 'root', layout }),
      node('node/center', { parentId: 'root', name: '居中', layout }),
      node('node/cd', { parentId: 'root', name: '三点', contentKind: 'countdownSequence', content: { markNodeIds: ['node/m1'], attachmentMode: 'CueTargetText' } }),
      node('node/m1', { parentId: 'node/cd', contentKind: 'textMark', content: { text: '●' } }),
      node('node/cd2', { parentId: 'root', contentKind: 'countdownSequence', content: { markNodeIds: [] } }),
      node('free/cd', { contentKind: 'countdownSequence', content: { markNodeIds: [] } }),
    ],
    programs: [{ id: 'program/primary', branchId: 'primary', rootContainerId: 'root', defaultContainerId: 'root/regular', clips: [], leadIns: [], leadInGeneration: null, ...program }],
    protocolVersion: '1',
  };
}

const lines: { id: string; text: string; startUs?: number; endUs?: number }[] = [
  { id: 'l1', text: '一行目のとても長い歌詞です、本当に', startUs: 4_000_000, endUs: 5_000_000 },
  { id: 'l2', text: '二行目', startUs: 6_000_000, endUs: 7_000_000 },
  { id: 'l3', text: '' },
  { id: 'l4', text: '四行目', startUs: 20_000_000, endUs: 21_000_000 },
];

describe('前奏（W4 设计 §14.2～§14.4）', () => {
  it('列表按目标句的顺序，不在来源里的排最后；提示组只取来源根下的；用某个提示组的前奏；时间与句子标签', () => {
    const model = buildOrchestration(scene({ leadIns: [leadIn('a', 'gone'), leadIn('b', 'l4'), leadIn('c', 'l1', { startUs: '2000000', endUs: '4000000' })] }));
    const program = model.programs[0]!;
    expect(leadInsOf(program, lines).map((l) => l.id)).toEqual(['c', 'b', 'a']);
    expect(countdownsOf(model, program).map((n) => n.id)).toEqual(['node/cd', 'node/cd2']);
    expect(leadInsUsing(model, 'node/cd').map((x) => x.leadIn.id)).toEqual(['a', 'b', 'c']);
    expect(leadInsUsing(model, 'node/cd2')).toEqual([]);
    expect([leadInSpan(program.leadIns![2]!), leadInSpan(program.leadIns![1]!)]).toEqual([{ startUs: 2_000_000, endUs: 4_000_000 }, undefined]);
    expect([lineLabel(lines, 'l1'), lineLabel(lines, 'l2'), lineLabel(lines, 'l3'), lineLabel(lines, 'x')]).toEqual([
      '第 1 句 · 一行目のとても長い歌詞で…',
      '第 2 句 · 二行目',
      '第 3 句',
      'x（不在来源里）',
    ]);
  });

  it('来源不支持时写明原因：跟随主歌词的计时，或明确设成非逐字的动画', () => {
    const program = buildOrchestration(scene()).programs[0]!;
    expect(leadInEditBlock({ timingMode: 'independent' }, program)).toBeUndefined();
    expect(leadInEditBlock({ timingMode: 'followPrimary' }, program)).toContain('前奏提示只支持');
    expect(leadInEditBlock({ timingMode: 'independent' }, { ...program, animationPolicy: 'Static' })).toContain('前奏提示只支持');
  });

  it('修改前奏带完整字段，只换改动的项；名称空白等于不命名', () => {
    const current = leadIn('a', 'l1', { name: '开头' });
    expect(updateLeadInCommand('primary', current, { durationUs: '1500000' })).toEqual({
      type: 'orchestration.updateLeadIn',
      branchId: 'primary',
      leadInId: 'a',
      name: '开头',
      targetLineId: 'l1',
      indicatorContainerId: 'node/cd',
      durationUs: '1500000',
      enabled: true,
    });
    expect(updateLeadInCommand('primary', current, { name: '  ' })).toMatchObject({ name: null });
    expect(updateLeadInCommand('primary', leadIn('a', 'l1'), { enabled: false })).toMatchObject({ name: null, enabled: false });
  });

  it('添加前奏：预填选中的一句，已有前奏或没选时取第一个有时间、没有前奏的句子；提前检查', () => {
    const program = buildOrchestration(scene({ leadIns: [leadIn('a', 'l1')] })).programs[0]!;
    expect(defaultLeadInTarget(program, lines, ['l4'])).toBe('l4');
    expect(defaultLeadInTarget(program, lines, ['l1'])).toBe('l2');
    expect(defaultLeadInTarget(program, lines, ['l2', 'l4'])).toBe('l2');
    expect(defaultLeadInTarget(program, lines.slice(0, 1), undefined)).toBeUndefined();
    expect([
      newLeadInBlock(program, undefined, 'node/cd', 2),
      newLeadInBlock(program, 'l1', 'node/cd', 2),
      newLeadInBlock(program, 'l2', undefined, 2),
      newLeadInBlock(program, 'l2', 'node/cd', 0),
      newLeadInBlock(program, 'l2', 'node/cd', 2),
    ]).toEqual(['请选择目标句。', '这一句已经有前奏。', '请选择提示组，或新建一个。', '时长要大于 0 秒。', undefined]);
    expect([markTextBlock(' '), markTextBlock('３')]).toEqual(['标记不能是空白。', undefined]);
  });

  it('preferredAttachmentLeadIn 优先挑选启用且时间有效的前奏，并补全附着测量失败文案', () => {
    const untimed = { leadIn: leadIn('a', 'l3', { enabled: true }) };
    const disabledTimed = { leadIn: leadIn('b', 'l1', { enabled: false, startUs: '2000000', endUs: '4000000' }) };
    const enabledTimed = { leadIn: leadIn('c', 'l2', { enabled: true, startUs: '4000000', endUs: '6000000' }) };
    expect(preferredAttachmentLeadIn([])).toBeUndefined();
    expect(preferredAttachmentLeadIn([untimed, disabledTimed, enabledTimed])).toBe(enabledTimed);
    expect(preferredAttachmentLeadIn([untimed, disabledTimed])).toBe(disabledTimed);
    expect(preferredAttachmentLeadIn([untimed])).toBe(untimed);
    expect(leadInFailureText('PackageBackendUnsupported')).toContain('GPU');
    expect(leadInFailureText('PreparedTimeMismatch')).toBeTruthy();
    expect(leadInFailureText('MediaFeatureOutputMismatch')).toBeTruthy();
    expect(leadInFailureText('SceneMeasurementWorkerFailed')).toBeTruthy();
    expect(leadInFailureText('RenderBackendUnavailable')).toBeTruthy();
  });
});

describe('自动生成（§14.5）', () => {
  const candidate = (targetLineId: string, status: string, reason = 'None'): LeadInGenerationCandidate => ({ targetLineId, status, reason });

  it('初值：来源已有设置时用它，否则用后端默认值、第一个提示组；没有提示组时不能预演', () => {
    const fresh = buildOrchestration(scene());
    expect(initialGenerationSettings(fresh, fresh.programs[0]!)).toEqual({
      includeOpening: true,
      includeLongGaps: true,
      gapThresholdUs: '8000000',
      durationUs: '3000000',
      indicatorContainerId: 'node/cd',
      excludedLineIds: [],
    });
    const savedSettings = { includeOpening: false, includeLongGaps: true, gapThresholdUs: '3000000', durationUs: '2000000', indicatorContainerId: 'node/cd2', excludedLineIds: ['l2'] };
    const saved = buildOrchestration(scene({ leadInGeneration: savedSettings }));
    expect(initialGenerationSettings(saved, saved.programs[0]!)).toEqual(savedSettings);
    const staleSaved = buildOrchestration(scene({ leadInGeneration: { ...savedSettings, indicatorContainerId: 'node/deleted' } }));
    expect(initialGenerationSettings(staleSaved, staleSaved.programs[0]!)).toEqual({ ...savedSettings, indicatorContainerId: 'node/cd' });
    const clearedSaved = buildOrchestration(scene({ leadInGeneration: { ...savedSettings, indicatorContainerId: null } }));
    expect(initialGenerationSettings(clearedSaved, clearedSaved.programs[0]!)).toEqual({ ...savedSettings, indicatorContainerId: 'node/cd' });
    const baseNoCd = scene({ leadInGeneration: { ...savedSettings, indicatorContainerId: 'node/deleted' } });
    const noCd = buildOrchestration({ ...baseNoCd, nodes: baseNoCd.nodes.filter((n) => n.contentKind !== 'countdownSequence') });
    expect(initialGenerationSettings(noCd, noCd.programs[0]!)).toEqual({ ...savedSettings, indicatorContainerId: null });
    expect([generationSettingsBlock(savedSettings), generationSettingsBlock({ ...savedSettings, indicatorContainerId: null })]).toEqual([undefined, '请选择提示组，或新建一个。']);
  });

  it('候选：新增、更新、移除能勾，默认勾新增和更新；汇总只写有的；应用后的提示只数勾选的', () => {
    const candidates = [candidate('l1', 'New'), candidate('l2', 'Update'), candidate('l3', 'Remove'), candidate('l4', 'Skipped', 'TooShort'), candidate('l5', 'Preserved', 'ExistingManual')];
    expect(candidates.map(candidateSelectable)).toEqual([true, true, true, false, false]);
    expect(defaultSelection(candidates)).toEqual(['l1', 'l2']);
    expect(candidateSummary(candidates)).toBe('新增 1 · 更新 1 · 移除 1 · 保留 1 · 跳过 1');
    expect(candidateSummary([candidate('l1', 'Skipped', 'NotEligible'), candidate('l2', 'Mystery')])).toBe('跳过 1 · 其他 1');
    expect(candidateSummary([])).toBe('没有候选');
    expect(appliedText(candidates, ['l1', 'l3', 'l4'])).toBe('已应用自动生成：新增 1、移除 1');
    expect([candidateReasonText('TooShort'), candidateReasonText('Unknown')]).toEqual(['起唱前的时间不够一个提示时长', 'Unknown']);
  });
});

describe('句子：本句微调与进入动画（§14.7、§14.8）', () => {
  it('本句微调带完整四项，原来没有时用初值；拖动按编辑参考换算，保留旋转和缩放', () => {
    expect(lineTransformCommand('primary', 'l1', null, { offsetXRatio: 0.1 })).toEqual({
      type: 'orchestration.setLineLocalTransform',
      branchId: 'primary',
      lineId: 'l1',
      transform: { offsetXRatio: 0.1, offsetYRatio: 0, rotationDegrees: 0, scale: 1 },
    });
    const reference: GeometryReference = {
      width: 1920,
      height: 1080,
      worldMatrix: { a: 1, b: 0, c: 0, d: 1, tx: 0, ty: 0 },
      pivot: { x: 0, y: 0 },
      worldCorners: [],
    };
    expect(lineDragTransform({ offsetXRatio: 0.1, offsetYRatio: 0, rotationDegrees: 10, scale: 1.2 }, reference, 192, -108)).toEqual({
      offsetXRatio: 0.2,
      offsetYRatio: -0.1,
      rotationDegrees: 10,
      scale: 1.2,
    });
    expect(lineDragTransform(null, { ...reference, width: 0 }, 10, 10)).toBeUndefined();
  });

  it('句子显示在哪里：片段的目标容器，否则默认容器', () => {
    const model = buildOrchestration(scene({ clips: [{ id: 'clip/1', name: '副歌', lineIds: ['l2'], containerId: 'node/center' }] }));
    const program = model.programs[0]!;
    expect([lineContainerText(model, program, 'l1'), lineContainerText(model, program, 'l2')]).toEqual(['常规（默认容器）', '居中（片段“副歌”的目标容器）']);
  });

  it('提示组图形与动画读取回落默认值，现代默认值使用 Circle + BlinkThenWipeFill', () => {
    expect(countdownShapeOf(undefined)).toEqual(DEFAULT_COUNTDOWN_SHAPE);
    expect(countdownAnimationOf(undefined)).toEqual(DEFAULT_COUNTDOWN_ANIMATION);
    expect(MODERN_COUNTDOWN_SHAPE.kind).toBe('Circle');
    expect(MODERN_COUNTDOWN_ANIMATION.mode).toBe('BlinkThenWipeFill');
    expect(
      countdownShapeOf({
        shape: { kind: 'SteppedBars', barMinHeightRatio: 0.3, svgXml: null, svgColorMode: 'OriginalColor' },
      }),
    ).toEqual({ kind: 'SteppedBars', barMinHeightRatio: 0.3, svgXml: null, svgColorMode: 'OriginalColor' });
    expect(
      countdownAnimationOf({
        animation: { mode: 'BlinkThenErase', blinkRatio: 0.25, blinkCount: 3, featherRatio: 0.1 },
      }),
    ).toEqual({ mode: 'BlinkThenErase', blinkRatio: 0.25, blinkCount: 3, featherRatio: 0.1 });
  });

  it('inlineSvgBlock 接收合法静态 SVG 并拦截空串、超限、危险声明与外链', () => {
    expect(inlineSvgBlock(DEFAULT_CUSTOM_SVG)).toBeUndefined();
    expect(inlineSvgBlock('   ')).toBeTruthy();
    expect(inlineSvgBlock(`<svg xmlns="http://www.w3.org/2000/svg"><path d="${'M0 0'.repeat(MAX_COUNTDOWN_SVG_BYTES)}"/></svg>`)).toContain('64 KiB');
    expect(inlineSvgBlock('<!DOCTYPE svg><svg></svg>')).toBeTruthy();
    expect(inlineSvgBlock('<svg><script>alert(1)</script></svg>')).toBeTruthy();
    expect(inlineSvgBlock('<svg><image href="#x"/></svg>')).toBeTruthy();
    expect(inlineSvgBlock('<svg><use href="https://example.com/a.svg#x"/></svg>')).toBeTruthy();
    expect(inlineSvgBlock('<svg><rect fill="url(https://example.com/g)"/></svg>')).toBeTruthy();
    expect(inlineSvgBlock('<div/>')).toBeTruthy();
  });
});
