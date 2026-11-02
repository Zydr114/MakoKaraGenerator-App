import { memo, useCallback, useEffect, useMemo, useRef, useState, type CSSProperties, type MouseEvent } from 'react';
import type { LyricBranchSummary, OrchestrationNode, PresetApplication, TemplateLibraryId } from '../../protocol';
import type { AudioController } from '../audio/audio-controller';
import { templateFacts } from '../connection';
import { NoAudioBar } from '../audio/NoAudioBar';
import { Transport } from '../audio/Transport';
import { TEMPLATE_NAME_MAX_LENGTH, type TemplateLibraryStore } from '../library/template-library-store';
import type { TemplateThumbnails } from '../library/template-thumbnails';
import {
  clipTarget,
  describeTargets,
  lineTarget,
  MAX_TARGETS,
  nodeKindText,
  pickTarget,
  PROJECT_TARGET,
  rangeTargets,
  sampleKeyOf,
  TARGET_KIND_TEXT,
  targetCountText,
  targetKey,
  tokenTarget,
  type AppearanceTarget,
  type NodeTarget,
} from '../model/appearance-targets';
import { sameInputNames } from '../model/branch-inputs';
import { BRANCH_ROLE_TEXT } from '../model/branches';
import { isTimed, stepToken, type EditorLine, type Selection } from '../model/lyrics';
import { clipName, clipsOf, lineRange, lineRangeText, nodeName, programOfNode, roleText, structureSections, type OrchestrationModel } from '../model/orchestration';
import { applicationNames, type ApplicationIndex, applicationsOn, nodeScopeOf, updateSource } from '../model/preset-applications';
import { overrideRows, sceneAppearanceRows, type OverrideRow } from '../model/style-overrides';
import { sourceGroups, sourceTarget, sourceText, STYLE_PROPERTY_TEXT, type SourceContext } from '../model/style-sources';
import { clipBands, clipColors } from '../orchestration/clip-bands';
import type { OrchestrationState, OrchestrationStore } from '../orchestration/orchestration-store';
import { nodeIcon } from '../orchestration/StructurePane';
import { PreviewPanel, previewTimeUs } from '../preview/PreviewPanel';
import { fitPreviewSize } from '../preview/preview-controller';
import { errorText, type EditorSession, type EditorState } from '../session/editor-session';
import { Timeline, type TimelineTrack } from '../timeline/Timeline';
import { Button, IconButton } from '../ui/Button';
import { Segmented, Switch } from '../ui/choices';
import { cls } from '../ui/cls';
import { toast } from '../ui/feedback';
import { TextField } from '../ui/fields';
import { useObservable } from '../ui/hooks';
import { Icon, type IconName } from '../ui/Icon';
import { ProgressBar } from '../ui/indicators';
import { Group, InspectorHead, Note, ReadError } from '../workspace/panel';
import { StatusBar } from '../workspace/StatusBar';
import { WorkLayout } from '../workspace/WorkLayout';
import { isEditing } from '../workspace/Workspace';
import { ApplicationsPanel, type ApplicationActions } from './ApplicationsPanel';
import type { AppearanceState, AppearanceStore } from './appearance-store';
import { TemplatePicker, type TemplateChoice } from './TemplatePicker';
import './appearance.css';

const PRESETS_WARNING = '后端预览不支持模板分配：工程里有分配时预览会失败。';

/** 在样式库中编辑副本（W7 设计 §2.7）：复制好之后跳到样式库，带上回来时要恢复的目标。 */
export interface EditCopyRequest {
  targets: readonly AppearanceTarget[];
  /** 返回栏上的目标路径：“主歌词 / 第 2 行”。 */
  targetText: string;
  templateId: string;
}

/** 从样式库“保存并返回试看”回来：恢复目标，打开选择器选中这个个人模板。 */
export interface ReturnTrial {
  targets: readonly AppearanceTarget[];
  templateId: string;
}

/** 编辑副本的来源：分配和它的模板所在的库；库还没读到时 libraryId 为 undefined。 */
interface CopySource {
  app: PresetApplication;
  libraryId: TemplateLibraryId | undefined;
  blocked?: string;
}

/** 副本名称：原名加“（副本）”，超长时截短原名。 */
export function copyName(name: string): string {
  const suffix = '（副本）';
  return `${name.slice(0, TEMPLATE_NAME_MAX_LENGTH - suffix.length)}${suffix}`;
}

/**
 * 外观页（整体设计 §5.4，W3 设计 §2.6、§2.7，W7 设计 §2.7）：左栏目标浏览器（歌词来源和编排对象），中栏成片预览，右栏检查器；
 * 底部是播放条与只读时间轴，点选或框选行、点选片段轨的片段同步目标。
 */
export function AppearancePage(props: {
  session: EditorSession;
  store: AppearanceStore;
  /** 编排状态：编排对象目标从这里取结构树和片段。 */
  orchestration: OrchestrationStore;
  library: TemplateLibraryStore;
  thumbnails: TemplateThumbnails;
  audio: AudioController | null;
  onUndo: () => void;
  onRedo: () => void;
  onSave: () => void;
  /** 复制好副本后去样式库。 */
  onEditCopy: (request: EditCopyRequest) => void;
  /** 从样式库返回试看；处理后调用 onReturnTrialDone。 */
  returnTrial: ReturnTrial | null;
  onReturnTrialDone: () => void;
}) {
  const { session, store, audio, library } = props;
  const state = useObservable(session.state$);
  const view = useObservable(store.state$);
  const libraries = useObservable(library.state$);
  const orchestration = useObservable(props.orchestration.state$);
  const model = orchestration.model;
  const [loopLine, setLoopLine] = useState(false);
  const [picking, setPicking] = useState(false);
  /** 选择器打开时先选中的模板（从样式库返回时）。 */
  const [pickInitial, setPickInitial] = useState<TemplateChoice | null>(null);
  const canApply = session.supportsCommand('presetApplications.apply') && !state.summary.isReadOnly;
  const canvasWidth = state.settings?.canvasWidth;
  const canvasHeight = state.settings?.canvasHeight;
  const previewSize = useMemo(() => fitPreviewSize(canvasWidth, canvasHeight), [canvasWidth, canvasHeight]);
  const branches = useProjectBranches(state);

  useEffect(() => store.followCurrentLine(), [store]);
  useEffect(() => store.setOrchestration(model), [store, model]);
  // “更新”要知道模板现在在哪个库里：进页面时读一次两个库，更新前再重读。
  useEffect(() => {
    if (!library.state.builtin && library.state.status !== 'loading') void library.load();
  }, [library]);

  const actions = useMemo((): ApplicationActions => {
    const has = (libraryId: 'builtin' | 'personal', templateId: string) => !!library.summary(libraryId, templateId);
    return {
      setEnabled: (app, enabled) => void store.setEnabled(app, enabled),
      remove: (apps, text) =>
        void store.remove(apps.map((a) => a.applicationId)).then((ok) => ok && toast(`已移除${text}`, { label: '撤销', run: props.onUndo })),
      update: (app) =>
        void (async () => {
          await library.load();
          const libraryId = updateSource(app, has);
          const list = libraryId ? library.state[libraryId] : undefined;
          const current = libraryId ? library.summary(libraryId, app.snapshot.templateId) : undefined;
          if (!libraryId || !list || !current) {
            session.notify({ level: 'warning', message: `库里已经没有“${app.snapshot.name}”，不能更新。` });
            return;
          }
          // 更新沿用原分配的来源输入映射：库里的当前版本要读取的输入名变了时，后端会拒绝，先在这里写明。
          let names: readonly string[];
          try {
            names = await templateFacts.loadBranchInputs(libraryId, list.revision, current);
          } catch (error) {
            session.notify({ level: 'error', message: `读不到“${app.snapshot.name}”需要读取的来源：${errorText(error)}` });
            return;
          }
          if (!sameInputNames(names, Object.keys(app.branchInputs))) {
            const text = names.length > 0 ? `需要读取 ${names.join('、')}` : '不再读取其他来源';
            session.notify({ level: 'warning', message: `库里的“${app.snapshot.name}”现在${text}，和原分配不同，不能直接更新。请用“换模板…”重新应用。` });
            return;
          }
          const result = await store.update(app, libraryId, list.revision);
          if (result === 'applied') toast(`已按库里的当前版本更新“${app.snapshot.name}”`, { label: '撤销', run: props.onUndo });
          else if (result === 'stale') await library.load();
        })(),
      updateBlocked: (app) => (!libraries.builtin && !libraries.personal ? undefined : updateSource(app, has) ? undefined : '库里已经没有这个模板，不能更新。'),
      entry: (app) => {
        const libraryId = updateSource(app, has);
        const list = libraryId ? library.state[libraryId] : undefined;
        const summary = libraryId ? library.summary(libraryId, app.snapshot.templateId) : undefined;
        return libraryId && list && summary ? { libraryId, revision: list.revision, summary } : undefined;
      },
    };
    // libraries 变化时重新判断能否更新。
  }, [library, libraries, session, store, props.onUndo]);
  useAppearanceKeys({ session, store, audio, onUndo: props.onUndo, onRedo: props.onRedo, onSave: props.onSave });

  // 从样式库返回：原目标还在就恢复并打开选择器试看副本，不在了就请用户重新选择。
  const { returnTrial, onReturnTrialDone } = props;
  useEffect(() => {
    if (!returnTrial) return;
    onReturnTrialDone();
    if (!store.restoreTargets(returnTrial.targets)) {
      session.notify({ level: 'warning', message: '原来的目标已经不存在。请重新选择目标，再用“选择模板…”试看。' });
      return;
    }
    setPickInitial({ libraryId: 'personal', templateId: returnTrial.templateId });
    setPicking(true);
  }, [returnTrial, onReturnTrialDone, store, session]);

  // 在样式库中编辑副本：单选并且有直接分配时可用，按分配快照的模板 ID 在两个库里找原模板。
  const index = view.applications?.index;
  const copySource = useMemo((): CopySource | undefined => {
    if (view.targets.length !== 1 || !index) return undefined;
    const direct = applicationsOn(index, view.targets);
    const app = direct.find((a) => a.enabled) ?? direct[0];
    if (!app) return undefined;
    if (!libraries.builtin || !libraries.personal) return { app, libraryId: undefined };
    const libraryId = updateSource(app, (id, templateId) => !!library.summary(id, templateId));
    return { app, libraryId, ...(libraryId ? {} : { blocked: '库里已经没有这个模板' }) };
  }, [view.targets, index, libraries, library]);
  const [copying, setCopying] = useState(false);
  const editCopy = async () => {
    if (!copySource?.libraryId || copying) return;
    const { app, libraryId } = copySource;
    const targets = store.state.targets;
    setCopying(true);
    try {
      await library.load();
      const current = library.summary(libraryId, app.snapshot.templateId);
      if (!current) {
        session.notify({ level: 'warning', message: `库里已经没有“${app.snapshot.name}”，不能编辑副本。` });
        return;
      }
      const templateId = await library.copy(libraryId, current.templateId, copyName(current.name));
      if (!templateId) {
        session.notify({ level: 'error', message: library.state.notice?.message ?? '复制模板失败。' });
        return;
      }
      props.onEditCopy({ targets, targetText: describeTargets(targets, session.state.branches, session.state.linesByBranch, model), templateId });
    } finally {
      setCopying(false);
    }
  };

  const selectedLine = state.selection ? state.lines.find((l) => l.id === state.selection!.lineId) : undefined;
  useEffect(() => {
    if (!audio) return;
    audio.setLoop(loopLine && selectedLine && isTimed(selectedLine) ? { startUs: selectedLine.startUs, endUs: selectedLine.endUs } : undefined);
  }, [audio, loopLine, selectedLine]);

  // 时间轴高亮：选中的行和文本单元；选中来源时高亮它的全部行，选中片段时高亮片段里的句子。
  const program = model?.programByBranch.get(state.branchId);
  const marked = useMemo(() => {
    const ids = new Set<string>();
    for (const t of view.targets) {
      if (t.kind === 'line') ids.add(t.lineId);
      else if (t.kind === 'token') ids.add(t.tokenId);
      else if (t.kind === 'branch' && t.branchId === state.branchId) for (const l of state.lines) ids.add(l.id);
      else if (t.kind === 'clip') for (const id of program?.clips?.find((c) => c.id === t.clipId)?.lineIds ?? []) ids.add(id);
    }
    return ids;
  }, [view.targets, state.branchId, state.lines, program]);

  // 只读的片段轨（W7 设计 §2.7）：当前来源的片段，选中的片段高亮；点选片段就选中它。
  const clips = useMemo(() => clipsOf(program, state.lines), [program, state.lines]);
  const tracks = useMemo((): TimelineTrack[] | undefined => {
    if (orchestration.status === 'unsupported') return undefined;
    const colors = model && program ? clipColors(model, program) : new Map<string, number>();
    const selected = new Set(view.targets.flatMap((t) => (t.kind === 'clip' ? [t.clipId] : [])));
    return [{ id: 'clips', empty: '没有片段', bands: clipBands(clips, state.lines, colors, selected) }];
  }, [orchestration.status, model, program, clips, state.lines, view.targets]);
  const selectBand = useCallback(
    (_track: string, clipId: string) => {
      const branchId = session.state.branchId;
      store.setTargets([clipTarget(branchId, clipId)]);
    },
    [session, store],
  );

  const selectFromTimeline = useCallback(
    (selection: Selection, options: { additive: boolean }) => {
      const line = session.state.lines.find((l) => l.id === selection.lineId);
      if (!line) return;
      const target = selection.tokenId ? tokenTarget(line, selection.tokenId) : lineTarget(line);
      store.setTargets(pickTarget(store.state.targets, target, options.additive));
    },
    [session, store],
  );

  const selectLines = useCallback(
    (lineIds: string[], options: { additive: boolean }) => {
      const lines = session.state.lines.filter((l) => lineIds.includes(l.id));
      const current = store.state.targets;
      const keep = options.additive && current[0]?.kind === 'line' ? current : [];
      const keys = new Set(keep.map(targetKey));
      const added = lines.map(lineTarget).filter((t) => !keys.has(targetKey(t)));
      store.setTargets([...keep, ...added].slice(0, MAX_TARGETS));
    },
    [session, store],
  );

  return (
    <WorkLayout
      page="appearance"
      className="appearance"
      stage="center"
      left={{
        label: '外观目标',
        content: <TargetBrowser state={state} view={view} orchestration={orchestration} onTargets={(targets, anchorKey) => store.setTargets(targets, anchorKey)} />,
      }}
      center={
        <section className="stage-canvas appearance-canvas">
          <PreviewPanel session={session} audio={audio} warning={view.presetsPreview === false ? PRESETS_WARNING : undefined} />
        </section>
      }
      right={{
        label: '模板与覆盖',
        content: (
          <div className="appearance-inspector">
            <Inspector
              state={state}
              branches={branches}
              view={view}
              model={model}
              thumbnails={props.thumbnails}
              onIncludeDescendants={(value) => store.setIncludeDescendants(value)}
              canApply={canApply}
              canUndoApply={store.canUndoApply}
              actions={actions}
              onPick={() => setPicking(true)}
              onUndoApply={() => void store.undoApply()}
              onRetry={() => store.reloadInspect()}
              onRetryApplications={() => store.reloadApplications()}
              canReadSources={store.canReadSources}
              onSourcesOpen={(open) => store.setSourcesOpen(open)}
              onRetrySources={() => store.reloadResolved()}
              onPickSource={(target) => store.setTargets([target])}
              copy={copySource && { blocked: copySource.blocked, busy: copying || libraries.busy || !copySource.libraryId, copying, run: () => void editCopy() }}
            />
          </div>
        ),
      }}
      timeline={!!audio}
      dock={
        <>
          <NoAudioBar session={session} />
          {audio && (
            <Timeline
              audio={audio}
              lines={state.lines}
              selection={state.selection}
              frameRate={session.frameRate}
              marked={marked}
              onSelect={selectFromTimeline}
              onSelectLines={selectLines}
              tracks={tracks}
              onSelectBand={selectBand}
              leading={
                <Transport
                  audio={audio}
                  loopLine={loopLine}
                  onLoopLine={setLoopLine}
                  loopAvailable={!!selectedLine && isTimed(selectedLine)}
                  help="空格 播放／暂停 · ↑↓ 选行 · ←→ 选文本单元"
                />
              }
            />
          )}
        </>
      }
      status={<StatusBar state={state} />}
    >
      <TemplatePicker
        open={picking}
        initialChoice={pickInitial}
        targetKind={sampleKeyOf(view.targets[0], model)}
        countText={targetCountText(view.targets, model)}
        targetPath={describeTargets(view.targets, state.branches, state.linesByBranch, model)}
        branches={branches}
        library={library}
        thumbnails={props.thumbnails}
        busy={!!view.applying}
        previewSize={previewSize}
        canTrial={store.canTrial}
        trial={view.trial}
        onTrial={(libraryId, revision, template, branchInputs) => store.trial(libraryId, revision, template, previewTimeUs(audio), previewSize, branchInputs)}
        onEndTrial={() => store.endTrial()}
        onApply={async (libraryId, revision, template, branchInputs) => {
          const result = await store.apply(libraryId, revision, template, branchInputs);
          if (result === 'applied') toast(`已应用“${template.name}”到 ${targetCountText(view.targets, model)}`, { label: '撤销', run: () => void store.undoApply() });
          return result;
        }}
        onClose={() => {
          setPicking(false);
          setPickInitial(null);
        }}
      />
    </WorkLayout>
  );
}

/** 工程里的来源；后端没有开放来源列表时只有当前来源。 */
function useProjectBranches(state: EditorState): readonly LyricBranchSummary[] {
  return useMemo(
    () => state.branches ?? [{ id: state.branchId, name: '歌词', role: 'primary' as const, timingMode: 'independent' as const, lineCount: state.lines.length }],
    [state.branches, state.branchId, state.lines.length],
  );
}

// ── 目标浏览器 ──

const BRANCH_ICON: Record<LyricBranchSummary['role'], IconName> = { primary: 'lyrics', translation: 'translate', annotation: 'text_fields', custom: 'description' };

/**
 * 左栏（稿件 #appearance）：上面是工程默认、各来源和它们的行（展开的行下面是文本单元），可以搜索行；
 * 下面一块浅底是编排对象，在“结构”和“片段”之间切换。
 */
function TargetBrowser(props: {
  state: EditorState;
  view: AppearanceState;
  orchestration: OrchestrationState;
  onTargets: (targets: AppearanceTarget[], anchorKey?: string) => void;
}) {
  const { state, view } = props;
  const model = props.orchestration.model;
  const branches = useProjectBranches(state);
  const [query, setQuery] = useState('');
  const [openBranches, setOpenBranches] = useState<ReadonlySet<string>>(() => new Set([state.branchId]));
  const [openLines, setOpenLines] = useState<ReadonlySet<string>>(() => new Set());
  const [openObjects, setOpenObjects] = useState<ReadonlySet<string>>(() => new Set([state.branchId]));
  const [objectView, setObjectView] = useState<ObjectView>('structure');
  const selected = useMemo(() => new Set(view.targets.map(targetKey)), [view.targets]);
  const assigned = view.applications?.index;
  const root = useRef<HTMLDivElement>(null);

  // 选中的行、文本单元、片段或节点所在的来源和行自动展开（片段、节点切到对应的显示），并滚进视野。
  const only = view.targets.length === 1 ? view.targets[0] : undefined;
  useEffect(() => {
    if (!only || only.kind === 'project' || only.kind === 'branch') return;
    if (only.kind === 'line' || only.kind === 'token') {
      setOpenBranches((open) => (open.has(only.branchId) ? open : new Set([...open, only.branchId])));
      if (only.kind === 'token') setOpenLines((open) => (open.has(only.lineId) ? open : new Set([...open, only.lineId])));
    } else {
      setObjectView(only.kind === 'clip' ? 'clips' : 'structure');
      const branchId = only.kind === 'clip' ? only.branchId : model && programOfNode(model, only.nodeId)?.branchId;
      if (branchId) setOpenObjects((open) => (open.has(branchId) ? open : new Set([...open, branchId])));
    }
    requestAnimationFrame(() => root.current?.querySelector(`[data-target-key="${CSS.escape(targetKey(only))}"]`)?.scrollIntoView({ block: 'nearest' }));
  }, [only, model]);

  const pick = (event: MouseEvent, target: AppearanceTarget, siblings: () => AppearanceTarget[]) => {
    if (event.shiftKey && target.kind !== 'project' && target.kind !== 'node') {
      props.onTargets(rangeTargets(siblings(), view.anchorKey, target), view.anchorKey);
      return;
    }
    props.onTargets(pickTarget(view.targets, target, event.ctrlKey || event.metaKey));
  };

  const q = query.trim().toLowerCase();
  const branchTargets = () => branches.map((b): AppearanceTarget => ({ kind: 'branch', branchId: b.id }));
  const invalid = assigned?.invalid.length ?? 0;

  return (
    <div className="appearance-targets" ref={root}>
      <div className="pane-head">
        <h3>外观目标</h3>
        <span className="spacer" />
        <span className="faint">Ctrl 加选，Shift 连选</span>
      </div>
      <div className="tsearch">
        <TextField label="搜索行" labelPlacement="none" icon="search" placeholder="搜索行" size="sm" value={query} onChange={setQuery} onEscape={() => setQuery('')} />
      </div>
      <div className="atree" role="tree" aria-label="外观目标" aria-multiselectable>
        <TreeRow
          level={1}
          icon="home"
          label="工程默认"
          title="所有歌词的基础外观"
          sub={invalid > 0 ? `${invalid} 个失效分配` : undefined}
          warn={invalid > 0}
          assigned={assigned}
          target={PROJECT_TARGET}
          selected={selected.has('project')}
          onPick={(e) => pick(e, PROJECT_TARGET, () => [])}
        />
        {branches.map((branch) => {
          const lines = state.linesByBranch[branch.id] ?? [];
          const visible = q ? lines.filter((l) => l.text.toLowerCase().includes(q)) : lines;
          const open = q ? visible.length > 0 : openBranches.has(branch.id);
          const target: AppearanceTarget = { kind: 'branch', branchId: branch.id };
          const lineTargets = () => lines.map(lineTarget);
          return (
            <div key={branch.id} role="group">
              <TreeRow
                level={1}
                icon={BRANCH_ICON[branch.role]}
                label={branch.name || branch.id}
                title={BRANCH_ROLE_TEXT[branch.role]}
                sub={`${lines.length} 行`}
                assigned={assigned}
                target={target}
                selected={selected.has(targetKey(target))}
                expanded={open}
                onToggle={() => setOpenBranches((s) => toggled(s, branch.id))}
                onPick={(e) => pick(e, target, branchTargets)}
              />
              {open &&
                visible.map((line) => (
                  <LineRows
                    key={line.id}
                    line={line}
                    number={lines.indexOf(line) + 1}
                    selected={selected}
                    assigned={assigned}
                    open={openLines.has(line.id)}
                    onToggle={() => setOpenLines((s) => toggled(s, line.id))}
                    onPick={(e, t) => pick(e, t, t.kind === 'line' ? lineTargets : () => line.tokens.map((k) => tokenTarget(line, k.id)))}
                  />
                ))}
            </div>
          );
        })}
        {q && branches.every((b) => !(state.linesByBranch[b.id] ?? []).some((l) => l.text.toLowerCase().includes(q))) && (
          <Note className="target-empty">没有匹配的行。</Note>
        )}
      </div>
      {!q && (
        <ObjectSections
          orchestration={props.orchestration}
          branches={branches}
          linesByBranch={state.linesByBranch}
          selected={selected}
          assigned={assigned}
          open={openObjects}
          view={objectView}
          onView={setObjectView}
          onToggle={(branchId) => setOpenObjects((s) => toggled(s, branchId))}
          onPick={pick}
        />
      )}
    </div>
  );
}

function toggled(set: ReadonlySet<string>, id: string): ReadonlySet<string> {
  const next = new Set(set);
  if (next.has(id)) next.delete(id);
  else next.add(id);
  return next;
}

type ObjectView = 'structure' | 'clips';

/**
 * 编排对象（W7 设计 §2.7）：按来源分节（只有一个来源时不写来源行），“结构”是结构树形状的容器、组、槽位、前奏提示组和标记，
 * “片段”是片段的名称和句子范围。节点一次只选一个；片段可以在同一来源里加选、连选。
 */
function ObjectSections(props: {
  orchestration: OrchestrationState;
  branches: readonly LyricBranchSummary[];
  linesByBranch: Readonly<Record<string, readonly EditorLine[]>>;
  selected: ReadonlySet<string>;
  assigned: ApplicationIndex | undefined;
  open: ReadonlySet<string>;
  view: ObjectView;
  onView: (view: ObjectView) => void;
  onToggle: (branchId: string) => void;
  onPick: (event: MouseEvent, target: AppearanceTarget, siblings: () => AppearanceTarget[]) => void;
}) {
  const { orchestration, assigned } = props;
  const model = orchestration.model;
  const sections = useMemo(() => (model ? structureSections(model, props.branches).filter((s) => s.kind !== 'orphans' && s.branchId) : []), [model, props.branches]);
  if (orchestration.status === 'unsupported') return null;
  const clipsBy = new Map(sections.map((s) => [s.key, clipsOf(s.program, props.linesByBranch[s.branchId!] ?? [])]));
  const clipCount = [...clipsBy.values()].reduce((n, clips) => n + clips.length, 0);
  const several = sections.length > 1;
  return (
    <section className="sect target-objects" aria-label="编排对象">
      <header>
        <h4>编排对象</h4>
        <span className="spacer" />
        {model && (
          <Segmented
            label="编排对象的显示"
            size="sm"
            value={props.view}
            options={[
              { value: 'structure', label: '结构' },
              { value: 'clips', label: `片段 ${clipCount}` },
            ]}
            onChange={props.onView}
          />
        )}
      </header>
      {!model ? (
        <Note error={orchestration.status === 'error'} className="target-empty">
          {orchestration.status === 'error' ? orchestration.error : '正在读取编排结构…'}
        </Note>
      ) : (
        <div className="atree" role="tree" aria-label="编排对象">
          {sections.map((section) => {
            const branchId = section.branchId!;
            const lines = props.linesByBranch[branchId] ?? [];
            const clips = clipsBy.get(section.key) ?? [];
            const open = !several || props.open.has(branchId);
            const clipTargets = () => clips.map((c) => clipTarget(branchId, c.id));
            const empty = props.view === 'structure' ? (section.rows.length === 0 ? '没有编排结构' : undefined) : clips.length === 0 ? '没有片段' : undefined;
            const indent = several ? 1 : 0;
            return (
              <div key={section.key} role="group" data-object-branch={branchId}>
                {several && (
                  <div role="treeitem" aria-level={1} aria-expanded={open} aria-selected={false} className="at object-section" onClick={() => props.onToggle(branchId)}>
                    <Icon name={open ? 'keyboard_arrow_down' : 'chevron_right'} className="car" />
                    <span className="tx">{section.title}</span>
                    <span className="sub">{props.view === 'structure' ? `${section.rows.length} 个对象` : `${clips.length} 个片段`}</span>
                  </div>
                )}
                {open && empty && <Note className="target-empty">{empty}</Note>}
                {open &&
                  props.view === 'structure' &&
                  section.rows.map(({ node, depth }) => {
                    const target: NodeTarget = { kind: 'node', nodeId: node.id, includeDescendants: nodeScopeOf(assigned, node.id) ?? true };
                    return (
                      <TreeRow
                        key={node.id}
                        level={1 + indent + depth}
                        depth={indent + depth}
                        icon={nodeIcon(model, node)}
                        label={nodeName(model, node)}
                        title={roleText(model, node)}
                        sub={node.contentKind === 'textMark' ? node.content?.text || undefined : undefined}
                        rowClass="object-row"
                        assigned={assigned}
                        target={target}
                        selected={props.selected.has(targetKey(target))}
                        onPick={(e) => props.onPick(e, target, () => [])}
                      />
                    );
                  })}
                {open &&
                  props.view === 'clips' &&
                  clips.map((clip, i) => {
                    const target = clipTarget(branchId, clip.id);
                    const range = lineRange(lines, clip.lineIds);
                    return (
                      <TreeRow
                        key={clip.id}
                        level={1 + indent}
                        depth={indent}
                        icon="view_quilt"
                        label={clipName(clip, i + 1)}
                        sub={range ? lineRangeText(range) : '没有句子'}
                        rowClass="object-row clip-row"
                        assigned={assigned}
                        target={target}
                        selected={props.selected.has(targetKey(target))}
                        onPick={(e) => props.onPick(e, target, clipTargets)}
                      />
                    );
                  })}
              </div>
            );
          })}
        </div>
      )}
    </section>
  );
}

const LineRows = memo(function LineRows(props: {
  line: EditorLine;
  number: number;
  selected: ReadonlySet<string>;
  assigned: ApplicationIndex | undefined;
  open: boolean;
  onToggle: () => void;
  onPick: (event: MouseEvent, target: AppearanceTarget) => void;
}) {
  const { line, assigned } = props;
  const target = lineTarget(line);
  return (
    <>
      <TreeRow
        level={2}
        depth={1}
        rowClass="line-row"
        label={line.text || '（空行）'}
        index={String(props.number)}
        assigned={assigned}
        target={target}
        selected={props.selected.has(targetKey(target))}
        expanded={line.tokens.length > 0 ? props.open : undefined}
        onToggle={props.onToggle}
        onPick={(e) => props.onPick(e, target)}
      />
      {props.open && line.tokens.length > 0 && (
        <div className="tokchips" role="group">
          {line.tokens.map((token) => {
            const t = tokenTarget(line, token.id);
            const key = targetKey(t);
            const template = templateText(assigned, t);
            return (
              <button
                key={token.id}
                type="button"
                role="treeitem"
                aria-level={3}
                aria-selected={props.selected.has(key)}
                data-target-key={key}
                className={cls('tok', props.selected.has(key) && 'sel', !!template && 'has-template')}
                title={template ? `模板：${template}` : undefined}
                onMouseDown={(e) => e.shiftKey && e.preventDefault()}
                onClick={(e) => props.onPick(e, t)}
              >
                {token.text || '␣'}
              </button>
            );
          })}
        </div>
      )}
    </>
  );
});

/** 目标的一行：展开钮、图标或行号、名称、说明，右边是直接分配的模板名。 */
function TreeRow(props: {
  level: number;
  /** 缩进的层数，缺省按 level - 1。 */
  depth?: number;
  rowClass?: string;
  icon?: IconName;
  index?: string;
  label: string;
  /** 悬停时的说明（例如来源的角色、节点的种类）。 */
  title?: string;
  /** 名称后面的浅色说明。 */
  sub?: string | undefined;
  warn?: boolean;
  assigned: ApplicationIndex | undefined;
  target: AppearanceTarget;
  selected: boolean;
  /** undefined 表示不能展开。 */
  expanded?: boolean | undefined;
  onToggle?: () => void;
  onPick: (event: MouseEvent) => void;
}) {
  const key = targetKey(props.target);
  const apps = props.assigned?.byTarget.get(key);
  const template = applicationNames(apps);
  const off = !!apps && apps.length > 0 && apps.every((a) => !a.enabled);
  return (
    <div
      role="treeitem"
      aria-level={props.level}
      aria-selected={props.selected}
      aria-expanded={props.expanded}
      data-target-key={key}
      className={cls('at', props.rowClass, props.selected && 'sel')}
      style={{ '--d': props.depth ?? props.level - 1 } as CSSProperties}
      title={props.title}
      onMouseDown={(e) => e.shiftKey && e.preventDefault()}
      onClick={props.onPick}
    >
      {props.expanded === undefined ? (
        <span className="car none" />
      ) : (
        <button
          type="button"
          className="car"
          aria-label={props.expanded ? '收起' : '展开'}
          onClick={(e) => {
            e.stopPropagation();
            props.onToggle?.();
          }}
        >
          <Icon name={props.expanded ? 'keyboard_arrow_down' : 'chevron_right'} />
        </button>
      )}
      {props.icon && <Icon name={props.icon} className="ti" />}
      {props.index && <span className="n">{props.index}</span>}
      <span className="tx">{props.label}</span>
      {props.sub && <span className={cls('sub', props.warn && 'warn')}>{props.sub}</span>}
      {template && (
        <span className={cls('asg', off && 'off')} title={`模板：${template}`}>
          <i />
          <span>{template}</span>
        </span>
      )}
    </div>
  );
}

function templateText(index: ApplicationIndex | undefined, target: AppearanceTarget): string | undefined {
  return index ? applicationNames(index.byTarget.get(targetKey(target))) : undefined;
}

// ── 检查器 ──

/** 检查器的标题：单选一行时名称是这行的文字，其余按目标路径拆成位置和名称。 */
function inspectorHead(targets: readonly AppearanceTarget[], state: EditorState, model: OrchestrationModel | undefined): { crumbs: string[]; title: string; kind?: string } {
  const first = targets[0];
  if (!first || first.kind === 'project') return { crumbs: [], title: '工程默认' };
  const path = describeTargets(targets, state.branches, state.linesByBranch, model);
  const node = first.kind === 'node' && targets.length === 1 ? model?.byId.get(first.nodeId) : undefined;
  const kindText = node && model ? nodeKindText(model, node) : TARGET_KIND_TEXT[first.kind];
  const kind = targets.length > 1 ? `${kindText} · ${targets.length} 个` : kindText;
  if (targets.length === 1 && first.kind === 'line') {
    const line = state.linesByBranch[first.branchId]?.find((l) => l.id === first.lineId);
    if (line) return { crumbs: path.split(' / '), title: line.text || '（空行）', kind };
  }
  if (path.includes('；')) return { crumbs: [], title: path, kind };
  const parts = path.split(' / ');
  return { crumbs: parts.slice(0, -1), title: parts.at(-1) ?? path, kind };
}

/** “本行直接分配”：单选并且有直接分配时写在模板组标题后面。 */
const DIRECT_UNIT: Record<AppearanceTarget['kind'], string> = { project: '工程', branch: '来源', line: '行', token: '文本单元', clip: '片段', node: '对象' };

function Inspector(props: {
  state: EditorState;
  branches: readonly LyricBranchSummary[];
  view: AppearanceState;
  model: OrchestrationModel | undefined;
  thumbnails: TemplateThumbnails;
  canApply: boolean;
  canUndoApply: boolean;
  actions: ApplicationActions;
  onPick: () => void;
  onUndoApply: () => void;
  onRetry: () => void;
  onRetryApplications: () => void;
  onIncludeDescendants: (value: boolean) => void;
  canReadSources: boolean;
  onSourcesOpen: (open: boolean) => void;
  onRetrySources: () => void;
  onPickSource: (target: AppearanceTarget) => void;
  /** “在样式库中编辑副本”；目标上没有直接分配时没有。 */
  copy: { blocked: string | undefined; busy: boolean; copying: boolean; run: () => void } | undefined;
}) {
  const { state, view, model } = props;
  const targets = view.targets;
  const first = targets[0];
  const kind = first?.kind ?? 'project';
  const index = view.applications?.index;
  const direct = index ? applicationsOn(index, targets) : [];
  const node = first?.kind === 'node' && targets.length === 1 ? model?.byId.get(first.nodeId) : undefined;
  const head = inspectorHead(targets, state, model);
  const busy = !props.canApply || !!view.applying;

  return (
    <div className="insp appearance-insp">
      <InspectorHead crumbs={head.crumbs} title={head.title} kind={head.kind} />
      <Group title="模板" hint={targets.length === 1 && direct.length > 0 ? `本${DIRECT_UNIT[kind]}直接分配` : undefined} className="template-grp">
        <ApplicationsPanel
          view={view}
          targets={targets}
          branches={props.branches}
          thumbnails={props.thumbnails}
          busy={busy}
          actions={props.actions}
          onRetry={props.onRetryApplications}
        />
        {first?.kind === 'node' && node && node.contentKind !== 'textMark' && (
          <ScopeSwitch node={node} includeDescendants={first.includeDescendants} disabled={busy} onChange={props.onIncludeDescendants} />
        )}
        <div className="btnrow">
          <Button variant="tonal" size="sm" icon={direct.length > 0 ? 'swap_horiz' : 'style'} isDisabled={busy} onPress={props.onPick}>
            {direct.length > 0 ? '换模板…' : '选择模板…'}
          </Button>
          {props.copy && (
            <Button variant="ghost" size="sm" icon="edit" isDisabled={!!props.copy.blocked || props.copy.busy} isPending={props.copy.copying} onPress={props.copy.run}>
              在样式库中编辑副本
            </Button>
          )}
        </div>
        {props.copy?.blocked && <Note>不能编辑副本：{props.copy.blocked}。</Note>}
        {view.lastApplied && props.canUndoApply && (
          <div className="applied-banner" role="status">
            <Icon name="check_circle" />
            <span>
              已应用“{view.lastApplied.templateName}”到 {view.lastApplied.targetText}
            </span>
            <Button variant="text" size="sm" onPress={props.onUndoApply}>
              撤销
            </Button>
          </div>
        )}
        {direct.some((a) => !a.source.comparable) && <Note>“更新”取库里这个模板的当前版本；后端还没记录分配来自哪个版本，暂时不能提示“库里有新版本”。</Note>}
      </Group>

      {kind === 'clip' || kind === 'node' ? (
        <ObjectAppearance node={node} kind={kind} multiple={targets.length > 1} />
      ) : (
        <DirectOverrides kind={kind} multiple={targets.length > 1} inspect={view.inspect} onRetry={props.onRetry} />
      )}

      <StyleSources
        view={view}
        context={{ branches: props.branches, linesByBranch: state.linesByBranch, applications: index?.byId, model }}
        canRead={props.canReadSources}
        onOpen={props.onSourcesOpen}
        onRetry={props.onRetrySources}
        onPick={props.onPickSource}
      />
    </div>
  );
}

/**
 * 外观来源（W7 设计 §2.7）：单选行或文本单元时，按属性分组列出解析后的取值和最后来自哪一层。
 * 点来源选中那个目标；只解析静态样式，不含动画和效果的像素。
 */
function StyleSources(props: {
  view: AppearanceState;
  context: SourceContext;
  canRead: boolean;
  onOpen: (open: boolean) => void;
  onRetry: () => void;
  onPick: (target: AppearanceTarget) => void;
}) {
  const { view, context } = props;
  const open = !!view.sourcesOpen;
  const only = view.targets.length === 1 ? view.targets[0] : undefined;
  const resolved = view.resolved;
  const groups = useMemo(() => (resolved?.response ? sourceGroups(resolved.response) : []), [resolved?.response]);
  return (
    <Group
      title="外观来源"
      className="style-sources"
      extra={
        <IconButton
          icon={open ? 'keyboard_arrow_up' : 'keyboard_arrow_down'}
          size="sm"
          label={open ? '收起外观来源' : '展开外观来源'}
          aria-expanded={open}
          className="section-toggle"
          onPress={() => props.onOpen(!open)}
        />
      }
    >
      {open &&
        (!only || (only.kind !== 'line' && only.kind !== 'token') ? (
          <Note>选中一行或一个文本单元后显示。</Note>
        ) : !props.canRead ? (
          <Note>后端没有开放外观来源读取。</Note>
        ) : resolved?.status === 'error' ? (
          <ReadError error={resolved.error} onRetry={props.onRetry} />
        ) : !resolved?.response ? (
          <ProgressBar label="正在读取外观来源" />
        ) : (
          <>
            <dl className="srcs">
              {groups.map((group) => (
                <div key={group.id} className="source-group">
                  <dt>{group.label}</dt>
                  <dd className="v">
                    {group.value.swatch && <i className="swatch" style={{ background: group.value.swatch }} />}
                    <span>{group.value.text}</span>
                  </dd>
                  <dd className="from">
                    {group.rows.map((row) => {
                      const text = `${row.property && group.rows.length > 1 ? `${STYLE_PROPERTY_TEXT[row.property]}：` : ''}${sourceText(row.source, context)}`;
                      const title = `${row.property ? `${STYLE_PROPERTY_TEXT[row.property]} · ` : ''}来自 ${sourceText(row.source, context)}（${row.path}）`;
                      const target = sourceTarget(row.source, context);
                      return target ? (
                        <button key={row.property ?? 'all'} type="button" className="source-row" title={title} onClick={() => props.onPick(target)}>
                          {text}
                        </button>
                      ) : (
                        <span key={row.property ?? 'all'} className="source-row" title={title}>
                          {text}
                        </span>
                      );
                    })}
                  </dd>
                </div>
              ))}
            </dl>
            <Note>只解析静态样式，不含动画和效果的像素。点来源选中那个目标。</Note>
          </>
        ))}
    </Group>
  );
}

/**
 * 节点的应用范围（W7 设计 §2.7）：默认包括里面的句子（提示组是里面的标记）；关掉时分配只作用于节点本身。
 * 只影响下一次试看和应用，已有的分配在上面写明它的范围。
 */
function ScopeSwitch(props: { node: OrchestrationNode; includeDescendants: boolean; disabled: boolean; onChange: (value: boolean) => void }) {
  const countdown = props.node.contentKind === 'countdownSequence';
  return (
    <div className="scope-switch">
      <Switch label={countdown ? '包括里面的标记' : '包括里面的句子'} checked={props.includeDescendants} disabled={props.disabled} onChange={props.onChange} />
      {!props.includeDescendants && <Note>{countdown ? '只作用于提示组本身，不影响里面的标记。' : '只作用于容器本身，不影响里面的歌词文字。'}</Note>}
    </div>
  );
}

/** 行、文本单元、来源的本层直接覆盖（GET .../style 的 local，只读）。 */
function DirectOverrides(props: { kind: AppearanceTarget['kind']; multiple: boolean; inspect: AppearanceState['inspect']; onRetry: () => void }) {
  const { kind, inspect } = props;
  const rows = inspect?.response ? overrideRows(inspect.response.local) : [];
  return (
    <Group title="本层直接覆盖 · 只读" className="direct-overrides">
      {kind === 'project' ? (
        <Note>工程默认是最底层，没有上一层可以覆盖。</Note>
      ) : props.multiple ? (
        <Note>选中一个目标时显示。</Note>
      ) : !inspect ? (
        <Note>后端没有开放样式读取。</Note>
      ) : inspect.status === 'error' ? (
        <ReadError error={inspect.error} onRetry={props.onRetry} />
      ) : !inspect.response ? (
        <ProgressBar label="正在读取直接覆盖" />
      ) : rows.length === 0 ? (
        <Note>没有直接覆盖：外观全部来自上一层和模板分配。</Note>
      ) : (
        <OverrideList rows={rows} />
      )}
      {kind !== 'project' && rows.length > 0 && <Note>直接覆盖写在歌词上（例如导入时带来的样式），外观页只显示、不编辑；它比模板分配更靠近目标。</Note>}
    </Group>
  );
}

/**
 * 编排对象自己的外观（W7 设计 §2.8）：前奏提示组的完整外观和标记的覆盖只读显示；要改它们，把模板分配到提示组或标记上。
 * 片段、容器和组没有自己的外观。
 */
function ObjectAppearance(props: { node: OrchestrationNode | undefined; kind: 'clip' | 'node'; multiple: boolean }) {
  const { node } = props;
  if (props.kind === 'clip' || !node || (node.contentKind !== 'countdownSequence' && node.contentKind !== 'textMark')) {
    return (
      <Group title="自己的外观" className="direct-overrides">
        <Note>{props.multiple ? '选中一个目标时显示。' : props.kind === 'clip' ? '片段没有自己的外观，里面的句子按分配和上一层显示。' : '容器和组没有自己的外观，外观全部来自模板分配。'}</Note>
      </Group>
    );
  }
  const countdown = node.contentKind === 'countdownSequence';
  const rows = countdown ? (node.content?.appearance ? sceneAppearanceRows(node.content.appearance) : []) : overrideRows(node.content?.appearanceOverride ?? []);
  return (
    <Group title={countdown ? '提示组外观 · 只读' : '标记的覆盖 · 只读'} className="direct-overrides">
      {rows.length > 0 ? <OverrideList rows={rows} /> : <Note>{countdown ? '读模型里没有提示组外观。' : '没有覆盖：外观来自提示组和模板分配。'}</Note>}
      <Note>提示组和标记自己的外观只显示、不编辑。要改颜色和字体，把模板分配到提示组（包括里面的标记）或单个标记上。</Note>
    </Group>
  );
}

function OverrideList(props: { rows: readonly OverrideRow[] }) {
  return (
    <dl className="dl override-list">
      {props.rows.map((row) => (
        <div key={row.property} className="override-row">
          <dt>{row.label}</dt>
          <dd>
            {row.swatch && <i className="swatch" style={{ background: row.swatch }} />}
            {row.text}
          </dd>
        </div>
      ))}
    </dl>
  );
}

// ── 快捷键 ──

/** 外观页快捷键（整体设计 §4）：空格播放，↑↓ 选行，←→ 选文本单元，Ctrl／⌘+Z、Shift+Z、Y、S。焦点在输入框里时让给输入框。 */
function useAppearanceKeys(options: {
  session: EditorSession;
  store: AppearanceStore;
  audio: AudioController | null;
  onUndo: () => void;
  onRedo: () => void;
  onSave: () => void;
}) {
  const latest = useRef(options);
  latest.current = options;

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (isEditing(event)) return;
      const { session, store, audio } = latest.current;
      const mod = event.ctrlKey || event.metaKey;
      const key = event.key.toLowerCase();
      if (mod && key === 'z') {
        event.preventDefault();
        if (event.shiftKey) latest.current.onRedo();
        else latest.current.onUndo();
        return;
      }
      if (mod && key === 'y') {
        event.preventDefault();
        latest.current.onRedo();
        return;
      }
      if (mod && key === 's') {
        event.preventDefault();
        latest.current.onSave();
        return;
      }
      if (mod) return;
      if (event.key === ' ') {
        event.preventDefault();
        if (document.activeElement instanceof HTMLElement && document.activeElement !== document.body) document.activeElement.blur();
        audio?.toggle();
        return;
      }

      const { lines } = session.state;
      const only = store.state.targets.length === 1 ? store.state.targets[0]! : undefined;
      const current = only?.kind === 'line' || only?.kind === 'token' ? lines.find((l) => l.id === only.lineId) : undefined;
      if (event.key === 'ArrowUp' || event.key === 'ArrowDown') {
        event.preventDefault();
        const index = current ? lines.indexOf(current) + (event.key === 'ArrowDown' ? 1 : -1) : 0;
        const line = lines[index];
        if (line) store.setTargets([lineTarget(line)]);
      } else if ((event.key === 'ArrowLeft' || event.key === 'ArrowRight') && current) {
        event.preventDefault();
        const delta = event.key === 'ArrowRight' ? 1 : -1;
        const next = only?.kind === 'token' ? stepToken(lines, only.tokenId, delta) : current.tokens[0];
        const line = next && lines.find((l) => l.id === next.lineId);
        if (next && line) store.setTargets([tokenTarget(line, next.id)]);
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, []);
}
