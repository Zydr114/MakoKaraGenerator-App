// 样式库页（整体设计 §5.5.1，W3 设计 §2.4、§2.5，W7 设计 §2.3，界面改版 §3.6）：左栏是模板或效果组件列表，
// 中栏（幕后区）是固定样本上的试样画布（G17，W4 设计 §9.2），草稿改动后跟着变；右栏是选中项的编辑。
// 内置模板只读，“复制并编辑”到我的模板；我的模板按完整定义编辑，含效果的也可以。

import { useEffect, useMemo, useRef, useState } from 'react';
import type { EffectPackageInspectResponse, FullTemplateDefinition, ScriptConfigurationSummary, TemplateKind, TemplateLibraryId, TemplateMaterialSlot, TemplateSummary } from '../../protocol';
import { definitionIssues, issueWhereText, shortRevision, staticDefinition } from '../model/full-template';
import { resolveChoice } from '../model/render-device';
import { changeKind, definitionProblem, groupAllowed, groupIncluded, groupSwatch, groupValueText, KIND_LIMIT_TEXT, propertySummary, TEMPLATE_GROUPS } from '../model/template-groups';
import { Button, IconButton } from '../ui/Button';
import { Segmented } from '../ui/choices';
import { cls } from '../ui/cls';
import { List } from '../ui/collections';
import { confirmAction } from '../ui/dialogs';
import { toast } from '../ui/feedback';
import { FileButton, Select, TextField } from '../ui/fields';
import { FormDialog } from '../ui/FormDialog';
import { Icon } from '../ui/Icon';
import { ProgressBar, Tag } from '../ui/indicators';
import { Menu, MenuItem, MenuSeparator, MenuTrigger } from '../ui/Menu';
import { Tabs } from '../ui/Tabs';
import { client, renderDevice, templateTransfers } from '../connection';
import { Group, Note, ReadError } from '../workspace/panel';
import { WorkLayout } from '../workspace/WorkLayout';
import { COMPONENT_LIBRARY_FEATURE, type ComponentLibraryStore } from './component-library-store';
import type { AuthorDraftStore } from './author-draft-store';
import type { DraftConfigEditor } from './draft-config-editor';
import { ComponentDetail, ComponentList, useComponentImport, type DetailProps } from './ComponentsPane';
import { DraftDetail } from './DraftDetail';
import { useObservable } from '../ui/hooks';
import { isEditing } from '../workspace/Workspace';
import { EffectsTab } from './EffectsTab';
import { MaterialTab } from './MaterialTab';
import { TemplateEditor } from './TemplateEditor';
import type { TransferFeatures } from './template-transfers';
import { ExportTemplatesDialog, ImportTemplatesDialog } from './TransferDialogs';
import { checkTemplateName, filterTemplates, isAdvanced, KIND_TEXT, type LibraryState, type TemplateLibraryStore } from './template-library-store';
import { SampleArea, SampleStage, TemplateThumb } from './TemplateThumb';
import type { SampleSource, TemplateThumbnails } from './template-thumbnails';
import { TemplateWorkshop } from './template-workshop';
import './library.css';

const KIND_OPTIONS: readonly { value: TemplateKind | 'all'; label: string }[] = [
  { value: 'all', label: '全部用途' },
  { value: 'line', label: KIND_TEXT.line },
  { value: 'token', label: KIND_TEXT.token },
  { value: 'composite', label: KIND_TEXT.composite },
];
const KIND_CHOICES = (['line', 'token', 'composite'] as const).map((value) => ({ value, label: KIND_TEXT[value] }));

/** 作者草稿的源码页签把右栏放宽（设计稿 #draft-source）。 */
const SOURCE_PANEL_WIDTH = 680;

/** 离开有未保存草稿的模板前确认（切换模板、离开页面共用）。 */
export async function confirmDiscardTemplate(store: TemplateLibraryStore): Promise<boolean> {
  if (!store.isDirty) return true;
  return confirmAction({ headline: '放弃未保存的模板修改？', description: '草稿只在本页，离开后不会保留。', confirmText: '放弃修改', danger: true });
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
  // 中栏：试样画布由右栏的详情经 portal 放进来。
  const [stage, setStage] = useState<HTMLElement | null>(null);

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
    const ok = await confirmAction({
      headline: `删除“${summary.name}”？`,
      description: '只从我的模板中删除；已经应用到工程里的模板不受影响。',
      confirmText: '删除',
      danger: true,
    });
    if (ok && (await store.remove(summary.templateId))) toast(`已删除“${summary.name}”`);
  };

  const selectedDraft = section === 'components' && componentState.selection?.kind === 'draft' ? componentState.selection.draftId : undefined;
  const wide = selectedDraft !== undefined && drafts.tab(selectedDraft) === 'source';
  const notice = section === 'components' ? (draftsState.notice ?? componentState.notice) : state.notice;

  const list =
    section === 'components' ? (
      <>
        <div className="ltools">
          <TextField label="搜索效果组件" labelPlacement="none" size="sm" icon="search" placeholder="搜索效果组件" value={componentQuery} onChange={setComponentQuery} className="library-search" />
        </div>
        <ComponentList store={components} state={componentState} drafts={drafts} draftsState={draftsState} query={componentQuery} />
        <div className="listfoot">
          <FileButton
            label="导入效果包…"
            accept=".mkfx"
            size="sm"
            icon="upload"
            onPick={componentImport.pick}
            disabled={componentState.status !== 'ready' || componentState.busy || componentImport.importing}
          />
        </div>
      </>
    ) : (
      <>
        <div className="ltools">
          <TextField label="搜索模板" labelPlacement="none" size="sm" icon="search" placeholder="搜索模板" value={query} onChange={setQuery} className="library-search" />
          <Select label="用途" labelPlacement="none" size="sm" value={kind} options={KIND_OPTIONS} onChange={setKind} className="library-kind" />
        </div>
        {state.status === 'loading' && !state.builtin && <ProgressBar label="正在读取模板库" className="pane-progress" />}
        {state.builtinError && <ReadError error={state.builtinError} onRetry={() => void store.load()} />}
        {state.personalError && <ReadError error={state.personalError} onRetry={() => void store.load()} />}
        <div className="llist">
          <TemplateList title="内置" libraryId="builtin" store={store} state={state} thumbnails={thumbnails} query={query} kind={kind} onChoose={choose} />
          <TemplateList title="我的" libraryId="personal" store={store} state={state} thumbnails={thumbnails} query={query} kind={kind} onChoose={choose} />
        </div>
        <div className="listfoot">
          <Button variant="tonal" size="sm" icon="add" isDisabled={!state.builtin || !state.personal || state.refreshing || state.busy} onPress={() => void createNew()}>
            新建
          </Button>
          <span className="spacer" />
          <Button variant="ghost" size="sm" icon="upload" isDisabled={!state.personal || state.refreshing || state.busy || !features?.canImport} onPress={() => setTransfer('import')}>
            导入…
          </Button>
          <MenuTrigger>
            <IconButton icon="more_vert" size="sm" label="模板库的更多操作" />
            <Menu placement="bottom end" onAction={(id) => id === 'export' && setTransfer('export')}>
              <MenuItem id="export" icon="download" disabled={state.status !== 'ready' || !features?.canExport}>
                导出模板…
              </MenuItem>
            </Menu>
          </MenuTrigger>
        </div>
      </>
    );

  return (
    <SampleStage.Provider value={stage}>
      <WorkLayout
        page="library"
        className="library"
        stage="center"
        top={props.returnBar && <ReturnBar bar={props.returnBar} store={store} state={state} />}
        left={{
          label: '样式库',
          content: (
            <div className="library-list">
              {componentsAvailable && (
                <div className="lseg">
                  <Segmented label="样式库内容" size="sm" stretch value={section} options={SECTIONS} onChange={(next) => void switchSection(next)} className="library-sections" />
                </div>
              )}
              {list}
            </div>
          ),
        }}
        center={
          <section className="stage-canvas library-canvas">
            <div className="library-stage" ref={setStage} />
            <p className="library-stage-empty">{section === 'components' ? '选中效果组件或草稿后，在这里看固定样本上的试样。' : '选中模板后，在这里看固定样本上的试样。'}</p>
          </section>
        }
        right={{
          label: section === 'components' ? '组件详情' : '模板编辑',
          width: wide ? SOURCE_PANEL_WIDTH : undefined,
          content: (
            <div className="library-detail">
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
            </div>
          ),
        }}
        status={
          <footer className="status-bar">
            <span className={`notice notice-${notice?.level ?? 'none'}`}>{notice?.message ?? '就绪'}</span>
            <span className="status-facts">
              {section === 'components'
                ? componentState.busy
                  ? '正在写入组件库…'
                  : `效果组件 ${componentState.configurations?.length ?? 0} 个`
                : state.busy
                  ? '正在写入模板库…'
                  : state.personal ? `我的模板 ${state.personal.templates.length} 个` : '我的模板不可用'}
            </span>
          </footer>
        }
      >
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
      </WorkLayout>
    </SampleStage.Provider>
  );
}

/** 效果组件页签的右侧：选中草稿时是草稿，否则是组件。 */
function ComponentPane(props: DetailProps) {
  const selection = props.state.selection;
  if (selection?.kind !== 'draft') return <ComponentDetail {...props} />;
  const draft = props.draftsState.drafts.find((d) => d.draftId === selection.draftId);
  if (!draft) return <Note className="insp-empty">{props.draftsState.status === 'ready' ? '这个草稿已经找不回来了。' : '正在读取草稿…'}</Note>;
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
    <div className="retbar library-return" role="region" aria-label="返回外观">
      <Icon name="arrow_back" />
      <span className="library-return-text">{bar.text}。保存后回到外观页，用这个模板继续试看。</span>
      {problem && <span className="retbar-problem">不能保存：{problem}</span>}
      <span className="spacer" />
      <Button variant="filled" size="sm" isPending={returning} isDisabled={!!problem || state.busy || returning} onPress={() => void run()}>
        保存并返回试看
      </Button>
      <IconButton icon="close" size="sm" label="关闭返回栏" onPress={bar.onDismiss} />
    </div>
  );
}

/** 新建、还没保存的模板在“我的”列表里占的那一行。 */
const NEW_ROW = '\u0000new';

function TemplateList(props: {
  title: string;
  libraryId: TemplateLibraryId;
  store: TemplateLibraryStore;
  state: LibraryState;
  thumbnails: TemplateThumbnails;
  query: string;
  kind: TemplateKind | 'all';
  onChoose: (libraryId: TemplateLibraryId, templateId: string) => void;
}) {
  const { state, libraryId } = props;
  const list = state[libraryId];
  const shown = useMemo(() => (list ? filterTemplates(list.templates, props.query, props.kind) : []), [list, props.query, props.kind]);
  if (!list) return null;
  const draftSelected = libraryId === 'personal' && state.selection?.templateId === null;
  const items = [...(draftSelected ? [{ id: NEW_ROW, summary: undefined }] : []), ...shown.map((t) => ({ id: t.templateId, summary: t as TemplateSummary | undefined }))];
  const selected = state.selection?.libraryId === libraryId ? (state.selection.templateId ?? NEW_ROW) : undefined;
  const dirty = props.store.isDirty;
  return (
    <section className="template-list" aria-label={`${props.title}模板`}>
      <h3 className="grouphead">
        {props.title}
        <span>{list.templates.length}</span>
      </h3>
      {items.length > 0 && (
        <List
          label={`${props.title}模板`}
          items={items}
          textValue={(item) => item.summary?.name ?? state.draft?.definition.name ?? '新模板'}
          selectedKeys={selected ? [selected] : []}
          onSelectionChange={(keys) => {
            const [key] = keys;
            if (key !== undefined && key !== NEW_ROW) props.onChoose(libraryId, String(key));
          }}
          className="tlist"
          rowClassName={() => 'trow'}
        >
          {({ id, summary }) =>
            summary ? (
              <span className="trow-body" data-template-id={summary.templateId} title={propertySummary(summary.kind, summary.properties)}>
                <span className="tthumb">
                  <TemplateThumb thumbnails={props.thumbnails} libraryId={libraryId} libraryRevision={list.revision} summary={summary} />
                </span>
                <span className="trow-text">
                  <b>{summary.name}</b>
                  <span className="small faint">
                    {KIND_TEXT[summary.kind]}
                    {isAdvanced(summary) ? '，含效果' : ''}
                  </span>
                </span>
                {dirty && selected === id && <i className="udot" title="有未保存的修改" />}
              </span>
            ) : (
              <span className="trow-body" data-new-template>
                <span className="tthumb empty">
                  <Icon name="style" />
                </span>
                <span className="trow-text">
                  <b>{state.draft?.definition.name}</b>
                  <span className="small faint">未保存{state.draft ? `，${KIND_TEXT[state.draft.definition.kind]}` : ''}</span>
                </span>
                <i className="udot" title="还没保存" />
              </span>
            )
          }
        </List>
      )}
      {items.length === 0 && (
        <Note className="list-note">
          {list.templates.length === 0 ? (libraryId === 'personal' ? '还没有自己的模板。复制一个内置模板，或点“新建”。' : '没有模板。') : '没有符合条件的模板。'}
        </Note>
      )}
    </section>
  );
}

type DetailTab = 'appearance' | 'material' | 'effects' | 'advanced';

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
  if (!selection) return <Note className="insp-empty">从左侧选择一个模板查看。</Note>;
  if (state.detailLoading) return <ProgressBar label="正在读取模板" className="pane-progress" />;

  const isNew = selection.templateId === null;
  const summary = isNew ? undefined : store.summary(selection.libraryId, selection.templateId!) ?? (state.detail?.summary.templateId === selection.templateId ? state.detail.summary : undefined);
  const draft = state.draft;
  const definition = draft?.definition ?? state.detail?.definition ?? null;
  if (!isNew && (!summary || !state.detail)) return <Note className="insp-empty">这个模板已不在库中。</Note>;
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
  const recipes = definition?.effectRecipes.length ?? 0;
  const tabs: readonly { value: DetailTab; label: string; badge?: number }[] = [
    { value: 'appearance', label: '外观' },
    { value: 'material', label: '材质' },
    { value: 'effects', label: '效果', ...(recipes > 0 ? { badge: recipes } : {}) },
    { value: 'advanced', label: '高级' },
  ];
  const menu = (id: unknown) => {
    if (!summary) return;
    if (id === 'rename') props.onRename(summary);
    else if (id === 'copy') props.onCopy('personal', summary);
    else if (id === 'delete') props.onRemove(summary);
  };

  return (
    <div className="lib-detail template-detail">
      <header className="phead">
        <div className="phead-row">
          <h2 className="template-name">{name}</h2>
          <Tag>{builtin ? '内置' : isNew ? '新建' : '我的模板'}</Tag>
          {!builtin && (isNew || dirty) && <Tag tone="warn">未保存</Tag>}
          {advanced && <Tag tone="accent">含效果</Tag>}
          <span className="spacer" />
          {draft && (
            <>
              <IconButton icon="undo" size="sm" label="撤销模板修改" isDisabled={draft.undo.length === 0 || state.busy} onPress={() => store.undo()} />
              <IconButton icon="redo" size="sm" label="重做模板修改" isDisabled={draft.redo.length === 0 || state.busy} onPress={() => store.redo()} />
            </>
          )}
          {!builtin && summary && (
            <MenuTrigger>
              <IconButton icon="more_vert" size="sm" label="模板操作" />
              <Menu placement="bottom end" onAction={menu}>
                <MenuItem id="rename" icon="edit" disabled={state.busy || dirty || !state.personal || state.refreshing}>
                  {dirty ? '改名（先保存或放弃修改）' : '改名…'}
                </MenuItem>
                <MenuItem id="copy" icon="content_copy" disabled={state.busy || !state.personal || state.refreshing}>
                  复制…
                </MenuItem>
                <MenuSeparator />
                <MenuItem id="delete" icon="delete" danger disabled={state.busy || !state.personal || state.refreshing}>
                  删除…
                </MenuItem>
              </Menu>
            </MenuTrigger>
          )}
        </div>
        <div className="phead-row template-actions">
          {builtin ? (
            <>
              <span className="meta">内置模板只读，复制到我的模板后才能改。</span>
              <span className="spacer" />
              <Button variant="tonal" size="sm" icon="content_copy" isDisabled={state.busy || !state.personal || state.refreshing} onPress={() => props.onCopy('builtin', summary!)}>
                复制并编辑
              </Button>
            </>
          ) : draft ? (
            <>
              <Select<TemplateKind>
                label="用途"
                labelPlacement="inside"
                size="sm"
                className="template-kind"
                value={draft.definition.kind}
                options={KIND_CHOICES}
                onChange={(next) => store.edit(changeKind(draft.definition, next))}
                disabled={state.busy}
              />
              <span className="spacer" />
              <Button variant="text" size="sm" isDisabled={state.busy || !dirty} onPress={() => store.discardDraft()}>
                {isNew ? '放弃' : '放弃修改'}
              </Button>
              <Button variant="filled" size="sm" icon="save" isPending={state.busy} isDisabled={state.busy || !state.personal || state.refreshing || !dirty || !!problem} onPress={() => void saveDraft(store)}>
                保存
              </Button>
            </>
          ) : (
            <span className="meta">用途：{KIND_TEXT[kind]}</span>
          )}
        </div>
      </header>
      {draft && problem && (
        <Note error className="template-problem">
          不能保存：{problem}
        </Note>
      )}
      {workshop && (
        <SampleArea workshop={workshop} source={problem ? undefined : sampleSource} scope={sampleScope} blocked={problem ? '草稿不完整，改好后再生成试样。' : undefined} />
      )}
      <Tabs label="模板编辑" value={tab} options={tabs} onChange={setTab} className="ptabs template-tabs" />
      <div className="pbody" data-tab={tab}>
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
            <TemplateEditor
              definition={draft.definition}
              baseProperties={draft.base.properties}
              onChange={(next) => store.edit(next)}
              onOpenMaterial={(next) => {
                setSlot(next);
                setTab('material');
              }}
              disabled={state.busy}
            />
          ) : definition ? (
            <DefinitionView definition={definition} />
          ) : null
        ) : definition ? (
          <AdvancedView definition={definition} closure={state.detail?.packageClosureDetails ?? []} />
        ) : null}
      </div>
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

/** 草稿不能保存的原因：先看整体（没有属性和效果、渐变不对），再看包材质与配方的本地校验（第一条）。 */
function draftProblem(definition: FullTemplateDefinition, issues: ReturnType<typeof definitionIssues>): string | undefined {
  const first = issues[0];
  return definitionProblem(definition) ?? (first ? `${issueWhereText(definition, first.where)}：${first.message}` : undefined);
}

/** 高级页签：完整定义的 JSON（只读，可以复制），和模板用到的效果包与文件清单。 */
function AdvancedView(props: { definition: FullTemplateDefinition; closure: readonly EffectPackageInspectResponse[] }) {
  const { definition, closure } = props;
  const json = JSON.stringify(definition, null, 2);
  const copy = () =>
    navigator.clipboard.writeText(json).then(
      () => toast('已复制完整定义'),
      () => toast('复制失败：浏览器没有允许写剪贴板。'),
    );
  const files = closure.reduce((n, p) => n + p.files.length, 0);
  return (
    <div className="template-advanced">
      <Group
        title="完整定义（只读）"
        extra={
          <Button variant="ghost" size="sm" icon="content_copy" onPress={() => void copy()}>
            复制
          </Button>
        }
      >
        <pre className="template-json code">{json}</pre>
      </Group>
      <Group title={`效果包 · ${definition.packages.length} 个`} hint={files > 0 ? `${files} 个文件` : undefined}>
        {definition.packages.length === 0 && <Note>这个模板没有用到效果包。</Note>}
        {closure.map((p) => (
          <section key={`${p.packageId}|${p.packageRevision}`} className="package-closure" data-package-id={p.packageId} aria-label={p.name}>
            <h5>
              {p.name} {p.version}
              <span className="faint">修订 {shortRevision(p.packageRevision)}</span>
            </h5>
            <ul className="files">
              {p.files.map((f) => (
                <li key={f.path} title={`SHA-256 ${f.sha256}`}>
                  <Icon name={f.path.endsWith('.cs') ? 'code' : 'description'} />
                  <span className="file-path">{f.path}</span>
                  <span className="faint">{f.role}</span>
                  <em>{formatSize(f.size)}</em>
                </li>
              ))}
            </ul>
          </section>
        ))}
      </Group>
    </div>
  );
}

function formatSize(bytes: number): string {
  return bytes < 1024 ? `${bytes} B` : `${(bytes / 1024).toFixed(1)} KB`;
}

/** 只读的分组视图：包含的组显示取值，不包含的组由目标继承；用途不允许的组不显示。 */
function DefinitionView(props: { definition: FullTemplateDefinition }) {
  const { definition } = props;
  const limit = KIND_LIMIT_TEXT[definition.kind];
  return (
    <div className="template-groups">
      <p className="pintro">只读：写进模板的项列出取值，其余由目标继承。</p>
      <dl className="def-list">
        {TEMPLATE_GROUPS.filter((g) => groupAllowed(g, definition.kind)).map((g) => {
          const included = groupIncluded(g, definition.properties);
          const swatch = included ? groupSwatch(g.id, definition.style) : undefined;
          return (
            <div key={g.id} className={cls('template-group', !included && 'excluded')} data-group={g.id}>
              <dt>{g.label}</dt>
              <dd className="template-group-value">
                {swatch && <i className="def-swatch" style={{ background: swatch }} />}
                <span>{included ? groupValueText(g.id, definition.style, definition.properties) : '由目标继承'}</span>
              </dd>
            </div>
          );
        })}
      </dl>
      {limit && <Note>{limit}</Note>}
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
      <TextField label="名称" value={name} onChange={setName} error={name ? error : undefined} onEnter={() => dialog && !error && props.onSubmit(dialog, name)} autoFocus />
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
