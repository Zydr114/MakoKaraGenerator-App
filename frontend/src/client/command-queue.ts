// ProjectCommandQueue（设计 §3.6，按后端审查 F4 修订）。
// - 每个 projectSessionId 一条 FIFO 队列，同时只有一个需要 expectedRevision 的操作在途。
// - 记录最近已知的 projectRevision，下一条操作用它作为 expectedRevision。
// - 修订只单调前进：同一 host、同一工程内用十进制比较，迟到的旧事件／旧响应不能把修订倒退。
// - revisionConflict：清空该工程队列中尚未发出的操作并抛给调用方；W0 不自动重放。
// - changed:false 的响应不更新修订号。

import { isNewerRevision, type Revision } from '../protocol';
import { BackendError, CommandDiscardedError } from './errors';

interface Task {
  run: (expectedRevision: Revision) => Promise<unknown>;
  resolve: (value: unknown) => void;
  reject: (error: unknown) => void;
}

interface Lane {
  revision: Revision | undefined;
  tasks: Task[];
  running: boolean;
}

export interface CommandQueueOptions {
  /** 队列还不知道修订时读取工程摘要。 */
  readRevision: (projectSessionId: string) => Promise<Revision>;
}

export class ProjectCommandQueue {
  private readonly lanes = new Map<string, Lane>();
  private readonly readRevision: (projectSessionId: string) => Promise<Revision>;

  constructor(options: CommandQueueOptions) {
    this.readRevision = options.readRevision;
  }

  /** 记录观察到的修订（创建、摘要、命令响应、project.* 事件）。只接受更新的值。 */
  observeRevision(projectSessionId: string, revision: Revision): void {
    const lane = this.lane(projectSessionId);
    if (isNewerRevision(revision, lane.revision)) lane.revision = revision;
  }

  knownRevision(projectSessionId: string): Revision | undefined {
    return this.lanes.get(projectSessionId)?.revision;
  }

  /** 工程关闭后丢弃队列状态。 */
  forget(projectSessionId: string): void {
    const lane = this.lanes.get(projectSessionId);
    if (!lane) return;
    for (const task of lane.tasks.splice(0)) task.reject(new CommandDiscardedError('工程已关闭'));
    this.lanes.delete(projectSessionId);
  }

  /** 主机重启等情况下清空全部状态。 */
  reset(reason: unknown): void {
    for (const lane of this.lanes.values()) {
      for (const task of lane.tasks.splice(0)) task.reject(new CommandDiscardedError(reason));
    }
    this.lanes.clear();
  }

  pendingCount(projectSessionId: string): number {
    const lane = this.lanes.get(projectSessionId);
    return lane ? lane.tasks.length + (lane.running ? 1 : 0) : 0;
  }

  /**
   * 串行执行一个需要 expectedRevision 的操作。run 返回后，若结果带 nextRevision（例如 changed:true
   * 的命令响应），队列据此前进。
   */
  enqueue<T>(
    projectSessionId: string,
    run: (expectedRevision: Revision) => Promise<T>,
    nextRevision?: (result: T) => Revision | undefined,
  ): Promise<T> {
    const lane = this.lane(projectSessionId);
    return new Promise<T>((resolve, reject) => {
      lane.tasks.push({
        run: async (expected) => {
          const result = await run(expected);
          const next = nextRevision?.(result);
          if (next !== undefined) this.observeRevision(projectSessionId, next);
          return result;
        },
        resolve: resolve as (value: unknown) => void,
        reject,
      });
      void this.drain(projectSessionId, lane);
    });
  }

  private async drain(projectSessionId: string, lane: Lane): Promise<void> {
    if (lane.running) return;
    lane.running = true;
    try {
      for (let task = lane.tasks.shift(); task; task = lane.tasks.shift()) {
        try {
          lane.revision ??= await this.readRevision(projectSessionId);
          task.resolve(await task.run(lane.revision));
        } catch (error) {
          task.reject(error);
          if (error instanceof BackendError && error.code === 'revisionConflict') {
            for (const pending of lane.tasks.splice(0)) pending.reject(new CommandDiscardedError(error));
          }
        }
      }
    } finally {
      lane.running = false;
    }
  }

  private lane(projectSessionId: string): Lane {
    let lane = this.lanes.get(projectSessionId);
    if (!lane) this.lanes.set(projectSessionId, (lane = { revision: undefined, tasks: [], running: false }));
    return lane;
  }
}
