import { useRef, useState, type DragEvent } from 'react';
import { isTimed, type EditorLine, type EditorToken } from '../model/lyrics';
import { graphemes } from '../model/token-segments';
import type { EditorSession } from '../session/editor-session';
import { Button, IconButton } from '../ui/Button';
import { cls } from '../ui/cls';
import { askReorderSlots } from '../ui/dialogs';
import type { LineActions } from './line-actions';

/**
 * 文本单元（整体设计 §5.2，界面改版稿件 #lyrics）：一排块，注音在上、时长在下。
 * 点击选中，再点回到整行；拖动排序；拆开（至少两个字且已有时间）、与后一个合并、按规则重切本行。
 */
export function TokenSection(props: { session: EditorSession; line: EditorLine; tokenId?: string | undefined; actions: LineActions }) {
  const { session, line } = props;
  // 拖动源放在 ref 里：drop 时直接读，不依赖 dragover 的状态更新是否已渲染。state 只用于显示插入标记。
  const dragFrom = useRef<number | null>(null);
  const [drag, setDrag] = useState<{ from: number; gap: number | null } | null>(null);
  const index = props.tokenId ? line.tokens.findIndex((t) => t.id === props.tokenId) : -1;
  const token = index >= 0 ? line.tokens[index] : undefined;
  const rubyOf = (tokenId: string) =>
    line.ruby
      .filter((r) => r.baseTokenId === tokenId)
      .map((r) => r.text)
      .join('');
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
    <section className="fs tokens">
      <header>
        <h4>文本单元</h4>
        <span className="faint small">
          {line.tokens.length} 个{canReorder ? '，可拖动排序' : ''}
        </span>
        <span className="spacer" />
        <Button
          variant="ghost"
          size="sm"
          icon="call_split"
          isDisabled={!!splitBlocked || !session.supportsCommand('lyrics.splitToken')}
          onPress={() => token && void session.command({ type: 'lyrics.splitToken', target: target(token) })}
        >
          拆开
        </Button>
        <Button
          variant="ghost"
          size="sm"
          icon="merge"
          isDisabled={!!mergeBlocked || !session.supportsCommand('lyrics.mergeToken')}
          onPress={() => token && void session.command({ type: 'lyrics.mergeToken', target: target(token) })}
        >
          与后一个合并
        </Button>
        <IconButton icon="wand_stars" label="按规则重切本行…" size="sm" isDisabled={!session.supportsCommand('lyrics.resplitLine')} onPress={() => props.actions.run('resplit', line)} />
      </header>

      <div className="tk-row" role="listbox" aria-label="文本单元" lang="ja">
        {line.tokens.length === 0 && <span className="faint">本行没有拆分文本单元，只能编辑整行时间。</span>}
        {line.tokens.map((t, i) => (
          <button
            key={t.id}
            type="button"
            role="option"
            aria-selected={t.id === props.tokenId}
            aria-label={t.text}
            aria-description={`${rubyOf(t.id) ? `注音 ${rubyOf(t.id)}，` : ''}${isTimed(t) ? `${((t.endUs - t.startUs) / 1e6).toFixed(2)} 秒` : '未打轴'}`}
            draggable={canReorder}
            className={cls(
              'tk',
              t.id === props.tokenId && 'is-sel',
              !isTimed(t) && 'untimed',
              drag?.from === i && 'dragging',
              drag?.gap === i && drag.from !== i && drag.from !== i - 1 && 'drop-before',
              drag?.gap === i + 1 && i === line.tokens.length - 1 && drag.from !== i && 'drop-after',
            )}
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
            <span className="tk-r">{rubyOf(t.id) || '\u00a0'}</span>
            <span className="tk-t">{t.text}</span>
            <span className="tk-d">{isTimed(t) ? ((t.endUs - t.startUs) / 1e6).toFixed(2) : '—'}</span>
          </button>
        ))}
      </div>
      {reason && <p className="hint">{reason}。</p>}
    </section>
  );
}
