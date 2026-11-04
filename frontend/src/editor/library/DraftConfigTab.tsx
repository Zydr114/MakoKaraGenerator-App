// 作者草稿的配置页签（W8 设计 §2.5）：名称、导出、作用于、叠放层、读取范围与输入名、固定随机种子、参数；材质组件只有名称、导出和参数。
// 控件提交后由 DraftConfigEditor 合并提交到草稿；撤销与重做是页面内的快照栈。

import type { EffectPackageInspectResponse } from '../../protocol';
import { seedProblem } from '../model/effect-params';
import { componentTargetProblem, hasOverlay, OVERLAY_LABEL, OVERLAY_LAYERS, READ_SCOPE_LABEL, READ_SCOPES, RECIPE_TARGET_LABEL, RECIPE_TARGETS } from '../model/effect-recipes';
import { parseObjectEntries } from '../model/json-text';
import { sameKindExports, withExport, type EditableConfiguration } from '../model/script-components';
import { confirmAction } from '../ui/dialogs';
import { CommitField, Select } from '../ui/fields';
import { useObservable } from '../ui/hooks';
import { Group, Note } from '../workspace/panel';
import type { FullDraft } from './author-draft-store';
import type { DraftConfigEditor } from './draft-config-editor';
import { ParameterFields } from './ParameterFields';

export function DraftConfigTab(props: {
  draft: FullDraft;
  editor: DraftConfigEditor;
  pkg: EffectPackageInspectResponse | undefined;
  /** 来源组件还在读（包信息也许马上就有）。 */
  checking: boolean;
  disabled: boolean;
}) {
  const { draft, editor, pkg, disabled } = props;
  const state = useObservable(editor.state$);
  const { value } = state;
  const kind = draft.configuration.kind;
  const material = kind === 'Material';
  const exp = pkg?.exports.find((e) => e.id === value.exportId);
  const choices = pkg ? sameKindExports(pkg, kind) : [];
  const issues = state.rejected ? [...state.issues, state.rejected] : state.issues;
  const overlay = exp ? hasOverlay(exp) : true;
  const change = (patch: Partial<EditableConfiguration>) => editor.change({ ...value, ...patch });

  const changeExport = async (exportId: string) => {
    const next = choices.find((e) => e.id === exportId);
    if (!next || next.id === value.exportId) return;
    if (hasOverrides(value.parametersJson)) {
      const ok = await confirmAction({ headline: `换成导出“${next.name}”？`, description: '换导出后，改过的参数都恢复默认。', confirmText: '换导出' });
      if (!ok) return;
    }
    editor.change(withExport(value, kind, next));
  };

  const inside = { labelPlacement: 'inside', size: 'sm' } as const;
  return (
    <section className="draft-config" aria-label="配置" data-pending={state.pending ? '' : undefined}>
      <p className="pintro">{state.pending ? '正在提交到草稿…' : '改动会自动提交到草稿；保存到组件库才会长期保留。'}</p>
      <Group title="组件">
        {issues
          .filter((i) => !i.parameterId)
          .map((issue) => (
            <p key={`${issue.field ?? ''}|${issue.message}`} className="note err draft-config-problem" data-field={issue.field} role="alert">
              {issue.message}
            </p>
          ))}
        <CommitField
          label="名称"
          labelPlacement="side"
          size="sm"
          className="draft-name"
          value={value.name}
          validate={(v) => (v.trim() ? undefined : '名称不能为空。')}
          onCommit={(name) => change({ name: name.trim() })}
          disabled={disabled}
        />
        <Select
          label="导出"
          labelPlacement="side"
          size="sm"
          className="draft-export"
          value={value.exportId}
          options={choices.some((e) => e.id === value.exportId) ? choices.map((e) => ({ value: e.id, label: e.name })) : [{ value: value.exportId, label: value.exportId }]}
          onChange={(id) => void changeExport(id)}
          disabled={disabled || !pkg || choices.length < 2}
        />
        {!material && (
          <>
            <div className="g2">
              <Select
                {...inside}
                label="作用于"
                className="recipe-target"
                value={value.relativeTarget}
                options={RECIPE_TARGETS.map((t) => {
                  const problem = t === value.relativeTarget ? undefined : componentTargetProblem(t, exp);
                  return { value: t, label: RECIPE_TARGET_LABEL[t], disabled: !!problem, ...(problem ? { description: problem } : {}) };
                })}
                onChange={(relativeTarget) => change({ relativeTarget })}
                disabled={disabled || !pkg}
              />
              <Select
                {...inside}
                label="叠放层"
                className="recipe-overlay"
                value={value.overlayLayer}
                options={OVERLAY_LAYERS.map((l) => ({ value: l, label: OVERLAY_LABEL[l], disabled: l !== 'package' && l !== value.overlayLayer && !overlay }))}
                onChange={(overlayLayer) => change({ overlayLayer })}
                disabled={disabled || !pkg}
              />
              <Select
                {...inside}
                label="读取"
                className="recipe-read-scope"
                value={value.readScope}
                options={READ_SCOPES.map((s) => ({ value: s, label: READ_SCOPE_LABEL[s] }))}
                onChange={(readScope) => change({ readScope, branchInput: readScope === 'branchInput' ? (value.branchInput ?? '') : null })}
                disabled={disabled}
              />
              {value.readScope === 'branchInput' && (
                <CommitField
                  {...inside}
                  label="输入名"
                  className="recipe-branch-input"
                  value={value.branchInput ?? ''}
                  placeholder="例如 翻译"
                  validate={(v) => (v.trim() ? undefined : '要填输入名。')}
                  onCommit={(v) => change({ branchInput: v.trim() })}
                  disabled={disabled}
                />
              )}
            </div>
            <CommitField
              label="随机种子"
              labelPlacement="side"
              size="sm"
              className="recipe-seed"
              value={value.fixedSeed ?? ''}
              placeholder="留空表示不固定"
              validate={(v) => (v.trim() === '' ? undefined : seedProblem(v.trim()))}
              onCommit={(v) => change({ fixedSeed: v.trim() === '' ? null : v.trim().replace(/^0+(?=\d)/, '') })}
              disabled={disabled}
            />
            {value.readScope === 'branchInput' && <Note>输入名在用到模板里、应用时再选对应的来源。</Note>}
          </>
        )}
        {material && <Note>材质组件只有参数；作用于和叠放层由使用它的模板决定。</Note>}
        {!material && !overlay && <Note>这个效果没有图元输出，叠放层只能“随包”。</Note>}
      </Group>
      <Group title="参数">
        {pkg && exp ? (
          <ParameterFields pkg={pkg} exportId={value.exportId} parametersJson={value.parametersJson} onChange={(parametersJson) => change({ parametersJson })} issues={issues} disabled={disabled} />
        ) : (
          <Note>
            {pkg
              ? `包“${pkg.name}”里没有导出 ${value.exportId}。`
              : props.checking
                ? '正在读取包信息…'
                : '当前在用修订尚无精确包元数据，导出、作用范围、叠放层和参数暂不编辑；已保存原值保留。'}
          </Note>
        )}
      </Group>
    </section>
  );
}

function hasOverrides(parametersJson: string): boolean {
  try {
    return parseObjectEntries(parametersJson).size > 0;
  } catch {
    return true;
  }
}
