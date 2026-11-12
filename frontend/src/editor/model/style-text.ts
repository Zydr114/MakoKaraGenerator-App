// 样式取值的只读文字与色块（模板分组、本层覆盖、外观来源共用）：颜色、材质、描边层、阴影、辉光、擦除。
// 两态（合同 1.50.0）的已唱一侧可跟随未唱，跟随写成“跟随未唱”。

import type { StyleColor, StyleFollow, StyleGlow, StyleMaterial, StyleShadow, StyleStroke, StyleWipeHighlight } from '../../protocol';
import { formatColor } from './settings';

/** 包材质（模板里的带参数，工程样式里的只有身份）。 */
export type AnyMaterial = StyleMaterial | { kind: 'package'; packageId: string; exportId: string };

export function cssColor(color: StyleColor): string {
  return `rgb(${color.red} ${color.green} ${color.blue} / ${round(color.alpha / 255, 3)})`;
}

/** 包材质的色块：斜纹，表示颜色由效果包决定。 */
export const PACKAGE_MATERIAL_CSS = 'repeating-linear-gradient(45deg, #9e9e9e 0 4px, #e0e0e0 4px 8px)';

/** 材质的 CSS 背景：纯色，或按色标从左到右的渐变条（只显示色标，不是渲染结果）；包材质是斜纹。 */
export function materialCss(material: AnyMaterial): string {
  if (material.kind === 'package') return PACKAGE_MATERIAL_CSS;
  if (material.kind === 'solid') return cssColor(material.color);
  const stops = material.gradient.stops.map((s) => `${cssColor(s.color)} ${round(s.offset * 100, 2)}%`);
  return `linear-gradient(90deg, ${stops.join(', ')})`;
}

export function materialText(material: AnyMaterial): string {
  if (material.kind === 'package') return `效果包材质 · ${material.exportId}`;
  if (material.kind === 'solid') return formatColor(material.color);
  const g = material.gradient;
  return `${g.kind === 'linear' ? `线性渐变 ${g.angleDegrees}°` : '径向渐变'} · ${g.stops.length} 个色标`;
}

export const ALIGN_TEXT: Record<string, string> = { left: '居左', center: '居中', right: '居右' };
export const JOIN_TEXT: Record<string, string> = { miter: '尖角', round: '圆角', bevel: '斜角' };
export const BLEND_TEXT: Record<string, string> = { normal: '普通', screen: '滤色', add: '相加' };
export const FOLLOW_TEXT = '跟随未唱';

export function percentText(value: number): string {
  return `${round(value * 100, 1)}%`;
}

/** 按字号的比例（羽化、高亮宽度）。 */
export function ratioText(value: number): string {
  return `${value} 倍字号`;
}

/** 一层描边。 */
export function strokeText(stroke: StyleStroke<AnyMaterial>): string {
  return `${stroke.widthPx} px · ${JOIN_TEXT[stroke.join] ?? stroke.join}连接 · ${materialText(stroke.material)}`;
}

/** 描边各层（由内到外）；空表是不描边。 */
export function strokesText(strokes: readonly StyleStroke<AnyMaterial>[]): string {
  if (strokes.length === 0) return '关';
  if (strokes.length === 1) return strokeText(strokes[0]!);
  return `${strokes.length} 层：${strokes.map(strokeText).join(' ／ ')}`;
}

export function shadowText(shadow: StyleShadow | null): string {
  if (!shadow) return '关';
  return `${formatColor(shadow.color)} · 不透明度 ${percentText(shadow.opacity)} · ${shadow.angleDegrees}° · 距离 ${shadow.distancePx} px · 模糊 ${shadow.blurRadiusPx} px`;
}

export function glowText(glow: StyleGlow | null): string {
  if (!glow) return '关';
  return `${formatColor(glow.color)} · 不透明度 ${percentText(glow.opacity)} · 扩张 ${glow.spreadPx} px · 模糊 ${glow.blurRadiusPx} px`;
}

export function highlightText(highlight: StyleWipeHighlight | null): string {
  if (!highlight) return '关';
  return `${formatColor(highlight.color)} · 宽 ${ratioText(highlight.widthRatio)} · 强度 ${percentText(highlight.opacity)} · ${BLEND_TEXT[highlight.blend] ?? highlight.blend}`;
}

export function featherText(ratio: number): string {
  return ratio === 0 ? '硬边' : ratioText(ratio);
}

/** 已唱一侧的跟随项：跟随写成“跟随未唱”，显式的按 text 写。 */
export function followText<T>(value: StyleFollow<T>, text: (value: T) => string): string {
  return value.kind === 'follow' ? FOLLOW_TEXT : text(value.value);
}

/** 已唱一侧实际生效的值：跟随时取未唱一侧。 */
export function followValue<T>(value: StyleFollow<T>, unsung: T): T {
  return value.kind === 'follow' ? unsung : value.value;
}

export function round(value: number, digits: number): number {
  const f = 10 ** digits;
  return Math.round(value * f) / f;
}
