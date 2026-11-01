// 对话框与右侧面板（界面改版 §2.4）。Esc 和点遮罩都走 onClose；dismissable 为 false 时（例如提交中）不能关。

import type { CSSProperties, ReactNode } from 'react';
import { Dialog as AriaDialog, Heading, Modal, ModalOverlay } from 'react-aria-components';
import { IconButton } from './Button';
import { cls, defined } from './cls';
import './overlays.css';

export function Dialog(props: {
  open: boolean;
  onClose: () => void;
  title: ReactNode;
  /** 标题下的一段说明。 */
  description?: ReactNode;
  children?: ReactNode;
  /** 底部按钮，从左到右；主要操作放最右。 */
  actions?: ReactNode;
  /** 标题行右侧的内容（例如分步对话框的步骤）。 */
  headerExtra?: ReactNode;
  dismissable?: boolean;
  /** 需要用户明确选择的提示（确认删除等）。 */
  alert?: boolean;
  className?: string;
  style?: CSSProperties;
}) {
  const dismissable = props.dismissable ?? true;
  return (
    <ModalOverlay
      isOpen={props.open}
      onOpenChange={(open) => {
        if (!open) props.onClose();
      }}
      isDismissable={dismissable}
      isKeyboardDismissDisabled={!dismissable}
      className="scrim"
    >
      <Modal className={cls('dialog', props.className)} {...defined({ style: props.style })}>
        <AriaDialog className="dialog-inner" role={props.alert ? 'alertdialog' : 'dialog'}>
          <header className="dhead">
            <Heading slot="title">{props.title}</Heading>
            {props.headerExtra}
          </header>
          {props.description && <p className="ddesc">{props.description}</p>}
          {props.children !== undefined && <div className="dbody">{props.children}</div>}
          {props.actions && <footer className="dfoot">{props.actions}</footer>}
        </AriaDialog>
      </Modal>
    </ModalOverlay>
  );
}

/** 右侧面板：工程设置、应用设置、管理来源。 */
export function Sheet(props: {
  open: boolean;
  onClose: () => void;
  title: ReactNode;
  children: ReactNode;
  /** 标题行里关闭按钮左边的操作。 */
  headerActions?: ReactNode;
  /** 标题下、可滚动内容之上的固定区域（例如页签）。 */
  toolbar?: ReactNode;
  className?: string;
}) {
  return (
    <ModalOverlay
      isOpen={props.open}
      onOpenChange={(open) => {
        if (!open) props.onClose();
      }}
      isDismissable
      className="sheet-scrim"
    >
      <Modal className={cls('sheet', props.className)}>
        <AriaDialog className="sheet-inner">
          <header className="shead">
            <Heading slot="title">{props.title}</Heading>
            <span className="spacer" />
            {props.headerActions}
            <IconButton icon="close" label="关闭" onPress={props.onClose} />
          </header>
          {props.toolbar}
          <div className="sbody">{props.children}</div>
        </AriaDialog>
      </Modal>
    </ModalOverlay>
  );
}
