// 独立、本地的采用候选。它不调用配置 PUT，也不在 CAS 失败后自动重发。
import { BackendError, ObservableValue } from '../../client';
import type { AuthorDraftConfiguration, AuthorSourceInspectionResponse } from '../../protocol';
import { configurationIssues, editableOf, rejectionIssue, type ConfigIssue, type EditableConfiguration } from '../model/script-components';
import type { CommitDraft } from '../ui/commit-drafts';
import { errorText } from '../session/editor-session';
import type { FullDraft } from './author-draft-store';
import { sameConfiguration, snapshotConfiguration, type SourceResult } from './draft-source';

export interface DraftAdoptionCandidate {
  draftId: string;
  targetSourceRevision: string;
  packageId: string;
  identity: string;
  baseDraftRevision: string;
  baseInUseSourceRevision: string;
  before: AuthorDraftConfiguration;
  value: EditableConfiguration;
  inputDrafts: Record<string, CommitDraft>;
  issues: ConfigIssue[];
  status: 'loading' | 'ready' | 'stale' | 'failed' | 'submitting';
  requestGeneration: number;
  metadata?: AuthorSourceInspectionResponse;
  error?: string | undefined;
  rejected?: ConfigIssue | undefined;
}

export interface DraftAdoptionState {
  open: boolean;
  candidate?: DraftAdoptionCandidate;
  preparing: boolean;
  error?: string | undefined;
}

export interface DraftAdoptionPort {
  identity(): string;
  current(): FullDraft | undefined;
  settleConfiguration(): Promise<boolean>;
  refresh(): Promise<FullDraft>;
  inspect(draft: FullDraft, sourceRevision: string): Promise<AuthorSourceInspectionResponse>;
  compiled(sourceRevision: string): boolean;
  adopt(candidate: DraftAdoptionCandidate): Promise<SourceResult>;
}

export function adoptionIssues(value: EditableConfiguration, pkg: AuthorSourceInspectionResponse): ConfigIssue[] {
  const exp = pkg.exports.find((item) => item.id === value.exportId);
  const issues = configurationIssues(value, exp?.kind === 'material' ? 'Material' : 'Effect', pkg);
  if (!pkg.structureValid) issues.push({ message: '目标包结构无效，请先修正包文本并保存。' });
  if (exp && exp.kind !== 'material' && exp.kind !== 'controller' && exp.kind !== 'graph')
    issues.push({ field: 'exportId', message: '请选择目标包的效果或材质导出。' });
  if (exp?.kind !== 'material' && value.readScope !== 'branchInput' && value.branchInput !== null)
    issues.push({ field: 'branchInput', message: '当前读取范围不能保留输入名，请清空。' });
  // 候选不自动修正跨类别配置；让每个不兼容字段可见并由用户选择。
  if (exp?.kind === 'material') {
    if (value.relativeTarget !== 'application') issues.push({ field: 'relativeTarget', message: '材质的作用于须选择“整个分配”。' });
    if (value.readScope !== 'boundSources') issues.push({ field: 'readScope', message: '材质的读取范围须选择“绑定的来源”。' });
    if (value.branchInput !== null) issues.push({ field: 'branchInput', message: '材质不能保留指定输入名，请清空。' });
    if (value.fixedSeed !== null) issues.push({ field: 'fixedSeed', message: '材质不能保留固定随机种子，请清空。' });
    if (value.overlayLayer !== 'package') issues.push({ field: 'overlayLayer', message: '材质的叠放层须选择“随包”。' });
  }
  return issues;
}

export class DraftAdoptionModel {
  readonly state$ = new ObservableValue<DraftAdoptionState>({ open: false, preparing: false });
  private generation = 0;
  private disposed = false;
  private undoValues: EditableConfiguration[] = [];
  private redoValues: EditableConfiguration[] = [];
  constructor(private readonly port: DraftAdoptionPort) {}
  get state(): DraftAdoptionState { return this.state$.get(); }
  get hasUnsaved(): boolean { return !!this.state.candidate; }
  get canUndo(): boolean { return this.undoValues.length > 0; }
  get canRedo(): boolean { return this.redoValues.length > 0; }
  get hasInputDrafts(): boolean { return Object.keys(this.state.candidate?.inputDrafts ?? {}).length > 0; }

  setInputDraft(key: string, draft: CommitDraft | undefined): void {
    const candidate = this.state.candidate;
    if (!candidate || candidate.status === 'submitting' || this.disposed) return;
    const inputDrafts = { ...candidate.inputDrafts };
    if (draft) inputDrafts[key] = { ...draft }; else delete inputDrafts[key];
    this.update({ ...candidate, inputDrafts });
  }

  clearInputDrafts(prefix: string): void {
    const candidate = this.state.candidate;
    if (!candidate || candidate.status === 'submitting') return;
    this.update({ ...candidate, inputDrafts: Object.fromEntries(Object.entries(candidate.inputDrafts).filter(([key]) => !key.startsWith(prefix))) });
  }

  compiled(candidate = this.state.candidate): boolean {
    const pkg = candidate?.metadata;
    if (!candidate || !pkg) return false;
    const requiresCompilation = pkg.packageVersion === 3 && pkg.sdkVersion.split('.')[0] === '2'
      && pkg.files.some((file) => file.role === 'csharp');
    return !requiresCompilation || this.port.compiled(candidate.targetSourceRevision);
  }

  async prepare(targetSourceRevision: string): Promise<boolean> {
    if (this.disposed || this.state.preparing || this.state.candidate?.status === 'submitting') return false;
    const retained = this.state.candidate;
    if (retained?.targetSourceRevision === targetSourceRevision) {
      this.set({ ...this.state, open: true });
      const draft = this.port.current();
      if (draft) this.serverChanged(draft);
      return true;
    }
    this.set({ ...this.state, open: true, preparing: true, error: undefined });
    try {
      if (!(await this.port.settleConfiguration())) throw new Error('在用配置还有未提交或被拒绝的改动，请先修正。');
      const draft = this.port.current();
      if (!draft || this.disposed) throw new Error('草稿已关闭。');
      this.undoValues = []; this.redoValues = [];
      const candidate = this.create(draft, targetSourceRevision, editableOf(draft.configuration));
      this.set({ open: true, preparing: false, candidate });
      await this.load(candidate);
      return true;
    } catch (error) {
      this.set({ ...this.state, preparing: false, error: errorText(error) });
      return false;
    }
  }

  close(): void {
    if (this.state.candidate?.status !== 'submitting' && !this.state.preparing) this.set({ ...this.state, open: false });
  }

  change(value: EditableConfiguration): void {
    const candidate = this.state.candidate;
    if (!candidate || candidate.status === 'submitting' || this.disposed) return;
    if (JSON.stringify(candidate.value) === JSON.stringify(value)) return;
    this.undoValues.push({ ...candidate.value }); this.redoValues = [];
    this.setValue(value);
  }

  private setValue(value: EditableConfiguration): void {
    const candidate = this.state.candidate;
    if (!candidate) return;
    this.update({ ...candidate, value, status: candidate.status === 'failed' && candidate.metadata ? 'ready' : candidate.status, issues: candidate.metadata ? adoptionIssues(value, candidate.metadata) : [], rejected: undefined, error: undefined });
  }

  undo(): void {
    if (this.state.preparing || this.state.candidate?.status === 'submitting' || !this.state.candidate) return;
    if (this.hasInputDrafts) { this.update({ ...this.state.candidate, error: '输入框还有未提交原文；请先按 Enter 提交或按 Esc 撤回，再撤销候选配置。' }); return; }
    const value = this.undoValues.pop();
    if (value) { this.redoValues.push({ ...this.state.candidate.value }); this.setValue(value); }
  }

  redo(): void {
    if (this.state.preparing || this.state.candidate?.status === 'submitting' || !this.state.candidate) return;
    if (this.hasInputDrafts) { this.update({ ...this.state.candidate, error: '输入框还有未提交原文；请先按 Enter 提交或按 Esc 撤回，再重做候选配置。' }); return; }
    const value = this.redoValues.pop();
    if (value) { this.undoValues.push({ ...this.state.candidate.value }); this.setValue(value); }
  }

  serverChanged(draft: FullDraft): void {
    const candidate = this.state.candidate;
    if (!candidate || candidate.status === 'submitting') return;
    if (candidate.identity !== this.port.identity() || candidate.draftId !== draft.draftId
      || candidate.packageId !== draft.packageId || candidate.baseDraftRevision !== draft.draftRevision
      || !sameConfiguration(candidate.before, draft.configuration))
      this.update({ ...candidate, status: 'stale', error: '准备后草稿已变化；输入已保留，请重新读取并比较。' });
  }

  async retryMetadata(): Promise<void> {
    const candidate = this.state.candidate;
    if (candidate && candidate.status !== 'submitting' && candidate.status !== 'stale') await this.load(candidate);
  }

  /** 用户明确重读，只更换候选的比较基准；保留 value，仍需再次明确采用。 */
  async recompare(): Promise<void> {
    const previous = this.state.candidate;
    if (!previous || previous.status === 'submitting' || this.state.preparing) return;
    this.set({ ...this.state, preparing: true, error: undefined });
    try {
      await this.port.refresh();
      if (!(await this.port.settleConfiguration())) throw new Error('在用配置仍未提交，请先修正。');
      const draft = this.port.current();
      if (!draft || this.disposed || previous.identity !== this.port.identity() || previous.packageId !== draft.packageId)
        throw new Error('连接或包身份已变化，此候选不能转移到另一个身份。');
      const latest = this.state.candidate ?? previous;
      const candidate = { ...this.create(draft, previous.targetSourceRevision, latest.value), inputDrafts: { ...latest.inputDrafts } };
      this.set({ open: true, preparing: false, candidate });
      await this.load(candidate);
    } catch (error) {
      this.set({ ...this.state, preparing: false, error: errorText(error) });
    }
  }

  async submit(): Promise<SourceResult> {
    const candidate = this.state.candidate;
    if (!candidate || candidate.status === 'submitting' || this.state.preparing) return { kind: 'busy' };
    const draft = this.port.current();
    if (!draft || candidate.identity !== this.port.identity() || draft.draftRevision !== candidate.baseDraftRevision
      || draft.packageId !== candidate.packageId || !sameConfiguration(candidate.before, draft.configuration)) {
      this.update({ ...candidate, status: 'stale', error: '草稿已变化；输入已保留，请重新读取并比较。' });
      return { kind: 'failed', message: '候选的草稿基准已过期。' };
    }
    if (this.hasInputDrafts) return { kind: 'failed', message: '输入框还有未提交原文，请先按 Enter 提交或按 Esc 撤回。' };
    if (candidate.status !== 'ready' || candidate.rejected || candidate.issues.length || !candidate.metadata || !this.compiled(candidate))
      return { kind: 'failed', message: '请先读取精确 metadata、修正配置，并确认目标修订编译通过。' };
    this.update({ ...candidate, status: 'submitting', error: undefined, rejected: undefined });
    try {
      const result = await this.port.adopt(candidate);
      if (this.disposed) return result;
      if (result.kind === 'adopted') { this.undoValues = []; this.redoValues = []; this.set({ open: false, preparing: false }); }
      else this.update({ ...this.state.candidate!,
        status: result.kind === 'failed' && (result.conflict || result.uncertain) ? 'stale' : 'ready',
        error: result.kind === 'failed' ? result.message : '操作正在进行，请稍后重试。',
        ...(result.kind === 'failed' && result.rejected ? { rejected: result.rejected } : {}) });
      return result;
    } catch (error) {
      const stale = error instanceof BackendError && error.status === 409;
      const rejected = error instanceof BackendError ? rejectionIssue(error.field?.replace(/^configuration\./, ''), error.message) : undefined;
      this.update({ ...this.state.candidate!, status: stale ? 'stale' : 'failed', error: errorText(error), ...(rejected ? { rejected } : {}) });
      return { kind: 'failed', message: errorText(error) };
    }
  }

  dispose(): void { this.disposed = true; ++this.generation; }

  private create(draft: FullDraft, targetSourceRevision: string, value: EditableConfiguration): DraftAdoptionCandidate {
    return { draftId: draft.draftId, targetSourceRevision, packageId: draft.packageId, identity: this.port.identity(),
      baseDraftRevision: draft.draftRevision, baseInUseSourceRevision: draft.configuration.sourceRevision,
      before: snapshotConfiguration(draft.configuration), value: { ...value }, inputDrafts: {}, issues: [], status: 'loading', requestGeneration: ++this.generation };
  }

  private async load(original: DraftAdoptionCandidate): Promise<void> {
    const candidate = { ...original, status: 'loading' as const, requestGeneration: ++this.generation, error: undefined };
    this.update(candidate);
    const draft = this.port.current();
    if (!draft || draft.draftRevision !== candidate.baseDraftRevision) {
      this.update({ ...candidate, status: 'stale', error: '草稿已变化，请重新读取并比较。' });
      return;
    }
    try {
      const metadata = await this.port.inspect(draft, candidate.targetSourceRevision);
      const current = this.port.current();
      const local = this.state.candidate;
      if (this.disposed || !local || local.requestGeneration !== candidate.requestGeneration) return;
      if (!current || candidate.identity !== this.port.identity() || current.draftRevision !== candidate.baseDraftRevision
        || !sameConfiguration(candidate.before, current.configuration)) {
        this.update({ ...local, status: 'stale', error: '草稿已变化；输入已保留，请重新读取并比较。' });
        return;
      }
      if (metadata.draftId !== candidate.draftId || metadata.draftRevision !== candidate.baseDraftRevision
        || metadata.packageId !== candidate.packageId || metadata.sourceRevision !== candidate.targetSourceRevision
        || metadata.packageRevision !== candidate.targetSourceRevision)
        throw new Error('目标 metadata 身份不匹配，未用于配置。');
      this.update({ ...local, metadata, status: 'ready', issues: adoptionIssues(local.value, metadata), error: undefined });
    } catch (error) {
      const local = this.state.candidate;
      if (!this.disposed && local?.requestGeneration === candidate.requestGeneration)
        this.update({ ...local, status: error instanceof BackendError && error.status === 409 ? 'stale' : 'failed', error: errorText(error) });
    }
  }

  private update(candidate: DraftAdoptionCandidate): void { this.set({ ...this.state, candidate }); }
  private set(state: DraftAdoptionState): void {
    if (this.disposed) return;
    // Optional fields are omitted, preserving exactOptionalPropertyTypes without non-null sentinels.
    const next = { ...state };
    if (next.error === undefined) delete next.error;
    if (next.candidate) {
      const candidate = { ...next.candidate };
      if (candidate.error === undefined) delete candidate.error;
      if (candidate.rejected === undefined) delete candidate.rejected;
      next.candidate = candidate;
    }
    this.state$.set(next);
  }
}
