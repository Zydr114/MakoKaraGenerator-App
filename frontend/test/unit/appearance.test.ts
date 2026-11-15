import { describe, expect, it } from 'vitest';
import { BackendError, ObservableValue, type MakoClient } from '../../src/client';
import { AppearanceStore, CANDIDATE_PREVIEW, PRESETS_PREVIEW_PROFILE, RESOLVED_STYLE_READ } from '../../src/editor/appearance/appearance-store';
import {
  applicability,
  clipTarget,
  commandTargets,
  describeTargets,
  lineTarget,
  pickTarget,
  PROJECT_TARGET,
  rangeTargets,
  reconcileTargets,
  sampleKeyOf,
  sampleTargetKeys,
  targetCountText,
  targetKey,
  targetsSignature,
  tokenTarget,
  type NodeTarget,
} from '../../src/editor/model/appearance-targets';
import { lineAppearance, linePlacement } from '../../src/editor/model/line-placement';
import type { EditorLine } from '../../src/editor/model/lyrics';
import { buildOrchestration } from '../../src/editor/model/orchestration';
import { applicationNames, applicationsOn, indexApplications, nodeScopeOf, orphanTargetText, scopeText, updateSource } from '../../src/editor/model/preset-applications';
import { DEFAULT_SCENE_TEXT_APPEARANCE, lyricStyleToCountdownAppearance, overrideRows, sceneAppearanceRows, toStaticSceneAppearance } from '../../src/editor/model/style-overrides';
import { parseSourcePath, sourceGroups, sourceTarget, sourceText, STYLE_PROPERTY_TEXT, type SourceContext } from '../../src/editor/model/style-sources';
import { TEMPLATE_GROUPS } from '../../src/editor/model/template-groups';
import { fitPreviewSize } from '../../src/editor/preview/preview-controller';
import type { EditorNotice, EditorSession } from '../../src/editor/session/editor-session';
import { revision, type CanonicalStyle, type OrchestrationNode, type PresetApplication, type PresetApplicationTargetRead, type ProjectCommand, type SceneTextAppearance, type TemplateSummary } from '../../src/protocol';

const line = (id: string, tokens: string[]): EditorLine => ({
  branchId: 'main',
  id,
  text: tokens.join(''),
  tokens: tokens.map((text, i) => ({ id: `${id}-t${i}`, lineId: id, text })),
  ruby: [],
});
const LINES = [line('l1', ['朝', 'の']), line('l2', ['空']), line('l3', ['風']), line('l4', ['君'])];
const BRANCHES = [{ id: 'main', name: '主歌词', role: 'primary' as const, timingMode: 'independent' as const, lineCount: 4 }];
const BY_BRANCH = { main: LINES };

describe('外观目标', () => {
  it('点选：加选只在同一层级内，最后一个不能取消；换层级或工程默认时只选一个；Shift 连选取锚点到目标一段', () => {
    const [l1, l2, l3, l4] = LINES.map(lineTarget);
    let targets = pickTarget([l1!], l3!, true);
    expect(targets.map(targetKey)).toEqual(['line:main/l1', 'line:main/l3']);
    targets = pickTarget(targets, l1!, true);
    expect(targets).toEqual([l3]);
    expect(pickTarget(targets, l3!, true)).toEqual([l3]);
    expect(pickTarget(targets, tokenTarget(LINES[0]!, 'l1-t0'), true)).toEqual([tokenTarget(LINES[0]!, 'l1-t0')]);
    expect(pickTarget(targets, PROJECT_TARGET, true)).toEqual([PROJECT_TARGET]);
    expect(rangeTargets(LINES.map(lineTarget), targetKey(l4!), l2!)).toEqual([l2, l3, l4]);
    expect(rangeTargets(LINES.map(lineTarget), undefined, l2!)).toEqual([l2]);
  });

  it('路径：行号压缩成区间，文本单元列出文字；失效的目标去掉，全部失效回到工程默认', () => {
    const lines = [LINES[0]!, LINES[1]!, LINES[2]!, LINES[3]!].map(lineTarget);
    expect(describeTargets([lines[0]!, lines[1]!, lines[2]!, lines[3]!], BRANCHES, BY_BRANCH)).toBe('主歌词 / 第 1～4 行');
    expect(describeTargets([lines[0]!, lines[2]!, lines[3]!], BRANCHES, BY_BRANCH)).toBe('主歌词 / 第 1、3、4 行');
    expect(describeTargets([tokenTarget(LINES[0]!, 'l1-t0'), tokenTarget(LINES[0]!, 'l1-t1')], BRANCHES, BY_BRANCH)).toBe('主歌词 / 第 1 行 / 「朝」「の」');

    const targets = [lines[0]!, { kind: 'line' as const, branchId: 'main', lineId: 'gone' }];
    expect(reconcileTargets(targets, ['main'], BY_BRANCH)).toEqual([lines[0]]);
    const unchanged = [lines[1]!];
    expect(reconcileTargets(unchanged, ['main'], BY_BRANCH)).toBe(unchanged);
    expect(reconcileTargets([{ kind: 'branch', branchId: 'deleted' }], ['main'], BY_BRANCH)).toEqual([PROJECT_TARGET]);
  });

  it('直接覆盖的显示与预览尺寸', () => {
    const rows = overrideRows([
      { property: 'typography.fontSizePx', value: 80 },
      { property: 'unsung.strokes', value: [] },
      { property: 'sung.fill', value: { kind: 'solid', color: { red: 255, green: 0, blue: 0, alpha: 255 } } },
      { property: 'sung.shadow', value: { kind: 'follow' } },
      { property: 'sung.glow', value: { kind: 'set', value: null } },
      { property: 'wipe.featherRatio', value: 0.2 },
      { property: 'future.property' as never, value: 1 },
    ]);
    expect(rows.map((r) => `${r.label}=${r.text}`)).toEqual([
      '字号=80 px',
      '未唱描边=关',
      '已唱填充=#ff0000',
      '已唱阴影=跟随未唱',
      '已唱辉光=关',
      '擦除羽化=0.2 倍字号',
      'future.property=1',
    ]);
    expect(rows[2]!.swatch).toBe('rgb(255 0 0 / 1)');

    expect(fitPreviewSize(1920, 1080)).toEqual({ width: 1280, height: 720 });
    expect(fitPreviewSize(1080, 1920)).toEqual({ width: 404, height: 720 });
    expect(fitPreviewSize(640, 360)).toEqual({ width: 640, height: 360 });
  });
});

const TEMPLATE: TemplateSummary = {
  templateId: 'p1',
  name: '红色',
  kind: 'line',
  templateRevision: 't1',
  properties: ['typography.fontSizePx', 'sung.fill'],
  hasEffectRecipes: false,
  hasPackageMaterials: false,
  hasPackages: false,
};

/** 只有外观状态用到的会话成员：命令按 reply 的设定成功或以 404 拒绝（模板库修订对不上）。 */
function fakeSession(options: { preview?: Partial<MakoClient['preview']>; projects?: Partial<MakoClient['projects']>; supports?: (feature: string) => boolean } = {}) {
  const state$ = new ObservableValue({
    summary: { projectRevision: '0', canUndo: false },
    branchId: 'main',
    branches: BRANCHES,
    lines: LINES,
    linesByBranch: BY_BRANCH,
    notice: undefined as EditorNotice | undefined,
  });
  const commands: ProjectCommand[] = [];
  const control = { reply: 'ok' as 'ok' | 'stale' };
  const session = {
    state$,
    get state() {
      return state$.get();
    },
    projectSessionId: 's1',
    get latestRevision() {
      return state$.get().summary.projectRevision;
    },
    supports: options.supports ?? (() => false),
    selectBranch: () => undefined,
    select: () => undefined,
    notify: (notice: EditorNotice) => state$.set({ ...state$.get(), notice }),
    async command(command: ProjectCommand) {
      commands.push(command);
      if (control.reply === 'stale') {
        state$.set({ ...state$.get(), notice: { level: 'error', message: '操作被拒绝', code: 'resourceNotFound' } });
        return undefined;
      }
      const projectRevision = String(Number(state$.get().summary.projectRevision) + 1);
      state$.set({ ...state$.get(), summary: { projectRevision, canUndo: true } });
      return { changed: true, projectRevision };
    },
    undo() {
      return session.command({ type: 'history.undo' });
    },
  };
  const client = {
    preview: { runtimeInfo: () => new Promise(() => undefined), ...options.preview },
    ...(options.projects ? { projects: options.projects } : {}),
  } as unknown as MakoClient;
  return { store: new AppearanceStore(client, session as unknown as EditorSession), commands, control, session };
}

/** 一个可以从外面结束的候选试看请求。 */
interface PendingTrial {
  input: Parameters<MakoClient['preview']['renderCandidateStill']>[1];
  resolve: (sha: string) => void;
  reject: (error: unknown) => void;
}

function trialSession() {
  const pending: PendingTrial[] = [];
  const renderCandidateStill = (_id: string, input: PendingTrial['input']) =>
    new Promise((resolve, reject) => {
      pending.push({ input, reject, resolve: (sha) => resolve({ blob: new Blob([sha]), sha256: sha, verified: true }) });
    });
  const runtimeInfo = () => Promise.resolve({ enabled: true, sourceProfiles: [PRESETS_PREVIEW_PROFILE] });
  const fake = fakeSession({ preview: { runtimeInfo, renderCandidateStill } as unknown as Partial<MakoClient['preview']>, supports: (f) => f === CANDIDATE_PREVIEW });
  return { ...fake, pending };
}

const SIZE = { width: 640, height: 360 };
const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

describe('外观应用', () => {
  it('一条命令应用到全部目标，带库修订；之后工程没再变才能一键撤销；库修订对不上时提示重新确认', async () => {
    const { store, commands, control, session } = fakeSession();
    const targets = LINES.slice(0, 2).map(lineTarget);
    store.setTargets(targets);
    expect(await store.apply('personal', 'lib-r1', TEMPLATE)).toBe('applied');
    expect(commands).toEqual([{ type: 'presetApplications.apply', templateLibraryId: 'personal', templateId: 'p1', templateRevision: 'lib-r1', targets }]);
    expect(store.state.lastApplied).toEqual({ templateName: '红色', targetText: '2 行', revision: '1' });
    expect(store.canUndoApply).toBe(true);

    await session.command({ type: 'history.redo' });
    expect(store.canUndoApply).toBe(false);

    control.reply = 'stale';
    expect(await store.apply('personal', 'lib-r0', TEMPLATE)).toBe('stale');
    control.reply = 'ok';
    await store.apply('personal', 'lib-r1', TEMPLATE, { 翻译: 'zh' });
    expect(commands.at(-1)).toMatchObject({ type: 'presetApplications.apply', branchInputs: { 翻译: 'zh' } });
    expect(session.state.notice?.level).toBe('warning');
    expect(store.state.applying).toBeUndefined();
  });

  it('适用规则：有试样选项的目标键时以它为准，没有时按本地规则（文本单元用途只能用于文本单元；用于文本单元的模板不能含字体、字号等）', () => {
    const token: TemplateSummary = { ...TEMPLATE, kind: 'token', properties: ['sung.fill'] };
    expect(applicability(token, 'line')).toMatch('只能用于文本单元');
    expect(applicability(token, 'token')).toBeUndefined();
    expect(applicability(TEMPLATE, 'token')).toBe('含文本单元不能用的属性：字号');
    expect(applicability(TEMPLATE, 'project')).toBeUndefined();
    // 含效果的模板本地不拦，以后端为准。
    const effect: TemplateSummary = { ...TEMPLATE, properties: ['sung.fill'], hasEffectRecipes: true, hasPackages: true };
    expect(applicability(effect, 'line')).toBeUndefined();
    expect(applicability(effect, 'token')).toBeUndefined();
    const lineKeys = ['line', 'project', 'branch', 'clip', 'group', 'mark'];
    expect(applicability(effect, 'token', lineKeys)).toBe('含作用于整行的效果，不能用于文本单元');
    expect(applicability(effect, 'token', [...lineKeys, 'token'])).toBeUndefined();
    expect(applicability(TEMPLATE, 'token', lineKeys)).toBe('含文本单元不能用的属性：字号');
    expect(applicability({ ...TEMPLATE, properties: ['sung.fill'] }, 'branch', ['line'])).toBe('这个模板不能用于来源');
    expect(
      sampleTargetKeys({
        samples: [
          { sampleId: 'workshop-single-line-v1', multiLine: false, durationUs: '12000000', targets: [{ key: 'line', binding: { kind: 'lines', ids: [], includeDescendants: false } }] },
          { sampleId: 'workshop-multi-line-v1', multiLine: true, durationUs: '12000000', targets: ['line', 'mark'].map((key) => ({ key, binding: { kind: key, ids: [], includeDescendants: false } })) },
        ],
      }),
    ).toEqual(['line', 'mark']);
  });
});

describe('模板试看（G16）', () => {
  it('用当前修订、播放头和全部目标请求候选帧；换模板中止上一次，只显示最新的；不发命令', async () => {
    const { store, pending, commands } = trialSession();
    await flush();
    expect(store.canTrial).toBe(true);
    const targets = LINES.slice(0, 2).map(lineTarget);
    store.setTargets(targets);
    store.trial('personal', 'lib-r1', TEMPLATE, 1_500_000.4, SIZE);
    expect(store.state.trial).toMatchObject({ status: 'loading', templateName: '红色', targetText: '2 行', revision: '0', timeUs: 1_500_000 });
    expect(pending[0]!.input).toMatchObject({
      expectedProjectRevision: '0',
      templateLibraryId: 'personal',
      templateLibraryRevision: 'lib-r1',
      templateId: 'p1',
      templateRevision: 't1',
      targets,
      size: SIZE,
      timeUs: '1500000',
    });

    store.trial('builtin', 'lib-b', { ...TEMPLATE, templateId: 'b1', name: '蓝色' }, 1_500_000, SIZE);
    expect(pending[0]!.input.signal?.aborted).toBe(true);
    pending[0]!.resolve('old');
    pending[1]!.resolve('new');
    await flush();
    expect(store.state.trial).toMatchObject({ status: 'ready', templateName: '蓝色', sha256: 'new' });

    // 同一个模板、同一时刻不重复请求。
    store.trial('builtin', 'lib-b', { ...TEMPLATE, templateId: 'b1', name: '蓝色' }, 1_500_000, SIZE);
    expect(pending).toHaveLength(2);
    expect(commands).toEqual([]);
  });

  it('来源输入映射带进候选；换映射是另一次试看', async () => {
    const { store, pending } = trialSession();
    await flush();
    store.trial('personal', 'lib-r1', TEMPLATE, 0, SIZE, { 翻译: 'zh' });
    expect(pending[0]!.input).toMatchObject({ branchInputs: { 翻译: 'zh' } });
    store.trial('personal', 'lib-r1', TEMPLATE, 0, SIZE, { 翻译: 'en' });
    expect(pending).toHaveLength(2);
    expect(pending[1]!.input).toMatchObject({ branchInputs: { 翻译: 'en' } });
    store.trial('personal', 'lib-r1', TEMPLATE, 0, SIZE);
    expect('branchInputs' in pending[2]!.input).toBe(false);
  });

  it('工程修订变了（含 409）按新修订重来；后端拒绝时写明原因；结束后不再请求', async () => {
    const { store, pending, session } = trialSession();
    await flush();
    store.trial('personal', 'lib-r1', TEMPLATE, 0, SIZE);
    pending[0]!.reject(new BackendError(409, { code: 'revisionConflict', message: '修订过期', protocolVersion: '1' }));
    await flush();
    expect(store.state.trial?.status).toBe('loading');

    await session.command({ type: 'history.redo' });
    expect(pending[1]!.input.expectedProjectRevision).toBe('1');
    pending[1]!.reject(new BackendError(422, { code: 'commandRejected', message: '所选预设包含不适用于文字目标的属性或相对配方。', protocolVersion: '1' }));
    await flush();
    expect(store.state.trial).toMatchObject({ status: 'error', revision: '1', error: '不能试看：所选预设包含不适用于文字目标的属性或相对配方。' });

    store.endTrial();
    expect(store.state.trial).toBeUndefined();
    await session.command({ type: 'history.redo' });
    expect(pending).toHaveLength(2);
  });
});

describe('工程里的分配', () => {
  const app = (applicationId: string, target: Partial<PresetApplicationTargetRead> & { kind: string }, extra: Partial<PresetApplication> = {}): PresetApplication => ({
    applicationId,
    target: { includeDescendants: false, ...target },
    targetValid: true,
    enabled: true,
    snapshot: { templateId: 'builtin.line', name: '经典整行', kind: 'line', properties: [], hasEffectRecipes: false, hasPackageMaterials: false, hasPackages: false },
    source: { libraryId: null, revision: null, comparable: false, newVersionAvailable: null },
    branchInputs: {},
    ...extra,
  });

  it('按目标归组；失效目标单独列出；停用的标明；更新只在一个库里找到模板时可用', () => {
    const index = indexApplications([
      app('a1', { kind: 'line', branchId: 'main', lineId: 'l1' }),
      app('a2', { kind: 'line', branchId: 'main', lineId: 'l1' }, { enabled: false }),
      app('a3', { kind: 'line', lineId: 'gone' }, { targetValid: false }),
      app('a4', { kind: 'node', nodeId: 'n1', includeDescendants: true }),
      app('a5', { kind: 'project' }),
    ]);
    expect([...index.byTarget.keys()]).toEqual([targetKey(lineTarget(LINES[0]!)), 'node:n1', 'project']);
    expect(applicationNames(index.byTarget.get('line:main/l1'))).toBe('经典整行、经典整行（已停用）');
    expect(index.invalid.map((a) => [a.applicationId, orphanTargetText(a)])).toEqual([['a3', '已删除的行']]);
    expect(index.other).toEqual([]);
    expect(index.byTarget.get('node:n1')?.map((a) => a.applicationId)).toEqual(['a4']);

    const a = app('a1', { kind: 'project' });
    expect(updateSource(a, (lib) => lib === 'builtin')).toBe('builtin');
    expect(updateSource(a, () => true)).toBeUndefined();
    expect(updateSource(a, () => false)).toBeUndefined();
    expect(updateSource({ ...a, source: { ...a.source, libraryId: 'personal' } }, () => true)).toBe('personal');
  });

  it('保留启用但目标缺失的分配；原编排对象恢复后，重新归到同一目标', () => {
    const target: NodeTarget = { kind: 'node', nodeId: 'harmony', includeDescendants: true };
    const orphan = app('harmony-effect', target, { targetValid: false });
    const project = app('main-style', { kind: 'project' });
    const index = indexApplications([project, orphan]);
    expect(index.byId.get(orphan.applicationId)).toBe(orphan);
    expect(applicationsOn(index, [target])).toEqual([]);
    expect(applicationsOn(index, [PROJECT_TARGET])).toEqual([project]);

    const restored = { ...orphan, targetValid: true };
    const restoredIndex = indexApplications([project, restored]);
    expect(restoredIndex.invalid).toEqual([]);
    expect(applicationsOn(restoredIndex, [target])).toEqual([restored]);
    expect(applicationsOn(restoredIndex, [PROJECT_TARGET])).toEqual([project]);
  });
});

// ── W7 第 8 步：编排对象作为目标（W7 设计 §2.7、§2.8） ──

const placement = { x: 0.5, y: 0.5, anchor: 'Center', rotationDegrees: 0, scale: 1 };
const rect = { widthRatio: 1, heightRatio: 1 };
const onode = (id: string, extra: Partial<OrchestrationNode> = {}): OrchestrationNode => ({ id, placement, referenceRect: rect, ...extra });
const COUNTDOWN: SceneTextAppearance = {
  typography: { fontFamilyName: null, fallbackFamilyNames: [], fontSizePx: 48, fontWeight: 400, isBold: false, isItalic: false, letterSpacingPx: 0 },
  pending: { fill: { kind: 'solid', color: { red: 255, green: 255, blue: 255, alpha: 255 } }, strokes: [], shadow: null, glow: null },
  elapsed: { fill: { kind: 'solid', color: { red: 255, green: 0, blue: 0, alpha: 255 } }, strokes: { kind: 'follow' }, shadow: { kind: 'follow' }, glow: { kind: 'follow' } },
  opacity: 1,
};

/** 主歌词：画面 → 常规（自动）＋ 前奏提示（两个标记）；两个片段，读模型里“c2”在前。 */
const MODEL = buildOrchestration({
  projectSessionId: 's1',
  projectRevision: revision('3'),
  nodes: [
    onode('main/display'),
    onode('main/display/regular', { parentId: 'main/display', layout: { kind: 'Auto', safeArea: { left: 0, top: 0, right: 1, bottom: 1 }, maxWidthRatio: 0.9, slotIds: [] } }),
    onode('cd', { name: '前奏提示', parentId: 'main/display', contentKind: 'countdownSequence', content: { markNodeIds: ['m1', 'm2'], attachmentMode: 'FixedParent', appearance: COUNTDOWN } }),
    onode('m1', { parentId: 'cd', contentKind: 'textMark', content: { text: '3', appearanceOverride: [{ property: 'typography.fontSizePx', value: 40 }] } }),
    onode('m2', { parentId: 'cd', contentKind: 'textMark', content: { text: '2', appearanceOverride: [] } }),
  ],
  programs: [
    {
      id: 'program/main',
      branchId: 'main',
      rootContainerId: 'main/display',
      defaultContainerId: 'main/display/regular',
      clips: [
        { id: 'c2', lineIds: ['l3', 'l4'], containerId: 'main/display/regular' },
        { id: 'c1', name: '主歌', lineIds: ['l1', 'l2'], containerId: 'main/display/regular' },
      ],
    },
  ],
  protocolVersion: '1',
});
const nodeTarget = (nodeId: string, includeDescendants = true): NodeTarget => ({ kind: 'node', nodeId, includeDescendants });

describe('编排对象目标', () => {
  it('点选：节点只选一个；片段只和同一来源的片段加选；同一来源的片段合成一个 clips 目标', () => {
    const [c1, c2] = [clipTarget('main', 'c1'), clipTarget('main', 'c2')];
    expect(pickTarget([nodeTarget('cd')], nodeTarget('m1'), true)).toEqual([nodeTarget('m1')]);
    expect(pickTarget([c1], c2, true)).toEqual([c1, c2]);
    expect(pickTarget([c1], clipTarget('zh', 'c9'), true)).toEqual([clipTarget('zh', 'c9')]);
    expect(pickTarget([c1], nodeTarget('cd'), true)).toEqual([nodeTarget('cd')]);
    expect(commandTargets([c2, nodeTarget('cd', false), c1, clipTarget('zh', 'c9')])).toEqual([
      { kind: 'clips', branchId: 'main', clipIds: ['c2', 'c1'] },
      { kind: 'node', nodeId: 'cd', includeDescendants: false },
      { kind: 'clips', branchId: 'zh', clipIds: ['c9'] },
    ]);
    // 应用范围不属于目标身份，但换了范围是另一次试看。
    expect(targetKey(nodeTarget('cd', false))).toBe(targetKey(nodeTarget('cd')));
    expect(targetsSignature([nodeTarget('cd', false)])).not.toBe(targetsSignature([nodeTarget('cd')]));
  });

  it('路径、数量、试样键与适用规则', () => {
    expect(describeTargets([nodeTarget('m1')], BRANCHES, BY_BRANCH, MODEL)).toBe('主歌词 / 画面 / 前奏提示 / 标记 1');
    expect(describeTargets([nodeTarget('m1')], BRANCHES, BY_BRANCH)).toBe('编排对象');
    // 没有名称的片段按来源里的顺序编号：c1 从第 1 句开始，排在前面。
    expect(describeTargets([clipTarget('main', 'c2'), clipTarget('main', 'c1')], BRANCHES, BY_BRANCH, MODEL)).toBe('主歌词 / 片段 2、主歌');
    expect(['main/display', 'main/display/regular', 'cd', 'm1'].map((id) => targetCountText([nodeTarget(id)], MODEL))).toEqual(['这个组', '这个容器', '这个前奏提示组', '这个标记']);
    expect(targetCountText([clipTarget('main', 'c1'), clipTarget('main', 'c2')])).toBe('2 个片段');
    expect([nodeTarget('m1'), nodeTarget('cd'), clipTarget('main', 'c1'), PROJECT_TARGET].map((t) => sampleKeyOf(t, MODEL))).toEqual(['mark', 'group', 'clip', 'project']);
    expect(sampleKeyOf(nodeTarget('m1'))).toBe('group');
    expect(applicability(TEMPLATE, 'mark', ['line', 'group'])).toBe('这个模板不能用于标记');
    expect(applicability(TEMPLATE, 'clip', ['line', 'clip'])).toBeUndefined();
    expect(applicability({ ...TEMPLATE, kind: 'token' }, 'group')).toMatch('只能用于文本单元');
  });

  it('编排结构读到之后去掉已不存在的片段和节点；没读到时先保留', () => {
    const targets = [clipTarget('main', 'c1'), clipTarget('main', 'gone')];
    expect(reconcileTargets(targets, ['main'], BY_BRANCH, MODEL)).toEqual([clipTarget('main', 'c1')]);
    expect(reconcileTargets(targets, ['main'], BY_BRANCH)).toBe(targets);
    expect(reconcileTargets([nodeTarget('gone')], ['main'], BY_BRANCH, MODEL)).toEqual([PROJECT_TARGET]);
    expect(reconcileTargets([clipTarget('deleted', 'c1')], ['main'], BY_BRANCH)).toEqual([PROJECT_TARGET]);
  });

  it('分配归到各自的对象上：片段按每个片段，节点写应用范围；失效的写明种类', () => {
    const app = (applicationId: string, target: Partial<PresetApplicationTargetRead> & { kind: string }, targetValid = true): PresetApplication => ({
      applicationId,
      target: { includeDescendants: false, ...target },
      targetValid,
      enabled: true,
      snapshot: { templateId: 't', name: '模板', kind: 'line', properties: [], hasEffectRecipes: false, hasPackageMaterials: false, hasPackages: false },
      source: { libraryId: null, revision: null, comparable: false, newVersionAvailable: null },
      branchInputs: {},
    });
    const index = indexApplications([
      app('a1', { kind: 'clips', branchId: 'main', clipIds: ['c1'] }),
      app('a2', { kind: 'clips', branchId: 'main', clipIds: ['c1', 'c2'] }),
      app('a3', { kind: 'node', nodeId: 'cd', includeDescendants: false }),
      app('a4', { kind: 'node', nodeId: 'gone' }, false),
      app('a5', { kind: 'clips', clipIds: ['gone'] }, false),
      app('a6', { kind: 'future' }),
    ]);
    expect(index.byTarget.get('clip:c1')?.map((a) => a.applicationId)).toEqual(['a1', 'a2']);
    expect(applicationsOn(index, [clipTarget('main', 'c1'), clipTarget('main', 'c2')]).map((a) => a.applicationId)).toEqual(['a1', 'a2']);
    expect(nodeScopeOf(index, 'cd')).toBe(false);
    expect(nodeScopeOf(index, 'm1')).toBeUndefined();
    expect(index.byTarget.get('node:cd')!.map(scopeText)).toEqual(['只作用于对象本身']);
    expect(scopeText(app('x', { kind: 'node', nodeId: 'n', includeDescendants: true }))).toBe('包括里面的内容');
    expect(scopeText(app('x', { kind: 'project' }))).toBeUndefined();
    expect(index.invalid.map(orphanTargetText)).toEqual(['已删除的编排对象', '已删除的片段']);
    expect(index.other.map(orphanTargetText)).toEqual(['认不出的目标（future）']);
  });

  it('提示组的完整外观与标记的覆盖', () => {
    expect(sceneAppearanceRows(COUNTDOWN).map((r) => `${r.label}=${r.text}`)).toEqual([
      '字体=后端默认字体',
      '字号=48 px',
      '字重=400',
      '粗体=否',
      '斜体=否',
      '字距=0 px',
      '未到填充=#ffffff',
      '已过填充=#ff0000',
      '未到描边=关',
      '已过描边=跟随未唱',
      '未到阴影=关',
      '已过阴影=跟随未唱',
      '未到辉光=关',
      '已过辉光=跟随未唱',
      '透明度=100%',
    ]);
    expect(overrideRows(MODEL.byId.get('m1')!.content!.appearanceOverride!).map((r) => r.text)).toEqual(['40 px']);
    const glow = { color: { red: 255, green: 0, blue: 0, alpha: 255 }, opacity: 0.5, spreadPx: 2, blurRadiusPx: 8 };
    const rows = sceneAppearanceRows({ ...COUNTDOWN, pending: { ...COUNTDOWN.pending, glow }, elapsed: { ...COUNTDOWN.elapsed, glow: { kind: 'set', value: null } } });
    expect(rows.find((row) => row.property === 'unsung.glow')?.text).toBe('#ff0000 · 不透明度 50% · 扩张 2 px · 模糊 8 px');
    expect(rows.find((row) => row.property === 'sung.glow')?.text).toBe('关');
  });

  it('外观状态：片段合成一个目标应用；节点的应用范围可以改，不读样式；编排结构更新后去掉失效目标', async () => {
    const reads: string[] = [];
    const { store, commands } = fakeSession({ supports: (f) => f === 'project-sessions.style.read' });
    (store as unknown as { client: MakoClient }).client = {
      projects: { style: async (_id: string, target: { kind: string }) => (reads.push(target.kind), new Promise(() => undefined)) },
    } as unknown as MakoClient;
    store.setOrchestration(MODEL);
    store.setTargets([clipTarget('main', 'c1'), clipTarget('main', 'c2')]);
    await store.apply('builtin', 'lib-r1', TEMPLATE);
    expect(commands.at(-1)).toMatchObject({ targets: [{ kind: 'clips', branchId: 'main', clipIds: ['c1', 'c2'] }] });
    expect(store.state.lastApplied?.targetText).toBe('2 个片段');

    store.setTargets([nodeTarget('cd')]);
    store.setIncludeDescendants(false);
    expect(store.state.targets).toEqual([nodeTarget('cd', false)]);
    await store.apply('builtin', 'lib-r1', TEMPLATE);
    expect(commands.at(-1)).toMatchObject({ targets: [{ kind: 'node', nodeId: 'cd', includeDescendants: false }] });
    expect(store.state.lastApplied?.targetText).toBe('这个前奏提示组');
    expect(reads).toEqual([]);
    expect(store.state.inspect).toBeUndefined();

    store.setOrchestration(buildOrchestration({ projectSessionId: 's1', projectRevision: revision('4'), nodes: [onode('main/display')], programs: [], protocolVersion: '1' }));
    expect(store.state.targets).toEqual([PROJECT_TARGET]);
  });
});

describe('外观来源', () => {
  const STYLE: CanonicalStyle = {
    typography: { ...COUNTDOWN.typography, fontFamilyName: 'Noto Sans CJK JP', fallbackFamilyNames: ['Arial'] },
    unsung: { fill: { kind: 'solid', color: { red: 255, green: 255, blue: 255, alpha: 255 } }, strokes: [], shadow: null, glow: null },
    sung: { fill: { kind: 'solid', color: { red: 255, green: 0, blue: 0, alpha: 255 } }, strokes: { kind: 'follow' }, shadow: { kind: 'follow' }, glow: { kind: 'follow' } },
    wipe: { featherRatio: 0, highlight: null },
    verticalCenterRatio: 0.5,
    horizontalAlignment: 'center',
    opacity: 1,
    scale: 1,
  };
  const app = (applicationId: string, name: string, target: Partial<PresetApplicationTargetRead> & { kind: string }, targetValid = true): PresetApplication => ({
    applicationId,
    target: { includeDescendants: false, ...target },
    targetValid,
    enabled: true,
    snapshot: { templateId: 't', name, kind: 'line', properties: [], hasEffectRecipes: false, hasPackageMaterials: false, hasPackages: false },
    source: { libraryId: null, revision: null, comparable: false, newVersionAvailable: null },
    branchInputs: {},
  });
  const apps = [
    app('a1', '红字', { kind: 'line', branchId: 'main', lineId: 'l2' }),
    app('a2', '大字', { kind: 'node', nodeId: 'main/display/regular', includeDescendants: true }),
    app('a3', '片段样式', { kind: 'clips', branchId: 'main', clipIds: ['c1'] }),
    app('a4', '光', { kind: 'token', branchId: 'main', lineId: 'l1', tokenId: 'l1-t1' }),
    app('a5', '旧', { kind: 'line', branchId: 'main', lineId: 'gone' }, false),
  ];
  const ctx: SourceContext = { branches: BRANCHES, linesByBranch: BY_BRANCH, applications: indexApplications(apps).byId, model: MODEL };

  it('来源路径：工程样式、三层直接覆盖、分配；认不出的原样保留', () => {
    expect(parseSourcePath('lyricStyle')).toEqual({ kind: 'project' });
    expect(parseSourcePath('lyrics.branches.main.styleOverride')).toEqual({ kind: 'branch', branchId: 'main' });
    expect(parseSourcePath('lyrics.branches.main.lines.l2.styleOverride')).toEqual({ kind: 'line', branchId: 'main', lineId: 'l2' });
    expect(parseSourcePath('lyrics.branches.main.lines.l1.tokens.l1-t1.styleOverride')).toEqual({ kind: 'token', branchId: 'main', lineId: 'l1', tokenId: 'l1-t1' });
    expect(parseSourcePath('presetApplications.a1.snapshot.style')).toEqual({ kind: 'application', applicationId: 'a1' });
    expect(parseSourcePath('somewhere.else')).toEqual({ kind: 'unknown', path: 'somewhere.else' });
  });

  it('来源写成一句话，点它选中的目标', () => {
    const text = (path: string) => sourceText(parseSourcePath(path), ctx);
    expect(text('lyricStyle')).toBe('工程样式');
    expect(text('lyrics.branches.main.styleOverride')).toBe('来源“主歌词”的直接覆盖');
    expect(text('lyrics.branches.main.lines.l1.tokens.l1-t1.styleOverride')).toBe('主歌词 / 第 1 行 / 「の」的直接覆盖');
    expect(['a1', 'a2', 'a3', 'a4', 'a5', 'nope'].map((id) => text(`presetApplications.${id}.snapshot.style`))).toEqual([
      '分配“红字”（第 2 行）',
      '分配“大字”（画面 / 常规）',
      '分配“片段样式”（片段“主歌”）',
      '分配“光”（第 1 行「の」）',
      '分配“旧”（目标已不存在）',
      '一个分配（还没读到）',
    ]);
    const target = (path: string) => sourceTarget(parseSourcePath(path), ctx);
    expect(target('lyricStyle')).toEqual(PROJECT_TARGET);
    expect(target('lyrics.branches.main.lines.l2.styleOverride')).toEqual({ kind: 'line', branchId: 'main', lineId: 'l2' });
    expect(target('presetApplications.a2.snapshot.style')).toEqual(nodeTarget('main/display/regular'));
    expect(target('presetApplications.a3.snapshot.style')).toEqual(clipTarget('main', 'c1'));
    expect(target('presetApplications.a5.snapshot.style')).toBeUndefined();
    expect(target('somewhere.else')).toBeUndefined();
  });

  it('按属性分组：组里来源相同时合成一行，不同时逐个属性列出', () => {
    const sources = TEMPLATE_GROUPS.flatMap((g) => g.properties).map((property) => ({
      property,
      sourcePath: property === 'unsung.shadow.color' ? 'presetApplications.a1.snapshot.style' : 'lyricStyle',
    }));
    const groups = sourceGroups({ style: STYLE, sources });
    expect(groups.map((g) => g.label)).toEqual(TEMPLATE_GROUPS.map((g) => g.label));
    expect(groups[0]).toMatchObject({ value: { text: 'Noto Sans CJK JP · 回退：Arial' }, rows: [{ path: 'lyricStyle' }] });
    const unsung = groups.find((g) => g.id === 'unsung')!;
    expect(unsung).toMatchObject({ value: { text: '#ffffff', swatch: 'rgb(255 255 255 / 1)' }, rows: [{ path: 'lyricStyle' }] });
    expect(groups.find((g) => g.id === 'sungShadow')?.value).toEqual({ text: '跟随未唱' });
    const shadow = groups.find((g) => g.id === 'shadow')!;
    expect(shadow.rows.map((r) => `${r.property ? `${STYLE_PROPERTY_TEXT[r.property]} · ` : ''}来自 ${sourceText(r.source, ctx)}`)).toEqual([
      '未唱阴影 · 来自 工程样式',
      '未唱阴影颜色 · 来自 分配“红字”（第 2 行）',
      '未唱阴影不透明度 · 来自 工程样式',
      '未唱阴影角度 · 来自 工程样式',
      '未唱阴影距离 · 来自 工程样式',
      '未唱阴影模糊 · 来自 工程样式',
    ]);
    // 来源里没有的组不列。
    expect(sourceGroups({ style: STYLE, sources: sources.filter((s) => s.property === 'scale') }).map((g) => g.id)).toEqual(['scale']);
  });
});

describe('歌词页的“外观”和“显示在”（界面改版批 4）', () => {
  const app = (name: string, target: Partial<PresetApplicationTargetRead> & { kind: string }, enabled = true): PresetApplication => ({
    applicationId: name,
    target: { includeDescendants: false, ...target },
    targetValid: true,
    enabled,
    snapshot: { templateId: 't', name, kind: 'line', properties: [], hasEffectRecipes: false, hasPackageMaterials: false, hasPackages: false },
    source: { libraryId: null, revision: null, comparable: false, newVersionAvailable: null },
    branchInputs: {},
  });
  const lines = [...LINES, line('l5', ['夜'])];
  const [l1, l2, l3, , l5] = lines as [EditorLine, EditorLine, EditorLine, EditorLine, EditorLine];

  it('显示在：所在片段与容器；不在片段里时是默认容器', () => {
    expect(linePlacement(MODEL, l1, lines)).toEqual({ title: '主歌', detail: '片段 第 1～2 句，容器：常规', clipId: 'c1', containerId: 'main/display/regular' });
    expect(linePlacement(MODEL, l3, lines)?.title).toBe('片段 2');
    expect(linePlacement(MODEL, l5, lines)).toEqual({ title: '常规', detail: '不在片段里，用默认容器', containerId: 'main/display/regular' });
  });

  it('外观：从行往上找最近一层启用的分配；节点要包括里面的内容才算', () => {
    const index = indexApplications([
      app('基础', { kind: 'project' }),
      app('来源样式', { kind: 'branch', branchId: 'main' }),
      app('只对象', { kind: 'node', nodeId: 'main/display/regular', includeDescendants: false }),
      app('大字', { kind: 'node', nodeId: 'main/display', includeDescendants: true }),
      app('片段样式', { kind: 'clips', branchId: 'main', clipIds: ['c1'] }),
      app('红字', { kind: 'line', branchId: 'main', lineId: 'l2' }),
      app('停用', { kind: 'line', branchId: 'main', lineId: 'l1' }, false),
    ]);
    expect(lineAppearance(index, MODEL, l2, lines, '主歌词')).toEqual({ name: '红字', source: '本行直接分配' });
    expect(lineAppearance(index, MODEL, l1, lines, '主歌词')).toEqual({ name: '片段样式', source: '继承自片段「主歌」' });
    expect(lineAppearance(index, MODEL, l3, lines, '主歌词')).toEqual({ name: '大字', source: '继承自「画面」' });
    // 编排结构还没读到时跳过片段和节点。
    expect(lineAppearance(index, undefined, l3, lines, '主歌词')).toEqual({ name: '来源样式', source: '继承自来源「主歌词」' });
    expect(lineAppearance(indexApplications([app('基础', { kind: 'project' })]), MODEL, l3, lines, '主歌词').source).toBe('继承自工程默认');
    expect(lineAppearance(indexApplications([]), MODEL, l3, lines, '主歌词')).toEqual({ source: '没有分配模板，用默认样式' });
  });

  it('前奏提示组外观转换：静态收敛 package 材质、默认空描边、同步歌词配色保留提示组字号字距', async () => {
    expect(DEFAULT_SCENE_TEXT_APPEARANCE.pending.strokes).toEqual([]);
    expect(DEFAULT_SCENE_TEXT_APPEARANCE.typography.fontSizePx).toBe(32);

    const withPackage: SceneTextAppearance = {
      ...DEFAULT_SCENE_TEXT_APPEARANCE,
      pending: {
        fill: { kind: 'package', packageId: 'pkg', revision: '1', exportId: 'f' },
        strokes: [{ material: { kind: 'package', packageId: 'pkg', revision: '1', exportId: 's' }, widthPx: 3, join: 'round' }],
        shadow: null,
        glow: null,
      },
    };
    const staticConverted = toStaticSceneAppearance(withPackage);
    expect(staticConverted.pending.fill).toEqual({ kind: 'solid', color: { red: 255, green: 255, blue: 255, alpha: 255 } });
    expect(staticConverted.pending.strokes[0]?.material).toEqual({ kind: 'solid', color: { red: 255, green: 255, blue: 255, alpha: 255 } });

    const lyricStyle: CanonicalStyle = {
      typography: { fontFamilyName: 'Noto Sans JP', fallbackFamilyNames: ['Meiryo'], fontSizePx: 64, fontWeight: 700, isBold: true, isItalic: false, letterSpacingPx: 2 },
      unsung: { fill: { kind: 'solid', color: { red: 240, green: 240, blue: 240, alpha: 255 } }, strokes: [], shadow: null, glow: null },
      sung: {
        fill: { kind: 'solid', color: { red: 255, green: 90, blue: 160, alpha: 255 } },
        strokes: { kind: 'follow' },
        shadow: { kind: 'follow' },
        glow: { kind: 'follow' },
      },
      wipe: { featherRatio: 0.1, highlight: null },
      verticalCenterRatio: 0.85,
      horizontalAlignment: 'center',
      opacity: 0.9,
      scale: 1,
    };
    const synced = lyricStyleToCountdownAppearance(lyricStyle, { ...DEFAULT_SCENE_TEXT_APPEARANCE, typography: { ...DEFAULT_SCENE_TEXT_APPEARANCE.typography, fontSizePx: 28, letterSpacingPx: 1 } });
    expect(synced.typography).toMatchObject({ fontFamilyName: 'Noto Sans JP', fontSizePx: 28, letterSpacingPx: 1, fontWeight: 700, isBold: true });
    expect(synced.pending.fill).toEqual(lyricStyle.unsung.fill);
    expect(synced.elapsed.fill).toEqual(lyricStyle.sung.fill);
    expect(synced.opacity).toBe(0.9);

    // 通过 AppearanceStore 直接设置外观 + 按 resolvedStyle / fallback staticHierarchy 同步歌词配色。
    const { store, commands } = fakeSession({
      supports: (f) => f === RESOLVED_STYLE_READ,
      projects: {
        resolvedStyle: async () => ({
          projectSessionId: 's1',
          projectRevision: revision('0'),
          context: {
            branchId: 'main',
            lineId: 'l1',
            tokenId: null,
            clipId: null,
            containerId: 'main/display/regular',
            nodeId: 't1',
            textNodeId: 't1',
            nodePath: [],
            contextKind: 'container',
          },
          style: lyricStyle,
          sources: [],
          resolution: 'staticPresetHierarchy',
          finalPixelAppearance: false,
          protocolVersion: '1',
        }),
        style: async () => ({
          projectSessionId: 's1',
          projectRevision: revision('0'),
          target: { kind: 'branch', branchId: 'main' },
          baseline: lyricStyle,
          local: [],
          staticHierarchy: { ...lyricStyle, opacity: 0.7 },
          hasPresetApplications: false,
          resolution: 'staticHierarchy',
          protocolVersion: '1',
        }),
      },
    });
    store.setOrchestration(MODEL);

    expect(await store.setCountdownAppearance('main/display/regular', staticConverted)).toBe('applied');
    expect(commands.at(-1)).toEqual({ type: 'orchestration.setCountdownAppearance', containerId: 'main/display/regular', appearance: staticConverted });

    expect(await store.syncCountdownWithLyricStyle('main/display/regular')).toBe('applied');
    expect(commands.at(-1)).toMatchObject({
      type: 'orchestration.setCountdownAppearance',
      containerId: 'main/display/regular',
      appearance: { opacity: 0.9, elapsed: { fill: lyricStyle.sung.fill } },
    });

    // 无 resolvedStyle 能力时回落到 projects.style。
    const fallback = fakeSession({
      supports: () => false,
      projects: {
        style: async () => ({
          projectSessionId: 's1',
          projectRevision: revision('0'),
          target: { kind: 'branch', branchId: 'main' },
          baseline: lyricStyle,
          local: [],
          staticHierarchy: { ...lyricStyle, opacity: 0.75 },
          hasPresetApplications: false,
          resolution: 'staticHierarchy',
          protocolVersion: '1',
        }),
      },
    });
    expect(await fallback.store.syncCountdownWithLyricStyle('main/display/regular')).toBe('applied');
    expect(fallback.commands.at(-1)).toMatchObject({
      type: 'orchestration.setCountdownAppearance',
      containerId: 'main/display/regular',
      appearance: { opacity: 0.75 },
    });
    store.dispose();
    fallback.store.dispose();
  });
});
