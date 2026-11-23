// 模板缩略图（G17，W4 设计 §9.2；W7 设计 §2.5 改用工坊单行样本），以及试样画布共用的来源与失败文案。
// 不依赖 React。像素只是模板卡片试样，不等于工程里的最终外观。详情区的试样画布见 template-workshop.ts。

import { BackendError, ObservableValue, PreviewFailedError, type MakoClient } from '../../client';
import {
  micros,
  type AuthorDraftSampleOptions,
  type FullTemplateDefinition,
  type PreviewFrameInspectionResponse,
  type PreviewSize,
  type TemplateKind,
  type TemplateLibraryId,
  type TemplatePreviewSource,
  type TemplateSummary,
} from '../../protocol';
import { staticDefinition } from '../model/full-template';
import { FOLLOW_DEFAULT, renderBackendField, type RenderBackendRequest } from '../model/render-device';
import { knownRenderFailureText } from '../preview/render-failure-text';
import { errorText } from '../session/editor-session';

export const TEMPLATE_PREVIEW = 'template-preview-sessions.create';
/** 缩略图用工坊单行样本，目标省略（后端取第一个兼容目标：整行模板是整行，文本单元模板是文本单元）。 */
export const THUMBNAIL_SAMPLE_ID = 'workshop-single-line-v1';
/** 取帧时刻：第一句正在唱。 */
export const SAMPLE_TIME_US = 3_000_000;
export const THUMBNAIL_SIZE: PreviewSize = { width: 320, height: 180 };
/** 试样画布的尺寸。 */
export const SAMPLE_SIZE: PreviewSize = { width: 640, height: 360 };
/** 缩略图同时在途的请求数。 */
const CONCURRENCY = 2;

/** 一张试样：unsupported 是后端稳定拒绝（组合、含效果等），error 是其他失败。 */
export interface SampleFrame {
  status: 'loading' | 'ready' | 'unsupported' | 'error';
  frame?: Blob;
  sha256?: string;
  message?: string;
  /** 帧的时刻（试样画布）。 */
  timeUs?: number;
  /** 失败时的库修订：库重读出新修订后才重试。 */
  libraryRevision?: string;
  /** 作者草稿试样失败时后端的失败编号（例如 WorkerPackageCompilationFailed）。 */
  failureCode?: string;
  /** 作者草稿的帧：出自哪个源码修订。 */
  revision?: string;
  /** 失败时仍显示的上一张成功帧（同一画布范围内）：只作参照，不是这次请求的结果。 */
  lastGood?: LastGoodFrame;
  /** 这一帧的检查投影（试样画布打开叠加层或成本时才取）；等新帧时跟着旧画面留着。 */
  inspection?: PreviewFrameInspectionResponse;
  /** 帧成功但投影没取到时的原因。 */
  inspectionError?: string;
}

/** 上一张成功帧的身份：画面、时刻和（作者草稿的）源码修订。 */
export interface LastGoodFrame {
  frame: Blob;
  sha256: string;
  timeUs: number;
  revision?: string;
}

export interface ThumbnailsState {
  /** 后端是否开放模板试样；第一次请求之前为 undefined。 */
  available: boolean | undefined;
  frames: ReadonlyMap<string, SampleFrame>;
}

interface Job {
  key: string;
  libraryId: TemplateLibraryId;
  libraryRevision: string;
  summary: TemplateSummary;
}

export function thumbnailKey(libraryId: TemplateLibraryId, summary: TemplateSummary): string {
  return `${libraryId}|${summary.templateId}|${summary.templateRevision}`;
}

/**
 * 缩略图缓存，整个应用一份：键是库、模板 ID 和模板修订，模板修订变了自然换键。
 * 同时最多 CONCURRENCY 个请求；后端没有开放试样时什么都不请求。
 */
export class TemplateThumbnails {
  readonly state$ = new ObservableValue<ThumbnailsState>({ available: undefined, frames: new Map() });
  private readonly queue: Job[] = [];
  private running = 0;
  /** 每个键最近一次请求的库修订：排队或在途的请求按旧修订失败时（例如复制模板后库修订变了），按它重试。 */
  private readonly wanted = new Map<string, string>();

  constructor(
    private readonly client: MakoClient,
    /** 渲染设备偏好：已经缓存的缩略图不重画，之后的请求按它传 renderBackend（渲染设备设计 §2.2）。 */
    private readonly device: RenderBackendRequest = FOLLOW_DEFAULT,
    private readonly concurrency = CONCURRENCY,
  ) {}

  get state(): ThumbnailsState {
    return this.state$.get();
  }

  get(libraryId: TemplateLibraryId, summary: TemplateSummary): SampleFrame | undefined {
    return this.state.frames.get(thumbnailKey(libraryId, summary));
  }

  /** 还没有缩略图（或上次失败后库修订变了）就排队请求；已有或在途时什么都不做。 */
  request(libraryId: TemplateLibraryId, libraryRevision: string, summary: TemplateSummary): void {
    if (this.state.available === false) return;
    const key = thumbnailKey(libraryId, summary);
    const existing = this.state.frames.get(key);
    if (existing?.status === 'loading') {
      this.wanted.set(key, libraryRevision);
      const queued = this.queue.find((j) => j.key === key);
      if (queued) queued.libraryRevision = libraryRevision;
      return;
    }
    if (existing && !(existing.status === 'error' && existing.libraryRevision !== libraryRevision)) return;
    this.wanted.set(key, libraryRevision);
    this.setFrame(key, { status: 'loading' });
    this.queue.push({ key, libraryId, libraryRevision, summary });
    this.pump();
  }

  private pump(): void {
    while (this.running < this.concurrency && this.queue.length > 0) {
      const job = this.queue.shift()!;
      this.running++;
      void this.run(job).finally(() => {
        this.running--;
        this.pump();
      });
    }
  }

  private async run(job: Job): Promise<void> {
    try {
      if (!(await templatePreviewAvailable(this.client))) {
        this.queue.length = 0;
        this.state$.set({ available: false, frames: new Map() });
        return;
      }
      if (this.state.available !== true) this.state$.set({ ...this.state, available: true });
      const still = await this.client.preview.renderTemplateStill({
        source: savedSource(job.libraryId, job.libraryRevision, job.summary),
        sampleId: THUMBNAIL_SAMPLE_ID,
        size: THUMBNAIL_SIZE,
        ...renderBackendField(this.device),
        timeUs: micros(SAMPLE_TIME_US),
      });
      this.setFrame(job.key, { status: 'ready', frame: still.blob, sha256: still.sha256 });
    } catch (error) {
      const wanted = this.wanted.get(job.key);
      if (wanted && wanted !== job.libraryRevision) {
        this.queue.push({ ...job, libraryRevision: wanted });
        return;
      }
      this.setFrame(job.key, { ...sampleFailure(error, job.summary.kind), libraryRevision: job.libraryRevision });
    }
  }

  private setFrame(key: string, frame: SampleFrame): void {
    const frames = new Map(this.state.frames);
    frames.set(key, frame);
    this.state$.set({ ...this.state, frames });
  }
}

/** 试样画面的来源：已保存的模板，样式库里编辑中的草稿（不写库），或作者草稿（W8 设计 §2.8）。 */
export type SampleSource =
  | { kind: 'saved'; libraryId: TemplateLibraryId; libraryRevision: string; summary: TemplateSummary }
  | { kind: 'draft'; definition: FullTemplateDefinition }
  | AuthorSampleSource;

/** 作者草稿：草稿修订、在用修订和草稿给的样本与目标（材质草稿还有三个部位）。 */
export interface AuthorSampleSource {
  kind: 'authorDraft';
  draftId: string;
  draftRevision: string;
  authorSourceRevision: string;
  options: AuthorDraftSampleOptions;
}

async function templatePreviewAvailable(client: MakoClient): Promise<boolean> {
  return (await client.capabilities()).features.includes(TEMPLATE_PREVIEW);
}

export function savedSource(libraryId: TemplateLibraryId, libraryRevision: string, summary: TemplateSummary): TemplatePreviewSource {
  return { kind: 'savedTemplate', libraryId, libraryRevision, templateId: summary.templateId, templateRevision: summary.templateRevision };
}

/** 没有效果的草稿按静态格式提交，其余按完整格式。 */
export function draftSource(draftRevision: string, definition: FullTemplateDefinition): TemplatePreviewSource {
  const plain = staticDefinition(definition);
  return plain ? { kind: 'draft', draftRevision, definition: plain } : { kind: 'draft', draftRevision, definitionFormat: 'full', definition };
}

/** 后端稳定拒绝（commandNotSupported）时是 unsupported，其余是 error。 */
export function sampleFailure(error: unknown, kind: TemplateKind): SampleFrame {
  if (error instanceof BackendError && error.code === 'commandNotSupported') {
    return { status: 'unsupported', message: kind === 'composite' ? '组合模板暂不支持试样' : '暂不支持试样' };
  }
  if (error instanceof PreviewFailedError) {
    const known = knownRenderFailureText(error.renderFailureCode);
    if (known) return { status: 'error', message: `试样失败：${known}`, ...(error.renderFailureCode ? { failureCode: error.renderFailureCode } : {}) };
  }
  return { status: 'error', message: `试样失败：${errorText(error)}` };
}
