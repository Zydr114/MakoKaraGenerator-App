// 作者草稿的源码页签：完整包文件编辑与旧 C# 源码接口兼容；修订、CAS、编译和显式采用。不依赖 React 和 CodeMirror。
// 编辑器的状态（撤销历史、光标）由 SourceEditor 寄存在这里，切文件、切页签、切草稿都不丢；读进别的修订时清空。
// 完整保留修订由 source-revisions 分页读出；本页保存过的修订另记在 sessionStorage，按 client 和草稿区分。

import type {
  AuthorPackageFileReplacementInspection,
  AuthorPackageFileReplacementInspectionInput,
  AuthorPackageFilesInput,
  AuthorPackageFilesResponse,
} from '../../protocol/author-package-files';
import { BackendError, ObservableValue } from '../../client';
import type { AuthorAdoptResponse, AuthorDraftConfiguration, AuthorPackageConfigurationInput, AuthorCompilation, AuthorDraft, AuthorDraftSourcesResponse, AuthorSourceFile, AuthorSourceRevisionResponse, JobResponse, JobSummary } from '../../protocol';
import { errorText } from '../session/editor-session';
import type { FullDraft } from './author-draft-store';
import { editableOf, rejectionIssue, type ConfigIssue } from '../model/script-components';

export const DRAFT_REVISIONS_KEY = 'mako.author-draft-revisions';

export type RevisionLabel = 'base' | 'latest' | 'saved' | 'retained' | 'compiled' | 'adopted' | 'inUse';
export const REVISION_LABEL: Record<RevisionLabel, string> = { base: '原包', latest: '最新', saved: '本页保存', retained: '历史', compiled: '编译通过', adopted: '已采用', inUse: '在用' };

export interface RevisionRow {
  revision: string;
  labels: RevisionLabel[];
}

/**
 * 修订列表：最新在前，然后是本页保存的（新的在前）、最近编译通过、在用和已采用的，再是其余保留的历史修订，原包在最后；同一修订合成一行。
 * “本页保存”“历史”只标在没有别的身份（原包、最新）的行上。
 */
export function revisionRows(
  draft: Pick<AuthorDraft, 'baseSourceRevision' | 'currentSourceRevision' | 'adoptedSourceRevision' | 'lastCompilation'> & { configuration: { sourceRevision: string } },
  saved: readonly string[],
  retained: readonly string[] = [],
): RevisionRow[] {
  const inUse = draft.configuration.sourceRevision;
  const adopted = draft.adoptedSourceRevision ?? undefined;
  const compiled = draft.lastCompilation?.succeeded ? draft.lastCompilation.sourceRevision : undefined;
  const order = [draft.currentSourceRevision, ...[...saved].reverse(), ...(compiled ? [compiled] : []), inUse, ...(adopted ? [adopted] : []),
    ...retained.filter((revision) => revision !== draft.baseSourceRevision), draft.baseSourceRevision];
  return [...new Set(order)].map((revision) => {
    const labels: RevisionLabel[] = [];
    if (revision === draft.baseSourceRevision) labels.push('base');
    if (revision === draft.currentSourceRevision) labels.push('latest');
    if (labels.length === 0 && saved.includes(revision)) labels.push('saved');
    else if (labels.length === 0 && retained.includes(revision)) labels.push('retained');
    if (draft.lastCompilation?.succeeded && revision === draft.lastCompilation.sourceRevision) labels.push('compiled');
    if (revision === adopted) labels.push('adopted');
    if (revision === inUse) labels.push('inUse');
    return { revision, labels };
  });
}

export function revisionLabelText(labels: readonly RevisionLabel[]): string {
  return labels.map((l) => REVISION_LABEL[l]).join(' · ');
}

export interface SourceFile {
  path: string;
  /** 旧 C# 接口省略 role/editable；包模式完全采用服务的 editable 判断。 */
  role?: string;
  editable?: boolean;
  sizeBytes?: number;
  sha256?: string;
  /** 读进来或最近一次保存时的内容。 */
  saved: string;
  /** 编辑器里的内容。 */
  text: string;
  /** 待保存的整份替换（图片、字体）；读进别的修订或保存成功后清掉。 */
  replacement?: PendingReplacement;
}

/** 已上传并通过只读检查的新文件；保存时按 path 提交 assetId。 */
export interface PendingReplacement {
  fileName: string;
  assetId: string;
  inspection: AuthorPackageFileReplacementInspection;
}

/** binary：图片、字体按字节整份替换；svg：读成文本放进编辑器，按文本保存。 */
export type ReplaceKind = 'binary' | 'svg';

export interface DraftSourceState {
  /** none：包里没有 C# 源码（材质、图），整个页签只写原因（R10，不看 canCompile）。 */
  status: 'idle' | 'loading' | 'ready' | 'none' | 'failed';
  /** 使用完整包接口；files 包含 manifest 与只读二进制描述。 */
  packageFiles?: boolean;
  /** 编辑器里的文件属于哪个修订；保存后是新修订。 */
  revision?: string;
  files: SourceFile[];
  active?: string;
  busy?: 'loading' | 'saving' | 'adopting';
  /** 读取失败的原因。 */
  error?: string;
  /** 正在上传、检查替换文件的路径。 */
  replacing?: string;
  /** 最近一次替换失败；换文件仍可查看。 */
  replaceError?: { path: string; message: string };
  /** 本页保存过的修订，旧的在前。 */
  saved: string[];
  /** 后端保留的全部源码修订（完整历史）；没读到时为空。 */
  retained: string[];
  /** 读取完整历史失败的原因；修订列表仍显示已知的修订。 */
  historyError?: string;
  /** 读进别的修订时加一：编辑器据此丢掉旧的撤销历史。 */
  generation: number;
  /** 最近一次编译的固定源码结果（本页编译的，或打开页签时读到的上一次）。 */
  compilation?: AuthorCompilation;
  /** 编译任务进行中。 */
  compiling?: { jobId?: string; canCancel: boolean; cancelling: boolean };
  compileError?: string;
  canUndoAdoption: boolean;
  canRedoAdoption: boolean;
  /** 让编辑器跳到某处（点诊断或验证问题）；nonce 每次加一，编辑器跳过后清掉。 */
  reveal?: SourceLocation & { nonce: number };
}

export interface SourceLocation {
  path: string;
  /** 从 1 开始。 */
  line: number;
  column: number;
}

/** saved.created：新建了修订；否则内容和最新修订相同，只是试样改用了它。 */
export type SourceResult = { kind: 'saved'; revision: string; created: boolean } | { kind: 'unchanged' } | { kind: 'adopted'; revision: string } | { kind: 'failed'; message: string; conflict?: boolean; uncertain?: boolean; rejected?: ConfigIssue } | { kind: 'busy' };

export interface AuthorAdoptionExpectation {
  draftId: string;
  packageId: string;
  sourceRevision: string;
  configuration: AuthorPackageConfigurationInput;
  draftRevision?: string;
}

export interface DraftSourcePort {
  current(): FullDraft | undefined;
  /** 固定包 manifest 的 SDK2 身份；省略保持 v1 行为。 */
  isV2?(): boolean;
  /** 在改变采用来源前先提交待写配置。 */
  settleConfiguration?(): Promise<boolean>;
  /** 配对提供时启用完整包编辑；未提供时保留旧 C# 源码流程。 */
  readPackage?(draft: FullDraft, revision: string): Promise<AuthorPackageFilesResponse>;
  savePackage?(draft: FullDraft, input: AuthorPackageFilesInput): Promise<AuthorDraft>;
  /** 与 inspectReplacement 配对提供时可整份替换图片、字体：上传后返回 assetId。 */
  uploadAsset?(file: Blob, fileName: string): Promise<string>;
  /** 只读检查替换；不改变草稿。 */
  inspectReplacement?(draft: FullDraft, input: AuthorPackageFileReplacementInspectionInput): Promise<AuthorPackageFileReplacementInspection>;
  read(draft: FullDraft, revision: string): Promise<AuthorDraftSourcesResponse>;
  save(draft: FullDraft, files: AuthorSourceFile[]): Promise<AuthorSourceRevisionResponse>;
  adopt(draft: FullDraft, revision: string, configuration?: AuthorPackageConfigurationInput): Promise<unknown>;
  lockConfiguration?(): () => void;
  adoptionBoundary?(draft: FullDraft): void;
  adoptionUncertain?(): void;
  /** 只把在用修订换成 revision（PUT 配置，采用标记不变），新草稿写回列表。 */
  use(draft: FullDraft, revision: string): Promise<FullDraft>;
  /** 重读草稿并写回列表。 */
  refresh(expectedAdoption?: AuthorAdoptionExpectation): Promise<FullDraft>;
  /** 后端保留的全部源码修订；不提供时只列已知身份。 */
  retainedRevisions?(draft: FullDraft): Promise<string[]>;
  /** 下载某个保留修订的完整 .mkfx。 */
  downloadPackage?(draft: FullDraft, revision: string): Promise<Blob>;
  /** 本页保存过的修订（sessionStorage）。 */
  savedRevisions(): string[];
  rememberSaved(revisions: string[]): void;
  /** 202：异步编译指定修订；省略时编译最新修订。 */
  compile(draft: FullDraft, sourceRevision?: string): Promise<JobResponse>;
  waitJob(jobId: string, onProgress: (job: JobResponse | JobSummary) => void, signal: AbortSignal): Promise<JobResponse>;
  cancelJob(jobId: string): Promise<unknown>;
  compilationResult(jobId: string): Promise<AuthorCompilation>;
  /** 上一次编译；没编译过时 undefined。 */
  lastCompilation(): Promise<AuthorCompilation | undefined>;
}

/** 每步采用都保留完整配置，parametersJson 原文不经过 JSON 数值解析。 */
export function snapshotConfiguration(value: AuthorDraftConfiguration): AuthorDraftConfiguration {
  return { ...value, bindingKinds: [...value.bindingKinds] };
}

export function sameConfiguration(a: AuthorDraftConfiguration, b: AuthorDraftConfiguration): boolean {
  return a.packageId === b.packageId && a.packageRevision === b.packageRevision && a.sourceRevision === b.sourceRevision
    && a.kind === b.kind && a.bindingKinds.length === b.bindingKinds.length && a.bindingKinds.every((kind, i) => kind === b.bindingKinds[i])
    && a.name === b.name && a.exportId === b.exportId && a.parametersJson === b.parametersJson
    && a.relativeTarget === b.relativeTarget && a.readScope === b.readScope && (a.branchInput ?? null) === (b.branchInput ?? null)
    && (a.fixedSeed ?? null) === (b.fixedSeed ?? null) && a.overlayLayer === b.overlayLayer;
}

type AdoptionChange = { before: AuthorDraftConfiguration; after: AuthorDraftConfiguration };
type StatePatch = { [K in keyof DraftSourceState]?: DraftSourceState[K] | undefined };

export class DraftSourceModel {
  readonly state$: ObservableValue<DraftSourceState>;
  /** SourceEditor 寄存的编辑器状态，按文件路径。 */
  private readonly views = new Map<string, unknown>();
  private readonly abort = new AbortController();
  private disposed = false;
  private lastRead = false;
  /** 包文件加载时观察的最新源码；409 后只能合并配置修订，不能覆盖别人保存的新源码。 */
  private packageBaseLatest: string | undefined;
  /** 跳转的序号，一直递增（跳过后 reveal 会清掉，序号不能从头再来）。 */
  private revealNonce = 0;
  private readonly undoAdoptions: AdoptionChange[] = [];
  private readonly redoAdoptions: AdoptionChange[] = [];
  private readonly compiledSources = new Set<string>();

  constructor(private readonly port: DraftSourcePort) {
    this.state$ = new ObservableValue<DraftSourceState>({ status: 'idle', files: [], saved: port.savedRevisions(), retained: [], generation: 0, canUndoAdoption: false, canRedoAdoption: false });
  }

  get state(): DraftSourceState {
    return this.state$.get();
  }

  /** 编辑器里有没保存成修订的改动（含待保存的替换）。 */
  get dirty(): boolean {
    return this.state.files.some((f) => (f.editable !== false && f.text !== f.saved) || !!f.replacement);
  }

  get isV2(): boolean { return this.port.isV2?.() ?? false; }

  /** 把一个修订读进编辑器；不给时读在用修订。编辑器里的改动会丢掉，由调用方先确认。 */
  async load(revision?: string): Promise<boolean> {
    if (this.state.busy) return false;
    this.patch({ busy: 'loading', ...(this.state.status === 'ready' ? {} : { status: 'loading' as const }) });
    try {
      if (this.port.readPackage && this.port.savePackage) {
        let latestAtRead: string | undefined;
        const response = await this.withFreshDraft((draft) => {
          latestAtRead = draft.currentSourceRevision;
          return this.port.readPackage!(draft, revision ?? draft.configuration.sourceRevision);
        });
        if (this.disposed) return false;
        const files = packageSourceFiles(response);
        const active = files.some((file) => file.path === this.state.active) ? this.state.active : files[0]?.path;
        this.packageBaseLatest = latestAtRead;
        this.views.clear();
        this.patch({ status: 'ready', packageFiles: true, revision: response.sourceRevision, files, active,
          error: undefined, replaceError: undefined, reveal: undefined, generation: this.state.generation + 1 });
        void this.readLastCompilation();
        void this.loadHistory();
        return true;
      }
      const response = await this.withFreshDraft((draft) => this.port.read(draft, revision ?? draft.configuration.sourceRevision));
      if (this.disposed) return false;
      const files = response.files.map((f) => ({ path: f.path, saved: f.text, text: f.text }));
      const active = files.some((f) => f.path === this.state.active) ? this.state.active : files[0]?.path;
      this.views.clear();
      this.patch({ status: 'ready', revision: response.sourceRevision, files, active, error: undefined, generation: this.state.generation + 1 });
      void this.readLastCompilation();
      void this.loadHistory();
      return true;
    } catch (error) {
      // 在用修订没有 C# 文件：包里本来就没有（不能加文件，G34），以后也不会有。
      if (!this.port.readPackage && error instanceof BackendError && error.status === 404 && (this.state.status !== 'ready' || revision === undefined)) this.patch({ status: 'none', files: [], error: undefined });
      else if (this.state.status === 'ready') this.patch({ error: `读取修订失败：${errorText(error)}` });
      else this.patch({ status: 'failed', error: `读取源码失败：${errorText(error)}` });
      return false;
    } finally {
      this.patch({ busy: undefined });
    }
  }

  /** 重读后端保留的完整修订历史；失败只记原因，不影响编辑。 */
  async loadHistory(): Promise<void> {
    const read = this.port.retainedRevisions;
    if (!read) return;
    try {
      const retained = await this.withFreshDraft((draft) => read.call(this.port, draft));
      if (!this.disposed) this.patch({ retained, historyError: undefined });
    } catch (error) {
      if (!this.disposed) this.patch({ historyError: `读取完整修订历史失败：${errorText(error)}` });
    }
  }

  /** 下载某个保留修订的完整包；不改变编辑器或草稿。 */
  async downloadPackage(revision: string): Promise<Blob> {
    const download = this.port.downloadPackage;
    if (!download) throw new Error('当前连接不提供效果包下载。');
    return this.withFreshDraft((draft) => download.call(this.port, draft, revision));
  }

  select(path: string): void {
    if (this.state.files.some((f) => f.path === path)) this.patch({ active: path });
  }

  /** 编辑器里的内容变了。 */
  edit(path: string, text: string): void {
    const files = this.state.files;
    const index = files.findIndex((f) => f.path === path);
    if (index < 0 || files[index]!.editable === false || files[index]!.text === text) return;
    this.patch({ files: files.map((f, i) => (i === index ? { ...f, text } : f)) });
  }

  /** 这个文件能不能在本页替换；只有包模式下的图片、字体（字节）和 SVG（文本）可以。 */
  replaceKind(file: Pick<SourceFile, 'role' | 'editable'>): ReplaceKind | undefined {
    if (!this.state.packageFiles) return undefined;
    if (file.editable !== false) return file.role === 'svg' ? 'svg' : undefined;
    return (file.role === 'image' || file.role === 'font') && this.port.uploadAsset && this.port.inspectReplacement ? 'binary' : undefined;
  }

  /**
   * 用一个本机文件替换包内文件。图片、字体先上传再按编辑器所看的修订只读检查，通过后记为待保存；
   * 格式不同、解码失败等由服务拒绝，尺寸、家族名变化只提醒。SVG 按严格 UTF-8 读成文本放进编辑器。
   */
  async replaceFile(path: string, file: Blob, fileName: string): Promise<boolean> {
    const target = this.state.files.find((candidate) => candidate.path === path);
    if (!target || this.state.busy || this.state.replacing || this.state.status !== 'ready') return false;
    const kind = this.replaceKind(target);
    if (kind === 'svg') return this.replaceSvgText(path, file);
    if (kind !== 'binary' || !this.state.revision) return false;
    const generation = this.state.generation;
    const sourceRevision = this.state.revision;
    const current = () => !this.disposed && this.state.generation === generation && this.state.revision === sourceRevision;
    this.patch({ replacing: path, replaceError: undefined });
    try {
      const assetId = await this.port.uploadAsset!(file, fileName);
      const inspection = await this.withFreshDraft((draft) => this.port.inspectReplacement!(draft, { sourceRevision, path, assetId }));
      if (!current()) return false;
      if (inspection.path !== path || inspection.sourceRevision !== sourceRevision) throw new Error('检查结果与要替换的文件不一致。');
      this.patch({ files: this.state.files.map((candidate) => (candidate.path === path ? { ...candidate, replacement: { fileName, assetId, inspection } } : candidate)) });
      return true;
    } catch (error) {
      if (current()) this.patch({ replaceError: { path, message: `替换 ${baseName(path)} 失败：${errorText(error)}` } });
      return false;
    } finally {
      this.patch({ replacing: undefined });
    }
  }

  /** 撤销一个待保存的替换。 */
  revertReplacement(path: string): void {
    if (this.state.busy === 'saving') return;
    const files = this.state.files;
    if (!files.some((file) => file.path === path && file.replacement)) return;
    this.patch({ files: files.map((file) => {
      if (file.path !== path) return file;
      const { replacement: _dropped, ...rest } = file;
      return rest;
    }), replaceError: undefined });
  }

  private async replaceSvgText(path: string, file: Blob): Promise<boolean> {
    this.patch({ replaceError: undefined });
    let text: string;
    try {
      text = new TextDecoder('utf-8', { fatal: true }).decode(await file.arrayBuffer());
    } catch {
      this.patch({ replaceError: { path, message: `替换 ${baseName(path)} 失败：文件不是 UTF-8 文本。` } });
      return false;
    }
    if (!/<svg[\s>]/i.test(text)) {
      this.patch({ replaceError: { path, message: `替换 ${baseName(path)} 失败：文件里没有 <svg> 元素。` } });
      return false;
    }
    this.edit(path, text);
    return true;
  }

  /**
   * 保存修订：一次提交全部 C# 文件（G34）；SDK2 只推进源码，编译后明确采用才改变试样来源。
   * v1 后端在“在用修订原来就是最新修订”时跟随更新，否则前端再提交一次配置。
   * 内容和最新修订相同时后端不新建修订（no-op）。提交期间又改了的文件仍算没保存。
   */
  async save(): Promise<SourceResult> {
    if (this.state.busy || this.state.replacing) return { kind: 'busy' };
    if (this.state.status !== 'ready' || !this.dirty) return { kind: 'unchanged' };
    if (this.state.packageFiles) return this.savePackageRevision();
    const submitted = this.state.files.map((f) => ({ path: f.path, text: f.text }));
    this.patch({ busy: 'saving', error: undefined });
    try {
      let before = '';
      const response = await this.withFreshDraft((draft) => {
        before = draft.draftRevision;
        return this.port.save(draft, submitted);
      });
      if (this.disposed) return { kind: 'failed', message: '草稿已关闭。' };
      const files = this.state.files.map((f) => {
        const sent = submitted.find((s) => s.path === f.path);
        return sent ? { ...f, saved: sent.text } : f;
      });
      const revision = response.sourceRevision;
      this.patch({ files, revision });
      const created = response.draftRevision !== before;
      if (created) {
        const saved = [...this.state.saved.filter((r) => r !== revision), revision];
        this.patch({ saved });
        this.port.rememberSaved(saved);
      }
      const fresh = created ? await this.port.refresh() : this.port.current();
      if (!this.isV2 && fresh && fresh.configuration.sourceRevision !== revision) await this.withFreshDraft((draft) => this.port.use(draft, revision));
      else if (!created) return { kind: 'unchanged' };
      return { kind: 'saved', revision, created };
    } catch (error) {
      return { kind: 'failed', message: `保存修订失败：${errorText(error)}` };
    } finally {
      this.patch({ busy: undefined });
    }
  }

  /**
   * 保存完整文本集合与待保存的替换；内容冲突保留本地缓冲，只有配置 CAS 漂移可重试。
   * 不可编辑文件取自编辑器所看的修订（baseSourceRevision），清单里的摘要由服务按替换后的字节重算。
   */
  private async savePackageRevision(): Promise<SourceResult> {
    const submitted = this.state.files.filter((file) => file.editable !== false).map((file) => ({ path: file.path, text: file.text }));
    const manifest = submitted.find((file) => file.path === 'manifest.json');
    if (!manifest || !this.port.savePackage || !this.port.readPackage || !this.state.revision) return { kind: 'failed', message: '完整包编辑入口不可用。' };
    const replacedFiles = this.state.files.flatMap((file) => (file.replacement ? [{ path: file.path, assetId: file.replacement.assetId }] : []));
    const input: AuthorPackageFilesInput = { manifestJson: manifest.text, textFiles: submitted.filter((file) => file.path !== 'manifest.json'),
      baseSourceRevision: this.state.revision, ...(replacedFiles.length > 0 ? { replacedFiles } : {}) };
    this.patch({ busy: 'saving', error: undefined });
    try {
      if (this.port.settleConfiguration && !(await this.port.settleConfiguration()))
        throw new Error('配置还有未提交或被拒绝的改动，请先修正。');
      let before = '';
      const response = await this.withFreshDraft((draft) => {
        if (draft.currentSourceRevision !== this.packageBaseLatest)
          throw new Error('草稿最新源码已由其他操作改变，本地文本已保留；请先比较或重新读取修订。');
        before = draft.draftRevision;
        return this.port.savePackage!(draft, input);
      });
      if (this.disposed) return { kind: 'failed', message: '草稿已关闭。' };
      const revision = response.currentSourceRevision;
      const created = response.draftRevision !== before;
      this.packageBaseLatest = revision;
      this.patch({ revision, files: this.state.files.map((file) => {
        const sent = submitted.find((candidate) => candidate.path === file.path);
        return sent ? { ...file, saved: sent.text } : file;
      }) });
      if (created) {
        const saved = [...this.state.saved.filter((candidate) => candidate !== revision), revision];
        this.patch({ saved });
        this.port.rememberSaved(saved);
      }
      try {
        await this.port.refresh();
        const canonical = await this.withFreshDraft((draft) => this.port.readPackage!(draft, revision));
        if (this.disposed) return { kind: 'failed', message: '草稿已关闭。' };
        // 服务派生 manifest SHA；未继续编辑的文本换成固定源原文，保存期间的新输入完整保留；替换已写进新修订。
        const files = packageSourceFiles(canonical).map((file) => {
          const local = this.state.files.find((candidate) => candidate.path === file.path);
          const sent = submitted.find((candidate) => candidate.path === file.path);
          return local && sent && local.text !== sent.text ? { ...file, text: local.text } : file;
        });
        this.patch({ files });
      } catch (error) {
        this.patch({ error: `修订已保存，读取固定包内容失败：${errorText(error)}` });
      }
      return created ? { kind: 'saved', revision, created } : { kind: 'unchanged' };
    } catch (error) {
      const message = `保存包修订失败：${errorText(error)}`;
      this.patch({ error: message });
      return { kind: 'failed', message };
    } finally {
      this.patch({ busy: undefined });
    }
  }

  /** 不确定的历史命令经用户重读后不猜测是否生效，清空本地采用历史。 */
  clearAdoptionHistory(): void {
    this.undoAdoptions.length = 0; this.redoAdoptions.length = 0;
    this.patch({ canUndoAdoption: false, canRedoAdoption: false });
  }

  /** 已知成功仅来自真实编译响应；后端仍核对 retained 的成功记录。 */
  hasSuccessfulCompilation(revision: string): boolean {
    const last = this.port.current()?.lastCompilation;
    return this.compiledSources.has(revision) || (!!last && last.sourceRevision === revision && last.succeeded);
  }

  async adopt(revision: string): Promise<SourceResult> {
    return this.changeAdoption(revision, 'adopt');
  }

  /** 候选只用准备时的 exact CAS，不 flush、刷新或自动重发。 */
  async adoptCandidate(revision: string, value: AuthorPackageConfigurationInput, expectedDraftRevision: string, expected: AuthorDraftConfiguration): Promise<SourceResult> {
    return this.changeAdoption(revision, 'adopt', expected, value, expectedDraftRevision);
  }

  async undoAdoption(): Promise<SourceResult> {
    const change = this.undoAdoptions.at(-1);
    return change ? this.changeAdoption(change.before.sourceRevision, 'undo', change.after, editableOf(change.before)) : { kind: 'unchanged' };
  }

  async redoAdoption(): Promise<SourceResult> {
    const change = this.redoAdoptions.at(-1);
    return change ? this.changeAdoption(change.after.sourceRevision, 'redo', change.before, editableOf(change.after)) : { kind: 'unchanged' };
  }

  private async changeAdoption(revision: string, action: 'adopt' | 'undo' | 'redo', expected?: AuthorDraftConfiguration,
    configuration?: AuthorPackageConfigurationInput, expectedDraftRevision?: string): Promise<SourceResult> {
    if (this.state.busy) return { kind: 'busy' };
    this.patch({ busy: 'adopting', error: undefined });
    let release: (() => void) | undefined;
    let posted = false;
    let confirmed = false;
    try {
      if (!expectedDraftRevision && this.port.settleConfiguration && !(await this.port.settleConfiguration()))
        throw new Error('配置还有未提交或被拒绝的改动，请先修正。');
      const draft = this.port.current();
      if (!draft || this.disposed) throw new Error('草稿已关闭。');
      if (expectedDraftRevision && draft.draftRevision !== expectedDraftRevision)
        throw new Error('草稿已变化，候选输入已保留；请重新读取并比较。');
      const before = snapshotConfiguration(draft.configuration);
      if (expected && !sameConfiguration(before, expected))
        throw new Error('当前完整配置已改变，本次操作不会覆盖后来的改动。');
      if (before.sourceRevision === revision && !configuration) return { kind: 'unchanged' };
      release = this.port.lockConfiguration?.();
      // 专用原子采用从不经 withFreshDraft。409 交回用户，保持所有候选与文本。
      posted = true;
      const receipt = await this.port.adopt(draft, revision, configuration);
      if (this.disposed) throw new Error('草稿已关闭。');
      const adopted = receipt as Partial<AuthorAdoptResponse> | undefined;
      if (adopted?.draftId !== undefined && (adopted.draftId !== draft.draftId || adopted.packageId !== draft.packageId || adopted.adoptedSourceRevision !== revision))
        throw new Error('采用回执身份不匹配，请重新读取草稿确认。');
      const target = configuration ?? editableOf(before);
      const fresh = await this.port.refresh({ draftId: draft.draftId, packageId: draft.packageId, sourceRevision: revision,
        configuration: target, ...(adopted?.draftRevision ? { draftRevision: adopted.draftRevision } : {}) });
      if (this.disposed || fresh.draftId !== draft.draftId || fresh.packageId !== draft.packageId
        || fresh.configuration.sourceRevision !== revision || fresh.configuration.packageRevision !== revision || fresh.configuration.packageId !== draft.packageId
        || (adopted?.draftRevision !== undefined && fresh.draftRevision !== adopted.draftRevision)
        || !sameEditableExact(editableOf(fresh.configuration), target))
        throw new Error('采用后的草稿与目标完整配置不一致，请重新读取并比较。');
      confirmed = true;
      const after = snapshotConfiguration(fresh.configuration);
      this.port.adoptionBoundary?.(fresh);
      if (this.isV2) {
        if (action === 'undo') this.redoAdoptions.push(this.undoAdoptions.pop()!);
        else if (action === 'redo') this.undoAdoptions.push(this.redoAdoptions.pop()!);
        else { this.undoAdoptions.push({ before, after }); this.redoAdoptions.length = 0; }
        this.patch({ canUndoAdoption: this.undoAdoptions.length > 0, canRedoAdoption: this.redoAdoptions.length > 0 });
      }
      // 保留显示的文件、cursor、undo 和 dirty；切到在用源仍由用户明确查看。
      return { kind: 'adopted', revision };
    } catch (error) {
      // 明确的后端拒绝没有发布；网络或回执/重读失败可能已发布，普通配置先冻结。
      if (posted && !confirmed && !(error instanceof BackendError && (error.status === 400 || error.status === 403 || error.status === 404 || error.status === 409 || error.status === 422)))
        this.port.adoptionUncertain?.();
      const message = `${action === 'undo' ? '撤销采用' : action === 'redo' ? '重做采用' : '采用修订'}失败：${errorText(error)}`;
      this.patch({ error: message });
      return { kind: 'failed', message,
        ...(error instanceof BackendError && error.status === 409 ? { conflict: true } : {}),
        ...(error instanceof BackendError ? { rejected: rejectionIssue(error.field?.replace(/^configuration\./, ''), error.message) } : {}),
        ...(posted && !confirmed && !(error instanceof BackendError && [400, 403, 404, 409, 422].includes(error.status)) ? { uncertain: true } : {}) };
    } finally {
      release?.();
      this.patch({ busy: undefined });
    }
  }

  /**
   * SDK2 编译编辑器显示的固定修订，v1 编译最新修订。编辑器有改动时先保存。
   * 编译不通过时任务照样成功，结果里 succeeded 为 false。
   */
  async compile(): Promise<SourceResult | undefined> {
    if (this.state.busy || this.state.compiling || this.state.status !== 'ready') return undefined;
    if (this.state.packageFiles && !this.port.current()?.canCompile) {
      this.patch({ compileError: '这个固定包没有可编译的 C# 入口；包资源仍可编辑和保存。' });
      return undefined;
    }
    let saved: SourceResult | undefined;
    if (this.dirty) {
      saved = await this.save();
      if (saved.kind === 'failed' || saved.kind === 'busy') return saved;
    }
    const sourceRevision = this.isV2 || this.state.packageFiles ? this.state.revision : undefined;
    this.patch({ compiling: { canCancel: false, cancelling: false }, compileError: undefined });
    try {
      const accepted = await this.withFreshDraft((draft) => this.port.compile(draft, sourceRevision));
      this.patch({ compiling: { jobId: accepted.jobId, canCancel: accepted.canCancel, cancelling: false } });
      const job = await this.port.waitJob(
        accepted.jobId,
        (current) => {
          const compiling = this.state.compiling;
          if (compiling?.jobId === accepted.jobId) this.patch({ compiling: { ...compiling, canCancel: current.canCancel } });
        },
        this.abort.signal,
      );
      if (job.state === 'succeeded' && job.authorCompilationResult) {
        const result = await this.port.compilationResult(job.jobId);
        if (result.succeeded) this.compiledSources.add(result.sourceRevision);
        this.patch({ compilation: result });
        // 草稿的编译摘要（lastCompilation）跟着更新。
        await this.port.refresh().catch(() => undefined);
      } else this.patch({ compileError: job.state === 'cancelled' ? '已取消编译。' : `编译没有完成：${job.error?.message ?? job.state}` });
    } catch (error) {
      if (!this.disposed) this.patch({ compileError: `编译失败：${errorText(error)}` });
    } finally {
      this.patch({ compiling: undefined });
    }
    return saved;
  }

  async cancelCompile(): Promise<void> {
    const compiling = this.state.compiling;
    if (!compiling?.jobId || !compiling.canCancel || compiling.cancelling) return;
    this.patch({ compiling: { ...compiling, cancelling: true } });
    try {
      await this.port.cancelJob(compiling.jobId);
    } catch (error) {
      const current = this.state.compiling;
      if (current) this.patch({ compiling: { ...current, cancelling: false }, compileError: `取消没有成功：${errorText(error)}` });
    }
  }

  /**
   * 跳到某个修订的某个位置：编辑器里不是这个修订时先读进来（编辑器里的改动会丢掉，由调用方先确认）。
   * 不给位置时只读进修订（运行时异常没有行号）。修订里没有这个文件时返回 false。
   */
  async goTo(revision: string, location?: SourceLocation): Promise<boolean> {
    if (this.state.revision !== revision && !(await this.load(revision))) return false;
    if (!location) return true;
    const file = this.state.files.find((candidate) => candidate.path === location.path)
      ?? [...this.state.files].sort((a, b) => b.path.length - a.path.length)
        .find((candidate) => location.path.startsWith(candidate.path + '.'));
    if (!file) return false;
    this.patch({ active: file.path, reveal: file.editable !== false && location.line > 0 && location.column > 0
      ? { ...location, path: file.path, nonce: ++this.revealNonce } : undefined });
    return true;
  }

  /** 编辑器已经跳过去了：之后切回页签不再跳。 */
  revealed(nonce: number): void {
    if (this.state.reveal?.nonce === nonce) this.patch({ reveal: undefined });
  }

  /** SourceEditor 寄存的编辑器状态。 */
  viewState(path: string): unknown {
    return this.views.get(path);
  }

  keepViewState(path: string, state: unknown): void {
    if (!this.disposed) this.views.set(path, state);
  }

  dispose(): void {
    this.disposed = true;
    this.abort.abort();
    this.views.clear();
  }

  /** 第一次读到源码时，草稿编译过就把上一次的结果读来。 */
  private async readLastCompilation(): Promise<void> {
    if (this.lastRead || this.state.compilation || !this.port.current()?.lastCompilation) return;
    this.lastRead = true;
    try {
      const last = await this.port.lastCompilation();
      if (last?.succeeded) this.compiledSources.add(last.sourceRevision);
      if (last && !this.state.compilation) this.patch({ compilation: last });
    } catch {
      // 读不到就当没编译过，再编译一次即可。
    }
  }

  private withFreshDraft<T>(send: (draft: FullDraft) => Promise<T>): Promise<T> {
    return withFreshDraft(this.port, send);
  }

  /** 值为 undefined 的键从状态中删除。 */
  private patch(patch: StatePatch): void {
    if (this.disposed) return;
    const next: Record<string, unknown> = { ...this.state };
    for (const [key, value] of Object.entries(patch)) {
      if (value === undefined) delete next[key];
      else next[key] = value;
    }
    this.state$.set(next as unknown as DraftSourceState);
  }
}

/** 用列表里的草稿修订发请求；草稿修订过期（409，例如配置页签刚提交过）时重读一次再发。 */
export async function withFreshDraft<T>(port: { current(): FullDraft | undefined; refresh(): Promise<FullDraft> }, send: (draft: FullDraft) => Promise<T>): Promise<T> {
  const draft = port.current();
  if (!draft) throw new Error('草稿已关闭。');
  try {
    return await send(draft);
  } catch (error) {
    if (!(error instanceof BackendError && error.status === 409)) throw error;
    return send(await port.refresh());
  }
}

/** 本页保存过的修订：{ clientId, drafts: { 草稿: 修订[] } }。 */
export function readSavedRevisions(clientId: string | undefined, draftId: string): string[] {
  try {
    const stored = JSON.parse(sessionStorage.getItem(DRAFT_REVISIONS_KEY) ?? 'null') as { clientId?: string; drafts?: Record<string, string[]> } | null;
    const list = stored && stored.clientId === clientId ? stored.drafts?.[draftId] : undefined;
    return Array.isArray(list) ? list.filter((r): r is string => typeof r === 'string') : [];
  } catch {
    return [];
  }
}

/** 写入一个草稿的修订；只保留 keep 里的草稿（关掉的草稿顺带清掉）。 */
export function writeSavedRevisions(clientId: string | undefined, draftId: string, revisions: string[], keep: readonly string[]): void {
  try {
    if (!clientId) return;
    const stored = JSON.parse(sessionStorage.getItem(DRAFT_REVISIONS_KEY) ?? 'null') as { clientId?: string; drafts?: Record<string, string[]> } | null;
    const drafts: Record<string, string[]> = {};
    if (stored?.clientId === clientId && stored.drafts) for (const id of keep) if (stored.drafts[id]) drafts[id] = stored.drafts[id];
    drafts[draftId] = revisions;
    sessionStorage.setItem(DRAFT_REVISIONS_KEY, JSON.stringify({ clientId, drafts }));
  } catch {
    // 存不下时刷新后只剩原包、最新、已采用、在用四个。
  }
}

/** manifest 是独立文本项；二进制只保留元数据，不能借编辑 API 变更其原始字节。 */
function packageSourceFiles(response: AuthorPackageFilesResponse): SourceFile[] {
  const files: SourceFile[] = [{ path: 'manifest.json', role: 'manifest', editable: true, saved: response.manifestJson, text: response.manifestJson }];
  for (const file of response.files) {
    if (file.editable && typeof file.text !== 'string') throw new Error(`后端没有返回可编辑文件 ${file.path} 的文本。`);
    files.push({ path: file.path, role: file.role, editable: file.editable, sizeBytes: file.sizeBytes, sha256: file.sha256,
      saved: file.editable ? file.text! : '', text: file.editable ? file.text! : '' });
  }
  return files;
}

/** 包内路径的文件名部分。 */
export function baseName(path: string): string {
  return path.slice(path.lastIndexOf('/') + 1);
}

export type SourceLanguage = 'csharp' | 'shader' | 'json' | 'xml' | 'plain';
/** 按服务 role 选择高亮；未知可编辑文本采用纯文本，不猜测执行能力。 */
export function sourceLanguage(file: Pick<SourceFile, 'role'>): SourceLanguage {
  switch (file.role) {
    case undefined: case 'csharp': return 'csharp';
    case 'shader': return 'shader';
    case 'manifest': case 'shaderProgram': case 'graph': case 'svgResource': return 'json';
    case 'svg': return 'xml';
    default: return 'plain';
  }
}

/** 同一次提交后的完整字段核对；数值 JSON 原文必须逐字保留。 */
export function sameEditableExact(a: AuthorPackageConfigurationInput, b: AuthorPackageConfigurationInput): boolean {
  return a.name === b.name && a.exportId === b.exportId && a.parametersJson === b.parametersJson
    && a.relativeTarget === b.relativeTarget && a.readScope === b.readScope && a.branchInput === b.branchInput
    && a.fixedSeed === b.fixedSeed && a.overlayLayer === b.overlayLayer;
}
