import { useEffect, useState } from 'react';
import { Dialog as AriaDialog, DialogTrigger, Heading, Popover } from 'react-aria-components';
import type { LyricTimingTarget, RubyAnnotation, RubyTextSegment, TimingStatus } from '../../protocol';
import type { AudioController } from '../audio/audio-controller';
import { isTimed, type EditorLine, type EditorToken } from '../model/lyrics';
import { nudgedRange, segmentTarget, selectedSegment, stampedEnd, stampedStart, type TimeRange } from '../model/ruby-segments';
import { formatTime, fromLyricTime, parseTime, toMicros } from '../model/time';
import type { EditorSession } from '../session/editor-session';
import { Button, IconButton } from '../ui/Button';
import { TextField } from '../ui/fields';
import { Menu, MenuItem, MenuTrigger } from '../ui/Menu';
import { StatusIcon } from './LineList';

/**
 * 时间栏（整体设计 §5.2，界面改版稿件 #lyrics）：行编辑区下面一条，所选元素的起止、±1 帧、清除；
 * 没选文本单元时是本行的起止。右边是整行平移。选中注音片段时改为这个片段（W6 设计 §2.5），多一个“取播放头”。
 */
export function TimeBar(props: {
  session: EditorSession;
  audio: AudioController | null;
  line: EditorLine | undefined;
  tokenId?: string | undefined;
  segmentId?: string | undefined;
}) {
  const { session, line } = props;
  if (!line) {
    return (
      <div className="timebar">
        <span className="faint">选择一行后在这里编辑时间。</span>
      </div>
    );
  }
  const token = props.tokenId ? line.tokens.find((t) => t.id === props.tokenId) : undefined;
  const selected = token && props.segmentId ? selectedSegment(line, { lineId: line.id, tokenId: token.id, segmentId: props.segmentId }) : undefined;
  const setTimeRange = (target: LyricTimingTarget) => (startUs: number, endUs: number) =>
    session.command({ type: 'lyrics.setTimeRange', target, startUs: toMicros(startUs), endUs: toMicros(endUs) });
  return (
    <div className="timebar" role="group" aria-label="时间栏">
      {selected ? (
        <SegmentTime session={session} audio={props.audio} line={line} ruby={selected.ruby} segment={selected.segment} />
      ) : token ? (
        <>
          <TimeEditor
            key={token.id}
            title="所选"
            name={token.text}
            ariaPrefix={`「${token.text}」`}
            item={token}
            onApply={setTimeRange({ branchId: line.branchId, lineId: line.id, tokenId: token.id })}
          />
          <TokenActions session={session} line={line} token={token} />
        </>
      ) : (
        <TimeEditor key={line.id} title="本行" ariaPrefix="行" item={line} onApply={setTimeRange({ branchId: line.branchId, lineId: line.id })} />
      )}
      <span className="spacer" />
      <LineShift session={session} line={line} />
    </div>
  );
}

function TokenActions(props: { session: EditorSession; line: EditorLine; token: EditorToken }) {
  const { session, line, token } = props;
  const target = { branchId: line.branchId, lineId: line.id, tokenId: token.id };
  const busy = session.state.pendingCommands > 0;
  const nudgeOff = !isTimed(token) || busy || !session.supportsCommand('lyrics.nudgeTokenTime');
  const nudge = (frameDelta: number) => void session.command({ type: 'lyrics.nudgeTokenTime', target, frameDelta });
  return (
    <>
      <IconButton icon="chevron_left" label="早一帧" size="sm" isDisabled={nudgeOff} onPress={() => nudge(-1)} />
      <IconButton icon="chevron_right" label="晚一帧" size="sm" isDisabled={nudgeOff} onPress={() => nudge(1)} />
      <Button
        variant="ghost"
        size="sm"
        isDisabled={token.startUs === undefined || busy || !session.supportsCommand('lyrics.clearTokenTime')}
        onPress={() => void session.command({ type: 'lyrics.clearTokenTime', target })}
      >
        清除
      </Button>
    </>
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
  const stamp = (which: 'start' | 'end') => {
    if (!audio) return;
    if (which === 'start') {
      void set(stampedStart(item, audio.playheadUs(), rate));
      return;
    }
    // 播放头随时在走，点的时候再判断。
    const range = stampedEnd(item, audio.playheadUs(), rate);
    if (typeof range !== 'string') void set(range);
    else session.notify({ level: 'warning', message: `终点取播放头：${range}。` });
  };
  return (
    <>
      <TimeEditor key={segment.id} title="注音片段" name={segment.text} ariaPrefix={`注音「${segment.text}」`} item={item} onApply={(startUs, endUs) => set({ startUs, endUs })} />
      {[-1, 1].map((frames) => (
        <IconButton
          key={frames}
          icon={frames < 0 ? 'chevron_left' : 'chevron_right'}
          label={frames < 0 ? '早一帧' : '晚一帧'}
          size="sm"
          isDisabled={!nudge(frames) || busy || !available}
          onPress={() => nudge(frames) && void set(nudge(frames)!)}
        />
      ))}
      <MenuTrigger>
        <Button variant="ghost" size="sm" trailingIcon="arrow_drop_down" isDisabled={!audio || busy || !available}>
          取播放头
        </Button>
        <Menu label="取播放头" onAction={(id) => stamp(id as 'start' | 'end')}>
          <MenuItem id="start">起点取播放头</MenuItem>
          <MenuItem id="end" disabled={item.startUs === undefined} {...(item.startUs === undefined ? { description: '先设起点' } : {})}>
            终点取播放头
          </MenuItem>
        </Menu>
      </MenuTrigger>
      <Button
        variant="ghost"
        size="sm"
        isDisabled={item.startUs === undefined || busy || !session.supportsCommand('lyrics.clearRubySegmentTime')}
        onPress={() => void session.command({ type: 'lyrics.clearRubySegmentTime', target })}
      >
        清除
      </Button>
    </>
  );
}

/** 整行平移：行与已定时元素一起移动（毫秒，可为负）。按钮弹出一个小输入框。 */
function LineShift(props: { session: EditorSession; line: EditorLine }) {
  const [open, setOpen] = useState(false);
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
    if (response) {
      setText('');
      setOpen(false);
    }
  };
  return (
    <DialogTrigger isOpen={open} onOpenChange={setOpen}>
      <Button size="sm" icon="swap_horiz" isDisabled={!available}>
        整行平移…
      </Button>
      <Popover className="popover shift-pop" offset={6} placement="top end">
        <AriaDialog className="shift-dialog" aria-label="整行平移">
          <Heading slot="title" className="shift-title">
            整行平移
          </Heading>
          <TextField label="毫秒（可为负）" value={text} error={error} helper="行和已打轴的文本单元一起移动" onChange={setText} onEnter={() => void apply()} autoFocus suffix="ms" />
          <div className="shift-actions">
            <Button variant="text" size="sm" onPress={() => setOpen(false)}>
              取消
            </Button>
            <Button variant="filled" size="sm" isDisabled={ms === null || !!error} onPress={() => void apply()}>
              平移
            </Button>
          </div>
        </AriaDialog>
      </Popover>
    </DialogTrigger>
  );
}

/** 起止时间输入：草稿留在本地，回车或点“应用”才提交一条命令（元素与行是 setTimeRange，注音片段是 setRubySegmentTime）。 */
function TimeEditor(props: {
  title: string;
  name?: string;
  /** 读屏用：两个输入框叫“<前缀>起点”“<前缀>终点”。 */
  ariaPrefix: string;
  item: { startUs?: number; endUs?: number; status?: TimingStatus };
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
  const startError = start && startUs === null ? '起点格式：分:秒.毫秒' : undefined;
  const endError = end && endUs === null ? '终点格式：分:秒.毫秒' : startUs !== null && endUs !== null && endUs <= startUs ? '终点要晚于起点' : undefined;
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
  const reset = () => {
    setStart(item.startUs !== undefined ? formatTime(item.startUs) : '');
    setEnd(item.endUs !== undefined ? formatTime(item.endUs) : '');
  };

  return (
    <>
      <span className="lbl">{props.title}</span>
      {props.name && <b lang="ja">{props.name}</b>}
      {item.status && <StatusIcon status={item.status} />}
      <span className="lbl tb-gap">起</span>
      <TextField label="起" ariaLabel={`${props.ariaPrefix}起点`} labelPlacement="none" size="sm" value={start} onChange={setStart} onEnter={() => void apply()} onEscape={reset} className="tb-time" />
      <span className="lbl">止</span>
      <TextField label="止" ariaLabel={`${props.ariaPrefix}终点`} labelPlacement="none" size="sm" value={end} onChange={setEnd} onEnter={() => void apply()} onEscape={reset} className="tb-time" />
      {startError || endError ? (
        <span className="tb-err" role="alert">
          {startError ?? endError}
        </span>
      ) : (
        startUs !== null && endUs !== null && endUs > startUs && <span className="faint small tnum">{((endUs - startUs) / 1e6).toFixed(3)} s</span>
      )}
      {!unchanged && (
        <Button variant="tonal" size="sm" isDisabled={!canApply} onPress={() => void apply()}>
          应用
        </Button>
      )}
    </>
  );
}
