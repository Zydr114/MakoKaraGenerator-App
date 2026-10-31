// 编排属性的编辑（W4 设计 §13.2～§13.5）：显示值与提交值的换算、属性命令的组装（完整替换，只换改动的项）、
// 跨字段检查、槽位动画的选项、布局类型切换时可复用的子节点、来源动画策略的预检查、属性命令的失败原因。无 DOM。

import {
  PLACEMENT_ANCHORS,
  type OrchestrationNode,
  type OrchestrationPlacement,
  type OrchestrationProgram,
  type OrchestrationPropertiesCommand,
  type OrchestrationReferenceRect,
  type OrchestrationSafeArea,
  type PlacementAnchor,
  type ProgramAnimationPolicy,
  type SceneTransform,
  type SlotAnimation,
  type SlotConflictPolicy,
  type SlotEasing,
  type SlotRetentionPolicy,
  type SlotRevealMode,
} from '../../protocol';
import { anchorText, isPlainGroup, type OrchestrationModel } from './orchestration';

/** 数值四舍五入到 digits 位小数（去掉 0.30000000000000004 这类尾巴）。 */
export const roundTo = (value: number, digits: number): number => Number(value.toFixed(digits));
/** 比例显示成百分数（最多两位小数）：0.3 → 30。 */
export const toPercent = (ratio: number): number => roundTo(ratio * 100, 2);
/** 微秒字符串显示成秒（最多三位小数）。 */
export const toSeconds = (us: string): number => roundTo(Number(us) / 1_000_000, 3);
/** 秒换成微秒字符串（取整到微秒）。 */
export const fromSeconds = (seconds: number): string => String(Math.round(seconds * 1_000_000));

export interface Option<T extends string> {
  value: T;
  label: string;
  disabled?: boolean;
}

/** 锚点的选项；当前值不是六个之一时列在最后，不能选。 */
export function anchorOptions(current: string): Option<string>[] {
  const options: Option<string>[] = PLACEMENT_ANCHORS.map((a) => ({ value: a, label: anchorText(a) }));
  if (!PLACEMENT_ANCHORS.includes(current as PlacementAnchor)) options.push({ value: current, label: current, disabled: true });
  return options;
}

// 写入只认这几个字段（additionalProperties: false），读模型以后多了字段也不带回去。
const placementOf = (p: OrchestrationPlacement): OrchestrationPlacement => ({ x: p.x, y: p.y, anchor: p.anchor, rotationDegrees: p.rotationDegrees, scale: p.scale });
const rectOf = (r: OrchestrationReferenceRect): OrchestrationReferenceRect => ({ widthRatio: r.widthRatio, heightRatio: r.heightRatio });
const safeAreaOf = (a: OrchestrationSafeArea): OrchestrationSafeArea => ({ left: a.left, top: a.top, right: a.right, bottom: a.bottom });
const transformOf = (t: SceneTransform): SceneTransform => ({ opacity: t.opacity, scale: t.scale, offsetXPx: t.offsetXPx, offsetYPx: t.offsetYPx, rotationDegrees: t.rotationDegrees });

/** 外层位置与参考框（§13.2）：带完整的两者，只换改动的项。 */
export function geometryCommand(
  node: OrchestrationNode,
  patch: { placement?: Partial<OrchestrationPlacement>; referenceRect?: Partial<OrchestrationReferenceRect> },
): OrchestrationPropertiesCommand {
  return {
    type: 'orchestration.setNodeGeometry',
    nodeId: node.id,
    placement: { ...placementOf(node.placement), ...patch.placement },
    referenceRect: { ...rectOf(node.referenceRect), ...patch.referenceRect },
  };
}

/** 内部布局的默认位置（§13.3）：设置时用节点当前的外层位置作为初值；patch 改其中几项；null 清除。 */
export function defaultPlacementCommand(node: OrchestrationNode, placement: Partial<OrchestrationPlacement> | null): OrchestrationPropertiesCommand {
  const base = node.layout?.defaultPlacement ?? node.placement;
  return { type: 'orchestration.setLayoutDefaultPlacement', containerId: node.id, placement: placement === null ? null : { ...placementOf(base), ...placement } };
}

/** 安全区四边（比例）的检查：都在 0～1，右大于左、下大于上。不合法时返回原因。 */
export function safeAreaBlock(a: OrchestrationSafeArea): string | undefined {
  const sides: [string, number][] = [
    ['左', a.left],
    ['上', a.top],
    ['右', a.right],
    ['下', a.bottom],
  ];
  for (const [label, value] of sides) if (!(value >= 0 && value <= 1)) return `${label}边要在 0～100% 之间。`;
  if (a.right <= a.left) return '右边要大于左边。';
  if (a.bottom <= a.top) return '下边要大于上边。';
  return undefined;
}

/** 安全区与最大宽度（§13.3）：一条 setLayoutSettings，两者都带上。 */
export function layoutSettingsCommand(node: OrchestrationNode, patch: { safeArea?: Partial<OrchestrationSafeArea>; maxWidthRatio?: number }): OrchestrationPropertiesCommand | undefined {
  const layout = node.layout;
  if (!layout) return undefined;
  return {
    type: 'orchestration.setLayoutSettings',
    containerId: node.id,
    safeArea: { ...safeAreaOf(layout.safeArea), ...patch.safeArea },
    maxWidthRatio: patch.maxWidthRatio ?? layout.maxWidthRatio,
  };
}

/** 改成轮换时可以用作槽位的现有子节点：直属的普通子节点（按读模型顺序），例如从轮换改回自动布局时留下的槽位。 */
export function reusableSlotChildren(model: OrchestrationModel, containerId: string): OrchestrationNode[] {
  return (model.children.get(containerId) ?? []).filter((n) => isPlainGroup(model, n));
}

/** 改成轮换（count 个槽位，1～64）：前面用现有的子节点，不够的由后端新建。 */
export function slotSequenceCommand(model: OrchestrationModel, containerId: string, count: number): OrchestrationPropertiesCommand {
  const reused = reusableSlotChildren(model, containerId)
    .slice(0, count)
    .map((n) => n.id);
  return { type: 'orchestration.setLayoutKind', containerId, kind: 'SlotSequence', slotIds: reused, newSlotCount: count - reused.length };
}

/** 改成自动布局：槽位节点留作普通子节点。 */
export function autoLayoutCommand(containerId: string): OrchestrationPropertiesCommand {
  return { type: 'orchestration.setLayoutKind', containerId, kind: 'Auto', slotIds: [], newSlotCount: 0 };
}

export const MAX_SLOTS = 64;

// ── 槽位动画（§13.4） ──

export const REVEAL_MODE_OPTIONS: readonly Option<SlotRevealMode>[] = [
  { value: 'Progress', label: '按进度' },
  { value: 'TimeAfterPreviousStart', label: '前一句开始后' },
  { value: 'LeadBeforeNextStart', label: '下一句开始前' },
];
export const CONFLICT_POLICY_OPTIONS: readonly Option<SlotConflictPolicy>[] = [
  { value: 'QueueUntilSlotFree', label: '排队等空位' },
  { value: 'AllowOverflow', label: '允许溢出' },
  { value: 'ReplacePrevious', label: '替换最早的' },
  { value: 'Skip', label: '跳过' },
];
export const RETENTION_POLICY_OPTIONS: readonly Option<SlotRetentionPolicy>[] = [
  { value: 'UntilLineEnd', label: '句子结束' },
  { value: 'UntilNextReveal', label: '下一句出现' },
  { value: 'Immediate', label: '立即消失' },
];
export const EASING_OPTIONS: readonly Option<SlotEasing>[] = [
  { value: 'Linear', label: '线性' },
  { value: 'EaseIn', label: '渐入' },
  { value: 'EaseOut', label: '渐出' },
  { value: 'EaseInOut', label: '渐入渐出' },
];

/** 选项里加上不认识的当前值（不能选），免得下拉框显示空白。 */
export function withCurrent<T extends string>(options: readonly Option<T>[], current: string): Option<string>[] {
  return options.some((o) => o.value === current) ? [...options] : [...options, { value: current, label: current, disabled: true }];
}

export type PoseKey = 'enterFrom' | 'hold' | 'exitTo';
export const POSE_TEXT: Record<PoseKey, string> = { enterFrom: '进入起点', hold: '停留', exitTo: '退出终点' };

/** 槽位动画（完整十四个字段）；patch 改其中几项，姿态按项合并。 */
export function slotAnimationCommand(
  containerId: string,
  animation: SlotAnimation,
  patch: Partial<Omit<SlotAnimation, PoseKey>> & { [K in PoseKey]?: Partial<SceneTransform> },
): OrchestrationPropertiesCommand {
  const { enterFrom, hold, exitTo, ...rest } = patch;
  return {
    type: 'orchestration.setSlotAnimation',
    containerId,
    slotAnimation: {
      revealMode: animation.revealMode,
      revealProgress: animation.revealProgress,
      revealDelayUs: animation.revealDelayUs,
      revealLeadUs: animation.revealLeadUs,
      maxVisibleLines: animation.maxVisibleLines,
      conflictPolicy: animation.conflictPolicy,
      retentionPolicy: animation.retentionPolicy,
      enterDurationUs: animation.enterDurationUs,
      exitDurationUs: animation.exitDurationUs,
      enterFrom: { ...transformOf(animation.enterFrom), ...enterFrom },
      hold: { ...transformOf(animation.hold), ...hold },
      exitTo: { ...transformOf(animation.exitTo), ...exitTo },
      exitEasing: animation.exitEasing,
      easing: animation.easing,
      ...rest,
    },
  };
}

// ── 来源的动画策略（§13.5）与逐字动画的限制（后端画面结构校验） ──

/** 下拉框的值：role 表示 null（按来源角色）。 */
export type PolicyChoice = ProgramAnimationPolicy | 'role';

/** 实际生效的动画：显式值；null 按来源角色，主歌词是逐字（卡拉 OK），其他来源是静态。 */
export function effectivePolicy(policy: string | null | undefined, primary: boolean): string {
  return policy ?? (primary ? 'Karaoke' : 'Static');
}

/** 来源只能用逐字动画的原因：有片段或前奏提示，或默认容器是轮换。没有限制时返回 undefined。 */
export function karaokeOnlyReason(model: OrchestrationModel, program: OrchestrationProgram): string | undefined {
  if ((program.clips?.length ?? 0) > 0 || (program.leadIns?.length ?? 0) > 0) return '这个来源有片段或前奏提示，只能用逐字（卡拉 OK）动画。';
  if (model.byId.get(program.defaultContainerId)?.layout?.kind === 'SlotSequence') return '这个来源的默认容器是轮换，只能用逐字（卡拉 OK）动画。';
  return undefined;
}

/** 动画策略的选项：只能用逐字时，实际不是逐字的选项（静态、跟随主歌词，非主歌词来源的“按来源角色”）不能选。 */
export function animationPolicyOptions(model: OrchestrationModel, program: OrchestrationProgram, primary: boolean): Option<PolicyChoice>[] {
  const restricted = !!karaokeOnlyReason(model, program);
  const blocked = (choice: PolicyChoice) => restricted && effectivePolicy(choice === 'role' ? null : choice, primary) !== 'Karaoke';
  const options: Option<PolicyChoice>[] = [
    { value: 'role', label: '按来源角色', disabled: blocked('role') },
    { value: 'Karaoke', label: '逐字（卡拉 OK）' },
    { value: 'Static', label: '静态文本', disabled: blocked('Static') },
    { value: 'FollowPrimary', label: '跟随主歌词', disabled: blocked('FollowPrimary') },
  ];
  const current = program.animationPolicy;
  if (current && !options.some((o) => o.value === current)) options.push({ value: current as PolicyChoice, label: current, disabled: true });
  return options;
}

/** 选了某个策略能不能提交；不能时返回原因（只是提前提示，结果以后端为准）。 */
export function animationPolicyBlock(model: OrchestrationModel, program: OrchestrationProgram, primary: boolean, choice: PolicyChoice): string | undefined {
  return animationPolicyOptions(model, program, primary).find((o) => o.value === choice)?.disabled ? karaokeOnlyReason(model, program) : undefined;
}

export function animationPolicyCommand(branchId: string, choice: PolicyChoice): OrchestrationPropertiesCommand {
  return { type: 'orchestration.setProgramAnimationPolicy', branchId, animationPolicy: choice === 'role' ? null : choice };
}

/** 轮换容器作默认容器，要求来源实际的动画是逐字。 */
export const SLOT_DEFAULT_NEEDS_KARAOKE = '来源的动画不是逐字（卡拉 OK），轮换容器不能作它的默认容器；先在“来源”里把动画改成逐字。';

/** 把容器改成轮换不行的原因：它是某个来源的默认容器，而那个来源实际的动画不是逐字。 */
export function slotSequenceBlock(model: OrchestrationModel, containerId: string, isPrimary: (branchId: string) => boolean): string | undefined {
  const blocked = model.programs.some((p) => p.defaultContainerId === containerId && effectivePolicy(p.animationPolicy, isPrimary(p.branchId)) !== 'Karaoke');
  return blocked ? '它是来源的默认容器，而来源的动画不是逐字（卡拉 OK），不能改成轮换；先在“来源”里把动画改成逐字。' : undefined;
}

/** 某个容器能不能作来源的默认容器：轮换容器要求来源实际的动画是逐字。 */
export function defaultContainerBlock(program: OrchestrationProgram, container: OrchestrationNode, primary: boolean): string | undefined {
  return container.layout?.kind === 'SlotSequence' && effectivePolicy(program.animationPolicy, primary) !== 'Karaoke' ? SLOT_DEFAULT_NEEDS_KARAOKE : undefined;
}

/** 属性命令失败的原因（W4 设计 §13）。 */
const PROPERTY_FAILURE_TEXT: Record<string, string> = {
  TargetStale: '这个节点已经不在了，或者不是歌词容器，请重新选择。',
  ContainerInvalid: '只有歌词容器能切换布局类型。',
  PresentationInvalid: '结果没有通过画面结构校验，没有修改。',
};

export function propertyFailureText(failureCode: string): string | undefined {
  return PROPERTY_FAILURE_TEXT[failureCode];
}
