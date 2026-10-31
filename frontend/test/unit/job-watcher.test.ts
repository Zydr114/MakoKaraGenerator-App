import { describe, expect, it } from 'vitest';
import { Emitter, JobWatcher } from '../../src/client';
import type { BackendEvent, JobResponse, JobState } from '../../src/protocol';

function job(state: JobState): JobResponse {
  return {
    jobId: 'job-1',
    kind: 'save',
    state,
    expiresAtUtc: '2026-10-16T11:00:00Z',
    cancellationRequested: false,
    canCancel: false,
  };
}

function setup(states: JobState[], pollIntervalMs = 10_000) {
  const bus = new Emitter<{ event: BackendEvent; resync: undefined }>();
  let reads = 0;
  const watcher = new JobWatcher({
    getJob: async () => job(states[Math.min(reads++, states.length - 1)]!),
    onEvent: (handler) => bus.on('event', handler),
    onResync: (handler) => bus.on('resync', handler),
    pollIntervalMs,
  });
  return { bus, watcher, reads: () => reads };
}

const event = (state: JobState, type = 'job.changed'): BackendEvent => ({
  eventSeq: '1' as BackendEvent['eventSeq'],
  eventCursor: 'h:c:1',
  type,
  hostInstanceId: 'h',
  occurredAtUtc: '2026-10-16T10:00:00Z',
  job: { jobId: 'job-1', kind: 'save', state, canCancel: false, expiresAtUtc: '2026-10-16T11:00:00Z' },
});

describe('JobWatcher', () => {
  it('首次读取已是终态时直接返回', async () => {
    const { watcher } = setup(['succeeded']);
    await expect(watcher.waitForTerminal('job-1')).resolves.toMatchObject({ state: 'succeeded' });
  });

  it('job.terminal 事件触发权威 GET', async () => {
    const { watcher, bus, reads } = setup(['running', 'succeeded']);
    const waiting = watcher.waitForTerminal('job-1');
    await new Promise((r) => setTimeout(r, 5));
    expect(reads()).toBe(1);
    bus.emit('event', event('succeeded', 'job.terminal'));
    await expect(waiting).resolves.toMatchObject({ state: 'succeeded' });
    expect(reads()).toBe(2);
  });

  it('事件丢失时靠低频轮询兜底', async () => {
    const { watcher } = setup(['queued', 'running', 'failed'], 10);
    await expect(watcher.waitForTerminal('job-1')).resolves.toMatchObject({ state: 'failed' });
  });

  it('unknown 原样返回', async () => {
    const { watcher } = setup(['committing', 'unknown'], 10);
    await expect(watcher.waitForTerminal('job-1')).resolves.toMatchObject({ state: 'unknown' });
  });

  it('其他任务的事件被忽略；progress 回调收到摘要', async () => {
    const { watcher, bus } = setup(['running', 'running', 'succeeded']);
    const progress: string[] = [];
    const waiting = watcher.waitForTerminal('job-1', { onProgress: (j) => progress.push(j.state) });
    await new Promise((r) => setTimeout(r, 5));
    bus.emit('event', { ...event('succeeded'), job: { ...event('succeeded').job!, jobId: 'other' } });
    bus.emit('event', event('running', 'job.progress'));
    bus.emit('resync', undefined);
    await new Promise((r) => setTimeout(r, 5));
    bus.emit('event', event('succeeded', 'job.terminal'));
    await expect(waiting).resolves.toMatchObject({ state: 'succeeded' });
    expect(progress).toContain('running');
  });

  it('可以中止等待', async () => {
    const { watcher } = setup(['running']);
    const controller = new AbortController();
    const waiting = watcher.waitForTerminal('job-1', { signal: controller.signal });
    controller.abort();
    await expect(waiting).rejects.toBeDefined();
  });
});
