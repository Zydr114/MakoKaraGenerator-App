// 界面配色（界面改版 §2.2）：由种子色、明暗和强度推出全部颜色角色。纯函数，不碰 DOM。
// M3 的标准角色直接取 material-color-utilities 的方案；设计稿的自定义角色按下面的色调表从色调板取值，
// 色调表 2026-11-01 对照设计稿 desk-stage.html（种子 #019BFE）校准。

import { Hct, SchemeTonalSpot, SchemeVibrant, TonalPalette, argbFromHex, hexFromArgb, type DynamicScheme } from '@material/material-color-utilities';

/** 默认种子色：用 QuantizerCelebi 加 Score 从 assets/logo/logo-icon.png 取出（用户 2026-11-01 选定）。 */
export const DEFAULT_SEED = '#019BFE';

export interface SchemeInput {
  /** #RRGGBB */
  seed: string;
  dark: boolean;
  /** 色彩强度：false 柔和（Tonal Spot），true 鲜明（Vibrant）。 */
  vivid: boolean;
}

/** M3 标准角色：CSS 变量名 → 方案上的取值。 */
const STANDARD = {
  primary: (s) => s.primary,
  'on-primary': (s) => s.onPrimary,
  'primary-container': (s) => s.primaryContainer,
  'on-primary-container': (s) => s.onPrimaryContainer,
  secondary: (s) => s.secondary,
  'on-secondary': (s) => s.onSecondary,
  'secondary-container': (s) => s.secondaryContainer,
  'on-secondary-container': (s) => s.onSecondaryContainer,
  tertiary: (s) => s.tertiary,
  'on-tertiary': (s) => s.onTertiary,
  'tertiary-container': (s) => s.tertiaryContainer,
  'on-tertiary-container': (s) => s.onTertiaryContainer,
  error: (s) => s.error,
  'on-error': (s) => s.onError,
  'error-container': (s) => s.errorContainer,
  'on-error-container': (s) => s.onErrorContainer,
  surface: (s) => s.surface,
  'surface-low': (s) => s.surfaceContainerLow,
  'surface-high': (s) => s.surfaceContainerHigh,
  'surface-highest': (s) => s.surfaceContainerHighest,
  'on-surface': (s) => s.onSurface,
  'on-surface-variant': (s) => s.onSurfaceVariant,
  outline: (s) => s.outline,
  'outline-variant': (s) => s.outlineVariant,
  'inverse-surface': (s) => s.inverseSurface,
  'inverse-on-surface': (s) => s.inverseOnSurface,
  'inverse-primary': (s) => s.inversePrimary,
} satisfies Record<string, (scheme: DynamicScheme) => number>;

/** 自定义角色取自哪块色调板。 */
interface Palettes {
  primary: TonalPalette;
  tertiary: TonalPalette;
  neutral: TonalPalette;
  /** 幕后：第三色色相，低彩度。 */
  stage: TonalPalette;
  /** 片段二：第三色色相再转 80°。 */
  clip2: TonalPalette;
  clip2Bar: TonalPalette;
  ok: TonalPalette;
  okContainer: TonalPalette;
  warn: TonalPalette;
  warnContainer: TonalPalette;
}

/** 自定义角色：[色调板, 浅色色调, 深色色调]。 */
const CUSTOM = {
  // 面板与台面：浅色时面板最亮、台面在下；深色时反过来，台面最暗。
  panel: ['neutral', 100, 12],
  desk: ['neutral', 94, 4],
  field: ['neutral', 95, 19],
  line: ['neutral', 90, 18],
  dialog: ['neutral', 96, 17],
  'dialog-field': ['neutral', 100, 24],
  stage: ['stage', 91, 8],
  'stage-high': ['stage', 97, 17],
  'stage-line': ['stage', 84, 22],
  'stage-ink2': ['stage', 35, 74],
  'stage-ink3': ['stage', 54, 54],
  wave: ['stage', 70, 33],
  'wave-on': ['tertiary', 48, 67],
  clip2: ['clip2', 88, 25],
  'clip2-bar': ['clip2Bar', 46, 73],
  // 画面上的选中框：画面是工程内容，始终偏暗，两种模式都用亮的主色。
  'frame-sel': ['primary', 82, 82],
  'frame-sel-ink': ['primary', 19, 19],
  ok: ['ok', 46, 78],
  'ok-container': ['okContainer', 93, 22],
  warn: ['warn', 48, 80],
  'warn-container': ['warnContainer', 94, 20],
} satisfies Record<string, readonly [keyof Palettes, number, number]>;

export type ColorRole = keyof typeof STANDARD | keyof typeof CUSTOM;
export type ColorRoles = Record<ColorRole, string>;

export const STANDARD_ROLES = Object.keys(STANDARD) as (keyof typeof STANDARD)[];

/** 解析 #RRGGBB（大小写都行，可省略 #）；不合法返回 undefined。结果是大写的 #RRGGBB。 */
export function normalizeSeed(text: string): string | undefined {
  const match = /^#?([0-9a-f]{6})$/i.exec(text.trim());
  return match ? `#${match[1]!.toUpperCase()}` : undefined;
}

export function createScheme(input: SchemeInput): DynamicScheme {
  const source = Hct.fromInt(argbFromHex(normalizeSeed(input.seed) ?? DEFAULT_SEED));
  return input.vivid ? new SchemeVibrant(source, input.dark, 0) : new SchemeTonalSpot(source, input.dark, 0);
}

export function buildScheme(input: SchemeInput): ColorRoles {
  const scheme = createScheme(input);
  const tertiary = scheme.tertiaryPalette;
  const palettes: Palettes = {
    primary: scheme.primaryPalette,
    tertiary,
    neutral: scheme.neutralPalette,
    stage: TonalPalette.fromHueAndChroma(tertiary.hue, tertiary.chroma * 0.42),
    clip2: TonalPalette.fromHueAndChroma((tertiary.hue + 80) % 360, tertiary.chroma * 0.75),
    clip2Bar: TonalPalette.fromHueAndChroma((tertiary.hue + 80) % 360, tertiary.chroma * 1.6),
    ok: TonalPalette.fromHueAndChroma(150, 46),
    okContainer: TonalPalette.fromHueAndChroma(150, 18),
    warn: TonalPalette.fromHueAndChroma(68, 48),
    warnContainer: TonalPalette.fromHueAndChroma(78, 16),
  };
  const roles = {} as ColorRoles;
  for (const role of STANDARD_ROLES) roles[role] = hexFromArgb(STANDARD[role](scheme));
  for (const [role, [palette, light, dark]] of Object.entries(CUSTOM)) {
    roles[role as keyof typeof CUSTOM] = hexFromArgb(palettes[palette].tone(input.dark ? dark : light));
  }
  return roles;
}

/**
 * 写到 `<html>` 上的变量串：每个角色一个颜色变量，另加一份 `--角色-rgb`（逗号分隔的三元组），
 * 给 canvas 和要叠透明度的地方用。
 */
export function schemeCss(roles: ColorRoles): string {
  let css = '';
  for (const [role, hex] of Object.entries(roles)) {
    const n = Number.parseInt(hex.slice(1), 16);
    css += `--${role}:${hex};--${role}-rgb:${(n >> 16) & 255}, ${(n >> 8) & 255}, ${n & 255};`;
  }
  return css;
}
