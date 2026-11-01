import { useEffect, useRef } from 'react';
import { formatTime } from '../model/time';
import { CompactSelect, Switch } from '../ui/legacy/fields';
import { useObservable } from '../ui/hooks';
import type { AudioController } from './audio-controller';

const RATES = [
  { value: '0.5', label: '0.5×' },
  { value: '0.75', label: '0.75×' },
  { value: '1', label: '1.0×' },
] as const;

/** 播放条：播放／暂停、播放头时间、速度、循环本行、打点模式（外观页没有打点，不传 onStampMode）。 */
export function Transport(props: {
  audio: AudioController;
  loopLine: boolean;
  onLoopLine: (on: boolean) => void;
  loopAvailable: boolean;
  stampMode?: boolean;
  onStampMode?: (on: boolean) => void;
  stampAvailable?: boolean;
  /** 就绪时的快捷键说明。 */
  help?: string;
  /** 循环开关的文字，缺省“循环本行”（编排页循环选中的几句）。 */
  loopLabel?: string;
}) {
  const state = useObservable(props.audio.state$);
  const time = useRef<HTMLSpanElement>(null);

  // 播放头文字每帧直接写 DOM，不触发 React 渲染。
  useEffect(() => {
    let frame = 0;
    let last = '';
    const tick = () => {
      frame = requestAnimationFrame(tick);
      const text = formatTime(props.audio.playheadUs());
      if (text !== last && time.current) {
        time.current.textContent = text;
        last = text;
      }
    };
    frame = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(frame);
  }, [props.audio]);

  const help =
    state.status === 'ready'
      ? props.stampMode
        ? '打点：按住 J 记起点，松开记终点并前进 · ←→ 选元素 · Alt＋←→ 微调一帧 · Delete 清除'
        : (props.help ?? '空格 播放／暂停 · ←→ 选元素 · ↑↓ 选行')
      : undefined;

  return (
    <div className="transport">
      <mdui-button-icon variant="filled" disabled={state.status !== 'ready'} onClick={() => props.audio.toggle()} aria-label={state.playing ? '暂停' : '播放'}>
        {state.playing ? <mdui-icon-pause /> : <mdui-icon-play-arrow />}
      </mdui-button-icon>
      <span className="transport-time">
        <span ref={time}>0:00.000</span>
        <span className="muted"> / {formatTime(state.durationUs)}</span>
      </span>
      <CompactSelect label="速度" value={String(state.rate) as (typeof RATES)[number]['value']} options={RATES} onChange={(v) => props.audio.setRate(Number(v))} className="transport-rate" />
      <Switch label={props.loopLabel ?? '循环本行'} checked={props.loopLine} disabled={!props.loopAvailable} onChange={props.onLoopLine} />
      {props.onStampMode && <Switch label="打点模式" checked={!!props.stampMode} disabled={!props.stampAvailable} onChange={props.onStampMode} />}
      {/* 窄窗口下说明只显示一行，完整文字在悬停提示里。 */}
      <span className="transport-help muted" title={state.status === 'error' ? state.error : help}>
        {state.status === 'loading' && '正在读取音频……'}
        {state.status === 'error' && <span className="notice-error">{state.error}</span>}
        {help}
      </span>
    </div>
  );
}
