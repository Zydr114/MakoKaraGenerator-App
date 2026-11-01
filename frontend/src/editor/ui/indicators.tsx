// 标签、进度条与加载圈（界面改版 §2.4）。

import type { ReactNode } from 'react';
import { ProgressBar as AriaProgressBar } from 'react-aria-components';
import { cls } from './cls';
import { Icon, type IconName } from './Icon';
import './controls.css';

export type TagTone = 'neutral' | 'accent' | 'ok' | 'warn' | 'error';

export function Tag(props: { children: ReactNode; tone?: TagTone; icon?: IconName; className?: string; title?: string }) {
  return (
    <span className={cls('tag', props.tone && props.tone !== 'neutral' && props.tone, props.className)} title={props.title}>
      {props.icon && <Icon name={props.icon} />}
      {props.children}
    </span>
  );
}

/** 线性进度：给出 value（0～100）是确定进度，不给是不确定进度。 */
export function ProgressBar(props: { label: string; value?: number | undefined; className?: string; showLabel?: boolean }) {
  return (
    <AriaProgressBar
      className={cls('progress', props.value === undefined && 'indeterminate', props.className)}
      aria-label={props.label}
      {...(props.value === undefined ? { isIndeterminate: true } : { value: props.value })}
    >
      {({ percentage, valueText, isIndeterminate }) => (
        <>
          {props.showLabel && (
            <span className="progress-label">
              <span>{props.label}</span>
              {!isIndeterminate && <span className="tnum">{valueText}</span>}
            </span>
          )}
          <span className="progress-track" style={{ '--v': `${isIndeterminate ? 0 : (percentage ?? 0)}%` } as React.CSSProperties}>
            <span className="progress-fill" />
          </span>
        </>
      )}
    </AriaProgressBar>
  );
}

/** 环形加载圈：只表示“正在进行”，用于按钮和小块区域。 */
export function Spinner(props: { label?: string; className?: string }) {
  return (
    <AriaProgressBar className={cls('spinner', props.className)} aria-label={props.label ?? '正在加载'} isIndeterminate>
      <svg viewBox="0 0 24 24" aria-hidden>
        <circle cx="12" cy="12" r="9" />
      </svg>
    </AriaProgressBar>
  );
}
