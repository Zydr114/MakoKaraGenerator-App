import { useEffect, useRef } from 'react';
import { Button as AriaButton } from 'react-aria-components';
import { formatTime } from '../model/time';
import { Switch } from '../ui/choices';
import { Select } from '../ui/fields';
import { useObservable } from '../ui/hooks';
import { Icon } from '../ui/Icon';
import type { AudioController } from './audio-controller';
import './audio.css';

const PRESET_RATES = [
  { value: '0.25', label: '0.25×' },
  { value: '0.5', label: '0.5×' },
  { value: '0.6', label: '0.6×' },
  { value: '0.75', label: '0.75×' },
  { value: '0.85', label: '0.85×' },
  { value: '1', label: '1.0×' },
  { value: '1.25', label: '1.25×' },
  { value: '1.5', label: '1.5×' },
  { value: '2', label: '2.0×' },
] as const;

function rateOptionsFor(currentRate: number): { value: string; label: string }[] {
  const key = String(currentRate);
  if (PRESET_RATES.some((item) => item.value === key)) {
    return [...PRESET_RATES];
  }
  const merged = [...PRESET_RATES, { value: key, label: `${currentRate.toFixed(2).replace(/\.?0+$/, '')}×` }];
  return merged.sort((a, b) => Number(a.value) - Number(b.value));
}

/**
 * 播放条：播放／暂停、播放头时间、速度、循环本行、打点模式（外观页没有打点，不传 onStampMode）。
 * 放在时间轴工具栏的最前面（界面改版 §2.6），播放按钮用第三色（幕后）。
 */
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
      <AriaButton className="play" isDisabled={state.status !== 'ready'} onPress={() => props.audio.toggle()} aria-label={state.playing ? '暂停' : '播放'}>
        <Icon name={state.playing ? 'pause' : 'play_arrow'} filled />
      </AriaButton>
      <span className="transport-time">
        <span ref={time}>0:00.000</span>
        <small>/ {formatTime(state.durationUs)}</small>
      </span>
      <div className="transport-rate-group" title="保调变速（0.25× ~ 2.0×，快捷键 Q 减速 / W 加速，按住 Shift 微调 ±5%）">
        <AriaButton
          className="transport-rate-step"
          isDisabled={state.rate <= 0.25}
          onPress={(e) => props.audio.stepRate(e.shiftKey ? -0.05 : -0.1)}
          aria-label="减速"
        >
          −
        </AriaButton>
        <Select
          label="速度"
          labelPlacement="none"
          size="sm"
          value={String(state.rate)}
          options={rateOptionsFor(state.rate)}
          onChange={(v) => props.audio.setRate(Number(v))}
          className="transport-rate"
        />
        <AriaButton
          className="transport-rate-step"
          isDisabled={state.rate >= 2.0}
          onPress={(e) => props.audio.stepRate(e.shiftKey ? 0.05 : 0.1)}
          aria-label="加速"
        >
          +
        </AriaButton>
      </div>
      <Switch label={props.loopLabel ?? '循环本行'} checked={props.loopLine} disabled={!props.loopAvailable} onChange={props.onLoopLine} />
      {props.onStampMode && <Switch label="打点模式" checked={!!props.stampMode} disabled={!props.stampAvailable} onChange={props.onStampMode} />}
      {/* 说明只显示一行，放不下时省略，完整文字在悬停提示里。 */}
      <span className="transport-help" title={state.status === 'error' ? state.error : help}>
        {state.status === 'loading' && '正在读取音频……'}
        {state.status === 'error' && <span className="notice-error">{state.error}</span>}
        {help}
      </span>
    </div>
  );
}
