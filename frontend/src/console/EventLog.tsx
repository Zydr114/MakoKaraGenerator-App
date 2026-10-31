import type { BackendEvent } from '../protocol';
import { eventLog, type EventLogEntry } from './console-store';
import { clockTime } from './format';
import { useObservable } from './hooks';

/** SSE 事件实时日志，包含连接、断线、重连与 resync 记录。 */
export function EventLog() {
  const list = useObservable(eventLog);
  return (
    <section className="panel event-panel">
      <div className="panel-head">
        <h2>事件流</h2>
        <span className="muted">{list.filter((e) => e.kind === 'event').length} 条事件</span>
      </div>
      <ol className="event-list">
        {list.map((entry) => (
          <li key={entry.id} className={`event-entry kind-${entry.kind} ${'level' in entry ? `level-${entry.level}` : ''}`}>
            <span className="time">{clockTime(entry.at)}</span>
            <EventBody entry={entry} />
          </li>
        ))}
        {list.length === 0 && <li className="empty">等待事件……</li>}
      </ol>
    </section>
  );
}

function EventBody({ entry }: { entry: EventLogEntry }) {
  if (entry.kind !== 'event') {
    return (
      <span className="event-text">
        {entry.kind === 'stream' ? '⟲ ' : '● '}
        {entry.message}
        {'cursor' in entry && entry.cursor && <span className="mono muted"> {entry.cursor}</span>}
      </span>
    );
  }
  return (
    <span className="event-text">
      <span className="mono seq">#{entry.event.eventSeq}</span> <strong>{entry.event.type}</strong> {summarize(entry.event)}
    </span>
  );
}

function summarize(event: BackendEvent): string {
  if (event.summary) {
    const s = event.summary;
    return `rev ${s.projectRevision}${s.isDirty ? ' · dirty' : ''}${s.savedProjectRevision ? ` · saved ${s.savedProjectRevision}` : ''}`;
  }
  if (event.job) return `${event.job.kind} · ${event.job.state}${event.job.errorCode ? ` · ${event.job.errorCode}` : ''}`;
  if (event.preview) {
    const p = event.preview;
    return `${p.state} · gen ${p.previewGeneration}${p.latestRequestSeq ? ` · seq ${p.latestRequestSeq}` : ''}${p.renderFailureCode ? ` · ${p.renderFailureCode}` : ''}`;
  }
  return '';
}
