import { useEffect, useRef, useState } from 'react';
import { TIMING_MODE_TEXT, autoPair, currentLinks, linkChanges, type LinkDraft } from '../model/branches';
import { batchHistoryText } from '../model/edit-batches';
import type { EditorSession } from '../session/editor-session';
import { toast } from '../ui/feedback';
import { FormDialog } from '../ui/FormDialog';

/**
 * 翻译关联（整体设计 §5.2）：左边是这个来源的行，右边选它关联的主歌词行。
 * 先在草稿里改（可以“按顺序自动配对”），提交时只发送有变化的行。
 * 提交是一次 project.applyEdits（G14），一条历史；超过 EDIT_BATCH_SIZE 处时分批。
 */
export function LinkDialog(props: { session: EditorSession; branchId: string | null; focusLineId?: string | undefined; onClose: () => void }) {
  const { session, branchId } = props;
  const state = session.state;
  const branch = state.branches?.find((b) => b.id === branchId);
  const lines = branchId ? session.linesOf(branchId) : [];
  const primaryLines = state.primaryBranchId ? session.linesOf(state.primaryBranchId) : [];
  const [draft, setDraft] = useState<LinkDraft>({});
  const [progress, setProgress] = useState<string | null>(null);
  const table = useRef<HTMLDivElement>(null);
  const open = branchId !== null && !!branch;

  useEffect(() => {
    if (!open) return;
    setDraft(currentLinks(lines));
    // 只在打开时取当前关联；打开期间的刷新不覆盖草稿。
  }, [open, branchId]);

  useEffect(() => {
    if (!open || !props.focusLineId) return;
    // 对话框内容在打开后才渲染，等一帧再滚动。
    const frame = requestAnimationFrame(() =>
      table.current?.querySelector(`[data-line-id="${CSS.escape(props.focusLineId!)}"]`)?.scrollIntoView({ block: 'center' }),
    );
    return () => cancelAnimationFrame(frame);
  }, [open, props.focusLineId]);

  const changes = linkChanges(lines, draft);
  const submit = async () => {
    if (!branchId || changes.length === 0) return;
    setProgress(`正在应用 ${changes.length} 处修改`);
    let result: Awaited<ReturnType<EditorSession['applyEdits']>>;
    try {
      result = await session.applyEdits(changes.map((change) => ({ type: 'lyrics.linkPrimaryLine', branchId, ...change })));
    } finally {
      setProgress(null);
    }
    const { applied, batches } = result;
    if (applied === 0) return;
    if (applied < changes.length) toast(`已修改 ${applied} 行关联，其余 ${changes.length - applied} 行未修改`);
    else toast(batches > 1 ? `已修改 ${applied} 行关联（${batches} 条历史）` : `已修改 ${applied} 行关联，可以一次撤销`);
    if (applied === changes.length) props.onClose();
  };

  const follows = branch?.timingMode === 'followPrimary';
  return (
    <FormDialog
      open={open}
      headline={`关联主歌词：${branch?.name ?? ''}`}
      description={`左边是「${branch?.name ?? ''}」的行，右边选它对应的主歌词行。只发送有变化的行，${batchHistoryText(Math.max(1, changes.length))}。`}
      submitText={changes.length > 0 ? `应用 ${changes.length} 处修改` : '应用'}
      submitDisabled={changes.length === 0}
      busy={progress !== null}
      onSubmit={() => void submit()}
      onClose={props.onClose}
      className="link-dialog"
    >
      {!follows && branch && (
        <p className="hint link-note">
          这个来源的时间策略是“{TIMING_MODE_TEXT[branch.timingMode]}”，关联只用于对照显示。要让翻译跟随主歌词显示，在“管理来源”里把时间策略改为“跟随主歌词”。
        </p>
      )}
      <div className="link-toolbar">
        <mdui-button variant="text" disabled={primaryLines.length === 0} onClick={() => setDraft(autoPair(lines, primaryLines))}>
          按顺序自动配对
        </mdui-button>
        <mdui-button variant="text" onClick={() => setDraft(Object.fromEntries(lines.map((l) => [l.id, null])))}>
          全部解除
        </mdui-button>
        <mdui-button variant="text" disabled={changes.length === 0} onClick={() => setDraft(currentLinks(lines))}>
          还原
        </mdui-button>
        <span className="spacer" />
        <span className="muted">{progress ?? (changes.length > 0 ? `${changes.length} 处修改` : '没有修改')}</span>
      </div>
      <div className="link-table" ref={table} role="table" aria-label="翻译关联">
        <div className="link-head" role="row">
          <span role="columnheader">#</span>
          <span role="columnheader">{branch?.name}</span>
          <span role="columnheader">主歌词</span>
        </div>
        {lines.map((line, i) => {
          const value = line.id in draft ? (draft[line.id] ?? null) : (line.primaryLineId ?? null);
          const changed = value !== (line.primaryLineId ?? null);
          const missing = value !== null && !primaryLines.some((p) => p.id === value);
          return (
            <div
              key={line.id}
              role="row"
              data-line-id={line.id}
              className={['link-row', changed ? 'changed' : '', line.id === props.focusLineId ? 'focused' : ''].filter(Boolean).join(' ')}
            >
              <span className="line-index">{String(i + 1).padStart(2, '0')}</span>
              <span className="link-text">{line.text || '（空行）'}</span>
              <select
                className="native-select"
                aria-label={`第 ${i + 1} 行关联的主歌词`}
                value={value ?? ''}
                disabled={progress !== null}
                onChange={(event) => {
                  const next = event.currentTarget.value || null;
                  setDraft((current) => ({ ...current, [line.id]: next }));
                }}
              >
                <option value="">不关联</option>
                {missing && <option value={value}>（已不存在的行）</option>}
                {primaryLines.map((p, n) => (
                  <option key={p.id} value={p.id}>
                    {`${String(n + 1).padStart(2, '0')}  ${p.text || '（空行）'}`}
                  </option>
                ))}
              </select>
            </div>
          );
        })}
      </div>
    </FormDialog>
  );
}
