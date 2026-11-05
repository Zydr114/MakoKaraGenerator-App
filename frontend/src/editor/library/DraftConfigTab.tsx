import { CommitDraftProvider } from '../ui/commit-drafts';
// 普通在用配置继续由独立编辑器自动合并提交；候选复用字段而不复用队列。
import type { EffectPackageSchema } from '../../protocol';
import { Button } from '../ui/Button';
import { Note } from '../workspace/panel';
import { useObservable } from '../ui/hooks';
import type { FullDraft } from './author-draft-store';
import type { DraftConfigEditor } from './draft-config-editor';
import { DraftConfigurationFields } from './DraftConfigurationFields';

export function DraftConfigTab(props: {
  draft: FullDraft;
  editor: DraftConfigEditor;
  pkg: EffectPackageSchema | undefined;
  checking: boolean;
  disabled: boolean;
  onCompareAdoption: () => void;
}) {
  const state = useObservable(props.editor.state$);
  const issues = state.rejected ? [...state.issues, state.rejected] : state.issues;
  return <section className="draft-config" aria-label="配置" data-pending={state.pending ? '' : undefined}>
    {state.adoptionUncertain && <Note error>采用结果待比较；普通配置保持冻结。
      <Button variant="text" size="sm" isDisabled={props.disabled} onPress={props.onCompareAdoption}>重读采用结果并比较</Button>
    </Note>}
    <p className="pintro">{state.pending ? '正在提交到草稿…' : '改动会自动提交到草稿；保存到组件库才会长期保留。'}</p>
    {Object.keys(state.inputDrafts).length > 0 && <details className="draft-input-drafts">
      <summary>未提交的输入原文（已保留）</summary>
      {Object.entries(state.inputDrafts).map(([key, draft]) => <p key={key} className="note">
        <code>{key.split('/').at(-1)}</code>：<code>{draft.text}</code>{draft.problem ? ` · ${draft.problem}` : ''}
        <Button variant="text" size="sm" isDisabled={props.disabled} onPress={() => props.editor.setInputDraft(key, undefined)}>撤回此输入</Button>
      </p>)}
    </details>}
    <CommitDraftProvider entries={state.inputDrafts} set={(key, value) => props.editor.setInputDraft(key, value)} clear={(prefix) => props.editor.clearInputDrafts(prefix)}>
    <DraftConfigurationFields value={state.value} onChange={(value) => props.editor.change(value)} pkg={props.pkg}
      kind={props.draft.configuration.kind} issues={issues} checking={props.checking} disabled={props.disabled || !!state.adoptionUncertain} />
    </CommitDraftProvider>
  </section>;
}
