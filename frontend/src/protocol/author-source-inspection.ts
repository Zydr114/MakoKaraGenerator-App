// 作者固定源码的只读声明；没有可分发 reference，不包含 manifest 原文。
import type { EffectPackageDiagnostic, EffectPackageSchema } from './effect-packages';
import type { EffectOverlayLayer, EffectReadScope, EffectRecipeTarget } from './templates';

export interface AuthorSourceInspectionResponse extends EffectPackageSchema {
  draftId: string;
  draftRevision: string;
  sourceRevision: string;
  packageId: string;
  packageRevision: string;
  name: string;
  version: string;
  packageVersion: number;
  sdkVersion: string;
  shaderProfile: string;
  bindingKinds: string[];
  inputs: { path: string; inputs: Record<string, unknown>[] }[];
  diagnostics: EffectPackageDiagnostic[];
  protocolVersion: '1';
}

/** 原子采用的完整目标配置；包和源码身份由服务从草稿及采用请求派生。 */
export interface AuthorPackageConfigurationInput {
  name: string;
  exportId: string;
  /** 保留 JSON 原文；不能经 JS Number 往返。 */
  parametersJson: string;
  relativeTarget: EffectRecipeTarget;
  readScope: EffectReadScope;
  branchInput: string | null;
  fixedSeed: string | null;
  overlayLayer: EffectOverlayLayer;
}
