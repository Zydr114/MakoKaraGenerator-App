import { useCallback, useEffect, useMemo, useState } from 'react';
import type { ProjectCommand } from '../../protocol';
import { lineNumbers } from '../model/branches';
import { batchHistoryText } from '../model/edit-batches';
import type { EditorLine } from '../model/lyrics';
import type { EditorSession, EditorState } from '../session/editor-session';
import { confirmAction } from '../ui/dialogs';
import { toast } from '../ui/feedback';
import { Select } from '../ui/fields';
import { ImportLyricsDialog, PasteLyricsDialog, type ImportedReport } from './AddLyricsDialogs';
import { ImportReportDialog } from './ImportReportDialog';
import { LineList, type LineAction, type LineNote } from './LineList';
import { LinkDialog } from './LinkDialog';
import { ResplitBranchDialog, ResplitLineDialog } from './ResplitDialogs';
import { SourcesDrawer } from './SourcesDrawer';

const SHOW_TRANSLATIONS_KEY = 'mako.editor.showTranslationsInLineList';
const PRIMARY_ACTIONS: readonly LineAction[] = ['insertAfter', 'duplicate', 'remove', 'resplit'];
const SECONDARY_ACTIONS: readonly LineAction[] = [...PRIMARY_ACTIONS, 'link'];

const ACTION_COMMAND: Record<LineAction, ProjectCommand['type']> = {
  insertAfter: 'lyrics.addLine',
  duplicate: 'lyrics.duplicateLine',
  remove: 'lyrics.removeLine',
  resplit: 'lyrics.resplitLine',
  link: 'lyrics.linkPrimaryLine',
};

/** 歌词页左栏：来源选择与管理、添加歌词、多选与批量删除、行列表和行操作、翻译关联。 */
export function LinePane(props: { session: EditorSession; state: EditorState; onUndo: () => void }) {
  const { session, state } = props;
  const [multi, setMulti] = useState(false);
  const [marked, setMarked] = useState<ReadonlySet<string>>(new Set());
  const [removing, setRemoving] = useState<string | null>(null);
  const [resplitLineId, setResplitLineId] = useState<string | null>(null);
  const [resplitBranchOpen, setResplitBranchOpen] = useState(false);
  const [sourcesOpen, setSourcesOpen] = useState(false);
  const [link, setLink] = useState<{ branchId: string; focusLineId?: string } | null>(null);
  const [adding, setAdding] = useState<'import' | 'paste' | null>(null);
  const [lastImport, setLastImport] = useState<ImportedReport | null>(null);
  const [reportOpen, setReportOpen] = useState(false);
  const [showTranslations, setShowTranslations] = useState(() => localStorage.getItem(SHOW_TRANSLATIONS_KEY) !== '0');
  const toggleTranslations = () => {
    localStorage.setItem(SHOW_TRANSLATIONS_KEY, showTranslations ? '0' : '1');
    setShowTranslations(!showTranslations);
  };

  // 换来源时清空多选；行被删掉后从多选里去掉。
  useEffect(() => setMarked(new Set()), [state.branchId]);
  useEffect(() => {
    setMarked((current) => {
      if (current.size === 0) return current;
      const alive = new Set(state.lines.filter((l) => current.has(l.id)).map((l) => l.id));
      return alive.size === current.size ? current : alive;
    });
  }, [state.lines]);

  const available = useCallback((action: LineAction) => session.supportsCommand(ACTION_COMMAND[action]), [session]);

  const act = useCallback(
    async (action: LineAction, line: EditorLine) => {
      const number = session.state.lines.findIndex((l) => l.id === line.id) + 1;
      const target = { branchId: line.branchId, lineId: line.id };
      if (action === 'resplit') {
        setResplitLineId(line.id);
      } else if (action === 'link') {
        setLink({ branchId: line.branchId, focusLineId: line.id });
      } else if (action === 'insertAfter') {
        const response = await session.command({ type: 'lyrics.addLine', branchId: line.branchId, afterLineId: line.id });
        if (response?.changed) toast(`已在第 ${number} 行下方插入空行`);
      } else if (action === 'duplicate') {
        const response = await session.command({ type: 'lyrics.duplicateLine', target });
        if (response?.changed) {
          if (response.feedback?.overlapsNext) session.notify({ level: 'warning', message: `第 ${number} 行的副本与下一行时间重叠，请调整时间。` });
          toast(`已复制第 ${number} 行`);
        }
      } else {
        const response = await session.command({ type: 'lyrics.removeLine', target });
        if (response?.changed) toast(`已删除第 ${number} 行`, { label: '撤销', run: props.onUndo });
      }
    },
    [session, props.onUndo],
  );

  // 批量删除（G13）：一次 project.applyEdits，一条历史；超过 EDIT_BATCH_SIZE 行时分批，每批一条历史。
  const removeMarked = async () => {
    const ids = state.lines.filter((l) => marked.has(l.id)).map((l) => l.id);
    if (ids.length === 0) return;
    const ok = await confirmAction({
      headline: `删除 ${ids.length} 行？`,
      description: `${batchHistoryText(ids.length)}。`,
      confirmText: '删除',
    });
    if (!ok) return;
    setRemoving(`正在删除 ${ids.length} 行`);
    let result: Awaited<ReturnType<EditorSession['applyEdits']>>;
    try {
      result = await session.applyEdits(ids.map((lineId) => ({ type: 'lyrics.removeLine', target: { branchId: state.branchId, lineId } })));
    } finally {
      setRemoving(null);
      setMarked(new Set());
    }
    const { applied, batches } = result;
    if (applied === 0) return;
    if (applied < ids.length) toast(`已删除 ${applied} 行，其余 ${ids.length - applied} 行未删除`);
    else if (batches > 1) toast(`已删除 ${applied} 行（${batches} 条历史）`);
    else toast(`已删除 ${applied} 行`, { label: '撤销', run: props.onUndo });
  };

  const addFirstLine = () => void session.command({ type: 'lyrics.addLine', branchId: state.branchId });
  const branch = state.branches?.find((b) => b.id === state.branchId);
  const isPrimary = state.branchId === state.primaryBranchId || !state.primaryBranchId;
  const hasSecondary = (state.branches?.length ?? 0) > 1;
  const notes = useLineNotes(state, isPrimary && showTranslations);
  const canImport = !!state.branches && session.supportsCommand('lyrics.importAsset');
  const canPaste = !!state.branches && session.supportsCommand('lyrics.appendText');

  return (
    <aside className="pane pane-lines">
      <div className="pane-lines-header">
        {state.branches && state.branches.length > 1 ? (
          <Select
            label="歌词来源"
            value={state.branchId}
            options={state.branches.map((b) => ({ value: b.id, label: b.name }))}
            onChange={(id) => session.selectBranch(id)}
            className="branch-select"
          />
        ) : (
          <div className="pane-title">
            {branch?.name ?? '主歌词'} <span className="muted">{state.lines.length} 行</span>
          </div>
        )}
        <mdui-button variant="text" disabled={!state.branches} onClick={() => setSourcesOpen(true)}>
          <mdui-icon-library-books slot="icon" />
          管理来源
        </mdui-button>
      </div>
      <div className="pane-lines-toolbar">
        <mdui-dropdown placement="bottom-start">
          <mdui-button slot="trigger" variant="text" disabled={!canImport && !canPaste && !lastImport}>
            <mdui-icon-add slot="icon" />
            添加歌词
            <mdui-icon-arrow-drop-down slot="end-icon" />
          </mdui-button>
          <mdui-menu>
            <mdui-menu-item disabled={!canImport} onClick={() => setAdding('import')}>
              <mdui-icon-upload-file slot="icon" />
              导入文件…
            </mdui-menu-item>
            <mdui-menu-item disabled={!canPaste} onClick={() => setAdding('paste')}>
              <mdui-icon-content-paste slot="icon" />
              粘贴文本…
            </mdui-menu-item>
            <mdui-menu-item disabled={!lastImport} onClick={() => setReportOpen(true)}>
              <mdui-icon-description slot="icon" />
              查看上次导入报告
            </mdui-menu-item>
          </mdui-menu>
        </mdui-dropdown>
        <span className="spacer" />
        <mdui-button
          variant={multi ? 'tonal' : 'text'}
          disabled={state.lines.length === 0}
          onClick={() => {
            setMulti(!multi);
            if (multi) setMarked(new Set());
          }}
        >
          多选
        </mdui-button>
        <mdui-dropdown placement="bottom-end">
          <mdui-button-icon slot="trigger" aria-label="来源操作">
            <mdui-icon-more-vert />
          </mdui-button-icon>
          <mdui-menu>
            <mdui-menu-item disabled={state.lines.length === 0 || !session.supportsCommand('lyrics.resplitBranch')} onClick={() => setResplitBranchOpen(true)}>
              <mdui-icon-call-split slot="icon" />
              按规则重切整个来源…
            </mdui-menu-item>
            {isPrimary ? (
              <mdui-menu-item disabled={!hasSecondary} onClick={toggleTranslations}>
                {showTranslations ? <mdui-icon-check slot="icon" /> : <span slot="icon" className="menu-icon-blank" />}
                在主歌词下显示翻译
              </mdui-menu-item>
            ) : (
              <mdui-menu-item
                disabled={state.lines.length === 0 || !session.supportsCommand('lyrics.linkPrimaryLine')}
                onClick={() => setLink({ branchId: state.branchId })}
              >
                <mdui-icon-link slot="icon" />
                关联主歌词…
              </mdui-menu-item>
            )}
          </mdui-menu>
        </mdui-dropdown>
      </div>

      {(multi || marked.size > 0) && (
        <div className="bulk-bar">
          <span>{removing ?? `已选 ${marked.size} 行`}</span>
          <span className="spacer" />
          {multi && (
            <mdui-button variant="text" disabled={!!removing} onClick={() => setMarked(new Set(state.lines.map((l) => l.id)))}>
              全选
            </mdui-button>
          )}
          <mdui-button variant="text" disabled={!!removing || marked.size === 0} onClick={() => setMarked(new Set())}>
            清空
          </mdui-button>
          <mdui-button variant="tonal" disabled={!!removing || marked.size === 0 || !available('remove')} onClick={() => void removeMarked()}>
            删除
          </mdui-button>
        </div>
      )}

      {state.lines.length === 0 ? (
        <div className="empty-state">
          <span>{branch ? `「${branch.name}」还没有歌词` : '还没有歌词'}</span>
          <div className="empty-actions">
            <mdui-button variant="tonal" disabled={!canImport} onClick={() => setAdding('import')}>
              <mdui-icon-upload-file slot="icon" />
              导入歌词文件…
            </mdui-button>
            <mdui-button variant="tonal" disabled={!canPaste} onClick={() => setAdding('paste')}>
              <mdui-icon-content-paste slot="icon" />
              粘贴文本…
            </mdui-button>
            <mdui-button variant="text" disabled={!available('insertAfter')} onClick={addFirstLine}>
              新建一行
            </mdui-button>
          </div>
        </div>
      ) : (
        <LineList
          lines={state.lines}
          selectedLineId={state.selection?.lineId}
          marked={marked}
          multi={multi}
          actions={isPrimary ? PRIMARY_ACTIONS : SECONDARY_ACTIONS}
          available={available}
          notes={notes}
          onSelect={(lineId) => session.select({ lineId })}
          onMark={setMarked}
          onAction={(action, line) => void act(action, line)}
        />
      )}

      <ResplitLineDialog
        session={session}
        line={state.lines.find((l) => l.id === resplitLineId)}
        open={resplitLineId !== null}
        onClose={() => setResplitLineId(null)}
      />
      <ResplitBranchDialog session={session} branchId={state.branchId} open={resplitBranchOpen} onClose={() => setResplitBranchOpen(false)} />
      <SourcesDrawer
        session={session}
        state={state}
        open={sourcesOpen}
        onClose={() => setSourcesOpen(false)}
        onLink={(branchId) => {
          setSourcesOpen(false);
          setLink({ branchId });
        }}
      />
      <LinkDialog session={session} branchId={link?.branchId ?? null} focusLineId={link?.focusLineId} onClose={() => setLink(null)} />
      <ImportLyricsDialog
        session={session}
        state={state}
        open={adding === 'import'}
        onClose={() => setAdding(null)}
        onImported={(imported) => {
          setLastImport(imported);
          setReportOpen(true);
        }}
      />
      <PasteLyricsDialog session={session} state={state} open={adding === 'paste'} onClose={() => setAdding(null)} onLink={(branchId) => setLink({ branchId })} />
      <ImportReportDialog imported={lastImport} open={reportOpen} onClose={() => setReportOpen(false)} />
    </aside>
  );
}

/**
 * 行列表的附加说明（整体设计 §5.2）：非主来源每行显示关联的主歌词，“跟随主歌词”的行时间显示为“跟随 第 N 行”；
 * 主来源可选在每行下显示关联的翻译。
 */
function useLineNotes(state: EditorState, showTranslations: boolean): ReadonlyMap<string, LineNote> | undefined {
  const { branchId, primaryBranchId, branches, linesByBranch, lines } = state;
  return useMemo(() => {
    if (!primaryBranchId || !branches) return undefined;
    const primaryLines = linesByBranch[primaryBranchId] ?? [];
    const notes = new Map<string, LineNote>();
    if (branchId !== primaryBranchId) {
      const follows = branches.find((b) => b.id === branchId)?.timingMode === 'followPrimary';
      const numbers = lineNumbers(primaryLines);
      const byId = new Map(primaryLines.map((l) => [l.id, l]));
      for (const line of lines) {
        if (!line.primaryLineId) continue;
        const number = numbers.get(line.primaryLineId);
        const primary = byId.get(line.primaryLineId);
        notes.set(
          line.id,
          number && primary
            ? { ...(follows ? { time: `跟随 第 ${number} 行` } : {}), extra: `主歌词 ${number}：${primary.text || '（空行）'}` }
            : { extra: '关联的主歌词行已不存在' },
        );
      }
      return notes;
    }
    if (!showTranslations) return undefined;
    for (const b of branches) {
      if (b.id === primaryBranchId || b.role !== 'translation') continue;
      for (const line of linesByBranch[b.id] ?? []) {
        if (!line.primaryLineId || !line.text) continue;
        const note = notes.get(line.primaryLineId);
        notes.set(line.primaryLineId, { extra: note?.extra ? `${note.extra}／${line.text}` : `翻译：${line.text}` });
      }
    }
    return notes;
  }, [branchId, primaryBranchId, branches, linesByBranch, lines, showTranslations]);
}
