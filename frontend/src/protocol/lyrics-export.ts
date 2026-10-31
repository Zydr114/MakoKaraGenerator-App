// 歌词文件导出（规范 §17.3，合同 1.25.0／1.27.0）：先评估得到固定快照与损失计划，再按计划导出，最后认证下载。
// 对应 BackendContracts.cs（LyricsExportPlanRequest／Response、LyricsExportRequest／Response、LyricsExportOptionsDto）。
// 评估、导出、下载都不推进工程历史；计划与产物保留 300 秒。

import type { IsoDateTime } from './common';
import type { Revision } from './int64';

export const LYRICS_EXPORT_FORMATS = ['lrc.plain', 'lrc.enhanced', 'ttml.project', 'ttml.amll'] as const;
export type LyricsExportFormat = (typeof LYRICS_EXPORT_FORMATS)[number];

/** branch 导出一个来源（必须给 branchId）；document 只用于 ttml.project，导出整个工程的全部来源（branchId 必须省略）。 */
export type LyricsExportScope = 'branch' | 'document';

/** 标题／作者／专辑不能含控制字符与 `]`，最多 1024 个字符。 */
export interface LyricsExportOptions {
  title?: string | null;
  artist?: string | null;
  album?: string | null;
  /** 省略为 "\n"。 */
  newLine?: '\n' | '\r\n';
  writeByteOrderMark?: boolean;
  millisecondPrecision?: boolean;
}

export interface LyricsExportPlanInput {
  format: LyricsExportFormat;
  options: LyricsExportOptions;
  scope?: LyricsExportScope;
  branchId?: string;
}

export interface LyricsExportLoss {
  feature: string;
  affectedCount: number;
  message: string;
}

export interface LyricsExportIssue {
  kind: string;
  lineId: string;
  message: string;
}

export interface LyricsExportAssessment {
  losses: LyricsExportLoss[];
  exportableLineCount: number;
  skippedLineCount: number;
  issues: LyricsExportIssue[];
  hasLosses: boolean;
}

export interface LyricsExportPlan {
  planId: string;
  projectSessionId: string;
  projectRevision: Revision;
  /** document 范围时为 null。 */
  branchId: string | null;
  format: LyricsExportFormat;
  options: Required<Pick<LyricsExportOptions, 'newLine' | 'writeByteOrderMark' | 'millisecondPrecision'>> &
    Pick<LyricsExportOptions, 'title' | 'artist' | 'album'>;
  encoding: 'utf-8' | 'utf-8-bom';
  expiresAtUtc: IsoDateTime;
  plan: LyricsExportAssessment;
  /** 注音、行／元素终点、主行关联、非法时间等补充损失。 */
  additionalLosses: LyricsExportLoss[];
  /** 为 true 时导出必须 acceptLosses。 */
  requiresLossAcceptance: boolean;
  /** branch 范围时省略了几个其他来源。 */
  omittedBranchCount: number;
  scope: LyricsExportScope;
  protocolVersion: '1';
}

export interface LyricsExportInput {
  planId: string;
  acceptLosses: boolean;
}

export interface LyricsExportReport {
  exporterId: string;
  exporterName: string;
  sourceLineCount: number;
  exportedLineCount: number;
  skippedLineCount: number;
  droppedTokenTimeCount: number;
  roundedTimeCount: number;
  writtenByteCount: number;
  metadataCount: number;
  plan: LyricsExportAssessment;
  writtenRubySegmentCount: number;
  skippedRubySegmentCount: number;
  droppedRubySegmentTimeCount: number;
}

export interface LyricsExportResponse {
  exportId: string;
  planId: string;
  projectSessionId: string;
  projectRevision: Revision;
  branchId: string | null;
  format: LyricsExportFormat;
  encoding: 'utf-8' | 'utf-8-bom';
  sizeBytes: number;
  sha256: string;
  /** 需要认证读取；不是公开 URL。 */
  contentUrl: string;
  expiresAtUtc: IsoDateTime;
  report: LyricsExportReport;
  additionalLosses: LyricsExportLoss[];
  scope: LyricsExportScope;
  protocolVersion: '1';
}
