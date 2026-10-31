// 外观来源（W7 设计 §2.7）：GET .../resolved-style 给出解析后的静态样式和 21 个属性各自最后来自哪里。
// 来源路径是工程里的持久路径，这里认出五种（工程样式、来源／行／文本单元的直接覆盖、分配），写成一句话，并给出点了要选中的目标。

import type { CanonicalStyle, LyricBranchSummary, PresetApplication, ProjectResolvedStyleResponse, TemplateProperty } from '../../protocol';
import { PROJECT_TARGET, type AppearanceTarget } from './appearance-targets';
import type { EditorLine } from './lyrics';
import { clipName, clipsOf, nodePath, type OrchestrationModel } from './orchestration';
import { describeValue } from './style-overrides';
import { TEMPLATE_GROUPS } from './template-groups';

export type StyleSource =
  | { kind: 'project' }
  | { kind: 'branch'; branchId: string }
  | { kind: 'line'; branchId: string; lineId: string }
  | { kind: 'token'; branchId: string; lineId: string; tokenId: string }
  | { kind: 'application'; applicationId: string }
  | { kind: 'unknown'; path: string };

const BRANCH = /^lyrics\.branches\.(.+?)\.styleOverride$/;
const LINE = /^lyrics\.branches\.(.+?)\.lines\.(.+?)\.styleOverride$/;
const TOKEN = /^lyrics\.branches\.(.+?)\.lines\.(.+?)\.tokens\.(.+?)\.styleOverride$/;
const APPLICATION = /^presetApplications\.(.+)\.snapshot\.style$/;

/** 来源路径 → 来源；认不出的原样保留。 */
export function parseSourcePath(path: string): StyleSource {
  if (path === 'lyricStyle') return { kind: 'project' };
  let m = TOKEN.exec(path);
  if (m) return { kind: 'token', branchId: m[1]!, lineId: m[2]!, tokenId: m[3]! };
  m = LINE.exec(path);
  if (m) return { kind: 'line', branchId: m[1]!, lineId: m[2]! };
  m = BRANCH.exec(path);
  if (m) return { kind: 'branch', branchId: m[1]! };
  m = APPLICATION.exec(path);
  if (m) return { kind: 'application', applicationId: m[1]! };
  return { kind: 'unknown', path };
}

/** 说明来源要用到的工程信息。 */
export interface SourceContext {
  branches: readonly LyricBranchSummary[] | undefined;
  linesByBranch: Readonly<Record<string, readonly EditorLine[]>>;
  /** 工程里的全部分配（按 ID 找）。 */
  applications: ReadonlyMap<string, PresetApplication> | undefined;
  model: OrchestrationModel | undefined;
}

export const STYLE_PROPERTY_TEXT: Record<TemplateProperty, string> = {
  fontFamilyName: '字体',
  fallbackFamilyNames: '回退字体',
  fontSizePx: '字号',
  fontWeight: '字重',
  isBold: '粗体',
  isItalic: '斜体',
  letterSpacingPx: '字距',
  verticalCenterRatio: '垂直中心',
  horizontalAlignment: '水平对齐',
  unsungColor: '未唱颜色',
  unsungGradientSpec: '未唱渐变',
  sungColor: '已唱颜色',
  sungGradientSpec: '已唱渐变',
  strokeColor: '描边颜色',
  strokeWidthPx: '描边宽度',
  strokeSpec: '描边设置',
  shadowColor: '阴影颜色',
  shadowBlurPx: '阴影模糊',
  shadowSpec: '阴影设置',
  opacity: '透明度',
  scale: '缩放',
};

function branchText(branchId: string, ctx: SourceContext): string {
  return ctx.branches?.find((b) => b.id === branchId)?.name || branchId;
}

/** 行号（从 1 开始）；找不到时为 undefined。 */
function lineNumber(branchId: string, lineId: string, ctx: SourceContext): number | undefined {
  const index = (ctx.linesByBranch[branchId] ?? []).findIndex((l) => l.id === lineId);
  return index < 0 ? undefined : index + 1;
}

function tokenText(branchId: string, lineId: string, tokenId: string, ctx: SourceContext): string {
  const line = (ctx.linesByBranch[branchId] ?? []).find((l) => l.id === lineId);
  return `「${line?.tokens.find((k) => k.id === tokenId)?.text ?? '?'}」`;
}

function lineText(branchId: string, lineId: string, ctx: SourceContext): string {
  const n = lineNumber(branchId, lineId, ctx);
  return n === undefined ? '已删除的行' : `第 ${n} 行`;
}

/** 分配的目标写成短语：“第 2 行”“第 2 行「光」”“来源“中文””“片段“副歌””“画面 / 常规”。 */
export function applicationTargetText(app: PresetApplication, ctx: SourceContext): string {
  const { kind, branchId, lineId, tokenId, nodeId, clipIds } = app.target;
  if (!app.targetValid) return '目标已不存在';
  if (kind === 'project') return '工程默认';
  if (kind === 'branch' && branchId) return `来源“${branchText(branchId, ctx)}”`;
  if (kind === 'line' && branchId && lineId) return lineText(branchId, lineId, ctx);
  if (kind === 'token' && branchId && lineId && tokenId) return `${lineText(branchId, lineId, ctx)}${tokenText(branchId, lineId, tokenId, ctx)}`;
  if (kind === 'clips' && clipIds?.length) {
    const clips = clipsOf(branchId ? ctx.model?.programByBranch.get(branchId) : undefined, branchId ? (ctx.linesByBranch[branchId] ?? []) : []);
    const names = clipIds.map((id) => {
      const i = clips.findIndex((c) => c.id === id);
      return i < 0 ? '片段' : clipName(clips[i]!, i + 1);
    });
    return `片段“${names.join('、')}”`;
  }
  if (kind === 'node' && nodeId) return ctx.model?.byId.has(nodeId) ? nodePath(ctx.model, nodeId, () => undefined).slice(1).join(' / ') : '编排对象';
  return kind;
}

/** 来源写成一句话（不带“来自”）。 */
export function sourceText(source: StyleSource, ctx: SourceContext): string {
  switch (source.kind) {
    case 'project':
      return '工程样式';
    case 'branch':
      return `来源“${branchText(source.branchId, ctx)}”的直接覆盖`;
    case 'line':
      return `${branchText(source.branchId, ctx)} / ${lineText(source.branchId, source.lineId, ctx)}的直接覆盖`;
    case 'token':
      return `${branchText(source.branchId, ctx)} / ${lineText(source.branchId, source.lineId, ctx)} / ${tokenText(source.branchId, source.lineId, source.tokenId, ctx)}的直接覆盖`;
    case 'application': {
      const app = ctx.applications?.get(source.applicationId);
      return app ? `分配“${app.snapshot.name}”（${applicationTargetText(app, ctx)}）` : '一个分配（还没读到）';
    }
    case 'unknown':
      return source.path;
  }
}

/** 分配读模型的目标 → 外观页目标；失效或认不出时为 undefined。 */
export function applicationTarget(app: PresetApplication): AppearanceTarget | undefined {
  const { kind, branchId, lineId, tokenId, nodeId, clipIds } = app.target;
  if (!app.targetValid) return undefined;
  if (kind === 'project') return PROJECT_TARGET;
  if (kind === 'node' && nodeId) return { kind: 'node', nodeId, includeDescendants: app.target.includeDescendants };
  if (!branchId) return undefined;
  if (kind === 'branch') return { kind: 'branch', branchId };
  if (kind === 'clips' && clipIds?.[0]) return { kind: 'clip', branchId, clipId: clipIds[0] };
  if (!lineId) return undefined;
  if (kind === 'line') return { kind: 'line', branchId, lineId };
  if (kind === 'token' && tokenId) return { kind: 'token', branchId, lineId, tokenId };
  return undefined;
}

/** 点来源要选中的目标：直接覆盖选中它所在的那一层，分配选中它的目标；工程样式选中工程默认。 */
export function sourceTarget(source: StyleSource, ctx: SourceContext): AppearanceTarget | undefined {
  switch (source.kind) {
    case 'project':
      return PROJECT_TARGET;
    case 'branch':
    case 'line':
    case 'token':
      return source;
    case 'application': {
      const app = ctx.applications?.get(source.applicationId);
      return app ? applicationTarget(app) : undefined;
    }
    case 'unknown':
      return undefined;
  }
}

export interface SourceRow {
  /** 属性名；组里所有属性来源相同时只有一行，property 为 undefined。 */
  property?: TemplateProperty;
  path: string;
  source: StyleSource;
}

export interface SourceGroup {
  id: string;
  label: string;
  /** 解析后的取值摘要。 */
  value: { text: string; swatch?: string };
  rows: SourceRow[];
}

/**
 * 按 W3 的属性分组整理来源：组里各属性来源相同时合成一行，不同时逐个属性列出。
 * 取值摘要按解析后的样式写（字体、字号、填充、描边等），组里没有一个属性出现在来源里时整组不列。
 */
export function sourceGroups(response: Pick<ProjectResolvedStyleResponse, 'style' | 'sources'>): SourceGroup[] {
  const byProperty = new Map(response.sources.map((s) => [s.property, s.sourcePath]));
  return TEMPLATE_GROUPS.flatMap((group): SourceGroup[] => {
    const items = group.properties.flatMap((property) => {
      const path = byProperty.get(property);
      return path === undefined ? [] : [{ property, path, source: parseSourcePath(path) }];
    });
    if (items.length === 0) return [];
    const same = items.every((i) => i.path === items[0]!.path);
    return [{ id: group.id, label: group.label, value: groupValue(group.id, response.style), rows: same ? [{ path: items[0]!.path, source: items[0]!.source }] : items }];
  });
}

function groupValue(id: string, style: CanonicalStyle): { text: string; swatch?: string } {
  const t = style.typography;
  switch (id) {
    case 'font':
      return { text: [t.fontFamilyName ?? '后端默认字体', ...t.fallbackFamilyNames].join('、') };
    case 'size':
      return describeValue('typography.fontSizePx', t.fontSizePx);
    case 'weight':
      return { text: [String(t.fontWeight), t.isBold ? '粗体' : '', t.isItalic ? '斜体' : ''].filter(Boolean).join(' · ') };
    case 'spacing':
      return describeValue('typography.letterSpacingPx', t.letterSpacingPx);
    case 'position':
      return { text: `垂直中心 ${describeValue('verticalCenterRatio', style.verticalCenterRatio).text} · ${describeValue('horizontalAlignment', style.horizontalAlignment).text}` };
    case 'unsung':
      return describeValue('unsungFill.material', style.unsungFill.material);
    case 'sung':
      return describeValue('sungFill.material', style.sungFill.material);
    case 'stroke':
      return describeValue('stroke', style.stroke);
    case 'shadow':
      return describeValue('shadow', style.shadow);
    case 'opacity':
      return describeValue('opacity', style.opacity);
    case 'scale':
      return describeValue('scale', style.scale);
    default:
      return { text: '' };
  }
}
