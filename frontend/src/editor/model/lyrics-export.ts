// 导出歌词文件（W5 设计 §1、§2.3）：格式选项与说明、请求的拼装、损失与问题的文字。纯函数，不依赖 React。

import type { LyricsExportFormat, LyricsExportLoss, LyricsExportOptions, LyricsExportPlan, LyricsExportPlanInput, LyricsExportReport } from '../../protocol';

export interface LyricsFormatOption {
  value: LyricsExportFormat;
  label: string;
  description: string;
  extension: string;
}

export const LYRICS_EXPORT_FORMAT_OPTIONS: readonly LyricsFormatOption[] = [
  { value: 'lrc.plain', label: 'LRC（逐行）', description: '只有每行的开始时间，几乎所有播放器都能读。逐字时间和注音不会写出。', extension: 'lrc' },
  { value: 'lrc.enhanced', label: '增强 LRC（逐字）', description: '每行加上逐字时间，支持逐字高亮的播放器能用。注音不会写出。', extension: 'lrc' },
  { value: 'ttml.project', label: 'TTML（整个工程）', description: '保存来源、关联、逐字时间和注音，适合以后再导入本软件；可以只导出一个来源。', extension: 'ttml' },
  { value: 'ttml.amll', label: 'TTML（AMLL）', description: '给 AMLL 等 TTML 歌词播放器用，只导出一个来源。', extension: 'ttml' },
];

export function isLrc(format: LyricsExportFormat): boolean {
  return format === 'lrc.plain' || format === 'lrc.enhanced';
}

/** 标题、歌手、专辑：不能含控制字符和“]”，最多 1024 个字符（只写进 LRC 的标签）。 */
export function checkMetadata(value: string): string | undefined {
  if (value.length > 1024) return '最多 1024 个字符。';
  if (/[\p{Cc}\]]/u.test(value)) return '不能含控制字符或“]”。';
  return undefined;
}

export interface LyricsExportForm {
  format: LyricsExportFormat;
  /** ttml.project 时可以导出整个工程（document）；其他格式只能一个来源。 */
  wholeProject: boolean;
  branchId: string;
  title: string;
  artist: string;
  album: string;
  crlf: boolean;
  bom: boolean;
  millisecondPrecision: boolean;
}

/** 拼出评估请求：整个工程不带 branchId；标题等只给 LRC，空的省略。 */
export function planInput(form: LyricsExportForm): LyricsExportPlanInput {
  const lrc = isLrc(form.format);
  const options: LyricsExportOptions = {
    newLine: form.crlf ? '\r\n' : '\n',
    writeByteOrderMark: form.bom,
    millisecondPrecision: lrc && form.millisecondPrecision,
    ...(lrc && form.title.trim() ? { title: form.title.trim() } : {}),
    ...(lrc && form.artist.trim() ? { artist: form.artist.trim() } : {}),
    ...(lrc && form.album.trim() ? { album: form.album.trim() } : {}),
  };
  if (form.format === 'ttml.project' && form.wholeProject) return { format: form.format, options, scope: 'document' };
  return { format: form.format, options, scope: 'branch', branchId: form.branchId };
}

/** 损失：评估里的加上补充说明（注音、终点、关联、非法时间等），每条“名称（N 处）：说明”。 */
export function lossLines(plan: Pick<LyricsExportPlan, 'plan' | 'additionalLosses'>): string[] {
  return [...plan.plan.losses, ...plan.additionalLosses].map(lossText);
}

/** 补充损失的 feature 是英文键（Core 评估里的已是中文）。 */
const LOSS_FEATURE_TEXT: Record<string, string> = {
  omittedBranches: '其他来源',
  crossBranchPrimaryLineLinks: '跨来源的主歌词关联',
  rubyAnnotations: '注音',
  lineEndTimes: '行终点',
  tokenEndTimes: '字终点',
  primaryLineLinks: '主歌词关联',
  invalidTimeRanges: '无效时间',
};

export function lossText(loss: LyricsExportLoss): string {
  const feature = LOSS_FEATURE_TEXT[loss.feature] ?? loss.feature;
  return `${feature}${loss.affectedCount > 0 ? `（${loss.affectedCount} 处）` : ''}：${loss.message}`;
}

/** 评估摘要：导出哪些、可导出几行、跳过几行、省略几个其他来源。 */
export function planSummary(plan: LyricsExportPlan, branchName: (id: string) => string): string {
  const what = plan.scope === 'document' ? '整个工程' : `「${plan.branchId ? branchName(plan.branchId) : '来源'}」`;
  const skipped = plan.plan.skippedLineCount > 0 ? `，跳过 ${plan.plan.skippedLineCount} 行` : '';
  const omitted = plan.omittedBranchCount > 0 ? `；另外 ${plan.omittedBranchCount} 个来源不在这个文件里` : '';
  return `导出${what}：${plan.plan.exportableLineCount} 行${skipped}${omitted}。`;
}

/** 导出结果的一句话：写了多少行、多少字节，时间被舍入或丢掉的数量。 */
export function reportSummary(report: LyricsExportReport, sizeBytes: number): string {
  const extras = [
    report.skippedLineCount > 0 ? `跳过 ${report.skippedLineCount} 行` : undefined,
    report.droppedTokenTimeCount > 0 ? `丢掉 ${report.droppedTokenTimeCount} 个逐字时间` : undefined,
    report.roundedTimeCount > 0 ? `${report.roundedTimeCount} 个时间被舍入` : undefined,
    report.skippedRubySegmentCount > 0 ? `${report.skippedRubySegmentCount} 段注音没有写出` : undefined,
  ].filter(Boolean);
  return `已导出 ${report.exportedLineCount}/${report.sourceLineCount} 行，${sizeBytes} 字节${extras.length ? `（${extras.join('，')}）` : ''}。`;
}

/** 下载的文件名：“工程名.lrc／.ttml”。响应头里的名字固定是“MakoKara-lyrics.*”，只在工程名为空时用。 */
export function lyricsFileName(headerName: string | null, projectName: string, format: LyricsExportFormat): string {
  const safe = projectName.trim().replace(/[\\/:*?"<>|]+/g, '-');
  if (!safe && headerName) return headerName;
  return `${safe || 'lyrics'}.${LYRICS_EXPORT_FORMAT_OPTIONS.find((o) => o.value === format)?.extension ?? 'txt'}`;
}
