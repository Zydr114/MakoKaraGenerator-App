// 连真实 Server 的编排编辑（W4 设计 §12 起）：G20 结构命令（合同 1.20.0，规范 §13.11）、G20-B 节点创建与同父复制（1.23.0，§13.17）、
// G21 属性与动画（1.21.0／1.22.0，§13.12／§13.13）、G22-A 固定容器参考几何（1.22.0，§13.14）、G23 倒计时容器与手工前奏（§13.15）、
// G24 单句局部变换（§13.16）。用例按顺序共享一个工程。

import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { BackendError, MakoClient, MemoryIdentityStore } from '../../src/client';
import { buildOrchestration } from '../../src/editor/model/orchestration';
import { micros, type ProjectCommand, type ProjectSessionSummary } from '../../src/protocol';
import { ContractRecorder, startTestBackend, type TestBackend } from '../support/backend';
import { makeWav } from '../support/fixtures';

let backend: TestBackend;
let client: MakoClient;
const recorder = new ContractRecorder();
let project: ProjectSessionSummary;
let primary = '';
let lineIds: string[] = [];

beforeAll(async () => {
  backend = await startTestBackend('scene-editing');
  client = await MakoClient.connect(backend.provider, {
    identityStore: new MemoryIdentityStore(),
    environment: null,
    onExchange: recorder.observer,
    jobPollIntervalMs: 1000,
  });
  const audio = await client.assets.upload(new Blob([makeWav(6)]), { fileName: '编排编辑.wav' });
  const lrc = await client.assets.upload(new Blob(['[00:01.00]一行目\n[00:02.50]二行目\n[00:04.00]三行目\n']), { fileName: '编排编辑.lrc' });
  project = await client.projects.create({ name: '编排编辑', audioAssetId: audio.assetId, lyricsAssetId: lrc.assetId, splitRule: 1 });
  primary = (await client.projects.branches(project.projectSessionId, project.projectRevision)).primaryBranchId;
  lineIds = (await client.projects.allLines(project.projectSessionId, primary, project.projectRevision)).map((l) => l.id);
});

afterAll(async () => {
  client?.dispose();
  await backend?.stop();
});

const id = () => project.projectSessionId;
const current = () => client.projects.knownRevision(id())!;
const read = async () => buildOrchestration(await client.projects.orchestration(id(), current()));
const command = (c: ProjectCommand) => client.projects.command(id(), c);
const program = async () => (await read()).programByBranch.get(primary)!;
const childIds = async (parentId: string) => ((await read()).children.get(parentId) ?? []).map((n) => n.id);

async function still(timeUs = 1_500_000): Promise<string> {
  const frame = await client.preview.renderStill({ projectSessionId: id(), sourceRevision: current(), timeUs: micros(timeUs), size: { width: 320, height: 180 } });
  expect(frame.verified).toBe(true);
  return frame.sha256;
}

async function rejected(c: ProjectCommand): Promise<BackendError> {
  const error = await command(c).catch((e: unknown) => e);
  if (error instanceof BackendError) return error;
  throw new Error(`命令 ${c.type} 没有被拒绝`);
}

/** 一条真实变化：修订 +1，区域含 orchestration 与 renderInputs。 */
async function changes(c: ProjectCommand) {
  const before = BigInt(current());
  const response = await command(c);
  expect(response.changed).toBe(true);
  expect(response.changeSet.areas).toEqual(expect.arrayContaining(['orchestration', 'renderInputs']));
  expect(BigInt(current())).toBe(before + 1n);
  return response;
}

/** no-op：changed=false，修订不变。 */
async function noop(c: ProjectCommand) {
  const before = current();
  const response = await command(c);
  expect(response.changed).toBe(false);
  expect(current()).toBe(before);
  return response;
}

let group = '';
let rotation = '';
let slots: string[] = [];

describe('编排编辑（真实 Server）', () => {
  it('能力列表有 G20～G24 的命令与两个读取', async () => {
    const { features } = await client.capabilities();
    const commands = [
      'setNode', 'reparent', 'reorderChildren', 'addSlot', 'removeNode', 'reorderSlots', 'createNode', 'copySubtree',
      'setLayoutDefaultPlacement', 'setNodeGeometry', 'setLayoutSettings', 'setSlotAnimation', 'setProgramAnimationPolicy',
      'createCountdown', 'createLeadIn', 'updateLeadIn', 'deleteLeadIn', 'setLineLocalTransform',
    ];
    for (const type of commands) expect(features).toContain(`project-sessions.commands.orchestration.${type}`);
    expect(features).toContain('project-sessions.orchestration.geometry-reference.read');
    expect(features).toContain('project-sessions.orchestration.line-local-transform.read');
  });

  it('读模型：自动布局容器的 slotAnimation 是 null，程序带空的 leadIns；来源的根是普通组', async () => {
    const model = await read();
    const p = model.programByBranch.get(primary)!;
    expect(p.leadIns).toEqual([]);
    expect(model.byId.get(p.rootContainerId)!.layout ?? null).toBeNull();
    expect(model.byId.get(p.defaultContainerId)!.layout).toMatchObject({ kind: 'Auto', slotAnimation: null });
  });

  it('G20-B 新建：组、自动布局容器、轮换容器（同一次带全部槽位与默认动画）；createdNodeIds 新根在前；撤销重做身份不变', async () => {
    const p = await program();
    const g = await changes({ type: 'orchestration.createNode', kind: 'group', parentId: p.rootContainerId, name: '新组', slotCount: null });
    expect(g.structureResult).toMatchObject({ operation: 'createNode', parentId: p.rootContainerId, changed: true });
    group = g.structureResult!.nodeId!;
    expect(g.structureResult!.createdNodeIds).toEqual([group]);
    const s = await changes({ type: 'orchestration.createNode', kind: 'slotSequence', parentId: group, name: '轮换', slotCount: 3 });
    rotation = s.structureResult!.nodeId!;
    const created = s.structureResult!.createdNodeIds!;
    expect(created).toHaveLength(4);
    expect(created[0]).toBe(rotation);
    const model = await read();
    const node = model.byId.get(rotation)!;
    expect(node).toMatchObject({ name: '轮换', parentId: group, layout: { kind: 'SlotSequence', slotIds: created.slice(1) } });
    expect(node.layout!.slotAnimation).toMatchObject({ maxVisibleLines: 2, revealMode: 'Progress' });
    slots = created.slice(1);
    await command({ type: 'history.undo' });
    expect((await read()).byId.has(rotation)).toBe(false);
    await command({ type: 'history.redo' });
    expect((await read()).byId.get(rotation)!.layout!.slotIds).toEqual(slots);
  });

  it('G20-B 新建拒绝：父级是歌词容器 ContainerInvalid、父级不存在 TargetStale；种类大小写不对、组带槽位数 400；都不改工程', async () => {
    const p = await program();
    const before = current();
    expect(await rejected({ type: 'orchestration.createNode', kind: 'auto', parentId: p.defaultContainerId, name: null, slotCount: null })).toMatchObject({ status: 422, failureCode: 'ContainerInvalid' });
    expect(await rejected({ type: 'orchestration.createNode', kind: 'group', parentId: 'node/不存在', name: null, slotCount: null })).toMatchObject({ status: 422, failureCode: 'TargetStale' });
    expect(await rejected({ type: 'orchestration.createNode', kind: 'Group' as 'group', parentId: null, name: null, slotCount: null })).toMatchObject({ status: 400 });
    expect(await rejected({ type: 'orchestration.createNode', kind: 'group', parentId: null, name: null, slotCount: 2 })).toMatchObject({ status: 400 });
    expect(current()).toBe(before);
  });

  it('G20 改名：同名 no-op；改名与清除名称各一条历史', async () => {
    await noop({ type: 'orchestration.setNode', nodeId: group, name: '新组' });
    const r = await changes({ type: 'orchestration.setNode', nodeId: group, name: '副歌组' });
    expect(r.structureResult).toMatchObject({ operation: 'setNode', nodeId: group, changed: true });
    expect((await read()).byId.get(group)!.name).toBe('副歌组');
    await changes({ type: 'orchestration.setNode', nodeId: group, name: null });
    expect((await read()).byId.get(group)!.name ?? null).toBeNull();
    await command({ type: 'history.undo' });
    expect((await read()).byId.get(group)!.name).toBe('副歌组');
  });

  it('G20 槽位：加槽位（前端给 ID）、重复 ID IdentityConflict、排序、删一个槽位；自动布局容器不能加槽位', async () => {
    const p = await program();
    await changes({ type: 'orchestration.addSlot', containerId: rotation, slotId: 'slot/集成测试', name: '加的槽位' });
    expect((await read()).byId.get(rotation)!.layout!.slotIds).toEqual([...slots, 'slot/集成测试']);
    expect(await rejected({ type: 'orchestration.addSlot', containerId: rotation, slotId: 'slot/集成测试', name: null })).toMatchObject({ status: 422, failureCode: 'IdentityConflict' });
    expect(await rejected({ type: 'orchestration.addSlot', containerId: p.defaultContainerId, slotId: 'slot/自动', name: null })).toMatchObject({ status: 422, failureCode: 'PresentationInvalid' });
    const reordered = ['slot/集成测试', ...slots];
    const r = await changes({ type: 'orchestration.reorderSlots', containerId: rotation, orderedIds: reordered });
    expect(r.structureResult).toMatchObject({ operation: 'reorderSlots', containerId: rotation, orderedIds: reordered });
    await noop({ type: 'orchestration.reorderSlots', containerId: rotation, orderedIds: reordered });
    expect(await rejected({ type: 'orchestration.reorderSlots', containerId: rotation, orderedIds: slots })).toMatchObject({ status: 422 });
    await changes({ type: 'orchestration.removeNode', nodeId: 'slot/集成测试' });
    expect((await read()).byId.get(rotation)!.layout!.slotIds).toEqual(slots);
  });

  it('G20 子节点排序与改父级：完整清单才行；改父级保持世界位置（局部几何跟着变）；不能形成循环，默认容器不能挪出来源的根', async () => {
    const p = await program();
    const order = await childIds(p.rootContainerId);
    expect(order).toEqual([p.defaultContainerId, group]);
    await changes({ type: 'orchestration.reorderChildren', parentId: p.rootContainerId, orderedIds: [group, p.defaultContainerId] });
    expect(await childIds(p.rootContainerId)).toEqual([group, p.defaultContainerId]);
    await noop({ type: 'orchestration.reorderChildren', parentId: p.rootContainerId, orderedIds: [group, p.defaultContainerId] });
    expect(await rejected({ type: 'orchestration.reorderChildren', parentId: p.rootContainerId, orderedIds: [group] })).toMatchObject({ status: 422, failureCode: 'PresentationInvalid' });
    // 先把组缩小移开，再把轮换容器挂到来源的根下：世界位置不变，所以局部几何要变。
    const g = (await read()).byId.get(group)!;
    await changes({ type: 'orchestration.setNodeGeometry', nodeId: group, placement: { ...g.placement, x: 0.25 }, referenceRect: { widthRatio: 0.5, heightRatio: 0.5 } });
    const worldBefore = (await client.projects.geometryReference(id(), current(), rotation)).reference.worldCorners;
    const r = await changes({ type: 'orchestration.reparent', nodeId: rotation, parentId: p.rootContainerId });
    expect(r.structureResult).toMatchObject({ operation: 'reparent', nodeId: rotation, parentId: p.rootContainerId });
    const moved = (await read()).byId.get(rotation)!;
    expect(moved.parentId).toBe(p.rootContainerId);
    expect(moved.referenceRect.widthRatio).toBeCloseTo(0.5);
    const worldAfter = (await client.projects.geometryReference(id(), current(), rotation)).reference.worldCorners;
    worldAfter.forEach((corner, i) => {
      expect(corner.x).toBeCloseTo(worldBefore[i]!.x, 3);
      expect(corner.y).toBeCloseTo(worldBefore[i]!.y, 3);
    });
    await command({ type: 'history.undo' });
    expect((await read()).byId.get(rotation)!.parentId).toBe(group);
    const before = current();
    expect(await rejected({ type: 'orchestration.reparent', nodeId: group, parentId: slots[0]! })).toMatchObject({ status: 422, failureCode: 'PresentationInvalid' });
    expect(await rejected({ type: 'orchestration.reparent', nodeId: p.defaultContainerId, parentId: null })).toMatchObject({ status: 422, failureCode: 'PresentationInvalid' });
    expect(current()).toBe(before);
  });

  it('G20-B 复制：同父复制整棵子树，新 ID 与内部引用重映射；复制单个槽位会追加到原轮换容器', async () => {
    const r = await changes({ type: 'orchestration.copySubtree', nodeId: rotation, name: '轮换 副本' });
    const created = r.structureResult!.createdNodeIds!;
    expect(created).toHaveLength(4);
    const model = await read();
    const copy = model.byId.get(created[0]!)!;
    expect(copy).toMatchObject({ name: '轮换 副本', parentId: group });
    expect(copy.layout!.slotIds).toEqual(created.slice(1));
    expect(copy.layout!.slotAnimation).toEqual(model.byId.get(rotation)!.layout!.slotAnimation);
    const slot = await changes({ type: 'orchestration.copySubtree', nodeId: slots[0]!, name: null });
    const slotCopy = slot.structureResult!.nodeId!;
    expect((await read()).byId.get(rotation)!.layout!.slotIds).toEqual([...slots, slotCopy]);
    await command({ type: 'history.undo' });
    await command({ type: 'history.undo' });
    expect((await read()).byId.has(created[0]!)).toBe(false);
  });

  it('G20 删除：被程序根或默认容器引用的不能删（PresentationInvalid）；删组连子树一起删，一条历史，撤销回来', async () => {
    const p = await program();
    const before = current();
    expect(await rejected({ type: 'orchestration.removeNode', nodeId: p.defaultContainerId })).toMatchObject({ status: 422, failureCode: 'PresentationInvalid' });
    expect(await rejected({ type: 'orchestration.removeNode', nodeId: p.rootContainerId })).toMatchObject({ status: 422, failureCode: 'PresentationInvalid' });
    expect(current()).toBe(before);
    await changes({ type: 'orchestration.removeNode', nodeId: group });
    const model = await read();
    expect([group, rotation, ...slots].some((n) => model.byId.has(n))).toBe(false);
    await command({ type: 'history.undo' });
    expect((await read()).byId.get(rotation)!.layout!.slotIds).toEqual(slots);
  });

  it('G21 属性：外层几何、布局设置、默认位置（null 清除）、槽位动画；同值 no-op；帧跟着变', async () => {
    const p = await program();
    const node = (await read()).byId.get(p.defaultContainerId)!;
    const frame = await still();
    const geometry: ProjectCommand = { type: 'orchestration.setNodeGeometry', nodeId: node.id, placement: { ...node.placement, y: 0.8 }, referenceRect: { widthRatio: 0.8, heightRatio: 0.3 } };
    expect((await changes(geometry)).propertiesResult).toEqual({ operation: 'setNodeGeometry', nodeId: node.id, changed: true });
    await noop(geometry);
    expect(await still()).not.toBe(frame);
    const reference = await client.projects.geometryReference(id(), current(), node.id);
    expect(reference).toMatchObject({ canvasWidth: 1920, canvasHeight: 1080, referenceKind: 'fixedContainerReferenceV1', finalPixelGeometry: false, parentId: p.rootContainerId });
    expect(reference.reference.worldCorners).toEqual([{ x: 192, y: 702 }, { x: 1728, y: 702 }, { x: 1728, y: 1026 }, { x: 192, y: 1026 }]);
    const settings: ProjectCommand = { type: 'orchestration.setLayoutSettings', containerId: node.id, safeArea: { left: 0.1, top: 0, right: 0.9, bottom: 1 }, maxWidthRatio: 0.6 };
    expect((await changes(settings)).propertiesResult).toMatchObject({ operation: 'setLayoutSettings', nodeId: node.id });
    await noop(settings);
    expect(await rejected({ ...settings, maxWidthRatio: 1.5 } as ProjectCommand)).toMatchObject({ status: 400 });
    const placement: ProjectCommand = { type: 'orchestration.setLayoutDefaultPlacement', containerId: node.id, placement: { x: 0.5, y: 0.25, anchor: 'TopCenter', rotationDegrees: 0, scale: 1 } };
    expect((await changes(placement)).layoutResult).toEqual({ containerId: node.id, changed: true });
    expect((await read()).byId.get(node.id)!.layout!.defaultPlacement).toMatchObject({ anchor: 'TopCenter', y: 0.25 });
    await changes({ type: 'orchestration.setLayoutDefaultPlacement', containerId: node.id, placement: null });
    await noop({ type: 'orchestration.setLayoutDefaultPlacement', containerId: node.id, placement: null });
    await noop({ type: 'orchestration.setSlotAnimation', containerId: node.id, slotAnimation: null });
    const animation = (await read()).byId.get(rotation)!.layout!.slotAnimation!;
    const faster = { ...animation, enterDurationUs: '100000', conflictPolicy: 'ReplacePrevious' as const };
    await changes({ type: 'orchestration.setSlotAnimation', containerId: rotation, slotAnimation: faster });
    expect((await read()).byId.get(rotation)!.layout!.slotAnimation).toEqual(faster);
    expect(await rejected({ type: 'orchestration.setSlotAnimation', containerId: rotation, slotAnimation: null })).toMatchObject({ status: 422, failureCode: 'PresentationInvalid' });
  });

  it('G21 动画策略：主歌词没有片段与前奏时可以设 Static，有前奏后只能 Karaoke；null 恢复按角色', async () => {
    await changes({ type: 'orchestration.setProgramAnimationPolicy', branchId: primary, animationPolicy: 'Static' });
    expect((await program()).animationPolicy).toBe('Static');
    await changes({ type: 'orchestration.setProgramAnimationPolicy', branchId: primary, animationPolicy: null });
    expect((await program()).animationPolicy ?? null).toBeNull();
    await noop({ type: 'orchestration.setProgramAnimationPolicy', branchId: primary, animationPolicy: null });
  });

  it('G23 倒计时容器与手工前奏：新建、读模型时间、帧变化；时间冲突 LeadInTimeConflict；同值 no-op；删除保留提示容器', async () => {
    const p = await program();
    const c = await changes({ type: 'orchestration.createCountdown', parentId: p.rootContainerId, name: null, count: 3 });
    const indicator = c.leadInResult!.indicatorContainerId!;
    expect(c.leadInResult).toMatchObject({ operation: 'createCountdown', changed: true });
    expect(c.leadInResult!.markNodeIds).toHaveLength(3);
    const model = await read();
    expect(model.byId.get(indicator)).toMatchObject({ name: '前奏提示', contentKind: 'countdownSequence', content: { markNodeIds: c.leadInResult!.markNodeIds } });
    expect(model.byId.get(c.leadInResult!.markNodeIds![0]!)).toMatchObject({ contentKind: 'textMark', content: { text: '●' } });
    const frame = await still(500_000);
    const fields = { branchId: primary, leadInId: 'leadin/集成测试', name: null, targetLineId: lineIds[0]!, indicatorContainerId: indicator, durationUs: '900000', enabled: true };
    const created = await changes({ type: 'orchestration.createLeadIn', ...fields });
    expect(created.leadInResult).toEqual({ operation: 'createLeadIn', branchId: primary, leadInId: fields.leadInId, indicatorContainerId: indicator, changed: true });
    expect((await program()).leadIns).toEqual([{ id: fields.leadInId, enabled: true, targetLineId: lineIds[0], indicatorContainerId: indicator, durationUs: '900000', origin: 'Manual', startUs: '100000', endUs: '1000000' }]);
    expect(await still(500_000)).not.toBe(frame);
    await noop({ type: 'orchestration.updateLeadIn', ...fields });
    await changes({ type: 'orchestration.updateLeadIn', ...fields, name: '第一句', durationUs: '600000' });
    expect((await program()).leadIns![0]).toMatchObject({ name: '第一句', startUs: '400000' });
    expect(await rejected({ type: 'orchestration.createLeadIn', ...fields })).toMatchObject({ status: 422, failureCode: 'IdentityConflict' });
    const conflict = { ...fields, leadInId: 'leadin/冲突', targetLineId: lineIds[1]!, durationUs: '1000000' };
    expect(await rejected({ type: 'orchestration.createLeadIn', ...conflict })).toMatchObject({ status: 422, failureCode: 'LeadInTimeConflict' });
    expect(await rejected({ type: 'orchestration.createLeadIn', ...conflict, indicatorContainerId: p.defaultContainerId, durationUs: '100000' })).toMatchObject({ status: 422, failureCode: 'ContainerInvalid' });
    expect(await rejected({ type: 'orchestration.setProgramAnimationPolicy', branchId: primary, animationPolicy: 'Static' })).toMatchObject({ status: 422, failureCode: 'PresentationInvalid' });
    // 有前奏引用的提示容器不能删。
    expect(await rejected({ type: 'orchestration.removeNode', nodeId: indicator })).toMatchObject({ status: 422 });
    await changes({ type: 'orchestration.deleteLeadIn', branchId: primary, leadInId: fields.leadInId });
    expect((await program()).leadIns).toEqual([]);
    expect((await read()).byId.has(indicator)).toBe(true);
    await command({ type: 'history.undo' });
    expect((await program()).leadIns).toHaveLength(1);
  });

  it('G24 单句局部变换：读到 null；设置一条历史、帧变化、读回；同值 no-op；null 清除', async () => {
    const lineId = lineIds[2]!;
    const first = await client.projects.lineLocalTransform(id(), current(), primary, lineId);
    expect(first).toMatchObject({ branchId: primary, lineId, transform: null });
    const frame = await still(4_500_000);
    const transform = { offsetXRatio: 0.1, offsetYRatio: -0.1, rotationDegrees: 10, scale: 1.2 };
    const set = await changes({ type: 'orchestration.setLineLocalTransform', branchId: primary, lineId, transform });
    expect(set.lineLocalTransformResult).toEqual({ branchId: primary, lineId, transform, changed: true });
    expect(set.changeSet.areas).toContain('lineLocalTransform');
    expect(await still(4_500_000)).not.toBe(frame);
    expect((await client.projects.lineLocalTransform(id(), current(), primary, lineId)).transform).toEqual(transform);
    await noop({ type: 'orchestration.setLineLocalTransform', branchId: primary, lineId, transform });
    const cleared = await changes({ type: 'orchestration.setLineLocalTransform', branchId: primary, lineId, transform: null });
    expect(cleared.lineLocalTransformResult).toMatchObject({ transform: null, changed: true });
    expect(await still(4_500_000)).toBe(frame);
    await noop({ type: 'orchestration.setLineLocalTransform', branchId: primary, lineId, transform: null });
    await expect(client.projects.lineLocalTransform(id(), current(), primary, 'line-不存在')).rejects.toMatchObject({ status: 422, failureCode: 'TargetStale' });
  });

  it('G22-A：来源的根的父参考是画布；节点不存在 404', async () => {
    const p = await program();
    const root = await client.projects.geometryReference(id(), current(), p.rootContainerId);
    expect(root.parentId).toBeUndefined();
    expect(root.parentReference.worldCorners).toEqual([{ x: 0, y: 0 }, { x: 1920, y: 0 }, { x: 1920, y: 1080 }, { x: 0, y: 1080 }]);
    await expect(client.projects.geometryReference(id(), current(), 'node/不存在')).rejects.toMatchObject({ status: 404 });
  });

  it('所有 JSON 响应符合冻结 OpenAPI', () => {
    expect(recorder.issues).toEqual([]);
    for (const route of ['GET /api/v1/project-sessions/{id}/orchestration/geometry 200', 'GET /api/v1/project-sessions/{id}/orchestration/line-local-transform 200']) {
      expect([...recorder.validated]).toContain(route);
    }
  });
});

