// 页签（界面改版 §2.4）：line 是下划线页签（面板里的页签），pill 是胶囊页签（顶栏的页面、侧边面板）。
// 受控：切换被拦下（例如未保存确认选了取消）时 onChange 不改值，页签停在原处。

import type { ReactNode } from 'react';
import { Tab, TabList, TabPanel, Tabs as AriaTabs } from 'react-aria-components';
import { cls } from './cls';
import { Icon, type IconName } from './Icon';
import './controls.css';

export interface TabOption<T extends string> {
  value: T;
  label: string;
  icon?: IconName;
  /** 页签上的数字或标记。 */
  badge?: ReactNode;
  disabled?: boolean;
}

export function Tabs<T extends string>(props: {
  label: string;
  value: T;
  options: readonly TabOption<T>[];
  onChange: (value: T) => void;
  variant?: 'line' | 'pill';
  className?: string;
  /** 各页签的内容；不给时只有页签条，内容由调用方自己摆。 */
  children?: (value: T) => ReactNode;
  panelClassName?: string;
}) {
  const variant = props.variant ?? 'line';
  return (
    <AriaTabs
      selectedKey={props.value}
      onSelectionChange={(key) => {
        if (key !== props.value) props.onChange(key as T);
      }}
      disabledKeys={props.options.filter((o) => o.disabled).map((o) => o.value)}
      className={cls('tabs', props.children && 'with-panels', props.className)}
    >
      <TabList aria-label={props.label} className={cls('tab-list', variant)}>
        {props.options.map((option) => (
          <Tab key={option.value} id={option.value} className="tab">
            {({ isSelected }) => (
              <>
                {option.icon && <Icon name={option.icon} filled={isSelected} />}
                {option.label}
                {option.badge !== undefined && <span className="badge">{option.badge}</span>}
              </>
            )}
          </Tab>
        ))}
      </TabList>
      {props.children &&
        props.options.map((option) => (
          <TabPanel key={option.value} id={option.value} className={cls('tab-panel', props.panelClassName)}>
            {props.children!(option.value)}
          </TabPanel>
        ))}
    </AriaTabs>
  );
}
