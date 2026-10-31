// 前奏提示与句子属性的模型（W4 设计 §14）：前奏列表、提示组、命令组装、自动生成的初值与候选，本句微调与进入动画。
import { describe, expect, it } from 'vitest';
import { buildOrchestration } from '../../src/editor/model/orchestration';
import {
  appliedText,
  candidateReasonText,
  candidateSelectable,
  candidateSummary,
  countdownsOf,
  DEFAULT_ENTER_ANIMATION,
  defaultLeadInTarget,
  defaultSelection,
  enterAnimationText,
  generationSettingsBlock,
  initialGenerationSettings,
  isNeutralAnimation,
  leadInEditBlock,
  leadInsOf,
  leadInSpan,
  leadInsUsing,
  lineContainerText,
  lineDragTransform,
  lineEnterCommand,
  lineLabel,
  lineTransformCommand,
  markTextBlock,
  newLeadInBlock,
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

  it('进入动画：只带五个字段，null 清除；看不出效果的判断；描述', () => {
    expect(lineEnterCommand('primary', 'l1', { ...DEFAULT_ENTER_ANIMATION, extra: 1 } as never)).toEqual({
      type: 'orchestration.setLineEnterAnimation',
      branchId: 'primary',
      lineId: 'l1',
      animation: DEFAULT_ENTER_ANIMATION,
    });
    expect(lineEnterCommand('primary', 'l1', null)).toMatchObject({ animation: null });
    expect([isNeutralAnimation(DEFAULT_ENTER_ANIMATION), isNeutralAnimation({ ...DEFAULT_ENTER_ANIMATION, startOpacity: 1, startOffsetPx: 0 })]).toEqual([false, true]);
    expect([enterAnimationText(DEFAULT_ENTER_ANIMATION), enterAnimationText(null)]).toEqual(['0.4 秒 · 渐出', '没有进入动画']);
  });

  it('句子显示在哪里：片段的目标容器，否则默认容器', () => {
    const model = buildOrchestration(scene({ clips: [{ id: 'clip/1', name: '副歌', lineIds: ['l2'], containerId: 'node/center' }] }));
    const program = model.programs[0]!;
    expect([lineContainerText(model, program, 'l1'), lineContainerText(model, program, 'l2')]).toEqual(['常规（默认容器）', '居中（片段“副歌”的目标容器）']);
  });
});
