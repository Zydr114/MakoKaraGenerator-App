// 作者草稿（W8 设计 §2.4）：本标签页的草稿列表、草稿与来源组件的对应、编辑（新建草稿）、关闭、是否已保存、保存与另存为。
// 不依赖 React。本页使用创建它的 client 读写草稿；后端可持久恢复草稿，本页尚未提供恢复入口。
// 后端不记录草稿来自哪个组件，对应关系记在 sessionStorage（按 client ID 区分）。

import { BackendError, ObservableValue, type MakoClient } from '../../client';
import { AUTHOR_DRAFT_LIMIT, type AuthorDraft, type AuthorSourceInspectionResponse, type EffectPackageInspectResponse, type ScriptConfigurationDetailResponse, type ScriptConfiguration } from '../../protocol';
import { FOLLOW_DEFAULT, renderBackendField, type RenderBackendRequest } from '../model/render-device';
import { componentFromDraft, configurationIssues, draftConfigurationInput, editableOf, sameAsComponent } from '../model/script-components';
import { errorText } from '../session/editor-session';
import type { ComponentLibraryStore } from './component-library-store';
import { DraftAdoptionModel } from './draft-adoption';
import { DraftConfigEditor } from './draft-config-editor';
import { DraftSourceModel, readSavedRevisions, sameEditableExact, writeSavedRevisions, type AuthorAdoptionExpectation } from './draft-source';
import { DraftValidationModel, readValidationJob, writeValidationJob } from './draft-validation';
import type { LibraryNotice } from './template-library-store';

export const DRAFT_ORIGINS_KEY = 'mako.author-draft-origins';
export const DRAFT_LIMIT_TEXT = `草稿已有 ${AUTHOR_DRAFT_LIMIT} 个，先关闭不用的草稿。`;

/** 草稿详情的页签。 */
export type DraftTab = 'config' | 'source' | 'validation';

/** saved：和来源组件一致；unsaved：有没保存到组件库的改动；orphan：没有来源组件（已删除或对应关系丢了）；checking：还在读。 */
export type DraftSaveState = 'saved' | 'unsaved' | 'orphan' | 'checking';

/** 带完整配置和试样选项的草稿（读取一定给出两者，列表和命令回执没有时补读）。 */
export type FullDraft = AuthorDraft & { configuration: NonNullable<AuthorDraft['configuration']>; sampleOptions: NonNullable<AuthorDraft['sampleOptions']> };

/** 由草稿正式 profile 分派；旧宿主可回读固定包清单。 */
export function isV2AuthorDraft(draft: AuthorDraft | undefined, pkg?: EffectPackageInspectResponse | AuthorSourceInspectionResponse): boolean {
  if (draft?.packageVersion !== undefined || draft?.sdkVersion !== undefined)
    return draft.packageVersion === 3 && draft.sdkVersion?.split('.')[0] === '2';
  if (pkg && 'packageVersion' in pkg) return pkg.packageVersion === 3 && pkg.sdkVersion.split('.')[0] === '2';
  return pkg?.manifest.packageVersion === 3 && typeof pkg.manifest.sdkVersion === 'string' && pkg.manifest.sdkVersion.split('.')[0] === '2';
}

/** 配置控件只使用在用源码的精确 metadata；来源组件同 packageId 不代表同内容。 */
export function exactDraftPackage<T extends { packageId: string; packageRevision: string }>(draft: FullDraft, pkg: T | undefined): T | undefined {
  return pkg?.packageId === draft.packageId && pkg.packageRevision === draft.configuration.sourceRevision ? pkg : undefined;
}

export interface DraftSourceMetadataState {
  status: 'loading' | 'ready' | 'failed';
  expectedDraftRevision: string;
  sourceRevision: string;
  identity: string;
  error?: string;
}

export interface DraftsState {
  status: 'idle' | 'loading' | 'ready' | 'failed';
  drafts: FullDraft[];
  /** 草稿 → 来源组件。 */
  origins: Readonly<Record<string, string>>;
  /** 来源组件当前的配置；null 表示已不在库里；没有键表示还在读。 */
  originConfigs: Readonly<Record<string, ScriptConfiguration | null>>;
  /** 配置页所用的固定包元数据；必须精确匹配 configuration.sourceRevision，不能拿来源组件旧清单解释新包。 */
  packages: Readonly<Record<string, AuthorSourceInspectionResponse>>;
  sourceMetadata: Readonly<Record<string, DraftSourceMetadataState>>;
  /** 各草稿停在哪个页签；没有键表示配置页签。 */
  tabs: Readonly<Record<string, DraftTab>>;
  /** 新建、关闭、保存进行中。 */
  busy: boolean;
  notice?: LibraryNotice;
  /** 连接身份重置（Server 重启等）后，说明当前页面尚无恢复入口。 */
  lost?: string;
}

type DraftsPatch = { [K in keyof DraftsState]?: DraftsState[K] | undefined };

export function draftSaveState(state: DraftsState, draft: FullDraft): DraftSaveState {
  const origin = state.origins[draft.draftId];
  if (!origin) return 'orphan';
  const config = state.originConfigs[origin];
  if (config === undefined) return 'checking';
  if (config === null) return 'orphan';
  return sameAsComponent(draft.configuration, config) ? 'saved' : 'unsaved';
}

export class AuthorDraftStore {
  readonly state$ = new ObservableValue<DraftsState>({ status: 'idle', drafts: [], origins: {}, originConfigs: {}, packages: {}, sourceMetadata: {}, tabs: {}, busy: false });
  private loading: Promise<void> | undefined;
  private libraryRevision: string | undefined;
  /** 各草稿配置页签的编辑器：撤销栈留在本页内存里，切草稿不丢。 */
  private readonly editors = new Map<string, DraftConfigEditor>();
  /** 各草稿源码页签的文件与编辑器状态，同样留在本页内存里。 */
  private readonly sources = new Map<string, DraftSourceModel>();
  /** 各草稿验证页签的任务与报告；离开页签时任务不停。 */
  private readonly validations = new Map<string, DraftValidationModel>();
  private readonly adoptions = new Map<string, DraftAdoptionModel>();
  private readonly metadataGenerations = new Map<string, number>();
  private readonly metadataCache = new Map<string, AuthorSourceInspectionResponse>();
  private metadataGeneration = 0;

  constructor(
    private readonly client: MakoClient,
    private readonly components: ComponentLibraryStore,
    /** 渲染设备偏好：作者验证按它传 renderBackend（渲染设备设计 §2.2）。 */
    private readonly device: RenderBackendRequest = FOLLOW_DEFAULT,
  ) {
    // 组件库修订变了（保存、改名、别处修改），来源组件的配置要重读。
    components.state$.subscribe((library) => {
      if (library.revision === this.libraryRevision) return;
      this.libraryRevision = library.revision;
      if (this.state.status === 'ready') void this.refreshOrigins();
    });
  }

  get state(): DraftsState {
    return this.state$.get();
  }

  draft(draftId: string): FullDraft | undefined {
    return this.state.drafts.find((d) => d.draftId === draftId);
  }

  /** 本标签页里来自这个组件的草稿。 */
  draftFor(configurationId: string): FullDraft | undefined {
    return this.state.drafts.find((d) => this.state.origins[d.draftId] === configurationId);
  }

  saveState(draft: FullDraft): DraftSaveState {
    return draftSaveState(this.state, draft);
  }

  /** 有没保存到组件库的草稿，或页签里有没进草稿的改动（配置没提交、源码没保存成修订；关闭标签页前提示）。 */
  get hasUnsaved(): boolean {
    return (
      this.state.drafts.some((d) => this.saveState(d) !== 'saved') ||
      [...this.editors.values()].some((e) => e.unsettled) ||
      [...this.sources.values()].some((m) => m.dirty) ||
      [...this.adoptions.values()].some((m) => m.hasUnsaved)
    );
  }

  tab(draftId: string): DraftTab {
    return this.state.tabs[draftId] ?? 'config';
  }

  setTab(draftId: string, tab: DraftTab): void {
    if (this.tab(draftId) !== tab) this.patch({ tabs: { ...this.state.tabs, [draftId]: tab } });
  }

  /** 草稿配置页签的编辑器；第一次用时建。 */
  configEditor(draftId: string): DraftConfigEditor | undefined {
    const existing = this.editors.get(draftId);
    if (existing) return existing;
    const draft = this.draft(draftId);
    if (!draft) return undefined;
    const editor = new DraftConfigEditor(
      {
        current: () => this.draft(draftId),
        validate: (value) => {
          const current = this.draft(draftId) ?? draft;
          const pkg = this.state.sourceMetadata[draftId]?.status === 'ready' ? exactDraftPackage(current, this.state.packages[draftId]) : undefined;
          const issues = configurationIssues(value, current.configuration.kind, pkg);
          if (!pkg) issues.push({ message: '正在等待当前在用源码的精确 metadata；配置原值已保留。' });
          return issues;
        },
        put: async (current, input) => {
          const next = await this.full(await this.client.drafts.setConfiguration(draftId, current.draftRevision, input));
          this.replace(next);
          return next;
        },
        refresh: () => this.refreshDraft(draftId),
      },
      draft,
    );
    this.editors.set(draftId, editor);
    return editor;
  }

  /** 草稿源码页签的模型；第一次用时建（这时还不读源码）。 */
  sourceModel(draftId: string): DraftSourceModel | undefined {
    const existing = this.sources.get(draftId);
    if (existing) return existing;
    if (!this.draft(draftId)) return undefined;
    const clientId = () => this.client.state$.get().clientId;
    const model = new DraftSourceModel({
      current: () => this.draft(draftId),
      isV2: () => isV2AuthorDraft(this.draft(draftId), this.state.packages[draftId]),
      settleConfiguration: async () => (await this.editors.get(draftId)?.flush()) ?? true,
      readPackage: (draft, revision) => this.client.drafts.packageFiles(draftId, draft.draftRevision, revision),
      savePackage: async (draft, input) => {
        const next = await this.full(await this.client.drafts.savePackageFiles(draftId, draft.draftRevision, input));
        this.replace(next);
        return next;
      },
      read: (draft, revision) => this.client.drafts.sources(draftId, draft.draftRevision, revision),
      save: (draft, files) => this.client.drafts.saveSources(draftId, draft.draftRevision, files),
      adopt: async (draft, revision, configuration) => {
        const response = await this.client.drafts.adopt(draftId, draft.draftRevision, revision, configuration);
        if (response.draftId !== draftId || response.packageId !== draft.packageId || response.adoptedSourceRevision !== revision
          || response.previousSourceRevision !== draft.configuration.sourceRevision || !response.draftRevision)
          throw new Error('采用回执身份不匹配，未更新页面；请重新读取并比较。');
        return response;
      },
      lockConfiguration: () => this.configEditor(draftId)?.lockForAdoption() ?? (() => {}),
      adoptionBoundary: (draft) => this.editors.get(draftId)?.schemaBoundary(draft),
      adoptionUncertain: () => this.editors.get(draftId)?.adoptionUncertain(),
      use: async (draft, revision) => {
        const next = await this.full(
          await this.client.drafts.setConfiguration(draftId, draft.draftRevision, { ...draftConfigurationInput(draft.configuration), sourceRevision: revision }),
        );
        this.replace(next);
        return next;
      },
      refresh: (expectedAdoption) => this.refreshDraft(draftId, expectedAdoption),
      compile: (draft, revision) => this.client.drafts.compile(draftId, draft.draftRevision, revision),
      waitJob: (jobId, onProgress, signal) => this.client.jobs.waitForTerminal(jobId, { signal, onProgress }),
      cancelJob: (jobId) => this.client.jobs.cancel(jobId),
      compilationResult: (jobId) => this.client.drafts.compilationResult(jobId),
      lastCompilation: async () => {
        try {
          return await this.client.drafts.compilation(draftId);
        } catch (error) {
          if (error instanceof BackendError && error.status === 404) return undefined;
          throw error;
        }
      },
      savedRevisions: () => readSavedRevisions(clientId(), draftId),
      rememberSaved: (revisions) =>
        writeSavedRevisions(
          clientId(),
          draftId,
          revisions,
          this.state.drafts.map((d) => d.draftId),
        ),
    });
    this.sources.set(draftId, model);
    return model;
  }

  /** 采用目标 metadata 和本地配置与普通自动提交编辑器完全分开。 */
  adoptionModel(draftId: string): DraftAdoptionModel | undefined {
    const existing = this.adoptions.get(draftId);
    if (existing) return existing;
    if (!this.draft(draftId)) return undefined;
    const model = new DraftAdoptionModel({
      identity: () => this.connectionIdentity(),
      current: () => this.draft(draftId),
      settleConfiguration: async () => (await this.configEditor(draftId)?.flush()) ?? false,
      refresh: () => this.readAdoptionComparison(draftId),
      inspect: (draft, sourceRevision) => this.client.drafts.inspectSource(draftId, draft.draftRevision, sourceRevision),
      compiled: (revision) => this.sourceModel(draftId)?.hasSuccessfulCompilation(revision) ?? false,
      adopt: async (candidate) => this.sourceModel(draftId)!.adoptCandidate(candidate.targetSourceRevision,
        candidate.value, candidate.baseDraftRevision, candidate.before),
    });
    this.adoptions.set(draftId, model);
    return model;
  }

  /** 显式用户比较入口：正常 reload/refresh/metadata arrival 都不能解除不确定采用冻结。 */
  async readAdoptionComparison(draftId: string): Promise<FullDraft> {
    const identity = this.connectionIdentity();
    const fresh = await this.refreshDraft(draftId);
    await this.loadSourceMetadata(draftId, true);
    const current = this.draft(draftId);
    if (identity !== this.connectionIdentity() || !current || current.draftRevision !== fresh.draftRevision
      || this.state.sourceMetadata[draftId]?.status !== 'ready')
      throw new Error('采用比较尚未获得同身份、同草稿的精确 metadata；保留冻结，请重试。');
    const editor = this.editors.get(draftId);
    if (editor?.state.adoptionUncertain) {
      editor.confirmAdoptionComparison(current);
      this.sources.get(draftId)?.clearAdoptionHistory();
    }
    return current;
  }

  /** 重试只读 metadata；不修改配置或源码。 */
  retrySourceMetadata(draftId: string): void { void this.loadSourceMetadata(draftId, true); }

  /** 草稿验证页签的模型；第一次用时建（这时还不重读上一次的任务）。 */
  validationModel(draftId: string): DraftValidationModel | undefined {
    const existing = this.validations.get(draftId);
    if (existing) return existing;
    if (!this.draft(draftId)) return undefined;
    const clientId = () => this.client.state$.get().clientId;
    const model = new DraftValidationModel({
      current: () => this.draft(draftId),
      refresh: () => this.refreshDraft(draftId),
      prepare: async () => {
        const editor = this.editors.get(draftId);
        if (editor && !(await editor.flush())) throw new Error(editor.state.rejected?.message ?? '配置里还有问题，改好后再验证。');
        if (this.sources.get(draftId)?.state.busy) throw new Error('源码修订正在修改，请稍后再验证。');
      },
      validate: (draft, input) => this.client.drafts.validate(draftId, draft.draftRevision, input),
      job: (jobId) => this.client.jobs.get(jobId),
      waitJob: (jobId, onProgress, signal) => this.client.jobs.waitForTerminal(jobId, { signal, onProgress }),
      cancelJob: (jobId) => this.client.jobs.cancel(jobId),
      report: (jobId) => this.client.jobs.validationResult(jobId),
      renderBackend: () => renderBackendField(this.device),
      lastJob: () => readValidationJob(clientId(), draftId),
      rememberJob: (jobId) =>
        writeValidationJob(
          clientId(),
          draftId,
          jobId,
          this.state.drafts.map((d) => d.draftId),
        ),
    });
    this.validations.set(draftId, model);
    return model;
  }

  /** 读取本 client 的草稿；已在读取时复用同一次。 */
  load(): Promise<void> {
    this.loading ??= this.reload().finally(() => (this.loading = undefined));
    return this.loading;
  }

  /** 编辑一个组件：本标签页已有它的草稿就打开，否则以它为来源新建草稿。 */
  async edit(configurationId: string): Promise<boolean> {
    const existing = this.draftFor(configurationId);
    if (existing) {
      this.components.selectDraft(existing.draftId);
      return true;
    }
    if (this.state.busy) return false;
    if (this.state.drafts.length >= AUTHOR_DRAFT_LIMIT) {
      this.patch({ notice: { level: 'warning', message: DRAFT_LIMIT_TEXT } });
      return false;
    }
    const revision = this.components.state.revision;
    const summary = this.components.summary(configurationId);
    if (!revision || !summary) return false;
    this.patch({ busy: true, notice: undefined, lost: undefined });
    try {
      const created = await this.full(
        await this.client.drafts.create({ exportId: summary.exportId, name: summary.name, configurationId, expectedConfigurationRevision: revision }),
      );
      this.setOrigins({ ...this.state.origins, [created.draftId]: configurationId });
      this.patch({ drafts: [...this.state.drafts, created] });
      void this.loadSourceMetadata(created.draftId);
      void this.refreshOrigins();
      this.components.selectDraft(created.draftId);
      return true;
    } catch (error) {
      if (error instanceof BackendError && error.status === 409) {
        this.patch({ notice: { level: 'warning', message: '组件库已被修改，已重新读取，请再点一次“编辑”。' } });
        void this.components.load();
      } else if (error instanceof BackendError && error.status === 429) {
        this.patch({ notice: { level: 'warning', message: DRAFT_LIMIT_TEXT } });
        void this.load();
      } else this.patch({ notice: { level: 'error', message: `新建草稿失败：${errorText(error)}` } });
      return false;
    } finally {
      this.patch({ busy: false });
    }
  }

  /** 关闭（删除）草稿。修订过期时读一次最新修订再删；草稿已经不在时当作关掉了。 */
  async close(draftId: string): Promise<boolean> {
    const draft = this.draft(draftId);
    if (!draft || this.state.busy) return false;
    this.patch({ busy: true, notice: undefined });
    try {
      try {
        await this.client.drafts.delete(draftId, draft.draftRevision);
      } catch (error) {
        if (error instanceof BackendError && error.status === 409) {
          const fresh = await this.client.drafts.get(draftId);
          await this.client.drafts.delete(draftId, fresh.draftRevision);
        } else if (!(error instanceof BackendError && error.status === 404)) throw error;
      }
      this.forget(draftId);
      return true;
    } catch (error) {
      this.patch({ notice: { level: 'error', message: `关闭草稿失败：${errorText(error)}` } });
      return false;
    } finally {
      this.patch({ busy: false });
    }
  }

  /** 保存到来源组件：替换命令，来源是草稿当前修订和在用修订。组件库修订过期时重读后再试一次。 */
  async save(draftId: string): Promise<boolean> {
    const draft = this.draft(draftId);
    const origin = this.state.origins[draftId];
    if (!draft || !origin || this.state.busy) return false;
    this.patch({ busy: true, notice: undefined });
    try {
      const response = await this.components.command({ type: 'replace', configurationId: origin, configuration: componentFromDraft(draft) }, '保存到组件库失败', {
        retryOnConflict: true,
      });
      if (response) await this.refreshOrigins();
      return !!response;
    } finally {
      this.patch({ busy: false });
    }
  }

  /** 另存为新组件：名称和草稿不同时先改草稿名称（两边一致才算已保存），再新建，并把对应关系改成新组件。 */
  async saveAs(draftId: string, name: string): Promise<boolean> {
    let draft = this.draft(draftId);
    const trimmed = name.trim();
    if (!draft || this.state.busy) return false;
    this.patch({ busy: true, notice: undefined });
    try {
      if (draft.configuration.name !== trimmed) {
        try {
          draft = await this.full(await this.client.drafts.setConfiguration(draftId, draft.draftRevision, { ...draftConfigurationInput(draft.configuration), name: trimmed }));
          this.replace(draft);
        } catch (error) {
          this.patch({ notice: { level: 'error', message: `改草稿名称失败：${errorText(error)}` } });
          return false;
        }
      }
      const response = await this.components.command({ type: 'create', configuration: componentFromDraft(draft) }, '另存为新组件失败', { retryOnConflict: true });
      if (!response) return false;
      this.setOrigins({ ...this.state.origins, [draftId]: response.configurationId });
      await this.refreshOrigins();
      return true;
    } finally {
      this.patch({ busy: false });
    }
  }

  /** 用后端返回的新草稿替换列表里的旧草稿（配置、源码、采用之后）。 */
  replace(draft: FullDraft): void {
    const packages = { ...this.state.packages };
    const pkg = packages[draft.draftId];
    if (pkg && !exactDraftPackage(draft, pkg)) delete packages[draft.draftId];
    this.patch({ drafts: this.state.drafts.map((d) => (d.draftId === draft.draftId ? draft : d)), packages });
    this.editors.get(draft.draftId)?.serverChanged(draft);
    this.adoptions.get(draft.draftId)?.serverChanged(draft);
    if (pkg && !packages[draft.draftId]) this.editors.get(draft.draftId)?.revalidate();
    void this.loadSourceMetadata(draft.draftId);
  }

  clearNotice(): void {
    if (this.state.notice) this.patch({ notice: undefined });
  }

  /** 连接身份重置（Server 重启、重新登记）：清掉本页的旧身份状态。 */
  reset(reason: string): void {
    const had = this.state.drafts.length > 0;
    this.dropPageState(() => true);
    this.metadataCache.clear();
    this.metadataGenerations.clear();
    this.setOrigins({});
    this.patch({ drafts: [], originConfigs: {}, packages: {}, sourceMetadata: {}, tabs: {}, busy: false, notice: undefined, lost: had ? `${reason}，当前页面未提供草稿恢复；已保存组件仍可用。` : undefined });
  }

  /** 重读草稿并写回列表。 */
  private async refreshDraft(draftId: string, expectedAdoption?: AuthorAdoptionExpectation): Promise<FullDraft> {
    const identity = this.connectionIdentity();
    const previous = this.draft(draftId);
    const fresh = await this.full(await this.client.drafts.get(draftId));
    const current = this.draft(draftId);
    if (identity !== this.connectionIdentity() || !previous || !current || fresh.draftId !== draftId || fresh.packageId !== previous.packageId)
      throw new Error('草稿或连接身份已变化，迟到的读取未写入页面。');
    if (current.draftRevision !== previous.draftRevision && current.draftRevision !== fresh.draftRevision)
      throw new Error('读取期间草稿已更新，迟到的读取未覆盖新修订；请重新比较。');
    if (expectedAdoption && (fresh.draftId !== expectedAdoption.draftId || fresh.packageId !== expectedAdoption.packageId
      || fresh.configuration.sourceRevision !== expectedAdoption.sourceRevision || fresh.configuration.packageRevision !== expectedAdoption.sourceRevision
      || (expectedAdoption.draftRevision !== undefined && fresh.draftRevision !== expectedAdoption.draftRevision)
      || !sameEditableExact(editableOf(fresh.configuration), expectedAdoption.configuration)))
      throw new Error('采用后的读取与固定目标不一致，未替换在用配置；请重新读取并比较。');
    this.replace(fresh);
    return fresh;
  }

  /** 列表或命令回执给的草稿没有配置或试样选项时补读一次。 */
  private async full(draft: AuthorDraft): Promise<FullDraft> {
    if (draft.configuration && draft.sampleOptions) return draft as FullDraft;
    const fresh = await this.client.drafts.get(draft.draftId);
    if (!fresh.configuration || !fresh.sampleOptions) throw new Error('后端没有给出草稿配置或试样选项。');
    return fresh as FullDraft;
  }

  private async reload(): Promise<void> {
    const identity = this.connectionIdentity();
    const previousDrafts = this.state.drafts;
    if (this.state.status !== 'ready') this.patch({ status: 'loading' });
    try {
      const list = await this.client.drafts.list();
      const drafts = await Promise.all(list.drafts.map((d) => this.full(d)));
      // 列表读取期间的创建、替换、关闭或身份重置优先；旧快照不能复活/回退草稿。
      if (identity !== this.connectionIdentity() || previousDrafts !== this.state.drafts) return;
      const stored = readOrigins(this.client.state$.get().clientId);
      const origins = Object.fromEntries(Object.entries({ ...stored, ...this.state.origins }).filter(([id]) => drafts.some((d) => d.draftId === id)));
      this.setOrigins(origins);
      this.patch({ status: 'ready', drafts, packages: Object.fromEntries(drafts.flatMap((draft) => {
        const pkg = exactDraftPackage(draft, this.state.packages[draft.draftId]);
        return pkg ? [[draft.draftId, pkg]] : [];
      })) });
      this.dropPageState((id) => !drafts.some((d) => d.draftId === id));
      for (const draft of drafts) {
        this.editors.get(draft.draftId)?.serverChanged(draft);
        this.adoptions.get(draft.draftId)?.serverChanged(draft);
        void this.loadSourceMetadata(draft.draftId);
      }
    } catch (error) {
      this.patch({ status: this.state.status === 'ready' ? 'ready' : 'failed', notice: { level: 'error', message: `读取草稿失败：${errorText(error)}` } });
      return;
    }
    await this.refreshOrigins();
  }

  /** 来源组件只用于保存状态比较；schema 由作者固定源码 inspection 提供。 */
  private async refreshOrigins(): Promise<void> {
    const ids = [...new Set(Object.values(this.state.origins))];
    if (ids.length === 0) return;
    if (this.components.state.status !== 'ready') await this.components.load();
    const details = new Map<string, ScriptConfigurationDetailResponse | null>();
    await Promise.all(
      ids.map(async (id) => {
        try {
          details.set(id, (await this.components.detailOf(id)) ?? null);
        } catch {
          // 读不到时保持“还在读”，下次库修订变化再试。
        }
      }),
    );
    const originConfigs: Record<string, ScriptConfiguration | null> = {};
    for (const [id, detail] of details) originConfigs[id] = detail?.configuration ?? null;
    this.patch({ originConfigs });
  }

  /** 从列表去掉；关的是选中的草稿时改选它的来源组件（不在库里时清空选择）。 */
  private forget(draftId: string): void {
    const { [draftId]: origin, ...origins } = this.state.origins;
    this.setOrigins(origins);
    const { [draftId]: _pkg, ...packages } = this.state.packages;
    const { [draftId]: _tab, ...tabs } = this.state.tabs;
    const { [draftId]: _metadata, ...sourceMetadata } = this.state.sourceMetadata;
    this.dropPageState((id) => id === draftId);
    this.patch({ drafts: this.state.drafts.filter((d) => d.draftId !== draftId), packages, sourceMetadata, tabs });
    const selection = this.components.state.selection;
    if (selection?.kind === 'draft' && selection.draftId === draftId) void this.components.select(origin ?? null);
  }

  /** 丢掉草稿在本页内存里的页签状态（配置编辑器、源码、验证）。 */
  private dropPageState(which: (draftId: string) => boolean): void {
    for (const map of [this.editors, this.sources, this.validations, this.adoptions]) {
      for (const [id, item] of map) {
        if (!which(id)) continue;
        item.dispose();
        map.delete(id);
        this.metadataGenerations.delete(id);
        for (const [key, cached] of this.metadataCache) if (cached.draftId === id) this.metadataCache.delete(key);
      }
    }
  }

  private connectionIdentity(): string {
    const state = this.client.state$.get();
    return JSON.stringify([state.hostInstanceId ?? null, state.clientId ?? null]);
  }

  /** Cache 只缓存不可变 source 声明；每个新 draft CAS 仍重新验证 owner/retained。 */
  private async loadSourceMetadata(draftId: string, force = false): Promise<void> {
    const draft = this.draft(draftId);
    if (!draft) return;
    const sourceRevision = draft.configuration.sourceRevision;
    const previous = this.state.sourceMetadata[draftId];
    if (!force && previous?.expectedDraftRevision === draft.draftRevision && previous.sourceRevision === sourceRevision && previous.identity === this.connectionIdentity()) return;
    const identity = this.connectionIdentity();
    const cacheKey = JSON.stringify([identity, draftId, sourceRevision]);
    const generation = ++this.metadataGeneration;
    this.metadataGenerations.set(draftId, generation);
    const cached = this.metadataCache.get(cacheKey);
    const packages = { ...this.state.packages };
    if (cached && exactDraftPackage(draft, cached)) packages[draftId] = cached;
    this.patch({ packages, sourceMetadata: { ...this.state.sourceMetadata,
      [draftId]: { status: 'loading', expectedDraftRevision: draft.draftRevision, sourceRevision, identity } } });
    // 同 source 的缓存只保留控件；新 CAS 未验证 ready 前，普通队列也必须立即暂停。
    this.editors.get(draftId)?.revalidate();
    try {
      const pkg = await this.client.drafts.inspectSource(draftId, draft.draftRevision, sourceRevision);
      const current = this.draft(draftId);
      if (!current || identity !== this.connectionIdentity() || this.metadataGenerations.get(draftId) !== generation) return;
      if (current.draftRevision !== draft.draftRevision || current.packageId !== draft.packageId || current.configuration.sourceRevision !== sourceRevision) {
        void this.loadSourceMetadata(draftId, true);
        return;
      }
      if (pkg.draftId !== draftId || pkg.draftRevision !== draft.draftRevision || pkg.sourceRevision !== sourceRevision
        || pkg.packageId !== draft.packageId || pkg.packageRevision !== sourceRevision)
        throw new Error('固定源码 metadata 身份不匹配。');
      this.metadataCache.set(cacheKey, pkg);
      this.patch({ packages: { ...this.state.packages, [draftId]: pkg }, sourceMetadata: { ...this.state.sourceMetadata,
        [draftId]: { status: 'ready', expectedDraftRevision: draft.draftRevision, sourceRevision, identity } } });
      this.editors.get(draftId)?.revalidate();
    } catch (error) {
      const current = this.draft(draftId);
      if (!current || identity !== this.connectionIdentity() || this.metadataGenerations.get(draftId) !== generation) return;
      if (current.draftRevision !== draft.draftRevision) { void this.loadSourceMetadata(draftId, true); return; }
      this.patch({ sourceMetadata: { ...this.state.sourceMetadata,
        [draftId]: { status: 'failed', expectedDraftRevision: draft.draftRevision, sourceRevision, identity, error: errorText(error) } } });
    }
  }

  private setOrigins(origins: Record<string, string>): void {
    this.patch({ origins });
    writeOrigins(this.client.state$.get().clientId, origins);
  }

  private patch(patch: DraftsPatch): void {
    const next = { ...this.state, ...patch } as Record<string, unknown>;
    for (const key of Object.keys(patch)) if (next[key] === undefined) delete next[key];
    this.state$.set(next as unknown as DraftsState);
  }
}

function readOrigins(clientId: string | undefined): Record<string, string> {
  try {
    const stored = JSON.parse(sessionStorage.getItem(DRAFT_ORIGINS_KEY) ?? 'null') as { clientId?: string; origins?: Record<string, string> } | null;
    return stored && stored.clientId === clientId && stored.origins ? stored.origins : {};
  } catch {
    return {};
  }
}

function writeOrigins(clientId: string | undefined, origins: Record<string, string>): void {
  try {
    if (!clientId || Object.keys(origins).length === 0) sessionStorage.removeItem(DRAFT_ORIGINS_KEY);
    else sessionStorage.setItem(DRAFT_ORIGINS_KEY, JSON.stringify({ clientId, origins }));
  } catch {
    // 存不下时刷新后只能“另存为新组件”。
  }
}
