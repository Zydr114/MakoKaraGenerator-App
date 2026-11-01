// 作者草稿的详情（W8 设计 §2.4、§2.5、§2.8）：是否已保存到组件库、保存与另存为、关闭；本页尚未提供跨客户端草稿恢复。
// 上面是草稿的试样画布（每个草稿一个预览会话，改动后换输入），下面是配置、源码和验证页签。

import { useEffect, useState } from 'react';
import type { DraftTab, FullDraft } from './author-draft-store';
import { draftSaveState, type AuthorDraftStore, type DraftsState } from './author-draft-store';
import { compiledOk, componentKindText } from '../model/script-components';
import { shortRevision } from '../model/full-template';
import { confirmAction } from '../ui/legacy/dialogs';
import { toast } from '../ui/legacy/feedback';
import { Tabs } from '../ui/legacy/fields';
import type { ComponentState } from './component-library-store';
import { ComponentNameDialog, SAVE_STATE_TEXT, type NameDialog } from './ComponentsPane';
import type { DraftConfigEditor } from './draft-config-editor';
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
  const tab = drafts.tab(draft.draftId);
  const saveState = draftSaveState(draftsState, draft);
  const origin = draftsState.origins[draft.draftId];
  const originConfig = origin ? draftsState.originConfigs[origin] : undefined;
  const pkg = draftsState.packages[draft.draftId];
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
    if (saveState !== 'saved' || editor?.unsettled || source?.dirty) {
      const ok = await confirmAction({ headline: `关闭草稿“${configuration.name}”？`, description: '草稿里的修改没有保存到组件库，关闭后找不回来。', confirmText: '关闭' });
      if (!ok) return;
    }
    setClosing(true);
    if (await drafts.close(draft.draftId)) toast(`已关闭草稿“${configuration.name}”`);
    else setClosing(false);
  };
  const saveAs = () => setNameDialog({ action: 'saveAs', id: draft.draftId, initial: editor?.state.value.name ?? configuration.name });

  return (
    <div className="template-detail component-detail draft-detail" data-draft-id={draft.draftId} data-save-state={saveState}>
      <div className="template-header">
        <h2 className="template-name">{configuration.name}</h2>
        <div className="chips">
          <span className="tag">
            草稿 · {componentKindText(configuration.kind)} · {SAVE_STATE_TEXT[saveState]}
          </span>
        </div>
        <div className="template-actions">
          {originConfig ? (
            <mdui-button variant="filled" loading={draftsState.busy} disabled={busy || saveState !== 'unsaved'} onClick={() => void save()}>
              保存到“{originConfig.name}”
            </mdui-button>
          ) : (
            <mdui-button variant="filled" disabled={busy} onClick={saveAs}>
              另存为新组件…
            </mdui-button>
          )}
          <mdui-button variant="text" disabled={busy} onClick={() => void close()}>
            关闭
          </mdui-button>
          <mdui-dropdown placement="bottom-end">
            <mdui-button-icon slot="trigger" aria-label="草稿操作">
              <mdui-icon-more-vert />
            </mdui-button-icon>
            <mdui-menu>
              <mdui-menu-item disabled={busy} onClick={saveAs}>
                另存为新组件…
              </mdui-menu-item>
            </mdui-menu>
          </mdui-dropdown>
        </div>
      </div>
      <p className="hint draft-scope">当前页面未提供草稿恢复；请及时保存到组件库，已保存组件仍可用。</p>
      {saveState === 'orphan' && <p className="hint">{origin ? '来源组件已经不在组件库里了' : '不知道这个草稿来自哪个组件'}，只能另存为新组件。</p>}
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
      <p className="hint draft-package">
        {pkg ? `${pkg.name} ${pkg.version} · ` : ''}
        {configuration.packageId} · 在用修订 {shortRevision(configuration.sourceRevision)}
        {configuration.sourceRevision === draft.baseSourceRevision ? '（原包）' : ''}
      </p>
      <Tabs value={tab} options={DRAFT_TABS} onChange={(next) => drafts.setTab(draft.draftId, next)} className="draft-tabs" />
      {tab === 'config' && editor && <DraftConfigTab draft={draft} editor={editor} pkg={pkg} checking={saveState === 'checking'} disabled={busy} />}
      {tab === 'source' && source && <DraftSourceTab key={draft.draftId} draft={draft} model={source} disabled={busy} />}
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
