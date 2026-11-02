import { Button as AriaButton } from 'react-aria-components';
import { BRANCH_ROLE_TEXT, lineNumbers } from '../model/branches';
import type { EditorLine } from '../model/lyrics';
import type { EditorSession, EditorState } from '../session/editor-session';
import { Button } from '../ui/Button';
import { Select } from '../ui/fields';
import { Icon } from '../ui/Icon';
import { Tag } from '../ui/indicators';
import type { LineActions } from './line-actions';

const UNLINKED = '__unlinked__';

/**
 * 行编辑区的“翻译”（整体设计 §5.2）：主歌词行列出关联到它的其他来源的行，点击跳过去，“改关联…”打开对照编辑；
 * 其他来源的行选择它关联的主歌词行，或打开对照编辑整体调整。只有一个来源时不显示。
 */
export function TranslationSection(props: { session: EditorSession; state: EditorState; line: EditorLine; actions: LineActions }) {
  const { session, state, line, actions } = props;
  const { primaryBranchId, branches } = state;
  if (!primaryBranchId || !branches || branches.length < 2) return null;

  if (line.branchId === primaryBranchId) {
    const linked = branches
      .filter((b) => b.id !== primaryBranchId)
      .flatMap((b) => (state.linesByBranch[b.id] ?? []).flatMap((l, i) => (l.primaryLineId === line.id ? [{ branch: b, line: l, number: i + 1 }] : [])));
    const only = linked.length === 1 ? linked[0] : undefined;
    const follows = (b: (typeof linked)[number]['branch']) => b.timingMode === 'followPrimary';
    return (
      <section className="fs trans">
        <header>
          <h4>{linked.every((l) => l.branch.role === 'translation') ? '翻译' : '关联的行'}</h4>
          {only && <Tag>{follows(only.branch) ? '跟随本句' : '只对照'}</Tag>}
          <span className="spacer" />
          {only && (
            <Button variant="text" size="sm" icon="link" isDisabled={!session.supportsCommand('lyrics.linkPrimaryLine')} onPress={() => actions.openLink(only.branch.id, only.line.id)}>
              改关联…
            </Button>
          )}
        </header>
        {linked.length === 0 ? (
          <p className="faint">没有其他来源的行关联到这一句。</p>
        ) : (
          <div className="tr-lines">
            {linked.map(({ branch, line: l, number }) => (
              <AriaButton
                key={l.id}
                className="tr-line"
                onPress={() => {
                  session.selectBranch(branch.id);
                  session.select({ lineId: l.id });
                }}
              >
                <span className="tr-text">{l.text || '（空行）'}</span>
                <span className="faint small">
                  {branch.name}（{BRANCH_ROLE_TEXT[branch.role]}）第 {number} 行{only ? '' : ` · ${follows(branch) ? '跟随本句' : '只对照'}`}
                </span>
                <Icon name="chevron_right" className="ro-go" />
              </AriaButton>
            ))}
          </div>
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
    <section className="fs trans">
      <header>
        <h4>关联主歌词</h4>
        <span className="spacer" />
        <Button variant="text" size="sm" icon="link" isDisabled={!canLink} onPress={() => actions.openLink(line.branchId, line.id)}>
          对照编辑…
        </Button>
      </header>
      <Select
        label="关联到"
        labelPlacement="none"
        value={missing ? UNLINKED : (line.primaryLineId ?? UNLINKED)}
        options={[
          { value: UNLINKED, label: '不关联' },
          ...primaryLines.map((p, i) => ({ value: p.id, label: `${String(i + 1).padStart(2, '0')}  ${p.text || '（空行）'}` })),
        ]}
        disabled={!canLink}
        onChange={link}
      />
      <p className="hint">
        {missing
          ? '关联的主歌词行已不存在，请重新选择。'
          : linkedNumber === undefined
            ? '这一行没有关联主歌词。'
            : branch?.timingMode === 'followPrimary'
              ? `跟随主歌词第 ${linkedNumber} 行显示。`
              : '这个来源不跟随主歌词，关联只用于对照显示。'}
      </p>
    </section>
  );
}
