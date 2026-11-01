import { useRef, useState } from 'react';
import type { LyricBranchRole, LyricBranchSummary, LyricBranchTimingMode } from '../../protocol';
import { BRANCH_ROLE_TEXT, TIMING_MODE_TEXT, branchDescription, roleOptions, timingModeOptions } from '../model/branches';
import type { EditorSession, EditorState } from '../session/editor-session';
import { confirmAction } from '../ui/legacy/dialogs';
import { toast } from '../ui/legacy/feedback';
import { Select, TextField } from '../ui/legacy/fields';
import { useDomEvent } from '../ui/hooks';

/**
 * 管理来源（整体设计 §5.2）：右侧面板列出所有来源；选中一项在下面修改名称、语言、角色、时间策略，或删除。
 * 主歌词不能删除，也不能改角色，相应控件禁用并写明原因。
 */
export function SourcesDrawer(props: {
  session: EditorSession;
  state: EditorState;
  open: boolean;
  onClose: () => void;
  onLink: (branchId: string) => void;
}) {
  const { session, state } = props;
  const ref = useRef<HTMLElement>(null);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);
  useDomEvent(ref, 'close', (event) => event.target === ref.current && props.onClose());

  const branches = state.branches ?? [];
  // 正在编辑的来源被删除（或撤销了新增）时，回到当前来源。
  const editing = branches.find((b) => b.id === editingId) ?? branches.find((b) => b.id === state.branchId) ?? branches[0];

  return (
    <mdui-navigation-drawer ref={ref} open={props.open} modal close-on-esc close-on-overlay-click placement="right" className="sources-drawer">
      <div className="drawer-content">
        <header className="drawer-header">
          <span className="drawer-title">管理来源</span>
          <span className="spacer" />
          <mdui-button-icon aria-label="关闭管理来源" onClick={props.onClose}>
            <mdui-icon-close />
          </mdui-button-icon>
        </header>

        {adding ? (
          <AddBranchForm
            session={session}
            onDone={(branchId) => {
              setAdding(false);
              if (branchId) setEditingId(branchId);
            }}
          />
        ) : (
          <mdui-button variant="tonal" disabled={!session.supportsCommand('lyrics.addTranslationBranch')} onClick={() => setAdding(true)}>
            <mdui-icon-add slot="icon" />
            添加翻译来源
          </mdui-button>
        )}

        <mdui-list className="branch-list">
          {branches.map((b) => (
            <mdui-list-item key={b.id} rounded active={b.id === editing?.id} data-branch-id={b.id} onClick={() => setEditingId(b.id)}>
              <span>{b.name}</span>
              <span slot="description">{branchDescription(b)}</span>
            </mdui-list-item>
          ))}
        </mdui-list>

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
      </div>
    </mdui-navigation-drawer>
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
    <div className="branch-form">
      <div className="section-title">添加翻译来源</div>
      <TextField label="新来源名称" value={name} error={name.trim() ? undefined : '名称不能为空'} onChange={setName} onEnter={() => void submit()} className="drawer-field" />
      <TextField label="新来源语言（可留空）" value={language} helper="例如 zh-Hans、en" onChange={setLanguage} onEnter={() => void submit()} className="drawer-field" />
      <p className="hint">新来源是空的，时间策略为“跟随主歌词”。添加后可以导入或粘贴歌词，再关联到主歌词。</p>
      <div className="form-actions">
        <mdui-button variant="text" disabled={busy} onClick={() => props.onDone()}>
          取消
        </mdui-button>
        <mdui-button variant="tonal" loading={busy} disabled={busy || !name.trim()} onClick={() => void submit()}>
          添加
        </mdui-button>
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
    });
    if (!ok) return;
    const response = await session.command({ type: 'lyrics.removeBranch', branchId: branch.id });
    if (response?.changed) toast(`已删除来源「${branch.name}」`);
  };

  const canConfigure = session.supportsCommand('lyrics.configureBranch');
  return (
    <div className="branch-form">
      <div className="section-title">来源设置</div>
      <TextField label="名称" value={name} error={name.trim() ? undefined : '名称不能为空'} disabled={!canConfigure} onChange={setName} onEnter={() => void save()} className="drawer-field" />
      <TextField label="语言（可留空）" value={language} helper="例如 ja、zh-Hans、en" disabled={!canConfigure} onChange={setLanguage} onEnter={() => void save()} className="drawer-field" />
      <Select
        label="角色"
        value={role}
        options={roleOptions(isPrimary).map((r) => ({ value: r, label: BRANCH_ROLE_TEXT[r] }))}
        disabled={isPrimary || !canConfigure}
        onChange={setRole}
        className="drawer-field"
      />
      {isPrimary && <p className="hint field-reason">主歌词不能改变角色。</p>}
      <Select
        label="时间策略"
        value={timingMode}
        options={timingModeOptions(isPrimary, branch.timingMode).map((m) => ({ value: m, label: TIMING_MODE_TEXT[m] }))}
        disabled={!canConfigure}
        onChange={setTimingMode}
        className="drawer-field"
      />
      <p className="hint field-reason">{TIMING_MODE_HINT[timingMode]}</p>
      <div className="form-actions">
        <mdui-button variant="tonal" loading={busy} disabled={!changed || !name.trim() || busy || !canConfigure} onClick={() => void save()}>
          保存
        </mdui-button>
        {!isPrimary && (
          <mdui-button variant="text" disabled={!session.supportsCommand('lyrics.linkPrimaryLine') || branch.lineCount === 0} onClick={props.onLink}>
            <mdui-icon-link slot="icon" />
            关联主歌词…
          </mdui-button>
        )}
        <span className="spacer" />
        <mdui-button variant="text" disabled={isPrimary || !session.supportsCommand('lyrics.removeBranch')} onClick={() => void remove()}>
          <mdui-icon-delete slot="icon" />
          删除来源
        </mdui-button>
      </div>
      {isPrimary && <p className="hint field-reason">主歌词不能删除。</p>}
    </div>
  );
}

const TIMING_MODE_HINT: Record<LyricBranchTimingMode, string> = {
  independent: '每行用自己的时间显示。',
  followPrimary: '关联了主歌词的行跟随那一句显示；没有关联的行用自己的时间。',
  none: '没有时间，由画面编排决定是否显示。',
};
