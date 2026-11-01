import type { ReactNode } from 'react';
import { Button } from './Button';
import { cls } from './cls';
import { Dialog } from './Dialog';

/**
 * 表单对话框：Esc、点遮罩、“取消”都走 onClose；提交按钮在 busy 或 submitDisabled 时禁用，提交中不允许关闭。
 * 接口与旧的 mdui 版（ui/legacy/FormDialog.tsx）相同。
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
  /** 标题行右侧的内容（例如分步对话框的步骤）。 */
  headerExtra?: ReactNode;
  children: ReactNode;
}) {
  const busy = !!props.busy;
  return (
    <Dialog
      open={props.open}
      onClose={() => {
        if (!busy) props.onClose();
      }}
      dismissable={!busy}
      title={props.headline}
      description={props.description}
      headerExtra={props.headerExtra}
      className={cls('form-dialog', props.className)}
      actions={
        <>
          {props.secondaryAction && (
            <Button variant="text" className="dialog-secondary-action" isDisabled={busy || !!props.secondaryAction.disabled} onPress={props.secondaryAction.onClick}>
              {props.secondaryAction.text}
            </Button>
          )}
          <Button variant="text" isDisabled={busy} onPress={props.onClose}>
            取消
          </Button>
          <Button variant="filled" isPending={busy} isDisabled={!!props.submitDisabled} onPress={props.onSubmit}>
            {props.submitText}
          </Button>
        </>
      }
    >
      {props.open && props.children}
    </Dialog>
  );
}
