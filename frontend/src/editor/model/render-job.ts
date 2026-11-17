// 正式验证与导出任务的文字（W5 设计 §2.3）：阶段、进度比例、报告摘要、失败原因。纯函数，不依赖 React。

import {
  TERMINAL_JOB_STATES,
  normalizeSeverity,
  type BackendDiagnostic,
  type ExportReport,
  type JobProgress,
  type JobResponse,
  type JobState,
  type ValidationIssue,
} from '../../protocol';
import { formatLabel, qualityLabel } from './export-readiness';
import { formatBytes } from './media';
import { recordedBackendText } from './render-device';
import { formatFrameRate } from './settings';
import { formatTime } from './time';

const STAGE_TEXT: Record<string, string> = {
  validating: '验证输入',
  probing: '读取音频',
  rendering: '渲染',
  muxing: '合成',
  verifying: '校验成片',
  'preparing-result': '整理结果',
  prepared: '整理结果',
  disposing: '收尾',
  committing: '发布',
  completed: '完成',
  failed: '失败',
  cancelled: '已取消',
};

const STATE_TEXT: Record<string, string> = {
  queued: '排队中',
  running: '进行中',
  committing: '正在发布',
  succeeded: '已完成',
  failed: '失败',
  cancelled: '已取消',
  unknown: '结果未知',
};

export function jobStateText(state: string): string {
  return STATE_TEXT[state] ?? state;
}

/** 进度的一行：“渲染 · 30/90 帧”；排队时“排队中”。 */
export function progressText(job: Pick<JobResponse, 'state' | 'progress' | 'cancellationRequested'>): string {
  if (job.cancellationRequested && !isTerminal(job.state)) return '正在取消……';
  if (job.state === 'queued') return '排队中（同一时间只跑一个任务，正在等前一个）';
  const progress = job.progress;
  if (!progress) return jobStateText(job.state);
  const stage = STAGE_TEXT[progress.stage] ?? progress.stage;
  const frames = progress.totalFrames > 0 ? ` · ${progress.completedFrames}/${progress.totalFrames} 帧` : '';
  const layer = progress.transparent === undefined ? '' : progress.transparent ? '（透明）' : '（普通）';
  return `${stage}${layer}${frames}`;
}

/** 0～1；没有帧数时为 undefined（不定进度）。 */
export function progressFraction(progress: JobProgress | undefined): number | undefined {
  if (!progress || progress.totalFrames <= 0) return undefined;
  return Math.min(1, progress.completedFrames / progress.totalFrames);
}

export function isTerminal(state: JobState): boolean {
  return TERMINAL_JOB_STATES.has(state);
}

const FAILURE_TEXT: Record<string, string> = {
  exportInputRejected: '工程里还有不满足导出要求的地方（例如某句或某个字没有时间、文本为空，或输出时长没有确定）。后端没有指出是哪一处（G29），请看上面的准备检查。',
  exportAudioUnsupported: '音频格式不受支持或文件不可用。请检查音频资源，或取消“含音轨”。',
  WorkerExportAudioUnsupported: '音频格式或声道不受支持，或音频文件已损坏。请检查或替换音频。',
  WorkerExportAudioDecodeFailed: '音频文件无法解码，可能已损坏。请检查或替换音频。',
  WorkerExportAudioMismatch: '音频文件已变化或校验失败，请重新添加音频后再导出。',
  WorkerExportAudioTooShort: '此任务因音轨长度不足失败，请重新开始导出。',
  validationInputRejected: '工程里还有不满足要求的地方，后端没有指出是哪一处（G29），请看上面的准备检查。',
  revisionConflict: '工程在开始前又改了，请再点一次。',
  quotaExceeded: '后端同时能跑的任务已满，请等前一个任务结束。',
  resourceTooLarge: '工程的渲染输入太大，后端拒绝了。',
  serviceUnavailable: '后端暂时不能执行这个任务。',
  jobCancelled: '任务已取消。',
};

/** 失败原因：已知代码查表，其余用后端的 message。 */
export function failureText(code: string | undefined, message: string | undefined): string {
  return (code && FAILURE_TEXT[code]) || message || (code ? `失败（${code}）` : '失败。');
}

/** 导出报告的摘要：尺寸、帧率、帧数、时长、音轨、编码、大小。 */
export function exportReportFacts(report: ExportReport): { label: string; value: string }[] {
  const duration = report.actualVideoDurationUs ?? report.derivedVideoDurationUs;
  return [
    { label: '格式', value: `${formatLabel(report.format)}${report.videoCodec ? ` · ${report.videoCodec}` : ''}${report.pixelFormat ? ` · ${report.pixelFormat}` : ''}` },
    { label: '画面', value: `${report.width} × ${report.height}，${formatFrameRate({ numerator: report.frameRateNumerator, denominator: report.frameRateDenominator })}` },
    { label: '帧数', value: `${report.framesWritten}/${report.expectedFrames}${report.droppedFrames || report.failedFrames ? `（丢 ${report.droppedFrames}，失败 ${report.failedFrames}）` : ''}` },
    { label: '时长', value: formatTime(Number(duration)) },
    { label: '音轨', value: report.audioIncluded ? `有${report.audioCodec ? `（${report.audioCodec}）` : ''}${report.actualAudioDurationUs ? `，${formatTime(Number(report.actualAudioDurationUs))}` : ''}` : '没有' },
    ...(report.audioIncluded && report.audioDurationUs !== null ? [{ label: '源音频', value: formatTime(Number(report.audioDurationUs)) }] : []),
    { label: '质量', value: report.actualQuality === report.requestedQuality ? qualityLabel(report.actualQuality) : `${qualityLabel(report.actualQuality)}（要求${qualityLabel(report.requestedQuality)}）` },
    { label: '大小', value: formatBytes(report.mediaSize) ?? `${report.mediaSize} B` },
    { label: '校验', value: report.strictlyVerified ? '已逐帧校验' : '没有通过严格校验' },
    { label: '设备', value: recordedBackendText(report.renderBackend) },
  ];
}

/** 诊断的一行：“[错误] 代码：说明（位置）”。 */
export function diagnosticText(diagnostic: BackendDiagnostic): string {
  const severity = normalizeSeverity(diagnostic.severity);
  const level = severity === 'error' ? '错误' : severity === 'warning' ? '警告' : '信息';
  const where = diagnostic.occurrence?.location;
  const location = diagnostic.location ? `（${diagnostic.location}）` : where ? `（${where.path}:${where.line}:${where.column}）` : '';
  if (diagnostic.sourceMaterialFailure) {
    const source = diagnostic.sourceMaterialFailure;
    const owner = source.owner;
    const material = owner.sourceMaterial;
    const budget = source.budget;
    const mib = (value: string) => (Number(value) / 1_048_576).toFixed(2);
    const detail = budget ? `；表面 ${budget.currentSurfaces} + ${budget.additionalSurfaces} / ${budget.maximumSurfaces}，估算 ${mib(budget.currentBytes)} + ${mib(budget.additionalBytes)} / ${mib(budget.maximumBytes)} MiB` : '';
    return `[${level}] ${diagnostic.code}：${diagnostic.message}（${owner.path} · ${material.packageId}/${material.exportId} · ${source.stage} · ${formatTime(Number(source.timeUs))}${detail}）`;
  }
  return `[${level}] ${diagnostic.code}：${diagnostic.message}${location}`;
}

/** 正式验证问题的一行：“[错误] 透明 · 0:00.033 · 代码：说明”。 */
export function validationIssueText(issue: ValidationIssue): string {
  const where = issue.location ? `（${issue.location.path}:${issue.location.line}:${issue.location.column}）` : '';
  return `[${issue.isError ? '错误' : '警告'}] ${issue.transparent ? '透明' : '普通'} · ${formatTime(Number(issue.timeUs))} · ${issue.code}${issue.message ? `：${issue.message}` : ''}${where}`;
}

/**
 * 下载的文件名：“工程名.mp4”。响应头里的名字是“MakoKara-<成片 ID>.mp4”，只在工程名为空时用；
 * 扩展名先看响应头，再看内容类型。
 */
export function downloadName(headerName: string | null, projectName: string, contentType: string): string {
  const safe = projectName.trim().replace(/[\\/:*?"<>|]+/g, '-');
  if (!safe && headerName) return headerName;
  const extension = headerName?.match(/\.(mp4|mov)$/i)?.[1]?.toLowerCase() ?? (contentType === 'video/quicktime' ? 'mov' : 'mp4');
  return `${safe || 'export'}.${extension}`;
}
