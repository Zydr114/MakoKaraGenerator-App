// 菜单（界面改版 §2.4）：触发按钮加弹出菜单。
//   <MenuTrigger>
//     <IconButton icon="more_vert" label="更多" />
//     <Menu onAction={run}>
//       <MenuItem id="saveAs" icon="save">另存为…</MenuItem>
//     </Menu>
//   </MenuTrigger>

import { createContext, useContext, type ReactNode } from 'react';
import {
  Header,
  Menu as AriaMenu,
  MenuItem as AriaMenuItem,
  MenuSection as AriaMenuSection,
  MenuTrigger as AriaMenuTrigger,
  Popover,
  Separator,
  SubmenuTrigger,
  Text,
  type Key,
  type MenuTriggerProps,
  type Selection,
} from 'react-aria-components';
import { cls, defined } from './cls';
import { Icon, type IconName } from './Icon';
import './controls.css';

export { SubmenuTrigger };

// React Aria 的子菜单只调用自己的 onAction；子菜单没给时沿用外层菜单的，调用方只写一处。
const MenuActionContext = createContext<((id: Key) => void) | undefined>(undefined);

export function MenuTrigger(props: MenuTriggerProps) {
  return <AriaMenuTrigger {...props} />;
}

export function Menu(props: {
  children: ReactNode;
  onAction?: (id: Key) => void;
  label?: string;
  placement?: 'bottom start' | 'bottom end' | 'top start' | 'top end' | 'end top';
  className?: string;
  /** 勾选式菜单（例如渲染设备）：单选或多选，已选项带对勾。 */
  selectionMode?: 'single' | 'multiple';
  selectedKeys?: Iterable<Key>;
  onSelectionChange?: (keys: Selection) => void;
}) {
  const inherited = useContext(MenuActionContext);
  const onAction = props.onAction ?? inherited;
  return (
    <Popover className="popover" offset={4} placement={props.placement ?? 'bottom start'}>
      {/* 包在 AriaMenu 外面：菜单项（含子菜单）在 AriaMenu 里面渲染，传给它的 children 只用来建集合。 */}
      <MenuActionContext.Provider value={onAction}>
        <AriaMenu
          className={cls('menu-list', props.className)}
          {...(props.label ? { 'aria-label': props.label } : {})}
          {...(onAction ? { onAction } : {})}
          {...(props.selectionMode ? { selectionMode: props.selectionMode } : {})}
          {...(props.selectedKeys ? { selectedKeys: props.selectedKeys } : {})}
          {...(props.onSelectionChange ? { onSelectionChange: props.onSelectionChange } : {})}
        >
          {props.children}
        </AriaMenu>
      </MenuActionContext.Provider>
    </Popover>
  );
}

export function MenuItem(props: {
  id: Key;
  children: ReactNode;
  icon?: IconName;
  /** 右侧的快捷键文字，例如 Ctrl+S。 */
  shortcut?: string;
  description?: ReactNode;
  disabled?: boolean;
  danger?: boolean;
  /** 读屏用的文字；children 不是纯文字时给出。 */
  textValue?: string;
  onAction?: () => void;
}) {
  return (
    <AriaMenuItem
      id={props.id}
      isDisabled={!!props.disabled}
      className={cls('menu-item', props.danger && 'danger')}
      {...defined({ textValue: props.textValue ?? (typeof props.children === 'string' ? props.children : undefined), onAction: props.onAction })}
    >
      {({ isSelected, selectionMode, hasSubmenu }) => (
        <>
          {selectionMode !== 'none' ? <Icon name="check" className={cls('menu-check', !isSelected && 'off')} /> : props.icon && <Icon name={props.icon} />}
          <span className="menu-text">
            <Text slot="label">{props.children}</Text>
            {props.description && (
              <Text slot="description" className="menu-desc">
                {props.description}
              </Text>
            )}
          </span>
          {props.shortcut && <kbd className="menu-kbd">{props.shortcut}</kbd>}
          {hasSubmenu && <Icon name="chevron_right" className="menu-sub" />}
        </>
      )}
    </AriaMenuItem>
  );
}

export function MenuSection(props: { title?: string; children: ReactNode }) {
  return (
    <AriaMenuSection className="menu-section">
      {props.title && <Header className="menu-head">{props.title}</Header>}
      {props.children}
    </AriaMenuSection>
  );
}

export function MenuSeparator() {
  return <Separator className="menu-sep" />;
}
