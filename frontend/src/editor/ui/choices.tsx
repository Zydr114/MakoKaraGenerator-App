// 选择类控件（界面改版 §2.4）：开关、复选框、单选（含卡片式）、分段按钮、滑块。

import { useEffect, useState, type CSSProperties, type ReactNode } from 'react';
import {
  CheckboxButton,
  CheckboxField,
  Label,
  RadioButton,
  RadioField,
  RadioGroup as AriaRadioGroup,
  Slider as AriaSlider,
  SliderOutput,
  SliderThumb,
  SliderTrack,
  SwitchButton,
  SwitchField,
  ToggleButton,
  ToggleButtonGroup,
} from 'react-aria-components';
import { cls, defined } from './cls';
import { Icon, type IconName } from './Icon';
import './controls.css';

export function Switch(props: {
  label: ReactNode;
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
  /** 标签只作可访问名称（例如分组标题里的开关）。 */
  hideLabel?: boolean;
  /** 标签在开关左边。 */
  labelFirst?: boolean;
  className?: string;
}) {
  const label = props.hideLabel ? undefined : <span className="switch-label">{props.label}</span>;
  return (
    <SwitchField isSelected={props.checked} onChange={props.onChange} isDisabled={!!props.disabled} className={cls('switch', props.className)}>
      <SwitchButton className="switch-btn" {...(props.hideLabel && typeof props.label === 'string' ? { 'aria-label': props.label } : {})}>
        {props.labelFirst && label}
        <span className="track" aria-hidden />
        {!props.labelFirst && label}
      </SwitchButton>
    </SwitchField>
  );
}

export function Checkbox(props: { label: ReactNode; checked: boolean; onChange: (checked: boolean) => void; indeterminate?: boolean; disabled?: boolean; className?: string }) {
  return (
    <CheckboxField
      isSelected={props.checked}
      isIndeterminate={!!props.indeterminate}
      onChange={props.onChange}
      isDisabled={!!props.disabled}
      className={cls('checkbox', props.className)}
    >
      <CheckboxButton className="checkbox-btn">
        {({ isSelected, isIndeterminate }) => (
          <>
            <span className="box" aria-hidden>
              {isIndeterminate ? <i className="dash" /> : isSelected && <Icon name="check" />}
            </span>
            <span className="checkbox-label">{props.label}</span>
          </>
        )}
      </CheckboxButton>
    </CheckboxField>
  );
}

export interface ChoiceOption<T extends string> {
  value: T;
  label: string;
  description?: ReactNode;
  disabled?: boolean;
  icon?: IconName;
}

/** 单选：list 是一列圆点，card 是带说明的卡片（例如导出格式）。 */
export function RadioGroup<T extends string>(props: {
  label: string;
  value: T;
  options: readonly ChoiceOption<T>[];
  onChange: (value: T) => void;
  variant?: 'list' | 'card';
  orientation?: 'vertical' | 'horizontal';
  hideLabel?: boolean;
  disabled?: boolean;
  className?: string;
}) {
  return (
    <AriaRadioGroup
      value={props.value}
      onChange={(value) => {
        if (value !== props.value) props.onChange(value as T);
      }}
      isDisabled={!!props.disabled}
      orientation={props.orientation ?? 'vertical'}
      className={cls('radio-group', props.variant === 'card' && 'cards', props.className)}
      {...(props.hideLabel ? { 'aria-label': props.label } : {})}
    >
      {!props.hideLabel && <Label className="lbl">{props.label}</Label>}
      <div className="radio-options">
        {props.options.map((option) => (
          <RadioField key={option.value} value={option.value} isDisabled={!!option.disabled} className="radio">
            <RadioButton className="radio-btn">
              <span className="dot" aria-hidden />
              <span className="radio-text">
                <span className="radio-label">{option.label}</span>
                {option.description && <span className="radio-desc">{option.description}</span>}
              </span>
            </RadioButton>
          </RadioField>
        ))}
      </div>
    </AriaRadioGroup>
  );
}

/** 分段按钮：二到五个互斥选项，选中项带对勾。 */
export function Segmented<T extends string>(props: {
  label: string;
  value: T;
  options: readonly ChoiceOption<T>[];
  onChange: (value: T) => void;
  size?: 'sm' | 'md';
  disabled?: boolean;
  /** 撑满所在宽度，各段等分。 */
  stretch?: boolean;
  className?: string;
}) {
  return (
    <ToggleButtonGroup
      aria-label={props.label}
      selectionMode="single"
      disallowEmptySelection
      selectedKeys={[props.value]}
      onSelectionChange={(keys) => {
        const [key] = [...keys];
        if (key !== undefined && key !== props.value) props.onChange(key as T);
      }}
      isDisabled={!!props.disabled}
      className={cls('seg', props.size === 'sm' && 'sm', props.stretch && 'stretch', props.className)}
    >
      {props.options.map((option) => (
        <ToggleButton key={option.value} id={option.value} isDisabled={!!option.disabled} className="seg-btn">
          {({ isSelected }) => (
            <>
              {isSelected ? <Icon name="check" /> : option.icon && <Icon name={option.icon} />}
              {option.label}
            </>
          )}
        </ToggleButton>
      ))}
    </ToggleButtonGroup>
  );
}

/** 滑块：拖动中只更新显示，松手才提交，连续拖动只算一次修改。 */
export function SliderField(props: {
  label: string;
  value: number;
  min: number;
  max: number;
  step: number;
  format: (value: number) => string;
  onCommit: (value: number) => void;
  /** 拖动中的值（例如缩放时间轴要跟手）。 */
  onPreview?: (value: number) => void;
  disabled?: boolean;
  /** 标签只作可访问名称，数值也不显示（工具栏里）。 */
  hideLabel?: boolean;
  className?: string;
  style?: CSSProperties;
}) {
  const [shown, setShown] = useState(props.value);
  useEffect(() => setShown(props.value), [props.value]);
  return (
    <AriaSlider
      value={shown}
      minValue={props.min}
      maxValue={props.max}
      step={props.step}
      isDisabled={!!props.disabled}
      onChange={(value) => {
        setShown(value);
        props.onPreview?.(value);
      }}
      onChangeEnd={(value) => {
        if (value !== props.value) props.onCommit(value);
      }}
      className={cls('slider-field', props.hideLabel && 'bare', props.className)}
      {...defined({ style: props.style })}
      {...(props.hideLabel ? { 'aria-label': props.label } : {})}
    >
      {!props.hideLabel && (
        <div className="slider-head">
          <Label className="lbl">{props.label}</Label>
          <SliderOutput className="slider-value">{() => props.format(shown)}</SliderOutput>
        </div>
      )}
      <SliderTrack className="slider">
        {({ state }) => (
          <>
            <span className="slider-rail" style={{ '--v': `${state.getThumbPercent(0) * 100}%` } as CSSProperties} />
            <SliderThumb className="slider-thumb" />
          </>
        )}
      </SliderTrack>
    </AriaSlider>
  );
}
