// 工程内显式预设分配的四种命令（合同 1.10.0 起，规范 §13.3）、分配读模型（G15，合同 1.12.0，规范 §13.4）与候选试看（G16，合同 1.15.0，规范 §13.6）。
// 模板快照由 Host 在工程 Writer 外按 templateRevision 固定；库以后改名、替换或删除都不影响已写入的分配。

import type { Revision } from './int64';
import type { PreviewAlphaMode, PreviewSize, RenderBackend } from './preview';
import type { TemplateKind, TemplatePreviewSource, TemplateProperty } from './templates';

export type TemplateLibraryId = 'builtin' | 'personal';

/**
 * 前四类与 StyleTarget 同形。1.24.0 加了 node：编排节点（包括标记），includeDescendants 必须给出；
 * 文本单元类模板不能用于节点，不带 branchInputs。1.37.0 加了 clips：同一来源的 1～1000 个片段，高级模板也可以用于节点。
 */
export type PresetApplicationTarget =
  | { kind: 'project' }
  | { kind: 'branch'; branchId: string }
  | { kind: 'line'; branchId: string; lineId: string }
  | { kind: 'token'; branchId: string; lineId: string; tokenId: string }
  | { kind: 'clips'; branchId: string; clipIds: string[] }
  | { kind: 'node'; nodeId: string; includeDescendants: boolean };

export type PresetApplicationCommand =
  | {
      type: 'presetApplications.apply';
      templateLibraryId: TemplateLibraryId;
      templateId: string;
      templateRevision: string;
      /** 1..1000 个不重复目标，整批成功或整批拒绝。 */
      targets: PresetApplicationTarget[];
      branchInputs?: Record<string, string> | null;
    }
  | {
      /** 只能换同一模板身份的新修订；换模板要先 remove 再 apply。 */
      type: 'presetApplications.update';
      applicationId: string;
      templateLibraryId: TemplateLibraryId;
      templateId: string;
      templateRevision: string;
      branchInputs?: Record<string, string> | null;
    }
  | { type: 'presetApplications.setEnabled'; applicationId: string; enabled: boolean }
  | { type: 'presetApplications.remove'; applicationIds: string[] };

/** 分配命令成功回执里的分配 ID：apply 是新建或沿用的分配，其余是受影响的分配。 */
export interface PresetApplicationCommandResult {
  applicationIds: string[];
}

/**
 * 读模型里的目标：除四类可写目标外，旧工程可能带 node／clips 绑定，只读返回。
 * 行和文本单元只按 ID 绑定：目标失效（targetValid=false）时找不到来源，branchId 等会缺省。
 */
export interface PresetApplicationTargetRead {
  kind: 'project' | 'branch' | 'line' | 'token' | 'node' | 'clips' | (string & {});
  branchId?: string;
  lineId?: string;
  tokenId?: string;
  nodeId?: string;
  clipIds?: string[];
  includeDescendants: boolean;
}

/** 写入工程的固定模板快照的摘要。 */
export interface PresetApplicationSnapshot {
  templateId: string;
  name: string;
  kind: TemplateKind;
  properties: TemplateProperty[];
  hasEffectRecipes: boolean;
  hasPackageMaterials: boolean;
  hasPackages: boolean;
}

/** 候选试看里模板从哪里来：库里固定修订的模板（旧四字段），或 source（库模板或静态／完整草稿，合同 1.37.0）。 */
export type PresetCandidateTemplate =
  | { templateLibraryId: TemplateLibraryId; templateLibraryRevision: string; templateId: string; templateRevision: string }
  | { source: TemplatePreviewSource };

/**
 * 模板候选试看（G16，合同 1.15.0，规范 §13.6；source 见 §24.3）：在精确的工程修订上只读试看一次分配，返回一个预览会话。
 * 不写工程历史、不改模板库；修订对不上 409，未知库或模板 404。
 */
export type PresetApplicationPreviewInput = PresetCandidateTemplate & {
  expectedProjectRevision: Revision;
  /** 1～1000 个：project／branch／line／token／clips／node。 */
  targets: PresetApplicationTarget[];
  branchInputs?: Record<string, string> | null;
  size: PreviewSize;
  alphaMode?: PreviewAlphaMode;
  /** 省略或 null 用启动默认。 */
  renderBackend?: RenderBackend | null;
};

/** 快照的来源库。工程还没有记录来源，始终 comparable=false、其余为 null；1.14.0 起三个可空字段总是返回（R3）。 */
export interface PresetApplicationSource {
  libraryId: TemplateLibraryId | null;
  /** 库修订（与 LibraryList.revision 同类，不是工程修订）。 */
  revision: string | null;
  comparable: boolean;
  newVersionAvailable: boolean | null;
}

export interface PresetApplication {
  applicationId: string;
  target: PresetApplicationTargetRead;
  targetValid: boolean;
  enabled: boolean;
  snapshot: PresetApplicationSnapshot;
  source: PresetApplicationSource;
  branchInputs: Record<string, string>;
}

/** GET .../preset-applications：精确修订下的全部分配，按工程里的顺序；超出预算整批 413。 */
export interface ProjectPresetApplicationsResponse {
  projectSessionId: string;
  projectRevision: Revision;
  applications: PresetApplication[];
  protocolVersion: '1';
}
