// 渐变色标的编辑规则（W3 设计 §4 第 5 步）。约束取自后端 GradientSpec.IsValid：
// 2..256 个色标，位置不递减，首个为 0、末个为 1；径向时中心在 0..1、半径大于 0。

import type { StyleColor, StyleGradient, StyleGradientStop } from '../../protocol';

export const MAX_GRADIENT_STOPS = 256;

/** 在最宽的间隙中点插入一个色标，颜色取两端的平均。 */
export function addStop(gradient: StyleGradient): StyleGradient {
  const stops = gradient.stops;
  if (stops.length >= MAX_GRADIENT_STOPS) return gradient;
  let at = 1;
  for (let i = 1; i < stops.length; i++) if (stops[i]!.offset - stops[i - 1]!.offset > stops[at]!.offset - stops[at - 1]!.offset) at = i;
  const a = stops[at - 1]!;
  const b = stops[at]!;
  const stop: StyleGradientStop = { offset: round((a.offset + b.offset) / 2), color: mix(a.color, b.color) };
  return { ...gradient, stops: [...stops.slice(0, at), stop, ...stops.slice(at)] };
}

/** 删除中间的色标；首末两个固定在 0 和 1，不能删。 */
export function removeStop(gradient: StyleGradient, index: number): StyleGradient {
  if (index <= 0 || index >= gradient.stops.length - 1) return gradient;
  return { ...gradient, stops: gradient.stops.filter((_, i) => i !== index) };
}

/** 改中间色标的位置（0..1），再按位置稳定排序；首末不能改。 */
export function setStopOffset(gradient: StyleGradient, index: number, offset: number): StyleGradient {
  if (index <= 0 || index >= gradient.stops.length - 1) return gradient;
  const clamped = Math.min(1, Math.max(0, offset));
  const stops = gradient.stops.map((s, i) => (i === index ? { ...s, offset: clamped } : s));
  const [first, ...rest] = stops;
  const last = rest.pop()!;
  return { ...gradient, stops: [first!, ...rest.sort((x, y) => x.offset - y.offset), last] };
}

export function setStopColor(gradient: StyleGradient, index: number, color: StyleColor): StyleGradient {
  return { ...gradient, stops: gradient.stops.map((s, i) => (i === index ? { ...s, color } : s)) };
}

/** 渐变不合法的原因；合法时返回 undefined。 */
export function gradientProblem(gradient: StyleGradient): string | undefined {
  const { stops } = gradient;
  if (stops.length < 2 || stops.length > MAX_GRADIENT_STOPS) return `色标要有 2～${MAX_GRADIENT_STOPS} 个。`;
  if (stops[0]!.offset !== 0 || stops.at(-1)!.offset !== 1) return '第一个色标的位置要是 0%，最后一个要是 100%。';
  if (stops.some((s, i) => i > 0 && s.offset < stops[i - 1]!.offset)) return '色标的位置不能倒序。';
  if (gradient.kind === 'radial') {
    if ([gradient.centerX, gradient.centerY].some((v) => v < 0 || v > 1)) return '径向渐变的中心要在 0～1 之间。';
    if (gradient.radiusX <= 0 || gradient.radiusY <= 0) return '径向渐变的半径要大于 0。';
  }
  return undefined;
}

function mix(a: StyleColor, b: StyleColor): StyleColor {
  const avg = (x: number, y: number) => Math.round((x + y) / 2);
  return { red: avg(a.red, b.red), green: avg(a.green, b.green), blue: avg(a.blue, b.blue), alpha: avg(a.alpha, b.alpha) };
}

function round(value: number): number {
  return Math.round(value * 10000) / 10000;
}
