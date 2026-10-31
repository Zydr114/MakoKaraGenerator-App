// 时间轴几何：视口（起点＋每像素微秒数＋宽度）与像素换算、缩放、标尺刻度、边界命中。
// 纯函数，Canvas 绘制与指针事件在 timeline/ 组件里。

export interface Viewport {
  /** 视口左边缘的时间（微秒，可以为负以便留白）。 */
  startUs: number;
  /** 每像素的微秒数。越小越放大。 */
  usPerPx: number;
  widthPx: number;
}

/** 最大放大：每像素 100 微秒（一帧 33 ms 约 330 px）。 */
export const MIN_US_PER_PX = 100;
/** 最大缩小：每像素 1 秒。 */
export const MAX_US_PER_PX = 1_000_000;

export function xOf(viewport: Viewport, us: number): number {
  return (us - viewport.startUs) / viewport.usPerPx;
}

export function usAt(viewport: Viewport, x: number): number {
  return Math.round(viewport.startUs + x * viewport.usPerPx);
}

export function endUs(viewport: Viewport): number {
  return viewport.startUs + viewport.widthPx * viewport.usPerPx;
}

export function clampUsPerPx(usPerPx: number): number {
  return Math.min(MAX_US_PER_PX, Math.max(MIN_US_PER_PX, usPerPx));
}

/** 以 anchorX 处的时间为不动点缩放。factor > 1 为缩小。 */
export function zoomAround(viewport: Viewport, factor: number, anchorX: number): Viewport {
  const anchorUs = viewport.startUs + anchorX * viewport.usPerPx;
  const usPerPx = clampUsPerPx(viewport.usPerPx * factor);
  return { ...viewport, usPerPx, startUs: anchorUs - anchorX * usPerPx };
}

/** 视口能完整显示 [0, durationUs]，两侧不留白。 */
export function fitViewport(durationUs: number, widthPx: number): Viewport {
  const usPerPx = clampUsPerPx(durationUs / Math.max(1, widthPx));
  return { startUs: 0, usPerPx, widthPx };
}

/** 平移视口，使 [0, durationUs] 之外最多留出半屏。 */
export function clampStart(viewport: Viewport, durationUs: number): Viewport {
  const span = viewport.widthPx * viewport.usPerPx;
  const min = -span / 2;
  const max = Math.max(min, durationUs - span / 2);
  return { ...viewport, startUs: Math.min(max, Math.max(min, viewport.startUs)) };
}

/** 若 us 不在视口内（留 margin 比例的边距），返回把它放到左侧 margin 处的新视口；否则原样返回。 */
export function follow(viewport: Viewport, us: number, margin = 0.1): Viewport {
  const span = viewport.widthPx * viewport.usPerPx;
  const left = viewport.startUs + span * margin;
  const right = viewport.startUs + span * (1 - margin);
  if (us >= left && us <= right) return viewport;
  return { ...viewport, startUs: us - span * margin };
}

const TICK_STEPS_US = [
  1_000, 2_000, 5_000, 10_000, 20_000, 50_000, 100_000, 200_000, 500_000,
  1_000_000, 2_000_000, 5_000_000, 10_000_000, 15_000_000, 30_000_000, 60_000_000, 120_000_000, 300_000_000,
];

/** 选择标尺主刻度间隔：相邻主刻度至少 minSpacingPx 像素。 */
export function tickStepUs(usPerPx: number, minSpacingPx = 80): number {
  const wanted = usPerPx * minSpacingPx;
  return TICK_STEPS_US.find((step) => step >= wanted) ?? TICK_STEPS_US[TICK_STEPS_US.length - 1]!;
}

/** 视口内的主刻度时间（step 的整数倍）。 */
export function ticks(viewport: Viewport, stepUs: number): number[] {
  const first = Math.ceil(viewport.startUs / stepUs) * stepUs;
  const last = endUs(viewport);
  const result: number[] = [];
  for (let t = first; t <= last; t += stepUs) result.push(t);
  return result;
}

export interface TimedSpan {
  id: string;
  startUs: number;
  endUs: number;
}

export type EdgeHit = { id: string; edge: 'start' | 'end'; distancePx: number };

/**
 * 找 x 附近 tolerancePx 内最近的区间边界。两个相邻区间共用边界时都返回（按距离、再按 end 在前排序），
 * 由调用方决定是否联动。
 */
export function hitEdges(viewport: Viewport, spans: readonly TimedSpan[], x: number, tolerancePx = 5): EdgeHit[] {
  const hits: EdgeHit[] = [];
  for (const span of spans) {
    for (const edge of ['start', 'end'] as const) {
      const distancePx = Math.abs(xOf(viewport, edge === 'start' ? span.startUs : span.endUs) - x);
      if (distancePx <= tolerancePx) hits.push({ id: span.id, edge, distancePx });
    }
  }
  return hits.sort((a, b) => a.distancePx - b.distancePx || (a.edge === b.edge ? 0 : a.edge === 'end' ? -1 : 1));
}

/** x 处落在哪个区间内（不含边界容差）。 */
export function hitSpan(viewport: Viewport, spans: readonly TimedSpan[], x: number): TimedSpan | undefined {
  const us = usAt(viewport, x);
  return spans.find((span) => us >= span.startUs && us < span.endUs);
}

/**
 * x 附近要拖动的边界：最近的一个，加上与它共用同一时间点、方向相反的相邻区间边界（一起移动）。
 * 元素轨和注音轨都用它；注音轨的相邻片段可以属于不同文本单元。
 */
export function linkedEdges<T extends TimedSpan>(viewport: Viewport, spans: readonly T[], x: number, tolerancePx = 5): { span: T; edge: 'start' | 'end' }[] {
  const hits = hitEdges(viewport, spans, x, tolerancePx);
  const first = hits[0];
  if (!first) return [];
  const byId = new Map(spans.map((s) => [s.id, s]));
  const at = (id: string, edge: 'start' | 'end') => (edge === 'start' ? byId.get(id)!.startUs : byId.get(id)!.endUs);
  const boundary = at(first.id, first.edge);
  return [first, ...hits.slice(1).filter((hit) => hit.edge !== first.edge && at(hit.id, hit.edge) === boundary)].map((hit) => ({ span: byId.get(hit.id)!, edge: hit.edge }));
}

/**
 * 一起拖动的边界的提交顺序：先让开、再占用。后端按顺序逐条检查相邻元素是否重叠，所以共用边界向右拖时
 * 先改后一个的起点，向左拖时先改前一个的终点；顺序反了，第一条就和邻居重叠，整条命令被拒绝。
 */
export function shrinkingFirst<T extends { edge: 'start' | 'end' }>(targets: readonly T[], fromUs: number, toUs: number): T[] {
  const shrinks = (t: T) => (t.edge === 'start' ? toUs > fromUs : toUs < fromUs);
  return [...targets.filter(shrinks), ...targets.filter((t) => !shrinks(t))];
}
