import type { PreviewAlphaMode, PreviewSize, PreviewSource } from './preview';
import type { Revision } from './int64';

export type PreviewStreamApi = 'vulkan' | 'cpu';
export type PreviewStreamTransport = 'dma-buf' | 'shm';

/** Public owner-authenticated state. Native paths, handles and nonces never enter this DTO. */
export interface GpuPreviewStreamState {
  streamId: string;
  streamRevision: string;
  inputGeneration: string;
  registrationState: string;
  source: PreviewSource;
  size: PreviewSize;
  alphaMode: PreviewAlphaMode;
  api: PreviewStreamApi;
  transport: PreviewStreamTransport;
  producerEpoch: string;
  poolGeneration: string;
  slotCount: number;
  allocationBytes: number;
  lastAppliedControlSeq: number | null;
  textureAlphaMode: 'premultiplied';
  workerState?: {state:string;drained:boolean;diagnostics?:GpuDiagnosticsSummary} | null;
  failureCode?: string | null;
  quarantined?: boolean | null;
}
export interface GpuPreviewStreamListResponse {
  streams: GpuPreviewStreamState[];
  protocolVersion: string;
}
export interface GpuPreviewCreateInput {
  source: PreviewSource;
  size: PreviewSize;
  alphaMode: PreviewAlphaMode;
  api: PreviewStreamApi;
  consumerId: string;
}
export interface GpuPlaybackState {
  mode: 'paused' | 'playing';
  mediaTimeUs: number;
  rate: number;
  targetFps: number;
  timelineResetId: string;
  /** performance.now() sampled immediately beside the audio playhead read. */
  samplePerformanceMs: number;
}
export interface DesktopPreviewEvent {
  type: string;
  streamId?: string;
  producerEpoch?: string;
  inputGeneration?: string;
  timelineEpoch?: string;
  timelineResetId?: string;
  controlSeq?: number;
  timeUs?: number;
  frameSeq?: number;
  transport?: PreviewStreamTransport;
  api?: PreviewStreamApi;
  code?: string;
  diagnostics?: GpuDiagnosticsSummary;
}
export interface GpuDiagnosticsSummary {
  total: number;
  warningCount: number;
  errorCount: number;
  truncated: boolean;
  items: { severity:string;code:string;message:string;sourceId?:string;stage?:string }[];
}
export interface DesktopPreviewBridge {
  previewConfig?(): Promise<{ mode: string; transport: PreviewStreamTransport }>;
  consumerId(): Promise<string>;
  acceptanceEnabled(): Promise<boolean>;
  injectAcceptanceFailure(kind: 'renderer-loss' | 'worker-loss'): Promise<void>;
  setAcceptanceVisibility(visible:boolean): Promise<void>;
  attachPreview(streamId: string, expectedInputGeneration: string): Promise<unknown>;
  setPlaybackState(state: GpuPlaybackState): Promise<{ controlSeq: number }>;
  readPreviewState(sinceRevision?:number): Promise<unknown>;
  detachPreview(): Promise<void>;
  onPreviewState(listener: (event: DesktopPreviewEvent) => void): () => void;
}
declare global {
  interface Window {
    makoDesktopPreview?: DesktopPreviewBridge;
    makoGpuPreviewAcceptance?: {
      resize(width:number,height:number):Promise<GpuPreviewStreamState>;
      current():GpuPreviewStreamState|undefined;
      read():Promise<GpuPreviewStreamState>;
      timings():{kind:string;streamId:string;inputGeneration:string;sourceRevision:string;rendererPerformanceMs:number}[];
      setBackground(red:number,green:number,blue:number,alpha:number):Promise<{sourceRevision:Revision}>;
    };
  }
}
