// 健康检查、能力、客户端登记／心跳／状态。
// 对应 BackendContracts.cs、BackendAudioPreparationContracts.cs；规范 §2、§3。

import type { IsoDateTime } from './common';
import type { JobSummary } from './jobs';
import type { ProjectSessionSummary } from './project';

export interface HealthResponse {
  status: string;
  hostInstanceId: string;
  protocolVersion: '1';
}

export interface AudioPreparationCapabilities {
  enabled: boolean;
  disabledReason?: string;
  profile: string;
  families: string[];
  maxPayloadBytes: number;
  maxBaseBuckets: number;
  baseBucketSamples: number;
  operationTimeoutSeconds: number;
  cancellationGraceSeconds: number;
  maxRunning: number;
  maxRunningPerClient: number;
  maxQueued: number;
  maxQueuedPerClient: number;
}

export interface BackendCapabilities {
  protocolVersion: '1';
  hostInstanceId: string;
  productVersion: string;
  /** 单个资源的字节上限；null 表示部署没有设这个上限（1.36.0 起默认 null）。JSON Int64 number。 */
  maxAssetBytes: number | null;
  /** 每个 client 的资源总字节上限；null 同上。 */
  maxAssetQuotaBytes: number | null;
  assetMimeTypes: string[];
  features: string[];
  serverTimeUtc: IsoDateTime;
  maxCommandRetryWindowSeconds: number;
  maxCommandReceiptsPerSession: number;
  clientTokenLifetimeSeconds: number;
  clientLeaseLifetimeSeconds: number;
  maxClients: number;
  maxSessionsPerClient: number;
  unreferencedAssetLifetimeSeconds: number;
  maxEventsPerClient: number;
  maxCommandReceiptsPerClient: number;
  maxReceiptBytes: number;
  maxReceiptBytesPerClient: number;
  maxUploadPreparations: number;
  maxUploadPreparationsPerClient: number;
  maxEventConnections: number;
  maxEventConnectionsPerClient: number;
  maxAssets: number;
  /** 可空尾部对象；enabled=false 时新建有音频工程不可用（交接 §3.3）。 */
  audioPreparation?: AudioPreparationCapabilities;
}

export interface ClientRegistrationRequest {
  protocolVersion?: '1';
  resumeClientId?: string | null;
  resumeToken?: string | null;
}

export interface ClientRegistrationResponse {
  clientId: string;
  hostInstanceId: string;
  accessToken: string;
  resumeToken: string;
  tokenExpiresAtUtc: IsoDateTime;
  leaseExpiresAtUtc: IsoDateTime;
  serverTimeUtc: IsoDateTime;
}

export interface ClientHeartbeatResponse {
  clientId: string;
  hostInstanceId: string;
  tokenExpiresAtUtc: IsoDateTime;
  leaseExpiresAtUtc: IsoDateTime;
  serverTimeUtc: IsoDateTime;
}

export interface ClientStateResponse {
  clientId: string;
  hostInstanceId: string;
  dormant: boolean;
  leaseExpiresAtUtc: IsoDateTime;
  eventCursor: string;
  sessions: ProjectSessionSummary[];
  jobs?: JobSummary[];
}
