// 对话框与右侧面板（界面改版 §2.4）。Esc 和点遮罩都走 onClose；dismissable 为 false 时（例如提交中）不能关。

import type { CSSProperties, ReactNode } from 'react';
import { Dialog as AriaDialog, Heading, Modal, ModalOverlay } from 'react-aria-components';
import { IconButton } from './Button';
import { cls, defined } from './cls';
import { Icon } from './Icon';
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
  /** 右边的幕后侧栏（例如新建工程的文件检查）。给出时对话框加宽，左边是表单，右边是这一栏。 */
  side?: ReactNode;
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
      <Modal className={cls('dialog', props.side !== undefined && 'has-side', props.className)} {...defined({ style: props.style })}>
        <AriaDialog className="dialog-inner" role={props.alert ? 'alertdialog' : 'dialog'}>
          <div className="dmain">
            <header className="dhead">
              <Heading slot="title">{props.title}</Heading>
              {props.headerExtra}
            </header>
            {props.description && <p className="ddesc">{props.description}</p>}
            {props.children !== undefined && <div className="dbody">{props.children}</div>}
            {props.actions && <footer className="dfoot">{props.actions}</footer>}
          </div>
          {props.side !== undefined && (
            <aside className="dside">
              <div className="stage-zone">{props.side}</div>
            </aside>
          )}
        </AriaDialog>
      </Modal>
    </ModalOverlay>
  );
}

/** 分步对话框标题旁的步骤：做过的、当前的、没到的。 */
export function DialogSteps(props: { steps: readonly string[]; current: number }) {
  return (
    <ol className="dsteps" aria-label="步骤">
      {props.steps.map((step, i) => (
        <li key={step} className={cls(i === props.current && 'on', i < props.current && 'done')} {...(i === props.current ? { 'aria-current': 'step' as const } : {})}>
          <b>{i < props.current ? <Icon name="check" /> : i + 1}</b>
          {step}
        </li>
      ))}
    </ol>
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
  /** 关闭按钮的可访问名称，缺省是“关闭”。 */
  closeLabel?: string;
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
            <IconButton icon="close" label={props.closeLabel ?? '关闭'} onPress={props.onClose} />
          </header>
          {props.toolbar}
          <div className="sbody">{props.children}</div>
        </AriaDialog>
      </Modal>
    </ModalOverlay>
  );
}
