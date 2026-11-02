import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { TokenTarget } from '../../protocol';
import type { AppearanceStore } from '../appearance/appearance-store';
import type { AudioController } from '../audio/audio-controller';
import { NoAudioBar } from '../audio/NoAudioBar';
import { Transport } from '../audio/Transport';
import { useLineActions } from '../lyrics/line-actions';
import { LineDetail } from '../lyrics/LineDetail';
import { LinePane } from '../lyrics/LinePane';
import { TimeBar } from '../lyrics/TimeBar';
import { WordPreview } from '../preview/WordPreview';
import { followSpans, lineTracks } from '../model/line-tracks';
import { isTimed, stepToken, type EditorLine, type Selection } from '../model/lyrics';
import { translationsByPrimary } from '../model/word-preview';
import { toMicros } from '../model/time';
import type { OrchestrationStore } from '../orchestration/orchestration-store';
import type { EditorSession } from '../session/editor-session';
import { Timeline, type RubyTimeEdit, type TokenTimeEdit } from '../timeline/Timeline';
import { useObservable } from '../ui/hooks';
import { StatusBar } from './StatusBar';
import { WorkLayout } from './WorkLayout';

const PREVIEW_COLLAPSED_KEY = 'mako.editor.wordPreviewCollapsed';

/**
 * 歌词页（整体设计 §5.2，界面改版 §2.6）：左栏行列表，中栏行编辑（下面是时间栏），右栏逐字预览；
 * 底部区是播放条与时间轴。逐字预览是幕后，和底部区连成 L 形。
 */
export function Workspace(props: {
  session: EditorSession;
  audio: AudioController | null;
  /** 行编辑区的“外观”“显示在”用：分配列表与编排结构（还没建好时为 null，这两项不显示）。 */
  appearance: AppearanceStore | null;
  orchestration: OrchestrationStore | null;
  onOpen: (page: 'appearance' | 'orchestration') => void;
  onUndo: () => void;
  onRedo: () => void;
  onSave: () => void;
}) {
  const { session, audio } = props;
  const state = useObservable(session.state$);
  const { actions, dialogs } = useLineActions(session, props.onUndo);
  const [loopLine, setLoopLine] = useState(false);
  const [stampMode, setStampMode] = useState(false);
  const selectedLineIndex = state.selection ? state.lines.findIndex((l) => l.id === state.selection!.lineId) : -1;
  const selectedLine = selectedLineIndex >= 0 ? state.lines[selectedLineIndex] : undefined;
  const stampAvailable = session.supportsCommand('lyrics.stampTokenStart') && session.supportsCommand('lyrics.stampTokenEnd');
  const [previewCollapsed, setPreviewCollapsed] = useState(() => localStorage.getItem(PREVIEW_COLLAPSED_KEY) === '1');
  const collapsePreview = useCallback((collapsed: boolean) => {
    setPreviewCollapsed(collapsed);
    localStorage.setItem(PREVIEW_COLLAPSED_KEY, collapsed ? '1' : '0');
  }, []);

  // 逐字预览以当前来源为准；选中翻译来源时改看主歌词，翻译显示在关联行下方。
  const branches = state.branches;
  const previewLines = useMemo(() => {
    const selected = branches?.find((b) => b.id === state.branchId);
    return selected?.role === 'translation' && state.primaryBranchId ? (state.linesByBranch[state.primaryBranchId] ?? []) : state.lines;
  }, [branches, state.branchId, state.primaryBranchId, state.linesByBranch, state.lines]);
  const translations = useMemo(
    () => translationsByPrimary((branches ?? []).filter((b) => b.role === 'translation').flatMap((b) => state.linesByBranch[b.id] ?? [])),
    [branches, state.linesByBranch],
  );

  // 时间轴行轨下面：看主歌词时是各翻译来源的行轨，看其他来源时是主歌词参考轨；跟随主歌词的行画在主歌词行的位置。
  const tracks = useMemo(
    () => lineTracks({ branches, primaryBranchId: state.primaryBranchId, branchId: state.branchId, linesByBranch: state.linesByBranch }),
    [branches, state.primaryBranchId, state.branchId, state.linesByBranch],
  );
  const follows = useMemo(
    () => followSpans(branches?.find((b) => b.id === state.branchId), state.lines, state.primaryBranchId ? (state.linesByBranch[state.primaryBranchId] ?? []) : []),
    [branches, state.branchId, state.lines, state.primaryBranchId, state.linesByBranch],
  );

  // 第一次进入时选中第一行。
  useEffect(() => {
    if (!state.selection && state.lines[0]) session.select({ lineId: state.lines[0].id });
  }, [session, state.selection, state.lines]);

  // 循环本行：跟随选中行。
  useEffect(() => {
    if (!audio) return;
    audio.setLoop(loopLine && selectedLine && isTimed(selectedLine) ? { startUs: selectedLine.startUs, endUs: selectedLine.endUs } : undefined);
  }, [audio, loopLine, selectedLine]);

  useWorkspaceKeys({ session, audio, stampMode, onUndo: props.onUndo, onRedo: props.onRedo, onSave: props.onSave });

  const commitTokens = useCallback(
    (edits: TokenTimeEdit[]) => {
      const branchOf = (lineId: string) => session.state.lines.find((l) => l.id === lineId)?.branchId ?? session.state.branchId;
      void session.command({
        type: 'lyrics.setTokenTimes',
        changes: edits.map((e) => ({
          target: { branchId: branchOf(e.lineId), lineId: e.lineId, tokenId: e.tokenId },
          startUs: toMicros(e.startUs),
          endUs: toMicros(e.endUs),
        })),
      });
    },
    [session],
  );

  const commitLine = useCallback(
    (lineId: string, startUs: number, endUs: number) => {
      const line = session.state.lines.find((l) => l.id === lineId);
      if (!line) return;
      void session.command({ type: 'lyrics.setTimeRange', target: { branchId: line.branchId, lineId }, startUs: toMicros(startUs), endUs: toMicros(endUs) });
    },
    [session],
  );

  const commitRuby = useCallback(
    (edits: RubyTimeEdit[]) => {
      const branchOf = (lineId: string) => session.state.lines.find((l) => l.id === lineId)?.branchId ?? session.state.branchId;
      void session.command({
        type: 'lyrics.setRubySegmentTimes',
        changes: edits.map((e) => ({
          target: { branchId: branchOf(e.lineId), lineId: e.lineId, tokenId: e.tokenId, rubyId: e.rubyId, segmentId: e.segmentId },
          startUs: toMicros(e.startUs),
          endUs: toMicros(e.endUs),
        })),
      });
    },
    [session],
  );

  const commitTrackLine = useCallback(
    (branchId: string, lineId: string, startUs: number, endUs: number) =>
      void session.command({ type: 'lyrics.setTimeRange', target: { branchId, lineId }, startUs: toMicros(startUs), endUs: toMicros(endUs) }),
    [session],
  );

  const openTrackLine = useCallback(
    (branchId: string, lineId: string) => {
      session.selectBranch(branchId);
      session.select({ lineId });
    },
    [session],
  );

  const select = useCallback((selection: Selection) => session.select(selection), [session]);

  return (
    <WorkLayout
      page="lyrics"
      className="workspace"
      stage="right"
      left={{ label: '歌词行', content: <LinePane session={session} state={state} onUndo={props.onUndo} actions={actions} /> }}
      center={
        <>
          <section className="line-edit" aria-label="行编辑">
            {selectedLine ? (
              <LineDetail
                session={session}
                state={state}
                audio={audio}
                line={selectedLine}
                lineNumber={selectedLineIndex + 1}
                tokenId={state.selection?.tokenId}
                segmentId={state.selection?.segmentId}
                actions={actions}
                appearance={props.appearance}
                orchestration={props.orchestration}
                onOpen={props.onOpen}
              />
            ) : (
              <p className="faint line-edit-empty">选择左侧的一行。</p>
            )}
          </section>
          <TimeBar session={session} audio={audio} line={selectedLine} tokenId={state.selection?.tokenId} segmentId={state.selection?.segmentId} />
          {dialogs}
        </>
      }
      right={{
        label: '逐字预览',
        collapsed: previewCollapsed,
        onCollapsedChange: collapsePreview,
        content: (
          <aside className="pane-preview">
            <WordPreview audio={audio} lines={previewLines} translations={translations} />
          </aside>
        ),
      }}
      timeline={!!audio}
      dock={
        <>
          <NoAudioBar session={session} />
          {audio && (
            <Timeline
              audio={audio}
              lines={state.lines}
              selection={state.selection}
              frameRate={session.frameRate}
              onSelect={select}
              onCommitTokens={commitTokens}
              onCommitLine={commitLine}
              showRuby
              {...(session.supportsCommand('lyrics.setRubySegmentTimes') ? { onCommitRuby: commitRuby } : {})}
              lineTracks={tracks}
              followSpans={follows}
              onCommitTrackLine={commitTrackLine}
              onOpenTrackLine={openTrackLine}
              leading={
                <Transport
                  audio={audio}
                  loopLine={loopLine}
                  onLoopLine={setLoopLine}
                  loopAvailable={!!selectedLine && isTimed(selectedLine)}
                  stampMode={stampMode}
                  onStampMode={setStampMode}
                  stampAvailable={stampAvailable}
                />
              }
            />
          )}
        </>
      }
      status={<StatusBar state={state} />}
    />
  );
}

/**
 * 工作区快捷键（设计 §4.6）。焦点在输入框、对话框或菜单里时全部让给它们。
 * 打点模式下按住 J 记起点、松开记终点并前进；按键当时的播放头才是打点时间。
 * 空格在捕获阶段处理：行列表、按钮等 React Aria 控件会自己处理空格并阻止冒泡。
 * 其余按键在冒泡阶段，控件已经处理过的（例如焦点在行列表里时的上下键）不再处理。
 */
function useWorkspaceKeys(options: {
  session: EditorSession;
  audio: AudioController | null;
  stampMode: boolean;
  onUndo: () => void;
  onRedo: () => void;
  onSave: () => void;
}) {
  const latest = useRef(options);
  latest.current = options;
  const stamping = useRef<TokenTarget | null>(null);

  useEffect(() => {
    const targetOf = (lines: readonly EditorLine[], selection: Selection | undefined): TokenTarget | undefined => {
      const line = lines.find((l) => l.id === selection?.lineId);
      const token = selection?.tokenId ? line?.tokens.find((t) => t.id === selection.tokenId) : line?.tokens[0];
      return line && token ? { branchId: line.branchId, lineId: line.id, tokenId: token.id } : undefined;
    };

    const onKeyDown = (event: KeyboardEvent) => {
      if (isEditing(event)) return;
      const { session, audio, stampMode } = latest.current;
      const { lines, selection } = session.state;
      const mod = event.ctrlKey || event.metaKey;
      const key = event.key.toLowerCase();

      if (mod && key === 'z') {
        event.preventDefault();
        if (event.shiftKey) latest.current.onRedo();
        else latest.current.onUndo();
      } else if (mod && key === 'y') {
        event.preventDefault();
        latest.current.onRedo();
      } else if (mod && key === 's') {
        event.preventDefault();
        latest.current.onSave();
      } else if (mod || event.defaultPrevented) {
        return;
      } else if (key === 'j' && stampMode && audio) {
        event.preventDefault();
        if (event.repeat || stamping.current) return;
        const target = targetOf(lines, selection);
        if (!target) return;
        const playheadUs = audio.playheadUs();
        stamping.current = target;
        session.select({ lineId: target.lineId, tokenId: target.tokenId });
        void session.command({ type: 'lyrics.stampTokenStart', target, playheadUs: toMicros(playheadUs) });
      } else if ((event.key === 'ArrowLeft' || event.key === 'ArrowRight') && selection) {
        event.preventDefault();
        const delta = event.key === 'ArrowRight' ? 1 : -1;
        const target = targetOf(lines, selection);
        if (event.altKey) {
          if (target && selection.tokenId) void session.command({ type: 'lyrics.nudgeTokenTime', target, frameDelta: delta });
        } else if (selection.tokenId) {
          const next = stepToken(lines, selection.tokenId, delta);
          if (next) session.select({ lineId: next.lineId, tokenId: next.id });
        } else if (target) {
          session.select({ lineId: target.lineId, tokenId: target.tokenId });
        }
      } else if ((event.key === 'ArrowUp' || event.key === 'ArrowDown') && selection) {
        event.preventDefault();
        const index = lines.findIndex((l) => l.id === selection.lineId) + (event.key === 'ArrowDown' ? 1 : -1);
        const line = lines[index];
        if (line) session.select({ lineId: line.id });
      } else if ((event.key === 'Delete' || event.key === 'Backspace') && selection?.tokenId) {
        event.preventDefault();
        const target = targetOf(lines, selection);
        if (target) void session.command({ type: 'lyrics.clearTokenTime', target });
      }
    };

    const onKeyUp = (event: KeyboardEvent) => {
      if (event.key.toLowerCase() !== 'j' || !stamping.current) return;
      const { session, audio } = latest.current;
      const target = stamping.current;
      stamping.current = null;
      if (!audio) return;
      const playheadUs = audio.playheadUs();
      void session.command({ type: 'lyrics.stampTokenEnd', target, playheadUs: toMicros(playheadUs) });
      const next = stepToken(session.state.lines, target.tokenId, 1);
      if (next) session.select({ lineId: next.lineId, tokenId: next.id });
    };

    // 在控件之前拿到空格：阻止默认动作，按钮不会在松开时被“点”一次，也不会滚动页面。
    const onSpace = (event: KeyboardEvent) => {
      if (event.key !== ' ' || event.ctrlKey || event.metaKey || event.altKey || isEditing(event)) return;
      event.preventDefault();
      event.stopPropagation();
      if (!event.repeat) latest.current.audio?.toggle();
    };

    window.addEventListener('keydown', onSpace, true);
    window.addEventListener('keydown', onKeyDown);
    window.addEventListener('keyup', onKeyUp);
    return () => {
      window.removeEventListener('keydown', onSpace, true);
      window.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('keyup', onKeyUp);
    };
  }, []);
}

const NON_TEXT_INPUTS = new Set(['checkbox', 'radio', 'range', 'button', 'submit', 'reset', 'file', 'color']);

/** 这些角色的元素里按键归它们：对话框、侧面板（React Aria 的 Modal 都是 dialog）、菜单。 */
const OVERLAY_ROLES = new Set(['dialog', 'alertdialog', 'menu']);

/**
 * 事件是否来自文字输入控件（包括 shadow DOM 里的 input）；复选框、开关等不算。
 * 来自对话框、菜单和下拉列表里的也算：它们是模态的，空格、方向键留给里面的控件。
 */
export function isEditing(event: Event): boolean {
  return event.composedPath().some(
    (node) =>
      node instanceof HTMLElement &&
      ((node instanceof HTMLInputElement && !NON_TEXT_INPUTS.has(node.type)) ||
        node.tagName === 'TEXTAREA' ||
        node.isContentEditable ||
        OVERLAY_ROLES.has(node.getAttribute('role') ?? '') ||
        // React Aria 的弹出层（菜单、下拉选择）带 data-trigger
        node.hasAttribute('data-trigger') ||
        // 对话框关闭后焦点可能还留在里面，只算打开的。
        (node.tagName === 'MDUI-DIALOG' && (node as HTMLElement & { open?: boolean }).open === true)),
  );
}
