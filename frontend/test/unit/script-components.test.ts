// 效果组件（W8 设计 §2.3、§2.4）：导入时每个导出的默认配置与名称、参数摘要、试样用的临时完整模板；草稿与组件的比较、保存的写法。

import { describe, expect, it } from 'vitest';
import { draftSaveState, type DraftsState, type FullDraft } from '../../src/editor/library/author-draft-store';
import { addComponentRecipe, compiledOk, componentEffectProblem, componentFromDraft, componentMaterialProblem, componentSample, importedConfiguration, importedName, parameterSummary, sameAsComponent, withComponentMaterial } from '../../src/editor/model/script-components';
import { TemplateLibraryStore } from '../../src/editor/library/template-library-store';
import type { MakoClient } from '../../src/client';
import type { EffectPackageExport, EffectPackageInspectResponse, FullTemplateDefinition, ScriptConfiguration, ScriptConfigurationDetailResponse } from '../../src/protocol';

const tokenOnly = { outputs: [{ id: 'scale', kind: 'Scale', target: 'tokenFamily' }] };
const lineOutput = { outputs: [{ id: 'offset', kind: 'Offset', target: 'line' }] };

function pkg(exports: EffectPackageExport[]): EffectPackageInspectResponse {
  return {
    reference: { kind: 'asset', packageId: 'demo.fx', packageRevision: 'sha256:fx1', packageAssetId: 'asset-1' },
    packageId: 'demo.fx',
    packageRevision: 'sha256:fx1',
    name: '演示包',
    version: '1.0.0',
    manifest: {},
    manifestJson: '{}',
    structureValid: true,
    parameters: [
      { id: 'peakScale', type: 'number', label: '放大比例', unit: '倍', defaultValueJson: '1.5', options: [] },
      { id: 'returnUs', type: 'duration', label: '回落时长', defaultValueJson: '120000', options: [] },
      { id: 'ease', type: 'curve', label: '缓动', defaultValueJson: '{"easing":"linear","kind":"easing"}', options: [] },
    ],
    exports,
    bindingKinds: ['lines', 'tokens'],
    inputs: [],
    files: [],
    diagnostics: [],
    protocolVersion: '1',
  };
}

const WORD: EffectPackageExport = { id: 'word', kind: 'controller', name: '逐字强调', parameterIds: ['peakScale', 'returnUs', 'ease'], bindingKinds: ['lines', 'tokens'], declaration: tokenOnly };
const SHAKE: EffectPackageExport = { id: 'shake', kind: 'controller', name: '整行抖动', parameterIds: [], bindingKinds: ['lines'], declaration: lineOutput };
const INK: EffectPackageExport = { id: 'ink', kind: 'material', name: '墨水', parameterIds: [], bindingKinds: [], declaration: { regions: ['fill', 'stroke'] } };
const OUTLINE: EffectPackageExport = { id: 'outline', kind: 'material', name: '描边光', parameterIds: [], bindingKinds: [], declaration: { regions: ['stroke'] } };

describe('导入效果包', () => {
  it('一个导出用导出名，多个导出用“包名 · 导出名”', () => {
    expect(importedName(pkg([WORD]), WORD)).toBe('逐字强调');
    expect(importedName(pkg([WORD, INK]), INK)).toBe('演示包 · 墨水');
  });

  it('默认配置：参数全默认、绑定的来源、随包、不固定种子，来源是上传的包引用', () => {
    const p = pkg([WORD, SHAKE, INK]);
    expect(importedConfiguration(p, WORD)).toEqual({
      name: '演示包 · 逐字强调',
      exportId: 'word',
      parametersJson: '{}',
      relativeTarget: 'tokenFamily',
      readScope: 'boundSources',
      branchInput: null,
      fixedSeed: null,
      overlayLayer: 'package',
      source: { package: p.reference },
    });
    // 作用于：只写文本单元族的取“逐字”，否则“整行”；材质取“整个分配”。
    expect(importedConfiguration(p, SHAKE).relativeTarget).toBe('line');
    expect(importedConfiguration(p, INK).relativeTarget).toBe('application');
  });
});

describe('选中组件', () => {
  const BASE: FullTemplateDefinition = {
    name: '经典整行',
    kind: 'line',
    style: {
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
    },
    properties: ['unsungColor', 'sungColor'],
    effectRecipes: [],
    packages: [],
  };

  const detail = (exp: EffectPackageExport, more: Partial<ScriptConfigurationDetailResponse['configuration']> = {}): ScriptConfigurationDetailResponse => {
    const p = pkg([WORD, SHAKE, INK, OUTLINE]);
    return {
      revision: 'a'.repeat(64),
      configuration: {
        configurationId: 'cfg-1',
        name: '我的逐字',
        packageId: 'demo.fx',
        packageRevision: 'sha256:fx1',
        sourceRevision: 'sha256:fx1',
        exportId: exp.id,
        parametersJson: '{"peakScale":3,"returnUs":250000,"ease":{"easing":"easeOut","kind":"easing"}}',
        relativeTarget: 'tokenFamily',
        readScope: 'boundSources',
        overlayLayer: 'package',
        kind: exp.kind === 'material' ? 'Material' : 'Effect',
        bindingKinds: exp.bindingKinds,
        ...more,
      },
      package: { ...p, reference: { kind: 'configuration', packageId: 'demo.fx', packageRevision: 'sha256:fx1', configurationId: 'cfg-1', configurationRevision: 'a'.repeat(64) } },
      protocolVersion: '1',
    };
  };

  it('参数摘要按导出声明的顺序，时长写毫秒、曲线写类型', () => {
    const d = detail(WORD);
    expect(parameterSummary(d.package, d.configuration)).toEqual(['放大比例：3 倍', '回落时长：250 毫秒', '缓动：缓动 · 减速']);
  });

  it('模板从组件复制完整配方与材质参数，同包沿用已有 asset 引用；目标不合时写明原因', () => {
    const d = detail(WORD, { readScope: 'branchInput', branchInput: '翻译', fixedSeed: '18446744073709551615', overlayLayer: 'aboveText' });
    const asset = pkg([WORD]).reference;
    const start = { ...BASE, packages: [asset] };
    const added = addComponentRecipe(start, d);
    expect(added.definition.packages).toEqual([asset]);
    expect(added.definition.effectRecipes[0]).toEqual({ recipeId: added.recipeId, name: d.configuration.name, target: 'tokenFamily', packageId: 'demo.fx', revision: 'sha256:fx1', exportId: 'word', parametersJson: d.configuration.parametersJson, overlayLayer: 'aboveText', enabled: true, readScope: 'branchInput', branchInput: '翻译', fixedSeed: '18446744073709551615', configurationId: 'cfg-1' });
    expect(componentEffectProblem('token', detail(SHAKE, { relativeTarget: 'line' }))).toContain('不能作用于整行');
    expect(componentEffectProblem('line', detail(WORD))).toBeUndefined();
    expect(componentMaterialProblem('unsungFill', detail(OUTLINE))).toContain('不能用在未唱填充');
    const material = withComponentMaterial(added.definition, 'sungFill', detail(INK, { parametersJson: '{"opacity":0.4}' }));
    expect(material.packages).toEqual([asset]);
    expect(material.style.sungFill.material).toEqual({ kind: 'package', packageId: 'demo.fx', revision: 'sha256:fx1', exportId: 'ink', parametersJson: '{"opacity":0.4}' });
    expect(withComponentMaterial(BASE, 'stroke', detail(INK))).toBe(BASE);
  });

  it('组件库修订变动时重定位草稿和撤销历史，保留配方快照；删除或换包修订时明确阻止保存', async () => {
    const d = detail(WORD);
    const added = addComponentRecipe(BASE, d).definition;
    let configurations = [d.configuration];
    let fresh = { ...d, revision: 'b'.repeat(64), configuration: { ...d.configuration, parametersJson: '{"peakScale":9}' }, package: { ...d.package, reference: { ...d.package.reference, kind: 'configuration' as const, configurationId: 'cfg-1', configurationRevision: 'b'.repeat(64) } } };
    const client = { scriptConfigurations: { list: async () => ({ revision: fresh.revision, configurations }), detail: async () => fresh } } as unknown as MakoClient;
    const store = new TemplateLibraryStore(client);
    store.state$.set({ ...store.state, status: 'ready', personal: { revision: 'missing', templates: [] }, draft: { templateId: null, base: BASE, definition: { ...added, name: '已改' }, undo: [added], redo: [], imported: [] } });
    expect(await store.refreshComponentReferences()).toBe(true);
    expect(store.state.draft?.definition.packages[0]).toMatchObject({ configurationRevision: 'b'.repeat(64) });
    expect(store.state.draft?.undo[0]?.packages[0]).toMatchObject({ configurationRevision: 'b'.repeat(64) });
    expect(store.state.draft?.definition.effectRecipes).toEqual(added.effectRecipes);
    fresh = { ...fresh, package: { ...fresh.package, reference: { ...fresh.package.reference, packageRevision: 'sha256:fx2' } } };
    expect(await store.refreshComponentReferences()).toBe(false);
    expect(store.state.notice?.message).toContain('包修订已经改变');
    configurations = [];
    expect(await store.save()).toBe(false);
    expect(store.state.notice?.message).toContain('已经删除，请移除这个效果后重新添加');
    expect(store.state.draft?.definition.name).toBe('已改');
  });

  it('效果组件：加一个带 configurationId 的配方，包引用用配置引用', () => {
    const d = detail(WORD, { fixedSeed: '18446744073709551615' });
    const sample = componentSample(BASE, d);
    if (!('definition' in sample)) throw new Error(sample.problem);
    expect(sample.materialSlot).toBeUndefined();
    expect(sample.definition.effectRecipes).toEqual([
      expect.objectContaining({ target: 'tokenFamily', exportId: 'word', parametersJson: d.configuration.parametersJson, fixedSeed: '18446744073709551615', branchInput: null, configurationId: 'cfg-1', enabled: true }),
    ]);
    expect(sample.definition.packages).toEqual([d.package.reference]);
    expect(sample.definition.properties).toEqual(BASE.properties);
  });

  it('材质组件放进未唱填充；只能描边的材质放进描边，底没有描边时写明原因', () => {
    const ink = componentSample(BASE, detail(INK, { relativeTarget: 'application', parametersJson: '{}' }));
    if (!('definition' in ink)) throw new Error(ink.problem);
    expect(ink.materialSlot).toBe('unsungFill');
    expect(ink.definition.style.unsungFill.material).toEqual({ kind: 'package', packageId: 'demo.fx', revision: 'sha256:fx1', exportId: 'ink', parametersJson: '{}' });
    expect(ink.definition.effectRecipes).toEqual([]);
    expect(componentSample(BASE, detail(OUTLINE, { relativeTarget: 'application' }))).toEqual({ problem: '这个材质只能用于描边，试样模板没有描边。' });
  });

  it('效果的作用于在整行模板上不行时不试样', () => {
    const sample = componentSample(BASE, detail(SHAKE, { relativeTarget: 'tokenFamily' }));
    expect('problem' in sample && sample.problem).toMatch(/^没法在整行模板上试样/);
  });
});

describe('作者草稿与组件', () => {
  const component: ScriptConfiguration = {
    configurationId: 'cfg-1',
    name: '逐字强调',
    packageId: 'demo.fx',
    packageRevision: 'sha256:fx1',
    sourceRevision: 'sha256:fx1',
    exportId: 'word',
    parametersJson: '{"peakScale":3,"returnUs":120000}',
    relativeTarget: 'tokenFamily',
    readScope: 'boundSources',
    overlayLayer: 'package',
    kind: 'Effect',
    bindingKinds: ['lines', 'tokens'],
  };
  const { configurationId: _id, ...fields } = component;
  const draft: FullDraft = {
    draftId: 'draft-1',
    draftRevision: 'sha256:d1',
    name: '逐字强调',
    packageId: 'demo.fx',
    baseSourceRevision: 'sha256:fx1',
    currentSourceRevision: 'sha256:fx1',
    exportId: 'word',
    canCompile: true,
    canAdopt: true,
    configuration: { ...fields, parametersJson: '{"returnUs":1.2e5,"peakScale":3.0}' },
    sampleOptions: { samples: [], materialSlots: [], defaultSampleId: 'workshop-single-line-v1' },
    protocolVersion: '1',
  };

  it('参数按 JSON 值比较（键序、数字写法不算改动）；其他字段逐个比较，省略和 null 相同', () => {
    expect(sameAsComponent(draft.configuration, component)).toBe(true);
    expect(sameAsComponent({ ...draft.configuration, fixedSeed: '7' }, component)).toBe(false);
    expect(sameAsComponent({ ...draft.configuration, sourceRevision: 'sha256:fx2' }, component)).toBe(false);
    expect(sameAsComponent({ ...draft.configuration, parametersJson: '{"peakScale":3}' }, component)).toBe(false);
    expect(sameAsComponent(draft.configuration, { ...component, branchInput: undefined as unknown as string })).toBe(true);
  });

  it('是否已保存：没有对应关系或组件已删除时只能另存为，组件还在读时是“正在核对”', () => {
    const state: DraftsState = { status: 'ready', drafts: [draft], origins: { 'draft-1': 'cfg-1' }, originConfigs: { 'cfg-1': component }, packages: {}, tabs: {}, busy: false };
    expect(draftSaveState(state, draft)).toBe('saved');
    expect(draftSaveState(state, { ...draft, configuration: { ...draft.configuration, name: '改过' } })).toBe('unsaved');
    expect(draftSaveState({ ...state, originConfigs: {} }, draft)).toBe('checking');
    expect(draftSaveState({ ...state, originConfigs: { 'cfg-1': null } }, draft)).toBe('orphan');
    expect(draftSaveState({ ...state, origins: {} }, draft)).toBe('orphan');
  });

  it('存进组件库的来源是草稿当前修订和在用修订，可空字段写 null', () => {
    expect(componentFromDraft(draft, '新名字')).toEqual({
      name: '新名字',
      exportId: 'word',
      parametersJson: draft.configuration.parametersJson,
      relativeTarget: 'tokenFamily',
      readScope: 'boundSources',
      branchInput: null,
      fixedSeed: null,
      overlayLayer: 'package',
      source: { draftId: 'draft-1', expectedDraftRevision: 'sha256:d1', sourceRevision: 'sha256:fx1' },
    });
  });

  it('在用修订编译通过：最近一次编译就是它、成功且没过期', () => {
    const compiled = (c: Partial<NonNullable<FullDraft['lastCompilation']>>) => ({
      ...draft,
      lastCompilation: { sourceRevision: 'sha256:fx1', succeeded: true, stale: false, artifactHash: 'h', diagnosticCount: 0, completedAtUtc: '2026-10-30T00:00:00Z', ...c },
    });
    expect(compiledOk(draft)).toBe(false);
    expect(compiledOk(compiled({}))).toBe(true);
    expect(compiledOk(compiled({ succeeded: false }))).toBe(false);
    expect(compiledOk(compiled({ sourceRevision: 'sha256:other' }))).toBe(false);
    expect(compiledOk(compiled({ stale: true }))).toBe(false);
    expect(compiledOk({ ...compiled({ stale: true }), packageVersion: 3, sdkVersion: '2.0.0', currentSourceRevision: 'sha256:new' })).toBe(true);
    expect(compiledOk({ ...compiled({ stale: true, sourceRevision: 'sha256:other' }), packageVersion: 3, sdkVersion: '2.0.0' })).toBe(false);
  });
});
