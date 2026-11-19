import { useEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { ObservableValue } from '../../client';
import type { PreviewStreamApi, PreviewStreamTransport, Revision } from '../../protocol';
import type { AudioController } from '../audio/audio-controller';
import { client, renderDevice } from '../connection';
import { previewFallbackText } from '../model/background-fallback';
import { previewStreamBadge, RENDER_BACKEND_LABEL, resolveChoice, resolvePreviewStreamApi, usesGpuStream } from '../model/render-device';
import { formatTime } from '../model/time';
import type { EditorSession } from '../session/editor-session';
import { Button } from '../ui/Button';
import { Select } from '../ui/fields';
import { useObservable } from '../ui/hooks';
import { fitPreviewSize, PreviewController, type PreviewViewState } from './preview-controller';
import { GpuPreviewController, type GpuPreviewViewState } from './gpu-preview-controller';
import './preview.css';

/** 预览用的时刻：播放头，留在音频末尾之前 1 毫秒；没有音频时是 0。 */
export function previewTimeUs(audio: AudioController | null): number {
  if (!audio) return 0;
  const durationUs = audio.state.durationUs ?? 0;
  return Math.max(0, Math.min(audio.playheadUs(), durationUs > 0 ? durationUs - 1000 : Infinity));
}

/** 播放中最多每 500 毫秒请求一帧（W1 设计 §4.7，远低于回执容量）。 */
const PLAYBACK_INTERVAL_MS = 500;

/** 画面上正在显示的成片帧。 */
export interface ShownFrame {
  revision: Revision;
  timeUs: number;
}

/** 换掉成片帧显示的另一张图（编排页的布局参考，W4 设计 §13.8）：这期间不请求成片帧。 */
export interface PreviewReplacement {
  title: string;
  imageUrl: string | undefined;
  /** 还没有图时画面中间的文字。 */
  placeholder: string;
  hint: string;
}

/**
 * 成片预览（W3 设计 §2.7）：当前修订在播放头时刻的真实渲染帧。暂停或定位时请求播放头处的帧，播放中低频刷新；
 * 编辑后自动换输入。失败时保留上一帧，标题写明它是哪个设备、哪个修订、哪个时刻的画面。
 * 渲染设备偏好变了就给已开的会话换设备（渲染设备设计 §2.3）。
 * overlay 画在画面上面（编排页的叠加层，W4 设计 §13.6），拿到正在显示的帧；replacement 换掉成片帧。
 * 桌面版按 usesGpuStream 换成 GPU 共享纹理的连续预览；浏览器里始终是静态预览。
 */
export function PreviewPanel(props: Parameters<typeof StaticPreviewPanel>[0]) {
  const device = useObservable(renderDevice.state$);
  useObservable(props.session.state$);
  const gpu = !!window.makoDesktopPreview && usesGpuStream(device.preference, device.backends, props.session.supports('preview.gpu-streams'));
  return gpu ? <GpuPreviewPanel {...props} /> : <StaticPreviewPanel {...props} />;
}

function StaticPreviewPanel(props: {
  session: EditorSession;
  audio: AudioController | null;
  warning?: string | undefined;
  replacement?: PreviewReplacement | undefined;
  overlay?: ((shown: ShownFrame | undefined) => ReactNode) | undefined;
}) {
  const { session, audio } = props;
  const state = useObservable(session.state$);
  const available = session.supports('preview.create.static-project') && session.supports('preview.frame-requests');
  const canvasWidth = state.settings?.canvasWidth;
  const canvasHeight = state.settings?.canvasHeight;
  const size = useMemo(() => fitPreviewSize(canvasWidth, canvasHeight), [canvasWidth, canvasHeight]);

  const device = useObservable(renderDevice.state$);
  const choice = resolveChoice(device.preference, device.backends);

  const [controller, setController] = useState<PreviewController | null>(null);
  useEffect(() => {
    if (!available) return;
    const next = new PreviewController(client, session.projectSessionId, size, renderDevice.choice());
    setController(next);
    return () => next.dispose();
  }, [available, session, size]);
  useEffect(() => {
    controller?.setRenderBackend({ request: choice.request, target: choice.target });
  }, [controller, choice.request, choice.target]);
  useEffect(() => onMediaFeaturesChanged(session.projectSessionId, () => controller?.refreshInput()), [controller, session]);

  // 每 250 毫秒看一次：暂停时播放头或修订变了就请求；播放中按间隔请求。没有音频时只看 0 时刻。换成别的图时不请求。
  const last = useRef({ timeUs: -1, revision: '', at: 0 });
  const replaced = useRef(false);
  replaced.current = !!props.replacement;
  useEffect(() => {
    if (!controller) return;
    last.current = { timeUs: -1, revision: '', at: 0 };
    const timer = setInterval(() => {
      if (replaced.current || (audio && audio.state.status !== 'ready')) return;
      const timeUs = previewTimeUs(audio);
      const revision = session.latestRevision;
      const now = performance.now();
      const changed = timeUs !== last.current.timeUs || revision !== last.current.revision;
      const due = audio?.playing ? now - last.current.at >= PLAYBACK_INTERVAL_MS : changed;
      if (!due || !changed) return;
      last.current = { timeUs, revision, at: now };
      controller.show(timeUs, revision);
    }, 250);
    return () => clearInterval(timer);
  }, [controller, audio, session]);

  const view = useObservable(controller?.state$ ?? IDLE);
  const stale = view.frameRevision !== undefined && view.frameRevision !== state.summary.projectRevision;
  const shown = view.frameUrl && view.frameRevision !== undefined && view.frameTimeUs !== undefined ? { revision: view.frameRevision, timeUs: view.frameTimeUs } : undefined;
  const replacement = props.replacement;

  if (replacement) {
    return (
      <div className="preview-panel">
        <div className="preview-title">
          <span className="pane-title-text">{replacement.title}</span>
        </div>
        <PreviewStage size={size}>
          {replacement.imageUrl && <img src={replacement.imageUrl} alt={replacement.title} />}
          {!replacement.imageUrl && <span className="preview-overlay">{replacement.placeholder}</span>}
          {props.overlay?.(undefined)}
        </PreviewStage>
        <p className="hint">{replacement.hint}</p>
      </div>
    );
  }

  return (
    <div className="preview-panel">
      <div className="preview-title">
        <span className="pane-title-text">
          {view.frameUrl && view.frameTimeUs !== undefined
            ? `成片 · ${view.frameBackend ? `${RENDER_BACKEND_LABEL[view.frameBackend]} · ` : ''}修订 ${view.frameRevision} · ${formatTime(view.frameTimeUs)}`
            : '成片'}
        </span>
        <span className="muted">
          {view.status === 'rendering' ? '渲染中' : stale && view.status === 'error' ? '这是旧修订的画面' : ''}
        </span>
      </div>
      <PreviewStage size={size}>
        {view.frameUrl && <img src={view.frameUrl} alt="成片预览" />}
        {!available && <span className="preview-overlay">后端没有开放预览（未配置渲染 Worker）。</span>}
        {available && !view.frameUrl && view.status !== 'error' && <span className="preview-overlay">正在准备预览……</span>}
        {view.status === 'error' && !view.frameUrl && (
          <span className="preview-overlay notice-error">
            {view.error}
            {view.deviceBlocked && <RetryButton onRetry={() => controller?.retry()} />}
          </span>
        )}
        {props.overlay?.(shown)}
      </PreviewStage>
      {view.status === 'error' && view.frameUrl && (
        <p className="notice-error preview-error">
          {view.deviceBlocked ? view.error : `预览失败：${view.error}`}
          {view.deviceBlocked && <RetryButton onRetry={() => controller?.retry()} />}
        </p>
      )}
      <p className="hint">
        {`静态预览 · 画布 ${state.settings ? `${state.settings.canvasWidth}×${state.settings.canvasHeight}` : '未知'} · 预览 ${size.width}×${size.height}。`}
        {props.warning && <span className="notice-warning"> {props.warning}</span>}
      </p>
      {view.fallback && (
        <p className="hint notice-warning preview-fallback" role="status">
          {view.fallback}
        </p>
      )}
    </div>
  );
}

const IDLE = new ObservableValue<PreviewViewState>({ status: 'idle' });
const GPU_IDLE = new ObservableValue<GpuPreviewViewState>({ status:'idle' });
const GPU_QUALITIES = [{ value:'720p24', label:'720p · 24 fps' }, { value:'1440p30', label:'1440p · 30 fps' }] as const;

function GpuPreviewPanel(props: Parameters<typeof StaticPreviewPanel>[0]) {
  const state=useObservable(props.session.state$);
  const device=useObservable(renderDevice.state$);
  const available=props.session.supports('preview.gpu-streams');
  const api: PreviewStreamApi = resolvePreviewStreamApi(device.preference, device.backends);
  const [defaultTransport, setDefaultTransport] = useState<PreviewStreamTransport>('dma-buf');
  useEffect(() => {
    let active = true;
    void window.makoDesktopPreview?.previewConfig?.().then((cfg) => {
      if (active && (cfg?.transport === 'shm' || cfg?.transport === 'dma-buf')) {
        setDefaultTransport(cfg.transport);
      }
    }).catch(() => {});
    return () => { active = false; };
  }, []);
  const [quality,setQuality]=useState<'720p24'|'1440p30'>('720p24');
  const targetFps=quality==='1440p30'?30:24;
  const size=useMemo(()=>{
    const max=quality==='1440p30'?{width:2560,height:1440}:{width:1280,height:720};
    const width=state.settings?.canvasWidth??1920,height=state.settings?.canvasHeight??1080;
    const scale=Math.min(max.width/width,max.height/height);
    return {width:Math.max(64,Math.floor(width*scale/2)*2),height:Math.max(64,Math.floor(height*scale/2)*2)};
  },[state.settings?.canvasWidth,state.settings?.canvasHeight,quality]);
  const [controller,setController]=useState<GpuPreviewController|null>(null);
  const [visible,setVisible]=useState(!document.hidden);
  useEffect(()=>{
    const changed=()=>setVisible(!document.hidden);
    document.addEventListener('visibilitychange',changed);
    return()=>document.removeEventListener('visibilitychange',changed);
  },[]);
  useEffect(()=>{
    if(!available)return;
    const next=new GpuPreviewController(client,props.session.projectSessionId,size,window.makoDesktopPreview!,async(red,green,blue,alpha)=>{
      const response=await props.session.command({type:'composition.setBackgroundColor',red,green,blue,alpha});
      if(!response)throw new Error('Background edit rejected');
      return {sourceRevision:response.projectRevision};
    },api);
    setController(next);
    return()=>next.dispose();
  },[available,props.session,size,api]);
  useEffect(()=>{controller?.setEnabled(visible&&!props.replacement);},[controller,visible,props.replacement]);
  useEffect(()=>onMediaFeaturesChanged(props.session.projectSessionId,()=>controller?.refreshInput()),[controller,props.session]);
  useEffect(()=>{
    if(!controller)return;
    const audio=props.audio;
    let lastImmediate='';
    const update=(clockCorrection=false)=>{
      if(audio&&audio.state.status!=='ready')return;
      const mediaTimeUs=previewTimeUs(audio);
      const samplePerformanceMs=performance.now();
      const mode=audio?.playing?'playing':'paused';
      const rate=audio?.state.rate??1;
      const timelineResetId=`${props.session.projectSessionId}:${audio?.timelineReset$.get()??0}`;
      const revision=props.session.latestRevision;
      const signature=`${revision}/${mode}/${rate}/${targetFps}/${timelineResetId}/${mode==='paused'?mediaTimeUs:''}`;
      if(signature===lastImmediate&&(!clockCorrection||mode==='paused'))return;
      lastImmediate=signature;
      controller.show(revision,{mode,mediaTimeUs,rate,targetFps,timelineResetId,samplePerformanceMs});
    };
    update();
    // Audio is the master clock. Full state corrections remain at most 2 Hz;
    // explicit seek/loop, play/pause and rate changes are immediate events.
    const timer=setInterval(()=>update(true),PLAYBACK_INTERVAL_MS);
    const offAudio=audio?.state$.subscribe(()=>update());
    const offTimeline=audio?.timelineReset$.subscribe(()=>update());
    const offProject=props.session.state$.subscribe(()=>update());
    return()=>{clearInterval(timer);offAudio?.();offTimeline?.();offProject();};
  },[controller,props.audio,props.session,targetFps,visible]);
  const view=useObservable(controller?.state$??GPU_IDLE);
  const effectiveApi: PreviewStreamApi = view.api ?? api;
  const canvasTransport: PreviewStreamTransport = view.transport ?? (effectiveApi === 'cpu' ? 'shm' : defaultTransport);
  const badge = previewStreamBadge(effectiveApi, view.transport ?? canvasTransport);
  const shown=view.frameRevision!==undefined&&view.frameTimeUs!==undefined?{revision:view.frameRevision,timeUs:view.frameTimeUs}:undefined;
  const fallback=previewFallbackText(view.diagnostics?.items??[]);
  const replacement=props.replacement;
  if(replacement)return <div className="preview-panel"><div className="preview-title"><span className="pane-title-text">{replacement.title}</span></div><PreviewStage size={size}>{replacement.imageUrl?<img src={replacement.imageUrl} alt={replacement.title}/>:<span className="preview-overlay">{replacement.placeholder}</span>}{props.overlay?.(undefined)}</PreviewStage><p className="hint">{replacement.hint}</p></div>;
  return <div className="preview-panel">
    <div className="preview-title">
      <span className="pane-title-text">{shown?`成片 · ${badge} · 修订 ${shown.revision} · ${formatTime(shown.timeUs)}`:`成片 · ${badge}`}</span>
      <span className="muted">{view.status==='rendering'?'准备中':''}</span>
      <Select label="GPU 预览规格" labelPlacement="none" size="sm" className="preview-quality" value={quality} options={GPU_QUALITIES} onChange={setQuality}/>
    </div>
    <PreviewStage size={size}>
      <canvas id="mako-gpu-preview" key={canvasTransport} data-preview-transport={canvasTransport} data-preview-api={effectiveApi} width={size.width} height={size.height} style={{width:'100%',height:'100%',objectFit:'contain'}} aria-label={`${badge} 成片预览`}/>
      {!available&&<span className="preview-overlay notice-error">当前后端没有开放桌面连续预览。</span>}
      {available&&!shown&&view.status!=='error'&&<span className="preview-overlay">{`正在准备 ${badge} 预览……`}</span>}
      {view.status==='error'&&<span className="preview-overlay notice-error">{view.error}<RetryButton onRetry={()=>controller?.retry()}/></span>}
      {props.overlay?.(shown)}
    </PreviewStage>
    <p className="hint">{`${badge} 连续预览 · 预览 ${size.width}×${size.height} · 音频主时钟。`}{props.warning&&<span className="notice-warning"> {props.warning}</span>}</p>
    {fallback&&<p className="hint notice-warning preview-fallback" role="status">{fallback}</p>}
    {!!view.diagnostics?.total&&<div className={view.diagnostics.errorCount?'notice-error':'notice-warning'} role="status">{`渲染诊断：${view.diagnostics.warningCount} 个警告，${view.diagnostics.errorCount} 个错误。`}{view.diagnostics.items.map((item,index)=><p key={index}>{item.code}：{item.message}</p>)}{view.diagnostics.truncated&&<p>其余诊断已省略。</p>}</div>}
  </div>;
}

/** 画面区：占满面板剩余高度，画面按预览比例完整放进去，不需要滚动。 */
function PreviewStage(props: { size: { width: number; height: number }; children: ReactNode }) {
  const { width, height } = props.size;
  return (
    <div className="preview-stage">
      <div className="preview-frame" style={{ aspectRatio: `${width} / ${height}`, '--preview-ratio': width / height } as CSSProperties}>
        {props.children}
      </div>
    </div>
  );
}

/** 设备错误后预览停住，点这里再试一次（渲染设备设计 §2.3）。 */
function RetryButton(props: { onRetry: () => void }) {
  return (
    <Button variant="text" size="sm" className="preview-retry" onPress={props.onRetry}>
      重试
    </Button>
  );
}

/** 宿主背景媒体特征分析推进时，按同一修订刷新该工程的固定预览输入。 */
function onMediaFeaturesChanged(projectSessionId: string, refresh: () => void): () => void {
  return client.on('event', (event) => {
    if (event.type === 'project.mediaFeaturesChanged' && event.summary?.projectSessionId === projectSessionId) refresh();
  });
}
