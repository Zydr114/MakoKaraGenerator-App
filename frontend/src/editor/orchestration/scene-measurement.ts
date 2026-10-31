// 同帧测量（W4 设计 §13.6、§13.8）：编排页画布的叠加层和布局参考模式用。
// 只保留最新想测的一次，同时只有一个请求在途（后端每个客户端同时只能有一个测量）；结果带着它的请求，
// 由页面按修订与时刻判断是否和当前画面对得上。失败时保留上一份结果并写明原因：名额用完（429）稍后自动重试一次，
// 修订过期（409）等下一次请求，不当作错误。不依赖 React。

import { BackendError, ObservableValue, type MakoClient } from '../../client';
import type { Revision, SceneMeasurement, SceneMeasurementMode } from '../../protocol';
import { FOLLOW_DEFAULT, renderBackendField, type RenderBackendRequest } from '../model/render-device';
import { toMicros } from '../model/time';

export const SCENE_MEASUREMENT = 'orchestration.measurements.scene-measurement-v1';

export interface MeasurementWant {
  mode: SceneMeasurementMode;
  revision: Revision;
  /** time 模式的时刻（微秒）；layout 模式是 null。 */
  timeUs: number | null;
  /** 输出 PNG 的尺寸（不超过 1280×720）；几何总是画布坐标。 */
  width: number;
  height: number;
}

export interface MeasurementResult {
  want: MeasurementWant;
  measurement: SceneMeasurement;
  /** layout 模式：PNG 的 object URL，画布显示它。 */
  imageUrl?: string;
}

export interface SceneMeasurementViewState {
  status: 'idle' | 'measuring' | 'ready' | 'error';
  /** 最近一次成功的测量。 */
  result?: MeasurementResult;
  error?: string;
}

export function sameWant(a: MeasurementWant, b: MeasurementWant): boolean {
  return a.mode === b.mode && a.revision === b.revision && a.timeUs === b.timeUs && a.width === b.width && a.height === b.height;
}

/** 失败原因（§13.6）。 */
export function measurementFailureText(error: unknown): string {
  if (error instanceof BackendError) {
    if (error.status === 413) return '画面太复杂，不能测量。';
    if (error.status === 429) return '测量请求太多，稍后再试。';
    if (error.code === 'taskUnavailable' || error.status === 503) return '画面测量暂时不可用。';
    return `测量失败：${error.message}`;
  }
  return `测量失败：${error instanceof Error ? error.message : String(error)}`;
}

function pngBlob(base64: string): Blob {
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return new Blob([bytes], { type: 'image/png' });
}

export class SceneMeasurementController {
  readonly state$ = new ObservableValue<SceneMeasurementViewState>({ status: 'idle' });
  private want: MeasurementWant | undefined;
  private running = false;
  private disposed = false;
  private readonly retryDelayMs: number;
  private readonly device: RenderBackendRequest;

  constructor(
    private readonly client: MakoClient,
    private readonly projectSessionId: string,
    /** device：渲染设备偏好，测量按它传 renderBackend（渲染设备设计 §2.2）。 */
    options?: { retryDelayMs?: number; device?: RenderBackendRequest },
  ) {
    this.retryDelayMs = options?.retryDelayMs ?? 1000;
    this.device = options?.device ?? FOLLOW_DEFAULT;
  }

  get state(): SceneMeasurementViewState {
    return this.state$.get();
  }

  /** 想测这一次；在途的请求结束后只处理最新的一次。与上次成功的结果相同时不再测。 */
  measure(want: MeasurementWant): void {
    if (this.disposed) return;
    this.want = want;
    if (!this.running) void this.pump();
  }

  private async pump(): Promise<void> {
    this.running = true;
    try {
      while (this.want && !this.disposed) {
        const want = this.want;
        this.want = undefined;
        const { result, status } = this.state;
        if (result && status !== 'error' && sameWant(result.want, want)) continue;
        this.patch({ status: 'measuring' });
        await this.run(want, false);
      }
    } finally {
      this.running = false;
    }
  }

  private async run(want: MeasurementWant, retried: boolean): Promise<void> {
    try {
      const response = await this.client.projects.measureScene(this.projectSessionId, {
        expectedRevision: want.revision,
        mode: want.mode,
        timeUs: want.mode === 'time' && want.timeUs !== null ? toMicros(want.timeUs) : null,
        width: want.width,
        height: want.height,
        alphaMode: 'opaque',
        ...renderBackendField(this.device),
      });
      if (this.disposed) return;
      const imageUrl = want.mode === 'layout' ? URL.createObjectURL(pngBlob(response.measurement.png)) : undefined;
      const previous = this.state.result?.imageUrl;
      this.patch({ status: 'ready', result: { want, measurement: response.measurement, ...(imageUrl ? { imageUrl } : {}) }, error: undefined });
      if (previous) URL.revokeObjectURL(previous);
    } catch (error) {
      if (this.disposed) return;
      // 修订已过期：工程又被编辑，等页面按新修订再请求。
      if (error instanceof BackendError && error.code === 'revisionConflict') {
        this.patch({ status: this.state.result ? 'ready' : 'idle' });
        return;
      }
      // 名额用完：稍后重试一次；这期间有了更新的请求就先做新的。
      if (error instanceof BackendError && error.status === 429 && !retried) {
        await new Promise((resolve) => setTimeout(resolve, this.retryDelayMs));
        if (this.disposed || this.want) return;
        await this.run(want, true);
        return;
      }
      this.patch({ status: 'error', error: measurementFailureText(error) });
    }
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    const url = this.state.result?.imageUrl;
    if (url) URL.revokeObjectURL(url);
  }

  private patch(partial: { [K in keyof SceneMeasurementViewState]?: SceneMeasurementViewState[K] | undefined }): void {
    if (this.disposed) return;
    const next: Record<string, unknown> = { ...this.state$.get() };
    for (const [key, value] of Object.entries(partial)) {
      if (value === undefined) delete next[key];
      else next[key] = value;
    }
    this.state$.set(next as unknown as SceneMeasurementViewState);
  }
}
