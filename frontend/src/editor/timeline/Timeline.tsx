// 时间轴（W1 设计 §4.6）：Canvas 自绘标尺、波形、元素轨、行轨与播放头；歌词页另有注音轨、翻译行轨和主歌词参考轨
// （W6 设计 §2.6），编排页另有片段轨（W4 设计 §10.2）。拖动时只在本地显示，松手才提交一条命令；相邻元素（注音片段）共用的边界一起移动
// （一次批量命令、一次撤销）。

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { AudioController } from '../audio/audio-controller';
import type { LineTrack } from '../model/line-tracks';
import { isTimed, type EditorLine, type Selection } from '../model/lyrics';
import { hasRuby, rubyBandSelected, rubyBands, type RubyBand } from '../model/ruby-segments';
import { formatTime, frameDurationUs, snapToFrame, type FrameRate } from '../model/time';
import {
  clampStart,
  fitViewport,
  follow,
  linkedEdges,
  shrinkingFirst,
  tickStepUs,
  ticks,
  usAt,
  xOf,
  zoomAround,
  type Viewport,
} from '../model/timeline';
import { useObservable } from '../ui/hooks';

/** 轨上的一个色块。color 是颜色序号（片段按目标容器区分），循环使用几种颜色；dim 时画淡（停用的前奏）。 */
export interface TimelineBand {
  id: string;
  startUs: number;
  endUs: number;
  label: string;
  color: number;
  selected: boolean;
  dim?: boolean;
}

/** 行轨下面的一条色块轨（编排页的片段轨、前奏轨）；没有色块时写 empty。 */
export interface TimelineTrack {
  id: string;
  empty: string;
  bands: readonly TimelineBand[];
}

export interface TokenTimeEdit {
  lineId: string;
  tokenId: string;
  startUs: number;
  endUs: number;
}

export interface RubyTimeEdit {
  lineId: string;
  tokenId: string;
  rubyId: string;
  segmentId: string;
  startUs: number;
  endUs: number;
}

const RULER = 22;
const WAVE = 72;
const RUBY = 22;
const TOKENS = 34;
const LINES = 22;
const CLIPS = 22;

/**
 * 各轨的顶边与画布高度。注音轨不显示时 ruby 与 tokens 相同。lineTracks 是第一条翻译行轨（参考轨）的顶边，各高 LINES；
 * clips 是第一条色块轨的顶边，各高 CLIPS；都依次往下排。
 */
interface Lanes {
  ruler: number;
  wave: number;
  ruby: number;
  tokens: number;
  lines: number;
  lineTracks: number;
  clips: number;
  height: number;
}

function lanesOf(ruby: boolean, lineTracks: number, clipTracks: number): Lanes {
  const tokens = RULER + WAVE + (ruby ? RUBY : 0);
  const lines = tokens + TOKENS;
  const clips = lines + LINES + lineTracks * LINES;
  return { ruler: 0, wave: RULER, ruby: RULER + WAVE, tokens, lines, lineTracks: lines + LINES, clips, height: clips + clipTracks * CLIPS };
}

/** 默认视口宽度：约 12 秒，适合逐字打轴。 */
const DEFAULT_SPAN_US = 12_000_000;

type EdgeTarget = { edge: 'start' | 'end'; startUs: number; endUs: number } & (
  | { kind: 'token'; lineId: string; tokenId: string }
  | { kind: 'line'; lineId: string }
  | { kind: 'ruby'; lineId: string; tokenId: string; rubyId: string; segmentId: string }
  | { kind: 'trackLine'; branchId: string; lineId: string }
);

interface Drag {
  pointerId: number;
  /** box：只读时在行轨空白处拖出时间范围，框选行。 */
  mode: 'edges' | 'scrub' | 'box';
  targets: EdgeTarget[];
  /** 拖动中的边界时间。 */
  us: number;
  originUs: number;
  moved: boolean;
  /** 框选时按着 Ctrl／⌘／Shift：加到已选的行里。 */
  additive: boolean;
}

interface Palette {
  surface: string;
  outline: string;
  text: string;
  muted: string;
  wave: string;
  token: string;
  tokenEstimated: string;
  tokenSelected: string;
  line: string;
  lineSelected: string;
  playhead: string;
  grid: string;
  loop: string;
  bands: string[];
}

function readPalette(): Palette {
  const style = getComputedStyle(document.documentElement);
  const color = (name: string, alpha = 1) => `rgba(${style.getPropertyValue(`--mdui-color-${name}`).trim() || '128, 128, 128'}, ${alpha})`;
  return {
    surface: color('surface-container-lowest'),
    outline: color('outline-variant'),
    text: color('on-surface'),
    muted: color('on-surface-variant'),
    wave: color('primary', 0.55),
    token: color('secondary-container'),
    tokenEstimated: color('tertiary-container'),
    tokenSelected: color('primary'),
    line: color('surface-container-highest'),
    lineSelected: color('primary-container'),
    playhead: color('error'),
    grid: color('outline-variant', 0.5),
    loop: color('primary', 0.08),
    bands: [color('primary-container'), color('tertiary-container'), color('secondary-container'), color('error-container')],
  };
}

export function Timeline(props: {
  audio: AudioController;
  lines: readonly EditorLine[];
  selection?: Selection | undefined;
  frameRate?: FrameRate | undefined;
  /** additive：按着 Ctrl／⌘／Shift 点选，用于外观页的多选。 */
  onSelect: (selection: Selection, options: { additive: boolean }) => void;
  /** 不提供这两个回调时时间轴只读：边界不能拖，点选只改变选择（外观页）。 */
  onCommitTokens?: (edits: TokenTimeEdit[]) => void;
  onCommitLine?: (lineId: string, startUs: number, endUs: number) => void;
  /** 歌词页：当前来源有注音时在元素轨上方显示注音轨；提供 onCommitRuby 时片段边界可拖。 */
  showRuby?: boolean;
  onCommitRuby?: (edits: RubyTimeEdit[]) => void;
  /** 歌词页：行轨下面的翻译行轨或主歌词参考轨。独立计时的翻译行可拖边界，双击切到那个来源并选中这一行。 */
  lineTracks?: readonly LineTrack[] | undefined;
  onCommitTrackLine?: (branchId: string, lineId: string, startUs: number, endUs: number) => void;
  onOpenTrackLine?: (branchId: string, lineId: string) => void;
  /** 当前来源跟随主歌词时，行轨里已关联的行画在主歌词行的位置（虚线框，不能拖）。 */
  followSpans?: ReadonlyMap<string, { startUs: number; endUs: number }> | undefined;
  /** 多选时高亮的行和文本单元 ID（外观页）；提供时代替 selection 决定高亮。 */
  marked?: ReadonlySet<string> | undefined;
  /** 只读时在行轨空白处拖动框选：与时间范围相交的行（外观页）。 */
  onSelectLines?: (lineIds: string[], options: { additive: boolean }) => void;
  /** 提供时在行轨下面依次加几条色块轨（编排页的片段轨、前奏轨）。 */
  tracks?: readonly TimelineTrack[] | undefined;
  onSelectBand?: (trackId: string, bandId: string) => void;
  /** 工具栏的操作说明（滚轮、缩放的说明之前那部分）。 */
  help?: string;
}) {
  const editable = !!props.onCommitTokens && !!props.onCommitLine;
  const rubyShown = !!props.showRuby && hasRuby(props.lines);
  const lanes = lanesOf(rubyShown, props.lineTracks?.length ?? 0, props.tracks?.length ?? 0);
  const height = lanes.height;
  const lanesRef = useRef(lanes);
  lanesRef.current = lanes;
  const heightRef = useRef(height);
  heightRef.current = height;
  const bands = useMemo(() => (rubyShown ? rubyBands(props.lines) : []), [rubyShown, props.lines]);
  const rubyRef = useRef<readonly RubyBand[]>(bands);
  rubyRef.current = bands;
  const resize = useRef<() => void>(() => undefined);
  const audioState = useObservable(props.audio.state$);
  const container = useRef<HTMLDivElement>(null);
  const canvas = useRef<HTMLCanvasElement>(null);
  const viewport = useRef<Viewport>({ startUs: 0, usPerPx: DEFAULT_SPAN_US / 1000, widthPx: 1000 });
  const drag = useRef<Drag | null>(null);
  const dirty = useRef(true);
  const palette = useRef<Palette | null>(null);
  const [snap, setSnap] = useState(true);
  const [, setZoomLabel] = useState(0);

  // 最新的 props 给绘制循环和指针处理用，避免每次渲染重建监听。
  const latest = useRef(props);
  latest.current = props;
  const snapRef = useRef(snap);
  snapRef.current = snap;
  const durationUs = audioState.durationUs;
  const durationRef = useRef(durationUs);
  durationRef.current = durationUs;

  const invalidate = useCallback(() => {
    dirty.current = true;
  }, []);

  useEffect(invalidate, [props.lines, props.selection, props.marked, props.tracks, props.lineTracks, props.followSpans, audioState.peaks, audioState.loop, invalidate]);
  // 后端波形的块陆续到达：到一块重画一次。
  useEffect(() => audioState.peaks?.subscribe(invalidate), [audioState.peaks, invalidate]);

  // 尺寸：宽度随容器，按 devicePixelRatio 放大画布。
  useEffect(() => {
    const element = container.current!;
    resize.current = () => {
      const c = canvas.current;
      if (!c) return; // 卸载后可能还有排队的回调
      const width = Math.max(100, element.clientWidth);
      const ratio = window.devicePixelRatio || 1;
      c.width = Math.round(width * ratio);
      c.height = Math.round(heightRef.current * ratio);
      c.style.width = `${width}px`;
      c.style.height = `${heightRef.current}px`;
      viewport.current = { ...viewport.current, widthPx: width };
      dirty.current = true;
    };
    const observer = new ResizeObserver(() => resize.current());
    observer.observe(element);
    return () => observer.disconnect();
  }, []);
  // 注音轨、翻译行轨、片段轨出现或消失时画布高度跟着变。
  useEffect(() => resize.current(), [height]);

  // 明暗主题或配色变化时重读颜色。
  useEffect(() => {
    palette.current = readPalette();
    const media = window.matchMedia('(prefers-color-scheme: dark)');
    const onChange = () => {
      palette.current = readPalette();
      dirty.current = true;
    };
    media.addEventListener('change', onChange);
    return () => media.removeEventListener('change', onChange);
  }, []);

  // 选中元素变化（键盘、打点后前进）且不在播放时，把它滚进视口。
  useEffect(() => {
    if (props.audio.playing || !props.selection) return;
    const { lineId, tokenId, segmentId } = props.selection;
    const line = props.lines.find((l) => l.id === lineId);
    const segment = segmentId ? rubyRef.current.find((b) => b.id === segmentId) : undefined;
    const item = segment ?? (tokenId ? line?.tokens.find((t) => t.id === tokenId) : (props.followSpans?.get(lineId) ?? line));
    if (item?.startUs !== undefined) {
      const next = follow(viewport.current, item.startUs, 0.2);
      if (next !== viewport.current) viewport.current = { ...next, startUs: Math.max(0, next.startUs) };
      dirty.current = true;
    }
  }, [props.selection, props.lines, props.followSpans, props.audio]);

  // 绘制循环：播放、拖动或数据变化时重画。
  useEffect(() => {
    let frame = 0;
    const loop = () => {
      frame = requestAnimationFrame(loop);
      const playing = props.audio.playing;
      if (playing && !drag.current) {
        const followed = follow(viewport.current, props.audio.playheadUs(), 0.15);
        if (followed !== viewport.current) viewport.current = { ...followed, startUs: Math.max(0, followed.startUs) };
      }
      if (!dirty.current && !playing && !drag.current) return;
      dirty.current = false;
      draw();
    };
    frame = requestAnimationFrame(loop);
    return () => cancelAnimationFrame(frame);
    // draw 只读 ref，循环只需建立一次。
  }, []);

  function previewTimes(): Map<string, { startUs: number; endUs: number }> {
    const result = new Map<string, { startUs: number; endUs: number }>();
    const d = drag.current;
    if (!d || d.mode !== 'edges' || !d.moved) return result;
    for (const t of d.targets) {
      const key = t.kind === 'token' ? t.tokenId : t.kind === 'ruby' ? `ruby:${t.segmentId}` : t.kind === 'trackLine' ? `track:${t.branchId}:${t.lineId}` : `line:${t.lineId}`;
      result.set(key, t.edge === 'start' ? { startUs: d.us, endUs: t.endUs } : { startUs: t.startUs, endUs: d.us });
    }
    return result;
  }

  function draw(): void {
    const c = canvas.current;
    const colors = palette.current;
    if (!c || !colors) return;
    const ctx = c.getContext('2d')!;
    const ratio = c.width / viewport.current.widthPx;
    ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
    const vp = viewport.current;
    const width = vp.widthPx;
    const { lines, selection, frameRate, marked, tracks, lineTracks, followSpans } = latest.current;
    const LANE = lanesRef.current;
    const isSelected = (id: string, fallback: boolean) => (marked ? marked.has(id) : fallback);
    const audio = props.audio.state;
    const total = heightRef.current;
    ctx.clearRect(0, 0, width, total);
    ctx.fillStyle = colors.surface;
    ctx.fillRect(0, 0, width, total);

    // 循环区间
    if (audio.loop) {
      ctx.fillStyle = colors.loop;
      ctx.fillRect(xOf(vp, audio.loop.startUs), 0, xOf(vp, audio.loop.endUs) - xOf(vp, audio.loop.startUs), total);
    }

    // 帧网格（足够放大时）
    if (frameRate && frameDurationUs(frameRate) / vp.usPerPx >= 8) {
      ctx.strokeStyle = colors.grid;
      ctx.lineWidth = 1;
      const step = frameDurationUs(frameRate);
      ctx.beginPath();
      for (let t = Math.ceil(vp.startUs / step) * step; xOf(vp, t) <= width; t += step) {
        const x = Math.round(xOf(vp, t)) + 0.5;
        ctx.moveTo(x, LANE.wave);
        ctx.lineTo(x, LANE.lines);
      }
      ctx.stroke();
    }

    // 标尺
    const step = tickStepUs(vp.usPerPx);
    ctx.fillStyle = colors.muted;
    ctx.strokeStyle = colors.outline;
    ctx.font = '11px system-ui, sans-serif';
    ctx.textBaseline = 'middle';
    ctx.beginPath();
    for (const t of ticks(vp, step)) {
      const x = Math.round(xOf(vp, t)) + 0.5;
      ctx.moveTo(x, RULER - 6);
      ctx.lineTo(x, RULER);
      if (t >= 0) ctx.fillText(formatTime(t, { millis: step < 1_000_000 }), x + 3, RULER / 2 - 1);
    }
    ctx.moveTo(0, RULER + 0.5);
    ctx.lineTo(width, RULER + 0.5);
    ctx.stroke();

    // 波形
    if (audio.peaks) {
      const peaks = audio.peaks.columns(vp.startUs, vp.usPerPx, Math.ceil(width));
      if ('loadedChunks' in audio.peaks) c.dataset.waveformChunks = String(audio.peaks.loadedChunks);
      const mid = LANE.wave + WAVE / 2;
      const half = WAVE / 2 - 4;
      ctx.fillStyle = colors.wave;
      for (let x = 0; x < peaks.min.length; x++) {
        const lo = peaks.min[x]!;
        const hi = peaks.max[x]!;
        if (Number.isNaN(lo)) continue;
        const top = mid - Math.min(1, hi) * half;
        const bottom = mid - Math.max(-1, lo) * half;
        ctx.fillRect(x, top, 1, Math.max(1, bottom - top));
      }
    } else {
      ctx.fillStyle = colors.muted;
      const pending = audio.peaksFallback === undefined ? '正在读取波形……' : '正在解码波形……';
      ctx.fillText(audio.peaksError ?? (audio.status === 'error' ? (audio.error ?? '音频不可用') : pending), 8, LANE.wave + WAVE / 2);
    }

    const overrides = previewTimes();
    const visible = (start: number, end: number) => xOf(vp, end) >= 0 && xOf(vp, start) <= width;

    // 行轨：跟随主歌词的行画在主歌词行的位置（虚线框）
    for (const line of lines) {
      const selected = isSelected(line.id, selection?.lineId === line.id);
      const span = followSpans?.get(line.id);
      if (span) {
        if (visible(span.startUs, span.endUs)) dashedBand(ctx, colors, vp, span, LANE.lines, line.text, selected);
        continue;
      }
      const time = overrides.get(`line:${line.id}`) ?? (isTimed(line) ? { startUs: line.startUs, endUs: line.endUs } : undefined);
      if (!time || !visible(time.startUs, time.endUs)) continue;
      const x0 = xOf(vp, time.startUs);
      const x1 = xOf(vp, time.endUs);
      ctx.fillStyle = selected ? colors.lineSelected : colors.line;
      ctx.fillRect(x0, LANE.lines + 3, Math.max(1, x1 - x0), LINES - 6);
      label(ctx, line.text, x0, x1, LANE.lines + LINES / 2, colors.text);
    }

    // 翻译行轨、主歌词参考轨：左端小字写来源名
    (lineTracks ?? []).forEach((track, i) => {
      const top = LANE.lineTracks + i * LINES;
      separator(ctx, colors, top, width);
      const name = track.lines.length > 0 ? track.name : `${track.name} · ${track.empty}`;
      // 色块的文字从来源名右边开始，不被来源名挡住。
      const nameRight = trackNameRight(ctx, name);
      for (const item of track.lines) {
        const time = overrides.get(`track:${track.branchId}:${item.lineId}`) ?? item;
        if (!visible(time.startUs, time.endUs)) continue;
        if (item.follow) {
          dashedBand(ctx, colors, vp, time, top, item.text, false, nameRight);
          continue;
        }
        const x0 = xOf(vp, time.startUs);
        const x1 = xOf(vp, time.endUs);
        ctx.fillStyle = colors.line;
        ctx.fillRect(x0, top + 3, Math.max(1, x1 - x0), LINES - 6);
        label(ctx, item.text, Math.max(x0, nameRight), x1, top + LINES / 2, colors.text);
      }
      trackName(ctx, colors, name, top + LINES / 2);
    });

    // 元素轨
    for (const line of lines) {
      for (const token of line.tokens) {
        const time = overrides.get(token.id) ?? (isTimed(token) ? { startUs: token.startUs, endUs: token.endUs } : undefined);
        if (!time || !visible(time.startUs, time.endUs)) continue;
        const x0 = xOf(vp, time.startUs);
        const x1 = xOf(vp, time.endUs);
        const selected = isSelected(token.id, selection?.tokenId === token.id);
        ctx.fillStyle = selected ? colors.tokenSelected : token.status === 'estimated' || token.status === 'imported' ? colors.tokenEstimated : colors.token;
        ctx.fillRect(x0 + 0.5, LANE.tokens + 3, Math.max(1, x1 - x0 - 1), TOKENS - 6);
        label(ctx, token.text, x0, x1, LANE.tokens + TOKENS / 2, selected ? colors.surface : colors.text);
      }
    }

    // 注音轨：有时间的片段画色块；整组没有时间时在文本单元的位置画虚线框，表示跟随文本单元
    for (const band of rubyRef.current) {
      const time = overrides.get(`ruby:${band.id}`) ?? band;
      if (!visible(time.startUs, time.endUs)) continue;
      const x0 = xOf(vp, time.startUs);
      const x1 = xOf(vp, time.endUs);
      const selected = rubyBandSelected(band, selection);
      if (band.follow) {
        dashedBand(ctx, colors, vp, time, LANE.ruby, band.text, selected);
        continue;
      }
      ctx.fillStyle = selected ? colors.tokenSelected : band.status === 'estimated' || band.status === 'imported' ? colors.tokenEstimated : colors.token;
      ctx.fillRect(x0 + 0.5, LANE.ruby + 3, Math.max(1, x1 - x0 - 1), RUBY - 6);
      label(ctx, band.text, x0, x1, LANE.ruby + RUBY / 2, selected ? colors.surface : colors.text);
    }

    // 色块轨（片段轨、前奏轨）
    (tracks ?? []).forEach((track, i) => {
      const top = LANE.clips + i * CLIPS;
      separator(ctx, colors, top, width);
      if (track.bands.length === 0) {
        ctx.fillStyle = colors.muted;
        ctx.font = '11px system-ui, sans-serif';
        ctx.fillText(track.empty, 8, top + CLIPS / 2);
      }
      for (const band of track.bands) {
        if (!visible(band.startUs, band.endUs)) continue;
        const x0 = xOf(vp, band.startUs);
        const x1 = xOf(vp, band.endUs);
        ctx.globalAlpha = band.dim ? 0.4 : 1;
        ctx.fillStyle = colors.bands[band.color % colors.bands.length]!;
        ctx.fillRect(x0 + 0.5, top + 3, Math.max(1, x1 - x0 - 1), CLIPS - 6);
        label(ctx, band.label, x0, x1, top + CLIPS / 2, colors.text);
        ctx.globalAlpha = 1;
        if (band.selected) {
          ctx.strokeStyle = colors.tokenSelected;
          ctx.lineWidth = 2;
          ctx.strokeRect(x0 + 1.5, top + 3, Math.max(1, x1 - x0 - 3), CLIPS - 6);
        }
      }
    });

    // 播放头
    const px = Math.round(xOf(vp, props.audio.playheadUs())) + 0.5;
    if (px >= 0 && px <= width) {
      ctx.strokeStyle = colors.playhead;
      ctx.lineWidth = 1.5;
      ctx.beginPath();
      ctx.moveTo(px, 0);
      ctx.lineTo(px, total);
      ctx.stroke();
    }

    // 框选范围
    const d = drag.current;
    if (d && d.mode === 'box' && d.moved) {
      const x0 = xOf(vp, Math.min(d.originUs, d.us));
      const x1 = xOf(vp, Math.max(d.originUs, d.us));
      ctx.fillStyle = colors.loop;
      ctx.fillRect(x0, LANE.lines, x1 - x0, LINES);
      ctx.strokeStyle = colors.tokenSelected;
      ctx.lineWidth = 1;
      ctx.strokeRect(x0 + 0.5, LANE.lines + 0.5, Math.max(1, x1 - x0 - 1), LINES - 1);
    }

    // 拖动中的边界时间
    if (d && d.mode === 'edges' && d.moved) {
      ctx.fillStyle = colors.text;
      ctx.fillText(formatTime(d.us), Math.min(width - 70, xOf(vp, d.us) + 4), LANE.wave + 10);
    }
  }

  // ── 指针 ──

  const pointToUs = (event: { clientX: number; altKey: boolean }): number => {
    const rect = canvas.current!.getBoundingClientRect();
    const us = usAt(viewport.current, event.clientX - rect.left);
    const rate = latest.current.frameRate;
    return rate && snapRef.current && !event.altKey ? snapToFrame(us, rate) : us;
  };

  /** y 坐标所在轨道上，x 附近可拖动的边界；相邻元素或注音片段共用同一边界时一起移动。 */
  const edgesAt = (x: number, y: number): EdgeTarget[] => {
    const LANE = lanesRef.current;
    const vp = viewport.current;
    const lines = latest.current.lines;
    if (y >= LANE.ruby && y < LANE.tokens) {
      if (!latest.current.onCommitRuby) return [];
      return linkedEdges(
        vp,
        rubyRef.current.filter((b) => !b.follow),
        x,
      ).map(({ span: b, edge }) => ({ kind: 'ruby', lineId: b.lineId, tokenId: b.tokenId, rubyId: b.rubyId, segmentId: b.segmentId, edge, startUs: b.startUs, endUs: b.endUs }));
    }
    if (!latest.current.onCommitTokens || !latest.current.onCommitLine) return [];
    if (y >= LANE.tokens && y < LANE.lines) {
      const spans: { id: string; lineId: string; startUs: number; endUs: number }[] = [];
      for (const line of lines) for (const t of line.tokens) if (isTimed(t)) spans.push({ id: t.id, lineId: line.id, startUs: t.startUs, endUs: t.endUs });
      return linkedEdges(vp, spans, x).map(({ span: t, edge }) => ({ kind: 'token', lineId: t.lineId, tokenId: t.id, edge, startUs: t.startUs, endUs: t.endUs }));
    }
    if (y >= LANE.lines && y < LANE.lineTracks) {
      const follows = latest.current.followSpans;
      const spans = lines.filter((l) => isTimed(l) && !follows?.has(l.id)).map((l) => ({ id: l.id, startUs: l.startUs!, endUs: l.endUs! }));
      const first = linkedEdges(vp, spans, x)[0];
      return first ? [{ kind: 'line', lineId: first.span.id, edge: first.edge, startUs: first.span.startUs, endUs: first.span.endUs }] : [];
    }
    const track = lineTrackAt(y);
    if (track?.editable && latest.current.onCommitTrackLine) {
      const spans = track.lines.filter((l) => !l.follow).map((l) => ({ ...l, id: l.lineId }));
      const first = linkedEdges(vp, spans, x)[0];
      return first ? [{ kind: 'trackLine', branchId: track.branchId, lineId: first.span.lineId, edge: first.edge, startUs: first.span.startUs, endUs: first.span.endUs }] : [];
    }
    return [];
  };

  /** y 所在的翻译行轨（参考轨）。 */
  const lineTrackAt = (y: number): LineTrack | undefined => {
    const LANE = lanesRef.current;
    return y >= LANE.lineTracks && y < LANE.clips ? latest.current.lineTracks?.[Math.floor((y - LANE.lineTracks) / LINES)] : undefined;
  };

  /** x 处翻译行轨（参考轨）上的行。 */
  const trackLineAt = (x: number, y: number): { branchId: string; lineId: string } | undefined => {
    const track = lineTrackAt(y);
    const us = usAt(viewport.current, x);
    const item = track?.lines.find((l) => us >= l.startUs && us < l.endUs);
    return track && item ? { branchId: track.branchId, lineId: item.lineId } : undefined;
  };

  const itemAt = (x: number, y: number): Selection | undefined => {
    const LANE = lanesRef.current;
    const us = usAt(viewport.current, x);
    if (y >= LANE.ruby && y < LANE.tokens) {
      const band = rubyRef.current.find((b) => us >= b.startUs && us < b.endUs);
      return band ? { lineId: band.lineId, tokenId: band.tokenId, segmentId: band.segmentId } : undefined;
    }
    for (const line of latest.current.lines) {
      if (y >= LANE.tokens && y < LANE.lines) {
        const token = line.tokens.find((t) => isTimed(t) && us >= t.startUs && us < t.endUs);
        if (token) return { lineId: line.id, tokenId: token.id };
      } else if (y >= LANE.lines && y < LANE.lineTracks) {
        const time = latest.current.followSpans?.get(line.id) ?? (isTimed(line) ? line : undefined);
        if (time && us >= time.startUs && us < time.endUs) return { lineId: line.id };
      }
    }
    return undefined;
  };

  const onPointerDown = (event: React.PointerEvent<HTMLCanvasElement>) => {
    if (event.button !== 0) return;
    const rect = canvas.current!.getBoundingClientRect();
    const x = event.clientX - rect.left;
    const y = event.clientY - rect.top;
    const edges = edgesAt(x, y);
    const us = pointToUs(event);
    const additive = event.ctrlKey || event.metaKey || event.shiftKey;
    if (edges.length > 0) {
      const first = edges[0]!;
      const originUs = first.edge === 'start' ? first.startUs : first.endUs;
      drag.current = { pointerId: event.pointerId, mode: 'edges', targets: edges, us: originUs, originUs, moved: false, additive: false };
      // 翻译行轨上的行属于别的来源：拖动不改变选择。
      if (first.kind !== 'trackLine') {
        const selection: Selection =
          first.kind === 'ruby' ? { lineId: first.lineId, tokenId: first.tokenId, segmentId: first.segmentId } : first.kind === 'token' ? { lineId: first.lineId, tokenId: first.tokenId } : { lineId: first.lineId };
        latest.current.onSelect(selection, { additive: false });
      }
    } else {
      const item = itemAt(x, y);
      if (item) {
        latest.current.onSelect(item, { additive });
        return;
      }
      // 翻译行轨上的行：单击没有动作，双击才切换来源（onDoubleClick）。
      if (trackLineAt(x, y)) return;
      const LANE = lanesRef.current;
      const track = y >= LANE.clips ? latest.current.tracks?.[Math.floor((y - LANE.clips) / CLIPS)] : undefined;
      if (track) {
        const raw = usAt(viewport.current, x);
        const band = track.bands.find((b) => raw >= b.startUs && raw < b.endUs);
        if (band) {
          latest.current.onSelectBand?.(track.id, band.id);
          return;
        }
      }
      if (y >= LANE.lines && y < LANE.lineTracks && !editable && latest.current.onSelectLines) {
        // 框选：松手时没有拖动就当作点击定位。
        const raw = usAt(viewport.current, x);
        drag.current = { pointerId: event.pointerId, mode: 'box', targets: [], us: raw, originUs: raw, moved: false, additive };
      } else {
        drag.current = { pointerId: event.pointerId, mode: 'scrub', targets: [], us, originUs: us, moved: false, additive: false };
        props.audio.seek(Math.max(0, us));
      }
    }
    canvas.current!.setPointerCapture(event.pointerId);
    dirty.current = true;
  };

  const onPointerMove = (event: React.PointerEvent<HTMLCanvasElement>) => {
    const d = drag.current;
    const rect = canvas.current!.getBoundingClientRect();
    if (!d) {
      const edges = edgesAt(event.clientX - rect.left, event.clientY - rect.top);
      canvas.current!.style.cursor = edges.length ? 'ew-resize' : 'default';
      return;
    }
    if (event.pointerId !== d.pointerId) return;
    const us = d.mode === 'box' ? usAt(viewport.current, event.clientX - rect.left) : pointToUs(event);
    if (us !== d.us) {
      d.us = us;
      // 框选至少拖过 3 像素才算，手抖不算框选。
      d.moved = d.moved || (d.mode === 'box' ? Math.abs(us - d.originUs) / viewport.current.usPerPx >= 3 : us !== d.originUs);
      if (d.mode === 'scrub') props.audio.seek(Math.max(0, us));
      dirty.current = true;
    }
  };

  const finishDrag = (commit: boolean) => {
    const d = drag.current;
    drag.current = null;
    dirty.current = true;
    if (d && commit && d.mode === 'box') {
      if (!d.moved) {
        props.audio.seek(Math.max(0, d.originUs));
        return;
      }
      const from = Math.min(d.originUs, d.us);
      const to = Math.max(d.originUs, d.us);
      const ids = latest.current.lines.filter((l) => isTimed(l) && l.startUs < to && l.endUs > from).map((l) => l.id);
      if (ids.length > 0) latest.current.onSelectLines?.(ids, { additive: d.additive });
      return;
    }
    if (!d || !commit || d.mode !== 'edges' || !d.moved) return;
    const tokenEdits: TokenTimeEdit[] = [];
    const rubyEdits: RubyTimeEdit[] = [];
    for (const t of shrinkingFirst(d.targets, d.originUs, d.us)) {
      const startUs = t.edge === 'start' ? d.us : t.startUs;
      const endUs = t.edge === 'end' ? d.us : t.endUs;
      if (endUs <= startUs) return; // 拖过了另一端：放弃，不提交
      if (t.kind === 'token') tokenEdits.push({ lineId: t.lineId, tokenId: t.tokenId, startUs, endUs });
      else if (t.kind === 'ruby') rubyEdits.push({ lineId: t.lineId, tokenId: t.tokenId, rubyId: t.rubyId, segmentId: t.segmentId, startUs, endUs });
      else if (t.kind === 'trackLine') latest.current.onCommitTrackLine?.(t.branchId, t.lineId, startUs, endUs);
      else latest.current.onCommitLine?.(t.lineId, startUs, endUs);
    }
    if (tokenEdits.length > 0) latest.current.onCommitTokens?.(tokenEdits);
    if (rubyEdits.length > 0) latest.current.onCommitRuby?.(rubyEdits);
  };

  const onPointerUp = (event: React.PointerEvent<HTMLCanvasElement>) => {
    if (drag.current?.pointerId !== event.pointerId) return;
    finishDrag(true);
  };

  const onDoubleClick = (event: React.MouseEvent<HTMLCanvasElement>) => {
    const rect = canvas.current!.getBoundingClientRect();
    const hit = trackLineAt(event.clientX - rect.left, event.clientY - rect.top);
    if (hit) latest.current.onOpenTrackLine?.(hit.branchId, hit.lineId);
  };

  // Esc 取消拖动；滚轮：Ctrl 缩放，否则横向滚动。
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && drag.current) {
        event.preventDefault();
        finishDrag(false);
      }
    };
    const c = canvas.current!;
    const onWheel = (event: WheelEvent) => {
      event.preventDefault();
      const rect = c.getBoundingClientRect();
      if (event.ctrlKey || event.metaKey) {
        viewport.current = zoomAround(viewport.current, Math.exp(event.deltaY * 0.002), event.clientX - rect.left);
        setZoomLabel((n) => n + 1);
      } else {
        const delta = Math.abs(event.deltaX) > Math.abs(event.deltaY) ? event.deltaX : event.deltaY;
        viewport.current = { ...viewport.current, startUs: viewport.current.startUs + delta * viewport.current.usPerPx };
      }
      viewport.current = clampStart(viewport.current, durationRef.current);
      dirty.current = true;
    };
    window.addEventListener('keydown', onKey);
    c.addEventListener('wheel', onWheel, { passive: false });
    return () => {
      window.removeEventListener('keydown', onKey);
      c.removeEventListener('wheel', onWheel);
    };
  }, []);

  const zoom = (factor: number) => {
    const vp = viewport.current;
    const anchor = Math.min(vp.widthPx, Math.max(0, xOf(vp, props.audio.playheadUs())));
    viewport.current = clampStart(zoomAround(vp, factor, anchor), durationUs);
    dirty.current = true;
    setZoomLabel((n) => n + 1);
  };

  const fitAll = () => {
    if (durationUs <= 0) return;
    viewport.current = fitViewport(durationUs, viewport.current.widthPx);
    dirty.current = true;
    setZoomLabel((n) => n + 1);
  };

  const visibleSeconds = (viewport.current.widthPx * viewport.current.usPerPx) / 1e6;

  return (
    <div className="timeline">
      <div className="timeline-toolbar">
        <mdui-button-icon onClick={() => zoom(1 / 1.5)} aria-label="放大">
          <mdui-icon-zoom-in />
        </mdui-button-icon>
        <mdui-button-icon onClick={() => zoom(1.5)} aria-label="缩小">
          <mdui-icon-zoom-out />
        </mdui-button-icon>
        <mdui-button variant="text" onClick={fitAll}>
          全曲
        </mdui-button>
        <span className="muted">可见 {visibleSeconds < 10 ? visibleSeconds.toFixed(1) : Math.round(visibleSeconds)} 秒</span>
        <span style={{ flex: 1 }} />
        {editable && (
          <label className="timeline-snap">
            <input type="checkbox" checked={snap} disabled={!props.frameRate} onChange={(e) => setSnap(e.currentTarget.checked)} />
            吸附到帧{props.frameRate ? `（${(props.frameRate.numerator / props.frameRate.denominator).toFixed(2).replace(/\.00$/, '')} fps）` : '（帧率未知）'}
          </label>
        )}
        <span className="muted">
          {props.help ?? (editable ? '拖边界改时间（Alt 不吸附，Esc 取消）' : props.onSelectLines ? '点选行或文本单元（Ctrl／Shift 加选）· 行轨空白处拖动框选' : '点选行或文本单元')} · 滚轮平移 · Ctrl＋滚轮缩放
        </span>
      </div>
      <div className="timeline-canvas" ref={container}>
        <canvas
          ref={canvas}
          aria-label="时间轴"
          data-waveform={audioState.peaks?.kind ?? 'none'}
          onPointerDown={onPointerDown}
          onPointerMove={onPointerMove}
          onPointerUp={onPointerUp}
          onPointerCancel={() => finishDrag(false)}
          onDoubleClick={onDoubleClick}
        />
      </div>
    </div>
  );
}

/** 轨道顶边的分隔线。 */
function separator(ctx: CanvasRenderingContext2D, colors: Palette, top: number, width: number): void {
  ctx.strokeStyle = colors.outline;
  ctx.lineWidth = 1;
  ctx.beginPath();
  ctx.moveTo(0, top + 0.5);
  ctx.lineTo(width, top + 0.5);
  ctx.stroke();
}

/** 虚线框（高 22 的轨）：跟随别处时间的注音、翻译行，只显示。文字从 textFrom 之后开始。 */
function dashedBand(
  ctx: CanvasRenderingContext2D,
  colors: Palette,
  vp: Viewport,
  time: { startUs: number; endUs: number },
  top: number,
  text: string,
  selected: boolean,
  textFrom = -Infinity,
): void {
  const x0 = xOf(vp, time.startUs);
  const x1 = xOf(vp, time.endUs);
  ctx.save();
  ctx.setLineDash([4, 3]);
  ctx.strokeStyle = selected ? colors.tokenSelected : colors.muted;
  ctx.lineWidth = selected ? 2 : 1;
  ctx.strokeRect(x0 + 1.5, top + 3.5, Math.max(1, x1 - x0 - 3), RUBY - 7);
  ctx.restore();
  label(ctx, text, Math.max(x0, textFrom), x1, top + RUBY / 2, colors.muted);
}

const TRACK_NAME_FONT = '11px system-ui, sans-serif';

/** 来源名小字（连底色）的右边缘。 */
function trackNameRight(ctx: CanvasRenderingContext2D, text: string): number {
  ctx.save();
  ctx.font = TRACK_NAME_FONT;
  const width = ctx.measureText(text).width;
  ctx.restore();
  return width + 10;
}

/** 轨道左端的小字（来源名），垫一层底色，免得和色块叠在一起。 */
function trackName(ctx: CanvasRenderingContext2D, colors: Palette, text: string, y: number): void {
  ctx.save();
  ctx.font = TRACK_NAME_FONT;
  ctx.textBaseline = 'middle';
  const width = ctx.measureText(text).width;
  ctx.fillStyle = colors.surface;
  ctx.globalAlpha = 0.85;
  ctx.fillRect(2, y - 7, width + 8, 14);
  ctx.globalAlpha = 1;
  ctx.fillStyle = colors.muted;
  ctx.fillText(text, 6, y);
  ctx.restore();
}

/** 在 [x0, x1] 内画居中裁切的文字。 */
function label(ctx: CanvasRenderingContext2D, text: string, x0: number, x1: number, y: number, color: string): void {
  const width = x1 - x0 - 6;
  if (width < 8 || !text) return;
  ctx.save();
  ctx.beginPath();
  ctx.rect(x0 + 3, y - 10, width, 20);
  ctx.clip();
  ctx.fillStyle = color;
  ctx.font = '13px system-ui, sans-serif';
  ctx.textBaseline = 'middle';
  ctx.fillText(text, x0 + 4, y);
  ctx.restore();
}
