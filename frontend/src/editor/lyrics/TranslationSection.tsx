import { useState } from 'react';
import { BRANCH_ROLE_TEXT, lineNumbers } from '../model/branches';
import type { EditorLine } from '../model/lyrics';
import type { EditorSession, EditorState } from '../session/editor-session';
import { Select } from '../ui/legacy/fields';
import { LinkDialog } from './LinkDialog';

const UNLINKED = '__unlinked__';

/**
 * 行编辑区的“关联”一节（整体设计 §5.2）：主歌词行列出关联到它的其他来源的行，点击跳过去；
 * 其他来源的行选择它关联的主歌词行，或打开对照视图整体调整。
 */
export function TranslationSection(props: { session: EditorSession; state: EditorState; line: EditorLine }) {
  const { session, state, line } = props;
  const [linkOpen, setLinkOpen] = useState(false);
  const { primaryBranchId, branches } = state;
  if (!primaryBranchId || !branches || branches.length < 2) return null;

  if (line.branchId === primaryBranchId) {
    const linked = branches
      .filter((b) => b.id !== primaryBranchId)
      .flatMap((b) => (state.linesByBranch[b.id] ?? []).flatMap((l, i) => (l.primaryLineId === line.id ? [{ branch: b, line: l, number: i + 1 }] : [])));
    return (
      <section className="translation-section">
        <div className="section-title">关联的翻译</div>
        {linked.length === 0 ? (
          <p className="muted">没有其他来源的行关联到这一句。</p>
        ) : (
          <mdui-list className="linked-lines">
            {linked.map(({ branch, line: l, number }) => (
              <mdui-list-item
                key={l.id}
                rounded
                onClick={() => {
                  session.selectBranch(branch.id);
                  session.select({ lineId: l.id });
                }}
              >
                <span>{l.text || '（空行）'}</span>
                <span slot="description">
                  {branch.name}（{BRANCH_ROLE_TEXT[branch.role]}）第 {number} 行 · {branch.timingMode === 'followPrimary' ? '跟随本句' : '只对照，不跟随'}
                </span>
              </mdui-list-item>
            ))}
          </mdui-list>
        )}
      </section>
    );
  }

  const primaryLines = state.linesByBranch[primaryBranchId] ?? [];
  const numbers = lineNumbers(primaryLines);
  const branch = branches.find((b) => b.id === line.branchId);
  const linkedNumber = line.primaryLineId ? numbers.get(line.primaryLineId) : undefined;
  const missing = !!line.primaryLineId && linkedNumber === undefined;
  const canLink = session.supportsCommand('lyrics.linkPrimaryLine');
  const link = (value: string) =>
    void session.command({ type: 'lyrics.linkPrimaryLine', branchId: line.branchId, lineId: line.id, primaryLineId: value === UNLINKED ? null : value });

  return (
    <section className="translation-section">
      <header className="section-header">
        <span className="section-title">关联主歌词</span>
        <span className="spacer" />
        <mdui-button variant="text" disabled={!canLink} onClick={() => setLinkOpen(true)}>
          <mdui-icon-link slot="icon" />
          对照编辑…
        </mdui-button>
      </header>
      <Select
        label="关联到"
        value={missing ? UNLINKED : (line.primaryLineId ?? UNLINKED)}
        options={[
          { value: UNLINKED, label: '不关联' },
          ...primaryLines.map((p, i) => ({ value: p.id, label: `${String(i + 1).padStart(2, '0')}  ${p.text || '（空行）'}` })),
        ]}
        disabled={!canLink}
        onChange={link}
        className="link-select"
      />
      <p className="hint">
        {missing
          ? '关联的主歌词行已不存在，请重新选择。'
          : linkedNumber === undefined
            ? '这一行没有关联主歌词。'
            : branch?.timingMode === 'followPrimary'
              ? `这一行跟随主歌词第 ${linkedNumber} 行显示。`
              : `这个来源不是“跟随主歌词”，关联只用于对照显示。`}
      </p>
      <LinkDialog session={session} branchId={linkOpen ? line.branchId : null} focusLineId={line.id} onClose={() => setLinkOpen(false)} />
    </section>
  );
}
