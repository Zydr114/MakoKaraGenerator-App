// 作者编译诊断（W8 设计 §2.6）：列表的排序与去重、结果摘要、诊断在源码里的位置。纯 TS。

import type { AuthorCompilation, AuthorCompilationDiagnostic } from '../../protocol';

export interface CompileRow extends AuthorCompilationDiagnostic {
  /** path:line:col；没有路径时为空。 */
  location: string;
}

/** 编辑器里的一个诊断标记（行、列从 1 开始）。 */
export interface SourceMarker {
  line: number;
  column: number;
  severity: 'error' | 'warning';
  message: string;
}

/** 错误在前，再按路径、行、列；完全相同的诊断只留一条。 */
export function compileRows(diagnostics: readonly AuthorCompilationDiagnostic[]): CompileRow[] {
  const seen = new Set<string>();
  const rows: CompileRow[] = [];
  for (const d of diagnostics) {
    const key = [d.isError, d.code, d.path ?? '', d.line, d.column, d.message].join('\u0000');
    if (seen.has(key)) continue;
    seen.add(key);
    rows.push({ ...d, location: d.path ? (d.line > 0 && d.column > 0 ? `${d.path}:${d.line}:${d.column}` : d.path) : '' });
  }
  return rows.sort(
    (a, b) => Number(b.isError) - Number(a.isError) || (a.path ?? '').localeCompare(b.path ?? '') || a.line - b.line || a.column - b.column,
  );
}

/** “通过”“失败 · 2 个错误、1 个警告”。 */
export function compileOutcomeText(result: Pick<AuthorCompilation, 'succeeded' | 'diagnostics'>): string {
  const rows = compileRows(result.diagnostics);
  const errors = rows.filter((d) => d.isError).length;
  const warnings = rows.length - errors;
  const counts = [errors ? `${errors} 个错误` : '', warnings ? `${warnings} 个警告` : ''].filter(Boolean).join('、');
  return `${result.succeeded ? '通过' : '失败'}${counts ? ` · ${counts}` : ''}`;
}

/**
 * 诊断在文本里的范围：行、列从 1 开始（越界时夹到文本里）。
 * 从那一列起标到标识符结尾，至少一个字符；在行尾时往前标一个字符。
 */
export function diagnosticRange(text: string, line: number, column: number): { from: number; to: number } {
  const lines = text.split('\n');
  const index = Math.min(Math.max(line, 1), lines.length) - 1;
  let start = 0;
  for (let i = 0; i < index; i++) start += lines[i]!.length + 1;
  const content = lines[index]!;
  const offset = Math.min(Math.max(column, 1) - 1, content.length);
  let end = offset;
  while (end < content.length && /[\p{L}\p{N}_]/u.test(content[end]!)) end++;
  if (end === offset) end = Math.min(offset + 1, content.length);
  if (end === offset && offset > 0) return { from: start + offset - 1, to: start + offset };
  return { from: start + offset, to: start + end };
}
