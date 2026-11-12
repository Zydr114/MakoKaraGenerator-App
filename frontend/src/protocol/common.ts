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
  /** originKind=imageEffect 时的图效果载荷（后端规范 §20.3）；导出报告原样登记的兜底诊断里，空槽位显式为 null。 */
  imageEffect?: BackendImageEffectDiagnostic | null;
  /** originKind=material 时的材质载荷（后端规范 §20.3）。 */
  material?: BackendMaterialDiagnostic | null;
}

/** 诊断指向的包资源：包、修订与导出名。 */
export interface BackendDiagnosticResource {
  packageId: string;
  revision: string;
  exportId: string;
}

export interface BackendImageEffectDiagnostic {
  instanceId?: string;
  artifactHash?: string;
  graph?: BackendDiagnosticResource;
  /** 出问题的图输入，例如缺背景时的 BackgroundMedia 输入。 */
  inputId?: string;
  code?: string;
}

export interface BackendMaterialDiagnostic {
  /** 材质的持有者：script（脚本写入）或 source（样式里的源材质）；其余字段只显示用，不展开。 */
  owner: { kind: string };
  material?: BackendDiagnosticResource;
  inputId?: string;
  code?: string;
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
