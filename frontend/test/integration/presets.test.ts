// 连真实 Server 的工程模板分配（合同 1.10.0 命令，1.12.0 读模型 G15，1.14.0 读取能力名与 source 空值（R3），1.15.0 候选试看 G16；规范 §13.3、§13.4、§13.6；W3 设计 §2.6、§3.1）。
// 用例按顺序共享一个工程；个人库属于本次运行的独立测试 profile（.dev/it-presets-*），目录留作证据，结束时删掉自己复制的模板。

import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { BackendError, MakoClient, MemoryIdentityStore } from '../../src/client';
import { micros, revision, type EditableLyricLine, type PresetApplication, type PresetApplicationTarget, type ProjectCommand, type ProjectSessionSummary } from '../../src/protocol';
import { ContractRecorder, startTestBackend, type TestBackend } from '../support/backend';
import { makeWav } from '../support/fixtures';

let backend: TestBackend;
let client: MakoClient;
const recorder = new ContractRecorder();
let project: ProjectSessionSummary;
let branchId = '';
let lines: EditableLyricLine[] = [];
let builtinRevision = '';
let copyId: string | undefined;

beforeAll(async () => {
  backend = await startTestBackend('presets');
  client = await MakoClient.connect(backend.provider, {
    identityStore: new MemoryIdentityStore(),
    environment: null,
    onExchange: recorder.observer,
    jobPollIntervalMs: 1000,
  });
  const audio = await client.assets.upload(new Blob([makeWav(6)]), { fileName: '分配测试.wav' });
  const lrc = await client.assets.upload(new Blob(['[00:00.50]朝の光\n[00:02.00]Hello world\n[00:04.00]终\n']), { fileName: '分配测试.lrc' });
  project = await client.projects.create({ name: '分配测试', audioAssetId: audio.assetId, lyricsAssetId: lrc.assetId, splitRule: 1 });
  branchId = (await client.projects.branches(project.projectSessionId, project.projectRevision)).primaryBranchId;
  lines = await client.projects.allLines(project.projectSessionId, branchId, project.projectRevision);
  builtinRevision = (await client.templates.listAll('builtin')).revision;
});

afterAll(async () => {
  if (copyId) {
    const { revision } = await client.templates.listAll('personal');
    await client.templates.command('personal', revision, { type: 'delete', templateId: copyId }).catch(() => undefined);
  }
  client?.dispose();
  await backend?.stop();
});

const id = () => project.projectSessionId;
const current = () => client.projects.knownRevision(id())!;
const line = (i: number): PresetApplicationTarget => ({ kind: 'line', branchId, lineId: lines[i]!.id });
const read = async (): Promise<PresetApplication[]> => (await client.projects.presetApplications(id(), current())).applications;
const command = (c: ProjectCommand) => client.projects.command(id(), c);

const size = { width: 320, height: 180 };
async function still(): Promise<string> {
  const result = await client.preview.renderStill({ projectSessionId: id(), sourceRevision: current(), timeUs: micros(1_000_000), size });
  expect(result.verified).toBe(true);
  return result.frame.content.sha256;
}

async function skipWithoutPresetPreview(ctx: { skip: () => void }): Promise<void> {
  const runtime = await client.preview.runtimeInfo();
  if (!runtime.enabled || !(runtime.sourceProfiles ?? []).includes('static-lyrics-scene-presets-v1')) {
    console.warn(`[skipped] 预设预览不可用：${runtime.disabledReason?.code ?? runtime.status}`);
    ctx.skip();
  }
}

async function rejected(c: ProjectCommand): Promise<BackendError> {
  const error = await command(c).catch((e: unknown) => e);
  if (error instanceof BackendError) return error;
  throw new Error(`命令 ${c.type} 没有被拒绝`);
}

describe('工程模板分配（真实 Server）', () => {
  it('一次应用到两行：回执带两个分配 ID 与 presetApplications 区域；读到两条，目标、启用、快照摘要齐全，来源暂不可比较', async () => {
    expect((await client.capabilities()).features).toContain('project-sessions.preset-applications.read');
    const response = await command({ type: 'presetApplications.apply', templateLibraryId: 'builtin', templateId: 'builtin:line-classic', templateRevision: builtinRevision, targets: [line(0), line(1)] });
    expect(response).toMatchObject({ changed: true, projectRevision: '1' });
    expect(response.changeSet.areas).toContain('presetApplications');
    const ids = response.presetApplicationResult!.applicationIds;
    expect(new Set(ids).size).toBe(2);

    const apps = await read();
    expect(apps.map((a) => a.applicationId)).toEqual(ids);
    expect(apps.map((a) => a.target)).toEqual([0, 1].map((i) => ({ ...line(i), includeDescendants: false })));
    expect(apps.every((a) => a.targetValid && a.enabled)).toBe(true);
    expect(apps[0]!.snapshot).toMatchObject({ templateId: 'builtin:line-classic', name: '经典整行', kind: 'line', hasEffectRecipes: false });
    expect(apps[0]!.source).toEqual({ libraryId: null, revision: null, comparable: false, newVersionAvailable: null });
  });

  it('拒绝：库修订对不上 404（command.templateRevision）；文本单元用途用在行上 422；读取用旧修订 409', async () => {
    const stale = await rejected({ type: 'presetApplications.apply', templateLibraryId: 'builtin', templateId: 'builtin:line-classic', templateRevision: 'missing', targets: [line(2)] });
    expect(stale).toMatchObject({ status: 404, code: 'resourceNotFound' });
    expect(stale.field).toBe('command.templateRevision');

    const kind = await rejected({ type: 'presetApplications.apply', templateLibraryId: 'builtin', templateId: 'builtin:token-highlight', templateRevision: builtinRevision, targets: [line(2)] });
    expect(kind).toMatchObject({ status: 422, code: 'commandRejected' });

    await expect(client.projects.presetApplications(id(), revision('0'))).rejects.toMatchObject({ status: 409, code: 'revisionConflict' });
  });

  it('停用；按库里同一版本更新没有变化；换模板沿用目标上的分配 ID 并重新启用', async () => {
    const [first] = await read();
    await command({ type: 'presetApplications.setEnabled', applicationId: first!.applicationId, enabled: false });
    expect((await read())[0]!.enabled).toBe(false);

    const before = current();
    const same = await command({ type: 'presetApplications.update', applicationId: first!.applicationId, templateLibraryId: 'builtin', templateId: 'builtin:line-classic', templateRevision: builtinRevision });
    expect(same.changed).toBe(false);
    expect(current()).toBe(before);

    // 内置的两个模板都是默认外观，画面上看不出来：复制一份并把未唱颜色改成红色。
    const personal = await client.templates.listAll('personal');
    const copied = await client.templates.command('personal', personal.revision, { type: 'copy', sourceLibraryId: 'builtin', sourceRevision: builtinRevision, templateId: 'builtin:line-classic', name: `分配测试副本-${Date.now().toString(36)}` });
    copyId = copied.templateId;
    const { definition } = await client.templates.detail('personal', copyId, copied.revision);
    const red = { kind: 'solid' as const, color: { red: 255, green: 0, blue: 0, alpha: 255 } };
    const edited = await client.templates.command('personal', copied.revision, { type: 'replace', templateId: copyId, definition: { ...definition!, style: { ...definition!.style, unsungFill: { material: red } } } });
    const replaced = await command({ type: 'presetApplications.apply', templateLibraryId: 'personal', templateId: copyId, templateRevision: edited.revision, targets: [line(0)] });
    expect(replaced.presetApplicationResult?.applicationIds).toEqual([first!.applicationId]);
    const apps = await read();
    expect(apps).toHaveLength(2);
    expect(apps[0]).toMatchObject({ applicationId: first!.applicationId, enabled: true, snapshot: { templateId: copyId } });
  });

  it('移除两条是一条历史；撤销恢复原 ID', async () => {
    const ids = (await read()).map((a) => a.applicationId);
    await command({ type: 'presetApplications.remove', applicationIds: ids });
    expect(await read()).toEqual([]);
    await client.projects.undo(id());
    expect((await read()).map((a) => a.applicationId)).toEqual(ids);
  });

  it('删掉目标行后分配仍在、targetValid=false 且目标缺来源；仍可移除', async () => {
    const [, second] = await read();
    await command({ type: 'lyrics.removeLine', target: { branchId, lineId: lines[1]!.id } });
    const gone = (await read()).find((a) => a.applicationId === second!.applicationId)!;
    expect(gone.targetValid).toBe(false);
    expect(gone.target).toEqual({ kind: 'line', lineId: lines[1]!.id, includeDescendants: false });
    await command({ type: 'presetApplications.remove', applicationIds: [second!.applicationId] });
    expect((await read()).map((a) => a.applicationId)).not.toContain(second!.applicationId);
  });

  it('含分配的工程能出静态预览帧：第 1 行用红色副本时与移除分配后的帧不同', async (ctx) => {
    await skipWithoutPresetPreview(ctx);
    expect((await read()).map((a) => a.snapshot.templateId)).toEqual([copyId]);
    const applied = await still();
    await command({ type: 'presetApplications.remove', applicationIds: (await read()).map((a) => a.applicationId) });
    expect(await still()).not.toBe(applied);
  });

  it('候选试看（G16）：只读，工程修订与分配都不变；输入固定；与随后正式应用的帧一致；工程修订过期 409', async (ctx) => {
    await skipWithoutPresetPreview(ctx);
    expect((await client.capabilities()).features).toContain('project-sessions.preset-application-previews.create');
    const personal = await client.templates.listAll('personal');
    const input = {
      expectedProjectRevision: current(),
      templateLibraryId: 'personal' as const,
      templateLibraryRevision: personal.revision,
      templateId: copyId!,
      templateRevision: personal.templates.find((t) => t.templateId === copyId)!.templateRevision,
      targets: [line(0)],
      size,
    };
    const before = current();
    const candidate = await client.preview.renderCandidateStill(id(), { ...input, timeUs: micros(1_000_000) });
    expect(candidate.verified).toBe(true);
    expect(candidate.preview).toMatchObject({ previewKind: 'presetCandidate', committable: false, source: { kind: 'presetCandidate' } });
    expect(current()).toBe(before);
    expect(await read()).toEqual([]);

    const live = await client.preview.createPresetCandidate(id(), input);
    const { previewGeneration, source } = live;
    await expect(client.preview.replaceInput(live.previewId, { expectedPreviewGeneration: previewGeneration, expectedSourceRevision: source.sourceRevision, source: { kind: 'project', sourceId: id(), sourceRevision: current() }, size })).rejects.toMatchObject({
      status: 422,
      code: 'commandNotSupported',
    });
    await client.preview.close(live.previewId, { expectedPreviewGeneration: previewGeneration, expectedSourceRevision: source.sourceRevision });

    await command({ type: 'presetApplications.apply', templateLibraryId: 'personal', templateId: copyId!, templateRevision: personal.revision, targets: [line(0)] });
    expect(await still()).toBe(candidate.sha256);
    await expect(client.preview.createPresetCandidate(id(), input)).rejects.toMatchObject({ status: 409, code: 'revisionConflict' });
  });

  it('所有 JSON 响应符合冻结 OpenAPI', () => {
    expect(recorder.issues).toEqual([]);
    expect([...recorder.validated]).toContain('GET /api/v1/project-sessions/{id}/preset-applications 200');
  });
});
