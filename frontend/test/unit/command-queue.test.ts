import { describe, expect, it } from 'vitest';
import { BackendError, CommandDiscardedError, ProjectCommandQueue } from '../../src/client';
import { revision, type Revision } from '../../src/protocol';

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

const conflict = () => new BackendError(409, { code: 'revisionConflict', message: 'x', protocolVersion: '1' });

describe('ProjectCommandQueue', () => {
  it('同一工程串行执行，并用上一条结果的修订作为下一条的 expectedRevision', async () => {
    const queue = new ProjectCommandQueue({ readRevision: async () => revision('0') });
    const seen: string[] = [];
    let inFlight = 0;
    let maxInFlight = 0;
    const op = (next: string) => (expected: Revision) => {
      seen.push(expected);
      inFlight++;
      maxInFlight = Math.max(maxInFlight, inFlight);
      return new Promise<{ rev: Revision }>((resolve) =>
        setTimeout(() => {
          inFlight--;
          resolve({ rev: revision(next) });
        }, 5),
      );
    };
    await Promise.all([
      queue.enqueue('p', op('1'), (r) => r.rev),
      queue.enqueue('p', op('2'), (r) => r.rev),
      queue.enqueue('p', op('3'), (r) => r.rev),
    ]);
    expect(seen).toEqual(['0', '1', '2']);
    expect(maxInFlight).toBe(1);
    expect(queue.knownRevision('p')).toBe('3');
  });

  it('不同工程互不阻塞', async () => {
    const queue = new ProjectCommandQueue({ readRevision: async () => revision('0') });
    const gate = deferred<void>();
    const a = queue.enqueue('a', () => gate.promise);
    const b = queue.enqueue('b', async () => 'b-done');
    await expect(b).resolves.toBe('b-done');
    gate.resolve();
    await a;
  });

  it('修订只单调前进：迟到的旧修订不覆盖新修订', () => {
    const queue = new ProjectCommandQueue({ readRevision: async () => revision('0') });
    queue.observeRevision('p', revision('5'));
    queue.observeRevision('p', revision('3'));
    expect(queue.knownRevision('p')).toBe('5');
    queue.observeRevision('p', revision('10'));
    expect(queue.knownRevision('p')).toBe('10');
  });

  it('changed:false（nextRevision 返回 undefined）不更新修订', async () => {
    const queue = new ProjectCommandQueue({ readRevision: async () => revision('4') });
    await queue.enqueue('p', async () => ({ changed: false }), () => undefined);
    expect(queue.knownRevision('p')).toBe('4');
  });

  it('revisionConflict：抛给调用方，并清空尚未发出的命令', async () => {
    const queue = new ProjectCommandQueue({ readRevision: async () => revision('0') });
    const first = deferred<void>();
    const sent: string[] = [];
    const a = queue.enqueue('p', async () => {
      sent.push('a');
      await first.promise;
      throw conflict();
    });
    const b = queue.enqueue('p', async () => void sent.push('b'));
    const c = queue.enqueue('p', async () => void sent.push('c'));
    first.resolve();
    await expect(a).rejects.toMatchObject({ code: 'revisionConflict' });
    await expect(b).rejects.toBeInstanceOf(CommandDiscardedError);
    await expect(c).rejects.toBeInstanceOf(CommandDiscardedError);
    expect(sent).toEqual(['a']);
    // 队列可以继续使用。
    await expect(queue.enqueue('p', async () => 'after')).resolves.toBe('after');
  });

  it('不知道修订时先读取工程摘要', async () => {
    let reads = 0;
    const queue = new ProjectCommandQueue({
      readRevision: async () => {
        reads++;
        return revision('7');
      },
    });
    const expected = await queue.enqueue('p', async (rev) => rev);
    expect(expected).toBe('7');
    await queue.enqueue('p', async (rev) => rev);
    expect(reads).toBe(1);
  });

  it('其他错误不影响后续命令', async () => {
    const queue = new ProjectCommandQueue({ readRevision: async () => revision('0') });
    const a = queue.enqueue('p', async () => {
      throw new BackendError(422, { code: 'commandRejected', message: 'x', protocolVersion: '1' });
    });
    const b = queue.enqueue('p', async () => 'ok');
    await expect(a).rejects.toMatchObject({ code: 'commandRejected' });
    await expect(b).resolves.toBe('ok');
  });
});
