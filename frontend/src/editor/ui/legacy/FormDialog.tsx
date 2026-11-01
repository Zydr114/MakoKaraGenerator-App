// 过渡期（界面改版 §4）：旧的 mdui 版封装。页面逐批改用 ui/ 下的新控件，批 7 删除本目录。
import { useRef, type ReactNode } from 'react';
import { useDomEvent } from '../hooks';

/**
 * 表单对话框：mdui-dialog 的受控封装。Esc、点遮罩、“取消”都走 onClose；
 * 提交按钮在 busy 或 submitDisabled 时禁用，提交中不允许关闭。
 */
export function FormDialog(props: {
  open: boolean;
  headline: string;
  description?: string;
  submitText: string;
  submitDisabled?: boolean;
  busy?: boolean;
  onSubmit: () => void;
  onClose: () => void;
  className?: string;
  /** 取消左边的另一个动作（例如分步对话框的“上一步”）。 */
  secondaryAction?: { text: string; onClick: () => void; disabled?: boolean };
  children: ReactNode;
}) {
  const ref = useRef<HTMLElement & { open: boolean }>(null);
  // mdui 自己关闭（Esc、遮罩）时发 close；受控关闭（props.open 已是 false）也会发，这时不拦也不回调。
  // 拦下 close（preventDefault）会让 mdui 把 open 改回 true，所以只在用户关闭且提交中时拦。
  useDomEvent(ref, 'close', (event) => {
    if (event.target !== ref.current || !props.open) return;
    if (props.busy) {
      event.preventDefault();
      return;
    }
    props.onClose();
  });
  return (
    <mdui-dialog ref={ref} open={props.open} headline={props.headline} description={props.description ?? ''} close-on-esc close-on-overlay-click className={props.className ? `form-dialog ${props.className}` : 'form-dialog'}>
      {props.open && props.children}
      {props.secondaryAction && (
        <mdui-button slot="action" variant="text" className="dialog-secondary-action" disabled={!!props.busy || !!props.secondaryAction.disabled} onClick={props.secondaryAction.onClick}>
          {props.secondaryAction.text}
        </mdui-button>
      )}
      <mdui-button slot="action" variant="text" disabled={!!props.busy} onClick={props.onClose}>
        取消
      </mdui-button>
      <mdui-button slot="action" variant="tonal" loading={!!props.busy} disabled={!!props.submitDisabled || !!props.busy} onClick={props.onSubmit}>
        {props.submitText}
      </mdui-button>
    </mdui-dialog>
  );
}
