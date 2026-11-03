// 外观检查器里只读显示的“本层直接覆盖”（GET .../style 的 local，合同 §6.11），
// 以及前奏提示组自己的完整外观（编排读模型，W7 设计 §2.8）。
// 属性名是闭合集合；取值只做显示，不回写。认不出的属性或取值原样显示 JSON。

import type { SceneTextAppearance, StaticMaterial, StyleChange, StyleColor, StyleGlow, StyleShadow, StyleStroke } from '../../protocol';
import { cssColor, materialCss, materialText } from './template-groups';
import { formatColor } from './settings';

export interface OverrideRow {
  property: string;
  label: string;
  text: string;
  /** 色块的 CSS 背景（颜色、材质）。 */
  swatch?: string;
}

const LABELS: Record<string, string> = {
  'typography.fontFamilyName': '字体',
  'typography.fallbackFamilyNames': '回退字体',
  'typography.fontSizePx': '字号',
  'typography.fontWeight': '字重',
  'typography.isBold': '粗体',
  'typography.isItalic': '斜体',
  'typography.letterSpacingPx': '字距',
  'unsungFill.material': '未唱填充',
  'sungFill.material': '已唱填充',
  stroke: '描边',
  'stroke.material': '描边材质',
  'stroke.widthPx': '描边宽度',
  'stroke.join': '描边转角',
  'stroke.position': '描边位置',
  shadow: '阴影',
  'shadow.color': '阴影颜色',
  'shadow.opacity': '阴影不透明度',
  'shadow.angleDegrees': '阴影角度',
  'shadow.distancePx': '阴影距离',
  'shadow.blurRadiusPx': '阴影模糊',
  glow: '辉光',
  'glow.color': '辉光颜色',
  'glow.opacity': '辉光不透明度',
  'glow.spreadPx': '辉光扩张',
  'glow.blurRadiusPx': '辉光模糊',
  verticalCenterRatio: '垂直中心',
  horizontalAlignment: '水平对齐',
  opacity: '透明度',
  scale: '缩放',
};

const ALIGN_TEXT: Record<string, string> = { left: '居左', center: '居中', right: '居右' };
const JOIN_TEXT: Record<string, string> = { miter: '尖角', round: '圆角', bevel: '斜角' };

export function overrideRows(local: readonly StyleChange[]): OverrideRow[] {
  return local.map(({ property, value }) => ({ property, label: LABELS[property] ?? property, ...describe(property, value) }));
}

/** 前奏提示组的完整外观；回退字体为空时不列。 */
export function sceneAppearanceRows(appearance: SceneTextAppearance): OverrideRow[] {
  const t = appearance.typography;
  const rows: Array<[property: string, value: unknown, label?: string]> = [
    ['typography.fontFamilyName', t.fontFamilyName],
    ...(t.fallbackFamilyNames.length > 0 ? [['typography.fallbackFamilyNames', t.fallbackFamilyNames] as [string, unknown]] : []),
    ['typography.fontSizePx', t.fontSizePx],
    ['typography.fontWeight', t.fontWeight],
    ['typography.isBold', t.isBold],
    ['typography.isItalic', t.isItalic],
    ['typography.letterSpacingPx', t.letterSpacingPx],
    ['unsungFill.material', appearance.pendingFill.material, '未到填充'],
    ['sungFill.material', appearance.elapsedFill.material, '已过填充'],
    ['stroke', appearance.stroke],
    ['shadow', appearance.shadow],
    ['glow', appearance.glow],
    ['opacity', appearance.opacity],
  ];
  return rows.map(([property, value, label]) => ({ property, label: label ?? LABELS[property] ?? property, ...describe(property, value) }));
}

/** 一个属性取值的显示：属性名用 StyleChange 的路径（例如 typography.fontSizePx、stroke）。 */
export function describeValue(property: string, value: unknown): { text: string; swatch?: string } {
  return describe(property, value);
}

function describe(property: string, value: unknown): { text: string; swatch?: string } {
  const raw = { text: JSON.stringify(value) };
  switch (property) {
    case 'typography.fontFamilyName':
      return value === null ? { text: '后端默认字体' } : typeof value === 'string' ? { text: value } : raw;
    case 'typography.fallbackFamilyNames':
      return Array.isArray(value) ? { text: value.length ? value.join('、') : '（清空）' } : raw;
    case 'typography.fontSizePx':
    case 'typography.letterSpacingPx':
    case 'stroke.widthPx':
    case 'shadow.distancePx':
    case 'shadow.blurRadiusPx':
    case 'glow.spreadPx':
    case 'glow.blurRadiusPx':
      return typeof value === 'number' ? { text: `${value} px` } : raw;
    case 'typography.fontWeight':
      return typeof value === 'number' ? { text: String(value) } : raw;
    case 'typography.isBold':
    case 'typography.isItalic':
      return typeof value === 'boolean' ? { text: value ? '是' : '否' } : raw;
    case 'unsungFill.material':
    case 'sungFill.material':
    case 'stroke.material':
      return isStaticMaterial(value) ? { text: materialText(value), swatch: materialCss(value) } : isPackage(value) ? { text: '包材质' } : raw;
    case 'stroke.join':
      return typeof value === 'string' ? { text: JOIN_TEXT[value] ?? value } : raw;
    case 'stroke.position':
      return value === 'outside' ? { text: '外侧' } : raw;
    case 'stroke': {
      if (value === null) return { text: '关' };
      const stroke = value as StyleStroke<StaticMaterial>;
      if (!isRecord(value) || typeof stroke.widthPx !== 'number') return raw;
      return isStaticMaterial(stroke.material)
        ? { text: `${stroke.widthPx} px · ${JOIN_TEXT[stroke.join] ?? stroke.join} · ${materialText(stroke.material)}`, swatch: materialCss(stroke.material) }
        : { text: `${stroke.widthPx} px · 包材质` };
    }
    case 'shadow': {
      if (value === null) return { text: '关' };
      const shadow = value as StyleShadow;
      if (!isRecord(value) || !isColor(shadow.color)) return raw;
      return {
        text: `${formatColor(shadow.color)} · 不透明度 ${percent(shadow.opacity)} · ${shadow.angleDegrees}° · 距离 ${shadow.distancePx} px · 模糊 ${shadow.blurRadiusPx} px`,
        swatch: cssColor(shadow.color),
      };
    }
    case 'glow': {
      if (value === null) return { text: '关' };
      const glow = value as StyleGlow;
      if (!isRecord(value) || !isColor(glow.color) || ![glow.opacity, glow.spreadPx, glow.blurRadiusPx].every((v) => typeof v === 'number')) return raw;
      return {
        text: `${formatColor(glow.color)} · 不透明度 ${percent(glow.opacity)} · 扩张 ${glow.spreadPx} px · 模糊 ${glow.blurRadiusPx} px`,
        swatch: cssColor(glow.color),
      };
    }
    case 'shadow.color':
    case 'glow.color':
      return isColor(value) ? { text: formatColor(value), swatch: cssColor(value) } : raw;
    case 'shadow.opacity':
    case 'glow.opacity':
    case 'verticalCenterRatio':
    case 'opacity':
      return typeof value === 'number' ? { text: percent(value) } : raw;
    case 'shadow.angleDegrees':
      return typeof value === 'number' ? { text: `${value}°` } : raw;
    case 'horizontalAlignment':
      return typeof value === 'string' ? { text: ALIGN_TEXT[value] ?? value } : raw;
    case 'scale':
      return typeof value === 'number' ? { text: `×${value}` } : raw;
    default:
      return raw;
  }
}

function percent(value: number): string {
  return `${Math.round(value * 1000) / 10}%`;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isColor(value: unknown): value is StyleColor {
  return isRecord(value) && ['red', 'green', 'blue', 'alpha'].every((k) => typeof value[k] === 'number');
}

function isStaticMaterial(value: unknown): value is StaticMaterial {
  if (!isRecord(value)) return false;
  if (value.kind === 'solid') return isColor(value.color);
  return value.kind === 'gradient' && isRecord(value.gradient) && Array.isArray(value.gradient.stops);
}

function isPackage(value: unknown): boolean {
  return isRecord(value) && value.kind === 'package';
}
