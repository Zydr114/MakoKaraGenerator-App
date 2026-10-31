// JobWatcher（设计 §3.7）：等待 202 任务到达终态。
// 订阅 job.changed／job.progress／job.terminal 事件作为提示，同时低频 GET 防止事件丢失。
// 终态 succeeded／failed／cancelled／unknown；unknown 原样返回，不当作失败也不当作成功。

import { isTerminalJobState, type BackendEvent, type JobResponse, type JobSummary } from '../protocol';
import type { Unsubscribe } from './observable';

export interface JobWatcherOptions {
  getJob: (jobId: string, signal?: AbortSignal) => Promise<JobResponse>;
  onEvent: (handler: (event: BackendEvent) => void) => Unsubscribe;
  /** 事件流 resync 后需要立即重读。 */
  onResync: (handler: () => void) => Unsubscribe;
  pollIntervalMs?: number;
}

export interface WaitForJobOptions {
  signal?: AbortSignal;
  /** 每次读到新状态（事件摘要或 GET 结果）时回调。 */
  onProgress?: (job: JobResponse | JobSummary) => void;
  timeoutMs?: number;
}

export class JobWatcher {
  private readonly options: JobWatcherOptions;

  constructor(options: JobWatcherOptions) {
    this.options = options;
  }

  waitForTerminal(jobId: string, wait: WaitForJobOptions = {}): Promise<JobResponse> {
    const { getJob, onEvent, onResync } = this.options;
    const pollMs = this.options.pollIntervalMs ?? 5000;

    return new Promise<JobResponse>((resolve, reject) => {
      let settled = false;
      let checking = false;
      let again = false;
      const cleanups: (() => void)[] = [];
      const finish = (fn: () => void) => {
        if (settled) return;
        settled = true;
        for (const cleanup of cleanups) cleanup();
        fn();
      };

      const check = async (): Promise<void> => {
        if (settled) return;
        if (checking) {
          again = true;
          return;
        }
        checking = true;
        try {
          do {
            again = false;
            const job = await getJob(jobId, wait.signal);
            if (settled) return;
            wait.onProgress?.(job);
            if (isTerminalJobState(job.state)) {
              finish(() => resolve(job));
              return;
            }
          } while (again);
        } catch (error) {
          finish(() => reject(error));
        } finally {
          checking = false;
        }
      };

      cleanups.push(
        onEvent((event) => {
          if (event.job?.jobId !== jobId) return;
          wait.onProgress?.(event.job);
          // 终态或状态变化时读取权威完整 JobResponse；进度事件只转发摘要。
          if (event.type !== 'job.progress' || isTerminalJobState(event.job.state)) void check();
        }),
        onResync(() => void check()),
      );
      const timer = setInterval(() => void check(), pollMs);
      cleanups.push(() => clearInterval(timer));
      if (wait.timeoutMs !== undefined) {
        const timeout = setTimeout(
          () => finish(() => reject(new Error(`等待任务 ${jobId} 超时（${wait.timeoutMs} ms）`))),
          wait.timeoutMs,
        );
        cleanups.push(() => clearTimeout(timeout));
      }
      if (wait.signal) {
        const onAbort = () => finish(() => reject(wait.signal?.reason ?? new DOMException('已中止', 'AbortError')));
        if (wait.signal.aborted) return onAbort();
        wait.signal.addEventListener('abort', onAbort, { once: true });
        cleanups.push(() => wait.signal?.removeEventListener('abort', onAbort));
      }
      void check();
    });
  }
}
