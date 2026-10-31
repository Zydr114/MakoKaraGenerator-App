// 连真实 Server 的模板库（合同 1.9.0，规范 §13.1～§13.2；W3 设计 §2.3、§2.4）与模板固定试样（G17，合同 1.16.0，规范 §13.7）。
// 个人库属于测试 profile（.dev/it-templates），跨次运行保留：每个用例只增删自己创建的模板，结束时库回到原样。

import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { BackendError, MakoClient, MemoryIdentityStore } from '../../src/client';
import { micros, revision, TEMPLATE_PREVIEW_SAMPLE_ID, type StaticTemplateDefinition, type TemplatePreviewSource, type TemplateSummary } from '../../src/protocol';
import { ContractRecorder, startTestBackend, type TestBackend } from '../support/backend';

let backend: TestBackend;
let client: MakoClient;
const recorder = new ContractRecorder();
const stamp = Date.now().toString(36);

beforeAll(async () => {
  backend = await startTestBackend('templates');
  client = await MakoClient.connect(backend.provider, {
    identityStore: new MemoryIdentityStore(),
    environment: null,
    onExchange: recorder.observer,
  });
});

afterAll(async () => {
  client?.dispose();
  await backend?.stop();
});

async function builtinDefinition(templateId: string): Promise<StaticTemplateDefinition> {
  const { revision } = await client.templates.listAll('builtin');
  const detail = await client.templates.detail('builtin', templateId, revision);
  expect(detail.definitionAvailability).toBe('staticAvailable');
  return detail.definition!;
}

async function personal(): Promise<{ revision: string; templates: TemplateSummary[] }> {
  return client.templates.listAll('personal');
}

describe('模板库（真实 Server）', () => {
  it('公布读写能力；内置库只读，至少有“经典整行”和“逐字强调”两个静态模板', async () => {
    const { features } = await client.capabilities();
    for (const f of ['read', 'templates.read', 'templates.detail', 'commands.create', 'commands.replace', 'commands.copy', 'commands.rename', 'commands.delete']) {
      expect(features).toContain(`template-libraries.${f}`);
    }
    const { libraries } = await client.templates.libraries();
    expect(libraries).toEqual(expect.arrayContaining([expect.objectContaining({ libraryId: 'builtin', readOnly: true }), expect.objectContaining({ libraryId: 'personal', readOnly: false })]));
    const builtin = await client.templates.listAll('builtin');
    const byId = new Map(builtin.templates.map((t) => [t.templateId, t]));
    expect(byId.get('builtin:line-classic')).toMatchObject({ kind: 'line' });
    expect(byId.get('builtin:token-highlight')).toMatchObject({ kind: 'token', properties: ['unsungColor', 'sungColor'] });
  });

  it('新建→整体替换→改名→删除；保存后重读的定义与提交的一致；旧修订 409', async () => {
    const base = await builtinDefinition('builtin:line-classic');
    const before = await personal();
    const definition: StaticTemplateDefinition = { ...base, name: `集成-${stamp}` };
    const created = await client.templates.command('personal', before.revision, { type: 'create', definition });
    expect(created).toMatchObject({ changed: true, previousRevision: before.revision });
    const id = created.templateId;

    const detail = await client.templates.detail('personal', id, created.revision);
    expect(detail.definition).toEqual(definition);

    const replaced: StaticTemplateDefinition = {
      ...definition,
      style: { ...definition.style, typography: { ...definition.style.typography, fontSizePx: 64 }, stroke: { material: { kind: 'solid', color: { red: 0, green: 0, blue: 0, alpha: 255 } }, widthPx: 4, join: 'round', position: 'outside' } },
      properties: ['fontSizePx', 'strokeColor', 'strokeWidthPx', 'strokeSpec'],
    };
    const r2 = await client.templates.command('personal', created.revision, { type: 'replace', templateId: id, definition: replaced });
    expect(r2.templateId).toBe(id);
    expect((await client.templates.detail('personal', id, r2.revision)).definition).toEqual(replaced);

    const stale = await client.templates.command('personal', created.revision, { type: 'rename', templateId: id, name: '不会写入' }).catch((e: unknown) => e);
    expect(stale).toBeInstanceOf(BackendError);
    expect(stale).toMatchObject({ status: 409, code: 'revisionConflict' });

    const r3 = await client.templates.command('personal', r2.revision, { type: 'rename', templateId: id, name: `集成改名-${stamp}` });
    const renamed = (await personal()).templates.find((t) => t.templateId === id)!;
    expect(renamed.name).toBe(`集成改名-${stamp}`);

    await client.templates.command('personal', r3.revision, { type: 'delete', templateId: id });
    expect((await personal()).templates.some((t) => t.templateId === id)).toBe(false);
  });

  it('从内置库复制到个人库得到新身份，定义相同，名称为新名称', async () => {
    const builtin = await client.templates.listAll('builtin');
    const before = await personal();
    const copied = await client.templates.command('personal', before.revision, {
      type: 'copy',
      sourceLibraryId: 'builtin',
      sourceRevision: builtin.revision,
      templateId: 'builtin:token-highlight',
      name: `复制-${stamp}`,
    });
    expect(copied.templateId).not.toBe('builtin:token-highlight');
    const detail = await client.templates.detail('personal', copied.templateId, copied.revision);
    const source = await builtinDefinition('builtin:token-highlight');
    expect(detail.definition).toEqual({ ...source, name: `复制-${stamp}` });
    await client.templates.command('personal', copied.revision, { type: 'delete', templateId: copied.templateId });
  });

  it('内置库不接受命令', async () => {
    const { revision } = await client.templates.listAll('builtin');
    const result = await client.templates.command('builtin', revision, { type: 'delete', templateId: 'builtin:line-classic' }).catch((e: unknown) => e);
    expect(result).toBeInstanceOf(BackendError);
    expect((result as BackendError).status).toBeGreaterThanOrEqual(400);
  });

  it('G17 模板固定试样：已保存模板与同定义的草稿帧相同，改了定义帧就变；不写个人库、不能换输入；组合模板 422', async (ctx) => {
    const runtime = await client.preview.runtimeInfo();
    if (!runtime.enabled) {
      console.warn(`[skipped] 预览不可用：${runtime.disabledReason?.code ?? runtime.status}`);
      ctx.skip();
    }
    expect((await client.capabilities()).features).toContain('template-preview-sessions.create');
    const builtin = await client.templates.listAll('builtin');
    const classic = builtin.templates.find((t) => t.templateId === 'builtin:line-classic')!;
    const before = await personal();
    const size = { width: 320, height: 180 };
    const render = (source: TemplatePreviewSource) => client.preview.renderTemplateStill({ source, sampleId: TEMPLATE_PREVIEW_SAMPLE_ID, size, timeUs: micros(650_000) });

    const saved = await render({ kind: 'savedTemplate', libraryId: 'builtin', libraryRevision: builtin.revision, templateId: classic.templateId, templateRevision: classic.templateRevision });
    expect(saved.verified).toBe(true);
    expect(saved.preview).toMatchObject({ previewKind: 'template', committable: false, source: { kind: 'template' } });
    expect(saved.frame).toMatchObject({ width: 320, height: 180 });

    const definition = await builtinDefinition(classic.templateId);
    const draft = await render({ kind: 'draft', draftRevision: `it-${stamp}-1`, definition });
    expect(draft.sha256).toBe(saved.sha256);
    const bigger: StaticTemplateDefinition = { ...definition, style: { ...definition.style, typography: { ...definition.style.typography, fontSizePx: definition.style.typography.fontSizePx * 1.5 } } };
    const changed = await render({ kind: 'draft', draftRevision: `it-${stamp}-2`, definition: bigger });
    expect(changed.sha256).not.toBe(saved.sha256);
    expect((await personal()).revision).toBe(before.revision);

    const live = await client.preview.createTemplatePreview({ source: { kind: 'draft', draftRevision: `it-${stamp}-3`, definition }, sampleId: TEMPLATE_PREVIEW_SAMPLE_ID, size });
    const { previewGeneration, source } = live;
    await expect(client.preview.replaceInput(live.previewId, { expectedPreviewGeneration: previewGeneration, expectedSourceRevision: source.sourceRevision, source: { kind: 'project', sourceId: 'none', sourceRevision: revision('0') }, size })).rejects.toMatchObject({ status: 422, code: 'commandNotSupported' });
    await client.preview.close(live.previewId, { expectedPreviewGeneration: previewGeneration, expectedSourceRevision: source.sourceRevision });

    const composite: StaticTemplateDefinition = { ...definition, kind: 'composite', properties: ['unsungColor', 'sungColor'] };
    await expect(client.preview.createTemplatePreview({ source: { kind: 'draft', draftRevision: `it-${stamp}-4`, definition: composite }, sampleId: TEMPLATE_PREVIEW_SAMPLE_ID, size })).rejects.toMatchObject({ status: 422, code: 'commandNotSupported' });
    await expect(
      client.preview.createTemplatePreview({ source: { kind: 'savedTemplate', libraryId: 'builtin', libraryRevision: 'f'.repeat(64), templateId: classic.templateId, templateRevision: classic.templateRevision }, sampleId: TEMPLATE_PREVIEW_SAMPLE_ID, size }),
    ).rejects.toMatchObject({ status: 409 });
  });

  it('JSON 响应符合冻结 OpenAPI（R5 已在 1.18.0 修复：预览来源修订是不透明字符串）', () => {
    expect(recorder.issues).toEqual([]);
    expect([...recorder.validated].some((v) => v.includes('/templates/detail 200'))).toBe(true);
  });
});
