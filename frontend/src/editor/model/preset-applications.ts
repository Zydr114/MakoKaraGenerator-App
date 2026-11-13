// 工程里的模板分配（G15 读模型，W3 设计 §2.6，W7 设计 §2.7）：按目标归组，供目标浏览器标模板名、检查器管理分配。
// 每个分配只有一个目标；同一目标可以有多个分配（后写的覆盖先写的同名属性）。编排节点和片段上的分配也归到各自的对象上。

import type { PresetApplication, PresetApplicationTargetRead, TemplateLibraryId } from '../../protocol';
import { targetKey, type AppearanceTarget } from './appearance-targets';

export interface ApplicationIndex {
  /** 分配 ID → 分配（外观来源按 ID 找）。 */
  byId: ReadonlyMap<string, PresetApplication>;
  /** 目标键（与 targetKey 相同）→ 这个目标上的直接分配，按工程里的顺序。 */
  byTarget: ReadonlyMap<string, readonly PresetApplication[]>;
  /** 顶层目标已不存在的分配：完整保留并参与保存，渲染时跳过；原目标恢复后，启用的分配重新生效，也可以移除。 */
  invalid: readonly PresetApplication[];
  /** 认不出的目标种类上的分配：只读列出。 */
  other: readonly PresetApplication[];
}

/**
 * 读模型目标对应的目标键；失效或认不出时为空。片段目标后端按一个片段一条记录保存，旧数据有多个片段时归到每个片段上。
 */
export function applicationTargetKeys(target: PresetApplicationTargetRead): string[] {
  if (target.kind === 'clips') return [...new Set(target.clipIds ?? [])].map((clipId) => targetKey({ kind: 'clip', branchId: target.branchId ?? '', clipId }));
  const key = applicationTargetKey(target);
  return key === undefined ? [] : [key];
}

/** 读模型目标对应的目标键（片段之外）；失效或认不出时为 undefined。 */
export function applicationTargetKey(target: PresetApplicationTargetRead): string | undefined {
  const { kind, branchId, lineId, tokenId, nodeId } = target;
  if (kind === 'project') return targetKey({ kind: 'project' });
  if (kind === 'node') return nodeId ? targetKey({ kind: 'node', nodeId, includeDescendants: target.includeDescendants }) : undefined;
  if (!branchId) return undefined;
  if (kind === 'branch') return targetKey({ kind: 'branch', branchId });
  if (!lineId) return undefined;
  if (kind === 'line') return targetKey({ kind: 'line', branchId, lineId });
  if (kind === 'token' && tokenId) return targetKey({ kind: 'token', branchId, lineId, tokenId });
  return undefined;
}

export function indexApplications(applications: readonly PresetApplication[]): ApplicationIndex {
  const byTarget = new Map<string, PresetApplication[]>();
  const invalid: PresetApplication[] = [];
  const other: PresetApplication[] = [];
  for (const app of applications) {
    const keys = applicationTargetKeys(app.target);
    if (!app.targetValid) invalid.push(app);
    else if (keys.length === 0) other.push(app);
    else for (const key of keys) byTarget.set(key, [...(byTarget.get(key) ?? []), app]);
  }
  return { byId: new Map(applications.map((a) => [a.applicationId, a])), byTarget, invalid, other };
}

/** 选中目标上的全部直接分配，按目标顺序，同一个分配只出现一次。 */
export function applicationsOn(index: ApplicationIndex, targets: readonly AppearanceTarget[]): PresetApplication[] {
  const result = new Map<string, PresetApplication>();
  for (const t of targets) for (const a of index.byTarget.get(targetKey(t)) ?? []) if (!result.has(a.applicationId)) result.set(a.applicationId, a);
  return [...result.values()];
}

/** 节点上已有分配的应用范围（最后一个分配的）；没有分配时为 undefined。 */
export function nodeScopeOf(index: ApplicationIndex | undefined, nodeId: string): boolean | undefined {
  return index?.byTarget.get(`node:${nodeId}`)?.at(-1)?.target.includeDescendants;
}

/** 分配行上的应用范围说明：只有节点上的分配有。 */
export function scopeText(app: PresetApplication): string | undefined {
  if (app.target.kind !== 'node') return undefined;
  return app.target.includeDescendants ? '包括里面的内容' : '只作用于对象本身';
}

/** 目标浏览器里的模板名：“经典整行”“经典整行（已停用）”，多个用“、”连接。 */
export function applicationNames(applications: readonly PresetApplication[] | undefined): string | undefined {
  if (!applications || applications.length === 0) return undefined;
  return applications.map((a) => (a.enabled ? a.snapshot.name : `${a.snapshot.name}（已停用）`)).join('、');
}

/** 多选时按模板名汇总：[["经典整行", 3], ["逐字强调", 1]]。 */
export function countByTemplate(applications: readonly PresetApplication[]): Array<[string, number]> {
  const counts = new Map<string, number>();
  for (const a of applications) counts.set(a.snapshot.name, (counts.get(a.snapshot.name) ?? 0) + 1);
  return [...counts];
}

const INVALID_TEXT: Record<string, string> = { branch: '已删除的来源', line: '已删除的行', token: '已删除的文本单元', node: '已删除的编排对象', clips: '已删除的片段' };

/** 失效或认不出的目标上的分配，目标写成一句话。 */
export function orphanTargetText(app: PresetApplication): string {
  const { kind } = app.target;
  return (app.targetValid ? undefined : INVALID_TEXT[kind]) ?? `认不出的目标（${kind}）`;
}

/**
 * “更新”用哪个库的当前版本：来源已记录就用它；否则按模板 ID 在两个库里找，只在一个库里找到才算。
 * has(libraryId, templateId) 告诉这个库里现在有没有这个模板。
 */
export function updateSource(app: PresetApplication, has: (libraryId: TemplateLibraryId, templateId: string) => boolean): TemplateLibraryId | undefined {
  const recorded = app.source.libraryId;
  if (recorded) return has(recorded, app.snapshot.templateId) ? recorded : undefined;
  const found = (['personal', 'builtin'] as const).filter((id) => has(id, app.snapshot.templateId));
  return found.length === 1 ? found[0] : undefined;
}
