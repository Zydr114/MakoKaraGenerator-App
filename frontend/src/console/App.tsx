import { useEffect, useRef, useState } from 'react';
import { client, clearLogs, startOnce } from './console-store';
import { EventLog } from './EventLog';
import { ExchangeLog } from './ExchangeLog';
import { useObservable } from './hooks';
import { StatusBar } from './StatusBar';
import { WorkflowPanel } from './WorkflowPanel';

/**
 * 协议控制台（设计 §8）：W0 的验收载体，也是后续排查协议问题的工具。
 * 顶部连接状态；左栏操作步骤；中间请求／响应 JSON；右栏 SSE 事件日志。
 */
export function App() {
  const snapshot = useObservable(client.state$);
  const [autoReconnect, setAutoReconnect] = useState(true);
  const [retryTick, setRetryTick] = useState(0);
  const attempts = useRef(0);

  useEffect(() => startOnce(), []);

  // Server 重启或连接失败（例如还没运行 pnpm dev:backend）时，按 1s、2s、4s…（上限 10s）重新初始化。
  useEffect(() => {
    if (snapshot.state !== 'hostRestarted' && snapshot.state !== 'failed') {
      attempts.current = 0;
      return;
    }
    if (!autoReconnect) return;
    const delay = Math.min(10_000, 1000 * 2 ** attempts.current);
    const timer = setTimeout(() => {
      attempts.current++;
      client.reinitialize('自动重新初始化').catch(() => setRetryTick((t) => t + 1));
    }, delay);
    return () => clearTimeout(timer);
  }, [snapshot.state, autoReconnect, retryTick]);

  return (
    <div className="console">
      <StatusBar snapshot={snapshot} autoReconnect={autoReconnect} onAutoReconnect={setAutoReconnect} />
      <main className="columns">
        <WorkflowPanel snapshot={snapshot} />
        <ExchangeLog />
        <EventLog />
      </main>
      <footer className="footer muted">
        凭证只保存在内存（bootstrap／access）与 sessionStorage（resumeToken），日志中已打码。
        <button className="link" onClick={clearLogs}>清空日志</button>
      </footer>
    </div>
  );
}
