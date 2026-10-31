// 完整 .mktpl 归档的导入与导出（合同 1.37.0，规范 §17.5）。
// 导入分两步：先上传资源做预检（只读），再凭 preflightId 导入；preflightId 用一次就作废，库修订变了 409。
// 每个客户端同时最多保留 2 个传输对象，各 5 分钟；第三个 429 resourceQuotaExceeded（G32）。

import type { TemplateSummary } from './templates';

export type TemplateTransferAvailability = 'staticAvailable' | 'advancedRuntimeAvailable' | 'advancedUnsupported';

export interface TemplateImportPreflightResponse {
  preflightId: string;
  assetSha256: string;
  archiveBytes: number;
  expandedBytes: number;
  /** 1..100 个。内置模板导出后 ID 带 exported:builtin: 前缀。 */
  templates: { summary: TemplateSummary; definitionAvailability: TemplateTransferAvailability }[];
  expiresAtUtc: string;
  /** 导入总是生成新 ID，不覆盖已有模板。 */
  identityPolicy: 'generateNewIds';
  protocolVersion: '1';
}

export interface TemplateImportResponse {
  libraryId: string;
  previousRevision: string;
  revision: string;
  changed: boolean;
  importedTemplates: { sourceTemplateId: string; templateId: string }[];
  protocolVersion: '1';
}

/** 导出结果：归档用 contentUrl（GET /template-transfers/{exportId}/archive）认证读取，过期后 404。 */
export interface TemplateArchiveExportResponse {
  exportId: string;
  libraryId: string;
  revision: string;
  templateIds: string[];
  sizeBytes: number;
  sha256: string;
  contentUrl: string;
  expiresAtUtc: string;
  protocolVersion: '1';
}
