import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { TokenTarget } from '../../protocol';
import type { AudioController } from '../audio/audio-controller';
import { NoAudioBar } from '../audio/NoAudioBar';
import { Transport } from '../audio/Transport';
import { LineDetail } from '../lyrics/LineDetail';
import { LinePane } from '../lyrics/LinePane';
import { TimeBar } from '../lyrics/TimeBar';
import { WordPreview } from '../preview/WordPreview';
import { followSpans, lineTracks } from '../model/line-tracks';
import { isTimed, stepToken, type EditorLine, type Selection } from '../model/lyrics';
import { translationsByPrimary } from '../model/word-preview';
import { toMicros } from '../model/time';
import type { EditorSession } from '../session/editor-session';
import { Timeline, type RubyTimeEdit, type TokenTimeEdit } from '../timeline/Timeline';
import { useObservable } from '../ui/hooks';

const PREVIEW_COLLAPSED_KEY = 'mako.editor.wordPreviewCollapsed';

/** 歌词页（整体设计 §5.2）：左栏行列表，中栏行编辑，右栏逐字预览；底部区是时间栏、播放条与时间轴。 */
export function Workspace(props: {
  session: EditorSession;
  audio: AudioController | null;
  onUndo: () => void;
  onRedo: () => void;
  onSave: () => void;
}) {
  const { session, audio } = props;
  const state = useObservable(session.state$);
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
    <div className={previewCollapsed ? 'workspace preview-collapsed' : 'workspace'}>
      <LinePane session={session} state={state} onUndo={props.onUndo} />
      <section className="pane pane-detail">
        {selectedLine ? (
          <LineDetail session={session} state={state} audio={audio} line={selectedLine} lineNumber={selectedLineIndex + 1} tokenId={state.selection?.tokenId} segmentId={state.selection?.segmentId} />
        ) : (
          <p className="muted">选择左侧的一行。</p>
        )}
      </section>
      <aside className="pane pane-preview">
        {previewCollapsed ? (
          <mdui-button-icon aria-label="展开逐字预览" onClick={() => collapsePreview(false)}>
            <mdui-icon-chevron-left />
          </mdui-button-icon>
        ) : (
          <WordPreview audio={audio} lines={previewLines} translations={translations} onCollapse={() => collapsePreview(true)} />
        )}
      </aside>
      <div className="workspace-bottom">
        <TimeBar session={session} audio={audio} line={selectedLine} tokenId={state.selection?.tokenId} segmentId={state.selection?.segmentId} />
        <NoAudioBar session={session} />
        {audio && (
          <>
            <Transport
              audio={audio}
              loopLine={loopLine}
              onLoopLine={setLoopLine}
              loopAvailable={!!selectedLine && isTimed(selectedLine)}
              stampMode={stampMode}
              onStampMode={setStampMode}
              stampAvailable={stampAvailable}
            />
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
            />
          </>
        )}
      </div>
      <footer className="status-bar">
        <span className={`notice notice-${state.notice?.level ?? 'none'}`}>{state.notice?.message ?? '就绪'}</span>
        <span className="muted">
          {state.syncing ? '同步中 · ' : ''}
          {state.pendingCommands > 0 ? `${state.pendingCommands} 条命令进行中 · ` : ''}修订 {state.summary.projectRevision}
          {state.readModel === 'minimal' ? ' · 读模型：最小投影' : ''}
        </span>
      </footer>
    </div>
  );
}

/**
 * 工作区快捷键（设计 §4.6）。焦点在输入框里时全部让给输入框。
 * 打点模式下按住 J 记起点、松开记终点并前进；按键当时的播放头才是打点时间。
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
      } else if (mod) {
        return;
      } else if (event.key === ' ') {
        event.preventDefault();
        // 刚点过的按钮仍有焦点时，松开空格会再“点”它一次：先移走焦点。
        if (document.activeElement instanceof HTMLElement && document.activeElement !== document.body) document.activeElement.blur();
        audio?.toggle();
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

    window.addEventListener('keydown', onKeyDown);
    window.addEventListener('keyup', onKeyUp);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('keyup', onKeyUp);
    };
  }, []);
}

const NON_TEXT_INPUTS = new Set(['checkbox', 'radio', 'range', 'button', 'submit', 'reset', 'file', 'color']);

/**
 * 事件是否来自文字输入控件（包括 mdui 组件 shadow DOM 里的 input）；复选框、开关等不算。
 * 来自对话框里的也算：对话框是模态的，空格、方向键留给对话框里的按钮。
 */
export function isEditing(event: Event): boolean {
  return event.composedPath().some(
    (node) =>
      node instanceof HTMLElement &&
      ((node instanceof HTMLInputElement && !NON_TEXT_INPUTS.has(node.type)) ||
        node.tagName === 'TEXTAREA' ||
        node.isContentEditable ||
        // 对话框关闭后焦点可能还留在里面，只算打开的。
        (node.tagName === 'MDUI-DIALOG' && (node as HTMLElement & { open?: boolean }).open === true)),
  );
}
