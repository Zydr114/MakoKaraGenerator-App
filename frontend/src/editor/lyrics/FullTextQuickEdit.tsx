import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import type { ProjectEditCommand } from '../../protocol';
import type { EditorLine } from '../model/lyrics';
import { rubyOf } from '../model/ruby-segments';
import type { EditorSession } from '../session/editor-session';
import { Button } from '../ui/Button';
import { Switch } from '../ui/choices';
import { toast } from '../ui/feedback';
import { Tag } from '../ui/indicators';
import { computeFullTextDiff, serializeBranchText } from './full-text-diff';

/**
 * 「全文快编」（Phase 3 §5.2）：
 * - 带行号与实时变更指示的多行编辑器（默认纯文本，可选切换内联注音 `二人[ふ・た・り]`）；
 * - 实时执行行级 + 字素级 LCS Diff 预览，直观展示未变/修改/新增/删除行数与预计保留的时间戳词元数；
 * - 点击「应用并返回打轴」提交 `lyrics.applyTextDiff`（单条可撤销历史），无损保留未改动字符的时间轴、注音与样式覆写。
 */
export function FullTextQuickEdit(props: {
  session: EditorSession;
  branchId: string;
  lines: readonly EditorLine[];
  mergeLongAndNasalInKanji: boolean;
  onDone: () => void;
  onCancel: () => void;
}) {
  const { session, branchId, lines, mergeLongAndNasalInKanji, onDone, onCancel } = props;
  const [inlineRuby, setInlineRuby] = useState(false);
  const [draft, setDraft] = useState(() => serializeBranchText(lines, false));
  const [busy, setBusy] = useState(false);
  const [showDiffPanel, setShowDiffPanel] = useState(true);
  const gutterRef = useRef<HTMLDivElement>(null);
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  // Re-initialize draft when switching branches.
  useEffect(() => {
    setDraft(serializeBranchText(lines, inlineRuby));
  }, [branchId]);

  const toggleInlineRuby = (next: boolean) => {
    setInlineRuby(next);
    // Only reset draft from authoritative lines if user hasn't modified text yet.
    const currentBaseline = serializeBranchText(lines, !next);
    if (draft === currentBaseline) {
      setDraft(serializeBranchText(lines, next));
    }
  };

  const diff = useMemo(
    () =>
      computeFullTextDiff(lines, draft, {
        inlineRuby,
        moraOptions: { mergeLongAndNasalInKanji, isKanjiBase: true },
      }),
    [lines, draft, inlineRuby, mergeLongAndNasalInKanji],
  );

  const hasInlineRubyDiff = useMemo(() => {
    if (!inlineRuby) return false;
    return draft !== serializeBranchText(lines, true);
  }, [inlineRuby, draft, lines]);

  const canApply = !busy && (diff.stats.hasTextChanges || hasInlineRubyDiff) && session.supportsCommand('lyrics.applyTextDiff');

  const apply = async () => {
    if (!canApply) return;
    setBusy(true);
    try {
      let preservedCount = 0;
      let resetCount = 0;
      if (diff.stats.hasTextChanges) {
        const res = await session.command({
          type: 'lyrics.applyTextDiff',
          branchId,
          lines: diff.diffLines,
        });
        if (!res) return;
        preservedCount = res.feedback?.preservedTokenCount ?? 0;
        resetCount = res.feedback?.resetTokenCount ?? 0;
      }

      // If inline ruby mode is enabled, also synchronize any changed inline ruby annotations.
      let rubyUpdates = 0;
      if (inlineRuby && session.supportsCommand('lyrics.setRubySegments')) {
        await session.refresh();
        const freshLines = session.linesOf(branchId);
        const edits: ProjectEditCommand[] = [];
        for (let i = 0; i < diff.rows.length && i < freshLines.length; i++) {
          const row = diff.rows[i]!;
          const freshLine = freshLines[i]!;
          if (freshLine.text !== row.text) continue;
          const spanByOffset = new Map<number, string[]>();
          for (const span of row.inlineRubySpans) {
            spanByOffset.set(span.start, span.segments);
          }
          let offset = 0;
          for (const token of freshLine.tokens) {
            const desired = spanByOffset.get(offset);
            const existing = rubyOf(freshLine, token.id);
            const existingSegs = existing ? existing.segments.map((s) => s.text) : [];
            if (desired) {
              const same = desired.length === existingSegs.length && desired.every((s, idx) => s === existingSegs[idx]);
              if (!same) {
                edits.push({
                  type: 'lyrics.setRubySegments',
                  target: { branchId, lineId: freshLine.id, tokenId: token.id },
                  segments: desired,
                });
              }
            } else if (existing && !row.inlineRubySpans.some((s) => s.start >= offset && s.start < offset + token.text.length)) {
              // User explicitly removed inline `[...]` from this token.
              edits.push({
                type: 'lyrics.setRubySegments',
                target: { branchId, lineId: freshLine.id, tokenId: token.id },
                segments: [],
              });
            }
            offset += token.text.length;
          }
        }
        if (edits.length > 0) {
          const batch = await session.applyEdits(edits);
          rubyUpdates = batch.applied;
        }
      }

      const parts: string[] = [];
      if (diff.stats.hasTextChanges) {
        parts.push(`已应用全文修改（保留 ${preservedCount} 个词元时间，重置 ${resetCount} 个）`);
      }
      if (rubyUpdates > 0) {
        parts.push(`同步 ${rubyUpdates} 处注音`);
      }
      if (parts.length > 0) toast(parts.join('，'));
      onDone();
    } finally {
      setBusy(false);
    }
  };

  const onKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') {
      event.preventDefault();
      void apply();
    }
  };

  const syncScroll = () => {
    if (gutterRef.current && textareaRef.current) {
      gutterRef.current.scrollTop = textareaRef.current.scrollTop;
    }
  };

  const changedRows = diff.rows.filter((r) => r.status !== 'unchanged');

  return (
    <section className="quick-edit" aria-label="全文快编">
      <header className="qe-bar">
        <div className="qe-stats">
          <Tag tone="neutral">{diff.stats.totalLines} 行</Tag>
          {diff.stats.modifiedLines > 0 && <Tag tone="warn">修改 {diff.stats.modifiedLines}</Tag>}
          {diff.stats.addedLines > 0 && <Tag tone="ok">新增 {diff.stats.addedLines}</Tag>}
          {diff.stats.removedLines > 0 && <Tag tone="error">删除 {diff.stats.removedLines}</Tag>}
          <span className="faint small">
            预计保留 {diff.stats.estimatedPreservedTokens} 个词元时间
          </span>
        </div>
        <span className="spacer" />
        <Switch label="内联注音 [ふ・た・り]" checked={inlineRuby} onChange={toggleInlineRuby} />
        <Button variant="text" size="sm" onPress={() => setShowDiffPanel((v) => !v)}>
          {showDiffPanel ? '隐藏变更预览' : '变更预览'}
        </Button>
        <Button variant="text" size="sm" isDisabled={busy} onPress={onCancel}>
          取消
        </Button>
        <Button variant="filled" size="sm" icon="check" isDisabled={!canApply} isPending={busy} onPress={() => void apply()}>
          应用并返回打轴
        </Button>
      </header>

      <div className="qe-body">
        <div className="qe-editor-wrap">
          <div className="qe-gutter" ref={gutterRef} aria-hidden>
            {(diff.rows.length > 0 ? diff.rows : [{ newLineIndex: 0, status: 'unchanged' as const }]).map((row) => (
              <div key={row.newLineIndex} className={`qe-gline st-${row.status}`}>
                <span className="qe-gmark">
                  {row.status === 'added' ? '+' : row.status === 'modified' ? '~' : ''}
                </span>
                <span className="qe-gnum">{row.newLineIndex + 1}</span>
              </div>
            ))}
          </div>
          <textarea
            ref={textareaRef}
            className="qe-textarea"
            value={draft}
            disabled={busy}
            spellCheck={false}
            wrap="off"
            aria-label="全文歌词编辑器"
            placeholder="每行一句歌词，应用后自动对齐 LCS 保留未改动词元的时间戳与注音…"
            onChange={(e) => setDraft(e.target.value)}
            onScroll={syncScroll}
            onKeyDown={onKeyDown}
          />
        </div>

        {showDiffPanel && (
          <aside className="qe-diff-side" aria-label="Diff 变更预览">
            <div className="qe-diff-head">
              <strong>LCS 对齐变更预览</strong>
              <span className="faint small">未改动行与词元将无损保留时间戳与注音</span>
            </div>
            <div className="qe-diff-list">
              {changedRows.length === 0 && diff.removedLines.length === 0 ? (
                <p className="faint small qe-diff-empty">正文尚未改动。直接编辑左侧文本，按 Ctrl+Enter 或点击右上角应用。</p>
              ) : (
                <>
                  {changedRows.map((row) => (
                    <div key={`${row.status}-${row.newLineIndex}`} className={`qe-diff-item st-${row.status}`}>
                      <span className="qe-diff-badge">
                        第 {row.newLineIndex + 1} 行 · {row.status === 'added' ? '新增' : '修改'}
                      </span>
                      {row.previousText !== undefined && (
                        <div className="qe-diff-old" lang="ja">
                          − {row.previousText || '（空行）'}
                        </div>
                      )}
                      <div className="qe-diff-new" lang="ja">
                        + {row.text || '（空行）'}
                      </div>
                    </div>
                  ))}
                  {diff.removedLines.map((rem) => (
                    <div key={rem.lineId} className="qe-diff-item st-removed">
                      <span className="qe-diff-badge">原第 {rem.lineIndex + 1} 行 · 删除</span>
                      <div className="qe-diff-old" lang="ja">
                        − {rem.text || '（空行）'}
                      </div>
                    </div>
                  ))}
                </>
              )}
            </div>
          </aside>
        )}
      </div>
    </section>
  );
}
