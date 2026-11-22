import { useEffect, useMemo, useState } from 'react';
import type { AppearanceStore } from '../appearance/appearance-store';
import { lineTarget } from '../model/appearance-targets';
import { BRANCH_ROLE_TEXT, lineNumbers } from '../model/branches';
import { batchHistoryText } from '../model/edit-batches';
import { linePlacement } from '../model/line-placement';
import type { OrchestrationStore } from '../orchestration/orchestration-store';
import type { EditorSession, EditorState } from '../session/editor-session';
import { Button as AriaButton } from 'react-aria-components';
import { Button, IconButton } from '../ui/Button';
import { confirmAction } from '../ui/dialogs';
import { toast } from '../ui/feedback';
import { Icon } from '../ui/Icon';
import { ContextMenu, Menu, MenuItem, MenuSection, MenuSeparator, MenuTrigger } from '../ui/Menu';
import { ImportLyricsDialog, PasteLyricsDialog, type ImportedReport } from './AddLyricsDialogs';
import { ImportReportDialog } from './ImportReportDialog';
import { lineActionItems, type LineAction, type LineActions } from './line-actions';
import { LineList, statusCounts, type LineNote } from './LineList';
import { ResplitBranchDialog } from './ResplitDialogs';
import { SourcesSheet } from './SourcesSheet';
import { lineHasAnyTiming } from './timing-keymap';
import './lyrics.css';

const SHOW_TRANSLATIONS_KEY = 'mako.editor.showTranslationsInLineList';

/**
 * 歌词页左栏（界面改版稿件 #lyrics）：标题行是来源选择（含“管理来源…”）、添加歌词、多选、来源操作；
 * 下面是行列表，最下面一行是行数与时间状态的统计。多选时列表上方出现批量操作条。
 * 单行的操作（插入、复制、删除、重切、关联）在行编辑区标题栏的“行操作”里；右键一行在指针处弹出同样的菜单。
 */
export function LinePane(props: {
  session: EditorSession;
  state: EditorState;
  onUndo: () => void;
  actions: LineActions;
  appearance?: AppearanceStore | null;
  orchestration?: OrchestrationStore | null;
  onOpenPage?: (page: 'appearance' | 'orchestration') => void;
  onClearBranchTiming?: () => void;
}) {
  const { session, state, actions } = props;
  const [multi, setMulti] = useState(false);
  const [marked, setMarked] = useState<ReadonlySet<string>>(new Set());
  const [removing, setRemoving] = useState<string | null>(null);
  const [resplitBranchOpen, setResplitBranchOpen] = useState(false);
  const [sourcesOpen, setSourcesOpen] = useState(false);
  const [menu, setMenu] = useState<{ lineId: string; at: { x: number; y: number } } | null>(null);
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

  const setMultiMode = (on: boolean) => {
    setMulti(on);
    if (!on) setMarked(new Set());
  };

  // 批量删除（G13）：一次 project.applyEdits，一条历史；超过 EDIT_BATCH_SIZE 行时分批，每批一条历史。
  const removeMarked = async () => {
    const ids = state.lines.filter((l) => marked.has(l.id)).map((l) => l.id);
    if (ids.length === 0) return;
    const ok = await confirmAction({ headline: `删除 ${ids.length} 行？`, description: `${batchHistoryText(ids.length)}。`, confirmText: '删除', danger: true });
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

  const branches = state.branches ?? [];
  const branch = branches.find((b) => b.id === state.branchId);
  const isPrimary = state.branchId === state.primaryBranchId || !state.primaryBranchId;
  const translationNames = branches.filter((b) => b.id !== state.primaryBranchId && b.role === 'translation').map((b) => b.name);
  const notes = useLineNotes(state, isPrimary && showTranslations);
  const canImport = !!state.branches && session.supportsCommand('lyrics.importAsset');
  const canPaste = !!state.branches && session.supportsCommand('lyrics.appendText');
  const canAddLine = session.supportsCommand('lyrics.addLine');
  const counts = useMemo(() => statusCounts(state.lines), [state.lines]);
  const bulk = multi || marked.size > 1;

  return (
    <aside className="line-pane">
      <header className="lp-head">
        <MenuTrigger>
          <AriaButton className="srcpick" aria-label={`歌词来源：${branch?.name ?? '主歌词'}`} isDisabled={!state.branches}>
            <span className="srcpick-name">{branch?.name ?? '主歌词'}</span>
            <Icon name="arrow_drop_down" />
          </AriaButton>
          <Menu
            label="歌词来源"
            onAction={(id) => {
              if (id === 'manage') setSourcesOpen(true);
              else session.selectBranch(String(id));
            }}
          >
            <MenuSection selectionMode="single" selectedKeys={[state.branchId]}>
              {branches.map((b) => (
                <MenuItem key={b.id} id={b.id} description={`${BRANCH_ROLE_TEXT[b.role]} · ${b.lineCount} 行`}>
                  {b.name}
                </MenuItem>
              ))}
            </MenuSection>
            <MenuSeparator />
            <MenuItem id="manage" icon="library_books">
              管理来源…
            </MenuItem>
          </Menu>
        </MenuTrigger>
        <span className="spacer" />
        <MenuTrigger>
          <IconButton icon="add" label="添加歌词" size="sm" isDisabled={!canImport && !canPaste && !lastImport} />
          <Menu label="添加歌词" placement="bottom end" onAction={(id) => (id === 'report' ? setReportOpen(true) : setAdding(id as 'import' | 'paste'))}>
            <MenuItem id="import" icon="upload_file" disabled={!canImport}>
              导入文件…
            </MenuItem>
            <MenuItem id="paste" icon="content_paste" disabled={!canPaste}>
              粘贴文本…
            </MenuItem>
            <MenuItem id="report" icon="description" disabled={!lastImport}>
              查看上次导入报告
            </MenuItem>
          </Menu>
        </MenuTrigger>
        <IconButton icon="checklist" label="多选" size="sm" selected={multi} onSelectedChange={setMultiMode} isDisabled={state.lines.length === 0} />
        <MenuTrigger>
          <IconButton icon="more_horiz" label="来源操作" size="sm" />
          <Menu
            label="来源操作"
            placement="bottom end"
            onAction={(id) => {
              if (id === 'resplit') setResplitBranchOpen(true);
              else if (id === 'translations') toggleTranslations();
              else if (id === 'link') actions.openLink(state.branchId);
              else if (id === 'clearBranchTiming') props.onClearBranchTiming?.();
            }}
          >
            <MenuItem id="resplit" icon="call_split" disabled={state.lines.length === 0 || !session.supportsCommand('lyrics.resplitBranch')}>
              按规则重切整个来源…
            </MenuItem>
            {props.onClearBranchTiming && (
              <MenuItem
                id="clearBranchTiming"
                icon="delete"
                danger
                disabled={
                  state.lines.length === 0 ||
                  !state.lines.some(lineHasAnyTiming) ||
                  !session.supportsCommand('lyrics.clearBranchTiming')
                }
              >
                清除全部时间戳（保留文本与分段）…
              </MenuItem>
            )}
            {isPrimary ? (
              <MenuSection selectionMode="multiple" selectedKeys={showTranslations ? ['translations'] : []}>
                <MenuItem id="translations" disabled={branches.length < 2}>
                  在主歌词下显示翻译
                </MenuItem>
              </MenuSection>
            ) : (
              <MenuItem id="link" icon="link" disabled={state.lines.length === 0 || !session.supportsCommand('lyrics.linkPrimaryLine')}>
                关联主歌词…
              </MenuItem>
            )}
          </Menu>
        </MenuTrigger>
      </header>

      {bulk && (
        <div className="bulk-bar" role="toolbar" aria-label="多选操作">
          <span className="bulk-count">{removing ?? `已选 ${marked.size} 行`}</span>
          <span className="spacer" />
          {multi && (
            <Button variant="text" size="sm" isDisabled={!!removing} onPress={() => setMarked(new Set(state.lines.map((l) => l.id)))}>
              全选
            </Button>
          )}
          <Button variant="text" size="sm" isDisabled={!!removing || marked.size === 0} onPress={() => setMarked(new Set())}>
            清空
          </Button>
          <Button variant="tonal" size="sm" danger icon="delete" isDisabled={!!removing || marked.size === 0 || !session.supportsCommand('lyrics.removeLine')} onPress={() => void removeMarked()}>
            删除
          </Button>
        </div>
      )}

      {state.lines.length === 0 ? (
        <div className="lp-empty">
          <p>{branch ? `「${branch.name}」还没有歌词` : '还没有歌词'}</p>
          <Button variant="tonal" size="sm" icon="upload_file" isDisabled={!canImport} onPress={() => setAdding('import')}>
            导入歌词文件…
          </Button>
          <Button variant="tonal" size="sm" icon="content_paste" isDisabled={!canPaste} onPress={() => setAdding('paste')}>
            粘贴文本…
          </Button>
          <Button variant="text" size="sm" icon="add" isDisabled={!canAddLine} onPress={() => void session.command({ type: 'lyrics.addLine', branchId: state.branchId })}>
            新建一行
          </Button>
        </div>
      ) : (
        <LineList
          lines={state.lines}
          selectedLineId={state.selection?.lineId}
          marked={marked}
          multi={multi}
          notes={notes}
          onSelect={(lineId) => session.select({ lineId })}
          onMark={setMarked}
          onContextMenu={(lineId, at) => {
            // 右键多选里的行时保留多选（菜单仍只作用于这一行）；否则先选中它
            if (!marked.has(lineId)) {
              setMarked(new Set());
              session.select({ lineId });
            }
            setMenu({ lineId, at });
          }}
        />
      )}

      <footer className="lp-foot" aria-label="时间状态统计">
        <span>{state.lines.length} 行</span>
        {counts.ok > 0 && (
          <span className="tone-ok" title="已确认或已修改">
            <Icon name="check_circle" filled />
            {counts.ok}
          </span>
        )}
        {counts.partial > 0 && (
          <span className="tone-warn" title="估算或导入">
            <Icon name="contrast" />
            {counts.partial}
          </span>
        )}
        {counts.missing > 0 && (
          <span title="未打轴">
            <Icon name="radio_button_unchecked" />
            {counts.missing}
          </span>
        )}
        {counts.invalid > 0 && (
          <span className="tone-error" title="时间无效">
            <Icon name="error" filled />
            {counts.invalid}
          </span>
        )}
        <span className="spacer" />
        {isPrimary && showTranslations && translationNames.length > 0 && <span className="lp-trans">翻译：{translationNames.join('、')}</span>}
      </footer>

      <ContextMenu
        at={menu?.at ?? null}
        label="行操作"
        onClose={() => setMenu(null)}
        onAction={(id) => {
          const line = state.lines.find((l) => l.id === menu?.lineId);
          if (!line) return;
          if (id === 'goAppearance' && props.appearance && props.onOpenPage) {
            props.appearance.setTargets([lineTarget(line)]);
            props.onOpenPage('appearance');
            return;
          }
          if (id === 'goOrchestration' && props.orchestration && props.onOpenPage) {
            const orchModel = props.orchestration.state$.get().model;
            const placement = orchModel ? linePlacement(orchModel, line, state.lines) : undefined;
            props.orchestration.setBranch(line.branchId);
            if (placement?.clipId) props.orchestration.selectClip(placement.clipId);
            else if (placement?.containerId) props.orchestration.select(placement.containerId);
            props.onOpenPage('orchestration');
            return;
          }
          actions.run(id as LineAction, line);
        }}
      >
        {lineActionItems(session, isPrimary)}
        {props.onOpenPage && (props.appearance || props.orchestration) && (
          <>
            <MenuSeparator />
            {props.appearance && (
              <MenuItem id="goAppearance" icon="palette">
                在外观页查看样式…
              </MenuItem>
            )}
            {props.orchestration && (
              <MenuItem id="goOrchestration" icon="dashboard">
                在编排页查看位置…
              </MenuItem>
            )}
          </>
        )}
      </ContextMenu>
      <ResplitBranchDialog session={session} branchId={state.branchId} open={resplitBranchOpen} onClose={() => setResplitBranchOpen(false)} />
      <SourcesSheet
        session={session}
        state={state}
        open={sourcesOpen}
        onClose={() => setSourcesOpen(false)}
        onLink={(branchId) => {
          setSourcesOpen(false);
          actions.openLink(branchId);
        }}
      />
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
      <PasteLyricsDialog session={session} state={state} open={adding === 'paste'} onClose={() => setAdding(null)} onLink={(branchId) => actions.openLink(branchId)} />
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
        notes.set(line.primaryLineId, { extra: note?.extra ? `${note.extra}／${line.text}` : line.text });
      }
    }
    return notes;
  }, [branchId, primaryBranchId, branches, linesByBranch, lines, showTranslations]);
}
