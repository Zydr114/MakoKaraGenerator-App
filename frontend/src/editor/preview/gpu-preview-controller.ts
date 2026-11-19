import { BackendError, ObservableValue, OutcomeUnknownError, type MakoClient } from '../../client';
import type { DesktopPreviewBridge, GpuDiagnosticsSummary, GpuPlaybackState, GpuPreviewStreamState, PreviewSize, PreviewStreamApi, PreviewStreamTransport, Revision } from '../../protocol';

export interface GpuPreviewViewState {
  status: 'idle' | 'rendering' | 'ready' | 'error';
  api?: PreviewStreamApi;
  transport?: PreviewStreamTransport;
  frameRevision?: Revision;
  frameTimeUs?: number;
  error?: string;
  diagnostics?: GpuDiagnosticsSummary;
}
interface Want { revision: Revision; playback: GpuPlaybackState }

function sameSize(actual: PreviewSize | undefined, desired: PreviewSize): boolean {
  return actual?.width === desired.width && actual.height === desired.height;
}

class QuarantinedStreamError extends Error {
  constructor(failureCode?: string | null) {
    super(
      failureCode
        ? `GPU 预览已进入故障隔离（${failureCode}），请重启应用后重试。`
        : 'GPU 预览已进入故障隔离，请重启应用后重试。',
    );
  }
}

const QUARANTINED_FAILURE_CODES = new Set([
  'GpuStreamQuarantined',
  'GpuConsumerCompletionUnknown',
  'GpuStreamOpenQuarantined',
]);

function isAuthorityAmbiguousError(error: unknown): boolean {
  if (error instanceof OutcomeUnknownError) return true;
  if (error instanceof BackendError) {
    return error.code === 'sessionQuotaExceeded' || error.status === 429 || error.status >= 500;
  }
  return false;
}

function isQuotaExceededError(error: unknown): boolean {
  return error instanceof BackendError && (error.code === 'sessionQuotaExceeded' || error.status === 429);
}

const nextAnimationFrame = (): Promise<void> =>
  typeof requestAnimationFrame === 'function'
    ? new Promise((resolve) => requestAnimationFrame(() => resolve()))
    : Promise.resolve();

function formatStreamError(error: unknown): string {
  if (error instanceof BackendError && error.body?.failureCode) {
    return `${error.message}（${error.body.failureCode}）`;
  }
  return error instanceof Error ? error.message : String(error);
}

/** Refreshes stale playback clock samples after long-running stream setup to avoid the 30s preload expiration gate. */
function freshPlayback(playback: GpuPlaybackState): GpuPlaybackState {
  const now = performance.now();
  const ageMs = now - playback.samplePerformanceMs;
  if (ageMs <= 1000) return playback;
  if (playback.mode === 'paused') return { ...playback, samplePerformanceMs: now };
  return {
    ...playback,
    mediaTimeUs: Math.max(0, Math.min(Number.MAX_SAFE_INTEGER, playback.mediaTimeUs + Math.round(ageMs * 1000 * playback.rate))),
    samplePerformanceMs: now,
  };
}
// React cleanup cannot await. Serialize ownership across old and new panels so
// an in-flight create must close before another panel consumes the one-stream quota.
let desktopOperations: Promise<void> = Promise.resolve();
let acceptanceOwner: GpuPreviewController | undefined;
const activeControllers = new Set<GpuPreviewController>();

/** 等待所有排队的桌面连续预览操作和关闭排空完成。 */
export async function flushDesktopPreviewOperations(): Promise<void> {
  await desktopOperations;
}

/** 关闭工程前先暂停对应工程的活动预览并等待关闭完成，若关闭被中止可调用返回函数恢复。 */
export async function suspendProjectPreviewForClose(projectSessionId: string): Promise<() => void> {
  const paused: GpuPreviewController[] = [];
  for (const controller of activeControllers) {
    if (controller.matchesProject(projectSessionId) && controller.isEnabled()) {
      controller.setEnabled(false);
      paused.push(controller);
    }
  }
  await desktopOperations;
  return () => {
    for (const controller of paused) {
      controller.setEnabled(true);
    }
  };
}

/** One public stream, one attached consumer and a latest-only control mailbox. */
export class GpuPreviewController {
  readonly state$ = new ObservableValue<GpuPreviewViewState>({ status:'idle' });
  private stream: GpuPreviewStreamState | undefined;
  private want: Want | undefined;
  private sent: Want | undefined;
  private enabled = true;
  private disposed = false;
  private running = false;
  private blocked = false;
  private quarantined = false;
  private attached = false;
  private needsRecreate = false;
  private needsReconcile = false;
  /** 背景媒体特征分析推进后，同一修订的固定输入需要重新捕获。 */
  private inputStale = false;
  private readonly timings:{kind:string;streamId:string;inputGeneration:string;sourceRevision:string;rendererPerformanceMs:number}[]=[];
  private inputEffective(kind:string):void {
    if(!this.stream)return;
    this.timings.push({kind,streamId:this.stream.streamId,inputGeneration:this.stream.inputGeneration,sourceRevision:this.stream.source.sourceRevision,rendererPerformanceMs:performance.now()});
    if(this.timings.length>128)this.timings.shift();
  }
  private readonly unsubscribe: () => void;
  constructor(private readonly client: MakoClient, private readonly projectSessionId: string, private size: PreviewSize | undefined, private readonly bridge: DesktopPreviewBridge,
    private readonly acceptanceEdit?: (red:number,green:number,blue:number,alpha:number)=>Promise<{sourceRevision:Revision}>,
    private readonly api: PreviewStreamApi = 'vulkan') {
    this.state$.set({ status: 'idle', api });
    activeControllers.add(this);
    acceptanceOwner=this;
    void bridge.acceptanceEnabled().then(enabled=>{
      if(enabled&&!this.disposed&&acceptanceOwner===this)window.makoGpuPreviewAcceptance={resize:(width,height)=>this.resizeForAcceptance(width,height),current:()=>this.stream,
        timings:()=>this.timings.slice(),
        read:()=>{if(!this.stream)throw new Error('No active acceptance stream');return this.client.gpuPreview.read(this.stream.streamId);},
        setBackground:async(red,green,blue,alpha)=>{
          if(this.disposed||!this.acceptanceEdit||![red,green,blue,alpha].every(value=>Number.isInteger(value)&&value>=0&&value<=255))throw new Error('Invalid acceptance background edit');
          return this.acceptanceEdit(red,green,blue,alpha);
        }};
    });
    this.unsubscribe=bridge.onPreviewState(event=>{
      if(this.disposed)return;
      const current=this.stream;
      if(event.streamId!==undefined&&(!current||event.streamId!==current.streamId||event.producerEpoch!==current.producerEpoch||event.inputGeneration!==current.inputGeneration))return;
      if(event.diagnostics)this.state$.set({...this.state$.get(),diagnostics:event.diagnostics});
      if(event.type==='failed'){this.blocked=true;this.needsRecreate=true;this.needsReconcile=true;this.state$.set({...this.state$.get(),status:'error',error:event.code??'连续预览失败'});}
      if(event.type==='drawn'&&this.attached&&current&&event.streamId===current.streamId&&event.producerEpoch===current.producerEpoch&&event.inputGeneration===current.inputGeneration&&event.timelineResetId===this.want?.playback.timelineResetId&&event.timeUs!==undefined){
        this.state$.set({status:'ready',api:current.api,transport:current.transport,frameRevision:current.source.sourceRevision as Revision,frameTimeUs:event.timeUs,...(event.diagnostics?{diagnostics:event.diagnostics}:{})});
      }
    });
  }
  matchesProject(projectSessionId: string): boolean { return !this.disposed && this.projectSessionId === projectSessionId; }
  isEnabled(): boolean { return this.enabled; }
  show(revision: Revision, playback: GpuPlaybackState): void {if(this.disposed)return;this.want={revision,playback};this.pump();}
  setSize(size: PreviewSize): void {if(this.disposed||sameSize(this.size,size))return;this.size=size;this.pump();}
  setEnabled(value: boolean): void {if(this.enabled===value)return;this.enabled=value;this.sent=undefined;this.pump();}
  retry(): void {if(this.disposed||this.quarantined)return;this.blocked=false;this.sent=undefined;this.pump();}
  /** 背景媒体特征分析推进：按同一修订替换固定输入。 */
  refreshInput(): void {if(this.disposed||!this.stream)return;this.inputStale=true;this.sent=undefined;this.pump();}
  dispose(): void {this.disposed=true;activeControllers.delete(this);this.unsubscribe();if(acceptanceOwner===this){acceptanceOwner=undefined;delete window.makoGpuPreviewAcceptance;}this.pump();}
  private async resizeForAcceptance(width:number,height:number):Promise<GpuPreviewStreamState> {
    if(!Number.isInteger(width)||!Number.isInteger(height)||width<64||height<64||width>2560||height>1440)throw new Error('Invalid acceptance preview size');
    if(this.disposed||!this.enabled||!this.stream)throw new Error('No active acceptance preview');
    const size={width,height};
    this.setSize(size);
    await flushDesktopPreviewOperations();
    if(this.disposed||!this.enabled)throw new Error('Acceptance panel closed during resize');
    if(this.blocked)throw new Error(this.state$.get().error??'Acceptance resize failed');
    if(!this.stream||!this.attached||!sameSize(this.stream.size,size))throw new Error('Acceptance resize was superseded');
    return this.stream;
  }
  private pump(): void {
    if(this.running)return;
    this.running=true;
    const task=desktopOperations.then(()=>this.run());
    desktopOperations=task.catch(()=>{});
  }
  private async detachIfAttached(): Promise<void> {
    if(!this.attached)return;
    await this.bridge.detachPreview();
    this.attached=false;
  }
  private async closeSingleStream(streamId: string, expectedRevision: string): Promise<void> {
    try{
      await this.client.gpuPreview.close(streamId,expectedRevision);
      return;
    }catch(error){
      if(error instanceof BackendError&&error.code==='sessionNotFound')return;
      if(error instanceof BackendError&&error.failureCode&&QUARANTINED_FAILURE_CODES.has(error.failureCode)){
        throw new QuarantinedStreamError(error.failureCode);
      }
      if(error instanceof BackendError&&error.code==='revisionConflict'){
        let latest:GpuPreviewStreamState;
        try{
          latest=await this.client.gpuPreview.read(streamId);
        }catch(readError){
          if(readError instanceof BackendError&&readError.code==='sessionNotFound')return;
          throw readError;
        }
        if(latest.quarantined)throw new QuarantinedStreamError(latest.failureCode);
        try{
          await this.client.gpuPreview.close(latest.streamId,latest.streamRevision);
          return;
        }catch(retryError){
          if(retryError instanceof BackendError&&retryError.code==='sessionNotFound')return;
          if(retryError instanceof BackendError&&retryError.failureCode&&QUARANTINED_FAILURE_CODES.has(retryError.failureCode)){
            throw new QuarantinedStreamError(retryError.failureCode);
          }
          throw retryError;
        }
      }
      throw error;
    }
  }
  private async close(): Promise<void> {
    const current=this.stream;
    await this.detachIfAttached();
    if(!current)return;
    try{
      await this.closeSingleStream(current.streamId,current.streamRevision);
    }finally{
      this.stream=undefined;
      this.sent=undefined;
    }
  }
  private async reconcileAuthority(): Promise<{ hadStreams: boolean }> {
    const listFn=this.client.gpuPreview.list;
    if(!listFn){
      const hadLocal=Boolean(this.stream);
      await this.close();
      return {hadStreams:hadLocal};
    }
    const {streams}=await listFn();
    const quarantined=streams.find(item=>item.quarantined===true);
    if(quarantined){
      await this.detachIfAttached();
      this.stream=undefined;
      this.sent=undefined;
      throw new QuarantinedStreamError(quarantined.failureCode);
    }
    await this.detachIfAttached();
    for(const item of streams){
      await this.closeSingleStream(item.streamId,item.streamRevision);
    }
    this.stream=undefined;
    this.sent=undefined;
    return {hadStreams:streams.length>0};
  }
  private applyErrorState(message: string): void {
    const failedTransport=this.stream?.transport??this.state$.get().transport;
    this.state$.set({...this.state$.get(),status:'error',api:this.stream?.api??this.api,...(failedTransport?{transport:failedTransport}:{}),error:message});
  }
  private async run(): Promise<void> {
    let autoRecovered=false;
    try{
      for(;;){
        try{
          for(;;){
            if(this.quarantined)return;
            if(this.disposed||!this.enabled){
              if(this.needsReconcile){await this.reconcileAuthority();this.needsReconcile=false;this.needsRecreate=false;}
              else{await this.close();this.needsRecreate=false;}
              if(!this.disposed&&this.enabled)continue;
              return;
            }
            if(this.needsReconcile){
              await this.reconcileAuthority();
              this.needsReconcile=false;
              this.needsRecreate=false;
            }else if(this.needsRecreate){
              await this.close();
              this.needsRecreate=false;
            }
            const want=this.want;
            const size=this.size;
            if(!want||!size||this.blocked)return;
            if(!this.stream){
              this.state$.set({...this.state$.get(),status:'rendering',api:this.api});
              const consumerId=await this.bridge.consumerId();
              if(this.disposed||!this.enabled||this.size!==size||this.want?.revision!==want.revision)continue;
              // This is the full composition preview. Straight alpha requests a
              // transparent lyrics layer and deliberately omits the project background.
              this.stream=await this.client.gpuPreview.create({source:{kind:'project',sourceId:this.projectSessionId,sourceRevision:want.revision},size,alphaMode:'opaque',api:this.api,consumerId});
              this.inputEffective('create-response');
            }else if(this.stream.source.sourceRevision!==want.revision||this.inputStale||!sameSize(this.stream.size,size)){
              this.sent=undefined;
              await this.detachIfAttached();
              if(this.disposed||!this.enabled||this.blocked||this.size!==size||this.want?.revision!==want.revision)continue;
              if(this.stream.source.sourceRevision!==want.revision||this.inputStale){
                this.inputStale=false;
                this.stream=await this.client.gpuPreview.replaceInput(this.stream.streamId,{expectedStreamRevision:this.stream.streamRevision,source:{kind:'project',sourceId:this.projectSessionId,sourceRevision:want.revision}});
                this.inputEffective('input-replacement-response');
              }else{
                const { api, transport }=this.stream;
                this.state$.set({status:'rendering',api,transport});
                this.stream=await this.client.gpuPreview.resize(this.stream.streamId,{expectedStreamRevision:this.stream.streamRevision,size});
                this.inputEffective('resize-response');
              }
            }
            const stream=this.stream;
            if(this.disposed||!this.enabled||this.blocked||this.size!==size||!sameSize(stream.size,size)||stream.source.sourceRevision!==this.want?.revision||this.inputStale)continue;
            if(!this.attached){
              const previousTransport=this.state$.get().transport;
              this.state$.set({...this.state$.get(),api:stream.api,transport:stream.transport});
              // Ensure <canvas key={transport}> has committed before attaching the consumer.
              if(previousTransport!==stream.transport){
                await nextAnimationFrame();
                if(this.disposed||!this.enabled||this.blocked||this.size!==size||stream.source.sourceRevision!==this.want?.revision||this.inputStale)continue;
              }
              await this.bridge.attachPreview(stream.streamId,stream.inputGeneration);
              this.attached=true;
            }
            // A newer UI state can supersede this snapshot during an HTTP operation.
            if(this.disposed||!this.enabled||this.blocked||this.size!==size||this.want!==want||this.inputStale)continue;
            if(this.sent===want)return;
            await this.bridge.setPlaybackState(freshPlayback(want.playback));
            this.sent=want;
          }
        }catch(error){
          if(error instanceof QuarantinedStreamError||(error instanceof BackendError&&error.failureCode&&QUARANTINED_FAILURE_CODES.has(error.failureCode))){
            this.blocked=true;this.quarantined=true;this.needsRecreate=false;this.needsReconcile=false;
            this.applyErrorState(error instanceof QuarantinedStreamError?error.message:new QuarantinedStreamError(error.failureCode).message);
            return;
          }
          if(!this.disposed&&this.enabled&&!autoRecovered&&isAuthorityAmbiguousError(error)){
            autoRecovered=true;
            try{
              const {hadStreams}=await this.reconcileAuthority();
              this.needsReconcile=false;this.needsRecreate=false;
              if(!(isQuotaExceededError(error)&&!hadStreams))continue;
            }catch(reconcileError){
              if(reconcileError instanceof QuarantinedStreamError){
                this.blocked=true;this.quarantined=true;this.needsRecreate=false;this.needsReconcile=false;
                this.applyErrorState(reconcileError.message);
                return;
              }
            }
          }
          this.blocked=true;this.needsRecreate=true;this.needsReconcile=true;
          this.applyErrorState(formatStreamError(error));
          // Dispose during HTTP must still reclaim the eventual result.
          if(this.disposed||!this.enabled){
            try{await this.reconcileAuthority();this.needsReconcile=false;this.needsRecreate=false;}
            catch{/* Keep the error visible; the owner lease remains the final backstop. */}
          }
          return;
        }
      }
    }finally{this.running=false;}
  }
}
