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
import { PreviewPanel, previewTimeUs } from '../preview/PreviewPanel';
import { fitPreviewSize } from '../preview/preview-controller';
import { errorText, type EditorSession, type EditorState } from '../session/editor-session';
import { Timeline, type TimelineTrack } from '../timeline/Timeline';
import { toast } from '../ui/legacy/feedback';
import { Switch, TextField } from '../ui/legacy/fields';
import { useObservable } from '../ui/hooks';
import { StatusBar } from '../workspace/StatusBar';
import { WorkLayout } from '../workspace/WorkLayout';
import { isEditing } from '../workspace/Workspace';
import { ApplicationsPanel, type ApplicationActions } from './ApplicationsPanel';
import type { AppearanceState, AppearanceStore } from './appearance-store';
import { TemplatePicker, type TemplateChoice } from './TemplatePicker';

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
        content: (
          <aside className="pane appearance-targets">
            <TargetBrowser state={state} view={view} orchestration={orchestration} onTargets={(targets, anchorKey) => store.setTargets(targets, anchorKey)} />
          </aside>
        ),
      }}
      center={
        <section className="pane appearance-preview">
          <PreviewPanel session={session} audio={audio} warning={view.presetsPreview === false ? PRESETS_WARNING : undefined} />
        </section>
      }
      right={{
        label: '模板与覆盖',
        content: (
          <aside className="pane appearance-inspector">
            <Inspector
              state={state}
              branches={branches}
              view={view}
              model={model}
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
          </aside>
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
  const selected = useMemo(() => new Set(view.targets.map(targetKey)), [view.targets]);
  const assigned = view.applications?.index;
  const tree = useRef<HTMLDivElement>(null);

  // 选中的行、文本单元、片段或节点所在的来源和行自动展开，并滚进视野。
  const only = view.targets.length === 1 ? view.targets[0] : undefined;
  useEffect(() => {
    if (!only || only.kind === 'project' || only.kind === 'branch') return;
    if (only.kind === 'line' || only.kind === 'token') {
      setOpenBranches((open) => (open.has(only.branchId) ? open : new Set([...open, only.branchId])));
      if (only.kind === 'token') setOpenLines((open) => (open.has(only.lineId) ? open : new Set([...open, only.lineId])));
    } else {
      const branchId = only.kind === 'clip' ? only.branchId : model && programOfNode(model, only.nodeId)?.branchId;
      if (branchId) setOpenObjects((open) => (open.has(branchId) ? open : new Set([...open, branchId])));
    }
    requestAnimationFrame(() => tree.current?.querySelector(`[data-target-key="${CSS.escape(targetKey(only))}"]`)?.scrollIntoView({ block: 'nearest' }));
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

  return (
    <>
      <div className="pane-header">
        <span className="pane-title-text">目标</span>
        <span className="muted">Ctrl 加选 · Shift 连选</span>
      </div>
      <TextField label="搜索行" value={query} onChange={setQuery} className="target-search" />
      <div className="target-tree" role="tree" aria-label="外观目标" aria-multiselectable ref={tree}>
        <TreeRow
          level={1}
          label="工程默认"
          meta={assigned?.invalid.length ? `所有歌词的基础外观 · ${assigned.invalid.length} 个失效分配` : '所有歌词的基础外观'}
          template={templateText(assigned, PROJECT_TARGET)}
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
                label={branch.name || branch.id}
                meta={`${BRANCH_ROLE_TEXT[branch.role]} · ${lines.length} 行`}
                template={templateText(assigned, target)}
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
        {q && branches.every((b) => !(state.linesByBranch[b.id] ?? []).some((l) => l.text.toLowerCase().includes(q))) && <p className="muted target-empty">没有匹配的行。</p>}
        {!q && (
          <ObjectSections
            orchestration={props.orchestration}
            branches={branches}
            linesByBranch={state.linesByBranch}
            selected={selected}
            assigned={assigned}
            open={openObjects}
            onToggle={(branchId) => setOpenObjects((s) => toggled(s, branchId))}
            onPick={pick}
          />
        )}
      </div>
    </>
  );
}

function toggled(set: ReadonlySet<string>, id: string): ReadonlySet<string> {
  const next = new Set(set);
  if (next.has(id)) next.delete(id);
  else next.add(id);
  return next;
}

/**
 * 编排对象（W7 设计 §2.7）：按来源分节，先是结构树形状的容器、组、槽位、前奏提示组和标记，再是片段（名称和句子范围）。
 * 节点一次只选一个；片段可以在同一来源里加选、连选。
 */
function ObjectSections(props: {
  orchestration: OrchestrationState;
  branches: readonly LyricBranchSummary[];
  linesByBranch: Readonly<Record<string, readonly EditorLine[]>>;
  selected: ReadonlySet<string>;
  assigned: ApplicationIndex | undefined;
  open: ReadonlySet<string>;
  onToggle: (branchId: string) => void;
  onPick: (event: MouseEvent, target: AppearanceTarget, siblings: () => AppearanceTarget[]) => void;
}) {
  const { orchestration, assigned } = props;
  const model = orchestration.model;
  const sections = useMemo(() => (model ? structureSections(model, props.branches).filter((s) => s.kind !== 'orphans' && s.branchId) : []), [model, props.branches]);
  if (orchestration.status === 'unsupported') return null;
  return (
    <div role="group" aria-label="编排对象" className="target-objects">
      <div className="target-heading">编排对象</div>
      {!model ? (
        orchestration.status === 'error' ? <p className="notice-error target-empty">{orchestration.error}</p> : <p className="muted target-empty">正在读取编排结构…</p>
      ) : (
        sections.map((section) => {
          const branchId = section.branchId!;
          const lines = props.linesByBranch[branchId] ?? [];
          const clips = clipsOf(section.program, lines);
          const open = props.open.has(branchId);
          const clipTargets = () => clips.map((c) => clipTarget(branchId, c.id));
          return (
            <div key={section.key} role="group" data-object-branch={branchId}>
              <div
                role="treeitem"
                aria-level={1}
                aria-expanded={open}
                className="target-row object-section"
                onClick={() => props.onToggle(branchId)}
              >
                <span className="target-toggle">{open ? <mdui-icon-expand-more /> : <mdui-icon-chevron-right />}</span>
                <span className="target-label">{section.title}</span>
                <span className="target-meta">{section.rows.length === 0 ? '没有编排结构' : `${clips.length} 个片段`}</span>
              </div>
              {open &&
                section.rows.map(({ node, depth }) => {
                  const target: NodeTarget = { kind: 'node', nodeId: node.id, includeDescendants: nodeScopeOf(assigned, node.id) ?? true };
                  return (
                    <TreeRow
                      key={node.id}
                      level={2 + depth}
                      rowClass="object-row"
                      indent={depth}
                      label={nodeName(model, node)}
                      meta={nodeMeta(model, node)}
                      template={templateText(assigned, target)}
                      target={target}
                      selected={props.selected.has(targetKey(target))}
                      onPick={(e) => props.onPick(e, target, () => [])}
                    />
                  );
                })}
              {open && clips.length > 0 && <div className="target-subheading">片段</div>}
              {open &&
                clips.map((clip, i) => {
                  const target = clipTarget(branchId, clip.id);
                  const range = lineRange(lines, clip.lineIds);
                  return (
                    <TreeRow
                      key={clip.id}
                      level={2}
                      rowClass="object-row clip-row"
                      indent={0}
                      label={clipName(clip, i + 1)}
                      meta={range ? lineRangeText(range) : '没有句子'}
                      template={templateText(assigned, target)}
                      target={target}
                      selected={props.selected.has(targetKey(target))}
                      onPick={(e) => props.onPick(e, target, clipTargets)}
                    />
                  );
                })}
            </div>
          );
        })
      )}
    </div>
  );
}

/** 节点行的说明：角色徽标；标记再加上它的文字。 */
function nodeMeta(model: OrchestrationModel, node: OrchestrationNode): string {
  const role = roleText(model, node);
  return node.contentKind === 'textMark' && node.content?.text ? `${role} · ${node.content.text}` : role;
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
        label={line.text || '（空行）'}
        index={String(props.number).padStart(2, '0')}
        template={templateText(assigned, target)}
        target={target}
        selected={props.selected.has(targetKey(target))}
        expanded={line.tokens.length > 0 ? props.open : undefined}
        onToggle={props.onToggle}
        onPick={(e) => props.onPick(e, target)}
      />
      {props.open && line.tokens.length > 0 && (
        <div className="target-tokens" role="group">
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
                className={`target-token${props.selected.has(key) ? ' selected' : ''}${template ? ' has-template' : ''}`}
                title={template ? `模板：${template}` : undefined}
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

function TreeRow(props: {
  level: number;
  /** 代替 level-N 的样式类（编排对象的行）。 */
  rowClass?: string;
  /** 编排对象在结构树里的深度：按它缩进。 */
  indent?: number;
  label: string;
  index?: string;
  meta?: string;
  /** 直接分配的模板名。 */
  template?: string | undefined;
  target: AppearanceTarget;
  selected: boolean;
  /** undefined 表示不能展开。 */
  expanded?: boolean | undefined;
  onToggle?: () => void;
  onPick: (event: MouseEvent) => void;
}) {
  const key = targetKey(props.target);
  return (
    <div
      role="treeitem"
      aria-level={props.level}
      aria-selected={props.selected}
      aria-expanded={props.expanded}
      data-target-key={key}
      className={`target-row ${props.rowClass ?? `level-${props.level}`}${props.selected ? ' selected' : ''}`}
      style={props.indent ? ({ '--indent': props.indent } as CSSProperties) : undefined}
      onMouseDown={(e) => e.shiftKey && e.preventDefault()}
      onClick={props.onPick}
    >
      {props.expanded === undefined ? (
        <span className="target-toggle" />
      ) : (
        <button
          type="button"
          className="target-toggle"
          aria-label={props.expanded ? '收起' : '展开'}
          onClick={(e) => {
            e.stopPropagation();
            props.onToggle?.();
          }}
        >
          {props.expanded ? <mdui-icon-expand-more /> : <mdui-icon-chevron-right />}
        </button>
      )}
      {props.index && <span className="line-index">{props.index}</span>}
      <span className="target-label">{props.label}</span>
      {props.template && (
        <span className="target-template" title={`模板：${props.template}`}>
          {props.template}
        </span>
      )}
      {props.meta && <span className="target-meta">{props.meta}</span>}
    </div>
  );
}

function templateText(index: ApplicationIndex | undefined, target: AppearanceTarget): string | undefined {
  return index ? applicationNames(index.byTarget.get(targetKey(target))) : undefined;
}

// ── 检查器 ──

function Inspector(props: {
  state: EditorState;
  branches: readonly LyricBranchSummary[];
  view: AppearanceState;
  model: OrchestrationModel | undefined;
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
  const path = describeTargets(targets, state.branches, state.linesByBranch, model);
  const index = view.applications?.index;
  const direct = index ? applicationsOn(index, targets) : [];
  const node = first?.kind === 'node' && targets.length === 1 ? model?.byId.get(first.nodeId) : undefined;
  const kindText = node && model ? nodeKindText(model, node) : TARGET_KIND_TEXT[kind];

  return (
    <div className="inspector">
      <div className="inspector-path">
        <span className="pane-title-text">{path}</span>
        {kind !== 'project' && (
          <span className="muted">
            {kindText}
            {targets.length > 1 ? ` · ${targets.length} 个` : ''}
          </span>
        )}
      </div>

      <h3 className="section-title">模板</h3>
      <ApplicationsPanel view={view} targets={targets} branches={props.branches} busy={!props.canApply || !!view.applying} actions={props.actions} onRetry={props.onRetryApplications} />
      {first?.kind === 'node' && node && node.contentKind !== 'textMark' && (
        <ScopeSwitch node={node} includeDescendants={first.includeDescendants} disabled={!props.canApply || !!view.applying} onChange={props.onIncludeDescendants} />
      )}
      <div className="inspector-actions">
        <mdui-button variant="tonal" disabled={!props.canApply || !!view.applying} onClick={props.onPick}>
          <mdui-icon-style slot="icon" />
          {direct.length > 0 ? '换模板…' : '选择模板…'}
        </mdui-button>
        {props.copy && (
          <mdui-button variant="text" disabled={!!props.copy.blocked || props.copy.busy} loading={props.copy.copying} onClick={props.copy.run}>
            <mdui-icon-edit slot="icon" />
            在样式库中编辑副本
          </mdui-button>
        )}
      </div>
      {props.copy?.blocked && <p className="hint">不能编辑副本：{props.copy.blocked}。</p>}
      {view.lastApplied && props.canUndoApply && (
        <div className="applied-banner" role="status">
          <span>
            已应用“{view.lastApplied.templateName}”到 {view.lastApplied.targetText}
          </span>
          <mdui-button variant="text" onClick={props.onUndoApply}>
            撤销
          </mdui-button>
        </div>
      )}
      {direct.some((a) => !a.source.comparable) && (
        <p className="hint">“更新”取库里这个模板的当前版本；后端还没记录分配来自哪个版本，暂时不能提示“库里有新版本”。</p>
      )}

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
    <section className="style-sources">
      <button type="button" className="section-title section-toggle" aria-expanded={open} onClick={() => props.onOpen(!open)}>
        外观来源
        {open ? <mdui-icon-expand-more /> : <mdui-icon-chevron-right />}
      </button>
      {open &&
        (!only || (only.kind !== 'line' && only.kind !== 'token') ? (
          <p className="hint">选中一行或一个文本单元后显示。</p>
        ) : !props.canRead ? (
          <p className="hint">后端没有开放外观来源读取。</p>
        ) : resolved?.status === 'error' ? (
          <p className="notice-error">
            {resolved.error}{' '}
            <mdui-button variant="text" onClick={props.onRetry}>
              重试
            </mdui-button>
          </p>
        ) : !resolved?.response ? (
          <mdui-linear-progress />
        ) : (
          <>
            <dl className="source-list">
              {groups.map((group) => (
                <div key={group.id} className="source-group">
                  <dt>{group.label}</dt>
                  <dd>
                    <span className="source-value">
                      {group.value.swatch && <span className="swatch" style={{ background: group.value.swatch }} />}
                      {group.value.text}
                    </span>
                    {group.rows.map((row) => {
                      const text = `${row.property ? `${STYLE_PROPERTY_TEXT[row.property]} · ` : ''}来自 ${sourceText(row.source, context)}`;
                      const target = sourceTarget(row.source, context);
                      return target ? (
                        <button key={row.property ?? 'all'} type="button" className="source-row" title={row.path} onClick={() => props.onPick(target)}>
                          {text}
                        </button>
                      ) : (
                        <span key={row.property ?? 'all'} className="source-row" title={row.path}>
                          {text}
                        </span>
                      );
                    })}
                  </dd>
                </div>
              ))}
            </dl>
            <p className="hint">只解析静态样式，不含动画和效果的像素。点来源选中那个目标。</p>
          </>
        ))}
    </section>
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
      {!props.includeDescendants && (
        <p className="hint">{countdown ? '只作用于提示组本身，不影响里面的标记。' : '只作用于容器本身，不影响里面的歌词文字。'}</p>
      )}
    </div>
  );
}

/** 行、文本单元、来源的本层直接覆盖（GET .../style 的 local，只读）。 */
function DirectOverrides(props: { kind: AppearanceTarget['kind']; multiple: boolean; inspect: AppearanceState['inspect']; onRetry: () => void }) {
  const { kind, inspect } = props;
  const rows = inspect?.response ? overrideRows(inspect.response.local) : [];
  return (
    <>
      <h3 className="section-title">本层直接覆盖 · 只读</h3>
      {kind === 'project' ? (
        <p className="hint">工程默认是最底层，没有上一层可以覆盖。</p>
      ) : props.multiple ? (
        <p className="hint">选中一个目标时显示。</p>
      ) : !inspect ? (
        <p className="hint">后端没有开放样式读取。</p>
      ) : inspect.status === 'error' ? (
        <p className="notice-error">
          {inspect.error}{' '}
          <mdui-button variant="text" onClick={props.onRetry}>
            重试
          </mdui-button>
        </p>
      ) : !inspect.response ? (
        <mdui-linear-progress />
      ) : rows.length === 0 ? (
        <p className="hint">没有直接覆盖：外观全部来自上一层和模板分配。</p>
      ) : (
        <OverrideList rows={rows} />
      )}
      {kind !== 'project' && rows.length > 0 && <p className="hint">直接覆盖写在歌词上（例如导入时带来的样式），外观页只显示、不编辑；它比模板分配更靠近目标。</p>}
    </>
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
      <>
        <h3 className="section-title">自己的外观</h3>
        <p className="hint">{props.multiple ? '选中一个目标时显示。' : props.kind === 'clip' ? '片段没有自己的外观，里面的句子按分配和上一层显示。' : '容器和组没有自己的外观，外观全部来自模板分配。'}</p>
      </>
    );
  }
  const countdown = node.contentKind === 'countdownSequence';
  const rows = countdown ? (node.content?.appearance ? sceneAppearanceRows(node.content.appearance) : []) : overrideRows(node.content?.appearanceOverride ?? []);
  return (
    <>
      <h3 className="section-title">{countdown ? '提示组外观 · 只读' : '标记的覆盖 · 只读'}</h3>
      {rows.length > 0 ? <OverrideList rows={rows} /> : <p className="hint">{countdown ? '读模型里没有提示组外观。' : '没有覆盖：外观来自提示组和模板分配。'}</p>}
      <p className="hint">提示组和标记自己的外观只显示、不编辑。要改颜色和字体，把模板分配到提示组（包括里面的标记）或单个标记上。</p>
    </>
  );
}

function OverrideList(props: { rows: readonly OverrideRow[] }) {
  return (
    <dl className="override-list">
      {props.rows.map((row) => (
        <div key={row.property} className="override-row">
          <dt>{row.label}</dt>
          <dd>
            {row.swatch && <span className="swatch" style={{ background: row.swatch }} />}
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
