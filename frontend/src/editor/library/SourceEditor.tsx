// 源码页签的代码编辑器（W8 设计 §2.6，§9 决定 1）：CodeMirror 6 的一层 React 包装，只带 C# 高亮、行号、括号匹配、撤销历史、Tab 缩进和诊断标记，
// 不带补全、搜索面板和格式化。源码页签第一次打开时才动态 import，打成单独的分块。
// 每个文件的 EditorState（内容、撤销历史、光标）寄存在 DraftSourceModel 里，切文件、切页签、切草稿都不丢。配色取主题令牌，跟随浅色和深色主题。

import { defaultKeymap, history, historyKeymap, indentWithTab } from '@codemirror/commands';
import { bracketMatching, HighlightStyle, indentOnInput, StreamLanguage, syntaxHighlighting } from '@codemirror/language';
import { csharp } from '@codemirror/legacy-modes/mode/clike';
import { lintGutter, setDiagnostics, type Diagnostic } from '@codemirror/lint';
import { EditorState } from '@codemirror/state';
import { drawSelection, EditorView, highlightActiveLine, highlightActiveLineGutter, highlightSpecialChars, keymap, lineNumbers } from '@codemirror/view';
import { tags } from '@lezer/highlight';
import { useEffect, useRef } from 'react';
import { diagnosticRange, type SourceMarker } from '../model/compile-diagnostics';

export interface SourceEditorProps {
  /** 文件身份（草稿、读进来的代次、路径）；变了就换成这个文件的编辑器状态。 */
  docKey: string;
  /** 没有寄存的状态时用来新建。 */
  text: string;
  /** 寄存的编辑器状态，建编辑器时才读（StrictMode 重新挂载时要拿到上一个编辑器刚寄存的状态）。 */
  keptState(): unknown;
  onChange(text: string): void;
  onKeep(state: unknown): void;
  onSave(): void;
  /** 当前文件的编译诊断（编辑器里是编译的那个修订时才给）。 */
  markers: readonly SourceMarker[];
  /** 标记的身份（编译结果、修订）；变了才重新标。之后的编辑由 CodeMirror 把标记跟着挪。 */
  markersKey: string;
  /** 跳到这里并选中；nonce 变了才跳。 */
  reveal: { line: number; column: number; nonce: number } | undefined;
  /** 跳过了：之后重新挂载（切回页签）不再跳。 */
  onRevealed(nonce: number): void;
}

const theme = EditorView.theme({
  '&': {
    height: '100%',
    fontSize: '13px',
    color: 'var(--on-surface)',
    backgroundColor: 'var(--field)',
  },
  '.cm-scroller': { fontFamily: 'var(--font-mono)', lineHeight: '1.5' },
  '.cm-content': { caretColor: 'var(--primary)' },
  '&.cm-focused': { outline: 'none' },
  '&.cm-focused .cm-cursor': { borderLeftColor: 'var(--primary)' },
  '.cm-gutters': {
    backgroundColor: 'var(--field)',
    color: 'var(--on-surface-variant)',
    borderRight: '1px solid var(--line)',
  },
  '.cm-activeLine': { backgroundColor: 'color-mix(in srgb, var(--primary) 6%, transparent)' },
  '.cm-activeLineGutter': { backgroundColor: 'color-mix(in srgb, var(--primary) 10%, transparent)' },
  '.cm-selectionBackground, &.cm-focused > .cm-scroller > .cm-selectionLayer .cm-selectionBackground': {
    backgroundColor: 'color-mix(in srgb, var(--primary) 22%, transparent)',
  },
  '.cm-matchingBracket, &.cm-focused .cm-matchingBracket': { backgroundColor: 'color-mix(in srgb, var(--tertiary) 25%, transparent)', outline: 'none' },
  '.cm-nonmatchingBracket, &.cm-focused .cm-nonmatchingBracket': { backgroundColor: 'color-mix(in srgb, var(--error) 25%, transparent)' },
});

/** 颜色取 library.css 里的 --mk-code-* 变量（浅色、深色两套）。 */
const highlight = HighlightStyle.define([
  { tag: [tags.keyword, tags.controlKeyword, tags.modifier], color: 'var(--mk-code-keyword)' },
  { tag: [tags.string, tags.special(tags.string), tags.character], color: 'var(--mk-code-string)' },
  { tag: [tags.comment, tags.lineComment, tags.blockComment], color: 'var(--mk-code-comment)', fontStyle: 'italic' },
  { tag: [tags.number, tags.bool, tags.null, tags.atom], color: 'var(--mk-code-number)' },
  { tag: [tags.typeName, tags.className, tags.namespace], color: 'var(--mk-code-type)' },
  { tag: [tags.definition(tags.variableName), tags.function(tags.variableName)], color: 'var(--mk-code-definition)' },
  { tag: [tags.meta, tags.annotation, tags.processingInstruction], color: 'var(--mk-code-meta)' },
  { tag: tags.standard(tags.variableName), color: 'var(--mk-code-builtin)' },
]);

const language = StreamLanguage.define(csharp);

export default function SourceEditor(props: SourceEditorProps) {
  const host = useRef<HTMLDivElement>(null);
  const view = useRef<EditorView | null>(null);
  const latest = useRef(props);
  latest.current = props;

  /** 已经跳过的 nonce。卸载时清掉：StrictMode 重新挂载后的编辑器要再跳一次（并拿到焦点）。 */
  const revealed = useRef<number | undefined>(undefined);

  const stateFor = (p: SourceEditorProps): EditorState => {
    const saved = p.keptState();
    const kept = saved instanceof EditorState ? saved : undefined;
    if (kept && kept.doc.toString() === p.text) return kept;
    return EditorState.create({
      doc: p.text,
      extensions: [
        lineNumbers(),
        highlightActiveLineGutter(),
        highlightSpecialChars(),
        history(),
        drawSelection(),
        indentOnInput(),
        bracketMatching(),
        highlightActiveLine(),
        lintGutter(),
        language,
        syntaxHighlighting(highlight),
        theme,
        EditorState.tabSize.of(4),
        keymap.of([
          {
            key: 'Mod-s',
            preventDefault: true,
            run: () => {
              latest.current.onSave();
              return true;
            },
          },
          indentWithTab,
          ...defaultKeymap,
          ...historyKeymap,
        ]),
        EditorView.updateListener.of((update) => {
          if (update.docChanged) latest.current.onChange(update.state.doc.toString());
          latest.current.onKeep(update.state);
        }),
      ],
    });
  };

  useEffect(() => {
    const editor = new EditorView({ parent: host.current!, state: stateFor(latest.current) });
    view.current = editor;
    return () => {
      latest.current.onKeep(editor.state);
      editor.destroy();
      view.current = null;
      revealed.current = undefined;
    };
  }, []);

  // 换文件（或读进了别的修订）：先寄存当前文件的状态，再换成新文件的。
  const shownKey = useRef(props.docKey);
  useEffect(() => {
    const editor = view.current;
    if (!editor || shownKey.current === props.docKey) return;
    shownKey.current = props.docKey;
    editor.setState(stateFor(props));
  }, [props.docKey]);

  // 诊断标记：换文件或换了编译结果时重新标。
  useEffect(() => {
    const editor = view.current;
    if (!editor) return;
    const text = editor.state.doc.toString();
    const diagnostics: Diagnostic[] = props.markers.map((m) => ({ ...diagnosticRange(text, m.line, m.column), severity: m.severity, message: m.message }));
    editor.dispatch(setDiagnostics(editor.state, diagnostics));
  }, [props.docKey, props.markersKey]);

  // 跳转：选中诊断所在的位置并滚到中间。
  useEffect(() => {
    const editor = view.current;
    const reveal = props.reveal;
    if (!editor || !reveal || revealed.current === reveal.nonce) return;
    revealed.current = reveal.nonce;
    const range = diagnosticRange(editor.state.doc.toString(), reveal.line, reveal.column);
    editor.dispatch({ selection: { anchor: range.from, head: range.to }, effects: EditorView.scrollIntoView(range.from, { y: 'center' }) });
    editor.focus();
    props.onRevealed(reveal.nonce);
  }, [props.docKey, props.reveal?.nonce]);

  return <div ref={host} className="source-editor" data-doc={props.docKey} />;
}
