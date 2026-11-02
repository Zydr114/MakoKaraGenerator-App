// 成功反馈（整体设计 §4.6，界面改版 §2.4）：底部提示条约 4 秒，新的顶替旧的，不排队；错误不用会自动消失的提示。
// 由根上的 <ToastHost /> 显示。

import { useEffect, useState } from 'react';
import { ObservableValue } from '../../client';
import { Button, IconButton } from './Button';
import { useObservable } from './hooks';
import './overlays.css';

interface ToastMessage {
  id: number;
  message: string;
  action?: { label: string; run: () => void };
}

const current = new ObservableValue<ToastMessage | undefined>(undefined);
let nextId = 1;

/** 显示一条成功消息；新消息顶替旧消息，不排队。 */
export function toast(message: string, action?: { label: string; run: () => void }): void {
  current.set({ id: nextId++, message, ...(action ? { action } : {}) });
}

const SHOW_MS = 4000;

export function ToastHost() {
  const shown = useObservable(current);
  // 鼠标停在提示条上或焦点在里面时不计时，移开后重新计时。
  const [held, setHeld] = useState(false);
  useEffect(() => {
    if (!shown || held) return;
    const timer = setTimeout(() => {
      if (current.get() === shown) current.set(undefined);
    }, SHOW_MS);
    return () => clearTimeout(timer);
  }, [shown, held]);
  const dismiss = () => current.set(undefined);
  // 区域一直在：先有 aria-live 区域，后放进去的文字才会被读出。
  return (
    <div className="toast-region" role="status" aria-live="polite">
      {shown && (
        <div
          key={shown.id}
          className="toast"
          onPointerEnter={() => setHeld(true)}
          onPointerLeave={() => setHeld(false)}
          onFocus={() => setHeld(true)}
          onBlur={() => setHeld(false)}
        >
          <span className="toast-text">{shown.message}</span>
          {shown.action && (
            <Button
              variant="text"
              size="sm"
              onPress={() => {
                shown.action!.run();
                dismiss();
              }}
            >
              {shown.action.label}
            </Button>
          )}
          <IconButton icon="close" size="sm" label="关闭提示" tooltip={false} onPress={dismiss} />
        </div>
      )}
    </div>
  );
}
