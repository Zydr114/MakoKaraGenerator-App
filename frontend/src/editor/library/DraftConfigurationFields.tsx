import { useCommitDraftScope } from '../ui/commit-drafts';
// 作者草稿的配置页签（W8 设计 §2.5）：名称、导出、作用于、叠放层、读取范围与输入名、固定随机种子、参数；材质组件只有名称、导出和参数。
// 控件提交后由 DraftConfigEditor 合并提交到草稿；撤销与重做是页面内的快照栈。

import type { EffectPackageSchema, ScriptConfigurationKind } from '../../protocol';
import { seedProblem } from '../model/effect-params';
import { componentTargetProblem, hasOverlay, OVERLAY_LABEL, OVERLAY_LAYERS, READ_SCOPE_LABEL, READ_SCOPES, RECIPE_TARGET_LABEL, RECIPE_TARGETS } from '../model/effect-recipes';
import { parseObjectEntries } from '../model/json-text';
import { sameKindExports, withExport, type ConfigIssue, type EditableConfiguration } from '../model/script-components';
import { confirmAction } from '../ui/dialogs';
import { CommitField, Select, TextField } from '../ui/fields';
import { Group, Note } from '../workspace/panel';
import { ParameterFields } from './ParameterFields';

/** 受控紧凑字段；普通配置的自动 PUT 与采用候选的本地状态由调用方分别持有。 */
export function DraftConfigurationFields(props: {
  value: EditableConfiguration;
  onChange: (value: EditableConfiguration) => void;
  pkg: EffectPackageSchema | undefined;
  kind: ScriptConfigurationKind;
  issues: ConfigIssue[];
  checking: boolean;
  disabled: boolean;
  adoption?: boolean;
}) {
  const { value, pkg, kind, disabled, issues } = props;
  const draftScope = useCommitDraftScope();
  const material = kind === 'Material';
  const exp = pkg?.exports.find((e) => e.id === value.exportId);
  const choices = pkg ? props.adoption ? pkg.exports.filter((e) => e.kind === 'material' || e.kind === 'controller' || e.kind === 'graph') : sameKindExports(pkg, kind) : [];
  const overlay = exp ? hasOverlay(exp) : true;
  const change = (patch: Partial<EditableConfiguration>) => props.onChange({ ...value, ...patch });

  const changeExport = async (exportId: string) => {
    const next = choices.find((e) => e.id === exportId);
    if (!next || next.id === value.exportId) return;
    if (hasOverrides(value.parametersJson) || (draftScope && Object.keys(draftScope.entries).some((key) => key.startsWith(`${draftScope.prefix}/parameter/`)))) {
      const ok = await confirmAction({ headline: `换成导出“${next.name}”？`, description: '换导出后，改过的参数都恢复默认。', confirmText: '换导出' });
      if (!ok) return;
    }
    draftScope?.clear(`${draftScope.prefix}/parameter/`);
    props.onChange(props.adoption ? { ...value, exportId: next.id, parametersJson: '{}' } : withExport(value, kind, next));
  };

  const inside = { labelPlacement: 'inside', size: 'sm' } as const;
  return (
    <>
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
          options={[...(choices.some((e) => e.id === value.exportId) ? [] : [{ value: value.exportId, label: `${value.exportId}（目标包缺少此导出）` }]), ...choices.map((e) => ({ value: e.id, label: e.name }))]}
          onChange={(id) => void changeExport(id)}
          disabled={disabled || !pkg || choices.length === 0 || (choices.length === 1 && choices[0]?.id === value.exportId)}
        />
        {(!material || props.adoption) && (
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
              {(value.readScope === 'branchInput' || (props.adoption && value.branchInput !== null)) && (
                <CommitField
                  {...inside}
                  label="输入名"
                  className="recipe-branch-input"
                  value={value.branchInput ?? ''}
                  placeholder="例如 翻译"
                  validate={(v) => (value.readScope !== 'branchInput' || v.trim() ? undefined : '要填输入名。')}
                  onCommit={(v) => change({ branchInput: v.trim() || (value.readScope === 'branchInput' ? '' : null) })}
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
        {material && !props.adoption && <Note>材质组件只有参数；作用于和叠放层由使用它的模板决定。</Note>}
        {!material && !overlay && <Note>这个效果没有图元输出，叠放层只能“随包”。</Note>}
      </Group>
      <Group title="参数">
        {props.adoption && <details className="draft-parameter-json">
          <summary>参数 JSON 原文</summary>
          <TextField label="完整参数 JSON" value={value.parametersJson} rows={4}
            onChange={(parametersJson) => change({ parametersJson })} disabled={disabled}
            helper="数值按 JSON 原文保留。可以在这里修复旧参数、格式或目标包不再支持的值。" />
        </details>}
        {pkg && exp ? (
          <ParameterFields pkg={pkg} exportId={value.exportId} parametersJson={value.parametersJson} onChange={(parametersJson) => change({ parametersJson })} issues={issues} disabled={disabled} />
        ) : (
          <Note>
            {pkg
              ? `固定包里没有导出 ${value.exportId}。`
              : props.checking
                ? '正在读取包信息…'
                : '当前在用修订尚无精确包元数据，导出、作用范围、叠放层和参数暂不编辑；已保存原值保留。'}
          </Note>
        )}
      </Group>
    </>
  );
}

function hasOverrides(parametersJson: string): boolean {
  try {
    return parseObjectEntries(parametersJson).size > 0;
  } catch {
    return true;
  }
}
