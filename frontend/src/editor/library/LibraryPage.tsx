// 样式库页（整体设计 §5.5.1，W3 设计 §2.4、§2.5，W7 设计 §2.3）：左栏模板列表（带缩略图），右侧模板详情。
// 内置模板只读，“复制并编辑”到我的模板；我的模板按完整定义编辑，含效果的也可以。
// 详情区顶部是固定样本上的试样画面（G17，W4 设计 §9.2），草稿改动后跟着变。

import { useEffect, useMemo, useRef, useState } from 'react';
import type { EffectPackageInspectResponse, FullTemplateDefinition, ScriptConfigurationSummary, TemplateKind, TemplateLibraryId, TemplateMaterialSlot, TemplateSummary } from '../../protocol';
import { definitionIssues, issueWhereText, shortRevision, staticDefinition } from '../model/full-template';
import { resolveChoice } from '../model/render-device';
import { definitionProblem, groupAllowed, groupIncluded, groupSwatch, groupValueText, KIND_LIMIT_TEXT, propertySummary, TEMPLATE_GROUPS } from '../model/template-groups';
import { confirmAction } from '../ui/dialogs';
import { toast } from '../ui/feedback';
import { FileButton, Select, Tabs, TextField } from '../ui/fields';
import { FormDialog } from '../ui/FormDialog';
import { client, renderDevice, templateTransfers } from '../connection';
import { COMPONENT_LIBRARY_FEATURE, type ComponentLibraryStore } from './component-library-store';
import type { AuthorDraftStore } from './author-draft-store';
import type { DraftConfigEditor } from './draft-config-editor';
import { ComponentDetail, ComponentList, useComponentImport, type DetailProps } from './ComponentsPane';
import { DraftDetail } from './DraftDetail';
import { useDomEvent, useObservable } from '../ui/hooks';
import { isEditing } from '../workspace/Workspace';
import { EffectsTab } from './EffectsTab';
import { MaterialTab } from './MaterialTab';
import { TemplateEditor } from './TemplateEditor';
import type { TransferFeatures } from './template-transfers';
import { ExportTemplatesDialog, ImportTemplatesDialog } from './TransferDialogs';
import { checkTemplateName, filterTemplates, isAdvanced, KIND_TEXT, type LibraryState, type TemplateLibraryStore } from './template-library-store';
import { SampleArea, TemplateThumb } from './TemplateThumb';
import type { SampleSource, TemplateThumbnails } from './template-thumbnails';
import { TemplateWorkshop } from './template-workshop';

const KIND_OPTIONS: readonly { value: TemplateKind | 'all'; label: string }[] = [
  { value: 'all', label: '全部用途' },
  { value: 'line', label: KIND_TEXT.line },
  { value: 'token', label: KIND_TEXT.token },
  { value: 'composite', label: KIND_TEXT.composite },
];

/** 离开有未保存草稿的模板前确认（切换模板、离开页面共用）。 */
export async function confirmDiscardTemplate(store: TemplateLibraryStore): Promise<boolean> {
  if (!store.isDirty) return true;
  return confirmAction({ headline: '放弃未保存的模板修改？', description: '草稿只在本页，离开后不会保留。', confirmText: '放弃修改' });
}

type NameDialog = { action: 'copy'; libraryId: TemplateLibraryId; templateId: string; initial: string } | { action: 'rename'; templateId: string; initial: string };

/** 左栏页签（W8 设计 §2.2）：只是界面状态，在本标签页里记住上次选的。 */
type Section = 'templates' | 'components';
const SECTIONS: readonly { value: Section; label: string }[] = [
  { value: 'templates', label: '模板' },
  { value: 'components', label: '效果组件' },
];
const SECTION_KEY = 'mako.library-section';

function storedSection(): Section {
  try {
    return sessionStorage.getItem(SECTION_KEY) === 'components' ? 'components' : 'templates';
  } catch {
    return 'templates';
  }
}

/** 从外观页进来编辑副本时的返回栏（W7 设计 §2.7）。 */
export interface LibraryReturnBar {
  /** “来自 外观 · 主歌词 / 第 2 行”。 */
  text: string;
  onReturn: () => Promise<void>;
  onDismiss: () => void;
}

export function LibraryPage(props: {
  store: TemplateLibraryStore;
  components: ComponentLibraryStore;
  drafts: AuthorDraftStore;
  thumbnails: TemplateThumbnails;
  returnBar?: LibraryReturnBar | undefined;
}) {
  const { store, components, drafts, thumbnails } = props;
  const workshop = useTemplateWorkshop();
  const state = useObservable(store.state$);
  const componentState = useObservable(components.state$);
  const draftsState = useObservable(drafts.state$);
  const [componentsAvailable, setComponentsAvailable] = useState<boolean>();
  const [chosenSection, setChosenSection] = useState(storedSection);
  const section: Section = componentsAvailable ? chosenSection : 'templates';
  const componentImport = useComponentImport(components, componentState.busy);
  const [query, setQuery] = useState('');
  const [componentQuery, setComponentQuery] = useState('');
  const [kind, setKind] = useState<TemplateKind | 'all'>('all');
  const [nameDialog, setNameDialog] = useState<NameDialog | null>(null);
  const [transfer, setTransfer] = useState<'export' | 'import' | null>(null);
  const [features, setFeatures] = useState<TransferFeatures>();

  useEffect(() => {
    void store.load();
    templateTransfers.features().then(setFeatures, () => setFeatures({ canExport: false, canImport: false }));
    client.capabilities().then(
      ({ features }) => setComponentsAvailable(features.includes(COMPONENT_LIBRARY_FEATURE)),
      () => setComponentsAvailable(false),
    );
  }, [store]);
  useEffect(() => {
    // 模板效果来源名称与选择器也消费组件库，不必先打开“效果组件”页签。
    if (componentsAvailable && components.state.status === 'idle') void components.load();
    if (!componentsAvailable) return;
    const refresh = () => { void components.load(); };
    window.addEventListener('focus', refresh);
    return () => window.removeEventListener('focus', refresh);
  }, [componentsAvailable, components]);
  useEffect(() => {
    // 未保存模板的 configuration 引用钉在整库修订上；库变了要让试样与保存一起恢复。
    if (componentState.revision) void store.refreshComponentReferences();
  }, [componentState.revision, store]);
  useEffect(() => {
    if (section !== 'components') return;
    if (components.state.status === 'idle') void components.load();
    if (drafts.state.status === 'idle') void drafts.load();
  }, [section, components, drafts]);
  // 效果组件页签里，撤销与重做归选中草稿的配置页签。
  useLibraryKeys(store, () => {
    if (section !== 'components') return undefined;
    const selection = componentState.selection;
    // 撤销与重做只交给配置页签；源码页签的撤销在编辑器里。
    return selection?.kind === 'draft' && drafts.tab(selection.draftId) === 'config' ? (drafts.configEditor(selection.draftId) ?? null) : null;
  });

  const choose = async (libraryId: TemplateLibraryId, templateId: string) => {
    const current = store.state.selection;
    if (current?.libraryId === libraryId && current.templateId === templateId) return;
    if (!(await confirmDiscardTemplate(store))) return;
    void store.select({ libraryId, templateId });
  };

  const createNew = async () => {
    if (!(await confirmDiscardTemplate(store))) return;
    void store.createDraft();
  };

  /** 离开模板页签时，模板草稿的未保存修改沿用 W7 的确认（确认后放弃）。 */
  const switchSection = async (next: Section) => {
    if (next === section) return;
    if (next === 'components') {
      if (!(await confirmDiscardTemplate(store))) return;
      store.discardDraft();
    }
    setChosenSection(next);
    try {
      sessionStorage.setItem(SECTION_KEY, next);
    } catch {
      // 存不下时只是下次不记得。
    }
  };

  const remove = async (summary: TemplateSummary) => {
    const ok = await confirmAction({ headline: `删除“${summary.name}”？`, description: '只从我的模板中删除；已经应用到工程里的模板不受影响。', confirmText: '删除' });
    if (ok && (await store.remove(summary.templateId))) toast(`已删除“${summary.name}”`);
  };

  return (
    <div className="library">
      {props.returnBar && <ReturnBar bar={props.returnBar} store={store} state={state} />}
      <aside className="pane library-list">
        <div className="pane-header">
          <span className="pane-title-text">样式库</span>
          {section === 'components' ? (
            <FileButton
              label="导入效果包…"
              accept=".mkfx"
              icon={<mdui-icon-add slot="icon" />}
              onPick={componentImport.pick}
              disabled={componentState.status !== 'ready' || componentState.busy || componentImport.importing}
            />
          ) : (
            <>
              <mdui-button variant="tonal" disabled={state.status !== 'ready' || state.busy} onClick={createNew}>
                <mdui-icon-add slot="icon" />
                新建
              </mdui-button>
              <mdui-dropdown placement="bottom-end">
                <mdui-button-icon slot="trigger" aria-label="模板库的更多操作">
                  <mdui-icon-more-vert />
                </mdui-button-icon>
                <mdui-menu>
                  <mdui-menu-item disabled={state.status !== 'ready' || !features?.canExport} onClick={() => setTransfer('export')}>
                    导出模板…
                  </mdui-menu-item>
                  <mdui-menu-item disabled={state.status !== 'ready' || state.busy || !features?.canImport} onClick={() => setTransfer('import')}>
                    导入模板…
                  </mdui-menu-item>
                </mdui-menu>
              </mdui-dropdown>
            </>
          )}
        </div>
        {componentsAvailable && <Tabs value={section} options={SECTIONS} onChange={(next) => void switchSection(next)} className="library-sections" />}
        {section === 'components' ? (
          <>
            <TextField label="搜索效果组件" value={componentQuery} onChange={setComponentQuery} className="library-search" />
            <ComponentList
              store={components}
              state={componentState}
              drafts={drafts}
              draftsState={draftsState}
              query={componentQuery}
              onImport={componentImport.pick}
              importing={componentImport.importing}
            />
          </>
        ) : (
          <>
            <TextField label="搜索模板" value={query} onChange={setQuery} className="library-search" />
            <Select label="用途" value={kind} options={KIND_OPTIONS} onChange={setKind} className="library-kind" />
            {state.status === 'loading' && !state.builtin && <mdui-linear-progress />}
            {state.status === 'failed' && (
              <div className="empty-state">
                读取模板库失败。
                <mdui-button variant="tonal" onClick={() => void store.load()}>
                  重试
                </mdui-button>
              </div>
            )}
            <div className="line-list">
              <TemplateList title="内置" libraryId="builtin" state={state} thumbnails={thumbnails} query={query} kind={kind} onChoose={choose} />
              <TemplateList title="我的" libraryId="personal" state={state} thumbnails={thumbnails} query={query} kind={kind} onChoose={choose} />
            </div>
          </>
        )}
      </aside>
      <section className="pane library-detail">
        {section === 'components' ? (
          <ComponentPane store={components} state={componentState} drafts={drafts} draftsState={draftsState} templates={store} workshop={workshop} />
        ) : (
          <TemplateDetail
            store={store}
            state={state}
            workshop={workshop}
            components={componentsAvailable ? components : undefined}
            configurations={componentState.configurations}
            onCopy={(libraryId, summary) => setNameDialog({ action: 'copy', libraryId, templateId: summary.templateId, initial: `${summary.name} 副本` })}
            onRename={(summary) => setNameDialog({ action: 'rename', templateId: summary.templateId, initial: summary.name })}
            onRemove={remove}
          />
        )}
      </section>
      <footer className="status-bar">
        {section === 'components' ? (
          <>
            <span className={`notice notice-${(draftsState.notice ?? componentState.notice)?.level ?? 'none'}`}>{(draftsState.notice ?? componentState.notice)?.message ?? '就绪'}</span>
            <span className="muted">{componentState.busy ? '正在写入组件库…' : `效果组件 ${componentState.configurations?.length ?? 0} 个`}</span>
          </>
        ) : (
          <>
            <span className={`notice notice-${state.notice?.level ?? 'none'}`}>{state.notice?.message ?? '就绪'}</span>
            <span className="muted">{state.busy ? '正在写入模板库…' : `我的模板 ${state.personal?.templates.length ?? 0} 个`}</span>
          </>
        )}
      </footer>
      {componentImport.dialog}
      <ExportTemplatesDialog
        open={transfer === 'export'}
        store={store}
        initialLibrary={state.selection?.libraryId ?? (state.personal?.templates.length ? 'personal' : 'builtin')}
        initialSelected={state.selection?.templateId ? [state.selection.templateId] : []}
        onClose={() => setTransfer(null)}
      />
      <ImportTemplatesDialog open={transfer === 'import'} store={store} confirmDiscard={() => confirmDiscardTemplate(store)} onClose={() => setTransfer(null)} />
      <TemplateNameDialog
        dialog={nameDialog}
        busy={state.busy}
        onClose={() => setNameDialog(null)}
        onSubmit={async (dialog, name) => {
          const ok = dialog.action === 'copy' ? await store.copy(dialog.libraryId, dialog.templateId, name) : await store.rename(dialog.templateId, name);
          if (!ok) return;
          setNameDialog(null);
          toast(dialog.action === 'copy' ? `已复制为“${name.trim()}”` : `已改名为“${name.trim()}”`);
        }}
      />
    </div>
  );
}

/** 效果组件页签的右侧：选中草稿时是草稿，否则是组件。 */
function ComponentPane(props: DetailProps) {
  const selection = props.state.selection;
  if (selection?.kind !== 'draft') return <ComponentDetail {...props} />;
  const draft = props.draftsState.drafts.find((d) => d.draftId === selection.draftId);
  if (!draft) return <div className="placeholder">{props.draftsState.status === 'ready' ? '这个草稿已经找不回来了。' : '正在读取草稿…'}</div>;
  return <DraftDetail key={draft.draftId} state={props.state} drafts={props.drafts} draftsState={props.draftsState} draft={draft} workshop={props.workshop} />;
}

/** 返回栏：有修改时先保存再回外观页试看；草稿不能保存时停用并写明原因。 */
function ReturnBar(props: { bar: LibraryReturnBar; store: TemplateLibraryStore; state: LibraryState }) {
  const { bar, store, state } = props;
  const [returning, setReturning] = useState(false);
  const draft = state.draft;
  const problem = draft && store.isDirty ? draftProblem(draft.definition, definitionIssues(draft.definition, state.catalog)) : undefined;
  const run = async () => {
    setReturning(true);
    try {
      await bar.onReturn();
    } finally {
      setReturning(false);
    }
  };
  return (
    <div className="library-return" role="region" aria-label="返回外观">
      <mdui-icon-arrow-back />
      <span className="library-return-text">{bar.text}</span>
      {problem && <span className="notice-error">不能保存：{problem}</span>}
      <div style={{ flexGrow: 1 }} />
      <mdui-button variant="filled" disabled={!!problem || state.busy || returning} loading={returning} onClick={() => void run()}>
        保存并返回试看
      </mdui-button>
      <mdui-button-icon aria-label="关闭返回栏" onClick={bar.onDismiss}>
        <mdui-icon-close />
      </mdui-button-icon>
    </div>
  );
}

function TemplateList(props: {
  title: string;
  libraryId: TemplateLibraryId;
  state: LibraryState;
  thumbnails: TemplateThumbnails;
  query: string;
  kind: TemplateKind | 'all';
  onChoose: (libraryId: TemplateLibraryId, templateId: string) => void;
}) {
  const { state, libraryId } = props;
  const list = state[libraryId];
  const shown = useMemo(() => (list ? filterTemplates(list.templates, props.query, props.kind) : []), [list, props.query, props.kind]);
  const draftSelected = libraryId === 'personal' && state.selection?.templateId === null;
  if (!list) return null;
  return (
    <mdui-list>
      <mdui-list-subheader>{props.title}</mdui-list-subheader>
      {draftSelected && (
        <mdui-list-item active rounded>
          {state.draft?.definition.name}
          <span slot="description">未保存 · {state.draft && KIND_TEXT[state.draft.definition.kind]}</span>
        </mdui-list-item>
      )}
      {shown.map((t) => (
        <mdui-list-item
          key={t.templateId}
          rounded
          data-template-id={t.templateId}
          active={state.selection?.libraryId === libraryId && state.selection.templateId === t.templateId}
          onClick={() => props.onChoose(libraryId, t.templateId)}
        >
          <span slot="icon" className="template-thumb-slot">
            <TemplateThumb thumbnails={props.thumbnails} libraryId={libraryId} libraryRevision={list.revision} summary={t} />
          </span>
          {t.name}
          <span slot="description">
            {KIND_TEXT[t.kind]}
            {isAdvanced(t) ? ' · 含效果' : ''} · {propertySummary(t.kind, t.properties)}
          </span>
        </mdui-list-item>
      ))}
      {shown.length === 0 && !draftSelected && (
        <div className="line-list empty">{list.templates.length === 0 ? (libraryId === 'personal' ? '还没有自己的模板。复制一个内置模板，或点“新建”。' : '没有模板。') : '没有符合条件的模板。'}</div>
      )}
    </mdui-list>
  );
}

function TemplateDetail(props: {
  store: TemplateLibraryStore;
  state: LibraryState;
  workshop: TemplateWorkshop | null;
  components: ComponentLibraryStore | undefined;
  configurations: readonly ScriptConfigurationSummary[] | undefined;
  onCopy: (libraryId: TemplateLibraryId, summary: TemplateSummary) => void;
  onRename: (summary: TemplateSummary) => void;
  onRemove: (summary: TemplateSummary) => void;
}) {
  const { store, state } = props;
  const [tab, setTab] = useState<DetailTab>('appearance');
  const [slot, setSlot] = useState<TemplateMaterialSlot>('unsungFill');
  const workshop = props.workshop;
  // 材质页签选中的部位是试样画布的材质焦点；别的页签不指定。
  useEffect(() => workshop?.setMaterialSlot(tab === 'material' ? slot : undefined), [workshop, tab, slot]);
  const selection = state.selection;
  if (!selection) return <div className="placeholder">从左侧选择一个模板查看。</div>;
  if (state.detailLoading) return <mdui-linear-progress />;

  const isNew = selection.templateId === null;
  const summary = isNew ? undefined : store.summary(selection.libraryId, selection.templateId!);
  const draft = state.draft;
  const definition = draft?.definition ?? state.detail?.definition ?? null;
  if (!isNew && (!summary || !state.detail)) return <div className="placeholder">这个模板已不在库中。</div>;
  const name = definition?.name ?? summary!.name;
  const kind = definition?.kind ?? summary!.kind;
  const builtin = selection.libraryId === 'builtin';
  const advanced = definition ? !staticDefinition(definition) : !!summary && isAdvanced(summary);
  const dirty = store.isDirty;
  const issues = draft ? definitionIssues(draft.definition, state.catalog) : [];
  const problem = draft ? draftProblem(draft.definition, issues) : undefined;
  // 没有改动时按已保存的模板试样（与缩略图同源），有改动或新建时按草稿；草稿不完整时留着上一张。
  const libraryRevision = state[selection.libraryId]?.revision;
  const sampleSource: SampleSource | null =
    draft && (isNew || dirty)
      ? { kind: 'draft', definition: draft.definition }
      : summary && libraryRevision
        ? { kind: 'saved', libraryId: selection.libraryId, libraryRevision, summary }
        : null;
  const sampleScope = `${selection.libraryId}|${selection.templateId ?? ''}`;

  return (
    <div className="template-detail">
      <div className="template-header">
        <h2 className="template-name">{name}</h2>
        <div className="chips">
          <span className="tag">{builtin ? '内置 · 只读' : isNew ? '新建 · 未保存' : dirty ? '我的模板 · 有未保存的修改' : '我的模板'}</span>
          <span className="tag">用途：{KIND_TEXT[kind]}</span>
          {advanced && <span className="tag">含效果</span>}
        </div>
        <div className="template-actions">
          {builtin ? (
            <mdui-button variant="tonal" disabled={state.busy} onClick={() => props.onCopy('builtin', summary!)}>
              <mdui-icon-content-copy slot="icon" />
              复制并编辑
            </mdui-button>
          ) : (
            <>
              {draft && (
                <>
                  <mdui-button-icon disabled={draft.undo.length === 0 || state.busy} onClick={() => store.undo()} aria-label="撤销模板修改">
                    <mdui-icon-undo />
                  </mdui-button-icon>
                  <mdui-button-icon disabled={draft.redo.length === 0 || state.busy} onClick={() => store.redo()} aria-label="重做模板修改">
                    <mdui-icon-redo />
                  </mdui-button-icon>
                </>
              )}
              {summary && (
                <mdui-dropdown placement="bottom-end">
                  <mdui-button-icon slot="trigger" aria-label="模板操作">
                    <mdui-icon-more-vert />
                  </mdui-button-icon>
                  <mdui-menu>
                    <mdui-menu-item disabled={state.busy || dirty} onClick={() => props.onRename(summary)}>
                      改名{dirty ? '（先保存或放弃修改）' : ''}
                    </mdui-menu-item>
                    <mdui-menu-item disabled={state.busy} onClick={() => props.onCopy('personal', summary)}>
                      复制
                    </mdui-menu-item>
                    <mdui-menu-item disabled={state.busy} onClick={() => props.onRemove(summary)}>
                      删除
                    </mdui-menu-item>
                  </mdui-menu>
                </mdui-dropdown>
              )}
              {draft && (
                <>
                  <mdui-button variant="text" disabled={state.busy || !dirty} onClick={() => store.discardDraft()}>
                    {isNew ? '放弃' : '放弃修改'}
                  </mdui-button>
                  <mdui-button variant="filled" loading={state.busy} disabled={state.busy || !dirty || !!problem} onClick={() => void saveDraft(store)}>
                    保存
                  </mdui-button>
                </>
              )}
            </>
          )}
        </div>
      </div>
      {draft && problem && <p className="notice-error template-problem">不能保存：{problem}</p>}
      {props.workshop && (
        <SampleArea workshop={props.workshop} source={problem ? undefined : sampleSource} scope={sampleScope} blocked={problem ? '草稿不完整，改好后再生成试样。' : undefined} />
      )}
      <Tabs value={tab} options={DETAIL_TABS} onChange={setTab} className="template-tabs" />
      {tab === 'material' && definition ? (
        <MaterialTab
          store={store}
          definition={definition}
          draft={draft}
          catalog={state.catalog}
          issues={issues}
          slot={slot}
          onSlot={setSlot}
          onOpenAppearance={() => setTab('appearance')}
          disabled={state.busy}
          components={props.components}
        />
      ) : tab === 'effects' && definition ? (
        <EffectsTab store={store} definition={definition} draft={draft} catalog={state.catalog} issues={issues} disabled={state.busy} components={props.components} configurations={props.configurations} />
      ) : tab === 'appearance' ? (
        draft ? (
          <TemplateEditor definition={draft.definition} baseProperties={draft.base.properties} onChange={(next) => store.edit(next)} disabled={state.busy} />
        ) : definition ? (
          <DefinitionView definition={definition} />
        ) : null
      ) : definition ? (
        <AdvancedView definition={definition} closure={state.detail?.packageClosureDetails ?? []} />
      ) : null}
      <p className="hint">试样只是固定样本上的效果；应用到工程后，可在外观页看成片预览。</p>
    </div>
  );
}

/** 样式库页自己的试样画布，离开页面时关闭会话（StrictMode 的双重挂载下旧实例会被释放）；渲染设备偏好变了就换设备重建。 */
function useTemplateWorkshop(): TemplateWorkshop | null {
  const [workshop, setWorkshop] = useState<TemplateWorkshop | null>(null);
  const device = useObservable(renderDevice.state$);
  const choice = resolveChoice(device.preference, device.backends);
  useEffect(() => {
    const next = new TemplateWorkshop(client, renderDevice);
    setWorkshop(next);
    return () => next.dispose();
  }, []);
  useEffect(() => workshop?.deviceChanged(), [workshop, choice.request]);
  return workshop;
}

async function saveDraft(store: TemplateLibraryStore): Promise<void> {
  const name = store.state.draft?.definition.name;
  if (await store.save()) toast(`已保存“${name}”`);
}

type DetailTab = 'appearance' | 'material' | 'effects' | 'advanced';
const DETAIL_TABS: readonly { value: DetailTab; label: string }[] = [
  { value: 'appearance', label: '外观' },
  { value: 'material', label: '材质' },
  { value: 'effects', label: '效果' },
  { value: 'advanced', label: '高级' },
];

/** 草稿不能保存的原因：先看整体（没有属性和效果、渐变不对），再看包材质与配方的本地校验（第一条）。 */
function draftProblem(definition: FullTemplateDefinition, issues: ReturnType<typeof definitionIssues>): string | undefined {
  const first = issues[0];
  return definitionProblem(definition) ?? (first ? `${issueWhereText(definition, first.where)}：${first.message}` : undefined);
}

/** 高级页签：完整定义的 JSON（只读），和模板用到的效果包与文件清单。 */
function AdvancedView(props: { definition: FullTemplateDefinition; closure: readonly EffectPackageInspectResponse[] }) {
  const { definition, closure } = props;
  return (
    <div className="template-advanced">
      <h3 className="template-advanced-title">完整定义（只读）</h3>
      <pre className="template-json">{JSON.stringify(definition, null, 2)}</pre>
      <h3 className="template-advanced-title">效果包 · {definition.packages.length} 个</h3>
      {definition.packages.length === 0 && <p className="hint">这个模板没有用到效果包。</p>}
      {closure.map((p) => (
        <details key={`${p.packageId}|${p.packageRevision}`} className="package-closure" data-package-id={p.packageId}>
          <summary>
            {p.name} {p.version} · 修订 {shortRevision(p.packageRevision)} · {p.files.length} 个文件
          </summary>
          <table className="package-files">
            <tbody>
              {p.files.map((f) => (
                <tr key={f.path}>
                  <td>{f.path}</td>
                  <td>{f.role}</td>
                  <td>{formatSize(f.size)}</td>
                  <td className="mono">{f.sha256.slice(0, 12)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </details>
      ))}
    </div>
  );
}

function formatSize(bytes: number): string {
  return bytes < 1024 ? `${bytes} B` : `${(bytes / 1024).toFixed(1)} KiB`;
}

/** 只读的分组视图：包含的组显示取值，不包含的组由目标继承；用途不允许的组不显示。 */
function DefinitionView(props: { definition: FullTemplateDefinition }) {
  const { definition } = props;
  const limit = KIND_LIMIT_TEXT[definition.kind];
  return (
    <div className="template-groups">
      {TEMPLATE_GROUPS.filter((g) => groupAllowed(g, definition.kind)).map((g) => {
        const included = groupIncluded(g, definition.properties);
        const swatch = included ? groupSwatch(g.id, definition.style) : undefined;
        return (
          <div key={g.id} className={included ? 'template-group' : 'template-group excluded'} data-group={g.id}>
            <span className="template-group-label">{g.label}</span>
            <span className="template-group-value">
              {swatch && <span className="swatch" style={{ background: swatch }} />}
              {included ? groupValueText(g.id, definition.style) : '不包含（由目标继承）'}
            </span>
          </div>
        );
      })}
      {limit && <p className="hint">{limit}</p>}
    </div>
  );
}

function TemplateNameDialog(props: { dialog: NameDialog | null; busy: boolean; onClose: () => void; onSubmit: (dialog: NameDialog, name: string) => void }) {
  const { dialog } = props;
  const [name, setName] = useState('');
  useEffect(() => setName(dialog?.initial ?? ''), [dialog]);
  const error = dialog ? checkTemplateName(name) : undefined;
  return (
    <FormDialog
      open={!!dialog}
      headline={dialog?.action === 'copy' ? '复制到我的模板' : '模板改名'}
      submitText={dialog?.action === 'copy' ? '复制' : '改名'}
      submitDisabled={!!error || (dialog?.action === 'rename' && name.trim() === dialog.initial.trim())}
      busy={props.busy}
      onSubmit={() => dialog && !error && props.onSubmit(dialog, name)}
      onClose={props.onClose}
    >
      <TextField label="名称" value={name} onChange={setName} error={name ? error : undefined} onEnter={() => dialog && !error && props.onSubmit(dialog, name)} />
    </FormDialog>
  );
}

/**
 * 样式库快捷键：Ctrl／⌘+Z 撤销、Shift+Z 或 Y 重做、S 保存草稿（焦点在输入框里时让给输入框）；有未保存草稿时关页前提示。
 * draftEditor 返回 undefined 表示在模板页签；返回 null 表示在效果组件页签但没选中草稿（不响应）。
 */
function useLibraryKeys(store: TemplateLibraryStore, draftEditor: () => DraftConfigEditor | null | undefined) {
  const target = useRef(draftEditor);
  target.current = draftEditor;
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (!(event.ctrlKey || event.metaKey) || isEditing(event)) return;
      const key = event.key.toLowerCase();
      const editor = target.current();
      if (editor !== undefined) {
        // 效果组件页没有 Ctrl+S 要保存的东西（源码的 Ctrl+S 在编辑器里），也不让浏览器弹出“保存网页”。
        if (key === 's') event.preventDefault();
        if (!editor || (key !== 'z' && key !== 'y')) return;
        event.preventDefault();
        if (key === 'y' || event.shiftKey) editor.redo();
        else editor.undo();
        return;
      }
      if (key === 'z') {
        event.preventDefault();
        if (event.shiftKey) store.redo();
        else store.undo();
      } else if (key === 'y') {
        event.preventDefault();
        store.redo();
      } else if (key === 's') {
        event.preventDefault();
        const definition = store.state.draft?.definition;
        if (store.isDirty && definition && !draftProblem(definition, definitionIssues(definition, store.state.catalog))) void saveDraft(store);
      }
    };
    const onBeforeUnload = (event: BeforeUnloadEvent) => {
      if (store.isDirty) event.preventDefault();
    };
    window.addEventListener('keydown', onKeyDown);
    window.addEventListener('beforeunload', onBeforeUnload);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('beforeunload', onBeforeUnload);
    };
  }, [store]);
}
