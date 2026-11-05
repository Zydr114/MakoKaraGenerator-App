// 效果包参数（W7 设计 §2.4）：按包的参数表读写 parametersJson、检查取值。不依赖 React。
// 取值一律是 JSON 原文（model/json-text.ts）：时长可以到 Int64 最大值，比较也按十进制原文做。
// 后端规则（Core EffectPackageParameterParser）：number 在 [min,max]；integer、duration 是整数且在 [min,max]；
// boolean 是 true／false；enum 是选项里的字符串；text 是字符串；color 是 {"r","g","b","a"}，各分量 0～1；
// 实例参数只能写导出声明过的参数（export.parameters）。curve 见 model/curves.ts；asset 是包内 role=image 文件的路径。

import type { EffectPackageExport, EffectPackageFile, EffectPackageSchema, EffectPackageParameter } from '../../protocol';
import { curveProblem } from './curves';
import { compareJsonNumbers, isJsonInteger, isJsonNumber, parseObjectEntries, sameJsonValue, withEntry } from './json-text';
import type { Rgba } from './settings';

/** 参数的当前取值原文：写了就用写的，没写就是包里的默认值。 */
export function parameterRaw(parametersJson: string, parameter: EffectPackageParameter): string {
  return entriesOf(parametersJson)?.get(parameter.id) ?? parameter.defaultValueJson;
}

export function isOverridden(parametersJson: string, id: string): boolean {
  return entriesOf(parametersJson)?.has(id) ?? false;
}

/** 改一个参数；raw 为 undefined 或等于默认值时去掉这个键，保持最小参数对象。 */
export function withParameter(parametersJson: string, parameter: EffectPackageParameter, raw: string | undefined): string {
  const same = raw !== undefined && safeSame(raw, parameter.defaultValueJson);
  return withEntry(parametersJson, parameter.id, same ? undefined : raw);
}

/** 去掉一个键（导出没有声明的参数）。 */
export function withoutParameter(parametersJson: string, id: string): string {
  return withEntry(parametersJson, id, undefined);
}

/** 一个取值的问题；合法时 undefined。图片参数给了 files 才核对文件在不在包里。 */
export function parameterProblem(parameter: EffectPackageParameter, raw: string, files?: readonly EffectPackageFile[]): string | undefined {
  let value: unknown;
  try {
    value = JSON.parse(raw);
  } catch {
    return '不是合法的 JSON。';
  }
  switch (parameter.type) {
    case 'number':
      return isJsonNumber(raw) ? rangeProblem(parameter, raw) : '请输入数字。';
    case 'integer':
    case 'duration':
      if (!isJsonNumber(raw) || !isJsonInteger(raw)) return parameter.type === 'duration' ? '时长要是整数微秒。' : '请输入整数。';
      return rangeProblem(parameter, raw);
    case 'boolean':
      return typeof value === 'boolean' ? undefined : '要是开或关。';
    case 'enum':
      return typeof value === 'string' && parameter.options.some((o) => o.value === value) ? undefined : '不在可选项里。';
    case 'text':
      return typeof value === 'string' ? undefined : '要是文字。';
    case 'color':
      return colorProblem(raw);
    case 'curve':
      return curveProblem(raw);
    case 'asset':
      if (typeof value !== 'string') return '要是包里图片的路径。';
      return !files || imageFiles(files).some((f) => f.path === value) ? undefined : `包里没有图片“${value}”。`;
  }
}

/** 包里能给图片参数用的文件（role=image）。 */
export function imageFiles(files: readonly EffectPackageFile[]): EffectPackageFile[] {
  return files.filter((f) => f.role === 'image');
}

export interface ParameterIssue {
  /** 参数 ID；导出没有声明的键也用它的键名。 */
  id: string;
  message: string;
}

/** 一个导出的整份参数：对象格式、多余的键、每个取值。 */
export function parametersIssues(parametersJson: string, pkg: EffectPackageSchema, exportId: string): ParameterIssue[] {
  const entries = entriesOf(parametersJson);
  if (!entries) return [{ id: '', message: '参数不是合法的 JSON 对象。' }];
  const declared = pkg.exports.find((e) => e.id === exportId)?.parameterIds ?? [];
  const issues: ParameterIssue[] = [];
  for (const [id, raw] of entries) {
    const parameter = declared.includes(id) ? pkg.parameters.find((p) => p.id === id) : undefined;
    if (!parameter) {
      issues.push({ id, message: `包里没有参数“${id}”。` });
      continue;
    }
    const problem = parameterProblem(parameter, raw, pkg.files);
    if (problem) issues.push({ id, message: `${parameter.label}：${problem}` });
  }
  return issues;
}

/** 导出用到的参数，按导出里的顺序。 */
export function exportParameters(pkg: EffectPackageSchema, exp: EffectPackageExport): EffectPackageParameter[] {
  return exp.parameterIds.flatMap((id) => pkg.parameters.find((p) => p.id === id) ?? []);
}

/** 范围的说明，例如“0～20 Hz”；没有范围时 undefined。 */
export function rangeText(parameter: EffectPackageParameter): string | undefined {
  if (parameter.minimumJson == null && parameter.maximumJson == null) return undefined;
  const unit = parameter.unit ? ` ${parameter.unit}` : '';
  if (parameter.type === 'duration') return `${microsToMillis(parameter.minimumJson ?? '0')}～${parameter.maximumJson ? microsToMillis(parameter.maximumJson) : '∞'} 毫秒`;
  return `${parameter.minimumJson ?? '−∞'}～${parameter.maximumJson ?? '∞'}${unit}`;
}

// ── 颜色 ──────────────────────────────────────────────

/** {"r","g","b","a"}（0～1）→ 0～255 的颜色；不是颜色时 undefined。 */
export function colorFromJson(raw: string): Rgba | undefined {
  if (colorProblem(raw)) return undefined;
  const value = JSON.parse(raw) as Record<'r' | 'g' | 'b' | 'a', number>;
  const byte = (v: number) => Math.round(v * 255);
  return { red: byte(value.r), green: byte(value.g), blue: byte(value.b), alpha: byte(value.a) };
}

/** 0～255 的颜色 → {"r","g","b","a"}，分量最多 4 位小数。 */
export function colorToJson(color: Rgba): string {
  const unit = (v: number) => String(Math.round((v / 255) * 10000) / 10000);
  return `{"r":${unit(color.red)},"g":${unit(color.green)},"b":${unit(color.blue)},"a":${unit(color.alpha)}}`;
}

// ── 时长：界面按毫秒，存微秒原文 ──────────────────────

/** 整数微秒原文 → 毫秒文本（字符串运算，不丢精度），例如 "120000" → "120"、"1500" → "1.5"。 */
export function microsToMillis(raw: string): string {
  const text = raw.trim();
  if (!/^-?\d+$/.test(text)) return text;
  const negative = text.startsWith('-');
  const digits = (negative ? text.slice(1) : text).padStart(4, '0');
  const whole = digits.slice(0, -3).replace(/^0+(?=\d)/, '');
  const fraction = digits.slice(-3).replace(/0+$/, '');
  return `${negative ? '-' : ''}${whole}${fraction ? `.${fraction}` : ''}`;
}

/** 毫秒文本（最多 3 位小数）→ 整数微秒原文；格式不对时 undefined。 */
export function millisToMicros(text: string): string | undefined {
  const match = /^(-?)(\d+)(?:\.(\d{1,3}))?$/.exec(text.trim());
  if (!match) return undefined;
  const digits = `${match[2]}${(match[3] ?? '').padEnd(3, '0')}`.replace(/^0+(?=\d)/, '');
  return digits === '0' ? '0' : `${match[1]}${digits}`;
}

// ── 种子 ──────────────────────────────────────────────

export const MAX_SEED = '18446744073709551615';

/** 固定随机种子：0～2^64−1 的十进制整数（不带符号、小数和指数）。 */
export function seedProblem(seed: string): string | undefined {
  if (!/^\d+$/.test(seed)) return '种子要是 0～18446744073709551615 的整数。';
  return compareJsonNumbers(seed, MAX_SEED) > 0 ? '种子不能大于 18446744073709551615。' : undefined;
}

// ── 内部 ──────────────────────────────────────────────

function entriesOf(parametersJson: string): Map<string, string> | undefined {
  try {
    return parseObjectEntries(parametersJson);
  } catch {
    return undefined;
  }
}

function safeSame(a: string, b: string): boolean {
  try {
    return sameJsonValue(a, b);
  } catch {
    return false;
  }
}

function rangeProblem(parameter: EffectPackageParameter, raw: string): string | undefined {
  const below = parameter.minimumJson != null && compareJsonNumbers(raw, parameter.minimumJson) < 0;
  const above = parameter.maximumJson != null && compareJsonNumbers(raw, parameter.maximumJson) > 0;
  return below || above ? `要在 ${rangeText(parameter)} 之间。` : undefined;
}

function colorProblem(raw: string): string | undefined {
  let value: unknown;
  try {
    value = JSON.parse(raw);
  } catch {
    return '不是合法的 JSON。';
  }
  if (!value || typeof value !== 'object' || Array.isArray(value)) return '颜色要写成 {"r","g","b","a"}。';
  const keys = Object.keys(value).sort().join(',');
  if (keys !== 'a,b,g,r') return '颜色要写成 {"r","g","b","a"}。';
  const parts = Object.values(value as Record<string, unknown>);
  return parts.every((v) => typeof v === 'number' && v >= 0 && v <= 1) ? undefined : '颜色分量要在 0～1 之间。';
}
