// 一条历史的批量修改（W6 设计 §2.2）：project.applyEdits 一次提交一批，整批一条历史。
// 一次能带多少条受回执预算限制（64 KiB，实测删除行 300 条可以、400 条 413），按 200 条分批留出余量。

/** 每批最多几条修改。 */
export const EDIT_BATCH_SIZE = 200;

/** 按顺序切成每批最多 size 条；空数组没有批次。 */
export function editBatches<T>(edits: readonly T[], size = EDIT_BATCH_SIZE): T[][] {
  const batches: T[][] = [];
  for (let i = 0; i < edits.length; i += size) batches.push(edits.slice(i, i + size));
  return batches;
}

/** 确认框里的历史说明：一批时“记一条历史，可以一次撤销”，多批时写明批数。 */
export function batchHistoryText(count: number, size = EDIT_BATCH_SIZE): string {
  const batches = Math.ceil(count / size);
  return batches <= 1 ? '记一条历史，可以一次撤销' : `超过 ${size} 条，将分 ${batches} 批提交，每批一条历史`;
}
