// 连真实 Server 的编排补齐（合同 1.24.0，规范 §13.18～§13.21）：布局类型切换、工程与单句进入动画、单句展示读取；
// 前奏自动生成（设置、预演、选择应用、删除并排除）、标记增改排、倒计时与标记外观、附着方式；节点模板目标；
// 真实 Worker 同帧测量。用例按顺序共享一个工程。

import { createHash } from 'node:crypto';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { BackendError, MakoClient, MemoryIdentityStore } from '../../src/client';
import { buildOrchestration } from '../../src/editor/model/orchestration';
import {
  micros,
  type LeadInGenerationCandidate,
  type LeadInGenerationSettings,
  type LineEnterAnimation,
  type ProjectCommand,
  type ProjectSessionSummary,
  type Revision,
  type SceneMeasurementRequest,
} from '../../src/protocol';
import { ContractRecorder, startTestBackend, type TestBackend } from '../support/backend';
import { makeWav } from '../support/fixtures';

let backend: TestBackend;
let client: MakoClient;
const recorder = new ContractRecorder();
let project: ProjectSessionSummary;
let primary = '';
let lineIds: string[] = [];

beforeAll(async () => {
  backend = await startTestBackend('scene-completion');
  client = await MakoClient.connect(backend.provider, {
    identityStore: new MemoryIdentityStore(),
    environment: null,
    onExchange: recorder.observer,
    jobPollIntervalMs: 1000,
  });
  const audio = await client.assets.upload(new Blob([makeWav(20)]), { fileName: '编排补齐.wav' });
  const lrc = await client.assets.upload(new Blob(['[00:04.00]一行目\n[00:05.50]二行目\n[00:14.00]三行目\n']), { fileName: '编排补齐.lrc' });
  project = await client.projects.create({ name: '编排补齐', audioAssetId: audio.assetId, lyricsAssetId: lrc.assetId, splitRule: 1 });
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
const node = async (nodeId: string) => (await read()).byId.get(nodeId)!;
const presentation = (lineId: string) => client.projects.linePresentation(id(), current(), primary, lineId);

async function still(timeUs: number): Promise<string> {
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

/** no-op 或只读：changed=false，修订不变。 */
async function noop(c: ProjectCommand) {
  const before = current();
  const response = await command(c);
  expect(response.changed).toBe(false);
  expect(current()).toBe(before);
  return response;
}

const byLine = (candidates: LeadInGenerationCandidate[]) => new Map(candidates.map((c) => [c.targetLineId, c]));

let switched = '';
let indicator = '';
let generated = '';

describe('编排补齐（真实 Server，1.24.0）', () => {
  it('能力列表有 14 种新命令、单句展示读取与同帧测量', async () => {
    const { features } = await client.capabilities();
    const commands = [
      'setLayoutKind', 'setLineEnterAnimation', 'setProjectEnterAnimation', 'setLeadInGeneration', 'previewLeadInGeneration',
      'applyLeadInGeneration', 'deleteLeadInAndExclude', 'setLeadInAttachment', 'addMark', 'setMarkText', 'reorderMarks',
      'spaceMarks', 'setCountdownAppearance', 'setMarkAppearance',
    ];
    for (const type of commands) expect(features).toContain(`project-sessions.commands.orchestration.${type}`);
    expect(features).toContain('project-sessions.orchestration.line-presentation.read');
    expect(features).toContain('orchestration.measurements.scene-measurement-v1');
  });

  it('读模型：工程进入动画与来源的前奏生成设置总是出现，起初都是 null', async () => {
    const raw = await client.projects.orchestration(id(), current());
    expect(raw.projectEnterAnimation).toBeNull();
    expect(raw.programs.map((p) => p.leadInGeneration)).toEqual([null]);
  });

  it('setLayoutKind：自动布局↔轮换，新槽位由后端生成，切回自动时槽位节点保留；同类型 no-op；拒绝不改工程', async () => {
    const p = await program();
    switched = (await command({ type: 'orchestration.createNode', kind: 'auto', parentId: p.rootContainerId, name: '切换', slotCount: null })).structureResult!.nodeId!;
    const toSlots = await changes({ type: 'orchestration.setLayoutKind', containerId: switched, kind: 'SlotSequence', slotIds: [], newSlotCount: 2 });
    expect(toSlots.propertiesResult).toEqual({ operation: 'setLayoutKind', nodeId: switched, changed: true });
    let model = await read();
    const slots = model.byId.get(switched)!.layout!.slotIds!;
    expect(slots).toHaveLength(2);
    expect((model.children.get(switched) ?? []).map((n) => n.id)).toEqual(slots);
    expect(model.byId.get(switched)!.layout).toMatchObject({ kind: 'SlotSequence', maxWidthRatio: 0.9, slotAnimation: { maxVisibleLines: 2 } });

    await changes({ type: 'orchestration.setLayoutKind', containerId: switched, kind: 'Auto', slotIds: [], newSlotCount: 0 });
    model = await read();
    expect(model.byId.get(switched)!.layout).toMatchObject({ kind: 'Auto', slotIds: [], slotAnimation: null });
    expect((model.children.get(switched) ?? []).map((n) => n.id)).toEqual(slots);
    await noop({ type: 'orchestration.setLayoutKind', containerId: switched, kind: 'Auto', slotIds: [], newSlotCount: 0 });

    // 用回原来的槽位节点，再加一个新的。
    await changes({ type: 'orchestration.setLayoutKind', containerId: switched, kind: 'SlotSequence', slotIds: slots, newSlotCount: 1 });
    const reused = (await node(switched)).layout!.slotIds!;
    expect(reused).toHaveLength(3);
    expect(reused.slice(0, 2)).toEqual(slots);

    const before = current();
    expect(await rejected({ type: 'orchestration.setLayoutKind', containerId: switched, kind: 'Auto', slotIds: [], newSlotCount: 1 })).toMatchObject({ status: 400 });
    expect(await rejected({ type: 'orchestration.setLayoutKind', containerId: switched, kind: 'SlotSequence', slotIds: [], newSlotCount: 0 })).toMatchObject({ status: 400 });
    expect(await rejected({ type: 'orchestration.setLayoutKind', containerId: p.rootContainerId, kind: 'Auto', slotIds: [], newSlotCount: 0 })).toMatchObject({ status: 422, failureCode: 'ContainerInvalid' });
    expect(current()).toBe(before);
  });

  it('进入动画：工程默认与本句覆盖；单句展示读到实际生效的动画与来源；时长 canonical 化；null 清除；帧跟着变', async () => {
    const line = lineIds[0]!;
    expect(await presentation(line)).toMatchObject({ branchId: primary, lineId: line, localTransform: null, localEnterAnimation: null, effectiveEnterAnimation: null, animationSource: 'none' });
    const frame = await still(4_100_000);
    const anim: LineEnterAnimation = { durationUs: '400000', easing: 'EaseOut', startOpacity: 0, startScale: 0.8, startOffsetPx: 40 };
    expect((await changes({ type: 'orchestration.setProjectEnterAnimation', animation: anim })).propertiesResult).toEqual({ operation: 'setProjectEnterAnimation', changed: true });
    await noop({ type: 'orchestration.setProjectEnterAnimation', animation: anim });
    expect((await client.projects.orchestration(id(), current())).projectEnterAnimation).toEqual(anim);
    expect(await presentation(line)).toMatchObject({ localEnterAnimation: null, effectiveEnterAnimation: anim, animationSource: 'project' });
    expect(await still(4_100_000)).not.toBe(frame);

    const local = await changes({ type: 'orchestration.setLineEnterAnimation', branchId: primary, lineId: line, animation: { ...anim, durationUs: '0200000', easing: 'Linear' } });
    expect(local.propertiesResult).toMatchObject({ operation: 'setLineEnterAnimation', branchId: primary, changed: true });
    const own = { ...anim, durationUs: '200000', easing: 'Linear' };
    expect(await presentation(line)).toMatchObject({ localEnterAnimation: own, effectiveEnterAnimation: own, animationSource: 'line' });
    expect(await presentation(lineIds[1]!)).toMatchObject({ localEnterAnimation: null, animationSource: 'project' });

    const before = current();
    expect(await rejected({ type: 'orchestration.setLineEnterAnimation', branchId: primary, lineId: line, animation: { ...anim, startScale: 0 } })).toMatchObject({ status: 400 });
    expect(await rejected({ type: 'orchestration.setLineEnterAnimation', branchId: primary, lineId: 'line-不存在', animation: anim })).toMatchObject({ status: 422, failureCode: 'TargetStale' });
    expect(current()).toBe(before);

    await changes({ type: 'orchestration.setLineEnterAnimation', branchId: primary, lineId: line, animation: null });
    expect(await presentation(line)).toMatchObject({ localEnterAnimation: null, animationSource: 'project' });
    await noop({ type: 'orchestration.setLineEnterAnimation', branchId: primary, lineId: line, animation: null });
    await changes({ type: 'orchestration.setProjectEnterAnimation', animation: null });
    expect(await presentation(line)).toMatchObject({ effectiveEnterAnimation: null, animationSource: 'none' });
    expect(await still(4_100_000)).toBe(frame);
  });

  it('前奏生成：保存设置不生成；预演只读；只应用选中的句子；空选择 no-op；删除并排除写进排除列表，撤销回来', async () => {
    // 第二句改成 5.5～7.0 秒，第三句前留出 7 秒间奏。
    await client.projects.setTimeRange(id(), { branchId: primary, lineId: lineIds[1]! }, micros(5_500_000), micros(7_000_000));
    const p = await program();
    indicator = (await command({ type: 'orchestration.createCountdown', parentId: p.rootContainerId, name: null, count: 3 })).leadInResult!.indicatorContainerId!;
    const settings: LeadInGenerationSettings = { includeOpening: true, includeLongGaps: true, gapThresholdUs: '3000000', durationUs: '2000000', indicatorContainerId: indicator, excludedLineIds: [] };

    const saved = await changes({ type: 'orchestration.setLeadInGeneration', branchId: primary, settings });
    expect(saved.leadInGenerationResult).toMatchObject({ operation: 'setLeadInGeneration', branchId: primary, settings, candidates: [], changed: true });
    expect(await program()).toMatchObject({ leadInGeneration: settings, leadIns: [] });
    await noop({ type: 'orchestration.setLeadInGeneration', branchId: primary, settings });

    const preview = (await noop({ type: 'orchestration.previewLeadInGeneration', branchId: primary, settings })).leadInGenerationResult!;
    expect(preview).toMatchObject({ operation: 'previewLeadInGeneration', diagnostics: [], selectedLineIds: [], changed: false });
    let candidates = byLine(preview.candidates);
    expect(candidates.get(lineIds[0]!)).toMatchObject({
      status: 'New',
      reason: 'None',
      startUs: '2000000',
      endUs: '4000000',
      proposed: { targetLineId: lineIds[0], indicatorContainerId: indicator, durationUs: '2000000', origin: 'Generated' },
      anchor: { timeUs: '4000000', usesLineTime: true },
    });
    expect(candidates.get(lineIds[1]!)).toMatchObject({ status: 'Skipped', reason: 'NotEligible' });
    expect(candidates.get(lineIds[2]!)).toMatchObject({ status: 'New', startUs: '12000000', endUs: '14000000' });

    const missing = (await noop({ type: 'orchestration.previewLeadInGeneration', branchId: primary, settings: { ...settings, indicatorContainerId: null } })).leadInGenerationResult!;
    expect(missing.candidates).toEqual([]);
    expect(missing.diagnostics[0]).toMatchObject({ severity: 'Error' });

    await noop({ type: 'orchestration.applyLeadInGeneration', branchId: primary, settings, selectedLineIds: [] });
    const applied = await changes({ type: 'orchestration.applyLeadInGeneration', branchId: primary, settings, selectedLineIds: [lineIds[0]!] });
    expect(applied.leadInGenerationResult).toMatchObject({ operation: 'applyLeadInGeneration', selectedLineIds: [lineIds[0]], changed: true });
    const leadIns = (await program()).leadIns!;
    expect(leadIns).toHaveLength(1);
    expect(leadIns[0]).toMatchObject({ targetLineId: lineIds[0], origin: 'Generated', startUs: '2000000', endUs: '4000000' });
    generated = leadIns[0]!.id;

    candidates = byLine((await noop({ type: 'orchestration.previewLeadInGeneration', branchId: primary, settings })).leadInGenerationResult!.candidates);
    expect(candidates.get(lineIds[0]!)).toMatchObject({ status: 'Preserved', existing: { id: generated } });
    expect(candidates.get(lineIds[2]!)).toMatchObject({ status: 'New' });

    const removed = await changes({ type: 'orchestration.deleteLeadInAndExclude', branchId: primary, leadInId: generated });
    expect(removed.leadInResult).toMatchObject({ operation: 'deleteLeadInAndExclude', branchId: primary, leadInId: generated, changed: true });
    expect(await program()).toMatchObject({ leadIns: [], leadInGeneration: { ...settings, excludedLineIds: [lineIds[0]] } });
    const excluded = byLine((await noop({ type: 'orchestration.previewLeadInGeneration', branchId: primary, settings: { ...settings, excludedLineIds: [lineIds[0]!] } })).leadInGenerationResult!.candidates);
    expect(excluded.get(lineIds[0]!)).toMatchObject({ status: 'Skipped', reason: 'Excluded' });
    await command({ type: 'history.undo' });
    expect(await program()).toMatchObject({ leadIns: [{ id: generated }], leadInGeneration: settings });
  });

  it('标记：增加（后端给 ID）、改字、完整排序、均匀排列；外观覆盖与倒计时外观；拒绝不改工程', async () => {
    const box = await node(indicator);
    expect(box.content).toMatchObject({ attachmentMode: 'CueTargetText', appearance: { opacity: 1, pending: { strokes: [], shadow: null } } });
    const added = await changes({ type: 'orchestration.addMark', containerId: indicator, text: '★' });
    expect(added.leadInResult).toMatchObject({ operation: 'addMark', indicatorContainerId: indicator, changed: true });
    const mark = added.leadInResult!.markNodeIds![0]!;
    let marks = (await node(indicator)).content!.markNodeIds!;
    expect(marks).toHaveLength(4);
    expect(marks[3]).toBe(mark);
    expect((await node(mark)).content).toEqual({ text: '★', appearanceOverride: [] });

    await changes({ type: 'orchestration.setMarkText', nodeId: marks[0]!, text: '３' });
    expect((await node(marks[0]!)).content!.text).toBe('３');
    const reversed = [...marks].reverse();
    expect((await changes({ type: 'orchestration.reorderMarks', containerId: indicator, orderedIds: reversed })).leadInResult!.markNodeIds).toEqual(reversed);
    await noop({ type: 'orchestration.reorderMarks', containerId: indicator, orderedIds: reversed });
    await changes({ type: 'orchestration.spaceMarks', containerId: indicator });
    await noop({ type: 'orchestration.spaceMarks', containerId: indicator });
    const model = await read();
    expect(reversed.map((m) => model.byId.get(m)!.placement.x)).toEqual([0.125, 0.375, 0.625, 0.875]);
    expect(reversed.map((m) => model.byId.get(m)!.referenceRect.widthRatio)).toEqual([0.25, 0.25, 0.25, 0.25]);

    await changes({ type: 'orchestration.setMarkAppearance', nodeId: mark, changes: [{ property: 'opacity', value: 0.5 }] });
    expect((await node(mark)).content!.appearanceOverride).toEqual([{ property: 'opacity', value: 0.5 }]);
    await changes({ type: 'orchestration.setMarkAppearance', nodeId: mark, changes: [] });
    expect((await node(mark)).content!.appearanceOverride).toEqual([]);
    const appearance = { ...(await node(indicator)).content!.appearance!, opacity: 0.6 };
    await changes({ type: 'orchestration.setCountdownAppearance', containerId: indicator, appearance: appearance as never });
    expect((await node(indicator)).content!.appearance).toEqual(appearance);
    await noop({ type: 'orchestration.setCountdownAppearance', containerId: indicator, appearance: appearance as never });

    const before = current();
    expect(await rejected({ type: 'orchestration.setMarkText', nodeId: mark, text: '  ' })).toMatchObject({ status: 400 });
    expect(await rejected({ type: 'orchestration.reorderMarks', containerId: indicator, orderedIds: reversed.slice(1) })).toMatchObject({ status: 422, failureCode: 'PresentationInvalid' });
    expect(await rejected({ type: 'orchestration.setMarkAppearance', nodeId: mark, changes: [{ property: 'scale', value: 2 }] })).toMatchObject({ status: 400 });
    expect(current()).toBe(before);
    // 删除标记用 removeNode。
    await changes({ type: 'orchestration.removeNode', nodeId: mark });
    marks = (await node(indicator)).content!.markNodeIds!;
    expect(marks).toEqual(reversed.slice(1));
  });

  it('附着方式：跟着文字↔固定在父级（后端换算位置）；同方式 no-op；不是这个前奏的提示组 422', async () => {
    const cue = await node(indicator);
    const fixed = await changes({ type: 'orchestration.setLeadInAttachment', branchId: primary, leadInId: generated, containerId: indicator, mode: 'FixedParent' });
    expect(fixed.propertiesResult).toEqual({ operation: 'setLeadInAttachment', nodeId: indicator, branchId: primary, changed: true });
    const parented = await node(indicator);
    expect(parented.content!.attachmentMode).toBe('FixedParent');
    // 换成固定在父级时，位置按真实文字参考换算成父级里的坐标（画面上不动）。
    expect(parented.placement).toMatchObject({ anchor: cue.placement.anchor, y: 0.5 });
    expect(parented.placement.x).toBeGreaterThan(0);
    await noop({ type: 'orchestration.setLeadInAttachment', branchId: primary, leadInId: generated, containerId: indicator, mode: 'FixedParent' });
    await changes({ type: 'orchestration.setLeadInAttachment', branchId: primary, leadInId: generated, containerId: indicator, mode: 'CueTargetText' });
    expect(await node(indicator)).toMatchObject({ placement: cue.placement, content: { attachmentMode: 'CueTargetText' } });
    const p = await program();
    // R8 已在 1.24.1 修复：容器不是这个前奏的提示组时带 ContainerInvalid 与字段；前奏不存在是 TargetStale。
    expect(await rejected({ type: 'orchestration.setLeadInAttachment', branchId: primary, leadInId: generated, containerId: p.defaultContainerId, mode: 'FixedParent' })).toMatchObject({
      status: 422,
      failureCode: 'ContainerInvalid',
      field: 'command.containerId',
    });
    expect(await rejected({ type: 'orchestration.setLeadInAttachment', branchId: primary, leadInId: 'lead-in/missing', containerId: indicator, mode: 'FixedParent' })).toMatchObject({
      status: 422,
      failureCode: 'TargetStale',
      field: 'command.leadInId',
    });
  });

  it('节点模板目标：静态模板可以用于编排节点（含后代）；文本单元类模板不行；撤销移除分配', async () => {
    const p = await program();
    const revision = (await client.templates.listAll('builtin')).revision;
    const apply = (templateId: string, includeDescendants: boolean): ProjectCommand => ({
      type: 'presetApplications.apply',
      templateLibraryId: 'builtin',
      templateId,
      templateRevision: revision,
      targets: [{ kind: 'node', nodeId: p.defaultContainerId, includeDescendants }],
    });
    const response = await command(apply('builtin:line-classic', true));
    expect(response.changed).toBe(true);
    expect((await client.projects.presetApplications(id(), current())).applications.map((a) => a.target)).toEqual([{ kind: 'node', nodeId: p.defaultContainerId, includeDescendants: true }]);
    expect(await rejected(apply('builtin:token-highlight', false))).toMatchObject({ status: 422 });
    await command({ type: 'history.undo' });
    expect((await client.projects.presetApplications(id(), current())).applications).toEqual([]);
  });

  it('同帧测量：layout 用代表试样（轮换的每个槽位一行，slotId 是槽位节点），time 按时刻采样；PNG 与 SHA 对得上；只读；拒绝', async () => {
    const before = current();
    const request = (patch: Partial<SceneMeasurementRequest>): SceneMeasurementRequest => ({ expectedRevision: current(), mode: 'layout', timeUs: null, width: 320, height: 180, alphaMode: 'opaque', ...patch });
    const layout = (await client.projects.measureScene(id(), request({}))).measurement;
    expect(layout).toMatchObject({ mode: 'layout', timeUs: null, samplingProfile: 'layout-reference-v1', canvasWidth: 1920, canvasHeight: 1080, width: 320, height: 180, alphaMode: 'opaque' });
    const png = Buffer.from(layout.png, 'base64');
    expect(png.subarray(1, 4).toString()).toBe('PNG');
    expect(createHash('sha256').update(png).digest('hex')).toBe(layout.pngSha256);
    const slots = (await node(switched)).layout!.slotIds!;
    const representative = layout.elements.filter((e) => e.kind === 'line' && e.representative);
    for (const slot of slots) expect(representative.find((e) => e.parentId === slot)).toMatchObject({ slotId: slot, geometryStatus: 'measuredInk', visible: true });
    expect(layout.elements.filter((e) => e.kind === 'line' && !e.representative).every((e) => e.geometryStatus === 'notVisible')).toBe(true);
    expect(layout.elements.filter((e) => e.kind === 'mark').every((e) => e.visible)).toBe(true);

    const time = (await client.projects.measureScene(id(), request({ mode: 'time', timeUs: '4500000', alphaMode: 'transparent' }))).measurement;
    expect(time).toMatchObject({ mode: 'time', timeUs: '4500000', samplingProfile: 'time-v1', alphaMode: 'transparent' });
    expect(time.sampleInputFingerprint).not.toBe(layout.sampleInputFingerprint);
    const lines = new Map(time.elements.filter((e) => e.kind === 'line').map((e) => [e.lineId, e]));
    expect(lines.get(lineIds[0]!)).toMatchObject({ representative: false, visible: true, geometryStatus: 'measuredInk', parentId: (await program()).defaultContainerId });
    expect(lines.get(lineIds[0]!)!.worldCorners).toHaveLength(4);
    expect(lines.get(lineIds[2]!)).toMatchObject({ visible: false, geometryStatus: 'notVisible', worldCorners: [] });
    expect(time.elements.find((e) => e.id === switched)).toMatchObject({ kind: 'container', geometryStatus: 'emptyReference', worldCorners: [] });
    expect(current()).toBe(before);

    for (const bad of [request({ timeUs: '0' }), request({ mode: 'time', timeUs: '01' }), request({ width: 1281 })]) {
      await expect(client.projects.measureScene(id(), bad)).rejects.toMatchObject({ status: 400 });
    }
    await expect(client.projects.measureScene(id(), request({ expectedRevision: '1' as Revision }))).rejects.toMatchObject({ status: 409, code: 'revisionConflict' });
  });

  it('所有 JSON 响应符合冻结 OpenAPI', () => {
    expect(recorder.issues).toEqual([]);
    for (const route of ['GET /api/v1/project-sessions/{id}/orchestration/line-presentation 200', 'POST /api/v1/project-sessions/{id}/orchestration/measurements 200']) {
      expect([...recorder.validated]).toContain(route);
    }
  });
});
