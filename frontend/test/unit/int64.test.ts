import { describe, expect, it } from 'vitest';
import {
  assertSafeJsonInteger,
  compareDecimal,
  isNewerRevision,
  micros,
  microsToNumber,
  nextSequence,
  revision,
  sequence,
} from '../../src/protocol';

describe('Int64 字符串辅助', () => {
  it('修订号只接受非负十进制字符串', () => {
    expect(revision('0')).toBe('0');
    expect(revision('9223372036854775807')).toBe('9223372036854775807');
    expect(() => revision('-1')).toThrow(RangeError);
    expect(() => revision('1.5')).toThrow(RangeError);
    expect(() => revision('9223372036854775808')).toThrow(RangeError);
    expect(() => revision('')).toThrow(RangeError);
  });

  it('微秒值可以为负，可由安全整数或 bigint 构造', () => {
    expect(micros('-1500000')).toBe('-1500000');
    expect(micros(1_500_000)).toBe('1500000');
    expect(micros(9007199254740993n)).toBe('9007199254740993');
    expect(() => micros(2 ** 53)).toThrow(RangeError);
    expect(() => micros(1.5)).toThrow(RangeError);
  });

  it('microsToNumber 超出安全整数时抛错而不是丢精度', () => {
    expect(microsToNumber(micros('9007199254740991'))).toBe(Number.MAX_SAFE_INTEGER);
    expect(microsToNumber(micros('-9007199254740991'))).toBe(Number.MIN_SAFE_INTEGER);
    expect(() => microsToNumber(micros('9007199254740992'))).toThrow(RangeError);
  });

  it('修订比较按数值而不是字典序', () => {
    expect(compareDecimal('10', '9')).toBe(1);
    expect(compareDecimal('9', '10')).toBe(-1);
    expect(compareDecimal('42', '42')).toBe(0);
    expect(compareDecimal('9223372036854775807', '9223372036854775806')).toBe(1);
    expect(isNewerRevision(revision('10'), revision('9'))).toBe(true);
    expect(isNewerRevision(revision('9'), revision('10'))).toBe(false);
    expect(isNewerRevision(revision('3'), undefined)).toBe(true);
  });

  it('序号递增不经过 number', () => {
    expect(nextSequence(undefined)).toBe('1');
    expect(nextSequence(sequence('9007199254740993'))).toBe('9007199254740994');
  });

  it('仍为 JSON number 的 Int64 字段必须是安全整数', () => {
    expect(() => assertSafeJsonInteger(3_000_000, 'x')).not.toThrow();
    expect(() => assertSafeJsonInteger(undefined, 'x')).not.toThrow();
    expect(() => assertSafeJsonInteger(JSON.parse('9007199254740993'), 'x')).toThrow(RangeError);
    expect(() => assertSafeJsonInteger('3000000', 'x')).toThrow(RangeError);
  });
});
