import { describe, expect, it } from 'vitest';
import { BackendError, ObservableValue, type MakoClient } from '../../src/client';
import {
  anchorText,
  basicLayoutFields,
  buildOrchestration,
  clipByLine,
  clipCreateBlock,
  clipEditBlock,
  clipFailureText,
  clipName,
  clipRangeBlock,
  CLIP_UNSUPPORTED,
  clipsOf,
  clipTimeSpan,
  containersOf,
  copyBlock,
  copyName,
  currentLayoutText,
  defaultClipContainer,
  defaultFor,
  extendLineSelection,
  insertionParent,
  isPlainGroup,
  LAYOUT_PRESETS,
  layoutFailureCodeText,
  layoutFailureText,
  layoutImpactText,
  layoutIsNoOp,
  lineRange,
  lineRangeText,
  linesBetween,
  moveBlock,
  moveTargets,
  nodeName,
  orderedChildren,
  normalizeLineSelection,
  quickLayoutBlock,
  QUICK_LAYOUT_UNSUPPORTED,
  nodePath,
  percent,
  programOfNode,
  referencesOf,
  removeBlock,
  reorderPlan,
  roleText,
  structureFailureText,
  structureSections,
  subtreeIds,
  unassignedCount,
} from '../../src/editor/model/orchestration';
import { LINE_PRESENTATION_READ, OrchestrationStore } from '../../src/editor/orchestration/orchestration-store';
import type { CommandOptions, EditorSession } from '../../src/editor/session/editor-session';
import type {
  BasicLayoutCommandResult,
  LeadInGenerationCommandResult,
  OrchestrationClip,
  OrchestrationLeadIn,
  OrchestrationNode,
  OrchestrationProgram,
  ProjectCommand,
  ProjectCommandResponse,
  ProjectLinePresentationResponse,
  ProjectOrchestrationResponse,
} from '../../src/protocol';
import { revision } from '../../src/protocol';
import { leadInFailureText } from '../../src/editor/model/orchestration-lead-ins';
import {
  clipMembersFailureText,
  defaultSplitPoint,
  mergedRangeText,
  mergeNeighbour,
  sameScheduleContext,
  scheduleContextNote,
  splitBlock,
  splitParts,
  splitPoints,
  splitResultText,
} from '../../src/editor/model/orchestration-clip-members';

const placement = { x: 0.5, y: 0.5, anchor: 'Center', rotationDegrees: 0, scale: 1 };
const rect = { widthRatio: 1, heightRatio: 1 };
const node = (id: string, extra: Partial<OrchestrationNode> = {}): OrchestrationNode => ({ id, placement, referenceRect: rect, ...extra });
const layout = (kind: string, slotIds: string[] = []) => ({ kind, safeArea: { left: 0, top: 0, right: 1, bottom: 1 }, maxWidthRatio: 0.9, slotIds });

/** 主歌词：画面 → 常规（自动）＋ 双行（轮换·2：上行、槽位 2）；另有一个父级缺失的孤立容器和一对互为父级的节点。 */
function response(rev = '3'): ProjectOrchestrationResponse {
  return {
    projectSessionId: 'p1',
    projectRevision: revision(rev),
    nodes: [
      node('branch/primary/display'),
      node('branch/primary/display/regular', { parentId: 'branch/primary/display', layout: layout('Auto') }),
      node('node/double', { name: '双行', parentId: 'branch/primary/display', layout: layout('SlotSequence', ['node/s1', 'node/s2']) }),
      node('node/s1', { name: '上行', parentId: 'node/double' }),
      node('node/s2', { parentId: 'node/double' }),
      node('node/lost', { parentId: 'node/gone', layout: layout('Auto') }),
      node('node/a', { parentId: 'node/b' }),
      node('node/b', { parentId: 'node/a', contentKind: 'Image' }),
    ],
    programs: [{ id: 'program/primary', branchId: 'primary', rootContainerId: 'branch/primary/display', defaultContainerId: 'node/double' }],
    protocolVersion: '1',
  };
}

describe('编排模型', () => {
  it('树、角色、名称、路径：槽位按轮换顺序编号，孤立节点单列（含成环的），默认容器选项只取来源根下的容器', () => {
    const model = buildOrchestration(response());
    const by = (id: string) => model.byId.get(id)!;
    expect(model.children.get('branch/primary/display')!.map((n) => n.id)).toEqual(['branch/primary/display/regular', 'node/double']);
    expect(['branch/primary/display', 'branch/primary/display/regular', 'node/double', 'node/s1', 'node/b'].map((id) => roleText(model, by(id)))).toEqual([
      '组',
      '自动布局',
      '轮换·2',
      '槽位',
      'Image',
    ]);
    expect(['branch/primary/display', 'branch/primary/display/regular', 'node/double', 'node/s1', 'node/s2', 'node/lost'].map((id) => nodeName(model, by(id)))).toEqual([
      '画面',
      // 不是默认容器了，按固定 ID 仍叫“常规”。
      '常规',
      '双行',
      '上行',
      '槽位 2',
      '未命名容器',
    ]);
    expect(model.orphans.map((n) => n.id)).toEqual(['node/lost', 'node/a']);
    expect(nodePath(model, 'node/s2', (id) => (id === 'primary' ? '主歌词' : undefined))).toEqual(['主歌词', '画面', '双行', '槽位 2']);
    expect(nodePath(model, 'node/lost', () => undefined)).toEqual(['其他节点', '未命名容器']);
    expect(programOfNode(model, 'node/s1')?.branchId).toBe('primary');
    expect(containersOf(model, model.programs[0]!).map((n) => n.id)).toEqual(['branch/primary/display/regular', 'node/double']);
    expect(defaultFor(model, 'node/double').map((p) => p.branchId)).toEqual(['primary']);
  });

  it('未知枚举原样显示；默认容器没有名称时叫“常规”', () => {
    const r = response();
    r.programs[0]!.defaultContainerId = 'branch/primary/display/regular';
    r.nodes[2] = node('node/double', { parentId: 'branch/primary/display', layout: layout('Future') });
    const model = buildOrchestration(r);
    expect(nodeName(model, model.byId.get('branch/primary/display/regular')!)).toBe('常规');
    expect(roleText(model, model.byId.get('node/double')!)).toBe('Future');
    expect([anchorText('LeftCenter'), anchorText('Somewhere'), percent(0.055), percent(0.9)]).toEqual(['左侧中点', 'Somewhere', '5.5%', '90%']);
  });

  it('结构树分节：按来源顺序；没有程序的来源是空节（R4）；来源列表里没有的程序另列；其他节点在最后，每个节点只出现一次', () => {
    const r = response();
    r.programs.push({ id: 'program/ghost', branchId: 'ghost', rootContainerId: 'node/a', defaultContainerId: 'node/a' });
    const sections = structureSections(buildOrchestration(r), [
      { id: 'tr', name: '翻译' },
      { id: 'primary', name: '' },
    ]);
    expect(sections.map((s) => [s.kind, s.title, s.rows.map((row) => `${row.depth}:${row.node.id}`)])).toEqual([
      ['branch', '翻译', []],
      ['branch', 'primary', ['0:branch/primary/display', '1:branch/primary/display/regular', '1:node/double', '2:node/s1', '2:node/s2']],
      ['unlisted', 'ghost', ['0:node/a', '1:node/b']],
      ['orphans', '其他节点', ['0:node/lost']],
    ]);
    expect(sections[0]!.program).toBeUndefined();
  });
});

/** 只有编排状态用到的会话成员；读取按 reads 的设定返回或 409。 */
function setup() {
  const state$ = new ObservableValue({
    summary: { projectRevision: '3' },
    branchId: 'primary',
    branches: [{ id: 'primary' }, { id: 'tr' }],
    linesByBranch: { primary: LINES } as Record<string, readonly { id: string; startUs?: number; endUs?: number }[]>,
    notice: undefined as { message: string } | undefined,
  });
  const commands: ProjectCommand[] = [];
  const reads: string[] = [];
  const control: {
    conflict: boolean;
    changed: boolean;
    defaultId?: string;
    clips?: OrchestrationClip[];
    preview?: (command: ProjectCommand) => Promise<unknown>;
    /** 命令被拒绝时的 failureCode：会话按命令自己的对照表写进 notice。 */
    reject?: string;
    /** 命令响应里额外的字段（structureResult 等）。 */
    result?: Partial<ProjectCommandResponse>;
    /** 读模型额外的节点（新建、复制出来的）。 */
    nodes?: OrchestrationNode[];
    leadIns?: OrchestrationLeadIn[];
    /** 单句展示的读取：给出时按它返回或抛错，否则返回没有设置的单句展示。 */
    presentation?: (lineId: string, revision: string) => Promise<ProjectLinePresentationResponse>;
  } = {
    conflict: false,
    changed: true,
  };
  const previews: ProjectCommand[] = [];
  const presentations: string[] = [];
  const session = {
    state$,
    get state() {
      return state$.get();
    },
    projectSessionId: 'p1',
    get latestRevision() {
      return state$.get().summary.projectRevision;
    },
    supports: (feature: string): boolean => feature === 'project-sessions.orchestration.read' || feature === LINE_PRESENTATION_READ,
    linesOf: (branchId: string) => state$.get().linesByBranch[branchId] ?? [],
    async command(command: ProjectCommand, options?: CommandOptions) {
      commands.push(command);
      if (control.reject) {
        state$.set({ ...state$.get(), notice: { message: `操作被拒绝：${options?.failureText?.(control.reject) ?? control.reject}` } });
        return undefined;
      }
      if (!control.changed) return { changed: false };
      const projectRevision = String(Number(state$.get().summary.projectRevision) + 1);
      state$.set({ ...state$.get(), summary: { projectRevision } });
      return { changed: true, projectRevision, ...control.result };
    },
  };
  const client = {
    projects: {
      async orchestration(_id: string, expectedRevision: string) {
        reads.push(expectedRevision);
        if (control.conflict) throw new BackendError(409, { code: 'revisionConflict', message: '修订冲突', protocolVersion: '1' });
        const result = response(expectedRevision);
        if (control.defaultId) result.programs[0]!.defaultContainerId = control.defaultId;
        if (control.clips) result.programs[0]!.clips = control.clips;
        if (control.nodes) result.nodes.push(...control.nodes);
        if (control.leadIns) result.programs[0]!.leadIns = control.leadIns;
        return result;
      },
      async linePresentation(_id: string, expectedRevision: string, branchId: string, lineId: string) {
        presentations.push(`${expectedRevision}:${branchId}:${lineId}`);
        if (control.presentation) return control.presentation(lineId, expectedRevision);
        return presentation(lineId, expectedRevision);
      },
      async command(_id: string, command: ProjectCommand) {
        previews.push(command);
        return control.preview!(command);
      },
    },
  } as unknown as MakoClient;
  const store = new OrchestrationStore(client, session as unknown as EditorSession, { randomId: () => 'fixed' });
  return { store, session, state$, commands, reads, control, previews, presentations };
}

function presentation(lineId: string, rev: string, extra: Partial<ProjectLinePresentationResponse> = {}): ProjectLinePresentationResponse {
  return {
    projectSessionId: 'p1',
    projectRevision: revision(rev),
    branchId: 'primary',
    lineId,
    localTransform: null,
    localEnterAnimation: null,
    effectiveEnterAnimation: null,
    animationSource: 'none',
    protocolVersion: '1',
    ...extra,
  };
}

const tick = () => new Promise((r) => setTimeout(r, 0));

/** 主歌词五句：第 3 句没有时间，第 5 句的时间排在第 1、2 句之间。 */
const LINES = [
  { id: 'l1', startUs: 0, endUs: 1_000_000 },
  { id: 'l2', startUs: 2_000_000, endUs: 3_000_000 },
  { id: 'l3' },
  { id: 'l4', startUs: 4_000_000, endUs: 5_000_000 },
  { id: 'l5', startUs: 1_200_000, endUs: 1_800_000 },
];

describe('编排状态', () => {
  it('按最新修订读取，默认选中当前来源的默认容器；409 不提示，等下一个修订；选中的节点跨刷新保留', async () => {
    const { store, state$, reads, control } = setup();
    await tick();
    expect(reads).toEqual(['3']);
    expect(store.state).toMatchObject({ status: 'ready', selectedId: 'node/double' });
    store.setBranch('tr');
    expect(store.state).toMatchObject({ branchId: 'tr', selectedId: 'node/double' });
    store.select('node/s1');
    expect(store.state.branchId).toBe('primary');
    store.select('node/lost');
    expect(store.state).toMatchObject({ branchId: 'primary', selectedId: 'node/lost' });
    store.select('node/s1');

    control.conflict = true;
    state$.set({ ...state$.get(), summary: { projectRevision: '4' } });
    await tick();
    expect(store.state.status).toBe('loading');
    expect(store.state.error).toBeUndefined();
    expect(store.state.model?.revision).toBe('3');

    control.conflict = false;
    state$.set({ ...state$.get(), summary: { projectRevision: '5' } });
    await tick();
    expect(reads).toEqual(['3', '4', '5']);
    expect(store.state).toMatchObject({ status: 'ready', selectedId: 'node/s1' });
    store.dispose();
  });

  it('设置默认容器：一条命令；no-op 返回 unchanged；没有读模型能力时不读取', async () => {
    const { store, commands, control } = setup();
    await tick();
    expect(await store.setDefaultContainer('primary', 'branch/primary/display/regular')).toBe('applied');
    expect(commands).toEqual([{ type: 'orchestration.setDefaultContainer', branchId: 'primary', containerId: 'branch/primary/display/regular' }]);
    control.changed = false;
    expect(await store.setDefaultContainer('primary', 'branch/primary/display/regular')).toBe('unchanged');
    expect(store.state.busy).toBeUndefined();
    store.dispose();

    const unsupported = setup();
    unsupported.session.supports = () => false;
    const other = new OrchestrationStore({} as MakoClient, unsupported.session as unknown as EditorSession);
    expect(other.state.status).toBe('unsupported');
    other.dispose();
    unsupported.store.dispose();
  });
});

describe('快速布局（G18）', () => {
  const result = (extra: Partial<BasicLayoutCommandResult> = {}): BasicLayoutCommandResult => ({
    branchId: 'primary',
    preset: 'DoubleCentered',
    scope: 'source',
    recognizedPreset: 'DoubleCentered',
    affectedLines: 12,
    existingClips: 0,
    preservedNodes: 0,
    splitClips: 0,
    changed: false,
    ...extra,
  });
  const double = LAYOUT_PRESETS.find((p) => p.preset === 'DoubleCentered')!;

  it('命令字段、当前布局、预演文字与失败原因', () => {
    expect(LAYOUT_PRESETS.map((p) => [p.label, p.slots])).toEqual([
      ['单行居中', 1],
      ['卡拉 OK 双行', 2],
      ['双行居中', 2],
      ['三行居中', 3],
    ]);
    expect(basicLayoutFields('primary', LAYOUT_PRESETS[3]!)).toEqual({
      branchId: 'primary',
      preset: 'TripleCentered',
      layoutName: '三行居中',
      slotNames: ['槽位 1', '槽位 2', '槽位 3'],
      selectedSegmentName: '歌词段布局',
      retainedSegmentName: '保留原布局的歌词段',
    });

    const model = buildOrchestration(response());
    const program = model.programs[0]!;
    expect(currentLayoutText(model, { ...program, recognizedLayoutPreset: 'KaraokeDouble' })).toBe('布局：卡拉 OK 双行');
    expect(currentLayoutText(model, { ...program, defaultContainerId: 'branch/primary/display/regular' })).toBe('布局：自动布局');
    expect(currentLayoutText(model, program)).toBe('布局：自定义');

    expect(layoutImpactText(result(), double, false)).toBe('将影响 12 句；新建“双行居中”容器和 2 个槽位，并设为默认容器；原有容器保留。');
    expect(layoutImpactText(result({ existingClips: 3, splitClips: 1 }), double, false)).toContain('涉及 3 个已有片段，其中 1 个需要拆分。');
    expect(layoutImpactText(result(), double, true)).toBe('已经是这个布局，应用不会有变化。');

    expect(layoutFailureText('RecipeConflict', '冲突', '双行居中')).toMatch(/^预设“双行居中”包含无法安全拆分.*（冲突）$/);
    expect(layoutFailureText('Whatever', '后端消息', '双行居中')).toBe('后端消息');
    expect(layoutFailureText(undefined, '后端消息', '双行居中')).toBe('后端消息');
  });

  it('预演经客户端直接发送、只保留最新、失败写明原因；应用经会话一条历史，读到新修订后选中新的默认容器', async () => {
    const { store, commands, previews, control } = setup();
    await tick();
    const pending: { resolve: (value: unknown) => void; reject: (error: unknown) => void }[] = [];
    control.preview = () => new Promise((resolve, reject) => pending.push({ resolve, reject }));

    void store.previewLayout('primary', LAYOUT_PRESETS[0]!);
    void store.previewLayout('primary', double);
    expect(store.state.layoutPreview).toMatchObject({ branchId: 'primary', preset: 'DoubleCentered', status: 'loading', revision: '3' });
    expect(previews.map((c) => c.type)).toEqual(['orchestration.previewBasicLayout', 'orchestration.previewBasicLayout']);
    expect(previews[1]).toMatchObject({ preset: 'DoubleCentered', slotNames: ['槽位 1', '槽位 2'] });
    pending[1]!.resolve({ changed: false, basicLayoutResult: result() });
    pending[0]!.resolve({ changed: false, basicLayoutResult: result({ preset: 'SingleCentered' }) });
    await tick();
    expect(store.state.layoutPreview).toMatchObject({ preset: 'DoubleCentered', status: 'ready', result: { affectedLines: 12 } });
    expect(commands).toEqual([]);

    void store.previewLayout('tr', LAYOUT_PRESETS[0]!);
    pending[2]!.reject(new BackendError(422, { code: 'commandRejected', message: '快速布局未通过工程领域校验。', failureCode: 'PresentationInvalid', protocolVersion: '1' }));
    await tick();
    expect(store.state.layoutPreview).toMatchObject({ branchId: 'tr', status: 'error', error: expect.stringContaining('不能套用快速布局') });
    store.endLayoutPreview();
    expect(store.state.layoutPreview).toBeUndefined();

    store.select('node/s1');
    control.defaultId = 'node/triple';
    expect(await store.applyLayout('primary', double)).toBe('applied');
    expect(commands).toEqual([{ type: 'orchestration.applyBasicLayout', ...basicLayoutFields('primary', double) }]);
    await tick();
    expect(store.state).toMatchObject({ branchId: 'primary', selectedId: 'node/triple' });

    control.changed = false;
    expect(await store.applyLayout('primary', double)).toBe('unchanged');
    store.dispose();
  });
});

describe('片段与范围（G19）', () => {
  const clips: OrchestrationClip[] = [
    { id: 'clip/b', lineIds: ['l4'], containerId: 'branch/primary/display/regular', scheduleContext: { lineIds: ['l4', 'l5'], isDefaultSegment: true } },
    { id: 'clip/a', name: '歌词段布局', lineIds: ['l2', 'l3'], containerId: 'node/double' },
    { id: 'clip/c', lineIds: ['gone'], containerId: 'node/double' },
  ];
  const program = (extra: Partial<OrchestrationProgram> = {}): OrchestrationProgram => ({
    id: 'program/primary',
    branchId: 'primary',
    rootContainerId: 'branch/primary/display',
    defaultContainerId: 'node/double',
    clips,
    ...extra,
  });
  const single = LAYOUT_PRESETS[0]!;

  it('选中的句子：范围与文字、扩展、框选时补上夹在中间的无时间句子，有时间的不补', () => {
    expect(lineRange(LINES, ['l4', 'l2', 'x'])).toEqual({ lineIds: ['l2', 'l4'], first: 2, last: 4, count: 2, contiguous: false });
    expect(lineRange(LINES, ['x'])).toBeUndefined();
    expect(lineRangeText(lineRange(LINES, ['l3'])!)).toBe('第 3 句');
    expect(lineRangeText(lineRange(LINES, ['l2', 'l3', 'l4'])!)).toBe('第 2～4 句');
    expect(lineRangeText(lineRange(LINES, ['l2', 'l4'])!)).toBe('第 2～4 句中的 2 句');

    expect(extendLineSelection(LINES, 'l4', 'l2')).toEqual(['l2', 'l3', 'l4']);
    expect(extendLineSelection(LINES, undefined, 'l2')).toEqual(['l2']);
    expect(extendLineSelection(LINES, 'gone', 'l2')).toEqual(['l2']);
    expect(extendLineSelection(LINES, 'l1', 'gone')).toEqual([]);

    expect(normalizeLineSelection(LINES, ['l4', 'l2'])).toEqual(['l2', 'l3', 'l4']);
    // 第 5 句的时间在第 1、2 句之间：框选第 1、5 句时第 2、4 句不补，结果不连续。
    expect(normalizeLineSelection(LINES, ['l5', 'l1'])).toEqual(['l1', 'l3', 'l5']);
    expect(lineRange(LINES, normalizeLineSelection(LINES, ['l5', 'l1']))!.contiguous).toBe(false);
    expect(normalizeLineSelection(LINES, ['gone'])).toEqual([]);
  });

  it('片段：按第一句排序、缺省名称、句子归属、未单独分配句数、时间范围', () => {
    const sorted = clipsOf(program(), LINES);
    expect(sorted.map((c) => c.id)).toEqual(['clip/a', 'clip/b', 'clip/c']);
    expect(sorted.map((c, i) => clipName(c, i + 1))).toEqual(['歌词段布局', '片段 2', '片段 3']);
    expect([...clipByLine(program()).keys()]).toEqual(['l4', 'l2', 'l3', 'gone']);
    expect(unassignedCount(program(), LINES)).toBe(2);
    expect(unassignedCount(program({ clips: [] }), LINES)).toBe(5);
    expect(unassignedCount(undefined, LINES)).toBe(5);
    expect(clipsOf(undefined, LINES)).toEqual([]);
    expect(clipTimeSpan(clips[1]!, LINES)).toEqual({ startUs: 2_000_000, endUs: 3_000_000 });
    expect(clipTimeSpan(clips[2]!, LINES)).toBeUndefined();
  });

  it('快速布局：不支持的来源、范围字段、确定不会变化的情况、范围预演文字', () => {
    expect(quickLayoutBlock({ timingMode: 'followPrimary' }, program())).toBe(QUICK_LAYOUT_UNSUPPORTED);
    expect(quickLayoutBlock({ timingMode: 'independent' }, program({ animationPolicy: 'Static' }))).toBe(QUICK_LAYOUT_UNSUPPORTED);
    expect(quickLayoutBlock({ timingMode: 'independent' }, program({ animationPolicy: 'Karaoke' }))).toBeUndefined();
    // 动画没有设置时不猜，交给后端。
    expect(quickLayoutBlock({ timingMode: 'independent' }, program())).toBeUndefined();
    expect(layoutFailureText('UnsupportedSource', '后端原因', '单行居中')).toBe(`${QUICK_LAYOUT_UNSUPPORTED}（后端原因）`);

    expect(basicLayoutFields('primary', single, ['l1', 'l2'])).toMatchObject({ selectedLineIds: ['l1', 'l2'], selectedSegmentName: '歌词段布局' });
    expect(basicLayoutFields('primary', single, [])).not.toHaveProperty('selectedLineIds');

    const recognized = program({ recognizedLayoutPreset: 'SingleCentered' });
    expect(layoutIsNoOp(recognized, single)).toBe(true);
    expect(layoutIsNoOp(recognized, single, ['l1'])).toBe(true);
    expect(layoutIsNoOp(recognized, single, ['l1', 'l2'])).toBe(false);
    expect(layoutIsNoOp(program(), single, ['l1'])).toBe(false);

    const result: BasicLayoutCommandResult = { branchId: 'primary', preset: 'SingleCentered', scope: 'range', affectedLines: 2, existingClips: 1, preservedNodes: 0, splitClips: 1, changed: false };
    expect(layoutImpactText(result, single, false, lineRange(LINES, ['l1', 'l2']))).toBe(
      '将影响第 1～2 句（2 句）；新建“单行居中”容器和 1 个槽位，只接这几句（新片段“歌词段布局”）；默认容器不变，其余句子保持原来的调度。涉及 1 个已有片段，其中 1 个需要拆分。',
    );
    expect(layoutImpactText({ ...result, existingClips: 0 }, single, false, lineRange(LINES, ['l3']))).toContain('将影响第 3 句；新建“单行居中”容器和 1 个槽位，只接这一句');
  });

  it('状态：点选、扩展、框选加选、选中片段（结构树跟着选目标容器）、换来源清空、歌词变了去掉不在的句子', async () => {
    const { store, state$, control } = setup();
    control.clips = clips;
    store.reload();
    await tick();
    store.clickLine('l2', false);
    expect(store.state).toMatchObject({ lineIds: ['l2'], lineAnchor: 'l2' });
    store.clickLine('l4', true);
    expect(store.state).toMatchObject({ lineIds: ['l2', 'l3', 'l4'], lineAnchor: 'l2' });
    store.selectLines(['l1'], true);
    expect(store.state).toMatchObject({ lineIds: ['l1', 'l2', 'l3', 'l4'], lineAnchor: 'l2' });
    store.selectLines(['l5', 'l1'], false);
    expect(store.state).toMatchObject({ lineIds: ['l1', 'l3', 'l5'], lineAnchor: 'l1' });

    store.selectClip('clip/a');
    expect(store.state).toMatchObject({ selectedClipId: 'clip/a', selectedId: 'node/double', lineIds: ['l2', 'l3'], lineAnchor: 'l2' });
    store.select('branch/primary/display');
    expect(store.state.selectedClipId).toBeUndefined();
    expect(store.state.lineIds).toEqual(['l2', 'l3']);
    store.selectClip('clip/a');
    store.clickLine('l1', false);
    expect(store.state.selectedClipId).toBeUndefined();

    state$.set({ ...state$.get(), linesByBranch: { primary: LINES.filter((l) => l.id !== 'l1') } });
    expect(store.state.lineIds).toBeUndefined();
    store.clickLine('l2', false);
    store.setBranch('tr');
    expect(store.state).toMatchObject({ branchId: 'tr' });
    expect(store.state.lineIds).toBeUndefined();
    store.clickLine('l2', false);
    expect(store.state.lineIds).toBeUndefined();
    store.dispose();
  });

  it('范围预演带上 selectedLineIds；范围应用后选中包含第一句的片段和它的目标容器；片段不在了取消片段选中', async () => {
    const { store, commands, previews, control } = setup();
    await tick();
    control.preview = async () => ({ changed: false, basicLayoutResult: { branchId: 'primary', preset: 'SingleCentered', scope: 'range', affectedLines: 2, existingClips: 0, preservedNodes: 0, splitClips: 0, changed: false } });
    await store.previewLayout('primary', single, ['l1', 'l2']);
    expect(previews[0]).toMatchObject({ type: 'orchestration.previewBasicLayout', selectedLineIds: ['l1', 'l2'] });
    expect(store.state.layoutPreview).toMatchObject({ status: 'ready', lineIds: ['l1', 'l2'] });
    await store.previewLayout('primary', single);
    expect(previews[1]).not.toHaveProperty('selectedLineIds');
    expect(store.state.layoutPreview).not.toHaveProperty('lineIds');

    store.clickLine('l1', false);
    store.clickLine('l2', true);
    control.clips = [{ id: 'clip/new', name: '歌词段布局', lineIds: ['l1', 'l2'], containerId: 'node/single' }];
    expect(await store.applyLayout('primary', single, ['l1', 'l2'])).toBe('applied');
    expect(commands[0]).toMatchObject({ type: 'orchestration.applyBasicLayout', selectedLineIds: ['l1', 'l2'] });
    await tick();
    expect(store.state).toMatchObject({ selectedClipId: 'clip/new', selectedId: 'node/single', lineIds: ['l1', 'l2'] });

    control.clips = [];
    control.defaultId = 'node/single';
    store.reload();
    await tick();
    expect(store.state.selectedClipId).toBeUndefined();
    expect(store.state.lineIds).toEqual(['l1', 'l2']);
    store.dispose();
  });
});

describe('片段编辑（G19-B）', () => {
  const recordedClip: OrchestrationClip = { id: 'clip/a', name: '副歌', lineIds: ['l1'], containerId: 'node/double' };
  const withClips = (clips: OrchestrationClip[] = []): OrchestrationProgram => ({ ...response().programs[0]!, clips });

  it('新建前的检查：按顺序给出第一条原因；句号范围；默认目标容器；不支持的来源；失败原因', () => {
    const program = withClips();
    expect(clipCreateBlock(program, LINES, [])).toBe('请选择这个来源里的句子。');
    expect(clipCreateBlock(program, LINES, ['l1', 'x'])).toBe('请选择这个来源里的句子。');
    expect(clipCreateBlock(program, LINES, ['l1', 'l3'])).toBe('片段需要连续的歌词段。');
    expect(clipCreateBlock(program, LINES, ['l2', 'l3'])).toBe('第 3 句没有时间；片段只能包含有时间的句子。');
    expect(clipCreateBlock(withClips([recordedClip]), LINES, ['l1'])).toBe('第 1 句已在片段“副歌”里；每句只能属于一个片段，先把那个片段缩小或删除。');
    expect(clipCreateBlock(program, LINES, ['l1', 'l2'])).toBe('第 5 句（片段外）的时间夹在这几句中间；片段的时间跨度里不能有别的句子。');
    expect(clipCreateBlock(program, LINES, ['l4', 'l5'])).toContain('时间顺序与来源顺序不一致');
    expect(clipCreateBlock(program, LINES, ['l4'])).toBeUndefined();

    expect(linesBetween(LINES, 2, 3)).toEqual(['l2', 'l3']);
    expect([linesBetween(LINES, 0, 1), linesBetween(LINES, 3, 2), linesBetween(LINES, 1, 6), linesBetween(LINES, 1.5, 2)]).toEqual([undefined, undefined, undefined, undefined]);

    const model = buildOrchestration(response());
    expect(defaultClipContainer(model, model.programs[0]!)).toBe('branch/primary/display/regular');
    const r = response();
    r.programs[0]!.defaultContainerId = 'branch/primary/display/regular';
    const other = buildOrchestration(r);
    expect(defaultClipContainer(other, other.programs[0]!)).toBe('node/double');

    expect(clipEditBlock({ timingMode: 'followPrimary' }, program)).toBe(CLIP_UNSUPPORTED);
    expect(clipEditBlock({ timingMode: 'independent' }, program)).toBeUndefined();
    expect(clipFailureText('ClipScheduleConflict')).toContain('每句只能属于一个片段');
    expect(clipFailureText('Unknown')).toBeUndefined();
    expect(layoutFailureCodeText('UnsupportedSource', '单行居中')).toBe(QUICK_LAYOUT_UNSUPPORTED);
  });

  it('新建：命令字段（名称去掉空白，空白不发）；读到新修订后选中新片段、它的句子和目标容器；被拒绝时返回片段的原因', async () => {
    const { store, commands, control } = setup();
    await tick();
    control.clips = [{ id: 'clip/fixed', name: '尾声', lineIds: ['l4'], containerId: 'branch/primary/display/regular' }];
    expect(await store.createClip('primary', { lineIds: ['l4'], containerId: 'branch/primary/display/regular', name: ' 尾声 ' })).toEqual({ result: 'applied' });
    expect(commands[0]).toEqual({ type: 'orchestration.createClip', branchId: 'primary', clipId: 'clip/fixed', lineIds: ['l4'], containerId: 'branch/primary/display/regular', name: '尾声' });
    await tick();
    expect(store.state).toMatchObject({ selectedClipId: 'clip/fixed', selectedId: 'branch/primary/display/regular', lineIds: ['l4'], lineAnchor: 'l4' });

    await store.createClip('primary', { lineIds: ['l4'], containerId: 'node/double', name: '  ' });
    expect(commands[1]).not.toHaveProperty('name');
    control.reject = 'ClipScheduleConflict';
    const failed = await store.createClip('primary', { lineIds: ['l1'], containerId: 'node/double' });
    expect(failed).toEqual({ result: 'failed', error: `操作被拒绝：${clipFailureText('ClipScheduleConflict')}` });
    expect(store.state.busy).toBeUndefined();
    store.dispose();
  });

  it('改名（空白清除名称）、改目标容器（结构树跟着选中）、删除（取消片段选中，句子保留，选中默认容器）；no-op 与失败', async () => {
    const { store, commands, control } = setup();
    control.clips = [recordedClip];
    store.reload();
    await tick();
    store.selectClip('clip/a');
    expect(await store.renameClip('primary', 'clip/a', '  ')).toBe('applied');
    expect(commands[0]).toEqual({ type: 'orchestration.renameClip', branchId: 'primary', clipId: 'clip/a', name: null });
    await store.renameClip('primary', 'clip/a', ' 主歌 ');
    expect(commands[1]).toMatchObject({ name: '主歌' });

    expect(await store.setClipContainer('primary', 'clip/a', 'branch/primary/display/regular')).toBe('applied');
    expect(commands[2]).toEqual({ type: 'orchestration.setClipContainer', branchId: 'primary', clipId: 'clip/a', containerId: 'branch/primary/display/regular' });
    expect(store.state).toMatchObject({ selectedClipId: 'clip/a', selectedId: 'branch/primary/display/regular' });

    control.changed = false;
    expect(await store.renameClip('primary', 'clip/a', '副歌')).toBe('unchanged');
    control.changed = true;
    control.reject = 'TargetStale';
    expect(await store.deleteClip('primary', 'clip/a')).toBe('failed');
    expect(store.state.selectedClipId).toBe('clip/a');
    delete control.reject;
    expect(await store.deleteClip('primary', 'clip/a')).toBe('applied');
    expect(commands.at(-1)).toEqual({ type: 'orchestration.deleteClip', branchId: 'primary', clipId: 'clip/a' });
    expect(store.state).toMatchObject({ selectedId: 'node/double', lineIds: ['l1'] });
    expect(store.state.selectedClipId).toBeUndefined();
    store.dispose();
  });

  it('快速布局应用被拒绝时，会话用快速布局自己的原因', async () => {
    const { store, session, control } = setup();
    await tick();
    control.reject = 'UnsupportedSource';
    expect(await store.applyLayout('primary', LAYOUT_PRESETS[0]!)).toBe('failed');
    expect(session.state.notice?.message).toBe(`操作被拒绝：${QUICK_LAYOUT_UNSUPPORTED}`);
    store.dispose();
  });
});

describe('结构编辑（G20）', () => {
  /**
   * 在 response() 上加：普通组“角落”（下有片段的目标容器和只有一个槽位的轮换），前奏提示组（两个标记，按标记顺序 m2、m1），
   * 两个画布根层的普通组；轮换“双行”的槽位顺序改成 s2、s1。
   */
  function scene(): ProjectOrchestrationResponse {
    const r = response();
    r.nodes[2] = node('node/double', { name: '双行', parentId: 'branch/primary/display', layout: layout('SlotSequence', ['node/s2', 'node/s1']) });
    r.nodes.push(
      node('node/g', { name: '角落', parentId: 'branch/primary/display' }),
      node('node/c', { parentId: 'node/g', layout: layout('Auto') }),
      node('node/one', { parentId: 'node/g', layout: layout('SlotSequence', ['node/only']) }),
      node('node/only', { parentId: 'node/one' }),
      node('node/cd', { parentId: 'branch/primary/display', contentKind: 'countdownSequence', content: { markNodeIds: ['node/m2', 'node/m1'] } }),
      node('node/m1', { parentId: 'node/cd', contentKind: 'textMark', content: { text: '●' } }),
      node('node/m2', { parentId: 'node/cd', contentKind: 'textMark', content: { text: '●' } }),
      node('node/free'),
      node('node/free2'),
    );
    r.programs[0]!.clips = [{ id: 'clip/a', lineIds: ['l1'], containerId: 'node/c' }];
    r.programs[0]!.leadIns = [{ id: 'lead/a', enabled: true, targetLineId: 'l1', indicatorContainerId: 'node/cd', durationUs: '3000000', origin: 'Manual' }];
    return r;
  }
  const branchName = (id: string) => (id === 'primary' ? '主歌词' : undefined);

  it('结构树：槽位按轮换顺序、标记按标记顺序排在前面；前奏提示组与标记的徽标和名称', () => {
    const model = buildOrchestration(scene());
    const rows = structureSections(model, [{ id: 'primary', name: '主歌词' }])[0]!.rows.map((row) => `${row.depth}:${row.node.id}`);
    expect(rows).toEqual([
      '0:branch/primary/display',
      '1:branch/primary/display/regular',
      '1:node/double',
      '2:node/s2',
      '2:node/s1',
      '1:node/g',
      '2:node/c',
      '2:node/one',
      '3:node/only',
      '1:node/cd',
      '2:node/m2',
      '2:node/m1',
    ]);
    const by = (id: string) => model.byId.get(id)!;
    expect([roleText(model, by('node/cd')), roleText(model, by('node/m1')), nodeName(model, by('node/m1')), nodeName(model, by('node/m2')), nodeName(model, by('node/s2'))]).toEqual([
      '前奏·2',
      '标记',
      '标记 2',
      '标记 1',
      '槽位 1',
    ]);
    // 轮换容器下的非槽位子节点排在槽位后面，只和非槽位的兄弟交换。
    const r = scene();
    r.nodes.push(node('node/x', { parentId: 'node/double' }));
    const withExtra = buildOrchestration(r);
    expect(orderedChildren(withExtra, 'node/double').map((n) => n.id)).toEqual(['node/s2', 'node/s1', 'node/x']);
    expect(reorderPlan(withExtra, 'node/x', -1)).toEqual({ block: '已经在最前。' });
  });

  it('引用与删除前的检查：根、默认容器、片段目标、前奏提示容器、子树里的引用、最后一个槽位', () => {
    const model = buildOrchestration(scene());
    expect(['branch/primary/display', 'node/double', 'node/c', 'node/cd', 'node/g'].map((id) => referencesOf(model, id).map((ref) => `${ref.kind}${ref.count ?? ''}`))).toEqual([
      ['root'],
      ['default'],
      ['clips1'],
      ['leadIns1'],
      [],
    ]);
    expect(subtreeIds(model, 'node/g')).toEqual(['node/g', 'node/c', 'node/one', 'node/only']);
    expect(subtreeIds(model, 'node/a')).toEqual(['node/a', 'node/b']);
    expect(['branch/primary/display', 'node/double', 'node/c', 'node/cd', 'node/g', 'node/only', 'node/gone'].map((id) => removeBlock(model, id, branchName))).toEqual([
      '来源的根不能删除。',
      '它是主歌词的默认容器：先把默认容器改到别的容器。',
      '有 1 个片段以它为目标容器：先删除这些片段或改目标容器。',
      '有 1 个前奏用它提示：先删除这些前奏。',
      '子树里的“未命名容器”是主歌词的 1 个片段的目标容器，不能删除。',
      '轮换至少要有一个槽位。',
      '这个节点已经不在了。',
    ]);
    expect(['node/s1', 'node/m1', 'branch/primary/display/regular', 'node/free'].map((id) => removeBlock(model, id, branchName))).toEqual([undefined, undefined, undefined, undefined]);
  });

  it('复制：根不能复制；副本名称“原名 副本”，没有名称的槽位和标记仍按序号显示', () => {
    const model = buildOrchestration(scene());
    const by = (id: string) => model.byId.get(id)!;
    expect(copyBlock(model, 'branch/primary/display')).toContain('来源的根不能复制');
    expect(copyBlock(model, 'node/double')).toBeUndefined();
    expect(['node/double', 'node/s1', 'node/s2', 'branch/primary/display/regular', 'node/cd', 'node/m1'].map((id) => copyName(model, by(id)))).toEqual([
      '双行 副本',
      '上行 副本',
      null,
      '常规 副本',
      '未命名内容 副本',
      null,
    ]);
  });

  it('移动：根、槽位、标记不能移动；目标只取普通组，去掉自己、后代和当前父级；子树里有被来源引用的节点时只能留在那个来源的根下', () => {
    const model = buildOrchestration(scene());
    expect(['branch/primary/display', 'node/s1', 'node/m1', 'branch/primary/display/regular'].map((id) => moveBlock(model, id))).toEqual([
      '来源的根固定在画布根层。',
      '槽位只能在它的轮换里调整顺序。',
      '标记只能留在它的提示组里。',
      undefined,
    ]);
    const all = [...model.byId.values()];
    expect(all.filter((n) => isPlainGroup(model, n)).map((n) => n.id)).toEqual(['branch/primary/display', 'node/a', 'node/g', 'node/free', 'node/free2']);
    const targets = (id: string) => {
      const { groups, canvas } = moveTargets(model, id, all);
      return [groups.map((n) => n.id), canvas];
    };
    expect(targets('branch/primary/display/regular')).toEqual([['node/a', 'node/g', 'node/free', 'node/free2'], true]);
    // 默认容器：只能留在主歌词的根下，不能去画布根层。
    expect(targets('node/double')).toEqual([['node/g'], false]);
    // 子树里有片段的目标容器；当前父级就是根，没有别的去处。
    expect(targets('node/g')).toEqual([[], false]);
    // 已经在画布根层。
    expect(targets('node/free')).toEqual([['branch/primary/display', 'node/a', 'node/g', 'node/free2'], false]);
    expect(targets('node/s1')).toEqual([[], false]);
  });

  it('上移下移：槽位用 reorderSlots，标记用 reorderMarks，其他用 reorderChildren 的完整同级清单；画布根层不动来源的根；到头时给出原因', () => {
    const model = buildOrchestration(scene());
    expect(reorderPlan(model, 'node/s2', 1)).toEqual({ command: { type: 'orchestration.reorderSlots', containerId: 'node/double', orderedIds: ['node/s1', 'node/s2'] } });
    expect(reorderPlan(model, 'node/s2', -1)).toEqual({ block: '已经是第一个槽位。' });
    expect(reorderPlan(model, 'node/s1', 1)).toEqual({ block: '已经是最后一个槽位。' });
    expect(reorderPlan(model, 'branch/primary/display/regular', 1)).toEqual({
      command: { type: 'orchestration.reorderChildren', parentId: 'branch/primary/display', orderedIds: ['node/double', 'branch/primary/display/regular', 'node/g', 'node/cd'] },
    });
    expect(reorderPlan(model, 'node/cd', 1)).toEqual({ block: '已经在最后。' });
    // 标记按提示组里的顺序（m2、m1），用 reorderMarks 的完整顺序。
    expect(reorderPlan(model, 'node/m1', -1)).toEqual({ command: { type: 'orchestration.reorderMarks', containerId: 'node/cd', orderedIds: ['node/m1', 'node/m2'] } });
    expect(reorderPlan(model, 'node/m1', 1)).toEqual({ block: '已经是最后一个标记。' });
    expect(reorderPlan(model, 'branch/primary/display', 1)).toEqual({ block: '来源之间的上下层次不在这里调整。' });
    expect(reorderPlan(model, 'node/free', 1)).toEqual({
      command: { type: 'orchestration.reorderChildren', parentId: null, orderedIds: ['branch/primary/display', 'node/free2', 'node/free'] },
    });
    expect(reorderPlan(model, 'node/free', -1)).toEqual({ block: '已经在最前。' });
  });

  it('新建的预填父级：选中节点所在最近的普通组，没有时用来源的根，再没有时是画布根层；失败原因', () => {
    const model = buildOrchestration(scene());
    const program = model.programs[0]!;
    expect(['node/s1', 'node/g', 'node/only', 'node/m1', 'node/free', 'node/lost'].map((id) => insertionParent(model, id, program))).toEqual([
      'branch/primary/display',
      'node/g',
      'node/g',
      'branch/primary/display',
      'node/free',
      'branch/primary/display',
    ]);
    expect([insertionParent(model, undefined, program), insertionParent(model, undefined, undefined)]).toEqual(['branch/primary/display', null]);
    expect(structureFailureText('ContainerInvalid')).toContain('普通组');
    expect(structureFailureText('Nope')).toBeUndefined();
  });

  it('状态：新建（名称去空白、只有轮换带槽位数）后选中新节点；被拒绝时返回结构的原因', async () => {
    const { store, commands, control } = setup();
    await tick();
    control.result = { structureResult: { operation: 'createNode', nodeId: 'node/new', changed: true, createdNodeIds: ['node/new', 'slot/n1'] } };
    control.nodes = [node('node/new', { parentId: 'branch/primary/display', layout: layout('SlotSequence', ['slot/n1']) }), node('slot/n1', { parentId: 'node/new' })];
    expect(await store.createNode({ kind: 'slotSequence', parentId: 'branch/primary/display', name: ' 副歌轮换 ', slotCount: 3 })).toEqual({ result: 'applied', nodeId: 'node/new' });
    expect(commands[0]).toEqual({ type: 'orchestration.createNode', kind: 'slotSequence', parentId: 'branch/primary/display', name: '副歌轮换', slotCount: 3 });
    await tick();
    expect(store.state.selectedId).toBe('node/new');

    await store.createNode({ kind: 'group', parentId: null, name: '  ', slotCount: 4 });
    expect(commands[1]).toEqual({ type: 'orchestration.createNode', kind: 'group', parentId: null, name: null, slotCount: null });
    control.reject = 'ContainerInvalid';
    expect(await store.createNode({ kind: 'auto', parentId: 'node/double', name: '' })).toEqual({ result: 'failed', error: `操作被拒绝：${structureFailureText('ContainerInvalid')}` });
    expect(store.state.busy).toBeUndefined();
    store.dispose();
  });

  it('状态：复制后选中副本；改名、移动、排序、加槽位的命令；排到头时不发命令；删除选中的节点后选中父级', async () => {
    const { store, commands, control } = setup();
    await tick();
    control.result = { structureResult: { operation: 'copySubtree', nodeId: 'node/copy', changed: true, createdNodeIds: ['node/copy'] } };
    control.nodes = [node('node/copy', { name: '上行 副本', parentId: 'node/double' })];
    expect(await store.copyNode('node/s1')).toBe('applied');
    expect(commands[0]).toEqual({ type: 'orchestration.copySubtree', nodeId: 'node/s1', name: '上行 副本' });
    await tick();
    expect(store.state.selectedId).toBe('node/copy');
    delete control.result;

    await store.renameNode('node/double', '  ');
    await store.moveNode('node/double', null);
    expect(await store.reorderNode('node/s1', -1)).toBe('failed');
    await store.reorderNode('node/s1', 1);
    await store.addSlot('node/double');
    expect(commands.slice(1)).toEqual([
      { type: 'orchestration.setNode', nodeId: 'node/double', name: null },
      { type: 'orchestration.reparent', nodeId: 'node/double', parentId: null },
      { type: 'orchestration.reorderSlots', containerId: 'node/double', orderedIds: ['node/s2', 'node/s1'] },
      { type: 'orchestration.addSlot', containerId: 'node/double', slotId: 'slot/fixed', name: null },
    ]);

    await tick();
    store.select('node/s2');
    control.changed = false;
    expect(await store.renameNode('node/s2', '')).toBe('unchanged');
    control.changed = true;
    expect(await store.removeNode('node/s2')).toBe('applied');
    expect(commands.at(-1)).toEqual({ type: 'orchestration.removeNode', nodeId: 'node/s2' });
    expect(store.state.selectedId).toBe('node/double');
    store.dispose();
  });

  it('状态：属性命令原样发出，no-op 返回 unchanged，被拒绝时按属性的对照表说明', async () => {
    const { store, commands, control, session } = setup();
    await tick();
    const command = { type: 'orchestration.setProgramAnimationPolicy', branchId: 'primary', animationPolicy: 'Static' } as const;
    expect(await store.setProperties(command)).toBe('applied');
    expect(commands).toEqual([command]);
    control.changed = false;
    expect(await store.setProperties(command)).toBe('unchanged');
    control.changed = true;
    control.reject = 'ContainerInvalid';
    expect(await store.setProperties({ type: 'orchestration.setLayoutKind', containerId: 'node/s1', kind: 'Auto', slotIds: [], newSlotCount: 0 })).toBe('failed');
    expect(session.state.notice?.message).toBe('操作被拒绝：只有歌词容器能切换布局类型。');
    expect(store.state.busy).toBeUndefined();
    store.dispose();
  });
});

describe('前奏与句子（G23、G24）', () => {
  const leadIn = (id: string, targetLineId: string, extra: Partial<OrchestrationLeadIn> = {}): OrchestrationLeadIn => ({
    id,
    targetLineId,
    indicatorContainerId: 'node/s1',
    durationUs: '2000000',
    enabled: true,
    origin: 'Manual',
    ...extra,
  });

  it('选择：点句子取得句子焦点；选前奏高亮目标句并选中提示组；选节点、片段时焦点结束；前奏不在了取消选中；两个列表一次只展开一个', async () => {
    const { store, control } = setup();
    control.leadIns = [leadIn('lead-in/a', 'l2')];
    control.clips = [{ id: 'clip/a', lineIds: ['l4'], containerId: 'node/double' }];
    store.reload();
    await tick();

    store.clickLine('l1', false);
    expect(store.state).toMatchObject({ lineIds: ['l1'], lineFocus: true });
    store.selectLeadIn('lead-in/a');
    expect(store.state).toMatchObject({ selectedLeadInId: 'lead-in/a', selectedId: 'node/s1', lineIds: ['l2'], lineAnchor: 'l2' });
    expect(store.state.lineFocus).toBeUndefined();
    store.selectLines(['l1', 'l2'], false);
    expect(store.state).toMatchObject({ lineIds: ['l1', 'l2'], lineFocus: true });
    expect(store.state.selectedLeadInId).toBeUndefined();
    store.selectClip('clip/a');
    expect(store.state).toMatchObject({ selectedClipId: 'clip/a', lineIds: ['l4'] });
    expect(store.state.lineFocus).toBeUndefined();
    store.clickLine('l4', false);
    store.select('node/s2');
    expect(store.state).toMatchObject({ selectedId: 'node/s2', lineIds: ['l4'] });
    expect(store.state.lineFocus).toBeUndefined();

    store.setBranch('tr');
    store.focusLine('primary', 'l5');
    expect(store.state).toMatchObject({ branchId: 'primary', lineIds: ['l5'], lineFocus: true });
    store.focusLine('primary', 'gone');
    expect(store.state.lineIds).toEqual(['l5']);

    store.selectLeadIn('lead-in/a');
    control.leadIns = [];
    store.reload();
    await tick();
    expect(store.state.selectedLeadInId).toBeUndefined();
    expect(store.state.lineIds).toEqual(['l2']);

    store.toggleClips();
    expect(store.state.panel).toBe('clips');
    store.toggleLeadIns();
    expect(store.state.panel).toBe('leadIns');
    store.toggleLeadIns();
    expect(store.state.panel).toBeUndefined();
    store.dispose();
  });

  it('单句展示：只在有焦点、只选一句时读；修订变了重读并保留上一份；换句时先清空；409 等下一个修订；几句或没有焦点时清掉', async () => {
    const { store, state$, presentations, control } = setup();
    await tick();
    store.clickLine('l1', false);
    expect(store.state.linePresentation).toMatchObject({ lineId: 'l1', revision: '3', status: 'loading' });
    await tick();
    expect(store.state.linePresentation).toMatchObject({ status: 'ready', response: { lineId: 'l1' } });

    state$.set({ ...state$.get(), summary: { projectRevision: '4' } });
    expect(store.state.linePresentation).toMatchObject({ revision: '4', status: 'loading', response: { projectRevision: '3' } });
    await tick();
    expect(store.state.linePresentation).toMatchObject({ status: 'ready', response: { projectRevision: '4' } });

    store.clickLine('l2', false);
    expect(store.state.linePresentation?.response).toBeUndefined();
    await tick();
    expect(presentations).toEqual(['3:primary:l1', '4:primary:l1', '4:primary:l2']);

    control.presentation = () => Promise.reject(new BackendError(409, { code: 'revisionConflict', message: '修订冲突', protocolVersion: '1' }));
    store.clickLine('l4', false);
    await tick();
    expect(store.state.linePresentation).toMatchObject({ lineId: 'l4', status: 'loading' });
    control.presentation = () =>
      Promise.reject(new BackendError(422, { code: 'commandRejected', message: '行不存在', failureCode: 'TargetStale', protocolVersion: '1' }));
    state$.set({ ...state$.get(), summary: { projectRevision: '5' } });
    await tick();
    expect(store.state.linePresentation).toMatchObject({ status: 'error', error: '读取这一句失败：这一句已经不在了，请重新选择。' });
    delete control.presentation;

    store.selectLines(['l1', 'l2'], false);
    expect(store.state.linePresentation).toBeUndefined();
    store.clickLine('l1', false);
    store.select('node/s1');
    expect(store.state.linePresentation).toBeUndefined();
    store.dispose();
  });

  it('前奏命令：添加（ID、读到新修订后选中）、修改、删除并排除、附着；被拒绝时用前奏的原因；新建提示组可以选中', async () => {
    const { store, commands, control } = setup();
    await tick();
    control.leadIns = [leadIn('lead-in/fixed', 'l1', { name: '开头' })];
    const created = await store.createLeadIn('primary', { targetLineId: 'l1', indicatorContainerId: 'node/s1', durationUs: '2000000', name: ' 开头 ' });
    expect(created).toEqual({ result: 'applied', leadInId: 'lead-in/fixed' });
    expect(commands[0]).toEqual({
      type: 'orchestration.createLeadIn',
      branchId: 'primary',
      leadInId: 'lead-in/fixed',
      name: '开头',
      targetLineId: 'l1',
      indicatorContainerId: 'node/s1',
      durationUs: '2000000',
      enabled: true,
    });
    await tick();
    expect(store.state).toMatchObject({ selectedLeadInId: 'lead-in/fixed', selectedId: 'node/s1', lineIds: ['l1'] });

    expect(await store.updateLeadIn('primary', control.leadIns[0]!, { enabled: false })).toBe('applied');
    expect(commands[1]).toMatchObject({ type: 'orchestration.updateLeadIn', leadInId: 'lead-in/fixed', name: '开头', enabled: false, durationUs: '2000000' });
    expect(await store.setLeadInAttachment('primary', 'lead-in/fixed', 'node/s1', 'FixedParent')).toBe('applied');
    expect(commands[2]).toEqual({ type: 'orchestration.setLeadInAttachment', branchId: 'primary', leadInId: 'lead-in/fixed', containerId: 'node/s1', mode: 'FixedParent' });
    expect(await store.deleteLeadIn('primary', 'lead-in/fixed', true)).toBe('applied');
    expect(commands[3]).toEqual({ type: 'orchestration.deleteLeadInAndExclude', branchId: 'primary', leadInId: 'lead-in/fixed' });
    expect(store.state.selectedLeadInId).toBeUndefined();
    expect(store.state.lineIds).toEqual(['l1']);

    control.result = { leadInResult: { operation: 'createCountdown', indicatorContainerId: 'node/cd', markNodeIds: ['node/m1'], changed: true } };
    control.nodes = [node('node/cd', { parentId: 'branch/primary/display', contentKind: 'countdownSequence' })];
    expect(await store.createCountdown('branch/primary/display', '  ', 3, { select: true })).toEqual({ result: 'applied', containerId: 'node/cd' });
    expect(commands[4]).toEqual({ type: 'orchestration.createCountdown', parentId: 'branch/primary/display', name: null, count: 3 });
    await tick();
    expect(store.state.selectedId).toBe('node/cd');

    control.reject = 'LeadInTimeConflict';
    const failed = await store.createLeadIn('primary', { targetLineId: 'l2', indicatorContainerId: 'node/cd', durationUs: '9000000' });
    expect(failed).toEqual({ result: 'failed', error: `操作被拒绝：${leadInFailureText('LeadInTimeConflict')}` });
    control.reject = 'TargetStale';
    expect(await store.setLineTransform('primary', 'l9', null)).toBe('failed');
    expect(store.state.busy).toBeUndefined();
    store.dispose();
  });

  it('自动生成：预演直接经客户端、只留最新一次，失败写原因；应用只带勾选的句子，成功后展开前奏列表', async () => {
    const { store, commands, control, previews } = setup();
    await tick();
    const settings = { includeOpening: true, includeLongGaps: true, gapThresholdUs: '8000000', durationUs: '3000000', indicatorContainerId: 'node/s1', excludedLineIds: [] };
    const result: LeadInGenerationCommandResult = {
      operation: 'previewLeadInGeneration',
      branchId: 'primary',
      candidates: [{ targetLineId: 'l1', status: 'New', reason: 'None' }],
      diagnostics: [],
      selectedLineIds: [],
      changed: false,
    };
    let release: (() => void) | undefined;
    control.preview = (command) =>
      'settings' in command && command.settings?.durationUs === '1000000'
        ? new Promise((resolve) => (release = () => resolve({ leadInGenerationResult: { ...result, candidates: [] } })))
        : Promise.resolve({ leadInGenerationResult: result });
    const slow = store.previewGeneration('primary', { ...settings, durationUs: '1000000' });
    await store.previewGeneration('primary', settings);
    release!();
    await slow;
    expect(previews.map((c) => c.type)).toEqual(['orchestration.previewLeadInGeneration', 'orchestration.previewLeadInGeneration']);
    expect(store.state.generationPreview).toMatchObject({ status: 'ready', settings: { durationUs: '3000000' }, result: { candidates: [{ targetLineId: 'l1' }] } });

    control.preview = () => Promise.reject(new BackendError(422, { code: 'commandRejected', message: 'x', failureCode: 'ContainerInvalid', protocolVersion: '1' }));
    await store.previewGeneration('primary', settings);
    expect(store.state.generationPreview).toMatchObject({ status: 'error', error: `预演失败：${leadInFailureText('ContainerInvalid')}` });
    store.endGenerationPreview();
    expect(store.state.generationPreview).toBeUndefined();

    expect(await store.applyGeneration('primary', settings, ['l1'])).toEqual({ result: 'applied' });
    expect(commands[0]).toEqual({ type: 'orchestration.applyLeadInGeneration', branchId: 'primary', settings, selectedLineIds: ['l1'] });
    expect(store.state.panel).toBe('leadIns');
    control.changed = false;
    expect(await store.applyGeneration('primary', settings, [])).toEqual({ result: 'unchanged' });
    store.dispose();
  });

  it('句子命令：本句微调、本句进入动画、工程默认进入动画；标记命令', async () => {
    const { store, commands } = setup();
    await tick();
    const transform = { offsetXRatio: 0.1, offsetYRatio: 0, rotationDegrees: 0, scale: 1 };
    await store.setLineTransform('primary', 'l1', transform);
    await store.setLineEnterAnimation('primary', 'l1', null);
    await store.setProjectEnterAnimation({ durationUs: '400000', easing: 'EaseOut', startOpacity: 0, startScale: 1, startOffsetPx: 12 });
    await store.addMark('node/cd', '●');
    await store.setMarkText('node/m1', '3');
    await store.spaceMarks('node/cd');
    expect(commands).toEqual([
      { type: 'orchestration.setLineLocalTransform', branchId: 'primary', lineId: 'l1', transform },
      { type: 'orchestration.setLineEnterAnimation', branchId: 'primary', lineId: 'l1', animation: null },
      { type: 'orchestration.setProjectEnterAnimation', animation: { durationUs: '400000', easing: 'EaseOut', startOpacity: 0, startScale: 1, startOffsetPx: 12 } },
      { type: 'orchestration.addMark', containerId: 'node/cd', text: '●' },
      { type: 'orchestration.setMarkText', nodeId: 'node/m1', text: '3' },
      { type: 'orchestration.spaceMarks', containerId: 'node/cd' },
    ]);
    store.dispose();
  });
});

describe('片段成员：调整句子、拆分与合并（§15）', () => {
  /** 第 1～5 句依次有时间，第 6 句没有。 */
  const M = [
    { id: 'm1', text: '一', startUs: 0, endUs: 1_000_000 },
    { id: 'm2', text: '二', startUs: 1_000_000, endUs: 2_000_000 },
    { id: 'm3', startUs: 2_000_000, endUs: 3_000_000 },
    { id: 'm4', text: '四', startUs: 3_000_000, endUs: 4_000_000 },
    { id: 'm5', text: '五', startUs: 4_000_000, endUs: 5_000_000 },
    { id: 'm6', text: '六' },
  ];
  const regular = 'branch/primary/display/regular';
  const p1: OrchestrationClip = { id: 'p1', lineIds: ['m1'], containerId: 'node/double' };
  const p2: OrchestrationClip = { id: 'p2', lineIds: ['m2', 'm3'], containerId: 'node/double' };
  const p3: OrchestrationClip = { id: 'p3', lineIds: ['m4'], containerId: regular };
  const withClips = (clips: OrchestrationClip[]): OrchestrationProgram => ({ ...response().programs[0]!, clips });
  const context = { lineIds: ['m2', 'm3', 'm4'], isDefaultSegment: false };

  it('调整句子的检查：相同、自己的句子不算占用、别的片段、原段；原段说明', () => {
    const program = withClips([p2, p3]);
    expect(clipRangeBlock(program, M, ['m2', 'm3'], p2)).toBe('和现在的句子相同。');
    expect(clipRangeBlock(program, M, ['m1', 'm2', 'm3'], p2)).toBeUndefined();
    expect(clipRangeBlock(program, M, ['m2'], p2)).toBeUndefined();
    expect(clipRangeBlock(program, M, ['m2', 'm3', 'm4'], p2)).toBe('第 4 句已在片段“片段 2”里；每句只能属于一个片段，先把那个片段缩小或删除。');
    expect(clipRangeBlock(program, M, ['m2'])).toContain('第 2 句已在片段“片段 1”里');
    expect(clipRangeBlock(program, M, ['m3', 'm4', 'm5', 'm6'], p3)).toBe('第 6 句没有时间；片段只能包含有时间的句子。');

    const kept = { ...p2, scheduleContext: context };
    expect(clipRangeBlock(withClips([kept]), M, ['m1', 'm2', 'm3'], kept)).toBe(
      '第 1 句不在原段（第 2～4 句）里：这个片段保持原段的槽位和显示时钟，句子只能在原段里调整。',
    );
    expect(clipRangeBlock(withClips([kept]), M, ['m2', 'm3', 'm4'], kept)).toBeUndefined();
    expect(scheduleContextNote(p2, M)).toBeUndefined();
    expect(scheduleContextNote(kept, M)).toBe('这个片段保持原段（第 2～4 句）的槽位和显示时钟，句子只能在这一段里调整。');
  });

  it('拆分：只有一句不能拆；拆分点的选项、预填（播放头所在句，否则中间那句）与结果说明', () => {
    const big: OrchestrationClip = { id: 'big', name: '副歌', lineIds: ['m1', 'm2', 'm3', 'm4'], containerId: 'node/double' };
    expect(splitBlock(p3)).toBe('只有一句，不能拆分。');
    expect(splitBlock(big)).toBeUndefined();
    expect(splitPoints(big, M)).toEqual([
      { lineId: 'm2', number: 2, label: '第 2 句 · 二' },
      { lineId: 'm3', number: 3, label: '第 3 句' },
      { lineId: 'm4', number: 4, label: '第 4 句 · 四' },
    ]);
    expect(defaultSplitPoint(big, M)).toBe('m3');
    expect(defaultSplitPoint(big, M, 3_500_000)).toBe('m4');
    expect(defaultSplitPoint(big, M, 500_000)).toBe('m3');
    expect(defaultSplitPoint(p3, M)).toBeUndefined();

    expect(splitParts(big, M, 'm4')).toEqual({ left: '第 1～3 句', right: '第 4 句' });
    expect(splitParts(big, M, 'm1')).toBeUndefined();
    expect(splitResultText(big, '副歌', M, 'm3')).toBe(
      '第 1～2 句留在“副歌”，第 3～4 句成为新片段（名称相同，目标容器相同）。拆开后两段都保持原段的槽位和显示时钟，句子只能在第 1～4 句里调整。',
    );
    expect(splitResultText({ ...p2, scheduleContext: context }, '片段 1', M, 'm3')).toContain('只能在第 2～4 句里调整');
  });

  it('合并：前后紧挨着的片段；没有句子、不在片段里、目标容器不同、调度上下文不同时的原因', () => {
    const model = buildOrchestration(response());
    const program = withClips([p1, p2, p3]);
    expect(mergeNeighbour(model, program, M, p2, 'previous')).toEqual({ clip: p1, label: '片段 1' });
    expect(mergeNeighbour(model, program, M, p1, 'previous')).toEqual({ block: '前面没有句子。' });
    expect(mergeNeighbour(model, program, M, p3, 'next')).toEqual({ block: '后面紧挨着的第 5 句不在片段里。' });
    const container = mergeNeighbour(model, program, M, p2, 'next');
    expect(container.block).toMatch(/^后一个片段“片段 3”在“.+”里，与这个片段的目标容器不同；先把它们改到同一个容器。$/);
    const kept = { ...p2, scheduleContext: context };
    expect(mergeNeighbour(model, withClips([p1, kept]), M, kept, 'previous')).toEqual({ block: '两个片段来自不同的原段（调度上下文不同），不能合并。' });
    expect(mergeNeighbour(model, withClips([{ ...p1, scheduleContext: { ...context, previousLineId: null } }, kept]), M, kept, 'previous').clip?.id).toBe('p1');
    expect(mergeNeighbour(model, program, M, { ...p1, lineIds: ['gone'] }, 'next')).toEqual({ block: '这个片段的句子已经不在了。' });

    expect(sameScheduleContext(undefined, null)).toBe(true);
    expect(sameScheduleContext(context, undefined)).toBe(false);
    expect(sameScheduleContext(context, { ...context, lineIds: ['m2', 'm3'] })).toBe(false);
    expect(sameScheduleContext(context, { ...context, isDefaultSegment: true })).toBe(false);
    expect(mergedRangeText(M, p2, p1)).toBe('第 1～3 句');
  });

  it('失败原因按操作补充，其余沿用片段命令的对照表', () => {
    expect(clipMembersFailureText('setClipMembers')('ScheduleContextConflict')).toContain('不在这个片段的原段里');
    expect(clipMembersFailureText('mergeClips')('ScheduleContextConflict')).toBe('两个片段来自不同的原段（调度上下文不同），不能合并。');
    expect(clipMembersFailureText('splitClip')('InvalidRange')).toBe('拆分点必须是这个片段里除第一句以外的句子。');
    expect(clipMembersFailureText('mergeClips')('ContainerInvalid')).toBe('合并的片段必须在同一个目标容器里。');
    expect(clipMembersFailureText('setClipMembers')('ContainerInvalid')).toBe(clipFailureText('ContainerInvalid'));
    expect(clipMembersFailureText('mergeClips')('TargetStale')).toBe(clipFailureText('TargetStale'));
    expect(clipMembersFailureText('splitClip')('Unknown')).toBeUndefined();
  });

  it('状态：三条命令的字段；成功后选中调整后的片段、拆出的新片段、合并保留的片段；no-op 与失败', async () => {
    const { store, commands, control } = setup();
    control.clips = [{ id: 'clip/a', lineIds: ['l1'], containerId: 'node/double' }];
    store.reload();
    await tick();
    store.selectClip('clip/a');

    control.changed = false;
    expect(await store.setClipMembers('primary', 'clip/a', ['l1'])).toEqual({ result: 'unchanged' });
    control.changed = true;
    control.clips = [{ id: 'clip/a', lineIds: ['l4'], containerId: 'node/double' }];
    expect(await store.setClipMembers('primary', 'clip/a', ['l4'])).toEqual({ result: 'applied' });
    expect(commands.at(-1)).toEqual({ type: 'orchestration.setClipMembers', branchId: 'primary', clipId: 'clip/a', lineIds: ['l4'] });
    await tick();
    expect(store.state).toMatchObject({ selectedClipId: 'clip/a', lineIds: ['l4'] });

    control.clips = [
      { id: 'clip/a', lineIds: ['l1'], containerId: 'node/double' },
      { id: 'clip/fixed', lineIds: ['l2'], containerId: 'node/double' },
    ];
    expect(await store.splitClip('primary', 'clip/a', 'l2')).toEqual({ result: 'applied' });
    expect(commands.at(-1)).toEqual({ type: 'orchestration.splitClip', branchId: 'primary', clipId: 'clip/a', splitBeforeLineId: 'l2', newClipId: 'clip/fixed' });
    await tick();
    expect(store.state).toMatchObject({ selectedClipId: 'clip/fixed', selectedId: 'node/double', lineIds: ['l2'] });

    control.reject = 'ScheduleContextConflict';
    expect(await store.mergeClips('primary', 'clip/fixed', 'clip/a')).toEqual({
      result: 'failed',
      error: '操作被拒绝：两个片段来自不同的原段（调度上下文不同），不能合并。',
    });
    expect(store.state.busy).toBeUndefined();
    delete control.reject;
    control.clips = [{ id: 'clip/fixed', lineIds: ['l1', 'l2'], containerId: 'node/double' }];
    expect(await store.mergeClips('primary', 'clip/fixed', 'clip/a')).toEqual({ result: 'applied' });
    expect(commands.at(-1)).toEqual({ type: 'orchestration.mergeClips', branchId: 'primary', clipIds: ['clip/fixed', 'clip/a'], targetClipId: 'clip/fixed' });
    await tick();
    expect(store.state).toMatchObject({ selectedClipId: 'clip/fixed', lineIds: ['l1', 'l2'] });
    store.dispose();
  });
});
