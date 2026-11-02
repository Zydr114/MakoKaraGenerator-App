// 歌词页行编辑区的“外观”和“显示在”（界面改版 §2.7）：只读，只用内存里已有的分配和编排结构，不为它发请求。
// 不显示槽位和同屏的行：那要在前端重算调度，现有数据给不出。

import type { PresetApplication } from '../../protocol';
import { clipTarget, lineTarget, targetKey } from './appearance-targets';
import type { EditorLine } from './lyrics';
import type { ApplicationIndex } from './preset-applications';
import { ancestors, clipName, clipsOf, lineRange, lineRangeText, nodeName, type OrchestrationModel } from './orchestration';

/** 所选行所在的片段和目标容器；不在片段里时是来源的默认容器。 */
export interface LinePlacement {
  /** 片段名；不在片段里时是默认容器的名称。 */
  title: string;
  detail: string;
  clipId?: string;
  containerId: string;
}

function placementOf(model: OrchestrationModel, line: EditorLine, lines: readonly EditorLine[]) {
  const program = model.programByBranch.get(line.branchId);
  if (!program) return undefined;
  const clips = clipsOf(program, lines);
  const index = clips.findIndex((c) => c.lineIds.includes(line.id));
  return { program, clip: index >= 0 ? clips[index] : undefined, number: index + 1 };
}

const containerName = (model: OrchestrationModel, id: string) => {
  const node = model.byId.get(id);
  return node ? nodeName(model, node) : '已删除的容器';
};

/** “显示在”：“前段”＋“片段 第 1～6 句，容器：前段”；来源没有编排程序时为 undefined。 */
export function linePlacement(model: OrchestrationModel, line: EditorLine, lines: readonly EditorLine[]): LinePlacement | undefined {
  const found = placementOf(model, line, lines);
  if (!found) return undefined;
  const { program, clip, number } = found;
  if (!clip) {
    const id = program.defaultContainerId;
    return { title: containerName(model, id), detail: '不在片段里，用默认容器', containerId: id };
  }
  const range = lineRange(lines, clip.lineIds);
  return {
    title: clipName(clip, number),
    detail: `片段${range ? ` ${lineRangeText(range)}` : ''}，容器：${containerName(model, clip.containerId)}`,
    clipId: clip.id,
    containerId: clip.containerId,
  };
}

/** 所选行生效的模板：最近一层有启用分配的目标。name 为空表示哪一层都没有分配。 */
export interface LineAppearance {
  name?: string;
  source: string;
}

/**
 * “外观”：按后端解析的顺序（工程 → 来源 → 编排节点从根到父 → 片段 → 行）从最近的一层往上找启用的分配。
 * 节点上的分配要“包括里面的内容”才作用到文字。编排结构还没读到时跳过片段和节点两层。
 * 模板只覆盖它包含的属性，各层会叠加；这里只写最近的一层，完整来源在外观页的“外观来源”里看。
 */
export function lineAppearance(
  index: ApplicationIndex,
  model: OrchestrationModel | undefined,
  line: EditorLine,
  lines: readonly EditorLine[],
  branchName: string,
): LineAppearance {
  const enabled = (key: string, filter: (a: PresetApplication) => boolean = () => true) => (index.byTarget.get(key) ?? []).filter((a) => a.enabled && filter(a));
  const layers: Array<[string, PresetApplication[]]> = [['本行直接分配', enabled(targetKey(lineTarget(line)))]];
  const found = model && placementOf(model, line, lines);
  if (model && found) {
    const { program, clip, number } = found;
    if (clip) layers.push([`继承自片段「${clipName(clip, number)}」`, enabled(targetKey(clipTarget(line.branchId, clip.id)))]);
    for (const node of ancestors(model, clip?.containerId ?? program.defaultContainerId)) {
      layers.push([`继承自「${nodeName(model, node)}」`, enabled(`node:${node.id}`, (a) => a.target.includeDescendants === true)]);
    }
  }
  layers.push([`继承自来源「${branchName}」`, enabled(targetKey({ kind: 'branch', branchId: line.branchId }))]);
  layers.push(['继承自工程默认', enabled(targetKey({ kind: 'project' }))]);
  const hit = layers.find(([, apps]) => apps.length > 0);
  if (!hit) return { source: '没有分配模板，用默认样式' };
  return { name: hit[1].map((a) => a.snapshot.name).join('、'), source: hit[0] };
}
