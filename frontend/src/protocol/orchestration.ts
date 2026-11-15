// 工程编排（W4 第一切片，合同 1.13.0，规范 §13.5）：结构树与每个来源的默认容器的只读投影，加一条设置默认容器的命令。
// 快速布局（G18，合同 1.17.0，规范 §13.8）：整来源的预演与应用两条命令，读模型标出默认容器识别出的预设。
// 片段（G19，合同 1.18.0，规范 §13.9）：快速布局可以只套连续几句；读模型的每个程序带片段列表。
// 片段的新建、改名、改容器、删除（G19-B，合同 1.19.0，规范 §13.10）：四条命令，回执带 clipResult；读片段仍只走编排读模型。
// 结构编辑（G20，合同 1.20.0，规范 §13.11）：改名、改父级、子节点排序、加槽位、删除、槽位排序；回执带 structureResult。
// 节点创建与同父复制（G20-B，合同 1.23.0，规范 §13.17）：组、自动布局容器、轮换容器；回执带 createdNodeIds。
// 内部布局默认位置（G21-A，合同 1.21.0，规范 §13.12）与外层几何、布局设置、槽位动画、来源动画策略（G21-B，合同 1.22.0，规范 §13.13）。
// 固定容器参考几何（G22-A，合同 1.22.0，规范 §13.14）：只读 GET，不是最终像素。
// 倒计时容器与手工前奏（G23，合同 1.22.0，规范 §13.15）；单句局部变换（G24，合同 1.22.0，规范 §13.16）。
// G20～G24 整体补齐（合同 1.24.0，规范 §13.18～§13.21）：布局类型切换、工程与单句进入动画、单句展示读取；
// 前奏自动生成（设置、预演、选择应用、删除并排除）、标记增改排、倒计时与标记外观、附着方式；真实 Worker 同帧测量。
// 读模型不含最终外观或画布像素矩形；节点身份只用稳定 ID，不用数组位置。
// 1.17.0 起每个歌词来源都有程序（R4）：还没写进工程的来源返回确定的隐含程序。

import type { BackendDiagnostic } from './common';
import type { Revision } from './int64';
import type { RenderBackend } from './preview';
import type { StaticMaterial, StyleAppearance, StyleChange, StyleMaterial, StyleSungAppearance, StyleTypography } from './style';

/**
 * 锚点（后端枚举名）。写入只接受这六个（大小写不敏感，回读是标准名）；读模型按字符串处理，未知值原样显示。
 */
export type PlacementAnchor = 'Center' | 'TopLeft' | 'TopCenter' | 'BottomRight' | 'LeftCenter' | 'RightCenter';
export const PLACEMENT_ANCHORS: readonly PlacementAnchor[] = ['Center', 'TopLeft', 'TopCenter', 'BottomRight', 'LeftCenter', 'RightCenter'];

/** 节点在父级里的位置；x、y 是父级参考框里的比例坐标，可以超出 0～1（画布外）。scale 大于 0。 */
export interface OrchestrationPlacement {
  x: number;
  y: number;
  anchor: string;
  rotationDegrees: number;
  scale: number;
}

/** 参考框宽高，相对父级的比例。 */
export interface OrchestrationReferenceRect {
  widthRatio: number;
  heightRatio: number;
}

export interface OrchestrationSafeArea {
  left: number;
  top: number;
  right: number;
  bottom: number;
}

/** 槽位动画里的一个姿态：透明度 0～1，缩放大于 0，像素偏移与旋转不限范围。 */
export interface SceneTransform {
  opacity: number;
  scale: number;
  offsetXPx: number;
  offsetYPx: number;
  rotationDegrees: number;
}

export type SlotRevealMode = 'Progress' | 'TimeAfterPreviousStart' | 'LeadBeforeNextStart';
export type SlotConflictPolicy = 'QueueUntilSlotFree' | 'AllowOverflow' | 'ReplacePrevious' | 'Skip';
export type SlotRetentionPolicy = 'UntilLineEnd' | 'UntilNextReveal' | 'Immediate';
export type SlotEasing = 'Linear' | 'EaseIn' | 'EaseOut' | 'EaseInOut';

/**
 * 轮换容器的调度与进入／退出动画（G21-B，1.22.0）：写入时十四个字段都要给。四个时间是非负 Int64 微秒字符串；
 * revealProgress 在 0～1；maxVisibleLines 是正整数。
 */
export interface SlotAnimation {
  revealMode: SlotRevealMode;
  revealProgress: number;
  revealDelayUs: string;
  revealLeadUs: string;
  maxVisibleLines: number;
  conflictPolicy: SlotConflictPolicy;
  retentionPolicy: SlotRetentionPolicy;
  enterDurationUs: string;
  exitDurationUs: string;
  enterFrom: SceneTransform;
  hold: SceneTransform;
  exitTo: SceneTransform;
  exitEasing: SlotEasing;
  easing: SlotEasing;
}

/** 歌词容器的内部布局（自由布局、槽位轮换等），kind 由后端定义。 */
export interface OrchestrationLayout {
  kind: string;
  defaultPlacement?: OrchestrationPlacement | null;
  safeArea: OrchestrationSafeArea;
  maxWidthRatio: number;
  /** 轮换容器的槽位，按轮换顺序。 */
  slotIds: string[];
  /** 槽位动画（1.22.0 起总是出现）：完整对象，或 null 表示没有。 */
  slotAnimation?: SlotAnimation | null;
}

/** 前奏提示容器图形种类（1.54.0）。 */
export type CountdownShapeKind = 'Text' | 'Circle' | 'SteppedBars' | 'Capsule' | 'Diamond' | 'Star' | 'CustomSvg';

/** 自定义内联 SVG 着色模式（1.54.0）。 */
export type CountdownSvgColorMode = 'SilhouetteStyle' | 'OriginalColor';

/** 前奏提示标记图形定义（1.54.0）；仅当 kind 为 CustomSvg 时 svgXml 为非 null 静态内联 SVG。 */
export interface CountdownShapeSpec {
  kind: CountdownShapeKind;
  barMinHeightRatio: number;
  svgXml: string | null;
  svgColorMode: CountdownSvgColorMode;
}

/** 前奏提示容器动画曲线与时间切换方式（1.54.0）。 */
export type CountdownAnimationMode = 'DiscreteSteps' | 'BlinkThenWipeFill' | 'BlinkThenErase';

/** 前奏提示组动画配置（1.54.0）。 */
export interface CountdownAnimationSpec {
  mode: CountdownAnimationMode;
  blinkRatio: number;
  blinkCount: number;
  featherRatio: number;
}

/**
 * 内容节点的有限投影（1.22.0）：倒计时容器（contentKind=countdownSequence）给 markNodeIds、attachmentMode、
 * appearance、shape、animation；文字标记（contentKind=textMark）给 text 与 appearanceOverride。不开放任意内容。
 */
export interface OrchestrationContent {
  markNodeIds?: string[];
  text?: string | null;
  attachmentMode?: string | null;
  /** 倒计时容器的完整外观（1.24.0）。 */
  appearance?: SceneTextAppearance | null;
  /** 标记本层的外观覆盖（1.24.0）：与静态样式的属性修改同形，空数组表示没有覆盖。 */
  appearanceOverride?: StyleChange[];
  /** 倒计时容器的图形与内联 SVG 配置（1.54.0）。 */
  shape?: CountdownShapeSpec | null;
  /** 倒计时容器的闪烁与渐进动画配置（1.54.0）。 */
  animation?: CountdownAnimationSpec | null;
}

/**
 * 倒计时容器的外观（1.24.0，1.50.0 起两态）：待唱（pending）与已过（elapsed，描边、阴影、辉光可跟随待唱）、不透明度。
 * 写入时不能用包材质（M = StaticMaterial）。
 */
export interface SceneTextAppearance<M = StyleMaterial> {
  typography: StyleTypography;
  pending: StyleAppearance<M>;
  elapsed: StyleSungAppearance<M>;
  /** 0..1。 */
  opacity: number;
}

/** 前奏提示组的附着方式（1.24.0；1.54.0 新增首字上方附着）：固定在父级、跟着目标句文字，或置于目标句首字上方（避让注音）。 */
export type LeadInAttachmentMode = 'FixedParent' | 'CueTargetText' | 'CueTargetAboveStart';

export interface OrchestrationNode {
  id: string;
  name?: string | null;
  /** 根节点没有父级。 */
  parentId?: string | null;
  placement: OrchestrationPlacement;
  referenceRect: OrchestrationReferenceRect;
  /** 只有歌词容器有内部布局。 */
  layout?: OrchestrationLayout | null;
  contentKind?: string | null;
  content?: OrchestrationContent | null;
}

/**
 * 片段的调度上下文：片段是从原来的一段（例如默认段）拆出来时，记下原段的成员和前后边界，保持未选句子的调度不变。
 * 直接为选中的句子新建的片段没有上下文。
 */
export interface OrchestrationClipScheduleContext {
  lineIds: string[];
  /** 原段是默认容器里的一段（没有片段的句子）。 */
  isDefaultSegment: boolean;
  previousLineId?: string | null;
  nextLineId?: string | null;
}

/** 片段：把连续几句交给某个歌词容器显示。lineIds 按来源顺序连续。 */
export interface OrchestrationClip {
  id: string;
  name?: string | null;
  lineIds: string[];
  containerId: string;
  scheduleContext?: OrchestrationClipScheduleContext | null;
}

/**
 * 前奏提示（G23，1.22.0）：目标句之前 durationUs 微秒，用 indicatorContainerId 指向的倒计时容器提示。
 * origin 是后端枚举名（手工建的是 Manual）；能算出时间时带 startUs／endUs。
 */
export interface OrchestrationLeadIn {
  id: string;
  name?: string | null;
  enabled: boolean;
  targetLineId: string;
  indicatorContainerId: string;
  durationUs: string;
  origin: string;
  startUs?: string;
  endUs?: string;
}

/** 来源的动画策略（后端枚举名）；null 表示按来源角色解释。 */
export type ProgramAnimationPolicy = 'Karaoke' | 'Static' | 'FollowPrimary';

/**
 * 前奏自动生成的设置（1.24.0）：开头、长间奏两类，间奏阈值与提示时长是 Int64 微秒字符串（时长为正），
 * indicatorContainerId 必须出现（可为 null），excludedLineIds 最多 1000 个、属于这个来源。
 */
export interface LeadInGenerationSettings {
  includeOpening: boolean;
  includeLongGaps: boolean;
  gapThresholdUs: string;
  durationUs: string;
  indicatorContainerId: string | null;
  excludedLineIds: string[];
}

/** 一个歌词来源的显示程序：根容器与默认容器（未单独分配的句子放在默认容器里）。 */
export interface OrchestrationProgram {
  id: string;
  branchId: string;
  rootContainerId: string;
  defaultContainerId: string;
  animationPolicy?: string | null;
  /** 默认容器完全符合某个快速布局预设时是预设名（BasicLayoutPreset），否则省略。 */
  recognizedLayoutPreset?: string | null;
  /** 片段（G19，1.18.0）；旧版本没有这个字段。 */
  clips?: OrchestrationClip[];
  /** 前奏提示（G23，1.22.0 起总是出现）。 */
  leadIns?: OrchestrationLeadIn[];
  /** 前奏自动生成的设置（1.24.0 起总是出现，null 是没有设置）。 */
  leadInGeneration?: LeadInGenerationSettings | null;
}

export interface ProjectOrchestrationResponse {
  projectSessionId: string;
  projectRevision: Revision;
  nodes: OrchestrationNode[];
  programs: OrchestrationProgram[];
  /** 工程默认的进入动画（1.24.0 起总是出现，null 是关闭）。 */
  projectEnterAnimation?: LineEnterAnimation | null;
  protocolVersion: '1';
}

export type LineEnterEasing = 'Linear' | 'EaseIn' | 'EaseOut' | 'EaseInOut';

/**
 * 句子的进入动画（1.24.0）：完整五个字段。时长 0～10000000 微秒（Int64 字符串），起始不透明度 0～1，
 * 起始缩放 (0, 4]，起始偏移 ±10000 画布像素。
 */
export interface LineEnterAnimation {
  durationUs: string;
  easing: LineEnterEasing;
  startOpacity: number;
  startScale: number;
  startOffsetPx: number;
}

/** 内部布局的类型（setLayoutKind 用后端枚举名，与 createNode 的小写 kind 不同）。 */
export type LayoutKind = 'Auto' | 'SlotSequence';

/** 快速布局的四种预设（后端枚举名），槽位数依次是 1、2、2、3。 */
export type BasicLayoutPreset = 'SingleCentered' | 'KaraokeDouble' | 'DoubleCentered' | 'TripleCentered';

/** 预演与应用共用的字段。新节点和片段的名称由前端给出，后端不改已有节点的名称。 */
export interface BasicLayoutFields {
  branchId: string;
  preset: BasicLayoutPreset;
  /**
   * 省略或 null：整个来源，新容器成为默认容器。
   * 非空数组（G19）：按来源顺序连续的几句，新容器只接这几句（新建片段），默认容器不变；乱序或断开 DiscontiguousRange，空、重复或不存在 InvalidRange。
   */
  selectedLineIds?: string[] | null;
  /** 新轮换容器的名称。 */
  layoutName: string;
  /** 新槽位的名称，个数必须等于预设的槽位数。 */
  slotNames: string[];
  /** 范围模式里选中几句的新片段名称；整来源模式也必须给。 */
  selectedSegmentName: string;
  /** 范围模式里，默认段被拆开后未选几句的片段名称（这些片段留在原来的默认容器）。 */
  retainedSegmentName: string;
}

/**
 * setDefaultContainer：containerId 必须是当前工程里已有的歌词容器；没有“清除默认容器”的语义。
 * previewBasicLayout：只预演，不写历史、修订、dirty、redo，回执只带 basicLayoutResult。
 * applyBasicLayout：一条历史；已经是这个布局时是 no-op。
 * 片段四条命令（G19-B）：只支持独立计时、逐字动画的来源（否则 UnsupportedSource）；每次真实变化一条历史。
 * createClip：clipId 由前端给出；lineIds 是 1～1000 个按来源顺序连续、都有时间的行；容器必须在来源展示根下。
 * renameClip：name 必须出现，null 清除名称；同名是 no-op。
 * setClipContainer：同容器是 no-op。
 * deleteClip：几句回到来源的默认容器；回执不带残留片段，要重读编排。
 */
export type OrchestrationCommand =
  | { type: 'orchestration.setDefaultContainer'; branchId: string; containerId: string }
  | OrchestrationStructureCommand
  | OrchestrationPropertiesCommand
  | OrchestrationLeadInCommand
  | OrchestrationLeadInGenerationCommand
  | { type: 'orchestration.setLineLocalTransform'; branchId: string; lineId: string; transform: LineLocalTransform | null }
  | { type: 'orchestration.createClip'; branchId: string; clipId: string; name?: string | null; lineIds: string[]; containerId: string }
  | { type: 'orchestration.renameClip'; branchId: string; clipId: string; name: string | null }
  | { type: 'orchestration.setClipContainer'; branchId: string; clipId: string; containerId: string }
  | { type: 'orchestration.deleteClip'; branchId: string; clipId: string }
  | OrchestrationClipMembersCommand
  | ({ type: 'orchestration.previewBasicLayout' } & BasicLayoutFields)
  | ({ type: 'orchestration.applyBasicLayout' } & BasicLayoutFields);

/**
 * 片段成员（合同 1.25.0）。只支持独立计时、逐字动画的来源。
 * setClipMembers：lineIds 是 1～1000 个按来源顺序连续、不重复的行；保留片段的身份、名称、目标容器和原调度上下文。
 * splitClip：splitBeforeLineId 之前留在原片段（保留身份），从它开始的几句进新片段 newClipId（ID 由前端给出），两者共享原调度上下文。
 * mergeClips：至少两个片段，目标容器与调度上下文相同、合并后成员连续；保留 targetClipId 的身份。
 */
export type OrchestrationClipMembersCommand =
  | { type: 'orchestration.setClipMembers'; branchId: string; clipId: string; lineIds: string[] }
  | { type: 'orchestration.splitClip'; branchId: string; clipId: string; splitBeforeLineId: string; newClipId: string }
  | { type: 'orchestration.mergeClips'; branchId: string; clipIds: string[]; targetClipId: string };

/** 新建节点的种类（大小写精确）：普通组、自动布局容器、轮换容器。 */
export type CreateNodeKind = 'group' | 'auto' | 'slotSequence';

/**
 * 结构命令（G20／G20-B）。所有业务字段都必须出现，null 与省略不同。ID 非空白、最多 128 个 UTF-16 单元；名称最多 256。
 * setNode：只改名，null 清除。reparent：parentId null 是根层，后端保持世界外观。
 * reorderChildren：orderedIds 必须是该父级全部直接子节点各一次；parentId null 是根层。
 * addSlot：给轮换容器追加空槽位，slotId 由前端给出（重复 IdentityConflict）。
 * removeNode：删除节点与子树；仍被程序根、默认容器、片段或前奏引用时拒绝，不隐式改绑。
 * reorderSlots：轮换容器的全部槽位各一次。
 * createNode：parentId null 是画布根层，否则必须是普通组；group／auto 的 slotCount 是 null，slotSequence 是 1～64。
 * copySubtree：同父复制整个子树，name null 清除副本根的名称；不复制歌词、程序、默认归属、片段、前奏、预设分配。
 * 失败：TargetStale（节点不存在）、IdentityConflict、ContainerInvalid（父级不是普通组）、PresentationInvalid（其余结构校验）。
 */
export type OrchestrationStructureCommand =
  | { type: 'orchestration.setNode'; nodeId: string; name: string | null }
  | { type: 'orchestration.reparent'; nodeId: string; parentId: string | null }
  | { type: 'orchestration.reorderChildren'; parentId: string | null; orderedIds: string[] }
  | { type: 'orchestration.addSlot'; containerId: string; slotId: string; name: string | null }
  | { type: 'orchestration.removeNode'; nodeId: string }
  | { type: 'orchestration.reorderSlots'; containerId: string; orderedIds: string[] }
  | { type: 'orchestration.createNode'; kind: CreateNodeKind; parentId: string | null; name: string | null; slotCount: number | null }
  | { type: 'orchestration.copySubtree'; nodeId: string; name: string | null };

/**
 * 属性命令（G21）。都是完整替换：
 * setLayoutDefaultPlacement：placement null 清除内部布局默认位置；回执 layoutResult。
 * setNodeGeometry：外层位置与参考框（宽高比例大于 0，不以 1 为上限）。
 * setLayoutSettings：安全区四边在 0～1 且右大于左、下大于上；maxWidthRatio 在 (0, 1]。保留布局类型与槽位。
 * setSlotAnimation：完整对象或 null（null 只能清除普通布局的；轮换容器必须有动画）。
 * setProgramAnimationPolicy：null 恢复按来源角色解释。
 * 后四条回执 propertiesResult。失败：TargetStale（目标不存在或不是布局）、PresentationInvalid。
 */
export type OrchestrationPropertiesCommand =
  | { type: 'orchestration.setLayoutDefaultPlacement'; containerId: string; placement: OrchestrationPlacement | null }
  | { type: 'orchestration.setNodeGeometry'; nodeId: string; placement: OrchestrationPlacement; referenceRect: OrchestrationReferenceRect }
  | { type: 'orchestration.setLayoutSettings'; containerId: string; safeArea: OrchestrationSafeArea; maxWidthRatio: number }
  | { type: 'orchestration.setSlotAnimation'; containerId: string; slotAnimation: SlotAnimation | null }
  | { type: 'orchestration.setProgramAnimationPolicy'; branchId: string; animationPolicy: ProgramAnimationPolicy | null }
  /**
   * 1.24.0：切换布局类型。Auto 要求 slotIds 为空、newSlotCount 为 0（槽位节点保留，只去掉布局引用和槽位动画）；
   * SlotSequence 的 slotIds 是现有的直属普通子节点，加上 newSlotCount 个由后端新建的槽位，合计 1～64。
   */
  | { type: 'orchestration.setLayoutKind'; containerId: string; kind: LayoutKind; slotIds: string[]; newSlotCount: number }
  /** 1.24.0：单句进入动画，null 清除本句覆盖、继承工程默认。 */
  | { type: 'orchestration.setLineEnterAnimation'; branchId: string; lineId: string; animation: LineEnterAnimation | null }
  /** 1.24.0：工程默认进入动画，null 关闭。 */
  | { type: 'orchestration.setProjectEnterAnimation'; animation: LineEnterAnimation | null }
  /** 1.24.0：前奏提示组的附着方式；containerId 必须是这个前奏的提示组。改成跟随文字时后端用真实 Worker 测量；同方式是 no-op。 */
  | { type: 'orchestration.setLeadInAttachment'; branchId: string; leadInId: string; containerId: string; mode: LeadInAttachmentMode };

/** 前奏的可编辑字段：全部必填，name 可为 null；durationUs 是正 Int64 微秒字符串。 */
export interface LeadInFields {
  branchId: string;
  leadInId: string;
  name: string | null;
  targetLineId: string;
  indicatorContainerId: string;
  durationUs: string;
  enabled: boolean;
}

/**
 * 前奏命令（G23）。createCountdown：在普通组 parentId 下新建倒计时容器（count 为 1～64 个标记），name null 用默认名。
 * createLeadIn：leadInId 由前端给出；目标是这个来源的真实歌词行，提示容器是同一程序根下的倒计时容器。新建的 origin 是 Manual。
 * updateLeadIn：完整替换；真实变化按后端规则转为 Manual。deleteLeadIn：只删前奏，保留提示容器。
 * 失败：SourceMissing、IdentityConflict、TargetStale、ContainerInvalid、LeadInTimeConflict、PresentationInvalid。
 */
export type OrchestrationLeadInCommand =
  | { type: 'orchestration.createCountdown'; parentId: string; name: string | null; count: number }
  | ({ type: 'orchestration.createLeadIn' } & LeadInFields)
  | ({ type: 'orchestration.updateLeadIn' } & LeadInFields)
  | { type: 'orchestration.deleteLeadIn'; branchId: string; leadInId: string }
  /** 1.24.0：删除前奏，并把目标句加进自动生成的排除列表。 */
  | { type: 'orchestration.deleteLeadInAndExclude'; branchId: string; leadInId: string }
  /** 1.24.0：标记。text 非空白、最多 4096 个 UTF-16 单元；新标记的 ID 由后端生成。删除标记用 removeNode。 */
  | { type: 'orchestration.addMark'; containerId: string; text: string }
  | { type: 'orchestration.setMarkText'; nodeId: string; text: string }
  /** 1.24.0：完整的标记顺序（最多 64 个）；spaceMarks 按后端规则均匀排列。 */
  | { type: 'orchestration.reorderMarks'; containerId: string; orderedIds: string[] }
  | { type: 'orchestration.spaceMarks'; containerId: string }
  /** 1.24.0：倒计时容器的完整外观；标记本层的完整覆盖（最多 34 项，不能改擦除、缩放、垂直中心、水平对齐，空数组清除）。1.54.0：设置倒计时提示组图形／SVG 与动画。 */
  | { type: 'orchestration.setCountdownAppearance'; containerId: string; appearance: SceneTextAppearance<StaticMaterial> }
  | { type: 'orchestration.setCountdownPresentation'; containerId: string; shape: CountdownShapeSpec | null; animation: CountdownAnimationSpec | null }
  | { type: 'orchestration.setMarkAppearance'; nodeId: string; changes: StyleChange[] };

/**
 * 前奏自动生成（1.24.0，规范 §13.19）。setLeadInGeneration：保存设置（null 清除），不会自动生成。
 * previewLeadInGeneration：只读分析，不写历史、修订、dirty、redo。applyLeadInGeneration：在当前修订重新分析，只应用
 * selectedLineIds 里的目标句（必须给出，可以是空数组；省略不等于全选）。回执 leadInGenerationResult。
 */
export type OrchestrationLeadInGenerationCommand =
  | { type: 'orchestration.setLeadInGeneration'; branchId: string; settings: LeadInGenerationSettings | null }
  | { type: 'orchestration.previewLeadInGeneration'; branchId: string; settings: LeadInGenerationSettings }
  | { type: 'orchestration.applyLeadInGeneration'; branchId: string; settings: LeadInGenerationSettings; selectedLineIds: string[] };

/** 单句局部变换（G24）：偏移是父参考宽高的比例（可以超出画布），旋转是度，缩放大于 0。 */
export interface LineLocalTransform {
  offsetXRatio: number;
  offsetYRatio: number;
  rotationDegrees: number;
  scale: number;
}

export interface OrchestrationCommandResult {
  branchId: string;
  containerId: string;
  changed: boolean;
}

/**
 * 片段命令的结果（G19-B，1.19.0）：命令之后片段的样子；删除时是删掉前的样子。
 * 失败（422 commandRejected）的 failureCode：SourceMissing、ClipExists、TargetStale（片段或行不存在）、InvalidRange、DiscontiguousRange、
 * UnsupportedSource、ContainerInvalid、ClipScheduleConflict（行已在别的片段，或片段的时间跨度和片段外的句子、别的片段重叠）、TargetConflict。
 */
export interface OrchestrationClipCommandResult {
  branchId: string;
  clipId: string;
  name?: string | null;
  lineIds: string[];
  containerId: string;
  changed: boolean;
}

/**
 * 片段成员命令的结果：改动后的片段（拆分时两个），被合并掉的片段，以及随之新建或删除的预设分配。
 * 拒绝的 failureCode：TargetStale、UnsupportedSource、InvalidRange、DiscontiguousRange、ScheduleContextConflict、ClipScheduleConflict、ReferenceConflict。
 */
export interface OrchestrationClipMembersCommandResult {
  operation: 'setClipMembers' | 'splitClip' | 'mergeClips';
  branchId: string;
  clips: OrchestrationClip[];
  removedClipIds: string[];
  changed: boolean;
  createdApplicationIds: string[];
  removedApplicationIds: string[];
}

/**
 * 快速布局的影响统计。预演时 changed 总是 false；应用真实改变时为 true。
 * 失败（422 commandRejected）的 failureCode：InvalidPreset、SourceMissing、UnsupportedSource（1.18.0：只支持独立计时、逐字动画的来源，翻译等跟随主歌词的来源不行）、
 * InvalidRange、DiscontiguousRange、PresentationInvalid、RecipeConflict、ReferenceConflict、ScheduleConflict、ScheduleFailure。
 */
export interface BasicLayoutCommandResult {
  branchId: string;
  preset: string;
  /** source：整来源；range：连续几句（G19）。 */
  scope: string;
  /** 结果里默认容器识别出的预设；识别不出时省略。 */
  recognizedPreset?: string | null;
  affectedLines: number;
  existingClips: number;
  preservedNodes: number;
  splitClips: number;
  changed: boolean;
  /** 本次新建的片段 ID（G19，1.18.0）：范围模式里选中几句的片段，以及拆出来的片段。 */
  clipIds?: string[];
}

/** 结构命令的结果（G20／G20-B）。operation 不带 orchestration. 前缀；按操作给 nodeId、parentId（根层省略）、containerId 或 orderedIds。 */
export interface OrchestrationStructureCommandResult {
  operation: string;
  nodeId?: string;
  parentId?: string;
  containerId?: string;
  orderedIds?: string[];
  changed: boolean;
  /** createNode／copySubtree（1.23.0）：全部新节点 ID，新根在第一个。 */
  createdNodeIds?: string[];
}

/** setLayoutDefaultPlacement 的结果（G21-A）。 */
export interface OrchestrationLayoutCommandResult {
  containerId: string;
  changed: boolean;
}

/** 四条属性命令的结果（G21-B）：只给实际目标；值要重读编排。 */
export interface OrchestrationPropertiesCommandResult {
  operation: string;
  nodeId?: string;
  branchId?: string;
  changed: boolean;
}

/** 前奏命令的结果（G23）：createCountdown 给 indicatorContainerId 与 markNodeIds；前奏操作给 branchId／leadInId／indicatorContainerId。 */
export interface OrchestrationLeadInCommandResult {
  operation: string;
  branchId?: string;
  leadInId?: string;
  indicatorContainerId?: string;
  markNodeIds?: string[];
  changed: boolean;
}

export type LeadInCandidateStatus = 'New' | 'Update' | 'Remove' | 'Preserved' | 'Skipped';
export type LeadInCandidateReason =
  | 'None'
  | 'MissingLineTime'
  | 'UnknownPrecedingTime'
  | 'TooShort'
  | 'NotEligible'
  | 'ExistingManual'
  | 'Disabled'
  | 'Excluded'
  | 'TimingConflict';

/** 起唱锚点：timingStatus 是后端的时间状态枚举名。 */
export interface LeadInGenerationAnchor {
  timeUs: string;
  timingStatus: string;
  usesLineTime: boolean;
  hasMissingPrefix: boolean;
}

/** 一个目标句的生成候选：新建、更新、移除、保留或跳过（附原因）；可用时带现有与拟定的完整前奏和时间。 */
export interface LeadInGenerationCandidate {
  targetLineId: string;
  status: LeadInCandidateStatus | (string & {});
  reason: LeadInCandidateReason | (string & {});
  existing?: OrchestrationLeadIn;
  proposed?: OrchestrationLeadIn;
  startUs?: string;
  endUs?: string;
  anchor?: LeadInGenerationAnchor;
}

export interface LeadInGenerationDiagnostic {
  code: string;
  severity: string;
  message: string;
  location?: string;
}

/** 前奏生成三条命令的结果（1.24.0）：全部候选与诊断，不截断。 */
export interface LeadInGenerationCommandResult {
  operation: string;
  branchId: string;
  settings?: LeadInGenerationSettings | null;
  candidates: LeadInGenerationCandidate[];
  diagnostics: LeadInGenerationDiagnostic[];
  selectedLineIds: string[];
  changed: boolean;
}

/** setLineLocalTransform 的结果（G24）：transform 总是出现，null 表示没有覆写。 */
export interface LineLocalTransformCommandResult {
  branchId: string;
  lineId: string;
  transform: LineLocalTransform | null;
  changed: boolean;
}

/** 世界矩阵：x' = a·x + c·y + tx，y' = b·x + d·y + ty；父矩阵乘子矩阵。 */
export interface GeometryMatrix {
  a: number;
  b: number;
  c: number;
  d: number;
  tx: number;
  ty: number;
}

export interface GeometryPoint {
  x: number;
  y: number;
}

/** 一个参考框：宽高、世界矩阵、轴心，以及左上、右上、右下、左下四个画布坐标角点。 */
export interface GeometryReference {
  width: number;
  height: number;
  worldMatrix: GeometryMatrix;
  pivot: GeometryPoint;
  worldCorners: GeometryPoint[];
}

/**
 * 单节点的固定容器参考几何（G22-A，1.22.0）：按节点的位置与参考框算出的画布坐标，不含文字测量、自动布局、动画与效果，
 * 不是最终像素（finalPixelGeometry 总是 false）。根节点没有 parentId，parentReference 是画布。
 * 失败：404（节点不存在）；422 GeometryReferenceInvalid；413 GeometryReferenceBudgetExceeded。
 */
export interface ProjectGeometryReferenceResponse {
  projectSessionId: string;
  projectRevision: Revision;
  nodeId: string;
  parentId?: string;
  canvasWidth: number;
  canvasHeight: number;
  reference: GeometryReference;
  parentReference: GeometryReference;
  referenceKind: 'fixedContainerReferenceV1';
  finalPixelGeometry: false;
  protocolVersion: '1';
}

/** 单句局部变换的读取（G24）：transform null 表示没有覆写。来源或行不存在 422 TargetStale。 */
export interface ProjectLineLocalTransformResponse {
  projectSessionId: string;
  projectRevision: Revision;
  branchId: string;
  lineId: string;
  transform: LineLocalTransform | null;
  protocolVersion: '1';
}

/**
 * 单句展示的静态继承（1.24.0，规范 §13.18）：本句的局部变换与进入动画覆盖，以及实际生效的进入动画和它的来源。
 * 三个可空字段总是出现。同帧实际几何用 measureScene。
 */
export interface ProjectLinePresentationResponse {
  projectSessionId: string;
  projectRevision: Revision;
  branchId: string;
  lineId: string;
  localTransform: LineLocalTransform | null;
  localEnterAnimation: LineEnterAnimation | null;
  effectiveEnterAnimation: LineEnterAnimation | null;
  animationSource: 'line' | 'project' | 'none';
  protocolVersion: '1';
}

export type SceneMeasurementMode = 'time' | 'layout';

/**
 * 同帧测量的请求（1.24.0，规范 §13.21）：time 模式在 timeUs 时刻采样，layout 模式用代表试样（timeUs 必须是 null）。
 * 输出最大 1280×720。只读，不写历史，可以按同一修订重试。
 */
export interface SceneMeasurementRequest {
  expectedRevision: Revision;
  mode: SceneMeasurementMode;
  timeUs: string | null;
  width: number;
  height: number;
  alphaMode: 'opaque' | 'transparent';
  /** 省略或 null 用启动默认。 */
  renderBackend?: RenderBackend | null;
}

/**
 * 一个测量元素：容器、槽位、行、注音或标记。worldCorners 是实际墨迹的角点（容器可含多组后代四角，没有墨迹时为空）；
 * fixedReference 是持久的固定参考框，editReference 是编辑用的父参考。representative 表示 layout 模式合成的代表行。
 */
export interface SceneMeasurementElement {
  id: string;
  parentId?: string;
  kind: 'container' | 'slot' | 'line' | 'ruby' | 'mark' | (string & {});
  branchId?: string;
  lineId?: string;
  tokenId?: string;
  rubyId?: string;
  bindingId?: string;
  slotId?: string;
  representative: boolean;
  visible: boolean;
  opacity: number;
  geometryStatus: 'notVisible' | 'emptyReference' | 'measuredInk' | (string & {});
  worldMatrix: GeometryMatrix | null;
  pivot: GeometryPoint | null;
  worldCorners: GeometryPoint[];
  fixedReference: GeometryReference | null;
  editReference: GeometryReference | null;
}

export interface SceneMeasurement {
  sourceSha256: string;
  sampleInputFingerprint: string;
  sourceProfile: string;
  samplingProfile: 'layout-reference-v1' | 'time-v1' | (string & {});
  mode: SceneMeasurementMode;
  timeUs: string | null;
  canvasWidth: number;
  canvasHeight: number;
  width: number;
  height: number;
  alphaMode: 'opaque' | 'transparent';
  elements: SceneMeasurementElement[];
  pngSha256: string;
  /** base64 的 PNG（与 elements 同一帧）。 */
  png: string;
  /** 这次测量用的设备；缺失或 null 表示没记录。 */
  renderBackend?: RenderBackend | null;
  /** 1.51.0：本帧缺背景时图效果或材质已按声明降级，去重后的兜底诊断（warning，最多 128 条）。旧后端缺失时按空。 */
  fallbackDiagnostics?: BackendDiagnostic[];
}

export interface SceneMeasurementResponse {
  projectSessionId: string;
  projectRevision: Revision;
  measurement: SceneMeasurement;
  protocolVersion: '1';
}
