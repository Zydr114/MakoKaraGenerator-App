// 按钮（界面改版 §2.4）：普通按钮与图标按钮，高 32，紧凑处 28。

import type { CSSProperties, ReactNode } from 'react';
import { Button as AriaButton, ToggleButton, type ButtonProps as AriaButtonProps } from 'react-aria-components';
import { cls } from './cls';
import { Icon, type IconName } from './Icon';
import { Spinner } from './indicators';
import { Tooltip } from './Tooltip';
import './controls.css';

export type ButtonVariant = 'filled' | 'tonal' | 'outlined' | 'text' | 'ghost';
export type ButtonSize = 'sm' | 'md' | 'lg';

type BaseProps = Omit<AriaButtonProps, 'children' | 'className' | 'style'> & {
  className?: string;
  style?: CSSProperties;
};

export interface ButtonProps extends BaseProps {
  /** 缺省是描边按钮。filled 用于一处最主要的操作，tonal 用于次要但常用的操作。 */
  variant?: ButtonVariant;
  size?: ButtonSize;
  icon?: IconName;
  trailingIcon?: IconName;
  /** 删除等不可撤回的操作。 */
  danger?: boolean;
  children?: ReactNode;
}

export function Button({ variant = 'outlined', size = 'md', icon, trailingIcon, danger, className, children, ...rest }: ButtonProps) {
  return (
    <AriaButton {...rest} className={cls('btn', variant, size !== 'md' && size, danger && 'danger', className)}>
      {rest.isPending ? <Spinner /> : icon && <Icon name={icon} />}
      {children}
      {trailingIcon && <Icon name={trailingIcon} className="trail" />}
    </AriaButton>
  );
}

export interface IconButtonProps extends BaseProps {
  icon: IconName;
  /** 可访问名称，也是提示文字。 */
  label: string;
  variant?: 'standard' | 'filled' | 'tonal' | 'outlined';
  size?: 'sm' | 'md';
  /** 给出时是开关按钮（例如循环播放）。 */
  selected?: boolean;
  onSelectedChange?: (selected: boolean) => void;
  /** 选中时用填充版图标。 */
  fillWhenSelected?: boolean;
  /** 提示文字；false 不显示提示（例如旁边已有文字）。缺省用 label。 */
  tooltip?: string | false;
}

export function IconButton({ icon, label, variant = 'standard', size = 'md', selected, onSelectedChange, fillWhenSelected, tooltip, className, ...rest }: IconButtonProps) {
  const classes = cls('btn', 'icon', variant !== 'standard' && variant, size === 'sm' && 'sm', className);
  const button =
    selected === undefined ? (
      <AriaButton {...rest} aria-label={label} className={classes}>
        {rest.isPending ? <Spinner /> : <Icon name={icon} />}
      </AriaButton>
    ) : (
      <ToggleButton
        isDisabled={!!rest.isDisabled}
        aria-label={label}
        className={classes}
        isSelected={selected}
        {...(onSelectedChange ? { onChange: onSelectedChange } : {})}
      >
        <Icon name={icon} filled={!!fillWhenSelected && selected} />
      </ToggleButton>
    );
  return tooltip === false ? button : <Tooltip label={tooltip ?? label}>{button}</Tooltip>;
}
