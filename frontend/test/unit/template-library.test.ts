import { describe, expect, it, vi } from 'vitest';
import { BackendError, type MakoClient } from '../../src/client';
import { NEW_TEMPLATE_NAME, NEW_TEMPLATE_SOURCE, TemplateLibraryStore } from '../../src/editor/library/template-library-store';
import { SAMPLE_SIZE, SAMPLE_TIME_US, TEMPLATE_PREVIEW, TemplateThumbnails, THUMBNAIL_SAMPLE_ID, THUMBNAIL_SIZE } from '../../src/editor/library/template-thumbnails';
import { TEMPLATE_PREVIEW_OPTIONS, TemplateWorkshop, workshopTitle, WORKSHOP_DURATION_US } from '../../src/editor/library/template-workshop';
import { addStop, gradientProblem, removeStop, setStopOffset } from '../../src/editor/model/gradient';
import { changeKind, definitionProblem, gradientFrom, groupAllowed, groupValueText, propertySummary, setGroupIncluded, setTemplateGlow, templateGlowEditing, TEMPLATE_GROUPS } from '../../src/editor/model/template-groups';
import {
  type EffectPackageInspectResponse,
  type EffectPackageReference,
  type FullTemplateDefinition,
  type TemplateLibraryCommand,
  type TemplateLibraryId,
  type TemplatePreviewInput,
  type TemplatePreviewOptionsResponse,
  type TemplateSummary,
} from '../../src/protocol';

const STYLE: FullTemplateDefinition['style'] = {
  typography: { fontFamilyName: null, fallbackFamilyNames: [], fontSizePx: 72, fontWeight: 400, isBold: false, isItalic: false, letterSpacingPx: 0 },
  unsungFill: { material: { kind: 'solid', color: { red: 74, green: 80, blue: 96, alpha: 255 } } },
  sungFill: { material: { kind: 'solid', color: { red: 76, green: 141, blue: 255, alpha: 255 } } },
  stroke: null,
  shadow: null,
  glow: null,
  verticalCenterRatio: 0.5,
  horizontalAlignment: 'center',
  opacity: 1,
  scale: 1,
};

const definition = (d: Omit<FullTemplateDefinition, 'effectRecipes' | 'packages'> & Partial<FullTemplateDefinition>): FullTemplateDefinition => ({
  effectRecipes: [],
  packages: [],
  ...d,
});

/**
 * 内存里的两个库：个人库按命令改修订，conflictNext 让下一条命令 409。
 * 完整详情里的包都给成绑定当前库修订的 library 引用；命令里带过期的 library 引用时 409（与后端一致）。
 */
function fakeLibraries() {
  const libraries: Record<TemplateLibraryId, { revision: number; templates: Map<string, FullTemplateDefinition> }> = {
    builtin: { revision: 1, templates: new Map([[NEW_TEMPLATE_SOURCE, definition({ name: '经典整行', kind: 'line', style: STYLE, properties: ['fontSizePx', 'unsungColor'] })]]) },
    personal: { revision: 0, templates: new Map() },
  };
  const versions = new Map<string, number>();
  const commands: { expectedRevision: string; command: TemplateLibraryCommand }[] = [];
  let nextId = 1;
  let conflictNext = false;
  const rev = (id: TemplateLibraryId) => (id === 'personal' && libraries.personal.revision === 0 ? 'missing' : `r${libraries[id].revision}`);
  const summary = (id: string, d: FullTemplateDefinition): TemplateSummary => ({
    templateId: id,
    name: d.name,
    kind: d.kind,
    templateRevision: `t${id}-${versions.get(id) ?? 0}`,
    properties: d.properties,
    hasEffectRecipes: d.effectRecipes.length > 0,
    hasPackageMaterials: false,
    hasPackages: d.packages.length > 0,
  });
  const write = (id: string, d: FullTemplateDefinition) => {
    libraries.personal.templates.set(id, d);
    versions.set(id, (versions.get(id) ?? 0) + 1);
  };
  const templates = {
    listAll: async (id: TemplateLibraryId) => ({ revision: rev(id), templates: [...libraries[id].templates].map(([k, d]) => summary(k, d)) }),
    fullDetail: async (id: TemplateLibraryId, templateId: string, expectedRevision: string) => {
      if (expectedRevision !== rev(id)) throw new BackendError(409, { code: 'revisionConflict', message: 'stale' } as never);
      const d = libraries[id].templates.get(templateId)!;
      const s = summary(templateId, d);
      const packages = d.packages.map(
        (p): EffectPackageReference => ({
          kind: 'library',
          packageId: p.packageId,
          packageRevision: p.packageRevision,
          libraryId: id,
          libraryRevision: rev(id),
          templateId,
          templateRevision: s.templateRevision,
        }),
      );
      return {
        libraryId: id,
        revision: rev(id),
        summary: s,
        definitionFormat: 'full',
        definitionAvailability: 'fullAvailable',
        definition: { ...d, packages },
        packageClosureDetails: [],
        protocolVersion: '1',
      };
    },
    command: async (_id: 'personal', expectedRevision: string, command: TemplateLibraryCommand) => {
      commands.push({ expectedRevision, command });
      const stale = 'definition' in command && (command.definition as Partial<FullTemplateDefinition>).packages?.some((p: EffectPackageReference) => p.kind === 'library' && p.libraryRevision !== rev('personal'));
      if (conflictNext || stale || expectedRevision !== rev('personal')) {
        conflictNext = false;
        throw new BackendError(409, { code: 'revisionConflict', message: 'stale' } as never);
      }
      const previousRevision = rev('personal');
      let templateId = '';
      if (command.type === 'create') write((templateId = `p${nextId++}`), command.definition as FullTemplateDefinition);
      if (command.type === 'replace') write((templateId = command.templateId), command.definition as FullTemplateDefinition);
      if (command.type === 'rename') {
        templateId = command.templateId;
        write(templateId, { ...libraries.personal.templates.get(templateId)!, name: command.name });
      }
      if (command.type === 'delete') libraries.personal.templates.delete((templateId = command.templateId));
      libraries.personal.revision++;
      return { libraryId: 'personal', previousRevision, revision: rev('personal'), changed: true, templateId, protocolVersion: '1' };
    },
  };
  return {
    client: { templates } as unknown as MakoClient,
    commands,
    libraries,
    write,
    conflict: () => void (conflictNext = true),
    bump: () => void libraries.personal.revision++,
  };
}

describe('样式库状态', () => {
  it('个人库读取失败时仍可查看内置模板，重试成功清除读取错误并保留其他提示', async () => {
    const fake = fakeLibraries();
    const list = fake.client.templates.listAll.bind(fake.client.templates);
    let failed = true;
    vi.spyOn(fake.client.templates, 'listAll').mockImplementation(async (id) => {
      if (id === 'personal' && failed) throw new BackendError(422, { code: 'commandRejected', failureCode: 'LibraryInvalid', message: 'invalid' } as never);
      return list(id);
    });
    const store = new TemplateLibraryStore(fake.client);
    await store.load();
    expect(store.state.status).toBe('ready');
    expect(store.state.personal).toBeUndefined();
    expect(store.state.personalError).toContain('个人模板库格式不受支持或内容无效，后端拒绝读写；原库保持不变。');
    await store.select({ libraryId: 'builtin', templateId: NEW_TEMPLATE_SOURCE });
    expect(store.state.detail?.definition.name).toBe('经典整行');
    await store.createDraft();
    expect(store.state.selection?.libraryId).toBe('builtin');
    expect(await store.copy('builtin', NEW_TEMPLATE_SOURCE, '副本')).toBeUndefined();
    expect(fake.commands).toHaveLength(0);
    store.state$.set({ ...store.state, notice: { level: 'warning', message: '其他提示' } });
    failed = false;
    await store.load();
    expect(store.state.personal?.revision).toBe('missing');
    expect(store.state.personalError).toBeUndefined();
    expect(store.state.notice?.message).toBe('其他提示');
  });

  it('重读失败丢弃旧个人库修订，保留脏草稿并在恢复后保存', async () => {
    const fake = fakeLibraries();
    const store = new TemplateLibraryStore(fake.client);
    await store.load();
    await store.createDraft();
    await store.save();
    store.edit({ ...store.state.draft!.definition, name: '未保存的修改' });
    const list = fake.client.templates.listAll.bind(fake.client.templates);
    let failed = true;
    vi.spyOn(fake.client.templates, 'listAll').mockImplementation(async (id) => {
      if (id === 'personal' && failed) throw new Error('读取失败');
      return list(id);
    });
    await store.load();
    expect(store.state.personal).toBeUndefined();
    expect(store.state.draft?.definition.name).toBe('未保存的修改');
    const count = fake.commands.length;
    expect(await store.save()).toBe(false);
    expect(await store.remove('p1')).toBe(false);
    expect(fake.commands).toHaveLength(count);
    failed = false;
    await store.load();
    expect(store.state.draft?.definition.name).toBe('未保存的修改');
    expect(store.isDirty).toBe(true);
    expect(await store.save()).toBe(true);
    expect(fake.libraries.personal.templates.get('p1')?.name).toBe('未保存的修改');
  });

  it('只含辉光不透明度且样式为 null 时编辑默认叶值，选择关闭后才明确关闭目标辉光', () => {
    const partial = definition({ name: '辉光默认叶', kind: 'line', style: STYLE, properties: ['glowOpacity'] });
    const state = templateGlowEditing(partial);
    expect(state).toEqual({ mode: 'partial', value: { color: { red: 0, green: 0, blue: 0, alpha: 0 }, opacity: 1, spreadPx: 0, blurRadiusPx: 0 } });
    expect(groupValueText('glow', partial.style, partial.properties)).toBe('仅覆盖：不透明度 100%');
    const edited = setTemplateGlow(partial, { ...state.value!, opacity: 0.5 }, 'glowOpacity');
    expect(edited.properties).toEqual(['glowOpacity']);
    expect(templateGlowEditing(edited).mode).toBe('partial');
    const closed = setTemplateGlow(partial, null);
    expect(closed.properties).toEqual(['glowSpec', 'glowOpacity']);
    expect(templateGlowEditing(closed)).toEqual({ mode: 'off', value: null });
    expect(groupValueText('glow', closed.style, closed.properties)).toBe('关');
  });

  it('编辑辉光叶属性时保留并补齐模板属性；整体关闭写入 glowSpec', () => {
    const partial = definition({ name: '辉光叶', kind: 'line', style: { ...STYLE, glow: { color: { red: 255, green: 255, blue: 255, alpha: 255 }, opacity: 1, spreadPx: 1, blurRadiusPx: 8 } }, properties: ['glowColor'] });
    const withBlur = setTemplateGlow(partial, partial.style.glow, 'glowBlurRadiusPx');
    expect(withBlur.properties).toEqual(['glowColor', 'glowBlurRadiusPx']);
    const closed = setTemplateGlow(withBlur, null);
    expect(closed.properties).toEqual(['glowSpec', 'glowColor', 'glowBlurRadiusPx']);
    expect(closed.style.glow).toBeNull();
  });

  it('新建草稿以“经典整行”为起点，算未保存；保存发 create（首次修订 missing），之后选中新模板、不再有修改', async () => {
    const fake = fakeLibraries();
    const store = new TemplateLibraryStore(fake.client);
    await store.load();
    await store.createDraft();
    expect(store.state.selection).toEqual({ libraryId: 'personal', templateId: null });
    expect(store.state.draft?.definition.name).toBe(NEW_TEMPLATE_NAME);
    expect(store.isDirty).toBe(true);

    expect(await store.save()).toBe(true);
    expect(fake.commands[0]).toMatchObject({ expectedRevision: 'missing', command: { type: 'create', definitionFormat: 'full', definition: { name: NEW_TEMPLATE_NAME, kind: 'line' } } });
    expect(store.state.selection).toEqual({ libraryId: 'personal', templateId: 'p1' });
    expect(store.state.personal?.templates.map((t) => t.name)).toEqual([NEW_TEMPLATE_NAME]);
    expect(store.isDirty).toBe(false);
  });

  it('命令 409：提示并重读；选中模板在别处被替换时，有修改的草稿保留修改、改以新定义为基准', async () => {
    const fake = fakeLibraries();
    const store = new TemplateLibraryStore(fake.client);
    await store.load();
    await store.createDraft();
    await store.save();

    // 本地改了字号（第 4 步的编辑界面会经 store 改草稿；这里直接改状态模拟）。
    const draft = store.state.draft!;
    const edited = { ...draft.definition, style: { ...draft.definition.style, typography: { ...draft.definition.style.typography, fontSizePx: 40 } } };
    store.state$.set({ ...store.state, draft: { ...draft, definition: edited } });
    // 别处改了同一模板的名称。
    fake.write('p1', { ...draft.base, name: '别处改名' });
    fake.bump();

    expect(await store.save()).toBe(false);
    expect(store.state.notice?.level).toBe('warning');
    expect(store.state.draft?.base.name).toBe('别处改名');
    expect(store.state.draft?.definition.style.typography.fontSizePx).toBe(40);
    expect(store.isDirty).toBe(true);
  });

  it('草稿历史：每次修改一步，撤销、重做；与当前相同的修改不记；放弃修改清空历史', async () => {
    const fake = fakeLibraries();
    const store = new TemplateLibraryStore(fake.client);
    await store.load();
    await store.createDraft();
    const start = store.state.draft!.definition;
    const a = { ...start, name: 'A' };
    store.edit(a);
    store.edit({ ...a });
    store.edit({ ...a, name: 'B' });
    expect(store.state.draft?.undo).toHaveLength(2);
    store.undo();
    expect(store.state.draft?.definition.name).toBe('A');
    store.redo();
    expect(store.state.draft?.definition.name).toBe('B');
    store.undo();
    store.edit({ ...a, name: 'C' });
    expect(store.state.draft?.redo).toEqual([]);

    await store.save();
    store.edit({ ...store.state.draft!.definition, name: 'D' });
    expect(store.isDirty).toBe(true);
    store.discardDraft();
    expect(store.isDirty).toBe(false);
    expect(store.state.draft?.undo).toEqual([]);
  });

  it('库修订变了：草稿当前定义和历史里的 library 引用换成新的，修改与撤销都保留，保存不再 409', async () => {
    const fake = fakeLibraries();
    const style = { ...STYLE, unsungFill: { material: { kind: 'package' as const, packageId: 'pk', revision: 'v1', exportId: 'ink', parametersJson: '{}' } } };
    fake.write('p1', definition({ name: '含效果', kind: 'line', style, properties: ['opacity'], packages: [{ kind: 'asset', packageId: 'pk', packageRevision: 'v1', packageAssetId: 'a1' }] }));
    fake.bump();
    const store = new TemplateLibraryStore(fake.client);
    await store.load();
    await store.select({ libraryId: 'personal', templateId: 'p1' });
    const before = store.state.draft!;
    expect(before.definition.packages[0]).toMatchObject({ kind: 'library', libraryRevision: 'r1' });
    store.edit({ ...before.definition, name: '改过' });

    // 别处在库里加了另一个模板：选中模板的内容没变，但库修订变了。
    fake.write('p9', definition({ name: '别的', kind: 'line', style: STYLE, properties: ['opacity'] }));
    fake.bump();
    await store.load();
    const after = store.state.draft!;
    expect(after.definition.name).toBe('改过');
    expect(after.definition.packages[0]).toMatchObject({ kind: 'library', libraryRevision: 'r2', packageId: 'pk' });
    expect(after.undo[0]!.packages[0]).toMatchObject({ libraryRevision: 'r2' });
    expect(store.isDirty).toBe(true);
    expect(await store.save()).toBe(true);
    expect(fake.libraries.personal.templates.get('p1')?.name).toBe('改过');
  });

  it('效果包：上传并检查后加入草稿（不进历史），用到时进 packages；同一包新修订替换旧修订；在用的包不能移除', async () => {
    const fake = fakeLibraries();
    const inspected = (revision: string) =>
      ({
        reference: { kind: 'asset', packageId: 'pk', packageRevision: revision, packageAssetId: `asset-${revision}` },
        packageId: 'pk',
        packageRevision: revision,
        name: '墨水',
        version: revision,
        structureValid: true,
        parameters: [],
        exports: [{ id: 'ink', kind: 'material', name: '墨水', parameterIds: [], bindingKinds: [], declaration: { regions: ['fill'] } }],
        files: [],
        diagnostics: [],
      }) as unknown as EffectPackageInspectResponse;
    const revisions = ['v1', 'v2'];
    const uploads: string[] = [];
    const client = {
      ...fake.client,
      assets: { upload: async (file: File) => (uploads.push(file.name), { assetId: `asset-${revisions[uploads.length - 1]}` }) },
      effectPackages: { inspect: async (input: { packageAssetId: string }) => inspected(input.packageAssetId.replace('asset-', '')) },
    } as unknown as MakoClient;
    const store = new TemplateLibraryStore(client);
    await store.load();
    await store.createDraft();

    const v1 = await store.inspectPackageFile(new File(['x'], 'ink.mkfx'));
    expect(store.state.catalog.has(`pk\u0000v1`)).toBe(true);
    expect(store.addPackage(v1)).toEqual([]);
    expect(store.state.draft?.imported).toEqual([v1.reference]);
    expect(store.state.draft?.undo).toEqual([]);
    expect(store.state.draft?.definition.packages).toEqual([]);

    const draft = store.state.draft!.definition;
    const material = { kind: 'package' as const, packageId: 'pk', revision: 'v1', exportId: 'ink', parametersJson: '{}' };
    store.edit({ ...draft, style: { ...draft.style, unsungFill: { material } } });
    expect(store.state.draft?.definition.packages).toEqual([v1.reference]);
    expect(store.removePackage('pk\u0000v1')).toEqual(['未唱填充']);
    expect(store.state.draft?.imported).toHaveLength(1);

    const v2 = await store.inspectPackageFile(new File(['y'], 'ink2.mkfx'));
    expect(store.addPackage(v2)).toEqual(['v1']);
    expect(store.state.draft?.definition.style.unsungFill.material).toMatchObject({ revision: 'v2' });
    expect(store.state.draft?.definition.packages).toEqual([v2.reference]);
    expect(store.removePackage('pk\u0000v1')).toEqual([]);
    expect(store.state.draft?.imported).toEqual([v2.reference]);

    // 撤销回到用 v1 的那一步：v1 的引用从历史里找回，packages 仍然恰好是用到的包。
    store.undo();
    expect(store.state.draft?.definition.packages).toEqual([v1.reference]);
    expect(store.addPackage({ ...v2, structureValid: false })).toEqual([]);
  });

  it('选中的模板被删除后清空选择', async () => {
    const fake = fakeLibraries();
    const store = new TemplateLibraryStore(fake.client);
    await store.load();
    await store.createDraft();
    await store.save();
    expect(await store.remove('p1')).toBe(true);
    expect(store.state.selection).toBeNull();
    expect(store.state.draft).toBeUndefined();
  });
});

describe('模板属性分组', () => {
  it('用途限制：文本单元包含辉光；组合不能有位置', () => {
    const ids = (kind: 'line' | 'token' | 'composite') => TEMPLATE_GROUPS.filter((g) => groupAllowed(g, kind)).map((g) => g.id);
    expect(ids('token')).toEqual(['unsung', 'sung', 'stroke', 'shadow', 'glow', 'opacity']);
    expect(ids('composite')).not.toContain('position');
    expect(ids('line')).toHaveLength(12);
    expect(propertySummary('token', ['unsungColor', 'sungColor'])).toBe('未唱填充、已唱填充');
    expect(propertySummary('token', ['unsungColor', 'sungColor', 'strokeSpec', 'shadowColor', 'glowSpec', 'opacity'])).toBe('全部属性');
  });

  it('开关组：重新打开原来包含的组恢复原子集，新打开的组写整组；换用途去掉不允许的属性', () => {
    const fill = TEMPLATE_GROUPS.find((g) => g.id === 'unsung')!;
    const stroke = TEMPLATE_GROUPS.find((g) => g.id === 'stroke')!;
    const base = definition({ name: 'x', kind: 'token', style: STYLE, properties: ['unsungColor', 'sungColor'] });
    const off = setGroupIncluded(base, fill, false, base.properties);
    expect(off.properties).toEqual(['sungColor']);
    expect(setGroupIncluded(off, fill, true, base.properties).properties).toEqual(['unsungColor', 'sungColor']);
    expect(setGroupIncluded(base, stroke, true, base.properties).properties).toEqual(['unsungColor', 'sungColor', 'strokeColor', 'strokeWidthPx', 'strokeSpec']);

    const line: FullTemplateDefinition = { ...base, kind: 'line', properties: ['fontSizePx', 'verticalCenterRatio', 'opacity'] };
    expect(changeKind(line, 'composite').properties).toEqual(['fontSizePx', 'opacity']);
    expect(changeKind(line, 'token').properties).toEqual(['opacity']);
  });
});

describe('渐变色标', () => {
  const black = { red: 0, green: 0, blue: 0, alpha: 255 };
  const white = { red: 255, green: 255, blue: 255, alpha: 255 };
  const base = gradientFrom(black);
  const g = base.kind === 'gradient' ? { ...base.gradient, stops: [{ offset: 0, color: black }, { offset: 1, color: white }] } : undefined!;

  it('添加在最宽间隙的中点、颜色取平均；改位置后中间色标重新排序，首末固定；只能删中间的', () => {
    const three = addStop(g);
    expect(three.stops.map((s) => s.offset)).toEqual([0, 0.5, 1]);
    expect(three.stops[1]!.color).toEqual({ red: 128, green: 128, blue: 128, alpha: 255 });
    const four = addStop(three);
    expect(four.stops.map((s) => s.offset)).toEqual([0, 0.25, 0.5, 1]);
    expect(setStopOffset(four, 1, 0.9).stops.map((s) => s.offset)).toEqual([0, 0.5, 0.9, 1]);
    expect(setStopOffset(four, 0, 0.3)).toBe(four);
    expect(removeStop(four, 3)).toBe(four);
    expect(removeStop(four, 1).stops).toHaveLength(3);
  });

  it('不合法：首末不是 0／1、倒序、径向半径不大于 0；定义里任何一处材质不合法都不能保存', () => {
    expect(gradientProblem(g)).toBeUndefined();
    expect(gradientProblem({ ...g, stops: [{ offset: 0.1, color: black }, { offset: 1, color: white }] })).toMatch('0%');
    expect(gradientProblem({ ...g, stops: [{ offset: 0, color: black }, { offset: 0.6, color: black }, { offset: 0.4, color: black }, { offset: 1, color: white }] })).toMatch('倒序');
    expect(gradientProblem({ ...g, kind: 'radial', radiusX: 0 })).toMatch('半径');
    const bad = definition({ name: 'x', kind: 'line', style: { ...STYLE, sungFill: { material: { kind: 'gradient', gradient: { ...g, kind: 'radial', radiusY: 0 } } } }, properties: ['opacity'] });
    expect(definitionProblem(bad)).toMatch('已唱填充');
    expect(definitionProblem({ ...bad, style: STYLE, properties: [] })).toMatch('至少要包含一组属性或一个效果');
  });
});

describe('模板试样（G17）', () => {
  const summary = (templateId: string, revision: string, extra: Partial<TemplateSummary> = {}): TemplateSummary => ({
    templateId,
    name: templateId,
    kind: 'line',
    templateRevision: revision,
    properties: [],
    hasEffectRecipes: false,
    hasPackageMaterials: false,
    hasPackages: false,
    ...extra,
  });

  /** 每次 renderTemplateStill 挂起，测试逐个 resolve／reject。 */
  function fakeClient(features: string[] = [TEMPLATE_PREVIEW]) {
    const pending: { input: TemplatePreviewInput & { signal?: AbortSignal }; resolve: (sha: string) => void; reject: (error: unknown) => void }[] = [];
    const client = {
      capabilities: async () => ({ features }),
      preview: {
        renderTemplateStill: (input: TemplatePreviewInput & { signal?: AbortSignal }) =>
          new Promise((resolve, reject) => pending.push({ input, resolve: (sha) => resolve({ blob: new Blob([sha]), sha256: sha }), reject })),
      },
    } as unknown as MakoClient;
    return { client, pending };
  }
  const flush = () => new Promise((r) => setTimeout(r, 0));
  const notSupported = () => new BackendError(422, { code: 'commandNotSupported', message: 'composite', protocolVersion: '1' });

  it('缩略图：按库、模板和模板修订缓存，同时最多 2 个请求；组合模板写明原因；失败后库修订变了才重试', async () => {
    const { client, pending } = fakeClient();
    const thumbnails = new TemplateThumbnails(client);
    const a = summary('a', 'ta1');
    thumbnails.request('builtin', 'r1', a);
    thumbnails.request('builtin', 'r1', summary('b', 'tb1'));
    thumbnails.request('builtin', 'r1', summary('c', 'tc1', { kind: 'composite' }));
    thumbnails.request('builtin', 'r1', a);
    await flush();
    expect(pending).toHaveLength(2);
    expect(pending[0]!.input).toMatchObject({
      source: { kind: 'savedTemplate', libraryId: 'builtin', libraryRevision: 'r1', templateId: 'a', templateRevision: 'ta1' },
      sampleId: THUMBNAIL_SAMPLE_ID,
      size: THUMBNAIL_SIZE,
      timeUs: String(SAMPLE_TIME_US),
    });
    expect(thumbnails.state.available).toBe(true);

    pending[0]!.resolve('sha-a');
    pending[1]!.reject(new BackendError(503, { code: 'previewUnavailable', message: 'busy', protocolVersion: '1' }));
    await flush();
    expect(pending).toHaveLength(3);
    pending[2]!.reject(notSupported());
    await flush();
    expect(thumbnails.get('builtin', a)).toMatchObject({ status: 'ready', sha256: 'sha-a' });
    expect(thumbnails.get('builtin', summary('c', 'tc1'))).toMatchObject({ status: 'unsupported', message: '组合模板暂不支持试样' });
    expect(thumbnails.get('builtin', summary('b', 'tb1'))?.status).toBe('error');

    // 同一库修订不重试失败的；库修订变了重试；模板修订变了是新键。
    thumbnails.request('builtin', 'r1', summary('b', 'tb1'));
    thumbnails.request('builtin', 'r2', a);
    expect(pending).toHaveLength(3);
    thumbnails.request('builtin', 'r2', summary('b', 'tb1'));
    thumbnails.request('builtin', 'r2', summary('a', 'ta2'));
    await flush();
    expect(pending.slice(3).map((p) => p.input.source)).toMatchObject([{ templateId: 'b' }, { templateId: 'a', templateRevision: 'ta2' }]);
  });

  it('缩略图：在途和排队时库修订变了，按旧修订失败的改用新修订重试', async () => {
    const { client, pending } = fakeClient();
    const thumbnails = new TemplateThumbnails(client, undefined, 1);
    const [x, y] = [summary('x', 'tx1'), summary('y', 'ty1')];
    thumbnails.request('personal', 'r1', x);
    thumbnails.request('personal', 'r1', y);
    await flush();
    // 复制模板后库修订成了 r2：x 在途，y 还在排队。
    thumbnails.request('personal', 'r2', x);
    thumbnails.request('personal', 'r2', y);
    expect(pending).toHaveLength(1);
    pending[0]!.reject(new BackendError(409, { code: 'revisionConflict', message: 'stale', protocolVersion: '1' }));
    await flush();
    expect(pending.map((p) => [p.input.source.kind === 'savedTemplate' && p.input.source.templateId, p.input.source.kind === 'savedTemplate' && p.input.source.libraryRevision])).toEqual([
      ['x', 'r1'],
      ['y', 'r2'],
    ]);
    pending[1]!.resolve('sha-y');
    await flush();
    expect(pending[2]!.input.source).toMatchObject({ templateId: 'x', libraryRevision: 'r2' });
    pending[2]!.resolve('sha-x');
    await flush();
    expect([thumbnails.get('personal', x)?.status, thumbnails.get('personal', y)?.status]).toEqual(['ready', 'ready']);
  });

  it('后端没有开放模板试样时不请求', async () => {
    const { client, pending } = fakeClient([]);
    const thumbnails = new TemplateThumbnails(client);
    thumbnails.request('builtin', 'r1', summary('a', 'ta1'));
    await flush();
    expect(thumbnails.state).toEqual({ available: false, frames: new Map() });
    thumbnails.request('builtin', 'r1', summary('b', 'tb1'));
    await flush();
    expect(pending).toHaveLength(0);

    const workshop = new TemplateWorkshop(client, undefined, 0);
    workshop.show({ kind: 'saved', libraryId: 'builtin', libraryRevision: 'r1', summary: summary('a', 'ta1') }, 'builtin|a');
    await flush();
    expect(workshop.state.available).toBe(false);
    expect(workshop.state.frame).toBeUndefined();
  });
});

describe('试样画布（W7 §2.5）', () => {
  const summary = (templateId: string, revision: string): TemplateSummary => ({
    templateId,
    name: templateId,
    kind: 'line',
    templateRevision: revision,
    properties: [],
    hasEffectRecipes: false,
    hasPackageMaterials: false,
    hasPackages: false,
  });
  const target = (key: string) => ({ key, binding: { kind: key, ids: [], includeDescendants: false } });
  const options = (single: string[], multi: string[] = single): TemplatePreviewOptionsResponse => ({
    source: { kind: 'template', sourceId: 's', sourceRevision: 'sr' } as never,
    samples: [
      { sampleId: 'workshop-single-line-v1', multiLine: false, durationUs: '12000000', targets: single.map(target) },
      { sampleId: 'workshop-multi-line-v1', multiLine: true, durationUs: '12000000', targets: multi.map(target) },
    ],
    materialSlots: [
      { slot: 'unsungFill', editable: true, selected: false, present: true, material: null },
      { slot: 'stroke', editable: true, selected: false, present: false, material: null },
    ],
    defaultSampleId: 'workshop-single-line-v1',
    protocolVersion: '1',
  });

  /**
   * 假后端：试样选项按 nextOptions 给；会话 ID 递增；seek 换新代次；帧的 sha 是“会话@时刻”。
   * hold 为真时 waitForFrame 挂起，由测试逐个放行。
   */
  function fakeClient() {
    let nextOptions = options(['line', 'project', 'clip']);
    let sessions = 0;
    let hold = false;
    const held: (() => void)[] = [];
    const log = {
      options: [] as unknown[],
      creates: [] as TemplatePreviewInput[],
      frames: [] as { previewId: string; timeUs: string; intent: string }[],
      closes: [] as string[],
    };
    const generations = new Map<string, number>();
    const client = {
      capabilities: async () => ({ features: [TEMPLATE_PREVIEW, TEMPLATE_PREVIEW_OPTIONS] }),
      templates: {
        previewOptions: async (source: unknown) => {
          log.options.push(source);
          return nextOptions;
        },
      },
      preview: {
        createTemplatePreview: async (input: TemplatePreviewInput) => {
          log.creates.push(input);
          const previewId = `v${++sessions}`;
          generations.set(previewId, 1);
          const echo = { sampleId: input.sampleId, targetKey: input.targetKey, ...(input.materialSlot ? { materialSlot: input.materialSlot } : {}) };
          return { previewId, previewGeneration: '1', source: { sourceRevision: 'sr', templateSample: echo } };
        },
        requestFrame: async (previewId: string, input: { timeUs: string; intent: string }) => {
          log.frames.push({ previewId, timeUs: input.timeUs, intent: input.intent });
          if (input.intent === 'seek') generations.set(previewId, generations.get(previewId)! + 1);
          return { previewGeneration: String(generations.get(previewId)) };
        },
        waitForFrame: async (previewId: string, expect: { requestSeq: string }, opts: { signal?: AbortSignal }) => {
          if (hold) await new Promise<void>((resolve) => held.push(resolve));
          if (opts.signal?.aborted) throw new DOMException('aborted', 'AbortError');
          const last = log.frames.filter((f) => f.previewId === previewId).at(-1)!;
          return { previewId, requestSeq: expect.requestSeq, content: { sha256: `${previewId}@${last.timeUs}` } };
        },
        fetchFrame: async (frame: { content: { sha256: string } }) => ({ blob: new Blob([frame.content.sha256]), sha256: frame.content.sha256, verified: true }),
        closeBestEffort: async (previewId: string) => {
          log.closes.push(previewId);
        },
      },
    } as unknown as MakoClient;
    return {
      client,
      log,
      setOptions: (next: TemplatePreviewOptionsResponse) => void (nextOptions = next),
      hold: (on: boolean) => void (hold = on),
      release: () => held.splice(0).forEach((r) => r()),
    };
  }

  const flush = () => new Promise((r) => setTimeout(r, 0));
  const saved = (templateId: string, revision: string) => ({ kind: 'saved' as const, libraryId: 'builtin' as const, libraryRevision: 'r1', summary: summary(templateId, revision) });

  it('先取试样选项：默认单行样本、第一个目标、3 秒；换目标或样本重建会话并关掉旧的；拖时刻前进用 playback、后退用 seek', async () => {
    vi.useFakeTimers();
    try {
      const { client, log } = fakeClient();
      const workshop = new TemplateWorkshop(client, undefined, 300);
      workshop.show(saved('a', 'ta1'), 'builtin|a');
      await vi.advanceTimersByTimeAsync(0);
      expect(log.options).toEqual([{ kind: 'savedTemplate', libraryId: 'builtin', libraryRevision: 'r1', templateId: 'a', templateRevision: 'ta1' }]);
      expect(log.creates[0]).toMatchObject({ sampleId: 'workshop-single-line-v1', targetKey: 'line', size: SAMPLE_SIZE });
      expect(log.creates[0]).not.toHaveProperty('materialSlot');
      expect(log.frames).toEqual([{ previewId: 'v1', timeUs: '3000000', intent: 'playback' }]);
      expect(workshop.state.frame).toMatchObject({ status: 'ready', sha256: 'v1@3000000' });
      expect(workshopTitle(workshop.state)).toBe('试样 · 单行样本 · 整行 · 0:03.000');

      workshop.seek(5_000_000);
      await vi.advanceTimersByTimeAsync(0);
      workshop.seek(1_000_000);
      await vi.advanceTimersByTimeAsync(0);
      workshop.seek(99_000_000);
      await vi.advanceTimersByTimeAsync(0);
      expect(log.frames.slice(1).map((f) => [f.timeUs, f.intent])).toEqual([
        ['5000000', 'playback'],
        ['1000000', 'seek'],
        [String(WORKSHOP_DURATION_US), 'playback'],
      ]);

      // 换目标：重建会话，旧会话关闭；材质焦点不存在的部位不传。
      workshop.setTarget('clip');
      await vi.advanceTimersByTimeAsync(0);
      expect(log.closes).toEqual(['v1']);
      expect(log.creates[1]).toMatchObject({ targetKey: 'clip' });
      workshop.setMaterialSlot('unsungFill');
      await vi.advanceTimersByTimeAsync(0);
      expect(log.creates[2]).toMatchObject({ targetKey: 'clip', materialSlot: 'unsungFill' });
      // 换样本：多行样本也有 clip，目标保留。
      workshop.setSample('workshop-multi-line-v1');
      await vi.advanceTimersByTimeAsync(0);
      expect(log.creates[3]).toMatchObject({ sampleId: 'workshop-multi-line-v1', targetKey: 'clip', materialSlot: 'unsungFill' });
      expect(log.closes).toEqual(['v1', 'v2', 'v3']);

      workshop.dispose();
      await vi.advanceTimersByTimeAsync(0);
      expect(log.closes).toEqual(['v1', 'v2', 'v3', 'v4']);
    } finally {
      vi.useRealTimers();
    }
  });

  it('材质焦点：描边关闭时不传，读到描边打开的新选项后按记下的部位传；标题写焦点，回显进状态', async () => {
    vi.useFakeTimers();
    try {
      const { client, log, setOptions } = fakeClient();
      const workshop = new TemplateWorkshop(client, undefined, 0);
      workshop.show(saved('a', 'ta1'), 'personal|a');
      await vi.advanceTimersByTimeAsync(0);
      workshop.setMaterialSlot('stroke');
      await vi.advanceTimersByTimeAsync(0);
      expect(workshop.state.materialSlot).toBeUndefined();
      expect(log.creates).toHaveLength(1);

      const withStroke = options(['line']);
      withStroke.materialSlots[1]!.present = true;
      setOptions(withStroke);
      workshop.show(saved('a', 'ta2'), 'personal|a');
      await vi.advanceTimersByTimeAsync(0);
      expect(log.creates.at(-1)).toMatchObject({ materialSlot: 'stroke' });
      expect(workshop.state.echoedSlot).toBe('stroke');
      expect(workshopTitle(workshop.state)).toBe('试样 · 单行样本 · 整行 · 材质焦点 描边 · 0:03.000');

      workshop.setMaterialSlot(undefined);
      await vi.advanceTimersByTimeAsync(0);
      expect(log.creates.at(-1)).not.toHaveProperty('materialSlot');
      expect(workshop.state.echoedSlot).toBeUndefined();
      workshop.dispose();
    } finally {
      vi.useRealTimers();
    }
  });

  it('草稿停一会儿再取选项，只保留最新；同一模板更新时留着上一张；草稿改回原样不重新取帧；改模板时目标不在了取第一个', async () => {
    vi.useFakeTimers();
    try {
      const { client, log, setOptions, hold, release } = fakeClient();
      const workshop = new TemplateWorkshop(client, undefined, 300);
      workshop.show(saved('a', 'ta1'), 'personal|a');
      await vi.advanceTimersByTimeAsync(0);
      workshop.setTarget('project');
      await vi.advanceTimersByTimeAsync(0);
      const shown = workshop.state.frame!.sha256;

      const edited = (size: number) => definition({ name: 'a', kind: 'line', style: { ...STYLE, typography: { ...STYLE.typography, fontSizePx: size } }, properties: ['fontSizePx'] });
      workshop.show({ kind: 'draft', definition: edited(80) }, 'personal|a');
      expect(workshop.state).toMatchObject({ source: 'draft', frame: { status: 'loading', sha256: shown } });
      await vi.advanceTimersByTimeAsync(100);
      workshop.show({ kind: 'draft', definition: edited(90) }, 'personal|a');
      workshop.show({ kind: 'draft', definition: edited(90) }, 'personal|a');
      await vi.advanceTimersByTimeAsync(299);
      expect(log.options).toHaveLength(1);
      // 这个草稿只能用于整行：目标 project 不在了，取第一个。
      setOptions(options(['line']));
      hold(true);
      await vi.advanceTimersByTimeAsync(1);
      expect(log.options[1]).toEqual({ kind: 'draft', draftRevision: 'draft-1', definition: { name: 'a', kind: 'line', style: edited(90).style, properties: ['fontSizePx'] } });
      expect(log.creates.at(-1)).toMatchObject({ targetKey: 'line', source: { draftRevision: 'draft-1' } });
      expect(workshop.state.targetKey).toBe('line');

      // 帧还没回来时又改了：旧会话关闭，晚到的帧不显示。
      workshop.show({ kind: 'draft', definition: edited(100) }, 'personal|a');
      setOptions(options(['line', 'project']));
      await vi.advanceTimersByTimeAsync(300);
      release();
      await vi.advanceTimersByTimeAsync(0);
      expect(workshop.state.frame?.status).toBe('loading');
      release();
      await vi.advanceTimersByTimeAsync(0);
      const draftFrame = workshop.state.frame!;
      expect(draftFrame).toMatchObject({ status: 'ready', sha256: `v${log.creates.length}@3000000` });
      expect(log.options.at(-1)).toMatchObject({ draftRevision: 'draft-2' });
      hold(false);

      // 改回上一份草稿：重新取选项，但会话不变、不重新取帧。
      const frames = log.frames.length;
      workshop.show({ kind: 'draft', definition: edited(90) }, 'personal|a');
      workshop.show({ kind: 'draft', definition: edited(100) }, 'personal|a');
      await vi.advanceTimersByTimeAsync(300);
      expect(log.frames).toHaveLength(frames);
      expect(workshop.state.frame).toBe(draftFrame);

      // 换到另一个模板：不留上一张。
      workshop.show(saved('b', 'tb1'), 'builtin|b');
      expect(workshop.state.frame).toEqual({ status: 'loading' });
      workshop.dispose();
    } finally {
      vi.useRealTimers();
    }
  });

  it('样本上没有目标：读到选项时换到有目标的样本；都没有时写明原因，不建会话', async () => {
    const { client, log, setOptions } = fakeClient();
    const workshop = new TemplateWorkshop(client, undefined, 0);
    setOptions(options([], ['mark']));
    workshop.show(saved('a', 'ta1'), 'builtin|a');
    await flush();
    await flush();
    expect(workshop.state).toMatchObject({ sampleId: 'workshop-multi-line-v1', targetKey: 'mark' });
    expect(log.creates).toHaveLength(1);

    setOptions(options([], []));
    workshop.show(saved('c', 'tc1'), 'builtin|c');
    await flush();
    expect(workshop.state.targetKey).toBeUndefined();
    expect(workshop.state.frame).toEqual({ status: 'unsupported', message: '这个模板在两个样本上没有可用的目标' });
    expect(log.creates).toHaveLength(1);
    expect(log.closes).toEqual(['v1']);
    workshop.dispose();
  });

  it('试播按本地时钟取帧，到结尾停在 12 秒；循环时回到 0；试播中拖时刻从新位置接着播', async () => {
    vi.useFakeTimers();
    try {
      let now = 0;
      const { client, log } = fakeClient();
      const workshop = new TemplateWorkshop(client, undefined, 0, () => now);
      workshop.show(saved('a', 'ta1'), 'builtin|a');
      await vi.advanceTimersByTimeAsync(0);
      workshop.seek(11_000_000);
      await vi.advanceTimersByTimeAsync(0);

      workshop.play();
      expect(workshop.state.playing).toBe(true);
      now = 500;
      await vi.advanceTimersByTimeAsync(40);
      expect(workshop.state.timeUs).toBe(11_500_000);
      now = 2000;
      await vi.advanceTimersByTimeAsync(40);
      expect(workshop.state.timeUs).toBe(WORKSHOP_DURATION_US);
      now = 2100;
      await vi.advanceTimersByTimeAsync(40);
      expect(workshop.state.playing).toBe(false);
      expect(log.frames.map((f) => f.timeUs).slice(-2)).toEqual(['11500000', '12000000']);

      // 在结尾按试播：从 0 开始；循环时过了结尾回到 0（seek）。
      workshop.setLoop(true);
      workshop.play();
      await vi.advanceTimersByTimeAsync(0);
      expect(log.frames.at(-1)).toMatchObject({ timeUs: '0', intent: 'seek' });
      workshop.seek(11_900_000);
      await vi.advanceTimersByTimeAsync(0);
      now = 2300;
      await vi.advanceTimersByTimeAsync(40);
      expect(workshop.state.timeUs).toBe(0);
      expect(workshop.state.playing).toBe(true);
      expect(log.frames.at(-1)).toMatchObject({ timeUs: '0', intent: 'seek' });
      workshop.pause();
      const count = log.frames.length;
      now = 5000;
      await vi.advanceTimersByTimeAsync(200);
      expect(log.frames).toHaveLength(count);
      workshop.dispose();
    } finally {
      vi.useRealTimers();
    }
  });
});
