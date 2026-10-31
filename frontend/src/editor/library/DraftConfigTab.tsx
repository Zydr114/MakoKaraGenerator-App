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

  return (
    <section className="draft-config" aria-label="配置" data-pending={state.pending ? '' : undefined}>
      <div className="draft-config-bar">
        <span className="muted">{state.pending ? '正在提交到草稿…' : '改动会自动提交到草稿；保存到组件库才会长期保留。'}</span>
        <mdui-button-icon disabled={disabled || !state.canUndo} onClick={() => editor.undo()} aria-label="撤销配置修改">
          <mdui-icon-undo />
        </mdui-button-icon>
        <mdui-button-icon disabled={disabled || !state.canRedo} onClick={() => editor.redo()} aria-label="重做配置修改">
          <mdui-icon-redo />
        </mdui-button-icon>
      </div>
      {issues
        .filter((i) => !i.parameterId)
        .map((issue) => (
          <p key={`${issue.field ?? ''}|${issue.message}`} className="notice-error draft-config-problem" data-field={issue.field}>
            {issue.message}
          </p>
        ))}
      <div className="recipe-grid">
        <CommitField label="名称" value={value.name} validate={(v) => (v.trim() ? undefined : '名称不能为空。')} onCommit={(name) => change({ name: name.trim() })} disabled={disabled} />
        <Select
          label="导出"
          className="draft-export"
          value={value.exportId}
          options={choices.some((e) => e.id === value.exportId) ? choices.map((e) => ({ value: e.id, label: e.name })) : [{ value: value.exportId, label: value.exportId }]}
          onChange={(id) => void changeExport(id)}
          disabled={disabled || choices.length < 2}
        />
        {!material && (
          <>
            <Select
              label="作用于"
              className="recipe-target"
              value={value.relativeTarget}
              options={RECIPE_TARGETS.map((t) => {
                const problem = t === value.relativeTarget ? undefined : componentTargetProblem(t, exp);
                return { value: t, label: problem ? `${RECIPE_TARGET_LABEL[t]}（不可用）` : RECIPE_TARGET_LABEL[t], disabled: !!problem };
              })}
              onChange={(relativeTarget) => change({ relativeTarget })}
              disabled={disabled}
            />
            <Select
              label="叠放层"
              className="recipe-overlay"
              value={value.overlayLayer}
              options={OVERLAY_LAYERS.map((l) => ({ value: l, label: OVERLAY_LABEL[l], disabled: l !== 'package' && l !== value.overlayLayer && !overlay }))}
              onChange={(overlayLayer) => change({ overlayLayer })}
              disabled={disabled}
            />
            <Select
              label="读取范围"
              className="recipe-read-scope"
              value={value.readScope}
              options={READ_SCOPES.map((s) => ({ value: s, label: READ_SCOPE_LABEL[s] }))}
              onChange={(readScope) => change({ readScope, branchInput: readScope === 'branchInput' ? (value.branchInput ?? '') : null })}
              disabled={disabled}
            />
            {value.readScope === 'branchInput' && (
              <CommitField
                label="输入名"
                className="recipe-branch-input"
                value={value.branchInput ?? ''}
                placeholder="例如 翻译"
                helper="用到模板里、应用时再选对应的来源"
                validate={(v) => (v.trim() ? undefined : '要填输入名。')}
                onCommit={(v) => change({ branchInput: v.trim() })}
                disabled={disabled}
              />
            )}
            <CommitField
              label="固定随机种子"
              className="recipe-seed"
              value={value.fixedSeed ?? ''}
              placeholder="留空表示不固定"
              helper="留空时每次应用各取一个"
              validate={(v) => (v.trim() === '' ? undefined : seedProblem(v.trim()))}
              onCommit={(v) => change({ fixedSeed: v.trim() === '' ? null : v.trim().replace(/^0+(?=\d)/, '') })}
              disabled={disabled}
            />
          </>
        )}
      </div>
      {material && <p className="hint">材质组件只有参数；作用于和叠放层由使用它的模板决定。</p>}
      {!material && !overlay && <p className="hint">这个效果没有图元输出，叠放层只能“随包”。</p>}
      {pkg && exp ? (
        <ParameterFields pkg={pkg} exportId={value.exportId} parametersJson={value.parametersJson} onChange={(parametersJson) => change({ parametersJson })} issues={issues} disabled={disabled} />
      ) : (
        <p className="hint">
          {pkg
            ? `包“${pkg.name}”里没有导出 ${value.exportId}。`
            : props.checking
              ? '正在读取包信息…'
              : '读不到包信息（来源组件已不在组件库里），参数只能原样保留；另存为新组件后可以继续编辑参数。'}
        </p>
      )}
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
