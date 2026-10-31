// 资源上传、工程创建／摘要／历史／歌词与具名命令。
// 对应 BackendContracts.cs（AssetResponse、CreateProjectSessionRequest、ProjectCommand*、Lyric*）；规范 §5、§6。

import type { IsoDateTime } from './common';
import type { TimingCommand, TimingCommandResult } from './editing';
import type {
  CompositionCommand,
  LyricBranchCommand,
  LyricBranchResult,
  LyricContentCommand,
  LyricContentFeedback,
  LyricImportCommand,
  LyricTokenCommand,
  LyricTokenResult,
  LyricsImportResult,
  RubySegmentCommand,
  RubySegmentResult,
} from './lyrics-structure';
import type { StyleCommand, StyleCommandResult } from './style';
import type { PresetApplicationCommand, PresetApplicationCommandResult } from './presets';
import type {
  BasicLayoutCommandResult,
  LineLocalTransformCommandResult,
  OrchestrationClipCommandResult,
  OrchestrationClipMembersCommandResult,
  OrchestrationCommand,
  OrchestrationCommandResult,
  OrchestrationLayoutCommandResult,
  OrchestrationLeadInCommandResult,
  LeadInGenerationCommandResult,
  OrchestrationPropertiesCommandResult,
  OrchestrationStructureCommandResult,
} from './orchestration';
import type { BackendErrorResponse } from './errors';
import type { Micros, Revision } from './int64';
import type { EditableLyricLine } from './editing';
import type { LyricsImportOptions, LyricsImportReport } from './lyrics-structure';

export interface AssetResponse {
  assetId: string;
  /** 完整字节的小写 SHA-256。 */
  sha256: string;
  size: number;
  contentType: string;
  fileName: string;
  expiresAtUtc?: IsoDateTime;
}

export interface Composition {
  canvasWidth?: number;
  canvasHeight?: number;
  frameRateNumerator?: number;
  frameRateDenominator?: number;
  backgroundRed?: number;
  backgroundGreen?: number;
  backgroundBlue?: number;
  backgroundAlpha?: number;
  /** 仍为 JSON Int64 number（规范 §1.3 例外），必须是安全整数。 */
  outputDurationUs?: number | null;
}

/** 0 None／1 Mixed／2 PerTextElement／3 LatinWords／4 ByPunctuation／5 ByWhitespace。 */
export type SplitRule = 0 | 1 | 2 | 3 | 4 | 5;

export interface CreateProjectSessionInput {
  name: string;
  /** 音频、歌词都可以省略（合同 1.36.0）：四种组合都能建工程，都省略就是空工程。 */
  audioAssetId?: string | null;
  lyricsAssetId?: string | null;
  composition?: Composition | null;
  importerId?: string | null;
  encodingName?: string | null;
  splitRule?: SplitRule;
  estimateSplitTimes?: boolean;
}

export interface ProjectAudioAnalysisSummary {
  status: string;
  algorithm?: string;
  sampleRate?: number;
  hopSamples?: number;
  originUs?: Micros;
  sourceHash?: string;
  pcmHash?: string;
  sampleCount?: string;
  durationUs?: string;
  revision?: string;
  contentHash?: string;
  pointCount?: number;
}

export interface ProjectSessionSummary {
  projectSessionId: string;
  hostInstanceId: string;
  projectRevision: Revision;
  name: string;
  audioAssetId: string;
  lyricsLineCount: number;
  /** 仍为 JSON Int64 number（规范 §1.3 例外），必须是安全整数。 */
  audioDurationUs?: number;
  isDirty: boolean;
  canUndo: boolean;
  canRedo: boolean;
  eventCursor?: string;
  protocolVersion: '1';
  hasPath: boolean;
  savedProjectRevision?: Revision;
  isReadOnly: boolean;
  audioAnalysis?: ProjectAudioAnalysisSummary;
}

export interface ProjectHistoryResponse {
  projectRevision: Revision;
  canUndo: boolean;
  canRedo: boolean;
  undoName?: string;
  redoName?: string;
  undoCount: number;
  redoCount: number;
  protocolVersion: '1';
}

/** 未打轴是 `{}`，不是零时间。 */
export interface LyricTime {
  startUs?: Micros;
  endUs?: Micros;
}

export interface LyricToken {
  id: string;
  text: string;
  time: LyricTime;
}

export interface LyricLine {
  branchId: string;
  /** 读模型身份字段叫 id；命令目标用 lineId／tokenId。 */
  id: string;
  text: string;
  time: LyricTime;
  tokens: LyricToken[];
}

export interface LyricsLinesResponse {
  projectSessionId: string;
  projectRevision: Revision;
  branchId: string;
  lines: LyricLine[];
  protocolVersion: '1';
}

export interface LyricTimingTarget {
  branchId: string;
  lineId: string;
  /** 省略表示整行。 */
  tokenId?: string | null;
}

/**
 * 当前公开的 96 种工程命令：原五种（规范 §6.3）、六种打轴（§6.5，editing.ts）、
 * 正文／行／设置／背景／来源／文本单元结构／导入／注音片段（§6.6～§6.9、§18，lyrics-structure.ts）、两种底层样式（§6.11，style.ts）、
 * 四种预设分配（§13.3，presets.ts）、编排命令（设置默认容器 §13.5、快速布局 §13.8、片段 §13.10、结构 §13.11／§13.17、属性 §13.12／§13.13、前奏 §13.15／§13.19、单句微调 §13.16／§13.18，orchestration.ts），
 * 以及工程改名、音频替换与清除（§17.2、§17.4，合同 1.25.0）、一次提交多条修改（§6.12，合同 1.36.0）。
 */
export type ProjectDataCommand =
  | { type: 'lyrics.setTimeRange'; target: LyricTimingTarget; startUs: Micros; endUs: Micros }
  | { type: 'history.undo' }
  | { type: 'history.redo' }
  | { type: 'audio.analysis.apply'; analysisJobId: string }
  | { type: 'audio.analysis.clear' }
  /** 换成另一个授权音频资源，后端重新准备；音轨没变时是 no-op。 */
  | { type: 'audio.replaceAsset'; assetId: string }
  | { type: 'audio.clear' }
  /** 非空白，最多 256 个 UTF-16 单元，去掉首尾空白后保存；不移动工程目录。 */
  | { type: 'project.rename'; name: string }
  | TimingCommand
  | LyricContentCommand
  | CompositionCommand
  | LyricBranchCommand
  | LyricTokenCommand
  | LyricImportCommand
  | RubySegmentCommand
  | StyleCommand
  | PresetApplicationCommand
  | OrchestrationCommand;

/** 可以放进 project.applyEdits 与编辑候选的修改：不含历史、资源导入、外部任务和嵌套批处理。 */
export type ProjectEditCommand = Exclude<
  ProjectDataCommand,
  {
    type:
      | 'history.undo'
      | 'history.redo'
      | 'audio.analysis.apply'
      | 'lyrics.importAsset'
      | 'audio.replaceAsset'
      | 'audio.clear'
      | 'composition.setBackgroundAsset';
  }
>;

/** project.applyEdits：按顺序执行几条修改，整体只提交一条历史；任何一条失败都不修改工程。 */
export type ProjectCommand = ProjectDataCommand | { type: 'project.applyEdits'; edits: ProjectEditCommand[] };

export type ProjectCommandType = ProjectCommand['type'];

/** 命令请求：expectedRevision 与三个回执字段都在顶层（规范 §1.2 例外）。 */
export interface ProjectCommandRequest {
  expectedRevision: Revision;
  command: ProjectCommand;
  requestId: string;
  hostInstanceId: string;
  validUntilUtc: IsoDateTime;
}

export interface ProjectChangeSet {
  areas: string[];
  branchIds: string[];
  lineIds: string[];
  tokenIds: string[];
  requiresFullRefresh: boolean;
}

export interface ProjectCommandResponse {
  projectSessionId: string;
  previousRevision: Revision;
  projectRevision: Revision;
  /** false 表示领域 no-op：不改修订、不产生 project.changed。 */
  changed: boolean;
  isDirty: boolean;
  canUndo: boolean;
  canRedo: boolean;
  undoName?: string;
  redoName?: string;
  changeSet: ProjectChangeSet;
  eventCursor?: string;
  protocolVersion: '1';
  /** 只有打点命令成功时出现。 */
  timingResult?: TimingCommandResult;
  /** 正文／注音／行操作的反馈与建议选择。 */
  feedback?: LyricContentFeedback;
  /** 新增／删除来源真实变化时的建议来源。 */
  branchResult?: LyricBranchResult;
  /** 文本单元结构命令的结果与建议选择。 */
  tokenResult?: LyricTokenResult;
  /** 导入／追加的结果；文件导入带完整报告。 */
  importResult?: LyricsImportResult;
  styleResult?: StyleCommandResult;
  /** 分配命令成功变化时的分配 ID（合同 1.12.0）。 */
  presetApplicationResult?: PresetApplicationCommandResult;
  /** 设置默认容器的结果（合同 1.13.0）；no-op 时 changed 为 false。 */
  orchestrationResult?: OrchestrationCommandResult;
  /** 快速布局预演或应用的统计（合同 1.17.0）。 */
  basicLayoutResult?: BasicLayoutCommandResult;
  /** 片段命令的结果（合同 1.19.0）。 */
  clipResult?: OrchestrationClipCommandResult;
  /** 结构命令的结果（G20，合同 1.20.0；新建与复制带 createdNodeIds，1.23.0）。 */
  structureResult?: OrchestrationStructureCommandResult;
  /** 布局默认位置的结果（G21-A，合同 1.21.0）。 */
  layoutResult?: OrchestrationLayoutCommandResult;
  /** 几何、布局设置、槽位动画、来源动画策略的结果（G21-B，合同 1.22.0）。 */
  propertiesResult?: OrchestrationPropertiesCommandResult;
  /** 倒计时容器与前奏的结果（G23，合同 1.22.0）。 */
  leadInResult?: OrchestrationLeadInCommandResult;
  /** 前奏自动生成（1.24.0）。 */
  leadInGenerationResult?: LeadInGenerationCommandResult;
  /** 单句局部变换的结果（G24，合同 1.22.0）。 */
  lineLocalTransformResult?: LineLocalTransformCommandResult;
  /** 注音片段命令的结果（合同 1.27.0）。 */
  rubyResult?: RubySegmentResult;
  /** 片段成员命令的结果（合同 1.25.0）。 */
  clipMembersResult?: OrchestrationClipMembersCommandResult;
  /** project.applyEdits 每条修改各自的结果，按提交顺序。 */
  editResults?: ProjectEditResult[];
}

/** applyEdits 与编辑候选里单条修改的结果；结果字段与命令回执同名。 */
export interface ProjectEditResult
  extends Pick<
    ProjectCommandResponse,
    | 'timingResult'
    | 'feedback'
    | 'branchResult'
    | 'tokenResult'
    | 'importResult'
    | 'styleResult'
    | 'presetApplicationResult'
    | 'orchestrationResult'
    | 'layoutResult'
    | 'structureResult'
    | 'basicLayoutResult'
    | 'clipResult'
    | 'leadInResult'
    | 'lineLocalTransformResult'
    | 'propertiesResult'
    | 'leadInGenerationResult'
    | 'rubyResult'
    | 'clipMembersResult'
  > {
  index: number;
  type: string;
  changed: boolean;
}

// ── 资源清单（规范 §17.2，合同 1.25.0） ──

export type ProjectResourceKind = 'media' | 'audioAnalysis' | 'effectPackageManifest' | 'effectPackageFile' | 'templateSnapshot';

export type ProjectResourceStatus =
  | 'available'
  | 'missing'
  | 'identityUnavailable'
  | 'unsupportedContent'
  | 'embedded'
  | 'noStream'
  | 'failed'
  | 'validated'
  | 'unsupported';

/** 捕获修订下工程实际引用的一项资源；未证明的事实省略。contentUrl 仍需认证读取。 */
export interface ProjectResource {
  resourceId: string;
  kind: ProjectResourceKind;
  /** 按引用来源合并去重，例如 audio、background。 */
  roles: string[];
  status: ProjectResourceStatus;
  /** 十进制字符串。 */
  sizeBytes?: string;
  sha256?: string;
  assetId?: string;
  contentUrl?: string;
  preparationStatus?: 'prepared' | 'unavailable';
  waveformStatus?: 'available' | 'noAudio' | 'cacheBudgetExceeded' | 'audioIdentityChanged';
  packageId?: string;
  packageRevision?: string;
  packagePath?: string;
  packageRole?: string;
  packageClosure?: Record<string, unknown>;
}

export interface ProjectResourcesResponse {
  projectSessionId: string;
  projectRevision: Revision;
  offset: number;
  totalCount: number;
  nextOffset?: number;
  resources: ProjectResource[];
  protocolVersion: '1';
}

// ── 歌词导入规划（规范 §6.12，合同 1.36.0）：只读，不改工程 ──

export interface LyricsImportPlanInput {
  assetId: string;
  options?: LyricsImportOptions;
}

export interface LyricsImportFormatCandidate {
  importerId: string;
  name: string;
  sourceFormat: string;
  reason: string;
}

export interface LyricsImportEncodingCandidate {
  name: string;
  reason: string;
}

export interface LyricsImportBranchCandidate {
  id: string;
  name: string;
  role: string;
  lineCount: number;
}

/** 解析失败时 preparationError 说明原因，report 与 previewLines 不代表成功。 */
export interface LyricsImportPlan {
  assetId: string;
  fileName: string;
  byteLength: number;
  detectedImporterId?: string;
  detectionReason?: string;
  isFormatAmbiguous: boolean;
  formatCandidates: LyricsImportFormatCandidate[];
  encodingName?: string;
  hadByteOrderMark: boolean;
  isEncodingAmbiguous: boolean;
  encodingCandidates: LyricsImportEncodingCandidate[];
  sourceBranches: LyricsImportBranchCandidate[];
  selectedSourceBranchId?: string;
  report?: LyricsImportReport;
  previewLines: EditableLyricLine[];
  preparationError?: BackendErrorResponse;
  protocolVersion: '1';
}
