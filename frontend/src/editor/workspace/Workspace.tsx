import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { LyricBeatTarget, ProjectEditCommand, TokenTimeChange } from '../../protocol';
import type { AppearanceStore } from '../appearance/appearance-store';
import type { AudioController } from '../audio/audio-controller';
import { NoAudioBar } from '../audio/NoAudioBar';
import { Transport } from '../audio/Transport';
import { DictionaryManagerDialog } from '../lyrics/DictionaryManagerDialog';
import { FullTextQuickEdit } from '../lyrics/FullTextQuickEdit';
import { useLineActions } from '../lyrics/line-actions';
import { LinePane } from '../lyrics/LinePane';
import { LyricsStageCanvas } from '../lyrics/LyricsStageCanvas';
import { LyricsToolbar } from '../lyrics/LyricsToolbar';
import { ResplitBranchDialog } from '../lyrics/ResplitDialogs';
import { RubyInlinePopover, type RubyPopoverAnchor } from '../lyrics/RubyInlinePopover';
import {
  collectBranchBeats,
  computeMonotonicStrikeUs,
  findBeatIndexForSelection,
  resolveTimingKeyAction,
  sameBeatTarget,
  stepBranchBeat,
  stepContentToken,
  type BeatItem,
  type LyricsWorkMode,
} from '../lyrics/timing-keymap';
import '../lyrics/lyrics-stage.css';
import { followSpans, lineTracks } from '../model/line-tracks';
import { isTimed, type EditorLine, type EditorToken, type Selection } from '../model/lyrics';
import { nudgedRange, rubyOf, segmentTarget, selectedSegment } from '../model/ruby-segments';
import { fromLyricTime, toMicros } from '../model/time';
import { graphemes } from '../model/token-segments';
import { isContentToken } from '../model/token-semantics';
import type { OrchestrationStore } from '../orchestration/orchestration-store';
import { defaultJapaneseDictStore, type DictCacheStatus } from '../phonetics/dict-store';
import { buildLineFuriganaPlan, defaultJapaneseFuriganaService } from '../phonetics/japanese-furigana';
import { splitJapaneseMora } from '../phonetics/mora-splitter';
import { loadTimingPreferences, updateTimingPreferences, type TimingPreferences } from '../phonetics/timing-preferences';
import { loadUserDictionary } from '../phonetics/user-dictionary';
import type { EditorSession } from '../session/editor-session';
import { Timeline, type RubyTimeEdit, type TokenTimeEdit } from '../timeline/Timeline';
import { toast } from '../ui/feedback';
import { useObservable } from '../ui/hooks';
import { StatusBar } from './StatusBar';
import { WorkLayout } from './WorkLayout';

interface StrikeHistoryEntry {
  struck: BeatItem;
  prevOpen: LyricBeatTarget | null;
}

/**
 * 歌词页（Phase 3 重构）：
 * - 左侧常驻窄栏：LinePane（来源切换、导入/粘贴、行列表与状态统计、右键含外观/编排入口）；
 * - 右侧合并原中栏与原右栏全部宽度：LyricsToolbar + LyricsStageCanvas / FullTextQuickEdit + RubyInlinePopover；
 * - 底部多轨音频时间轴坞：0.25x~2.0x 保调变速 Transport + 波形/注音/词元/行轨，与画布连打即时联动。
 */
export function Workspace(props: {
  session: EditorSession;
  audio: AudioController | null;
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

  const [mode, setMode] = useState<LyricsWorkMode>('timing');
  const [prevNonQuickMode, setPrevNonQuickMode] = useState<Exclude<LyricsWorkMode, 'quickEdit'>>('timing');
  const [loopLine, setLoopLine] = useState(false);
  const [prefs, setPrefs] = useState<TimingPreferences>(() => loadTimingPreferences());
  const [dictStatus, setDictStatus] = useState<DictCacheStatus>({ installed: false, backend: 'none' });
  const [dictDialogOpen, setDictDialogOpen] = useState(false);
  const [dictPromptReason, setDictPromptReason] = useState<string | undefined>(undefined);
  const [pendingFuriganaScope, setPendingFuriganaScope] = useState<'line' | 'branch' | null>(null);
  const [furiganaBusy, setFuriganaBusy] = useState(false);
  const [rubyAnchor, setRubyAnchor] = useState<RubyPopoverAnchor | null>(null);
  const [inlineEditingLineId, setInlineEditingLineId] = useState<string | null>(null);
  const [tokenMenuOpen, setTokenMenuOpen] = useState(false);
  const [resplitBranchOpen, setResplitBranchOpen] = useState(false);

  // Blind-tapping live beat state (mirrored to refs for zero-latency alternating J/K taps).
  const [openBeat, setOpenBeat] = useState<LyricBeatTarget | null>(null);
  const openBeatRef = useRef<LyricBeatTarget | null>(null);
  const lastStrikeUsRef = useRef<number | null>(null);
  const strikeHistoryRef = useRef<StrikeHistoryEntry[]>([]);
  const stageRootRef = useRef<HTMLDivElement | null>(null);

  const clearOpenBeat = useCallback(() => {
    openBeatRef.current = null;
    lastStrikeUsRef.current = null;
    setOpenBeat(null);
  }, []);

  const handleModeChange = useCallback(
    (next: LyricsWorkMode) => {
      if (next !== 'quickEdit') setPrevNonQuickMode(next);
      setMode(next);
      clearOpenBeat();
      setRubyAnchor(null);
      setInlineEditingLineId(null);
      setTokenMenuOpen(false);
    },
    [clearOpenBeat],
  );

  const handleUpdatePrefs = useCallback((patch: Partial<TimingPreferences>) => {
    setPrefs(updateTimingPreferences(patch));
  }, []);

  // Load Japanese dictionary cache status once on mount.
  useEffect(() => {
    let active = true;
    void defaultJapaneseDictStore.getStatus().then((status) => {
      if (active) setDictStatus(status);
    });
    return () => {
      active = false;
    };
  }, []);

  // Clear live tapping & popovers on branch switch.
  useEffect(() => {
    clearOpenBeat();
    strikeHistoryRef.current = [];
    setRubyAnchor(null);
    setInlineEditingLineId(null);
    setTokenMenuOpen(false);
  }, [state.branchId, clearOpenBeat]);

  const selectedLineIndex = state.selection ? state.lines.findIndex((l) => l.id === state.selection!.lineId) : -1;
  const selectedLine = selectedLineIndex >= 0 ? state.lines[selectedLineIndex] : undefined;

  // Timeline reference tracks (translations or primary reference).
  const branches = state.branches;
  const tracks = useMemo(
    () => lineTracks({ branches, primaryBranchId: state.primaryBranchId, branchId: state.branchId, linesByBranch: state.linesByBranch }),
    [branches, state.primaryBranchId, state.branchId, state.linesByBranch],
  );
  const follows = useMemo(
    () => followSpans(branches?.find((b) => b.id === state.branchId), state.lines, state.primaryBranchId ? (state.linesByBranch[state.primaryBranchId] ?? []) : []),
    [branches, state.branchId, state.lines, state.primaryBranchId, state.linesByBranch],
  );

  // Select first line & first content token on initial load.
  useEffect(() => {
    if (!state.selection && state.lines[0]) {
      const firstLine = state.lines[0];
      const firstToken = firstLine.tokens.find(isContentToken);
      session.select(firstToken ? { lineId: firstLine.id, tokenId: firstToken.id } : { lineId: firstLine.id });
    }
  }, [session, state.selection, state.lines]);

  // Loop current line when toggle is enabled.
  useEffect(() => {
    if (!audio) return;
    audio.setLoop(loopLine && selectedLine && isTimed(selectedLine) ? { startUs: selectedLine.startUs, endUs: selectedLine.endUs } : undefined);
  }, [audio, loopLine, selectedLine]);

  // Open F2 inline ruby popover anchored to a specific token element.
  const openRubyPopover = useCallback(
    (lineId: string, tokenId: string, element: HTMLElement | null) => {
      if (prefs.pauseOnRubyPopover && audio?.playing) {
        audio.pause();
      }
      const el = element ?? stageRootRef.current?.querySelector<HTMLElement>(`[data-token-id="${tokenId}"]`) ?? null;
      setRubyAnchor({ lineId, tokenId, element: el });
    },
    [audio, prefs.pauseOnRubyPopover],
  );

  // Commit non-destructive ruby segments via Phase 1 lyrics.setRubySegments.
  const commitRubySegments = useCallback(
    async (line: EditorLine, token: EditorToken, segments: string[]) => {
      if (session.supportsCommand('lyrics.setRubySegments')) {
        await session.command({
          type: 'lyrics.setRubySegments',
          target: { branchId: line.branchId, lineId: line.id, tokenId: token.id },
          segments,
        });
      } else if (session.supportsCommand('lyrics.setRubyText')) {
        await session.command({
          type: 'lyrics.setRubyText',
          target: { branchId: line.branchId, lineId: line.id, tokenId: token.id },
          text: segments.join(''),
        });
      }
    },
    [session],
  );

  // Navigate to adjacent content token from inside RubyInlinePopover.
  const navigateRubyPopover = useCallback(
    (delta: -1 | 1) => {
      const currentSel = rubyAnchor ? { lineId: rubyAnchor.lineId, tokenId: rubyAnchor.tokenId } : session.state.selection;
      const next = stepContentToken(session.state.lines, currentSel, delta);
      if (!next?.tokenId) return;
      session.select(next);
      requestAnimationFrame(() => {
        const el = stageRootRef.current?.querySelector<HTMLElement>(`[data-token-id="${next.tokenId}"]`) ?? null;
        setRubyAnchor({ lineId: next.lineId, tokenId: next.tokenId!, element: el });
      });
    },
    [rubyAnchor, session],
  );

  // Japanese Auto-Furigana pipeline (checks NAIST-jdic OPFS cache first; prompts download if absent).
  const runAutoFurigana = useCallback(
    async (scope: 'line' | 'branch') => {
      if (furiganaBusy) return;
      const currentStatus = await defaultJapaneseDictStore.getStatus();
      setDictStatus(currentStatus);
      if (!currentStatus.installed) {
        setPendingFuriganaScope(scope);
        setDictPromptReason('首次使用日语自动注音前，请先确认按需下载或从本地导入 NAIST-jdic 0.6.3b 离线词典（BSD-3-Clause，约 14~16 MB）。');
        setDictDialogOpen(true);
        return;
      }

      const branchId = session.state.branchId;
      const targetLines = scope === 'line' ? (selectedLine ? [selectedLine] : []) : session.state.lines;
      const nonEmpty = targetLines.filter((l) => l.text.trim().length > 0);
      if (nonEmpty.length === 0) {
        toast('没有需要注音的非空歌词行');
        return;
      }

      setFuriganaBusy(true);
      try {
        const currentPrefs = loadTimingPreferences();
        const userDictionary = loadUserDictionary();
        const spansPerLine = await defaultJapaneseFuriganaService.analyzeLines(
          nonEmpty.map((l) => l.text),
          {
            mergeLongAndNasalInKanji: currentPrefs.mergeLongAndNasalInKanji,
            userDictionary,
          },
        );

        const plans = nonEmpty.map((line, i) => ({ line, plan: buildLineFuriganaPlan(spansPerLine[i] ?? []) }));

        // Step 1: resplit any line whose compound kanji token boundaries changed (e.g. 二 + 人 -> 二人).
        const resplitEdits: ProjectEditCommand[] = [];
        if (session.supportsCommand('lyrics.resplitLine')) {
          for (const { line, plan } of plans) {
            if (plan.rubyByTokenIndex.size === 0) continue;
            if (plan.tokenTexts.join('') !== line.text) continue;
            const currentTexts = line.tokens.map((t) => t.text);
            const sameTokens = currentTexts.length === plan.tokenTexts.length && currentTexts.every((t, idx) => t === plan.tokenTexts[idx]);
            if (!sameTokens) {
              resplitEdits.push({
                type: 'lyrics.resplitLine',
                target: { branchId, lineId: line.id },
                segments: plan.tokenTexts,
                estimateTimes: false,
              });
            }
          }
        }

        if (resplitEdits.length > 0) {
          await session.applyEdits(resplitEdits);
          await session.refresh();
        }

        // Step 2: apply non-destructive ruby segments in one undoable batch.
        const freshById = new Map(session.linesOf(branchId).map((l) => [l.id, l]));
        const rubyEdits: ProjectEditCommand[] = [];
        for (const { line, plan } of plans) {
          const fresh = freshById.get(line.id);
          if (!fresh) continue;
          for (const [tokenIdx, segments] of plan.rubyByTokenIndex.entries()) {
            const token = fresh.tokens[tokenIdx];
            if (!token || segments.length === 0) continue;
            const existing = rubyOf(fresh, token.id);
            const same =
              existing &&
              existing.segments.length === segments.length &&
              existing.segments.every((s, idx) => s.text === segments[idx]);
            if (!same) {
              rubyEdits.push({
                type: 'lyrics.setRubySegments',
                target: { branchId, lineId: fresh.id, tokenId: token.id },
                segments,
              });
            }
          }
        }

        if (rubyEdits.length === 0) {
          toast('当前歌词没有需要新增或更新的日语汉字注音');
        } else {
          const res = await session.applyEdits(rubyEdits);
          toast(`已为 ${nonEmpty.length} 行生成 ${res.applied} 处日语多拍注音`, { label: '撤销', run: props.onUndo });
        }
      } catch (err) {
        const message = err instanceof Error ? err.message : String(err);
        session.notify({ level: 'error', message: `日语自动注音失败：${message}` });
      } finally {
        setFuriganaBusy(false);
      }
    },
    [furiganaBusy, session, selectedLine, props.onUndo],
  );

  // Fill missing content token times on selected line evenly within untimed gaps.
  const handleFillMissingTimes = useCallback(async () => {
    if (!selectedLine || !isTimed(selectedLine) || !session.supportsCommand('lyrics.setTokenTimes')) return;
    const contentTokens = selectedLine.tokens.filter(isContentToken);
    if (contentTokens.length === 0) return;

    const changes: TokenTimeChange[] = [];
    let i = 0;
    while (i < contentTokens.length) {
      if (isTimed(contentTokens[i]!)) {
        i++;
        continue;
      }
      const runStart = i;
      while (i < contentTokens.length && !isTimed(contentTokens[i]!)) i++;
      const runEnd = i;
      const prevTok = runStart > 0 ? contentTokens[runStart - 1] : undefined;
      const nextTok = runEnd < contentTokens.length ? contentTokens[runEnd] : undefined;
      const gapStartUs: number = prevTok && isTimed(prevTok) ? prevTok.endUs : selectedLine.startUs;
      const gapEndUs: number = nextTok && isTimed(nextTok) ? nextTok.startUs : selectedLine.endUs;
      if (gapEndUs <= gapStartUs) continue;

      const weights = contentTokens.slice(runStart, runEnd).map((t) => Math.max(1, graphemes(t.text).length));
      const totalWeight = weights.reduce((a, b) => a + b, 0);
      let cursorUs = gapStartUs;
      let acc = 0;
      for (let k = 0; k < weights.length; k++) {
        const tok = contentTokens[runStart + k]!;
        acc += weights[k]!;
        const nextUs = k === weights.length - 1 ? gapEndUs : Math.round(gapStartUs + ((gapEndUs - gapStartUs) * acc) / totalWeight);
        if (nextUs > cursorUs) {
          changes.push({
            target: { branchId: selectedLine.branchId, lineId: selectedLine.id, tokenId: tok.id },
            startUs: toMicros(cursorUs),
            endUs: toMicros(nextUs),
          });
          cursorUs = nextUs;
        }
      }
    }

    if (changes.length === 0) {
      toast('本行所有实义词元均已具备时间');
      return;
    }
    const res = await session.command({ type: 'lyrics.setTokenTimes', changes });
    if (res?.changed) toast(`已补全 ${changes.length} 个词元时间`, { label: '撤销', run: props.onUndo });
  }, [selectedLine, session, props.onUndo]);

  const handleClearLineTimes = useCallback(async () => {
    if (!selectedLine || !session.supportsCommand('lyrics.clearTokenTime')) return;
    const edits: ProjectEditCommand[] = selectedLine.tokens
      .filter((t) => t.startUs !== undefined)
      .map((t) => ({ type: 'lyrics.clearTokenTime', target: { branchId: selectedLine.branchId, lineId: selectedLine.id, tokenId: t.id } }));
    if (edits.length === 0) return;
    const res = await session.applyEdits(edits);
    if (res.applied > 0) toast(`已清除本行 ${res.applied} 个词元时间`, { label: '撤销', run: props.onUndo });
  }, [selectedLine, session, props.onUndo]);

  // Mode M1 global keyboard state machine.
  useLyricsKeymap({
    session,
    audio,
    mode,
    prefs,
    openBeatRef,
    lastStrikeUsRef,
    strikeHistoryRef,
    setOpenBeat,
    clearOpenBeat,
    openRubyPopover,
    onInlineEditLine: setInlineEditingLineId,
    onToggleTokenMenu: () => setTokenMenuOpen((v) => !v),
    onUndo: props.onUndo,
    onRedo: props.onRedo,
    onSave: props.onSave,
  });

  // Timeline callbacks.
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

  const select = useCallback(
    (selection: Selection) => {
      clearOpenBeat();
      session.select(selection);
    },
    [session, clearOpenBeat],
  );

  // Resolve popover target line & token.
  const rubyLine = rubyAnchor ? state.lines.find((l) => l.id === rubyAnchor.lineId) : undefined;
  const rubyToken = rubyAnchor && rubyLine ? rubyLine.tokens.find((t) => t.id === rubyAnchor.tokenId) : undefined;

  return (
    <WorkLayout
      page="lyrics"
      className="workspace"
      stage="center"
      left={{
        label: '歌词行',
        content: (
          <LinePane
            session={session}
            state={state}
            onUndo={props.onUndo}
            actions={actions}
            appearance={props.appearance}
            orchestration={props.orchestration}
            onOpenPage={props.onOpen}
          />
        ),
      }}
      center={
        <>
          <LyricsToolbar
            session={session}
            state={state}
            mode={mode}
            onModeChange={handleModeChange}
            selectedLine={selectedLine}
            dictStatus={dictStatus}
            furiganaBusy={furiganaBusy}
            prefs={prefs}
            onUpdatePrefs={handleUpdatePrefs}
            onAutoFurigana={(scope) => void runAutoFurigana(scope)}
            onOpenDictManager={() => {
              setPendingFuriganaScope(null);
              setDictPromptReason(undefined);
              setDictDialogOpen(true);
            }}
            onResplitLine={() => selectedLine && actions.run('resplit', selectedLine)}
            onResplitBranch={() => setResplitBranchOpen(true)}
            onFillMissingTimes={() => void handleFillMissingTimes()}
            onClearLineTimes={() => void handleClearLineTimes()}
            appearance={props.appearance}
            orchestration={props.orchestration}
            onOpenPage={props.onOpen}
          />

          {mode === 'quickEdit' ? (
            <FullTextQuickEdit
              session={session}
              branchId={state.branchId}
              lines={state.lines}
              mergeLongAndNasalInKanji={prefs.mergeLongAndNasalInKanji}
              onDone={() => handleModeChange('timing')}
              onCancel={() => handleModeChange(prevNonQuickMode)}
            />
          ) : (
            <LyricsStageCanvas
              session={session}
              state={state}
              audio={audio}
              mode={mode}
              scrollMode={prefs.scrollMode}
              openBeat={openBeat}
              inlineEditingLineId={inlineEditingLineId}
              onInlineEditLine={setInlineEditingLineId}
              tokenMenuOpen={tokenMenuOpen}
              onTokenMenuOpenChange={setTokenMenuOpen}
              onOpenRubyPopover={openRubyPopover}
              onResplitLine={(line) => actions.run('resplit', line)}
              stageRootRef={stageRootRef}
            />
          )}

          <RubyInlinePopover
            anchor={rubyAnchor}
            line={rubyLine}
            token={rubyToken}
            mergeLongAndNasalInKanji={prefs.mergeLongAndNasalInKanji}
            onCommit={commitRubySegments}
            onNavigateToken={navigateRubyPopover}
            onClose={() => setRubyAnchor(null)}
          />

          <DictionaryManagerDialog
            open={dictDialogOpen}
            onClose={() => {
              setDictDialogOpen(false);
              setPendingFuriganaScope(null);
              setDictPromptReason(undefined);
            }}
            promptReason={dictPromptReason}
            onStatusChange={setDictStatus}
            onReadyAction={
              pendingFuriganaScope
                ? () => {
                    const scope = pendingFuriganaScope;
                    setPendingFuriganaScope(null);
                    void runAutoFurigana(scope);
                  }
                : undefined
            }
            readyActionLabel={pendingFuriganaScope === 'branch' ? '立即为整个来源注音' : '立即为当前行注音'}
          />

          <ResplitBranchDialog session={session} branchId={state.branchId} open={resplitBranchOpen} onClose={() => setResplitBranchOpen(false)} />
          {dialogs}
        </>
      }
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
              liveBeatTarget={openBeat}
              leading={
                <Transport
                  audio={audio}
                  loopLine={loopLine}
                  onLoopLine={setLoopLine}
                  loopAvailable={!!selectedLine && isTimed(selectedLine)}
                  help={
                    mode === 'timing'
                      ? 'Space 播放/暂停 · J/K 交替连打 · L 收尾 ● · Backspace/G 回退复听 · F2 注音 · Q/W 变速'
                      : 'Space 播放/暂停 · F2 注音 · L 停顿提示 ○ · Enter 断行 · Shift+Enter 合行'
                  }
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
 * 模式 M1 工作区键盘状态机（Phase 1/3）：
 * - 焦点位于输入框、对话框、菜单、浮窗（含 RubyInlinePopover）时完全让出按键；
 * - Space 在捕获阶段接管全局播放/暂停；
 * - 打轴模式：J/K 双指轮指连打（lyrics.strikeBeat）、L 显式收尾（lyrics.closeBeat）、Backspace/G 撤回并自动回跳复听；
 * - 编辑模式：L 翻转停顿 ○、Enter 断行、Shift+Enter 合行、Ctrl+Enter 行内改词、Alt+方向键微调帧；
 * - 通用：Q/W 保调变速、Z/X 快退/快进、F2 原地注音浮窗、[ / ] 拆合注音拍点。
 */
function useLyricsKeymap(options: {
  session: EditorSession;
  audio: AudioController | null;
  mode: LyricsWorkMode;
  prefs: TimingPreferences;
  openBeatRef: React.MutableRefObject<LyricBeatTarget | null>;
  lastStrikeUsRef: React.MutableRefObject<number | null>;
  strikeHistoryRef: React.MutableRefObject<StrikeHistoryEntry[]>;
  setOpenBeat: (target: LyricBeatTarget | null) => void;
  clearOpenBeat: () => void;
  openRubyPopover: (lineId: string, tokenId: string, element: HTMLElement | null) => void;
  onInlineEditLine: (lineId: string | null) => void;
  onToggleTokenMenu: () => void;
  onUndo: () => void;
  onRedo: () => void;
  onSave: () => void;
}) {
  const latest = useRef(options);
  latest.current = options;

  useEffect(() => {
    const onSpace = (event: KeyboardEvent) => {
      if (event.key !== ' ' || event.ctrlKey || event.metaKey || event.altKey || isEditing(event)) return;
      event.preventDefault();
      event.stopPropagation();
      if (!event.repeat) latest.current.audio?.toggle();
    };

    const onKeyDown = (event: KeyboardEvent) => {
      if (isEditing(event)) return;
      const {
        session,
        audio,
        mode,
        prefs,
        openBeatRef,
        lastStrikeUsRef,
        strikeHistoryRef,
        setOpenBeat,
        clearOpenBeat,
        openRubyPopover,
        onInlineEditLine,
        onToggleTokenMenu,
      } = latest.current;

      const mod = event.ctrlKey || event.metaKey;
      const key = event.key.toLowerCase();

      // Always keep global Undo / Redo / Save intact.
      if (mod && key === 'z') {
        event.preventDefault();
        if (event.shiftKey) latest.current.onRedo();
        else latest.current.onUndo();
        return;
      }
      if (mod && key === 'y') {
        event.preventDefault();
        latest.current.onRedo();
        return;
      }
      if (mod && key === 's') {
        event.preventDefault();
        latest.current.onSave();
        return;
      }
      if (event.defaultPrevented) return;

      const intent = resolveTimingKeyAction(event, mode, prefs.keymapOverrides);
      if (!intent) return;

      const { lines, selection, branchId } = session.state;

      switch (intent.kind) {
        case 'strikeBeat': {
          event.preventDefault();
          if (!audio) {
            session.notify({ level: 'warning', message: '请先加载音频后再打轴。' });
            return;
          }
          const beats = collectBranchBeats(lines);
          const idx = findBeatIndexForSelection(beats, selection);
          const beat = idx >= 0 ? beats[idx] : undefined;
          if (!beat) return;

          const playheadUs = computeMonotonicStrikeUs(audio.playheadUs(), prefs.tagOffsetMs, lastStrikeUsRef.current);
          lastStrikeUsRef.current = playheadUs;

          const prevOpen = openBeatRef.current;
          const closeTarget = prevOpen && !sameBeatTarget(prevOpen, beat.target) ? prevOpen : null;

          openBeatRef.current = beat.target;
          setOpenBeat(beat.target);
          strikeHistoryRef.current.push({ struck: beat, prevOpen });
          if (strikeHistoryRef.current.length > 200) strikeHistoryRef.current.shift();

          // Advance cursor synchronously to next beat so rapid alternating J/K taps never wait on network RTT.
          const nextBeat = beats[idx + 1];
          session.select(nextBeat ? nextBeat.selection : beat.selection);

          if (session.supportsCommand('lyrics.strikeBeat')) {
            void session
              .command({
                type: 'lyrics.strikeBeat',
                branchId,
                ...(closeTarget ? { closeTarget } : {}),
                openTarget: beat.target,
                playheadUs: toMicros(playheadUs),
              })
              .then((res) => {
                if (res?.timingResult?.closeGuardApplied) {
                  toast('上一句触发跨间奏自动兜底收尾（虚线估算），可在波形微调');
                }
              });
          } else if (session.supportsCommand('lyrics.stampTokenStart')) {
            void session.command({
              type: 'lyrics.stampTokenStart',
              target: { branchId: beat.target.branchId, lineId: beat.target.lineId, tokenId: beat.target.tokenId },
              playheadUs: toMicros(playheadUs),
            });
          }
          return;
        }

        case 'closeBeat': {
          event.preventDefault();
          if (!audio) return;
          const beats = collectBranchBeats(lines);
          let target = openBeatRef.current;
          if (!target) {
            const idx = findBeatIndexForSelection(beats, selection);
            target = idx >= 0 ? beats[idx]!.target : null;
          }
          if (!target) return;

          const playheadUs = computeMonotonicStrikeUs(audio.playheadUs(), prefs.tagOffsetMs, lastStrikeUsRef.current);
          clearOpenBeat();

          if (session.supportsCommand('lyrics.closeBeat')) {
            void session.command({
              type: 'lyrics.closeBeat',
              target,
              playheadUs: toMicros(playheadUs),
            });
          } else if (session.supportsCommand('lyrics.stampTokenEnd')) {
            void session.command({
              type: 'lyrics.stampTokenEnd',
              target: { branchId: target.branchId, lineId: target.lineId, tokenId: target.tokenId },
              playheadUs: toMicros(playheadUs),
            });
          }
          return;
        }

        case 'undoAndRewind': {
          event.preventDefault();
          const popped = strikeHistoryRef.current.pop();
          clearOpenBeat();
          latest.current.onUndo();
          if (popped) {
            session.select(popped.struck.selection);
          } else {
            const beats = collectBranchBeats(lines);
            const prev = stepBranchBeat(beats, selection, -1);
            if (prev) session.select(prev.selection);
          }
          if (audio) {
            audio.rewind(prefs.rewindMs, true);
          }
          return;
        }

        case 'togglePhraseEndHint': {
          event.preventDefault();
          if (!selection?.tokenId || !session.supportsCommand('lyrics.togglePhraseEndHint')) return;
          const line = lines.find((l) => l.id === selection.lineId);
          const token = line?.tokens.find((t) => t.id === selection.tokenId);
          if (!line || !token || !isContentToken(token)) return;
          void session.command({
            type: 'lyrics.togglePhraseEndHint',
            target: { branchId: line.branchId, lineId: line.id, tokenId: token.id },
          });
          return;
        }

        case 'clearSelectedTiming': {
          event.preventDefault();
          if (!selection?.tokenId) return;
          const line = lines.find((l) => l.id === selection.lineId);
          if (!line) return;
          const seg = selection.segmentId ? selectedSegment(line, selection) : undefined;
          if (seg && session.supportsCommand('lyrics.clearRubySegmentTime')) {
            void session.command({
              type: 'lyrics.clearRubySegmentTime',
              target: segmentTarget(line, seg.ruby, seg.segment.id),
            });
          } else if (session.supportsCommand('lyrics.clearTokenTime')) {
            void session.command({
              type: 'lyrics.clearTokenTime',
              target: { branchId: line.branchId, lineId: line.id, tokenId: selection.tokenId },
            });
          }
          return;
        }

        case 'speedDown':
        case 'speedUp': {
          event.preventDefault();
          if (!audio) return;
          const delta = (intent.kind === 'speedUp' ? 1 : -1) * (intent.fine ? 0.05 : 0.1);
          const next = audio.stepRate(delta);
          toast(`播放速度 ${next.toFixed(2).replace(/\.?0+$/, '')}×`);
          return;
        }

        case 'seekBack':
        case 'seekForward': {
          event.preventDefault();
          if (!audio) return;
          clearOpenBeat();
          const deltaUs = intent.kind === 'seekBack' ? -2_000_000 : 3_000_000;
          audio.seek(Math.max(0, audio.playheadUs() + deltaUs));
          return;
        }

        case 'openRubyPopover': {
          event.preventDefault();
          const line = lines.find((l) => l.id === selection?.lineId) ?? lines[0];
          if (!line) return;
          let token = selection?.tokenId ? line.tokens.find((t) => t.id === selection.tokenId) : undefined;
          if (!token || !isContentToken(token)) {
            token = line.tokens.find(isContentToken);
          }
          if (!token) return;
          session.select({ lineId: line.id, tokenId: token.id });
          openRubyPopover(line.id, token.id, null);
          return;
        }

        case 'prevLine':
        case 'nextLine': {
          event.preventDefault();
          clearOpenBeat();
          const delta = intent.kind === 'nextLine' ? 1 : -1;
          const curIdx = selection ? lines.findIndex((l) => l.id === selection.lineId) : -1;
          const nextLine = lines[curIdx < 0 ? 0 : curIdx + delta];
          if (!nextLine) return;
          const firstContent = nextLine.tokens.find(isContentToken);
          session.select(firstContent ? { lineId: nextLine.id, tokenId: firstContent.id } : { lineId: nextLine.id });
          return;
        }

        case 'prevToken':
        case 'nextToken': {
          event.preventDefault();
          clearOpenBeat();
          const delta = intent.kind === 'nextToken' ? 1 : -1;
          const next = stepContentToken(lines, selection, delta);
          if (next) session.select(next);
          return;
        }

        case 'prevBeat':
        case 'nextBeat': {
          event.preventDefault();
          clearOpenBeat();
          const delta = intent.kind === 'nextBeat' ? 1 : -1;
          const beats = collectBranchBeats(lines);
          const next = stepBranchBeat(beats, selection, delta);
          if (next) session.select(next.selection);
          return;
        }

        case 'nudgeTime': {
          event.preventDefault();
          if (!selection?.tokenId) return;
          const line = lines.find((l) => l.id === selection.lineId);
          if (!line) return;
          const seg = selection.segmentId ? selectedSegment(line, selection) : undefined;
          if (seg && session.supportsCommand('lyrics.setRubySegmentTime')) {
            const range = fromLyricTime(seg.segment.time);
            if (!isTimed(range)) return;
            const moved = nudgedRange(range, intent.frameDelta, session.frameRate);
            if (moved) {
              void session.command({
                type: 'lyrics.setRubySegmentTime',
                target: segmentTarget(line, seg.ruby, seg.segment.id),
                startUs: toMicros(moved.startUs),
                endUs: toMicros(moved.endUs),
              });
            }
          } else if (session.supportsCommand('lyrics.nudgeTokenTime')) {
            void session.command({
              type: 'lyrics.nudgeTokenTime',
              target: { branchId: line.branchId, lineId: line.id, tokenId: selection.tokenId },
              frameDelta: intent.frameDelta,
            });
          }
          return;
        }

        case 'splitRubyBeat':
        case 'mergeRubyBeat': {
          event.preventDefault();
          if (!selection?.tokenId || !session.supportsCommand('lyrics.setRubySegments')) return;
          const line = lines.find((l) => l.id === selection.lineId);
          const token = line?.tokens.find((t) => t.id === selection.tokenId);
          if (!line || !token) return;
          const ruby = rubyOf(line, token.id);
          if (!ruby || ruby.segments.length === 0) return;

          const currentSegs = ruby.segments.map((s) => s.text);
          let nextSegs: string[] | null = null;
          if (intent.kind === 'splitRubyBeat') {
            // Split selected segment (or first multi-mora segment) into standard morae.
            const targetIdx = selection.segmentId
              ? ruby.segments.findIndex((s) => s.id === selection.segmentId)
              : currentSegs.findIndex((s) => splitJapaneseMora(s).length > 1);
            const idx = targetIdx >= 0 ? targetIdx : 0;
            const split = splitJapaneseMora(currentSegs[idx]!);
            if (split.length > 1) {
              nextSegs = [...currentSegs.slice(0, idx), ...split, ...currentSegs.slice(idx + 1)];
            } else {
              const gs = graphemes(currentSegs[idx]!);
              if (gs.length > 1) {
                nextSegs = [...currentSegs.slice(0, idx), ...gs, ...currentSegs.slice(idx + 1)];
              }
            }
          } else if (currentSegs.length >= 2) {
            // Merge selected segment with next (or last two segments).
            const selIdx = selection.segmentId ? ruby.segments.findIndex((s) => s.id === selection.segmentId) : currentSegs.length - 2;
            const mergeAt = Math.max(0, Math.min(currentSegs.length - 2, selIdx < 0 ? currentSegs.length - 2 : selIdx));
            nextSegs = [
              ...currentSegs.slice(0, mergeAt),
              currentSegs[mergeAt]! + currentSegs[mergeAt + 1]!,
              ...currentSegs.slice(mergeAt + 2),
            ];
          }

          if (nextSegs) {
            void session.command({
              type: 'lyrics.setRubySegments',
              target: { branchId: line.branchId, lineId: line.id, tokenId: token.id },
              segments: nextSegs,
            });
          }
          return;
        }

        case 'splitLine': {
          event.preventDefault();
          if (!selection?.tokenId || !session.supportsCommand('lyrics.splitLine')) return;
          const line = lines.find((l) => l.id === selection.lineId);
          if (!line) return;
          const tokenIndex = line.tokens.findIndex((t) => t.id === selection.tokenId);
          if (tokenIndex <= 0) return;
          void session.command({
            type: 'lyrics.splitLine',
            target: { branchId: line.branchId, lineId: line.id },
            tokenIndex,
          });
          return;
        }

        case 'mergeLines': {
          event.preventDefault();
          if (!selection?.lineId || !session.supportsCommand('lyrics.mergeLines')) return;
          const lineIdx = lines.findIndex((l) => l.id === selection.lineId);
          if (lineIdx <= 0) return;
          const line = lines[lineIdx]!;
          void session.command({
            type: 'lyrics.mergeLines',
            target: { branchId: line.branchId, lineId: line.id },
          });
          return;
        }

        case 'inlineEditLine': {
          event.preventDefault();
          if (selection?.lineId && session.supportsCommand('lyrics.setLineText')) {
            onInlineEditLine(selection.lineId);
          }
          return;
        }

        case 'openTokenMenu': {
          event.preventDefault();
          if (selection?.tokenId) {
            onToggleTokenMenu();
          }
          return;
        }
      }
    };

    window.addEventListener('keydown', onSpace, true);
    window.addEventListener('keydown', onKeyDown);
    return () => {
      window.removeEventListener('keydown', onSpace, true);
      window.removeEventListener('keydown', onKeyDown);
    };
  }, []);
}

const NON_TEXT_INPUTS = new Set(['checkbox', 'radio', 'range', 'button', 'submit', 'reset', 'file', 'color']);

/** 这些角色的元素里按键归它们：对话框、侧面板（React Aria 的 Modal 都是 dialog）、菜单、原地注音浮窗。 */
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
        node.hasAttribute('data-trigger') ||
        (node.tagName === 'MDUI-DIALOG' && (node as HTMLElement & { open?: boolean }).open === true)),
  );
}
