// 从备份恢复（W5 设计 §2.3）：候选按保存时间倒序、按工程名搜索、本地时间的简短写法。纯函数，不依赖 React。

import type { RecoveryCandidate } from '../../protocol';

/** 按保存时间倒序；query 非空时只留工程名包含它的（不分大小写）。 */
export function sortCandidates(candidates: readonly RecoveryCandidate[], query = ''): RecoveryCandidate[] {
  const q = query.trim().toLocaleLowerCase();
  return candidates
    .filter((c) => !q || c.projectName.toLocaleLowerCase().includes(q))
    .sort((a, b) => Date.parse(b.savedAtUtc) - Date.parse(a.savedAtUtc));
}

const pad = (n: number) => String(n).padStart(2, '0');

/** “今天 14:20”“昨天 09:05”“10月17日 18:26”，不是今年时加年份。时间按本地时区。 */
export function formatSavedAt(savedAtUtc: string, now: Date = new Date()): string {
  const at = new Date(savedAtUtc);
  if (Number.isNaN(at.getTime())) return savedAtUtc;
  const time = `${pad(at.getHours())}:${pad(at.getMinutes())}`;
  const day = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
  const days = Math.round((day(now) - day(at)) / 86_400_000);
  if (days === 0) return `今天 ${time}`;
  if (days === 1) return `昨天 ${time}`;
  const date = `${at.getMonth() + 1}月${at.getDate()}日 ${time}`;
  return at.getFullYear() === now.getFullYear() ? date : `${at.getFullYear()}年${date}`;
}

export function candidateDescription(candidate: RecoveryCandidate, now?: Date): string {
  return `${formatSavedAt(candidate.savedAtUtc, now)} · ${candidate.hasOriginalPath ? '记得原来的保存位置' : '还没有保存位置'}`;
}
