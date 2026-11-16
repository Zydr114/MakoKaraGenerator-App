// 添加歌词（整体设计 §5.2）：导入文件与粘贴文本的选项、导入规划的识别结果、导入报告的整理。纯函数，不依赖 React。

import type { LyricBranchRole, LyricsImportIssue, LyricsImportIssueKind, LyricsImportIssueSeverity, LyricsImportPlan, LyricsImportReport } from '../../protocol';
import { BRANCH_ROLE_TEXT } from './branches';
import { formatTime } from './time';

/** 文件选择器接受的扩展名（与后端 LyricsFormatCatalog.PickerExtensions 一致）。 */
export const LYRICS_FILE_ACCEPT = '.lrc,.elrc,.txt,.srt,.vtt,.ass,.ssa,.ttml,.xml,text/plain';

/** 空字符串表示自动识别（发送 null）。导入器 ID 是后端固定的八个。 */
export const IMPORTER_OPTIONS: readonly { value: string; label: string }[] = [
  { value: '', label: '自动识别' },
  { value: 'lrc.enhanced', label: '增强 LRC（含逐字时间）' },
  { value: 'lrc.plain', label: '普通 LRC（仅行级时间）' },
  { value: 'text.plain', label: '纯文本（未打轴）' },
  { value: 'srt', label: 'SRT 字幕（仅行级时间）' },
  { value: 'vtt', label: 'WebVTT 字幕（可含逐字）' },
  { value: 'ass', label: 'ASS / SSA 卡拉 OK 字幕（可含逐字）' },
  { value: 'ttml', label: 'TTML 逐字歌词（Apple Music / AMLL）' },
  { value: 'ustar', label: 'UltraStar 卡拉 OK（逐字 + 音高）' },
];

/** 空字符串表示自动识别。值是 .NET 编码名，后端不认识时按自动识别处理。 */
export const ENCODING_OPTIONS: readonly { value: string; label: string }[] = [
  { value: '', label: '自动识别' },
  { value: 'utf-8', label: 'UTF-8' },
  { value: 'gb18030', label: 'GB18030（简体中文）' },
  { value: 'big5', label: 'Big5（繁体中文）' },
  { value: 'shift_jis', label: 'Shift_JIS（日文）' },
  { value: 'euc-kr', label: 'EUC-KR（韩文）' },
  { value: 'utf-16', label: 'UTF-16 LE' },
  { value: 'utf-16BE', label: 'UTF-16 BE' },
];

export const NEW_BRANCH_ROLES = ['translation', 'annotation', 'custom'] as const;
export type NewBranchRole = (typeof NEW_BRANCH_ROLES)[number];

/** 新来源的默认名称：文件名去掉扩展名。 */
export function fileStem(fileName: string): string {
  const dot = fileName.lastIndexOf('.');
  return (dot > 0 ? fileName.slice(0, dot) : fileName).trim();
}

/** 粘贴文本会追加的行数，规则与后端一致：统一换行后跳过空白行。 */
export function pastedLineCount(text: string): number {
  return text.split(/\r\n|\r|\n/).filter((row) => row.trim() !== '').length;
}

// ── 导入规划（W5 设计 §2.6，W6 设计 §2.3）：新建工程与导入文件两个对话框共用 ──

/** 规划用在哪里：新建工程会恢复多来源文件的全部来源，导入文件一次只导入一个来源。 */
export type PlanPurpose = 'create' | 'import';

const ENCODING_LABEL = new Map(ENCODING_OPTIONS.map((o) => [o.value.toLowerCase(), o.label]));

export function encodingLabel(name: string): string {
  return ENCODING_LABEL.get(name.toLowerCase()) ?? name;
}

/** 识别结果的一行：“识别为：增强 LRC（含逐字时间） · UTF-8 · 12 行 · 逐字时间 98/100”；多来源时行数写全部来源的合计。解析失败时为 undefined。 */
export function planSummary(plan: LyricsImportPlan): string | undefined {
  const report = plan.report;
  if (plan.preparationError || !report) return undefined;
  const sources = plan.sourceBranches.length;
  const lines = sources > 1 ? `${sources} 个来源共 ${report.lineCount} 行` : `${report.lineCount} 行`;
  const parts = [`识别为：${report.importerName}`, encodingLabel(report.encodingName), lines];
  if (report.tokenCount > 0) parts.push(report.timedTokenCount === 0 ? '没有逐字时间' : `逐字时间 ${report.timedTokenCount}/${report.tokenCount}`);
  return parts.join(' · ');
}

/**
 * 需要用户留意的地方：格式或编码不确定、文件里有多个来源、跳过的行。
 * 多个来源可以是工程 TTML，也可以是带歌唱者或旁轨的标准 TTML。
 * 新建时全部恢复；导入到已有工程时默认仍选一个来源，另有一次导入全部来源的入口。
 */
export function planNotes(plan: LyricsImportPlan, purpose: PlanPurpose): string[] {
  const notes: string[] = [];
  if (plan.preparationError) return notes;
  if (plan.isFormatAmbiguous && plan.formatCandidates.length > 1) notes.push(`格式不确定：${plan.detectionReason ?? '有多种可能'}。不对时请在下面改选。`);
  if (plan.isEncodingAmbiguous) notes.push('编码不确定：预览里有乱码时请改选编码。');
  const sources = plan.sourceBranches.length;
  if (sources > 1) {
    notes.push(
      purpose === 'create'
        ? `文件有 ${sources} 个来源，新建时会全部恢复。`
        : `文件有 ${sources} 个来源。可以在下面选择一个来源，或一次导入全部来源（不覆盖已有来源，记一条撤销）。`,
    );
  }
  if (plan.report && plan.report.skippedLineCount > 0) notes.push(`跳过了 ${plan.report.skippedLineCount} 行（空行或无法识别的行）。`);
  return notes;
}

export interface Choice {
  value: string;
  label: string;
}

/** “自动识别”的选项值。不用空字符串：下拉框里空值表示没有选择。 */
export const AUTO = 'auto';

/** 选项值转成请求里的值：自动识别时省略。 */
export function chosen(value: string): string | undefined {
  return value === AUTO || value === '' ? undefined : value;
}

const AUTO_CHOICE: Choice = { value: AUTO, label: '自动识别' };

/** 格式候选：识别出的候选；解析失败（没有候选）时给全部导入器。第一项总是“自动识别”。 */
export function formatChoices(plan: LyricsImportPlan | undefined): Choice[] {
  const candidates = plan?.formatCandidates ?? [];
  if (candidates.length === 0) return [AUTO_CHOICE, ...IMPORTER_OPTIONS.filter((o) => o.value)];
  return [AUTO_CHOICE, ...candidates.map((c) => ({ value: c.importerId, label: c.name }))];
}

/** 编码候选：后端给的候选名；没有时用常用编码。第一项总是“自动识别”。 */
export function encodingChoices(plan: LyricsImportPlan | undefined): Choice[] {
  const candidates = plan?.encodingCandidates ?? [];
  if (candidates.length === 0) return [AUTO_CHOICE, ...ENCODING_OPTIONS.filter((o) => o.value)];
  return [AUTO_CHOICE, ...candidates.map((c) => ({ value: c.name, label: encodingLabel(c.name) }))];
}

/** 新建工程时只在识别有疑问或失败时给出格式、编码改选；导入文件对话框总是给出。 */
export function needsFormatChoice(plan: LyricsImportPlan): boolean {
  return formatChoices(plan).length > 2 || !!plan.preparationError || plan.isFormatAmbiguous || plan.isEncodingAmbiguous;
}

/** 来源候选：名称、角色和行数。只有一个来源时不需要选。 */
export function sourceChoices(plan: LyricsImportPlan): Choice[] {
  return plan.sourceBranches.map((b) => {
    const role = BRANCH_ROLE_TEXT[b.role as LyricBranchRole] ?? b.role;
    return { value: b.id, label: `${b.name}（${role}，${b.lineCount} 行）` };
  });
}

/**
 * 解析失败的说明。有些导入器（如 TTML）解析出错时不抛错，只在报告里记一条错误、得到空文档，
 * 后端因此报“没有可导入的行”；这时用报告里第一条错误说明原因。
 */
export function planErrorText(plan: LyricsImportPlan): string | undefined {
  const error = plan.preparationError;
  if (!error) return undefined;
  if (error.failureCode === 'UnsupportedLyricsFormat') return '无法识别这个文件的歌词格式。可以改选格式或编码再试，或换一个文件。';
  if (error.failureCode === 'EmptyImport') {
    const cause = plan.report?.issues.find((issue) => issue.severity === 'error');
    return cause ? `文件解析失败：${cause.message}` : '文件里没有可以导入的歌词行。';
  }
  return error.message;
}

/**
 * 导入到主歌词时不能带关联主歌词的行（后端拒绝）。预览行就是所选来源的全部行，可以提前判断。
 * 能导入时返回 undefined。
 */
export function primaryImportBlock(plan: LyricsImportPlan, intoPrimary: boolean): string | undefined {
  if (!intoPrimary || !plan.previewLines.some((line) => line.primaryLineId)) return undefined;
  return '这个来源的行关联着主歌词，不能导入到主歌词。请作为新来源导入，或替换一个翻译来源。';
}

export interface PreviewRow {
  id: string;
  text: string;
  /** 注音：“朝（あさ）”，多个片段用“·”隔开；没有注音时省略。 */
  ruby?: string;
  /** 时间：逐字时间、部分逐字时间、只有行时间、没有时间。 */
  timing: string;
  /** 行的开始时间（m:ss.mmm）；没有时间时省略。 */
  start?: string;
}

const timed = (time: { startUs?: string; endUs?: string }) => time.startUs !== undefined && time.endUs !== undefined;

/** 预览的前几行：正文、注音和是否有逐字时间。 */
export function previewRows(plan: LyricsImportPlan, limit: number): PreviewRow[] {
  return plan.previewLines.slice(0, limit).map((line) => {
    const timedTokens = line.tokens.filter((t) => timed(t.time)).length;
    const timing =
      line.tokens.length > 0 && timedTokens === line.tokens.length
        ? '逐字时间'
        : timedTokens > 0
          ? `逐字时间 ${timedTokens}/${line.tokens.length}`
          : timed(line.time)
            ? '行时间'
            : '没有时间';
    const ruby = line.rubyAnnotations
      .map((r) => {
        const base = line.tokens.find((t) => t.id === r.baseTokenId)?.text ?? '';
        const text = r.segments.length > 1 ? r.segments.map((s) => s.text).join('·') : r.text;
        return `${base}（${text}）`;
      })
      .join(' ');
    const start = line.time.startUs ?? line.tokens.find((t) => t.time.startUs !== undefined)?.time.startUs;
    return { id: line.id, text: line.text, timing, ...(ruby ? { ruby } : {}), ...(start !== undefined ? { start: formatTime(Number(start)) } : {}) };
  });
}

export interface PlanStats {
  lines: number;
  /** 至少有一个文本单元带时间的行。 */
  wordTimed: number;
  /** 只有行时间的行。 */
  lineTimed: number;
  untimed: number;
}

/** 新建工程文件检查里的统计：按所选来源的行数，带逐字时间、只有行时间、没有时间各几行。解析失败时为 undefined。 */
export function planStats(plan: LyricsImportPlan): PlanStats | undefined {
  if (plan.preparationError || !plan.report) return undefined;
  const stats: PlanStats = { lines: plan.previewLines.length, wordTimed: 0, lineTimed: 0, untimed: 0 };
  for (const line of plan.previewLines) {
    if (line.tokens.some((t) => timed(t.time))) stats.wordTimed++;
    else if (timed(line.time)) stats.lineTimed++;
    else stats.untimed++;
  }
  return stats;
}

// ── 导入报告 ──

export const SEVERITY_TEXT: Record<LyricsImportIssueSeverity, string> = { error: '错误', warning: '警告', info: '提示' };

export const ISSUE_KIND_TEXT: Record<LyricsImportIssueKind, string> = {
  unknownTag: '未知标签',
  malformedTag: '标签格式错误',
  textWithoutTimestamp: '没有时间标签的文字',
  emptyText: '空歌词行',
  duplicateTimestamp: '重复的时间',
  outOfOrder: '时间倒序',
  negativeTimeClamped: '负时间已改为 0',
  wordTimeOutsideLine: '逐字时间超出本行',
  wordTimesNotIncreasing: '逐字时间没有递增',
  encodingAssumed: '编码是推测的',
  lineEndFallback: '行尾时间是推定的',
  discardedFeature: '丢弃了不支持的内容',
  formatAmbiguous: '格式有歧义',
  tokenSplitApplied: '导入后重新切分',
  tokenTextMismatch: '逐字文字与行文字不一致',
};

const NEW_LINE_TEXT: Record<LyricsImportReport['newLineKind'], string> = {
  none: '无换行',
  lineFeed: 'LF',
  carriageReturnLineFeed: 'CRLF',
  carriageReturn: 'CR',
  mixed: '混合',
};

const SEVERITY_ORDER: Record<LyricsImportIssueSeverity, number> = { error: 0, warning: 1, info: 2 };

export function issueCounts(issues: readonly LyricsImportIssue[]): Record<LyricsImportIssueSeverity, number> {
  const counts = { error: 0, warning: 0, info: 0 };
  for (const issue of issues) counts[issue.severity]++;
  return counts;
}

/** 按严重程度筛选；源文件行号的顺序保持后端给出的顺序。 */
export function filterIssues(issues: readonly LyricsImportIssue[], severity: LyricsImportIssueSeverity | 'all'): LyricsImportIssue[] {
  return severity === 'all' ? [...issues] : issues.filter((i) => i.severity === severity);
}

export function worstSeverity(a: LyricsImportIssueSeverity | undefined, b: LyricsImportIssueSeverity): LyricsImportIssueSeverity {
  return a === undefined || SEVERITY_ORDER[b] < SEVERITY_ORDER[a] ? b : a;
}

export interface ReportFact {
  label: string;
  value: string;
}

/** 报告概要：格式、编码、行与文本单元的数量，只列出有内容的项。 */
export function reportFacts(report: LyricsImportReport): ReportFact[] {
  const facts: ReportFact[] = [
    { label: '格式', value: report.importerName + (report.isFormatAmbiguous ? '（有歧义）' : '') },
  ];
  if (report.detectionReason) facts.push({ label: '识别依据', value: report.detectionReason });
  if (report.isFormatAmbiguous && report.candidateNames.length > 1) facts.push({ label: '候选格式', value: report.candidateNames.join('、') });
  facts.push({
    label: '编码',
    value: `${report.encodingName}${report.hadByteOrderMark ? '（有 BOM）' : ''} · 换行 ${NEW_LINE_TEXT[report.newLineKind]}`,
  });
  const skipped = report.skippedLineCount > 0 ? `，跳过 ${report.skippedLineCount} 行` : '';
  facts.push({ label: '行数', value: `源文件 ${report.sourceLineCount} 行 → 歌词 ${report.lineCount} 行${skipped}` });
  const tokenParts = [`有时间 ${report.timedTokenCount}`, `缺时间 ${report.missingTokenCount}`];
  if (report.estimatedTokenCount > 0) tokenParts.push(`估算 ${report.estimatedTokenCount}`);
  facts.push({ label: '文本单元', value: `${report.tokenCount} 个：${tokenParts.join('，')}` });
  const lineParts: string[] = [];
  if (report.mergedContinuationCount > 0) lineParts.push(`合并续行 ${report.mergedContinuationCount}`);
  if (report.derivedLineEndCount > 0) lineParts.push(`由下一行推出行尾 ${report.derivedLineEndCount}`);
  if (report.fallbackLineEndCount > 0) lineParts.push(`行尾用默认时长 ${report.fallbackLineEndCount}`);
  if (lineParts.length > 0) facts.push({ label: '行处理', value: lineParts.join('，') });
  if (report.offsetUs !== '0') facts.push({ label: '时间偏移', value: `${formatOffset(report.offsetUs)} 秒（文件里的 offset 已应用）` });
  if (report.splitLineCount > 0) {
    const estimated = report.splitEstimatedTokenCount > 0 ? `，估算 ${report.splitEstimatedTokenCount} 个时间` : '';
    facts.push({ label: '重新切分', value: `${report.splitLineCount} 行，新建 ${report.splitCreatedTokenCount} 个文本单元${estimated}` });
  }
  if (report.discardedFeatures.length > 0) facts.push({ label: '丢弃的内容', value: report.discardedFeatures.join('、') });
  return facts;
}

/** 微秒偏移（十进制字符串）转成带符号的秒，最多三位小数。 */
function formatOffset(offsetUs: string): string {
  const negative = offsetUs.startsWith('-');
  const digits = (negative ? offsetUs.slice(1) : offsetUs).padStart(7, '0');
  const whole = digits.slice(0, -6).replace(/^0+(?=\d)/, '');
  const fraction = digits.slice(-6, -3).replace(/0+$/, '');
  return `${negative ? '−' : '+'}${whole}${fraction ? `.${fraction}` : ''}`;
}

// ── 源文件对照 ──

/**
 * 报告里的实际编码名（.NET WebName）对应的 TextDecoder 标签；浏览器不支持时返回 undefined。
 * iso-8859-1 在浏览器里按 windows-1252 解码，0x80～0x9F 以外完全相同。
 */
export function decoderLabel(encodingName: string): string | undefined {
  const name = encodingName.toLowerCase();
  if (name === 'utf-16') return 'utf-16le';
  if (name === 'utf-16be') return 'utf-16be';
  if (name.startsWith('utf-32')) return undefined;
  try {
    new TextDecoder(name);
    return name;
  } catch {
    return undefined;
  }
}

/** 按源文件行号（从 1 开始）切行：CRLF、CR、LF 都算一次换行。 */
export function splitSourceLines(text: string): string[] {
  return text.split(/\r\n|\r|\n/);
}

/** 源文件很长时只显示焦点行附近的一段：返回 [start, end)（0 起下标）。 */
export function sourceWindow(total: number, focusLine: number | undefined, size: number): [number, number] {
  if (total <= size) return [0, total];
  const center = focusLine && focusLine > 0 ? Math.min(focusLine, total) - 1 : 0;
  const start = Math.max(0, Math.min(center - Math.floor(size / 2), total - size));
  return [start, start + size];
}
