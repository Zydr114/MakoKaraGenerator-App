// PreviewController（W1 设计 §4.7）：一个工程一条静态预览会话。
// 只保留“最新想看的时刻＋修订”，同时只有一条请求在途；时间前进用 playback，后退用 seek（新代次）；
// 修订变化先 PUT input 换输入。帧按 generation／sourceRevision／requestSeq 核对后才显示，晚到的丢弃。
// 渲染设备（渲染设备设计 §2.3）：创建时带偏好；偏好变了，下一次取帧前用 PUT render-backend 换设备。
// 设备错误（创建或换设备）之后不再取帧，改偏好或 retry() 才再试，不会悄悄用原来的设备继续出新帧。
// 会话一定要关：释放时有请求在途（包括还没返回的创建），等它结束再按最新代次关闭；丢弃会话前也先关闭，
// 否则每个客户端 4 个预览会话的名额很快被占满（429 sessionQuotaExceeded）。

import { BackendError, ObservableValue, PreviewFailedError, type MakoClient } from '../../client';
import { nextSequence, type Generation, type PreviewSize, type RenderBackend, type Revision, type Sequence } from '../../protocol';
import { RENDER_BACKEND_LABEL, type RenderBackendChoice } from '../model/render-device';
import { toMicros } from '../model/time';
import { renderBackendErrorText } from './render-device';

export interface PreviewViewState {
  status: 'idle' | 'rendering' | 'ready' | 'error';
  /** 当前显示的帧（object URL），以及它对应的时刻与修订。 */
  frameUrl?: string;
  frameTimeUs?: number;
  frameRevision?: Revision;
  /** 正在显示的帧实际用的设备（帧元数据）。 */
  frameBackend?: RenderBackend;
  error?: string;
  /** 设备错误：停止取帧，等改偏好或重试。 */
  deviceBlocked?: boolean;
}

/** 创建或换设备时的设备错误（field=renderBackend）；记下来，之后的取帧直接报它。 */
class RenderBackendBlocked extends Error {
  override readonly name = 'RenderBackendBlocked';
}

interface Want {
  timeUs: number;
  revision: Revision;
}

/** 预览上限（runtime-info 的 maxPixelWidth／maxPixelHeight，W3 设计 §2.7）。 */
export const MAX_PREVIEW_SIZE: PreviewSize = { width: 1280, height: 720 };

/** 按画布比例缩到不超过上限，不放大；宽高取偶数。画布未知时按 16:9。 */
export function fitPreviewSize(canvasWidth: number | undefined, canvasHeight: number | undefined, max: PreviewSize = MAX_PREVIEW_SIZE): PreviewSize {
  const width = canvasWidth && canvasWidth > 0 ? canvasWidth : 1920;
  const height = canvasHeight && canvasHeight > 0 ? canvasHeight : 1080;
  const scale = Math.min(1, max.width / width, max.height / height);
  const even = (value: number) => Math.max(2, Math.floor((value * scale) / 2) * 2);
  return { width: even(width), height: even(height) };
}

export class PreviewController {
  readonly state$ = new ObservableValue<PreviewViewState>({ status: 'idle' });
  private previewId: string | undefined;
  private generation: Generation | undefined;
  private sourceRevision: Revision | undefined;
  private lastTimeUs: number | undefined;
  private seq: Sequence | undefined;
  private want: Want | undefined;
  /** 最近一次 show 的时刻与修订：换设备或重试时重新请求它。 */
  private lastWant: Want | undefined;
  /** 会话固定的设备（回执里的 renderBackend）。 */
  private sessionBackend: RenderBackend | undefined;
  private blocked: RenderBackendBlocked | undefined;
  private running = false;
  private disposed = false;
  /** 释放时中止：停止创建的退避重试、等帧和读帧。 */
  private readonly abort = new AbortController();

  constructor(
    private readonly client: MakoClient,
    private readonly projectSessionId: string,
    private readonly size: PreviewSize,
    private choice: RenderBackendChoice = { request: undefined, target: undefined },
  ) {}

  /** 想看 revision 下 timeUs 时刻的画面；在途请求结束后只处理最新的一次。 */
  show(timeUs: number, revision: Revision): void {
    if (this.disposed) return;
    this.want = { timeUs: Math.max(0, Math.round(timeUs)), revision };
    this.lastWant = this.want;
    if (!this.running) void this.pump();
  }

  /** 偏好变了：清掉设备错误，按新设备重画当前时刻。 */
  setRenderBackend(choice: RenderBackendChoice): void {
    if (choice.request === this.choice.request && choice.target === this.choice.target) return;
    this.choice = choice;
    this.retry();
  }

  /** 设备错误后再试一次（创建或换设备）。 */
  retry(): void {
    if (this.disposed) return;
    this.blocked = undefined;
    this.want ??= this.lastWant;
    if (this.want && !this.running) void this.pump();
  }

  private async pump(): Promise<void> {
    this.running = true;
    try {
      while (this.want && !this.disposed) {
        const want = this.want;
        this.want = undefined;
        const current = this.state$.get();
        const sameBackend = this.choice.target === undefined || current.frameBackend === this.choice.target;
        if (current.status === 'ready' && current.frameTimeUs === want.timeUs && current.frameRevision === want.revision && sameBackend) continue;
        if (this.blocked) {
          this.patch({ status: 'error', error: this.blocked.message, deviceBlocked: true });
          continue;
        }
        this.patch({ status: 'rendering' });
        try {
          await this.render(want);
        } catch (error) {
          if (this.disposed) return;
          if (error instanceof RenderBackendBlocked) {
            this.patch({ status: 'error', error: error.message, deviceBlocked: true });
            continue;
          }
          // 修订已过期（工程又被编辑）或帧已过期：等下一次 show，不当作错误。
          if (error instanceof BackendError && (error.code === 'revisionConflict' || error.code === 'previewFrameExpired')) {
            this.patch({ status: this.state$.get().frameUrl ? 'ready' : 'idle' });
            continue;
          }
          // 会话已经没了：下次重建。代次或序号乱了：先关掉旧会话（占着名额），下次重建。
          if (error instanceof BackendError && error.code === 'previewSessionNotFound') {
            this.forgetSession();
            this.want ??= want;
            continue;
          }
          if (error instanceof BackendError && (error.code === 'previewGenerationConflict' || error.code === 'previewRequestOutOfOrder')) {
            await this.closeSession();
            this.want ??= want;
            continue;
          }
          this.patch({ status: 'error', error: describe(error), deviceBlocked: undefined });
        }
      }
    } finally {
      this.running = false;
      // 释放时还有请求在途：现在都结束了，会话 ID 和代次是最新的。
      if (this.disposed) void this.closeSession();
    }
  }

  private async render(want: Want): Promise<void> {
    const source = { kind: 'project' as const, sourceId: this.projectSessionId, sourceRevision: want.revision };
    if (!this.previewId) {
      const request = this.choice.request;
      const created = await this.client.preview
        .create({ source, size: this.size, alphaMode: 'opaque', ...(request ? { renderBackend: request } : {}) }, this.abort.signal)
        .catch((error: unknown) => {
          throw this.blockOnDeviceError(error, () => `${renderBackendErrorText(error)}。可以在顶栏改用别的渲染设备。`);
        });
      this.previewId = created.previewId;
      this.generation = created.previewGeneration;
      this.sessionBackend = created.renderBackend;
      this.sourceRevision = want.revision;
      this.lastTimeUs = undefined;
      if (this.disposed) return;
    }
    const target = this.choice.target;
    if (target && this.sessionBackend !== target) {
      // 相同设备是 no-op；不同设备成功后换新代次，同代次的播放时刻从头算。失败时旧会话不变。
      const from = this.sessionBackend;
      const switched = await this.client.preview
        .setRenderBackend(this.previewId, { expectedPreviewGeneration: this.generation!, renderBackend: target })
        .catch((error: unknown) => {
          throw this.blockOnDeviceError(
            error,
            () => `没有换到 ${RENDER_BACKEND_LABEL[target]}：${renderBackendErrorText(error)}。${from ? `画面是 ${RENDER_BACKEND_LABEL[from]} 渲染的最后一帧。` : ''}`,
          );
        });
      this.generation = switched.previewGeneration;
      this.sessionBackend = switched.renderBackend;
      this.lastTimeUs = undefined;
      if (this.disposed) return;
    }
    if (this.sourceRevision !== want.revision) {
      const replaced = await this.client.preview.replaceInput(this.previewId, {
        expectedPreviewGeneration: this.generation!,
        expectedSourceRevision: this.sourceRevision!,
        source,
        size: this.size,
        alphaMode: 'opaque',
      });
      this.generation = replaced.previewGeneration;
      this.sourceRevision = want.revision;
      this.lastTimeUs = undefined;
      if (this.disposed) return;
    }

    // 同代次 playback 时间不能后退；后退用 seek（服务端换新代次）。
    const intent = this.lastTimeUs === undefined || want.timeUs >= this.lastTimeUs ? 'playback' : 'seek';
    this.seq = nextSequence(this.seq);
    const accepted = await this.client.preview.requestFrame(this.previewId, {
      expectedSourceRevision: want.revision,
      previewGeneration: this.generation!,
      requestSeq: this.seq,
      timeUs: toMicros(want.timeUs),
      intent,
    });
    this.generation = accepted.previewGeneration;
    this.lastTimeUs = want.timeUs;
    const frame = await this.client.preview.waitForFrame(
      this.previewId,
      { generation: accepted.previewGeneration, sourceRevision: want.revision, requestSeq: this.seq },
      { signal: this.abort.signal },
    );
    const binary = await this.client.fetchBinary(frame.content.contentUrl, { signal: this.abort.signal });
    if (!binary.blob || this.disposed) return;
    // 等待期间已有更新的请求：这帧仍然有效（匹配本次身份），先显示，下一轮再换。
    const previous = this.state$.get().frameUrl;
    this.patch({
      status: 'ready',
      frameUrl: URL.createObjectURL(binary.blob),
      frameTimeUs: want.timeUs,
      frameRevision: want.revision,
      frameBackend: frame.renderBackend,
      error: undefined,
      deviceBlocked: undefined,
    });
    if (previous) URL.revokeObjectURL(previous);
  }

  /** 设备错误记下来并换成 RenderBackendBlocked；别的错误原样返回。 */
  private blockOnDeviceError(error: unknown, message: () => string): unknown {
    if (renderBackendErrorText(error) === undefined) return error;
    this.blocked = new RenderBackendBlocked(message());
    return this.blocked;
  }

  private forgetSession(): void {
    this.previewId = undefined;
    this.generation = undefined;
    this.sessionBackend = undefined;
    this.sourceRevision = undefined;
    this.lastTimeUs = undefined;
  }

  /** 关闭当前会话（尽力而为），之后再取帧会重建。 */
  private async closeSession(): Promise<void> {
    const { previewId, generation, sourceRevision } = this;
    this.forgetSession();
    if (previewId && generation && sourceRevision) {
      await this.client.preview.closeBestEffort(previewId, { expectedPreviewGeneration: generation, expectedSourceRevision: sourceRevision });
    }
  }

  /** 释放帧并关闭服务端预览会话；有请求在途时由 pump 在它结束后关闭。 */
  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.abort.abort();
    const url = this.state$.get().frameUrl;
    if (url) URL.revokeObjectURL(url);
    if (!this.running) void this.closeSession();
  }

  /** 值为 undefined 的键从状态中删除。 */
  private patch(partial: { [K in keyof PreviewViewState]?: PreviewViewState[K] | undefined }): void {
    if (this.disposed) return;
    const next: Record<string, unknown> = { ...this.state$.get() };
    for (const [key, value] of Object.entries(partial)) {
      if (value === undefined) delete next[key];
      else next[key] = value;
    }
    this.state$.set(next as unknown as PreviewViewState);
  }
}

function describe(error: unknown): string {
  if (error instanceof PreviewFailedError) return `渲染失败：${error.message}`;
  if (error instanceof BackendError) return `${error.message}（${error.code ?? error.status}）`;
  return error instanceof Error ? error.message : String(error);
}
