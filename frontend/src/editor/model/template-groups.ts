// 模板属性分组（W3 设计 §2.5）：界面按组开关“包含在模板中”，每组对应若干 TemplateProperty。
// 用途限制取协议层的 TOKEN／COMPOSITE 属性表；实际属性语义以后端 StyleBatchEditor 为准。

import {
  COMPOSITE_TEMPLATE_PROPERTIES,
  TEMPLATE_PROPERTIES,
  TOKEN_TEMPLATE_PROPERTIES,
  type CanonicalStyle,
  type FullTemplateDefinition,
  type StaticMaterial,
  type StyleColor,
  type StyleGlow,
  type StyleMaterial,
  type StyleShadow,
  type StyleStroke,
  type TemplateKind,
  type TemplateProperty,
} from '../../protocol';
import { gradientProblem } from './gradient';
import { formatColor } from './settings';

export type TemplateGroupId = 'font' | 'size' | 'weight' | 'spacing' | 'position' | 'unsung' | 'sung' | 'stroke' | 'shadow' | 'glow' | 'opacity' | 'scale';

export interface TemplateGroup {
  id: TemplateGroupId;
  label: string;
  properties: readonly TemplateProperty[];
}

export const TEMPLATE_GROUPS: readonly TemplateGroup[] = [
  { id: 'font', label: '字体', properties: ['fontFamilyName', 'fallbackFamilyNames'] },
  { id: 'size', label: '字号', properties: ['fontSizePx'] },
  { id: 'weight', label: '字重与字形', properties: ['fontWeight', 'isBold', 'isItalic'] },
  { id: 'spacing', label: '字距', properties: ['letterSpacingPx'] },
  { id: 'position', label: '位置', properties: ['verticalCenterRatio', 'horizontalAlignment'] },
  { id: 'unsung', label: '未唱填充', properties: ['unsungColor', 'unsungGradientSpec'] },
  { id: 'sung', label: '已唱填充', properties: ['sungColor', 'sungGradientSpec'] },
  { id: 'stroke', label: '描边', properties: ['strokeColor', 'strokeWidthPx', 'strokeSpec'] },
  { id: 'shadow', label: '阴影', properties: ['shadowColor', 'shadowBlurPx', 'shadowSpec'] },
  { id: 'glow', label: '辉光', properties: ['glowColor', 'glowOpacity', 'glowSpreadPx', 'glowBlurRadiusPx', 'glowSpec'] },
  { id: 'opacity', label: '透明度', properties: ['opacity'] },
  { id: 'scale', label: '缩放', properties: ['scale'] },
];

const ALLOWED: Record<TemplateKind, ReadonlySet<TemplateProperty> | null> = {
  line: null,
  token: new Set(TOKEN_TEMPLATE_PROPERTIES),
  composite: new Set(COMPOSITE_TEMPLATE_PROPERTIES),
};

/** 这个用途的模板能否包含这一组。 */
export function groupAllowed(group: TemplateGroup, kind: TemplateKind): boolean {
  const allowed = ALLOWED[kind];
  return !allowed || group.properties.every((p) => allowed.has(p));
}

/** 组内任一属性在 properties 里就算包含。 */
export function groupIncluded(group: TemplateGroup, properties: readonly TemplateProperty[]): boolean {
  return group.properties.some((p) => properties.includes(p));
}

/** 用途的说明：哪些组不可用。 */
export const KIND_LIMIT_TEXT: Record<TemplateKind, string | null> = {
  line: null,
  token: '文本单元用途只能包含填充、描边、阴影、辉光和透明度。',
  composite: '组合用途不能包含位置。',
};

// ── 只读显示 ──

export function cssColor(color: StyleColor): string {
  return `rgb(${color.red} ${color.green} ${color.blue} / ${round(color.alpha / 255, 3)})`;
}

/** 包材质（模板里的带参数，工程样式里的只有身份）。 */
type AnyMaterial = StyleMaterial | { kind: 'package'; packageId: string; exportId: string };
/** 显示用的样式：材质可以是任一种。 */
type AnyStyle = CanonicalStyle<AnyMaterial>;

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

const ALIGN_TEXT = { left: '居左', center: '居中', right: '居右' } as const;
const JOIN_TEXT = { miter: '尖角', round: '圆角', bevel: '斜角' } as const;

/** 一组的取值摘要（不含材质色块，材质另行显示）。 */
export function groupValueText(group: TemplateGroupId, style: AnyStyle, properties?: readonly TemplateProperty[]): string {
  const t = style.typography;
  switch (group) {
    case 'font':
      return [t.fontFamilyName ?? '后端默认字体', ...(t.fallbackFamilyNames.length ? [`回退：${t.fallbackFamilyNames.join('、')}`] : [])].join(' · ');
    case 'size':
      return `${t.fontSizePx} px`;
    case 'weight':
      return [`字重 ${t.fontWeight}`, ...(t.isBold ? ['粗体'] : []), ...(t.isItalic ? ['斜体'] : [])].join(' · ');
    case 'spacing':
      return `${t.letterSpacingPx} px`;
    case 'position':
      return `垂直中心 ${round(style.verticalCenterRatio * 100, 1)}% · ${ALIGN_TEXT[style.horizontalAlignment]}`;
    case 'unsung':
      return materialText(style.unsungFill.material);
    case 'sung':
      return materialText(style.sungFill.material);
    case 'stroke':
      return style.stroke ? `${style.stroke.widthPx} px · ${JOIN_TEXT[style.stroke.join]}连接 · ${materialText(style.stroke.material)}` : '关';
    case 'shadow': {
      const s = style.shadow;
      return s ? `${formatColor(s.color)} · 不透明度 ${round(s.opacity * 100, 1)}% · ${s.angleDegrees}° · 距离 ${s.distancePx} px · 模糊 ${s.blurRadiusPx} px` : '关';
    }
    case 'glow': {
      if (properties && !properties.includes('glowSpec')) {
        const g = templateGlowEditing({ style, properties }).value!;
        const values: [GlowLeafProperty, string][] = [
          ['glowColor', `颜色 ${formatColor(g.color)}`],
          ['glowOpacity', `不透明度 ${round(g.opacity * 100, 1)}%`],
          ['glowSpreadPx', `扩张 ${g.spreadPx} px`],
          ['glowBlurRadiusPx', `模糊 ${g.blurRadiusPx} px`],
        ];
        return `仅覆盖：${values.filter(([property]) => properties.includes(property)).map(([, text]) => text).join(' · ')}`;
      }
      const g = style.glow;
      return g ? `${formatColor(g.color)} · 不透明度 ${round(g.opacity * 100, 1)}% · 扩张 ${g.spreadPx} px · 模糊 ${g.blurRadiusPx} px` : '关';
    }
    case 'opacity':
      return `${round(style.opacity * 100, 1)}%`;
    case 'scale':
      return `×${style.scale}`;
  }
}

/** 组的材质色块（填充、描边、阴影），其余组没有。 */
export function groupSwatch(group: TemplateGroupId, style: AnyStyle): string | undefined {
  switch (group) {
    case 'unsung':
      return materialCss(style.unsungFill.material);
    case 'sung':
      return materialCss(style.sungFill.material);
    case 'stroke':
      return style.stroke ? materialCss(style.stroke.material) : undefined;
    case 'shadow':
      return style.shadow ? cssColor(style.shadow.color) : undefined;
    case 'glow':
      return style.glow ? cssColor(style.glow.color) : undefined;
    default:
      return undefined;
  }
}

/** 属性摘要：包含的组名，用于列表和模板卡片；用途允许的组都包含时写“全部属性”。 */
export function propertySummary(kind: TemplateKind, properties: readonly TemplateProperty[]): string {
  const allowed = TEMPLATE_GROUPS.filter((g) => groupAllowed(g, kind));
  const included = allowed.filter((g) => groupIncluded(g, properties));
  return included.length === allowed.length ? '全部属性' : included.map((g) => g.label).join('、');
}

// ── 编辑（§2.5） ──

/**
 * 打开或关闭一组。关闭移除整组属性；打开时，原定义（base）就包含这组的恢复它原有的子集，
 * 否则写入整组。结果按固定顺序排列。
 */
export function setGroupIncluded<D extends { properties: TemplateProperty[] }>(
  definition: D,
  group: TemplateGroup,
  included: boolean,
  baseProperties: readonly TemplateProperty[],
): D {
  const rest = definition.properties.filter((p) => !group.properties.includes(p));
  const original = group.properties.filter((p) => baseProperties.includes(p));
  const added = included ? (original.length > 0 ? original : group.properties) : [];
  return { ...definition, properties: sortProperties([...rest, ...added]) };
}

/** 换用途：去掉新用途不允许的属性。 */
export function changeKind<D extends { kind: TemplateKind; properties: TemplateProperty[] }>(definition: D, kind: TemplateKind): D {
  const allowed = ALLOWED[kind];
  return { ...definition, kind, properties: definition.properties.filter((p) => !allowed || allowed.has(p)) };
}

export function sortProperties(properties: readonly TemplateProperty[]): TemplateProperty[] {
  return [...new Set(properties)].sort((a, b) => TEMPLATE_PROPERTIES.indexOf(a) - TEMPLATE_PROPERTIES.indexOf(b));
}

export type GlowLeafProperty = 'glowColor' | 'glowOpacity' | 'glowSpreadPx' | 'glowBlurRadiusPx';

/** 仅选择叶属性时，null 样式提供这些默认叶值，并不关闭目标上的辉光。 */
export function templateGlowEditing(definition: { properties: readonly TemplateProperty[]; style: Pick<CanonicalStyle, 'glow'> }): { mode: 'partial' | 'on' | 'off'; value: StyleGlow | null } {
  const glow = definition.style.glow;
  if (definition.properties.includes('glowSpec')) return { mode: glow ? 'on' : 'off', value: glow };
  return { mode: 'partial', value: glow ?? { color: { red: 0, green: 0, blue: 0, alpha: 0 }, opacity: 1, spreadPx: 0, blurRadiusPx: 0 } };
}

/** 保留原模板的属性子集，并纳入刚修改的辉光叶；开关整体时用 glowSpec 表达明确关闭。 */
export function setTemplateGlow(definition: FullTemplateDefinition, glow: StyleGlow | null, property?: GlowLeafProperty): FullTemplateDefinition {
  return {
    ...definition,
    style: { ...definition.style, glow },
    properties: definition.properties.includes('glowSpec') ? definition.properties : sortProperties([...definition.properties, property ?? 'glowSpec']),
  };
}

/** 不能保存的原因；可以保存时返回 undefined。数值范围由各输入框就地校验，这里只查整体约束。 */
export function definitionProblem(definition: FullTemplateDefinition): string | undefined {
  if (!definition.name.trim()) return '名称不能为空。';
  if (definition.properties.length === 0 && definition.effectRecipes.length === 0) return '至少要包含一组属性或一个效果。';
  const fallbacks = definition.style.typography.fallbackFamilyNames;
  if (fallbacks.length > 32) return '回退字体最多 32 个。';
  if (new Set(fallbacks).size !== fallbacks.length) return '回退字体不能重复。';
  // 未包含的组也要是合法的完整取值（规范 §13.1），所以三处材质都查。
  const { style } = definition;
  const materials: [string, AnyMaterial | undefined][] = [
    ['未唱填充', style.unsungFill.material],
    ['已唱填充', style.sungFill.material],
    ['描边', style.stroke?.material],
  ];
  for (const [label, material] of materials) {
    const problem = material?.kind === 'gradient' ? gradientProblem(material.gradient) : undefined;
    if (problem) return `${label}：${problem}`;
  }
  return undefined;
}

/** 描边、阴影、辉光从“关”打开时的初值。 */
export const DEFAULT_STROKE: StyleStroke<StaticMaterial> = { material: { kind: 'solid', color: { red: 0, green: 0, blue: 0, alpha: 255 } }, widthPx: 4, join: 'round', position: 'outside' };
export const DEFAULT_SHADOW: StyleShadow = { color: { red: 0, green: 0, blue: 0, alpha: 255 }, opacity: 0.5, angleDegrees: 45, distancePx: 4, blurRadiusPx: 4 };
export const DEFAULT_GLOW: StyleGlow = { color: { red: 255, green: 255, blue: 255, alpha: 255 }, opacity: 1, spreadPx: 0, blurRadiusPx: 8 };

/** 纯色改为渐变时的初值：同色到半透明的两个色标；其余参数取后端 GradientSpec 的默认值。 */
export function gradientFrom(color: StyleColor): StaticMaterial {
  return {
    kind: 'gradient',
    gradient: {
      kind: 'linear',
      angleDegrees: 0,
      centerX: 0.5,
      centerY: 0.5,
      radiusX: 0.5,
      radiusY: 0.5,
      coordinateSpace: 'line',
      colorSpace: 'linearRgb',
      huePath: 'shorter',
      stops: [
        { offset: 0, color },
        { offset: 1, color: { ...color, alpha: Math.round(color.alpha / 2) } },
      ],
    },
  };
}

/** 渐变改为纯色：取第一个色标。 */
export function solidFrom(material: StaticMaterial): StaticMaterial {
  return material.kind === 'solid' ? material : { kind: 'solid', color: material.gradient.stops[0]!.color };
}

function round(value: number, digits: number): number {
  const f = 10 ** digits;
  return Math.round(value * f) / f;
}
