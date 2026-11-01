// 过渡期（界面改版 §4）：旧的 mdui 版封装。页面逐批改用 ui/ 下的新控件，批 7 删除本目录。
// mdui 表单组件的受控薄封装（设计 §3.2）：值经 property 写入，变化经 DOM 事件读出。

import { useEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { formatColor, parseColor, type Rgba } from '../../model/settings';
import { useDomEvent } from '../hooks';

type Valued = HTMLElement & { value: string };
type Validatable = Valued & { setCustomValidity(message: string): void; updateComplete: Promise<boolean> };
type Checked = HTMLElement & { checked: boolean };

export function TextField(props: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  onEnter?: () => void;
  onEscape?: () => void;
  onBlur?: () => void;
  helper?: string;
  error?: string | undefined;
  disabled?: boolean;
  placeholder?: string;
  className?: string;
  style?: CSSProperties;
  variant?: 'filled' | 'outlined';
  ariaLabel?: string;
  /** 给出行数时是多行输入框（Enter 换行，不要同时给 onEnter）。 */
  rows?: number;
}) {
  const ref = useRef<Validatable>(null);
  useDomEvent(ref, 'input', () => props.onChange(ref.current!.value));
  // 错误经 setCustomValidity 交给 mdui 显示（红色状态与错误文案），清空时恢复 helper。
  // Lit 元素首次渲染是异步的，内部 input 出现前调用会抛错，所以等 updateComplete。
  const error = props.error ?? '';
  useEffect(() => {
    const element = ref.current;
    if (!element) return;
    let active = true;
    void element.updateComplete.then(() => {
      if (active) element.setCustomValidity(error);
    });
    return () => {
      active = false;
    };
  }, [error]);
  useDomEvent<KeyboardEvent>(ref, 'keydown', (event) => {
    if (event.key === 'Enter' && props.onEnter) {
      event.preventDefault();
      props.onEnter();
    } else if (event.key === 'Escape' && props.onEscape) {
      event.preventDefault();
      props.onEscape();
    }
  });
  useDomEvent(ref, 'blur', () => props.onBlur?.());
  return (
    <mdui-text-field
      ref={ref}
      variant={props.variant ?? 'outlined'}
      label={props.label}
      value={props.value}
      helper={props.helper ?? ''}
      disabled={!!props.disabled}
      placeholder={props.placeholder ?? ''}
      className={props.className}
      style={props.style}
      aria-label={props.ariaLabel ?? props.label}
      {...(props.rows ? { rows: props.rows } : {})}
    />
  );
}

interface SelectProps<T extends string> {
  label: string;
  value: T;
  /** disabled 的选项列出但不能选（仍可作为当前值显示）。 */
  options: readonly { value: T; label: string; disabled?: boolean }[];
  onChange: (value: T) => void;
  disabled?: boolean;
  className?: string;
}

export function Select<T extends string>(props: SelectProps<T>) {
  // 选中项的文字只在值变化时更新；选项文字变了（例如来源改名）就整个重建，让显示跟上。
  return <SelectField key={props.options.map((o) => `${o.value}\u0000${o.label}\u0000${!!o.disabled}`).join('\u0001')} {...props} />;
}

function SelectField<T extends string>(props: SelectProps<T>) {
  const ref = useRef<Valued>(null);
  useDomEvent(ref, 'change', () => {
    const value = ref.current!.value;
    // React 改 value 时 mdui-menu 也会发 change：值与受控值相同就不是用户的选择，忽略。
    if (value === props.value) return;
    // mdui-select 单选时，再次点击已选项会清空；忽略空值，保持受控值。
    if (value) props.onChange(value as T);
    else ref.current!.value = props.value;
  });
  return (
    <mdui-select ref={ref} variant="outlined" label={props.label} value={props.value} disabled={!!props.disabled} className={props.className}>
      {props.options.map((option) => (
        <mdui-menu-item key={option.value} value={option.value} disabled={!!option.disabled}>
          {option.label}
        </mdui-menu-item>
      ))}
    </mdui-select>
  );
}

/** 工具栏里的紧凑下拉框：原生 select，标签写在左边，高度和工具栏按钮一样（不用 mdui 的 56px 输入框）。 */
export function CompactSelect<T extends string>(props: SelectProps<T> & { title?: string }) {
  return (
    <label className={props.className ? `compact-select ${props.className}` : 'compact-select'} title={props.title}>
      <span className="compact-select-label">{props.label}</span>
      <select aria-label={props.label} value={props.value} disabled={!!props.disabled} onChange={(event) => props.onChange(event.currentTarget.value as T)}>
        {props.options.map((option) => (
          <option key={option.value} value={option.value} disabled={!!option.disabled}>
            {option.label}
          </option>
        ))}
      </select>
    </label>
  );
}

export function Switch(props: { label: ReactNode; checked: boolean; onChange: (checked: boolean) => void; disabled?: boolean }) {
  const ref = useRef<Checked>(null);
  useDomEvent(ref, 'change', () => props.onChange(ref.current!.checked));
  return (
    <label className="switch-field">
      <mdui-switch ref={ref} checked={props.checked} disabled={!!props.disabled} />
      <span>{props.label}</span>
    </label>
  );
}

/** 文件选择：mdui 按钮触发隐藏的原生 input。clearable 时选了文件后可以移除。 */
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
      <input
        ref={input}
        type="file"
        accept={props.accept}
        hidden
        aria-label={props.label}
        onChange={(event) => {
          props.onChange(event.currentTarget.files?.[0] ?? null);
          // 清空 value：移除后再选同一个文件也会触发 change。
          event.currentTarget.value = '';
        }}
      />
      <mdui-button variant="tonal" disabled={!!props.disabled} onClick={() => input.current?.click()}>
        {props.label}
      </mdui-button>
      <span className={props.file ? 'file-name' : 'file-name muted'}>{props.file ? props.file.name : (props.emptyText ?? '未选择')}</span>
      {props.clearable && props.file && (
        <mdui-button-icon disabled={!!props.disabled} onClick={() => props.onChange(null)} aria-label={`移除 ${props.file.name}`}>
          <mdui-icon-close />
        </mdui-button-icon>
      )}
    </div>
  );
}

/** 只是一个按钮：点了选文件，选好就回调，不显示文件名（替换音频、选择背景等）。 */
export function FileButton(props: {
  label: string;
  accept: string;
  onPick: (file: File) => void;
  disabled?: boolean;
  variant?: 'tonal' | 'text' | 'outlined' | 'filled';
  icon?: ReactNode;
}) {
  const input = useRef<HTMLInputElement>(null);
  return (
    <>
      <input
        ref={input}
        type="file"
        accept={props.accept}
        hidden
        aria-label={props.label}
        onChange={(event) => {
          const file = event.currentTarget.files?.[0];
          event.currentTarget.value = '';
          if (file) props.onPick(file);
        }}
      />
      <mdui-button variant={props.variant ?? 'tonal'} disabled={!!props.disabled} onClick={() => input.current?.click()}>
        {props.icon}
        {props.label}
      </mdui-button>
    </>
  );
}

/**
 * 提交式输入框（整体设计 §5.2 正文编辑）：草稿留在本地，Enter 或离开输入框时提交，Esc 撤回。
 * 权威值变化（命令成功、撤销、别处修改）时用新值覆盖草稿。
 */
export function CommitField(props: {
  label: string;
  value: string;
  onCommit: (value: string) => void;
  disabled?: boolean;
  placeholder?: string;
  helper?: string;
  className?: string;
  /** 返回错误文案时就地显示，且不提交。只校验与权威值不同的草稿。 */
  validate?: (value: string) => string | undefined;
}) {
  const [draft, setDraft] = useState(props.value);
  // Enter 之后紧接着失焦不再重复提交同一草稿。
  const sent = useRef<string | null>(null);
  useEffect(() => {
    setDraft(props.value);
    sent.current = null;
  }, [props.value]);
  const error = draft !== props.value ? props.validate?.(draft) : undefined;
  const commit = () => {
    if (draft === props.value || draft === sent.current || error) return;
    sent.current = draft;
    props.onCommit(draft);
  };
  return (
    <TextField
      label={props.label}
      value={draft}
      onChange={setDraft}
      onEnter={commit}
      onBlur={commit}
      onEscape={() => setDraft(props.value)}
      disabled={!!props.disabled}
      placeholder={props.placeholder ?? ''}
      error={error}
      {...(props.helper !== undefined ? { helper: props.helper } : {})}
      {...(props.className !== undefined ? { className: props.className } : {})}
    />
  );
}

/**
 * 颜色：原生取色器加十六进制输入框。取色器拖动时连续发 input，只在关闭取色器（change）时提交一次，
 * 取色器不管不透明度，提交时沿用原值；输入框接受 #RRGGBB 或 #RRGGBBAA。
 */
export function ColorField(props: { label: string; value: Rgba; onCommit: (color: Rgba) => void; disabled?: boolean; helper?: string }) {
  const picker = useRef<HTMLInputElement>(null);
  const latest = useRef(props);
  latest.current = props;
  useDomEvent(picker, 'change', () => {
    const parsed = parseColor(picker.current!.value);
    if (parsed.ok) latest.current.onCommit({ ...parsed.value, alpha: latest.current.value.alpha });
  });
  const hex = formatColor({ ...props.value, alpha: 255 });
  // 取色器不受控：React 受控的 input 会在每次 input 后把值改回去，拖动时跳回旧色。权威值变化时再写回。
  useEffect(() => {
    if (picker.current) picker.current.value = hex;
  }, [hex]);
  return (
    <div className="color-row">
      <input ref={picker} type="color" className="color-swatch" aria-label={`选择${props.label}`} defaultValue={hex} disabled={!!props.disabled} />
      <CommitField
        label={props.label}
        value={formatColor(props.value)}
        helper={props.helper ?? '#RRGGBB，或 #RRGGBBAA 带不透明度'}
        disabled={!!props.disabled}
        validate={(v) => {
          const parsed = parseColor(v);
          return parsed.ok ? undefined : parsed.error;
        }}
        onCommit={(v) => {
          const parsed = parseColor(v);
          if (parsed.ok) props.onCommit(parsed.value);
        }}
        className="color-field"
      />
    </div>
  );
}

/** 数值输入：提交式，按范围校验；min／max 含端点，greaterThan 不含。 */
export function NumberField(props: {
  label: string;
  value: number;
  onCommit: (value: number) => void;
  min?: number;
  max?: number;
  greaterThan?: number;
  integer?: boolean;
  suffix?: string;
  disabled?: boolean;
  className?: string;
  helper?: string;
  /** 范围之外的检查（例如安全区右边要大于左边）：返回原因时就地显示，不提交。 */
  check?: (value: number) => string | undefined;
}) {
  const parse = (text: string): number | string => {
    const value = Number(text.trim());
    if (!text.trim() || !Number.isFinite(value)) return '请输入数字。';
    if (props.integer && !Number.isInteger(value)) return '请输入整数。';
    if (props.greaterThan !== undefined && value <= props.greaterThan) return `必须大于 ${props.greaterThan}。`;
    if (props.min !== undefined && value < props.min) return `不能小于 ${props.min}。`;
    if (props.max !== undefined && value > props.max) return `不能大于 ${props.max}。`;
    return props.check?.(value) ?? value;
  };
  return (
    <CommitField
      label={props.suffix ? `${props.label}（${props.suffix}）` : props.label}
      value={String(props.value)}
      disabled={!!props.disabled}
      validate={(v) => {
        const parsed = parse(v);
        return typeof parsed === 'string' ? parsed : undefined;
      }}
      onCommit={(v) => {
        const parsed = parse(v);
        if (typeof parsed === 'number' && parsed !== props.value) props.onCommit(parsed);
      }}
      {...(props.className !== undefined ? { className: props.className } : {})}
      {...(props.helper !== undefined ? { helper: props.helper } : {})}
    />
  );
}

type SliderElement = HTMLElement & { value: number; labelFormatter: (value: number) => string };

/** 滑块：拖动中只更新显示，松手（change）才提交，连续拖动只算一次修改。 */
export function SliderField(props: {
  label: string;
  value: number;
  min: number;
  max: number;
  step: number;
  format: (value: number) => string;
  onCommit: (value: number) => void;
  disabled?: boolean;
}) {
  const ref = useRef<SliderElement>(null);
  const [shown, setShown] = useState(props.value);
  useEffect(() => setShown(props.value), [props.value]);
  useEffect(() => {
    if (ref.current) ref.current.labelFormatter = props.format;
  }, [props.format]);
  useDomEvent(ref, 'input', () => setShown(Number(ref.current!.value)));
  useDomEvent(ref, 'change', () => {
    const value = Number(ref.current!.value);
    if (value !== props.value) props.onCommit(value);
  });
  return (
    <label className="slider-field">
      <span className="slider-label">
        {props.label}
        <span className="slider-value">{props.format(shown)}</span>
      </span>
      <mdui-slider ref={ref} min={props.min} max={props.max} step={props.step} value={props.value} disabled={!!props.disabled} aria-label={props.label} />
    </label>
  );
}

/** 页签条（受控）：切换被拦下（例如未保存确认选了取消）时按受控值改回来。 */
export function Tabs<T extends string>(props: { value: T; options: readonly { value: T; label: string }[]; onChange: (value: T) => void; className: string }) {
  const ref = useRef<HTMLElement & { value: string }>(null);
  useDomEvent(ref, 'change', () => {
    const value = props.options.find((t) => t.value === ref.current?.value)?.value;
    if (value && value !== props.value) props.onChange(value);
  });
  // 切换被拦下（未保存确认选了取消）时，mdui 已经把页签换过去了，按受控值改回来。
  useEffect(() => {
    if (ref.current && ref.current.value !== props.value) ref.current.value = props.value;
  });
  return (
    <mdui-tabs ref={ref} value={props.value} className={props.className}>
      {props.options.map((t) => (
        <mdui-tab key={t.value} value={t.value}>
          {t.label}
        </mdui-tab>
      ))}
    </mdui-tabs>
  );
}
