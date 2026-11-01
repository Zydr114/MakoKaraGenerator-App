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
import { askText } from '../ui/legacy/dialogs';
import { CommitField } from '../ui/legacy/fields';
import { StatusGlyph } from './LineList';

/**
 * 注音（W6 设计 §2.4）：所选文本单元的注音组。整段文字只在没有注音或只有一个没有时间的片段时能直接改；
 * 片段一排小按钮，点选后可以改字、左右移、删除；加片段、按字拆开、删除注音。
 */
export function RubySection(props: { session: EditorSession; line: EditorLine; tokenId?: string | undefined; segmentId?: string | undefined }) {
  const { session, line } = props;
  const token = props.tokenId ? line.tokens.find((t) => t.id === props.tokenId) : undefined;
  if (!token) return <p className="muted">选中文本单元后可以编辑它的注音。</p>;

  const ruby = rubyOf(line, token.id);
  const selected = selectedSegment(line, { lineId: line.id, tokenId: token.id, ...(props.segmentId ? { segmentId: props.segmentId } : {}) });
  const locked = rubyTextLocked(ruby);
  const blocked = ruby ? splitBlocked(ruby) : undefined;
  const canSegments = session.supportsCommand('lyrics.addRubySegment');
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
    <section className="ruby-section">
      <header className="section-header">
        <span className="section-title">注音（所选「{token.text}」）</span>
        <span className="spacer" />
        {ruby && (
          <>
            <mdui-button variant="text" disabled={!!blocked || !canSegments} title={blocked} onClick={() => void session.applyEdits(splitEdits(line, ruby))}>
              <mdui-icon-call-split slot="icon" />
              按字拆开
            </mdui-button>
            <mdui-button variant="text" disabled={!canSegments} onClick={() => void session.applyEdits(removeRubyEdits(line, ruby))}>
              <mdui-icon-delete slot="icon" />
              删除注音
            </mdui-button>
          </>
        )}
      </header>
      <CommitField
        key={`${token.id}:${ruby?.id ?? ''}`}
        label="整段注音"
        value={ruby?.text ?? ''}
        placeholder={ruby ? '' : '输入后回车添加注音'}
        helper={locked ?? (ruby ? '清空即删除注音' : '添加后可以拆成几个片段，各自打轴')}
        disabled={!!locked || !session.supportsCommand('lyrics.setRubyText')}
        onCommit={setRuby}
        className="ruby-field"
      />
      {ruby && (
        <div className="ruby-segments" role="listbox" aria-label="注音片段">
          {ruby.segments.map((segment) => (
            <button
              key={segment.id}
              type="button"
              role="option"
              aria-selected={segment.id === selected?.segment.id}
              className={['ruby-chip', segment.id === selected?.segment.id ? 'selected' : '', segmentTimed(segment) ? '' : 'untimed'].filter(Boolean).join(' ')}
              title={TIMING_STATUS_TEXT[segment.timingStatus]}
              onClick={() => select(segment.id === selected?.segment.id ? undefined : segment.id)}
            >
              <span className="ruby-chip-text">{segment.text}</span>
              <StatusGlyph status={segment.timingStatus} />
            </button>
          ))}
          <mdui-button variant="text" disabled={!canSegments} onClick={() => void addSegment()}>
            <mdui-icon-add slot="icon" />
            加片段
          </mdui-button>
        </div>
      )}
      {ruby && selected && (
        <div className="ruby-segment-editor">
          <CommitField
            key={selected.segment.id}
            label={`所选片段「${selected.segment.text}」`}
            value={selected.segment.text}
            helper="不能为空；要删除请用“删除片段”"
            validate={(text) => (text.trim() ? undefined : '不能为空')}
            disabled={!canSegments}
            onCommit={(text) => void session.command({ type: 'lyrics.setRubySegmentText', target: segmentTarget(line, ruby, selected.segment.id), text })}
            className="ruby-segment-field"
          />
          <mdui-button variant="text" disabled={!canSegments || selected.index === 0} onClick={() => move(-1)}>
            <mdui-icon-chevron-left slot="icon" />
            左移
          </mdui-button>
          <mdui-button variant="text" disabled={!canSegments || selected.index === ruby.segments.length - 1} onClick={() => move(1)}>
            <mdui-icon-chevron-right slot="end-icon" />
            右移
          </mdui-button>
          <mdui-button
            variant="text"
            disabled={!canSegments}
            onClick={() => void session.command({ type: 'lyrics.removeRubySegment', target: segmentTarget(line, ruby, selected.segment.id) })}
          >
            <mdui-icon-delete slot="icon" />
            {ruby.segments.length === 1 ? '删除注音' : '删除片段'}
          </mdui-button>
        </div>
      )}
      {ruby && !selected && ruby.segments.length > 0 && <p className="hint">点选片段可以改字、调整顺序或删除{blocked ? `；按字拆开：${blocked}` : ''}。</p>}
    </section>
  );
}
