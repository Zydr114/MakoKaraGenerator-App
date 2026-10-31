import type { MakoSnapshot } from '../client';
import { client } from './console-store';
import { clockTime, formatCountdown, shortCursor, shortId } from './format';
import { useNow } from './hooks';

const STATE_LABEL: Record<string, string> = {
  connecting: '连接中',
  ready: '已连接',
  refreshing: '恢复令牌中',
  needsRegistration: '需要重新登记',
  hostRestarted: 'Server 已重启',
  failed: '连接失败',
};

const EVENTS_LABEL: Record<string, string> = {
  stopped: '已停止',
  connecting: '连接中',
  open: '已连接',
  waiting: '等待重连',
};

export function StatusBar({ snapshot, autoReconnect, onAutoReconnect }: {
  snapshot: MakoSnapshot;
  autoReconnect: boolean;
  onAutoReconnect: (value: boolean) => void;
}) {
  const now = useNow(1000);
  const serverNow = now + snapshot.serverOffsetMs;
  const ready = snapshot.state === 'ready';
  return (
    <header className="status-bar">
      <div className="brand">
        <strong>MakoKara</strong> 协议控制台 <span className="muted">W0</span>
      </div>
      <dl className="status-grid">
        <div>
          <dt>连接</dt>
          <dd>
            <span className={`chip chip-${snapshot.state}`}>{STATE_LABEL[snapshot.state] ?? snapshot.state}</span>
          </dd>
        </div>
        <div>
          <dt>Server</dt>
          <dd title={snapshot.baseUrl}>{snapshot.baseUrl ?? '—'}</dd>
        </div>
        <div>
          <dt>hostInstanceId</dt>
          <dd className="mono" title={snapshot.hostInstanceId}>{shortId(snapshot.hostInstanceId)}</dd>
        </div>
        <div>
          <dt>clientId</dt>
          <dd title={snapshot.clientId}>
            <span className="mono">{shortId(snapshot.clientId)}</span>
            {snapshot.origin && <span className="muted"> · {snapshot.origin === 'resumed' ? 'resume 恢复' : '新登记'}</span>}
          </dd>
        </div>
        <div>
          <dt>令牌 / 租约</dt>
          <dd>
            {formatCountdown(snapshot.tokenExpiresAtMs === undefined ? undefined : snapshot.tokenExpiresAtMs - serverNow)}
            {' / '}
            {formatCountdown(snapshot.leaseExpiresAtMs === undefined ? undefined : snapshot.leaseExpiresAtMs - serverNow)}
          </dd>
        </div>
        <div>
          <dt>心跳</dt>
          <dd>
            <span className={`dot dot-${snapshot.heartbeat}`} />
            {snapshot.heartbeat === 'ok' && snapshot.lastHeartbeatAtMs
              ? `${clockTime(snapshot.lastHeartbeatAtMs - snapshot.serverOffsetMs)}`
              : snapshot.heartbeat === 'failing'
                ? '失败'
                : '—'}
            {snapshot.heartbeatIntervalMs && <span className="muted"> · 每 {Math.round(snapshot.heartbeatIntervalMs / 1000)}s</span>}
          </dd>
        </div>
        <div>
          <dt>事件流</dt>
          <dd>
            <span className={`dot dot-sse-${snapshot.events.status}`} />
            {EVENTS_LABEL[snapshot.events.status]} <span className="mono muted">{shortCursor(snapshot.events.cursor)}</span>
            <span className="muted"> · 收 {snapshot.events.received} · 重连 {snapshot.events.reconnects}</span>
          </dd>
        </div>
        <div>
          <dt>产品</dt>
          <dd>{snapshot.productVersion ?? '—'}</dd>
        </div>
      </dl>
      <div className="status-actions">
        <button disabled={!ready} onClick={() => void client.heartbeat()}>心跳</button>
        <button disabled={!ready} onClick={() => void client.resume().catch(() => undefined)}>resume</button>
        {snapshot.state === 'needsRegistration' && (
          <button className="primary" onClick={() => void client.registerNew().catch(() => undefined)}>重新登记</button>
        )}
        {(snapshot.state === 'hostRestarted' || snapshot.state === 'failed') && (
          <button className="primary" onClick={() => void client.reinitialize('手动重新初始化').catch(() => undefined)}>重新初始化</button>
        )}
        <label className="toggle">
          <input type="checkbox" checked={autoReconnect} onChange={(e) => onAutoReconnect(e.target.checked)} />
          自动重连
        </label>
      </div>
      {snapshot.reason && snapshot.state !== 'ready' && <div className="status-reason">{snapshot.reason}</div>}
    </header>
  );
}
