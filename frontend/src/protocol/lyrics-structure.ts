// 歌词内容与结构命令、工程标量设置、导入与追加（合同 1.3.0～1.6.0，规范 §6.6～§6.9）。
// 对应 BackendProjectCommandContracts.cs；前端设计 docs/web-client-w2-design.md。

import type { LyricBranchRole, LyricBranchTimingMode, LineTarget, TokenTarget } from './editing';
import type { Micros } from './int64';

/** 文本单元拆分规则（导入与整来源重切共用的字符串枚举）。 */
export type TokenSplitRuleName = 'none' | 'mixed' | 'perTextElement' | 'latinWords' | 'byPunctuation' | 'byWhitespace';

// ── S3a 正文、注音、行操作（§6.6） ──

export type LyricContentCommand =
  | { type: 'lyrics.setLineText'; target: LineTarget; text: string }
  | { type: 'lyrics.setRubyText'; target: TokenTarget; text: string }
  | { type: 'lyrics.addLine'; branchId: string; afterLineId?: string | null }
  | { type: 'lyrics.duplicateLine'; target: LineTarget }
  | { type: 'lyrics.removeLine'; target: LineTarget };

/** S3a 标量工程设置（§6.6）。 */
export type CompositionCommand =
  | { type: 'composition.setCanvasSize'; width: number; height: number }
  | { type: 'composition.setFrameRate'; numerator: number; denominator: number }
  | { type: 'composition.setBackgroundColor'; red: number; green: number; blue: number; alpha: number }
  /** null 表示尚未确定（导出前必须确定）。 */
  | { type: 'composition.setOutputDuration'; durationUs: Micros | null }
  | { type: 'composition.useLyricsEnd' }
  | { type: 'composition.useAudioDuration' }
  /**
   * 背景图片或视频（合同 1.36.0）：assetId 是本 client 上传的授权资源；fit、playback 省略时保持现值（不能是 null）。
   * clearBackground 只去掉媒体，底色、适配方式与视频播放策略保留。
   */
  | { type: 'composition.setBackgroundAsset'; assetId: string; kind: BackgroundMediaKind; fit?: BackgroundFit; playback?: BackgroundPlayback }
  | { type: 'composition.clearBackground' }
  | { type: 'composition.setBackgroundFit'; fit: BackgroundFit }
  | { type: 'composition.setBackgroundPlayback'; playback: BackgroundPlayback };

export type BackgroundMediaKind = 'image' | 'video';
export type BackgroundFit = 'contain' | 'cover' | 'original';
/** 视频背景播完后：循环，或停在最后一帧。 */
export type BackgroundPlayback = 'loop' | 'freezeLastFrame';

// ── S3b 来源管理（§6.7） ──

export type LyricBranchCommand =
  | { type: 'lyrics.addTranslationBranch'; name: string; language?: string | null }
  | {
      type: 'lyrics.configureBranch';
      branchId: string;
      name: string;
      language: string | null;
      role: LyricBranchRole;
      timingMode: LyricBranchTimingMode;
    }
  | { type: 'lyrics.removeBranch'; branchId: string }
  /** primaryLineId 为 null 表示解除关联。 */
  | { type: 'lyrics.linkPrimaryLine'; branchId: string; lineId: string; primaryLineId: string | null };

// ── S3c 文本单元结构（§6.8） ──

export type LyricTokenCommand =
  | { type: 'lyrics.splitToken'; target: TokenTarget }
  | { type: 'lyrics.mergeToken'; target: TokenTarget }
  | { type: 'lyrics.reorderToken'; target: TokenTarget; destinationIndex: number; reassignTimeSlots: boolean }
  /** segments 必须精确拼回原正文，且落在 Unicode 文本元素边界上。 */
  | { type: 'lyrics.resplitLine'; target: LineTarget; segments: string[]; estimateTimes: boolean }
  | { type: 'lyrics.resplitBranch'; branchId: string; rule: TokenSplitRuleName; estimateTimes: boolean };

// ── S3d 再次导入与粘贴追加（§6.9） ──

export type ImportLyricsDestination =
  | { kind: 'existing'; branchId: string }
  | { kind: 'newBranch'; name: string; role: 'translation' | 'annotation' | 'custom' };

export type AppendLyricsDestination = { kind: 'existing'; branchId: string } | { kind: 'newTranslation'; name: string };

export interface LyricsImportOptions {
  importerId?: string | null;
  encodingName?: string | null;
  splitRule?: TokenSplitRuleName;
  estimateSplitTimes?: boolean;
  /** 多来源的 TTML 工程文件必须指定导入哪一个来源（合同 1.27.0）。 */
  sourceBranchId?: string | null;
}

export type LyricImportCommand =
  | { type: 'lyrics.importAsset'; assetId: string; destination: ImportLyricsDestination; options?: LyricsImportOptions }
  | { type: 'lyrics.appendText'; text: string; destination: AppendLyricsDestination };

// ── 注音片段时间轴（合同 1.27.0，规范 §18） ──

/** 注音组（四级目标）。 */
export interface RubyGroupTarget {
  branchId: string;
  lineId: string;
  tokenId: string;
  rubyId: string;
}

/** 注音片段（五级目标）。 */
export interface RubySegmentTarget extends RubyGroupTarget {
  segmentId: string;
}

/**
 * 注音片段的七条命令。新片段没有时间；afterSegmentId 必须出现，null 表示加在最前面。
 * reorderRubySegments 要给出这个注音组全部片段的完整顺序；setRubySegmentTimes 一次 1～1000 条。
 */
export type RubySegmentCommand =
  | { type: 'lyrics.setRubySegmentTime'; target: RubySegmentTarget; startUs: Micros; endUs: Micros }
  | { type: 'lyrics.clearRubySegmentTime'; target: RubySegmentTarget }
  | { type: 'lyrics.removeRubySegment'; target: RubySegmentTarget }
  | { type: 'lyrics.setRubySegmentText'; target: RubySegmentTarget; text: string }
  | { type: 'lyrics.addRubySegment'; target: RubyGroupTarget; text: string; afterSegmentId: string | null }
  | { type: 'lyrics.reorderRubySegments'; target: RubyGroupTarget; orderedSegmentIds: string[] }
  | { type: 'lyrics.setRubySegmentTimes'; changes: { target: RubySegmentTarget; startUs: Micros; endUs: Micros }[] };

// ── 命令响应的可选结果 ──

/** 注音片段命令的结果。 */
export interface RubySegmentResult {
  changedSegmentCount: number;
  target?: RubyGroupTarget;
  selectedSegmentId?: string;
  createdSegmentId?: string;
  selectionChanged: boolean;
}

/** S3a 五种歌词命令的反馈。selection 缺省表示不建议改变选择；hasSelection=false 表示清空该来源选区。 */
export interface LyricContentFeedback {
  selection?: { branchId: string; hasSelection: boolean; lineId?: string };
  createdLineId?: string;
  deletedLineId?: string;
  insertedIndex?: number;
  overlapsNext?: boolean;
  preservedTokenCount?: number;
  resetTokenCount?: number;
}

/** S3b 新增／删除来源真实变化时建议选中的来源。 */
export interface LyricBranchResult {
  selectedBranchId: string;
}

/** S3c 文本单元结构命令的结果。 */
export interface LyricTokenResult {
  changedLineCount: number;
  selection?: { branchId: string; lineId: string; hasTokenSelection: boolean; tokenId?: string };
  preservedTokenCount?: number;
  createdTokenCount?: number;
  estimatedTokenCount?: number;
}

export type LyricsImportIssueKind =
  | 'unknownTag'
  | 'malformedTag'
  | 'textWithoutTimestamp'
  | 'emptyText'
  | 'duplicateTimestamp'
  | 'outOfOrder'
  | 'negativeTimeClamped'
  | 'wordTimeOutsideLine'
  | 'wordTimesNotIncreasing'
  | 'encodingAssumed'
  | 'lineEndFallback'
  | 'discardedFeature'
  | 'formatAmbiguous'
  | 'tokenSplitApplied'
  | 'tokenTextMismatch';

export type LyricsImportIssueSeverity = 'info' | 'warning' | 'error';

export interface LyricsImportIssue {
  kind: LyricsImportIssueKind;
  severity: LyricsImportIssueSeverity;
  /** 0 表示不对应源文件中的某一行。 */
  sourceLineNumber: number;
  rawText: string;
  message: string;
}

/** 文件导入的完整报告（27 个字段）。 */
export interface LyricsImportReport {
  importerId: string;
  importerName: string;
  sourceFormat: string;
  sourceLineCount: number;
  lineCount: number;
  tokenCount: number;
  timedTokenCount: number;
  missingTokenCount: number;
  estimatedTokenCount: number;
  skippedLineCount: number;
  mergedContinuationCount: number;
  derivedLineEndCount: number;
  fallbackLineEndCount: number;
  offsetUs: Micros;
  encodingName: string;
  hadByteOrderMark: boolean;
  newLineKind: 'none' | 'lineFeed' | 'carriageReturnLineFeed' | 'carriageReturn' | 'mixed';
  issues: LyricsImportIssue[];
  detectionReason?: string;
  candidateNames: string[];
  isFormatAmbiguous: boolean;
  discardedFeatures: string[];
  splitLineCount: number;
  splitCreatedTokenCount: number;
  splitEstimatedTokenCount: number;
  issueCount: number;
  /** 没有问题时省略。 */
  maxSeverity?: LyricsImportIssueSeverity;
}

export interface LyricsImportResult {
  lineCount: number;
  selectedBranchId: string;
  selectedLineId: string;
  /** 只有文件导入有报告，粘贴追加没有。 */
  report?: LyricsImportReport;
}
