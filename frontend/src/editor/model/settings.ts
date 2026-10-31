// 工程设置（整体设计 §5.6）：画布、帧率、背景色、输出时长的选项、解析与显示。纯函数，不依赖 React。

import type { AnalysisProgress } from '../../protocol';

/** 常用画布尺寸，取值为“宽x高”；首页新建和工程设置共用。 */
export const CANVAS_OPTIONS = [
  { value: '1920x1080', label: '1920 × 1080' },
  { value: '1280x720', label: '1280 × 720' },
  { value: '1080x1920', label: '1080 × 1920（竖屏）' },
] as const;

/** 常用帧率，取值为“分子/分母”。 */
export const FPS_OPTIONS = [
  { value: '30/1', label: '30 fps' },
  { value: '60/1', label: '60 fps' },
  { value: '24/1', label: '24 fps' },
  { value: '25/1', label: '25 fps' },
  { value: '30000/1001', label: '29.97 fps' },
  { value: '60000/1001', label: '59.94 fps' },
  { value: '24000/1001', label: '23.976 fps' },
] as const;

/** 选择框里“自定义…”的取值。 */
export const CUSTOM = 'custom';

export interface Size {
  width: number;
  height: number;
}

export interface Ratio {
  numerator: number;
  denominator: number;
}

export interface Rgba {
  red: number;
  green: number;
  blue: number;
  alpha: number;
}

type Parsed<T> = { ok: true; value: T } | { ok: false; error: string };

/** 画布宽或高：正整数（后端允许奇数）。 */
export function parseDimension(text: string): Parsed<number> {
  const trimmed = text.trim();
  if (!/^\d+$/.test(trimmed) || Number(trimmed) <= 0 || !Number.isSafeInteger(Number(trimmed))) return { ok: false, error: '请填正整数' };
  return { ok: true, value: Number(trimmed) };
}

/** 帧率：整数（30）、小数（29.97，最多三位）或分数（30000/1001）。分母分子都是正整数，约分交给后端。 */
export function parseFrameRate(text: string): Parsed<Ratio> {
  const trimmed = text.trim().replace(/\s*fps$/i, '');
  const fraction = /^(\d+)\s*\/\s*(\d+)$/.exec(trimmed);
  if (fraction) {
    const numerator = Number(fraction[1]);
    const denominator = Number(fraction[2]);
    if (numerator > 0 && denominator > 0 && Number.isSafeInteger(numerator) && Number.isSafeInteger(denominator)) return { ok: true, value: { numerator, denominator } };
    return { ok: false, error: '分子和分母都要是正整数' };
  }
  const decimal = /^(\d+)(?:\.(\d{1,3}))?$/.exec(trimmed);
  if (decimal) {
    const denominator = 10 ** (decimal[2]?.length ?? 0);
    const numerator = Number(decimal[1]) * denominator + Number(decimal[2] ?? 0);
    if (numerator > 0) return { ok: true, value: reduce({ numerator, denominator }) };
  }
  return { ok: false, error: '例如 30、29.97 或 30000/1001' };
}

function reduce(ratio: Ratio): Ratio {
  const gcd = (a: number, b: number): number => (b === 0 ? a : gcd(b, a % b));
  const g = gcd(ratio.numerator, ratio.denominator);
  return { numerator: ratio.numerator / g, denominator: ratio.denominator / g };
}

/** 帧率的显示：整数直接显示，其余显示约三位小数并附分数。 */
export function formatFrameRate(ratio: Ratio): string {
  if (ratio.denominator === 1) return `${ratio.numerator} fps`;
  const decimal = (ratio.numerator / ratio.denominator).toFixed(3).replace(/\.?0+$/, '');
  return `${decimal} fps（${ratio.numerator}/${ratio.denominator}）`;
}

/** 颜色：#RGB、#RRGGBB 或 #RRGGBBAA（大小写不限，# 可省略）。 */
export function parseColor(text: string): Parsed<Rgba> {
  const hex = text.trim().replace(/^#/, '');
  const expanded = /^[0-9a-f]{3}$/i.test(hex) ? [...hex].map((c) => c + c).join('') : hex;
  if (!/^(?:[0-9a-f]{6}|[0-9a-f]{8})$/i.test(expanded)) return { ok: false, error: '例如 #000000 或 #00000080' };
  const byte = (i: number) => parseInt(expanded.slice(i, i + 2), 16);
  return { ok: true, value: { red: byte(0), green: byte(2), blue: byte(4), alpha: expanded.length === 8 ? byte(6) : 255 } };
}

/** #rrggbb；不透明度不是 255 时附上 aa。 */
export function formatColor(color: Rgba): string {
  const hex = (n: number) => n.toString(16).padStart(2, '0');
  return `#${hex(color.red)}${hex(color.green)}${hex(color.blue)}${color.alpha === 255 ? '' : hex(color.alpha)}`;
}

/** 分析进度的一句话：解码中按已完成时长与音频时长估算百分比，阶段名翻译成中文。 */
export function analysisProgressText(progress: AnalysisProgress | undefined, audioDurationUs: number | undefined): { text: string; fraction?: number } {
  if (!progress) return { text: '排队中' };
  const stage = ANALYSIS_STAGE_TEXT[progress.stage] ?? progress.stage;
  if (progress.totalSamples && Number(progress.totalSamples) > 0) {
    return { text: stage, fraction: Math.min(1, Number(progress.completedSamples) / Number(progress.totalSamples)) };
  }
  if (progress.timeUs && audioDurationUs && audioDurationUs > 0) {
    const fraction = Math.min(1, Number(progress.timeUs) / audioDurationUs);
    return { text: `${stage} ${Math.round(fraction * 100)}%`, fraction };
  }
  return { text: stage };
}

const ANALYSIS_STAGE_TEXT: Record<string, string> = {
  'validating-source': '核对音频',
  probing: '读取音频信息',
  decoding: '解码并分析',
  'finalizing-analysis': '整理结果',
  prepared: '整理结果',
  committing: '写入结果',
  completed: '完成',
};
