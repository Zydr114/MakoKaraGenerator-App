// 效果包（.mkfx）的引用与只读检查（合同 1.37.0，规范 §24.1～§24.2）。
// 包内的数值（参数默认值、上下限）以 JSON 文本保留原精度：时长上限可以是 Int64 最大值，不能转成 JS Number。

/**
 * 模板定义里固定的包身份与来源，三种来源字段互斥。
 * asset：刚上传的 .mkfx 资源；library：完整详情为库里每个包给出的引用，绑定库修订与模板修订，库一改就过期（409）；
 * configuration：个人配置库（W8）。读回时其余来源的字段显式为 null。
 */
export type EffectPackageReference =
  | { kind: 'asset'; packageId: string; packageRevision: string; packageAssetId: string }
  | {
      kind: 'library';
      packageId: string;
      packageRevision: string;
      libraryId: string;
      libraryRevision: string;
      templateId: string;
      templateRevision: string;
    }
  | { kind: 'configuration'; packageId: string; packageRevision: string; configurationId: string; configurationRevision: string };

export const EFFECT_PARAMETER_TYPES = ['number', 'integer', 'duration', 'boolean', 'enum', 'text', 'color', 'curve', 'asset'] as const;
export type EffectParameterType = (typeof EFFECT_PARAMETER_TYPES)[number];

export interface EffectPackageParameter {
  id: string;
  type: EffectParameterType;
  label: string;
  unit?: string;
  /** 完整 JSON 文本。颜色是 {"r","g","b","a"}（0..1）。 */
  defaultValueJson: string;
  minimumJson?: string;
  maximumJson?: string;
  /** enum 的取值。 */
  options: { value: string; label: string }[];
}

export type EffectExportKind = 'controller' | 'material' | 'graph';

export interface EffectPackageExport {
  id: string;
  kind: EffectExportKind;
  name: string;
  /** 这个导出用到的参数。 */
  parameterIds: string[];
  bindingKinds: string[];
  declaration: Record<string, unknown>;
}

export interface EffectPackageFile {
  /** 包内逻辑路径。 */
  path: string;
  role: string;
  size: number;
  sha256: string;
}

export interface EffectPackageDiagnostic {
  code: string;
  path: string;
  message: string;
}

/** 已上传的包资源（可带期望的包修订），或一个完整引用；二选一。 */
export type EffectPackageInspectInput =
  | { packageAssetId: string; expectedPackageRevision?: string | null }
  | { reference: EffectPackageReference };

/**
 * POST /effect-packages/inspect：只解析包结构，不编译脚本、不运行、不解码图片。
 * 结构语义不合法时仍是 200，structureValid=false，原因在 diagnostics；包本身读不出来才是错误。
 */
/** 控件只读的声明投影；作者草稿无需制造可分发包引用。 */
export interface EffectPackageSchema {
  structureValid: boolean;
  parameters: EffectPackageParameter[];
  exports: EffectPackageExport[];
  files: EffectPackageFile[];
}

export interface EffectPackageInspectResponse extends EffectPackageSchema {
  /** 固定后的引用：上传的资源返回 asset 引用。 */
  reference: EffectPackageReference;
  packageId: string;
  packageRevision: string;
  name: string;
  version: string;
  manifest: Record<string, unknown>;
  /** manifest 的原文，保留十进制整数。 */
  manifestJson: string;
  structureValid: boolean;
  parameters: EffectPackageParameter[];
  exports: EffectPackageExport[];
  bindingKinds: string[];
  inputs: { path: string; inputs: Record<string, unknown>[] }[];
  files: EffectPackageFile[];
  diagnostics: EffectPackageDiagnostic[];
  protocolVersion: '1';
}
