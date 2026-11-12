// W7 第 4、5 步：效果包参数的读写与校验、包闭包、材质类型切换、效果配方、本地校验（W7 设计 §2.2～§2.4）。

import { describe, expect, it } from 'vitest';
import {
  colorFromJson,
  colorToJson,
  microsToMillis,
  millisToMicros,
  parameterProblem,
  parameterRaw,
  parametersIssues,
  rangeText,
  seedProblem,
  withoutParameter,
  withParameter,
} from '../../src/editor/model/effect-params';
import {
  changeMaterialKind,
  definitionIssues,
  materialCandidates,
  materialExports,
  packageKey,
  packageMaterial,
  packageUsers,
  replacePackageRevision,
  syncPackages,
  usedPackageKeys,
} from '../../src/editor/model/full-template';
import {
  addRecipe,
  defaultTarget,
  duplicateRecipe,
  exportProblem,
  moveRecipe,
  nextRecipeId,
  recipeProblems,
  removeRecipe,
  targetProblem,
  updateRecipe,
  withReadScope,
} from '../../src/editor/model/effect-recipes';
import type { EffectPackageInspectResponse, EffectPackageParameter, EffectPackageReference, FullTemplateDefinition, TemplateEffectRecipe } from '../../src/protocol';

const INT64_MAX = '9223372036854775807';

const param = (p: Partial<EffectPackageParameter> & Pick<EffectPackageParameter, 'id' | 'type' | 'defaultValueJson'>): EffectPackageParameter => ({ label: p.id, options: [], ...p });

const AMPLITUDE = param({ id: 'amplitude', type: 'number', label: '幅度', defaultValueJson: '0.1', minimumJson: '0', maximumJson: '0.5' });
const FREQUENCY = param({ id: 'frequencyHz', type: 'number', label: '频率', unit: 'Hz', defaultValueJson: '2', minimumJson: '0', maximumJson: '20' });
const COLOR_A = param({ id: 'colorA', type: 'color', label: '颜色 A', defaultValueJson: '{"r":1,"g":1,"b":1,"a":1}' });
const PEAK = param({ id: 'peakScale', type: 'number', label: '放大比例', unit: '倍', defaultValueJson: '1.5', minimumJson: '1', maximumJson: '4' });
const RETURN = param({ id: 'returnUs', type: 'duration', label: '回落时长', defaultValueJson: '120000', minimumJson: '0', maximumJson: '2000000' });
const MODE = param({ id: 'mode', type: 'enum', label: '模式', defaultValueJson: '"wave"', options: [{ value: 'wave', label: '波' }, { value: 'pulse', label: '脉冲' }] });
const COUNT = param({ id: 'count', type: 'integer', label: '次数', defaultValueJson: '1', minimumJson: '0', maximumJson: INT64_MAX });

function inkPackage(revision = 'sha256:ink1', extra: Partial<EffectPackageInspectResponse> = {}): EffectPackageInspectResponse {
  return {
    reference: { kind: 'asset', packageId: 'demo.ink', packageRevision: revision, packageAssetId: `asset-${revision}` },
    packageId: 'demo.ink',
    packageRevision: revision,
    name: '自定义墨水',
    version: '1.0.0',
    manifest: {},
    manifestJson: '{}',
    structureValid: true,
    parameters: [AMPLITUDE, FREQUENCY, COLOR_A, PEAK, RETURN, MODE, COUNT],
    exports: [
      { id: 'custom-ink', kind: 'material', name: '墨水', parameterIds: ['amplitude', 'frequencyHz', 'colorA'], bindingKinds: [], declaration: { regions: ['fill', 'stroke'], fallback: { kind: 'solid', color: '#3380CCBF' } } },
      { id: 'fill-only', kind: 'material', name: '只填充', parameterIds: [], bindingKinds: [], declaration: { regions: ['fill'] } },
      { id: 'word-emphasis', kind: 'controller', name: '逐字强调', parameterIds: ['peakScale', 'returnUs', 'mode', 'count'], bindingKinds: ['lines', 'tokens'], declaration: {} },
    ],
    bindingKinds: ['lines', 'tokens'],
    inputs: [],
    files: [],
    diagnostics: [],
    protocolVersion: '1',
    ...extra,
  };
}

const STYLE: FullTemplateDefinition['style'] = {
  typography: { fontFamilyName: null, fallbackFamilyNames: [], fontSizePx: 72, fontWeight: 400, isBold: false, isItalic: false, letterSpacingPx: 0 },
  unsung: { fill: { kind: 'solid', color: { red: 74, green: 80, blue: 96, alpha: 255 } }, strokes: [], shadow: null, glow: null },
  sung: { fill: { kind: 'solid', color: { red: 76, green: 141, blue: 255, alpha: 255 } }, strokes: { kind: 'follow' }, shadow: { kind: 'follow' }, glow: { kind: 'follow' } },
  wipe: { featherRatio: 0, highlight: null },
  verticalCenterRatio: 0.5,
  horizontalAlignment: 'center',
  opacity: 1,
  scale: 1,
};

const recipe = (r: Partial<TemplateEffectRecipe> = {}): TemplateEffectRecipe => ({
  recipeId: 'r1',
  name: '逐字强调',
  enabled: true,
  packageId: 'demo.ink',
  revision: 'sha256:ink1',
  exportId: 'word-emphasis',
  target: 'tokenFamily',
  overlayLayer: 'package',
  readScope: 'boundSources',
  branchInput: null,
  fixedSeed: null,
  configurationId: null,
  parametersJson: '{}',
  ...r,
});

const asset = (revision: string): EffectPackageReference => ({ kind: 'asset', packageId: 'demo.ink', packageRevision: revision, packageAssetId: `asset-${revision}` });

function withInk(parametersJson = '{}', more: Partial<FullTemplateDefinition> = {}): FullTemplateDefinition {
  return {
    name: '含效果',
    kind: 'line',
    properties: ['unsung.fill'],
    style: { ...STYLE, unsung: { ...STYLE.unsung, fill: { kind: 'package', packageId: 'demo.ink', revision: 'sha256:ink1', exportId: 'custom-ink', parametersJson } } },
    effectRecipes: [],
    packages: [asset('sha256:ink1')],
    ...more,
  };
}

describe('效果包参数', () => {
  it('没写的参数取默认值；改成默认值时去掉键，保持最小参数对象，其余键原文不动', () => {
    expect(parameterRaw('{}', AMPLITUDE)).toBe('0.1');
    const a = withParameter('{}', AMPLITUDE, '0.3');
    expect(a).toBe('{"amplitude":0.3}');
    const b = withParameter(a, FREQUENCY, '6');
    expect(b).toBe('{"amplitude":0.3,"frequencyHz":6}');
    expect(withParameter(b, AMPLITUDE, '0.10')).toBe('{"frequencyHz":6}');
    expect(withParameter(b, FREQUENCY, undefined)).toBe('{"amplitude":0.3}');
    expect(withoutParameter('{"old":1,"amplitude":0.3}', 'old')).toBe('{"amplitude":0.3}');
    expect(parameterRaw(`{"count":${INT64_MAX}}`, COUNT)).toBe(INT64_MAX);
  });

  it('按类型与范围检查，Int64 上限按原文比较', () => {
    expect(parameterProblem(AMPLITUDE, '0.5')).toBeUndefined();
    expect(parameterProblem(AMPLITUDE, '0.6')).toBe('要在 0～0.5 之间。');
    expect(parameterProblem(FREQUENCY, '-1')).toBe('要在 0～20 Hz 之间。');
    expect(parameterProblem(FREQUENCY, '"6"')).toBe('请输入数字。');
    expect(parameterProblem(COUNT, INT64_MAX)).toBeUndefined();
    expect(parameterProblem(COUNT, '9223372036854775808')).toMatch(/之间/);
    expect(parameterProblem(COUNT, '1.5')).toBe('请输入整数。');
    expect(parameterProblem(RETURN, '1.5')).toBe('时长要是整数微秒。');
    expect(parameterProblem(RETURN, '3000000')).toBe('要在 0～2000 毫秒 之间。');
    expect(parameterProblem(MODE, '"pulse"')).toBeUndefined();
    expect(parameterProblem(MODE, '"zigzag"')).toBe('不在可选项里。');
    expect(parameterProblem(COLOR_A, '{"r":1,"g":0,"b":0,"a":0.5}')).toBeUndefined();
    expect(parameterProblem(COLOR_A, '{"r":2,"g":0,"b":0,"a":1}')).toBe('颜色分量要在 0～1 之间。');
    expect(parameterProblem(COLOR_A, '{"r":1,"g":0,"b":0}')).toMatch(/r","g","b","a/);
    expect(parameterProblem(AMPLITUDE, '{')).toBe('不是合法的 JSON。');
  });

  it('整份参数：多余的键和每个取值的问题都列出，带参数名', () => {
    const pkg = inkPackage();
    expect(parametersIssues('{"amplitude":0.2}', pkg, 'custom-ink')).toEqual([]);
    expect(parametersIssues('{"amplitude":0.9,"peakScale":2}', pkg, 'custom-ink')).toEqual([
      { id: 'amplitude', message: '幅度：要在 0～0.5 之间。' },
      // peakScale 是包里的参数，但不是这个导出的。
      { id: 'peakScale', message: '包里没有参数“peakScale”。' },
    ]);
    expect(parametersIssues('[1]', pkg, 'custom-ink')).toEqual([{ id: '', message: '参数不是合法的 JSON 对象。' }]);
  });

  it('范围说明、颜色与时长换算、种子', () => {
    expect(rangeText(FREQUENCY)).toBe('0～20 Hz');
    expect(rangeText(RETURN)).toBe('0～2000 毫秒');
    expect(rangeText(MODE)).toBeUndefined();
    expect(colorFromJson('{"r":1,"g":0.5,"b":0,"a":0.75}')).toEqual({ red: 255, green: 128, blue: 0, alpha: 191 });
    expect(colorToJson({ red: 255, green: 128, blue: 0, alpha: 191 })).toBe('{"r":1,"g":0.502,"b":0,"a":0.749}');
    expect(colorFromJson('"red"')).toBeUndefined();
    expect(microsToMillis('120000')).toBe('120');
    expect(microsToMillis('1500')).toBe('1.5');
    expect(microsToMillis('7')).toBe('0.007');
    expect(microsToMillis(INT64_MAX)).toBe('9223372036854775.807');
    expect(millisToMicros('1.5')).toBe('1500');
    expect(millisToMicros('0')).toBe('0');
    expect(millisToMicros('9223372036854775.807')).toBe(INT64_MAX);
    expect(millisToMicros('1.2345')).toBeUndefined();
    expect(millisToMicros('abc')).toBeUndefined();
    expect(seedProblem('18446744073709551615')).toBeUndefined();
    expect(seedProblem('18446744073709551616')).toMatch(/不能大于/);
    expect(seedProblem('-1')).toMatch(/整数/);
    expect(seedProblem('1e3')).toMatch(/整数/);
  });
});

describe('包闭包与材质', () => {
  it('packages 恰好是材质和配方用到的包：去掉没用到的，新用到的从导入过的引用里取', () => {
    const solid = { ...withInk(), style: STYLE };
    expect(syncPackages(solid, []).packages).toEqual([]);
    const ink = withInk();
    expect(syncPackages(ink, [])).toBe(ink);
    const noRefs = { ...ink, packages: [] };
    expect(syncPackages(noRefs, [asset('sha256:ink1'), asset('sha256:other')]).packages).toEqual([asset('sha256:ink1')]);
    const both = { ...ink, effectRecipes: [recipe({ revision: 'sha256:ink2' })] };
    expect(usedPackageKeys(both)).toEqual([packageKey('demo.ink', 'sha256:ink1'), packageKey('demo.ink', 'sha256:ink2')]);
    expect(syncPackages(both, [asset('sha256:ink2')]).packages.map((p) => p.packageRevision)).toEqual(['sha256:ink1', 'sha256:ink2']);
  });

  it('谁在用一个包；换成新修订时材质和配方都改指新修订，参数原样保留', () => {
    const d = withInk('{"amplitude":0.3}', { effectRecipes: [recipe()] });
    const key = packageKey('demo.ink', 'sha256:ink1');
    expect(packageUsers(d, key)).toEqual(['未唱填充', '效果“逐字强调”']);
    expect(packageUsers(d, packageKey('demo.ink', 'sha256:x'))).toEqual([]);
    const next = replacePackageRevision(d, 'demo.ink', 'sha256:ink1', 'sha256:ink2');
    expect(next.style.unsung.fill).toMatchObject({ revision: 'sha256:ink2', parametersJson: '{"amplitude":0.3}' });
    expect(next.effectRecipes[0]!.revision).toBe('sha256:ink2');
    expect(next.style.sung.fill).toEqual(d.style.sung.fill);
  });

  it('部位能用的材质导出看 regions，同一个包只列一次；材质类型切换：包材质回到纯色时用退化色，没有可用包材质时不能切换', () => {
    const pkg = inkPackage();
    expect(materialExports(pkg, 'unsungFill').map((e) => e.id)).toEqual(['custom-ink', 'fill-only']);
    expect(materialExports(pkg, 'stroke').map((e) => e.id)).toEqual(['custom-ink']);
    const catalog = new Map([[packageKey(pkg.packageId, pkg.packageRevision), pkg]]);
    expect(materialCandidates([asset('sha256:ink1'), asset('sha256:unknown')], catalog, 'stroke').map((c) => c.exports.map((e) => e.id))).toEqual([['custom-ink']]);
    // 刚导入就用作材质：同一个包既在定义里又在导入过的包里，只列一次。
    expect(materialCandidates([asset('sha256:ink1'), asset('sha256:ink1')], catalog, 'unsungFill')).toHaveLength(1);

    const solid = STYLE.unsung.fill;
    const candidate = packageMaterial(pkg, 'custom-ink');
    expect(changeMaterialKind(solid, 'package', undefined, candidate)).toEqual(candidate);
    expect(changeMaterialKind(solid, 'package', undefined, undefined)).toBeUndefined();
    expect(changeMaterialKind(candidate, 'solid', '#3380CCBF', undefined)).toEqual({ kind: 'solid', color: { red: 0x33, green: 0x80, blue: 0xcc, alpha: 0xbf } });
    expect(changeMaterialKind(candidate, 'solid', undefined, undefined)).toEqual({ kind: 'solid', color: { red: 0, green: 0, blue: 0, alpha: 255 } });
    expect(changeMaterialKind(candidate, 'gradient', '#3380CC', undefined)).toMatchObject({ kind: 'gradient' });
    expect(changeMaterialKind(solid, 'gradient', undefined, undefined)).toMatchObject({ kind: 'gradient' });
    expect(changeMaterialKind(solid, 'solid', undefined, undefined)).toBe(solid);
  });
});

describe('本地校验', () => {
  const pkg = inkPackage();
  const catalog = new Map([[packageKey(pkg.packageId, pkg.packageRevision), pkg]]);

  it('合法的定义没有问题；参数越界、多余的键带参数 ID，写在对应部位', () => {
    expect(definitionIssues(withInk('{"amplitude":0.3}', { effectRecipes: [recipe({ fixedSeed: '18446744073709551615' })] }), catalog)).toEqual([]);
    expect(definitionIssues(withInk('{"amplitude":0.9,"gone":1}'), catalog)).toEqual([
      { where: { kind: 'slot', slot: 'unsungFill' }, parameterId: 'amplitude', message: '幅度：要在 0～0.5 之间。' },
      { where: { kind: 'slot', slot: 'unsungFill' }, parameterId: 'gone', message: '包里没有参数“gone”。' },
    ]);
  });

  it('导出不存在、种类不对、部位不支持、包不在 packages 里、没用到的包、种子越界', () => {
    const issues = (d: FullTemplateDefinition) => definitionIssues(d, catalog).map((i) => i.message);
    expect(issues({ ...withInk(), style: { ...withInk().style, unsung: { ...withInk().style.unsung, fill: { ...packageMaterial(pkg, 'nope') } } } })).toEqual(['包“自定义墨水”里没有导出 nope。']);
    expect(issues(withInk('{}', { effectRecipes: [recipe({ exportId: 'custom-ink' })] }))).toEqual(['导出“墨水”是材质，这里要用控制器或图。']);
    const stroke = { join: 'round' as const, widthPx: 2, material: packageMaterial(pkg, 'fill-only') };
    expect(issues({ ...withInk(), style: { ...withInk().style, unsung: { ...withInk().style.unsung, strokes: [stroke] } } })).toEqual(['材质“只填充”不能用在未唱描边。']);
    expect(issues({ ...withInk(), packages: [] })).toEqual(['模板里没有包 demo.ink（修订 ink1）。']);
    expect(issues({ ...withInk(), packages: [asset('sha256:ink1'), asset('sha256:spare')] })).toEqual(['包 demo.ink 没有被用到。']);
    expect(issues(withInk('{}', { effectRecipes: [recipe({ fixedSeed: '18446744073709551616' })] }))).toEqual(['种子不能大于 18446744073709551615。']);
  });

  it('还没读到包信息时只查是否在 packages 里', () => {
    expect(definitionIssues(withInk('{"amplitude":99}'), new Map())).toEqual([]);
  });
});

describe('效果配方', () => {
  const output = (kind: string, target: string) => ({ id: `${kind}-${target}`, kind, target, requires: [], dependsOn: [] });
  const effect = (id: string, outputs: ReturnType<typeof output>[], bindingKinds = ['project', 'branch', 'lines', 'tokens', 'clips', 'node']) => ({
    id,
    kind: 'controller' as const,
    name: id,
    parameterIds: [],
    bindingKinds,
    declaration: { outputs },
  });
  const emphasis = effect('emphasis', [output('VisualTransform', 'tokenFamily')]);
  const glitch = effect('glitch', [output('ImageGraph', 'line')], ['project', 'branch', 'lines', 'clips', 'node']);
  const burst = effect('burst', [output('Overlay', 'line')]);
  const grouped = effect('grouped', [output('VisualTransform', 'group')]);
  const pkg = inkPackage('sha256:fx', { exports: [emphasis, glitch, burst, grouped] });

  it('默认目标与能否加入：按模板用途、导出的绑定种类和输出', () => {
    expect(defaultTarget('line', emphasis)).toBe('tokenFamily');
    expect(defaultTarget('line', glitch)).toBe('line');
    expect(defaultTarget('line', burst)).toBe('line');
    expect(defaultTarget('token', burst)).toBe('tokenFamily');
    expect(defaultTarget('token', glitch)).toBeUndefined();
    expect(exportProblem('token', glitch)).toBe('“glitch”不能作用于文本单元。');
    expect(defaultTarget('composite', grouped)).toBe('application');
    expect(exportProblem('line', grouped)).toBe('“grouped”会写入组，只能用在组合模板里。');
    expect(targetProblem('token', 'line', emphasis)).toBe('文本单元模板的效果不能作用于整行。');
    expect(targetProblem('line', 'tokenFamily', glitch)).toBe('“glitch”不能作用于文本单元。');
    const lineAndTokens = effect('mixed', [output('ImageGraph', 'line')]);
    expect(targetProblem('line', 'tokenFamily', lineAndTokens)).toBe('“mixed”有作用于整行的输出，不能逐字使用。');
    expect(targetProblem('line', 'application', lineAndTokens)).toBeUndefined();
  });

  it('加、复制、移动、删除、改设置；ID 不重复，复制排在原项后面', () => {
    const base = withInk();
    const a = addRecipe(base, pkg, emphasis);
    expect(a.recipeId).toBe('effect-1');
    expect(a.definition.effectRecipes[0]).toMatchObject({ name: 'emphasis', target: 'tokenFamily', packageId: 'demo.ink', revision: 'sha256:fx', parametersJson: '{}', fixedSeed: null });
    const b = addRecipe(a.definition, pkg, burst);
    const c = duplicateRecipe(b.definition, 'effect-1');
    expect(c.definition.effectRecipes.map((r) => r.recipeId)).toEqual(['effect-1', 'effect-3', 'effect-2']);
    expect(c.definition.effectRecipes[1]!.name).toBe('emphasis（副本）');
    const moved = moveRecipe(c.definition, 'effect-2', -1);
    expect(moved.effectRecipes.map((r) => r.recipeId)).toEqual(['effect-1', 'effect-2', 'effect-3']);
    expect(moveRecipe(moved, 'effect-1', -1)).toBe(moved);
    const removed = removeRecipe(moved, 'effect-2');
    expect(nextRecipeId(removed)).toBe('effect-4');
    expect(updateRecipe(removed, 'effect-3', { enabled: false }).effectRecipes[1]!.enabled).toBe(false);
    const r = removed.effectRecipes[0]!;
    expect(withReadScope(r, 'branchInput')).toEqual({ readScope: 'branchInput', branchInput: '' });
    expect(withReadScope({ ...r, readScope: 'branchInput', branchInput: '翻译' }, 'branchInput')).toEqual({ readScope: 'branchInput', branchInput: '翻译' });
    expect(withReadScope({ ...r, readScope: 'branchInput', branchInput: '翻译' }, 'project')).toEqual({ readScope: 'project', branchInput: null });
  });

  it('配方的问题：名称、目标、叠放层、输入名；并进本地校验，ID 重复也报', () => {
    const r = recipe({ exportId: 'emphasis', revision: 'sha256:fx' });
    expect(recipeProblems('line', r, emphasis)).toEqual([]);
    expect(recipeProblems('line', { ...r, name: ' ', overlayLayer: 'aboveText', readScope: 'branchInput', branchInput: '' }, emphasis)).toEqual([
      '名称不能为空。',
      '“emphasis”没有图元输出，叠放层只能“随包”。',
      '读取范围是“指定输入”时要填输入名。',
    ]);
    expect(recipeProblems('line', { ...r, overlayLayer: 'aboveText' }, burst)).toEqual([]);
    expect(recipeProblems('token', { ...r, target: 'line' }, undefined)).toEqual(['文本单元模板的效果不能作用于整行。']);

    const catalog = new Map([[packageKey(pkg.packageId, pkg.packageRevision), pkg]]);
    const d: FullTemplateDefinition = { ...withInk(), style: STYLE, packages: [asset('sha256:fx')], effectRecipes: [{ ...r, exportId: 'glitch', target: 'tokenFamily' }, { ...r }] };
    expect(definitionIssues(d, catalog).map((i) => [i.where, i.message])).toEqual([
      [{ kind: 'recipe', recipeId: 'r1' }, '效果 ID r1 重复。'],
      [{ kind: 'recipe', recipeId: 'r1' }, '“glitch”不能作用于文本单元。'],
      [{ kind: 'recipe', recipeId: 'r1' }, '效果 ID r1 重复。'],
    ]);
  });
});
