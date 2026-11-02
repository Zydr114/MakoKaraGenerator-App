// 工作区布局（界面改版 §2.6）：分栏宽度、时间轴高度的范围与本机记忆，窗口宽度断点。纯函数，不碰 DOM。

export type LayoutPage = 'lyrics' | 'orchestration' | 'appearance' | 'library';
export type Side = 'left' | 'right';

/** 分栏宽度（像素）；双击分隔条回到 initial。 */
export const SIDE_LIMITS: Record<Side, { min: number; max: number; initial: number }> = {
  left: { min: 240, max: 400, initial: 300 },
  right: { min: 300, max: 440, initial: 340 },
};

/** 个别页面的默认宽度另取：样式库的右栏要放下编辑页签。 */
const PAGE_INITIAL: Partial<Record<LayoutPage, Partial<Record<Side, number>>>> = { library: { right: 400 } };

export function initialSide(page: LayoutPage, side: Side): number {
  return PAGE_INITIAL[page]?.[side] ?? SIDE_LIMITS[side].initial;
}

/** 时间轴画布高度（像素）；没拖过时按轨道数取自然高度。 */
export const TIMELINE_LIMITS = { min: 120, max: 320 } as const;

/** 收起或改成覆盖面板后，栏剩下的窄条宽度。 */
export const RAIL_WIDTH = 40;

export interface LayoutSizes {
  left: number;
  right: number;
  /** undefined：自然高度。 */
  timeline: number | undefined;
}

export const layoutKey = (page: LayoutPage) => `mako.layout.${page}`;

const clamp = (value: number, min: number, max: number) => Math.min(max, Math.max(min, Math.round(value)));

export function clampSide(side: Side, px: number): number {
  return clamp(px, SIDE_LIMITS[side].min, SIDE_LIMITS[side].max);
}

export function clampTimeline(px: number): number {
  return clamp(px, TIMELINE_LIMITS.min, TIMELINE_LIMITS.max);
}

/** 读本机记的尺寸；缺项、坏值、越界都回到默认或夹到范围里。 */
export function parseLayout(text: string | null, page: LayoutPage = 'lyrics'): LayoutSizes {
  let raw: Record<string, unknown> = {};
  try {
    const parsed: unknown = text ? JSON.parse(text) : {};
    if (parsed && typeof parsed === 'object') raw = parsed as Record<string, unknown>;
  } catch {
    // 坏值当没有
  }
  const num = (value: unknown) => (typeof value === 'number' && Number.isFinite(value) ? value : undefined);
  const left = num(raw.left);
  const right = num(raw.right);
  const timeline = num(raw.timeline);
  return {
    left: left === undefined ? initialSide(page, 'left') : clampSide('left', left),
    right: right === undefined ? initialSide(page, 'right') : clampSide('right', right),
    timeline: timeline === undefined ? undefined : clampTimeline(timeline),
  };
}

/**
 * 窗口宽度断点（界面改版 §2.6、§7.1）：
 * ≥ 1440 两栏常驻；1200～1439 右栏改为覆盖面板；< 1200 左右栏都是覆盖面板，同一时间只开一个。
 * < 960 另外在顶部提示换更宽的窗口（布局同 < 1200）。
 */
export type WidthMode = 'docked' | 'rightOverlay' | 'bothOverlay';

export const NARROW_WIDTH = 960;

export function widthMode(width: number): WidthMode {
  return width >= 1440 ? 'docked' : width >= 1200 ? 'rightOverlay' : 'bothOverlay';
}

export function isOverlaySide(mode: WidthMode, side: Side): boolean {
  return mode === 'bothOverlay' || (mode === 'rightOverlay' && side === 'right');
}
