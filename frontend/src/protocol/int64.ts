// Int64 字段在协议层保持十进制字符串（设计 §5，后端规范 §1.3）。
// 修订号、游标、序号只比较相等或大小，不做算术；微秒只在明确的边界转换成 number。

declare const brand: unique symbol;
type Brand<T, B extends string> = T & { readonly [brand]: B };

/** 工程修订、预览来源修订、savedProjectRevision。非负十进制字符串。 */
export type Revision = Brand<string, 'Revision'>;
/** 预览代次。非负十进制字符串。 */
export type Generation = Brand<string, 'Generation'>;
/** 帧请求序号、事件序号、诊断序号。非负十进制字符串。 */
export type Sequence = Brand<string, 'Sequence'>;
/** 微秒时间。有符号十进制字符串。 */
export type Micros = Brand<string, 'Micros'>;

const UNSIGNED = /^[0-9]+$/;
const SIGNED = /^-?[0-9]+$/;
const INT64_MIN = -(2n ** 63n);
const INT64_MAX = 2n ** 63n - 1n;

function checkInt64(value: string, pattern: RegExp, what: string): void {
  if (!pattern.test(value)) throw new RangeError(`${what} 必须是十进制整数字符串：${JSON.stringify(value)}`);
  const big = BigInt(value);
  if (big < INT64_MIN || big > INT64_MAX) throw new RangeError(`${what} 超出 Int64 范围：${value}`);
}

export function revision(value: string): Revision {
  checkInt64(value, UNSIGNED, 'revision');
  return value as Revision;
}

export function generation(value: string): Generation {
  checkInt64(value, UNSIGNED, 'generation');
  return value as Generation;
}

export function sequence(value: string | bigint | number): Sequence {
  const text = typeof value === 'string' ? value : toIntegerString(value, 'sequence');
  checkInt64(text, UNSIGNED, 'sequence');
  return text as Sequence;
}

/** 由字符串、bigint 或安全整数构造微秒值。 */
export function micros(value: string | bigint | number): Micros {
  const text = typeof value === 'string' ? value : toIntegerString(value, 'micros');
  checkInt64(text, SIGNED, 'micros');
  return text as Micros;
}

function toIntegerString(value: bigint | number, what: string): string {
  if (typeof value === 'number' && !Number.isSafeInteger(value)) {
    throw new RangeError(`${what} 不是安全整数：${value}`);
  }
  return BigInt(value).toString();
}

/** 微秒转 number；超出 Number.MAX_SAFE_INTEGER 时抛错，不悄悄丢精度。 */
export function microsToNumber(value: Micros): number {
  const big = BigInt(value);
  if (big > BigInt(Number.MAX_SAFE_INTEGER) || big < BigInt(Number.MIN_SAFE_INTEGER)) {
    throw new RangeError(`微秒值超出 JS 安全整数范围：${value}`);
  }
  return Number(big);
}

/** 比较两个十进制整数字符串：a<b 为负，相等为 0，a>b 为正。用于同一作用域内的修订单调性检查。 */
export function compareDecimal(a: string, b: string): number {
  const x = BigInt(a);
  const y = BigInt(b);
  return x < y ? -1 : x > y ? 1 : 0;
}

/** 同一 host、同一工程会话内，candidate 是否比 current 新。current 未知时视为新。 */
export function isNewerRevision(candidate: Revision, current: Revision | undefined): boolean {
  return current === undefined || compareDecimal(candidate, current) > 0;
}

/** 下一个序号（预览 requestSeq 递增用）。 */
export function nextSequence(value: Sequence | undefined): Sequence {
  return (value === undefined ? '1' : (BigInt(value) + 1n).toString()) as Sequence;
}

/**
 * 仍为 JSON number 的两个 Int64 字段（composition.outputDurationUs、summary.audioDurationUs）。
 * JSON.parse 对超过 2^53 的整数会静默失真，因此接收时必须检查安全整数，不能保真时拒绝。
 */
export function assertSafeJsonInteger(value: unknown, what: string): void {
  if (value === undefined || value === null) return;
  if (typeof value !== 'number' || !Number.isSafeInteger(value)) {
    throw new RangeError(`${what} 不是可以保真的 JSON 整数：${String(value)}`);
  }
}
