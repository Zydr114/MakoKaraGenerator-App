// 作者草稿的源码页签（W8 设计 §2.6）：左边是文件和修订，右边是代码编辑器和编译结果。编辑器（CodeMirror）第一次打开这个页签时才加载。
// 本页只接入已有 C# 文件编辑；没有 C# 源码时说明尚未接入包文件编辑。

import { Component, lazy, Suspense, useEffect, useRef, type ReactNode } from 'react';
import { compileOutcomeText, compileRows, type CompileRow, type SourceMarker } from '../model/compile-diagnostics';
import { shortRevision } from '../model/full-template';
import { confirmAction } from '../ui/legacy/dialogs';
import { toast } from '../ui/legacy/feedback';
import { useObservable } from '../ui/hooks';
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

  if (state.status === 'none') return <p className="hint draft-source-none">{NO_SOURCE_TEXT}</p>;
  if (state.status === 'idle' || state.status === 'loading') return <p className="hint">正在读取源码…</p>;
  if (state.status === 'failed')
    return (
      <div className="draft-source-failed">
        <p className="notice-error">{state.error}</p>
        <mdui-button variant="text" onClick={() => void model.load()}>
          重试
        </mdui-button>
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
    <section className="draft-source" aria-label="源码" data-revision={state.revision} data-dirty={dirty ? '' : undefined}>
      <aside className="draft-source-side">
        <h3 className="section-title">文件</h3>
        <ul className="source-files">
          {state.files.map((file) => (
            <li key={file.path}>
              <button
                type="button"
                className={`source-file${file.path === state.active ? ' active' : ''}`}
                data-path={file.path}
                data-dirty={file.text !== file.saved ? '' : undefined}
                onClick={() => model.select(file.path)}
              >
                <span className="source-dot" aria-label={file.text !== file.saved ? '改过' : undefined} />
                {file.path}
              </button>
            </li>
          ))}
        </ul>
        <h3 className="section-title">修订</h3>
        <ul className="source-revisions">
          {rows.map((row) => (
            <li key={row.revision} className={`source-revision${row.revision === state.revision ? ' shown' : ''}`} data-revision={row.revision}>
              <div className="source-revision-name">
                <code>{shortRevision(row.revision)}</code>
                <span className="muted">{revisionLabelText(row.labels)}</span>
              </div>
              <div className="source-revision-actions">
                <mdui-button variant="text" className="source-revision-view" disabled={busy || row.revision === state.revision} onClick={() => void view(row.revision)}>
                  查看
                </mdui-button>
                <mdui-button variant="text" className="source-revision-adopt" disabled={busy || row.revision === inUse} onClick={() => void adopt(row.revision)}>
                  改用此修订
                </mdui-button>
              </div>
            </li>
          ))}
        </ul>
        <p className="hint">后端不列出草稿保留的修订：这里只有原包、最新、已采用、在用和本页保存过的。</p>
        <div className="source-actions">
          <mdui-button variant="filled" className="source-save" loading={state.busy === 'saving'} disabled={busy || !dirty} onClick={() => void save()}>
            保存修订
          </mdui-button>
          <mdui-button variant="tonal" className="source-compile-button" loading={!!state.compiling} disabled={busy || !!state.compiling} onClick={() => void compile()}>
            {dirty ? '保存并编译' : `编译最新修订（${shortRevision(latest)}）`}
          </mdui-button>
        </div>
        {inUse !== latest && <p className="hint">编译的是最新修订；试样在用的是 {shortRevision(inUse)}。</p>}
      </aside>
      <div className="draft-source-main">
        <p className="hint source-shown">
          编辑器里是修订 {state.revision ? shortRevision(state.revision) : '?'}
          {shown && shown.labels.length > 0 ? `（${revisionLabelText(shown.labels)}）` : ''}
          {state.revision !== inUse ? ` · 试样在用的是 ${shortRevision(inUse)}` : ''}
          {dirty ? ' · 有改动没保存，Ctrl+S 保存为新修订' : ''}
        </p>
        {state.error && <p className="notice-error">{state.error}</p>}
        <div className="source-editor-frame">
          {active ? (
            <EditorBoundary>
              <Suspense fallback={<p className="hint">正在加载编辑器…</p>}>
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
            <p className="hint">这个修订没有 C# 文件。</p>
          )}
        </div>
        <section ref={compileRef} className="source-compile" aria-label="编译" data-outcome={compilation ? (compilation.succeeded ? 'passed' : 'failed') : undefined} data-stale={stale ? '' : undefined}>
          <div className="source-compile-head">
            <span className="source-compile-outcome">
              {compilation ? `编译 · ${shortRevision(compilation.sourceRevision)} · ${compileOutcomeText(compilation)}` : '还没有编译过'}
            </span>
            {stale && <span className="muted">已过期，需要重新编译</span>}
            {state.compiling && (
              <>
                <span className="muted">{state.compiling.cancelling ? '正在取消…' : '正在编译…'}</span>
                <mdui-button variant="text" disabled={!state.compiling.canCancel || state.compiling.cancelling} onClick={() => void model.cancelCompile()}>
                  取消
                </mdui-button>
              </>
            )}
          </div>
          {state.compileError && <p className="notice-error">{state.compileError}</p>}
          {rowsOf.length > 0 && (
            <ul className="compile-rows">
              {rowsOf.map((row, i) => (
                <li key={i}>
                  <button type="button" className="compile-row" data-severity={row.isError ? 'error' : 'warning'} disabled={!row.path} onClick={() => void jump(row)}>
                    {row.isError ? <mdui-icon-error className="compile-icon" /> : <mdui-icon-warning className="compile-icon" />}
                    <code>{row.code}</code>
                    {row.location && <span className="compile-location">{row.location}</span>}
                    <span className="compile-message">{row.message}</span>
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
        <p className="notice-error">代码编辑器出错了：{this.state.error instanceof Error ? this.state.error.message : String(this.state.error)}</p>
        <mdui-button
          variant="text"
          onClick={() => {
            SourceEditor = loadEditor();
            this.setState({ error: undefined });
          }}
        >
          重试
        </mdui-button>
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

/** 编辑器里有没保存成修订的改动时先确认。 */
export function confirmLoseEdits(action: string): Promise<boolean> {
  return confirmAction({ headline: `${action}？`, description: '源码页签里有改动还没保存成修订，继续的话这些改动会丢掉。', confirmText: '继续' });
}
