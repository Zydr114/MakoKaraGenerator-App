import { describe, expect, it } from 'vitest';
import { BackendError, NetworkError, decideRetry, retryDelayMs } from '../../src/client';

const err = (status: number, code?: string) =>
  new BackendError(status, code ? { code, message: code, protocolVersion: '1' } : undefined);

describe('重试决策表（设计 §3.3）', () => {
  it.each([
    ['网络错误（有副作用）', new NetworkError('x'), true, 'retry'],
    ['网络错误（只读）', new NetworkError('x'), false, 'retry'],
    ['401 clientTokenExpired', err(401, 'clientTokenExpired'), true, 'resume'],
    ['401 unauthorizedClient', err(401, 'unauthorizedClient'), true, 'reauthenticate'],
    ['409 hostRestarted', err(409, 'hostRestarted'), true, 'hostRestarted'],
    ['500 internalError（有副作用）', err(500, 'internalError'), true, 'unknownOutcome'],
    ['500 internalError（只读）', err(500, 'internalError'), false, 'retry'],
    ['409 requestIdReuse', err(409, 'requestIdReuse'), true, 'fail'],
    ['409 revisionConflict', err(409, 'revisionConflict'), true, 'fail'],
    ['429 requestQuotaExceeded', err(429, 'requestQuotaExceeded'), true, 'fail'],
    ['503 taskUnavailable', err(503, 'taskUnavailable'), true, 'fail'],
    ['416 无正文', err(416), false, 'fail'],
    ['普通异常', new Error('bug'), true, 'fail'],
  ] as const)('%s → %s', (_name, error, sideEffect, expected) => {
    expect(decideRetry(error, sideEffect)).toBe(expected);
  });

  it('退避从 250ms 翻倍，上限 2 秒', () => {
    expect([0, 1, 2, 3, 4, 10].map(retryDelayMs)).toEqual([250, 500, 1000, 2000, 2000, 2000]);
  });
});
