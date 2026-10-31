import { describe, expect, it } from 'vitest';
import { ReceiptFactory, ServerClock, toMetadata } from '../../src/client';

describe('ServerClock', () => {
  it('按 serverTimeUtc 计算偏移，接受 7 位小数与 +00:00', () => {
    let local = Date.parse('2026-10-16T10:00:00Z');
    const clock = new ServerClock(() => local);
    expect(clock.isSynced).toBe(false);
    clock.observe('2026-10-16T10:00:30.1234567+00:00');
    expect(clock.isSynced).toBe(true);
    expect(clock.offset).toBe(30_123);
    local += 1000;
    expect(new Date(clock.now()).toISOString()).toBe('2026-10-16T10:00:31.123Z');
  });

  it('忽略无法解析的时间', () => {
    const clock = new ServerClock(() => 0);
    clock.observe('not-a-date');
    expect(clock.isSynced).toBe(false);
    expect(clock.offset).toBe(0);
  });
});

describe('ReceiptFactory', () => {
  const local = Date.parse('2026-10-16T10:00:00Z');

  it('截止时间用服务端时间 + 60 秒，而不是本地时间', () => {
    const clock = new ServerClock(() => local);
    clock.observe('2026-10-16T12:00:00Z'); // 服务端比本地快两小时
    const factory = new ReceiptFactory(clock, () => 'host-H', { randomId: () => 'uuid' });
    const receipt = factory.create('cmd');
    expect(receipt.requestId).toBe('cmd-uuid');
    expect(receipt.hostInstanceId).toBe('host-H');
    expect(receipt.validUntilUtc).toBe('2026-10-16T12:01:00.000Z');
    expect(toMetadata(receipt)).toEqual({ requestId: 'cmd-uuid', hostInstanceId: 'host-H', validUntilUtc: '2026-10-16T12:01:00.000Z' });
  });

  it('窗口不超过 capabilities.maxCommandRetryWindowSeconds', () => {
    const clock = new ServerClock(() => local);
    clock.observe('2026-10-16T10:00:00Z');
    const factory = new ReceiptFactory(clock, () => 'h', { maxWindowSeconds: () => 30, randomId: () => 'x' });
    expect(factory.create('op').validUntilUtc).toBe('2026-10-16T10:00:30.000Z');
  });

  it('默认 requestId 带操作前缀且每次不同，长度不超过 128', () => {
    const factory = new ReceiptFactory(new ServerClock(), () => 'h');
    const a = factory.create('upload');
    const b = factory.create('upload');
    expect(a.requestId).toMatch(/^upload-[0-9a-f-]{36}$/);
    expect(a.requestId).not.toBe(b.requestId);
    expect(factory.create('x'.repeat(200)).requestId.length).toBe(128);
  });
});
