// 作者草稿的源码页签（W8 设计 §2.6）：左边是文件和修订，右边是代码编辑器和编译结果。编辑器（CodeMirror）第一次打开这个页签时才加载。
// 完整包文件入口保留旧 C# 模型兼容；manifest 与服务标为 editable 的文本共用编辑器，二进制只读。

import { Component, lazy, Suspense, useEffect, useRef, type ReactNode } from 'react';
import { compileOutcomeText, compileRows, type CompileRow, type SourceMarker } from '../model/compile-diagnostics';
import { shortRevision } from '../model/full-template';
import { Button, IconButton } from '../ui/Button';
import { cls } from '../ui/cls';
import { confirmAction } from '../ui/dialogs';
import { FileButton } from '../ui/fields';
import { saveBlob } from '../ui/download';
import { toast } from '../ui/feedback';
import { useObservable } from '../ui/hooks';
import { Icon } from '../ui/Icon';
import { Note, ReadError } from '../workspace/panel';
import type { FullDraft } from './author-draft-store';
import { baseName, revisionLabelText, revisionRows, sourceLanguage, type DraftSourceModel, type SourceFile, type SourceResult } from './draft-source';

const loadEditor = () => lazy(() => import('./SourceEditor'));
/** lazy 会记住失败的加载，重试时换一个新的。 */
let SourceEditor = loadEditor();

export const NO_SOURCE_TEXT = '这个包没有 C# 源码，当前连接使用旧源码接口；完整包文件入口不可用。';

export function DraftSourceTab(props: { draft: FullDraft; model: DraftSourceModel; disabled: boolean; onPrepareAdoption?: (revision: string) => void | Promise<void> }) {
  const { draft, model, disabled } = props;
  const state = useObservable(model.state$);
  useEffect(() => {
    if (state.status === 'idle') void model.load();
  }, [model, state.status]);
  // 编译出了新结果：把结果滚进视野（编辑器下方，可能在屏幕外）。
  const compileRef = useRef<HTMLElement>(null);
  const shownCompilation = useRef(state.compilation);
  useEffect(() => {
    if (state.compilation === shownCompilation.current) return;
    shownCompilation.current = state.compilation;
    compileRef.current?.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
  }, [state.compilation]);

  if (state.status === 'none') return <Note className="pintro draft-source-none">{NO_SOURCE_TEXT}</Note>;
  if (state.status === 'idle' || state.status === 'loading') return <Note className="pintro">正在读取源码…</Note>;
  if (state.status === 'failed')
    return (
      <div className="draft-source-failed">
        <ReadError error={state.error} onRetry={() => void model.load()} />
      </div>
    );

  const inUse = draft.configuration.sourceRevision;
  const rows = revisionRows(draft, state.saved, state.retained);
  const shown = rows.find((r) => r.revision === state.revision);
  const active = state.files.find((f) => f.path === state.active);
  const dirty = model.dirty;
  const busy = disabled || !!state.busy;

  const reportResult = (result: SourceResult) => report(result);
  const save = async () => reportResult(await model.save());
  const view = async (revision: string) => {
    if (model.dirty && !(await confirmLoseEdits('查看别的修订'))) return;
    await model.load(revision);
  };
  const adopt = async (revision: string) => {
    if (props.onPrepareAdoption) await props.onPrepareAdoption(revision);
    else reportResult(await model.adopt(revision));
  };
  const compile = async () => {
    const saved = await model.compile();
    if (saved) reportResult(saved);
  };
  const download = async (revision: string) => {
    try {
      saveBlob(await model.downloadPackage(revision), packageFileName(draft.configuration.name, revision));
    } catch (error) {
      toast(`下载效果包失败：${error instanceof Error ? error.message : String(error)}`);
    }
  };
  const replace = async (path: string, file: File) => {
    const target = state.files.find((f) => f.path === path);
    const svg = !!target && model.replaceKind(target) === 'svg';
    if ((await model.replaceFile(path, file, file.name)) && svg) toast(`已把 ${file.name} 的内容放进编辑器，保存修订后生效。`);
  };
  const undoAdoption = async () => reportResult(await model.undoAdoption());
  const redoAdoption = async () => reportResult(await model.redoAdoption());

  const latest = draft.currentSourceRevision;
  const compilation = state.compilation;
  const rowsOf = compilation ? compileRows(compilation.diagnostics) : [];
  const stale = !!compilation && compilation.sourceRevision !== latest;
  // 编辑器里是编译的那个修订时，当前文件的诊断标在编辑器里。
  const markers: SourceMarker[] =
    compilation && active && compilation.sourceRevision === state.revision
      ? rowsOf.filter((d) => d.path === active.path && d.line > 0 && d.column > 0).map((d) => ({ line: d.line, column: d.column, severity: d.isError ? 'error' : 'warning', message: `${d.code} ${d.message}` }))
      : [];
  const jump = async (row: CompileRow) => {
    if (!compilation || !row.path) return;
    if (state.revision !== compilation.sourceRevision && model.dirty && !(await confirmLoseEdits('读进编译的修订'))) return;
    if (!(await model.goTo(compilation.sourceRevision, { path: row.path, line: row.line, column: row.column }))) toast('编译的修订里没有这个文件。');
  };

  return (
    <section className="srcwrap draft-source" aria-label="源码" data-revision={state.revision} data-dirty={dirty ? '' : undefined}>
      <aside className="srcside draft-source-side">
        <h5>文件</h5>
        <ul className="source-files">
          {state.files.map((file) => {
            const replaceable = model.replaceKind(file) === 'binary';
            const status = file.replacement ? '待替换' : replaceable ? '可替换' : file.editable === false ? '只读' : '';
            return (
              <li key={file.path}>
                <button
                  type="button"
                  className={cls('fitem', 'source-file', file.path === state.active && 'active')}
                  data-path={file.path}
                  data-dirty={file.text !== file.saved || file.replacement ? '' : undefined}
                  aria-current={file.path === state.active ? 'true' : undefined}
                  title={`${file.path}${file.role ? ` · ${file.role}` : ''}${file.replacement ? ` · 待替换为 ${file.replacement.fileName}` : status ? ` · ${status}` : ''}`}
                  aria-label={`${file.path}${status ? `，${status}` : ''}`}
                  onClick={() => model.select(file.path)}
                >
                  <Icon name={file.replacement ? 'swap_horiz' : replaceable ? (file.role === 'font' ? 'font_download' : 'image') : file.editable === false ? 'lock' : 'code'} />
                  <span className="fitem-name">{baseName(file.path)}</span>
                  {(file.text !== file.saved || file.replacement) && <i className="udot source-dot" aria-label="改过" />}
                </button>
              </li>
            );
          })}
        </ul>
        <h5>修订</h5>
        <ul className="source-revisions">
          {rows.map((row) => (
            <li key={row.revision} className={cls('rev', 'source-revision', row.revision === state.revision && 'shown')} data-revision={row.revision}>
              <span className="rev-text source-revision-name">
                <b>{shortRevision(row.revision)}</b>
                <span>{revisionLabelText(row.labels)}</span>
              </span>
              <span className="rev-tools source-revision-actions">
                <IconButton
                  icon="visibility"
                  size="sm"
                  label={`查看修订 ${shortRevision(row.revision)}`}
                  tooltip="查看"
                  className="source-revision-view"
                  isDisabled={busy || row.revision === state.revision}
                  onPress={() => void view(row.revision)}
                />
                <IconButton
                  icon="download"
                  size="sm"
                  label={`下载修订 ${shortRevision(row.revision)} 的效果包`}
                  tooltip="下载 .mkfx"
                  className="source-revision-download"
                  isDisabled={disabled}
                  onPress={() => void download(row.revision)}
                />
                <IconButton
                  icon="check_circle"
                  size="sm"
                  label={`采用修订 ${shortRevision(row.revision)}`}
                  tooltip={draft.canCompile ? '采用此修订（须编译通过）' : '采用此固定资源修订'}
                  className="source-revision-adopt"
                  isDisabled={busy || row.revision === inUse}
                  onPress={() => void adopt(row.revision)}
                />
              </span>
            </li>
          ))}
        </ul>
        <Note className="srcside-note">这里列出草稿保留的全部修订，可查看、采用或下载为 .mkfx。</Note>
        {state.historyError && <Note error className="srcside-note">{state.historyError}
          <Button variant="text" size="sm" onPress={() => void model.loadHistory()}>重试</Button></Note>}
        <Note className="srcside-note">保存不会改变在用修订。{draft.canCompile
          ? '查看任一修订后可编译，编译通过再采用；撤销采用的目标也须先编译通过。'
          : '纯资源修订可按包门禁显式采用，实际运行使用验证页签。'}</Note>
        {state.packageFiles && <Note className="srcside-note">manifest 与可编辑文本一起保存；图片、字体可整份换成同一格式的文件，SVG 可用文件替换文本，其余二进制只读。保存会检查包结构，编译或资源运行结果另行显示。</Note>}
      </aside>
      <div className="srcmain draft-source-main">
        <div className="srcbar">
          <b className="srcbar-file" title={active?.path}>
            {active ? baseName(active.path) : '没有文件'}
          </b>
          <span className="small faint source-shown" title="Ctrl+S 保存为新修订">
            修订 {state.revision ? shortRevision(state.revision) : '?'}
            {shown && shown.labels.length > 0 ? `（${revisionLabelText(shown.labels)}）` : ''}
            {dirty ? '，有改动没保存' : ''}
          </span>
          <span className="spacer" />
          {active && model.replaceKind(active) === 'svg' && (
            <FileButton label="用文件替换…" accept=".svg,image/svg+xml" variant="text" size="sm" icon="upload_file" disabled={busy || !!state.replacing}
              onPick={(file) => void replace(active.path, file)} />
          )}
          <IconButton icon="undo" size="sm" label="撤销源码采用" isDisabled={busy || !state.canUndoAdoption} onPress={() => void undoAdoption()} />
          <IconButton icon="redo" size="sm" label="重做源码采用" isDisabled={busy || !state.canRedoAdoption} onPress={() => void redoAdoption()} />
          <Button variant="filled" size="sm" icon="save" className="source-save" isPending={state.busy === 'saving'} isDisabled={busy || !dirty} onPress={() => void save()}>
            保存修订
          </Button>
          <Button variant="tonal" size="sm" icon="build" className="source-compile-button" isPending={!!state.compiling} isDisabled={busy || !!state.compiling || !draft.canCompile} onPress={() => void compile()}>
            {dirty ? '保存并编译' : '编译查看的修订'}
          </Button>
        </div>
        {state.error && <Note error>{state.error}</Note>}
        {state.replaceError && active?.editable !== false && state.replaceError.path === active?.path && <Note error>{state.replaceError.message}</Note>}
        <div className="source-editor-frame">
          {active?.editable === false ? (
            <BinaryPane file={active} model={model} replacing={state.replacing} replaceError={state.replaceError}
              disabled={busy} onReplace={(file) => void replace(active.path, file)} />
          ) : active ? (
            <EditorBoundary>
              <Suspense fallback={<Note className="pintro">正在加载编辑器…</Note>}>
                <SourceEditor
                  docKey={`${draft.draftId}|${state.generation}|${active.path}|${sourceLanguage(active)}`}
                  language={sourceLanguage(active)}
                  text={active.text}
                  keptState={() => model.viewState(active.path)}
                  onChange={(text) => model.edit(active.path, text)}
                  onKeep={(view) => model.keepViewState(active.path, view)}
                  onSave={() => void save()}
                  markers={markers}
                  markersKey={`${compilation?.sourceRevision ?? ''}|${compilation?.artifactHash ?? ''}|${rowsOf.length}|${state.revision ?? ''}`}
                  reveal={state.reveal?.path === active.path ? state.reveal : undefined}
                  onRevealed={(nonce) => model.revealed(nonce)}
                />
              </Suspense>
            </EditorBoundary>
          ) : (
            <Note className="pintro">这个修订没有可显示的文件。</Note>
          )}
        </div>
        {!draft.canCompile && <Note>这个包没有可编译的 C# 入口，文件仍可编辑和保存；资源运行验证使用验证页签。</Note>}
        <section
          ref={compileRef}
          className="diag source-compile"
          aria-label="编译"
          data-outcome={compilation ? (compilation.succeeded ? 'passed' : 'failed') : undefined}
          data-stale={stale ? '' : undefined}
        >
          <header>
            {compilation && <Icon name={compilation.succeeded ? 'check_circle' : 'error'} className={compilation.succeeded ? 'ok' : 'bad'} />}
            <span className="source-compile-outcome">
              {compilation ? `编译 · 修订 ${shortRevision(compilation.sourceRevision)} · ${compileOutcomeText(compilation)}` : '还没有编译过'}
            </span>
            {stale && <span className="diag-stale">此结果属于旧修订，最新源码尚未由此结果验证</span>}
            <span className="faint">
              {state.compiling
                ? state.compiling.cancelling
                  ? '正在取消…'
                  : '正在编译…'
                : `编译目标 ${shortRevision(state.revision ?? latest)}；在用 ${shortRevision(inUse)}`}
            </span>
            {state.compiling && (
              <Button variant="text" size="sm" isDisabled={!state.compiling.canCancel || state.compiling.cancelling} onPress={() => void model.cancelCompile()}>
                取消
              </Button>
            )}
          </header>
          {state.compileError && <Note error>{state.compileError}</Note>}
          {compilation?.succeeded && compilation.sourceRevision !== inUse && (
            <Button variant="tonal" size="sm" isDisabled={busy || !!state.compiling} onPress={() => void adopt(compilation.sourceRevision)}>
              采用编译通过的修订 {shortRevision(compilation.sourceRevision)}
            </Button>
          )}
          {rowsOf.length > 0 && (
            <ul className="compile-rows">
              {rowsOf.map((row, i) => (
                <li key={i}>
                  <button type="button" className="dline compile-row" data-severity={row.isError ? 'error' : 'warning'} disabled={!row.path} onClick={() => void jump(row)}>
                    <Icon name={row.isError ? 'error' : 'warning'} className="compile-icon" />
                    {row.location && <span className="loc compile-location">{row.location}</span>}
                    <span className="compile-message">
                      <code>{row.code}</code> {row.message}
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </section>
      </div>
    </section>
  );
}

/** 编辑器分块加载失败或编辑器出错时只在这里写明原因，不让整个页面卸掉；编辑内容留在模型里，重试不丢。 */
class EditorBoundary extends Component<{ children: ReactNode }, { error?: unknown }> {
  override state: { error?: unknown } = {};

  static getDerivedStateFromError(error: unknown) {
    return { error };
  }

  override render() {
    if (this.state.error === undefined) return this.props.children;
    return (
      <div className="draft-source-failed">
        <ReadError
          error={`代码编辑器出错了：${this.state.error instanceof Error ? this.state.error.message : String(this.state.error)}`}
          onRetry={() => {
            SourceEditor = loadEditor();
            this.setState({ error: undefined });
          }}
        />
      </div>
    );
  }
}

function report(result: SourceResult): void {
  switch (result.kind) {
    case 'saved':
      toast(`已保存修订 ${shortRevision(result.revision)}；在用修订保持，采用后才用于试样`);
      break;
    case 'unchanged':
      toast('内容没有变化');
      break;
    case 'adopted':
      toast(`已采用修订 ${shortRevision(result.revision)}，试样跟着换了`);
      break;
    case 'failed':
      toast(result.message);
      break;
    case 'busy':
      break;
  }
}

/** 下载文件名：组件名＋修订短号；去掉文件名里不能用的字符。 */
export const REPLACE_ACCEPT: Record<string, string> = {
  image: 'image/*,.png,.jpg,.jpeg,.webp,.gif,.bmp',
  font: '.ttf,.otf,.ttc,.otc,font/*',
};

/** 包内二进制文件：图片、字体可整份替换成同一格式的本机文件，保存修订时一起提交；其余只读。 */
function BinaryPane(props: { file: SourceFile; model: DraftSourceModel; replacing: string | undefined; replaceError: { path: string; message: string } | undefined;
  disabled: boolean; onReplace: (file: File) => void }) {
  const { file, model } = props;
  const replaceable = model.replaceKind(file) === 'binary';
  const pending = file.replacement;
  const working = props.replacing === file.path;
  return (
    <div className="source-binary" aria-label={replaceable ? '包资源' : '只读包资源'} data-replaced={pending ? '' : undefined}>
      <Note>{replaceable
        ? '可以整份换成同一格式的本机文件（不能改格式、改名或删除），保存修订时与文本一起提交。'
        : '这个文件以固定原始字节保留，当前包文本入口只读。'}</Note>
      <dl><dt>路径</dt><dd>{file.path}</dd><dt>角色</dt><dd>{file.role}</dd>
        <dt>大小</dt><dd>{file.sizeBytes} 字节</dd><dt>SHA-256</dt><dd>{file.sha256}</dd></dl>
      {replaceable && (
        <div className="source-replace-actions">
          <FileButton label={pending ? '换一个文件…' : '替换…'} accept={REPLACE_ACCEPT[file.role ?? ''] ?? '*/*'} size="sm" icon="upload_file"
            disabled={props.disabled || !!props.replacing} onPick={props.onReplace} />
          {pending && <Button variant="text" size="sm" icon="undo" className="source-replace-revert" isDisabled={props.disabled}
            onPress={() => model.revertReplacement(file.path)}>撤销替换</Button>}
          {working && <span className="small faint">正在上传并检查…</span>}
        </div>
      )}
      {props.replaceError?.path === file.path && <Note error>{props.replaceError.message}</Note>}
      {pending && <ReplacementSummary pending={pending} />}
    </div>
  );
}

function ReplacementSummary(props: { pending: NonNullable<SourceFile['replacement']> }) {
  const { inspection, fileName } = props.pending;
  const rows: [string, string, string][] = [
    ['格式', inspection.current.format, inspection.replacement.format],
    ['大小', `${inspection.current.sizeBytes} 字节`, `${inspection.replacement.sizeBytes} 字节`],
    ...(inspection.role === 'image'
      ? [['尺寸', dimensions(inspection.current), dimensions(inspection.replacement)] as [string, string, string]]
      : [['字体家族', (inspection.current.familyNames ?? []).join('、'), (inspection.replacement.familyNames ?? []).join('、')] as [string, string, string]]),
    ['SHA-256', inspection.current.sha256.slice(0, 12), inspection.replacement.sha256.slice(0, 12)],
  ];
  return (
    <section className="source-replace-summary" aria-label="待保存的替换">
      <h6>待保存：换成 {fileName}</h6>
      <table>
        <thead><tr><th /><th>原文件</th><th>新文件</th></tr></thead>
        <tbody>{rows.map(([label, before, after]) => (
          <tr key={label} data-changed={before !== after ? '' : undefined}><th>{label}</th><td>{before}</td><td>{after}</td></tr>
        ))}</tbody>
      </table>
      {inspection.warnings.length > 0 && (
        <ul className="source-replace-warnings">
          {inspection.warnings.map((warning) => (
            <li key={warning.code} data-code={warning.code}><Icon name="warning" />{warning.message}</li>
          ))}
        </ul>
      )}
    </section>
  );
}

function dimensions(info: { width?: number; height?: number }): string {
  return info.width !== undefined && info.height !== undefined ? `${info.width}×${info.height}` : '?';
}

function packageFileName(name: string, revision: string): string {
  const safe = name.replace(/[\\/:*?"<>|\u0000-\u001f]/g, '_').trim() || 'effect';
  return `${safe}-${shortRevision(revision)}.mkfx`;
}


/** 编辑器里有没保存成修订的改动时先确认。 */
export function confirmLoseEdits(action: string): Promise<boolean> {
  return confirmAction({ headline: `${action}？`, description: '源码页签里有改动还没保存成修订，继续的话这些改动会丢掉。', confirmText: '继续' });
}
