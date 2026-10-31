// 完整模板定义的纯函数（W7 设计 §2.2～§2.3）：包引用重定位、静态定义的判断与转换、材质部位、包闭包与本地校验。

import type {
  EffectExportKind,
  EffectPackageExport,
  EffectPackageInspectResponse,
  EffectPackageReference,
  FullTemplateDefinition,
  FullTemplateMaterial,
  FullTemplateStyle,
  StaticMaterial,
  StaticTemplateDefinition,
  StyleColor,
  TemplateMaterialSlot,
} from '../../protocol';
import { parametersIssues, seedProblem } from './effect-params';
import { duplicateRecipeIds, recipeProblems } from './effect-recipes';
import { parseColor } from './settings';
import { gradientFrom, solidFrom } from './template-groups';

/** 同一个包：包 ID 与包修订都相同。 */
export function packageKey(packageId: string, packageRevision: string): string {
  return `${packageId}\u0000${packageRevision}`;
}

/** 包修订的前 12 位（去掉 sha256: 前缀），用于显示。 */
export function shortRevision(revision: string): string {
  return revision.replace(/^sha256:/, '').slice(0, 12);
}

export function referenceKey(reference: EffectPackageReference): string {
  return packageKey(reference.packageId, reference.packageRevision);
}

/**
 * 库修订变了以后，草稿里旧的 library／configuration 引用会过期（409）：换成新读到的、同一包的引用。
 * asset 引用（新导入、还没保存的包）不动；没有变化时返回原对象。
 */
export function rebasePackageReferences(definition: FullTemplateDefinition, fresh: readonly EffectPackageReference[]): FullTemplateDefinition {
  const latest = new Map(fresh.filter((r) => r.kind === 'library').map((r) => [referenceKey(r), r]));
  const configurations = new Map(fresh.filter((r) => r.kind === 'configuration').map((r) => [`${r.configurationId}\u0000${referenceKey(r)}`, r]));
  let changed = false;
  const packages = definition.packages.map((reference) => {
    if (reference.kind === 'asset') return reference;
    const next = reference.kind === 'configuration' ? configurations.get(`${reference.configurationId}\u0000${referenceKey(reference)}`) : latest.get(referenceKey(reference));
    if (!next || JSON.stringify(next) === JSON.stringify(reference)) {
      return reference;
    }
    changed = true;
    return next;
  });
  return changed ? { ...definition, packages } : definition;
}

export function isStaticMaterial(material: FullTemplateMaterial): material is StaticMaterial {
  return material.kind !== 'package';
}

/** 定义里用到包材质的部位。 */
export function packageMaterials(definition: FullTemplateDefinition): FullTemplateMaterial[] {
  const { style } = definition;
  return [style.unsungFill.material, style.sungFill.material, ...(style.stroke ? [style.stroke.material] : [])].filter((m) => m.kind === 'package');
}

/** 没有包、配方和包材质时，按静态格式给出同一份定义；否则 undefined。 */
export function staticDefinition(definition: FullTemplateDefinition): StaticTemplateDefinition | undefined {
  if (definition.packages.length > 0 || definition.effectRecipes.length > 0 || packageMaterials(definition).length > 0) return undefined;
  const { style } = definition;
  const unsung = style.unsungFill.material as StaticMaterial;
  const sung = style.sungFill.material as StaticMaterial;
  return {
    name: definition.name,
    kind: definition.kind,
    properties: definition.properties,
    style: {
      ...style,
      unsungFill: { material: unsung },
      sungFill: { material: sung },
      stroke: style.stroke ? { ...style.stroke, material: style.stroke.material as StaticMaterial } : null,
    },
  };
}

// ── 材质部位 ──────────────────────────────────────────

export const MATERIAL_SLOTS: readonly TemplateMaterialSlot[] = ['unsungFill', 'sungFill', 'stroke'];
export const SLOT_LABEL: Record<TemplateMaterialSlot, string> = { unsungFill: '未唱填充', sungFill: '已唱填充', stroke: '描边' };

/** 部位的材质；描边关闭时 null。 */
export function slotMaterial(style: FullTemplateStyle, slot: TemplateMaterialSlot): FullTemplateMaterial | null {
  if (slot === 'stroke') return style.stroke?.material ?? null;
  return style[slot].material;
}

/** 换一个部位的材质；描边关闭时不变。 */
export function withSlotMaterial(style: FullTemplateStyle, slot: TemplateMaterialSlot, material: FullTemplateMaterial): FullTemplateStyle {
  if (slot === 'stroke') return style.stroke ? { ...style, stroke: { ...style.stroke, material } } : style;
  return { ...style, [slot]: { material } };
}

/** 包材质导出能用在哪类部位（后端的 region：fill 是两种填充，stroke 是描边）。 */
export function slotRegion(slot: TemplateMaterialSlot): 'fill' | 'stroke' {
  return slot === 'stroke' ? 'stroke' : 'fill';
}

/** 包里能用在这个部位的材质导出；导出没写 regions 时不限。 */
export function materialExports(pkg: EffectPackageInspectResponse, slot: TemplateMaterialSlot): EffectPackageExport[] {
  return pkg.exports.filter((e) => e.kind === 'material' && regionsOf(e)?.includes(slotRegion(slot)) !== false);
}

/** 导出声明的 regions；没写时 undefined。 */
function regionsOf(exp: EffectPackageExport): string[] | undefined {
  const regions = exp.declaration['regions'];
  return Array.isArray(regions) ? regions.filter((r): r is string => typeof r === 'string') : undefined;
}

/** 材质导出声明的退化色（fallback 纯色 #RRGGBB(AA)）；没有时 undefined。 */
export function materialFallbackColor(exp: EffectPackageExport | undefined): string | undefined {
  const fallback = exp?.declaration['fallback'];
  if (!fallback || typeof fallback !== 'object') return undefined;
  const color = (fallback as Record<string, unknown>)['color'];
  return typeof color === 'string' ? color : undefined;
}

/** 一个部位能选的包材质：包和其中能用在这个部位的材质导出。 */
export interface MaterialCandidate {
  pkg: EffectPackageInspectResponse;
  exports: EffectPackageExport[];
}

/**
 * 列出的包里能用在这个部位的包材质，按包的顺序；读不到包信息的包不列。
 * 同一个包可能既在定义里又在草稿导入过的包里（刚导入就用作材质），只列一次。
 */
export function materialCandidates(
  references: readonly EffectPackageReference[],
  catalog: ReadonlyMap<string, EffectPackageInspectResponse>,
  slot: TemplateMaterialSlot,
): MaterialCandidate[] {
  const seen = new Set<string>();
  return references.flatMap((reference) => {
    const key = referenceKey(reference);
    if (seen.has(key)) return [];
    seen.add(key);
    const pkg = catalog.get(key);
    const exports = pkg ? materialExports(pkg, slot) : [];
    return pkg && exports.length > 0 ? [{ pkg, exports }] : [];
  });
}

/** 指向一个包材质导出，参数全部取默认值。 */
export function packageMaterial(pkg: EffectPackageInspectResponse, exportId: string): FullTemplateMaterial {
  return { kind: 'package', packageId: pkg.packageId, revision: pkg.packageRevision, exportId, parametersJson: '{}' };
}

/**
 * 材质页签换类型。纯色和渐变互转沿用外观页签的规则；包材质换成纯色或渐变时从导出的退化色出发（没有时黑色）；
 * 换成包材质时用 candidate（第一个能用的包材质），没有时 undefined。
 */
export function changeMaterialKind(
  material: FullTemplateMaterial,
  kind: FullTemplateMaterial['kind'],
  fallback: string | undefined,
  candidate: FullTemplateMaterial | undefined,
): FullTemplateMaterial | undefined {
  if (kind === material.kind) return material;
  if (kind === 'package') return candidate;
  const start: StaticMaterial = material.kind === 'package' ? { kind: 'solid', color: fallbackColor(fallback) } : material;
  if (kind === 'solid') return solidFrom(start);
  return start.kind === 'solid' ? gradientFrom(start.color) : start;
}

function fallbackColor(text: string | undefined): StyleColor {
  const parsed = text ? parseColor(text) : undefined;
  return parsed?.ok ? parsed.value : { red: 0, green: 0, blue: 0, alpha: 255 };
}

// ── 包闭包 ────────────────────────────────────────────

/** 定义里用到的包（包 ID＋修订），按第一次出现的顺序。 */
export function usedPackageKeys(definition: FullTemplateDefinition): string[] {
  const keys = [
    ...packageMaterials(definition).map((m) => (m.kind === 'package' ? packageKey(m.packageId, m.revision) : '')),
    ...definition.effectRecipes.map((r) => packageKey(r.packageId, r.revision)),
  ];
  return [...new Set(keys)];
}

/**
 * 后端要求 packages 恰好是材质和配方用到的包（多一个、少一个都是 InvalidDefinition）。
 * 按用到的包重算 packages：已有的引用优先，其次取 available 里同一包的引用（新导入的 asset 引用）；
 * 哪里都找不到的包留给校验报告。没有变化时返回原对象。
 */
export function syncPackages(definition: FullTemplateDefinition, available: Iterable<EffectPackageReference>): FullTemplateDefinition {
  const pool = new Map<string, EffectPackageReference>();
  for (const reference of available) if (!pool.has(referenceKey(reference))) pool.set(referenceKey(reference), reference);
  for (const reference of definition.packages) pool.set(referenceKey(reference), reference);
  const packages = usedPackageKeys(definition).flatMap((key) => pool.get(key) ?? []);
  const same = packages.length === definition.packages.length && packages.every((p) => definition.packages.includes(p));
  return same ? definition : { ...definition, packages };
}

/** 谁在用这个包：例如 ["未唱填充", "效果“逐字强调”"]。 */
export function packageUsers(definition: FullTemplateDefinition, key: string): string[] {
  const users: string[] = [];
  for (const slot of MATERIAL_SLOTS) {
    const material = slotMaterial(definition.style, slot);
    if (material?.kind === 'package' && packageKey(material.packageId, material.revision) === key) users.push(SLOT_LABEL[slot]);
  }
  for (const recipe of definition.effectRecipes) if (packageKey(recipe.packageId, recipe.revision) === key) users.push(`效果“${recipe.name}”`);
  return users;
}

/** 同一包换成新修订：用到旧修订的材质和配方都改指新修订；参数原样保留（新修订没有的键由校验标出）。 */
export function replacePackageRevision(definition: FullTemplateDefinition, packageId: string, from: string, to: string): FullTemplateDefinition {
  const swap = <T extends { packageId: string; revision: string }>(item: T): T => (item.packageId === packageId && item.revision === from ? { ...item, revision: to } : item);
  const swapMaterial = (m: FullTemplateMaterial): FullTemplateMaterial => (m.kind === 'package' ? swap(m) : m);
  const { style } = definition;
  return {
    ...definition,
    style: {
      ...style,
      unsungFill: { material: swapMaterial(style.unsungFill.material) },
      sungFill: { material: swapMaterial(style.sungFill.material) },
      stroke: style.stroke ? { ...style.stroke, material: swapMaterial(style.stroke.material) } : null,
    },
    effectRecipes: definition.effectRecipes.map(swap),
  };
}

// ── 本地校验（W7 设计 §2.2，弥补 G31） ────────────────

export type IssueWhere = { kind: 'slot'; slot: TemplateMaterialSlot } | { kind: 'recipe'; recipeId: string } | { kind: 'package'; key: string };

export interface DefinitionIssue {
  where: IssueWhere;
  /** 参数 ID（参数控件旁显示）；整项的问题没有。 */
  parameterId?: string;
  message: string;
}

/**
 * 包材质与配方的问题：包在 packages 里、读到了包信息、导出存在且种类对、部位支持、参数合法、种子合法；
 * 配方的目标、叠放层、读取范围与名称（effect-recipes.ts）。
 * catalog 里没有的包（还在读取）只查是否在 packages 里，其余留给后端。
 */
export function definitionIssues(definition: FullTemplateDefinition, catalog: ReadonlyMap<string, EffectPackageInspectResponse>): DefinitionIssue[] {
  const issues: DefinitionIssue[] = [];
  const declared = new Set(definition.packages.map(referenceKey));
  const check = (where: IssueWhere, packageId: string, revision: string, exportId: string, parametersJson: string, kinds: readonly EffectExportKind[], slot?: TemplateMaterialSlot) => {
    const key = packageKey(packageId, revision);
    if (!declared.has(key)) {
      issues.push({ where, message: `模板里没有包 ${packageId}（修订 ${shortRevision(revision)}）。` });
      return;
    }
    const pkg = catalog.get(key);
    if (!pkg) return;
    const exp = pkg.exports.find((e) => e.id === exportId);
    if (!exp) {
      issues.push({ where, message: `包“${pkg.name}”里没有导出 ${exportId}。` });
      return;
    }
    if (!kinds.includes(exp.kind)) {
      issues.push({ where, message: `导出“${exp.name}”是${EXPORT_KIND_TEXT[exp.kind]}，这里要用${kinds.map((k) => EXPORT_KIND_TEXT[k]).join('或')}。` });
      return;
    }
    if (slot && regionsOf(exp)?.includes(slotRegion(slot)) === false) issues.push({ where, message: `材质“${exp.name}”不能用在${SLOT_LABEL[slot]}。` });
    for (const issue of parametersIssues(parametersJson, pkg, exportId)) issues.push({ where, ...(issue.id ? { parameterId: issue.id } : {}), message: issue.message });
  };
  for (const slot of MATERIAL_SLOTS) {
    const material = slotMaterial(definition.style, slot);
    if (material?.kind === 'package') check({ kind: 'slot', slot }, material.packageId, material.revision, material.exportId, material.parametersJson, ['material'], slot);
  }
  const duplicates = duplicateRecipeIds(definition);
  for (const recipe of definition.effectRecipes) {
    const where: IssueWhere = { kind: 'recipe', recipeId: recipe.recipeId };
    if (duplicates.includes(recipe.recipeId)) issues.push({ where, message: `效果 ID ${recipe.recipeId} 重复。` });
    check(where, recipe.packageId, recipe.revision, recipe.exportId, recipe.parametersJson, ['controller', 'graph']);
    const exp = catalog.get(packageKey(recipe.packageId, recipe.revision))?.exports.find((e) => e.id === recipe.exportId && e.kind !== 'material');
    for (const message of recipeProblems(definition.kind, recipe, exp)) issues.push({ where, message });
    if (recipe.fixedSeed !== null) {
      const problem = seedProblem(recipe.fixedSeed);
      if (problem) issues.push({ where, message: problem });
    }
  }
  const used = new Set(usedPackageKeys(definition));
  for (const reference of definition.packages) {
    const key = referenceKey(reference);
    if (!used.has(key)) issues.push({ where: { kind: 'package', key }, message: `包 ${reference.packageId} 没有被用到。` });
  }
  return issues;
}

/** 问题所在位置的说明，例如“未唱填充”“效果“逐字强调””。 */
export function issueWhereText(definition: FullTemplateDefinition, where: IssueWhere): string {
  if (where.kind === 'slot') return SLOT_LABEL[where.slot];
  if (where.kind === 'recipe') return `效果“${definition.effectRecipes.find((r) => r.recipeId === where.recipeId)?.name ?? where.recipeId}”`;
  return '效果包';
}

export const EXPORT_KIND_TEXT: Record<EffectExportKind, string> = { material: '材质', controller: '控制器', graph: '图' };
