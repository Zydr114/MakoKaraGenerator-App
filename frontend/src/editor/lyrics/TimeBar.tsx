import { useEffect, useState } from 'react';
import type { LyricTimingTarget, RubyAnnotation, RubyTextSegment } from '../../protocol';
import type { AudioController } from '../audio/audio-controller';
import { TIMING_STATUS_TEXT, isTimed, type EditorLine, type EditorToken } from '../model/lyrics';
import { nudgedRange, segmentTarget, selectedSegment, stampedEnd, stampedStart, type TimeRange } from '../model/ruby-segments';
import { formatTime, fromLyricTime, parseTime, toMicros } from '../model/time';
import type { EditorSession } from '../session/editor-session';
import { TextField } from '../ui/legacy/fields';

/**
 * 时间栏（整体设计 §5.2）：固定在播放条上方，左边是所选元素的起止、±1 帧、清除，
 * 右边是本行起止和整行平移。放在底部区，行编辑区怎么滚动都不会被挤走。
 * 选中注音片段时，左边一组改为这个片段（W6 设计 §2.5）。
 */
export function TimeBar(props: {
  session: EditorSession;
  audio: AudioController | null;
  line: EditorLine | undefined;
  tokenId?: string | undefined;
  segmentId?: string | undefined;
}) {
  const { session, line } = props;
  if (!line) return <div className="time-bar muted">选择一行后在这里编辑时间。</div>;
  const token = props.tokenId ? line.tokens.find((t) => t.id === props.tokenId) : undefined;
  const selected = token && props.segmentId ? selectedSegment(line, { lineId: line.id, tokenId: token.id, segmentId: props.segmentId }) : undefined;
  const setTimeRange = (target: LyricTimingTarget) => (startUs: number, endUs: number) =>
    session.command({ type: 'lyrics.setTimeRange', target, startUs: toMicros(startUs), endUs: toMicros(endUs) });
  return (
    <div className="time-bar">
      <div className="time-group">
        {selected ? (
          <SegmentTime session={session} audio={props.audio} line={line} ruby={selected.ruby} segment={selected.segment} />
        ) : token ? (
          <>
            <TimeEditor
              key={token.id}
              title={`所选「${token.text}」`}
              labels={['起点', '终点']}
              item={token}
              onApply={setTimeRange({ branchId: line.branchId, lineId: line.id, tokenId: token.id })}
            />
            <TokenActions session={session} line={line} token={token} />
          </>
        ) : (
          <span className="muted time-group-hint">{line.tokens.length > 0 ? '点选文本单元编辑它的时间' : '本行没有拆分文本单元，只能编辑整行时间'}</span>
        )}
      </div>
      <div className="time-group">
        <TimeEditor key={line.id} title="本行" labels={['行起点', '行终点']} item={line} onApply={setTimeRange({ branchId: line.branchId, lineId: line.id })} />
        <LineShift session={session} line={line} />
      </div>
    </div>
  );
}

function TokenActions(props: { session: EditorSession; line: EditorLine; token: EditorToken }) {
  const { session, line, token } = props;
  const target = { branchId: line.branchId, lineId: line.id, tokenId: token.id };
  const busy = session.state.pendingCommands > 0;
  const nudge = (frameDelta: number) => void session.command({ type: 'lyrics.nudgeTokenTime', target, frameDelta });
  return (
    <div className="time-actions">
      <mdui-button variant="outlined" disabled={!isTimed(token) || busy || !session.supportsCommand('lyrics.nudgeTokenTime')} onClick={() => nudge(-1)}>
        −1 帧
      </mdui-button>
      <mdui-button variant="outlined" disabled={!isTimed(token) || busy || !session.supportsCommand('lyrics.nudgeTokenTime')} onClick={() => nudge(1)}>
        +1 帧
      </mdui-button>
      <mdui-button
        variant="text"
        disabled={token.startUs === undefined || busy || !session.supportsCommand('lyrics.clearTokenTime')}
        onClick={() => void session.command({ type: 'lyrics.clearTokenTime', target })}
      >
        清除时间
      </mdui-button>
    </div>
  );
}

/**
 * 注音片段的时间（W6 设计 §2.5）：起止、±1 帧、清除、起点或终点取播放头。
 * 后端没有片段的 nudge 和打点命令，都在本地算好（按帧吸附）再发 setRubySegmentTime。
 */
function SegmentTime(props: { session: EditorSession; audio: AudioController | null; line: EditorLine; ruby: RubyAnnotation; segment: RubyTextSegment }) {
  const { session, audio, segment } = props;
  const target = segmentTarget(props.line, props.ruby, segment.id);
  const item = { ...fromLyricTime(segment.time), status: segment.timingStatus };
  const rate = session.frameRate;
  const busy = session.state.pendingCommands > 0;
  const available = session.supportsCommand('lyrics.setRubySegmentTime');
  const set = (range: TimeRange) => session.command({ type: 'lyrics.setRubySegmentTime', target, startUs: toMicros(range.startUs), endUs: toMicros(range.endUs) });
  const nudge = (frames: number) => (isTimed(item) ? nudgedRange(item, frames, rate) : undefined);
  return (
    <>
      <TimeEditor key={segment.id} title={`注音片段「${segment.text}」`} labels={['起点', '终点']} item={item} onApply={(startUs, endUs) => set({ startUs, endUs })} />
      <div className="time-actions">
        {[-1, 1].map((frames) => (
          <mdui-button key={frames} variant="outlined" disabled={!nudge(frames) || busy || !available} onClick={() => nudge(frames) && void set(nudge(frames)!)}>
            {frames < 0 ? '−1 帧' : '+1 帧'}
          </mdui-button>
        ))}
        <mdui-button
          variant="text"
          disabled={item.startUs === undefined || busy || !session.supportsCommand('lyrics.clearRubySegmentTime')}
          onClick={() => void session.command({ type: 'lyrics.clearRubySegmentTime', target })}
        >
          清除时间
        </mdui-button>
        <mdui-button variant="text" disabled={!audio || busy || !available} onClick={() => audio && void set(stampedStart(item, audio.playheadUs(), rate))}>
          起点取播放头
        </mdui-button>
        <mdui-button
          variant="text"
          disabled={!audio || item.startUs === undefined || busy || !available}
          title={item.startUs === undefined ? '先设起点' : undefined}
          onClick={() => {
            // 播放头随时在走，点的时候再判断。
            const range = audio && stampedEnd(item, audio.playheadUs(), rate);
            if (range && typeof range !== 'string') void set(range);
            else if (range) session.notify({ level: 'warning', message: `终点取播放头：${range}。` });
          }}
        >
          终点取播放头
        </mdui-button>
      </div>
    </>
  );
}

/** 整行平移：行与已定时元素一起移动（毫秒，可为负）。 */
function LineShift(props: { session: EditorSession; line: EditorLine }) {
  const [text, setText] = useState('');
  const ms = text.trim() === '' ? null : Number(text);
  const error = text.trim() !== '' && (ms === null || !Number.isFinite(ms) || ms === 0) ? '填写非零的毫秒数，例如 -120' : undefined;
  const available = props.session.supportsCommand('lyrics.shiftLine') && isTimed(props.line);
  const apply = async () => {
    if (ms === null || error || !available) return;
    const response = await props.session.command({
      type: 'lyrics.shiftLine',
      target: { branchId: props.line.branchId, lineId: props.line.id },
      deltaUs: toMicros(Math.round(ms * 1000)),
    });
    if (response) setText('');
  };
  return (
    <div className="time-actions">
      <TextField label="整行平移（毫秒）" value={text} error={error} disabled={!available} onChange={setText} onEnter={apply} className="shift-field" />
      <mdui-button variant="outlined" disabled={!available || ms === null || !!error} onClick={apply}>
        平移
      </mdui-button>
    </div>
  );
}

export function StatusBadge(props: { status: NonNullable<EditorToken['status']> }) {
  return <span className={`status-badge status-${props.status}`}>{TIMING_STATUS_TEXT[props.status]}</span>;
}

/** 起止时间输入：草稿留在本地，回车或点“应用”才提交一条命令（元素与行是 setTimeRange，注音片段是 setRubySegmentTime）。 */
function TimeEditor(props: {
  title: string;
  labels: readonly [string, string];
  item: { startUs?: number; endUs?: number; status?: EditorToken['status'] };
  onApply: (startUs: number, endUs: number) => Promise<unknown>;
}) {
  const { item } = props;
  const [start, setStart] = useState(() => (item.startUs !== undefined ? formatTime(item.startUs) : ''));
  const [end, setEnd] = useState(() => (item.endUs !== undefined ? formatTime(item.endUs) : ''));
  const [busy, setBusy] = useState(false);

  // 权威时间变化（命令成功、撤销、别处修改）时，用新值覆盖草稿。
  useEffect(() => {
    setStart(item.startUs !== undefined ? formatTime(item.startUs) : '');
    setEnd(item.endUs !== undefined ? formatTime(item.endUs) : '');
  }, [item.startUs, item.endUs]);

  const startUs = parseTime(start);
  const endUs = parseTime(end);
  const startError = start && startUs === null ? '格式：分:秒.毫秒' : undefined;
  const endError = end && endUs === null ? '格式：分:秒.毫秒' : startUs !== null && endUs !== null && endUs <= startUs ? '终点要晚于起点' : undefined;
  const unchanged = startUs === (item.startUs ?? null) && endUs === (item.endUs ?? null);
  const canApply = startUs !== null && endUs !== null && !startError && !endError && !unchanged && !busy;

  const apply = async () => {
    if (!canApply) return;
    setBusy(true);
    try {
      await props.onApply(startUs!, endUs!);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="time-editor">
      <span className="time-editor-title">
        {props.title}
        {item.status && <StatusBadge status={item.status} />}
      </span>
      <TextField label={props.labels[0]} value={start} error={startError} onChange={setStart} onEnter={apply} className="time-field" />
      <TextField label={props.labels[1]} value={end} error={endError} onChange={setEnd} onEnter={apply} className="time-field" />
      {startUs !== null && endUs !== null && endUs > startUs && <span className="muted time-duration">{((endUs - startUs) / 1e6).toFixed(3)} 秒</span>}
      <mdui-button variant="tonal" disabled={!canApply} onClick={apply}>
        应用
      </mdui-button>
    </div>
  );
}
