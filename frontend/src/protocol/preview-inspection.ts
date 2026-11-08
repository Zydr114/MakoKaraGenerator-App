// 按帧检查投影（合同 1.48.0）：GET /api/v1/preview-frames/{frameId}/inspection。
// 取帧时带 inspect: true，帧元数据才有 inspectionUrl；投影与帧同寿命，帧过期它也过期。
// 几何一律是工程画布坐标（inspection.canvas），多边形是平铺的 [x0, y0, x1, y1, …]，点数不固定（变形后的轮廓可多达 16 点）。
// 超出上限的条目只计入省略数；整份投影超过 256 KiB 时只保留成本与计数。
// 毫秒数只用于比较量级：同一帧重复请求也会有抖动。

import type { Generation, Micros } from './int64';
import type { PreviewSourceRevision, RenderBackend } from './preview';

/** 平铺的多边形顶点 [x0, y0, x1, y1, …]，工程画布坐标。 */
export type InspectionPolygon = number[];

export type InspectionProgressSource = 'unavailable' | 'fromLine' | 'fromToken' | 'fromRubySegment';

export type InspectionScriptStage =
  | 'Initialize'
  | 'Advance'
  | 'Evaluate'
  | 'OnEvent'
  | 'AdvanceVisuals'
  | 'EvaluateVisuals'
  | 'EvaluateEmitters'
  | 'other';

export type InspectionBindingKind = 'project' | 'branch' | 'lines' | 'tokens' | 'clips' | 'node' | 'unknown';

export interface PreviewInspectionCanvas {
  width: number;
  height: number;
}

export interface PreviewInspectionCluster {
  /** 字形簇在文本单元里的序号。 */
  unitIndex: number;
  /** 这个簇属于哪些文本单元；多于一个说明它跨单元（连字等）。 */
  ownerTokenIds: string[];
  /** 能否单独运动。 */
  independent: boolean;
  diagnosticCode?: string;
  ink?: InspectionPolygon;
}

export interface PreviewInspectionRuby {
  rubyId: string;
  ink?: InspectionPolygon;
}

export interface PreviewInspectionToken {
  tokenId: string;
  bindingId: string;
  lineId: string;
  independent: boolean;
  diagnosticCode?: string;
  /** 墨迹包围（经过变形）；没有可见墨迹时不带。 */
  ink?: InspectionPolygon;
  clusters: PreviewInspectionCluster[];
  ruby: PreviewInspectionRuby[];
  omittedClusters: number;
}

export interface PreviewInspectionLine {
  lineId: string;
  bindingId: string;
  bounds?: InspectionPolygon;
}

export interface PreviewInspectionCursor {
  lineId: string;
  bindingId: string;
  tokenId?: string;
  /** 0～1。 */
  progress: number;
  progressSource: InspectionProgressSource;
  /** [x, y]。 */
  point: number[];
  start: number[];
  end: number[];
}

export interface PreviewInspectionParticles {
  instanceId: string;
  births: number;
  alive: number;
  trailSamples: number;
  /** 本帧出生点 [x0, y0, …]。 */
  birthPoints: number[];
  omittedBirthPoints: number;
}

export interface PreviewInspectionEffect {
  instanceId: string;
  packageId?: string;
  packageRevision?: string;
  exportId?: string;
  sdkMajor: 1 | 2;
  bindingKind: InspectionBindingKind;
  lineIds: string[];
  /** 与 cost.scripts[].scriptId 对应；不跑脚本的实例不带。 */
  scriptId?: string;
  omittedLines: number;
}

export interface PreviewInspectionScript {
  /** 16 位大写十六进制。 */
  scriptId: string;
  stage: InspectionScriptStage;
  calls: number;
  wallMs: number;
  maxMs: number;
}

export interface PreviewInspectionAnchors {
  uniqueTimes: number;
  textCalls: number;
  futureEndpoints: number;
  memoHits: number;
  memoMisses: number;
}

/** Worker 内一帧的分阶段成本。 */
export interface PreviewInspectionCost {
  wallMs: number;
  prepareMs: number;
  scriptMs: number;
  graphMs: number;
  materialMs: number;
  particleMs: number;
  rasterMs: number;
  validationMs: number;
  readbackMs: number;
  scriptCalls: number;
  readbackBytes: number;
  allocatedBytes: number;
  gc0: number;
  gc1: number;
  gc2: number;
  /** 按脚本与阶段汇总，最多 16 条，其余计入 scriptOverflow。 */
  scripts: PreviewInspectionScript[];
  scriptOverflow: number;
  anchors: PreviewInspectionAnchors;
  /** 会话打开用时；只在会话的第一帧带。 */
  sessionOpenMs?: number;
}

export interface PreviewInspectionTruncation {
  tokens: number;
  lines: number;
  cursors: number;
  particles: number;
  effects: number;
}

export interface PreviewInspection {
  canvas: PreviewInspectionCanvas;
  /** 这一帧有没有经过效果准备阶段；没有脚本效果的静态模板为 false，这时没有几何，只有成本。 */
  prepared: boolean;
  tokens: PreviewInspectionToken[];
  lines: PreviewInspectionLine[];
  cursors: PreviewInspectionCursor[];
  particles: PreviewInspectionParticles[];
  effects: PreviewInspectionEffect[];
  cost: PreviewInspectionCost;
  truncated: PreviewInspectionTruncation;
}

/** 宿主侧成本：渲染往返（含 Worker 内全部）与 PNG 编码。 */
export interface PreviewInspectionHostCost {
  renderRoundTripMs: number;
  encodeMs: number;
  pngBytes: number;
}

export interface PreviewFrameInspectionResponse {
  frameId: string;
  previewId: string;
  previewGeneration: Generation;
  sourceRevision: PreviewSourceRevision;
  timeUs: Micros;
  width: number;
  height: number;
  renderBackend: RenderBackend;
  inspection: PreviewInspection;
  host: PreviewInspectionHostCost;
  protocolVersion: '1';
}
