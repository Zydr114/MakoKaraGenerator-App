import { useRef, useState, type DragEvent } from 'react';
import { isTimed, type EditorLine, type EditorToken } from '../model/lyrics';
import { graphemes } from '../model/token-segments';
import type { EditorSession } from '../session/editor-session';
import { askReorderSlots } from '../ui/dialogs';
import { ResplitLineDialog } from './ResplitDialogs';

/**
 * 文本单元区（整体设计 §5.2）：点击选中，再点回到整行；拖动排序；
 * 拆开（至少两个字且已有时间）、与后一个合并、按规则重切本行。
 */
export function TokenSection(props: { session: EditorSession; line: EditorLine; tokenId?: string | undefined }) {
  const { session, line } = props;
  // 拖动源放在 ref 里：drop 时直接读，不依赖 dragover 的状态更新是否已渲染。state 只用于显示插入标记。
  const dragFrom = useRef<number | null>(null);
  const [drag, setDrag] = useState<{ from: number; gap: number | null } | null>(null);
  const [resplitOpen, setResplitOpen] = useState(false);
  const index = props.tokenId ? line.tokens.findIndex((t) => t.id === props.tokenId) : -1;
  const token = index >= 0 ? line.tokens[index] : undefined;
  const rubyOf = (tokenId: string) => line.ruby.filter((r) => r.baseTokenId === tokenId).map((r) => r.text).join('');
  const target = (t: EditorToken) => ({ branchId: line.branchId, lineId: line.id, tokenId: t.id });

  const splitBlocked = !token
    ? '先选中一个文本单元'
    : graphemes(token.text).length < 2
      ? '只有一个字，不能再拆开'
      : !isTimed(token)
        ? '拆开需要它已有时间；未打轴时可以用“按规则重切本行”'
        : undefined;
  const mergeBlocked = !token ? '先选中一个文本单元' : index === line.tokens.length - 1 ? '已是本行最后一个文本单元' : undefined;
  const reason = token ? [splitBlocked && `拆开：${splitBlocked}`, mergeBlocked && `合并：${mergeBlocked}`].filter(Boolean).join('；') : '';

  const reorder = async (from: number, gap: number) => {
    const moved = line.tokens[from];
    // gap 是插入位置（0～n，在第 gap 个单元之前）；后端要的是移动后的最终下标。
    const destinationIndex = gap > from ? gap - 1 : gap;
    if (!moved || destinationIndex === from) return;
    const reassign = line.tokens.some(isTimed) ? await askReorderSlots() : false;
    if (reassign === null) return;
    await session.command({ type: 'lyrics.reorderToken', target: target(moved), destinationIndex, reassignTimeSlots: reassign });
  };

  const gapAt = (event: DragEvent<HTMLButtonElement>, i: number) => {
    const rect = event.currentTarget.getBoundingClientRect();
    return event.clientX < rect.left + rect.width / 2 ? i : i + 1;
  };

  const dragOver = (event: DragEvent<HTMLButtonElement>, i: number) => {
    const from = dragFrom.current;
    if (from === null) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = 'move';
    const gap = gapAt(event, i);
    if (gap !== drag?.gap) setDrag({ from, gap });
  };

  const drop = (event: DragEvent<HTMLButtonElement>, i: number) => {
    const from = dragFrom.current;
    if (from === null) return;
    event.preventDefault();
    dragFrom.current = null;
    setDrag(null);
    void reorder(from, gapAt(event, i));
  };

  const canReorder = session.supportsCommand('lyrics.reorderToken') && line.tokens.length > 1;

  return (
    <section className="token-section">
      <header className="section-header">
        <span className="section-title">文本单元</span>
        <span className="spacer" />
        <mdui-button
          variant="text"
          disabled={!!splitBlocked || !session.supportsCommand('lyrics.splitToken')}
          onClick={() => token && void session.command({ type: 'lyrics.splitToken', target: target(token) })}
        >
          <mdui-icon-call-split slot="icon" />
          拆开
        </mdui-button>
        <mdui-button
          variant="text"
          disabled={!!mergeBlocked || !session.supportsCommand('lyrics.mergeToken')}
          onClick={() => token && void session.command({ type: 'lyrics.mergeToken', target: target(token) })}
        >
          <mdui-icon-merge slot="icon" />
          与后一个合并
        </mdui-button>
        <mdui-button variant="text" disabled={!session.supportsCommand('lyrics.resplitLine')} onClick={() => setResplitOpen(true)}>
          按规则重切本行…
        </mdui-button>
      </header>

      <div className="token-row" role="listbox" aria-label="文本单元">
        {line.tokens.length === 0 && <span className="muted">本行没有拆分文本单元，只能编辑整行时间。</span>}
        {line.tokens.map((t, i) => (
          <button
            key={t.id}
            type="button"
            role="option"
            aria-selected={t.id === props.tokenId}
            draggable={canReorder}
            className={[
              'token-chip',
              `status-${t.status ?? 'unknown'}`,
              t.id === props.tokenId ? 'selected' : '',
              isTimed(t) ? '' : 'untimed',
              drag?.from === i ? 'dragging' : '',
              drag?.gap === i && drag.from !== i && drag.from !== i - 1 ? 'drop-before' : '',
              drag?.gap === i + 1 && i === line.tokens.length - 1 && drag.from !== i ? 'drop-after' : '',
            ]
              .filter(Boolean)
              .join(' ')}
            onClick={() => session.select(t.id === props.tokenId ? { lineId: line.id } : { lineId: line.id, tokenId: t.id })}
            onDragStart={(event) => {
              event.dataTransfer.effectAllowed = 'move';
              event.dataTransfer.setData('text/plain', t.text);
              dragFrom.current = i;
              setDrag({ from: i, gap: null });
            }}
            onDragOver={(event) => dragOver(event, i)}
            onDrop={(event) => drop(event, i)}
            onDragEnd={() => {
              dragFrom.current = null;
              setDrag(null);
            }}
          >
            {rubyOf(t.id) && <span className="token-ruby">{rubyOf(t.id)}</span>}
            <span className="token-text">{t.text}</span>
          </button>
        ))}
      </div>
      <p className="hint">
        点选文本单元编辑它的时间和注音，再点一次回到整行；拖动可以调整顺序。{reason && <span className="token-reason">{reason}。</span>}
      </p>

      <ResplitLineDialog session={session} line={line} open={resplitOpen} onClose={() => setResplitOpen(false)} />
    </section>
  );
}
