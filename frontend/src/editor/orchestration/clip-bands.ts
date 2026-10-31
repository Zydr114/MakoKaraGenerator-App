// 时间轴片段轨的色块（编排页可编辑，外观页只读，W7 设计 §2.7）：颜色按目标容器区分。

import type { OrchestrationClip, OrchestrationProgram } from '../../protocol';
import type { EditorLine } from '../model/lyrics';
import { clipName, clipTimeSpan, containersOf, type OrchestrationModel } from '../model/orchestration';
import type { TimelineBand } from '../timeline/Timeline';

/** 片段轨的颜色种数，与 Timeline 的调色板和 .band-N 样式一致。 */
export const BAND_COLORS = 4;

/** 容器 → 颜色序号：按来源根下容器的顺序；不在根下的排在后面。 */
export function clipColors(model: OrchestrationModel, program: OrchestrationProgram): Map<string, number> {
  const colors = new Map(containersOf(model, program).map((node, i) => [node.id, i]));
  for (const clip of program.clips ?? []) if (!colors.has(clip.containerId)) colors.set(clip.containerId, colors.size);
  return colors;
}

/** 片段轨的色块：没有时间的片段不画。clips 按 clipsOf 排好序，没有名称的按序号命名。 */
export function clipBands(clips: readonly OrchestrationClip[], lines: readonly EditorLine[], colors: ReadonlyMap<string, number>, selected: ReadonlySet<string>): TimelineBand[] {
  return clips.flatMap((clip, i) => {
    const span = clipTimeSpan(clip, lines);
    return span ? [{ id: clip.id, ...span, label: clipName(clip, i + 1), color: colors.get(clip.containerId) ?? 0, selected: selected.has(clip.id) }] : [];
  });
}
