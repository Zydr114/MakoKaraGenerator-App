// 字体目录与整形检查（合同 1.36.0，规范 §6.12）：读的是实际渲染 Worker 的字体环境，不安装字体，也不修改样式。

import type { Revision } from './int64';

/** GET /fonts：Worker 环境里的全部字体族；family 查询参数不过滤目录。 */
export interface FontListResponse {
  families: string[];
  /** 例如 worker-skia。 */
  environment: string;
  protocolVersion: '1';
}

/** POST /fonts/inspect 的八个字段都必填；sessionId 非 null 时 expectedRevision 必须对上。 */
export interface FontInspectInput {
  /** 最多 100000 个 UTF-16 单元。 */
  text: string;
  /** null 表示后端默认字体。 */
  family: string | null;
  fontSizePx: number;
  /** 100..900。 */
  fontWeight: number;
  italic: boolean;
  /** 最多 32 个。 */
  fallbackFamilies: string[] | null;
  sessionId: string | null;
  expectedRevision: Revision | null;
}

export interface FontElementDiagnostic {
  elementIndex: number;
  utf16Start: number;
  text: string;
  actualFamily: string;
  isFallback: boolean;
  hasMissingGlyph: boolean;
}

export interface FontInspectResponse {
  /** 请求的字体族不在环境里时为 false，elements 为空。 */
  requestedAvailable: boolean;
  resolvedFamily: string;
  availableWeights: number[];
  supportsItalic: boolean;
  /** 逐元素的实际字体、是否回退、是否缺字。 */
  elements: FontElementDiagnostic[];
  environment: string;
  protocolVersion: '1';
}
