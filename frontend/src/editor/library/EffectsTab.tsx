// 效果页签（W7 设计 §2.3）：配方列表（启用、上移、下移、复制、删除），“加效果”从包里的控制器或图导出新建，
// 下面编辑选中的一项：名称、作用于、叠放层、读取范围（与输入名）、固定随机种子、参数。

import { useState } from 'react';
import type { EffectPackageExport, EffectPackageInspectResponse, FullTemplateDefinition, ScriptConfigurationSummary, TemplateEffectRecipe } from '../../protocol';
import { seedProblem } from '../model/effect-params';
import {
  addRecipe,
  duplicateRecipe,
  effectExports,
  exportProblem,
  hasOverlay,
  moveRecipe,
  OVERLAY_LABEL,
  OVERLAY_LAYERS,
  READ_SCOPE_LABEL,
  READ_SCOPES,
  RECIPE_TARGET_LABEL,
  RECIPE_TARGETS,
  removeRecipe,
  targetProblem,
  updateRecipe,
  withReadScope,
} from '../model/effect-recipes';
import { packageKey, referenceKey, type DefinitionIssue } from '../model/full-template';
import { componentEffectProblem } from '../model/script-components';
import { CommitField, Select, Switch } from '../ui/legacy/fields';
import { PackagesPanel } from './PackagesPanel';
import { ParameterFields } from './ParameterFields';
import type { TemplateDraft, TemplateLibraryStore } from './template-library-store';
import type { ComponentLibraryStore } from './component-library-store';
import { ComponentPicker } from './ComponentPicker';

export function EffectsTab(props: {
  store: TemplateLibraryStore;
  definition: FullTemplateDefinition;
  draft: TemplateDraft | undefined;
  catalog: ReadonlyMap<string, EffectPackageInspectResponse>;
  issues: readonly DefinitionIssue[];
  disabled: boolean;
  components: ComponentLibraryStore | undefined;
  configurations: readonly ScriptConfigurationSummary[] | undefined;
}) {
  const { store, definition, draft, catalog } = props;
  const editable = !!draft && !props.disabled;
  const recipes = definition.effectRecipes;
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const selected = recipes.find((r) => r.recipeId === selectedId) ?? recipes[0];
  const edit = (next: FullTemplateDefinition) => store.edit(next);
  const issuesOf = (recipeId: string) => props.issues.filter((i) => i.where.kind === 'recipe' && i.where.recipeId === recipeId);
  const lookup = (r: TemplateEffectRecipe) => {
    const pkg = catalog.get(packageKey(r.packageId, r.revision));
    return { pkg, exp: pkg?.exports.find((e) => e.id === r.exportId) };
  };

  return (
    <div className="effects-tab">
      <PackagesPanel store={store} definition={definition} draft={draft} catalog={catalog} disabled={props.disabled} />
      <h3 className="template-advanced-title">效果 · {recipes.length} 个</h3>
      {recipes.length === 0 && <p className="hint">这个模板没有效果。{draft ? '在下面从包里的导出或我的效果组件加一个。' : ''}</p>}
      {recipes.length > 0 && (
        <ul className="recipe-list">
          {recipes.map((r, index) => {
            const { pkg, exp } = lookup(r);
            const active = r.recipeId === selected?.recipeId;
            const problem = issuesOf(r.recipeId).length > 0;
            return (
              <li key={r.recipeId} className={active ? 'recipe-item selected' : 'recipe-item'} data-recipe={r.recipeId} data-selected={active}>
                <button type="button" className="recipe-item-text" onClick={() => setSelectedId(r.recipeId)}>
                  <span className="recipe-name">
                    {r.name}
                    {problem && <mdui-icon-error className="recipe-problem-icon" />}
                  </span>
                  <span className="muted">
                    {RECIPE_TARGET_LABEL[r.target]} · {pkg?.name ?? r.packageId} · {exp?.name ?? r.exportId}
                    {r.enabled ? '' : ' · 已停用'}
                  </span>
                </button>
                <Switch label="启用" checked={r.enabled} onChange={(enabled) => edit(updateRecipe(definition, r.recipeId, { enabled }))} disabled={!editable} />
                {draft && (
                  <span className="recipe-actions">
                    <mdui-button-icon disabled={!editable || index === 0} onClick={() => edit(moveRecipe(definition, r.recipeId, -1))} aria-label={`上移 ${r.name}`}>
                      <mdui-icon-arrow-upward />
                    </mdui-button-icon>
                    <mdui-button-icon disabled={!editable || index === recipes.length - 1} onClick={() => edit(moveRecipe(definition, r.recipeId, 1))} aria-label={`下移 ${r.name}`}>
                      <mdui-icon-arrow-downward />
                    </mdui-button-icon>
                    <mdui-button-icon
                      disabled={!editable}
                      onClick={() => {
                        const copy = duplicateRecipe(definition, r.recipeId);
                        edit(copy.definition);
                        setSelectedId(copy.recipeId);
                      }}
                      aria-label={`复制 ${r.name}`}
                    >
                      <mdui-icon-content-copy />
                    </mdui-button-icon>
                    <mdui-button-icon disabled={!editable} onClick={() => edit(removeRecipe(definition, r.recipeId))} aria-label={`删除 ${r.name}`}>
                      <mdui-icon-delete />
                    </mdui-button-icon>
                  </span>
                )}
              </li>
            );
          })}
        </ul>
      )}
      {draft && (
        <>
        <h3 className="template-advanced-title">包里的导出</h3>
        <AddEffect
          definition={definition}
          draft={draft}
          catalog={catalog}
          disabled={!editable}
          onAdd={(pkg, exp) => {
            const added = addRecipe(definition, pkg, exp);
            edit(added.definition);
            setSelectedId(added.recipeId);
          }}
        />
        {props.components && <>
          <h3 className="template-advanced-title">我的效果组件</h3>
          <ComponentPicker
            components={props.components}
            kind="Effect"
            problemOf={(detail) => componentEffectProblem(definition.kind, detail)}
            disabled={!editable}
            onChoose={(detail) => {
              const recipeId = store.addComponentEffect(detail);
              if (!recipeId) return false;
              setSelectedId(recipeId);
              return true;
            }}
          />
        </>}
        </>
      )}
      {selected && (
        <RecipeFields
          key={selected.recipeId}
          definition={definition}
          recipe={selected}
          {...lookup(selected)}
          issues={issuesOf(selected.recipeId)}
          onChange={(patch) => edit(updateRecipe(definition, selected.recipeId, patch))}
          disabled={!editable}
          sourceName={selected.configurationId === null ? undefined : props.configurations?.find((c) => c.configurationId === selected.configurationId)?.name ?? (props.configurations ? null : undefined)}
        />
      )}
    </div>
  );
}

/** “加效果”：列出模板的包和导入过的包里的控制器、图导出；这个用途用不了的列出但不能选，写明原因。 */
function AddEffect(props: {
  definition: FullTemplateDefinition;
  draft: TemplateDraft;
  catalog: ReadonlyMap<string, EffectPackageInspectResponse>;
  disabled: boolean;
  onAdd: (pkg: EffectPackageInspectResponse, exp: EffectPackageExport) => void;
}) {
  const { definition, catalog } = props;
  const seen = new Set<string>();
  const choices = [...definition.packages, ...props.draft.imported].flatMap((reference) => {
    const key = referenceKey(reference);
    const pkg = catalog.get(key);
    if (!pkg || seen.has(key)) return [];
    seen.add(key);
    return effectExports(pkg).map((exp) => ({ value: `${key}\u0001${exp.id}`, pkg, exp, problem: exportProblem(definition.kind, exp) }));
  });
  const usable = choices.filter((c) => !c.problem);
  const [picked, setPicked] = useState<string | null>(null);
  const current = usable.find((c) => c.value === picked) ?? usable[0];
  if (choices.length === 0) return <p className="hint add-effect">要加效果，先在上面导入含控制器或图导出的效果包。</p>;
  return (
    <div className="add-effect">
      <Select
        label="要加的效果"
        value={current?.value ?? ''}
        options={choices.map((c) => ({ value: c.value, label: `${c.exp.name}（${c.pkg.name}）${c.problem ? ` · ${c.problem}` : ''}`, disabled: !!c.problem }))}
        onChange={setPicked}
        disabled={props.disabled || usable.length === 0}
      />
      <mdui-button variant="tonal" disabled={props.disabled || !current} onClick={() => current && props.onAdd(current.pkg, current.exp)}>
        <mdui-icon-add slot="icon" />
        加效果
      </mdui-button>
    </div>
  );
}

function RecipeFields(props: {
  definition: FullTemplateDefinition;
  recipe: TemplateEffectRecipe;
  pkg: EffectPackageInspectResponse | undefined;
  exp: EffectPackageExport | undefined;
  issues: readonly DefinitionIssue[];
  onChange: (patch: Partial<Omit<TemplateEffectRecipe, 'recipeId'>>) => void;
  disabled: boolean;
  sourceName: string | null | undefined;
}) {
  const { definition, recipe, pkg, exp, disabled } = props;
  const overlay = exp ? hasOverlay(exp) : true;
  const whole = props.issues.filter((i) => !i.parameterId);
  return (
    <section className="recipe-fields" data-recipe={recipe.recipeId} aria-label={`效果“${recipe.name}”`}>
      <h3 className="template-advanced-title">{recipe.name}</h3>
      <div className="recipe-grid">
        <CommitField label="名称" value={recipe.name} validate={(v) => (v.trim() ? undefined : '名称不能为空。')} onCommit={(name) => props.onChange({ name: name.trim() })} disabled={disabled} />
        <Select
          label="作用于"
          className="recipe-target"
          value={recipe.target}
          options={RECIPE_TARGETS.map((t) => {
            const problem = t === recipe.target ? undefined : targetProblem(definition.kind, t, exp);
            return { value: t, label: problem ? `${RECIPE_TARGET_LABEL[t]}（不可用）` : RECIPE_TARGET_LABEL[t], disabled: !!problem };
          })}
          onChange={(target) => props.onChange({ target })}
          disabled={disabled}
        />
        <Select
          label="叠放层"
          className="recipe-overlay"
          value={recipe.overlayLayer}
          options={OVERLAY_LAYERS.map((l) => ({ value: l, label: OVERLAY_LABEL[l], disabled: l !== 'package' && l !== recipe.overlayLayer && !overlay }))}
          onChange={(overlayLayer) => props.onChange({ overlayLayer })}
          disabled={disabled}
        />
        <Select
          label="读取范围"
          className="recipe-read-scope"
          value={recipe.readScope}
          options={READ_SCOPES.map((s) => ({ value: s, label: READ_SCOPE_LABEL[s] }))}
          onChange={(readScope) => props.onChange(withReadScope(recipe, readScope))}
          disabled={disabled}
        />
        {recipe.readScope === 'branchInput' && (
          <CommitField
            label="输入名"
            className="recipe-branch-input"
            value={recipe.branchInput ?? ''}
            placeholder="例如 翻译"
            helper="应用时再选对应的来源"
            validate={(v) => (v.trim() ? undefined : '要填输入名。')}
            onCommit={(v) => props.onChange({ branchInput: v.trim() })}
            disabled={disabled}
          />
        )}
        <CommitField
          label="固定随机种子"
          className="recipe-seed"
          value={recipe.fixedSeed ?? ''}
          placeholder="留空表示不固定"
          helper="留空时每次应用各取一个"
          validate={(v) => (v.trim() === '' ? undefined : seedProblem(v.trim()))}
          onCommit={(v) => props.onChange({ fixedSeed: v.trim() === '' ? null : v.trim().replace(/^0+(?=\d)/, '') })}
          disabled={disabled}
        />
      </div>
      {!overlay && <p className="hint">这个效果没有图元输出，叠放层只能“随包”。</p>}
      {recipe.configurationId !== null && <p className="hint">{props.sourceName === undefined ? '正在读取效果组件来源…' : props.sourceName === null ? '来自效果组件（已删除）' : `来自效果组件“${props.sourceName}”`}</p>}
      {whole.map((issue) => (
        <p key={issue.message} className="notice-error">
          {issue.message}
        </p>
      ))}
      {pkg && exp ? (
        <ParameterFields
          pkg={pkg}
          exportId={recipe.exportId}
          parametersJson={recipe.parametersJson}
          onChange={(parametersJson) => props.onChange({ parametersJson })}
          issues={props.issues}
          disabled={disabled}
        />
      ) : (
        <p className="hint">{pkg ? `包“${pkg.name}”里没有导出 ${recipe.exportId}。` : '正在读取包信息…读不到时参数原样保留，保存由后端检查。'}</p>
      )}
    </section>
  );
}
