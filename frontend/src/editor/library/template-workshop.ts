// 样式库详情区的试样画布（W7 设计 §2.5）：在工坊样本上为一个模板保持一个预览会话。
// 不依赖 React。选模板（草稿改动后停 DRAFT_DEBOUNCE_MS）先取试样选项，再按样本、目标和材质焦点建会话。
// 模板试样会话的输入是固定的（没有 PUT input）：换来源、样本、目标、材质焦点或渲染设备都关掉旧会话重建。
// 作者草稿（W8 设计 §2.8）：样本与目标取草稿的 sampleOptions，每个草稿一个会话；草稿修订、样本、目标或材质部位变了用 PUT input 换输入。
// 作者草稿的会话失败后出不了新帧：读完诊断就关掉，下次取帧重建（运行时出错之前的时刻还能看）。
// 取帧规则同 PreviewController：同时只有一条请求在途，只处理最新想看的时刻；时间前进用 playback，后退用 seek。
// 试播按本地时钟取帧，两帧之间至少隔 PLAYBACK_INTERVAL_MS。

import { authorDraftSource, BackendError, ObservableValue, PreviewFailedError, type MakoClient } from '../../client';
import {
  nextSequence,
  normalizeSeverity,
  type AuthorDraftSampleSelection,
  type BackendDiagnostic,
  type BackendSourceDiagnostic,
  type Generation,
  type PreviewSessionResponse,
  type PreviewSourceRevision,
  type RenderBackend,
  type Sequence,
  type TemplateMaterialSlot,
  type TemplatePreviewInput,
  type TemplatePreviewOptionsResponse,
  type TemplatePreviewSource,
  type TemplateWorkshopSampleId,
} from '../../protocol';
import { SLOT_LABEL } from '../model/full-template';
import { FOLLOW_DEFAULT, renderBackendField, type RenderBackendRequest } from '../model/render-device';
import { formatTime, toMicros } from '../model/time';
import { renderBackendErrorText } from '../preview/render-device';
import { errorText } from '../session/editor-session';
import { draftSource, sampleFailure, savedSource, SAMPLE_SIZE, TEMPLATE_PREVIEW, type AuthorSampleSource, type SampleFrame, type SampleSource } from './template-thumbnails';

export const TEMPLATE_PREVIEW_OPTIONS = 'template-preview-options.read';
export const WORKSHOP_DEFAULT_SAMPLE: TemplateWorkshopSampleId = 'workshop-single-line-v1';
/** 默认时刻：第一句正在唱。 */
export const WORKSHOP_DEFAULT_TIME_US = 3_000_000;
/** 工坊样本的长度（试样选项的 durationUs 固定是它）；超过它的时刻后端拒绝。 */
export const WORKSHOP_DURATION_US = 12_000_000;
/** 草稿停止修改多久后再请求。 */
const DRAFT_DEBOUNCE_MS = 300;
/** 试播时两次取帧的最小间隔。 */
const PLAYBACK_INTERVAL_MS = 40;
/** 编译失败时在画面上列出的诊断条数。 */
const COMPILE_LINES = 3;

export const SAMPLE_LABEL: Record<TemplateWorkshopSampleId, string> = {
  'workshop-single-line-v1': '单行样本',
  'workshop-multi-line-v1': '多行样本',
};

const TARGET_LABEL: Record<string, string> = {
  line: '整行',
  project: '整个工程',
  branch: '来源',
  clip: '片段',
  group: '组',
  mark: '标记',
  token: '文本单元',
};

/** 后端给的目标键显示成中文；不认识的键原样显示。 */
export function targetLabel(key: string): string {
  return TARGET_LABEL[key] ?? key;
}

export interface WorkshopState {
  /** 后端是否开放模板试样与试样选项；第一次请求之前为 undefined。 */
  available: boolean | undefined;
  source?: SampleSource['kind'];
  /** 当前来源的试样选项；换来源后、读到新选项前保留上一份。 */
  options?: TemplatePreviewOptionsResponse;
  sampleId: TemplateWorkshopSampleId;
  targetKey?: string;
  /** 材质焦点（材质页签选中的部位）；不影响画面里运行的内容。所指部位不存在（描边关闭）时不带。 */
  materialSlot?: TemplateMaterialSlot;
  /** 当前会话里后端回显的材质焦点。 */
  echoedSlot?: string;
  /** 想看的时刻（微秒）；试播时跟着本地时钟走。 */
  timeUs: number;
  playing: boolean;
  loop: boolean;
  frame?: SampleFrame;
}

/** 读到选项的来源：模板试样建会话用同一份 DTO（草稿的 draftRevision 不变）；作者草稿记着草稿与修订。 */
type LoadedSource = { kind: 'template'; key: string; dto: TemplatePreviewSource } | { kind: 'authorDraft'; key: string; author: AuthorSampleSource };

interface Input {
  /** 会话身份，变了要重建：模板试样的输入固定，和 key 相同；作者草稿是草稿与设备。 */
  sessionKey: string;
  /** 来源、样本、目标、材质焦点与设备：变了要重建会话（模板试样）或换输入（作者草稿）。 */
  key: string;
  request:
    | { kind: 'template'; input: TemplatePreviewInput }
    | { kind: 'authorDraft'; draftId: string; draftRevision: string; authorSourceRevision: string; sample: AuthorDraftSampleSelection; renderBackend?: RenderBackend };
}

interface Session {
  /** 会话身份（Input.sessionKey）。 */
  key: string;
  /** 会话当前的输入（Input.key）。 */
  inputKey: string;
  previewId: string;
  generation: Generation;
  sourceRevision: PreviewSourceRevision;
  lastTimeUs?: number;
  seq?: Sequence;
  /** 关闭会话时中止还在等的帧。 */
  abort: AbortController;
}

export class TemplateWorkshop {
  readonly state$ = new ObservableValue<WorkshopState>({
    available: undefined,
    sampleId: WORKSHOP_DEFAULT_SAMPLE,
    timeUs: WORKSHOP_DEFAULT_TIME_US,
    playing: false,
    loop: false,
  });
  private sourceKey: string | undefined;
  /** 材质页签想看的部位：读到新选项时按它重新判断部位是否存在。 */
  private wantedSlot: TemplateMaterialSlot | undefined;
  private scope: string | undefined;
  private source: LoadedSource | undefined;
  private draftSeq = 0;
  private timer: ReturnType<typeof setTimeout> | undefined;
  private loading: AbortController | undefined;
  private session: Session | undefined;
  private want: number | undefined;
  private running = false;
  /** 已经显示的帧：同一输入、同一时刻不再请求。 */
  private shown: { key: string; timeUs: number; frame: SampleFrame } | undefined;
  private clock: { startedAt: number; fromUs: number } | undefined;
  private tick: ReturnType<typeof setTimeout> | undefined;
  private lastRequestAt = 0;
  private disposed = false;

  constructor(
    private readonly client: MakoClient,
    private readonly device: RenderBackendRequest = FOLLOW_DEFAULT,
    private readonly debounceMs = DRAFT_DEBOUNCE_MS,
    private readonly now: () => number = () => performance.now(),
  ) {}

  get state(): WorkshopState {
    return this.state$.get();
  }

  /** scope 标识“哪个模板”；来源和上次相同时什么都不做。同一个模板更新时先留着上一张，避免闪烁。 */
  show(source: SampleSource | null, scope: string): void {
    if (this.disposed) return;
    const key = source ? sampleKey(source) : undefined;
    if (key === this.sourceKey && scope === this.scope) return;
    const sameScope = scope === this.scope;
    this.sourceKey = key;
    this.scope = scope;
    this.cancelLoading();
    if (!sameScope) this.pause();
    if (!source || !key) {
      this.source = undefined;
      this.closeSession();
      const { source: _source, options: _options, frame: _frame, ...rest } = this.state;
      this.state$.set(rest);
      return;
    }
    this.patch({ source: source.kind, frame: loading(sameScope ? this.state.frame : undefined) });
    // 作者草稿的选项不用另外读，但也放到下一轮再用：同一轮里换掉画布下拉框的选项会让刚建的 mdui-select 还没初始化就被换掉。
    if (source.kind === 'authorDraft') this.timer = setTimeout(() => this.useAuthor(source, key), 0);
    else if (source.kind === 'draft') this.timer = setTimeout(() => void this.load(source, key), this.debounceMs);
    else void this.load(source, key);
  }

  /** 详情区不再显示这个来源：画布还停在 scope 上（没有别的详情接手）时关掉会话。 */
  release(scope: string): void {
    if (scope === this.scope) this.show(null, '');
  }

  setSample(sampleId: TemplateWorkshopSampleId): void {
    if (sampleId === this.state.sampleId) return;
    this.patch({ sampleId, targetKey: targetFor(this.state.options, sampleId, this.state.targetKey) });
    this.restart();
  }

  setTarget(targetKey: string): void {
    if (targetKey === this.state.targetKey) return;
    this.patch({ targetKey });
    this.restart();
  }

  /** 材质页签（或作者材质草稿的部位选择）换部位时调用；undefined 表示不指定。 */
  setMaterialSlot(wanted: TemplateMaterialSlot | undefined): void {
    this.wantedSlot = wanted;
    const options = this.state.options;
    const materialSlot = options ? this.pick(options).materialSlot : wanted;
    if (materialSlot === this.state.materialSlot) return;
    this.patch({ materialSlot });
    this.restart();
  }

  /** 跳到某个时刻；试播中从这里接着播。含效果的模板取一帧可能要几秒，等待时画面变淡（试播中不变淡，免得闪）。 */
  seek(timeUs: number): void {
    const time = clampTime(timeUs);
    if (this.clock) this.clock = { startedAt: this.now(), fromUs: time };
    const waiting = !this.clock && this.state.frame?.status === 'ready' && this.state.frame.timeUs !== time;
    this.patch({ timeUs: time, ...(waiting ? { frame: loading(this.state.frame) } : {}) });
    this.request(time);
  }

  play(): void {
    if (this.state.playing || this.disposed) return;
    const from = this.state.timeUs >= WORKSHOP_DURATION_US ? 0 : this.state.timeUs;
    this.clock = { startedAt: this.now(), fromUs: from };
    this.patch({ playing: true, timeUs: from });
    this.request(from);
  }

  pause(): void {
    clearTimeout(this.tick);
    this.tick = undefined;
    this.clock = undefined;
    if (this.state.playing) this.patch({ playing: false });
  }

  setLoop(loop: boolean): void {
    if (loop !== this.state.loop) this.patch({ loop });
  }

  /** 渲染设备偏好变了：设备不同就重建会话。 */
  deviceChanged(): void {
    if (this.input()?.sessionKey !== this.session?.key) this.restart();
  }

  dispose(): void {
    if (this.disposed) return;
    this.pause();
    this.cancelLoading();
    this.closeSession();
    this.disposed = true;
  }

  private cancelLoading(): void {
    clearTimeout(this.timer);
    this.timer = undefined;
    this.loading?.abort();
    this.loading = undefined;
  }

  /** 作者草稿的样本、目标和材质部位都在草稿里，不用另外读。 */
  private useAuthor(source: AuthorSampleSource, key: string): void {
    this.timer = undefined;
    const options = authorOptions(source);
    this.source = { kind: 'authorDraft', key, author: source };
    this.patch({ available: true, options, ...this.pick(options) });
    this.restart();
  }

  /** 按新选项挑样本、目标与材质焦点；作者材质草稿必须有部位，没指定时用“未唱填充”。 */
  private pick(options: TemplatePreviewOptionsResponse): ReturnType<typeof pickSelection> {
    const picked = pickSelection(options, { ...this.state, materialSlot: this.wantedSlot });
    if (picked.materialSlot || this.source?.kind !== 'authorDraft') return picked;
    const slots = options.materialSlots.map((s) => s.slot);
    return { ...picked, materialSlot: slots.includes('unsungFill') ? 'unsungFill' : slots[0] };
  }

  private async load(source: Exclude<SampleSource, AuthorSampleSource>, key: string): Promise<void> {
    this.timer = undefined;
    const abort = new AbortController();
    this.loading = abort;
    const current = () => !abort.signal.aborted && this.sourceKey === key;
    try {
      const features = (await this.client.capabilities()).features;
      if (!current()) return;
      if (!features.includes(TEMPLATE_PREVIEW) || !features.includes(TEMPLATE_PREVIEW_OPTIONS)) {
        this.closeSession();
        this.state$.set({ available: false, sampleId: this.state.sampleId, timeUs: this.state.timeUs, playing: false, loop: this.state.loop });
        return;
      }
      const dto = source.kind === 'saved' ? savedSource(source.libraryId, source.libraryRevision, source.summary) : draftSource(`draft-${++this.draftSeq}`, source.definition);
      const options = await this.client.templates.previewOptions(dto, abort.signal);
      if (!current()) return;
      this.source = { kind: 'template', key, dto };
      this.patch({ available: true, options, ...this.pick(options) });
      this.restart();
    } catch (error) {
      if (!current()) return;
      this.source = undefined;
      this.closeSession();
      this.pause();
      const kind = source.kind === 'saved' ? source.summary.kind : source.definition.kind;
      this.patch({ frame: sampleFailure(error, kind) });
    } finally {
      if (this.loading === abort) this.loading = undefined;
    }
  }

  /** 当前选择对应的会话输入；还没读到选项、或这个样本上没有目标时为 undefined。 */
  private input(): Input | undefined {
    const { options, sampleId, targetKey, materialSlot } = this.state;
    if (!this.source || !options || !targetKey) return undefined;
    const backend = renderBackendField(this.device);
    const selection = [sampleId, targetKey, materialSlot ?? ''];
    if (this.source.kind === 'authorDraft') {
      const { draftId, draftRevision, authorSourceRevision } = this.source.author;
      const sessionKey = ['author', draftId, backend.renderBackend ?? ''].join('\u0000');
      return {
        sessionKey,
        key: [sessionKey, draftRevision, authorSourceRevision, ...selection].join('\u0000'),
        request: { kind: 'authorDraft', draftId, draftRevision, authorSourceRevision, sample: { sampleId, targetKey, ...(materialSlot ? { materialSlot } : {}) }, ...backend },
      };
    }
    const request: TemplatePreviewInput = {
      source: this.source.dto,
      sampleId,
      targetKey,
      ...(materialSlot ? { materialSlot } : {}),
      size: SAMPLE_SIZE,
      ...backend,
    };
    const key = [this.source.key, ...selection, backend.renderBackend ?? ''].join('\u0000');
    return { sessionKey: key, key, request: { kind: 'template', input: request } };
  }

  /** 选择变了：样本上没有目标时写明原因，否则按当前时刻重画（会话在取帧前重建）。 */
  private restart(): void {
    const { options, sampleId, targetKey } = this.state;
    if (!this.source || !options) return;
    if (!targetKey) {
      this.closeSession();
      this.pause();
      const where = options.samples.some((s) => s.targets.length > 0) ? SAMPLE_LABEL[sampleId] : '两个样本';
      this.patch({ frame: { status: 'unsupported', message: `这个${this.source.kind === 'authorDraft' ? '组件' : '模板'}在${where}上没有可用的目标` } });
      return;
    }
    if (this.state.frame?.status !== 'loading') this.patch({ frame: loading(this.state.frame) });
    this.request(this.state.timeUs);
  }

  private request(timeUs: number): void {
    if (this.disposed) return;
    this.want = timeUs;
    if (!this.running) void this.pump();
  }

  private async pump(): Promise<void> {
    this.running = true;
    try {
      while (this.want !== undefined && !this.disposed) {
        const timeUs = this.want;
        this.want = undefined;
        const input = this.input();
        if (!input) break;
        if (this.shown?.key === input.key && this.shown.timeUs === timeUs) {
          // 例如草稿改回了原样：会话没换，把“正在生成”换回已有的帧。
          if (this.state.frame !== this.shown.frame) this.patch({ frame: this.shown.frame });
          this.scheduleTick();
          continue;
        }
        let session: Session | undefined;
        try {
          session = this.session?.key === input.sessionKey ? this.session : await this.open(input);
          if (session !== this.session) continue;
          if (session.inputKey !== input.key) {
            await this.replace(session, input);
            if (session !== this.session || this.input()?.key !== input.key) continue;
          }
          const frame = await this.render(session, timeUs);
          if (session !== this.session) continue;
          this.shown = { key: input.key, timeUs, frame: { status: 'ready', frame: frame.blob, sha256: frame.sha256, timeUs } };
          this.patch({ frame: this.shown.frame });
        } catch (error) {
          let failed: SampleFrame | undefined;
          if (error instanceof PreviewFailedError && session && session === this.session && input.request.kind === 'authorDraft') {
            const diagnostics = await this.diagnosticsOf(session);
            if (session === this.session) this.closeSession();
            failed = authorSampleFailure(error, diagnostics);
          }
          if (this.disposed || this.input()?.key !== input.key) continue;
          // 代次或序号乱了、会话没了：丢掉会话，下次重建。
          if (error instanceof BackendError && (error.code === 'previewGenerationConflict' || error.code === 'previewRequestOutOfOrder' || error.code === 'previewSessionNotFound')) {
            this.closeSession();
            this.want ??= timeUs;
            continue;
          }
          this.pause();
          this.patch({ frame: failed ?? frameFailure(error) });
          continue;
        }
        this.scheduleTick();
      }
    } finally {
      this.running = false;
    }
  }

  private async open(input: Input): Promise<Session> {
    this.closeSession();
    const { request } = input;
    let created: PreviewSessionResponse;
    if (request.kind === 'template') created = await this.client.preview.createTemplatePreview(request.input);
    else {
      const { kind: _kind, ...author } = request;
      created = await this.client.preview.createAuthorDraft({ ...author, size: SAMPLE_SIZE });
    }
    const session: Session = {
      key: input.sessionKey,
      inputKey: input.key,
      previewId: created.previewId,
      generation: created.previewGeneration,
      sourceRevision: created.source.sourceRevision,
      abort: new AbortController(),
    };
    // 建好之前选择又变了：这个会话已经没用，直接关掉（作者草稿只看草稿和设备，其余的换输入就行）。
    if (this.disposed || this.input()?.sessionKey !== input.sessionKey) {
      void this.closeRemote(session);
      return session;
    }
    this.session = session;
    this.shown = undefined;
    this.patch({ echoedSlot: created.source.templateSample?.materialSlot });
    return session;
  }

  /** 作者草稿换输入：新代次、新来源修订，旧帧作废。 */
  private async replace(session: Session, input: Input): Promise<void> {
    const { request } = input;
    if (request.kind !== 'authorDraft') return;
    const updated = await this.client.preview.replaceInput(session.previewId, {
      expectedPreviewGeneration: session.generation,
      expectedSourceRevision: session.sourceRevision,
      source: authorDraftSource(request.draftId, request.draftRevision, request.authorSourceRevision, request.sample),
      size: SAMPLE_SIZE,
      alphaMode: 'opaque',
    });
    if (session !== this.session) return;
    session.generation = updated.previewGeneration;
    session.sourceRevision = updated.source.sourceRevision;
    session.inputKey = input.key;
    delete session.lastTimeUs;
    this.shown = undefined;
    this.patch({ echoedSlot: updated.source.templateSample?.materialSlot });
  }

  /** 失败会话的诊断（第一页）；读不到时为空。 */
  private async diagnosticsOf(session: Session): Promise<BackendDiagnostic[]> {
    try {
      return (await this.client.preview.diagnostics(session.previewId)).diagnostics;
    } catch {
      return [];
    }
  }

  private async render(session: Session, timeUs: number): Promise<{ blob: Blob; sha256: string }> {
    const intent = session.lastTimeUs === undefined || timeUs >= session.lastTimeUs ? 'playback' : 'seek';
    session.seq = nextSequence(session.seq);
    const requestSeq = session.seq;
    this.lastRequestAt = this.now();
    const accepted = await this.client.preview.requestFrame(session.previewId, {
      expectedSourceRevision: session.sourceRevision,
      previewGeneration: session.generation,
      requestSeq,
      timeUs: toMicros(timeUs),
      intent,
    });
    session.generation = accepted.previewGeneration;
    session.lastTimeUs = timeUs;
    const frame = await this.client.preview.waitForFrame(
      session.previewId,
      { generation: accepted.previewGeneration, sourceRevision: session.sourceRevision, requestSeq },
      { signal: session.abort.signal },
    );
    return this.client.preview.fetchFrame(frame, session.abort.signal);
  }

  /** 试播中：按本地时钟排下一帧；到结尾时循环或停在最后一帧。 */
  private scheduleTick(): void {
    if (!this.clock || this.tick || this.want !== undefined) return;
    const delay = Math.max(0, PLAYBACK_INTERVAL_MS - (this.now() - this.lastRequestAt));
    this.tick = setTimeout(() => {
      this.tick = undefined;
      if (!this.clock) return;
      let timeUs = Math.round(this.clock.fromUs + (this.now() - this.clock.startedAt) * 1000);
      if (timeUs >= WORKSHOP_DURATION_US) {
        if (this.state.loop) {
          this.clock = { startedAt: this.now(), fromUs: 0 };
          timeUs = 0;
        } else if (this.state.timeUs >= WORKSHOP_DURATION_US) {
          this.pause();
          return;
        } else {
          timeUs = WORKSHOP_DURATION_US;
        }
      }
      this.patch({ timeUs });
      this.request(timeUs);
    }, delay);
  }

  private closeSession(): void {
    const session = this.session;
    this.session = undefined;
    this.shown = undefined;
    if (session) void this.closeRemote(session);
  }

  /** 关闭服务端会话（尽力而为），并中止还在等的帧。 */
  private async closeRemote(session: Session): Promise<void> {
    session.abort.abort();
    await this.client.preview.closeBestEffort(session.previewId, { expectedPreviewGeneration: session.generation, expectedSourceRevision: session.sourceRevision });
  }

  /** 值为 undefined 的键从状态中删除。 */
  private patch(partial: { [K in keyof WorkshopState]?: WorkshopState[K] | undefined }): void {
    if (this.disposed) return;
    const next: Record<string, unknown> = { ...this.state };
    for (const [key, value] of Object.entries(partial)) {
      if (value === undefined) delete next[key];
      else next[key] = value;
    }
    this.state$.set(next as unknown as WorkshopState);
  }
}

/** 标题：“试样 · 单行样本 · 整行 · 0:03.000”。 */
export function workshopTitle(state: WorkshopState): string {
  const focus = state.materialSlot ? [`材质焦点 ${SLOT_LABEL[state.materialSlot]}`] : [];
  return ['试样', SAMPLE_LABEL[state.sampleId], ...(state.targetKey ? [targetLabel(state.targetKey)] : []), ...focus, formatTime(state.timeUs)].join(' · ');
}

/**
 * 读到新选项后的样本与目标：原来的样本还有目标就留着，否则用默认样本，再不行用第一个有目标的样本；
 * 目标留着原来的键，没有就取第一个。材质焦点所指的部位不存在时去掉。
 */
export function pickSelection(
  options: TemplatePreviewOptionsResponse,
  current: { sampleId: TemplateWorkshopSampleId; targetKey?: string | undefined; materialSlot?: TemplateMaterialSlot | undefined },
): { sampleId: TemplateWorkshopSampleId; targetKey: string | undefined; materialSlot: TemplateMaterialSlot | undefined } {
  const usable = (id: TemplateWorkshopSampleId) => options.samples.some((s) => s.sampleId === id && s.targets.length > 0);
  const sampleId = [current.sampleId, options.defaultSampleId, ...options.samples.map((s) => s.sampleId)].find(usable) ?? options.defaultSampleId;
  const present = options.materialSlots.some((s) => s.slot === current.materialSlot && s.present);
  return { sampleId, targetKey: targetFor(options, sampleId, current.targetKey), materialSlot: present ? current.materialSlot : undefined };
}

function targetFor(options: TemplatePreviewOptionsResponse | undefined, sampleId: TemplateWorkshopSampleId, current: string | undefined): string | undefined {
  const targets = options?.samples.find((s) => s.sampleId === sampleId)?.targets ?? [];
  return targets.some((t) => t.key === current) ? current : targets[0]?.key;
}

/** 等新帧时留着上一张（变淡显示），避免闪烁。 */
function loading(previous: SampleFrame | undefined): SampleFrame {
  return { status: 'loading', ...(previous?.frame ? { frame: previous.frame } : {}), ...(previous?.sha256 ? { sha256: previous.sha256 } : {}) };
}

function clampTime(timeUs: number): number {
  return Math.min(WORKSHOP_DURATION_US, Math.max(0, Math.round(timeUs)));
}

function sampleKey(source: SampleSource): string {
  switch (source.kind) {
    case 'saved':
      return ['saved', source.libraryId, source.libraryRevision, source.summary.templateId, source.summary.templateRevision].join('|');
    case 'draft':
      return `draft|${JSON.stringify(source.definition)}`;
    case 'authorDraft':
      return ['author', source.draftId, source.draftRevision, source.authorSourceRevision].join('|');
  }
}

/** 作者草稿的样本选项写成模板试样选项的样子（部位都存在），画布界面共用。 */
function authorOptions(source: AuthorSampleSource): TemplatePreviewOptionsResponse {
  return {
    source: { kind: 'authorDraft', sourceId: source.draftId, sourceRevision: source.draftRevision },
    samples: source.options.samples,
    materialSlots: source.options.materialSlots.map((slot) => ({ slot, editable: false, selected: false, present: true, material: null })),
    defaultSampleId: source.options.defaultSampleId,
    protocolVersion: '1',
  };
}

/**
 * 作者草稿试样失败时的文字（W8 设计 §2.8）：编译失败列出前几条编译诊断，运行出错跟上诊断的消息，超时写明；
 * 其他（设备不可用等）同模板试样。
 */
export function authorSampleFailure(error: PreviewFailedError, diagnostics: readonly BackendDiagnostic[]): SampleFrame {
  const code = error.renderFailureCode;
  const failed = (message: string): SampleFrame => ({ status: 'error', message, ...(code ? { failureCode: code } : {}) });
  switch (code) {
    case 'WorkerPackageCompilationFailed': {
      const compile = compileDiagnostics(diagnostics);
      const more = compile.length > COMPILE_LINES ? [`还有 ${compile.length - COMPILE_LINES} 条`] : [];
      return failed(['脚本编译失败', ...compile.slice(0, COMPILE_LINES).map(compileLine), ...more].join('\n'));
    }
    case 'ScriptExecutionFailed': {
      const runtime = diagnostics.find((d) => d.originKind === 'runtime' && normalizeSeverity(d.severity) === 'error');
      return failed(runtime?.message ? `脚本运行出错：${runtime.message}` : `脚本运行出错：${error.message}\n预览未返回详细诊断，请到验证页签运行检查。`);
    }
    case 'WorkerPackageTimedOut':
      return failed('脚本运行超时（约 25 秒没有返回）');
    default:
      return frameFailure(error);
  }
}

/** 预览诊断里的编译诊断：每个效果实例各报一条，按编码、路径、行、列去重。 */
export function compileDiagnostics(diagnostics: readonly BackendDiagnostic[]): BackendSourceDiagnostic[] {
  const seen = new Set<string>();
  return diagnostics.flatMap((d) => {
    if (!d.source) return [];
    const key = [d.source.code, d.source.path ?? '', d.source.line, d.source.column].join('\u0000');
    if (seen.has(key)) return [];
    seen.add(key);
    return [d.source];
  });
}

function compileLine(d: BackendSourceDiagnostic): string {
  return [d.code, ...(d.path ? [`${d.path}:${d.line}:${d.column}`] : []), d.message].join(' ');
}

function frameFailure(error: unknown): SampleFrame {
  const device = renderBackendErrorText(error);
  if (device) return { status: 'error', message: `${device}。可以在顶栏改用别的渲染设备。` };
  return { status: 'error', message: `试样失败：${errorText(error)}` };
}
