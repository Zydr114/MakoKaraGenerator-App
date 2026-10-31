// 公共小类型：回执、日期、诊断。
// 对应 BackendContracts.cs（RequestMetadataDto、BackendDiagnosticDto 等）。

/** ISO 8601 DateTimeOffset 字符串；后端可能给 7 位小数与 +00:00 偏移。 */
export type IsoDateTime = string;

/** 有副作用请求的回执三元组（规范 §1.2）。放置位置随路由不同，由客户端库决定。 */
export interface RequestMetadata {
  requestId: string;
  hostInstanceId: string;
  validUntilUtc: IsoDateTime;
}

/**
 * 规范 §8：severity 是普通字符串，不是 enum。持久化诊断用 Info/Warning/Error，
 * Worker 运行时用小写；统一用 normalizeSeverity 处理。
 */
export type DiagnosticSeverity = 'Info' | 'Warning' | 'Error' | 'info' | 'warning' | 'error' | (string & {});

export interface BackendDiagnosticOccurrence {
  epoch: string;
  sequence: string;
  instanceId: string;
  artifactHash: string;
  stage: string;
  timeUs: string;
  location?: { path: string; line: number; column: number };
  package?: { packageId: string; revision: string; exportId: string };
}

export interface BackendDiagnostic {
  code: string;
  severity: DiagnosticSeverity;
  message: string;
  location?: string;
  originKind?: string;
  occurrence?: BackendDiagnosticOccurrence;
  /** originKind=compileSource 时的编译诊断（后端规范 §20.3）；行列从 1 开始，0 也合法。 */
  source?: BackendSourceDiagnostic;
}

export interface BackendSourceDiagnostic {
  packageId: string;
  revision: string;
  instanceId?: string;
  exportId?: string;
  code: string;
  message: string;
  isError: boolean;
  path?: string;
  line: number;
  column: number;
}

export function normalizeSeverity(severity: DiagnosticSeverity): 'info' | 'warning' | 'error' | 'unknown' {
  const lower = severity.toLowerCase();
  return lower === 'info' || lower === 'warning' || lower === 'error' ? lower : 'unknown';
}
