// 列表与树（界面改版 §2.4）：键盘导航、单选／多选、拖放排序、长列表虚拟滚动都交给 React Aria。
// 行内容由调用方渲染；选中、悬停、焦点的外观在 collections.css。

import type { CSSProperties, ReactNode } from 'react';
import {
  Button as AriaButton,
  Collection,
  DropIndicator,
  GridList,
  GridListItem,
  ListLayout,
  Tree as AriaTree,
  TreeItem,
  TreeItemContent,
  Virtualizer,
  useDragAndDrop,
  type Key,
  type Selection,
} from 'react-aria-components';
import { cls, defined } from './cls';
import { Icon } from './Icon';
import './collections.css';

export type { Key };

export interface RowState {
  isSelected: boolean;
  isFocusVisible: boolean;
}

/** React Aria 的 'all' 换成全部键。 */
function toKeySet(selection: Selection, all: () => Key[]): Set<Key> {
  return selection === 'all' ? new Set(all()) : new Set(selection);
}

export interface ListProps<T extends { id: Key }> {
  label: string;
  items: readonly T[];
  textValue: (item: T) => string;
  children: (item: T, state: RowState) => ReactNode;
  selectionMode?: 'none' | 'single' | 'multiple';
  selectedKeys?: Iterable<Key>;
  onSelectionChange?: (keys: Set<Key>) => void;
  /** 双击或回车。 */
  onAction?: (key: Key) => void;
  disabledKeys?: Iterable<Key>;
  /** 给出时可以拖动排序：被拖的键，落在哪一行的前或后。 */
  onReorder?: (keys: Key[], target: Key, position: 'before' | 'after') => void;
  /** 给出时虚拟滚动，每行固定这个高度（px）；行高不固定时给 estimatedRowSize。 */
  rowSize?: number;
  estimatedRowSize?: number;
  emptyText?: ReactNode;
  className?: string;
  rowClassName?: (item: T) => string | undefined;
  style?: CSSProperties;
}

export function List<T extends { id: Key }>(props: ListProps<T>) {
  const { dragAndDropHooks } = useDragAndDrop({
    getItems: (keys) => [...keys].map((key) => ({ 'text/plain': String(key) })),
    renderDropIndicator: (target) => <DropIndicator target={target} className="drop-line" />,
    ...(props.onReorder
      ? {
          onReorder: (event) => {
            if (event.target.dropPosition !== 'on') props.onReorder!([...event.keys], event.target.key, event.target.dropPosition);
          },
        }
      : {}),
  });
  const list = (
    <GridList
      aria-label={props.label}
      items={props.items}
      selectionMode={props.selectionMode ?? 'single'}
      selectionBehavior="replace"
      className={cls('list', props.className)}
      {...defined({ style: props.style })}
      renderEmptyState={() => (props.emptyText ? <div className="list-empty">{props.emptyText}</div> : null)}
      {...(props.selectedKeys ? { selectedKeys: props.selectedKeys } : {})}
      {...(props.onSelectionChange ? { onSelectionChange: (s: Selection) => props.onSelectionChange!(toKeySet(s, () => props.items.map((i) => i.id))) } : {})}
      {...(props.onAction ? { onAction: props.onAction } : {})}
      {...(props.disabledKeys ? { disabledKeys: props.disabledKeys } : {})}
      {...(props.onReorder ? { dragAndDropHooks } : {})}
    >
      {(item) => (
        <GridListItem id={item.id} textValue={props.textValue(item)} className={cls('row', props.rowClassName?.(item))}>
          {(state) => (
            <>
              {props.onReorder && (
                <AriaButton slot="drag" className="drag">
                  <Icon name="drag_indicator" />
                </AriaButton>
              )}
              {props.children(item, state)}
            </>
          )}
        </GridListItem>
      )}
    </GridList>
  );
  if (props.rowSize === undefined && props.estimatedRowSize === undefined) return list;
  return (
    <Virtualizer layout={ListLayout} layoutOptions={props.rowSize !== undefined ? { rowSize: props.rowSize } : { estimatedRowSize: props.estimatedRowSize! }}>
      {list}
    </Virtualizer>
  );
}

export interface TreeData {
  id: Key;
  children?: readonly TreeData[];
}

export interface TreeRowState extends RowState {
  level: number;
  isExpanded: boolean;
  hasChildItems: boolean;
}

export function Tree<T extends TreeData>(props: {
  label: string;
  items: readonly T[];
  textValue: (item: T) => string;
  children: (item: T, state: TreeRowState) => ReactNode;
  selectionMode?: 'none' | 'single' | 'multiple';
  selectedKeys?: Iterable<Key>;
  onSelectionChange?: (keys: Set<Key>) => void;
  onAction?: (key: Key) => void;
  expandedKeys?: Iterable<Key>;
  defaultExpandedKeys?: Iterable<Key>;
  onExpandedChange?: (keys: Set<Key>) => void;
  disabledKeys?: Iterable<Key>;
  emptyText?: ReactNode;
  className?: string;
  rowClassName?: (item: T) => string | undefined;
}) {
  const allKeys = () => {
    const keys: Key[] = [];
    const walk = (nodes: readonly TreeData[]) => nodes.forEach((n) => (keys.push(n.id), n.children && walk(n.children)));
    walk(props.items);
    return keys;
  };
  const renderItem = (item: T): ReactNode => (
    <TreeItem id={item.id} textValue={props.textValue(item)} className={cls('tree-row', props.rowClassName?.(item))}>
      <TreeItemContent>
        {(state) => (
          <div className="tree-cell" style={{ '--d': state.level - 1 } as CSSProperties}>
            {state.hasChildItems ? (
              <AriaButton slot="chevron" className="caret">
                <Icon name="chevron_right" />
              </AriaButton>
            ) : (
              <span className="caret none" />
            )}
            {props.children(item, state)}
          </div>
        )}
      </TreeItemContent>
      {item.children && <Collection items={item.children as readonly T[]}>{renderItem}</Collection>}
    </TreeItem>
  );
  return (
    <AriaTree
      aria-label={props.label}
      items={props.items}
      selectionMode={props.selectionMode ?? 'single'}
      selectionBehavior="replace"
      className={cls('tree', props.className)}
      renderEmptyState={() => (props.emptyText ? <div className="list-empty">{props.emptyText}</div> : null)}
      {...(props.selectedKeys ? { selectedKeys: props.selectedKeys } : {})}
      {...(props.onSelectionChange ? { onSelectionChange: (s: Selection) => props.onSelectionChange!(toKeySet(s, allKeys)) } : {})}
      {...(props.onAction ? { onAction: props.onAction } : {})}
      {...(props.expandedKeys ? { expandedKeys: props.expandedKeys } : {})}
      {...(props.defaultExpandedKeys ? { defaultExpandedKeys: props.defaultExpandedKeys } : {})}
      {...(props.onExpandedChange ? { onExpandedChange: (keys: Set<Key>) => props.onExpandedChange!(new Set(keys)) } : {})}
      {...(props.disabledKeys ? { disabledKeys: props.disabledKeys } : {})}
    >
      {renderItem}
    </AriaTree>
  );
}
