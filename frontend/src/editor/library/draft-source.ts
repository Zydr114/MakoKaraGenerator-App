// 作者草稿的源码页签（W8 设计 §2.6）：读某个修订的 C# 文件、编辑、保存修订、查看与改用修订、编译与诊断。不依赖 React 和 CodeMirror。
// 编辑器的状态（撤销历史、光标）由 SourceEditor 寄存在这里，切文件、切页签、切草稿都不丢；读进别的修订时清空。
// 后端没有列出草稿保留了哪些修订（G35）：本页保存过的修订记在 sessionStorage，按 client 和草稿区分。

import { BackendError, ObservableValue } from '../../client';
import type { AuthorCompilation, AuthorDraft, AuthorDraftSourcesResponse, AuthorSourceFile, AuthorSourceRevisionResponse, JobResponse, JobSummary } from '../../protocol';
import { errorText } from '../session/editor-session';
import type { FullDraft } from './author-draft-store';

export const DRAFT_REVISIONS_KEY = 'mako.author-draft-revisions';

export type RevisionLabel = 'base' | 'latest' | 'saved' | 'adopted' | 'inUse';
export const REVISION_LABEL: Record<RevisionLabel, string> = { base: '原包', latest: '最新', saved: '本页保存', adopted: '已采用', inUse: '在用' };

export interface RevisionRow {
  revision: string;
  labels: RevisionLabel[];
}

/**
 * 修订列表：最新在前，然后是本页保存的（新的在前）、在用和已采用的，原包在最后；同一修订合成一行。
 * “本页保存”只标在没有别的身份（原包、最新）的行上。
 */
export function revisionRows(
  draft: Pick<AuthorDraft, 'baseSourceRevision' | 'currentSourceRevision' | 'adoptedSourceRevision'> & { configuration: { sourceRevision: string } },
  saved: readonly string[],
): RevisionRow[] {
  const inUse = draft.configuration.sourceRevision;
  const adopted = draft.adoptedSourceRevision ?? undefined;
  const order = [draft.currentSourceRevision, ...[...saved].reverse(), inUse, ...(adopted ? [adopted] : []), draft.baseSourceRevision];
  return [...new Set(order)].map((revision) => {
    const labels: RevisionLabel[] = [];
    if (revision === draft.baseSourceRevision) labels.push('base');
    if (revision === draft.currentSourceRevision) labels.push('latest');
    if (labels.length === 0 && saved.includes(revision)) labels.push('saved');
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
  /** 读进来或最近一次保存时的内容。 */
  saved: string;
  /** 编辑器里的内容。 */
  text: string;
}

export interface DraftSourceState {
  /** none：包里没有 C# 源码（材质、图），整个页签只写原因（R10，不看 canCompile）。 */
  status: 'idle' | 'loading' | 'ready' | 'none' | 'failed';
  /** 编辑器里的文件属于哪个修订；保存后是新修订。 */
  revision?: string;
  files: SourceFile[];
  active?: string;
  busy?: 'loading' | 'saving' | 'adopting';
  /** 读取失败的原因。 */
  error?: string;
  /** 本页保存过的修订，旧的在前。 */
  saved: string[];
  /** 读进别的修订时加一：编辑器据此丢掉旧的撤销历史。 */
  generation: number;
  /** 最近一次编译的结果（本页编译的，或打开页签时读到的上一次）。编译的是当时的最新修订。 */
  compilation?: AuthorCompilation;
  /** 编译任务进行中。 */
  compiling?: { jobId?: string; canCancel: boolean; cancelling: boolean };
  compileError?: string;
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
export type SourceResult = { kind: 'saved'; revision: string; created: boolean } | { kind: 'unchanged' } | { kind: 'adopted'; revision: string } | { kind: 'failed'; message: string } | { kind: 'busy' };

export interface DraftSourcePort {
  current(): FullDraft | undefined;
  read(draft: FullDraft, revision: string): Promise<AuthorDraftSourcesResponse>;
  save(draft: FullDraft, files: AuthorSourceFile[]): Promise<AuthorSourceRevisionResponse>;
  adopt(draft: FullDraft, revision: string): Promise<unknown>;
  /** 只把在用修订换成 revision（PUT 配置，采用标记不变），新草稿写回列表。 */
  use(draft: FullDraft, revision: string): Promise<FullDraft>;
  /** 重读草稿并写回列表。 */
  refresh(): Promise<FullDraft>;
  /** 本页保存过的修订（sessionStorage）。 */
  savedRevisions(): string[];
  rememberSaved(revisions: string[]): void;
  /** 202：异步编译最新修订。 */
  compile(draft: FullDraft): Promise<JobResponse>;
  waitJob(jobId: string, onProgress: (job: JobResponse | JobSummary) => void, signal: AbortSignal): Promise<JobResponse>;
  cancelJob(jobId: string): Promise<unknown>;
  compilationResult(jobId: string): Promise<AuthorCompilation>;
  /** 上一次编译；没编译过时 undefined。 */
  lastCompilation(): Promise<AuthorCompilation | undefined>;
}

type StatePatch = { [K in keyof DraftSourceState]?: DraftSourceState[K] | undefined };

export class DraftSourceModel {
  readonly state$: ObservableValue<DraftSourceState>;
  /** SourceEditor 寄存的编辑器状态，按文件路径。 */
  private readonly views = new Map<string, unknown>();
  private readonly abort = new AbortController();
  private disposed = false;
  private lastRead = false;
  /** 跳转的序号，一直递增（跳过后 reveal 会清掉，序号不能从头再来）。 */
  private revealNonce = 0;

  constructor(private readonly port: DraftSourcePort) {
    this.state$ = new ObservableValue<DraftSourceState>({ status: 'idle', files: [], saved: port.savedRevisions(), generation: 0 });
  }

  get state(): DraftSourceState {
    return this.state$.get();
  }

  /** 编辑器里有没保存成修订的改动。 */
  get dirty(): boolean {
    return this.state.files.some((f) => f.text !== f.saved);
  }

  /** 把一个修订读进编辑器；不给时读在用修订。编辑器里的改动会丢掉，由调用方先确认。 */
  async load(revision?: string): Promise<boolean> {
    if (this.state.busy) return false;
    this.patch({ busy: 'loading', ...(this.state.status === 'ready' ? {} : { status: 'loading' as const }) });
    try {
      const response = await this.withFreshDraft((draft) => this.port.read(draft, revision ?? draft.configuration.sourceRevision));
      if (this.disposed) return false;
      const files = response.files.map((f) => ({ path: f.path, saved: f.text, text: f.text }));
      const active = files.some((f) => f.path === this.state.active) ? this.state.active : files[0]?.path;
      this.views.clear();
      this.patch({ status: 'ready', revision: response.sourceRevision, files, active, error: undefined, generation: this.state.generation + 1 });
      void this.readLastCompilation();
      return true;
    } catch (error) {
      // 在用修订没有 C# 文件：包里本来就没有（不能加文件，G34），以后也不会有。
      if (error instanceof BackendError && error.status === 404 && (this.state.status !== 'ready' || revision === undefined)) this.patch({ status: 'none', files: [], error: undefined });
      else if (this.state.status === 'ready') this.patch({ error: `读取修订失败：${errorText(error)}` });
      else this.patch({ status: 'failed', error: `读取源码失败：${errorText(error)}` });
      return false;
    } finally {
      this.patch({ busy: undefined });
    }
  }

  select(path: string): void {
    if (this.state.files.some((f) => f.path === path)) this.patch({ active: path });
  }

  /** 编辑器里的内容变了。 */
  edit(path: string, text: string): void {
    const files = this.state.files;
    const index = files.findIndex((f) => f.path === path);
    if (index < 0 || files[index]!.text === text) return;
    this.patch({ files: files.map((f, i) => (i === index ? { ...f, text } : f)) });
  }

  /**
   * 保存修订：一次提交全部 C# 文件（G34），然后让试样用编辑器里的这份。
   * 后端只在“在用修订原来就是最新修订”时让在用修订跟着换（§0）；在用的是别的修订（例如采用了原包）时，前端再提交一次配置把它换过来。
   * 内容和最新修订相同时后端不新建修订（no-op）。提交期间又改了的文件仍算没保存。
   */
  async save(): Promise<SourceResult> {
    if (this.state.busy) return { kind: 'busy' };
    if (this.state.status !== 'ready' || !this.dirty) return { kind: 'unchanged' };
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
      if (fresh && fresh.configuration.sourceRevision !== revision) await this.withFreshDraft((draft) => this.port.use(draft, revision));
      else if (!created) return { kind: 'unchanged' };
      return { kind: 'saved', revision, created };
    } catch (error) {
      return { kind: 'failed', message: `保存修订失败：${errorText(error)}` };
    } finally {
      this.patch({ busy: undefined });
    }
  }

  /** 改用一个修订：在用修订和采用标记都换成它，试样跟着变。编辑器里没有改动时跟着读进这个修订。 */
  async adopt(revision: string): Promise<SourceResult> {
    if (this.state.busy) return { kind: 'busy' };
    this.patch({ busy: 'adopting', error: undefined });
    try {
      await this.withFreshDraft((draft) => this.port.adopt(draft, revision));
      const fresh = await this.port.refresh();
      this.patch({ busy: undefined });
      if (this.state.status === 'ready' && !this.dirty && this.state.revision !== fresh.configuration.sourceRevision) await this.load();
      return { kind: 'adopted', revision: fresh.configuration.sourceRevision };
    } catch (error) {
      return { kind: 'failed', message: `改用修订失败：${errorText(error)}` };
    } finally {
      this.patch({ busy: undefined });
    }
  }

  /**
   * 编译最新修订（§0：编译只针对最新修订，不一定是试样在用的那份）。编辑器有改动时先保存，返回保存的结果供页面提示。
   * 编译不通过时任务照样成功，结果里 succeeded 为 false。
   */
  async compile(): Promise<SourceResult | undefined> {
    if (this.state.busy || this.state.compiling || this.state.status !== 'ready') return undefined;
    let saved: SourceResult | undefined;
    if (this.dirty) {
      saved = await this.save();
      if (saved.kind === 'failed' || saved.kind === 'busy') return saved;
    }
    this.patch({ compiling: { canCancel: false, cancelling: false }, compileError: undefined });
    try {
      const accepted = await this.withFreshDraft((draft) => this.port.compile(draft));
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
        this.patch({ compilation: await this.port.compilationResult(job.jobId) });
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
    if (!this.state.files.some((f) => f.path === location.path)) return false;
    this.patch({ active: location.path, reveal: { ...location, nonce: ++this.revealNonce } });
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
