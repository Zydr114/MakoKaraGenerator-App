// 静态工程预览。对应 BackendPreviewContracts.cs、BackendPreviewCommandContracts.cs；规范 §9。

import type { BackendDiagnostic, IsoDateTime } from './common';
import type { BackendErrorResponse } from './errors';
import type { Generation, Micros, Revision, Sequence } from './int64';
import type { TemplateMaterialSlot, TemplateWorkshopSampleId } from './templates';

export interface PreviewRuntimeInfo {
  enabled: boolean;
  /** configured-unverified 只表示路径已配置，不等于渲染已验证。 */
  status: string;
  sourceProfile: string;
  sourceProfiles?: string[];
  executionMode: string;
  maxPixelWidth: number;
  maxPixelHeight: number;
  maxRawFrameBytes: number;
  disabledReason?: BackendErrorResponse;
  maxSnapshotBytes: number;
  frameLeaseLifetimeSeconds: number;
  maxRetainedFramesPerSession: number;
  maxRetainedFrameBytesPerSession: number;
  maxReadLeases: number;
  maxReadLeaseBytes: number;
  clientLeaseExpiryPolicy: string;
  eventDelivery: string;
}

/**
 * 预览会话、帧与事件里的来源修订（合同 1.18.0 起，R5）：不透明字符串，取帧、换输入、关闭时原样带回。
 * 工程预览通常是十进制工程修订，模板试样（G17）是“库修订:模板修订”或草稿修订；不得按 Int64 解析。
 */
export type PreviewSourceRevision = string;

export type PreviewAlphaMode = 'opaque' | 'straight';

/**
 * 渲染设备（合同 1.39.0，规范 §25）：公开值只有小写 cpu、gpu，GPU 的实际 API 是 Vulkan。
 * 请求里省略或 null 用 Server 启动时的默认；GPU 失败不会自动改用 CPU。
 */
export const RENDER_BACKENDS = ['cpu', 'gpu'] as const;
export type RenderBackend = (typeof RENDER_BACKENDS)[number];

export interface RenderBackendDeviceInfo {
  vendor?: string;
  device: string;
  driver?: string;
  apiVersion?: string;
}

/** 一种设备的检查结果：可用时有 device，不可用时有 unavailableReason（field=renderBackend 与稳定 failureCode）。 */
export interface RenderBackendAvailability {
  renderBackend: RenderBackend;
  available: boolean;
  /** CPU 为 raster，GPU 为 vulkan。 */
  api: string;
  device?: RenderBackendDeviceInfo;
  unavailableReason?: BackendErrorResponse;
}

/** GET /render-backends：隔离 Worker 当时真实检查的快照，不保证之后一定能渲染。backends 里 CPU、GPU 各一项。 */
export interface RenderBackendsResponse {
  defaultBackend: RenderBackend;
  backends: RenderBackendAvailability[];
}
export type PreviewIntent = 'playback' | 'seek';
export type PreviewState = 'idle' | 'queued' | 'rendering' | 'ready' | 'failed' | 'closing' | 'closed';

/** 草稿试样的样本、目标与材质焦点；材质草稿必须给部位。 */
export interface AuthorDraftSampleSelection {
  sampleId: TemplateWorkshopSampleId;
  targetKey: string;
  materialSlot?: TemplateMaterialSlot | null;
}

/**
 * 工程，或作者草稿（合同 1.38.0，规范 §21）：sourceId 是草稿 ID，sourceRevision 是草稿修订，
 * authorSourceRevision 必须等于草稿的在用修订（否则 409）。换输入只能换成同一个草稿。
 */
export type PreviewSource =
  | { kind: 'project'; sourceId: string; sourceRevision: Revision }
  | { kind: 'authorDraft'; sourceId: string; sourceRevision: string; authorSourceRevision: string; templateSample: AuthorDraftSampleSelection };

/** 会话回执里的来源：project、presetCandidate（G16）、template（G17）或 authorDraft。取帧、关闭时原样带回 sourceRevision。 */
export interface PreviewSessionSource {
  kind: string;
  sourceId: string;
  sourceRevision: PreviewSourceRevision;
  /** 模板试样实际用的样本、目标与材质焦点（工坊样本，合同 1.37.0）。 */
  templateSample?: { sampleId: string; targetKey: string; materialSlot?: string };
  /** 作者草稿试样固定的源码修订。 */
  authorSourceRevision?: string | null;
}

export interface PreviewSize {
  width: number;
  height: number;
}

export interface PreviewLimits {
  maxWidth: number;
  maxHeight: number;
  maxInFlightRequests: number;
  maxRetainedFrames: number;
  maxRetainedFrameBytes: number;
  frameLeaseLifetimeSeconds: number;
  maxDiagnosticsPageSize: number;
  maxRawFrameBytes: number;
}

export interface PreviewBinaryHandle {
  handleId: string;
  contentType: string;
  sizeBytes: number;
  sha256: string;
  expiresAtUtc: IsoDateTime;
  /** 相对路径，用 baseUrl 拼接；路由 id 是 frameId。 */
  contentUrl: string;
}

export interface PreviewFrameMetadata {
  frameId: string;
  previewId: string;
  previewGeneration: Generation;
  sourceRevision: PreviewSourceRevision;
  requestSeq: Sequence;
  timeUs: Micros;
  width: number;
  height: number;
  alphaMode: PreviewAlphaMode;
  content: PreviewBinaryHandle;
  diagnosticsCursor?: string;
  eventSeq?: Sequence;
  /** 这一帧实际用的设备。 */
  renderBackend: RenderBackend;
  /** 取帧时带了 inspect: true 且投影可用时才有（合同 1.48.0），见 preview-inspection.ts。 */
  inspectionUrl?: string;
}

export interface PreviewSessionResponse {
  previewId: string;
  source: PreviewSessionSource;
  previewGeneration: Generation;
  size: PreviewSize;
  alphaMode: PreviewAlphaMode;
  state: PreviewState;
  limits: PreviewLimits;
  latestRequestSeq?: Sequence;
  latestFrame?: PreviewFrameMetadata;
  diagnosticsCursor?: string;
  protocolVersion: '1';
  /** project：工程预览；presetCandidate：模板候选试看（G16，合同 1.15.0）；template：模板固定试样（G17，合同 1.16.0），这两种输入固定、不能换输入；authorDraft：作者草稿试样（合同 1.38.0）。 */
  previewKind: string;
  /** 候选试看为 false：它不是工程内容，正式生效仍要走分配命令。 */
  committable: boolean;
  /** 会话固定的设备；换设备用 PUT .../render-backend。 */
  renderBackend: RenderBackend;
}

export interface PreviewSessionStateResponse {
  session: PreviewSessionResponse;
  failure?: BackendErrorResponse;
  renderFailureCode?: string;
}

export interface CreatePreviewInput {
  source: PreviewSource;
  size: PreviewSize;
  alphaMode?: PreviewAlphaMode;
  /** 省略或 null 用启动默认。 */
  renderBackend?: RenderBackend | null;
  /** 省略为 project；作者草稿必须写 authorDraft。 */
  previewKind?: 'project' | 'authorDraft';
  /** 省略为 true；作者草稿必须写 false。 */
  committable?: boolean;
}

export interface ReplacePreviewInput {
  expectedPreviewGeneration: Generation;
  expectedSourceRevision: PreviewSourceRevision;
  source: PreviewSource;
  size: PreviewSize;
  alphaMode?: PreviewAlphaMode;
  /** 省略或 null 保留会话原来的设备。 */
  renderBackend?: RenderBackend | null;
}

/** 只给已有预览换设备（规范 §25.3）：相同设备是 no-op；不同设备成功后换新代次、旧帧作废；失败时旧状态不变。 */
export interface PreviewRenderBackendInput {
  expectedPreviewGeneration: Generation;
  renderBackend: RenderBackend;
}

export interface PreviewFrameRequestInput {
  expectedSourceRevision: PreviewSourceRevision;
  previewGeneration: Generation;
  /** 同代次严格递增，必须为正。 */
  requestSeq: Sequence;
  /** 非负微秒。 */
  timeUs: Micros;
  intent: PreviewIntent;
  /** 同时生成按帧检查投影（合同 1.48.0）；不影响画面。 */
  inspect?: boolean | null;
}

export interface PreviewFrameAcceptedResponse {
  previewId: string;
  /** seek 会生成新代次，客户端采用这里的值。 */
  previewGeneration: Generation;
  requestSeq: Sequence;
  timeUs: Micros;
  state: string;
  eventSeq?: Sequence;
  protocolVersion: '1';
}

export interface ClosePreviewInput {
  expectedPreviewGeneration: Generation;
  expectedSourceRevision: PreviewSourceRevision;
}

export interface PreviewDiagnosticsPage {
  previewId: string;
  previewGeneration: Generation;
  sourceRevision: PreviewSourceRevision;
  diagnostics: BackendDiagnostic[];
  hasMore: boolean;
  nextCursor?: string;
  protocolVersion: '1';
}
