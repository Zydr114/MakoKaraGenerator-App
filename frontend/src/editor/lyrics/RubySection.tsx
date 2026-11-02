import type { EditorLine } from '../model/lyrics';
import { TIMING_STATUS_TEXT } from '../model/lyrics';
import {
  groupTarget,
  movedOrder,
  removeRubyEdits,
  rubyOf,
  rubyTextLocked,
  segmentTarget,
  segmentTimed,
  selectedSegment,
  splitBlocked,
  splitEdits,
} from '../model/ruby-segments';
import type { EditorSession } from '../session/editor-session';
import { IconButton } from '../ui/Button';
import { cls } from '../ui/cls';
import { askText } from '../ui/dialogs';
import { CommitField } from '../ui/fields';
import { Menu, MenuItem, MenuTrigger } from '../ui/Menu';
import { StatusIcon } from './LineList';

/**
 * 注音（W6 设计 §2.4）：所选文本单元的注音组。整段文字只在没有注音或只有一个没有时间的片段时能直接改；
 * 片段一排小块，点选后可以改字、左右移、删除；加片段；标题栏的菜单里是按字拆开、删除注音。
 */
export function RubySection(props: { session: EditorSession; line: EditorLine; tokenId?: string | undefined; segmentId?: string | undefined }) {
  const { session, line } = props;
  const token = props.tokenId ? line.tokens.find((t) => t.id === props.tokenId) : undefined;
  const ruby = token ? rubyOf(line, token.id) : undefined;
  const selected = token ? selectedSegment(line, { lineId: line.id, tokenId: token.id, ...(props.segmentId ? { segmentId: props.segmentId } : {}) }) : undefined;
  const locked = rubyTextLocked(ruby);
  const blocked = ruby ? splitBlocked(ruby) : undefined;
  const canSegments = session.supportsCommand('lyrics.addRubySegment');

  const header = (
    <header>
      <h4>注音</h4>
      {token && <span className="faint small">所选「{token.text}」</span>}
      <span className="spacer" />
      {ruby && (
        <MenuTrigger>
          <IconButton icon="more_horiz" label="注音操作" size="sm" />
          <Menu
            label="注音操作"
            placement="bottom end"
            onAction={(id) => void session.applyEdits(id === 'split' ? splitEdits(line, ruby) : removeRubyEdits(line, ruby))}
          >
            <MenuItem id="split" icon="call_split" disabled={!!blocked || !canSegments} {...(blocked ? { description: blocked } : {})}>
              按字拆开
            </MenuItem>
            <MenuItem id="remove" icon="delete" danger disabled={!canSegments}>
              删除注音
            </MenuItem>
          </Menu>
        </MenuTrigger>
      )}
    </header>
  );
  if (!token) {
    return (
      <section className="fs ruby">
        {header}
        <p className="faint">选中文本单元后编辑它的注音。</p>
      </section>
    );
  }

  const select = (segmentId?: string) => session.select({ lineId: line.id, tokenId: token.id, ...(segmentId ? { segmentId } : {}) });
  const setRuby = (text: string) => void session.command({ type: 'lyrics.setRubyText', target: { branchId: line.branchId, lineId: line.id, tokenId: token.id }, text });
  const addSegment = async () => {
    if (!ruby) return;
    const text = await askText({ headline: `给「${token.text}」加注音片段`, label: '片段文字', confirmText: '加片段' });
    if (text) await session.command({ type: 'lyrics.addRubySegment', target: groupTarget(line, ruby), text, afterSegmentId: selected?.segment.id ?? ruby.segments.at(-1)!.id });
  };
  const move = (delta: -1 | 1) => {
    const order = ruby && selected && movedOrder(ruby, selected.segment.id, delta);
    if (order) void session.command({ type: 'lyrics.reorderRubySegments', target: groupTarget(line, ruby), orderedSegmentIds: order });
  };

  return (
    <section className="fs ruby">
      {header}
      <CommitField
        key={`${token.id}:${ruby?.id ?? ''}`}
        label="整段注音"
        labelPlacement="none"
        value={ruby?.text ?? ''}
        placeholder={ruby ? '' : '输入后回车添加注音'}
        {...(locked ? { helper: locked } : {})}
        disabled={!!locked || !session.supportsCommand('lyrics.setRubyText')}
        onCommit={setRuby}
        className="ruby-text"
      />
      {ruby && (
        <div className="rb-segs" role="listbox" aria-label="注音片段" lang="ja">
          {ruby.segments.map((segment) => (
            <button
              key={segment.id}
              type="button"
              role="option"
              aria-selected={segment.id === selected?.segment.id}
              className={cls('rb-seg', segment.id === selected?.segment.id && 'is-sel', !segmentTimed(segment) && 'untimed')}
              title={TIMING_STATUS_TEXT[segment.timingStatus]}
              onClick={() => select(segment.id === selected?.segment.id ? undefined : segment.id)}
            >
              <span>{segment.text}</span>
              <StatusIcon status={segment.timingStatus} />
            </button>
          ))}
          <IconButton icon="add" label="加片段" size="sm" isDisabled={!canSegments} onPress={() => void addSegment()} />
        </div>
      )}
      {ruby && selected && (
        <div className="rb-edit">
          <CommitField
            key={selected.segment.id}
            label={`所选片段「${selected.segment.text}」`}
            labelPlacement="none"
            size="sm"
            value={selected.segment.text}
            validate={(text) => (text.trim() ? undefined : '不能为空')}
            disabled={!canSegments}
            onCommit={(text) => void session.command({ type: 'lyrics.setRubySegmentText', target: segmentTarget(line, ruby, selected.segment.id), text })}
          />
          <IconButton icon="chevron_left" label="左移" size="sm" isDisabled={!canSegments || selected.index === 0} onPress={() => move(-1)} />
          <IconButton icon="chevron_right" label="右移" size="sm" isDisabled={!canSegments || selected.index === ruby.segments.length - 1} onPress={() => move(1)} />
          <IconButton
            icon="delete"
            label={ruby.segments.length === 1 ? '删除注音' : '删除片段'}
            size="sm"
            isDisabled={!canSegments}
            onPress={() => void session.command({ type: 'lyrics.removeRubySegment', target: segmentTarget(line, ruby, selected.segment.id) })}
          />
        </div>
      )}
    </section>
  );
}
