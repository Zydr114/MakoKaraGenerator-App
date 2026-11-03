// 独立模板库（合同 1.9.0 起，规范 §13.1～§13.2）、完整定义（合同 1.37.0，规范 §24.1）与模板试样（G17，合同 1.16.0，规范 §13.7；工坊样本 §24.3）。
// 库属于后端 profile，与工程、播放头和工程修订无关。
// 库修订是不透明字符串：个人库还没有入口时为 'missing'（与已发布的空库不同），否则是 64 位小写十六进制。

import type { EffectPackageInspectResponse, EffectPackageReference } from './effect-packages';
import type { CanonicalStyle, StaticMaterial } from './style';
import type { TemplateLibraryId } from './presets';
import type { PreviewAlphaMode, PreviewSize, PreviewSessionSource, RenderBackend } from './preview';

/** 摘要窗口 limit 的上限（默认 50）。 */
export const TEMPLATE_LIST_MAX_LIMIT = 100;

export type TemplateKind = 'line' | 'token' | 'composite';

/** 模板可以控制的 26 个属性（与 styles.patch 的 29 个路径无关）。 */
export const TEMPLATE_PROPERTIES = [
  'fontFamilyName',
  'fallbackFamilyNames',
  'fontSizePx',
  'fontWeight',
  'isBold',
  'isItalic',
  'letterSpacingPx',
  'verticalCenterRatio',
  'horizontalAlignment',
  'unsungColor',
  'sungColor',
  'unsungGradientSpec',
  'sungGradientSpec',
  'strokeColor',
  'strokeWidthPx',
  'strokeSpec',
  'shadowColor',
  'shadowBlurPx',
  'shadowSpec',
  'opacity',
  'scale',
  'glowSpec',
  'glowColor',
  'glowOpacity',
  'glowBlurRadiusPx',
  'glowSpreadPx',
] as const;
export type TemplateProperty = (typeof TEMPLATE_PROPERTIES)[number];

/** 各用途允许的属性（OpenAPI StaticTemplateDefinitionDto 的 allOf）；文本单元目标也只接受前者范围内的模板。 */
export const TOKEN_TEMPLATE_PROPERTIES: readonly TemplateProperty[] = [
  'unsungColor',
  'sungColor',
  'unsungGradientSpec',
  'sungGradientSpec',
  'strokeColor',
  'strokeWidthPx',
  'strokeSpec',
  'shadowColor',
  'shadowBlurPx',
  'shadowSpec',
  'opacity',
  'glowSpec',
  'glowColor',
  'glowOpacity',
  'glowBlurRadiusPx',
  'glowSpreadPx',
];
export const COMPOSITE_TEMPLATE_PROPERTIES: readonly TemplateProperty[] = TEMPLATE_PROPERTIES.filter(
  (p) => p !== 'verticalCenterRatio' && p !== 'horizontalAlignment',
);

export interface TemplateLibraryDescriptor {
  libraryId: TemplateLibraryId;
  name: string;
  readOnly: boolean;
}

export interface TemplateLibrariesResponse {
  libraries: TemplateLibraryDescriptor[];
  protocolVersion: string;
}

export interface TemplateSummary {
  /** 保留原 Unicode、空格和斜杠，不 trim。 */
  templateId: string;
  name: string;
  kind: TemplateKind;
  /** 单个模板的固定修订；应用到工程时原样提交。 */
  templateRevision: string;
  properties: TemplateProperty[];
  hasEffectRecipes: boolean;
  hasPackageMaterials: boolean;
  hasPackages: boolean;
}

export interface TemplateListResponse {
  libraryId: TemplateLibraryId;
  revision: string;
  offset: number;
  totalCount: number;
  nextOffset?: number;
  templates: TemplateSummary[];
  protocolVersion: string;
}

export type TemplateStyle = CanonicalStyle<StaticMaterial>;

export interface StaticTemplateDefinition {
  name: string;
  kind: TemplateKind;
  style: TemplateStyle;
  /** 至少 1 个；按用途受限。 */
  properties: TemplateProperty[];
}

/** 包材质：参数是对象 JSON 的文本（键为参数 ID，只写改过的参数），保留 Int64 与原数值精度，不能先转成 JS Number。 */
export interface FullPackageMaterial {
  kind: 'package';
  packageId: string;
  revision: string;
  exportId: string;
  parametersJson: string;
}
export type FullTemplateMaterial = StaticMaterial | FullPackageMaterial;
/** 完整规范样式：材质可以是包材质；未选的属性也完整保留。 */
export type FullTemplateStyle = CanonicalStyle<FullTemplateMaterial>;

export type EffectRecipeTarget = 'line' | 'tokenFamily' | 'application';
export type EffectOverlayLayer = 'package' | 'behindText' | 'aboveText';
export type EffectReadScope = 'boundSources' | 'project' | 'branchInput';

/** 效果配方：用包里的一个 controller／graph 导出作用于整行、逐字或整个分配。字段都必填，可空的显式写 null。 */
export interface TemplateEffectRecipe {
  recipeId: string;
  name: string;
  target: EffectRecipeTarget;
  packageId: string;
  revision: string;
  exportId: string;
  parametersJson: string;
  overlayLayer: EffectOverlayLayer;
  enabled: boolean;
  readScope: EffectReadScope;
  /** readScope=branchInput 时读取的输入名。 */
  branchInput: string | null;
  /** UInt64 十进制字符串。 */
  fixedSeed: string | null;
  configurationId: string | null;
}

/**
 * 完整定义（definitionFormat=full）：静态模板也可以按这个格式读写。
 * properties 与 effectRecipes 至少一个非空；纯配方模板 properties 为空数组。packages 固定定义用到的全部包。
 */
export interface FullTemplateDefinition {
  name: string;
  kind: TemplateKind;
  style: FullTemplateStyle;
  properties: TemplateProperty[];
  effectRecipes: TemplateEffectRecipe[];
  packages: EffectPackageReference[];
}

/**
 * 省略 definitionFormat 的旧读法：高级模板 definition 为 null。
 * advancedRuntimeAvailable：已有整行／元素高级闭包可以执行（应用、试看）；advancedUnsupported：不能执行。
 */
export interface TemplateDetailResponse {
  libraryId: TemplateLibraryId;
  revision: string;
  summary: TemplateSummary;
  definitionAvailability: 'staticAvailable' | 'advancedRuntimeAvailable' | 'advancedUnsupported';
  definition: StaticTemplateDefinition | null;
  protocolVersion: string;
}

/** definitionFormat=full 的读法：任何模板都返回完整定义；packages 里是绑定当前库修订的 library 引用。 */
export interface TemplateFullDetailResponse {
  libraryId: TemplateLibraryId;
  revision: string;
  summary: TemplateSummary;
  definitionFormat: 'full';
  definitionAvailability: 'fullAvailable';
  definition: FullTemplateDefinition;
  /** 每个包的检查结果（只读说明，保存时不回传）。 */
  packageClosureDetails: EffectPackageInspectResponse[];
  protocolVersion: string;
}

/** 只对 personal 库；所有命令都带库的 expectedRevision 做 CAS。create／replace 带 definitionFormat=full 时提交完整定义。 */
export type TemplateLibraryCommand =
  | { type: 'create'; definition: StaticTemplateDefinition }
  | { type: 'create'; definitionFormat: 'full'; definition: FullTemplateDefinition }
  | { type: 'replace'; templateId: string; definition: StaticTemplateDefinition }
  | { type: 'replace'; templateId: string; definitionFormat: 'full'; definition: FullTemplateDefinition }
  | { type: 'copy'; sourceLibraryId: TemplateLibraryId; sourceRevision: string; templateId: string; name: string }
  | { type: 'rename'; templateId: string; name: string }
  | { type: 'delete'; templateId: string };

export interface TemplateLibraryCommandResponse {
  libraryId: 'personal';
  previousRevision: string;
  revision: string;
  changed: boolean;
  /** create／copy 时是新模板的 ID。 */
  templateId: string;
  protocolVersion: string;
}

/** 旧的三秒试样：640×360 画布上一行“Template sample”，0.1～1.2 秒唱第一个元素；不接受 targetKey 与 materialSlot。 */
export const TEMPLATE_PREVIEW_SAMPLE_ID = 'default-v1';
/** 工坊样本：单行、多行各 12 秒，目标从 preview-options 取。 */
export const TEMPLATE_WORKSHOP_SAMPLE_IDS = ['workshop-single-line-v1', 'workshop-multi-line-v1'] as const;
export type TemplateWorkshopSampleId = (typeof TEMPLATE_WORKSHOP_SAMPLE_IDS)[number];
export type TemplateSampleId = typeof TEMPLATE_PREVIEW_SAMPLE_ID | TemplateWorkshopSampleId;
export type TemplateMaterialSlot = 'unsungFill' | 'sungFill' | 'stroke';

/**
 * 已保存的模板（锁定库修订与模板修订），或客户端的草稿（draftRevision 由客户端生成，最长 256，定义只留在会话里、不写库）。
 * 静态草稿省略 definitionFormat；完整草稿带 definitionFormat=full，可以含包材质与配方。
 */
export type TemplatePreviewSource =
  | { kind: 'savedTemplate'; libraryId: TemplateLibraryId; libraryRevision: string; templateId: string; templateRevision: string }
  | { kind: 'draft'; draftRevision: string; definition: StaticTemplateDefinition }
  | { kind: 'draft'; draftRevision: string; definitionFormat: 'full'; definition: FullTemplateDefinition };

/** 返回 previewKind=template、committable=false 的预览会话；样本像素只是试看，不等于工程里的最终外观。 */
export interface TemplatePreviewInput {
  source: TemplatePreviewSource;
  sampleId: TemplateSampleId;
  size: PreviewSize;
  alphaMode?: PreviewAlphaMode;
  /** 只用于工坊样本；省略取第一个兼容目标。 */
  targetKey?: string | null;
  /** 只是编辑焦点，仍运行完整模板。只用于工坊样本。 */
  materialSlot?: TemplateMaterialSlot | null;
  /** 省略或 null 用启动默认。 */
  renderBackend?: RenderBackend | null;
}

/** 样本里模板可以作用的一个目标：key 是 line／project／branch／clip／group／mark／token 之一，binding 是原领域绑定。 */
export interface TemplateSampleTarget {
  key: string;
  binding: { kind: string; id?: string; ids: string[]; includeDescendants: boolean };
}

export interface TemplateSampleOption {
  sampleId: TemplateWorkshopSampleId;
  multiLine: boolean;
  /** 固定 "12000000"。 */
  durationUs: string;
  /** 按模板能否作用于该目标筛过；为空表示这个样本上没有可用目标。 */
  targets: TemplateSampleTarget[];
}

export interface TemplateMaterialSlotOption {
  slot: TemplateMaterialSlot;
  editable: boolean;
  selected: boolean;
  /** 关闭描边时 stroke 为 false，material 为 null。 */
  present: boolean;
  material: FullTemplateMaterial | null;
}

/** POST /template-preview-options：只读，不建会话、没有回执。 */
export interface TemplatePreviewOptionsResponse {
  source: PreviewSessionSource;
  samples: TemplateSampleOption[];
  materialSlots: TemplateMaterialSlotOption[];
  defaultSampleId: TemplateWorkshopSampleId;
  protocolVersion: '1';
}
