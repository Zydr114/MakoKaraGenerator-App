// 作者草稿的源码页签（W8 设计 §2.6）：左边是文件和修订，右边是代码编辑器和编译结果。编辑器（CodeMirror）第一次打开这个页签时才加载。
// 本页只接入已有 C# 文件编辑；没有 C# 源码时说明尚未接入包文件编辑。

import { Component, lazy, Suspense, useEffect, useRef, type ReactNode } from 'react';
import { compileOutcomeText, compileRows, type CompileRow, type SourceMarker } from '../model/compile-diagnostics';
import { shortRevision } from '../model/full-template';
import { Button, IconButton } from '../ui/Button';
import { cls } from '../ui/cls';
import { confirmAction } from '../ui/dialogs';
import { toast } from '../ui/feedback';
import { useObservable } from '../ui/hooks';
import { Icon } from '../ui/Icon';
import { Note, ReadError } from '../workspace/panel';
import type { FullDraft } from './author-draft-store';
import { revisionLabelText, revisionRows, type DraftSourceModel, type SourceResult } from './draft-source';

const loadEditor = () => lazy(() => import('./SourceEditor'));
/** lazy 会记住失败的加载，重试时换一个新的。 */
let SourceEditor = loadEditor();

export const NO_SOURCE_TEXT = '这个包没有 C# 源码（材质和图由着色器、图文件构成），当前页面尚未接入包文件编辑。';

export function DraftSourceTab(props: { draft: FullDraft; model: DraftSourceModel; disabled: boolean }) {
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
  const rows = revisionRows(draft, state.saved);
  const shown = rows.find((r) => r.revision === state.revision);
  const active = state.files.find((f) => f.path === state.active);
  const dirty = model.dirty;
  const busy = disabled || !!state.busy;

  const save = async () => report(await model.save());
  const view = async (revision: string) => {
    if (model.dirty && !(await confirmLoseEdits('查看别的修订'))) return;
    await model.load(revision);
  };
  const adopt = async (revision: string) => report(await model.adopt(revision));
  const compile = async () => {
    const saved = await model.compile();
    if (saved) report(saved);
  };
  const undoAdoption = async () => report(await model.undoAdoption());
  const redoAdoption = async () => report(await model.redoAdoption());

  const latest = draft.currentSourceRevision;
  const compilation = state.compilation;
  const rowsOf = compilation ? compileRows(compilation.diagnostics) : [];
  const stale = !!compilation && compilation.sourceRevision !== latest;
  // 编辑器里是编译的那个修订时，当前文件的诊断标在编辑器里。
  const markers: SourceMarker[] =
    compilation && active && compilation.sourceRevision === state.revision
      ? rowsOf.filter((d) => d.path === active.path).map((d) => ({ line: d.line, column: d.column, severity: d.isError ? 'error' : 'warning', message: `${d.code} ${d.message}` }))
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
          {state.files.map((file) => (
            <li key={file.path}>
              <button
                type="button"
                className={cls('fitem', 'source-file', file.path === state.active && 'active')}
                data-path={file.path}
                data-dirty={file.text !== file.saved ? '' : undefined}
                aria-current={file.path === state.active ? 'true' : undefined}
                title={file.path}
                onClick={() => model.select(file.path)}
              >
                <Icon name="code" />
                <span className="fitem-name">{baseName(file.path)}</span>
                {file.text !== file.saved && <i className="udot source-dot" aria-label="改过" />}
              </button>
            </li>
          ))}
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
                  icon="check_circle"
                  size="sm"
                  label={`${model.isV2 ? '采用' : '改用'}修订 ${shortRevision(row.revision)}`}
                  tooltip={model.isV2 ? '采用此修订（须编译通过）' : '改用此修订'}
                  className="source-revision-adopt"
                  isDisabled={busy || row.revision === inUse}
                  onPress={() => void adopt(row.revision)}
                />
              </span>
            </li>
          ))}
        </ul>
        <Note className="srcside-note">这里列出原包、最新、已采用、在用和本页保存过的修订。</Note>
        {model.isV2 && <Note className="srcside-note">保存和编译均不会改变在用修订。查看任一修订后可编译，编译通过再采用；撤销采用的目标也须先编译通过。</Note>}
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
          {model.isV2 && <>
            <IconButton icon="undo" size="sm" label="撤销源码采用" isDisabled={busy || !state.canUndoAdoption} onPress={() => void undoAdoption()} />
            <IconButton icon="redo" size="sm" label="重做源码采用" isDisabled={busy || !state.canRedoAdoption} onPress={() => void redoAdoption()} />
          </>}
          <Button variant="filled" size="sm" icon="save" className="source-save" isPending={state.busy === 'saving'} isDisabled={busy || !dirty} onPress={() => void save()}>
            保存修订
          </Button>
          <Button variant="tonal" size="sm" icon="build" className="source-compile-button" isPending={!!state.compiling} isDisabled={busy || !!state.compiling} onPress={() => void compile()}>
            {dirty ? '保存并编译' : model.isV2 ? '编译查看的修订' : '编译'}
          </Button>
        </div>
        {state.error && <Note error>{state.error}</Note>}
        <div className="source-editor-frame">
          {active ? (
            <EditorBoundary>
              <Suspense fallback={<Note className="pintro">正在加载编辑器…</Note>}>
                <SourceEditor
                  docKey={`${draft.draftId}|${state.generation}|${active.path}`}
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
            <Note className="pintro">这个修订没有 C# 文件。</Note>
          )}
        </div>
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
            {stale && <span className="diag-stale">{model.isV2 ? '此结果属于旧修订，最新源码尚未由此结果验证' : '已过期，需要重新编译'}</span>}
            <span className="faint">
              {state.compiling
                ? state.compiling.cancelling
                  ? '正在取消…'
                  : '正在编译…'
                : model.isV2
                  ? `编译目标 ${shortRevision(state.revision ?? latest)}；在用 ${shortRevision(inUse)}`
                  : inUse !== latest
                  ? `编译的是最新修订 ${shortRevision(latest)}；试样在用的是 ${shortRevision(inUse)}`
                  : ''}
            </span>
            {state.compiling && (
              <Button variant="text" size="sm" isDisabled={!state.compiling.canCancel || state.compiling.cancelling} onPress={() => void model.cancelCompile()}>
                取消
              </Button>
            )}
          </header>
          {state.compileError && <Note error>{state.compileError}</Note>}
          {model.isV2 && compilation?.succeeded && compilation.sourceRevision !== inUse && (
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
      toast(result.created ? `已保存为修订 ${shortRevision(result.revision)}，试样已改用它` : `内容和修订 ${shortRevision(result.revision)} 相同，试样已改用它`);
      break;
    case 'unchanged':
      toast('内容没有变化');
      break;
    case 'adopted':
      toast(`已改用修订 ${shortRevision(result.revision)}，试样跟着换了`);
      break;
    case 'failed':
      toast(result.message);
      break;
    case 'busy':
      break;
  }
}

function baseName(path: string): string {
  return path.slice(path.lastIndexOf('/') + 1);
}

/** 编辑器里有没保存成修订的改动时先确认。 */
export function confirmLoseEdits(action: string): Promise<boolean> {
  return confirmAction({ headline: `${action}？`, description: '源码页签里有改动还没保存成修订，继续的话这些改动会丢掉。', confirmText: '继续' });
}
