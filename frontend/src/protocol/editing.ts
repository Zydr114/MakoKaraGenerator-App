// W1 编辑读模型与打轴命令：合同 1.1.0（S1，后端 5b23115）与 1.2.0（S2，后端 bd537ad），行为见规范 §6.4、§6.5。
// 对应 BackendProjectReadContracts.cs、BackendProjectCommandContracts.cs；前端设计 docs/web-client-w1-design.md §8。

import type { Micros, Revision } from './int64';
import type { LyricTime } from './project';

// ── S1 读模型（G9） ─────────────────────────────────────

export type LyricBranchRole = 'primary' | 'translation' | 'annotation' | 'custom';
export type LyricBranchTimingMode = 'independent' | 'followPrimary' | 'none';
export type TimingStatus = 'missing' | 'imported' | 'estimated' | 'edited' | 'confirmed' | 'invalid';
export type RubyAlignment = 'center' | 'start' | 'end';

/** 分页读取的公共字段：同一修订下 offset／limit 分页，最后一页没有 nextOffset。 */
export interface RevisionPage {
  projectSessionId: string;
  projectRevision: Revision;
  offset: number;
  totalCount: number;
  nextOffset?: number;
  protocolVersion: '1';
}

export interface LyricBranchSummary {
  id: string;
  name: string;
  role: LyricBranchRole;
  language?: string;
  timingMode: LyricBranchTimingMode;
  lineCount: number;
}

export interface LyricBranchesPage extends RevisionPage {
  primaryBranchId: string;
  branches: LyricBranchSummary[];
}

/** 注音片段（合同 1.27.0）：注音可以分成几段，各自有时间。 */
export interface RubyTextSegment {
  id: string;
  text: string;
  time: LyricTime;
  timingStatus: TimingStatus;
}

export interface RubyAnnotation {
  id: string;
  baseTokenId: string;
  text: string;
  language?: string;
  relativeScale: number;
  gapPx: number;
  alignment: RubyAlignment;
  /** 1.27.0 起总是出现。 */
  segments: RubyTextSegment[];
}

export interface EditableLyricToken {
  id: string;
  text: string;
  /** 未打轴是 `{}`；`startUs: "0"` 是合法零起点。 */
  time: LyricTime;
  timingStatus: TimingStatus;
  /** 句中收尾提示覆写（省略表示按语境自动推导）。 */
  phraseEndHint?: boolean;
}

export interface EditableLyricLine {
  branchId: string;
  id: string;
  /** 行正文是权威原文，不由元素拼接。 */
  text: string;
  time: LyricTime;
  timingStatus: TimingStatus;
  /** FollowPrimary 来源的关联主歌词行。 */
  primaryLineId?: string;
  tokens: EditableLyricToken[];
  rubyAnnotations: RubyAnnotation[];
}

export interface LyricWindowPage extends RevisionPage {
  branchId: string;
  lines: EditableLyricLine[];
}

export interface ProjectSettings {
  projectSessionId: string;
  projectRevision: Revision;
  protocolVersion: '1';
  name: string;
  canvasWidth: number;
  canvasHeight: number;
  frameRateNumerator: number;
  frameRateDenominator: number;
  backgroundRed: number;
  backgroundGreen: number;
  backgroundBlue: number;
  backgroundAlpha: number;
  /** 省略表示尚未确定：导出前必须显式设置，不会自动取音频或歌词末尾（合同 1.2.1 勘误）。 */
  outputDurationUs?: Micros;
  audioDurationUs: Micros;
  /** 背景（合同 1.36.0）：kind 为 color 时没有媒体；有媒体时 resourceId 对应资源清单里的一项。 */
  background?: ProjectBackground;
}

export interface ProjectBackground {
  kind: 'color' | 'image' | 'video';
  fit: 'contain' | 'cover' | 'original';
  playback: 'loop' | 'freezeLastFrame';
  resourceId?: string;
}

/** 每次窗口读取的行数上限。 */
export const LYRIC_WINDOW_MAX_LIMIT = 100;

// ── S2 打轴命令（G3／G10），并入 ProjectCommand ─────────────

export interface TokenTarget {
  branchId: string;
  lineId: string;
  tokenId: string;
}

export interface LineTarget {
  branchId: string;
  lineId: string;
}

export interface TokenTimeChange {
  target: TokenTarget;
  startUs: Micros;
  endUs: Micros;
}

/** 批量元素时间每次 1～1000 项，重复目标整批拒绝。 */
export const TOKEN_BATCH_MAX = 1000;

export interface LyricBeatTarget {
  branchId: string;
  lineId: string;
  tokenId: string;
  /** 指向注音分段时须与 segmentId 同时提供；省略表示基础词元。 */
  rubyId?: string;
  segmentId?: string;
}

export type TimingCommand =
  | { type: 'lyrics.stampTokenStart'; target: TokenTarget; playheadUs: Micros }
  | { type: 'lyrics.stampTokenEnd'; target: TokenTarget; playheadUs: Micros }
  | { type: 'lyrics.clearTokenTime'; target: TokenTarget }
  | { type: 'lyrics.nudgeTokenTime'; target: TokenTarget; frameDelta: number }
  | { type: 'lyrics.shiftLine'; target: LineTarget; deltaUs: Micros }
  | { type: 'lyrics.setTokenTimes'; changes: TokenTimeChange[] }
  | {
      type: 'lyrics.strikeBeat';
      branchId: string;
      closeTarget?: LyricBeatTarget | null;
      openTarget: LyricBeatTarget;
      playheadUs: Micros;
      closeGuardGapUs?: Micros | null;
      fallbackDurationUs?: Micros | null;
    }
  | { type: 'lyrics.closeBeat'; target: LyricBeatTarget; playheadUs: Micros }
  | { type: 'lyrics.togglePhraseEndHint'; target: TokenTarget };

export type TimingCommandType = TimingCommand['type'];

/** 打点／连打响应附带的结果；只有 stampTokenStart／stampTokenEnd／strikeBeat／closeBeat 成功时出现。 */
export interface TimingCommandResult {
  /** 起点打点因缺少可用终点而补了一帧估算终点。 */
  estimatedPlaceholder: boolean;
  /** 连打时上一拍是否触发了忘按 L 的跨间奏兜底截断（标记为 estimated）。 */
  closeGuardApplied?: boolean;
}

/**
 * 打轴命令被领域拒绝（422 commandRejected）时 failureCode 的已知取值（规范 §6.5）。
 * 后端可能新增其他原因，界面对未知值显示后端 message。
 */
export const TIMING_FAILURE_CODES = [
  'Untimed',
  'InvalidRange',
  'NudgeOutOfBounds',
  'BeyondContentDuration',
  'DomainValidationFailed',
  'TargetStale',
] as const;
export type TimingFailureCode = (typeof TIMING_FAILURE_CODES)[number];
