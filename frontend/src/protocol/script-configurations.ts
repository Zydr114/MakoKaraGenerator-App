// 个人配置库（界面叫“效果组件库”，合同 1.38.0，规范 §21）：属于后端 profile，Server 重启和别的 client 都能看到。
// 库修订：还没有内容时是 'missing'，之后是 64 位小写十六进制；所有命令带 expectedRevision 做 CAS，详情要带精确修订。

import type { AuthorDraftConfigurationInput, ScriptConfigurationFields, ScriptConfigurationKind } from './author';
import type { EffectPackageInspectResponse, EffectPackageReference } from './effect-packages';

/** 摘要里没有包的名称和版本号，要从详情读。 */
export interface ScriptConfigurationSummary {
  configurationId: string;
  name: string;
  kind: ScriptConfigurationKind;
  packageId: string;
  packageRevision: string;
  sourceRevision: string;
  exportId: string;
  bindingKinds: string[];
}

export interface ScriptConfigurationListResponse {
  revision: string;
  configurations: ScriptConfigurationSummary[];
  protocolVersion: '1';
}

export interface ScriptConfiguration extends ScriptConfigurationFields {
  configurationId: string;
}

/** package.reference 是 configuration 引用，configurationRevision 就是这里的库修订；库一改就过期（409）。 */
export interface ScriptConfigurationDetailResponse {
  revision: string;
  configuration: ScriptConfiguration;
  package: EffectPackageInspectResponse;
  protocolVersion: '1';
}

/** 新建或替换时的来源，三选一：草稿的一个修订、库里的另一个配置、包引用（例如刚上传的 asset 引用）。 */
export type ScriptConfigurationSource =
  | { draftId: string; expectedDraftRevision: string; sourceRevision: string }
  | { configurationId: string; expectedConfigurationRevision: string }
  | { package: EffectPackageReference };

/** 写法：可空字段显式写 null。包 ID 和源码修订由来源决定。 */
export type ScriptConfigurationInput = Omit<AuthorDraftConfigurationInput, 'packageId' | 'sourceRevision'> & {
  source: ScriptConfigurationSource;
};

export type ScriptConfigurationCommand =
  | { type: 'create'; configuration: ScriptConfigurationInput }
  | { type: 'replace'; configurationId: string; configuration: ScriptConfigurationInput }
  /** 允许重名。 */
  | { type: 'copy'; configurationId: string; name: string }
  | { type: 'rename'; configurationId: string; name: string }
  | { type: 'delete'; configurationId: string };

export interface ScriptConfigurationCommandResponse {
  previousRevision: string;
  revision: string;
  changed: boolean;
  /** create／copy 时是新配置的 ID。 */
  configurationId: string;
  previousConfigurationId?: string;
  protocolVersion: '1';
}
