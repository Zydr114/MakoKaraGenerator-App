// 过渡期（界面改版 §4）：旧的 mdui 版封装。页面逐批改用 ui/ 下的新控件，批 7 删除本目录。
// 用 mdui 的函数式对话框问用户：未保存时如何关闭、确认删除、重排时间槽、填一小段文字。

import { confirm } from 'mdui/functions/confirm.js';
import { dialog } from 'mdui/functions/dialog.js';
import { prompt } from 'mdui/functions/prompt.js';

export type CloseChoice = 'save' | 'discard' | 'cancel';

export function askCloseUnsaved(): Promise<CloseChoice> {
  return new Promise((resolve) => {
    let choice: CloseChoice = 'cancel';
    dialog({
      headline: '工程有未保存的修改',
      description: '关闭前要保存吗？',
      closeOnEsc: true,
      actions: [
        { text: '取消' },
        { text: '不保存', onClick: () => void (choice = 'discard') },
        { text: '保存后关闭', onClick: () => void (choice = 'save') },
      ],
      onClosed: () => resolve(choice),
    });
  });
}

/** 确认框；确认返回 true，取消、Esc 或点遮罩返回 false。 */
export async function confirmAction(options: { headline: string; description: string; confirmText: string }): Promise<boolean> {
  try {
    await confirm({ ...options, cancelText: '取消', closeOnEsc: true, closeOnOverlayClick: true });
    return true;
  } catch {
    return false;
  }
}

/** 拖动排序后问时间怎么办（整体设计 §5.2，后端 reassignTimeSlots）；取消返回 null。 */
export function askReorderSlots(): Promise<boolean | null> {
  return new Promise((resolve) => {
    let choice: boolean | null = null;
    dialog({
      headline: '调整文本单元顺序',
      description: '已打轴的时间怎么处理？',
      closeOnEsc: true,
      closeOnOverlayClick: true,
      stackedActions: true,
      actions: [
        { text: '时间槽跟着新顺序重排', onClick: () => void (choice = true) },
        { text: '保持每个单元原来的时间', onClick: () => void (choice = false) },
        { text: '取消' },
      ],
      onClosed: () => resolve(choice),
    });
  });
}

/** 填一小段文字（例如新注音片段）；空白不能提交。确认返回文字，取消、Esc 或点遮罩返回 null。 */
export async function askText(options: { headline: string; label: string; confirmText: string }): Promise<string | null> {
  try {
    const value = await prompt({
      headline: options.headline,
      confirmText: options.confirmText,
      cancelText: '取消',
      closeOnEsc: true,
      closeOnOverlayClick: true,
      textFieldOptions: { label: options.label },
      validator: (text) => (text.trim() ? true : '不能为空'),
    });
    return value.trim();
  } catch {
    return null;
  }
}
