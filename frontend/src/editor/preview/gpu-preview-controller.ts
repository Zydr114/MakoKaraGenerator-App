import { ObservableValue, type MakoClient } from '../../client';
import type { DesktopPreviewBridge, GpuDiagnosticsSummary, GpuPlaybackState, GpuPreviewStreamState, PreviewSize, Revision } from '../../protocol';

export interface GpuPreviewViewState {
  status: 'idle' | 'rendering' | 'ready' | 'error';
  frameRevision?: Revision;
  frameTimeUs?: number;
  error?: string;
  diagnostics?: GpuDiagnosticsSummary;
}
interface Want { revision: Revision; playback: GpuPlaybackState }
// React cleanup cannot await. Serialize ownership across old and new panels so
// an in-flight create must close before another panel consumes the one-stream quota.
let desktopOperations: Promise<void> = Promise.resolve();
let acceptanceOwner: GpuPreviewController | undefined;

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
  private attached = false;
  private needsRecreate = false;
  /** 背景媒体特征分析推进后，同一修订的固定输入需要重新捕获。 */
  private inputStale = false;
  private readonly timings:{kind:string;streamId:string;inputGeneration:string;sourceRevision:string;rendererPerformanceMs:number}[]=[];
  private inputEffective(kind:string):void {
    if(!this.stream)return;
    this.timings.push({kind,streamId:this.stream.streamId,inputGeneration:this.stream.inputGeneration,sourceRevision:this.stream.source.sourceRevision,rendererPerformanceMs:performance.now()});
    if(this.timings.length>128)this.timings.shift();
  }
  private readonly unsubscribe: () => void;
  constructor(private readonly client: MakoClient, private readonly projectSessionId: string, private size: PreviewSize, private readonly bridge: DesktopPreviewBridge,
    private readonly acceptanceEdit?: (red:number,green:number,blue:number,alpha:number)=>Promise<{sourceRevision:Revision}>) {
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
      if(event.type==='failed'){this.blocked=true;this.needsRecreate=true;this.state$.set({...this.state$.get(),status:'error',error:event.code??'GPU 预览失败'});}
      if(event.type==='drawn'&&current&&event.streamId===current.streamId&&event.producerEpoch===current.producerEpoch&&event.inputGeneration===current.inputGeneration&&event.timelineResetId===this.want?.playback.timelineResetId&&event.timeUs!==undefined){
        this.state$.set({status:'ready',frameRevision:current.source.sourceRevision as Revision,frameTimeUs:event.timeUs,...(event.diagnostics?{diagnostics:event.diagnostics}:{})});
      }
    });
  }
  show(revision: Revision, playback: GpuPlaybackState): void {if(this.disposed)return;this.want={revision,playback};this.pump();}
  setEnabled(value: boolean): void {if(this.enabled===value)return;this.enabled=value;this.sent=undefined;this.pump();}
  retry(): void {this.blocked=false;this.sent=undefined;this.pump();}
  /** 背景媒体特征分析推进：按同一修订替换固定输入。 */
  refreshInput(): void {if(this.disposed||!this.stream)return;this.inputStale=true;this.sent=undefined;this.pump();}
  dispose(): void {this.disposed=true;this.unsubscribe();if(acceptanceOwner===this){acceptanceOwner=undefined;delete window.makoGpuPreviewAcceptance;}this.pump();}
  private resizeForAcceptance(width:number,height:number):Promise<GpuPreviewStreamState> {
    if(!Number.isInteger(width)||!Number.isInteger(height)||width<64||height<64||width>2560||height>1440)return Promise.reject(new Error('Invalid acceptance preview size'));
    const task=desktopOperations.then(async()=>{
      if(this.disposed||!this.enabled||!this.stream)throw new Error('No active acceptance preview');
      if(this.attached){await this.bridge.detachPreview();this.attached=false;}
      this.stream=await this.client.gpuPreview.resize(this.stream.streamId,{expectedStreamRevision:this.stream.streamRevision,size:{width,height}});
      this.inputEffective('resize-response');
      this.size={width,height};this.sent=undefined;
      if(this.disposed||!this.enabled){await this.close();throw new Error('Acceptance panel closed during resize');}
      await this.bridge.attachPreview(this.stream.streamId,this.stream.inputGeneration);this.attached=true;
      return this.stream;
    });
    desktopOperations=task.then(()=>{},()=>{});
    void task.finally(()=>this.pump()).catch(()=>{});
    return task;
  }
  private pump(): void {
    if(this.running)return;
    this.running=true;
    const task=desktopOperations.then(()=>this.run());
    desktopOperations=task.catch(()=>{});
  }
  private async close(): Promise<void> {
    const current=this.stream;
    if(!current)return;
    if(this.attached){await this.bridge.detachPreview();this.attached=false;}
    await this.client.gpuPreview.close(current.streamId,current.streamRevision);
    this.stream=undefined;this.sent=undefined;
  }
  private async run(): Promise<void> {
    try{
      for(;;){
        if(this.disposed||!this.enabled){await this.close();break;}
        if(this.needsRecreate){await this.close();this.needsRecreate=false;}
        const want=this.want;
        if(!want||this.blocked||this.sent===want)break;
        if(!this.stream){
          this.state$.set({...this.state$.get(),status:'rendering'});
          const consumerId=await this.bridge.consumerId();
          // This is the full composition preview. Straight alpha requests a
          // transparent lyrics layer and deliberately omits the project background.
          this.stream=await this.client.gpuPreview.create({source:{kind:'project',sourceId:this.projectSessionId,sourceRevision:want.revision},size:this.size,alphaMode:'opaque',api:'vulkan',consumerId});
          this.inputEffective('create-response');
          if(this.disposed||!this.enabled)continue;
          await this.bridge.attachPreview(this.stream.streamId,this.stream.inputGeneration);
          this.attached=true;
        }else if(this.stream.source.sourceRevision!==want.revision||this.inputStale){
          this.inputStale=false;
          await this.bridge.detachPreview();
          this.attached=false;
          this.stream=await this.client.gpuPreview.replaceInput(this.stream.streamId,{expectedStreamRevision:this.stream.streamRevision,source:{kind:'project',sourceId:this.projectSessionId,sourceRevision:want.revision}});
          this.inputEffective('input-replacement-response');
          if(this.disposed||!this.enabled)continue;
          await this.bridge.attachPreview(this.stream.streamId,this.stream.inputGeneration);
          this.attached=true;
        }
        if(this.disposed||!this.enabled)continue;
        // A newer UI state can supersede this snapshot during an HTTP operation.
        if(this.want!==want)continue;
        await this.bridge.setPlaybackState(want.playback);
        this.sent=want;
      }
    }catch(error){
      this.blocked=true;this.needsRecreate=true;
      this.state$.set({...this.state$.get(),status:'error',error:error instanceof Error?error.message:String(error)});
      // Dispose during HTTP must still reclaim the eventual result.
      if(this.disposed||!this.enabled)try{await this.close();}catch{/* Keep the error visible; the owner lease remains the final backstop. */}
    }
    finally{this.running=false;}
  }
}
