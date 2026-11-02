import { useState } from 'react';
import type { LyricBranchRole, LyricBranchSummary, LyricBranchTimingMode } from '../../protocol';
import { BRANCH_ROLE_TEXT, TIMING_MODE_TEXT, branchDescription, roleOptions, timingModeOptions } from '../model/branches';
import type { EditorSession, EditorState } from '../session/editor-session';
import { Button } from '../ui/Button';
import { List } from '../ui/collections';
import { Sheet } from '../ui/Dialog';
import { confirmAction } from '../ui/dialogs';
import { toast } from '../ui/feedback';
import { Select, TextField } from '../ui/fields';

/**
 * 管理来源（整体设计 §5.2）：右侧面板列出所有来源；选中一项在下面修改名称、语言、角色、时间策略，或删除。
 * 主歌词不能删除，也不能改角色，相应控件禁用并写明原因。
 */
export function SourcesSheet(props: {
  session: EditorSession;
  state: EditorState;
  open: boolean;
  onClose: () => void;
  onLink: (branchId: string) => void;
}) {
  const { session, state } = props;
  const [editingId, setEditingId] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);

  const branches = state.branches ?? [];
  // 正在编辑的来源被删除（或撤销了新增）时，回到当前来源。
  const editing = branches.find((b) => b.id === editingId) ?? branches.find((b) => b.id === state.branchId) ?? branches[0];

  return (
    <Sheet open={props.open} onClose={props.onClose} title="管理来源" closeLabel="关闭管理来源" className="sources-sheet">
      <section className="sset">
        <h4>来源</h4>
        <List
          label="来源"
          items={branches}
          textValue={(b) => b.name}
          selectedKeys={editing ? [editing.id] : []}
          onSelectionChange={(keys) => {
            const id = [...keys][0];
            if (id !== undefined) setEditingId(String(id));
          }}
          className="branch-list"
        >
          {(b) => (
            <span className="branch-row">
              <span className="branch-name">{b.name}</span>
              <span className="faint">{branchDescription(b)}</span>
            </span>
          )}
        </List>
        {adding ? (
          <AddBranchForm
            session={session}
            onDone={(branchId) => {
              setAdding(false);
              if (branchId) setEditingId(branchId);
            }}
          />
        ) : (
          <div className="btnrow">
            <Button variant="tonal" size="sm" icon="add" isDisabled={!session.supportsCommand('lyrics.addTranslationBranch')} onPress={() => setAdding(true)}>
              添加翻译来源
            </Button>
          </div>
        )}
      </section>

      {editing && (
        <BranchForm
          // 权威值变化（保存成功、撤销）时重建草稿。
          key={`${editing.id}|${editing.name}|${editing.language ?? ''}|${editing.role}|${editing.timingMode}`}
          session={session}
          branch={editing}
          isPrimary={editing.id === state.primaryBranchId}
          onLink={() => props.onLink(editing.id)}
        />
      )}
    </Sheet>
  );
}

function AddBranchForm(props: { session: EditorSession; onDone: (branchId?: string) => void }) {
  const [name, setName] = useState('翻译');
  const [language, setLanguage] = useState('');
  const [busy, setBusy] = useState(false);
  const submit = async () => {
    if (!name.trim() || busy) return;
    setBusy(true);
    try {
      const response = await props.session.command({ type: 'lyrics.addTranslationBranch', name: name.trim(), language: language.trim() || null });
      if (!response) return;
      toast(`已添加翻译来源「${name.trim()}」`);
      props.onDone(response.branchResult?.selectedBranchId);
    } finally {
      setBusy(false);
    }
  };
  return (
    <div className="branch-add" role="group" aria-label="添加翻译来源">
      <div className="g2">
        <TextField label="新来源名称" value={name} error={name.trim() ? undefined : '名称不能为空'} onChange={setName} onEnter={() => void submit()} autoFocus />
        <TextField label="新来源语言（可留空）" value={language} helper="例如 zh-Hans、en" onChange={setLanguage} onEnter={() => void submit()} />
      </div>
      <p className="hint">新来源是空的，时间策略为“跟随主歌词”。添加后可以导入或粘贴歌词，再关联到主歌词。</p>
      <div className="btnrow">
        <span className="spacer" />
        <Button variant="text" size="sm" isDisabled={busy} onPress={() => props.onDone()}>
          取消
        </Button>
        <Button variant="filled" size="sm" isPending={busy} isDisabled={!name.trim()} onPress={() => void submit()}>
          添加
        </Button>
      </div>
    </div>
  );
}

function BranchForm(props: { session: EditorSession; branch: LyricBranchSummary; isPrimary: boolean; onLink: () => void }) {
  const { session, branch, isPrimary } = props;
  const [name, setName] = useState(branch.name);
  const [language, setLanguage] = useState(branch.language ?? '');
  const [role, setRole] = useState<LyricBranchRole>(branch.role);
  const [timingMode, setTimingMode] = useState<LyricBranchTimingMode>(branch.timingMode);
  const [busy, setBusy] = useState(false);

  const changed = name.trim() !== branch.name || language.trim() !== (branch.language ?? '') || role !== branch.role || timingMode !== branch.timingMode;
  const save = async () => {
    if (!changed || !name.trim() || busy) return;
    setBusy(true);
    try {
      const response = await session.command({
        type: 'lyrics.configureBranch',
        branchId: branch.id,
        name: name.trim(),
        language: language.trim() || null,
        role,
        timingMode,
      });
      if (response?.changed) toast(`已保存「${name.trim()}」的设置`);
    } finally {
      setBusy(false);
    }
  };

  const remove = async () => {
    const ok = await confirmAction({
      headline: `删除来源「${branch.name}」？`,
      description: branch.lineCount > 0 ? `它的 ${branch.lineCount} 行歌词会一起删除。可以撤销。` : '这个来源是空的。可以撤销。',
      confirmText: '删除',
      danger: true,
    });
    if (!ok) return;
    const response = await session.command({ type: 'lyrics.removeBranch', branchId: branch.id });
    if (response?.changed) toast(`已删除来源「${branch.name}」`);
  };

  const canConfigure = session.supportsCommand('lyrics.configureBranch');
  return (
    <section className="sset" aria-label="来源设置">
      <h4>来源设置</h4>
      <div className="g2">
        <TextField label="名称" value={name} error={name.trim() ? undefined : '名称不能为空'} disabled={!canConfigure} onChange={setName} onEnter={() => void save()} />
        <TextField label="语言（可留空）" value={language} helper="例如 ja、zh-Hans、en" disabled={!canConfigure} onChange={setLanguage} onEnter={() => void save()} />
      </div>
      <Select
        label="角色"
        value={role}
        options={roleOptions(isPrimary).map((r) => ({ value: r, label: BRANCH_ROLE_TEXT[r] }))}
        disabled={isPrimary || !canConfigure}
        onChange={setRole}
      />
      {isPrimary && <p>主歌词不能改变角色。</p>}
      <Select
        label="时间策略"
        value={timingMode}
        options={timingModeOptions(isPrimary, branch.timingMode).map((m) => ({ value: m, label: TIMING_MODE_TEXT[m] }))}
        disabled={!canConfigure}
        onChange={setTimingMode}
      />
      <p>{TIMING_MODE_HINT[timingMode]}</p>
      <div className="btnrow">
        <Button variant="filled" size="sm" isPending={busy} isDisabled={!changed || !name.trim() || !canConfigure} onPress={() => void save()}>
          保存
        </Button>
        {!isPrimary && (
          <Button variant="text" size="sm" icon="link" isDisabled={!session.supportsCommand('lyrics.linkPrimaryLine') || branch.lineCount === 0} onPress={props.onLink}>
            关联主歌词…
          </Button>
        )}
        <span className="spacer" />
        <Button variant="text" size="sm" icon="delete" danger isDisabled={isPrimary || !session.supportsCommand('lyrics.removeBranch')} onPress={() => void remove()}>
          删除来源
        </Button>
      </div>
      {isPrimary && <p>主歌词不能删除。</p>}
    </section>
  );
}

const TIMING_MODE_HINT: Record<LyricBranchTimingMode, string> = {
  independent: '每行用自己的时间显示。',
  followPrimary: '关联了主歌词的行跟随那一句显示；没有关联的行用自己的时间。',
  none: '没有时间，由画面编排决定是否显示。',
};
