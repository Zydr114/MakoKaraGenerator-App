// 输入类控件（界面改版 §2.4）：填充式输入框、提交式输入框、数值、颜色、下拉选择、文件。
// 接口沿用旧的 mdui 封装（ui/legacy/fields.tsx），页面改版时只换导入路径和少数布局属性。
// 标签有四种摆法：top 在框上方（对话框、表单），side 在左边一列（外观检查器），inside 在框内左边（编排检查器的紧凑网格），
// none 不显示（工具栏，只作可访问名称）。

import { useEffect, useId, useRef, useState, type CSSProperties, type DragEvent, type KeyboardEvent, type ReactNode } from 'react';
import {
  Button as AriaButton,
  FieldError,
  Input,
  Label,
  ListBox,
  ListBoxItem,
  Popover,
  Select as AriaSelect,
  SelectValue,
  Text,
  TextArea,
  TextField as AriaTextField,
} from 'react-aria-components';
import { acceptsFile } from '../model/media';
import { formatColor, parseColor, type Rgba } from '../model/settings';
import { Button, IconButton, type ButtonVariant } from './Button';
import { cls, defined } from './cls';
import { Icon, type IconName } from './Icon';
import './controls.css';

export type LabelPlacement = 'top' | 'side' | 'inside' | 'none';

interface FieldLayout {
  labelPlacement?: LabelPlacement;
  /** sm 是 28 高的紧凑输入框。 */
  size?: 'sm' | 'md';
  className?: string;
  style?: CSSProperties;
}

export interface TextFieldProps extends FieldLayout {
  label: string;
  value: string;
  onChange: (value: string) => void;
  onEnter?: () => void;
  onEscape?: () => void;
  onBlur?: () => void;
  helper?: string;
  error?: string | undefined;
  disabled?: boolean;
  readOnly?: boolean;
  placeholder?: string;
  /** 可访问名称与可见标签不同时给出。 */
  ariaLabel?: string;
  /** 给出行数时是多行输入框（Enter 换行，不要同时给 onEnter）。 */
  rows?: number;
  /** 框内左侧的图标（例如搜索）。 */
  icon?: IconName;
  /** 框内左侧的其他内容（例如颜色色块）。 */
  lead?: ReactNode;
  /** 框内右侧的单位。 */
  suffix?: string;
  autoFocus?: boolean;
}

export function TextField(props: TextFieldProps) {
  const placement = props.labelPlacement ?? 'top';
  const onKeyDown = (event: KeyboardEvent) => {
    if (event.key === 'Enter' && props.onEnter && !event.nativeEvent.isComposing) {
      event.preventDefault();
      props.onEnter();
    } else if (event.key === 'Escape' && props.onEscape) {
      event.preventDefault();
      props.onEscape();
    }
  };
  // 键盘处理挂在原生输入元素上：React Aria 的 onKeyDown 会阻止冒泡，全局快捷键（保存等）就收不到了。
  const inputProps = { placeholder: props.placeholder ?? '', onKeyDown, ...(props.onBlur ? { onBlur: props.onBlur } : {}) };
  return (
    <AriaTextField
      value={props.value}
      onChange={props.onChange}
      isDisabled={!!props.disabled}
      isReadOnly={!!props.readOnly}
      isInvalid={!!props.error}
      autoFocus={!!props.autoFocus}
      className={cls('tf', placement, props.className)}
      {...defined({ style: props.style })}
      {...(placement === 'none' || props.ariaLabel ? { 'aria-label': props.ariaLabel ?? props.label } : {})}
    >
      {(placement === 'top' || placement === 'side') && <Label className="lbl">{props.label}</Label>}
      <div className={cls('field', props.size === 'sm' && 'sm', !!props.rows && 'multi')}>
        {placement === 'inside' && <Label className="in-lbl">{props.label}</Label>}
        {props.icon && <Icon name={props.icon} className="lead" />}
        {props.lead}
        {props.rows ? <TextArea rows={props.rows} {...inputProps} /> : <Input {...inputProps} />}
        {props.suffix && <em className="unit">{props.suffix}</em>}
      </div>
      {props.error ? (
        <FieldError className="help err">{props.error}</FieldError>
      ) : (
        props.helper && (
          <Text slot="description" className="help">
            {props.helper}
          </Text>
        )
      )}
    </AriaTextField>
  );
}

export interface CommitFieldProps extends FieldLayout {
  label: string;
  value: string;
  onCommit: (value: string) => void;
  disabled?: boolean;
  placeholder?: string;
  helper?: string;
  icon?: IconName;
  lead?: ReactNode;
  suffix?: string;
  /** 返回错误文案时就地显示，且不提交。只校验与权威值不同的草稿。 */
  validate?: (value: string) => string | undefined;
}

/**
 * 提交式输入框（整体设计 §5.2 正文编辑）：草稿留在本地，Enter 或离开输入框时提交，Esc 撤回。
 * 权威值变化（命令成功、撤销、别处修改）时用新值覆盖草稿。
 */
export function CommitField({ value, onCommit, validate, ...rest }: CommitFieldProps) {
  const [draft, setDraft] = useState(value);
  // Enter 之后紧接着失焦不再重复提交同一草稿。
  const sent = useRef<string | null>(null);
  useEffect(() => {
    setDraft(value);
    sent.current = null;
  }, [value]);
  const error = draft !== value ? validate?.(draft) : undefined;
  const commit = () => {
    if (draft === value || draft === sent.current || error) return;
    sent.current = draft;
    onCommit(draft);
  };
  return <TextField {...rest} value={draft} onChange={setDraft} onEnter={commit} onBlur={commit} onEscape={() => setDraft(value)} error={error} />;
}

/** 数值输入：提交式，按范围校验，不合法时写明原因且不提交；min／max 含端点，greaterThan 不含。单位写在框内。 */
export function NumberField(
  props: FieldLayout & {
    label: string;
    value: number;
    onCommit: (value: number) => void;
    min?: number;
    max?: number;
    greaterThan?: number;
    integer?: boolean;
    suffix?: string;
    disabled?: boolean;
    helper?: string;
    /** 范围之外的检查（例如安全区右边要大于左边）：返回原因时就地显示，不提交。 */
    check?: (value: number) => string | undefined;
  },
) {
  const { value, onCommit, min, max, greaterThan, integer, check, ...rest } = props;
  const parse = (text: string): number | string => {
    const parsed = Number(text.trim());
    if (!text.trim() || !Number.isFinite(parsed)) return '请输入数字。';
    if (integer && !Number.isInteger(parsed)) return '请输入整数。';
    if (greaterThan !== undefined && parsed <= greaterThan) return `必须大于 ${greaterThan}。`;
    if (min !== undefined && parsed < min) return `不能小于 ${min}。`;
    if (max !== undefined && parsed > max) return `不能大于 ${max}。`;
    return check?.(parsed) ?? parsed;
  };
  return (
    <CommitField
      {...rest}
      className={cls('num', rest.className)}
      value={String(value)}
      validate={(text) => {
        const parsed = parse(text);
        return typeof parsed === 'string' ? parsed : undefined;
      }}
      onCommit={(text) => {
        const parsed = parse(text);
        if (typeof parsed === 'number' && parsed !== value) onCommit(parsed);
      }}
    />
  );
}

/**
 * 颜色：框内左边是色块（点开系统取色器），右边是十六进制。取色器拖动时连续发 input，只在关闭（change）时提交一次，
 * 取色器不管不透明度，提交时沿用原值；输入框接受 #RRGGBB 或 #RRGGBBAA。
 */
export function ColorField(props: FieldLayout & { label: string; value: Rgba; onCommit: (color: Rgba) => void; disabled?: boolean; helper?: string }) {
  const picker = useRef<HTMLInputElement>(null);
  const latest = useRef(props);
  latest.current = props;
  const hex = formatColor({ ...props.value, alpha: 255 });
  // 取色器不受控：受控的 input 会在每次 input 后改回原值，拖动时跳回旧色。权威值变化时再写回。
  useEffect(() => {
    if (picker.current) picker.current.value = hex;
  }, [hex]);
  useEffect(() => {
    const input = picker.current;
    if (!input) return;
    const onChange = () => {
      const parsed = parseColor(input.value);
      if (parsed.ok) latest.current.onCommit({ ...parsed.value, alpha: latest.current.value.alpha });
    };
    input.addEventListener('change', onChange);
    return () => input.removeEventListener('change', onChange);
  }, []);
  const { value, onCommit, ...rest } = props;
  return (
    <CommitField
      {...rest}
      className={cls('color-input', rest.className)}
      value={formatColor(value)}
      lead={
        <label className="swatch" style={{ '--c': formatColor(value) } as CSSProperties} title={`选择${props.label}`}>
          <input ref={picker} type="color" aria-label={`选择${props.label}`} defaultValue={hex} disabled={!!props.disabled} />
        </label>
      }
      validate={(text) => {
        const parsed = parseColor(text);
        return parsed.ok ? undefined : parsed.error;
      }}
      onCommit={(text) => {
        const parsed = parseColor(text);
        if (parsed.ok) onCommit(parsed.value);
      }}
    />
  );
}

export interface SelectOption<T extends string> {
  value: T;
  label: string;
  /** 列出但不能选（仍可作为当前值显示）。 */
  disabled?: boolean;
  description?: string;
}

export interface SelectProps<T extends string> extends FieldLayout {
  label: string;
  value: T;
  options: readonly SelectOption<T>[];
  onChange: (value: T) => void;
  disabled?: boolean;
  icon?: IconName;
  /** 工具栏里不显示标签时的提示。 */
  title?: string;
}

export function Select<T extends string>(props: SelectProps<T>) {
  const placement = props.labelPlacement ?? 'top';
  return (
    <AriaSelect
      value={props.value}
      onChange={(key) => {
        if (key !== null && key !== props.value) props.onChange(key as T);
      }}
      isDisabled={!!props.disabled}
      disabledKeys={props.options.filter((o) => o.disabled).map((o) => o.value)}
      className={cls('tf', placement, props.className)}
      {...defined({ style: props.style })}
      {...(placement === 'none' || placement === 'inside' ? { 'aria-label': props.label } : {})}
    >
      {(placement === 'top' || placement === 'side') && <Label className="lbl">{props.label}</Label>}
      <AriaButton className={cls('field', 'select', props.size === 'sm' && 'sm')} {...(props.title ? { 'aria-description': props.title } : {})}>
        {/* 按钮里不能放 label 元素：框内标签只是看的，可访问名称用上面的 aria-label */}
        {placement === 'inside' && (
          <b className="in-lbl" aria-hidden>
            {props.label}
          </b>
        )}
        {props.icon && <Icon name={props.icon} className="lead" />}
        <SelectValue className="select-value" />
        <Icon name="arrow_drop_down" className="arrow" />
      </AriaButton>
      <Popover className="popover" offset={4} placement="bottom start">
        <ListBox className="menu-list">
          {props.options.map((option) => (
            <ListBoxItem key={option.value} id={option.value} textValue={option.label} className="menu-item">
              {({ isSelected }) => (
                <>
                  <span className="menu-text">
                    {option.label}
                    {option.description && (
                      <Text slot="description" className="menu-desc">
                        {option.description}
                      </Text>
                    )}
                  </span>
                  {isSelected && <Icon name="check" className="menu-check" />}
                </>
              )}
            </ListBoxItem>
          ))}
        </ListBox>
      </Popover>
    </AriaSelect>
  );
}

/** 文件选择：按钮触发隐藏的原生 input，旁边写已选的文件名。clearable 时选了文件后可以移除。 */
export function FilePicker(props: {
  label: string;
  accept: string;
  file: File | null;
  onChange: (file: File | null) => void;
  disabled?: boolean;
  clearable?: boolean;
  /** 没选文件时的说明，缺省为“未选择”。 */
  emptyText?: string;
}) {
  const input = useRef<HTMLInputElement>(null);
  return (
    <div className="file-picker">
      <HiddenFileInput ref={input} label={props.label} accept={props.accept} onPick={props.onChange} />
      <Button variant="tonal" icon="upload_file" isDisabled={!!props.disabled} onPress={() => input.current?.click()}>
        {props.label}
      </Button>
      <span className={props.file ? 'file-name' : 'file-name muted'}>{props.file ? props.file.name : (props.emptyText ?? '未选择')}</span>
      {props.clearable && props.file && <IconButton icon="close" size="sm" label={`移除 ${props.file.name}`} isDisabled={!!props.disabled} onPress={() => props.onChange(null)} />}
    </div>
  );
}

/**
 * 文件框（稿件的新建工程）：输入框底色的一长条，写已选的文件名和说明，可以更换、移除，也可以把文件拖进来。
 * 隐藏的文件输入框的可访问名称是“选择＋标签”（例如“选择音频”）。
 */
export function FileBox(props: {
  label: string;
  accept: string;
  file: File | null;
  onChange: (file: File | null) => void;
  icon: IconName;
  /** 没选文件时的说明。 */
  emptyText: string;
  /** 文件名后面的说明（大小等）。 */
  detail?: string | undefined;
  /** 拖进来的文件不符合 accept 时的说明。 */
  rejectText: (file: File) => string;
  disabled?: boolean;
}) {
  const input = useRef<HTMLInputElement>(null);
  const [over, setOver] = useState(false);
  const [rejected, setRejected] = useState<string>();
  const labelId = useId();
  const disabled = !!props.disabled;
  const pick = (file: File | null) => {
    setRejected(undefined);
    props.onChange(file);
  };
  const onDrop = (event: DragEvent) => {
    event.preventDefault();
    setOver(false);
    const file = event.dataTransfer.files[0];
    if (!file || disabled) return;
    if (acceptsFile(file, props.accept)) pick(file);
    else setRejected(props.rejectText(file));
  };
  return (
    <div className="tf">
      <span className="lbl" id={labelId}>
        {props.label}
      </span>
      <div
        role="group"
        aria-labelledby={labelId}
        className={cls('filebox', over && 'over', disabled && 'disabled')}
        onDragOver={(event) => {
          if (disabled || !event.dataTransfer.types.includes('Files')) return;
          event.preventDefault();
          setOver(true);
        }}
        onDragLeave={() => setOver(false)}
        onDrop={onDrop}
      >
        <HiddenFileInput ref={input} label={`选择${props.label}`} accept={props.accept} onPick={(file) => file && pick(file)} />
        <Icon name={props.icon} />
        {props.file ? (
          <span className="filebox-text">
            <b className="file-name">{props.file.name}</b>
            {props.detail && <span className="filebox-detail">{props.detail}</span>}
          </span>
        ) : (
          <span className="filebox-text filebox-empty">{props.emptyText}</span>
        )}
        <Button variant="ghost" size="sm" isDisabled={disabled} onPress={() => input.current?.click()}>
          {props.file ? '更换' : '选择…'}
        </Button>
        {props.file && <IconButton icon="close" size="sm" label={`移除${props.label}`} isDisabled={disabled} onPress={() => pick(null)} />}
      </div>
      {rejected && <span className="help err">{rejected}</span>}
    </div>
  );
}

/** 只是一个按钮：点了选文件，选好就回调，不显示文件名（替换音频、选择背景等）。 */
export function FileButton(props: { label: string; accept: string; onPick: (file: File) => void; disabled?: boolean; variant?: ButtonVariant; icon?: IconName; size?: 'sm' | 'md' }) {
  const input = useRef<HTMLInputElement>(null);
  return (
    <>
      <HiddenFileInput ref={input} label={props.label} accept={props.accept} onPick={(file) => file && props.onPick(file)} />
      <Button variant={props.variant ?? 'tonal'} size={props.size ?? 'md'} isDisabled={!!props.disabled} onPress={() => input.current?.click()} {...(props.icon ? { icon: props.icon } : {})}>
        {props.label}
      </Button>
    </>
  );
}

function HiddenFileInput(props: { ref: React.Ref<HTMLInputElement>; label: string; accept: string; onPick: (file: File | null) => void }) {
  return (
    <input
      ref={props.ref}
      type="file"
      accept={props.accept}
      hidden
      aria-label={props.label}
      onChange={(event) => {
        props.onPick(event.currentTarget.files?.[0] ?? null);
        // 清空 value：移除后再选同一个文件也会触发 change。
        event.currentTarget.value = '';
      }}
    />
  );
}

/** 只读的一行说明加右侧操作，常用在检查器里（例如“外观来源”）。 */
export function FieldNote(props: { icon?: IconName; children: ReactNode; action?: ReactNode }) {
  return (
    <div className="field-note">
      {props.icon && <Icon name={props.icon} />}
      <span>{props.children}</span>
      {props.action}
    </div>
  );
}
