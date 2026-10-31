// 样式库的“效果组件”页签（W8 设计 §2.2、§2.3）：左栏是本标签页的草稿和组件库，右侧是选中组件的摘要与试样（草稿见 DraftDetail）；
// 导入 .mkfx、编辑（新建草稿）、改名、复制、删除。试样用内置“经典整行”做底临时拼一个完整模板，走 W7 的工坊试样，不写库。

import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { AUTHOR_DRAFT_LIMIT, type EffectPackageExport, type EffectPackageInspectResponse, type FullTemplateDefinition, type ScriptConfigurationFields } from '../../protocol';
import { OVERLAY_LABEL, READ_SCOPE_LABEL, RECIPE_TARGET_LABEL } from '../model/effect-recipes';
import { EXPORT_KIND_TEXT, shortRevision } from '../model/full-template';
import { checkComponentName, componentKindText, componentSample, filterByName, importableExports, importedName, parameterSummary } from '../model/script-components';
import { errorText } from '../session/editor-session';
import { confirmAction } from '../ui/dialogs';
import { toast } from '../ui/feedback';
import { FileButton, TextField } from '../ui/fields';
import { FormDialog } from '../ui/FormDialog';
import { DRAFT_LIMIT_TEXT, draftSaveState, type AuthorDraftStore, type DraftSaveState, type DraftsState } from './author-draft-store';
import { selectedComponent, type ComponentLibraryStore, type ComponentState } from './component-library-store';
import type { TemplateLibraryStore } from './template-library-store';
import { SampleArea } from './TemplateThumb';
import type { TemplateWorkshop } from './template-workshop';

export const SAVE_STATE_TEXT: Record<DraftSaveState, string> = { saved: '已保存', unsaved: '未保存到组件库', orphan: '没有对应的组件', checking: '正在核对…' };

export function ComponentList(props: {
  store: ComponentLibraryStore;
  state: ComponentState;
  drafts: AuthorDraftStore;
  draftsState: DraftsState;
  query: string;
  onImport: (file: File) => void;
  importing: boolean;
}) {
  const { store, state, draftsState } = props;
  const configurations = state.configurations;
  const shown = useMemo(() => filterByName(configurations ?? [], props.query), [configurations, props.query]);
  const draftItems = useMemo(() => draftsState.drafts.filter((d) => filterByName([d.configuration], props.query).length > 0), [draftsState.drafts, props.query]);
  const selectedDraft = state.selection?.kind === 'draft' ? state.selection.draftId : undefined;
  if (state.status === 'failed') {
    return (
      <div className="empty-state">
        读取组件库失败。
        <mdui-button variant="tonal" onClick={() => void store.load()}>
          重试
        </mdui-button>
      </div>
    );
  }
  if (!configurations) return <mdui-linear-progress />;
  return (
    <div className="line-list">
      {(draftsState.drafts.length > 0 || draftsState.lost) && (
        <mdui-list className="draft-list">
          <mdui-list-subheader>
            草稿 · 本标签页 · {draftsState.drafts.length}/{AUTHOR_DRAFT_LIMIT}
          </mdui-list-subheader>
          {draftsState.lost && <div className="line-list empty notice-warning">{draftsState.lost}</div>}
          {draftItems.map((d) => {
            const saveState = draftSaveState(draftsState, d);
            return (
              <mdui-list-item key={d.draftId} rounded data-draft-id={d.draftId} data-save-state={saveState} active={selectedDraft === d.draftId} onClick={() => store.selectDraft(d.draftId)}>
                {d.configuration.name}
                <span slot="description">
                  {componentKindText(d.configuration.kind)} · {SAVE_STATE_TEXT[saveState]}
                </span>
                {saveState !== 'saved' && saveState !== 'checking' && <span slot="end-icon" className="draft-dot" aria-label="有没保存到组件库的改动" />}
              </mdui-list-item>
            );
          })}
        </mdui-list>
      )}
      <mdui-list>
        <mdui-list-subheader>我的效果组件</mdui-list-subheader>
        {shown.map((c) => (
          <mdui-list-item
            key={c.configurationId}
            rounded
            data-configuration-id={c.configurationId}
            active={selectedComponent(state) === c.configurationId}
            onClick={() => void store.select(c.configurationId)}
          >
            {c.name}
            <span slot="description">
              {c.packageId} · {c.exportId}
            </span>
            <span slot="end-icon" className="component-kind">
              {componentKindText(c.kind)}
            </span>
          </mdui-list-item>
        ))}
        {configurations.length === 0 ? (
          <div className="line-list empty component-empty">
            还没有效果组件 ·
            <FileButton label="导入效果包…" accept=".mkfx" variant="text" onPick={props.onImport} disabled={state.busy || props.importing} />
          </div>
        ) : (
          shown.length === 0 && <div className="line-list empty">没有符合条件的组件。</div>
        )}
      </mdui-list>
    </div>
  );
}

export type NameDialog = { action: 'copy' | 'rename' | 'saveAs'; id: string; initial: string };

const NAME_DIALOG_TEXT: Record<NameDialog['action'], { headline: string; submit: string }> = {
  copy: { headline: '复制效果组件', submit: '复制' },
  rename: { headline: '效果组件改名', submit: '改名' },
  saveAs: { headline: '另存为新组件', submit: '保存' },
};

export type DetailProps = { store: ComponentLibraryStore; state: ComponentState; drafts: AuthorDraftStore; draftsState: DraftsState; templates: TemplateLibraryStore; workshop: TemplateWorkshop | null };

export function ComponentDetail(props: DetailProps) {
  const { store, state, drafts, draftsState, workshop } = props;
  const [nameDialog, setNameDialog] = useState<NameDialog | null>(null);
  const starter = useStarter(props.templates);
  const selection = selectedComponent(state);
  const detail = state.detail?.configuration.configurationId === selection ? state.detail : undefined;
  const sample = useMemo(() => (detail && starter.definition ? componentSample(starter.definition, detail) : undefined), [detail, starter.definition]);
  const materialSlot = sample && 'definition' in sample ? sample.materialSlot : undefined;
  useEffect(() => workshop?.setMaterialSlot(materialSlot), [workshop, materialSlot]);

  if (selection === undefined) return <div className="placeholder">从左侧选择一个效果组件查看，或导入效果包。</div>;
  if (state.detailLoading || (!detail && store.summary(selection))) return <mdui-linear-progress />;
  if (!detail) return <div className="placeholder">这个组件已不在库中。</div>;

  const { configuration, package: pkg } = detail;
  const existing = drafts.draftFor(configuration.configurationId);
  const full = !existing && draftsState.drafts.length >= AUTHOR_DRAFT_LIMIT;
  const remove = async () => {
    const ok = await confirmAction({ headline: `删除“${configuration.name}”？`, description: '只从效果组件库中删除；已经用在模板里的不受影响。', confirmText: '删除' });
    if (ok && (await store.remove(configuration.configurationId))) toast(`已删除“${configuration.name}”`);
  };

  return (
    <div className="template-detail component-detail" data-configuration-id={configuration.configurationId}>
      <div className="template-header">
        <h2 className="template-name">{configuration.name}</h2>
        <div className="chips">
          <span className="tag">效果组件 · {componentKindText(configuration.kind)}</span>
        </div>
        <div className="template-actions">
          <mdui-button variant="tonal" disabled={state.busy || draftsState.busy || full || draftsState.status !== 'ready'} onClick={() => void drafts.edit(configuration.configurationId)}>
            <mdui-icon-edit slot="icon" />
            {existing ? '打开草稿' : '编辑'}
          </mdui-button>
          <mdui-dropdown placement="bottom-end">
            <mdui-button-icon slot="trigger" aria-label="组件操作">
              <mdui-icon-more-vert />
            </mdui-button-icon>
            <mdui-menu>
              <mdui-menu-item disabled={state.busy} onClick={() => setNameDialog({ action: 'rename', id: configuration.configurationId, initial: configuration.name })}>
                改名…
              </mdui-menu-item>
              <mdui-menu-item disabled={state.busy} onClick={() => setNameDialog({ action: 'copy', id: configuration.configurationId, initial: `${configuration.name} 副本` })}>
                复制…
              </mdui-menu-item>
              <mdui-menu-item disabled={state.busy} onClick={() => void remove()}>
                删除…
              </mdui-menu-item>
            </mdui-menu>
          </mdui-dropdown>
        </div>
      </div>
      {full && <p className="hint">{DRAFT_LIMIT_TEXT}</p>}
      {workshop &&
        (starter.error ? (
          <p className="notice-error">读不到试样用的内置模板：{starter.error}</p>
        ) : (
          <SampleArea
            workshop={workshop}
            source={sample && 'definition' in sample ? { kind: 'draft', definition: sample.definition } : undefined}
            scope={`component|${configuration.configurationId}|${detail.revision}`}
            blocked={sample && 'problem' in sample ? sample.problem : undefined}
            sourceNote=""
          />
        ))}
      <ConfigurationFacts configuration={configuration} pkg={pkg} />
      <p className="hint">试样用内置“经典整行”做底，只是固定样本上的效果。用到模板里后，可在外观页看成片预览。</p>
      <ComponentNameDialog
        dialog={nameDialog}
        busy={state.busy}
        onClose={() => setNameDialog(null)}
        onSubmit={async (dialog, name) => {
          const ok = dialog.action === 'copy' ? await store.copy(dialog.id, name) : await store.rename(dialog.id, name);
          if (!ok) return;
          setNameDialog(null);
          toast(dialog.action === 'copy' ? `已复制为“${name.trim()}”` : `已改名为“${name.trim()}”`);
        }}
      />
    </div>
  );
}

/** 组件或草稿配置的摘要；没有包信息时只写包 ID 和导出 ID。 */
export function ConfigurationFacts(props: { configuration: ScriptConfigurationFields; pkg: EffectPackageInspectResponse | undefined }) {
  const { configuration, pkg } = props;
  const exp = pkg?.exports.find((e) => e.id === configuration.exportId);
  const material = configuration.kind === 'Material';
  const parameters = pkg ? parameterSummary(pkg, configuration) : [];
  return (
    <dl className="component-facts">
      <Fact label="包">
        {pkg && `${pkg.name} ${pkg.version}`}
        <span className="muted">
          {pkg ? ' · ' : ''}
          {configuration.packageId} · 修订 {shortRevision(configuration.packageRevision)}
        </span>
      </Fact>
      <Fact label="导出">
        {exp ? `${exp.name}（${EXPORT_KIND_TEXT[exp.kind]}）` : configuration.exportId}
        {pkg && !exp && <span className="notice-error"> 包里没有这个导出</span>}
      </Fact>
      {!material && <Fact label="作用于">{RECIPE_TARGET_LABEL[configuration.relativeTarget]}</Fact>}
      <Fact label="读取范围">
        {READ_SCOPE_LABEL[configuration.readScope]}
        {configuration.branchInput ? `：${configuration.branchInput}` : ''}
      </Fact>
      {!material && <Fact label="叠放层">{OVERLAY_LABEL[configuration.overlayLayer]}</Fact>}
      {!material && <Fact label="种子">{configuration.fixedSeed ?? '不固定'}</Fact>}
      <Fact label="参数">
        {!pkg ? (
          <code>{configuration.parametersJson}</code>
        ) : parameters.length === 0 ? (
          '全部默认'
        ) : (
          <ul className="component-params">
            {parameters.map((p) => (
              <li key={p}>{p}</li>
            ))}
          </ul>
        )}
      </Fact>
    </dl>
  );
}

function Fact(props: { label: string; children: ReactNode }) {
  return (
    <>
      <dt>{props.label}</dt>
      <dd>{props.children}</dd>
    </>
  );
}

/** 试样的底：内置“经典整行”的完整定义。 */
export function useStarter(templates: TemplateLibraryStore): { definition?: FullTemplateDefinition; error?: string } {
  const [starter, setStarter] = useState<{ definition?: FullTemplateDefinition; error?: string }>({});
  useEffect(() => {
    let live = true;
    templates.starterDefinition().then(
      (definition) => live && setStarter({ definition }),
      (error: unknown) => live && setStarter({ error: errorText(error) }),
    );
    return () => {
      live = false;
    };
  }, [templates]);
  return starter;
}

export function ComponentNameDialog(props: { dialog: NameDialog | null; busy: boolean; onClose: () => void; onSubmit: (dialog: NameDialog, name: string) => void }) {
  const { dialog } = props;
  const [name, setName] = useState('');
  useEffect(() => setName(dialog?.initial ?? ''), [dialog]);
  const error = dialog ? checkComponentName(name) : undefined;
  const submit = () => dialog && !error && props.onSubmit(dialog, name);
  return (
    <FormDialog
      open={!!dialog}
      headline={NAME_DIALOG_TEXT[dialog?.action ?? 'rename'].headline}
      submitText={NAME_DIALOG_TEXT[dialog?.action ?? 'rename'].submit}
      submitDisabled={!!error || (dialog?.action === 'rename' && name.trim() === dialog.initial.trim())}
      busy={props.busy}
      onSubmit={submit}
      onClose={props.onClose}
    >
      <TextField label="名称" value={name} onChange={setName} error={name ? error : undefined} onEnter={submit} />
    </FormDialog>
  );
}

type Pending = { status: 'inspecting'; fileName: string } | { status: 'ready'; pkg: EffectPackageInspectResponse } | { status: 'failed'; fileName: string; message: string };

/** 导入效果包：选文件后上传并检查，在对话框里勾选导出（默认全选），每个勾选的导出新建一个组件。 */
export function useComponentImport(store: ComponentLibraryStore, busy: boolean): { pick: (file: File) => void; importing: boolean; dialog: ReactNode } {
  const [pending, setPending] = useState<Pending | null>(null);
  const [checked, setChecked] = useState<ReadonlySet<string>>(new Set());
  // 每次选文件或关闭加一：关掉对话框后晚到的检查结果不再打开它。
  const seq = useRef(0);
  const close = () => {
    seq.current++;
    setPending(null);
  };
  const pick = (file: File) => {
    const mine = ++seq.current;
    setPending({ status: 'inspecting', fileName: file.name });
    store.inspectPackageFile(file).then(
      (pkg) => {
        if (mine !== seq.current) return;
        setChecked(new Set(importableExports(pkg).map((e) => e.id)));
        setPending({ status: 'ready', pkg });
      },
      (error: unknown) => mine === seq.current && setPending({ status: 'failed', fileName: file.name, message: errorText(error) }),
    );
  };
  const pkg = pending?.status === 'ready' ? pending.pkg : undefined;
  const exports = pkg ? importableExports(pkg) : [];
  const selected = exports.filter((e) => checked.has(e.id));
  const submit = async () => {
    if (!pkg || selected.length === 0) return;
    const count = await store.importPackage(pkg, selected);
    if (count === 0) return;
    close();
    toast(count === 1 ? `已导入“${importedName(pkg, selected[0]!)}”` : `已导入 ${count} 个效果组件`);
  };
  const dialog = (
    <FormDialog
      open={!!pending}
      headline="导入效果包"
      submitText={selected.length > 1 ? `导入 ${selected.length} 个` : '导入'}
      submitDisabled={!pkg || !pkg.structureValid || selected.length === 0}
      busy={busy}
      onSubmit={() => void submit()}
      onClose={close}
      className="component-import"
    >
      {pending?.status === 'inspecting' && (
        <div className="package-pending" data-status="inspecting">
          <mdui-linear-progress />
          <span className="muted">正在上传并检查 {pending.fileName}…</span>
        </div>
      )}
      {pending?.status === 'failed' && <p className="notice-error">读不出 {pending.fileName}：{pending.message}</p>}
      {pkg && <ImportChoices pkg={pkg} exports={exports} checked={checked} onChange={setChecked} disabled={busy} />}
    </FormDialog>
  );
  return { pick, importing: pending?.status === 'inspecting', dialog };
}

function ImportChoices(props: {
  pkg: EffectPackageInspectResponse;
  exports: readonly EffectPackageExport[];
  checked: ReadonlySet<string>;
  onChange: (checked: ReadonlySet<string>) => void;
  disabled: boolean;
}) {
  const { pkg, exports, checked } = props;
  const toggle = (id: string) => {
    const next = new Set(checked);
    if (!next.delete(id)) next.add(id);
    props.onChange(next);
  };
  return (
    <div className="package-pending" data-status="ready" data-valid={pkg.structureValid}>
      <div className="package-pending-title">
        <strong>{pkg.name}</strong> {pkg.version} · 修订 {shortRevision(pkg.packageRevision)} · {pkg.files.length} 个文件
      </div>
      {pkg.structureValid && exports.length > 0 && <p className="hint">每个勾选的导出在组件库里新建一个组件，参数全部默认。</p>}
      <div className="transfer-list" role="list">
        {exports.map((e) => (
          <label key={e.id} role="listitem" className="transfer-row" data-export-id={e.id}>
            <mdui-checkbox checked={checked.has(e.id)} disabled={props.disabled || !pkg.structureValid} aria-label={`导入 ${e.name}`} onClick={() => toggle(e.id)} />
            <span className="transfer-name">{importedName(pkg, e)}</span>
            <span className="target-meta">
              {EXPORT_KIND_TEXT[e.kind]} · {e.parameterIds.length} 个参数
            </span>
          </label>
        ))}
      </div>
      {exports.length === 0 && <p className="notice-error">包里没有能导入的导出。</p>}
      {pkg.diagnostics.length > 0 && (
        <ul className="package-diagnostics">
          {pkg.diagnostics.map((d, i) => (
            <li key={i}>
              <code>{d.code}</code> {d.path && <span className="muted">{d.path}</span>} {d.message}
            </li>
          ))}
        </ul>
      )}
      {!pkg.structureValid && <p className="notice-error">包的结构无效，不能导入。</p>}
    </div>
  );
}
