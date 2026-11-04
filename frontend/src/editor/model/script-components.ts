// 效果组件（W8 设计 §2.3，后端叫个人配置库）：导入效果包时每个导出的默认配置与名称、列表筛选、
// 选中组件的摘要文字，以及试样用的临时完整模板。不依赖 React。

import type {
  AuthorDraft,
  AuthorDraftConfiguration,
  AuthorDraftConfigurationInput,
  EffectOverlayLayer,
  EffectPackageExport,
  EffectPackageInspectResponse,
  EffectPackageParameter,
  FullTemplateDefinition,
  ScriptConfiguration,
  ScriptConfigurationDetailResponse,
  ScriptConfigurationFields,
  ScriptConfigurationInput,
  ScriptConfigurationKind,
  EffectReadScope,
  EffectRecipeTarget,
  TemplateEffectRecipe,
  TemplateMaterialSlot,
  TemplateKind,
} from '../../protocol';
import { CURVE_KIND_LABEL, EASING_LABEL, readCurve } from './curves';
import { componentTargetProblem, defaultTarget, effectExports, exportProblem, hasOverlay, nextRecipeId, targetProblem } from './effect-recipes';
import { exportParameters, isOverridden, microsToMillis, parameterRaw, parametersIssues, seedProblem } from './effect-params';
import { materialExports, SLOT_LABEL, slotMaterial, syncPackages, withSlotMaterial } from './full-template';
import { parseObjectEntries, sameJsonValue } from './json-text';

/** 领域分类的中文；不认识的原样显示。 */
export function componentKindText(kind: ScriptConfigurationKind): string {
  return kind === 'Effect' ? '效果' : kind === 'Material' ? '材质' : kind;
}

/** 能导入成组件的导出：材质、控制器和图。 */
export function importableExports(pkg: EffectPackageInspectResponse): EffectPackageExport[] {
  return pkg.exports.filter((e) => e.kind === 'material' || e.kind === 'controller' || e.kind === 'graph');
}

/** 导入时的名称：包里只有一个能导入的导出时用导出名，有多个时用“包名 · 导出名”。 */
export function importedName(pkg: EffectPackageInspectResponse, exp: EffectPackageExport): string {
  return importableExports(pkg).length > 1 ? `${pkg.name} · ${exp.name}` : exp.name;
}

/**
 * 导入一个导出时新建组件的配置：来源是上传得到的包引用，参数全部默认，读取范围“绑定的来源”，叠放层“随包”，种子不固定；
 * 作用于：材质取“整个分配”，效果按 W7 行模板的默认规则（只写文本单元族的导出取“逐字”，否则“整行”，都不行时“整个分配”）。
 */
export function importedConfiguration(pkg: EffectPackageInspectResponse, exp: EffectPackageExport): ScriptConfigurationInput {
  return {
    name: importedName(pkg, exp),
    exportId: exp.id,
    parametersJson: '{}',
    relativeTarget: exp.kind === 'material' ? 'application' : (defaultTarget('line', exp) ?? 'application'),
    readScope: 'boundSources',
    branchInput: null,
    fixedSeed: null,
    overlayLayer: 'package',
    source: { package: pkg.reference },
  };
}

/** 名称包含搜索词（不区分大小写）。 */
export function filterByName<T extends { name: string }>(items: readonly T[], query: string): T[] {
  const needle = query.trim().toLocaleLowerCase();
  return needle ? items.filter((i) => i.name.toLocaleLowerCase().includes(needle)) : [...items];
}

/** 组件名称的校验文案；合法时 undefined。 */
export function checkComponentName(name: string): string | undefined {
  return name.trim() ? undefined : '名称不能为空。';
}

/** 改过的参数，按导出声明的顺序：“标签：取值”。 */
export function parameterSummary(pkg: EffectPackageInspectResponse, configuration: Pick<ScriptConfigurationFields, 'exportId' | 'parametersJson'>): string[] {
  const exp = pkg.exports.find((e) => e.id === configuration.exportId);
  if (!exp) return [];
  return exportParameters(pkg, exp)
    .filter((p) => isOverridden(configuration.parametersJson, p.id))
    .map((p) => `${p.label}：${parameterValueText(p, parameterRaw(configuration.parametersJson, p))}`);
}

function parameterValueText(parameter: EffectPackageParameter, raw: string): string {
  switch (parameter.type) {
    case 'duration':
      return `${microsToMillis(raw)} 毫秒`;
    case 'curve': {
      const read = readCurve(raw);
      if ('problem' in read) return raw;
      return read.curve.kind === 'easing' ? `${CURVE_KIND_LABEL.easing} · ${EASING_LABEL[read.curve.easing]}` : CURVE_KIND_LABEL[read.curve.kind];
    }
    case 'enum': {
      const value = jsonString(raw);
      return parameter.options.find((o) => o.value === value)?.label ?? value ?? raw;
    }
    case 'text':
    case 'asset':
      return jsonString(raw) ?? raw;
    default:
      return parameter.unit && parameter.type !== 'boolean' ? `${raw} ${parameter.unit}` : raw;
  }
}

function jsonString(raw: string): string | undefined {
  try {
    const value: unknown = JSON.parse(raw);
    return typeof value === 'string' ? value : undefined;
  } catch {
    return undefined;
  }
}

// ── 模板里选用组件（§2.10）───────────────────────────────

/** 组件的目标保持原样，不能为了适配模板悄悄换成另一个目标。 */
export function componentEffectProblem(kind: TemplateKind, detail: ScriptConfigurationDetailResponse): string | undefined {
  const { configuration, package: pkg } = detail;
  if (!pkg.structureValid) return '效果包的结构无效。';
  const exp = pkg.exports.find((e) => e.id === configuration.exportId);
  if (!exp) return `包里没有导出“${configuration.exportId}”。`;
  if (configuration.kind !== 'Effect' || (exp.kind !== 'controller' && exp.kind !== 'graph')) return '这个组件不是效果。';
  return targetProblem(kind, configuration.relativeTarget, exp) ?? exportProblem(kind, exp);
}

/** 材质组件只能用于导出声明支持的部位。 */
export function componentMaterialProblem(slot: TemplateMaterialSlot, detail: ScriptConfigurationDetailResponse): string | undefined {
  const { configuration, package: pkg } = detail;
  if (!pkg.structureValid) return '效果包的结构无效。';
  const exp = pkg.exports.find((e) => e.id === configuration.exportId);
  if (!exp) return `包里没有导出“${configuration.exportId}”。`;
  if (configuration.kind !== 'Material' || exp.kind !== 'material') return '这个组件不是材质。';
  return materialExports(pkg, slot).some((e) => e.id === exp.id) ? undefined : `材质“${exp.name}”不能用在${SLOT_LABEL[slot]}。`;
}

/** 拷贝组件配置为模板自己的配方；只保留来源 ID，之后两边的编辑互不影响。 */
export function addComponentRecipe(definition: FullTemplateDefinition, detail: ScriptConfigurationDetailResponse): { definition: FullTemplateDefinition; recipeId: string } {
  const { configuration: c } = detail;
  const recipeId = nextRecipeId(definition);
  const recipe: TemplateEffectRecipe = {
    recipeId,
    name: c.name,
    target: c.relativeTarget,
    packageId: c.packageId,
    revision: c.packageRevision,
    exportId: c.exportId,
    parametersJson: c.parametersJson,
    overlayLayer: c.overlayLayer,
    enabled: true,
    readScope: c.readScope,
    branchInput: c.branchInput ?? null,
    fixedSeed: c.fixedSeed ?? null,
    configurationId: c.configurationId,
  };
  // 同一包已有 asset／library／configuration 引用时优先沿用，不能重复声明。
  return { definition: syncPackages({ ...definition, effectRecipes: [...definition.effectRecipes, recipe] }, [detail.package.reference]), recipeId };
}

/** 材质只拷贝包、导出与参数，不写效果配方的来源字段。 */
export function withComponentMaterial(definition: FullTemplateDefinition, slot: TemplateMaterialSlot, detail: ScriptConfigurationDetailResponse): FullTemplateDefinition {
  if (!slotMaterial(definition.style, slot)) return definition;
  const { configuration: c } = detail;
  const material = { kind: 'package' as const, packageId: c.packageId, revision: c.packageRevision, exportId: c.exportId, parametersJson: c.parametersJson };
  return syncPackages({ ...definition, style: withSlotMaterial(definition.style, slot, material) }, [detail.package.reference]);
}

/** 选中组件的试样：临时完整模板，和材质组件所在的部位（材质焦点）。 */
export type ComponentSample = { definition: FullTemplateDefinition; materialSlot?: TemplateMaterialSlot } | { problem: string };

/**
 * 用内置“经典整行”（base）做底拼一个完整模板（§2.3）：效果组件是一个带 configurationId 的配方，
 * 材质组件放进“未唱填充”（只能用于描边的材质放进描边）。包引用用详情里的 configuration 引用。不写库。
 */
export function componentSample(base: FullTemplateDefinition, detail: ScriptConfigurationDetailResponse): ComponentSample {
  const { configuration, package: pkg } = detail;
  const exp = pkg.exports.find((e) => e.id === configuration.exportId);
  if (!exp) return { problem: `包里没有导出“${configuration.exportId}”。` };
  const packages = [...base.packages.filter((r) => r.packageId !== pkg.packageId || r.packageRevision !== pkg.packageRevision), pkg.reference];
  if (exp.kind === 'material') {
    const slot = (['unsungFill', 'stroke'] as const).find((s) => materialExports(pkg, s).some((e) => e.id === exp.id));
    if (!slot) return { problem: '这个材质不能用在填充或描边上，没法试样。' };
    if (slot === 'stroke' && !base.style.stroke) return { problem: '这个材质只能用于描边，试样模板没有描边。' };
    const material = { kind: 'package' as const, packageId: pkg.packageId, revision: pkg.packageRevision, exportId: exp.id, parametersJson: configuration.parametersJson };
    return { definition: { ...base, name: configuration.name, style: withSlotMaterial(base.style, slot, material), packages }, materialSlot: slot === 'stroke' ? 'stroke' : 'unsungFill' };
  }
  const problem = targetProblem(base.kind, configuration.relativeTarget, exp);
  if (problem) return { problem: `没法在整行模板上试样：${problem}` };
  const recipe: TemplateEffectRecipe = {
    recipeId: 'component',
    name: configuration.name,
    target: configuration.relativeTarget,
    packageId: pkg.packageId,
    revision: pkg.packageRevision,
    exportId: exp.id,
    parametersJson: configuration.parametersJson,
    overlayLayer: configuration.overlayLayer,
    enabled: true,
    readScope: configuration.readScope,
    branchInput: configuration.branchInput ?? null,
    fixedSeed: configuration.fixedSeed ?? null,
    configurationId: configuration.configurationId,
  };
  return { definition: { ...base, name: configuration.name, effectRecipes: [...base.effectRecipes, recipe], packages } };
}

// ── 作者草稿（§2.4）──────────────────────────────────────

/** 草稿配置是否和组件一致：名称、导出、参数、作用于、读取范围、输入名、种子、叠放层和在用修订。参数按 JSON 值比较，不看键序和数字写法。 */
export function sameAsComponent(draft: AuthorDraftConfiguration, component: ScriptConfiguration): boolean {
  return (
    draft.name === component.name &&
    draft.packageId === component.packageId &&
    draft.sourceRevision === component.sourceRevision &&
    draft.exportId === component.exportId &&
    draft.relativeTarget === component.relativeTarget &&
    draft.readScope === component.readScope &&
    (draft.branchInput ?? null) === (component.branchInput ?? null) &&
    (draft.fixedSeed ?? null) === (component.fixedSeed ?? null) &&
    draft.overlayLayer === component.overlayLayer &&
    sameParameters(draft.parametersJson, component.parametersJson)
  );
}

function sameParameters(a: string, b: string): boolean {
  try {
    const x = parseObjectEntries(a);
    const y = parseObjectEntries(b);
    return x.size === y.size && [...x].every(([key, raw]) => y.has(key) && sameJsonValue(raw, y.get(key)!));
  } catch {
    return a === b;
  }
}

/** 草稿配置的写法（可空字段显式写 null）。 */
export function draftConfigurationInput(configuration: AuthorDraftConfiguration): AuthorDraftConfigurationInput {
  return {
    name: configuration.name,
    packageId: configuration.packageId,
    sourceRevision: configuration.sourceRevision,
    exportId: configuration.exportId,
    parametersJson: configuration.parametersJson,
    relativeTarget: configuration.relativeTarget,
    readScope: configuration.readScope,
    branchInput: configuration.branchInput ?? null,
    fixedSeed: configuration.fixedSeed ?? null,
    overlayLayer: configuration.overlayLayer,
  };
}

/** 存进组件库时的写法：来源是草稿的当前修订和在用修订。 */
export function componentFromDraft(draft: AuthorDraft & { configuration: AuthorDraftConfiguration }, name = draft.configuration.name): ScriptConfigurationInput {
  const { packageId: _packageId, sourceRevision, ...rest } = draftConfigurationInput(draft.configuration);
  return { ...rest, name, source: { draftId: draft.draftId, expectedDraftRevision: draft.draftRevision, sourceRevision } };
}

/** 最近一次编译是否验证在用修订；SDK2 的旧于最新标记不使该固定源的成功失效。 */
export function compiledOk(draft: AuthorDraft): boolean {
  const last = draft.lastCompilation;
  const v2 = draft.packageVersion === 3 && draft.sdkVersion?.split('.')[0] === '2';
  return !!last && !!draft.configuration && last.sourceRevision === draft.configuration.sourceRevision && last.succeeded && (v2 || !last.stale);
}

// ── 配置页签（§2.5）──────────────────────────────────────

/** 配置页签能改的字段。包和在用修订归源码页签管，不进撤销栈，提交时取草稿当时的值。 */
export interface EditableConfiguration {
  name: string;
  exportId: string;
  parametersJson: string;
  relativeTarget: EffectRecipeTarget;
  readScope: EffectReadScope;
  branchInput: string | null;
  fixedSeed: string | null;
  overlayLayer: EffectOverlayLayer;
}

export function editableOf(configuration: AuthorDraftConfiguration): EditableConfiguration {
  return {
    name: configuration.name,
    exportId: configuration.exportId,
    parametersJson: configuration.parametersJson,
    relativeTarget: configuration.relativeTarget,
    readScope: configuration.readScope,
    branchInput: configuration.branchInput ?? null,
    fixedSeed: configuration.fixedSeed ?? null,
    overlayLayer: configuration.overlayLayer,
  };
}

/** 两份可改字段是否相同；参数按 JSON 值比较。 */
export function sameEditable(a: EditableConfiguration, b: EditableConfiguration): boolean {
  return (
    a.name === b.name &&
    a.exportId === b.exportId &&
    a.relativeTarget === b.relativeTarget &&
    a.readScope === b.readScope &&
    a.branchInput === b.branchInput &&
    a.fixedSeed === b.fixedSeed &&
    a.overlayLayer === b.overlayLayer &&
    sameParameters(a.parametersJson, b.parametersJson)
  );
}

/** 提交的完整配置：可改字段用页签里的，包和在用修订用草稿当时的。 */
export function configurationWith(current: AuthorDraftConfiguration, value: EditableConfiguration): AuthorDraftConfigurationInput {
  return { ...draftConfigurationInput(current), ...value };
}

/** 配置的一个问题：参数的写 parameterId（显示在参数下），其他字段写 field（name、exportId、relativeTarget、overlayLayer、branchInput、fixedSeed），都没有的显示在页签顶部。 */
export interface ConfigIssue {
  field?: string;
  parameterId?: string;
  message: string;
}

/** 能换成的导出：同一个包里同类别的（效果是控制器和图，材质是材质）。 */
export function sameKindExports(pkg: EffectPackageInspectResponse, kind: ScriptConfigurationKind): EffectPackageExport[] {
  return kind === 'Material' ? pkg.exports.filter((e) => e.kind === 'material') : effectExports(pkg);
}

/**
 * 提交前的本地校验（沿用 W7 的规则）：名称、参数、固定种子、“指定输入”要有输入名；效果还查作用于和叠放层。
 * 材质只有名称和参数（作用于、种子、叠放层后端固定）。包信息没读到时参数只查是不是 JSON 对象，其余留给后端。
 */
export function configurationIssues(value: EditableConfiguration, kind: ScriptConfigurationKind, pkg: EffectPackageInspectResponse | undefined): ConfigIssue[] {
  const issues: ConfigIssue[] = [];
  if (!value.name.trim()) issues.push({ field: 'name', message: '名称不能为空。' });
  const exp = pkg?.exports.find((e) => e.id === value.exportId);
  if (pkg && !exp) issues.push({ field: 'exportId', message: `包里没有导出“${value.exportId}”。` });
  if (pkg) issues.push(...parametersIssues(value.parametersJson, pkg, value.exportId).map((i) => (i.id ? { parameterId: i.id, message: i.message } : { message: i.message })));
  else if (!isObjectJson(value.parametersJson)) issues.push({ message: '参数不是合法的 JSON 对象。' });
  if (kind === 'Material') return issues;
  const target = componentTargetProblem(value.relativeTarget, exp);
  if (target) issues.push({ field: 'relativeTarget', message: target });
  if (exp && value.overlayLayer !== 'package' && !hasOverlay(exp)) issues.push({ field: 'overlayLayer', message: `“${exp.name}”没有图元输出，叠放层只能“随包”。` });
  if (value.readScope === 'branchInput' && !value.branchInput?.trim()) issues.push({ field: 'branchInput', message: '要填输入名。' });
  const seed = value.fixedSeed === null ? undefined : seedProblem(value.fixedSeed);
  if (seed) issues.push({ field: 'fixedSeed', message: seed });
  return issues;
}

/** 换导出：参数清空；效果按新导出取默认的作用于（同导入时），没有图元输出时叠放层改回“随包”。 */
export function withExport(value: EditableConfiguration, kind: ScriptConfigurationKind, exp: EffectPackageExport): EditableConfiguration {
  if (kind === 'Material') return { ...value, exportId: exp.id, parametersJson: '{}' };
  const relativeTarget = componentTargetProblem(value.relativeTarget, exp) ? (defaultTarget('line', exp) ?? 'application') : value.relativeTarget;
  return { ...value, exportId: exp.id, parametersJson: '{}', relativeTarget, overlayLayer: hasOverlay(exp) ? value.overlayLayer : 'package' };
}

/** 后端拒绝时写在哪：field 是 parameters.<参数> 的写在参数下，其他（含材质的 configuration）写在页签顶部、用后端原话。 */
export function rejectionIssue(field: string | undefined, message: string): ConfigIssue {
  const parameter = field?.match(/^parameters\.([^.]+)/)?.[1];
  return parameter ? { parameterId: parameter, message } : { ...(field ? { field } : {}), message };
}

function isObjectJson(text: string): boolean {
  try {
    parseObjectEntries(text);
    return true;
  } catch {
    return false;
  }
}
