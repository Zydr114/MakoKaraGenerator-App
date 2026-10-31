// 成功反馈（整体设计 §4.6）：底部 snackbar 约 4 秒；错误不用会自动消失的提示。

import { snackbar } from 'mdui/functions/snackbar.js';

let current: ReturnType<typeof snackbar> | undefined;

/** 显示一条成功消息；新消息顶替旧消息，不排队。 */
export function toast(message: string, action?: { label: string; run: () => void }): void {
  if (current) current.open = false;
  current = snackbar({
    message,
    placement: 'bottom',
    autoCloseDelay: 4000,
    ...(action ? { action: action.label, onActionClick: () => action.run() } : {}),
  });
}
