import { memo, useEffect, useRef, type MouseEvent } from 'react';
import type { TimingStatus } from '../../protocol';
import { TIMING_STATUS_TEXT, type EditorLine } from '../model/lyrics';
import { formatTime } from '../model/time';
import { cls } from '../ui/cls';
import { List, type Key } from '../ui/collections';
import { Icon, type IconName } from '../ui/Icon';
import { Tag } from '../ui/indicators';

/** 行列表里附加的说明：时间位置的替代文字（“跟随 第 3 行”）和正文下的一行（关联的主歌词或翻译）。 */
export interface LineNote {
  time?: string;
  extra?: string;
}

/** 行高固定（两行文字），长列表按它虚拟滚动；行间距与 collections.css 的 .list 一致。 */
const ROW = 50;
const GAP = 2;

/**
 * 左栏行列表（整体设计 §5.2，界面改版稿件 #lyrics）：单击选中这一行来编辑；Ctrl／⌘ 点击加选，Shift 点击连选，
 * 选了两行以上就是多选（批量删除）。多选开关打开后单击就是勾选，正在编辑的行不变。
 * 右键一行：选中它并在指针处打开行操作菜单。长列表虚拟滚动。
 */
export const LineList = memo(function LineList(props: {
  lines: readonly EditorLine[];
  selectedLineId?: string | undefined;
  marked: ReadonlySet<string>;
  multi: boolean;
  notes?: ReadonlyMap<string, LineNote> | undefined;
  onSelect: (lineId: string) => void;
  onMark: (marked: ReadonlySet<string>) => void;
  onContextMenu?: (lineId: string, at: { x: number; y: number }) => void;
}) {
  const container = useRef<HTMLDivElement>(null);
  const { lines, notes, multi, marked } = props;
  const rows = lines.map((line, index) => ({ id: line.id, line, index }));

  // 选中的行不在视野里时滚过去（虚拟滚动时不在视野里的行没有渲染，按行高算位置）。
  useEffect(() => {
    const list = container.current?.querySelector<HTMLElement>('.list');
    const index = props.selectedLineId ? lines.findIndex((l) => l.id === props.selectedLineId) : -1;
    if (!list || index < 0) return;
    const top = index * (ROW + GAP);
    if (top < list.scrollTop) list.scrollTop = top;
    else if (top + ROW > list.scrollTop + list.clientHeight) list.scrollTop = top + ROW - list.clientHeight;
  }, [props.selectedLineId]);

  const selectedKeys = multi || marked.size > 0 ? [...marked] : props.selectedLineId ? [props.selectedLineId] : [];
  const change = (keys: Set<Key>) => {
    const ids = [...keys].map(String);
    if (multi || ids.length > 1) {
      props.onMark(new Set(ids));
      return;
    }
    if (marked.size > 0) props.onMark(new Set());
    if (ids[0]) props.onSelect(ids[0]);
  };

  const contextMenu = (event: MouseEvent) => {
    const id = (event.target as HTMLElement).closest<HTMLElement>('[data-line-id]')?.dataset.lineId;
    if (!id || !props.onContextMenu) return;
    event.preventDefault();
    props.onContextMenu(id, { x: event.clientX, y: event.clientY });
  };

  return (
    <div className={cls('line-list', multi && 'multi')} ref={container} onContextMenu={contextMenu}>
      <List
        label="歌词行"
        items={rows}
        textValue={(r) => `${r.index + 1} ${r.line.text}`}
        selectionMode="multiple"
        selectionBehavior={multi ? 'toggle' : 'replace'}
        selectedKeys={selectedKeys}
        onSelectionChange={change}
        rowSize={ROW}
        rowClassName={(r) => cls('line-row', r.line.id === props.selectedLineId && 'editing', !r.line.status || r.line.status === 'missing' ? 'untimed' : undefined)}
      >
        {(r, state) => <LineRow line={r.line} index={r.index} note={notes?.get(r.id)} checked={multi ? state.isSelected : undefined} />}
      </List>
    </div>
  );
});

function LineRow(props: { line: EditorLine; index: number; note?: LineNote | undefined; checked: boolean | undefined }) {
  const { line, note } = props;
  const time = note?.time ?? (line.startUs !== undefined ? formatTime(line.startUs) : '未打轴');
  return (
    <span className="ll" data-line-id={line.id}>
      {props.checked !== undefined && <span className={cls('ll-check', props.checked && 'on')}>{props.checked && <Icon name="check" />}</span>}
      <span className="ll-n">{props.index + 1}</span>
      <span className="ll-body">
        <span className="ll-text" lang="ja">
          {line.text || '（空行）'}
        </span>
        <span className="ll-sub">
          <span className="ll-note">{note?.extra}</span>
          <span className="ll-time">{time}</span>
        </span>
      </span>
      {line.status && !note?.time ? <StatusIcon status={line.status} /> : <span className="status-icon" />}
    </span>
  );
}

/** 时间状态图标（整体设计 §5.2）：已确认／已修改、估算或导入、未打轴、无效；悬停显示文字。 */
const STATUS_ICON: Record<TimingStatus, { icon: IconName; filled?: boolean; tone: 'ok' | 'warn' | 'error' | 'none' }> = {
  confirmed: { icon: 'check_circle', filled: true, tone: 'ok' },
  edited: { icon: 'check_circle', filled: true, tone: 'ok' },
  estimated: { icon: 'contrast', tone: 'warn' },
  imported: { icon: 'contrast', tone: 'warn' },
  missing: { icon: 'radio_button_unchecked', tone: 'none' },
  invalid: { icon: 'error', filled: true, tone: 'error' },
};

export function StatusIcon(props: { status: TimingStatus; className?: string }) {
  const text = TIMING_STATUS_TEXT[props.status];
  const { icon, filled, tone } = STATUS_ICON[props.status];
  return (
    <span className={cls('status-icon', `tone-${tone}`, props.className)} title={text} role="img" aria-label={text}>
      <Icon name={icon} filled={!!filled} />
    </span>
  );
}

/** 时间状态标签（行编辑区标题、时间栏）：颜色同状态图标。 */
export function StatusTag(props: { status: TimingStatus }) {
  const { tone } = STATUS_ICON[props.status];
  return <Tag tone={tone === 'none' ? 'neutral' : tone}>{TIMING_STATUS_TEXT[props.status]}</Tag>;
}

/** 列表底部的统计：已确认（含已修改）、估算或导入、未打轴、无效。 */
export function statusCounts(lines: readonly EditorLine[]): { ok: number; partial: number; missing: number; invalid: number } {
  const counts = { ok: 0, partial: 0, missing: 0, invalid: 0 };
  for (const line of lines) {
    const tone = line.status ? STATUS_ICON[line.status].tone : 'none';
    if (tone === 'ok') counts.ok++;
    else if (tone === 'warn') counts.partial++;
    else if (tone === 'error') counts.invalid++;
    else counts.missing++;
  }
  return counts;
}
