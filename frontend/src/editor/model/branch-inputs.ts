// 来源输入（W7 设计 §2.7）：模板里读取范围为“指定输入”的配方只声明输入名，应用时由用户把每个输入名映射到工程里的一个来源。
// 后端要求映射恰好覆盖模板声明的输入名（多一个、少一个都拒绝），停用的配方也算。不依赖 React。

import type { FullTemplateDefinition, LyricBranchSummary } from '../../protocol';
import { BRANCH_ROLE_TEXT } from './branches';

/** 模板声明的输入名：读取范围为“指定输入”的配方的输入名，去重后排序。 */
export function branchInputNames(definition: Pick<FullTemplateDefinition, 'effectRecipes'>): string[] {
  const names = new Set<string>();
  for (const recipe of definition.effectRecipes) if (recipe.readScope === 'branchInput' && recipe.branchInput) names.add(recipe.branchInput);
  return [...names].sort((a, b) => a.localeCompare(b, 'zh-CN'));
}

/**
 * 初始映射：保留已经选好、来源仍在的；否则找名称与输入名相同的来源，或者用途名与输入名相同且只有一个的来源（“翻译”→ 唯一的翻译来源）；都没有时留空。
 */
export function defaultInputMapping(names: readonly string[], branches: readonly LyricBranchSummary[], current: Readonly<Record<string, string>> = {}): Record<string, string> {
  const mapping: Record<string, string> = {};
  for (const name of names) {
    const kept = current[name];
    if (kept && branches.some((b) => b.id === kept)) {
      mapping[name] = kept;
      continue;
    }
    const byName = branches.filter((b) => b.name === name);
    const byRole = branches.filter((b) => BRANCH_ROLE_TEXT[b.role] === name);
    const found = byName.length === 1 ? byName[0] : byName.length === 0 && byRole.length === 1 ? byRole[0] : undefined;
    mapping[name] = found?.id ?? '';
  }
  return mapping;
}

/** 还没选来源（或选的来源已经不在）的输入名。 */
export function missingInputs(names: readonly string[], mapping: Readonly<Record<string, string>>, branches: readonly LyricBranchSummary[]): string[] {
  return names.filter((name) => !branches.some((b) => b.id === mapping[name]));
}

/** 提交用的映射：恰好是声明的输入名；没有输入时为 undefined（不带 branchInputs）。 */
export function branchInputsField(names: readonly string[], mapping: Readonly<Record<string, string>>): Record<string, string> | undefined {
  if (names.length === 0) return undefined;
  return Object.fromEntries(names.map((name) => [name, mapping[name] ?? '']));
}

/** 两组输入名是否相同（不计顺序）。 */
export function sameInputNames(a: readonly string[], b: readonly string[]): boolean {
  return a.length === b.length && [...a].sort().every((name, i) => name === [...b].sort()[i]);
}

/** 分配上的映射写成一句话：“翻译 → 中文翻译、注释 → 已删除的来源”。 */
export function inputMappingText(inputs: Readonly<Record<string, string>>, branches: readonly LyricBranchSummary[] | undefined): string {
  return Object.keys(inputs)
    .sort((a, b) => a.localeCompare(b, 'zh-CN'))
    .map((name) => {
      const branch = branches?.find((b) => b.id === inputs[name]);
      return `${name} → ${branch ? branch.name || branch.id : branches ? '已删除的来源' : inputs[name]}`;
    })
    .join('、');
}
