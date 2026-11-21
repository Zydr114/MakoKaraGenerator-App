import {
  memo,
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type KeyboardEvent,
  type MouseEvent,
  type RefObject,
} from 'react';
import type { LyricBeatTarget, LyricTimingTarget, ProjectEditCommand, RubyAnnotation, RubyTextSegment, TimingStatus } from '../../protocol';
import type { AudioController } from '../audio/audio-controller';
import { isTimed, lineAt, type EditorLine, type EditorToken, type Selection } from '../model/lyrics';
import { nudgedRange, rubyOf, segmentTarget, selectedSegment, stampedEnd, stampedStart, type TimeRange } from '../model/ruby-segments';
import { formatTime, fromLyricTime, parseTime, toMicros } from '../model/time';
import { graphemes } from '../model/token-segments';
import { classifyToken, hasPhraseEndHint, isContentToken, isLastContentToken } from '../model/token-semantics';
import type { ScrollMode } from '../phonetics/timing-preferences';
import type { EditorSession, EditorState } from '../session/editor-session';
import { Button, IconButton } from '../ui/Button';
import { cls } from '../ui/cls';
import { confirmAction } from '../ui/dialogs';
import { toast } from '../ui/feedback';
import { TextField } from '../ui/fields';
import { Icon } from '../ui/Icon';
import { StatusIcon } from './LineList';
import {
  beatKeyOf,
  computeRubySegmentWipeProgress,
  computeTokenWipeProgress,
  computeUntimedBridgeFill,
  type LyricsWorkMode,
} from './timing-keymap';

export interface GhostCompanion {
  branchId: string;
  branchName: string;
  lineId: string;
  text: string;
}

/** 计算当前分支每一行对应的其他分支伴随 Ghost 行（直接关联 primaryLineId 或时间重叠）。 */
function computeGhostMap(state: EditorState): Map<string, GhostCompanion[]> {
  const result = new Map<string, GhostCompanion[]>();
  const branches = state.branches ?? [];
  if (branches.length < 2) return result;

  const otherBranches = branches.filter((b) => b.id !== state.branchId);
  if (otherBranches.length === 0) return result;

  // Build quick lookup for primaryLineId links.
  const linkedToActive = new Map<string, GhostCompanion[]>();
  const otherLinesByBranch: Array<{ branchId: string; branchName: string; lines: readonly EditorLine[] }> = [];

  for (const b of otherBranches) {
    const bLines = state.linesByBranch[b.id] ?? [];
    otherLinesByBranch.push({ branchId: b.id, branchName: b.name, lines: bLines });
    for (const ol of bLines) {
      if (!ol.primaryLineId || !ol.text) continue;
      const list = linkedToActive.get(ol.primaryLineId);
      const item: GhostCompanion = { branchId: b.id, branchName: b.name, lineId: ol.id, text: ol.text };
      if (list) list.push(item);
      else linkedToActive.set(ol.primaryLineId, [item]);
    }
  }

  for (const line of state.lines) {
    const seen = new Set<string>();
    const companions: GhostCompanion[] = [];
    const pushUnique = (item: GhostCompanion) => {
      const k = `${item.branchId}:${item.lineId}`;
      if (seen.has(k)) return;
      seen.add(k);
      companions.push(item);
    };

    // 1) Other branch lines explicitly linked to this line.
    const linked = linkedToActive.get(line.id);
    if (linked) {
      for (const item of linked) pushUnique(item);
    }

    // 2) If active branch is non-primary and this line links to a primary line, show that primary line.
    if (line.primaryLineId && state.primaryBranchId && state.primaryBranchId !== state.branchId) {
      const primaryBranch = branches.find((b) => b.id === state.primaryBranchId);
      const primaryLine = (state.linesByBranch[state.primaryBranchId] ?? []).find((l) => l.id === line.primaryLineId);
      if (primaryBranch && primaryLine && primaryLine.text) {
        pushUnique({ branchId: primaryBranch.id, branchName: primaryBranch.name, lineId: primaryLine.id, text: primaryLine.text });
      }
    }

    // 3) Any other branch's timed line overlapping this timed line.
    if (isTimed(line)) {
      for (const ob of otherLinesByBranch) {
        for (const ol of ob.lines) {
          if (!ol.text || !isTimed(ol)) continue;
          if (ol.startUs < line.endUs && ol.endUs > line.startUs) {
            pushUnique({ branchId: ob.branchId, branchName: ob.branchName, lineId: ol.id, text: ol.text });
          }
        }
      }
    }

    if (companions.length > 0) {
      result.set(line.id, companions);
    }
  }

  return result;
}

interface WipeTokenNode {
  el: HTMLElement;
  lineIndex: number;
  tokenIndex: number;
  kind: 'content' | 'whitespace' | 'punctuation';
  lastWipe: string;
  lastActive: boolean;
}

interface WipeRubyNode {
  el: HTMLElement;
  lineIndex: number;
  tokenIndex: number;
  segmentIndex: number;
  lastWipe: string;
}

interface EditTokenRange {
  lineId: string;
  anchorIndex: number;
  focusIndex: number;
}

/**
 * 动态滚动卡拉 OK 打轴画布（Phase 3 §5.2）：
 * - 60fps 独立 rAF 循环直接操作 CSS 自定义属性 `--wipe`，绝不每帧触发 React 重渲染；
 * - 严格对齐 Phase 0 A1/B1（多拍注音按字素权重分段同步、无时间空白/标点跟亮桥接）；
 * - 三层字块（上层分拍注音、中层扫字正文、下层 ▸ / ▸▸ / ○ / ● / 虚线 Estimated 节奏点）；
 * - 同时间段/关联其他分支低对比度 Ghost 衬底层（严格只读，双击确认后切换分支）；
 * - 编辑模式支持双击行号/Ctrl+Enter 原地改词，以及拖选/Shift+点击多字弹出浮动工具条；
 * - 底部集成精确起止时间微调条与实时模式键位速查。
 */
export function LyricsStageCanvas(props: {
  session: EditorSession;
  state: EditorState;
  audio: AudioController | null;
  mode: Exclude<LyricsWorkMode, 'quickEdit'>;
  scrollMode: ScrollMode;
  openBeat: LyricBeatTarget | null;
  inlineEditingLineId: string | null;
  onInlineEditLine: (lineId: string | null) => void;
  tokenMenuOpen: boolean;
  onTokenMenuOpenChange: (open: boolean) => void;
  onOpenRubyPopover: (lineId: string, tokenId: string, element: HTMLElement | null) => void;
  onResplitLine: (line: EditorLine) => void;
  stageRootRef: RefObject<HTMLDivElement | null>;
}) {
  const {
    session,
    state,
    audio,
    mode,
    scrollMode,
    openBeat,
    inlineEditingLineId,
    onInlineEditLine,
    tokenMenuOpen,
    onTokenMenuOpenChange,
    onOpenRubyPopover,
    onResplitLine,
    stageRootRef,
  } = props;

  const scrollRef = useRef<HTMLDivElement>(null);
  const [editRange, setEditRange] = useState<EditTokenRange | null>(null);
  const draggingRangeRef = useRef<{ lineId: string; anchorIndex: number } | null>(null);

  // Clear multi-token range when switching modes or branches.
  useEffect(() => {
    setEditRange(null);
    onTokenMenuOpenChange(false);
  }, [mode, state.branchId, onTokenMenuOpenChange]);

  const ghostMap = useMemo(() => computeGhostMap(state), [state.branchId, state.branches, state.linesByBranch, state.lines, state.primaryBranchId]);

  const selectedLineId = state.selection?.lineId;
  const selectedLineIndex = selectedLineId ? state.lines.findIndex((l) => l.id === selectedLineId) : -1;
  const selectedLine = selectedLineIndex >= 0 ? state.lines[selectedLineIndex] : undefined;
  const openBeatKey = openBeat ? beatKeyOf(openBeat) : null;

  // Keep refs for zero-React-render 60fps wipe loop.
  const linesRef = useRef(state.lines);
  linesRef.current = state.lines;
  const audioRef = useRef(audio);
  audioRef.current = audio;
  const scrollModeRef = useRef(scrollMode);
  scrollModeRef.current = scrollMode;

  const lineElsRef = useRef<Map<string, HTMLElement>>(new Map());
  const wipeTokensRef = useRef<WipeTokenNode[]>([]);
  const wipeRubiesRef = useRef<WipeRubyNode[]>([]);
  const needsPaintRef = useRef(true);
  const activeSingingLineIdRef = useRef<string | null>(null);

  // Rebuild DOM index ONLY when lines structure changes (not per frame).
  useLayoutEffect(() => {
    const root = scrollRef.current;
    if (!root) return;
    const lineMap = new Map<string, HTMLElement>();
    for (const el of root.querySelectorAll<HTMLElement>('[data-stage-line]')) {
      const id = el.dataset.stageLine;
      if (id) lineMap.set(id, el);
    }
    lineElsRef.current = lineMap;

    const tokenNodes: WipeTokenNode[] = [];
    for (const el of root.querySelectorAll<HTMLElement>('[data-wipe-tok]')) {
      const li = Number(el.dataset.li);
      const ti = Number(el.dataset.ti);
      const kind = (el.dataset.kind as 'content' | 'whitespace' | 'punctuation') || 'content';
      tokenNodes.push({ el, lineIndex: li, tokenIndex: ti, kind, lastWipe: '', lastActive: false });
    }
    wipeTokensRef.current = tokenNodes;

    const rubyNodes: WipeRubyNode[] = [];
    for (const el of root.querySelectorAll<HTMLElement>('[data-wipe-rseg]')) {
      rubyNodes.push({
        el,
        lineIndex: Number(el.dataset.li),
        tokenIndex: Number(el.dataset.ti),
        segmentIndex: Number(el.dataset.si),
        lastWipe: '',
      });
    }
    wipeRubiesRef.current = rubyNodes;
    needsPaintRef.current = true;
  }, [state.lines]);

  const scrollLineToCenter = useCallback((lineId: string, smooth: boolean) => {
    const container = scrollRef.current;
    const el = lineElsRef.current.get(lineId);
    if (!container || !el) return;
    const targetTop = el.offsetTop - container.clientHeight / 2 + el.offsetHeight / 2;
    const clamped = Math.max(0, targetTop);
    if (Math.abs(container.scrollTop - clamped) < 6) return;
    container.scrollTo({ top: clamped, behavior: smooth ? 'smooth' : 'auto' });
  }, []);

  // Scroll selected line into center when selection changes (lock mode, or follow mode while paused).
  useEffect(() => {
    if (!selectedLineId) return;
    if (scrollMode === 'free') return;
    if (scrollMode === 'follow' && audio?.playing) return;
    scrollLineToCenter(selectedLineId, true);
  }, [selectedLineId, scrollMode, audio, scrollLineToCenter]);

  // 60fps requestAnimationFrame karaoke wipe & playback scroll engine.
  useEffect(() => {
    let frame = 0;
    let lastUs = Number.NaN;

    const tick = () => {
      frame = requestAnimationFrame(tick);
      const a = audioRef.current;
      const us = a ? a.playheadUs() : 0;
      const lines = linesRef.current;

      if (us === lastUs && !needsPaintRef.current) return;
      lastUs = us;
      needsPaintRef.current = false;

      // 1) Update token wipe percentages.
      const baseProgressByKey = new Map<string, number>();
      for (const item of wipeTokensRef.current) {
        const line = lines[item.lineIndex];
        const token = line?.tokens[item.tokenIndex];
        if (!line || !token) continue;
        let progress = 0;
        if (item.kind === 'content') {
          const ruby = rubyOf(line, token.id);
          progress = computeTokenWipeProgress(token, ruby, us);
          baseProgressByKey.set(`${item.lineIndex}:${item.tokenIndex}`, progress);
        } else {
          progress = computeUntimedBridgeFill(line.tokens, item.tokenIndex, us);
        }
        const pct = `${(progress * 100).toFixed(2)}%`;
        if (pct !== item.lastWipe) {
          item.el.style.setProperty('--wipe', pct);
          item.lastWipe = pct;
        }
        const active = progress > 0.001 && progress < 0.999;
        if (active !== item.lastActive) {
          if (active) item.el.dataset.active = '1';
          else delete item.el.dataset.active;
          item.lastActive = active;
        }
      }

      // 2) Update individual ruby segment wipe percentages.
      for (const item of wipeRubiesRef.current) {
        const line = lines[item.lineIndex];
        const token = line?.tokens[item.tokenIndex];
        const ruby = line && token ? rubyOf(line, token.id) : undefined;
        const seg = ruby?.segments[item.segmentIndex];
        if (!seg) continue;
        const baseFallback = baseProgressByKey.get(`${item.lineIndex}:${item.tokenIndex}`) ?? 0;
        const progress = computeRubySegmentWipeProgress(fromLyricTime(seg.time), baseFallback, us);
        const pct = `${(progress * 100).toFixed(2)}%`;
        if (pct !== item.lastWipe) {
          item.el.style.setProperty('--wipe', pct);
          item.lastWipe = pct;
        }
      }

      // 3) Highlight currently singing line and follow scroll during playback.
      const singing = lineAt(lines, us);
      const singingId = singing?.id ?? null;
      if (singingId !== activeSingingLineIdRef.current) {
        if (activeSingingLineIdRef.current) {
          const prevEl = lineElsRef.current.get(activeSingingLineIdRef.current);
          if (prevEl) delete prevEl.dataset.singing;
        }
        activeSingingLineIdRef.current = singingId;
        if (singingId) {
          const nextEl = lineElsRef.current.get(singingId);
          if (nextEl) nextEl.dataset.singing = '1';
          if (scrollModeRef.current === 'follow' && a?.playing && !draggingRangeRef.current) {
            scrollLineToCenter(singingId, true);
          }
        }
      }
    };

    frame = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(frame);
  }, [scrollLineToCenter]);

  // Global mouseup terminates edit-mode drag selection cleanly.
  useEffect(() => {
    const onUp = () => {
      draggingRangeRef.current = null;
    };
    window.addEventListener('mouseup', onUp);
    return () => window.removeEventListener('mouseup', onUp);
  }, []);

  const handleGhostDoubleClick = useCallback(
    async (ghost: GhostCompanion) => {
      const ok = await confirmAction({
        headline: `切换到「${ghost.branchName}」进行编辑？`,
        description: `当前处于只读伴随预览层。确认后将切换当前激活分支至「${ghost.branchName}」并选中该行。`,
        confirmText: '切换分支',
      });
      if (!ok) return;
      session.selectBranch(ghost.branchId);
      session.select({ lineId: ghost.lineId });
    },
    [session],
  );

  // Resolve active floating bar token set (multi-selection range in edit mode, or single token when `tokenMenuOpen`).
  const floatingSelection = useMemo(() => {
    if (mode !== 'edit') return null;
    if (editRange) {
      const line = state.lines.find((l) => l.id === editRange.lineId);
      if (!line) return null;
      const from = Math.max(0, Math.min(editRange.anchorIndex, editRange.focusIndex));
      const to = Math.min(line.tokens.length - 1, Math.max(editRange.anchorIndex, editRange.focusIndex));
      if (to >= from && (to > from || tokenMenuOpen)) {
        return {
          line,
          lineIndex: state.lines.findIndex((l) => l.id === line.id),
          from,
          to,
          tokens: line.tokens.slice(from, to + 1),
        };
      }
    }
    if (tokenMenuOpen && selectedLine && state.selection?.tokenId) {
      const idx = selectedLine.tokens.findIndex((t) => t.id === state.selection!.tokenId);
      if (idx >= 0) {
        return {
          line: selectedLine,
          lineIndex: selectedLineIndex,
          from: idx,
          to: idx,
          tokens: [selectedLine.tokens[idx]!],
        };
      }
    }
    return null;
  }, [mode, editRange, tokenMenuOpen, state.lines, selectedLine, selectedLineIndex, state.selection?.tokenId]);

  const selectedRangeTokenIds = useMemo(() => {
    if (!editRange) return null;
    const line = state.lines.find((l) => l.id === editRange.lineId);
    if (!line) return null;
    const from = Math.max(0, Math.min(editRange.anchorIndex, editRange.focusIndex));
    const to = Math.min(line.tokens.length - 1, Math.max(editRange.anchorIndex, editRange.focusIndex));
    if (to <= from) return null;
    const ids = new Set<string>();
    for (let i = from; i <= to; i++) ids.add(line.tokens[i]!.id);
    return ids;
  }, [editRange, state.lines]);

  // Floating bar actions
  const runMergeSelectedTokens = async () => {
    if (!floatingSelection || floatingSelection.tokens.length < 2) return;
    const { line, from, to } = floatingSelection;
    const firstToken = line.tokens[from]!;
    const count = to - from;
    const edits: ProjectEditCommand[] = Array.from({ length: count }, () => ({
      type: 'lyrics.mergeToken',
      target: { branchId: line.branchId, lineId: line.id, tokenId: firstToken.id },
    }));
    const res = await session.applyEdits(edits);
    if (res.applied > 0) {
      setEditRange(null);
      onTokenMenuOpenChange(false);
      toast(`已合并 ${count + 1} 个词元`);
    }
  };

  const runSplitSingleToken = async () => {
    if (!floatingSelection || floatingSelection.tokens.length !== 1) return;
    const { line, from, tokens } = floatingSelection;
    const token = tokens[0]!;
    const chars = graphemes(token.text);
    if (chars.length < 2) return;
    if (isTimed(token) && session.supportsCommand('lyrics.splitToken')) {
      const res = await session.command({
        type: 'lyrics.splitToken',
        target: { branchId: line.branchId, lineId: line.id, tokenId: token.id },
      });
      if (res?.changed) onTokenMenuOpenChange(false);
      return;
    }
    if (session.supportsCommand('lyrics.resplitLine')) {
      const segments = [
        ...line.tokens.slice(0, from).map((t) => t.text),
        ...chars,
        ...line.tokens.slice(from + 1).map((t) => t.text),
      ];
      if (segments.join('') === line.text) {
        const res = await session.command({
          type: 'lyrics.resplitLine',
          target: { branchId: line.branchId, lineId: line.id },
          segments,
          estimateTimes: false,
        });
        if (res?.changed) onTokenMenuOpenChange(false);
      }
    }
  };

  const runClearSelectedTimes = async () => {
    if (!floatingSelection) return;
    const { line, tokens } = floatingSelection;
    const edits: ProjectEditCommand[] = tokens
      .filter((t) => t.startUs !== undefined)
      .map((t) => ({ type: 'lyrics.clearTokenTime', target: { branchId: line.branchId, lineId: line.id, tokenId: t.id } }));
    if (edits.length === 0) return;
    const res = await session.applyEdits(edits);
    if (res.applied > 0) {
      setEditRange(null);
      onTokenMenuOpenChange(false);
      toast(`已清除 ${res.applied} 个词元的时间`);
    }
  };

  const runClearSelectedRuby = async () => {
    if (!floatingSelection) return;
    const { line, tokens } = floatingSelection;
    const edits: ProjectEditCommand[] = tokens
      .filter((t) => !!rubyOf(line, t.id))
      .map((t) => ({
        type: 'lyrics.setRubySegments',
        target: { branchId: line.branchId, lineId: line.id, tokenId: t.id },
        segments: [],
      }));
    if (edits.length === 0) return;
    const res = await session.applyEdits(edits);
    if (res.applied > 0) {
      setEditRange(null);
      onTokenMenuOpenChange(false);
      toast(`已清除 ${res.applied} 处注音`);
    }
  };

  const runToggleSelectedPhraseEnd = async () => {
    if (!floatingSelection) return;
    const { line, tokens } = floatingSelection;
    const edits: ProjectEditCommand[] = tokens
      .filter((t) => isContentToken(t))
      .map((t) => ({
        type: 'lyrics.togglePhraseEndHint',
        target: { branchId: line.branchId, lineId: line.id, tokenId: t.id },
      }));
    if (edits.length === 0) return;
    await session.applyEdits(edits);
  };

  const runSplitLineAtSelection = async () => {
    if (!floatingSelection || floatingSelection.from <= 0) return;
    const { line, from } = floatingSelection;
    const res = await session.command({
      type: 'lyrics.splitLine',
      target: { branchId: line.branchId, lineId: line.id },
      tokenIndex: from,
    });
    if (res?.changed) {
      setEditRange(null);
      onTokenMenuOpenChange(false);
      toast('已在该词元前断行');
    }
  };

  const runMergeWithPrevLine = async () => {
    if (!floatingSelection || floatingSelection.lineIndex <= 0) return;
    const { line } = floatingSelection;
    const res = await session.command({
      type: 'lyrics.mergeLines',
      target: { branchId: line.branchId, lineId: line.id },
    });
    if (res?.changed) {
      setEditRange(null);
      onTokenMenuOpenChange(false);
      toast('已与上一行合并');
    }
  };

  return (
    <div className={cls('lyrics-stage-shell', `mode-${mode}`)} ref={stageRootRef}>
      <div className="lyrics-stage-scroll" ref={scrollRef} aria-label="动态歌词打轴画布">
        {state.lines.length === 0 ? (
          <div className="stage-empty">
            <Icon name="lyrics" className="stage-empty-ic" />
            <h3>当前来源还没有歌词</h3>
            <p className="faint">从左栏导入歌词文件、粘贴文本，或切换右上角「全文快编」直接输入歌词。</p>
          </div>
        ) : (
          <div className="stage-track">
            {state.lines.map((line, lineIndex) => {
              const isSelectedLine = line.id === selectedLineId;
              const isInlineEditing = inlineEditingLineId === line.id;
              const ghosts = ghostMap.get(line.id);
              const showFloatHere = floatingSelection?.line.id === line.id;

              return (
                <div
                  key={line.id}
                  className={cls('stage-line', isSelectedLine && 'is-selected', !isTimed(line) && 'is-untimed')}
                  data-stage-line={line.id}
                  onClick={(e) => {
                    // Clicking empty area on line selects line (first content token if in timing mode).
                    if ((e.target as HTMLElement).closest('button, input')) return;
                    setEditRange(null);
                    onTokenMenuOpenChange(false);
                    const firstContent = line.tokens.find(isContentToken);
                    session.select(firstContent ? { lineId: line.id, tokenId: firstContent.id } : { lineId: line.id });
                  }}
                >
                  {showFloatHere && floatingSelection && (
                    <div className="stage-float-bar" role="toolbar" aria-label="选中词元操作">
                      <span className="sfb-count">已选 {floatingSelection.tokens.length} 词元</span>
                      {floatingSelection.tokens.length >= 2 && (
                        <Button
                          variant="tonal"
                          size="sm"
                          icon="merge"
                          isDisabled={!session.supportsCommand('lyrics.mergeToken')}
                          onPress={() => void runMergeSelectedTokens()}
                        >
                          合并
                        </Button>
                      )}
                      {floatingSelection.tokens.length === 1 && graphemes(floatingSelection.tokens[0]!.text).length >= 2 && (
                        <Button variant="tonal" size="sm" icon="call_split" onPress={() => void runSplitSingleToken()}>
                          拆开
                        </Button>
                      )}
                      {floatingSelection.tokens.length === 1 && isContentToken(floatingSelection.tokens[0]!) && (
                        <Button
                          variant="tonal"
                          size="sm"
                          icon="translate"
                          onPress={() => {
                            const tok = floatingSelection.tokens[0]!;
                            const el = stageRootRef.current?.querySelector<HTMLElement>(`[data-token-id="${tok.id}"]`) ?? null;
                            onOpenRubyPopover(line.id, tok.id, el);
                          }}
                        >
                          注音 (F2)
                        </Button>
                      )}
                      <Button variant="ghost" size="sm" icon="wand_stars" onPress={() => onResplitLine(line)}>
                        重切本行
                      </Button>
                      {floatingSelection.from > 0 && session.supportsCommand('lyrics.splitLine') && (
                        <Button variant="ghost" size="sm" onPress={() => void runSplitLineAtSelection()}>
                          在此断行
                        </Button>
                      )}
                      {lineIndex > 0 && session.supportsCommand('lyrics.mergeLines') && (
                        <Button variant="ghost" size="sm" onPress={() => void runMergeWithPrevLine()}>
                          与上行合并
                        </Button>
                      )}
                      {floatingSelection.tokens.some(isContentToken) && session.supportsCommand('lyrics.togglePhraseEndHint') && (
                        <Button variant="ghost" size="sm" onPress={() => void runToggleSelectedPhraseEnd()}>
                          切换停顿 ○
                        </Button>
                      )}
                      {floatingSelection.tokens.some((t) => t.startUs !== undefined) && (
                        <Button variant="ghost" size="sm" onPress={() => void runClearSelectedTimes()}>
                          清时间
                        </Button>
                      )}
                      {floatingSelection.tokens.some((t) => !!rubyOf(line, t.id)) && (
                        <Button variant="ghost" size="sm" danger onPress={() => void runClearSelectedRuby()}>
                          删注音
                        </Button>
                      )}
                      <IconButton
                        icon="close"
                        label="关闭浮动栏"
                        size="sm"
                        onPress={() => {
                          setEditRange(null);
                          onTokenMenuOpenChange(false);
                        }}
                      />
                    </div>
                  )}

                  <div
                    className="stage-line-gutter"
                    title={mode === 'edit' ? '双击原地修改本行歌词正文' : '点击选中本行'}
                    onDoubleClick={(e) => {
                      e.stopPropagation();
                      if (mode === 'edit' && session.supportsCommand('lyrics.setLineText')) {
                        onInlineEditLine(line.id);
                      }
                    }}
                  >
                    <span className="slg-num">{String(lineIndex + 1).padStart(2, '0')}</span>
                    {line.status && <StatusIcon status={line.status} />}
                    <span className="slg-time tnum">{line.startUs !== undefined ? formatTime(line.startUs) : '未打轴'}</span>
                    {mode === 'edit' && session.supportsCommand('lyrics.setLineText') && (
                      <button
                        type="button"
                        className="slg-edit-btn"
                        aria-label={`编辑第 ${lineIndex + 1} 行文本`}
                        title="原地修改本行正文 (Ctrl+Enter)"
                        onClick={(e) => {
                          e.stopPropagation();
                          onInlineEditLine(line.id);
                        }}
                      >
                        <Icon name="edit" />
                      </button>
                    )}
                  </div>

                  <div className="stage-line-main">
                    {isInlineEditing ? (
                      <InlineLineEditor
                        line={line}
                        onCommit={async (nextText) => {
                          if (nextText !== line.text) {
                            await session.command({
                              type: 'lyrics.setLineText',
                              target: { branchId: line.branchId, lineId: line.id },
                              text: nextText,
                            });
                          }
                          onInlineEditLine(null);
                        }}
                        onCancel={() => onInlineEditLine(null)}
                      />
                    ) : line.tokens.length === 0 ? (
                      <div className="stage-empty-line faint" lang="ja">
                        {line.text || '（空行）'}
                      </div>
                    ) : (
                      <div className="stage-tokens" lang="ja">
                        {line.tokens.map((token, tokenIndex) => (
                          <StageTokenBlock
                            key={token.id}
                            line={line}
                            lineIndex={lineIndex}
                            token={token}
                            tokenIndex={tokenIndex}
                            ruby={rubyOf(line, token.id)}
                            selection={state.selection}
                            mode={mode}
                            openBeatKey={openBeatKey}
                            inMultiSelect={!!selectedRangeTokenIds?.has(token.id)}
                            onSelect={(sel) => {
                              setEditRange(null);
                              onTokenMenuOpenChange(false);
                              session.select(sel);
                            }}
                            onTokenMouseDown={(event) => {
                              if (mode !== 'edit' || event.button !== 0) return;
                              if (event.shiftKey && state.selection?.lineId === line.id && state.selection.tokenId) {
                                const anchorIdx = line.tokens.findIndex((t) => t.id === state.selection!.tokenId);
                                if (anchorIdx >= 0) {
                                  event.preventDefault();
                                  setEditRange({ lineId: line.id, anchorIndex: anchorIdx, focusIndex: tokenIndex });
                                  return;
                                }
                              }
                              draggingRangeRef.current = { lineId: line.id, anchorIndex: tokenIndex };
                            }}
                            onTokenMouseEnter={() => {
                              const drag = draggingRangeRef.current;
                              if (mode !== 'edit' || !drag || drag.lineId !== line.id) return;
                              if (tokenIndex !== drag.anchorIndex) {
                                setEditRange({ lineId: line.id, anchorIndex: drag.anchorIndex, focusIndex: tokenIndex });
                              }
                            }}
                            onOpenRuby={(el) => onOpenRubyPopover(line.id, token.id, el)}
                          />
                        ))}
                      </div>
                    )}

                    {/* Ghost companion layer for other branches (strictly read-only; double-click prompts branch switch) */}
                    {ghosts && ghosts.length > 0 && (
                      <div className="stage-ghost-group" aria-label="其他分支伴随预览">
                        {ghosts.map((g) => (
                          <div
                            key={`${g.branchId}:${g.lineId}`}
                            className="stage-ghost"
                            title={`伴随预览（${g.branchName}，只读）· 双击切换至该分支编辑`}
                            onDoubleClick={(e) => {
                              e.stopPropagation();
                              void handleGhostDoubleClick(g);
                            }}
                          >
                            <span className="sg-tag">{g.branchName}</span>
                            <span className="sg-text" lang="ja">
                              {g.text}
                            </span>
                          </div>
                        ))}
                      </div>
                    )}
                  </div>
                </div>
              );
            })}
          </div>
        )}
      </div>

      {/* Canvas footer: precision timing editor + live Mode M1 shortcut cheat sheet */}
      <StageFooterBar
        session={session}
        audio={audio}
        line={selectedLine}
        tokenId={state.selection?.tokenId}
        segmentId={state.selection?.segmentId}
        mode={mode}
      />
    </div>
  );
}

/** 三层字块：上层分拍注音、中层扫字正文、下层节奏指示器（▸ / ▸▸ / ○ / ● / 虚线 Estimated）。 */
const StageTokenBlock = memo(function StageTokenBlock(props: {
  line: EditorLine;
  lineIndex: number;
  token: EditorToken;
  tokenIndex: number;
  ruby: RubyAnnotation | undefined;
  selection: Selection | undefined;
  mode: Exclude<LyricsWorkMode, 'quickEdit'>;
  openBeatKey: string | null;
  inMultiSelect: boolean;
  onSelect: (selection: Selection) => void;
  onTokenMouseDown: (event: MouseEvent<HTMLElement>) => void;
  onTokenMouseEnter: () => void;
  onOpenRuby: (el: HTMLElement | null) => void;
}) {
  const { line, lineIndex, token, tokenIndex, ruby, selection, mode, openBeatKey, inMultiSelect } = props;
  const kind = classifyToken(token.text);
  const isSelectedToken = selection?.lineId === line.id && selection.tokenId === token.id;

  if (kind === 'whitespace') {
    const isPause = token.text.length > 1 || token.text !== ' ';
    return (
      <span
        className={cls('stage-tok kind-ws', isPause && 'is-pause-ws', isSelectedToken && 'is-sel', inMultiSelect && 'in-multi')}
        data-token-id={token.id}
        data-wipe-tok="1"
        data-li={lineIndex}
        data-ti={tokenIndex}
        data-kind="whitespace"
        title={isPause ? '停顿空格（前字自动挂载 ○ 提示按 L 收尾）' : '切分空格'}
        onMouseDown={props.onTokenMouseDown}
        onMouseEnter={props.onTokenMouseEnter}
        onClick={(e) => {
          if (mode === 'edit') {
            e.stopPropagation();
            props.onSelect({ lineId: line.id, tokenId: token.id });
          }
        }}
      >
        <span className="st-ws-visual" aria-hidden>
          {token.text === '\u3000' ? '␣' : mode === 'edit' ? '·' : '\u00A0'}
        </span>
      </span>
    );
  }

  if (kind === 'punctuation') {
    return (
      <span
        className={cls('stage-tok kind-punct', isSelectedToken && 'is-sel', inMultiSelect && 'in-multi')}
        data-token-id={token.id}
        data-wipe-tok="1"
        data-li={lineIndex}
        data-ti={tokenIndex}
        data-kind="punctuation"
        onMouseDown={props.onTokenMouseDown}
        onMouseEnter={props.onTokenMouseEnter}
        onClick={(e) => {
          if (mode === 'edit') {
            e.stopPropagation();
            props.onSelect({ lineId: line.id, tokenId: token.id });
          }
        }}
      >
        <span className="st-base st-punct-text">{token.text}</span>
      </span>
    );
  }

  // Content token: 3-layer block
  const phraseHint = hasPhraseEndHint(line.tokens, tokenIndex);
  const lastContent = isLastContentToken(line.tokens, tokenIndex);
  const isPhraseEnd = phraseHint || lastContent;
  const multiBeatRuby = ruby && ruby.segments.length >= 2 ? ruby : undefined;
  const tokenBeatKey = `${line.id}:${token.id}`;
  const isTokenOpenBeat = openBeatKey === tokenBeatKey;

  return (
    <span
      className={cls(
        'stage-tok kind-content',
        isSelectedToken && 'is-sel',
        inMultiSelect && 'in-multi',
        !isTimed(token) && 'is-untimed',
        isTokenOpenBeat && 'is-open-beat',
        isPhraseEnd && 'has-phrase-end',
      )}
      data-token-id={token.id}
      onMouseDown={props.onTokenMouseDown}
      onMouseEnter={props.onTokenMouseEnter}
      onClick={(e) => {
        e.stopPropagation();
        if (multiBeatRuby && mode === 'timing') {
          props.onSelect({ lineId: line.id, tokenId: token.id, segmentId: multiBeatRuby.segments[0]!.id });
        } else {
          props.onSelect({ lineId: line.id, tokenId: token.id });
        }
      }}
      onDoubleClick={(e) => {
        e.stopPropagation();
        props.onSelect({ lineId: line.id, tokenId: token.id });
        props.onOpenRuby(e.currentTarget);
      }}
    >
      {/* Layer 1: Ruby (single or multi-beat segments) */}
      <span className="st-ruby" aria-label={ruby ? `注音 ${ruby.text}` : undefined}>
        {ruby ? (
          ruby.segments.map((seg, segIdx) => {
            const segKey = `${line.id}:${token.id}:${seg.id}`;
            const isSegSel = isSelectedToken && selection?.segmentId === seg.id;
            const isSegOpen = openBeatKey === segKey;
            return (
              <span
                key={seg.id}
                className={cls('st-rseg', isSegSel && 'is-sel', isSegOpen && 'is-open-beat')}
                data-wipe-rseg="1"
                data-li={lineIndex}
                data-ti={tokenIndex}
                data-si={segIdx}
                onClick={(e) => {
                  e.stopPropagation();
                  props.onSelect({ lineId: line.id, tokenId: token.id, segmentId: seg.id });
                }}
              >
                {seg.text}
              </span>
            );
          })
        ) : (
          <span className="st-ruby-placeholder" aria-hidden>
            {'\u200B'}
          </span>
        )}
      </span>

      {/* Layer 2: Base lyric text with 60fps wipe gradient */}
      <span
        className="st-base"
        data-wipe-tok="1"
        data-li={lineIndex}
        data-ti={tokenIndex}
        data-kind="content"
      >
        {token.text}
      </span>

      {/* Layer 3: Rhythm / beat & phrase-end indicators */}
      <span className="st-dots" aria-hidden>
        {multiBeatRuby ? (
          multiBeatRuby.segments.map((seg, segIdx) => {
            const segTime = fromLyricTime(seg.time);
            const segTimed = isTimed(segTime);
            const segKey = `${line.id}:${token.id}:${seg.id}`;
            const isSegOpen = openBeatKey === segKey;
            const isLastSeg = segIdx === multiBeatRuby.segments.length - 1;
            return (
              <BeatIndicatorDot
                key={seg.id}
                timed={segTimed}
                status={seg.timingStatus}
                isOpen={isSegOpen}
                isPhraseEnd={isPhraseEnd && isLastSeg}
              />
            );
          })
        ) : (
          <BeatIndicatorDot
            timed={isTimed(token)}
            status={token.status}
            isOpen={isTokenOpenBeat}
            isPhraseEnd={isPhraseEnd}
          />
        )}
      </span>
    </span>
  );
});

function BeatIndicatorDot(props: {
  timed: boolean;
  status: TimingStatus | undefined;
  isOpen: boolean;
  isPhraseEnd: boolean;
}) {
  const { timed, status, isOpen, isPhraseEnd } = props;
  if (isOpen) {
    return <span className="st-dot dot-live" title="正在演唱拍（按 J/K 连打下一拍，按 L 收尾）">●</span>;
  }
  if (status === 'invalid') {
    return <span className="st-dot dot-invalid" title="时间无效">!</span>;
  }
  if (status === 'estimated') {
    return (
      <span className="st-dot dot-estimated" title="估算／忘按 L 跨间奏自动兜底截断（可在波形微调）">
        ◌
      </span>
    );
  }
  if (isPhraseEnd) {
    if (timed && (status === 'edited' || status === 'confirmed')) {
      return <span className="st-dot dot-closed" title="已显式收尾">●</span>;
    }
    return <span className="st-dot dot-phrase-hint" title="句中停顿或行末提示：按 L 收尾">○</span>;
  }
  if (!timed) {
    return <span className="st-dot dot-untimed" title="待打拍">▸</span>;
  }
  return <span className="st-dot dot-timed" title="已打轴">•</span>;
}

/** 行内原地正文输入框（编辑模式双击行号 / Ctrl+Enter）。 */
function InlineLineEditor(props: {
  line: EditorLine;
  onCommit: (text: string) => Promise<void>;
  onCancel: () => void;
}) {
  const [text, setText] = useState(props.line.text);
  const [busy, setBusy] = useState(false);

  const commit = async () => {
    if (busy) return;
    setBusy(true);
    try {
      await props.onCommit(text);
    } finally {
      setBusy(false);
    }
  };

  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.nativeEvent.isComposing) return;
    if (e.key === 'Enter') {
      e.preventDefault();
      void commit();
    } else if (e.key === 'Escape') {
      e.preventDefault();
      props.onCancel();
    }
  };

  return (
    <div className="stage-inline-editor">
      <input
        type="text"
        className="sie-input"
        value={text}
        disabled={busy}
        autoFocus
        aria-label="修改本行歌词正文"
        onChange={(e) => setText(e.target.value)}
        onKeyDown={onKeyDown}
      />
      <Button variant="filled" size="sm" isDisabled={busy} onPress={() => void commit()}>
        保存
      </Button>
      <Button variant="text" size="sm" isDisabled={busy} onPress={props.onCancel}>
        取消
      </Button>
    </div>
  );
}

/** 画布底栏：左侧选中项精确时间微调与试听，右侧模式 M1 实时键位速查。 */
function StageFooterBar(props: {
  session: EditorSession;
  audio: AudioController | null;
  line: EditorLine | undefined;
  tokenId?: string | undefined;
  segmentId?: string | undefined;
  mode: Exclude<LyricsWorkMode, 'quickEdit'>;
}) {
  const { session, audio, line, mode } = props;
  const token = line && props.tokenId ? line.tokens.find((t) => t.id === props.tokenId) : undefined;
  const selectedSeg = line && token && props.segmentId ? selectedSegment(line, { lineId: line.id, tokenId: token.id, segmentId: props.segmentId }) : undefined;

  const setTimeRange = (target: LyricTimingTarget) => (startUs: number, endUs: number) =>
    session.command({ type: 'lyrics.setTimeRange', target, startUs: toMicros(startUs), endUs: toMicros(endUs) });

  const auditionUs = selectedSeg
    ? fromLyricTime(selectedSeg.segment.time).startUs
    : token?.startUs ?? line?.startUs;

  return (
    <footer className="stage-footer" role="group" aria-label="时间微调与键位速查">
      <div className="sf-timing">
        {!line ? (
          <span className="faint small">选择一行或词元后可在此精确微调时间。</span>
        ) : selectedSeg ? (
          <FooterSegmentTime session={session} audio={audio} line={line} ruby={selectedSeg.ruby} segment={selectedSeg.segment} />
        ) : token ? (
          <FooterTokenTime
            session={session}
            line={line}
            token={token}
            onApply={setTimeRange({ branchId: line.branchId, lineId: line.id, tokenId: token.id })}
          />
        ) : (
          <CompactTimeEditor
            key={line.id}
            title="本行"
            ariaPrefix="行"
            item={line}
            onApply={setTimeRange({ branchId: line.branchId, lineId: line.id })}
          />
        )}
        {audio && auditionUs !== undefined && (
          <IconButton
            icon="play_arrow"
            label="从此处试听"
            size="sm"
            onPress={() => {
              audio.seek(Math.max(0, auditionUs - 300_000));
              audio.play();
            }}
          />
        )}
      </div>

      <span className="spacer" />

      <div className="sf-hints" aria-label="快捷键提示">
        {mode === 'timing' ? (
          <>
            <span><kbd>Space</kbd> 播放/暂停</span>
            <span><kbd>J</kbd>/<kbd>K</kbd> 连打</span>
            <span><kbd>L</kbd> 收尾 ●</span>
            <span><kbd>⌫</kbd>/<kbd>G</kbd> 回退复听</span>
            <span><kbd>F2</kbd> 注音</span>
            <span><kbd>Q</kbd>/<kbd>W</kbd> 变速</span>
            <span><kbd>Z</kbd>/<kbd>X</kbd> 寻轨</span>
          </>
        ) : (
          <>
            <span><kbd>Space</kbd> 播放</span>
            <span><kbd>F2</kbd> 注音</span>
            <span><kbd>L</kbd> 停顿 ○</span>
            <span><kbd>↵</kbd> 断行</span>
            <span><kbd>⇧↵</kbd> 合行</span>
            <span><kbd>Alt</kbd>+<kbd>←→</kbd> 微调帧</span>
            <span>拖选/Shift多选合并</span>
          </>
        )}
      </div>
    </footer>
  );
}

function FooterTokenTime(props: {
  session: EditorSession;
  line: EditorLine;
  token: EditorToken;
  onApply: (startUs: number, endUs: number) => Promise<unknown>;
}) {
  const { session, line, token } = props;
  const target = { branchId: line.branchId, lineId: line.id, tokenId: token.id };
  const busy = session.state.pendingCommands > 0;
  const nudgeOff = !isTimed(token) || busy || !session.supportsCommand('lyrics.nudgeTokenTime');
  return (
    <>
      <CompactTimeEditor key={token.id} title="词元" name={token.text} ariaPrefix={`「${token.text}」`} item={token} onApply={props.onApply} />
      <IconButton icon="chevron_left" label="早一帧" size="sm" isDisabled={nudgeOff} onPress={() => void session.command({ type: 'lyrics.nudgeTokenTime', target, frameDelta: -1 })} />
      <IconButton icon="chevron_right" label="晚一帧" size="sm" isDisabled={nudgeOff} onPress={() => void session.command({ type: 'lyrics.nudgeTokenTime', target, frameDelta: 1 })} />
      <IconButton
        icon="delete"
        label="清除词元时间"
        size="sm"
        isDisabled={token.startUs === undefined || busy || !session.supportsCommand('lyrics.clearTokenTime')}
        onPress={() => void session.command({ type: 'lyrics.clearTokenTime', target })}
      />
    </>
  );
}

function FooterSegmentTime(props: {
  session: EditorSession;
  audio: AudioController | null;
  line: EditorLine;
  ruby: RubyAnnotation;
  segment: RubyTextSegment;
}) {
  const { session, audio, line, ruby, segment } = props;
  const target = segmentTarget(line, ruby, segment.id);
  const item = { ...fromLyricTime(segment.time), status: segment.timingStatus };
  const rate = session.frameRate;
  const busy = session.state.pendingCommands > 0;
  const available = session.supportsCommand('lyrics.setRubySegmentTime');
  const set = (range: TimeRange) => session.command({ type: 'lyrics.setRubySegmentTime', target, startUs: toMicros(range.startUs), endUs: toMicros(range.endUs) });
  const nudge = (frames: number) => (isTimed(item) ? nudgedRange(item, frames, rate) : undefined);

  return (
    <>
      <CompactTimeEditor key={segment.id} title="注音拍" name={segment.text} ariaPrefix={`注音「${segment.text}」`} item={item} onApply={(startUs, endUs) => set({ startUs, endUs })} />
      {[-1, 1].map((frames) => (
        <IconButton
          key={frames}
          icon={frames < 0 ? 'chevron_left' : 'chevron_right'}
          label={frames < 0 ? '早一帧' : '晚一帧'}
          size="sm"
          isDisabled={!nudge(frames) || busy || !available}
          onPress={() => nudge(frames) && void set(nudge(frames)!)}
        />
      ))}
      {audio && (
        <>
          <Button variant="ghost" size="sm" isDisabled={busy || !available} onPress={() => void set(stampedStart(item, audio.playheadUs(), rate))}>
            起=播放头
          </Button>
          <Button
            variant="ghost"
            size="sm"
            isDisabled={busy || !available || item.startUs === undefined}
            onPress={() => {
              const range = stampedEnd(item, audio.playheadUs(), rate);
              if (typeof range !== 'string') void set(range);
              else session.notify({ level: 'warning', message: `终点取播放头：${range}。` });
            }}
          >
            止=播放头
          </Button>
        </>
      )}
      <IconButton
        icon="delete"
        label="清除注音拍时间"
        size="sm"
        isDisabled={item.startUs === undefined || busy || !session.supportsCommand('lyrics.clearRubySegmentTime')}
        onPress={() => void session.command({ type: 'lyrics.clearRubySegmentTime', target })}
      />
    </>
  );
}

function CompactTimeEditor(props: {
  title: string;
  name?: string;
  ariaPrefix: string;
  item: { startUs?: number; endUs?: number; status?: TimingStatus };
  onApply: (startUs: number, endUs: number) => Promise<unknown>;
}) {
  const { item } = props;
  const [start, setStart] = useState(() => (item.startUs !== undefined ? formatTime(item.startUs) : ''));
  const [end, setEnd] = useState(() => (item.endUs !== undefined ? formatTime(item.endUs) : ''));
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    setStart(item.startUs !== undefined ? formatTime(item.startUs) : '');
    setEnd(item.endUs !== undefined ? formatTime(item.endUs) : '');
  }, [item.startUs, item.endUs]);

  const startUs = parseTime(start);
  const endUs = parseTime(end);
  const startError = start && startUs === null ? '起点格式：分:秒.毫秒' : undefined;
  const endError = end && endUs === null ? '终点格式：分:秒.毫秒' : startUs !== null && endUs !== null && endUs <= startUs ? '终点要晚于起点' : undefined;
  const unchanged = startUs === (item.startUs ?? null) && endUs === (item.endUs ?? null);
  const canApply = startUs !== null && endUs !== null && !startError && !endError && !unchanged && !busy;

  const apply = async () => {
    if (!canApply) return;
    setBusy(true);
    try {
      await props.onApply(startUs!, endUs!);
    } finally {
      setBusy(false);
    }
  };
  const reset = () => {
    setStart(item.startUs !== undefined ? formatTime(item.startUs) : '');
    setEnd(item.endUs !== undefined ? formatTime(item.endUs) : '');
  };

  return (
    <>
      <span className="sf-lbl">{props.title}</span>
      {props.name && (
        <b className="sf-name" lang="ja">
          {props.name}
        </b>
      )}
      {item.status && <StatusIcon status={item.status} />}
      <TextField label="起" ariaLabel={`${props.ariaPrefix}起点`} labelPlacement="none" size="sm" value={start} onChange={setStart} onEnter={() => void apply()} onEscape={reset} className="tb-time" />
      <span className="sf-sep">–</span>
      <TextField label="止" ariaLabel={`${props.ariaPrefix}终点`} labelPlacement="none" size="sm" value={end} onChange={setEnd} onEnter={() => void apply()} onEscape={reset} className="tb-time" />
      {(startError || endError) && (
        <span className="tb-err" role="alert">
          {startError ?? endError}
        </span>
      )}
      {!unchanged && (
        <Button variant="tonal" size="sm" isDisabled={!canApply} onPress={() => void apply()}>
          应用
        </Button>
      )}
    </>
  );
}
