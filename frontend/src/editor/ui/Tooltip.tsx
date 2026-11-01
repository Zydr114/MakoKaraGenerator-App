// 提示（界面改版 §2.4）：悬停或键盘聚焦后出现，子元素要能聚焦（Button、IconButton 等 React Aria 控件）。

import type { ReactElement, ReactNode } from 'react';
import { Tooltip as AriaTooltip, TooltipTrigger } from 'react-aria-components';
import './controls.css';

export function Tooltip(props: { label: ReactNode; children: ReactElement; placement?: 'top' | 'bottom' | 'start' | 'end'; disabled?: boolean }) {
  return (
    <TooltipTrigger delay={600} closeDelay={100} isDisabled={!!props.disabled}>
      {props.children}
      <AriaTooltip className="tooltip" offset={6} placement={props.placement ?? 'bottom'}>
        {props.label}
      </AriaTooltip>
    </TooltipTrigger>
  );
}
