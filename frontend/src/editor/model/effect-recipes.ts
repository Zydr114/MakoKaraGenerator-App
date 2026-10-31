// 效果配方（W7 设计 §2.3 效果页签）：增删、排序、复制，各项设置的取值范围与本地校验。不依赖 React。
// 规则照后端 Core 的 StyleTemplateEffects.Validate 与 ScriptConfiguration：
// - 文本单元模板不能用“整行”；
// - 导出要是控制器或图，且 bindingKinds 含相对目标对应的种类（整行 lines、逐字 tokens；
//   整个分配按模板用途：行 lines、文本单元 tokens，组合模板不查）；写入组的导出只剩 project、node；
// - 行和文本单元模板不能用写入组的导出；逐字或文本单元模板里，导出的非图元输出只能写文本单元族（tokenFamily）；
// - 没有图元（Overlay）输出时叠放层只能“随包”；
// - 读取范围是“指定输入”时要有输入名，其他范围不带输入名；名称不能为空；配方 ID 不重复。

import type {
  EffectOverlayLayer,
  EffectPackageExport,
  EffectPackageInspectResponse,
  EffectReadScope,
  EffectRecipeTarget,
  FullTemplateDefinition,
  TemplateEffectRecipe,
  TemplateKind,
} from '../../protocol';

export const RECIPE_TARGETS: readonly EffectRecipeTarget[] = ['line', 'tokenFamily', 'application'];
export const RECIPE_TARGET_LABEL: Record<EffectRecipeTarget, string> = { line: '整行', tokenFamily: '逐字', application: '整个分配' };
export const OVERLAY_LAYERS: readonly EffectOverlayLayer[] = ['package', 'behindText', 'aboveText'];
export const OVERLAY_LABEL: Record<EffectOverlayLayer, string> = { package: '随包', behindText: '文字后面', aboveText: '文字上面' };
export const READ_SCOPES: readonly EffectReadScope[] = ['boundSources', 'project', 'branchInput'];
export const READ_SCOPE_LABEL: Record<EffectReadScope, string> = { boundSources: '绑定的来源', project: '整个工程', branchInput: '指定输入' };

export interface ExportOutput {
  id: string;
  kind: string;
  target: string;
}

/** 包里能做效果的导出（控制器或图）。 */
export function effectExports(pkg: EffectPackageInspectResponse): EffectPackageExport[] {
  return pkg.exports.filter((e) => e.kind === 'controller' || e.kind === 'graph');
}

/** 导出声明里的输出。 */
export function exportOutputs(exp: EffectPackageExport): ExportOutput[] {
  const outputs = exp.declaration['outputs'];
  if (!Array.isArray(outputs)) return [];
  return outputs.flatMap((o) => {
    if (!o || typeof o !== 'object') return [];
    const { id, kind, target } = o as Record<string, unknown>;
    return [{ id: String(id ?? ''), kind: String(kind ?? ''), target: String(target ?? '') }];
  });
}

/** 实际能绑定的种类：写入组的导出只能绑定工程或节点（后端 ScriptConfiguration）。 */
export function effectiveBindingKinds(exp: EffectPackageExport): string[] {
  const writesGroup = exportOutputs(exp).some((o) => o.target === 'group');
  return writesGroup ? exp.bindingKinds.filter((k) => k === 'project' || k === 'node') : exp.bindingKinds;
}

/** 这个用途的模板里，配方用这个目标时的问题（与导出无关的部分和与导出有关的部分）；可以时 undefined。 */
export function targetProblem(kind: TemplateKind, target: EffectRecipeTarget, exp: EffectPackageExport | undefined): string | undefined {
  if (kind === 'token' && target === 'line') return '文本单元模板的效果不能作用于整行。';
  if (!exp) return undefined;
  const outputs = exportOutputs(exp);
  if (kind !== 'composite' && outputs.some((o) => o.target === 'group')) return `“${exp.name}”会写入组，只能用在组合模板里。`;
  const binding = kind === 'composite' ? undefined : target === 'application' ? (kind === 'line' ? 'lines' : 'tokens') : target === 'line' ? 'lines' : 'tokens';
  if (binding && !effectiveBindingKinds(exp).includes(binding)) return `“${exp.name}”不能作用于${binding === 'lines' ? '整行' : '文本单元'}。`;
  if ((target === 'tokenFamily' || kind === 'token') && outputs.some((o) => o.kind !== 'Overlay' && o.target !== 'tokenFamily'))
    return `“${exp.name}”有作用于整行的输出，不能逐字使用。`;
  return undefined;
}

/**
 * 效果组件的作用于（W8 设计 §2.5）：只看与模板用途无关的规则。整行要能绑定行；逐字要能绑定文本单元，且图元以外的输出都写文本单元族。
 * “整个分配”要看用在什么模板里，到“从效果组件选”时再判断（§2.10）。
 */
export function componentTargetProblem(target: EffectRecipeTarget, exp: EffectPackageExport | undefined): string | undefined {
  if (!exp || target === 'application') return undefined;
  const binding = target === 'line' ? 'lines' : 'tokens';
  if (!effectiveBindingKinds(exp).includes(binding)) return `“${exp.name}”不能作用于${binding === 'lines' ? '整行' : '文本单元'}。`;
  if (target === 'tokenFamily' && exportOutputs(exp).some((o) => o.kind !== 'Overlay' && o.target !== 'tokenFamily')) return `“${exp.name}”有作用于整行的输出，不能逐字使用。`;
  return undefined;
}

/** 导出有没有图元（Overlay）输出：没有时叠放层只能“随包”。 */
export function hasOverlay(exp: EffectPackageExport): boolean {
  return exportOutputs(exp).some((o) => o.kind === 'Overlay');
}

/** 新配方默认的目标：文本单元模板逐字，组合模板整个分配；行模板按导出的输出，都写文本单元族时逐字，否则整行。不行时取第一个可以的。 */
export function defaultTarget(kind: TemplateKind, exp: EffectPackageExport): EffectRecipeTarget | undefined {
  const outputs = exportOutputs(exp);
  const preferred: EffectRecipeTarget =
    kind === 'token' ? 'tokenFamily' : kind === 'composite' ? 'application' : outputs.length > 0 && outputs.every((o) => o.target === 'tokenFamily') ? 'tokenFamily' : 'line';
  return [preferred, ...RECIPE_TARGETS].find((t) => !targetProblem(kind, t, exp));
}

/** 这个导出能不能加进这个用途的模板：有一个可用目标就行，否则给出原因。 */
export function exportProblem(kind: TemplateKind, exp: EffectPackageExport): string | undefined {
  return defaultTarget(kind, exp) ? undefined : targetProblem(kind, kind === 'composite' ? 'application' : kind === 'token' ? 'tokenFamily' : 'line', exp);
}

/** 不和已有配方重复的 ID：effect-1、effect-2…… */
export function nextRecipeId(definition: FullTemplateDefinition): string {
  const used = new Set(definition.effectRecipes.map((r) => r.recipeId));
  let n = definition.effectRecipes.length + 1;
  while (used.has(`effect-${n}`)) n++;
  return `effect-${n}`;
}

/** 加一个配方（排在最后）：名称取导出名，参数全部默认，其他设置取默认。 */
export function addRecipe(definition: FullTemplateDefinition, pkg: EffectPackageInspectResponse, exp: EffectPackageExport): { definition: FullTemplateDefinition; recipeId: string } {
  const recipeId = nextRecipeId(definition);
  const recipe: TemplateEffectRecipe = {
    recipeId,
    name: exp.name,
    target: defaultTarget(definition.kind, exp) ?? 'application',
    packageId: pkg.packageId,
    revision: pkg.packageRevision,
    exportId: exp.id,
    parametersJson: '{}',
    overlayLayer: 'package',
    enabled: true,
    readScope: 'boundSources',
    branchInput: null,
    fixedSeed: null,
    configurationId: null,
  };
  return { definition: { ...definition, effectRecipes: [...definition.effectRecipes, recipe] }, recipeId };
}

export function updateRecipe(definition: FullTemplateDefinition, recipeId: string, patch: Partial<Omit<TemplateEffectRecipe, 'recipeId'>>): FullTemplateDefinition {
  return { ...definition, effectRecipes: definition.effectRecipes.map((r) => (r.recipeId === recipeId ? { ...r, ...patch } : r)) };
}

export function removeRecipe(definition: FullTemplateDefinition, recipeId: string): FullTemplateDefinition {
  return { ...definition, effectRecipes: definition.effectRecipes.filter((r) => r.recipeId !== recipeId) };
}

/** 上移（-1）或下移（+1）；到头时不变。 */
export function moveRecipe(definition: FullTemplateDefinition, recipeId: string, delta: -1 | 1): FullTemplateDefinition {
  const recipes = [...definition.effectRecipes];
  const from = recipes.findIndex((r) => r.recipeId === recipeId);
  const to = from + delta;
  if (from < 0 || to < 0 || to >= recipes.length) return definition;
  [recipes[from], recipes[to]] = [recipes[to]!, recipes[from]!];
  return { ...definition, effectRecipes: recipes };
}

/** 复制一项，排在它后面；新 ID，名称加“（副本）”，不带效果组件来源。 */
export function duplicateRecipe(definition: FullTemplateDefinition, recipeId: string): { definition: FullTemplateDefinition; recipeId: string } {
  const index = definition.effectRecipes.findIndex((r) => r.recipeId === recipeId);
  if (index < 0) return { definition, recipeId };
  const copyId = nextRecipeId(definition);
  const copy = { ...definition.effectRecipes[index]!, recipeId: copyId, name: `${definition.effectRecipes[index]!.name}（副本）`, configurationId: null };
  const recipes = [...definition.effectRecipes];
  recipes.splice(index + 1, 0, copy);
  return { definition: { ...definition, effectRecipes: recipes }, recipeId: copyId };
}

/** 换读取范围：“指定输入”保留已有的输入名（没有时为空，等用户填），其他范围去掉输入名。 */
export function withReadScope(recipe: TemplateEffectRecipe, readScope: EffectReadScope): Pick<TemplateEffectRecipe, 'readScope' | 'branchInput'> {
  return { readScope, branchInput: readScope === 'branchInput' ? (recipe.branchInput ?? '') : null };
}

/** 一个配方与导出无关、或与导出声明有关的问题（参数、包和种子另查）。exp 还没读到时只查与导出无关的。 */
export function recipeProblems(kind: TemplateKind, recipe: TemplateEffectRecipe, exp: EffectPackageExport | undefined): string[] {
  const problems: string[] = [];
  if (!recipe.name.trim()) problems.push('名称不能为空。');
  const target = targetProblem(kind, recipe.target, exp);
  if (target) problems.push(target);
  if (exp && recipe.overlayLayer !== 'package' && !hasOverlay(exp)) problems.push(`“${exp.name}”没有图元输出，叠放层只能“随包”。`);
  if (recipe.readScope === 'branchInput' ? !recipe.branchInput?.trim() : recipe.branchInput !== null) problems.push('读取范围是“指定输入”时要填输入名。');
  return problems;
}

/** 配方 ID 重复（后端拒绝整份定义）。 */
export function duplicateRecipeIds(definition: FullTemplateDefinition): string[] {
  const seen = new Set<string>();
  const duplicates = new Set<string>();
  for (const r of definition.effectRecipes) (seen.has(r.recipeId) ? duplicates : seen).add(r.recipeId);
  return [...duplicates];
}
