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
import { Button, IconButton } from '../ui/Button';
import { Switch } from '../ui/choices';
import { cls } from '../ui/cls';
import { CommitField, Select } from '../ui/fields';
import { Icon } from '../ui/Icon';
import { Group, Note } from '../workspace/panel';
import { PackagesPanel } from './PackagesPanel';
import { ParameterFields } from './ParameterFields';
import type { TemplateDraft, TemplateLibraryStore } from './template-library-store';
import type { ComponentLibraryStore } from './component-library-store';
import { ComponentPicker } from './ComponentPicker';

/** 效果页签（设计稿 #library-fx）：配方列表与加效果，选中一项的设置与参数，模板里的效果包。 */
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
  const picker = draft && props.components && (
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
  );

  return (
    <div className="effects-tab">
      <Group title="效果" hint={recipes.length > 1 ? '从上到下依次叠加' : undefined} className="recipe-group" extra={picker}>
        {recipes.length === 0 && <Note>这个模板没有效果。{draft ? '从下面包里的导出，或从效果组件加一个。' : ''}</Note>}
        {recipes.length > 0 && (
          <ul className="rlist recipe-list" aria-label="效果列表">
            {recipes.map((r, index) => {
              const active = r.recipeId === selected?.recipeId;
              const problem = issuesOf(r.recipeId).length > 0;
              return (
                <li
                  key={r.recipeId}
                  className={cls('rcp', 'recipe-item', active && 'selected', !r.enabled && 'off')}
                  data-recipe={r.recipeId}
                  data-selected={active}
                  onClick={() => setSelectedId(r.recipeId)}
                >
                  <Icon name={problem ? 'error' : 'wand_stars'} className={problem ? 'recipe-problem-icon' : 'recipe-icon'} />
                  <button type="button" className="rcp-text recipe-item-text" aria-pressed={active} onClick={() => setSelectedId(r.recipeId)}>
                    <b className="recipe-name">{r.name}</b>
                    <span className="small">
                      {RECIPE_TARGET_LABEL[r.target]}，{OVERLAY_LABEL[r.overlayLayer]}
                      {r.enabled ? '' : '，已停用'}
                    </span>
                  </button>
                  {draft && active ? (
                    <span className="rtools recipe-actions">
                      <IconButton icon="arrow_upward" size="sm" label={`上移 ${r.name}`} isDisabled={!editable || index === 0} onPress={() => edit(moveRecipe(definition, r.recipeId, -1))} />
                      <IconButton
                        icon="arrow_downward"
                        size="sm"
                        label={`下移 ${r.name}`}
                        isDisabled={!editable || index === recipes.length - 1}
                        onPress={() => edit(moveRecipe(definition, r.recipeId, 1))}
                      />
                      <IconButton
                        icon="content_copy"
                        size="sm"
                        label={`复制 ${r.name}`}
                        isDisabled={!editable}
                        onPress={() => {
                          const copy = duplicateRecipe(definition, r.recipeId);
                          edit(copy.definition);
                          setSelectedId(copy.recipeId);
                        }}
                      />
                      <IconButton icon="delete" size="sm" label={`删除 ${r.name}`} isDisabled={!editable} onPress={() => edit(removeRecipe(definition, r.recipeId))} />
                    </span>
                  ) : (
                    <span />
                  )}
                  <Switch label={`启用 ${r.name}`} hideLabel checked={r.enabled} onChange={(enabled) => edit(updateRecipe(definition, r.recipeId, { enabled }))} disabled={!editable} />
                </li>
              );
            })}
          </ul>
        )}
        {draft && (
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
        )}
      </Group>
      {selected && (
        <RecipeFields
          key={selected.recipeId}
          definition={definition}
          recipe={selected}
          {...lookup(selected)}
          issues={issuesOf(selected.recipeId)}
          onChange={(patch) => edit(updateRecipe(definition, selected.recipeId, patch))}
          disabled={!editable}
          sourceName={
            selected.configurationId === null
              ? undefined
              : (props.configurations?.find((c) => c.configurationId === selected.configurationId)?.name ?? (props.configurations ? null : undefined))
          }
        />
      )}
      <PackagesPanel store={store} definition={definition} draft={draft} catalog={catalog} disabled={props.disabled} />
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
  if (choices.length === 0) return <Note className="add-effect">要加效果，先在下面导入含控制器或图导出的效果包。</Note>;
  return (
    <div className="addfx add-effect">
      <Select
        label="要加的效果"
        labelPlacement="none"
        size="sm"
        value={current?.value ?? ''}
        options={choices.map((c) => ({ value: c.value, label: `${c.exp.name}（${c.pkg.name}）`, disabled: !!c.problem, ...(c.problem ? { description: c.problem } : {}) }))}
        onChange={setPicked}
        disabled={props.disabled || usable.length === 0}
      />
      <Button variant="tonal" size="sm" icon="add" isDisabled={props.disabled || !current} onPress={() => current && props.onAdd(current.pkg, current.exp)}>
        加效果
      </Button>
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
  const inside = { labelPlacement: 'inside', size: 'sm' } as const;
  return (
    <>
      <Group
        title={recipe.name}
        hint={pkg ? `包“${pkg.name}”的导出 ${exp?.name ?? recipe.exportId}` : undefined}
        className="recipe-fields"
        data-recipe={recipe.recipeId}
      >
        <CommitField
          label="名称"
          labelPlacement="side"
          size="sm"
          className="recipe-name-field"
          value={recipe.name}
          validate={(v) => (v.trim() ? undefined : '名称不能为空。')}
          onCommit={(name) => props.onChange({ name: name.trim() })}
          disabled={disabled}
        />
        <div className="g2">
          <Select
            {...inside}
            label="作用于"
            className="recipe-target"
            value={recipe.target}
            options={RECIPE_TARGETS.map((t) => {
              const problem = t === recipe.target ? undefined : targetProblem(definition.kind, t, exp);
              return { value: t, label: RECIPE_TARGET_LABEL[t], disabled: !!problem, ...(problem ? { description: problem } : {}) };
            })}
            onChange={(target) => props.onChange({ target })}
            disabled={disabled}
          />
          <Select
            {...inside}
            label="叠放层"
            className="recipe-overlay"
            value={recipe.overlayLayer}
            options={OVERLAY_LAYERS.map((l) => ({ value: l, label: OVERLAY_LABEL[l], disabled: l !== 'package' && l !== recipe.overlayLayer && !overlay }))}
            onChange={(overlayLayer) => props.onChange({ overlayLayer })}
            disabled={disabled}
          />
          <Select
            {...inside}
            label="读取"
            className="recipe-read-scope"
            value={recipe.readScope}
            options={READ_SCOPES.map((s) => ({ value: s, label: READ_SCOPE_LABEL[s] }))}
            onChange={(readScope) => props.onChange(withReadScope(recipe, readScope))}
            disabled={disabled}
          />
          <CommitField
            {...inside}
            label="种子"
            className="recipe-seed"
            value={recipe.fixedSeed ?? ''}
            placeholder="不固定"
            validate={(v) => (v.trim() === '' ? undefined : seedProblem(v.trim()))}
            onCommit={(v) => props.onChange({ fixedSeed: v.trim() === '' ? null : v.trim().replace(/^0+(?=\d)/, '') })}
            disabled={disabled}
          />
          {recipe.readScope === 'branchInput' && (
            <CommitField
              {...inside}
              label="输入名"
              className="recipe-branch-input span2"
              value={recipe.branchInput ?? ''}
              placeholder="例如 翻译"
              helper="应用时再选对应的来源"
              validate={(v) => (v.trim() ? undefined : '要填输入名。')}
              onCommit={(v) => props.onChange({ branchInput: v.trim() })}
              disabled={disabled}
            />
          )}
        </div>
        <Note>种子留空时，每次应用各取一个。</Note>
        {!overlay && <Note>这个效果没有图元输出，叠放层只能“随包”。</Note>}
        {recipe.configurationId !== null && (
          <Note>{props.sourceName === undefined ? '正在读取效果组件来源…' : props.sourceName === null ? '来自效果组件（已删除）' : `来自效果组件“${props.sourceName}”`}</Note>
        )}
        {whole.map((issue) => (
          <Note key={issue.message} error>
            {issue.message}
          </Note>
        ))}
      </Group>
      <Group title="参数" className="recipe-params">
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
          <Note>{pkg ? `包“${pkg.name}”里没有导出 ${recipe.exportId}。` : '正在读取包信息…读不到时参数原样保留，保存由后端检查。'}</Note>
        )}
      </Group>
    </>
  );
}
