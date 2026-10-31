// AudioController（W1 设计 §4.4、§4.5；W2 设计 §2.3）：鉴权读取工程音频 → Blob URL → <audio> 播放。
// 波形优先读后端（G11，按块懒加载）；后端不可用或波形中途失效时，用同一份字节在浏览器里解码算金字塔。不依赖 React。

import { BackendError, ObservableValue, type MakoClient } from '../../client';
import { BackendPeakSource } from '../model/backend-peaks';
import { buildPyramid, pyramidSource, type PeakSource } from '../model/peaks';
import { secondsToUs, usToSeconds } from '../model/time';

export interface AudioState {
  status: 'loading' | 'ready' | 'error';
  error?: string;
  /** 浏览器解码得到的时长（微秒）；元数据就绪前为 0。 */
  durationUs: number;
  playing: boolean;
  rate: number;
  /** 循环播放区间（例如“循环本行”）。 */
  loop?: { startUs: number; endUs: number } | undefined;
  /** 波形：取到元数据或解码完成前为 undefined；失败时播放仍可用。 */
  peaks?: PeakSource | undefined;
  peaksError?: string | undefined;
  /** 没用上后端波形的原因（改用浏览器解码时）。 */
  peaksFallback?: string | undefined;
}

export interface AudioSource {
  assetId: string;
  projectSessionId: string;
  /** 后端公布了波形读取能力（project-sessions.waveform.read 与 .peaks.read）。 */
  backendWaveform: boolean;
}

/** 后端说波形不可用的原因。 */
const UNAVAILABLE_TEXT: Record<string, string> = {
  noAudio: '工程没有音频',
  cacheBudgetExceeded: '后端波形缓存已满',
  audioIdentityChanged: '音频文件已变化',
};

export class AudioController {
  readonly state$ = new ObservableValue<AudioState>({ status: 'loading', durationUs: 0, playing: false, rate: 1 });
  private readonly audio: HTMLAudioElement;
  private url: string | undefined;
  private disposed = false;
  private loopTimer: number | undefined;
  private rangeTimer: number | undefined;

  constructor() {
    this.audio = new Audio();
    this.audio.preload = 'auto';
    this.audio.preservesPitch = true;
    const sync = () => this.patch({ playing: !this.audio.paused, durationUs: this.durationFromElement() });
    for (const type of ['play', 'pause', 'ended', 'durationchange', 'loadedmetadata']) this.audio.addEventListener(type, sync);
    this.audio.addEventListener('error', () => this.patch({ status: 'error', error: '浏览器无法播放这个音频。', playing: false }));
  }

  /** 读取资源并开始取波形。失败时 state.status 为 error。 */
  async load(client: MakoClient, source: AudioSource): Promise<void> {
    const content = client.assets.fetchContent(source.assetId);
    const blob = content.then((r) => r.blob ?? Promise.reject(new Error(`读取音频失败（HTTP ${r.status}）。`)));
    blob.catch(() => {}); // 用后端波形时没人等这份字节；读取失败由下面的 try 报告。
    void this.loadPeaks(client, source, blob);
    try {
      const result = await content;
      if (!result.blob) throw new Error(`读取音频失败（HTTP ${result.status}）。`);
      if (this.disposed) return;
      this.url = URL.createObjectURL(result.blob);
      this.audio.src = this.url;
      await new Promise<void>((resolve, reject) => {
        const done = () => {
          this.audio.removeEventListener('loadedmetadata', done);
          this.audio.removeEventListener('error', fail);
          resolve();
        };
        const fail = () => {
          this.audio.removeEventListener('loadedmetadata', done);
          this.audio.removeEventListener('error', fail);
          reject(new Error('浏览器无法播放这个音频。'));
        };
        this.audio.addEventListener('loadedmetadata', done);
        this.audio.addEventListener('error', fail);
      });
      this.patch({ status: 'ready', durationUs: this.durationFromElement() });
    } catch (error) {
      this.patch({ status: 'error', error: error instanceof Error ? error.message : String(error) });
    }
  }

  /** 后端波形可用就用它；否则（或中途失效）退回浏览器解码。 */
  private async loadPeaks(client: MakoClient, source: AudioSource, blob: Promise<Blob>): Promise<void> {
    let fallbackReason = '后端没有提供波形';
    if (source.backendWaveform) {
      try {
        const meta = await client.projects.waveform(source.projectSessionId);
        if (this.disposed) return;
        if (meta.available) {
          const peaks = new BackendPeakSource(
            meta,
            (request, signal) => client.projects.waveformPeaks(source.projectSessionId, { waveformId: meta.waveformId, ...request }, signal),
            {
              isFatal: (error) => error instanceof BackendError && error.status === 404,
              onLost: () => void this.decodePeaks(blob, '后端波形已失效'),
            },
          );
          this.patch({ peaks });
          return;
        }
        fallbackReason = UNAVAILABLE_TEXT[meta.reason] ?? meta.reason;
      } catch (error) {
        fallbackReason = `读取后端波形失败：${error instanceof Error ? error.message : String(error)}`;
      }
    }
    await this.decodePeaks(blob, fallbackReason);
  }

  private async decodePeaks(blobPromise: Promise<Blob>, reason?: string): Promise<void> {
    this.state.peaks?.dispose();
    this.patch({ peaks: undefined, peaksFallback: reason });
    try {
      const bytes = await (await blobPromise).arrayBuffer();
      // OfflineAudioContext 不需要用户手势；解码结果按上下文采样率重采样，时间位置不变。
      const context = new OfflineAudioContext(1, 1, 44_100);
      const buffer = await context.decodeAudioData(bytes);
      const channels = Array.from({ length: buffer.numberOfChannels }, (_, i) => buffer.getChannelData(i));
      if (this.disposed) return;
      this.patch({ peaks: pyramidSource(buildPyramid(channels, buffer.sampleRate)) });
    } catch (error) {
      this.patch({ peaksError: `无法解码波形：${error instanceof Error ? error.message : String(error)}` });
    }
  }

  /** 当前播放头（整数微秒）。播放中每帧调用开销很小，不经过 state$。 */
  playheadUs(): number {
    return secondsToUs(this.audio.currentTime);
  }

  get playing(): boolean {
    return !this.audio.paused;
  }

  async play(): Promise<void> {
    if (this.state.status !== 'ready') return;
    try {
      await this.audio.play();
    } catch (error) {
      this.patch({ error: `无法开始播放：${error instanceof Error ? error.message : String(error)}` });
    }
  }

  pause(): void {
    this.audio.pause();
  }

  /** 从区间起点播放一遍，到终点自动暂停（“试听此行”）。中途暂停或再次调用即取消。 */
  playRange(startUs: number, endUs: number): void {
    if (this.rangeTimer !== undefined) cancelAnimationFrame(this.rangeTimer);
    this.rangeTimer = undefined;
    this.seek(startUs);
    void this.play();
    const tick = () => {
      if (this.disposed || this.audio.paused) {
        this.rangeTimer = undefined;
        return;
      }
      if (this.playheadUs() >= endUs) {
        this.audio.pause();
        this.rangeTimer = undefined;
        return;
      }
      this.rangeTimer = requestAnimationFrame(tick);
    };
    this.rangeTimer = requestAnimationFrame(tick);
  }

  toggle(): void {
    if (this.audio.paused) void this.play();
    else this.pause();
  }

  seek(us: number): void {
    const duration = this.state.durationUs;
    const clamped = Math.max(0, duration > 0 ? Math.min(us, duration) : us);
    this.audio.currentTime = usToSeconds(clamped);
  }

  setRate(rate: number): void {
    this.audio.playbackRate = rate;
    this.patch({ rate });
  }

  /** 循环播放一个区间；传 undefined 取消。设置时跳到区间起点。 */
  setLoop(loop: { startUs: number; endUs: number } | undefined): void {
    this.patch({ loop });
    if (this.loopTimer !== undefined) cancelAnimationFrame(this.loopTimer);
    this.loopTimer = undefined;
    if (!loop) return;
    this.seek(loop.startUs);
    const tick = () => {
      if (this.disposed || !this.state.loop) return;
      if (!this.audio.paused && this.playheadUs() >= this.state.loop.endUs) this.seek(this.state.loop.startUs);
      this.loopTimer = requestAnimationFrame(tick);
    };
    this.loopTimer = requestAnimationFrame(tick);
  }

  get state(): AudioState {
    return this.state$.get();
  }

  dispose(): void {
    this.disposed = true;
    if (this.loopTimer !== undefined) cancelAnimationFrame(this.loopTimer);
    if (this.rangeTimer !== undefined) cancelAnimationFrame(this.rangeTimer);
    this.state.peaks?.dispose();
    this.audio.pause();
    this.audio.removeAttribute('src');
    this.audio.load();
    if (this.url) URL.revokeObjectURL(this.url);
  }

  private durationFromElement(): number {
    return Number.isFinite(this.audio.duration) ? secondsToUs(this.audio.duration) : 0;
  }

  private patch(partial: Partial<AudioState>): void {
    if (this.disposed) return;
    this.state$.set({ ...this.state$.get(), ...partial });
  }
}
