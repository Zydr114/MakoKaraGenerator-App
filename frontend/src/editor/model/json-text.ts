// 包参数的无损 JSON 文本（设计 W7 §2.1）：parametersJson 是对象 JSON 的字符串，时长等整数可以到 Int64 最大值，
// 先 JSON.parse 再 stringify 会丢精度。这里只把顶层拆成“键 → 值的原文”，数值比较也按十进制原文做，不经过 JS Number。

const NUMBER = /^-?(0|[1-9]\d*)(\.\d+)?([eE][+-]?\d+)?$/;

/** 拆开对象 JSON 的顶层，值保留原文（去掉两侧空白）。不是对象、语法错误或键重复时抛错。 */
export function parseObjectEntries(text: string): Map<string, string> {
  const parsed: unknown = JSON.parse(text);
  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) throw new SyntaxError('参数必须是 JSON 对象。');
  const entries = new Map<string, string>();
  let i = skipSpace(text, 0) + 1;
  i = skipSpace(text, i);
  if (text[i] === '}') return entries;
  for (;;) {
    const keyEnd = scanString(text, i);
    const key = JSON.parse(text.slice(i, keyEnd)) as string;
    i = skipSpace(text, keyEnd) + 1;
    const valueStart = skipSpace(text, i);
    const valueEnd = scanValue(text, valueStart);
    if (entries.has(key)) throw new SyntaxError(`参数 ${key} 重复。`);
    entries.set(key, text.slice(valueStart, valueEnd));
    i = skipSpace(text, valueEnd);
    if (text[i] === '}') return entries;
    i = skipSpace(text, i + 1);
  }
}

/** 按给定顺序拼回对象 JSON；值原样写入，调用方保证每个值本身是合法 JSON。 */
export function stringifyObjectEntries(entries: Iterable<readonly [string, string]>): string {
  const parts: string[] = [];
  for (const [key, raw] of entries) parts.push(`${JSON.stringify(key)}:${raw}`);
  return `{${parts.join(',')}}`;
}

/** 改一个键（raw 为 undefined 时删掉）并按键名排序拼回，与后端的规范形式一致。 */
export function withEntry(text: string, key: string, raw: string | undefined): string {
  const entries = parseObjectEntries(text);
  if (raw === undefined) entries.delete(key);
  else entries.set(key, raw);
  return stringifyObjectEntries([...entries].sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)));
}

export function isJsonNumber(raw: string): boolean {
  return NUMBER.test(raw.trim());
}

/** 没有小数部分的数值原文（1e3、1.50e2 这类按数值算也算整数）。 */
export function isJsonInteger(raw: string): boolean {
  const d = decimal(raw);
  return d !== null && d.exp >= 0;
}

/** 比较两个 JSON 数值原文的大小（精确，不经过 JS Number）；不是数值时抛错。 */
export function compareJsonNumbers(a: string, b: string): -1 | 0 | 1 {
  const x = decimal(a);
  const y = decimal(b);
  if (!x || !y) throw new SyntaxError(`不是数值：${!x ? a : b}`);
  if (x.sign !== y.sign) return x.sign < y.sign ? -1 : 1;
  if (x.sign === 0) return 0;
  const magnitude = compareMagnitude(x, y);
  return (x.sign > 0 ? magnitude : -magnitude) as -1 | 0 | 1;
}

/** 两个值原文表示的是否同一个 JSON 值：数值精确比较，对象不看键序。 */
export function sameJsonValue(a: string, b: string): boolean {
  if (isJsonNumber(a) && isJsonNumber(b)) return compareJsonNumbers(a, b) === 0;
  return deepEqual(JSON.parse(a), JSON.parse(b));
}

// ── 内部 ──────────────────────────────────────────────

/** 值 = sign × 有效数字 × 10^exp；digits 去掉了首尾的 0。 */
interface Decimal {
  sign: -1 | 0 | 1;
  digits: string;
  exp: number;
}

function decimal(raw: string): Decimal | null {
  const m = NUMBER.exec(raw.trim());
  if (!m) return null;
  const fraction = m[2] ? m[2].slice(1) : '';
  let digits = m[1]! + fraction;
  let exp = (m[3] ? Number(m[3].slice(1)) : 0) - fraction.length;
  digits = digits.replace(/^0+/, '');
  if (digits === '') return { sign: 0, digits: '', exp: 0 };
  const trimmed = digits.replace(/0+$/, '');
  exp += digits.length - trimmed.length;
  return { sign: raw.trim().startsWith('-') ? -1 : 1, digits: trimmed, exp };
}

function compareMagnitude(x: Decimal, y: Decimal): -1 | 0 | 1 {
  // 先比最高位所在的数量级，相同时补零后逐位比较。
  const mx = x.digits.length + x.exp;
  const my = y.digits.length + y.exp;
  if (mx !== my) return mx < my ? -1 : 1;
  const length = Math.max(x.digits.length, y.digits.length);
  const dx = x.digits.padEnd(length, '0');
  const dy = y.digits.padEnd(length, '0');
  return dx === dy ? 0 : dx < dy ? -1 : 1;
}

function deepEqual(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (typeof a !== 'object' || typeof b !== 'object' || a === null || b === null) return false;
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  const ka = Object.keys(a);
  const kb = Object.keys(b);
  return ka.length === kb.length && ka.every((k) => Object.hasOwn(b, k) && deepEqual((a as Record<string, unknown>)[k], (b as Record<string, unknown>)[k]));
}

function skipSpace(text: string, i: number): number {
  while (i < text.length && ' \t\n\r'.includes(text[i]!)) i++;
  return i;
}

/** text[i] 是引号；返回字符串结束后的位置。 */
function scanString(text: string, i: number): number {
  for (i++; i < text.length; i++) {
    if (text[i] === '\\') i++;
    else if (text[i] === '"') return i + 1;
  }
  return i;
}

/** 已知整段是合法 JSON，只需找到值的结尾。 */
function scanValue(text: string, i: number): number {
  const c = text[i];
  if (c === '"') return scanString(text, i);
  if (c === '{' || c === '[') {
    let depth = 0;
    for (; i < text.length; i++) {
      const ch = text[i];
      if (ch === '"') i = scanString(text, i) - 1;
      else if (ch === '{' || ch === '[') depth++;
      else if (ch === '}' || ch === ']') {
        depth--;
        if (depth === 0) return i + 1;
      }
    }
    return i;
  }
  while (i < text.length && !',}] \t\n\r'.includes(text[i]!)) i++;
  return i;
}
