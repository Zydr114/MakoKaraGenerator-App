// 四层静态样式的底层读写（合同 1.8.0 起，规范 §6.11）、模板库共用的规范样式形状（§13）与最终静态样式的来源解释（合同 1.37.0，规范 §17.6）。
// 合同 1.50.0 起样式是两态：未唱（unsung）与已唱（sung，描边、阴影、辉光可跟随未唱），另有擦除边缘（wipe）；
// 样式读写、模板属性、解析来源与文字标记共用一张 43 项属性路径表（StylePropertyPath）。
// 用户决定（docs/web-ui-design.md §11）：外观页只做模板分配，不用 styles.* 写命令编辑样式；
// 这两个命令只声明类型，供合同对照。GET .../style 用来只读显示“直接覆盖”。

import type { Revision } from './int64';

/** 43 项属性路径，顺序即 local 与 sources 的输出顺序（OpenAPI StylePropertyPath）。父项（如 unsung.shadow）与其叶不能同批。 */
export const STYLE_PROPERTY_PATHS = [
  'typography.fontFamilyName',
  'typography.fallbackFamilyNames',
  'typography.fontSizePx',
  'typography.fontWeight',
  'typography.isBold',
  'typography.isItalic',
  'typography.letterSpacingPx',
  'unsung.fill',
  'sung.fill',
  'unsung.strokes',
  'sung.strokes',
  'unsung.shadow',
  'unsung.shadow.color',
  'unsung.shadow.opacity',
  'unsung.shadow.angleDegrees',
  'unsung.shadow.distancePx',
  'unsung.shadow.blurRadiusPx',
  'sung.shadow',
  'sung.shadow.color',
  'sung.shadow.opacity',
  'sung.shadow.angleDegrees',
  'sung.shadow.distancePx',
  'sung.shadow.blurRadiusPx',
  'unsung.glow',
  'unsung.glow.color',
  'unsung.glow.opacity',
  'unsung.glow.blurRadiusPx',
  'unsung.glow.spreadPx',
  'sung.glow',
  'sung.glow.color',
  'sung.glow.opacity',
  'sung.glow.blurRadiusPx',
  'sung.glow.spreadPx',
  'wipe.featherRatio',
  'wipe.highlight',
  'wipe.highlight.color',
  'wipe.highlight.widthRatio',
  'wipe.highlight.opacity',
  'wipe.highlight.blend',
  'verticalCenterRatio',
  'horizontalAlignment',
  'opacity',
  'scale',
] as const;
export type StylePropertyPath = (typeof STYLE_PROPERTY_PATHS)[number];

export type StyleTarget =
  | { kind: 'project' }
  | { kind: 'branch'; branchId: string }
  | { kind: 'line'; branchId: string; lineId: string }
  | { kind: 'token'; branchId: string; lineId: string; tokenId: string };

/** 属性路径与对应取值，前端不构造，只原样转交。 */
export interface StyleChange {
  property: StylePropertyPath;
  value: unknown;
}

export type StyleCommand =
  | { type: 'styles.patch'; targets: StyleTarget[]; changes: StyleChange[] }
  | { type: 'styles.restore'; targets: StyleTarget[]; properties: StylePropertyPath[] };

export interface StyleCommandResult {
  changedTargetCount: number;
}

/** 0..255 的整数分量。 */
export interface StyleColor {
  red: number;
  green: number;
  blue: number;
  alpha: number;
}

export interface StyleGradientStop {
  /** 0..1。 */
  offset: number;
  color: StyleColor;
}

export interface StyleGradient {
  kind: 'linear' | 'radial';
  angleDegrees: number;
  /** 径向时 0..1；线性时也必须给出（原样保留）。 */
  centerX: number;
  centerY: number;
  /** 径向时必须大于 0。 */
  radiusX: number;
  radiusY: number;
  /** 1.50.0 起没有 progress。 */
  coordinateSpace: 'canvas' | 'line' | 'glyph';
  colorSpace: 'linearRgb' | 'hsv' | 'oklch';
  huePath: 'shorter' | 'longer' | 'increasing' | 'decreasing';
  /** 2..256 个。 */
  stops: StyleGradientStop[];
}

export type StaticMaterial = { kind: 'solid'; color: StyleColor } | { kind: 'gradient'; gradient: StyleGradient };
/** 包材质只读：只有身份，不能作为新写入的材质回传。 */
export interface PackageMaterial {
  kind: 'package';
  packageId: string;
  revision: string;
  exportId: string;
}
export type StyleMaterial = StaticMaterial | PackageMaterial;

export interface StyleTypography {
  /** null 表示后端默认字体。 */
  fontFamilyName: string | null;
  fallbackFamilyNames: string[];
  fontSizePx: number;
  /** 100..900。 */
  fontWeight: number;
  isBold: boolean;
  isItalic: boolean;
  letterSpacingPx: number;
}

/** 一层描边；1.50.0 起没有 position。 */
export interface StyleStroke<M = StyleMaterial> {
  material: M;
  widthPx: number;
  join: 'miter' | 'round' | 'bevel';
}

/** 一侧最多 4 层描边，第 0 层是最内层；包材质只会出现在第 0 层（读取），HTTP 写入只接受纯色与渐变。 */
export const MAX_STROKE_LAYERS = 4;

export interface StyleShadow {
  color: StyleColor;
  /** 0..1。 */
  opacity: number;
  angleDegrees: number;
  distancePx: number;
  blurRadiusPx: number;
}

export interface StyleGlow {
  color: StyleColor;
  /** 有限值，0..1。 */
  opacity: number;
  /** 有限非负值，逻辑像素。 */
  blurRadiusPx: number;
  /** 有限非负值，逻辑像素。 */
  spreadPx: number;
}

/** 已唱一侧可跟随未唱：follow 不带 value；set 必须带 value。 */
export type StyleFollow<T> = { kind: 'follow' } | { kind: 'set'; value: T };

/** 未唱一侧的外观。shadow／glow 为 null 表示明确关闭；strokes 为空数组表示不描边。 */
export interface StyleAppearance<M = StyleMaterial> {
  fill: M;
  strokes: StyleStroke<M>[];
  shadow: StyleShadow | null;
  glow: StyleGlow | null;
}

/** 已唱一侧的外观：填充必填，描边、阴影、辉光可跟随未唱。set 的 null 表示已唱一侧关闭。 */
export interface StyleSungAppearance<M = StyleMaterial> {
  fill: M;
  strokes: StyleFollow<StyleStroke<M>[]>;
  shadow: StyleFollow<StyleShadow | null>;
  glow: StyleFollow<StyleGlow | null>;
}

export type StyleWipeHighlightBlend = 'normal' | 'screen' | 'add';

/** 擦除前沿的高亮带：作用于填充与描边。 */
export interface StyleWipeHighlight {
  color: StyleColor;
  /** 按字号的比例，大于 0、至多 2。 */
  widthRatio: number;
  /** 0..1。 */
  opacity: number;
  blend: StyleWipeHighlightBlend;
}

/** 擦除边缘：featherRatio 按字号的比例 0..2，0 是硬边；highlight 为 null 表示没有高亮带。 */
export interface StyleWipe {
  featherRatio: number;
  highlight: StyleWipeHighlight | null;
}

export type HorizontalAlignment = 'left' | 'center' | 'right';

/** 完整规范样式（两态）。 */
export interface CanonicalStyle<M = StyleMaterial> {
  typography: StyleTypography;
  unsung: StyleAppearance<M>;
  sung: StyleSungAppearance<M>;
  wipe: StyleWipe;
  /** 0..1。 */
  verticalCenterRatio: number;
  horizontalAlignment: HorizontalAlignment;
  /** 0..1。 */
  opacity: number;
  /** 大于 0。 */
  scale: number;
}

/** GET .../style：baseline→branch→line→token 四层静态解析；不是最终帧外观（不含模板分配与编排上下文）。 */
export interface ProjectStyleResponse {
  projectSessionId: string;
  projectRevision: Revision;
  target: StyleTarget;
  baseline: CanonicalStyle;
  /** 本层真实覆盖；缺少的属性表示继承。project 目标恒为空数组。 */
  local: StyleChange[];
  staticHierarchy: CanonicalStyle;
  /** 只说明工程里有分配（含停用的、不作用于此目标的），不证明这个目标被覆盖。 */
  hasPresetApplications: boolean;
  resolution: 'staticHierarchy';
  protocolVersion: string;
}

/** 最终静态样式所在的上下文：nodePath 从根到 Runtime 的文字叶（最后一个是 textNodeId）。 */
export interface ResolvedStyleContext {
  branchId: string;
  lineId: string;
  tokenId: string | null;
  clipId: string | null;
  containerId: string;
  nodeId: string;
  textNodeId: string;
  nodePath: string[];
  contextKind: 'container' | 'scheduledSlot';
}

/** GET .../resolved-style 的查询：clipId／nodeId 只用来核对归属，不能任填父链。 */
export interface ResolvedStyleQuery {
  branchId: string;
  lineId: string;
  tokenId?: string;
  clipId?: string;
  nodeId?: string;
}

/**
 * 工程、来源、节点、片段、行、文字各层覆盖与模板分配叠加后的静态样式，以及 43 个属性路径各自最后来自哪里（按路径表顺序）。
 * sourcePath 是工程里的持久路径，例如 lyricStyle、lyrics.branches.<b>.lines.<l>.styleOverride、presetApplications.<id>.snapshot.style。
 * 不运行动画、脚本与字体测量，finalPixelAppearance 恒为 false。
 */
export interface ProjectResolvedStyleResponse {
  projectSessionId: string;
  projectRevision: Revision;
  context: ResolvedStyleContext;
  style: CanonicalStyle;
  sources: { property: StylePropertyPath; sourcePath: string }[];
  resolution: 'staticPresetHierarchy';
  finalPixelAppearance: false;
  protocolVersion: '1';
}
