// 外观检查器里只读显示的“本层直接覆盖”（GET .../style 的 local，合同 §6.11），
// 以及前奏提示组自己的完整外观（编排读模型，W7 设计 §2.8）。
// 属性路径是 43 项闭合集合（合同 1.50.0）；取值只做显示，不回写。认不出的属性或取值原样显示 JSON。

import type {
  CanonicalStyle,
  SceneTextAppearance,
  StaticMaterial,
  StyleAppearance,
  StyleChange,
  StyleColor,
  StyleGlow,
  StylePropertyPath,
  StyleShadow,
  StyleStroke,
  StyleSungAppearance,
  StyleWipeHighlight,
} from '../../protocol';
import { formatColor } from './settings';
import {
  ALIGN_TEXT,
  type AnyMaterial,
  BLEND_TEXT,
  cssColor,
  featherText,
  FOLLOW_TEXT,
  glowText,
  highlightText,
  materialCss,
  materialText,
  percentText,
  ratioText,
  shadowText,
  strokesText,
} from './style-text';

export interface OverrideRow {
  property: string;
  label: string;
  text: string;
  /** 色块的 CSS 背景（颜色、材质）。 */
  swatch?: string;
}

const SIDE_TEXT = { unsung: '未唱', sung: '已唱' } as const;
const SHADOW_LEAF_TEXT = { color: '颜色', opacity: '不透明度', angleDegrees: '角度', distancePx: '距离', blurRadiusPx: '模糊' } as const;
const GLOW_LEAF_TEXT = { color: '颜色', opacity: '不透明度', blurRadiusPx: '模糊', spreadPx: '扩张' } as const;

function sideLabels(side: 'unsung' | 'sung'): Record<string, string> {
  const s = SIDE_TEXT[side];
  return {
    [`${side}.fill`]: `${s}填充`,
    [`${side}.strokes`]: `${s}描边`,
    [`${side}.shadow`]: `${s}阴影`,
    ...Object.fromEntries(Object.entries(SHADOW_LEAF_TEXT).map(([k, v]) => [`${side}.shadow.${k}`, `${s}阴影${v}`])),
    [`${side}.glow`]: `${s}辉光`,
    ...Object.fromEntries(Object.entries(GLOW_LEAF_TEXT).map(([k, v]) => [`${side}.glow.${k}`, `${s}辉光${v}`])),
  };
}

/** 43 项属性路径的中文名。 */
export const STYLE_PROPERTY_TEXT = {
  'typography.fontFamilyName': '字体',
  'typography.fallbackFamilyNames': '回退字体',
  'typography.fontSizePx': '字号',
  'typography.fontWeight': '字重',
  'typography.isBold': '粗体',
  'typography.isItalic': '斜体',
  'typography.letterSpacingPx': '字距',
  ...sideLabels('unsung'),
  ...sideLabels('sung'),
  'wipe.featherRatio': '擦除羽化',
  'wipe.highlight': '擦除高亮',
  'wipe.highlight.color': '高亮颜色',
  'wipe.highlight.widthRatio': '高亮宽度',
  'wipe.highlight.opacity': '高亮强度',
  'wipe.highlight.blend': '高亮混合',
  verticalCenterRatio: '垂直中心',
  horizontalAlignment: '水平对齐',
  opacity: '透明度',
  scale: '缩放',
} as Record<StylePropertyPath, string>;

function label(property: string): string {
  return (STYLE_PROPERTY_TEXT as Record<string, string>)[property] ?? property;
}

export function overrideRows(local: readonly StyleChange[]): OverrideRow[] {
  return local.map(({ property, value }) => ({ property, label: label(property), ...describe(property, value) }));
}

const FALLBACK_WHITE_COLOR: StyleColor = { red: 255, green: 255, blue: 255, alpha: 255 };

/** 前奏提示组的标准默认外观（可编辑静态材质，对应后端 Core 默认）。 */
export const DEFAULT_SCENE_TEXT_APPEARANCE: SceneTextAppearance<StaticMaterial> = {
  typography: {
    fontFamilyName: null,
    fallbackFamilyNames: [],
    fontSizePx: 32,
    fontWeight: 400,
    isBold: false,
    isItalic: false,
    letterSpacingPx: 0,
  },
  pending: {
    fill: { kind: 'solid', color: { ...FALLBACK_WHITE_COLOR } },
    strokes: [],
    shadow: null,
    glow: null,
  },
  elapsed: {
    fill: { kind: 'solid', color: { red: 95, green: 95, blue: 95, alpha: 255 } },
    strokes: { kind: 'follow' },
    shadow: { kind: 'follow' },
    glow: { kind: 'follow' },
  },
  opacity: 1,
};

function cloneStaticMaterial(material: SceneTextAppearance['pending']['fill']): StaticMaterial {
  if (material.kind === 'package') {
    return { kind: 'solid', color: { ...FALLBACK_WHITE_COLOR } };
  }
  if (material.kind === 'solid') {
    return { kind: 'solid', color: { ...material.color } };
  }
  return {
    kind: 'gradient',
    gradient: {
      ...material.gradient,
      stops: material.gradient.stops.map((stop) => ({ offset: stop.offset, color: { ...stop.color } })),
    },
  };
}

function toStaticStrokes(strokes: readonly StyleStroke[]): StyleStroke<StaticMaterial>[] {
  return strokes.map((stroke) => ({
    ...stroke,
    material: cloneStaticMaterial(stroke.material),
  }));
}

function toStaticAppearance(side: StyleAppearance): StyleAppearance<StaticMaterial> {
  return {
    fill: cloneStaticMaterial(side.fill),
    strokes: toStaticStrokes(side.strokes),
    shadow: side.shadow ? { ...side.shadow, color: { ...side.shadow.color } } : null,
    glow: side.glow ? { ...side.glow, color: { ...side.glow.color } } : null,
  };
}

function toStaticSungAppearance(side: StyleSungAppearance): StyleSungAppearance<StaticMaterial> {
  return {
    fill: cloneStaticMaterial(side.fill),
    strokes: side.strokes.kind === 'follow' ? { kind: 'follow' } : { kind: 'set', value: toStaticStrokes(side.strokes.value) },
    shadow: side.shadow.kind === 'follow'
      ? { kind: 'follow' }
      : { kind: 'set', value: side.shadow.value ? { ...side.shadow.value, color: { ...side.shadow.value.color } } : null },
    glow: side.glow.kind === 'follow'
      ? { kind: 'follow' }
      : { kind: 'set', value: side.glow.value ? { ...side.glow.value, color: { ...side.glow.value.color } } : null },
  };
}

/** 将场景文本外观收敛为纯静态材质（遇到只读 package 材质时回落为纯白 #ffffff）。 */
export function toStaticSceneAppearance(appearance: SceneTextAppearance): SceneTextAppearance<StaticMaterial> {
  return {
    typography: {
      ...appearance.typography,
      fallbackFamilyNames: [...appearance.typography.fallbackFamilyNames],
    },
    pending: toStaticAppearance(appearance.pending),
    elapsed: toStaticSungAppearance(appearance.elapsed),
    opacity: appearance.opacity,
  };
}

/** 将歌词（主样式）外观转换为前奏提示组的独立外观，保留提示组原有的字号与字距（若有）。 */
export function lyricStyleToCountdownAppearance(
  style: CanonicalStyle,
  current?: SceneTextAppearance | null,
): SceneTextAppearance<StaticMaterial> {
  return toStaticSceneAppearance({
    typography: {
      fontFamilyName: style.typography.fontFamilyName,
      fallbackFamilyNames: [...style.typography.fallbackFamilyNames],
      fontSizePx: current?.typography.fontSizePx ?? style.typography.fontSizePx,
      fontWeight: style.typography.fontWeight,
      isBold: style.typography.isBold,
      isItalic: style.typography.isItalic,
      letterSpacingPx: current?.typography.letterSpacingPx ?? style.typography.letterSpacingPx,
    },
    pending: style.unsung,
    elapsed: style.sung,
    opacity: style.opacity,
  });
}

/** 前奏提示组的完整外观（未到／已过两态）；回退字体为空时不列。 */
export function sceneAppearanceRows(appearance: SceneTextAppearance): OverrideRow[] {
  const t = appearance.typography;
  const { pending, elapsed } = appearance;
  const rows: Array<[property: string, value: unknown, label?: string]> = [
    ['typography.fontFamilyName', t.fontFamilyName],
    ...(t.fallbackFamilyNames.length > 0 ? [['typography.fallbackFamilyNames', t.fallbackFamilyNames] as [string, unknown]] : []),
    ['typography.fontSizePx', t.fontSizePx],
    ['typography.fontWeight', t.fontWeight],
    ['typography.isBold', t.isBold],
    ['typography.isItalic', t.isItalic],
    ['typography.letterSpacingPx', t.letterSpacingPx],
    ['unsung.fill', pending.fill, '未到填充'],
    ['sung.fill', elapsed.fill, '已过填充'],
    ['unsung.strokes', pending.strokes, '未到描边'],
    ['sung.strokes', elapsed.strokes, '已过描边'],
    ['unsung.shadow', pending.shadow, '未到阴影'],
    ['sung.shadow', elapsed.shadow, '已过阴影'],
    ['unsung.glow', pending.glow, '未到辉光'],
    ['sung.glow', elapsed.glow, '已过辉光'],
    ['opacity', appearance.opacity],
  ];
  return rows.map(([property, value, text]) => ({ property, label: text ?? label(property), ...describe(property, value) }));
}

/** 一个属性取值的显示：属性名用属性路径（例如 typography.fontSizePx、unsung.strokes）。 */
export function describeValue(property: string, value: unknown): { text: string; swatch?: string } {
  return describe(property, value);
}

function describe(property: string, value: unknown): { text: string; swatch?: string } {
  const raw = { text: JSON.stringify(value) };
  // 已唱一侧的整项是跟随项：跟随写“跟随未唱”，显式的按未唱同一种取值说明。
  if (property === 'sung.strokes' || property === 'sung.shadow' || property === 'sung.glow') {
    if (!isRecord(value)) return raw;
    if (value.kind === 'follow') return { text: FOLLOW_TEXT };
    return value.kind === 'set' && 'value' in value ? describe(property.replace('sung.', 'unsung.'), value.value) : raw;
  }
  const leaf = /^(?:unsung|sung)\.(shadow|glow)\.(.+)$/.exec(property);
  if (leaf) return describeEffectLeaf(leaf[2]!, value, raw);
  switch (property) {
    case 'typography.fontFamilyName':
      return value === null ? { text: '后端默认字体' } : typeof value === 'string' ? { text: value } : raw;
    case 'typography.fallbackFamilyNames':
      return Array.isArray(value) ? { text: value.length ? value.join('、') : '（清空）' } : raw;
    case 'typography.fontSizePx':
    case 'typography.letterSpacingPx':
      return typeof value === 'number' ? { text: `${value} px` } : raw;
    case 'typography.fontWeight':
      return typeof value === 'number' ? { text: String(value) } : raw;
    case 'typography.isBold':
    case 'typography.isItalic':
      return typeof value === 'boolean' ? { text: value ? '是' : '否' } : raw;
    case 'unsung.fill':
    case 'sung.fill':
      return isStaticMaterial(value) ? { text: materialText(value), swatch: materialCss(value) } : isPackage(value) ? { text: '包材质' } : raw;
    case 'unsung.strokes': {
      if (!Array.isArray(value) || !value.every(isStroke)) return raw;
      const strokes = value as StyleStroke<AnyMaterial>[];
      return { text: strokesText(strokes), ...(strokes[0] ? { swatch: materialCss(strokes[0].material) } : {}) };
    }
    case 'unsung.shadow':
      if (value === null) return { text: '关' };
      return isRecord(value) && isColor(value.color) ? { text: shadowText(value as unknown as StyleShadow), swatch: cssColor(value.color) } : raw;
    case 'unsung.glow': {
      if (value === null) return { text: '关' };
      const glow = value as StyleGlow;
      if (!isRecord(value) || !isColor(glow.color) || ![glow.opacity, glow.spreadPx, glow.blurRadiusPx].every((v) => typeof v === 'number')) return raw;
      return { text: glowText(glow), swatch: cssColor(glow.color) };
    }
    case 'wipe.featherRatio':
      return typeof value === 'number' ? { text: featherText(value) } : raw;
    case 'wipe.highlight':
      if (value === null) return { text: '关' };
      return isRecord(value) && isColor(value.color) ? { text: highlightText(value as unknown as StyleWipeHighlight), swatch: cssColor(value.color) } : raw;
    case 'wipe.highlight.color':
      return isColor(value) ? { text: formatColor(value), swatch: cssColor(value) } : raw;
    case 'wipe.highlight.widthRatio':
      return typeof value === 'number' ? { text: ratioText(value) } : raw;
    case 'wipe.highlight.opacity':
    case 'verticalCenterRatio':
    case 'opacity':
      return typeof value === 'number' ? { text: percentText(value) } : raw;
    case 'wipe.highlight.blend':
      return typeof value === 'string' ? { text: BLEND_TEXT[value] ?? value } : raw;
    case 'horizontalAlignment':
      return typeof value === 'string' ? { text: ALIGN_TEXT[value] ?? value } : raw;
    case 'scale':
      return typeof value === 'number' ? { text: `×${value}` } : raw;
    default:
      return raw;
  }
}

/** 阴影、辉光的叶：颜色、不透明度、角度与像素值。 */
function describeEffectLeaf(leaf: string, value: unknown, raw: { text: string }): { text: string; swatch?: string } {
  switch (leaf) {
    case 'color':
      return isColor(value) ? { text: formatColor(value), swatch: cssColor(value) } : raw;
    case 'opacity':
      return typeof value === 'number' ? { text: percentText(value) } : raw;
    case 'angleDegrees':
      return typeof value === 'number' ? { text: `${value}°` } : raw;
    default:
      return typeof value === 'number' ? { text: `${value} px` } : raw;
  }
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

function isStroke(value: unknown): boolean {
  return isRecord(value) && typeof value.widthPx === 'number' && typeof value.join === 'string' && (isStaticMaterial(value.material) || isPackage(value.material));
}
