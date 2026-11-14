// W8 的真实 Server 关键链路；错误编译和控件交互由 smoke:author 覆盖，避免重复一套场景。
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { packVariantEffect, sampleEffectsDir } from '../../scripts/effect-packages.mjs';
import { MakoClient, MemoryIdentityStore } from '../../src/client';
import { componentFromDraft, componentSample, draftConfigurationInput } from '../../src/editor/model/script-components';
import type { AuthorDraft } from '../../src/protocol';
import { ContractRecorder, startTestBackend, type TestBackend } from '../support/backend';

const samples = sampleEffectsDir();
if (!samples) console.warn('[skipped] 找不到 samples/ScriptEffectsV2（MAKO_BACKEND_REPO），跳过作者集成测试。');
let backend: TestBackend;
let client: MakoClient;
let draft: AuthorDraft | undefined;
let configurationId = '';
let templateId = '';
const recorder = new ContractRecorder();

beforeAll(async () => {
  if (!samples) return;
  backend = await startTestBackend('author');
  client = await MakoClient.connect(backend.provider, { identityStore: new MemoryIdentityStore(), environment: null, onExchange: recorder.observer, jobPollIntervalMs: 1000 });
});

afterAll(async () => {
  try {
    if (templateId) {
      const { revision } = await client.templates.listAll('personal');
      await client.templates.command('personal', revision, { type: 'delete', templateId });
    }
    if (configurationId) {
      const { revision } = await client.scriptConfigurations.list();
      await client.scriptConfigurations.command(revision, { type: 'delete', configurationId });
    }
    if (draft) {
      const current = await client.drafts.get(draft.draftId);
      await client.drafts.delete(current.draftId, current.draftRevision);
    }
  } finally {
    client?.dispose();
    await backend?.stop();
  }
});

describe.skipIf(!samples)('作者关键链路（真实 Server）', () => {
  it('草稿配置和源码→编译→720 帧验证→组件库→模板保存，删除组件后模板仍保留包', async () => {
    const name = `集成作者-${Date.now().toString(36)}`;
    const asset = await client.assets.upload(new Blob([packVariantEffect('WordEmphasisCurves')]), { fileName: 'WordEmphasisCurves.mkfx' });
    draft = await client.drafts.create({ packageAssetId: asset.assetId, exportId: 'word-emphasis', name });
    const configuration = draft.configuration;
    if (!configuration) throw new Error('草稿没有返回配置');
    draft = await client.drafts.setConfiguration(draft.draftId, draft.draftRevision, {
      ...draftConfigurationInput(configuration), relativeTarget: 'tokenFamily', fixedSeed: '18446744073709551615',
      parametersJson: '{"peakScale":3,"shape":{"kind":"easing","easing":"easeOut"}}',
    });
    const source = await client.drafts.sources(draft.draftId, draft.draftRevision, draft.currentSourceRevision);
    const files = source.files.map((file) => ({ ...file, text: `${file.text}\n// 作者集成往返\n` }));
    const saved = await client.drafts.saveSources(draft.draftId, draft.draftRevision, files);
    draft = await client.drafts.get(draft.draftId);
    expect(saved.sourceRevision).not.toBe(source.sourceRevision);
    expect((await client.drafts.sources(draft.draftId, draft.draftRevision, saved.sourceRevision)).files).toEqual(files);
    expect(draft.currentSourceRevision).toBe(saved.sourceRevision);
    expect(draft.configuration?.sourceRevision).toBe(source.sourceRevision);

    const compilation = await client.drafts.compile(draft.draftId, draft.draftRevision, saved.sourceRevision);
    expect((await client.jobs.waitForTerminal(compilation.jobId, { timeoutMs: 120_000 })).state).toBe('succeeded');
    expect(await client.drafts.compilationResult(compilation.jobId)).toMatchObject({ succeeded: true, sourceRevision: saved.sourceRevision });
    await client.drafts.adopt(draft.draftId, draft.draftRevision, saved.sourceRevision);
    draft = await client.drafts.get(draft.draftId);
    expect(draft.configuration?.sourceRevision).toBe(saved.sourceRevision);
    const gpuAvailable = (await client.preview.renderBackends()).backends.some((backend) => backend.renderBackend === 'gpu' && backend.available);
    if (gpuAvailable) {
      const validation = await client.drafts.validate(draft.draftId, draft.draftRevision, { sampleId: 'workshop-single-line-v1', targetKey: 'line', renderBackend: 'gpu' });
      const terminal = await client.jobs.waitForTerminal(validation.jobId, { timeoutMs: 150_000 });
      expect(terminal.state, terminal.error?.message ?? terminal.failureCode).toBe('succeeded');
      expect(await client.jobs.validationResult(validation.jobId)).toMatchObject({ succeeded: true, opaqueFrames: 360, transparentFrames: 360, renderBackend: 'gpu' });
    }

    draft = await client.drafts.get(draft.draftId);
    if (!draft.configuration) throw new Error('草稿丢失配置');
    const library = await client.scriptConfigurations.list();
    const created = await client.scriptConfigurations.command(library.revision, { type: 'create', configuration: componentFromDraft({ ...draft, configuration: draft.configuration }) });
    configurationId = created.configurationId;
    const detail = await client.scriptConfigurations.detail(configurationId, created.revision);
    expect(detail.configuration).toMatchObject({ name, fixedSeed: '18446744073709551615', sourceRevision: saved.sourceRevision, parametersJson: draft.configuration.parametersJson });
    const builtin = await client.templates.listAll('builtin');
    const base = await client.templates.fullDetail('builtin', 'builtin:line-classic', builtin.revision);
    const sample = componentSample(base.definition, detail);
    if (!('definition' in sample)) throw new Error(sample.problem);
    const personal = await client.templates.listAll('personal');
    const template = await client.templates.command('personal', personal.revision, { type: 'create', definitionFormat: 'full', definition: sample.definition });
    templateId = template.templateId;
    const reread = await client.templates.fullDetail('personal', templateId, template.revision);
    expect(reread.definition.effectRecipes[0]).toMatchObject({ configurationId, parametersJson: detail.configuration.parametersJson, fixedSeed: '18446744073709551615' });
    await client.scriptConfigurations.command(created.revision, { type: 'delete', configurationId });
    configurationId = '';
    expect((await client.templates.fullDetail('personal', templateId, template.revision)).definition.packages).toEqual(reread.definition.packages);
    expect(recorder.issues).toEqual([]);
  });
});
