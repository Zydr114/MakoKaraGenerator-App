import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { LyricBranchSummary, OrchestrationClip, OrchestrationLeadIn, OrchestrationNode, OrchestrationProgram } from '../../protocol';
import type { AudioController } from '../audio/audio-controller';
import { NoAudioBar } from '../audio/NoAudioBar';
import { Transport } from '../audio/Transport';
import { isTimed, type EditorLine, type Selection } from '../model/lyrics';
import {
  clipEditBlock,
  clipName,
  clipsOf,
  clipTimeSpan,
  containersOf,
  defaultFor,
  lineRange,
  lineRangeText,
  NODE_KIND_TEXT,
  nodeName,
  nodePath,
  presetLabel,
  programOfNode,
  quickLayoutBlock,
  roleText,
  structureSections,
  subtreeIds,
  type StructureSection,
  type OrchestrationModel,
} from '../model/orchestration';
import { clipLabel, mergedRangeText, splitParts } from '../model/orchestration-clip-members';
import { appliedText, DEFAULT_MARK_COUNT, DEFAULT_MARK_TEXT, leadInEditBlock, leadInsOf, leadInSpan, lineNumberText } from '../model/orchestration-lead-ins';
import { slotSequenceBlock, toSeconds } from '../model/orchestration-properties';
import { formatTime } from '../model/time';
import type { EditorSession } from '../session/editor-session';
import { Timeline, type TimelineBand, type TimelineTrack } from '../timeline/Timeline';
import { Button } from '../ui/Button';
import { toast } from '../ui/feedback';
import { CommitField, Select } from '../ui/fields';
import { useObservable } from '../ui/hooks';
import { StatusBar } from '../workspace/StatusBar';
import { WorkLayout } from '../workspace/WorkLayout';
import { isEditing } from '../workspace/Workspace';
import { Group, InspectorHead, Note, Row, ToolButton } from '../workspace/panel';
import type { OrchestrationState, OrchestrationStore } from './orchestration-store';
import { clipBands, clipColors } from './clip-bands';
import { ClipMemberButtons, ClipMembersDialog, SplitClipDialog, type ClipMemberActions } from './ClipMemberViews';
import {
  AddLeadInDialog,
  CountdownSections,
  GenerateLeadInsDialog,
  LeadInInspector,
  MarkSection,
  type LeadInActions,
  type MarkActions,
  type PresentationActions,
} from './LeadInViews';
import { LineInspector, LinesSummary, type LineActions } from './LineInspector';
import { NewClipDialog } from './NewClipDialog';
import { QuickLayoutDialog } from './QuickLayoutDialog';
import { AnimationPolicyField, GeometrySection, LayoutSection, SlotAnimationSection, type PropertyActions } from './PropertySections';
import { SceneCanvas, type FocusedLine } from './SceneCanvas';
import { MoveNodeDialog, NewNodeDialog } from './StructureDialogs';
import { ALL_BLOCKED, AssignmentSection, nodeBlocks, StructurePane, type ClipActions, type StructureActions } from './StructurePane';
import { NO_PROGRAM } from './parts';
import './orchestration.css';

const CLIP_COMMANDS = ['orchestration.createClip', 'orchestration.renameClip', 'orchestration.setClipContainer', 'orchestration.deleteClip'] as const;
const CLIP_MEMBER_COMMANDS = ['orchestration.setClipMembers', 'orchestration.splitClip', 'orchestration.mergeClips'] as const;
const STRUCTURE_COMMANDS = [
  'orchestration.createNode',
  'orchestration.copySubtree',
  'orchestration.setNode',
  'orchestration.reparent',
  'orchestration.reorderChildren',
  'orchestration.reorderSlots',
  'orchestration.addSlot',
  'orchestration.removeNode',
] as const;
const PROPERTY_COMMANDS = [
  'orchestration.setNodeGeometry',
  'orchestration.setLayoutKind',
  'orchestration.setLayoutSettings',
  'orchestration.setLayoutDefaultPlacement',
  'orchestration.setSlotAnimation',
  'orchestration.setProgramAnimationPolicy',
] as const;
const LEAD_IN_COMMANDS = [
  'orchestration.createCountdown',
  'orchestration.createLeadIn',
  'orchestration.updateLeadIn',
  'orchestration.deleteLeadIn',
  'orchestration.deleteLeadInAndExclude',
  'orchestration.previewLeadInGeneration',
  'orchestration.applyLeadInGeneration',
] as const;
const MARK_COMMANDS = ['orchestration.addMark', 'orchestration.setMarkText', 'orchestration.reorderMarks', 'orchestration.spaceMarks'] as const;
const LINE_COMMANDS = ['orchestration.setLineLocalTransform'] as const;
const TIMELINE_HELP = '点选句子（Shift／Ctrl 扩展成连续几句）· 行轨空白处拖动框选 · 点片段轨、前奏轨选片段、前奏';

/**
 * 编排页（整体设计 §5.3，W4 设计 §2.6、§10～§14，界面改版稿件 #orchestration）：左栏上面是画面结构，下面是歌词分配与片段、前奏列表；
 * 中栏是画布（工具栏、成片预览与叠加层），右栏是前奏、片段、句子或节点的属性；底部是播放条与只读时间轴（分配来源的行、片段轨和前奏轨）。
 * 编排直接生效，撤销靠工程历史（整体设计 §11）。
 */
export function OrchestrationPage(props: {
  session: EditorSession;
  store: OrchestrationStore;
  audio: AudioController | null;
  onUndo: () => void;
  onRedo: () => void;
  onSave: () => void;
}) {
  const { session, store, audio } = props;
  const state = useObservable(session.state$);
  const view = useObservable(store.state$);
  const [loopLine, setLoopLine] = useState(false);
  const branches = useMemo(
    (): LyricBranchSummary[] => state.branches ?? [{ id: state.branchId, name: '歌词', role: 'primary', timingMode: 'independent', lineCount: state.lines.length }],
    [state.branches, state.branchId, state.lines.length],
  );
  const sections = useMemo(() => (view.model ? structureSections(view.model, branches) : []), [view.model, branches]);
  const candidates = useMemo(() => sections.flatMap((s) => s.rows.map((r) => r.node)), [sections]);
  const branchNameOf = useCallback((id: string) => branches.find((b) => b.id === id)?.name || undefined, [branches]);

  // 时间轴显示分配条来源的行；选中的句子是编排页自己的，不改歌词页的当前行。
  const lines = useMemo(() => state.linesByBranch[view.branchId] ?? [], [state.linesByBranch, view.branchId]);
  const program = view.model?.programByBranch.get(view.branchId);
  const clips = useMemo(() => clipsOf(program, lines), [program, lines]);
  const range = useMemo(() => (view.lineIds ? lineRange(lines, view.lineIds) : undefined), [lines, view.lineIds]);
  const marked = useMemo(() => new Set(view.lineIds ?? []), [view.lineIds]);
  const anchor = useMemo(() => (view.lineAnchor ? { lineId: view.lineAnchor } : undefined), [view.lineAnchor]);
  const colors = useMemo(() => (view.model && program ? clipColors(view.model, program) : new Map<string, number>()), [view.model, program]);
  const leadIns = useMemo(() => leadInsOf(program, lines), [program, lines]);
  const tracks = useMemo(
    (): TimelineTrack[] => [
      { id: 'clips', empty: '没有片段', bands: clipBands(clips, lines, colors, new Set(view.selectedClipId ? [view.selectedClipId] : [])) },
      { id: 'leadIns', empty: '没有前奏提示', bands: leadInBands(leadIns, view.selectedLeadInId) },
    ],
    [clips, lines, colors, view.selectedClipId, leadIns, view.selectedLeadInId],
  );
  const loopSpan = useMemo(() => timedSpan(lines, marked), [lines, marked]);
  useEffect(() => {
    if (!audio) return;
    audio.setLoop(loopLine && loopSpan ? loopSpan : undefined);
  }, [audio, loopLine, loopSpan]);
  const clickLine = useCallback((selection: Selection, options: { additive: boolean }) => store.clickLine(selection.lineId, options.additive), [store]);
  const boxLines = useCallback((ids: string[], options: { additive: boolean }) => store.selectLines(ids, options.additive), [store]);
  const selectBand = useCallback((trackId: string, id: string) => (trackId === 'clips' ? store.selectClip(id) : store.selectLeadIn(id)), [store]);
  const [newClipOpen, setNewClipOpen] = useState(false);

  // 片段编辑（G19-B）：工程只读、后端没开放、来源不支持时不可用，写明原因。
  const branch = branches.find((b) => b.id === view.branchId);
  const branchName = branch?.name || view.branchId;
  const clipBlock = !program
    ? NO_PROGRAM
    : state.summary.isReadOnly
      ? '工程只读，不能编辑片段。'
      : !CLIP_COMMANDS.every((type) => session.supportsCommand(type))
        ? '后端没有开放片段编辑。'
        : clipEditBlock(branch, program);
  // 调整句子、拆分与合并（§15）：判断与片段编辑相同，命令单独检查。
  const memberBlock = !program
    ? NO_PROGRAM
    : state.summary.isReadOnly
      ? '工程只读，不能编辑片段。'
      : !CLIP_MEMBER_COMMANDS.every((type) => session.supportsCommand(type))
        ? '后端没有开放片段的调整句子、拆分与合并。'
        : clipEditBlock(branch, program);
  const [adjustClipId, setAdjustClipId] = useState<string | undefined>();
  const [split, setSplit] = useState<{ clipId: string; playheadUs: number | undefined } | undefined>();
  const adjustClip = clips.find((c) => c.id === adjustClipId);
  const splitClip = clips.find((c) => c.id === split?.clipId);
  // 结构编辑（G20）：工程只读、后端没开放时不可用，写明原因。
  const [newNodeOpen, setNewNodeOpen] = useState(false);
  const [moveId, setMoveId] = useState<string | undefined>();
  const undo = { label: '撤销', run: props.onUndo };
  const label = (id: string) => {
    const node = view.model?.byId.get(id);
    return node && view.model ? nodeName(view.model, node) : id;
  };
  const structure: StructureActions = {
    block: state.summary.isReadOnly
      ? '工程只读，不能编辑画面结构。'
      : !STRUCTURE_COMMANDS.every((type) => session.supportsCommand(type))
        ? '后端没有开放结构编辑。'
        : undefined,
    busy: !!view.busy,
    blocks: (id) => (view.model ? nodeBlocks(view.model, id, candidates, branchNameOf) : ALL_BLOCKED),
    onNew: () => setNewNodeOpen(true),
    onCopy: async (id) => {
      const text = label(id);
      if ((await store.copyNode(id)) === 'applied') toast(`已复制“${text}”`, undo);
    },
    onReorder: async (id, delta) => {
      const text = label(id);
      const slot = view.model?.slotOf.has(id);
      if ((await store.reorderNode(id, delta)) === 'applied') toast(slot ? '已调整槽位顺序' : `已${delta < 0 ? '上移' : '下移'}“${text}”`, undo);
    },
    onMove: (id) => setMoveId(id),
    onRemove: async (id) => {
      const model = view.model;
      const node = model?.byId.get(id);
      if (!model || !node) return;
      const slot = model.slotOf.get(id);
      const inside = subtreeIds(model, id).length - 1;
      const text = slot && !node.name ? `已删除槽位 ${slot.index}` : `已删除“${nodeName(model, node)}”${inside > 0 ? `及其中 ${inside} 个节点` : ''}`;
      if ((await store.removeNode(id)) === 'applied') toast(text, undo);
    },
    onRename: async (id, name) => {
      if ((await store.renameNode(id, name)) === 'applied') toast(name.trim() ? `已改名为“${name.trim()}”` : '已清除名称', undo);
    },
    onAddSlot: async (containerId) => {
      if ((await store.addSlot(containerId)) === 'applied') toast('已加槽位', undo);
    },
  };
  // 属性编辑（G21）：工程只读、后端没开放时不可用，写明原因。
  const properties: PropertyActions = {
    block: state.summary.isReadOnly
      ? '工程只读，不能编辑属性。'
      : !PROPERTY_COMMANDS.every((type) => session.supportsCommand(type))
        ? '后端没有开放属性编辑。'
        : undefined,
    busy: !!view.busy,
    run: async (command, done) => {
      const result = await store.setProperties(command);
      if (result === 'applied') toast(done, undo);
      return result;
    },
  };
  // 前奏提示（G23）：工程只读、后端没开放、来源不支持时不可用，写明原因。
  const readOnly = state.summary.isReadOnly;
  const [addLeadIn, setAddLeadIn] = useState<{ lineId?: string } | undefined>();
  const [generateOpen, setGenerateOpen] = useState(false);
  const leadInActions: LeadInActions = {
    block: !program
      ? NO_PROGRAM
      : readOnly
        ? '工程只读，不能编辑前奏提示。'
        : !LEAD_IN_COMMANDS.every((type) => session.supportsCommand(type))
          ? '后端没有开放前奏提示。'
          : leadInEditBlock(branch, program),
    busy: !!view.busy,
    onAdd: (lineId) => setAddLeadIn(lineId ? { lineId } : {}),
    onGenerate: () => setGenerateOpen(true),
    onUpdate: async (leadIn, patch, done) => {
      const result = await store.updateLeadIn(view.branchId, leadIn, patch);
      if (result === 'applied') toast(done, undo);
      return result;
    },
    onDelete: async (leadIn, exclude) => {
      const text = lineNumberText(lines, leadIn.targetLineId);
      if ((await store.deleteLeadIn(view.branchId, leadIn.id, exclude)) === 'applied') toast(exclude ? `已删除${text}的前奏，自动生成不会再加这一句` : `已删除${text}的前奏`, undo);
    },
    onAttachment: async (branchId, leadIn, mode) => {
      const result = await store.setLeadInAttachment(branchId, leadIn.id, leadIn.indicatorContainerId, mode);
      if (result === 'applied') {
        toast(
          mode === 'CueTargetAboveStart'
            ? '提示组已改为放在目标句首字上方'
            : mode === 'FixedParent'
              ? '提示组已改为固定在父级'
              : '提示组已改为跟着目标句的文字',
          undo,
        );
        return undefined;
      }
      if (result === 'failed') return session.state.notice?.message ?? '附着方式没有修改成功。';
      return undefined;
    },
    onSelect: (branchId, id) => {
      if (branchId !== view.branchId) store.setBranch(branchId);
      store.selectLeadIn(id);
    },
    onSelectNode: (id) => store.select(id),
  };
  const attachmentBlock = readOnly ? '工程只读，不能切换附着方式。' : !session.supportsCommand('orchestration.setLeadInAttachment') ? '后端没有开放附着方式。' : undefined;
  const countdownBlock = readOnly ? '工程只读，不能新建提示组。' : !session.supportsCommand('orchestration.createCountdown') ? '后端没有开放前奏提示组。' : undefined;
  const modernDefaults = session.supportsCommand('orchestration.setCountdownPresentation');
  const countdownPresentation: PresentationActions = {
    block: readOnly ? '工程只读，不能修改提示组图形与动画。' : !modernDefaults ? '后端没有开放提示组图形与动画。' : undefined,
    busy: !!view.busy,
    onShape: async (containerId, shape, done) => {
      const result = await store.setCountdownPresentation(containerId, { shape });
      if (result === 'applied') toast(done, undo);
      return result;
    },
    onAnimation: async (containerId, animation, done) => {
      const result = await store.setCountdownPresentation(containerId, { animation });
      if (result === 'applied') toast(done, undo);
      return result;
    },
  };
  const createCountdown = async (): Promise<{ containerId: string } | { error: string }> => {
    if (!program) return { error: NO_PROGRAM };
    const outcome = await store.createCountdown(program.rootContainerId, '', DEFAULT_MARK_COUNT, { modernDefaults });
    if (outcome.result === 'failed') return { error: outcome.error };
    toast('已新建提示组“前奏提示”', undo);
    return { containerId: outcome.containerId };
  };
  const previewGeneration = useCallback((settings: Parameters<OrchestrationStore['previewGeneration']>[1]) => void store.previewGeneration(view.branchId, settings), [store, view.branchId]);
  const closeGenerate = () => {
    setGenerateOpen(false);
    store.endGenerationPreview();
  };
  const marks: MarkActions = {
    block: readOnly ? '工程只读，不能编辑标记。' : !MARK_COMMANDS.every((type) => session.supportsCommand(type)) ? '后端没有开放标记编辑。' : undefined,
    busy: !!view.busy,
    onText: async (id, text) => {
      if ((await store.setMarkText(id, text)) === 'applied') toast(`标记已改为“${text}”`, undo);
    },
    onAdd: async (containerId) => {
      if ((await store.addMark(containerId, DEFAULT_MARK_TEXT)) === 'applied') toast('已加标记', undo);
    },
    onSpace: async (containerId) => {
      const result = await store.spaceMarks(containerId);
      if (result === 'applied') toast('已均匀排列标记', undo);
      else if (result === 'unchanged') toast('已经是均匀排列');
    },
    structureBlock: structure.block,
    blocks: structure.blocks,
    onReorder: structure.onReorder,
    onRemove: structure.onRemove,
  };
  const linesOf = (branchId: string) => state.linesByBranch[branchId] ?? [];
  const selectedLeadIn = view.selectedLeadInId ? program?.leadIns?.find((l) => l.id === view.selectedLeadInId) : undefined;

  // 句子（G24）：本句微调与进入动画。有句子焦点、只选了一句时属性区显示这一句，画布上可以拖动它。
  const lineActions: LineActions = {
    block: readOnly ? '工程只读，不能调整句子。' : !LINE_COMMANDS.every((type) => session.supportsCommand(type)) ? '后端没有开放本句微调与进入动画。' : undefined,
    busy: !!view.busy,
    run: async (command, done) => {
      const result = await store.setLine(command);
      if (result === 'applied') toast(done, undo);
      return result;
    },
  };
  const focusedId = view.lineFocus && view.lineIds?.length === 1 ? view.lineIds[0] : undefined;
  const focused = focusedId ? lines.find((l) => l.id === focusedId) : undefined;
  const presentation = view.linePresentation;
  const shown = focused && presentation?.branchId === view.branchId && presentation.lineId === focused.id ? presentation : undefined;
  const canvasLine: FocusedLine | undefined = focused && {
    branchId: view.branchId,
    lineId: focused.id,
    transform: shown?.response?.localTransform ?? null,
    ready: shown?.status === 'ready' && shown.revision === session.latestRevision,
    label: lineNumberText(lines, focused.id),
  };

  // Delete 键等同于工具栏的“删除”；不能删时在状态栏写明原因。
  const deleteSelected = () => {
    const id = view.selectedId;
    if (!id || structure.busy) return;
    const reason = structure.block ?? structure.blocks(id).remove;
    if (reason) session.notify({ level: 'warning', message: `不能删除：${reason}` });
    else void structure.onRemove(id);
  };
  useOrchestrationKeys({ store, audio, sections, onUndo: props.onUndo, onRedo: props.onRedo, onSave: props.onSave, onDelete: deleteSelected });

  // 歌词分配：改默认容器；快速布局（画布工具栏）。
  const canSetDefault = session.supportsCommand('orchestration.setDefaultContainer') && !readOnly;
  const changeDefault = async (containerId: string) => {
    const target = view.model?.byId.get(containerId);
    const result = await store.setDefaultContainer(view.branchId, containerId);
    if (result === 'applied') toast(`默认容器已改为“${target && view.model ? nodeName(view.model, target) : containerId}”`, undo);
  };
  const [layoutOpen, setLayoutOpen] = useState(false);
  const canLayout = session.supportsCommand('orchestration.previewBasicLayout') && session.supportsCommand('orchestration.applyBasicLayout') && !readOnly;
  const layoutBlock = !program ? NO_PROGRAM : (quickLayoutBlock(branch, program) ?? (canLayout ? undefined : readOnly ? '工程只读，不能快速布局。' : '后端没有开放快速布局。'));
  const closeLayout = () => {
    setLayoutOpen(false);
    store.endLayoutPreview();
  };

  const clipActions: ClipActions = {
    block: clipBlock,
    busy: !!view.busy,
    onNew: () => setNewClipOpen(true),
    onRename: async (clip, name) => {
      const result = await store.renameClip(view.branchId, clip.id, name);
      if (result === 'applied') toast(name.trim() ? `片段已改名为“${name.trim()}”` : '已清除片段名称', undo);
    },
    onMove: async (clip, label, containerId) => {
      const target = view.model?.byId.get(containerId);
      const result = await store.setClipContainer(view.branchId, clip.id, containerId);
      if (result === 'applied') toast(`片段“${label}”已改到“${target && view.model ? nodeName(view.model, target) : containerId}”`, undo);
    },
    onDelete: async (clip, label) => {
      const clipRange = lineRange(lines, clip.lineIds);
      const result = await store.deleteClip(view.branchId, clip.id);
      if (result === 'applied') toast(`已删除片段“${label}”，${clipRange ? lineRangeText(clipRange) : '这几句'}回到默认容器`, undo);
    },
  };
  const memberActions: ClipMemberActions = {
    block: memberBlock,
    busy: !!view.busy,
    onAdjust: (clip) => setAdjustClipId(clip.id),
    onSplit: (clip) => setSplit({ clipId: clip.id, playheadUs: audio?.playheadUs() }),
    // 合并不用确认框；失败由会话写在状态栏。
    onMerge: async (clip, label, other, otherLabel) => {
      const merged = mergedRangeText(lines, clip, other);
      const outcome = await store.mergeClips(view.branchId, clip.id, other.id);
      if (outcome.result === 'applied') toast(`已把片段“${otherLabel}”合并进“${label}”${merged ? `（${merged}）` : ''}`, undo);
    },
  };

  return (
    <WorkLayout
      page="orchestration"
      className="orchestration"
      stage="center"
      left={{
        label: '画面结构',
        content: (
          <StructurePane
            view={view}
            sections={sections}
            branches={branches}
            actions={structure}
            onSelect={(id) => store.select(id)}
            onRetry={() => store.reload()}
            assignment={
              <AssignmentSection
                view={view}
                store={store}
                branches={branches}
                lines={lines}
                range={range}
                clips={clips}
                colors={colors}
                leadIns={leadIns}
                canSetDefault={canSetDefault}
                clipActions={clipActions}
                leadInActions={leadInActions}
                onDefaultContainer={(id) => void changeDefault(id)}
              />
            }
          />
        ),
      }}
      center={
        <section className="orch-canvas stage-canvas">
          <SceneCanvas
            session={session}
            audio={audio}
            view={view}
            properties={properties}
            line={canvasLine}
            lineActions={lineActions}
            onSelect={(id) => store.select(id)}
            onFocusLine={(line) => store.focusLine(line.branchId, line.lineId)}
            tools={
              <span className="tool-wrap" {...(view.model && layoutBlock ? { title: `快速布局：${layoutBlock}` } : {})}>
                <Button size="sm" icon="wand_stars" isDisabled={!view.model || !!layoutBlock || !!view.busy} onPress={() => setLayoutOpen(true)}>
                  快速布局…
                </Button>
              </span>
            }
          />
        </section>
      }
      right={{
        label: '属性',
        content: (
          <div className="orch-insp">
            {selectedLeadIn && view.model && program ? (
              <LeadInInspector
                model={view.model}
                program={program}
                leadIn={selectedLeadIn}
                lines={lines}
                branchName={branchName}
                actions={leadInActions}
                attachmentBlock={attachmentBlock}
              />
            ) : view.selectedClipId && view.model && program && clips.some((c) => c.id === view.selectedClipId) ? (
              <ClipInspector
                model={view.model}
                program={program}
                clips={clips}
                clipId={view.selectedClipId}
                lines={lines}
                branchName={branchName}
                actions={clipActions}
                members={memberActions}
                onSelectNode={(id) => store.select(id)}
              />
            ) : focused && view.model ? (
              <LineInspector
                model={view.model}
                program={program}
                branchId={view.branchId}
                branchName={branchName}
                line={focused}
                lines={lines}
                presentation={shown}
                session={session}
                actions={lineActions}
                leadIns={
                  program && !leadInEditBlock(branch, program)
                    ? { block: leadInActions.block, onAdd: (lineId) => leadInActions.onAdd(lineId), onSelect: (leadIn) => store.selectLeadIn(leadIn.id) }
                    : undefined
                }
                onSeek={(us) => audio?.seek(us)}
              />
            ) : view.lineFocus && range && range.count > 1 && view.model ? (
              <LinesSummary branchName={branchName} range={range} actions={lineActions} clip={{ block: clipActions.block, busy: clipActions.busy, onNew: clipActions.onNew }} />
            ) : (
              <NodeInspector
                view={view}
                branches={branches}
                actions={structure}
                properties={properties}
                leadIns={leadInActions}
                attachmentBlock={attachmentBlock}
                presentation={countdownPresentation}
                marks={marks}
                linesOf={linesOf}
                onSelect={(id) => store.select(id)}
              />
            )}
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
              lines={lines}
              selection={anchor}
              frameRate={session.frameRate}
              marked={marked}
              onSelect={clickLine}
              onSelectLines={boxLines}
              tracks={tracks}
              onSelectBand={selectBand}
              help={TIMELINE_HELP}
              leading={
                <Transport
                  audio={audio}
                  loopLine={loopLine}
                  onLoopLine={setLoopLine}
                  loopAvailable={!!loopSpan}
                  loopLabel="循环选中"
                  help="空格 播放／暂停 · ↑↓ 在结构里移动 · Delete 删除节点"
                />
              }
            />
          )}
        </>
      }
      status={<StatusBar state={state} />}
    >
      <QuickLayoutDialog
        open={layoutOpen}
        branchName={branchName}
        lineCount={branch?.lineCount}
        program={program}
        range={range}
        preview={view.layoutPreview?.branchId === view.branchId ? view.layoutPreview : undefined}
        busy={!!view.busy}
        onPreview={(preset, lineIds) => void store.previewLayout(view.branchId, preset, lineIds)}
        onApply={async (preset, lineIds) => {
          const target = lineIds && range ? lineRangeText(range) : branchName;
          const result = await store.applyLayout(view.branchId, preset, lineIds);
          if (result === 'failed') return;
          closeLayout();
          if (result === 'applied') toast(`已套用“${presetLabel(preset.preset)}”到${lineIds ? '' : ' '}${target}`, undo);
          else toast('已经是这个布局，没有变化');
        }}
        onClose={closeLayout}
      />
      <AddLeadInDialog
        open={!!addLeadIn}
        model={view.model}
        program={program}
        lines={lines}
        selected={addLeadIn?.lineId ? [addLeadIn.lineId] : view.lineIds}
        busy={!!view.busy}
        createBlock={countdownBlock}
        onCreateCountdown={createCountdown}
        onCreate={async (leadIn) => {
          const outcome = await store.createLeadIn(view.branchId, leadIn);
          if (outcome.result === 'failed') return outcome.error;
          setAddLeadIn(undefined);
          toast(`已为${lineNumberText(lines, leadIn.targetLineId)}添加前奏`, undo);
          return undefined;
        }}
        onClose={() => setAddLeadIn(undefined)}
      />
      <GenerateLeadInsDialog
        open={generateOpen}
        model={view.model}
        program={program}
        lines={lines}
        preview={view.generationPreview?.branchId === view.branchId ? view.generationPreview : undefined}
        busy={!!view.busy}
        createBlock={countdownBlock}
        onCreateCountdown={createCountdown}
        onPreview={previewGeneration}
        onApply={async (settings, selected, result) => {
          const outcome = await store.applyGeneration(view.branchId, settings, selected);
          if (outcome.result === 'failed') return outcome.error;
          closeGenerate();
          if (outcome.result === 'applied') toast(appliedText(result.candidates, selected), undo);
          else toast('没有变化');
          return undefined;
        }}
        onClose={closeGenerate}
      />
      <NewClipDialog
        open={newClipOpen}
        branchName={branchName}
        model={view.model}
        program={program}
        lines={lines}
        range={range}
        busy={!!view.busy}
        onCreate={async (clip, { first, last }) => {
          const outcome = await store.createClip(view.branchId, clip);
          if (outcome.result === 'failed') return outcome.error;
          setNewClipOpen(false);
          const text = first === last ? `第 ${first} 句` : `第 ${first}～${last} 句`;
          toast(`已为${text}新建片段${clip.name?.trim() ? `“${clip.name.trim()}”` : ''}`, undo);
          return undefined;
        }}
        onClose={() => setNewClipOpen(false)}
      />
      <ClipMembersDialog
        clip={adjustClip}
        label={adjustClip && program ? clipLabel(program, lines, adjustClip) : ''}
        program={program}
        lines={lines}
        busy={!!view.busy}
        onApply={async (clip, lineIds, rangeText) => {
          const label = program ? clipLabel(program, lines, clip) : clip.id;
          const outcome = await store.setClipMembers(view.branchId, clip.id, lineIds);
          if (outcome.result === 'failed') return outcome.error;
          setAdjustClipId(undefined);
          if (outcome.result === 'applied') toast(`片段“${label}”改为${rangeText}`, undo);
          else toast('没有变化');
          return undefined;
        }}
        onClose={() => setAdjustClipId(undefined)}
      />
      <SplitClipDialog
        clip={splitClip}
        label={splitClip && program ? clipLabel(program, lines, splitClip) : ''}
        lines={lines}
        playheadUs={split?.playheadUs}
        busy={!!view.busy}
        onSplit={async (clip, splitLineId) => {
          const label = program ? clipLabel(program, lines, clip) : clip.id;
          const parts = splitParts(clip, lines, splitLineId);
          const outcome = await store.splitClip(view.branchId, clip.id, splitLineId);
          if (outcome.result === 'failed') return outcome.error;
          setSplit(undefined);
          if (outcome.result === 'applied') toast(parts ? `已把片段“${label}”拆成${parts.left}和${parts.right}` : `已拆分片段“${label}”`, undo);
          return undefined;
        }}
        onClose={() => setSplit(undefined)}
      />
      <NewNodeDialog
        open={newNodeOpen}
        model={view.model}
        candidates={candidates}
        program={program}
        selectedId={view.selectedId}
        branchName={branchNameOf}
        busy={!!view.busy}
        countdown={session.supportsCommand('orchestration.createCountdown')}
        onCreate={async (node) => {
          if (node.kind === 'countdown') {
            const created = await store.createCountdown(node.parentId, node.name, node.count, { select: true, modernDefaults });
            if (created.result === 'failed') return created.error;
            setNewNodeOpen(false);
            const name = node.name.trim();
            toast(`已新建前奏提示组${name ? `“${name}”` : ''}（${node.count} 个标记）`, undo);
            return undefined;
          }
          const outcome = await store.createNode(node);
          if (outcome.result === 'failed') return outcome.error;
          setNewNodeOpen(false);
          const name = node.name.trim();
          toast(`已新建${NODE_KIND_TEXT[node.kind]}${name ? `“${name}”` : ''}${node.kind === 'slotSequence' ? `（${node.slotCount ?? 2} 个槽位）` : ''}`, undo);
          return undefined;
        }}
        onClose={() => setNewNodeOpen(false)}
      />
      <MoveNodeDialog
        nodeId={moveId}
        model={view.model}
        candidates={candidates}
        branchName={branchNameOf}
        busy={!!view.busy}
        onMove={async (parentId) => {
          const id = moveId!;
          const text = label(id);
          const target = parentId ? label(parentId) : undefined;
          const result = await store.moveNode(id, parentId);
          if (result === 'failed') return session.state.notice?.message ?? '移动没有成功。';
          setMoveId(undefined);
          if (result === 'applied') toast(target ? `已把“${text}”移到“${target}”` : `已把“${text}”移到画布根层`, undo);
          return undefined;
        }}
        onClose={() => setMoveId(undefined)}
      />
    </WorkLayout>
  );
}

// ── 属性 ──

/** 节点的属性（W4 设计 §12、§13、§14.6）：名称、位置与变换、参考框、内部布局与槽位、调度与动画、提示组与标记、来源。 */
function NodeInspector(props: {
  view: OrchestrationState;
  branches: LyricBranchSummary[];
  actions: StructureActions;
  properties: PropertyActions;
  leadIns: LeadInActions;
  attachmentBlock: string | undefined;
  presentation: PresentationActions;
  marks: MarkActions;
  linesOf: (branchId: string) => readonly EditorLine[];
  onSelect: (id: string) => void;
}) {
  const { view, actions, properties } = props;
  const model = view.model;
  const node = model && view.selectedId ? model.byId.get(view.selectedId) : undefined;
  if (!model || !node) {
    const text = view.status === 'unsupported' ? '后端没有开放编排读模型。' : !model ? '正在读取编排结构…' : '在左侧选择一个节点。';
    return (
      <div className="insp">
        <Note className="insp-empty">{text}</Note>
      </div>
    );
  }

  const branchName = (id: string) => props.branches.find((b) => b.id === id)?.name;
  const sourceName = (id: string) => branchName(id) ?? id;
  const isPrimary = (id: string) => props.branches.find((b) => b.id === id)?.role === 'primary';
  const program = programOfNode(model, node.id);
  const slot = model.slotOf.get(node.id);
  const slotContainer = slot && model.byId.get(slot.containerId);
  const defaults = defaultFor(model, node.id);
  const layout = node.layout;
  const programDefault = program && model.byId.get(program.defaultContainerId);
  const disabled = !!actions.block || actions.busy;
  const unnamed = nodeName(model, { ...node, name: null });
  const path = nodePath(model, node.id, branchName);

  return (
    <div className="insp">
      <InspectorHead crumbs={path.slice(0, -1)} title={path.at(-1) ?? nodeName(model, node)} kind={roleText(model, node)} id={node.id} />
      <Group title="节点">
        <CommitField
          key={node.id}
          label="名称"
          labelPlacement="inside"
          value={node.name ?? ''}
          placeholder={unnamed}
          helper={`清空表示不命名，显示为“${unnamed}”。`}
          disabled={disabled}
          onCommit={(value) => void actions.onRename(node.id, value)}
        />
        {defaults.length > 0 && <Note>{defaults.map((p) => sourceName(p.branchId)).join('、')}的默认容器：没有单独分配的歌词放在这里。</Note>}
        {slotContainer && (
          <Note>
            “{nodeName(model, slotContainer)}”轮换的第 {slot.index} 个槽位。
          </Note>
        )}
        {properties.block && <Note className="property-block">{properties.block}</Note>}
      </Group>
      <GeometrySection key={`geometry-${node.id}`} model={model} node={node} actions={properties} />
      {layout && (
        <LayoutSection
          key={`layout-${node.id}`}
          model={model}
          node={node}
          kindBlock={slotSequenceBlock(model, node.id, isPrimary)}
          actions={properties}
          slots={
            (layout.kind === 'SlotSequence' || layout.slotIds.length > 0) && <SlotList model={model} container={node} actions={actions} disabled={disabled} onSelect={props.onSelect} />
          }
        />
      )}
      {layout?.slotAnimation && <SlotAnimationSection key={`slots-${node.id}`} node={node} animation={layout.slotAnimation} actions={properties} />}
      {node.contentKind === 'countdownSequence' && (
        <CountdownSections
          model={model}
          node={node}
          lines={props.linesOf}
          branchName={sourceName}
          actions={props.leadIns}
          attachmentBlock={props.attachmentBlock}
          presentation={props.presentation}
          marks={props.marks}
          onSelectNode={props.onSelect}
        />
      )}
      {model.markOf.has(node.id) && <MarkSection model={model} node={node} marks={props.marks} />}

      {program && (
        <Group title="来源">
          <dl className="dl info-list">
            <Row label="来源" value={sourceName(program.branchId)} />
            <Row label="默认容器" value={programDefault ? nodeName(model, programDefault) : `${program.defaultContainerId}（不存在）`} />
          </dl>
          <AnimationPolicyField key={`policy-${program.id}`} model={model} program={program} primary={isPrimary(program.branchId)} actions={properties} />
        </Group>
      )}
    </div>
  );
}

/** 轮换的槽位（W4 设计 §12.5）：放在“内部布局”里，按轮换顺序，每行可上移、下移、删除；下方加槽位。 */
function SlotList(props: { model: OrchestrationModel; container: OrchestrationNode; actions: StructureActions; disabled: boolean; onSelect: (id: string) => void }) {
  const { model, container, actions } = props;
  const slotIds = container.layout?.slotIds ?? [];
  return (
    <div className="slot-block">
      <div className="sub">槽位 · {slotIds.length} 个</div>
      <div className="slots" role="list" aria-label="槽位">
        {slotIds.map((id, i) => {
          const slot = model.byId.get(id);
          const blocks = slot ? actions.blocks(id) : ALL_BLOCKED;
          const own = (reason: string | undefined) => actions.block ?? (slot ? reason : '这个槽位不存在。');
          return (
            <div key={id} role="listitem" className="slot" data-slot-id={id}>
              <b className="slot-n">{i + 1}</b>
              <button type="button" className="slot-name" disabled={!slot} onClick={() => props.onSelect(id)} title="在结构树里选中">
                {slot ? nodeName(model, slot) : `${id}（不存在）`}
              </button>
              <ToolButton icon="arrow_upward" label={`上移槽位 ${i + 1}`} reason={own(blocks.up)} disabled={props.disabled} onPress={() => void actions.onReorder(id, -1)} />
              <ToolButton icon="arrow_downward" label={`下移槽位 ${i + 1}`} reason={own(blocks.down)} disabled={props.disabled} onPress={() => void actions.onReorder(id, 1)} />
              <ToolButton icon="delete" label={`删除槽位 ${i + 1}`} reason={own(blocks.remove)} disabled={props.disabled} onPress={() => void actions.onRemove(id)} />
            </div>
          );
        })}
      </div>
      <div className="row-actions">
        <Button variant="text" size="sm" icon="add" isDisabled={props.disabled} onPress={() => void actions.onAddSlot(container.id)}>
          加槽位
        </Button>
      </div>
      <Note>槽位数变了，“同时最多显示”不会跟着变，在下面的“调度”里调整。</Note>
    </div>
  );
}

/** 片段的属性（W4 设计 §10.5、§11.3～§11.5、§15.2）：名称、句子、时间、目标容器、调度上下文；改名、调整句子、拆分、合并、改目标容器、删除。 */
function ClipInspector(props: {
  model: OrchestrationModel;
  program: OrchestrationProgram;
  clips: readonly OrchestrationClip[];
  clipId: string;
  lines: readonly EditorLine[];
  branchName: string;
  actions: ClipActions;
  members: ClipMemberActions;
  onSelectNode: (id: string) => void;
}) {
  const { actions } = props;
  const disabled = !!actions.block || actions.busy;
  const { model, lines } = props;
  const index = props.clips.findIndex((c) => c.id === props.clipId);
  const clip = props.clips[index]!;
  const name = clipName(clip, index + 1);
  const range = lineRange(lines, clip.lineIds);
  const span = clipTimeSpan(clip, lines);
  const container = model.byId.get(clip.containerId);
  const context = clip.scheduleContext;
  const contextRange = context ? lineRange(lines, context.lineIds) : undefined;
  const lineNumber = (id: string | null | undefined) => {
    if (!id) return '无';
    const i = lines.findIndex((l) => l.id === id);
    return i < 0 ? `${id}（不在来源里）` : `第 ${i + 1} 句`;
  };
  const missing = clip.lineIds.length - (range?.count ?? 0);
  const containers = containersOf(model, props.program);
  // 目标容器不在来源根下时（结构异常）也列出来，免得下拉框显示空白。
  const options = container && !containers.includes(container) ? [container, ...containers] : containers;

  return (
    <div className="insp clip-inspector">
      <InspectorHead crumbs={[props.branchName, '片段']} title={name} kind="片段" id={clip.id} />
      <Group title="片段">
        <CommitField
          key={clip.id}
          label="名称"
          labelPlacement="inside"
          value={clip.name ?? ''}
          placeholder={`片段 ${index + 1}`}
          helper="清空表示不命名，列表里显示“片段 N”。"
          disabled={disabled}
          onCommit={(value) => void actions.onRename(clip, value)}
        />
      </Group>
      <Group title="句子">
        <dl className="dl info-list">
          <Row label="范围" value={range ? `${lineRangeText(range)}${range.count > 1 ? `（${range.count} 句）` : ''}` : '句子都不在来源里'} />
          {missing > 0 && <Row label="不在来源里" value={`${missing} 句`} />}
          <Row label="时间" value={span ? `${formatTime(span.startUs)}～${formatTime(span.endUs)}` : '没有时间'} />
        </dl>
        <ClipMemberButtons model={model} program={props.program} lines={lines} clip={clip} label={name} actions={props.members} />
      </Group>
      <Group
        title="目标容器"
        extra={
          container && (
            <Button variant="text" size="sm" onPress={() => props.onSelectNode(container.id)}>
              在结构树里查看
            </Button>
          )
        }
      >
        <Select
          label="目标容器"
          labelPlacement="none"
          value={clip.containerId}
          options={[
            ...options.map((n) => ({ value: n.id, label: `${nodeName(model, n)} · ${roleText(model, n)}${n.id === props.program.defaultContainerId ? '（默认容器）' : ''}` })),
            ...(container ? [] : [{ value: clip.containerId, label: `${clip.containerId}（不存在）`, disabled: true }]),
          ]}
          disabled={disabled || options.length < 2}
          onChange={(id) => void actions.onMove(clip, name, id)}
        />
      </Group>
      {context && (
        <Group title="调度上下文">
          <dl className="dl info-list">
            <Row label="原段" value={contextRange ? lineRangeText(contextRange) : '句子都不在来源里'} />
            <Row label="默认段" value={context.isDefaultSegment ? '是（原来在默认容器里）' : '否'} />
            <Row label="前一句" value={lineNumber(context.previousLineId)} />
            <Row label="后一句" value={lineNumber(context.nextLineId)} />
          </dl>
          <Note>从原来的一段拆出，保持原来的槽位和显示时钟。</Note>
        </Group>
      )}
      <footer className="insp-foot">
        <div className="insp-actions">
          <Button danger size="sm" icon="delete" isDisabled={disabled} onPress={() => void actions.onDelete(clip, name)}>
            删除片段
          </Button>
        </div>
        <Note>{actions.block ?? '删除后这几句回到默认容器。改动都可以撤销。'}</Note>
      </footer>
    </div>
  );
}

// ── 时间轴 ──

/** 前奏轨的色块：“▸ N 秒”，停用的画淡；算不出时间的不画。 */
function leadInBands(leadIns: readonly OrchestrationLeadIn[], selectedId: string | undefined): TimelineBand[] {
  return leadIns.flatMap((leadIn) => {
    const span = leadInSpan(leadIn);
    return span ? [{ id: leadIn.id, ...span, label: `▸ ${toSeconds(leadIn.durationUs)} 秒`, color: 1, selected: leadIn.id === selectedId, dim: !leadIn.enabled }] : [];
  });
}

/** 选中句子的时间范围（循环选中用）；都没有时间时返回 undefined。 */
function timedSpan(lines: readonly EditorLine[], ids: ReadonlySet<string>): { startUs: number; endUs: number } | undefined {
  let startUs = Infinity;
  let endUs = -Infinity;
  for (const line of lines) {
    if (!ids.has(line.id) || !isTimed(line)) continue;
    startUs = Math.min(startUs, line.startUs);
    endUs = Math.max(endUs, line.endUs);
  }
  return startUs < endUs ? { startUs, endUs } : undefined;
}

// ── 快捷键 ──

const inCollection = (event: Event) => event.composedPath().some((node) => node instanceof HTMLElement && ['treegrid', 'grid'].includes(node.getAttribute('role') ?? ''));

/** 编排页快捷键（整体设计 §4）：空格播放，↑↓ 在结构树里移动，Delete 删除选中的节点，Ctrl／⌘+Z、Shift+Z、Y、S。焦点在输入框里时让给输入框。 */
function useOrchestrationKeys(options: {
  store: OrchestrationStore;
  audio: AudioController | null;
  sections: StructureSection[];
  onUndo: () => void;
  onRedo: () => void;
  onSave: () => void;
  onDelete: () => void;
}) {
  const latest = useRef(options);
  latest.current = options;

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (isEditing(event)) return;
      const { store, audio, sections } = latest.current;
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
      if (event.key === 'Delete') {
        event.preventDefault();
        latest.current.onDelete();
        return;
      }
      if (event.key === 'ArrowUp' || event.key === 'ArrowDown') {
        // 焦点在结构树、片段或前奏列表里时由它们自己移动。
        if (inCollection(event)) return;
        const order = sections.flatMap((s) => s.rows.map((r) => r.node.id));
        if (order.length === 0) return;
        event.preventDefault();
        const index = order.indexOf(store.state.selectedId ?? '');
        const next = index < 0 ? 0 : Math.max(0, Math.min(order.length - 1, index + (event.key === 'ArrowDown' ? 1 : -1)));
        store.select(order[next]!);
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, []);
}
