// 函数式对话框（界面改版 §2.4）：问用户未保存时如何关闭、确认删除、重排时间槽、填一小段文字。
// 接口与旧的 mdui 版（ui/legacy/dialogs.ts）相同。请求排成队列，由根上的 <DialogHost /> 一次显示一个。

import { useState } from 'react';
import { ObservableValue } from '../../client';
import { Button, type ButtonVariant } from './Button';
import { Dialog } from './Dialog';
import { TextField } from './fields';
import { useObservable } from './hooks';

interface ChoiceRequest {
  kind: 'choice';
  id: number;
  headline: string;
  description: string;
  /** 从左到右；cancel 是 Esc、点遮罩时的结果。 */
  actions: { text: string; value: unknown; variant?: ButtonVariant; danger?: boolean }[];
  cancel: unknown;
  stacked?: boolean;
  resolve: (value: unknown) => void;
}

interface TextRequest {
  kind: 'text';
  id: number;
  headline: string;
  label: string;
  confirmText: string;
  resolve: (value: unknown) => void;
}

type Request = ChoiceRequest | TextRequest;

const queue = new ObservableValue<readonly Request[]>([]);
let nextId = 1;

function ask<T>(request: Omit<ChoiceRequest, 'resolve' | 'id'> | Omit<TextRequest, 'resolve' | 'id'>): Promise<T> {
  return new Promise<T>((resolve) => {
    queue.update((current) => [...current, { ...request, id: nextId++, resolve: resolve as (value: unknown) => void } as Request]);
  });
}

export type CloseChoice = 'save' | 'discard' | 'cancel';

export function askCloseUnsaved(): Promise<CloseChoice> {
  return ask<CloseChoice>({
    kind: 'choice',
    headline: '工程有未保存的修改',
    description: '关闭前要保存吗？',
    cancel: 'cancel',
    actions: [
      { text: '取消', value: 'cancel', variant: 'text' },
      { text: '不保存', value: 'discard', variant: 'text' },
      { text: '保存后关闭', value: 'save', variant: 'filled' },
    ],
  });
}

/** 确认框；确认返回 true，取消、Esc 或点遮罩返回 false。 */
export function confirmAction(options: { headline: string; description: string; confirmText: string; danger?: boolean }): Promise<boolean> {
  return ask<boolean>({
    kind: 'choice',
    headline: options.headline,
    description: options.description,
    cancel: false,
    actions: [
      { text: '取消', value: false, variant: 'text' },
      { text: options.confirmText, value: true, variant: 'filled', danger: !!options.danger },
    ],
  });
}

/** 拖动排序后问时间怎么办（整体设计 §5.2，后端 reassignTimeSlots）；取消返回 null。 */
export function askReorderSlots(): Promise<boolean | null> {
  return ask<boolean | null>({
    kind: 'choice',
    headline: '调整文本单元顺序',
    description: '已打轴的时间怎么处理？',
    cancel: null,
    stacked: true,
    actions: [
      { text: '时间槽跟着新顺序重排', value: true, variant: 'tonal' },
      { text: '保持每个单元原来的时间', value: false, variant: 'tonal' },
      { text: '取消', value: null, variant: 'text' },
    ],
  });
}

/** 填一小段文字（例如新注音片段）；空白不能提交。确认返回去掉首尾空白的文字，取消、Esc 或点遮罩返回 null。 */
export function askText(options: { headline: string; label: string; confirmText: string }): Promise<string | null> {
  return ask<string | null>({ kind: 'text', ...options });
}

/** 挂在根上，显示队列里的第一个请求。 */
export function DialogHost() {
  const [current] = useObservable(queue);
  if (!current) return null;
  const finish = (value: unknown) => {
    queue.update((list) => list.filter((r) => r !== current));
    current.resolve(value);
  };
  return current.kind === 'choice' ? <ChoiceDialog key={current.id} request={current} onFinish={finish} /> : <TextDialog key={current.id} request={current} onFinish={finish} />;
}

function ChoiceDialog(props: { request: ChoiceRequest; onFinish: (value: unknown) => void }) {
  const { request } = props;
  return (
    <Dialog
      open
      alert
      onClose={() => props.onFinish(request.cancel)}
      title={request.headline}
      description={request.description}
      className={request.stacked ? 'ask stacked' : 'ask'}
      actions={request.actions.map((action) => (
        <Button key={action.text} variant={action.variant ?? 'text'} danger={!!action.danger} onPress={() => props.onFinish(action.value)}>
          {action.text}
        </Button>
      ))}
    />
  );
}

function TextDialog(props: { request: TextRequest; onFinish: (value: unknown) => void }) {
  const { request } = props;
  const [text, setText] = useState('');
  const [touched, setTouched] = useState(false);
  const empty = !text.trim();
  const submit = () => {
    setTouched(true);
    if (!empty) props.onFinish(text.trim());
  };
  return (
    <Dialog
      open
      onClose={() => props.onFinish(null)}
      title={request.headline}
      className="ask"
      actions={
        <>
          <Button variant="text" onPress={() => props.onFinish(null)}>
            取消
          </Button>
          <Button variant="filled" onPress={submit}>
            {request.confirmText}
          </Button>
        </>
      }
    >
      <TextField label={request.label} value={text} onChange={setText} onEnter={submit} error={touched && empty ? '不能为空' : undefined} autoFocus />
    </Dialog>
  );
}
