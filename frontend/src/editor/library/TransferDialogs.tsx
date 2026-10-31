import { useMemo, useState } from 'react';
import { BackendError, formatBytes } from '../../client';
import type { TemplateImportPreflightResponse, TemplateLibraryId } from '../../protocol';
import { templateTransfers } from '../connection';
import { errorText } from '../session/editor-session';
import { saveBlob } from '../ui/download';
import { toast } from '../ui/feedback';
import { FilePicker, Select } from '../ui/fields';
import { FormDialog } from '../ui/FormDialog';
import { useObservable } from '../ui/hooks';
import { AVAILABILITY_TEXT, MAX_TRANSFER_TEMPLATES, PreflightGoneError, TRANSFER_FILE_NAME } from './template-transfers';
import { KIND_TEXT, type TemplateLibraryStore } from './template-library-store';

const LIBRARY_OPTIONS: ReadonlyArray<{ value: TemplateLibraryId; label: string }> = [
  { value: 'builtin', label: '内置' },
  { value: 'personal', label: '我的' },
];

const CONFLICT_TEXT = '模板库已被修改，已重新读取。请确认后再试一次。';

function isConflict(error: unknown): boolean {
  return error instanceof BackendError && error.code === 'revisionConflict';
}

/** 导出模板（W7 设计 §2.6）：一次只导出一个库，勾选 1～100 个，下载 MakoKara-templates.mktpl 并核对大小和 SHA-256。 */
export function ExportTemplatesDialog(props: {
  open: boolean;
  store: TemplateLibraryStore;
  initialLibrary: TemplateLibraryId;
  initialSelected: readonly string[];
  onClose: () => void;
}) {
  const { store } = props;
  const state = useObservable(store.state$);
  const [libraryId, setLibraryId] = useState<TemplateLibraryId>(props.initialLibrary);
  const [checked, setChecked] = useState<ReadonlySet<string>>(() => new Set(props.open ? props.initialSelected : []));
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  // 每次打开时取初始的库和勾选、清掉上次的提示。放在渲染里而不是 effect 里，免得打开后的第一帧还是上次的勾选。
  const [wasOpen, setWasOpen] = useState(props.open);
  if (props.open !== wasOpen) {
    setWasOpen(props.open);
    if (props.open) {
      setLibraryId(props.initialLibrary);
      setChecked(new Set(props.initialSelected));
      setMessage(null);
    }
  }

  const templates = useMemo(() => state[libraryId]?.templates ?? [], [state, libraryId]);
  // 重读之后不在库里的模板不算。
  const ids = templates.filter((t) => checked.has(t.templateId)).map((t) => t.templateId);
  const tooMany = ids.length > MAX_TRANSFER_TEMPLATES;

  const toggle = (templateId: string) =>
    setChecked((current) => {
      const next = new Set(current);
      if (next.has(templateId)) next.delete(templateId);
      else next.add(templateId);
      return next;
    });

  const submit = async () => {
    const list = state[libraryId];
    if (!list || ids.length === 0 || tooMany) return;
    setBusy(true);
    setMessage(null);
    try {
      const { blob, response } = await templateTransfers.exportArchive(libraryId, list.revision, ids);
      saveBlob(blob, TRANSFER_FILE_NAME);
      toast(`已导出 ${ids.length} 个模板（${formatBytes(response.sizeBytes)}），大小和 SHA-256 已核对`);
      props.onClose();
    } catch (error) {
      if (isConflict(error)) {
        await store.load();
        setMessage(CONFLICT_TEXT);
      } else setMessage(errorText(error));
    } finally {
      setBusy(false);
    }
  };

  return (
    <FormDialog
      open={props.open}
      headline="导出模板"
      description={`把勾选的模板打包成 ${TRANSFER_FILE_NAME}。一次只导出一个库，最多 ${MAX_TRANSFER_TEMPLATES} 个。`}
      submitText={ids.length > 0 ? `导出 ${ids.length} 个模板` : '导出'}
      submitDisabled={ids.length === 0 || tooMany}
      busy={busy}
      onSubmit={() => void submit()}
      onClose={props.onClose}
      className="transfer-dialog"
    >
      <div className="transfer-toolbar">
        <Select
          label="库"
          value={libraryId}
          options={LIBRARY_OPTIONS}
          onChange={(id) => {
            setLibraryId(id);
            setChecked(new Set());
          }}
          disabled={busy}
        />
        <mdui-button variant="text" disabled={busy || templates.length === 0} onClick={() => setChecked(new Set(templates.slice(0, MAX_TRANSFER_TEMPLATES).map((t) => t.templateId)))}>
          全选
        </mdui-button>
        <mdui-button variant="text" disabled={busy || ids.length === 0} onClick={() => setChecked(new Set())}>
          全不选
        </mdui-button>
      </div>
      <div className="transfer-list" role="list" aria-label="要导出的模板">
        {templates.length === 0 && <p className="hint">这个库里还没有模板。</p>}
        {templates.map((t) => (
          <label key={t.templateId} role="listitem" className="transfer-row" data-template-id={t.templateId}>
            <mdui-checkbox checked={checked.has(t.templateId)} disabled={busy} aria-label={`导出 ${t.name}`} onClick={() => toggle(t.templateId)} />
            <span className="transfer-name">{t.name}</span>
            <span className="target-meta">
              {KIND_TEXT[t.kind]}
              {t.hasEffectRecipes || t.hasPackageMaterials ? ' · 含效果' : ''}
            </span>
          </label>
        ))}
      </div>
      {tooMany && <p className="notice-warning">一次最多导出 {MAX_TRANSFER_TEMPLATES} 个模板。</p>}
      {message && <p className="notice-error">{message}</p>}
    </FormDialog>
  );
}

/** 导入模板（W7 设计 §2.6）：选文件后上传预检，列出将导入的模板，确认后全部以新 ID 写入“我的”，选中第一个。 */
export function ImportTemplatesDialog(props: {
  open: boolean;
  store: TemplateLibraryStore;
  /** 导入后要选中新模板：有未保存的草稿时先确认放弃。 */
  confirmDiscard: () => Promise<boolean>;
  onClose: () => void;
}) {
  const { store } = props;
  const [file, setFile] = useState<File | null>(null);
  const [preflight, setPreflight] = useState<TemplateImportPreflightResponse | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  // 每次打开时清掉上次的文件、预检和提示（同上，放在渲染里）。
  const [wasOpen, setWasOpen] = useState(props.open);
  if (props.open !== wasOpen) {
    setWasOpen(props.open);
    if (props.open) {
      setFile(null);
      setPreflight(null);
      setMessage(null);
    }
  }

  const choose = async (next: File | null) => {
    setFile(next);
    setPreflight(null);
    setMessage(null);
    if (!next) return;
    setBusy(true);
    try {
      setPreflight(await templateTransfers.preflight(next));
    } catch (error) {
      setMessage(`不能导入 ${next.name}：${errorText(error)}`);
    } finally {
      setBusy(false);
    }
  };

  const submit = async () => {
    if (!preflight) return;
    if (!(await props.confirmDiscard())) return;
    setBusy(true);
    setMessage(null);
    try {
      if (!store.state.personal) await store.load();
      const revision = store.state.personal?.revision;
      if (!revision) throw new Error('读不到“我的”模板库。');
      const response = await templateTransfers.importArchive(revision, preflight);
      store.discardDraft();
      await store.showChanged(response.importedTemplates[0]?.templateId);
      toast(`已导入 ${response.importedTemplates.length} 个模板`);
      props.onClose();
    } catch (error) {
      if (isConflict(error)) {
        await store.load();
        setMessage('“我的”模板库已被修改，已重新读取。请再点一次“导入”。');
      } else {
        setMessage(errorText(error));
        if (error instanceof PreflightGoneError) setPreflight(null);
      }
    } finally {
      setBusy(false);
    }
  };

  const count = preflight?.templates.length ?? 0;
  return (
    <FormDialog
      open={props.open}
      headline="导入模板"
      description="从 .mktpl 归档导入到“我的”。"
      submitText={count > 0 ? `导入 ${count} 个模板` : '导入'}
      submitDisabled={!preflight}
      busy={busy}
      onSubmit={() => void submit()}
      onClose={props.onClose}
      className="transfer-dialog"
    >
      <FilePicker label="选择 .mktpl 文件" accept=".mktpl" file={file} onChange={(f) => void choose(f)} disabled={busy} />
      {busy && !preflight && file && <mdui-linear-progress />}
      {preflight && (
        <>
          <p className="hint">
            归档 {formatBytes(preflight.archiveBytes)}，{count} 个模板。导入后全部生成新 ID，不会覆盖已有模板。
          </p>
          <div className="transfer-list" role="list" aria-label="将导入的模板">
            {preflight.templates.map(({ summary, definitionAvailability }) => (
              <div key={summary.templateId} role="listitem" className="transfer-row">
                <span className="transfer-name">{summary.name}</span>
                <span className="target-meta">
                  {KIND_TEXT[summary.kind]}
                  {AVAILABILITY_TEXT[definitionAvailability] ? ` · ${AVAILABILITY_TEXT[definitionAvailability]}` : ''}
                </span>
              </div>
            ))}
          </div>
        </>
      )}
      {message && <p className="notice-error">{message}</p>}
    </FormDialog>
  );
}
