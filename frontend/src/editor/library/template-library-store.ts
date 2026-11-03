// 样式库状态（W3 设计 §2.4，W7 设计 §2.2）：两个库的摘要列表、选中模板的完整详情与本地草稿、个人库命令。
// 不依赖 React；页面通过 state$ 订阅。库内容以后端为准：命令成功后重读列表，不在本地改写摘要。
// 草稿和保存一律用完整定义（definitionFormat=full），含效果的个人模板也能编辑。

import { BackendError, ObservableValue, type MakoClient } from '../../client';
import {
  TEMPLATE_PROPERTIES,
  type EffectPackageInspectResponse,
  type EffectPackageReference,
  type FullTemplateDefinition,
  type ScriptConfigurationDetailResponse,
  type TemplateFullDetailResponse,
  type TemplateKind,
  type TemplateLibraryCommand,
  type TemplateLibraryCommandResponse,
  type TemplateLibraryId,
  type TemplateSummary,
} from '../../protocol';
import { packageKey, packageUsers, rebasePackageReferences, referenceKey, replacePackageRevision, syncPackages, usedPackageKeys } from '../model/full-template';
import { addComponentRecipe, componentEffectProblem, componentMaterialProblem, withComponentMaterial } from '../model/script-components';
import type { TemplateMaterialSlot } from '../../protocol';
import { errorText } from '../session/editor-session';

/** 新建模板的起点（§2.4：不凭空构造定义）。 */
export const NEW_TEMPLATE_SOURCE = 'builtin:line-classic';
export const NEW_TEMPLATE_NAME = '新模板';
/** 后端对新名称的上限（规范 §13.2，UTF-16 code units）。 */
export const TEMPLATE_NAME_MAX_LENGTH = 256;

export const KIND_TEXT: Record<TemplateKind, string> = { line: '行', token: '文本单元', composite: '组合' };

export interface LibraryList {
  revision: string;
  templates: TemplateSummary[];
}

/** templateId 为 null 表示个人库里新建、尚未保存的草稿。 */
export interface TemplateSelection {
  libraryId: TemplateLibraryId;
  templateId: string | null;
}

export interface TemplateDraft {
  /** 新建且未保存时为 null。 */
  templateId: string | null;
  /** 草稿的出发点：已保存模板的服务端定义，或新建时的起点。 */
  base: FullTemplateDefinition;
  definition: FullTemplateDefinition;
  /** 草稿自己的历史，与工程撤销无关；每次 edit 一步。 */
  undo: FullTemplateDefinition[];
  redo: FullTemplateDefinition[];
  /** 在这份草稿里导入过的包（asset 引用）：还没被材质或配方用到时不进 packages，但仍列在包列表里。不进历史。 */
  imported: EffectPackageReference[];
}

export interface LibraryNotice {
  level: 'info' | 'warning' | 'error';
  message: string;
}

export interface LibraryState {
  status: 'loading' | 'ready' | 'failed';
  builtin?: LibraryList;
  personal?: LibraryList;
  builtinError?: string;
  personalError?: string;
  refreshing: boolean;
  selection: TemplateSelection | null;
  /** 选中的已保存模板的完整详情；读取中或新建草稿时没有。 */
  detail?: TemplateFullDetailResponse;
  detailLoading: boolean;
  /** 个人库的模板（和新建草稿）有草稿；内置模板只读。 */
  draft?: TemplateDraft;
  /** 个人库命令进行中。 */
  busy: boolean;
  notice?: LibraryNotice;
  /** 读到的包信息，键是 packageKey（包 ID＋修订）：来自完整详情的包闭包、导入时的检查、按引用补读。 */
  catalog: ReadonlyMap<string, EffectPackageInspectResponse>;
}

type LibraryPatch = { [K in keyof LibraryState]?: LibraryState[K] | undefined };

const CONFLICT_TEXT = '模板库已被修改，已重新读取，请检查后重试。';
const SAVE_CONFLICT_TEXT = '模板库已被修改，已重新读取；你的修改仍在，可再次保存。';
/** 后端不说明完整定义哪里不对（G31）。 */
export const INVALID_DEFINITION_TEXT = '后端拒绝了这份定义（InvalidDefinition），没有说明原因。请检查效果的参数和包。';
/** 草稿历史的上限，超出时丢弃最早的。 */
const HISTORY_LIMIT = 200;

export class TemplateLibraryStore {
  readonly state$ = new ObservableValue<LibraryState>({ status: 'loading', refreshing: false, selection: null, detailLoading: false, busy: false, catalog: new Map() });
  /** 正在按引用补读的包。 */
  private readonly inspecting = new Set<string>();
  /** 完整详情缓存：键含库修订，因为包的 library 引用绑定库修订。 */
  private readonly details = new Map<string, TemplateFullDetailResponse>();
  /** 每次改选择加一；晚到的旧详情不覆盖新选择。 */
  private selectSeq = 0;
  private loading: Promise<void> | undefined;
  private componentRefresh: Promise<boolean> | undefined;
  private saving = false;
  /** 删除组件以后仍能说明哪个未保存引用失效。 */
  private readonly componentNames = new Map<string, string>();

  constructor(private readonly client: MakoClient) {}

  get state(): LibraryState {
    return this.state$.get();
  }

  /** 新建草稿一定算未保存；已保存模板比较规范化定义。 */
  get isDirty(): boolean {
    const draft = this.state.draft;
    return !!draft && (draft.templateId === null || !sameDefinition(draft.base, draft.definition));
  }

  /** 读取两个库；已在读取时复用同一次。选中的模板消失时清空选择，库修订变化时重读详情（草稿有修改就保留）。 */
  load(): Promise<void> {
    if (!this.loading) {
      this.patch({ refreshing: true });
      this.loading = this.reload().finally(() => {
        this.patch({ refreshing: false });
        this.loading = undefined;
      });
    }
    return this.loading;
  }

  /** 命令之后重读：等进行中的读取（可能早于命令）结束，再读一次。 */
  private async reloadAfterChange(): Promise<void> {
    await this.loading;
    await this.load();
  }

  async select(selection: { libraryId: TemplateLibraryId; templateId: string } | null): Promise<void> {
    const seq = ++this.selectSeq;
    if (!selection) {
      this.patch({ selection: null, detail: undefined, draft: undefined, detailLoading: false });
      return;
    }
    const summary = this.summary(selection.libraryId, selection.templateId);
    if (!summary) return;
    const cached = this.details.get(this.detailKey(selection.libraryId, summary));
    this.patch({ selection, detail: cached, draft: cached && draftFor(cached), detailLoading: !cached });
    if (cached) return;
    try {
      const detail = await this.fetchDetail(selection.libraryId, summary);
      if (seq !== this.selectSeq) return;
      this.patch({ detail, draft: draftFor(detail), detailLoading: false });
    } catch (error) {
      if (seq !== this.selectSeq) return;
      this.patch({ detailLoading: false });
      await this.fail('读取模板失败', error);
    }
  }

  /** 新建：以内置“经典整行”为起点的未保存草稿。 */
  async createDraft(): Promise<void> {
    if (!this.state.personal || this.state.refreshing) return;
    const seq = ++this.selectSeq;
    const source = this.summary('builtin', NEW_TEMPLATE_SOURCE);
    if (!source) {
      this.patch({ notice: { level: 'error', message: '内置库里没有“经典整行”，无法新建。' } });
      return;
    }
    try {
      const detail = this.details.get(this.detailKey('builtin', source)) ?? (await this.fetchDetail('builtin', source));
      if (seq !== this.selectSeq || !this.state.personal || this.state.refreshing) return;
      const base = { ...detail.definition, name: NEW_TEMPLATE_NAME };
      this.patch({ selection: { libraryId: 'personal', templateId: null }, detail: undefined, detailLoading: false, draft: { templateId: null, base, definition: base, undo: [], redo: [], imported: [] } });
    } catch (error) {
      await this.fail('新建失败', error);
    }
  }

  /** 内置“经典整行”的完整定义（新建模板和效果组件试样的底）；读过就用缓存。 */
  async starterDefinition(): Promise<FullTemplateDefinition> {
    if (!this.state.builtin) await this.load();
    const source = this.summary('builtin', NEW_TEMPLATE_SOURCE);
    if (!source) throw new Error('内置库里没有“经典整行”。');
    const detail = this.details.get(this.detailKey('builtin', source)) ?? (await this.fetchDetail('builtin', source));
    return detail.definition;
  }

  /** 放弃修改：新建草稿整个丢掉，已保存模板回到服务端定义（历史清空）。 */
  discardDraft(): void {
    const draft = this.state.draft;
    if (!draft) return;
    if (draft.templateId === null) void this.select(null);
    else this.patch({ draft: { ...draft, definition: draft.base, undo: [], redo: [] } });
  }

  /** 改草稿，记一步历史；与当前相同时不记。packages 按材质和配方用到的包重算（后端要求恰好是这些包）。 */
  edit(next: FullTemplateDefinition): void {
    const draft = this.state.draft;
    if (!draft || this.state.busy) return;
    const synced = syncPackages(next, knownReferences(draft));
    if (sameDefinition(draft.definition, synced)) return;
    this.patch({ draft: { ...draft, definition: synced, undo: [...draft.undo, draft.definition].slice(-HISTORY_LIMIT), redo: [] } });
  }

  /** 从组件复制一个配方。包目录收下详情；同一包优先沿用模板已有的引用。 */
  addComponentEffect(detail: ScriptConfigurationDetailResponse): string | undefined {
    const draft = this.state.draft;
    if (!draft || this.state.busy) return undefined;
    const problem = componentEffectProblem(draft.definition.kind, detail);
    if (problem) {
      this.patch({ notice: { level: 'error', message: problem } });
      return undefined;
    }
    this.rememberComponent(detail);
    const added = addComponentRecipe(draft.definition, detail);
    this.edit(added.definition);
    return added.recipeId;
  }

  /** 材质没有 configurationId；包引用仍然带组件来源，保存前可重定位。 */
  useComponentMaterial(slot: TemplateMaterialSlot, detail: ScriptConfigurationDetailResponse): boolean {
    const draft = this.state.draft;
    if (!draft || this.state.busy) return false;
    const problem = componentMaterialProblem(slot, detail);
    if (problem) {
      this.patch({ notice: { level: 'error', message: problem } });
      return false;
    }
    this.rememberComponent(detail);
    this.edit(withComponentMaterial(draft.definition, slot, detail));
    return true;
  }

  private rememberComponent(detail: ScriptConfigurationDetailResponse): void {
    this.componentNames.set(detail.configuration.configurationId, detail.configuration.name);
    this.remember([detail.package]);
  }

  /**
   * 组件库修订属于整库：只换来源引用，不追改模板的配置和包内容快照。当前草稿及历史一起换，撤销不恢复过期引用。
   * 组件已删或改用了另一个包修订时，旧包无从授权；保留草稿，让用户移除后重新添加。
   */
  refreshComponentReferences(): Promise<boolean> {
    this.componentRefresh ??= this.rebaseComponentReferences().finally(() => { this.componentRefresh = undefined; });
    return this.componentRefresh;
  }

  private async rebaseComponentReferences(): Promise<boolean> {
    const before = this.state.draft;
    if (!before || !before.definition.packages.some((r) => r.kind === 'configuration')) return true;
    const seq = this.selectSeq;
    try {
      const list = await this.client.scriptConfigurations.list();
      const refs = [...before.definition.packages, ...knownReferences(before)].filter((r) => r.kind === 'configuration');
      const ids = [...new Set(refs.map((r) => r.configurationId))];
      const fresh: EffectPackageReference[] = [];
      const unavailable = new Map<string, 'deleted' | 'changed'>();
      const sourceKey = (reference: Extract<EffectPackageReference, { kind: 'configuration' }>) => `${reference.configurationId}\u0000${referenceKey(reference)}`;
      for (const id of ids) {
        const summary = list.configurations.find((c) => c.configurationId === id);
        if (!summary) {
          for (const reference of refs.filter((r) => r.configurationId === id)) unavailable.set(sourceKey(reference), 'deleted');
          continue;
        }
        this.componentNames.set(id, summary.name);
        const detail = await this.client.scriptConfigurations.detail(id, list.revision);
        this.rememberComponent(detail);
        fresh.push(detail.package.reference);
        for (const reference of refs.filter((r) => r.configurationId === id && referenceKey(r) !== referenceKey(detail.package.reference))) unavailable.set(sourceKey(reference), 'changed');
      }
      if (seq !== this.selectSeq) return false;
      const current = this.state.draft;
      if (!current) return false;
      const rebase = (d: FullTemplateDefinition) => rebasePackageReferences(d, fresh);
      this.patch({ draft: {
        ...current, base: rebase(current.base), definition: rebase(current.definition), undo: current.undo.map(rebase), redo: current.redo.map(rebase),
        imported: rebasePackageReferences({ ...current.definition, packages: current.imported }, fresh).packages,
      } });
      // 历史里已经删去的组件不妨碍当前定义保存；撤销回来时再给出原因。
      const blocked = current.definition.packages.find((r) => r.kind === 'configuration' && unavailable.has(sourceKey(r)));
      if (blocked?.kind === 'configuration') {
        const name = this.componentNames.get(blocked.configurationId) ?? current.definition.effectRecipes.find((r) => r.configurationId === blocked.configurationId)?.name ?? blocked.configurationId;
        const message = unavailable.get(sourceKey(blocked)) === 'deleted'
          ? `效果组件“${name}”已经删除，请移除这个效果后重新添加（材质请重新选择）。`
          : `效果组件“${name}”的包修订已经改变，请移除这个效果后重新添加（材质请重新选择）。`;
        this.patch({ notice: { level: 'error', message } });
        return false;
      }
      return true;
    } catch (error) {
      if (seq === this.selectSeq) this.patch({ notice: { level: 'error', message: `重新定位效果组件失败：${errorText(error)}。请重试保存。` } });
      return false;
    }
  }

  /** 上传 .mkfx 并检查（只读，不改草稿）；结果进包目录。失败时抛错，由调用方显示。 */
  async inspectPackageFile(file: File): Promise<EffectPackageInspectResponse> {
    const asset = await this.client.assets.upload(file, { fileName: file.name });
    const inspected = await this.client.effectPackages.inspect({ packageAssetId: asset.assetId });
    this.remember([inspected]);
    return inspected;
  }

  /**
   * 把检查过的包加进草稿的包列表。草稿里已经用着同一包的别的修订时，改成新修订（一步历史）。
   * 结构无效的包不加。返回被换掉的旧修订（没有时为空）。
   */
  addPackage(inspected: EffectPackageInspectResponse): string[] {
    const draft = this.state.draft;
    if (!draft || this.state.busy || !inspected.structureValid) return [];
    const key = packageKey(inspected.packageId, inspected.packageRevision);
    const imported = draft.imported.some((r) => referenceKey(r) === key) ? draft.imported : [...draft.imported, inspected.reference];
    this.patch({ draft: { ...draft, imported } });
    const replaced = usedPackageKeys(draft.definition)
      .map((k) => draft.definition.packages.find((r) => referenceKey(r) === k))
      .filter((r): r is EffectPackageReference => !!r && r.packageId === inspected.packageId && r.packageRevision !== inspected.packageRevision)
      .map((r) => r.packageRevision);
    if (replaced.length > 0) {
      let next = draft.definition;
      for (const from of replaced) next = replacePackageRevision(next, inspected.packageId, from, inspected.packageRevision);
      this.edit(next);
    }
    return replaced;
  }

  /** 从包列表移除；还有材质或配方在用时不移除，返回在用的地方。 */
  removePackage(key: string): string[] {
    const draft = this.state.draft;
    if (!draft || this.state.busy) return [];
    const users = packageUsers(draft.definition, key);
    if (users.length > 0) return users;
    this.patch({ draft: { ...draft, imported: draft.imported.filter((r) => referenceKey(r) !== key) } });
    return [];
  }

  undo(): void {
    const draft = this.state.draft;
    const previous = draft?.undo.at(-1);
    if (!draft || !previous || this.state.busy) return;
    this.patch({ draft: { ...draft, definition: previous, undo: draft.undo.slice(0, -1), redo: [...draft.redo, draft.definition] } });
  }

  redo(): void {
    const draft = this.state.draft;
    const next = draft?.redo.at(-1);
    if (!draft || !next || this.state.busy) return;
    this.patch({ draft: { ...draft, definition: next, undo: [...draft.undo, draft.definition], redo: draft.redo.slice(0, -1) } });
  }

  /** 新建草稿发 create，已有模板发 replace，都用完整定义；成功后选中保存后的模板。 */
  async save(): Promise<boolean> {
    if (this.saving || this.state.busy || !this.state.draft || !this.state.personal || this.state.refreshing) return false;
    this.saving = true;
    try {
      if (!(await this.refreshComponentReferences())) return false;
      const draft = this.state.draft;
      if (!draft) return false;
      const { definition } = draft;
      const command: TemplateLibraryCommand =
        draft.templateId === null
          ? { type: 'create', definitionFormat: 'full', definition }
          : { type: 'replace', templateId: draft.templateId, definitionFormat: 'full', definition };
      const response = await this.command(command, '保存失败', SAVE_CONFLICT_TEXT);
      if (!response) return false;
      await this.selectSaved(response.templateId);
      return true;
    } finally {
      this.saving = false;
    }
  }

  /** 服务端复制到个人库（来源库按读到的修订固定），成功后选中新模板并返回它的 ID。 */
  async copy(sourceLibraryId: TemplateLibraryId, templateId: string, name: string): Promise<string | undefined> {
    const source = this.state[sourceLibraryId];
    if (!source) return undefined;
    const response = await this.command({ type: 'copy', sourceLibraryId, sourceRevision: source.revision, templateId, name }, '复制失败');
    if (!response) return undefined;
    await this.selectSaved(response.templateId);
    return response.templateId;
  }

  /** 库在别处写入之后（导入 .mktpl）：重读两个库，选中给出的个人模板。 */
  async showChanged(templateId: string | undefined): Promise<void> {
    await this.reloadAfterChange();
    if (templateId) await this.selectSaved(templateId);
  }

  /** 选中刚写入的个人模板：先读好详情再换选择，编辑面板不经过“读取中”。 */
  private async selectSaved(templateId: string): Promise<void> {
    const summary = this.summary('personal', templateId);
    if (summary && !this.details.has(this.detailKey('personal', summary))) await this.fetchDetail('personal', summary).catch(() => undefined);
    await this.select({ libraryId: 'personal', templateId });
  }

  async rename(templateId: string, name: string): Promise<boolean> {
    return !!(await this.command({ type: 'rename', templateId, name }, '改名失败'));
  }

  async remove(templateId: string): Promise<boolean> {
    return !!(await this.command({ type: 'delete', templateId }, '删除失败'));
  }

  clearNotice(): void {
    if (this.state.notice) this.patch({ notice: undefined });
  }

  summary(libraryId: TemplateLibraryId, templateId: string): TemplateSummary | undefined {
    return this.state[libraryId]?.templates.find((t) => t.templateId === templateId);
  }

  /** 个人库命令：带读到的库修订做 CAS；409 时重读两个库。成功后重读列表，选中的模板随之刷新。 */
  private async command(command: TemplateLibraryCommand, failure: string, conflictText = CONFLICT_TEXT): Promise<TemplateLibraryCommandResponse | undefined> {
    if (this.state.busy || this.state.refreshing || !this.state.personal) return undefined;
    const personal = this.state.personal;
    if (!personal) return undefined;
    this.patch({ busy: true, notice: undefined });
    try {
      const response = await this.client.templates.command('personal', personal.revision, command);
      await this.reloadAfterChange();
      return response;
    } catch (error) {
      await this.fail(failure, error, conflictText);
      return undefined;
    } finally {
      this.patch({ busy: false });
    }
  }

  private async reload(): Promise<void> {
    const [builtin, personal] = await Promise.allSettled([this.client.templates.listAll('builtin'), this.client.templates.listAll('personal')]);
    this.patch({
      status: builtin.status === 'fulfilled' || personal.status === 'fulfilled' ? 'ready' : 'failed',
      builtin: builtin.status === 'fulfilled' ? builtin.value : undefined,
      personal: personal.status === 'fulfilled' ? personal.value : undefined,
      builtinError: builtin.status === 'rejected' ? `读取内置模板库失败：${libraryErrorText(builtin.reason)}` : undefined,
      personalError: personal.status === 'rejected' ? `读取个人模板库失败：${libraryErrorText(personal.reason)}` : undefined,
    });
    const selection = this.state.selection;
    if (!selection || this.state[selection.libraryId]) await this.refreshSelection();
  }

  /**
   * 列表重读后：选中的模板没了就清空（草稿有修改时转成新建草稿，修改不丢）；
   * 库修订变了就重读完整详情：模板没变或草稿有修改时只换 base，并把草稿各步里过期的包引用换成新的（§2.2），修改和历史都保留。
   */
  private async refreshSelection(): Promise<void> {
    const { selection, detail, draft, detailLoading } = this.state;
    if (!selection || selection.templateId === null || detailLoading) return;
    const summary = this.summary(selection.libraryId, selection.templateId);
    if (!summary) {
      if (draft && this.isDirty) {
        this.selectSeq++;
        this.patch({
          selection: { libraryId: 'personal', templateId: null },
          detail: undefined,
          draft: { ...draft, templateId: null },
          notice: { level: 'warning', message: '这个模板已在别处删除；你的修改保留为未保存的新模板。' },
        });
      } else await this.select(null);
      return;
    }
    if (detail && detail.revision === this.state[selection.libraryId]?.revision && detail.summary.templateRevision === summary.templateRevision) return;
    const seq = this.selectSeq;
    const dirty = this.isDirty;
    const sameTemplate = detail?.summary.templateRevision === summary.templateRevision;
    try {
      const next = await this.fetchDetail(selection.libraryId, summary);
      if (seq !== this.selectSeq) return;
      const fresh = draftFor(next);
      const current = this.state.draft;
      const keep = current && fresh && (dirty || sameTemplate);
      this.patch({ detail: next, draft: keep ? rebaseDraft({ ...current, base: fresh.base }, next) : fresh });
    } catch (error) {
      if (seq === this.selectSeq) this.patch({ notice: { level: 'error', message: `读取模板失败：${errorText(error)}` } });
    }
  }

  private async fetchDetail(libraryId: TemplateLibraryId, summary: TemplateSummary): Promise<TemplateFullDetailResponse> {
    const revision = this.state[libraryId]!.revision;
    const detail = await this.client.templates.fullDetail(libraryId, summary.templateId, revision);
    this.details.set(this.detailKey(libraryId, detail.summary, detail.revision), detail);
    this.remember(detail.packageClosureDetails);
    void this.inspectMissing(detail.definition.packages);
    return detail;
  }

  private remember(packages: readonly EffectPackageInspectResponse[]): void {
    const missing = packages.filter((p) => !this.state.catalog.has(packageKey(p.packageId, p.packageRevision)));
    if (missing.length === 0) return;
    const catalog = new Map(this.state.catalog);
    for (const p of missing) catalog.set(packageKey(p.packageId, p.packageRevision), p);
    this.patch({ catalog });
  }

  /** 包闭包里没有给出的包按引用补读一次（尽力而为）。 */
  private async inspectMissing(references: readonly EffectPackageReference[]): Promise<void> {
    for (const reference of references) {
      const key = referenceKey(reference);
      if (this.state.catalog.has(key) || this.inspecting.has(key)) continue;
      this.inspecting.add(key);
      try {
        this.remember([await this.client.effectPackages.inspect({ reference })]);
      } catch {
        // 读不到时包列表写“没有读到包信息”，保存仍交给后端判断。
      } finally {
        this.inspecting.delete(key);
      }
    }
  }

  private detailKey(libraryId: TemplateLibraryId, summary: TemplateSummary, libraryRevision = this.state[libraryId]?.revision ?? ''): string {
    return `${libraryId}\u0000${libraryRevision}\u0000${summary.templateId}\u0000${summary.templateRevision}`;
  }

  private async fail(prefix: string, error: unknown, conflictText = CONFLICT_TEXT): Promise<void> {
    if (error instanceof BackendError && error.status === 409) {
      this.patch({ notice: { level: 'warning', message: conflictText } });
      await this.reloadAfterChange();
      return;
    }
    this.patch({ notice: { level: 'error', message: `${prefix}：${libraryErrorText(error)}` } });
  }

  private patch(patch: LibraryPatch): void {
    const next = { ...this.state, ...patch } as Record<string, unknown>;
    for (const key of Object.keys(patch)) if (next[key] === undefined) delete next[key];
    this.state$.set(next as unknown as LibraryState);
  }
}

/** 个人库的模板才有草稿。 */
function draftFor(detail: TemplateFullDetailResponse): TemplateDraft | undefined {
  if (detail.libraryId !== 'personal') return undefined;
  return { templateId: detail.summary.templateId, base: detail.definition, definition: detail.definition, undo: [], redo: [], imported: [] };
}

/** 草稿各步、出发点和导入过的包里出现过的引用：重算 packages 时从这里取新用到的包的引用。 */
function knownReferences(draft: TemplateDraft): EffectPackageReference[] {
  return [...draft.imported, ...draft.base.packages, ...[...draft.undo, ...draft.redo].flatMap((d) => d.packages)];
}

/** 草稿当前定义和历史里的 library 引用都换成新详情里的。 */
function rebaseDraft(draft: TemplateDraft, detail: TemplateFullDetailResponse): TemplateDraft {
  const fresh = detail.definition.packages;
  const rebase = (d: FullTemplateDefinition) => rebasePackageReferences(d, fresh);
  return { ...draft, definition: rebase(draft.definition), undo: draft.undo.map(rebase), redo: draft.redo.map(rebase) };
}

function libraryErrorText(error: unknown): string {
  if (error instanceof BackendError && error.failureCode === 'LibraryInvalid') return '个人模板库格式不受支持或内容无效，后端拒绝读写；原库保持不变。';
  if (error instanceof BackendError && error.failureCode === 'InvalidDefinition') return INVALID_DEFINITION_TEXT;
  return errorText(error);
}

/** 规范化比较：对象键排序、属性列表按固定顺序。 */
export function sameDefinition(a: FullTemplateDefinition, b: FullTemplateDefinition): boolean {
  return canonicalJson(normalize(a)) === canonicalJson(normalize(b));
}

function normalize(definition: FullTemplateDefinition): FullTemplateDefinition {
  const order = (p: (typeof TEMPLATE_PROPERTIES)[number]) => TEMPLATE_PROPERTIES.indexOf(p);
  return { ...definition, properties: [...definition.properties].sort((x, y) => order(x) - order(y)) };
}

function canonicalJson(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(',')}]`;
  if (value && typeof value === 'object') {
    const entries = Object.entries(value as Record<string, unknown>).sort(([x], [y]) => (x < y ? -1 : x > y ? 1 : 0));
    return `{${entries.map(([k, v]) => `${JSON.stringify(k)}:${canonicalJson(v)}`).join(',')}}`;
  }
  return JSON.stringify(value);
}

/** 新名称的校验文案；合法时返回 undefined。 */
export function checkTemplateName(name: string): string | undefined {
  const trimmed = name.trim();
  if (!trimmed) return '名称不能为空。';
  if (trimmed.length > TEMPLATE_NAME_MAX_LENGTH) return `名称最多 ${TEMPLATE_NAME_MAX_LENGTH} 个字符。`;
  return undefined;
}

/** 列表筛选：名称包含搜索词（不区分大小写），用途为 all 时不限。 */
export function filterTemplates(templates: readonly TemplateSummary[], query: string, kind: TemplateKind | 'all'): TemplateSummary[] {
  const needle = query.trim().toLocaleLowerCase();
  return templates.filter((t) => (kind === 'all' || t.kind === kind) && (!needle || t.name.toLocaleLowerCase().includes(needle)));
}

/** 含效果的模板：有配方、包或包材质。 */
export function isAdvanced(summary: TemplateSummary): boolean {
  return summary.hasEffectRecipes || summary.hasPackageMaterials || summary.hasPackages;
}
