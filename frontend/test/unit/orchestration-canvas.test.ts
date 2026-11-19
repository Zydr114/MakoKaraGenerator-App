// 编排页画布与属性（W4 第六批）：画布几何、属性的换算与命令、同帧测量。
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { BackendError, type MakoClient } from '../../src/client';
import { buildOrchestration } from '../../src/editor/model/orchestration';
import {
  anchorOptions,
  animationPolicyBlock,
  animationPolicyCommand,
  animationPolicyOptions,
  autoLayoutCommand,
  defaultContainerBlock,
  defaultPlacementCommand,
  EASING_OPTIONS,
  effectivePolicy,
  fromSeconds,
  geometryCommand,
  karaokeOnlyReason,
  layoutSettingsCommand,
  reusableSlotChildren,
  safeAreaBlock,
  SLOT_DEFAULT_NEEDS_KARAOKE,
  slotAnimationCommand,
  slotSequenceBlock,
  slotSequenceCommand,
  toPercent,
  toSeconds,
  withCurrent,
} from '../../src/editor/model/orchestration-properties';
import { measurementFailureText, sameWant, SceneMeasurementController, type MeasurementWant } from '../../src/editor/orchestration/scene-measurement';
import type { RenderBackendRequest } from '../../src/editor/model/render-device';
import {
  coversCanvas,
  dragReference,
  hitLine,
  hitNode,
  lineDragReference,
  overlayShapes,
  pointInQuad,
  quadArea,
  quads,
  ratioDelta,
  toCanvasPoint,
  type Quad,
} from '../../src/editor/model/scene-geometry';
import type {
  GeometryPoint,
  GeometryReference,
  OrchestrationNode,
  OrchestrationProgram,
  ProjectOrchestrationResponse,
  SceneMeasurement,
  SceneMeasurementElement,
  SceneMeasurementRequest,
  SceneMeasurementResponse,
  SlotAnimation,
} from '../../src/protocol';
import { revision } from '../../src/protocol';

const placement = { x: 0.5, y: 0.5, anchor: 'Center', rotationDegrees: 0, scale: 1 };
const rect = { widthRatio: 1, heightRatio: 1 };
const layout = { kind: 'Auto', safeArea: { left: 0, top: 0, right: 1, bottom: 1 }, maxWidthRatio: 0.9, slotIds: [] as string[], slotAnimation: null };
const node = (id: string, extra: Partial<OrchestrationNode> = {}): OrchestrationNode => ({ id, placement, referenceRect: rect, ...extra });
const program = (extra: Partial<OrchestrationProgram> = {}): OrchestrationProgram => ({
  id: 'program/p',
  branchId: 'p',
  rootContainerId: 'root',
  defaultContainerId: 'regular',
  clips: [],
  leadIns: [],
  ...extra,
});
const model = (nodes: OrchestrationNode[], programs: OrchestrationProgram[] = [program()]) =>
  buildOrchestration({ projectSessionId: 's', projectRevision: revision('3'), nodes, programs, protocolVersion: '1' } as ProjectOrchestrationResponse);

// ── 画布几何 ──

const rectCorners = (x: number, y: number, w: number, h: number): GeometryPoint[] => [
  { x, y },
  { x: x + w, y },
  { x: x + w, y: y + h },
  { x, y: y + h },
];
const identity = { a: 1, b: 0, c: 0, d: 1, tx: 0, ty: 0 };
const reference = (x: number, y: number, w: number, h: number, matrix = { ...identity, tx: x, ty: y }): GeometryReference => ({
  width: w,
  height: h,
  worldMatrix: matrix,
  pivot: { x: x + w / 2, y: y + h / 2 },
  worldCorners: rectCorners(x, y, w, h),
});
const CANVAS = reference(0, 0, 1920, 1080);
const element = (id: string, kind: string, extra: Partial<SceneMeasurementElement> = {}): SceneMeasurementElement => ({
  id,
  kind,
  representative: false,
  visible: true,
  opacity: 1,
  geometryStatus: 'measuredInk',
  worldMatrix: identity,
  pivot: null,
  worldCorners: [],
  fixedReference: null,
  editReference: null,
  ...extra,
});
const measurement = (elements: SceneMeasurementElement[]): SceneMeasurement => ({
  sourceSha256: '',
  sampleInputFingerprint: '',
  sourceProfile: '',
  samplingProfile: 'time-v1',
  mode: 'time',
  timeUs: '0',
  canvasWidth: 1920,
  canvasHeight: 1080,
  width: 320,
  height: 180,
  alphaMode: 'opaque',
  elements,
  pngSha256: '',
  png: '',
});

describe('画布几何', () => {
  const square: Quad = [
    { x: 0, y: 0 },
    { x: 10, y: 0 },
    { x: 10, y: 10 },
    { x: 0, y: 10 },
  ];

  it('角点按 4 个一组，尾巴丢掉；面积；点在四边形里（含边上、旋转后、两种绕向）；退化的不算', () => {
    expect(quads([...rectCorners(0, 0, 1, 1), ...rectCorners(5, 5, 1, 1), { x: 9, y: 9 }])).toHaveLength(2);
    expect(quadArea(square)).toBe(100);
    expect(pointInQuad({ x: 5, y: 5 }, square)).toBe(true);
    expect(pointInQuad({ x: 10, y: 5 }, square)).toBe(true);
    expect(pointInQuad({ x: 11, y: 5 }, square)).toBe(false);
    expect(pointInQuad({ x: 5, y: 5 }, [...square].reverse() as unknown as Quad)).toBe(true);
    const diamond: Quad = [
      { x: 5, y: 0 },
      { x: 10, y: 5 },
      { x: 5, y: 10 },
      { x: 0, y: 5 },
    ];
    expect(pointInQuad({ x: 5, y: 5 }, diamond)).toBe(true);
    expect(pointInQuad({ x: 1, y: 1 }, diamond)).toBe(false);
    const flat: Quad = [square[0], square[0], square[0], square[0]];
    expect(pointInQuad(square[0], flat)).toBe(false);
  });

  it('铺满画布：面积达到画布的 90%（整画布大小、只是挪了位置的槽位也算）', () => {
    expect(coversCanvas(quads(rectCorners(0, 0, 1920, 1080))[0]!, 1920, 1080)).toBe(true);
    expect(coversCanvas(quads(rectCorners(96, 108, 1920, 1080))[0]!, 1920, 1080)).toBe(true);
    expect(coversCanvas(quads(rectCorners(480, 270, 960, 540))[0]!, 1920, 1080)).toBe(false);
  });

  it('显示坐标换成画布坐标', () => {
    expect(toCanvasPoint(320, 90, { width: 640, height: 360 }, { width: 1920, height: 1080 })).toEqual({ x: 960, y: 270 });
  });

  it('位移换算：按父级编辑参考的逆矩阵换成局部位移再除以宽高；旋转 90° 的父级把画布的横移换成纵向；奇异矩阵不能换算', () => {
    expect(ratioDelta(CANVAS, 192, -108)).toEqual({ dx: 0.1, dy: -0.1 });
    const scaled = reference(0, 0, 960, 540, { a: 2, b: 0, c: 0, d: 2, tx: 0, ty: 0 });
    expect(ratioDelta(scaled, 192, 108)).toEqual({ dx: 0.1, dy: 0.1 });
    const rotated = reference(0, 0, 1000, 500, { a: 0, b: 1, c: -1, d: 0, tx: 0, ty: 0 });
    const delta = ratioDelta(rotated, 100, 0)!;
    expect(delta.dx).toBeCloseTo(0);
    expect(delta.dy).toBeCloseTo(-0.2);
    expect(ratioDelta(reference(0, 0, 10, 10, { a: 0, b: 0, c: 0, d: 0, tx: 0, ty: 0 }), 1, 1)).toBeUndefined();
  });

  // 结构：来源的根（整画布）→ 默认容器 regular（整画布）与轮换 k（中间 960×540）→ 槽位 s1、s2；提示组 cue 下的标记 m1。
  const nodes = [
    node('root'),
    node('regular', { parentId: 'root', layout }),
    node('k', { parentId: 'root', layout: { ...layout, kind: 'SlotSequence', slotIds: ['s1', 's2'] } }),
    node('s1', { parentId: 'k' }),
    node('s2', { parentId: 'k' }),
    node('cue', { parentId: 'root', contentKind: 'countdownSequence', content: { markNodeIds: ['m1'] } }),
    node('m1', { parentId: 'cue', contentKind: 'textMark', content: { text: '●' } }),
  ];
  const m = model(nodes);
  const lineInk = rectCorners(100, 900, 400, 60);
  const scene = measurement([
    element('root', 'container', { fixedReference: CANVAS, editReference: CANVAS, worldCorners: [...lineInk, ...rectCorners(700, 400, 200, 50)] }),
    element('regular', 'container', { fixedReference: CANVAS, editReference: CANVAS, worldCorners: lineInk }),
    element('k', 'container', { fixedReference: reference(480, 270, 960, 540), editReference: CANVAS, worldCorners: rectCorners(700, 400, 200, 50) }),
    element('s1', 'slot', { fixedReference: reference(480, 270, 960, 270), editReference: reference(480, 270, 960, 540), worldCorners: rectCorners(700, 400, 200, 50) }),
    element('s2', 'slot', { geometryStatus: 'emptyReference', visible: false, fixedReference: reference(480, 540, 960, 270), editReference: reference(480, 270, 960, 540) }),
    element('text/l1', 'line', { parentId: 'regular', lineId: 'l1', worldCorners: lineInk }),
    element('text/l1/ruby', 'ruby', { parentId: 'text/l1', worldCorners: rectCorners(100, 870, 100, 20) }),
    element('text/l2', 'line', { parentId: 's1', lineId: 'l2', slotId: 's1', worldCorners: rectCorners(700, 400, 200, 50) }),
    element('text/l3', 'line', { parentId: 'k', lineId: 'l3', visible: false, geometryStatus: 'notVisible', worldMatrix: null }),
    element('m1', 'mark', { parentId: 'cue', worldCorners: rectCorners(60, 900, 30, 30), fixedReference: reference(60, 900, 30, 30), editReference: CANVAS }),
    element('internal', 'container', { fixedReference: reference(0, 0, 50, 50) }),
  ]);

  it('点选顺序：句子的墨迹选它所在的容器或槽位，注音沿父级找到容器，标记选自己；否则选包含这一点、面积最小的参考框；铺满画布的不算；都没有时不选', () => {
    expect(hitNode(scene, m, { x: 200, y: 930 })).toBe('regular');
    expect(hitNode(scene, m, { x: 150, y: 880 })).toBe('regular');
    expect(hitNode(scene, m, { x: 800, y: 420 })).toBe('s1');
    expect(hitNode(scene, m, { x: 70, y: 910 })).toBe('m1');
    expect(hitNode(scene, m, { x: 960, y: 700 })).toBe('s2');
    expect(hitNode(scene, m, { x: 500, y: 300 })).toBe('s1');
    expect(hitNode(scene, m, { x: 1500, y: 100 })).toBeUndefined();
    expect(hitNode(scene, m, { x: 10, y: 10 })).toBeUndefined();
  });

  it('拖动起点：普通节点按在参考框里；铺满画布的要按在它的墨迹上；没有编辑参考的不能拖', () => {
    expect(dragReference(scene, 'k', { x: 500, y: 300 })).toEqual(CANVAS);
    expect(dragReference(scene, 'k', { x: 100, y: 100 })).toBeUndefined();
    expect(dragReference(scene, 'regular', { x: 1500, y: 100 })).toBeUndefined();
    expect(dragReference(scene, 'regular', { x: 200, y: 930 })).toEqual(CANVAS);
    expect(dragReference(scene, 'internal', { x: 10, y: 10 })).toBeUndefined();
    expect(dragReference(scene, 'missing', { x: 10, y: 10 })).toBeUndefined();
  });

  it('叠加层：结构里的容器和槽位画参考框，可见的墨迹都画；选中的节点给出参考框和全部后代墨迹', () => {
    const shapes = overlayShapes(scene, m, 'root');
    expect(shapes.frames.map((f) => f.id)).toEqual(['root', 'regular', 'k', 's1', 's2']);
    expect(shapes.inks.map((i) => i.id)).toEqual(['text/l1', 'text/l1/ruby', 'text/l2', 'm1']);
    expect(shapes.selectedFrame).toEqual(quads(CANVAS.worldCorners)[0]);
    expect(shapes.selectedInks).toHaveLength(2);
    expect(overlayShapes(scene, m, 's2').selectedInks).toEqual([]);
    expect(overlayShapes(scene, m, undefined)).toMatchObject({ selectedInks: [] });
    expect(overlayShapes(scene, m, undefined).selectedFrame).toBeUndefined();
  });

  it('句子（§14.8）：双击命中句子或注音的墨迹给出来源和句子，标记、代表文字不算；拖动要按在句子的墨迹上；焦点句可见时叠加层只画它的墨迹', () => {
    const ms = model(nodes, [program({ rootContainerId: 'root' })]);
    expect(hitLine(scene, ms, { x: 200, y: 930 })).toEqual({ branchId: 'p', lineId: 'l1' });
    expect(hitLine(scene, ms, { x: 150, y: 880 })).toEqual({ branchId: 'p', lineId: 'l1' });
    expect(hitLine(scene, ms, { x: 800, y: 420 })).toEqual({ branchId: 'p', lineId: 'l2' });
    expect(hitLine(scene, ms, { x: 70, y: 910 })).toBeUndefined();
    expect(hitLine(scene, ms, { x: 1500, y: 100 })).toBeUndefined();
    const layoutScene = measurement([element('rep/s1', 'line', { parentId: 's1', lineId: 'l2', representative: true, worldCorners: rectCorners(700, 400, 200, 50) })]);
    expect(hitLine(layoutScene, ms, { x: 800, y: 420 })).toBeUndefined();
    const tagged = measurement([element('x', 'line', { branchId: 'tr', lineId: 't1', worldCorners: rectCorners(0, 0, 10, 10) })]);
    expect(hitLine(tagged, ms, { x: 5, y: 5 })).toEqual({ branchId: 'tr', lineId: 't1' });

    const edited = measurement([
      element('regular', 'container', { fixedReference: CANVAS, editReference: CANVAS, worldCorners: lineInk }),
      element('text/l1', 'line', { parentId: 'regular', lineId: 'l1', worldCorners: lineInk, editReference: CANVAS }),
      element('text/l3', 'line', { parentId: 'regular', lineId: 'l3', visible: false, editReference: CANVAS }),
    ]);
    expect(lineDragReference(edited, ms, { branchId: 'p', lineId: 'l1' }, { x: 200, y: 930 })).toEqual(CANVAS);
    expect(lineDragReference(edited, ms, { branchId: 'p', lineId: 'l1' }, { x: 1500, y: 100 })).toBeUndefined();
    expect(lineDragReference(edited, ms, { branchId: 'other', lineId: 'l1' }, { x: 200, y: 930 })).toBeUndefined();
    expect(lineDragReference(edited, ms, { branchId: 'p', lineId: 'l3' }, { x: 200, y: 930 })).toBeUndefined();

    const focused = overlayShapes(edited, ms, 'regular', { branchId: 'p', lineId: 'l1' });
    expect(focused).toMatchObject({ line: true, selectedInks: quads(lineInk) });
    expect(focused.selectedFrame).toBeUndefined();
    const hidden = overlayShapes(edited, ms, 'regular', { branchId: 'p', lineId: 'l3' });
    expect(hidden.line).toBeUndefined();
    expect(hidden.selectedFrame).toEqual(quads(CANVAS.worldCorners)[0]);
  });
});

// ── 属性 ──

describe('属性的换算与命令', () => {
  const container = node('c', { placement: { x: 0.3, y: 0.4, anchor: 'TopLeft', rotationDegrees: 10, scale: 1.2 }, referenceRect: { widthRatio: 0.5, heightRatio: 0.5 }, layout });

  it('显示值：比例→百分数、微秒→秒去掉浮点尾巴；秒→微秒取整', () => {
    expect(toPercent(0.3)).toBe(30);
    expect(toPercent(0.123456)).toBe(12.35);
    expect(toSeconds('300000')).toBe(0.3);
    expect(fromSeconds(0.25)).toBe('250000');
    expect(fromSeconds(1.0000004)).toBe('1000000');
  });

  it('外层几何：带完整的位置和参考框，只换改动的项；读模型多出的字段不带回去', () => {
    const extra = { ...container, placement: { ...container.placement, extra: 1 } as never };
    expect(geometryCommand(extra, { placement: { y: 0.9 } })).toEqual({
      type: 'orchestration.setNodeGeometry',
      nodeId: 'c',
      placement: { x: 0.3, y: 0.9, anchor: 'TopLeft', rotationDegrees: 10, scale: 1.2 },
      referenceRect: { widthRatio: 0.5, heightRatio: 0.5 },
    });
    expect(geometryCommand(container, { referenceRect: { widthRatio: 0.8 } })).toMatchObject({ referenceRect: { widthRatio: 0.8, heightRatio: 0.5 } });
  });

  it('默认位置：设置时用当前外层位置作为初值；已有时只改一项；null 清除', () => {
    expect(defaultPlacementCommand(container, {})).toEqual({ type: 'orchestration.setLayoutDefaultPlacement', containerId: 'c', placement: container.placement });
    const withDefault = { ...container, layout: { ...layout, defaultPlacement: placement } };
    expect(defaultPlacementCommand(withDefault, { anchor: 'BottomRight' })).toMatchObject({ placement: { ...placement, anchor: 'BottomRight' } });
    expect(defaultPlacementCommand(withDefault, null)).toMatchObject({ placement: null });
  });

  it('安全区：范围与右大于左、下大于上；布局设置同时带上安全区和最大宽度；不是容器时没有命令', () => {
    expect(safeAreaBlock({ left: 0.1, top: 0, right: 0.9, bottom: 1 })).toBeUndefined();
    expect(safeAreaBlock({ left: 0.5, top: 0, right: 0.5, bottom: 1 })).toBe('右边要大于左边。');
    expect(safeAreaBlock({ left: 0, top: 0.6, right: 1, bottom: 0.4 })).toBe('下边要大于上边。');
    expect(safeAreaBlock({ left: -0.1, top: 0, right: 1, bottom: 1 })).toBe('左边要在 0～100% 之间。');
    expect(layoutSettingsCommand(container, { safeArea: { left: 0.05 } })).toEqual({
      type: 'orchestration.setLayoutSettings',
      containerId: 'c',
      safeArea: { left: 0.05, top: 0, right: 1, bottom: 1 },
      maxWidthRatio: 0.9,
    });
    expect(layoutSettingsCommand(container, { maxWidthRatio: 0.7 })).toMatchObject({ maxWidthRatio: 0.7, safeArea: layout.safeArea });
    expect(layoutSettingsCommand(node('g'), { maxWidthRatio: 0.7 })).toBeUndefined();
  });

  it('布局类型：改成轮换时复用直属普通子节点（槽位留下的），不够的新建；改回自动布局不带槽位', () => {
    const m = model([
      node('root'),
      node('c', { parentId: 'root', layout }),
      node('old1', { parentId: 'c' }),
      node('nested', { parentId: 'c', layout }),
      node('old2', { parentId: 'c' }),
      node('mark', { parentId: 'c', contentKind: 'textMark' }),
    ]);
    expect(reusableSlotChildren(m, 'c').map((n) => n.id)).toEqual(['old1', 'old2']);
    expect(slotSequenceCommand(m, 'c', 3)).toEqual({ type: 'orchestration.setLayoutKind', containerId: 'c', kind: 'SlotSequence', slotIds: ['old1', 'old2'], newSlotCount: 1 });
    expect(slotSequenceCommand(m, 'c', 1)).toMatchObject({ slotIds: ['old1'], newSlotCount: 0 });
    expect(autoLayoutCommand('c')).toEqual({ type: 'orchestration.setLayoutKind', containerId: 'c', kind: 'Auto', slotIds: [], newSlotCount: 0 });
  });

  it('槽位动画：带完整十四个字段，只换改动的项，姿态按项合并', () => {
    const pose = { opacity: 1, scale: 1, offsetXPx: 0, offsetYPx: 0, rotationDegrees: 0 };
    const animation: SlotAnimation = {
      revealMode: 'Progress',
      revealProgress: 0.5,
      revealDelayUs: '0',
      revealLeadUs: '0',
      maxVisibleLines: 2,
      conflictPolicy: 'QueueUntilSlotFree',
      retentionPolicy: 'UntilLineEnd',
      enterDurationUs: '300000',
      exitDurationUs: '250000',
      enterFrom: { ...pose, opacity: 0, offsetYPx: 12 },
      hold: pose,
      exitTo: { ...pose, opacity: 0, offsetYPx: -12 },
      exitEasing: 'EaseIn',
      easing: 'EaseOut',
    };
    const command = slotAnimationCommand('k', animation, { maxVisibleLines: 1, enterFrom: { scale: 0.8 } });
    expect(command).toEqual({ type: 'orchestration.setSlotAnimation', containerId: 'k', slotAnimation: { ...animation, maxVisibleLines: 1, enterFrom: { ...animation.enterFrom, scale: 0.8 } } });
  });

  it('选项：不认识的当前值列在最后、不能选；锚点六个', () => {
    expect(withCurrent(EASING_OPTIONS, 'Bounce').at(-1)).toEqual({ value: 'Bounce', label: 'Bounce', disabled: true });
    expect(withCurrent(EASING_OPTIONS, 'Linear')).toHaveLength(4);
    expect(anchorOptions('Center').map((o) => o.label)).toEqual(['中心', '左上角', '顶部中点', '右下角', '左侧中点', '右侧中点']);
    expect(anchorOptions('Weird').at(-1)).toMatchObject({ value: 'Weird', disabled: true });
  });

  it('动画策略：有片段、前奏或默认容器是轮换时，实际不是逐字的选项不能选并写明原因；按来源角色发 null', () => {
    const nodes = [node('root', { parentId: null }), node('regular', { parentId: 'root', layout })];
    const plain = model(nodes);
    expect(animationPolicyBlock(plain, program(), true, 'Static')).toBeUndefined();
    const withClip = program({ clips: [{ id: 'clip/1', lineIds: ['l1'], containerId: 'regular' }] });
    expect(karaokeOnlyReason(plain, withClip)).toContain('片段或前奏提示');
    expect(animationPolicyBlock(plain, withClip, true, 'Static')).toBe(karaokeOnlyReason(plain, withClip));
    expect(animationPolicyBlock(plain, withClip, true, 'Karaoke')).toBeUndefined();
    // 按来源角色：主歌词是逐字，可以；其他来源是静态，不行
    expect(animationPolicyBlock(plain, withClip, true, 'role')).toBeUndefined();
    expect(animationPolicyBlock(plain, withClip, false, 'role')).toBeDefined();
    const slots = model([node('root', { parentId: null }), node('regular', { parentId: 'root', layout: { ...layout, kind: 'SlotSequence', slotIds: [] } })]);
    expect(karaokeOnlyReason(slots, program())).toContain('默认容器是轮换');
    expect(animationPolicyOptions(slots, program(), true).filter((o) => o.disabled).map((o) => o.value)).toEqual(['Static', 'FollowPrimary']);
    expect(animationPolicyOptions(plain, program({ animationPolicy: 'Custom' }), true).at(-1)).toMatchObject({ value: 'Custom', disabled: true });
    expect(animationPolicyCommand('p', 'role')).toEqual({ type: 'orchestration.setProgramAnimationPolicy', branchId: 'p', animationPolicy: null });
    expect(animationPolicyCommand('p', 'Static')).toMatchObject({ animationPolicy: 'Static' });
  });

  it('轮换要求逐字：动画不是逐字的来源，默认容器不能改成轮换，轮换容器也不能作默认容器', () => {
    const nodes = [node('root', { parentId: null }), node('regular', { parentId: 'root', layout }), node('rot', { parentId: 'root', layout: { ...layout, kind: 'SlotSequence' } })];
    const primary = (id: string) => id === 'p';
    expect(effectivePolicy(null, true)).toBe('Karaoke');
    expect(effectivePolicy(null, false)).toBe('Static');
    expect(slotSequenceBlock(model(nodes), 'regular', primary)).toBeUndefined();
    expect(slotSequenceBlock(model(nodes, [program({ animationPolicy: 'Static' })]), 'regular', primary)).toContain('不能改成轮换');
    expect(slotSequenceBlock(model(nodes, [program({ branchId: 't' })]), 'regular', primary)).toContain('不能改成轮换');
    expect(slotSequenceBlock(model(nodes, [program({ animationPolicy: 'Static' })]), 'rot', primary)).toBeUndefined();
    const rot = model(nodes).byId.get('rot')!;
    expect(defaultContainerBlock(program(), rot, true)).toBeUndefined();
    expect(defaultContainerBlock(program({ animationPolicy: 'FollowPrimary' }), rot, true)).toBe(SLOT_DEFAULT_NEEDS_KARAOKE);
    expect(defaultContainerBlock(program(), model(nodes).byId.get('regular')!, false)).toBeUndefined();
  });
});

// ── 同帧测量 ──

describe('同帧测量', () => {
  beforeEach(() => { vi.useFakeTimers(); });
  afterEach(() => { vi.useRealTimers(); });

  const want = (patch: Partial<MeasurementWant> = {}): MeasurementWant => ({ mode: 'time', revision: revision('3'), timeUs: 1_500_000, width: 320, height: 180, ...patch });
  const problem = (status: number, code: string) => new BackendError(status, { code, message: code, protocolVersion: '1' });

  function setup(device?: RenderBackendRequest) {
    const requests: SceneMeasurementRequest[] = [];
    const pending: { signal: AbortSignal; resolve: (r: SceneMeasurementResponse) => void; reject: (e: unknown) => void }[] = [];
    const client = {
      projects: {
        measureScene: (_id: string, request: SceneMeasurementRequest, signal: AbortSignal) =>
          new Promise<SceneMeasurementResponse>((resolve, reject) => {
            requests.push(request);
            pending.push({ signal, resolve, reject });
          }),
      },
    } as unknown as MakoClient;
    const controller = new SceneMeasurementController(client, 'p1', device ? { device } : undefined);
    const reply = (i: number) =>
      pending[i]!.resolve({ projectSessionId: 'p1', projectRevision: requests[i]!.expectedRevision, measurement: { ...measurement([]), png: btoa('PNG') }, protocolVersion: '1' });
    return { controller, requests, pending, reply };
  }
  const settle = () => vi.advanceTimersByTimeAsync(0);

  it('重复请求不取消；新请求取消旧测量，只发最新且忽略旧回执；layout 给出 PNG', async () => {
    const { controller, requests, pending, reply } = setup();
    controller.measure(want());
    controller.measure(want());
    expect(pending[0]!.signal.aborted).toBe(false);
    controller.measure(want({ timeUs: 2_000_000 }));
    controller.measure(want({ timeUs: 2_500_000 }));
    expect(pending[0]!.signal.aborted).toBe(true);
    expect(requests).toHaveLength(1);
    expect(requests[0]).toEqual({ expectedRevision: '3', mode: 'time', timeUs: '1500000', width: 320, height: 180, alphaMode: 'opaque' });
    expect(controller.state.status).toBe('measuring');
    reply(0);
    await settle();
    expect(requests.map((r) => r.timeUs)).toEqual(['1500000', '2500000']);
    expect(controller.state.result).toBeUndefined();
    expect(pending[1]!.signal.aborted).toBe(false);
    reply(1);
    await settle();
    expect(controller.state).toMatchObject({ status: 'ready', result: { want: want({ timeUs: 2_500_000 }) } });
    expect(controller.state.result?.imageUrl).toBeUndefined();

    controller.measure(want({ timeUs: 2_500_000 }));
    await settle();
    expect(requests).toHaveLength(2);

    controller.measure(want({ mode: 'layout', timeUs: null }));
    expect(requests[2]).toMatchObject({ mode: 'layout', timeUs: null });
    reply(2);
    await settle();
    expect(controller.state.result?.imageUrl).toMatch(/^blob:/);
    controller.dispose();
  });

  it('按渲染设备偏好带 renderBackend；跟随默认时不带（上一项）', () => {
    const { controller, requests } = setup({ choice: () => ({ request: 'gpu', target: 'gpu' }) });
    controller.measure(want());
    expect(requests[0]!.renderBackend).toBe('gpu');
    controller.dispose();
  });

  it('切回已有结果时取消在途请求，恢复就绪且不重新测量', async () => {
    const { controller, requests, pending, reply } = setup();
    controller.measure(want());
    reply(0);
    await settle();
    controller.measure(want({ timeUs: 2_000_000 }));
    controller.measure(want());
    expect(pending[1]!.signal.aborted).toBe(true);
    pending[1]!.reject(problem(429, 'requestQuotaExceeded'));
    await settle();
    expect(controller.state).toMatchObject({ status: 'ready', result: { want: want() } });
    expect(vi.getTimerCount()).toBe(0);
    expect(requests).toHaveLength(2);
    controller.dispose();
  });

  it('销毁时取消在途请求，不发布旧回执也不接受新请求', async () => {
    const { controller, requests, pending, reply } = setup();
    controller.measure(want({ mode: 'layout', timeUs: null }));
    controller.dispose();
    expect(pending[0]!.signal.aborted).toBe(true);
    controller.measure(want());
    reply(0);
    await settle();
    expect(controller.state.result).toBeUndefined();
    expect(requests).toHaveLength(1);
  });

  it('409 等下一次，不算错误；其他状态不按 429 退避', async () => {
    const { controller, requests, pending, reply } = setup();
    controller.measure(want());
    reply(0);
    await settle();
    controller.measure(want({ revision: revision('4') }));
    pending[1]!.reject(problem(409, 'revisionConflict'));
    await settle();
    expect(controller.state).toMatchObject({ status: 'ready', result: { want: want() } });
    expect(controller.state.error).toBeUndefined();

    controller.measure(want({ revision: revision('5') }));
    pending[2]!.reject(problem(503, 'requestQuotaExceeded'));
    await settle();
    expect(controller.state).toMatchObject({ status: 'error', error: '画面测量暂时不可用。', result: { want: want() } });
    expect(vi.getTimerCount()).toBe(0);
    expect(requests).toHaveLength(3);

    expect(measurementFailureText(problem(413, 'resourceTooLarge'))).toBe('画面太复杂，不能测量。');
    expect(measurementFailureText(problem(422, 'taskUnavailable'))).toBe('画面测量暂时不可用。');
    expect(measurementFailureText(new Error('断开'))).toBe('测量失败：断开');
    expect(sameWant(want(), want({ width: 640 }))).toBe(false);
    controller.dispose();
  });

  it('429 按 1、2、4 秒只重试三次，失败后保留上一份结果', async () => {
    const { controller, requests, pending, reply } = setup();
    controller.measure(want());
    reply(0);
    await settle();
    const latest = want({ revision: revision('4') });
    controller.measure(latest);
    for (const [index, delay] of [1_000, 2_000, 4_000].entries()) {
      pending[index + 1]!.reject(problem(429, 'requestQuotaExceeded'));
      await settle();
      controller.measure(latest);
      await vi.advanceTimersByTimeAsync(delay - 1);
      expect(requests).toHaveLength(index + 2);
      expect(controller.state.status).toBe('measuring');
      await vi.advanceTimersByTimeAsync(1);
      expect(requests).toHaveLength(index + 3);
    }
    pending[4]!.reject(problem(429, 'requestQuotaExceeded'));
    await settle();
    expect(controller.state).toMatchObject({ status: 'error', error: '测量请求太多，稍后再试。', result: { want: want() } });
    expect(vi.getTimerCount()).toBe(0);
    expect(requests.slice(1).map((request) => request.expectedRevision)).toEqual(['4', '4', '4', '4']);
    controller.dispose();
  });

  it('新的测量不等待旧请求的退避，销毁也会清除退避计时器', async () => {
    const { controller, requests, pending } = setup();
    controller.measure(want());
    pending[0]!.reject(problem(429, 'requestQuotaExceeded'));
    await settle();
    expect(vi.getTimerCount()).toBe(1);

    controller.measure(want({ timeUs: 2_000_000 }));
    expect(pending[0]!.signal.aborted).toBe(true);
    await settle();
    expect(requests.map((request) => request.timeUs)).toEqual(['1500000', '2000000']);
    expect(vi.getTimerCount()).toBe(0);

    pending[1]!.reject(problem(429, 'requestQuotaExceeded'));
    await settle();
    expect(vi.getTimerCount()).toBe(1);
    controller.dispose();
    expect(pending[1]!.signal.aborted).toBe(true);
    expect(vi.getTimerCount()).toBe(0);
    await vi.advanceTimersByTimeAsync(7_000);
    expect(requests).toHaveLength(2);
    expect(controller.state.error).toBeUndefined();
  });
});
