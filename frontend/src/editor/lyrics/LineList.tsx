import { memo, useEffect, useRef, type MouseEvent, type ReactNode } from 'react';
import type { TimingStatus } from '../../protocol';
import { TIMING_STATUS_TEXT, isTimed, type EditorLine } from '../model/lyrics';
import { formatTime } from '../model/time';

export type LineAction = 'insertAfter' | 'duplicate' | 'remove' | 'resplit' | 'link';

/** 行列表里附加的说明：时间位置的替代文字（“跟随 第 3 行”）和正文下的一行（关联的主歌词或翻译）。 */
export interface LineNote {
  time?: string;
  extra?: string;
}

const ACTIONS: ReadonlyArray<{ action: LineAction; label: string; icon: ReactNode }> = [
  { action: 'insertAfter', label: '在下方插入', icon: <mdui-icon-playlist-add slot="icon" /> },
  { action: 'duplicate', label: '复制本行', icon: <mdui-icon-content-copy slot="icon" /> },
  { action: 'remove', label: '删除本行', icon: <mdui-icon-delete slot="icon" /> },
  { action: 'resplit', label: '按规则重切本行…', icon: <mdui-icon-call-split slot="icon" /> },
  { action: 'link', label: '关联到主歌词某行…', icon: <mdui-icon-link slot="icon" /> },
];

/**
 * 左栏行列表（整体设计 §5.2）。点击选中；Ctrl／⌘ 点击加选，Shift 点击连选；
 * 多选开关打开后每行出现复选框。每行的 ⋯ 菜单和右键菜单提供行操作。
 */
export const LineList = memo(function LineList(props: {
  lines: readonly EditorLine[];
  selectedLineId?: string | undefined;
  marked: ReadonlySet<string>;
  multi: boolean;
  /** 这个来源的行菜单里出现哪些操作。 */
  actions: readonly LineAction[];
  available: (action: LineAction) => boolean;
  notes?: ReadonlyMap<string, LineNote> | undefined;
  onSelect: (lineId: string) => void;
  onMark: (marked: ReadonlySet<string>) => void;
  onAction: (action: LineAction, line: EditorLine) => void;
}) {
  const container = useRef<HTMLDivElement>(null);
  const anchor = useRef<string | undefined>(undefined);

  useEffect(() => {
    if (!props.selectedLineId) return;
    const item = container.current?.querySelector(`[data-line-id="${CSS.escape(props.selectedLineId)}"]`);
    item?.scrollIntoView({ block: 'nearest' });
  }, [props.selectedLineId]);

  if (props.lines.length === 0) return null;

  const toggle = (lineId: string) => {
    const next = new Set(props.marked);
    if (next.has(lineId)) next.delete(lineId);
    else next.add(lineId);
    anchor.current = lineId;
    props.onMark(next);
  };

  const click = (event: MouseEvent, line: EditorLine) => {
    if (event.shiftKey) {
      const from = props.lines.findIndex((l) => l.id === (anchor.current ?? props.selectedLineId));
      const to = props.lines.findIndex((l) => l.id === line.id);
      if (from >= 0) {
        const [a, b] = from < to ? [from, to] : [to, from];
        props.onMark(new Set(props.lines.slice(a, b + 1).map((l) => l.id)));
        return;
      }
    }
    if (event.ctrlKey || event.metaKey) {
      toggle(line.id);
      return;
    }
    anchor.current = line.id;
    if (props.marked.size > 0 && !props.multi) props.onMark(new Set());
    props.onSelect(line.id);
  };

  return (
    <div className="line-list" ref={container}>
      <mdui-list>
        {props.lines.map((line, index) => (
          <LineRow
            key={line.id}
            line={line}
            index={index}
            active={line.id === props.selectedLineId}
            marked={props.marked.has(line.id)}
            multi={props.multi}
            actions={props.actions}
            available={props.available}
            note={props.notes?.get(line.id)}
            onClick={click}
            onToggle={toggle}
            onAction={props.onAction}
          />
        ))}
      </mdui-list>
    </div>
  );
});

const LineRow = memo(function LineRow(props: {
  line: EditorLine;
  index: number;
  active: boolean;
  marked: boolean;
  multi: boolean;
  actions: readonly LineAction[];
  available: (action: LineAction) => boolean;
  note?: LineNote | undefined;
  onClick: (event: MouseEvent, line: EditorLine) => void;
  onToggle: (lineId: string) => void;
  onAction: (action: LineAction, line: EditorLine) => void;
}) {
  const { line, note } = props;
  const menu = useRef<HTMLElement & { open: boolean }>(null);
  return (
    <mdui-list-item
      data-line-id={line.id}
      active={props.active}
      rounded
      className={props.marked ? 'line-row marked' : 'line-row'}
      onMouseDown={(event) => event.shiftKey && event.preventDefault()}
      onClick={(event) => props.onClick(event, line)}
      onContextMenu={(event) => {
        event.preventDefault();
        if (menu.current) menu.current.open = true;
      }}
    >
      {props.multi ? (
        <mdui-checkbox
          slot="icon"
          checked={props.marked}
          aria-label={`选择第 ${props.index + 1} 行`}
          onClick={(event) => {
            event.stopPropagation();
            props.onToggle(line.id);
          }}
        />
      ) : (
        <span slot="icon" className="line-index">
          {String(props.index + 1).padStart(2, '0')}
        </span>
      )}
      <span className="line-text">{line.text || '（空行）'}</span>
      <span slot="description" className="line-meta">
        {line.status && !note?.time && <StatusGlyph status={line.status} />}
        {note?.time ?? (isTimed(line) ? `${formatTime(line.startUs)} – ${formatTime(line.endUs)}` : '未打轴')}
        {note?.extra && <span className="line-note">{note.extra}</span>}
      </span>
      <mdui-dropdown slot="end-icon" placement="bottom-end" ref={menu} onClick={(event) => event.stopPropagation()}>
        <mdui-button-icon slot="trigger" className="line-menu" aria-label={`第 ${props.index + 1} 行操作`}>
          <mdui-icon-more-vert />
        </mdui-button-icon>
        <mdui-menu>
          {ACTIONS.filter((a) => props.actions.includes(a.action)).map((a) => (
            <mdui-menu-item key={a.action} disabled={!props.available(a.action)} onClick={() => props.onAction(a.action, line)}>
              {a.icon}
              {a.label}
            </mdui-menu-item>
          ))}
        </mdui-menu>
      </mdui-dropdown>
    </mdui-list-item>
  );
});

/** 时间状态图标（整体设计 §5.2）：✓ 已确认／已修改，◐ 估算或导入，○ 未打轴，⚠ 无效；悬停显示文字。 */
const STATUS_GLYPH: Record<TimingStatus, string> = { confirmed: '✓', edited: '✓', estimated: '◐', imported: '◐', missing: '○', invalid: '⚠' };

export function StatusGlyph(props: { status: TimingStatus }) {
  const text = TIMING_STATUS_TEXT[props.status];
  return (
    <span className={`status-glyph status-${props.status}`} title={text} aria-label={text}>
      {STATUS_GLYPH[props.status]}
    </span>
  );
}
