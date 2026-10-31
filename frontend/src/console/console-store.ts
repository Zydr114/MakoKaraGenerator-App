// 控制台的非 React 状态：MakoClient 单例、HTTP 往返日志、事件日志。
// 单例放在模块级，避免 StrictMode 的双重挂载创建两个 client。

import {
  MakoClient,
  ObservableValue,
  devServerBootstrapProvider,
  type ExchangeRecord,
} from '../client';
import type { BackendEvent } from '../protocol';

export type EventLogEntry =
  | { id: number; at: number; kind: 'event'; event: BackendEvent }
  | { id: number; at: number; kind: 'stream'; level: 'info' | 'warn'; message: string; cursor?: string }
  | { id: number; at: number; kind: 'client'; level: 'info' | 'warn'; message: string };

const MAX_EXCHANGES = 200;
const MAX_EVENTS = 500;
let nextId = 1;

export const exchanges = new ObservableValue<readonly ExchangeRecord[]>([]);
export const eventLog = new ObservableValue<readonly EventLogEntry[]>([]);

type NewEntry = EventLogEntry extends infer E ? (E extends unknown ? Omit<E, 'id' | 'at'> : never) : never;

function pushEvent(entry: NewEntry): void {
  const full = { ...entry, id: nextId++, at: Date.now() } as EventLogEntry;
  eventLog.update((list) => [full, ...list].slice(0, MAX_EVENTS));
}

export const client = MakoClient.create(devServerBootstrapProvider(), {
  onExchange: (record) => exchanges.update((list) => [{ ...record }, ...list].slice(0, MAX_EXCHANGES)),
});

client.on('event', (event) => pushEvent({ kind: 'event', event }));
client.on('stream-log', (log) =>
  pushEvent({
    kind: 'stream',
    level: log.kind === 'error' || log.kind === 'backoff' || log.kind === 'idle' ? 'warn' : 'info',
    message: log.message,
    ...(log.cursor ? { cursor: log.cursor } : {}),
  }),
);
client.on('resync', ({ state }) =>
  pushEvent({ kind: 'client', level: 'warn', message: `resync 完成：重新读取 client state，新游标 ${state.eventCursor}` }),
);
client.on('reset', ({ reason }) => pushEvent({ kind: 'client', level: 'warn', message: `连接身份作废：${reason}` }));
client.on('log', (message) => pushEvent({ kind: 'client', level: 'info', message }));

export function clearLogs(): void {
  exchanges.set([]);
  eventLog.set([]);
}

let started = false;
/** 首次挂载时连接；失败时状态为 failed，由页面的自动重连处理。 */
export function startOnce(): void {
  if (started) return;
  started = true;
  client.initialize().catch(() => undefined);
}
