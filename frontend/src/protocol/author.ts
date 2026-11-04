// 作者草稿：源码、配置、编译、采用与作者验证（合同 1.38.0，规范 §21）。
// 普通草稿接口只允许 owner client；1.40.0 另有发现与显式认领接口，当前界面尚未接入。
// 草稿修订和源码修订都是 sha256:<64 位小写十六进制>，按内容计算，原样带回，不解析。

import type { EffectOverlayLayer, EffectReadScope, EffectRecipeTarget, TemplateMaterialSlot, TemplateSampleOption, TemplateWorkshopSampleId } from './templates';
import type { RenderBackend } from './preview';

/** 每个 client 最多保留的草稿数；再建返回 429 requestQuotaExceeded（field=drafts）。 */
export const AUTHOR_DRAFT_LIMIT = 16;

/** 领域分类：控制器和图导出是 Effect，材质导出是 Material（注意首字母大写，与包导出的 kind 不同）。 */
export type ScriptConfigurationKind = 'Effect' | 'Material' | (string & {});

/** 草稿与个人配置库共用的配置字段（读法）：没有输入名、没有固定种子时字段直接省略。 */
export interface ScriptConfigurationFields {
  name: string;
  packageId: string;
  packageRevision: string;
  /** 在用修订：试样、验证和存进配置库时实际用的源码。 */
  sourceRevision: string;
  exportId: string;
  /** 对象 JSON 文本，键为参数 ID，只写改过的参数；保留十进制原文。 */
  parametersJson: string;
  relativeTarget: EffectRecipeTarget;
  readScope: EffectReadScope;
  branchInput?: string;
  /** UInt64 十进制字符串。 */
  fixedSeed?: string;
  overlayLayer: EffectOverlayLayer;
  kind: ScriptConfigurationKind;
  bindingKinds: string[];
}

export type AuthorDraftConfiguration = ScriptConfigurationFields;

/** 草稿的工坊样本：目标按草稿配置筛过；材质草稿的 materialSlots 是三个部位，效果草稿为空。 */
export interface AuthorDraftSampleOptions {
  samples: TemplateSampleOption[];
  materialSlots: TemplateMaterialSlot[];
  defaultSampleId: TemplateWorkshopSampleId;
}

export interface AuthorCompilationSummary {
  sourceRevision: string;
  succeeded: boolean;
  /** 草稿已经保存了更新的源码。 */
  stale: boolean;
  artifactHash: string;
  diagnosticCount: number;
  completedAtUtc: string;
}

/**
 * 草稿里的四个修订（W8 设计 §0）：baseSourceRevision 是导入时的原包，currentSourceRevision 是最近保存的修订，
 * adoptedSourceRevision 是最近采用的修订，configuration.sourceRevision 是在用修订。SDK2 保存源码不改变采用或在用修订；v1 保持原跟随规则。
 */
export interface AuthorDraft {
  draftId: string;
  draftRevision: string;
  name: string;
  packageId: string;
  baseSourceRevision: string;
  currentSourceRevision: string;
  exportId: string;
  adoptedSourceRevision?: string | null;
  /** 当前源码修订有 C# 且宿主提供编译参考集（R10 在合同 1.40.0 修复）；不等于在用修订能否编辑。 */
  canCompile: boolean;
  canAdopt: boolean;
  lastCompilation?: AuthorCompilationSummary;
  configuration?: AuthorDraftConfiguration;
  sampleOptions?: AuthorDraftSampleOptions;
  /** 当前固定包的正式格式身份，用于 SDK1／SDK2 作者流程分派。 */
  packageVersion?: number;
  sdkVersion?: string;
  protocolVersion: '1';
}

export interface AuthorDraftListResponse {
  drafts: AuthorDraft[];
  protocolVersion: '1';
}

/** 从刚上传的 .mkfx 建，或从配置库里的一个配置建（带精确库修订）。 */
export type AuthorDraftCreateInput = { exportId: string; name: string } & (
  | { packageAssetId: string }
  | { configurationId: string; expectedConfigurationRevision: string }
);

/**
 * PUT .../configuration 的完整配置（写法）：可空字段显式写 null。sourceRevision 可以是草稿保留的任一修订，
 * 这时采用标记不变；SDK2 C# 来源变更必须已有成功编译记录。材质只能作用于 application，不能有种子和叠放层（AuthorConfigurationInvalid）。
 */
export interface AuthorDraftConfigurationInput {
  name: string;
  packageId: string;
  sourceRevision: string;
  exportId: string;
  parametersJson: string;
  relativeTarget: EffectRecipeTarget;
  readScope: EffectReadScope;
  branchInput: string | null;
  fixedSeed: string | null;
  overlayLayer: EffectOverlayLayer;
}

export interface AuthorSourceFile {
  /** 包内路径，例如 src/WordEmphasis.cs。 */
  path: string;
  text: string;
}

/** 只返回 C# 文件；没有 C# 文件的包（材质、图）返回 404。 */
export interface AuthorDraftSourcesResponse {
  draftId: string;
  draftRevision: string;
  sourceRevision: string;
  packageId: string;
  packageRevision: string;
  files: AuthorSourceFile[];
  protocolVersion: '1';
}

/** 保存源码：必须一次提交现有的全部且仅全部 C# 文件（G34）。内容相同是 no-op。 */
export interface AuthorSourceRevisionResponse {
  draftId: string;
  draftRevision: string;
  packageId: string;
  sourceRevision: string;
  sourcePaths: string[];
  protocolVersion: '1';
}

export interface AuthorCompilationDiagnostic {
  /** 例如 CS0103、PackageCSharpMissing。 */
  code: string;
  /** 编译器原文（英文）。 */
  message: string;
  isError: boolean;
  path?: string;
  /** 从 1 开始。 */
  line: number;
  column: number;
}

/** 编译结果属于请求捕获的源码修订；旧修订成功不表示最新修订成功。 */
export interface AuthorCompilation {
  draftId: string;
  draftRevision: string;
  sourceRevision: string;
  succeeded: boolean;
  stale: boolean;
  artifactHash: string;
  diagnostics: AuthorCompilationDiagnostic[];
  protocolVersion: '1';
}

/** 采用：在用修订和采用标记都换成这个修订；SDK2 C# 来源必须已有成功编译记录。 */
export interface AuthorAdoptResponse {
  draftId: string;
  draftRevision: string;
  packageId: string;
  previousSourceRevision: string;
  adoptedSourceRevision: string | null;
  exportId: string;
  protocolVersion: '1';
}

export interface AuthorDraftDeleteResponse {
  draftId: string;
  draftRevision: string;
  protocolVersion: '1';
}

/** 作者验证：在草稿的一个工坊样本上按固定规格（640×360、30 fps、12 秒）跑不透明和透明两个输出。 */
export interface AuthorValidationInput {
  sampleId: TemplateWorkshopSampleId;
  targetKey: string;
  /** 材质草稿必填。 */
  materialSlot?: TemplateMaterialSlot | null;
  /** 省略或 null 用启动默认。 */
  renderBackend?: RenderBackend | null;
}

/** 任务里固定的作者编译输入。 */
export interface AuthorCompilationJobInput {
  draftId: string;
  draftRevision: string;
  sourceRevision: string;
}

/** 任务里固定的作者验证输入。 */
export interface AuthorValidationJobInput {
  draftId: string;
  draftRevision: string;
  sourceRevision: string;
  sampleId: TemplateWorkshopSampleId;
  targetKey: string;
  materialSlot?: TemplateMaterialSlot | null;
  renderBackend?: RenderBackend | null;
}
