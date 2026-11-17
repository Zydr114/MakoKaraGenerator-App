// 导出准备检查（W5 设计 §2.3）：由设置、摘要、资源、歌词和导出能力算出每一项检查，以及总帧数与正式验证的默认帧数。
// 纯函数，不依赖 React。歌词时间按后端的导出领域检查重算（G29：后端拒绝时不说是哪句），规则见 ProjectValidator。

import type { ExportCapabilities, ExportFormat, ExportQuality, LyricBranchRole, LyricBranchTimingMode, ProjectResource } from '../../protocol';
import type { EditorLine } from './lyrics';
import { formatBytes, resourceStatusText, resourceUsable } from './media';
import { formatFrameRate } from './settings';
import { US_PER_SECOND, formatTime, type FrameRate } from './time';

export type CheckLevel = 'ok' | 'info' | 'warning' | 'block';

export type SettingsGroup = 'project' | 'canvas' | 'background' | 'audio' | 'duration';

/** 修复入口：设置面板的某一组，或歌词页的某一句（有字的问题时选中第一个有问题的字）。 */
export type CheckFix = { kind: 'settings'; group: SettingsGroup } | { kind: 'lyrics'; branchId: string; lineId: string; tokenId?: string };

export interface ReadinessCheck {
  id: string;
  level: CheckLevel;
  title: string;
  text: string;
  fix?: CheckFix;
}

export const FORMAT_OPTIONS: { value: ExportFormat; label: string; extension: string }[] = [
  { value: 'opaqueMp4', label: 'MP4（H.264）', extension: 'mp4' },
  { value: 'transparentProRes4444', label: 'MOV 透明（ProRes 4444）', extension: 'mov' },
];

export const QUALITY_OPTIONS: { value: ExportQuality; label: string }[] = [
  { value: 'draft', label: '草稿' },
  { value: 'standard', label: '标准' },
  { value: 'high', label: '高' },
];

export function formatLabel(format: string): string {
  return FORMAT_OPTIONS.find((o) => o.value === format)?.label ?? format;
}

export function qualityLabel(quality: string): string {
  return QUALITY_OPTIONS.find((o) => o.value === quality)?.label ?? quality;
}

/** 总帧数，规则与后端相同：max(1, round(时长 × 帧率))，远离零取整。 */
export function totalFrames(outputDurationUs: number, rate: FrameRate): number {
  const exact = (outputDurationUs * rate.numerator) / (rate.denominator * US_PER_SECOND);
  return Math.max(1, Math.sign(exact) * Math.round(Math.abs(exact)));
}

/** 正式验证默认检查的帧数：min(总帧数, 每种输出的上限)。 */
export function defaultValidationFrames(total: number, maxFramesPerOutput: number): number {
  return Math.max(1, Math.min(total, maxFramesPerOutput));
}

// ── 歌词时间 ──

export interface TimingBranch {
  id: string;
  name: string;
  role: LyricBranchRole;
  timingMode: LyricBranchTimingMode;
  /** 编排里给来源指定的动画策略（Karaoke／Static／FollowPrimary）；没指定时按角色：主歌词逐字，其他静态。 */
  animationPolicy?: string | null | undefined;
}

export interface TimingProblem {
  branchId: string;
  lineId: string;
  /** 1 起的行号（来源里的顺序）。 */
  lineNumber: number;
  text: string;
  missingLineTime: boolean;
  /** 缺时间的字数。 */
  missingTokens: number;
  /** 空字（或空行文本）。 */
  emptyTexts: number;
  /** 第一个有问题的字。 */
  tokenId?: string;
}

/**
 * 按后端导出检查（ProjectValidator，Export 上下文）找出会阻断导出的句子：
 * 行文本为空；行没有时间（跟随主歌词、关联了主歌词行且自己没有时间的除外）；
 * 字文本为空；字没有时间（静态来源里没有时间的字不算）。没有字的行在静态来源里不检查字。
 */
export function timingProblems(branches: readonly TimingBranch[], linesByBranch: Readonly<Record<string, readonly EditorLine[]>>): TimingProblem[] {
  const problems: TimingProblem[] = [];
  for (const branch of branches) {
    const policy = branch.animationPolicy ?? (branch.role === 'primary' ? 'Karaoke' : 'Static');
    const isStatic = policy === 'Static';
    const followPrimary = branch.timingMode === 'followPrimary';
    (linesByBranch[branch.id] ?? []).forEach((line, index) => {
      const lineTimed = line.startUs !== undefined && line.endUs !== undefined;
      const missingLineTime = !lineTimed && !(followPrimary && line.primaryLineId);
      let emptyTexts = line.text === '' ? 1 : 0;
      let missingTokens = 0;
      let tokenId: string | undefined;
      if (!isStatic || line.tokens.length > 0) {
        for (const token of line.tokens) {
          const empty = token.text === '';
          const timed = token.startUs !== undefined && token.endUs !== undefined;
          const hasAnyTime = token.startUs !== undefined || token.endUs !== undefined;
          const missing = !timed && (!isStatic || hasAnyTime);
          if (empty) emptyTexts++;
          if (missing) missingTokens++;
          if ((empty || missing) && tokenId === undefined) tokenId = token.id;
        }
      }
      if (missingLineTime || missingTokens > 0 || emptyTexts > 0) {
        problems.push({ branchId: branch.id, lineId: line.id, lineNumber: index + 1, text: line.text, missingLineTime, missingTokens, emptyTexts, ...(tokenId ? { tokenId } : {}) });
      }
    });
  }
  return problems;
}

export function timingProblemText(problems: readonly TimingProblem[], branchName: (id: string) => string): string {
  const first = problems[0]!;
  const lines = problems.length;
  const tokens = problems.reduce((n, p) => n + p.missingTokens, 0);
  const untimedLines = problems.filter((p) => p.missingLineTime).length;
  const empty = problems.reduce((n, p) => n + p.emptyTexts, 0);
  const parts = [
    untimedLines > 0 ? `${untimedLines} 句没有时间` : undefined,
    tokens > 0 ? `${tokens} 个字没有时间` : undefined,
    empty > 0 ? `${empty} 处文本为空` : undefined,
  ].filter(Boolean);
  const where = `第一处是「${branchName(first.branchId)}」第 ${first.lineNumber} 句${first.text ? `“${first.text.length > 16 ? `${first.text.slice(0, 16)}…` : first.text}”` : ''}`;
  return `${lines} 句还不能导出：${parts.join('，')}。${where}。导出要求每句、每个字都有时间，文本不能为空。`;
}

// ── 准备检查 ──

export interface ReadinessInput {
  /** 还没读到时为 undefined；读取失败时为 null。 */
  capabilities: ExportCapabilities | null | undefined;
  capabilitiesError?: string;
  format: ExportFormat;
  includeAudio: boolean;
  canvasWidth: number;
  canvasHeight: number;
  frameRate: FrameRate;
  outputDurationUs?: number;
  hasAudio: boolean;
  /** 资源清单还没读到时为 null。 */
  resources: readonly ProjectResource[] | null;
  audioResource?: ProjectResource;
  /** 有背景媒体时才有。 */
  backgroundResource?: ProjectResource;
  hasBackgroundMedia: boolean;
  timing: readonly TimingProblem[];
  branchName: (id: string) => string;
  /** 正式验证：没做过、当前修订通过、当前修订未通过、验证的是旧修订。 */
  validation: 'none' | 'running' | 'passed' | 'failed' | 'stale';
}

const ok = (id: string, title: string, text: string): ReadinessCheck => ({ id, level: 'ok', title, text });

export function readinessChecks(input: ReadinessInput): ReadinessCheck[] {
  const checks: ReadinessCheck[] = [];
  const caps = input.capabilities;

  // 导出可用、格式可用
  if (caps === undefined) checks.push({ id: 'capabilities', level: 'info', title: '导出能力', text: '正在读取后端的导出能力……' });
  else if (caps === null) checks.push({ id: 'capabilities', level: 'block', title: '导出能力', text: input.capabilitiesError ?? '读取导出能力失败。' });
  else if (!caps.enabled) checks.push({ id: 'capabilities', level: 'block', title: '导出能力', text: caps.disabledReason?.message ?? '后端的导出不可用。' });
  else if (!caps.formats.includes(input.format)) checks.push({ id: 'capabilities', level: 'block', title: '格式', text: `这台后端不支持${formatLabel(input.format)}。` });
  else checks.push(ok('capabilities', '导出能力', `可以导出${formatLabel(input.format)}。`));

  // 输出时长
  const duration = input.outputDurationUs;
  if (duration === undefined || duration <= 0) {
    checks.push({ id: 'duration', level: 'block', title: '输出时长', text: '输出时长还没有确定。', fix: { kind: 'settings', group: 'duration' } });
  } else {
    checks.push(ok('duration', '输出时长', `${formatTime(duration)}，共 ${totalFrames(duration, input.frameRate)} 帧。`));
  }

  // 歌词时间
  if (input.timing.length > 0) {
    const first = input.timing[0]!;
    checks.push({ id: 'timing', level: 'block', title: '歌词时间', text: timingProblemText(input.timing, input.branchName), fix: { kind: 'lyrics', branchId: first.branchId, lineId: first.lineId, ...(first.tokenId ? { tokenId: first.tokenId } : {}) } });
  } else {
    checks.push(ok('timing', '歌词时间', '每句、每个字都有时间。'));
  }

  // 画布
  const { canvasWidth: w, canvasHeight: h } = input;
  if (input.format === 'opaqueMp4' && (w % 2 !== 0 || h % 2 !== 0)) {
    checks.push({ id: 'canvas', level: 'block', title: '画布', text: `MP4 要求宽高都是偶数，现在是 ${w} × ${h}。MOV 透明不受这个限制。`, fix: { kind: 'settings', group: 'canvas' } });
  } else {
    checks.push(ok('canvas', '画布', `${w} × ${h}，${formatFrameRate(input.frameRate)}。`));
  }

  // 部署限制：只检查能力里给了值的项
  if (caps) {
    const limits: string[] = [];
    const frames = duration && duration > 0 ? totalFrames(duration, input.frameRate) : undefined;
    if (caps.maxFrames != null && frames !== undefined && frames > caps.maxFrames) limits.push(`帧数最多 ${caps.maxFrames}（现在 ${frames}）`);
    if (caps.maxDurationUs != null && duration !== undefined && duration > Number(caps.maxDurationUs)) limits.push(`时长最多 ${formatTime(Number(caps.maxDurationUs))}`);
    const fps = input.frameRate.numerator / input.frameRate.denominator;
    if (caps.maxFrameRate != null && fps > caps.maxFrameRate) limits.push(`帧率最多 ${caps.maxFrameRate} fps`);
    if (caps.maxCanvasWidth != null && w > caps.maxCanvasWidth) limits.push(`宽度最多 ${caps.maxCanvasWidth}`);
    if (caps.maxCanvasHeight != null && h > caps.maxCanvasHeight) limits.push(`高度最多 ${caps.maxCanvasHeight}`);
    if (limits.length > 0) checks.push({ id: 'limits', level: 'block', title: '部署限制', text: `超出这台后端的限制：${limits.join('；')}。` });
  }

  // 音轨：没有音频时自动不含音轨
  if (!input.hasAudio) {
    checks.push({ id: 'audio', level: 'info', title: '音轨', text: '工程没有音频，只导出画面。', fix: { kind: 'settings', group: 'audio' } });
  } else if (input.includeAudio) {
    const audio = input.audioResource;
    if (audio && (!resourceUsable(audio) || audio.preparationStatus === 'unavailable')) {
      checks.push({ id: 'audio', level: 'block', title: '音轨', text: `音频${audio.preparationStatus === 'unavailable' ? '无法准备' : resourceStatusText(audio)}，不能放进成片。可以替换音频，或取消“含音轨”。`, fix: { kind: 'settings', group: 'audio' } });
    } else {
      const size = audio ? formatBytes(audio.sizeBytes) : undefined;
      checks.push(ok('audio', '音轨', `包含音频${size ? `（${size}）` : ''}。音轨较短时尾部补静音，较长时截断至成片结束。`));
    }
  } else {
    checks.push({ id: 'audio', level: 'info', title: '音轨', text: '不含音轨，只导出画面。' });
  }

  // 背景媒体
  if (input.hasBackgroundMedia) {
    const bg = input.backgroundResource;
    if (input.resources === null) checks.push({ id: 'background', level: 'info', title: '背景媒体', text: '正在读取资源状态……' });
    else if (!bg || !resourceUsable(bg)) checks.push({ id: 'background', level: 'block', title: '背景媒体', text: `背景媒体${bg ? resourceStatusText(bg) : '找不到'}，不能导出。可以替换或移除。`, fix: { kind: 'settings', group: 'background' } });
    else checks.push(ok('background', '背景媒体', '背景媒体可用。'));
  }

  // 正式验证
  const validation: Record<ReadinessInput['validation'], ReadinessCheck> = {
    none: { id: 'validation', level: 'info', title: '正式验证', text: '还没有做正式验证（可选）。' },
    running: { id: 'validation', level: 'info', title: '正式验证', text: '正在验证……' },
    passed: ok('validation', '正式验证', '当前修订验证通过。'),
    failed: { id: 'validation', level: 'warning', title: '正式验证', text: '当前修订验证没有通过，导出可能失败。可以先看下面的问题。' },
    stale: { id: 'validation', level: 'info', title: '正式验证', text: '上次验证之后又有修改，可以重新验证。' },
  };
  checks.push(validation[input.validation]);
  return checks;
}

export function blocked(checks: readonly ReadinessCheck[]): boolean {
  return checks.some((c) => c.level === 'block');
}
