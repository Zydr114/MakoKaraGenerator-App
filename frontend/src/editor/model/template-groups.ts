// 模板属性分组（W3 设计 §2.5）：界面按组开关“包含在模板中”，每组对应若干属性路径（合同 1.50.0 起 43 项，与 styles.* 共用）。
// 用途限制取协议层的 TOKEN／COMPOSITE 属性表；实际属性语义以后端 StyleBatchEditor 为准。
// 两态：未唱与已唱各有填充、描边、阴影、辉光，已唱的描边、阴影、辉光可跟随未唱；擦除（羽化、高亮）两态共享。

import {
  COMPOSITE_TEMPLATE_PROPERTIES,
  MAX_STROKE_LAYERS,
  TEMPLATE_PROPERTIES,
  TOKEN_TEMPLATE_PROPERTIES,
  type CanonicalStyle,
  type FullTemplateDefinition,
  type FullTemplateMaterial,
  type StaticMaterial,
  type StyleColor,
  type StyleGlow,
  type StyleShadow,
  type StyleStroke,
  type StyleWipeHighlight,
  type TemplateKind,
  type TemplateProperty,
} from '../../protocol';
import { gradientProblem } from './gradient';
import { describeValue } from './style-overrides';
import {
  ALIGN_TEXT,
  cssColor,
  featherText,
  followText,
  followValue,
  glowText,
  highlightText,
  materialCss,
  materialText,
  round,
  shadowText,
  strokesText,
  type AnyMaterial,
} from './style-text';

export { cssColor, materialCss, materialText, PACKAGE_MATERIAL_CSS } from './style-text';

export type TemplateGroupId =
  | 'font'
  | 'size'
  | 'weight'
  | 'spacing'
  | 'position'
  | 'unsung'
  | 'sung'
  | 'stroke'
  | 'sungStroke'
  | 'shadow'
  | 'sungShadow'
  | 'glow'
  | 'sungGlow'
  | 'feather'
  | 'highlight'
  | 'opacity'
  | 'scale';

export interface TemplateGroup {
  id: TemplateGroupId;
  label: string;
  properties: readonly TemplateProperty[];
}

const SHADOW_LEAVES = ['color', 'opacity', 'angleDegrees', 'distancePx', 'blurRadiusPx'] as const;
const GLOW_LEAVES = ['color', 'opacity', 'blurRadiusPx', 'spreadPx'] as const;
const HIGHLIGHT_LEAVES = ['color', 'widthRatio', 'opacity', 'blend'] as const;
const withLeaves = (parent: string, leaves: readonly string[]) => [parent, ...leaves.map((leaf) => `${parent}.${leaf}`)] as TemplateProperty[];

export const TEMPLATE_GROUPS: readonly TemplateGroup[] = [
  { id: 'font', label: '字体', properties: ['typography.fontFamilyName', 'typography.fallbackFamilyNames'] },
  { id: 'size', label: '字号', properties: ['typography.fontSizePx'] },
  { id: 'weight', label: '字重与字形', properties: ['typography.fontWeight', 'typography.isBold', 'typography.isItalic'] },
  { id: 'spacing', label: '字距', properties: ['typography.letterSpacingPx'] },
  { id: 'position', label: '位置', properties: ['verticalCenterRatio', 'horizontalAlignment'] },
  // 按状态排（与模板编辑器的分组一致）：未唱四项，再已唱四项。
  { id: 'unsung', label: '未唱填充', properties: ['unsung.fill'] },
  { id: 'stroke', label: '未唱描边', properties: ['unsung.strokes'] },
  { id: 'shadow', label: '未唱阴影', properties: withLeaves('unsung.shadow', SHADOW_LEAVES) },
  { id: 'glow', label: '未唱辉光', properties: withLeaves('unsung.glow', GLOW_LEAVES) },
  { id: 'sung', label: '已唱填充', properties: ['sung.fill'] },
  { id: 'sungStroke', label: '已唱描边', properties: ['sung.strokes'] },
  { id: 'sungShadow', label: '已唱阴影', properties: withLeaves('sung.shadow', SHADOW_LEAVES) },
  { id: 'sungGlow', label: '已唱辉光', properties: withLeaves('sung.glow', GLOW_LEAVES) },
  { id: 'feather', label: '擦除羽化', properties: ['wipe.featherRatio'] },
  { id: 'highlight', label: '擦除高亮', properties: withLeaves('wipe.highlight', HIGHLIGHT_LEAVES) },
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
  token: '文本单元用途只能包含填充、描边、阴影、辉光、擦除和透明度。',
  composite: '组合用途不能包含位置。',
};

// ── 两态取值 ──

/** 显示用的样式：材质可以是任一种。 */
type AnyStyle = CanonicalStyle<AnyMaterial>;

/** 已唱一侧实际生效的描边：跟随时取未唱一侧。 */
export function sungStrokes<M>(style: CanonicalStyle<M>): StyleStroke<M>[] {
  return followValue(style.sung.strokes, style.unsung.strokes);
}

// ── 只读显示 ──

const LEAF_SHORT: Record<string, string> = {
  color: '颜色',
  opacity: '不透明度',
  angleDegrees: '角度',
  distancePx: '距离',
  blurRadiusPx: '模糊',
  spreadPx: '扩张',
  widthRatio: '宽度',
  blend: '混合',
};

/** 一组的取值摘要（不含材质色块，材质另行显示）。properties 给出时，只含叶的效果组写“仅覆盖：…”。 */
export function groupValueText(group: TemplateGroupId, style: AnyStyle, properties?: readonly TemplateProperty[]): string {
  const t = style.typography;
  if (isEffectGroup(group) && properties) {
    const editing = templateEffectEditing({ style, properties }, group);
    if (editing.mode === 'partial') {
      const parent = EFFECT_PARENT[group];
      const value = editing.value as unknown as Record<string, unknown>;
      const leaves = TEMPLATE_GROUPS.find((g) => g.id === group)!.properties.filter((p) => p !== parent && properties.includes(p));
      return `仅覆盖：${leaves
        .map((p) => {
          const key = p.slice(parent.length + 1);
          return `${group === 'highlight' && key === 'opacity' ? '强度' : LEAF_SHORT[key]} ${describeValue(p, value[key]).text}`;
        })
        .join(' · ')}`;
    }
  }
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
      return materialText(style.unsung.fill);
    case 'sung':
      return materialText(style.sung.fill);
    case 'stroke':
      return strokesText(style.unsung.strokes);
    case 'sungStroke':
      return followText(style.sung.strokes, strokesText);
    case 'shadow':
      return shadowText(style.unsung.shadow);
    case 'sungShadow':
      return followText(style.sung.shadow, shadowText);
    case 'glow':
      return glowText(style.unsung.glow);
    case 'sungGlow':
      return followText(style.sung.glow, glowText);
    case 'feather':
      return featherText(style.wipe.featherRatio);
    case 'highlight':
      return highlightText(style.wipe.highlight);
    case 'opacity':
      return `${round(style.opacity * 100, 1)}%`;
    case 'scale':
      return `×${style.scale}`;
  }
}

/** 组的色块（填充、描边最内层、阴影、辉光、高亮；已唱跟随时取未唱一侧），其余组没有。 */
export function groupSwatch(group: TemplateGroupId, style: AnyStyle): string | undefined {
  switch (group) {
    case 'unsung':
      return materialCss(style.unsung.fill);
    case 'sung':
      return materialCss(style.sung.fill);
    case 'stroke':
    case 'sungStroke': {
      const first = (group === 'stroke' ? style.unsung.strokes : sungStrokes(style))[0];
      return first ? materialCss(first.material) : undefined;
    }
    case 'shadow':
    case 'sungShadow':
    case 'glow':
    case 'sungGlow':
    case 'highlight': {
      const value = effectValue(style, group).value;
      return value ? cssColor(value.color) : undefined;
    }
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

// ── 效果组：阴影、辉光（两态）与擦除高亮，可以整项或只选叶 ──

export type EffectGroupId = 'shadow' | 'sungShadow' | 'glow' | 'sungGlow' | 'highlight';
export type EffectOf<G extends EffectGroupId> = G extends 'shadow' | 'sungShadow' ? StyleShadow : G extends 'glow' | 'sungGlow' ? StyleGlow : StyleWipeHighlight;
type EffectValue = StyleShadow | StyleGlow | StyleWipeHighlight;

const EFFECT_PARENT: Record<EffectGroupId, TemplateProperty> = {
  shadow: 'unsung.shadow',
  sungShadow: 'sung.shadow',
  glow: 'unsung.glow',
  sungGlow: 'sung.glow',
  highlight: 'wipe.highlight',
};

/** 只选叶且样式里是 null 时的基准（后端部分覆盖从透明色合并，见 StyleBatchEditor）；不会关闭目标上的效果。 */
const TRANSPARENT: StyleColor = { red: 0, green: 0, blue: 0, alpha: 0 };
const EFFECT_BASIS: Record<EffectGroupId, EffectValue> = {
  shadow: { color: TRANSPARENT, opacity: 1, angleDegrees: 0, distancePx: 0, blurRadiusPx: 0 },
  sungShadow: { color: TRANSPARENT, opacity: 1, angleDegrees: 0, distancePx: 0, blurRadiusPx: 0 },
  glow: { color: TRANSPARENT, opacity: 1, spreadPx: 0, blurRadiusPx: 0 },
  sungGlow: { color: TRANSPARENT, opacity: 1, spreadPx: 0, blurRadiusPx: 0 },
  highlight: { color: TRANSPARENT, widthRatio: 0.25, opacity: 1, blend: 'screen' },
};

export function isEffectGroup(group: TemplateGroupId): group is EffectGroupId {
  return group in EFFECT_PARENT;
}

/** 已唱一侧的组能“跟随未唱”。 */
export function isSungEffect(group: EffectGroupId): boolean {
  return group === 'sungShadow' || group === 'sungGlow';
}

/** 效果实际生效的值；已唱跟随时取未唱一侧，follow 标出是跟随。 */
function effectValue(style: AnyStyle, group: EffectGroupId): { follow: boolean; value: EffectValue | null } {
  switch (group) {
    case 'shadow':
      return { follow: false, value: style.unsung.shadow };
    case 'glow':
      return { follow: false, value: style.unsung.glow };
    case 'sungShadow':
      return { follow: style.sung.shadow.kind === 'follow', value: followValue(style.sung.shadow, style.unsung.shadow) };
    case 'sungGlow':
      return { follow: style.sung.glow.kind === 'follow', value: followValue(style.sung.glow, style.unsung.glow) };
    case 'highlight':
      return { follow: false, value: style.wipe.highlight };
  }
}

export type EffectMode = 'partial' | 'on' | 'off' | 'follow';

/**
 * 模板里一组效果的编辑状态：包含父项时是整项（开、关，已唱一侧还可跟随）；只含叶时是部分覆盖，
 * 样式里是 null 时用基准提供叶的初值，并不关闭目标上的效果。
 */
export function templateEffectEditing<G extends EffectGroupId>(
  definition: { properties: readonly TemplateProperty[]; style: AnyStyle },
  group: G,
): { mode: EffectMode; value: EffectOf<G> | null } {
  const { follow, value } = effectValue(definition.style, group);
  if (definition.properties.includes(EFFECT_PARENT[group])) return { mode: follow ? 'follow' : value ? 'on' : 'off', value: value as EffectOf<G> | null };
  return { mode: 'partial', value: (value ?? EFFECT_BASIS[group]) as EffectOf<G> };
}

/** 写一组效果：follow 只用于已唱一侧。 */
function writeEffect<D extends { style: CanonicalStyle<M> }, M>(definition: D, group: EffectGroupId, value: EffectValue | null | 'follow'): D {
  const style = definition.style;
  const sung = <T>(v: T | null | 'follow') => (v === 'follow' ? ({ kind: 'follow' } as const) : ({ kind: 'set', value: v } as const));
  switch (group) {
    case 'shadow':
      return { ...definition, style: { ...style, unsung: { ...style.unsung, shadow: value as StyleShadow | null } } };
    case 'glow':
      return { ...definition, style: { ...style, unsung: { ...style.unsung, glow: value as StyleGlow | null } } };
    case 'sungShadow':
      return { ...definition, style: { ...style, sung: { ...style.sung, shadow: sung(value as StyleShadow | null | 'follow') } } };
    case 'sungGlow':
      return { ...definition, style: { ...style, sung: { ...style.sung, glow: sung(value as StyleGlow | null | 'follow') } } };
    case 'highlight':
      return { ...definition, style: { ...style, wipe: { ...style.wipe, highlight: value as StyleWipeHighlight | null } } };
  }
}

/**
 * 改一组效果：整项已包含时只改取值；部分覆盖时保留原子集，并纳入刚改的叶（leaf 为叶名，例如 color）；
 * 开关整项（leaf 省略）时加入父项，用它表达明确关闭或跟随。
 */
export function setTemplateEffect<G extends EffectGroupId>(
  definition: FullTemplateDefinition,
  group: G,
  value: EffectOf<G> | null | 'follow',
  leaf?: keyof EffectOf<G> & string,
): FullTemplateDefinition {
  const parent = EFFECT_PARENT[group];
  const next = writeEffect(definition, group, value);
  if (definition.properties.includes(parent)) return next;
  return { ...next, properties: sortProperties([...definition.properties, leaf ? (`${parent}.${leaf}` as TemplateProperty) : parent]) };
}

// ── 描边层列表：由内到外，最多 4 层；效果包材质只能在最内层 ──

type StrokeLayers = readonly StyleStroke<FullTemplateMaterial>[];
export const PACKAGE_INNER_TEXT = '效果包材质只能用在最内层描边。';

/** 加一层外圈：沿用最外层的宽度、连接方式与静态材质（最外层是包材质或没有层时取默认黑色描边）。满 4 层时原样返回。 */
export function addStrokeLayer(layers: StrokeLayers): StyleStroke<FullTemplateMaterial>[] {
  if (layers.length >= MAX_STROKE_LAYERS) return [...layers];
  const last = layers.at(-1);
  if (!last) return [DEFAULT_STROKE];
  return [...layers, { ...last, material: last.material.kind === 'package' ? DEFAULT_STROKE.material : last.material }];
}

export function removeStrokeLayer(layers: StrokeLayers, index: number): StyleStroke<FullTemplateMaterial>[] {
  return layers.filter((_, i) => i !== index);
}

/** 第 index 层向外（+1）或向内（−1）移一层不能做的原因；能移时返回 undefined。 */
export function strokeMoveProblem(layers: StrokeLayers, index: number, delta: 1 | -1): string | undefined {
  const target = index + delta;
  if (target < 0) return '已经是最内层。';
  if (target >= layers.length) return '已经是最外层。';
  const isPackage = (i: number) => layers[i]!.material.kind === 'package';
  if ((isPackage(index) && target !== 0) || (isPackage(target) && index !== 0)) return PACKAGE_INNER_TEXT;
  return undefined;
}

/** 和相邻一层对调；不能移时原样返回。 */
export function moveStrokeLayer(layers: StrokeLayers, index: number, delta: 1 | -1): StyleStroke<FullTemplateMaterial>[] {
  const next = [...layers];
  if (strokeMoveProblem(layers, index, delta)) return next;
  [next[index], next[index + delta]] = [next[index + delta]!, next[index]!];
  return next;
}

// ── 校验与初值 ──

/** 不能保存的原因；可以保存时返回 undefined。数值范围由各输入框就地校验，这里只查整体约束。 */
export function definitionProblem(definition: FullTemplateDefinition): string | undefined {
  if (!definition.name.trim()) return '名称不能为空。';
  if (definition.properties.length === 0 && definition.effectRecipes.length === 0) return '至少要包含一组属性或一个效果。';
  const fallbacks = definition.style.typography.fallbackFamilyNames;
  if (fallbacks.length > 32) return '回退字体最多 32 个。';
  if (new Set(fallbacks).size !== fallbacks.length) return '回退字体不能重复。';
  // 未包含的组也要是合法的完整取值（规范 §13.1），所以两态的填充与各层描边都查。
  const { style } = definition;
  const sides: [string, StyleStroke<AnyMaterial>[]][] = [
    ['未唱描边', style.unsung.strokes],
    ...(style.sung.strokes.kind === 'set' ? [['已唱描边', style.sung.strokes.value] as [string, StyleStroke<AnyMaterial>[]]] : []),
  ];
  const materials: [string, AnyMaterial][] = [
    ['未唱填充', style.unsung.fill],
    ['已唱填充', style.sung.fill],
  ];
  for (const [label, strokes] of sides) {
    if (strokes.length > MAX_STROKE_LAYERS) return `${label}最多 ${MAX_STROKE_LAYERS} 层。`;
    strokes.forEach((stroke, i) => {
      materials.push([strokes.length > 1 ? `${label}第 ${i + 1} 层` : label, stroke.material]);
    });
    if (strokes.slice(1).some((s) => s.material.kind === 'package')) return `${label}只有最内层能用效果包材质。`;
  }
  for (const [label, material] of materials) {
    const problem = material.kind === 'gradient' ? gradientProblem(material.gradient) : undefined;
    if (problem) return `${label}：${problem}`;
  }
  return undefined;
}

/** 描边、阴影、辉光、高亮从“关”打开时的初值（高亮取后端默认：白色、0.25 倍字号、强度 100%、滤色）。 */
export const DEFAULT_STROKE: StyleStroke<StaticMaterial> = { material: { kind: 'solid', color: { red: 0, green: 0, blue: 0, alpha: 255 } }, widthPx: 4, join: 'round' };
export const DEFAULT_SHADOW: StyleShadow = { color: { red: 0, green: 0, blue: 0, alpha: 255 }, opacity: 0.5, angleDegrees: 45, distancePx: 4, blurRadiusPx: 4 };
export const DEFAULT_GLOW: StyleGlow = { color: { red: 255, green: 255, blue: 255, alpha: 255 }, opacity: 1, spreadPx: 0, blurRadiusPx: 8 };
export const DEFAULT_HIGHLIGHT: StyleWipeHighlight = { color: { red: 255, green: 255, blue: 255, alpha: 255 }, widthRatio: 0.25, opacity: 1, blend: 'screen' };

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
