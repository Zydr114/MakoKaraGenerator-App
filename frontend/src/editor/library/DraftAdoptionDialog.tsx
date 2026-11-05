import { CommitDraftProvider } from '../ui/commit-drafts';
// 保留现有紧凑控件，只增加显式采用时的本地配置对话框。
import { shortRevision } from '../model/full-template';
import { Button, IconButton } from '../ui/Button';
import { Dialog } from '../ui/Dialog';
import { toast } from '../ui/feedback';
import { useObservable } from '../ui/hooks';
import { Note } from '../workspace/panel';
import type { DraftAdoptionModel } from './draft-adoption';
import type { DraftSourceModel } from './draft-source';
import { DraftConfigurationFields } from './DraftConfigurationFields';

export function DraftAdoptionDialog(props: { model: DraftAdoptionModel; source: DraftSourceModel; disabled: boolean }) {
  const { model, source } = props;
  const state = useObservable(model.state$);
  // dirty 与编译结果也有独立订阅；取消对话框不销毁源码状态。
  useObservable(source.state$);
  const candidate = state.candidate;
  const busy = props.disabled || state.preparing || candidate?.status === 'submitting';
  const exp = candidate?.metadata?.exports.find((item) => item.id === candidate.value.exportId);
  const issues = candidate?.rejected ? [...candidate.issues, candidate.rejected] : candidate?.issues ?? [];
  const stale = candidate?.status === 'stale';
  return <Dialog open={state.open} onClose={() => model.close()} title={candidate ? `采用修订并配置 · ${shortRevision(candidate.targetSourceRevision)}` : '准备采用修订'}
    description="配置先保留在本地。确认后用一次命令同时采用保存的固定源码与完整配置。"
    dismissable={!busy} className="draft-adoption-dialog"
    actions={<>
      <Button variant="text" isDisabled={busy} onPress={() => model.close()}>取消并保留输入</Button>
      {candidate && <Button variant="text" isDisabled={busy} onPress={() => void model.recompare()}>重读并重新比较</Button>}
      <Button variant="filled" isPending={candidate?.status === 'submitting'} isDisabled={busy || candidate?.status !== 'ready' || issues.length > 0 || model.hasInputDrafts || !model.compiled()}
        onPress={() => void model.submit().then((result) => {
          if (result.kind === 'adopted') toast(`已采用修订 ${shortRevision(result.revision)} 与完整配置`);
          else if (result.kind === 'failed') toast(result.message);
        })}>采用源码与配置</Button>
    </>}>
    {state.error && <Note error>{state.error}</Note>}
    {state.preparing && <Note>正在提交在用配置并准备固定候选…</Note>}
    {candidate && <>
      <Note>准备时在用 {shortRevision(candidate.baseInUseSourceRevision)}；目标 {shortRevision(candidate.targetSourceRevision)}。
        {model.compiled() ? ' 固定目标的编译门禁已满足。' : ' 尚未确认此固定目标编译通过，请先在源码页编译后重新比较。'}</Note>
      {source.dirty && <Note>源码编辑器还有未保存的文本。本次只采用已保存并编译的固定修订；编辑器中的文本、光标和撤销历史保留。</Note>}
      {candidate.error && <Note error>{candidate.error}</Note>}
      {candidate.status === 'loading' && <Note>正在读取目标源码的精确 metadata…</Note>}
      {candidate.status === 'failed' && !stale && <Button variant="text" size="sm" isDisabled={busy} onPress={() => void model.retryMetadata()}>重试目标 metadata</Button>}
      {stale && <Note>服务器草稿已变化。候选输入仍在；重读比较后再次明确确认。</Note>}
      <div className="ptabs-tools" aria-label="候选配置历史">
        <IconButton icon="undo" size="sm" label="撤销候选配置" isDisabled={busy || !model.canUndo} onPress={() => model.undo()} />
        <IconButton icon="redo" size="sm" label="重做候选配置" isDisabled={busy || !model.canRedo} onPress={() => model.redo()} />
      </div>
      {model.hasInputDrafts && <Note>输入框的原文已保留。请先按 Enter 或离开输入框提交有效值；无效输入须修正或按 Esc 撤回后才能采用。</Note>}
      <CommitDraftProvider entries={candidate.inputDrafts} set={(key, value) => model.setInputDraft(key, value)} clear={(prefix) => model.clearInputDrafts(prefix)}>
      <div className="draft-config" aria-label="采用候选配置" data-source-revision={candidate.targetSourceRevision}>
        <DraftConfigurationFields value={candidate.value} onChange={(value) => model.change(value)}
          pkg={candidate.metadata} kind={exp?.kind === 'material' ? 'Material' : 'Effect'} issues={issues}
          checking={candidate.status === 'loading'} disabled={busy || candidate.status === 'loading'} adoption />
      </div>
      </CommitDraftProvider>
    </>}
  </Dialog>;
}
