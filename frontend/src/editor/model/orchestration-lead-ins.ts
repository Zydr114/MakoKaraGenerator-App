// 前奏提示（W4 设计 §14.2～§14.6）与句子属性（§14.7、§14.8）：前奏列表、提示组、命令的组装与失败原因，
// 自动生成的设置、候选的显示与默认勾选；本句微调与进入动画的命令、句子拖动的换算。无 DOM。

import type {
  GeometryReference,
  LeadInCandidateReason,
  LeadInCandidateStatus,
  LeadInFields,
  LeadInGenerationCandidate,
  LeadInGenerationSettings,
  LineLocalTransform,
  LyricBranchTimingMode,
  OrchestrationLeadIn,
  OrchestrationLeadInCommand,
  OrchestrationNode,
  OrchestrationProgram,
  ProjectCommand,
} from '../../protocol';
import type {
  CountdownAnimationMode,
  CountdownAnimationSpec,
  CountdownShapeKind,
  CountdownShapeSpec,
  CountdownSvgColorMode,
} from '../../protocol';
import { clipByLine, nodeName, quickLayoutBlock, subtreeIds, type OrchestrationModel } from './orchestration';
import { fromSeconds, roundTo, toSeconds, type Option } from './orchestration-properties';
import { ratioDelta } from './scene-geometry';

interface LineLike {
  id: string;
  text?: string;
  startUs?: number;
  endUs?: number;
}

// ── 前奏（§14.2～§14.4） ──

export const LEAD_IN_UNSUPPORTED = '前奏提示只支持独立计时、逐字（卡拉 OK）动画的歌词来源；翻译这类跟随主歌词的来源不能加前奏。';

/** 来源能不能加前奏：与快速布局、片段的判断相同（后端要求独立计时、逐字动画）。不能时返回原因。 */
export function leadInEditBlock(branch: { timingMode: LyricBranchTimingMode } | undefined, program: OrchestrationProgram): string | undefined {
  return quickLayoutBlock(branch, program) ? LEAD_IN_UNSUPPORTED : undefined;
}

/** 来源的前奏，按目标句的来源顺序；目标句不在来源里的排在最后（保持读模型顺序）。 */
export function leadInsOf(program: OrchestrationProgram | undefined, lines: readonly LineLike[]): OrchestrationLeadIn[] {
  const order = new Map(lines.map((l, i) => [l.id, i]));
  const index = (l: OrchestrationLeadIn) => order.get(l.targetLineId) ?? Infinity;
  return [...(program?.leadIns ?? [])].sort((a, b) => index(a) - index(b));
}

/** 目标句是 lineId 的前奏。 */
export function leadInOfLine(program: OrchestrationProgram | undefined, lineId: string): OrchestrationLeadIn | undefined {
  return program?.leadIns?.find((l) => l.targetLineId === lineId);
}

/** 来源根下的提示组（倒计时容器），按结构树顺序。 */
export function countdownsOf(model: OrchestrationModel, program: OrchestrationProgram): OrchestrationNode[] {
  return subtreeIds(model, program.rootContainerId)
    .map((id) => model.byId.get(id)!)
    .filter((n) => n.contentKind === 'countdownSequence');
}

/** 用这个提示组的前奏（各个来源里的），按来源与读模型顺序。 */
export function leadInsUsing(model: OrchestrationModel, containerId: string): { program: OrchestrationProgram; leadIn: OrchestrationLeadIn }[] {
  return model.programs.flatMap((program) => (program.leadIns ?? []).filter((l) => l.indicatorContainerId === containerId).map((leadIn) => ({ program, leadIn })));
}

/** 为提示组切换附着方式挑选最可靠的参考前奏：优先启用且有有效区间，其次有有效区间，最后退回首条。 */
export function preferredAttachmentLeadIn<T extends { leadIn: OrchestrationLeadIn }>(users: readonly T[]): T | undefined {
  return users.find((u) => u.leadIn.enabled && leadInSpan(u.leadIn)) ?? users.find((u) => leadInSpan(u.leadIn)) ?? users[0];
}

/** 前奏的时间（微秒）；后端算不出时（目标句没有时间、起唱前不够一个时长）是 undefined。 */
export function leadInSpan(leadIn: OrchestrationLeadIn): { startUs: number; endUs: number } | undefined {
  if (leadIn.startUs === undefined || leadIn.endUs === undefined) return undefined;
  const startUs = Number(leadIn.startUs);
  const endUs = Number(leadIn.endUs);
  return Number.isFinite(startUs) && Number.isFinite(endUs) && startUs < endUs ? { startUs, endUs } : undefined;
}

/** 微秒（字符串或数）显示成“N 秒”。 */
export function secondsText(us: string | number): string {
  return `${toSeconds(String(us))} 秒`;
}

/** “第 N 句”；不在来源里时写 ID。 */
export function lineNumberText(lines: readonly LineLike[], lineId: string): string {
  const i = lines.findIndex((l) => l.id === lineId);
  return i < 0 ? `${lineId}（不在来源里）` : `第 ${i + 1} 句`;
}

/** “第 N 句 · 文字开头”（文字最多 max 个字，多的用省略号）。 */
export function lineLabel(lines: readonly LineLike[], lineId: string, max = 12): string {
  const line = lines.find((l) => l.id === lineId);
  const text = line?.text?.trim() ?? '';
  const short = [...text].length > max ? `${[...text].slice(0, max).join('')}…` : text;
  return short ? `${lineNumberText(lines, lineId)} · ${short}` : lineNumberText(lines, lineId);
}

export const ORIGIN_TEXT: Record<string, string> = { Manual: '手工', Generated: '自动' };
export const originText = (origin: string): string => ORIGIN_TEXT[origin] ?? origin;

/** 前奏与附着命令失败的原因（§14.3）。 */
const LEAD_IN_FAILURE_TEXT: Record<string, string> = {
  SourceMissing: '歌词来源已失效。',
  UnsupportedSource: LEAD_IN_UNSUPPORTED,
  IdentityConflict: '前奏编号重复，请重试。',
  TargetStale: '这个前奏或目标句已经不在了，请重新选择。',
  ContainerInvalid: '提示组必须是这个来源根下的前奏提示组。',
  LeadInTimeConflict: '前奏放不下：起唱前的时间不够这么长，或和别的句子、前奏的时间重叠。缩短时长，或换一句。',
  SceneAttachmentReferenceUnavailable: '目标句的文字测不到（例如没有时间或在画面外），不能切换附着方式。',
  SceneMeasurementUnavailable: '后端没有启用画面测量组件，不能切换附着方式。',
  SceneMeasurementWorkerFailed: '画面测量没有完成，不能切换附着方式。',
  SceneMeasurementDeadlineExceeded: '画面测量超时，请稍后再试。',
  SceneMeasurementBudgetExceeded: '当前画面元素过多，超出测量预算，不能切换附着方式。',
  PackageBackendUnsupported: '当前工程启用了需要 GPU 的效果包，无法在 CPU 后端测量附着位置。',
  RenderBackendUnavailable: '当前渲染设备不可用，不能测量附着位置。',
  RenderBackendUnsupported: '当前渲染设备不支持工程中的效果包，不能测量附着位置。',
  PreparedTimeMismatch: '前奏测量时刻与场景帧不一致，请重试。',
  MediaFeatureOutputMismatch: '前奏测量输出模式与背景媒体特征输入不匹配。',
  PresentationInvalid: '结果没有通过画面结构校验，没有修改。',
  PackageSvgBudgetExceeded: 'SVG 内容或复杂度超过后端预算限制。',
  PackageSvgXmlUnsafe: 'SVG 包含不安全的 XML 声明或禁止的标签/属性。',
  PackageSvgImageUnsafe: '静态内联 SVG 不允许引用外部图片资源。',
  PackageSvgSyntaxInvalid: 'SVG XML 语法无效，请检查标签与属性格式。',
};

export function leadInFailureText(failureCode: string): string | undefined {
  return LEAD_IN_FAILURE_TEXT[failureCode];
}

export type LeadInPatch = Partial<Pick<LeadInFields, 'name' | 'targetLineId' | 'indicatorContainerId' | 'durationUs' | 'enabled'>>;

/** 修改前奏（updateLeadIn）：完整字段，只换改动的项；名称空白等于不命名。 */
export function updateLeadInCommand(branchId: string, leadIn: OrchestrationLeadIn, patch: LeadInPatch): OrchestrationLeadInCommand {
  const name = patch.name !== undefined ? patch.name : (leadIn.name ?? null);
  return {
    type: 'orchestration.updateLeadIn',
    branchId,
    leadInId: leadIn.id,
    name: name?.trim() || null,
    targetLineId: patch.targetLineId ?? leadIn.targetLineId,
    indicatorContainerId: patch.indicatorContainerId ?? leadIn.indicatorContainerId,
    durationUs: patch.durationUs ?? leadIn.durationUs,
    enabled: patch.enabled ?? leadIn.enabled,
  };
}

/** 新建前奏和新建提示组的默认值（后端 Core 的默认）。 */
export const DEFAULT_LEAD_IN_SECONDS = 3;
export const DEFAULT_MARK_COUNT = 3;
export const DEFAULT_MARK_TEXT = '●';
export const MAX_MARKS = 64;
export const MAX_COUNTDOWN_SVG_BYTES = 64 * 1024;

/** 后端 Core 的前奏提示图形与动画默认值（保持旧项目向后兼容）。 */
export const DEFAULT_COUNTDOWN_SHAPE: CountdownShapeSpec = {
  kind: 'Text',
  barMinHeightRatio: 0.45,
  svgXml: null,
  svgColorMode: 'SilhouetteStyle',
};

export const DEFAULT_COUNTDOWN_ANIMATION: CountdownAnimationSpec = {
  mode: 'DiscreteSteps',
  blinkRatio: 0.35,
  blinkCount: 2,
  featherRatio: 0.06,
};

/** 前端新建提示组时应用的现代默认图形与经典“先闪烁再填充”动画。 */
export const MODERN_COUNTDOWN_SHAPE: CountdownShapeSpec = {
  kind: 'Circle',
  barMinHeightRatio: 0.45,
  svgXml: null,
  svgColorMode: 'SilhouetteStyle',
};

export const MODERN_COUNTDOWN_ANIMATION: CountdownAnimationSpec = {
  mode: 'BlinkThenWipeFill',
  blinkRatio: 0.35,
  blinkCount: 2,
  featherRatio: 0.06,
};

export const DEFAULT_CUSTOM_SVG =
  '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100"><circle cx="50" cy="50" r="40" fill="currentColor"/></svg>';

export interface CountdownChoiceOption<T extends string> {
  value: T;
  label: string;
  note: string;
}

export const COUNTDOWN_SHAPE_OPTIONS: readonly CountdownChoiceOption<CountdownShapeKind>[] = [
  { value: 'Text', label: '文字标记', note: '渲染每个标记节点自身的文字内容（例如 ●）。' },
  { value: 'Circle', label: '圆点', note: '几何圆形指示器，适配提示组双态填充与描边。' },
  { value: 'SteppedBars', label: '阶梯竖条', note: '按倒计时序列高度递减的圆角竖条。' },
  { value: 'Capsule', label: '胶囊', note: '圆角胶囊形指示器。' },
  { value: 'Diamond', label: '菱形', note: '几何菱形指示器。' },
  { value: 'Star', label: '五角星', note: '五角星形指示器。' },
  { value: 'CustomSvg', label: '自定义 SVG', note: '静态内联矢量 SVG，支持适配配色或保留原色。' },
];

export const COUNTDOWN_SVG_COLOR_OPTIONS: readonly CountdownChoiceOption<CountdownSvgColorMode>[] = [
  { value: 'SilhouetteStyle', label: '使用提示组配色', note: '提取 SVG 轮廓并应用提示组的未到/已过填充、描边与渐变擦除。' },
  { value: 'OriginalColor', label: '保留 SVG 原色', note: '按 SVG 自身颜色渲染，仅随序列进度做透明度与闪烁/擦除过渡。' },
];

export const COUNTDOWN_ANIMATION_OPTIONS: readonly CountdownChoiceOption<CountdownAnimationMode>[] = [
  { value: 'BlinkThenWipeFill', label: '先闪烁再渐进填充', note: '经典卡拉 OK 效果：先按拍点闪烁预备，再沿序列逐步填充。' },
  { value: 'BlinkThenErase', label: '先闪烁再渐进消除', note: '先按拍点闪烁预备，再沿序列逐步消除填充。' },
  { value: 'DiscreteSteps', label: '逐格跳变', note: '按标记时间逐个切换点亮状态，无闪烁和连续擦除。' },
];

/** 读取提示组当前的图形配置（缺失时回落到 Core 默认）。 */
export function countdownShapeOf(content: { shape?: CountdownShapeSpec | null } | null | undefined): CountdownShapeSpec {
  const shape = content?.shape;
  if (!shape) return DEFAULT_COUNTDOWN_SHAPE;
  return {
    kind: shape.kind,
    barMinHeightRatio: shape.barMinHeightRatio,
    svgXml: shape.svgXml ?? null,
    svgColorMode: shape.svgColorMode ?? 'SilhouetteStyle',
  };
}

/** 读取提示组当前的动画配置（缺失时回落到 Core 默认）。 */
export function countdownAnimationOf(content: { animation?: CountdownAnimationSpec | null } | null | undefined): CountdownAnimationSpec {
  const animation = content?.animation;
  if (!animation) return DEFAULT_COUNTDOWN_ANIMATION;
  return {
    mode: animation.mode,
    blinkRatio: animation.blinkRatio,
    blinkCount: animation.blinkCount,
    featherRatio: animation.featherRatio,
  };
}

/**
 * 本地快速校验静态内联 SVG：拦截空串、超 64 KiB、不安全声明/脚本/外链元素以及明显语法错误；
 * 返回问题说明或 undefined。
 */
export function inlineSvgBlock(svgXml: string | null | undefined): string | undefined {
  const trimmed = svgXml?.trim() ?? '';
  if (!trimmed) return '请输入包含 <svg> 根节点的静态矢量 SVG 代码。';
  if (new TextEncoder().encode(trimmed).length > MAX_COUNTDOWN_SVG_BYTES) return 'SVG 内容超过 64 KiB 限制。';
  if (/<!(?:DOCTYPE|ENTITY)\b/i.test(trimmed)) return '静态内联 SVG 不允许包含 DOCTYPE 或实体声明。';
  if (/<(?:script|foreignObject|iframe|object|embed|audio|video|animate|animateTransform|animateMotion|animateColor|set)\b/i.test(trimmed)) {
    return '静态内联 SVG 不允许包含脚本、外部对象或动画元素。';
  }
  if (/<image\b/i.test(trimmed)) return '前奏内联 SVG 仅支持纯矢量，不允许 <image> 元素。';
  if (/\bon[a-z]+\s*=|\bjavascript\s*:/i.test(trimmed)) return '静态内联 SVG 不允许包含事件处理属性或脚本链接。';
  if (/\b(?:xlink:)?href\s*=\s*['"]\s*(?!#)/i.test(trimmed) || /\burl\(\s*['"]?(?!#)/i.test(trimmed)) {
    return '静态内联 SVG 不允许引用外部 URI，只支持文档内部 #id。';
  }
  if (typeof DOMParser !== 'undefined') {
    const doc = new DOMParser().parseFromString(trimmed, 'image/svg+xml');
    if (doc.getElementsByTagName('parsererror').length > 0) return 'SVG XML 语法无效，请检查标签与引号是否闭合。';
    const root = doc.documentElement;
    if (!root || root.localName.toLowerCase() !== 'svg') return 'SVG 根节点必须是 <svg>。';
  } else {
    const stripped = trimmed.replace(/^\s*(?:<\?xml[\s\S]*?\?>|<!--[\s\S]*?-->|\s)+/i, '');
    if (!/^<svg(?:\s|>)/i.test(stripped) || !/<\/svg>\s*$/i.test(trimmed)) {
      return 'SVG 必须以 <svg> 开头并以 </svg> 结尾。';
    }
  }
  return undefined;
}

/** 添加前奏的预填目标句：选中的那一句（还没有前奏时）；否则第一个有时间、还没有前奏的句子。 */
export function defaultLeadInTarget(program: OrchestrationProgram, lines: readonly LineLike[], selected: readonly string[] | undefined): string | undefined {
  const taken = new Set((program.leadIns ?? []).map((l) => l.targetLineId));
  const pick = selected?.length === 1 ? selected[0]! : undefined;
  if (pick && !taken.has(pick) && lines.some((l) => l.id === pick)) return pick;
  return lines.find((l) => l.startUs !== undefined && !taken.has(l.id))?.id;
}

/** 添加前奏前的检查：目标句、提示组、时长（秒）。只是提前提示，结果以后端为准。 */
export function newLeadInBlock(program: OrchestrationProgram, targetLineId: string | undefined, indicatorContainerId: string | undefined, seconds: number): string | undefined {
  if (!targetLineId) return '请选择目标句。';
  if ((program.leadIns ?? []).some((l) => l.targetLineId === targetLineId)) return '这一句已经有前奏。';
  if (!indicatorContainerId) return '请选择提示组，或新建一个。';
  if (!(seconds > 0)) return '时长要大于 0 秒。';
  return undefined;
}

/** 标记的字：不能是空白，最多 4096 个 UTF-16 单元。 */
export function markTextBlock(text: string): string | undefined {
  if (!text.trim()) return '标记不能是空白。';
  if (text.length > 4096) return '标记太长了。';
  return undefined;
}

// ── 自动生成（§14.5） ──

/** 后端 Core 的默认设置：开头与长间奏都生成，间奏阈值 8 秒，提示时长 3 秒。 */
export const GENERATION_DEFAULTS = { includeOpening: true, includeLongGaps: true, gapThresholdUs: '8000000', durationUs: '3000000' } as const;

/** 对话框的初值：来源已有设置时用它，否则用默认值，提示组取第一个提示组。 */
export function initialGenerationSettings(model: OrchestrationModel, program: OrchestrationProgram): LeadInGenerationSettings {
  const countdowns = countdownsOf(model, program);
  const fallbackId = countdowns[0]?.id ?? null;
  const saved = program.leadInGeneration;
  if (saved) {
    const indicatorContainerId =
      saved.indicatorContainerId && countdowns.some((c) => c.id === saved.indicatorContainerId)
        ? saved.indicatorContainerId
        : fallbackId;
    return {
      includeOpening: saved.includeOpening,
      includeLongGaps: saved.includeLongGaps,
      gapThresholdUs: saved.gapThresholdUs,
      durationUs: saved.durationUs,
      indicatorContainerId,
      excludedLineIds: [...saved.excludedLineIds],
    };
  }
  return { ...GENERATION_DEFAULTS, indicatorContainerId: fallbackId, excludedLineIds: [] };
}

/** 设置能不能预演：要有提示组。时长、阈值的范围由输入框检查。 */
export function generationSettingsBlock(settings: LeadInGenerationSettings): string | undefined {
  return settings.indicatorContainerId ? undefined : '请选择提示组，或新建一个。';
}

export const CANDIDATE_STATUS_TEXT: Record<LeadInCandidateStatus, string> = { New: '新增', Update: '更新', Remove: '移除', Preserved: '保留', Skipped: '跳过' };

export const CANDIDATE_REASON_TEXT: Record<LeadInCandidateReason, string> = {
  None: '',
  MissingLineTime: '这一句没有时间',
  UnknownPrecedingTime: '前面有句子没有时间，判断不了间奏',
  TooShort: '起唱前的时间不够一个提示时长',
  NotEligible: '不是开头，前面的间奏也不够长（或没有勾选这一类）',
  ExistingManual: '已有手工前奏，不会改动',
  Disabled: '已有前奏但停用了，不会改动',
  Excluded: '已排除',
  TimingConflict: '和别的句子或前奏的时间重叠',
};

export const candidateStatusText = (status: string): string => CANDIDATE_STATUS_TEXT[status as LeadInCandidateStatus] ?? status;
export const candidateReasonText = (reason: string): string => CANDIDATE_REASON_TEXT[reason as LeadInCandidateReason] ?? reason;

/** 能勾选应用的候选：新增、更新、移除。 */
export function candidateSelectable(candidate: LeadInGenerationCandidate): boolean {
  return candidate.status === 'New' || candidate.status === 'Update' || candidate.status === 'Remove';
}

/** 默认勾选：新增和更新；移除要用户确认，默认不勾。 */
export function defaultSelection(candidates: readonly LeadInGenerationCandidate[]): string[] {
  return candidates.filter((c) => c.status === 'New' || c.status === 'Update').map((c) => c.targetLineId);
}

const SUMMARY_ORDER: readonly LeadInCandidateStatus[] = ['New', 'Update', 'Remove', 'Preserved', 'Skipped'];

/** 各状态的个数：“新增 1 · 跳过 2”，只写有的；没有候选时写“没有候选”。 */
export function candidateSummary(candidates: readonly LeadInGenerationCandidate[]): string {
  const parts = SUMMARY_ORDER.map((s) => [s, candidates.filter((c) => c.status === s).length] as const)
    .filter(([, n]) => n > 0)
    .map(([s, n]) => `${CANDIDATE_STATUS_TEXT[s]} ${n}`);
  const unknown = candidates.filter((c) => !SUMMARY_ORDER.includes(c.status as LeadInCandidateStatus)).length;
  if (unknown > 0) parts.push(`其他 ${unknown}`);
  return parts.length > 0 ? parts.join(' · ') : '没有候选';
}

/** 应用后的提示：“已应用自动生成：新增 1、更新 1”。 */
export function appliedText(candidates: readonly LeadInGenerationCandidate[], selected: readonly string[]): string {
  const chosen = new Set(selected);
  const parts = (['New', 'Update', 'Remove'] as const)
    .map((s) => [s, candidates.filter((c) => c.status === s && chosen.has(c.targetLineId)).length] as const)
    .filter(([, n]) => n > 0)
    .map(([s, n]) => `${CANDIDATE_STATUS_TEXT[s]} ${n}`);
  return parts.length > 0 ? `已应用自动生成：${parts.join('、')}` : '已保存自动生成的设置';
}

// ── 句子：本句微调与进入动画（§14.7、§14.8） ──

/** 没有微调时的初值：不偏移、不旋转、不缩放。 */
export const NO_TRANSFORM: LineLocalTransform = { offsetXRatio: 0, offsetYRatio: 0, rotationDegrees: 0, scale: 1 };

/** 本句微调：带完整的四项，只换改动的项；原来没有微调时其余取初值。 */
export function lineTransformCommand(branchId: string, lineId: string, current: LineLocalTransform | null, patch: Partial<LineLocalTransform>): OrchestrationCommandOf<'orchestration.setLineLocalTransform'> {
  const base = current ?? NO_TRANSFORM;
  return {
    type: 'orchestration.setLineLocalTransform',
    branchId,
    lineId,
    transform: {
      offsetXRatio: patch.offsetXRatio ?? base.offsetXRatio,
      offsetYRatio: patch.offsetYRatio ?? base.offsetYRatio,
      rotationDegrees: patch.rotationDegrees ?? base.rotationDegrees,
      scale: patch.scale ?? base.scale,
    },
  };
}

type OrchestrationCommandOf<T extends ProjectCommand['type']> = Extract<ProjectCommand, { type: T }>;

/**
 * 在画布上拖动句子（§14.8）：画布位移按句子的编辑参考（所在容器的参考）换成比例，加到原偏移上，保留旋转和缩放。
 * 换算不了时返回 undefined。
 */
export function lineDragTransform(current: LineLocalTransform | null, reference: GeometryReference, dx: number, dy: number): LineLocalTransform | undefined {
  const delta = ratioDelta(reference, dx, dy);
  if (!delta) return undefined;
  const base = current ?? NO_TRANSFORM;
  return { ...base, offsetXRatio: roundTo(base.offsetXRatio + delta.dx, 4), offsetYRatio: roundTo(base.offsetYRatio + delta.dy, 4) };
}

/** 句子显示在哪里：所在片段的目标容器，否则来源的默认容器。 */
export function lineContainerText(model: OrchestrationModel, program: OrchestrationProgram, lineId: string): string {
  const clip = clipByLine(program).get(lineId);
  const id = clip?.containerId ?? program.defaultContainerId;
  const node = model.byId.get(id);
  const name = node ? nodeName(model, node) : `${id}（不存在）`;
  return clip ? `${name}（片段${clip.name ? `“${clip.name}”` : ''}的目标容器）` : `${name}（默认容器）`;
}
