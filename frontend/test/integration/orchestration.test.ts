// 连真实 Server 的工程编排（合同 1.13.0 W4 第一切片，规范 §13.5；G18 快速布局与 R4 修复，合同 1.17.0，规范 §13.8；
// G19 片段范围与 R6／R7 修复，合同 1.18.0，规范 §13.9；G19-B 片段的新建、改名、改容器、删除，合同 1.19.0，规范 §13.10；
// 片段成员的调整、拆分与合并，合同 1.25.0，规范 §17.7；W4 设计 §3、§10、§11、§15）。
// 用例按顺序共享一个工程：快速布局造出第二个容器后，才测得到“改到另一个容器→撤销”。片段成员另开一个六句的工程。

import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { BackendError, MakoClient, MemoryIdentityStore } from '../../src/client';
import { buildOrchestration, containersOf, nodeName } from '../../src/editor/model/orchestration';
import { micros, revision, type BasicLayoutFields, type BasicLayoutPreset, type ProjectCommand, type ProjectSessionSummary } from '../../src/protocol';
import { ContractRecorder, startTestBackend, type TestBackend } from '../support/backend';
import { makeWav } from '../support/fixtures';

let backend: TestBackend;
let client: MakoClient;
const recorder = new ContractRecorder();
let project: ProjectSessionSummary;
let primary = '';

beforeAll(async () => {
  backend = await startTestBackend('orchestration');
  client = await MakoClient.connect(backend.provider, {
    identityStore: new MemoryIdentityStore(),
    environment: null,
    onExchange: recorder.observer,
    jobPollIntervalMs: 1000,
  });
  const audio = await client.assets.upload(new Blob([makeWav(4)]), { fileName: '编排测试.wav' });
  const lrc = await client.assets.upload(new Blob(['[00:00.50]一行目\n[00:02.00]二行目\n']), { fileName: '编排测试.lrc' });
  project = await client.projects.create({ name: '编排测试', audioAssetId: audio.assetId, lyricsAssetId: lrc.assetId, splitRule: 1 });
  primary = (await client.projects.branches(project.projectSessionId, project.projectRevision)).primaryBranchId;
});

afterAll(async () => {
  client?.dispose();
  await backend?.stop();
});

const id = () => project.projectSessionId;
const current = () => client.projects.knownRevision(id())!;
const read = () => client.projects.orchestration(id(), current());
const command = (c: ProjectCommand) => client.projects.command(id(), c);

async function still(): Promise<string> {
  const frame = await client.preview.renderStill({ projectSessionId: id(), sourceRevision: current(), timeUs: micros(1_000_000), size: { width: 320, height: 180 } });
  expect(frame.verified).toBe(true);
  return frame.sha256;
}

const SLOTS: Record<BasicLayoutPreset, number> = { SingleCentered: 1, KaraokeDouble: 2, DoubleCentered: 2, TripleCentered: 3 };
const layout = (branchId: string, preset: BasicLayoutPreset): BasicLayoutFields => ({
  branchId,
  preset,
  layoutName: `布局-${preset}`,
  slotNames: Array.from({ length: SLOTS[preset] }, (_, i) => `槽位 ${i + 1}`),
  selectedSegmentName: '歌词段布局',
  retainedSegmentName: '保留原布局的歌词段',
});

async function rejected(c: ProjectCommand): Promise<BackendError> {
  const error = await command(c).catch((e: unknown) => e);
  if (error instanceof BackendError) return error;
  throw new Error(`命令 ${c.type} 没有被拒绝`);
}

describe('工程编排（真实 Server）', () => {
  it('能力列表有读模型和设置默认容器', async () => {
    const { features } = await client.capabilities();
    expect(features).toContain('project-sessions.orchestration.read');
    expect(features).toContain('project-sessions.commands.orchestration.setDefaultContainer');
    expect(features).toContain('project-sessions.commands.orchestration.previewBasicLayout');
    expect(features).toContain('project-sessions.commands.orchestration.applyBasicLayout');
    for (const type of ['createClip', 'renameClip', 'setClipContainer', 'deleteClip']) expect(features).toContain(`project-sessions.commands.orchestration.${type}`);
  });

  it('新工程：主歌词一个程序，根（组）下一个自动布局容器，它就是默认容器；没有孤立节点', async () => {
    const response = await read();
    expect(response.projectRevision).toBe(current());
    const model = buildOrchestration(response);
    expect(model.programs).toHaveLength(1);
    const program = model.programByBranch.get(primary)!;
    const root = model.byId.get(program.rootContainerId)!;
    expect(root.parentId ?? null).toBeNull();
    const containers = containersOf(model, program);
    expect(containers.map((n) => n.id)).toEqual([program.defaultContainerId]);
    expect(containers[0]!.layout).toMatchObject({ kind: 'Auto', slotIds: [] });
    expect(nodeName(model, containers[0]!)).toBe('常规');
    expect(program.recognizedLayoutPreset ?? null).toBeNull();
    expect(model.orphans).toEqual([]);
  });

  it('读取用旧修订 409', async () => {
    await command({ type: 'lyrics.setLineText', target: { branchId: primary, lineId: (await client.projects.allLines(id(), primary, current()))[0]!.id }, text: '一行目！' });
    await expect(client.projects.orchestration(id(), revision('0'))).rejects.toMatchObject({ status: 409, code: 'revisionConflict' });
  });

  it('设成当前的默认容器是 no-op：changed=false、没有区域、修订不变，回执仍带 orchestrationResult', async () => {
    const program = (await read()).programs[0]!;
    const before = current();
    const response = await command({ type: 'orchestration.setDefaultContainer', branchId: primary, containerId: program.defaultContainerId });
    expect(response.changed).toBe(false);
    expect(response.changeSet.areas).toEqual([]);
    expect(response.orchestrationResult).toEqual({ branchId: primary, containerId: program.defaultContainerId, changed: false });
    expect(current()).toBe(before);
  });

  it('目标不是歌词容器（来源的根）：422', async () => {
    const program = (await read()).programs[0]!;
    const error = await rejected({ type: 'orchestration.setDefaultContainer', branchId: primary, containerId: program.rootContainerId });
    expect(error).toMatchObject({ status: 422, code: 'commandRejected' });
  });

  let frameBefore = '';
  let counts: unknown;

  it('G18 预演：只回统计，修订、历史、读模型都不变', async () => {
    frameBefore = await still();
    const before = current();
    const structure = await read();
    const response = await command({ type: 'orchestration.previewBasicLayout', ...layout(primary, 'DoubleCentered') });
    expect(response.changed).toBe(false);
    expect(response.changeSet.areas).toEqual([]);
    expect(response.basicLayoutResult).toMatchObject({ branchId: primary, preset: 'DoubleCentered', scope: 'source', recognizedPreset: 'DoubleCentered', affectedLines: 2, splitClips: 0, changed: false });
    const { changed: _, ...rest } = response.basicLayoutResult!;
    counts = rest;
    expect(current()).toBe(before);
    expect((await read()).nodes).toEqual(structure.nodes);
  });

  it('G18 应用：一条历史；根下多出轮换容器和 2 个槽位，成为默认容器并识别为双行居中；统计与预演一致；帧变化', async () => {
    const before = current();
    const old = (await read()).programs[0]!;
    const response = await command({ type: 'orchestration.applyBasicLayout', ...layout(primary, 'DoubleCentered') });
    expect(response.changed).toBe(true);
    expect(BigInt(current())).toBe(BigInt(before) + 1n);
    expect(response.changeSet).toMatchObject({ areas: expect.arrayContaining(['orchestration', 'renderInputs']), requiresFullRefresh: true });
    const { changed, ...rest } = response.basicLayoutResult!;
    expect(changed).toBe(true);
    expect(rest).toEqual(counts);

    const model = buildOrchestration(await read());
    const program = model.programByBranch.get(primary)!;
    expect(program.recognizedLayoutPreset).toBe('DoubleCentered');
    expect(program.defaultContainerId).not.toBe(old.defaultContainerId);
    const container = model.byId.get(program.defaultContainerId)!;
    expect(container).toMatchObject({ name: '布局-DoubleCentered', parentId: program.rootContainerId, layout: { kind: 'SlotSequence' } });
    expect(container.layout!.slotIds.map((slot) => model.byId.get(slot)!.name)).toEqual(['槽位 1', '槽位 2']);
    expect(containersOf(model, program).map((n) => n.id)).toEqual([old.defaultContainerId, program.defaultContainerId]);
    expect(await still()).not.toBe(frameBefore);
  });

  it('G18 再套同一预设是 no-op：changed=false，修订不变', async () => {
    const before = current();
    const response = await command({ type: 'orchestration.applyBasicLayout', ...layout(primary, 'DoubleCentered') });
    expect(response.changed).toBe(false);
    expect(response.basicLayoutResult).toMatchObject({ changed: false, recognizedPreset: 'DoubleCentered' });
    expect(current()).toBe(before);
  });

  it('R6 已修复：有两个容器后把默认容器改回“常规”是一条历史，读模型跟着变，撤销重做都对', async () => {
    const program = (await read()).programs[0]!;
    const regular = `${program.rootContainerId}/regular`;
    const before = current();
    const response = await command({ type: 'orchestration.setDefaultContainer', branchId: primary, containerId: regular });
    expect(response.changed).toBe(true);
    expect(response.orchestrationResult).toEqual({ branchId: primary, containerId: regular, changed: true });
    expect(BigInt(current())).toBe(BigInt(before) + 1n);
    const changed = (await read()).programs[0]!;
    expect(changed.defaultContainerId).toBe(regular);
    expect(changed.recognizedLayoutPreset ?? null).toBeNull();

    await command({ type: 'history.undo' });
    expect((await read()).programs[0]).toEqual(program);
    await command({ type: 'history.redo' });
    expect((await read()).programs[0]!.defaultContainerId).toBe(regular);
    await command({ type: 'history.undo' });
    expect((await read()).programs[0]).toEqual(program);
  });

  it('G18 撤销应用：结构、默认容器与帧都恢复', async () => {
    await command({ type: 'history.undo' });
    const model = buildOrchestration(await read());
    const program = model.programByBranch.get(primary)!;
    expect(containersOf(model, program)).toHaveLength(1);
    expect(nodeName(model, model.byId.get(program.defaultContainerId)!)).toBe('常规');
    expect(program.recognizedLayoutPreset ?? null).toBeNull();
    expect(program.clips ?? []).toEqual([]);
    expect(await still()).toBe(frameBefore);
  });

  it('拒绝：槽位名个数不对 400；范围乱序 DiscontiguousRange、行不存在或重复 InvalidRange；未知预设 InvalidPreset；都不改工程', async () => {
    const before = current();
    const fields = layout(primary, 'TripleCentered');
    expect(await rejected({ type: 'orchestration.applyBasicLayout', ...fields, slotNames: ['只有一个'] })).toMatchObject({ status: 400, code: 'invalidRequest', field: 'command.slotNames' });
    const [first, second] = (await client.projects.allLines(id(), primary, current())).map((line) => line.id);
    const range = (selectedLineIds: string[]): ProjectCommand => ({ type: 'orchestration.previewBasicLayout', ...fields, selectedLineIds });
    expect(await rejected(range([second!, first!]))).toMatchObject({ status: 422, code: 'commandRejected', failureCode: 'DiscontiguousRange' });
    expect(await rejected(range([first!, 'line/不存在']))).toMatchObject({ status: 422, code: 'commandRejected', failureCode: 'InvalidRange' });
    expect(await rejected(range([first!, first!]))).toMatchObject({ status: 422, code: 'commandRejected', failureCode: 'InvalidRange' });
    const unknown = { type: 'orchestration.previewBasicLayout', ...fields, preset: 'QuadCentered' } as unknown as ProjectCommand;
    expect(await rejected(unknown)).toMatchObject({ status: 422, code: 'commandRejected', failureCode: 'InvalidPreset' });
    expect(current()).toBe(before);
  });

  let rangeCounts: unknown;

  it('G19 范围预演：scope=range，只算选中的句子，不改工程', async () => {
    const before = current();
    const [first] = (await client.projects.allLines(id(), primary, current())).map((line) => line.id);
    const response = await command({ type: 'orchestration.previewBasicLayout', ...layout(primary, 'SingleCentered'), selectedLineIds: [first!] });
    expect(response.changed).toBe(false);
    expect(response.basicLayoutResult).toMatchObject({ branchId: primary, preset: 'SingleCentered', scope: 'range', affectedLines: 1, existingClips: 0, changed: false });
    const { changed: _, clipIds: __, ...rest } = response.basicLayoutResult!;
    rangeCounts = rest;
    expect(current()).toBe(before);
    expect((await read()).programs[0]!.clips ?? []).toEqual([]);
  });

  it('G19 范围应用：一条历史；选中的句子成为新容器的片段，其余句子拆成留在默认容器的片段；默认容器不变；帧变化', async () => {
    const before = current();
    const [first, second] = (await client.projects.allLines(id(), primary, current())).map((line) => line.id);
    const old = (await read()).programs[0]!;
    const response = await command({ type: 'orchestration.applyBasicLayout', ...layout(primary, 'SingleCentered'), selectedLineIds: [first!] });
    expect(response.changed).toBe(true);
    expect(BigInt(current())).toBe(BigInt(before) + 1n);
    const { changed, clipIds, ...rest } = response.basicLayoutResult!;
    expect(changed).toBe(true);
    expect(rest).toEqual(rangeCounts);

    const model = buildOrchestration(await read());
    const program = model.programByBranch.get(primary)!;
    expect(program.defaultContainerId).toBe(old.defaultContainerId);
    const clips = program.clips ?? [];
    expect(new Set(clips.map((c) => c.id))).toEqual(new Set(clipIds));
    const selected = clips.find((c) => c.lineIds.includes(first!))!;
    const retained = clips.find((c) => c.lineIds.includes(second!))!;
    expect(selected).toMatchObject({ name: '歌词段布局', lineIds: [first] });
    expect(selected.scheduleContext ?? null).toBeNull();
    expect(model.byId.get(selected.containerId)).toMatchObject({ name: '布局-SingleCentered', parentId: program.rootContainerId, layout: { kind: 'SlotSequence' } });
    expect(retained).toMatchObject({ name: '保留原布局的歌词段', lineIds: [second], containerId: old.defaultContainerId, scheduleContext: { lineIds: [first, second], isDefaultSegment: true } });
    expect(await still()).not.toBe(frameBefore);
  });

  it('G19 同一范围再套同一预设是 no-op；撤销后片段和新容器都消失，帧恢复', async () => {
    const [first] = (await client.projects.allLines(id(), primary, current())).map((line) => line.id);
    const before = current();
    const again = await command({ type: 'orchestration.applyBasicLayout', ...layout(primary, 'SingleCentered'), selectedLineIds: [first!] });
    expect(again.changed).toBe(false);
    expect(current()).toBe(before);

    await command({ type: 'history.undo' });
    const model = buildOrchestration(await read());
    const program = model.programByBranch.get(primary)!;
    expect(program.clips ?? []).toEqual([]);
    expect(containersOf(model, program)).toHaveLength(1);
    expect(await still()).toBe(frameBefore);
  });

  const CLIP = 'clip/集成测试';

  it('G19-B 新建片段：一条历史，回执 clipResult 与读模型一致；撤销重做都对', async () => {
    const [first] = (await client.projects.allLines(id(), primary, current())).map((line) => line.id);
    const program = (await read()).programs[0]!;
    const before = current();
    const response = await command({ type: 'orchestration.createClip', branchId: primary, clipId: CLIP, name: '副歌', lineIds: [first!], containerId: program.defaultContainerId });
    expect(response.changed).toBe(true);
    expect(BigInt(current())).toBe(BigInt(before) + 1n);
    expect(response.changeSet).toMatchObject({ areas: expect.arrayContaining(['orchestration', 'renderInputs']), requiresFullRefresh: true });
    expect(response.clipResult).toEqual({ branchId: primary, clipId: CLIP, name: '副歌', lineIds: [first], containerId: program.defaultContainerId, changed: true });
    expect((await read()).programs[0]!.clips).toEqual([{ id: CLIP, name: '副歌', lineIds: [first], containerId: program.defaultContainerId }]);

    await command({ type: 'history.undo' });
    expect((await read()).programs[0]!.clips ?? []).toEqual([]);
    await command({ type: 'history.redo' });
    expect((await read()).programs[0]!.clips).toHaveLength(1);
  });

  it('G19-B 拒绝：身份重复 ClipExists、行已在别的片段 ClipScheduleConflict、乱序 DiscontiguousRange、行不存在 InvalidRange、容器是根 ContainerInvalid、片段不存在 TargetStale、改名缺 name 400；都不改工程', async () => {
    const [first, second] = (await client.projects.allLines(id(), primary, current())).map((line) => line.id);
    const program = (await read()).programs[0]!;
    const before = current();
    const create = (clipId: string, lineIds: string[], containerId = program.defaultContainerId): ProjectCommand => ({ type: 'orchestration.createClip', branchId: primary, clipId, lineIds, containerId });
    expect(await rejected(create(CLIP, [second!]))).toMatchObject({ status: 422, code: 'commandRejected', failureCode: 'ClipExists' });
    expect(await rejected(create('clip/另一个', [first!]))).toMatchObject({ status: 422, code: 'commandRejected', failureCode: 'ClipScheduleConflict' });
    expect(await rejected(create('clip/另一个', [second!, first!]))).toMatchObject({ status: 422, failureCode: 'DiscontiguousRange' });
    expect(await rejected(create('clip/另一个', ['line/不存在']))).toMatchObject({ status: 422, failureCode: 'InvalidRange' });
    expect(await rejected(create('clip/另一个', [second!], program.rootContainerId))).toMatchObject({ status: 422, failureCode: 'ContainerInvalid' });
    expect(await rejected({ type: 'orchestration.renameClip', branchId: primary, clipId: 'clip/不存在', name: '名字' })).toMatchObject({ status: 422, failureCode: 'TargetStale' });
    expect(await rejected({ type: 'orchestration.deleteClip', branchId: primary, clipId: 'clip/不存在' })).toMatchObject({ status: 422, failureCode: 'TargetStale' });
    const noName = { type: 'orchestration.renameClip', branchId: primary, clipId: CLIP } as unknown as ProjectCommand;
    expect(await rejected(noName)).toMatchObject({ status: 400, code: 'invalidRequest' });
    expect(current()).toBe(before);
  });

  it('G19-B 改名：同名 no-op 保留修订；改名、清除名称各一条历史', async () => {
    const before = current();
    const same = await command({ type: 'orchestration.renameClip', branchId: primary, clipId: CLIP, name: '副歌' });
    expect(same.changed).toBe(false);
    expect(same.clipResult).toMatchObject({ name: '副歌', changed: false });
    expect(current()).toBe(before);

    const renamed = await command({ type: 'orchestration.renameClip', branchId: primary, clipId: CLIP, name: '主歌' });
    expect(renamed.clipResult).toMatchObject({ clipId: CLIP, name: '主歌', changed: true });
    const cleared = await command({ type: 'orchestration.renameClip', branchId: primary, clipId: CLIP, name: null });
    expect(cleared.changed).toBe(true);
    expect(cleared.clipResult!.name ?? null).toBeNull();
    expect(BigInt(current())).toBe(BigInt(before) + 2n);
    expect((await read()).programs[0]!.clips![0]!.name ?? null).toBeNull();
    await command({ type: 'history.undo' });
    expect((await read()).programs[0]!.clips![0]!.name).toBe('主歌');
  });

  it('G19-B 删除：几句回到默认容器，一条历史；撤销后片段原样回来', async () => {
    const clip = (await read()).programs[0]!.clips![0]!;
    const before = current();
    const response = await command({ type: 'orchestration.deleteClip', branchId: primary, clipId: CLIP });
    expect(response.changed).toBe(true);
    expect(response.clipResult).toEqual({ branchId: primary, clipId: CLIP, name: clip.name, lineIds: clip.lineIds, containerId: clip.containerId, changed: true });
    expect(BigInt(current())).toBe(BigInt(before) + 1n);
    expect((await read()).programs[0]!.clips ?? []).toEqual([]);
    await command({ type: 'history.undo' });
    expect((await read()).programs[0]!.clips).toEqual([clip]);
    await command({ type: 'history.redo' });
    expect((await read()).programs[0]!.clips ?? []).toEqual([]);
  });

  it('G19-B 改容器：范围快速布局造出新容器后，把选中几句的片段移回默认容器是一条历史，帧变化；同容器 no-op；撤销后帧恢复', async () => {
    const [first] = (await client.projects.allLines(id(), primary, current())).map((line) => line.id);
    await command({ type: 'orchestration.applyBasicLayout', ...layout(primary, 'SingleCentered'), selectedLineIds: [first!] });
    const program = (await read()).programs[0]!;
    const selected = program.clips!.find((c) => c.lineIds.includes(first!))!;
    expect(selected.containerId).not.toBe(program.defaultContainerId);
    const laidOut = await still();

    const before = current();
    const same = await command({ type: 'orchestration.setClipContainer', branchId: primary, clipId: selected.id, containerId: selected.containerId });
    expect(same.changed).toBe(false);
    expect(current()).toBe(before);

    const moved = await command({ type: 'orchestration.setClipContainer', branchId: primary, clipId: selected.id, containerId: program.defaultContainerId });
    expect(moved.clipResult).toMatchObject({ clipId: selected.id, lineIds: [first], containerId: program.defaultContainerId, changed: true });
    expect(BigInt(current())).toBe(BigInt(before) + 1n);
    expect((await read()).programs[0]!.clips!.find((c) => c.id === selected.id)!.containerId).toBe(program.defaultContainerId);
    expect(await still()).not.toBe(laidOut);
    await command({ type: 'history.undo' });
    expect(await still()).toBe(laidOut);
    await command({ type: 'history.undo' });
    expect((await read()).programs[0]!.clips ?? []).toEqual([]);
  });

  it('R4 已修复：后加的翻译来源立即有自己的程序；跨来源设默认容器 422。R7 已修复：翻译来源套快速布局（整来源或范围）稳定拒绝 UnsupportedSource；新建片段也是', async () => {
    const response = await command({ type: 'lyrics.addTranslationBranch', name: 'English', language: 'en' });
    const translation = response.branchResult!.selectedBranchId;
    const model = buildOrchestration(await read());
    const program = model.programByBranch.get(translation)!;
    expect(program).toMatchObject({ rootContainerId: `branch/${translation}/display`, defaultContainerId: `branch/${translation}/display/regular` });
    expect(model.byId.get(program.rootContainerId)!.parentId ?? null).toBeNull();
    expect(containersOf(model, program).map((n) => n.id)).toEqual([program.defaultContainerId]);
    const primaryDefault = model.programByBranch.get(primary)!.defaultContainerId;
    const before = current();
    expect(await rejected({ type: 'orchestration.setDefaultContainer', branchId: translation, containerId: primaryDefault })).toMatchObject({ status: 422, code: 'commandRejected' });
    const lineId = (await client.projects.allLines(id(), translation, current()))[0]?.id;
    for (const type of ['orchestration.previewBasicLayout', 'orchestration.applyBasicLayout'] as const) {
      expect(await rejected({ type, ...layout(translation, 'SingleCentered') })).toMatchObject({ status: 422, code: 'commandRejected', failureCode: 'UnsupportedSource' });
      if (lineId) expect(await rejected({ type, ...layout(translation, 'SingleCentered'), selectedLineIds: [lineId] })).toMatchObject({ status: 422, failureCode: 'UnsupportedSource' });
    }
    if (lineId) {
      const create: ProjectCommand = { type: 'orchestration.createClip', branchId: translation, clipId: 'clip/翻译', lineIds: [lineId], containerId: program.defaultContainerId };
      expect(await rejected(create)).toMatchObject({ status: 422, failureCode: 'UnsupportedSource' });
    }
    expect(current()).toBe(before);
  });

  describe('片段成员：调整句子、拆分与合并（§15）', () => {
    let members: ProjectSessionSummary;
    let L: string[] = [];
    let start = '';
    const mid = () => members.projectSessionId;
    const run = (c: ProjectCommand) => client.projects.command(mid(), c);
    const revisionOf = () => client.projects.knownRevision(mid())!;
    const clips = async () => (await client.projects.orchestration(mid(), revisionOf())).programs[0]!.clips ?? [];
    const lines = (...numbers: number[]) => numbers.map((n) => L[n - 1]!);
    async function refused(c: ProjectCommand): Promise<BackendError> {
      const error = await run(c).catch((e: unknown) => e);
      if (error instanceof BackendError) return error;
      throw new Error(`命令 ${c.type} 没有被拒绝`);
    }
    const setMembers = (clipId: string, lineIds: string[]): ProjectCommand => ({ type: 'orchestration.setClipMembers', branchId: primary, clipId, lineIds });

    beforeAll(async () => {
      const audio = await client.assets.upload(new Blob([makeWav(14)]), { fileName: '片段成员.wav' });
      const lrc = await client.assets.upload(new Blob(['[00:01.00]一\n[00:03.00]二\n[00:05.00]三\n[00:07.00]四\n[00:09.00]五\n[00:11.00]六\n[00:13.00]\n']), { fileName: '片段成员.lrc' });
      members = await client.projects.create({ name: '片段成员', audioAssetId: audio.assetId, lyricsAssetId: lrc.assetId, splitRule: 1 });
      L = (await client.projects.allLines(mid(), primary, members.projectRevision)).map((line) => line.id);
      expect(L).toHaveLength(6);
      start = revisionOf();
    });

    it('能力列表有三条命令', async () => {
      const features = (await client.capabilities()).features;
      for (const type of ['setClipMembers', 'splitClip', 'mergeClips']) expect(features).toContain(`project-sessions.commands.orchestration.${type}`);
    });

    it('调整句子：相同是 no-op；扩大到第 2～4 句一条历史，身份、名称、容器不变；回执与读模型一致', async () => {
      const program = (await client.projects.orchestration(mid(), revisionOf())).programs[0]!;
      await run({ type: 'orchestration.createClip', branchId: primary, clipId: 'clip/A', name: '副歌', lineIds: lines(2, 3), containerId: program.defaultContainerId });
      const before = revisionOf();
      const same = await run(setMembers('clip/A', lines(2, 3)));
      expect(same.changed).toBe(false);
      expect(revisionOf()).toBe(before);

      const grown = await run(setMembers('clip/A', lines(2, 3, 4)));
      expect(grown.changed).toBe(true);
      expect(BigInt(revisionOf())).toBe(BigInt(before) + 1n);
      expect(grown.changeSet).toMatchObject({ areas: expect.arrayContaining(['orchestration', 'renderInputs']) });
      const expected = { id: 'clip/A', name: '副歌', lineIds: lines(2, 3, 4), containerId: program.defaultContainerId };
      expect(grown.clipMembersResult).toMatchObject({ operation: 'setClipMembers', branchId: primary, clips: [expected], removedClipIds: [], changed: true });
      expect(await clips()).toEqual([expected]);
    });

    it('调整句子的拒绝：空列表 400、不连续 DiscontiguousRange、片段不存在 TargetStale、和别的片段重叠 ClipScheduleConflict；都不改工程', async () => {
      await run({ type: 'orchestration.createClip', branchId: primary, clipId: 'clip/B', lineIds: lines(5), containerId: (await clips())[0]!.containerId });
      const before = revisionOf();
      expect(await refused(setMembers('clip/A', []))).toMatchObject({ status: 400 });
      expect(await refused(setMembers('clip/A', lines(2, 4)))).toMatchObject({ status: 422, code: 'commandRejected', failureCode: 'DiscontiguousRange' });
      expect(await refused(setMembers('clip/不存在', lines(2)))).toMatchObject({ status: 422, failureCode: 'TargetStale' });
      expect(await refused(setMembers('clip/A', lines(2, 3, 4, 5)))).toMatchObject({ status: 422, failureCode: 'ClipScheduleConflict' });
      expect(revisionOf()).toBe(before);
      await run({ type: 'history.undo' });
    });

    it('拆分：从第 3 句拆开一条历史，名称与容器相同，两段的调度上下文都是原段；拆分点是第一句 InvalidRange，新 ID 重复 ClipExists', async () => {
      const before = revisionOf();
      expect(await refused({ type: 'orchestration.splitClip', branchId: primary, clipId: 'clip/A', splitBeforeLineId: L[1]!, newClipId: 'clip/C' })).toMatchObject({ failureCode: 'InvalidRange' });
      expect(await refused({ type: 'orchestration.splitClip', branchId: primary, clipId: 'clip/A', splitBeforeLineId: L[2]!, newClipId: 'clip/A' })).toMatchObject({ failureCode: 'ClipExists' });
      expect(revisionOf()).toBe(before);

      const split = await run({ type: 'orchestration.splitClip', branchId: primary, clipId: 'clip/A', splitBeforeLineId: L[2]!, newClipId: 'clip/C' });
      expect(BigInt(revisionOf())).toBe(BigInt(before) + 1n);
      const context = { lineIds: lines(2, 3, 4), isDefaultSegment: false };
      const after = await clips();
      expect(after).toEqual([
        expect.objectContaining({ id: 'clip/A', name: '副歌', lineIds: lines(2), scheduleContext: expect.objectContaining(context) }),
        expect.objectContaining({ id: 'clip/C', name: '副歌', lineIds: lines(3, 4), containerId: after[0]!.containerId, scheduleContext: expect.objectContaining(context) }),
      ]);
      expect(split.clipMembersResult).toMatchObject({ operation: 'splitClip', clips: after, removedClipIds: [], changed: true });
    });

    it('调度上下文：拆出的片段扩到原段外 ScheduleContextConflict（即使那句不在片段里）；原段里缩小可以', async () => {
      const before = revisionOf();
      expect(await refused(setMembers('clip/C', lines(3, 4, 5)))).toMatchObject({ failureCode: 'ScheduleContextConflict' });
      expect(await refused(setMembers('clip/A', lines(1, 2)))).toMatchObject({ failureCode: 'ScheduleContextConflict' });
      expect(revisionOf()).toBe(before);
      await run(setMembers('clip/C', lines(3)));
      expect((await clips()).find((c) => c.id === 'clip/C')!.lineIds).toEqual(lines(3));
      await run({ type: 'history.undo' });
    });

    it('合并：保留目标的身份与名称，一条历史，回执带被合并掉的片段；和没有调度上下文的片段合并 ScheduleContextConflict', async () => {
      const program = (await client.projects.orchestration(mid(), revisionOf())).programs[0]!;
      await run({ type: 'orchestration.createClip', branchId: primary, clipId: 'clip/D', lineIds: lines(5), containerId: program.clips![0]!.containerId });
      const before = revisionOf();
      expect(await refused({ type: 'orchestration.mergeClips', branchId: primary, clipIds: ['clip/C', 'clip/D'], targetClipId: 'clip/C' })).toMatchObject({ failureCode: 'ScheduleContextConflict' });
      expect(revisionOf()).toBe(before);

      const merged = await run({ type: 'orchestration.mergeClips', branchId: primary, clipIds: ['clip/C', 'clip/A'], targetClipId: 'clip/C' });
      expect(BigInt(revisionOf())).toBe(BigInt(before) + 1n);
      expect(merged.clipMembersResult).toMatchObject({ operation: 'mergeClips', removedClipIds: ['clip/A'], changed: true });
      const [kept] = await clips();
      expect(kept).toMatchObject({ id: 'clip/C', name: '副歌', lineIds: lines(2, 3, 4), scheduleContext: { lineIds: lines(2, 3, 4) } });
    });

    it('连续撤销回到开始：每条命令一条历史', async () => {
      // 新建 A、扩大、拆分、新建 D、合并：五条。
      for (let i = 0; i < 5; i++) await run({ type: 'history.undo' });
      expect(await clips()).toEqual([]);
      expect((await client.projects.history(mid())).canUndo).toBe(false);
      expect(BigInt(revisionOf())).toBeGreaterThan(BigInt(start));
    });
  });

  it('所有 JSON 响应符合冻结 OpenAPI', () => {
    expect(recorder.issues).toEqual([]);
    expect([...recorder.validated]).toContain('GET /api/v1/project-sessions/{id}/orchestration 200');
  });
});
