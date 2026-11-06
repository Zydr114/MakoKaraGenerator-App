// 作者草稿的详情（W8 设计 §2.4、§2.5、§2.8）：是否已保存到组件库、保存与另存为、关闭；跨客户端恢复见左栏“可恢复的草稿”。
// 上面是草稿的试样画布（每个草稿一个预览会话，改动后换输入），下面是配置、源码和验证页签。

import { useEffect, useState } from 'react';
import type { DraftTab, FullDraft } from './author-draft-store';
import { draftSaveState, isV2AuthorDraft, type AuthorDraftStore, type DraftsState } from './author-draft-store';
import { compiledOk, componentKindText } from '../model/script-components';
import { shortRevision } from '../model/full-template';
import { Button, IconButton } from '../ui/Button';
import { confirmAction } from '../ui/dialogs';
import { toast } from '../ui/feedback';
import { useObservable } from '../ui/hooks';
import { Tag } from '../ui/indicators';
import { Tabs } from '../ui/Tabs';
import { Note } from '../workspace/panel';
import type { ComponentState } from './component-library-store';
import { ComponentNameDialog, SAVE_STATE_TEXT, type NameDialog } from './ComponentsPane';
import type { DraftConfigEditor } from './draft-config-editor';
import { DraftAdoptionDialog } from './DraftAdoptionDialog';
import { DraftConfigTab } from './DraftConfigTab';
import type { DraftSourceModel } from './draft-source';
import { DraftSourceTab } from './DraftSourceTab';
import { DraftValidationTab } from './DraftValidationTab';
import { SampleArea } from './TemplateThumb';
import type { TemplateWorkshop } from './template-workshop';

const DRAFT_TABS: readonly { value: DraftTab; label: string }[] = [
  { value: 'config', label: '配置' },
  { value: 'source', label: '源码' },
  { value: 'validation', label: '验证' },
];

export function DraftDetail(props: { state: ComponentState; drafts: AuthorDraftStore; draftsState: DraftsState; draft: FullDraft; workshop: TemplateWorkshop | null }) {
  const { drafts, draftsState, draft, workshop } = props;
  const [nameDialog, setNameDialog] = useState<NameDialog | null>(null);
  /** 正在关闭：先关掉试样会话，免得重画时又给要删的草稿开会话。 */
  const [closing, setClosing] = useState(false);
  const { configuration } = draft;
  const editor = drafts.configEditor(draft.draftId);
  const source = drafts.sourceModel(draft.draftId);
  const validation = drafts.validationModel(draft.draftId);
  const adoption = drafts.adoptionModel(draft.draftId);
  const tab = drafts.tab(draft.draftId);
  const saveState = draftSaveState(draftsState, draft);
  const origin = draftsState.origins[draft.draftId];
  const originConfig = origin ? draftsState.originConfigs[origin] : undefined;
  const metadata = draftsState.sourceMetadata[draft.draftId];
  const pkg = draftsState.packages[draft.draftId];
  const schema = pkg;
  const busy = draftsState.busy || props.state.busy;
  // 离开草稿时，没有别的详情接手画布（例如换成占位文字）就关掉它的试样会话。放到下一轮再看：
  // 接手的详情在同一轮里给画布换来源；同一轮里清空画布还会让它刚建的下拉框没初始化就被换掉。
  const scope = `draft|${draft.draftId}`;
  useEffect(() => () => void setTimeout(() => workshop?.release(scope), 0), [workshop, scope]);

  const save = async () => {
    if (!originConfig || !(await settle(editor)) || !(await confirmSourceEdits(source)) || !(await confirmUncompiled(draft))) return;
    if (await drafts.save(draft.draftId)) toast(`已保存到“${originConfig.name}”`);
  };
  const close = async () => {
    if (saveState !== 'saved' || editor?.unsettled || source?.dirty || adoption?.hasUnsaved) {
      const ok = await confirmAction({ headline: `关闭草稿“${configuration.name}”？`, description: '草稿里的修改没有保存到组件库，关闭后找不回来。', confirmText: '关闭', danger: true });
      if (!ok) return;
    }
    setClosing(true);
    if (await drafts.close(draft.draftId)) toast(`已关闭草稿“${configuration.name}”`);
    else setClosing(false);
  };
  const saveAs = () => setNameDialog({ action: 'saveAs', id: draft.draftId, initial: editor?.state.value.name ?? configuration.name });

  return (
    <div className="lib-detail draft-detail" data-draft-id={draft.draftId} data-save-state={saveState}
      data-author-source-revision={isV2AuthorDraft(draft, pkg) ? configuration.sourceRevision : undefined}>
      <header className="phead">
        <div className="phead-row">
          <h2 className="template-name">{configuration.name}</h2>
          <Tag>草稿</Tag>
          {saveState !== 'saved' && <Tag tone={saveState === 'checking' ? 'neutral' : 'warn'}>{SAVE_STATE_TEXT[saveState]}</Tag>}
          <span className="spacer" />
          <IconButton icon="close" size="sm" label="关闭草稿" isDisabled={busy} onPress={() => void close()} />
        </div>
        <div className="phead-row template-actions">
          <span className="meta draft-package" title={`${pkg ? `${pkg.name} ${pkg.version} · ` : ''}${configuration.packageId}`}>
            {isV2AuthorDraft(draft, pkg) ? 'SDK 2 · ' : ''}
            {componentKindText(configuration.kind)}，在用修订 {shortRevision(configuration.sourceRevision)}
            {configuration.sourceRevision === draft.baseSourceRevision ? '（原包）' : ''}
          </span>
          <span className="spacer" />
          {originConfig ? (
            <>
              <Button variant="text" size="sm" isDisabled={busy} onPress={saveAs}>
                另存为新组件…
              </Button>
              <Button variant="filled" size="sm" icon="save" isPending={draftsState.busy} isDisabled={busy || saveState !== 'unsaved'} onPress={() => void save()}>
                保存到组件库
              </Button>
            </>
          ) : (
            <Button variant="filled" size="sm" icon="save" isDisabled={busy} onPress={saveAs}>
              另存为新组件…
            </Button>
          )}
        </div>
      </header>
      <Note className="phead-note draft-scope">
        {originConfig ? `保存会更新组件“${originConfig.name}”。` : ''}草稿保存在本机，Server 重启或换标签页后可在“可恢复的草稿”里认领；仍请及时保存到组件库。
      </Note>
      {saveState === 'orphan' && <Note className="phead-note">{origin ? '来源组件已经不在组件库里了' : '不知道这个草稿来自哪个组件'}，只能另存为新组件。</Note>}
      {isV2AuthorDraft(draft, pkg) && <Note className="phead-note">
        最新源码 {shortRevision(draft.currentSourceRevision)}；试样请求与保存使用在用修订 {shortRevision(configuration.sourceRevision)}。
        {draft.currentSourceRevision !== configuration.sourceRevision ? ' 新源码须编译通过并采用后才用于试样。' : ''}
        {' SDK 2 试样和验证需要 Vulkan，请在顶栏明确选择 GPU。'}
      </Note>}
      {workshop && (
        <SampleArea
          workshop={workshop}
          source={closing ? null : { kind: 'authorDraft', draftId: draft.draftId, draftRevision: draft.draftRevision, authorSourceRevision: configuration.sourceRevision, options: draft.sampleOptions }}
          scope={scope}
          blocked={undefined}
          sourceNote=""
          failureLink={{ code: 'WorkerPackageCompilationFailed', label: '在源码页签查看', onClick: () => drafts.setTab(draft.draftId, 'source') }}
        />
      )}
      <div className="ptabs-row">
        <Tabs label="草稿编辑" value={tab} options={DRAFT_TABS} onChange={(next) => drafts.setTab(draft.draftId, next)} className="ptabs draft-tabs" />
        {tab === 'config' && editor && <ConfigUndo editor={editor} disabled={busy} />}
      </div>
      <div className="pbody" data-tab={tab}>
        {tab === 'config' && metadata?.status === 'failed' && <Note error>
          读取在用修订的配置声明失败：{metadata.error}
          <Button variant="text" size="sm" onPress={() => drafts.retrySourceMetadata(draft.draftId)}>重试读取</Button>
        </Note>}
        {tab === 'config' && editor && <DraftConfigTab draft={draft} editor={editor} pkg={schema} checking={metadata?.status === 'loading'} disabled={busy} onCompareAdoption={() => void drafts.readAdoptionComparison(draft.draftId).catch((error) => toast(error instanceof Error ? error.message : String(error)))} />}
        {tab === 'source' && source && <DraftSourceTab key={draft.draftId} draft={draft} model={source} disabled={busy} onPrepareAdoption={async (revision) => {
          const existing = adoption?.state.candidate;
          if (existing && existing.targetSourceRevision !== revision && !(await confirmAction({ headline: '准备另一个修订？', description: '当前采用候选的本地输入将由新候选替换。', confirmText: '准备新候选' }))) return;
          await adoption?.prepare(revision);
        }} />}
        {tab === 'validation' && validation && (
          <DraftValidationTab
            key={draft.draftId}
            draft={draft}
            model={validation}
            source={source}
            workshop={workshop}
            onShowSource={() => drafts.setTab(draft.draftId, 'source')}
            disabled={busy}
          />
        )}
      </div>
      {adoption && source && <DraftAdoptionDialog model={adoption} source={source} disabled={busy} />}
      <ComponentNameDialog
        dialog={nameDialog}
        busy={busy}
        onClose={() => setNameDialog(null)}
        onSubmit={async (_dialog, name) => {
          // 名称和页签里的不同时，先作为一步配置修改提交（能撤销），再新建组件。
          if (editor && editor.state.value.name !== name.trim()) editor.change({ ...editor.state.value, name: name.trim() });
          if (!(await settle(editor)) || !(await confirmSourceEdits(source)) || !(await confirmUncompiled(draft))) return;
          if (!(await drafts.saveAs(draft.draftId, name))) return;
          setNameDialog(null);
          toast(`已另存为“${name.trim()}”`);
        }}
      />
    </div>
  );
}

/** 配置页签的撤销与重做，放在页签条右边。 */
function ConfigUndo(props: { editor: DraftConfigEditor; disabled: boolean }) {
  const state = useObservable(props.editor.state$);
  return (
    <span className="ptabs-tools">
      <IconButton icon="undo" size="sm" label="撤销配置修改" isDisabled={props.disabled || !state.canUndo} onPress={() => props.editor.undo()} />
      <IconButton icon="redo" size="sm" label="重做配置修改" isDisabled={props.disabled || !state.canRedo} onPress={() => props.editor.redo()} />
    </span>
  );
}

/** 保存到组件库之前，把配置页签里还没提交的改动先提交；有问题时写明原因并停下。 */
async function settle(editor: DraftConfigEditor | undefined): Promise<boolean> {
  if (!editor || (await editor.flush())) return true;
  toast(editor.state.rejected?.message ?? '配置里还有问题，改好后再保存。');
  return false;
}

/** 源码页签里有没保存成修订的改动：存进组件库的是在用修订，这些改动不在里面。 */
async function confirmSourceEdits(source: DraftSourceModel | undefined): Promise<boolean> {
  if (!source?.dirty) return true;
  return confirmAction({
    headline: '源码还有改动没保存成修订',
    description: '保存到组件库的是在用修订，源码页签里没保存的改动不在里面。仍要保存吗？',
    confirmText: '仍然保存',
  });
}

/**
 * 保存前的检查（§2.4）：在用修订改过源码、又没有编译通过时先确认；后端不要求编译通过。
 * 在用修订就是原包时不问（原包的源码没改过，材质和图包也没有 C# 可编译，见 R10）。
 */
async function confirmUncompiled(draft: FullDraft): Promise<boolean> {
  if (draft.configuration.sourceRevision === draft.baseSourceRevision || compiledOk(draft)) return true;
  return confirmAction({
    headline: '在用修订还没有编译通过',
    description: '这个修订还没有编译通过，用到它的模板可能出不了画面。仍要保存吗？',
    confirmText: '仍然保存',
  });
}
