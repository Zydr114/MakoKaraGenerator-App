// 连真实 Server 的完整模板（合同 1.37.0，规范 §24；W7 设计 §0、§2）：完整定义往返、效果包检查、工坊试样、
// 用到编排节点和片段上、外观来源、.mktpl 往返与传输配额（G32）。
// 效果包用后端仓库 samples/ScriptEffects 的示例运行时打包；找不到后端仓库时整个文件跳过并写明原因。
// 个人库属于本次运行的独立测试 profile（.dev/it-full-templates-*）；结束时删掉本文件写入的模板，目录留作证据。

import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { packSampleEffect, sampleEffectsDir } from '../../scripts/effect-packages.mjs';
import { BackendError, MakoClient, MemoryIdentityStore } from '../../src/client';
import { TemplateTransfers } from '../../src/editor/library/template-transfers';
import { addRecipe, updateRecipe } from '../../src/editor/model/effect-recipes';
import { MAX_SEED } from '../../src/editor/model/effect-params';
import { withSlotMaterial } from '../../src/editor/model/full-template';
import { withEntry } from '../../src/editor/model/json-text';
import {
  micros,
  sequence,
  type EffectPackageInspectResponse,
  type FullPackageMaterial,
  type FullTemplateDefinition,
  type PresetApplicationTarget,
  type ProjectCommand,
  type ProjectSessionSummary,
  type TemplatePreviewSource,
} from '../../src/protocol';
import { ContractRecorder, startTestBackend, type TestBackend } from '../support/backend';
import { makeWav } from '../support/fixtures';

const samples = sampleEffectsDir();
if (!samples) console.warn('[skipped] 找不到后端仓库的 samples/ScriptEffects（MAKO_BACKEND_REPO），跳过完整模板集成测试。');

let backend: TestBackend;
let client: MakoClient;
const recorder = new ContractRecorder();
const stamp = Date.now().toString(36);
/** 本文件写进个人库的模板，结束时删除。 */
const written = new Set<string>();
const packages = new Map<string, EffectPackageInspectResponse>();
let definition: FullTemplateDefinition;
let templateId = '';

beforeAll(async () => {
  if (!samples) return;
  backend = await startTestBackend('full-templates');
  client = await MakoClient.connect(backend.provider, {
    identityStore: new MemoryIdentityStore(),
    environment: null,
    onExchange: recorder.observer,
    jobPollIntervalMs: 1000,
  });
});

afterAll(async () => {
  if (client && written.size > 0) {
    let { revision, templates } = await client.templates.listAll('personal');
    for (const t of templates.filter((t) => written.has(t.templateId))) {
      revision = (await client.templates.command('personal', revision, { type: 'delete', templateId: t.templateId })).revision;
    }
  }
  client?.dispose();
  await backend?.stop();
});

async function inspect(name: string): Promise<EffectPackageInspectResponse> {
  const asset = await client.assets.upload(new Blob([packSampleEffect(name)]), { fileName: `${name}.mkfx` });
  const inspected = await client.effectPackages.inspect({ packageAssetId: asset.assetId });
  packages.set(name, inspected);
  return inspected;
}

const exportOf = (pkg: EffectPackageInspectResponse, id: string) => pkg.exports.find((e) => e.id === id)!;
const withoutPackages = ({ packages: _, ...rest }: FullTemplateDefinition) => rest;
const inkMaterial = (parametersJson: string): FullPackageMaterial => {
  const ink = packages.get('CustomInk')!;
  return { kind: 'package', packageId: ink.packageId, revision: ink.packageRevision, exportId: 'custom-ink', parametersJson };
};

describe.skipIf(!samples)('完整模板（真实 Server）', () => {
  it('效果包检查：材质、控制器的导出与参数表；时长上限按原文给出；坏 ZIP 422', async () => {
    const ink = await inspect('CustomInk');
    expect(ink).toMatchObject({ packageId: 'makokara.custom-ink', name: '自定义波纹材质', structureValid: true, reference: { kind: 'asset', packageId: 'makokara.custom-ink' } });
    expect(exportOf(ink, 'custom-ink')).toMatchObject({ kind: 'material' });
    expect(ink.parameters.map((p) => p.id)).toEqual(['frequencyHz', 'amplitude', 'colorA', 'colorB']);

    const emphasis = await inspect('WordEmphasis');
    expect(exportOf(emphasis, 'word-emphasis')).toMatchObject({ kind: 'controller' });
    const burst = await inspect('EventBurst');
    expect(burst.structureValid).toBe(true);
    // manifest 原文保留 Int64 上限，前端不能先转成数字。
    expect(burst.manifestJson).toContain('9223372036854775807');

    const broken = await client.assets.upload(new Blob([new TextEncoder().encode('PK 不是 ZIP')]), { fileName: 'broken.mkfx' });
    await expect(client.effectPackages.inspect({ packageAssetId: broken.assetId })).rejects.toMatchObject({ status: 422 });
  });

  it('完整定义往返：包材质、两个配方、参数原文和最大种子保存后不变；包引用读回变成 library 引用', async () => {
    const builtin = await client.templates.listAll('builtin');
    const classic = await client.templates.fullDetail('builtin', 'builtin:line-classic', builtin.revision);
    expect(classic.definition).toMatchObject({ effectRecipes: [], packages: [] });

    const ink = packages.get('CustomInk')!;
    const emphasis = packages.get('WordEmphasis')!;
    const burst = packages.get('EventBurst')!;
    let draft: FullTemplateDefinition = {
      ...classic.definition,
      name: `集成完整-${stamp}`,
      style: withSlotMaterial(classic.definition.style, 'unsungFill', inkMaterial('{"frequencyHz":4.5}')),
      packages: [ink.reference, emphasis.reference, burst.reference],
    };
    const first = addRecipe(draft, emphasis, exportOf(emphasis, 'word-emphasis'));
    draft = updateRecipe(first.definition, first.recipeId, { parametersJson: '{"peakScale":2}', fixedSeed: MAX_SEED });
    const second = addRecipe(draft, burst, exportOf(burst, 'event-burst'));
    // 和参数控件一样经 withEntry 改写（键按名称排序，与后端的规范形式一致）。停用：只核对参数原文，不让粒子进画面。
    const burstParameters = withEntry(withEntry('{}', 'lifeUs', '9223372036854775807'), 'count', '3');
    draft = updateRecipe(second.definition, second.recipeId, { parametersJson: burstParameters, enabled: false });
    definition = draft;

    const { revision } = await client.templates.listAll('personal');
    const created = await client.templates.command('personal', revision, { type: 'create', definitionFormat: 'full', definition });
    templateId = created.templateId;
    written.add(templateId);

    const detail = await client.templates.fullDetail('personal', templateId, created.revision);
    expect(detail.definitionAvailability).toBe('fullAvailable');
    expect(withoutPackages(detail.definition)).toEqual(withoutPackages(definition));
    expect(detail.definition.effectRecipes[0]).toMatchObject({ parametersJson: '{"peakScale":2}', fixedSeed: '18446744073709551615' });
    expect(detail.definition.effectRecipes[1]!.parametersJson).toBe('{"count":3,"lifeUs":9223372036854775807}');
    // 读回的包按 packageId 排序。
    expect(detail.definition.packages).toEqual(
      [ink, burst, emphasis].map((p) => ({ kind: 'library', packageId: p.packageId, packageRevision: p.packageRevision, libraryId: 'personal', libraryRevision: created.revision, templateId, templateRevision: expect.any(String) })),
    );
    expect(detail.summary).toMatchObject({ hasEffectRecipes: true, hasPackageMaterials: true });

    // 参数越界：后端只说“未通过校验”，不指出哪一项（G31）。
    const outOfRange = updateRecipe(definition, first.recipeId, { parametersJson: '{"peakScale":9}' });
    await expect(client.templates.command('personal', created.revision, { type: 'replace', templateId, definitionFormat: 'full', definition: outOfRange })).rejects.toMatchObject({ status: 422 });
  });

  it('工坊试样：两个样本和能用的目标，三个材质部位；单帧回显样本、目标和材质焦点；改参数画面就变', async () => {
    const { revision, templates } = await client.templates.listAll('personal');
    const summary = templates.find((t) => t.templateId === templateId)!;
    const saved: TemplatePreviewSource = { kind: 'savedTemplate', libraryId: 'personal', libraryRevision: revision, templateId, templateRevision: summary.templateRevision };

    const options = await client.templates.previewOptions(saved);
    expect(options.samples.map((s) => s.sampleId)).toEqual(['workshop-single-line-v1', 'workshop-multi-line-v1']);
    const single = options.samples[0]!;
    expect(single.durationUs).toBe('12000000');
    expect(single.targets.map((t) => t.key)).toEqual(expect.arrayContaining(['line', 'project', 'branch', 'clip', 'group']));
    expect(options.samples[1]!.targets.map((t) => t.key)).toContain('mark');
    expect(options.materialSlots.map((s) => s.slot)).toEqual(['unsungFill', 'sungFill', 'stroke']);
    expect(options.materialSlots[0]).toMatchObject({ present: true, material: { kind: 'package', packageId: 'makokara.custom-ink' } });

    const runtime = await client.preview.runtimeInfo();
    if (!runtime.enabled) {
      console.warn(`[skipped] 预览不可用，不取试样帧：${runtime.disabledReason?.code ?? runtime.status}`);
      return;
    }
    const size = { width: 320, height: 180 };
    const still = (source: TemplatePreviewSource) =>
      client.preview.renderTemplateStill({ source, sampleId: 'workshop-single-line-v1', targetKey: 'line', materialSlot: 'sungFill', size, timeUs: micros(3_000_000) });
    const frame = await still(saved);
    expect(frame.verified).toBe(true);
    expect(frame.preview).toMatchObject({ previewKind: 'template', committable: false, source: { kind: 'template', templateSample: { sampleId: 'workshop-single-line-v1', targetKey: 'line', materialSlot: 'sungFill' } } });

    // 草稿用 asset 引用：不受库修订影响。
    const draft = (parametersJson: string): TemplatePreviewSource => ({
      kind: 'draft',
      draftRevision: `it-${stamp}-${parametersJson.length}`,
      definitionFormat: 'full',
      definition: { ...definition, style: withSlotMaterial(definition.style, 'unsungFill', inkMaterial(parametersJson)) },
    });
    const same = await still(draft('{"frequencyHz":4.5}'));
    expect(same.sha256).toBe(frame.sha256);
    const red = await still(draft('{"frequencyHz":4.5,"colorA":{"r":1,"g":0,"b":0,"a":1},"colorB":{"r":1,"g":0.2,"b":0,"a":1}}'));
    expect(red.sha256).not.toBe(frame.sha256);

    // 按帧检查投影（合同 1.48.0）：含脚本效果的试样经过效果准备，有几何；响应由 recorder 按冻结 OpenAPI 校验。
    const live = await client.preview.createTemplatePreview({ source: saved, sampleId: 'workshop-single-line-v1', targetKey: 'line', materialSlot: 'sungFill', size });
    try {
      const accepted = await client.preview.requestFrame(live.previewId, {
        expectedSourceRevision: live.source.sourceRevision,
        previewGeneration: live.previewGeneration,
        requestSeq: sequence(1),
        timeUs: micros(3_000_000),
        intent: 'seek',
        inspect: true,
      });
      const inspected = await client.preview.waitForFrame(live.previewId, { generation: accepted.previewGeneration, sourceRevision: live.source.sourceRevision, requestSeq: sequence(1) });
      expect((await client.preview.fetchFrame(inspected)).sha256).toBe(frame.sha256);
      const read = (await client.preview.readInspection(inspected))!;
      expect(read.inspection.prepared).toBe(true);
      expect(read.inspection.tokens.length).toBeGreaterThan(0);
      expect(read.inspection.cursors.length).toBeGreaterThan(0);
      expect(read.inspection.effects.length).toBeGreaterThan(0);
      for (const cursor of read.inspection.cursors) expect(['unavailable', 'fromLine', 'fromToken', 'fromRubySegment']).toContain(cursor.progressSource);
    } finally {
      await client.preview.closeBestEffort(live.previewId, { expectedPreviewGeneration: live.previewGeneration, expectedSourceRevision: live.source.sourceRevision });
    }
  });

  describe('用在工程里', () => {
    let project: ProjectSessionSummary;
    let branchId = '';
    let lineIds: string[] = [];
    let containerId = '';
    const CLIP = 'clip/full-template';
    const id = () => project.projectSessionId;
    const current = () => client.projects.knownRevision(id())!;
    const command = (c: ProjectCommand) => client.projects.command(id(), c);
    const appPath = (applicationId: string) => `presetApplications.${applicationId}.snapshot.style`;
    const sourceOf = async (lineIndex: number, property: string) =>
      (await client.projects.resolvedStyle(id(), current(), { branchId, lineId: lineIds[lineIndex]! })).sources.find((s) => s.property === property)?.sourcePath;
    // templateRevision 是库修订。
    async function applyCommand(target: PresetApplicationTarget): Promise<ProjectCommand> {
      const { revision } = await client.templates.listAll('personal');
      return { type: 'presetApplications.apply', templateLibraryId: 'personal', templateId, templateRevision: revision, targets: [target] };
    }
    const apply = async (target: PresetApplicationTarget) => (await command(await applyCommand(target))).presetApplicationResult!.applicationIds[0]!;

    beforeAll(async () => {
      const audio = await client.assets.upload(new Blob([makeWav(6)]), { fileName: '完整模板.wav' });
      const lrc = await client.assets.upload(new Blob(['[00:00.50]一行目\n[00:02.00]二行目\n[00:04.00]三行目\n']), { fileName: '完整模板.lrc' });
      project = await client.projects.create({ name: '完整模板', audioAssetId: audio.assetId, lyricsAssetId: lrc.assetId, splitRule: 1 });
      branchId = (await client.projects.branches(id(), project.projectRevision)).primaryBranchId;
      lineIds = (await client.projects.allLines(id(), branchId, project.projectRevision)).map((l) => l.id);
      containerId = (await client.projects.orchestration(id(), project.projectRevision)).programs[0]!.defaultContainerId;
      await command({ type: 'orchestration.createClip', branchId, clipId: CLIP, name: '副歌', lineIds: [lineIds[1]!], containerId });
    });

    it('默认容器（含里面的句子）和片段都能作为目标；外观来源按“节点→片段”叠加；文本单元目标被拒绝', async () => {
      expect(await sourceOf(0, 'unsungColor')).toBe('lyricStyle');

      const nodeApp = await apply({ kind: 'node', nodeId: containerId, includeDescendants: true });
      expect(await sourceOf(0, 'unsungColor')).toBe(appPath(nodeApp));
      expect(await sourceOf(1, 'unsungColor')).toBe(appPath(nodeApp));

      const clipApp = await apply({ kind: 'clips', branchId, clipIds: [CLIP] });
      expect(clipApp).not.toBe(nodeApp);
      expect(await sourceOf(0, 'unsungColor')).toBe(appPath(nodeApp));
      expect(await sourceOf(1, 'unsungColor')).toBe(appPath(clipApp));

      const apps = (await client.projects.presetApplications(id(), current())).applications;
      expect(apps.map((a) => a.target)).toEqual([
        { kind: 'node', nodeId: containerId, includeDescendants: true },
        { kind: 'clips', branchId, clipIds: [CLIP], includeDescendants: false },
      ]);
      expect(apps[0]!.snapshot).toMatchObject({ templateId, hasEffectRecipes: true });

      const line = (await client.projects.allLines(id(), branchId, current()))[0]!;
      const error = await command(await applyCommand({ kind: 'token', branchId, lineId: line.id, tokenId: line.tokens[0]!.id })).catch((e: unknown) => e);
      expect(error).toBeInstanceOf(BackendError);
      expect(error).toMatchObject({ status: 422 });

      // 撤销片段上的分配，第二句回到节点分配。
      await client.projects.undo(id());
      expect(await sourceOf(1, 'unsungColor')).toBe(appPath(nodeApp));
    });

    it('应用后工程预览照常出帧，和应用前不同；撤销后与应用前相同', async (ctx) => {
      const runtime = await client.preview.runtimeInfo();
      if (!runtime.enabled) {
        console.warn(`[skipped] 预览不可用：${runtime.disabledReason?.code ?? runtime.status}`);
        ctx.skip();
      }
      const still = async () => {
        const frame = await client.preview.renderStill({ projectSessionId: id(), sourceRevision: current(), timeUs: micros(2_500_000), size: { width: 320, height: 180 } });
        expect(frame.verified).toBe(true);
        return frame.sha256;
      };
      const applied = await still();
      await client.projects.undo(id());
      const before = await still();
      expect(before).not.toBe(applied);
      await client.projects.redo(id());
      expect(await still()).toBe(applied);
    });
  });

  it('.mktpl：导出含效果和静态两个模板并核对，预检后导入得到新 ID、定义相同（包引用除外）；预检只能用一次；传输配额 2 个', async () => {
    const transfers = new TemplateTransfers(client);
    let personal = await client.templates.listAll('personal');
    const builtin = await client.templates.listAll('builtin');
    const plain = await client.templates.command('personal', personal.revision, { type: 'copy', sourceLibraryId: 'builtin', sourceRevision: builtin.revision, templateId: 'builtin:token-highlight', name: `集成静态-${stamp}` });
    written.add(plain.templateId);
    const ids = [templateId, plain.templateId];

    // 第 1 个传输对象：导出。大小和 SHA-256 由 TemplateTransfers 核对。
    const exported = await transfers.exportArchive('personal', plain.revision, ids);
    expect(exported.response).toMatchObject({ libraryId: 'personal', templateIds: ids });
    expect(exported.blob.size).toBe(exported.response.sizeBytes);

    // 第 2 个：预检。不写库。
    const preflight = await transfers.preflight(new File([exported.blob], 'it.mktpl'));
    expect(preflight).toMatchObject({ identityPolicy: 'generateNewIds', archiveBytes: exported.response.sizeBytes });
    expect(preflight.templates.map((t) => [t.summary.templateId, t.definitionAvailability])).toEqual([
      [templateId, 'advancedRuntimeAvailable'],
      [plain.templateId, 'staticAvailable'],
    ]);
    personal = await client.templates.listAll('personal');
    expect(personal.revision).toBe(plain.revision);

    // 导入释放预检；新 ID 与原 ID 一一对应。
    const imported = await transfers.importArchive(personal.revision, preflight);
    for (const t of imported.importedTemplates) written.add(t.templateId);
    expect(imported.importedTemplates.map((t) => t.sourceTemplateId)).toEqual(ids);
    expect(imported.importedTemplates.every((t) => !ids.includes(t.templateId))).toBe(true);
    for (const { sourceTemplateId, templateId: newId } of imported.importedTemplates) {
      const [source, copy] = await Promise.all([client.templates.fullDetail('personal', sourceTemplateId, imported.revision), client.templates.fullDetail('personal', newId, imported.revision)]);
      expect(withoutPackages(copy.definition)).toEqual(withoutPackages(source.definition));
      expect(copy.definition.packages.map((p) => [p.packageId, p.packageRevision])).toEqual(source.definition.packages.map((p) => [p.packageId, p.packageRevision]));
    }
    await expect(transfers.importArchive(imported.revision, preflight)).rejects.toThrow('预检已经过期或用过了');

    // 导出那份还占着：再导出一次占满，第三次预检 429，并写明要等几分钟。
    await transfers.exportArchive('personal', imported.revision, [plain.templateId]);
    await expect(transfers.preflight(new File([exported.blob], 'it.mktpl'))).rejects.toThrow(/大约 [1-5] 分钟后可以/);
  });

  it('JSON 响应符合冻结 OpenAPI', () => {
    expect(recorder.issues).toEqual([]);
    expect([...recorder.validated].some((v) => v.includes('/effect-packages/inspect 200'))).toBe(true);
  });
});
