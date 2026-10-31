import { useState } from 'react';
import type { ExchangeRecord } from '../client';
import { exchanges } from './console-store';
import { clockTime } from './format';
import { useObservable } from './hooks';

/** 最近的 HTTP 往返（凭证已在客户端库内打码）。默认展开最新一条。 */
export function ExchangeLog() {
  const list = useObservable(exchanges);
  const [openId, setOpenId] = useState<number | null>(null);
  const [hideHeartbeat, setHideHeartbeat] = useState(true);
  const visible = hideHeartbeat ? list.filter((r) => r.label !== 'clients.heartbeat' && r.label !== 'preview.get') : list;
  const expanded = openId ?? visible[0]?.id ?? null;
  return (
    <section className="panel exchange-panel">
      <div className="panel-head">
        <h2>请求 / 响应</h2>
        <label className="toggle">
          <input type="checkbox" checked={hideHeartbeat} onChange={(e) => setHideHeartbeat(e.target.checked)} />
          隐藏心跳与预览轮询
        </label>
      </div>
      <ol className="exchange-list">
        {visible.map((record) => (
          <li key={record.id} className={expanded === record.id ? 'open' : ''}>
            <button className="exchange-row" onClick={() => setOpenId(expanded === record.id ? -1 : record.id)}>
              <span className={`status status-${statusClass(record)}`}>{record.status ?? 'ERR'}</span>
              <span className="method">{record.method}</span>
              <span className="url mono">{record.url}</span>
              <span className="muted">{record.durationMs}ms · {clockTime(record.startedAt)}</span>
            </button>
            {expanded === record.id && <ExchangeDetail record={record} />}
          </li>
        ))}
        {visible.length === 0 && <li className="empty">还没有请求。</li>}
      </ol>
    </section>
  );
}

function ExchangeDetail({ record }: { record: ExchangeRecord }) {
  return (
    <div className="exchange-detail">
      {record.label && <div className="muted">操作：{record.label}</div>}
      {record.error && <div className="error-text">{record.error}</div>}
      <h3>请求头</h3>
      <pre>{JSON.stringify(record.requestHeaders, null, 2)}</pre>
      {record.requestBody !== undefined && (
        <>
          <h3>请求正文</h3>
          <pre>{stringify(record.requestBody)}</pre>
        </>
      )}
      {record.responseBody !== undefined && (
        <>
          <h3>响应正文</h3>
          <pre>{stringify(record.responseBody)}</pre>
        </>
      )}
    </div>
  );
}

function stringify(value: unknown): string {
  return typeof value === 'string' ? value : JSON.stringify(value, null, 2);
}

function statusClass(record: ExchangeRecord): string {
  if (record.status === undefined) return 'err';
  if (record.status >= 500) return 'err';
  if (record.status >= 400) return 'warn';
  if (record.status === 202) return 'accepted';
  return 'ok';
}
