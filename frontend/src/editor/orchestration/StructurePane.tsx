// 编排页左栏（界面改版稿件 #orchestration）：上面是画面结构树，下面是歌词分配（来源、默认容器，片段或前奏列表）。

import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import type { LyricBranchSummary, OrchestrationClip, OrchestrationLeadIn, OrchestrationNode } from '../../protocol';
import { BRANCH_ROLE_TEXT } from '../model/branches';
import type { EditorLine } from '../model/lyrics';
import {
  clipName,
  containersOf,
  copyBlock,
  currentLayoutText,
  defaultFor,
  lineRange,
  lineRangeText,
  moveBlock,
  moveTargets,
  nodeName,
  nodeRole,
  quickLayoutBlock,
  removeBlock,
  reorderPlan,
  roleText,
  unassignedCount,
  type LineRange,
  type OrchestrationModel,
  type StructureSection,
} from '../model/orchestration';
import { defaultContainerBlock, SLOT_DEFAULT_NEEDS_KARAOKE } from '../model/orchestration-properties';
import { Button, IconButton } from '../ui/Button';
import { Segmented } from '../ui/choices';
import { List, Tree, type Key } from '../ui/collections';
import { Select } from '../ui/fields';
import { Icon, type IconName } from '../ui/Icon';
import { ProgressBar, Tag } from '../ui/indicators';
import { Menu, MenuItem, MenuSeparator, MenuTrigger } from '../ui/Menu';
import { Note, ReadError } from '../workspace/panel';
import { BAND_COLORS } from './clip-bands';
import { LeadInList, type LeadInActions } from './LeadInViews';
import type { OrchestrationState, OrchestrationStore } from './orchestration-store';
import { NO_PROGRAM } from './parts';

const NO_NODE = '先在结构树里选择一个节点。';

// ── 画面结构 ──

/** 选中节点上每个结构操作不能做的原因（W4 设计 §12.4）；undefined 表示可以做。 */
export interface NodeBlocks {
  copy: string | undefined;
  up: string | undefined;
  down: string | undefined;
  move: string | undefined;
  remove: string | undefined;
}

export const ALL_BLOCKED: NodeBlocks = { copy: NO_NODE, up: NO_NODE, down: NO_NODE, move: NO_NODE, remove: NO_NODE };

export function nodeBlocks(model: OrchestrationModel, id: string, candidates: readonly OrchestrationNode[], branchName: (id: string) => string | undefined): NodeBlocks {
  if (!model.byId.has(id)) return ALL_BLOCKED;
  const reorder = (delta: -1 | 1) => {
    const plan = reorderPlan(model, id, delta);
    return 'block' in plan ? plan.block : undefined;
  };
  const targets = moveTargets(model, id, candidates);
  return {
    copy: copyBlock(model, id),
    up: reorder(-1),
    down: reorder(1),
    move: moveBlock(model, id) ?? (targets.groups.length === 0 && !targets.canvas ? '没有可以移到的普通组：子树里有被来源引用的节点时，只能留在那个来源的根下。' : undefined),
    remove: removeBlock(model, id, branchName),
  };
}

/** 结构编辑（G20）：block 是整体不可用的原因（只读、后端没开放），blocks 给出选中节点上各操作的原因。 */
export interface StructureActions {
  block: string | undefined;
  busy: boolean;
  blocks: (id: string) => NodeBlocks;
  onNew: () => void;
  onCopy: (id: string) => Promise<void>;
  onReorder: (id: string, delta: -1 | 1) => Promise<void>;
  onMove: (id: string) => void;
  onRemove: (id: string) => Promise<void>;
  onRename: (id: string, name: string) => Promise<void>;
  onAddSlot: (containerId: string) => Promise<void>;
}

/** 结构树里的一个节点，子节点按树的顺序。 */
interface TreeNode {
  id: string;
  node: OrchestrationNode;
  children?: TreeNode[];
}

/** 一节的行（先序，带深度）还原成树。 */
function nest(rows: StructureSection['rows']): TreeNode[] {
  const roots: TreeNode[] = [];
  const stack: { depth: number; item: TreeNode }[] = [];
  for (const { node, depth } of rows) {
    const item: TreeNode = { id: node.id, node };
    while (stack.length > 0 && stack.at(-1)!.depth >= depth) stack.pop();
    const parent = stack.at(-1)?.item;
    if (parent) (parent.children ??= []).push(item);
    else roots.push(item);
    stack.push({ depth, item });
  }
  return roots;
}

/** 节点图标：组、自动布局、轮换、槽位、前奏提示组、标记（稿件的结构树）。 */
function nodeIcon(model: OrchestrationModel, node: OrchestrationNode): IconName {
  switch (nodeRole(model, node)) {
    case 'slot':
      return 'crop_16_9';
    case 'container':
      return node.layout?.kind === 'SlotSequence' ? 'autorenew' : 'crop_free';
    case 'content':
      return node.contentKind === 'countdownSequence' ? 'pending' : 'radio_button_unchecked';
    case 'group':
      return 'layers';
  }
}

export function StructurePane(props: {
  view: OrchestrationState;
  sections: StructureSection[];
  branches: LyricBranchSummary[];
  actions: StructureActions;
  onSelect: (id: string) => void;
  onRetry: () => void;
  /** 下面的歌词分配。 */
  assignment: ReactNode;
}) {
  const { view, actions } = props;
  const model = view.model;
  const id = view.selectedId;
  const blocks = id ? actions.blocks(id) : ALL_BLOCKED;
  const reason = (own: string | undefined) => actions.block ?? own;
  const run = (key: Key) => {
    if (!id) return;
    if (key === 'copy') void actions.onCopy(id);
    else if (key === 'up') void actions.onReorder(id, -1);
    else if (key === 'down') void actions.onReorder(id, 1);
    else if (key === 'move') actions.onMove(id);
    else if (key === 'remove') void actions.onRemove(id);
  };
  const item = (key: keyof NodeBlocks, label: string, icon: IconName, danger?: boolean) => {
    const why = reason(blocks[key]);
    return (
      <MenuItem id={key} icon={icon} disabled={!!why || actions.busy} {...(why ? { description: why } : {})} {...(danger ? { danger } : {})}>
        {label}
      </MenuItem>
    );
  };

  return (
    <div className="orch-left">
      <header className="pane-head">
        <h3>画面结构</h3>
        {view.status === 'loading' && model && <span className="faint">刷新中…</span>}
        <span className="spacer" />
        {model && (
          <>
            <IconButton icon="add" size="sm" label="新建…" isDisabled={!!actions.block || actions.busy} onPress={actions.onNew} />
            <IconButton icon="content_copy" size="sm" label="复制" isDisabled={!!reason(blocks.copy) || actions.busy} onPress={() => run('copy')} />
            <MenuTrigger>
              <IconButton icon="more_vert" size="sm" label="结构操作" />
              <Menu onAction={run} placement="bottom end">
                {item('copy', '复制', 'content_copy')}
                {item('up', '上移', 'arrow_upward')}
                {item('down', '下移', 'arrow_downward')}
                {item('move', '移到…', 'drive_file_move')}
                <MenuSeparator />
                {item('remove', '删除', 'delete', true)}
              </Menu>
            </MenuTrigger>
          </>
        )}
      </header>
      {model && actions.block && <Note className="pane-note">{actions.block}</Note>}
      {view.status === 'error' && <ReadError error={view.error} onRetry={props.onRetry} />}
      <div className="structure-scroll">
        {view.status === 'unsupported' ? (
          <Note className="pane-note">后端没有开放编排读模型。</Note>
        ) : !model ? (
          view.status !== 'error' && <ProgressBar label="正在读取编排结构" className="pane-progress" />
        ) : (
          <StructureTree model={model} view={view} sections={props.sections} branches={props.branches} onSelect={props.onSelect} />
        )}
      </div>
      {props.assignment}
    </div>
  );
}

function StructureTree(props: { model: OrchestrationModel; view: OrchestrationState; sections: StructureSection[]; branches: LyricBranchSummary[]; onSelect: (id: string) => void }) {
  const { model, view } = props;
  const items = useMemo(() => props.sections.map((s) => ({ section: s, items: nest(s.rows) })), [props.sections]);
  // 收起的节点；选中的节点（↑↓、画布点选、换来源时）所在的分支自动展开。
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(() => new Set());
  const parents = useMemo(() => {
    const ids: string[] = [];
    const walk = (nodes: readonly TreeNode[]) => nodes.forEach((n) => n.children && (ids.push(n.id), walk(n.children)));
    items.forEach((s) => walk(s.items));
    return ids;
  }, [items]);
  const selectedId = view.selectedId;
  useEffect(() => {
    if (!selectedId) return;
    const up: string[] = [];
    for (let parent = model.byId.get(selectedId)?.parentId; parent; parent = model.byId.get(parent)?.parentId) up.push(parent);
    setCollapsed((current) => (up.some((p) => current.has(p)) ? new Set([...current].filter((c) => !up.includes(c))) : current));
  }, [model, selectedId]);

  // 选中的节点滚进视野。
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!selectedId) return;
    requestAnimationFrame(() => box.current?.querySelector(`[data-node-id="${CSS.escape(selectedId)}"]`)?.scrollIntoView({ block: 'nearest' }));
  }, [selectedId]);

  // 选中片段、前奏或句子时，结构树高亮的是相关的节点；再点这一行回到节点本身。选择没变时 React Aria 不发事件，这里自己接。
  const related = !!(view.selectedClipId || view.selectedLeadInId || view.lineFocus);
  const reselect = (target: EventTarget) => {
    const el = target as HTMLElement;
    if (!related || el.closest('.caret')) return;
    const id = el.closest('.tree-row')?.querySelector<HTMLElement>('.node')?.dataset.nodeId;
    if (id && id === selectedId) props.onSelect(id);
  };

  const meta = (section: StructureSection) => {
    if (section.kind === 'orphans') return '不属于任何来源';
    if (section.kind === 'unlisted') return '来源列表里没有';
    const role = props.branches.find((b) => b.id === section.branchId)?.role;
    // 主来源通常就叫“主歌词”，与角色相同时不重复。
    return role && BRANCH_ROLE_TEXT[role] !== section.title ? BRANCH_ROLE_TEXT[role] : undefined;
  };

  return (
    <div ref={box} className="structure-tree" onClickCapture={(e) => reselect(e.target)} onKeyDownCapture={(e) => e.key === 'Enter' && reselect(e.target)}>
      <Tree<TreeNode>
        label="画面结构"
        sections={items.map(({ section, items: nodes }) => ({
          id: section.key,
          items: nodes,
          header: (
            <>
              <b className="sec-title">{section.title}</b>
              {meta(section) && <span>{meta(section)}</span>}
              {section.program && <span className="sec-layout">{currentLayoutText(model, section.program)}</span>}
              {section.kind === 'branch' && !section.program && <span className="sec-empty">{NO_PROGRAM}</span>}
            </>
          ),
        }))}
        textValue={(item) => nodeName(model, item.node)}
        selectedKeys={selectedId ? [selectedId] : []}
        onSelectionChange={(keys) => {
          const [key] = keys;
          if (key !== undefined && key !== selectedId) props.onSelect(String(key));
        }}
        expandedKeys={parents.filter((p) => !collapsed.has(p))}
        onExpandedChange={(keys) => setCollapsed(new Set(parents.filter((p) => !keys.has(p))))}
      >
        {(item) => <NodeLabel model={model} node={item.node} />}
      </Tree>
    </div>
  );
}

function NodeLabel(props: { model: OrchestrationModel; node: OrchestrationNode }) {
  const { model, node } = props;
  const isDefault = defaultFor(model, node.id).length > 0;
  const clipCount = model.programs.reduce((n, p) => n + (p.clips ?? []).filter((c) => c.containerId === node.id).length, 0);
  return (
    <span className="node" data-node-id={node.id}>
      <Icon name={nodeIcon(model, node)} />
      <span className="node-name">{nodeName(model, node)}</span>
      {isDefault && (
        <span className="node-badge" title="没有单独分配的歌词放在这里">
          默认
        </span>
      )}
      {clipCount > 0 && (
        <span className="node-badge" title={`${clipCount} 个片段以它为目标容器`}>
          片段 {clipCount}
        </span>
      )}
      <span className="role">{roleText(model, node)}</span>
    </span>
  );
}

// ── 歌词分配 ──

/** 片段的增删改（G19-B）：不可用时 block 是原因。 */
export interface ClipActions {
  block: string | undefined;
  busy: boolean;
  onNew: () => void;
  onRename: (clip: OrchestrationClip, name: string) => Promise<void>;
  onMove: (clip: OrchestrationClip, label: string, containerId: string) => Promise<void>;
  onDelete: (clip: OrchestrationClip, label: string) => Promise<void>;
}

/**
 * 歌词分配（稿件左栏下部）：来源、默认容器、当前布局与未单独分配的句数；选了句子时可以为它们新建片段；
 * 下面是片段或前奏列表（与时间轴的片段轨、前奏轨是同一份数据，联动选中）。
 */
export function AssignmentSection(props: {
  view: OrchestrationState;
  store: OrchestrationStore;
  branches: LyricBranchSummary[];
  /** 分配条来源的行。 */
  lines: readonly EditorLine[];
  range: LineRange | undefined;
  clips: readonly OrchestrationClip[];
  colors: ReadonlyMap<string, number>;
  leadIns: readonly OrchestrationLeadIn[];
  canSetDefault: boolean;
  clipActions: ClipActions;
  leadInActions: LeadInActions;
  onDefaultContainer: (containerId: string) => void;
}) {
  const { view, store, clipActions } = props;
  const model = view.model;
  const program = model?.programByBranch.get(view.branchId);
  const branch = props.branches.find((b) => b.id === view.branchId);
  const containers = model && program ? containersOf(model, program) : [];
  // 当前默认容器不在来源根下时（结构异常）也列出来，免得下拉框显示空白。
  const current = model && program ? model.byId.get(program.defaultContainerId) : undefined;
  const options = current && !containers.includes(current) ? [current, ...containers] : containers;
  const blocked = program ? quickLayoutBlock(branch, program) : undefined;
  const primary = branch?.role === 'primary';
  const slotDefaultBlocked = !!program && options.some((n) => n.id !== program.defaultContainerId && !!defaultContainerBlock(program, n, primary));
  const panel = view.panel ?? 'clips';

  const hint = !model
    ? undefined
    : !program
      ? NO_PROGRAM
      : blocked
        ? blocked
        : options.length < 2
          ? '这个来源只有一个歌词容器；“快速布局…”会新建容器。'
          : slotDefaultBlocked
            ? `没有单独分配的歌词放在默认容器里。${SLOT_DEFAULT_NEEDS_KARAOKE}`
            : '没有单独分配的歌词放在默认容器里。';

  return (
    <section className="sect assign" aria-label="歌词分配">
      <header>
        <h4>歌词分配</h4>
        <span className="spacer" />
        <Segmented
          label="歌词分配下面的列表"
          size="sm"
          value={panel}
          disabled={!program}
          options={[
            { value: 'clips', label: program ? `片段 ${program.clips?.length ?? 0}` : '片段' },
            { value: 'leadIns', label: program ? `前奏 ${props.leadIns.length}` : '前奏' },
          ]}
          onChange={(next) => store.showPanel(next)}
          className="assign-panels"
        />
      </header>
      <div className="kv">
        <Select
          label="来源"
          labelPlacement="side"
          size="sm"
          value={view.branchId}
          options={props.branches.map((b) => ({ value: b.id, label: b.name || b.id }))}
          onChange={(id) => store.setBranch(id)}
        />
        <Select
          label="默认容器"
          labelPlacement="side"
          size="sm"
          value={program?.defaultContainerId ?? ''}
          options={
            model && program
              ? options.map((n) => ({ value: n.id, label: `${nodeName(model, n)} · ${roleText(model, n)}`, disabled: n.id !== program.defaultContainerId && !!defaultContainerBlock(program, n, primary) }))
              : []
          }
          disabled={!props.canSetDefault || !program || !!view.busy || options.length < 2}
          onChange={props.onDefaultContainer}
        />
      </div>
      {model && program && (
        <p className="assign-meta">
          <span className="assign-layout">{currentLayoutText(model, program)}</span>
          <span className="assign-unassigned">未单独分配 {unassignedCount(program, props.lines)} 句</span>
        </p>
      )}
      {hint && <Note className="assign-hint">{hint}</Note>}
      {props.range && (
        <div className="assign-range">
          <Tag tone="accent">已选{lineRangeText(props.range)}</Tag>
          {!view.selectedClipId && (
            <Button variant="text" size="sm" isDisabled={!!clipActions.block || clipActions.busy} onPress={clipActions.onNew}>
              为这几句新建片段
            </Button>
          )}
          <span className="spacer" />
          <IconButton icon="close" size="sm" label="取消选中的句子" onPress={() => store.clearLines()} />
        </div>
      )}
      {model && program && panel === 'clips' && (
        <ClipList model={model} clips={props.clips} lines={props.lines} colors={props.colors} selectedId={view.selectedClipId} actions={clipActions} onSelect={(id) => store.selectClip(id)} />
      )}
      {model && program && panel === 'leadIns' && <LeadInList model={model} leadIns={props.leadIns} lines={props.lines} selectedId={view.selectedLeadInId} actions={props.leadInActions} onSelect={(id) => store.selectLeadIn(id)} />}
    </section>
  );
}

/** 片段列表（W4 设计 §10.3、§11.2）：与片段轨显示同一份数据，联动选中；上面可以新建片段。 */
function ClipList(props: {
  model: OrchestrationModel;
  clips: readonly OrchestrationClip[];
  lines: readonly EditorLine[];
  colors: ReadonlyMap<string, number>;
  selectedId: string | undefined;
  actions: ClipActions;
  onSelect: (id: string) => void;
}) {
  const { model, actions } = props;
  const items = props.clips.map((clip, i) => ({ id: clip.id, clip, name: clipName(clip, i + 1) }));
  return (
    <div className="assign-list clip-list">
      <div className="assign-tools">
        <Button variant="text" size="sm" icon="add" isDisabled={!!actions.block || actions.busy} onPress={actions.onNew}>
          新建片段
        </Button>
      </div>
      {actions.block && <Note>{actions.block}</Note>}
      <List
        label="片段列表"
        items={items}
        textValue={(item) => item.name}
        selectedKeys={props.selectedId ? [props.selectedId] : []}
        onSelectionChange={(keys) => {
          const [key] = keys;
          if (key !== undefined && key !== props.selectedId) props.onSelect(String(key));
        }}
        emptyText="这个来源还没有片段。在时间轴上选几句，点“为这几句新建片段”，或给这几句套快速布局。"
      >
        {({ clip, name }) => {
          const range = lineRange(props.lines, clip.lineIds);
          const container = model.byId.get(clip.containerId);
          return (
            <span className="clip" data-clip-id={clip.id}>
              <i className={`swatch band-${(props.colors.get(clip.containerId) ?? 0) % BAND_COLORS}`} aria-hidden />
              <span className="clip-text">
                <span className="clip-name">{name}</span>
                <span className="faint">
                  → {container ? nodeName(model, container) : `${clip.containerId}（不存在）`}
                  {clip.scheduleContext && <span title="从原来的一段拆出，保持原来的槽位和显示时钟"> · 保留原调度</span>}
                </span>
              </span>
              <span className="faint tnum">{range ? lineRangeText(range) : '句子不在来源里'}</span>
            </span>
          );
        }}
      </List>
    </div>
  );
}
