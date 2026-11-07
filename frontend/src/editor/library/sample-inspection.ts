// 试样画布的调试叠加层与成本视图（SDK v2 检查投影设计 §5）。不依赖 React。
// 叠加层只画在图片上方，PNG 不变；坐标是工程画布坐标，SVG 按 viewBox 缩放到图片的显示尺寸。
// 成本面板把同一帧 Worker 内的分阶段耗时和 Host 的往返、编码分开写，脚本行按 scriptId 对回效果实例。

import type {
  InspectionPolygon,
  InspectionProgressSource,
  InspectionScriptStage,
  PreviewFrameInspectionResponse,
  PreviewInspection,
  PreviewInspectionEffect,
} from '../../protocol';

export const INSPECTION_PREFS_KEY = 'mako.sampleInspection';

export const INSPECTION_LAYERS = ['clusters', 'ruby', 'cursors', 'particles', 'effects'] as const;
export type InspectionLayer = (typeof INSPECTION_LAYERS)[number];

export const LAYER_LABEL: Record<InspectionLayer, string> = {
  clusters: '字簇',
  ruby: '注音归属',
  cursors: '播放头',
  particles: '粒子出生点',
  effects: '效果范围',
};

/** 叠加层与成本面板开关、图例里隐藏的类别；记在本机。 */
export interface InspectionPrefs {
  overlay: boolean;
  cost: boolean;
  hidden: InspectionLayer[];
}

export const DEFAULT_INSPECTION_PREFS: InspectionPrefs = { overlay: false, cost: false, hidden: [] };

type PreferenceStorage = Pick<Storage, 'getItem' | 'setItem'>;

export function readInspectionPrefs(storage: PreferenceStorage | undefined): InspectionPrefs {
  try {
    const raw = storage?.getItem(INSPECTION_PREFS_KEY);
    if (!raw) return DEFAULT_INSPECTION_PREFS;
    const value = JSON.parse(raw) as Partial<InspectionPrefs>;
    const hidden = Array.isArray(value.hidden) ? INSPECTION_LAYERS.filter((layer) => value.hidden!.includes(layer)) : [];
    return { overlay: value.overlay === true, cost: value.cost === true, hidden };
  } catch {
    return DEFAULT_INSPECTION_PREFS;
  }
}

export function writeInspectionPrefs(storage: PreferenceStorage | undefined, prefs: InspectionPrefs): void {
  try {
    storage?.setItem(INSPECTION_PREFS_KEY, JSON.stringify(prefs));
  } catch {
    // 本机存储不可用：只在这次打开里生效。
  }
}

/** 任一开关打开时，取帧都带 inspect。 */
export function wantsInspection(prefs: InspectionPrefs): boolean {
  return prefs.overlay || prefs.cost;
}

// ———— 叠加层 ————

export type OverlayShape =
  | { layer: InspectionLayer; kind: 'polygon'; points: string; tone: OverlayTone; title?: string }
  | { layer: InspectionLayer; kind: 'line'; x1: number; y1: number; x2: number; y2: number; tone: OverlayTone }
  | { layer: InspectionLayer; kind: 'dot'; x: number; y: number; tone: OverlayTone; title?: string }
  | { layer: InspectionLayer; kind: 'label'; x: number; y: number; text: string; tone: OverlayTone };

/** 颜色类别，对应 CSS 类 inspect-<tone>。 */
export type OverlayTone = 'token' | 'cluster' | 'broken' | 'ruby' | 'cursor' | 'track' | 'particle' | 'effect';

export interface OverlayModel {
  width: number;
  height: number;
  /** 文字大小（画布单位），约等于显示时的 11px。 */
  fontSize: number;
  shapes: OverlayShape[];
  /** 每类有几个对象（图例里写）。 */
  counts: Record<InspectionLayer, number>;
}

const PROGRESS_SOURCE_LABEL: Record<InspectionProgressSource, string> = {
  unavailable: '无进度',
  fromLine: '按行',
  fromToken: '按字',
  fromRubySegment: '按注音',
};

/** 把投影转成要画的图形；hidden 里的类别不画（计数照算）。 */
export function overlayModel(inspection: PreviewInspection, hidden: readonly InspectionLayer[] = []): OverlayModel {
  const { width, height } = inspection.canvas;
  const fontSize = Math.max(8, Math.round(height / 45));
  const show = (layer: InspectionLayer) => !hidden.includes(layer);
  const shapes: OverlayShape[] = [];
  const counts: Record<InspectionLayer, number> = { clusters: 0, ruby: 0, cursors: 0, particles: 0, effects: 0 };

  for (const token of inspection.tokens) {
    counts.clusters += token.clusters.length;
    if (show('clusters')) {
      const tokenPoints = polygonPoints(token.ink);
      if (tokenPoints) shapes.push({ layer: 'clusters', kind: 'polygon', points: tokenPoints, tone: token.independent ? 'token' : 'broken', title: token.tokenId });
      for (const cluster of token.clusters) {
        const points = polygonPoints(cluster.ink);
        if (!points) continue;
        const shared = cluster.ownerTokenIds.length > 1;
        const broken = !cluster.independent || shared;
        const code = cluster.diagnosticCode ?? (shared ? `跨 ${cluster.ownerTokenIds.length} 个文本单元` : undefined);
        shapes.push({ layer: 'clusters', kind: 'polygon', points, tone: broken ? 'broken' : 'cluster', title: [token.tokenId, `#${cluster.unitIndex}`, ...(code ? [code] : [])].join(' ') });
        if (broken && code) {
          const [x, y] = topLeft(cluster.ink!);
          shapes.push({ layer: 'clusters', kind: 'label', x, y: y - fontSize * 0.3, text: code, tone: 'broken' });
        }
      }
      if (!token.independent && token.diagnosticCode && token.ink) {
        const [x, y] = topLeft(token.ink);
        shapes.push({ layer: 'clusters', kind: 'label', x, y: y - fontSize * 0.3, text: token.diagnosticCode, tone: 'broken' });
      }
    }
    counts.ruby += token.ruby.length;
    if (show('ruby')) {
      const owner = token.ink ? centroid(token.ink) : undefined;
      for (const ruby of token.ruby) {
        const points = polygonPoints(ruby.ink);
        if (!points) continue;
        shapes.push({ layer: 'ruby', kind: 'polygon', points, tone: 'ruby', title: `${ruby.rubyId} → ${token.tokenId}` });
        if (owner) {
          const [x1, y1] = centroid(ruby.ink!);
          shapes.push({ layer: 'ruby', kind: 'line', x1, y1, x2: owner[0], y2: owner[1], tone: 'ruby' });
        }
      }
    }
  }

  counts.cursors = inspection.cursors.length;
  if (show('cursors')) {
    for (const cursor of inspection.cursors) {
      const [sx, sy] = cursor.start;
      const [ex, ey] = cursor.end;
      const [px, py] = cursor.point;
      if (sx !== undefined && sy !== undefined && ex !== undefined && ey !== undefined) shapes.push({ layer: 'cursors', kind: 'line', x1: sx, y1: sy, x2: ex, y2: ey, tone: 'track' });
      if (px === undefined || py === undefined) continue;
      const text = `${Math.round(cursor.progress * 100)}% ${PROGRESS_SOURCE_LABEL[cursor.progressSource]}`;
      shapes.push({ layer: 'cursors', kind: 'dot', x: px, y: py, tone: 'cursor', title: [cursor.lineId, cursor.tokenId ?? '', text].filter(Boolean).join(' ') });
      shapes.push({ layer: 'cursors', kind: 'label', x: px + fontSize * 0.5, y: py - fontSize * 0.5, text, tone: 'cursor' });
    }
  }

  for (const particles of inspection.particles) {
    counts.particles += particles.birthPoints.length / 2 + particles.omittedBirthPoints;
    if (!show('particles')) continue;
    for (let i = 0; i + 1 < particles.birthPoints.length; i += 2)
      shapes.push({ layer: 'particles', kind: 'dot', x: particles.birthPoints[i]!, y: particles.birthPoints[i + 1]!, tone: 'particle' });
  }

  counts.effects = inspection.effects.length;
  if (show('effects')) {
    const bounds = new Map(inspection.lines.map((line) => [line.lineId, line.bounds]));
    // 同一行有几个效果：标签往下错开，免得叠在一起。
    const stacked = new Map<string, number>();
    for (const effect of inspection.effects) {
      for (const lineId of effect.lineIds) {
        const line = bounds.get(lineId);
        const points = polygonPoints(line);
        if (!points) continue;
        const index = stacked.get(lineId) ?? 0;
        stacked.set(lineId, index + 1);
        const name = effectName(effect);
        shapes.push({ layer: 'effects', kind: 'polygon', points, tone: 'effect', title: `${name} · ${lineId}` });
        const [x, y] = topLeft(line!);
        shapes.push({ layer: 'effects', kind: 'label', x, y: y - fontSize * (0.4 + index * 1.2), text: name, tone: 'effect' });
      }
    }
  }
  return { width, height, fontSize, shapes, counts };
}

/** 平铺坐标转成 SVG points；少于 3 个点或长度不对时不画。 */
export function polygonPoints(polygon: InspectionPolygon | undefined | null): string | undefined {
  if (!polygon || polygon.length < 6 || polygon.length % 2 !== 0) return undefined;
  const pairs: string[] = [];
  for (let i = 0; i < polygon.length; i += 2) pairs.push(`${round(polygon[i]!)},${round(polygon[i + 1]!)}`);
  return pairs.join(' ');
}

function centroid(polygon: InspectionPolygon): [number, number] {
  let x = 0;
  let y = 0;
  const n = polygon.length / 2;
  for (let i = 0; i < polygon.length; i += 2) {
    x += polygon[i]!;
    y += polygon[i + 1]!;
  }
  return [x / n, y / n];
}

function topLeft(polygon: InspectionPolygon): [number, number] {
  let x = Infinity;
  let y = Infinity;
  for (let i = 0; i < polygon.length; i += 2) {
    x = Math.min(x, polygon[i]!);
    y = Math.min(y, polygon[i + 1]!);
  }
  return [x, y];
}

function round(value: number): number {
  return Math.round(value * 100) / 100;
}

/** 效果实例的显示名：包与导出名；都没有时用实例 ID。 */
export function effectName(effect: PreviewInspectionEffect): string {
  const parts = [effect.packageId, effect.exportId].filter((p): p is string => !!p);
  return parts.length > 0 ? parts.join(' · ') : effect.instanceId;
}

// ———— 成本 ————

export interface CostPhase {
  key: string;
  label: string;
  ms: number;
}

export interface CostScriptRow {
  scriptId: string;
  stage: InspectionScriptStage;
  /** 用这段脚本的效果实例（包 · 导出名）；对不上时写脚本 ID。 */
  owner: string;
  calls: number;
  wallMs: number;
  maxMs: number;
}

export interface CostModel {
  /** Worker 内一帧总耗时。 */
  wallMs: number;
  phases: CostPhase[];
  /** 阶段条的满格（取各阶段与总耗时的最大值）。 */
  scaleMs: number;
  counts: { label: string; value: string }[];
  scripts: CostScriptRow[];
  scriptOverflow: number;
  host: { label: string; value: string }[];
  /** 投影超出上限省略的条目；没有省略时为 undefined。 */
  truncated?: string;
}

const STAGE_LABEL: Record<InspectionScriptStage, string> = {
  Initialize: '初始化',
  Advance: 'Advance',
  Evaluate: 'Evaluate',
  OnEvent: 'OnEvent',
  AdvanceVisuals: 'AdvanceVisuals',
  EvaluateVisuals: 'EvaluateVisuals',
  EvaluateEmitters: 'EvaluateEmitters',
  other: '其他',
};

export function stageLabel(stage: InspectionScriptStage): string {
  return STAGE_LABEL[stage] ?? stage;
}

export function costModel(response: PreviewFrameInspectionResponse): CostModel {
  const { inspection, host } = response;
  const cost = inspection.cost;
  const phases: CostPhase[] = [
    { key: 'prepare', label: '准备', ms: cost.prepareMs },
    { key: 'script', label: '脚本', ms: cost.scriptMs },
    { key: 'graph', label: '图', ms: cost.graphMs },
    { key: 'material', label: '材质', ms: cost.materialMs },
    { key: 'particle', label: '粒子', ms: cost.particleMs },
    { key: 'raster', label: '栅格', ms: cost.rasterMs },
    { key: 'validation', label: '验证', ms: cost.validationMs },
    { key: 'readback', label: '读回', ms: cost.readbackMs },
  ];
  const scaleMs = Math.max(cost.wallMs, ...phases.map((p) => p.ms), 0.001);
  const births = inspection.particles.reduce((sum, p) => sum + p.births, 0);
  const alive = inspection.particles.reduce((sum, p) => sum + p.alive, 0);
  const trails = inspection.particles.reduce((sum, p) => sum + p.trailSamples, 0);
  const anchors = cost.anchors;
  const counts = [
    { label: '脚本调用', value: String(cost.scriptCalls) },
    { label: '锚点补算', value: `${anchors.uniqueTimes} 个时刻 · ${anchors.textCalls} 次文本 · ${anchors.futureEndpoints} 个未来端点` },
    { label: '锚点缓存', value: `命中 ${anchors.memoHits} · 未命中 ${anchors.memoMisses}` },
    { label: '粒子', value: `出生 ${births} · 存活 ${alive} · 拖尾 ${trails}` },
    { label: '读回', value: formatBytes(cost.readbackBytes) },
    { label: '分配（全进程）', value: formatBytes(cost.allocatedBytes) },
    { label: 'GC', value: `${cost.gc0} / ${cost.gc1} / ${cost.gc2}` },
    ...(cost.sessionOpenMs !== undefined ? [{ label: '会话打开', value: formatMs(cost.sessionOpenMs) }] : []),
  ];
  const owners = new Map<string, string[]>();
  for (const effect of inspection.effects) {
    if (!effect.scriptId) continue;
    const list = owners.get(effect.scriptId) ?? [];
    const name = effectName(effect);
    if (!list.includes(name)) list.push(name);
    owners.set(effect.scriptId, list);
  }
  const scripts = cost.scripts.map((s) => ({
    scriptId: s.scriptId,
    stage: s.stage,
    owner: owners.get(s.scriptId)?.join('、') ?? s.scriptId,
    calls: s.calls,
    wallMs: s.wallMs,
    maxMs: s.maxMs,
  }));
  const hostRows = [
    { label: '渲染往返', value: formatMs(host.renderRoundTripMs) },
    { label: 'PNG 编码', value: formatMs(host.encodeMs) },
    { label: 'PNG 大小', value: formatBytes(host.pngBytes) },
  ];
  const truncated = truncationText(inspection);
  return { wallMs: cost.wallMs, phases, scaleMs, counts, scripts, scriptOverflow: cost.scriptOverflow, host: hostRows, ...(truncated ? { truncated } : {}) };
}

/** 超出上限被省略的条目，逐类写出；整类和单项（字簇、出生点、效果的行）都算。 */
export function truncationText(inspection: PreviewInspection): string | undefined {
  const t = inspection.truncated;
  const clusters = inspection.tokens.reduce((sum, token) => sum + token.omittedClusters, 0);
  const birthPoints = inspection.particles.reduce((sum, p) => sum + p.omittedBirthPoints, 0);
  const lines = inspection.effects.reduce((sum, e) => sum + e.omittedLines, 0);
  const parts = [
    [t.tokens, '文本单元'],
    [clusters, '字簇'],
    [t.lines, '行'],
    [t.cursors, '播放头'],
    [t.particles, '粒子实例'],
    [birthPoints, '出生点'],
    [t.effects, '效果实例'],
    [lines, '效果绑定的行'],
  ] as const;
  const text = parts.filter(([count]) => count > 0).map(([count, label]) => `${label} ${count}`);
  return text.length > 0 ? `超出上限未列出：${text.join('，')}` : undefined;
}

export function formatMs(ms: number): string {
  return ms >= 100 ? `${ms.toFixed(0)} ms` : ms >= 10 ? `${ms.toFixed(1)} ms` : `${ms.toFixed(2)} ms`;
}

export function formatBytes(bytes: number): string {
  if (bytes >= 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MiB`;
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(1)} KiB`;
  return `${bytes} B`;
}
