// SSE 事件。对应 BackendContracts.cs（BackendEventResponse、ClientEventsResponse、PreviewEventDto）；规范 §4。
// 事件只是提示：需要权威数据时去 GET。

import type { IsoDateTime } from './common';
import type { BackendErrorResponse } from './errors';
import type { Generation, Revision, Sequence } from './int64';
import type { JobSummary } from './jobs';
import type { PreviewFrameMetadata, PreviewSourceRevision, RenderBackend } from './preview';
import type { ProjectChangeSet, ProjectSessionSummary } from './project';

export const BACKEND_EVENT_TYPES = [
  'project.opened',
  'project.changed',
  'project.saved',
  'project.closed',
  /** 宿主背景媒体特征分析推进：修订不变，固定预览输入需按同一修订重新捕获。 */
  'project.mediaFeaturesChanged',
  'job.changed',
  'job.progress',
  'job.terminal',
  'preview.stateChanged',
  'preview.frameReady',
  'preview.failed',
  'preview.closed',
] as const;

export type BackendEventType = (typeof BACKEND_EVENT_TYPES)[number];

/** SSE 控制事件：data 不是 BackendEventResponse。 */
export const SSE_RESYNC_REQUIRED = 'resync.required';
export const SSE_CLIENT_EXPIRED = 'client.expired';

export interface PreviewEvent {
  previewId: string;
  previewGeneration: Generation;
  sourceRevision: PreviewSourceRevision;
  state: string;
  latestRequestSeq?: Sequence;
  latestFrame?: PreviewFrameMetadata;
  failure?: BackendErrorResponse;
  renderFailureCode?: string;
  renderBackend: RenderBackend;
}

export interface BackendEvent {
  eventSeq: Sequence;
  eventCursor: string;
  type: BackendEventType | (string & {});
  hostInstanceId: string;
  occurredAtUtc: IsoDateTime;
  summary?: ProjectSessionSummary;
  changeSet?: ProjectChangeSet;
  job?: JobSummary;
  preview?: PreviewEvent;
}

/** resync.required 的 data。 */
export interface ClientEventsResponse {
  eventCursor: string;
  events: BackendEvent[];
  resyncRequired: boolean;
}
