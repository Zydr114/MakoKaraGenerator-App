// 效果组件库状态（W8 设计 §2.3）：组件库修订、摘要列表、详情缓存、左栏的选中项（组件或草稿），和库命令（导入、改名、复制、删除）。
// 不依赖 React；页面通过 state$ 订阅。库内容以后端为准：命令成功后重读列表，不在本地改写摘要。
// 组件库属于后端 profile，Server 重启和别的标签页都看得到。

import { BackendError, ObservableValue, type MakoClient } from '../../client';
import type {
  EffectPackageExport,
  EffectPackageInspectResponse,
  ScriptConfigurationCommand,
  ScriptConfigurationCommandResponse,
  ScriptConfigurationDetailResponse,
  ScriptConfigurationSummary,
} from '../../protocol';
import { importedConfiguration } from '../model/script-components';
import { errorText } from '../session/editor-session';
import type { LibraryNotice } from './template-library-store';

/** 后端开放组件库读取的能力名。 */
export const COMPONENT_LIBRARY_FEATURE = 'script-configurations.read';

/** 左栏选中的：组件库里的组件，或本标签页的作者草稿。 */
export type ComponentSelection = { kind: 'component'; configurationId: string } | { kind: 'draft'; draftId: string };

export interface ComponentState {
  /** idle：还没读过（页签没打开过）。 */
  status: 'idle' | 'loading' | 'ready' | 'failed';
  /** 组件库修订：还没有内容时是 missing。 */
  revision?: string;
  configurations?: ScriptConfigurationSummary[];
  selection: ComponentSelection | null;
  /** 选中组件的详情（与 revision 一致时才算新的）。 */
  detail?: ScriptConfigurationDetailResponse;
  detailLoading: boolean;
  /** 库命令进行中。 */
  busy: boolean;
  notice?: LibraryNotice;
}

type ComponentPatch = { [K in keyof ComponentState]?: ComponentState[K] | undefined };

const CONFLICT_TEXT = '组件库已被修改，已重新读取，请检查后重试。';

/** 选中的组件 ID；没选或选的是草稿时 undefined。 */
export function selectedComponent(state: ComponentState): string | undefined {
  return state.selection?.kind === 'component' ? state.selection.configurationId : undefined;
}

export class ComponentLibraryStore {
  readonly state$ = new ObservableValue<ComponentState>({ status: 'idle', selection: null, detailLoading: false, busy: false });
  /** 每次改选择加一；晚到的旧详情不覆盖新选择。 */
  private selectSeq = 0;
  private loading: Promise<void> | undefined;
  /** 详情缓存，键含库修订（详情里的包引用钉在库修订上）。 */
  private readonly details = new Map<string, Promise<ScriptConfigurationDetailResponse>>();

  constructor(private readonly client: MakoClient) {}

  get state(): ComponentState {
    return this.state$.get();
  }

  summary(configurationId: string): ScriptConfigurationSummary | undefined {
    return this.state.configurations?.find((c) => c.configurationId === configurationId);
  }

  /** 读取列表；已在读取时复用同一次。选中的组件消失时清空选择，库修订变了就重读详情。 */
  load(): Promise<void> {
    this.loading ??= this.reload().finally(() => (this.loading = undefined));
    return this.loading;
  }

  /** 命令之后重读：等进行中的读取（可能早于命令）结束，再读一次。 */
  private async reloadAfterChange(): Promise<void> {
    await this.loading;
    await this.load();
  }

  async select(configurationId: string | null): Promise<void> {
    const seq = ++this.selectSeq;
    if (configurationId === null || !this.summary(configurationId)) {
      this.patch({ selection: null, detail: undefined, detailLoading: false });
      return;
    }
    const cached = this.state.detail?.configuration.configurationId === configurationId && this.state.detail.revision === this.state.revision;
    this.patch({ selection: { kind: 'component', configurationId }, detail: cached ? this.state.detail : undefined, detailLoading: !cached });
    if (!cached) await this.fetchSelected(seq, configurationId);
  }

  selectDraft(draftId: string): void {
    this.selectSeq++;
    this.patch({ selection: { kind: 'draft', draftId }, detailLoading: false });
  }

  /** 按当前库修订读一个组件的详情（缓存）；组件已不在库里时 undefined。 */
  async detailOf(configurationId: string): Promise<ScriptConfigurationDetailResponse | undefined> {
    const revision = this.state.revision;
    if (!revision || !this.summary(configurationId)) return undefined;
    const key = `${revision}\u0000${configurationId}`;
    let pending = this.details.get(key);
    if (!pending) {
      pending = this.client.scriptConfigurations.detail(configurationId, revision);
      this.details.set(key, pending);
      pending.catch(() => this.details.delete(key));
    }
    return pending;
  }

  /** 上传 .mkfx 并检查（只读）。失败时抛错，由调用方显示。 */
  async inspectPackageFile(file: File): Promise<EffectPackageInspectResponse> {
    const asset = await this.client.assets.upload(file, { fileName: file.name });
    return this.client.effectPackages.inspect({ packageAssetId: asset.assetId });
  }

  /**
   * 导入：每个导出新建一个组件（§2.3），依次提交，用上一条回执的修订接下一条。成功后选中第一个新组件。
   * 中途失败时已建的保留（每条命令各自生效），返回已建的个数。
   */
  async importPackage(pkg: EffectPackageInspectResponse, exports: readonly EffectPackageExport[]): Promise<number> {
    if (this.state.busy || exports.length === 0) return 0;
    if (!this.state.revision) await this.load();
    let revision = this.state.revision;
    if (!revision) return 0;
    this.patch({ busy: true, notice: undefined });
    const created: string[] = [];
    try {
      for (const exp of exports) {
        const response = await this.client.scriptConfigurations.command(revision, { type: 'create', configuration: importedConfiguration(pkg, exp) });
        revision = response.revision;
        created.push(response.configurationId);
      }
    } catch (error) {
      const done = created.length > 0 ? `（已导入 ${created.length} 个）` : '';
      await this.fail(`导入失败${done}`, error);
    } finally {
      this.patch({ busy: false });
    }
    if (created.length > 0) {
      await this.reloadAfterChange();
      await this.select(created[0]!);
    }
    return created.length;
  }

  /** 复制（允许重名），成功后选中新组件。 */
  async copy(configurationId: string, name: string): Promise<boolean> {
    const response = await this.command({ type: 'copy', configurationId, name: name.trim() }, '复制失败');
    if (!response) return false;
    await this.select(response.configurationId);
    return true;
  }

  async rename(configurationId: string, name: string): Promise<boolean> {
    return !!(await this.command({ type: 'rename', configurationId, name: name.trim() }, '改名失败'));
  }

  async remove(configurationId: string): Promise<boolean> {
    return !!(await this.command({ type: 'delete', configurationId }, '删除失败'));
  }

  clearNotice(): void {
    if (this.state.notice) this.patch({ notice: undefined });
  }

  /** Server 重启后组件库还在（属于 profile），但读到的修订和详情要重读；选中的草稿已经找不回来。 */
  reset(): void {
    this.selectSeq++;
    this.details.clear();
    this.patch({ detail: undefined, detailLoading: false, ...(this.state.selection?.kind === 'draft' ? { selection: null } : {}) });
    if (this.state.status !== 'idle') void this.load();
  }

  /**
   * 库命令：带读到的库修订做 CAS。409 时重读；retryOnConflict 时用新修订再试一次（只用于整份写入的替换与新建），
   * 否则让用户再点一次，不自动重放。成功后重读列表。
   */
  async command(command: ScriptConfigurationCommand, failure: string, options: { retryOnConflict?: boolean } = {}): Promise<ScriptConfigurationCommandResponse | undefined> {
    if (this.state.busy) return undefined;
    if (!this.state.revision) await this.load();
    if (!this.state.revision) return undefined;
    this.patch({ busy: true, notice: undefined });
    try {
      let response: ScriptConfigurationCommandResponse;
      try {
        response = await this.client.scriptConfigurations.command(this.state.revision, command);
      } catch (error) {
        if (!options.retryOnConflict || !(error instanceof BackendError && error.status === 409)) throw error;
        await this.reloadAfterChange();
        response = await this.client.scriptConfigurations.command(this.state.revision, command);
      }
      await this.reloadAfterChange();
      return response;
    } catch (error) {
      await this.fail(failure, error);
      return undefined;
    } finally {
      this.patch({ busy: false });
    }
  }

  private async reload(): Promise<void> {
    if (this.state.status === 'idle' || this.state.status === 'failed') this.patch({ status: 'loading' });
    try {
      const list = await this.client.scriptConfigurations.list();
      if (list.revision !== this.state.revision) this.details.clear();
      this.patch({ status: 'ready', revision: list.revision, configurations: list.configurations });
    } catch (error) {
      this.patch({ status: this.state.configurations ? 'ready' : 'failed', notice: { level: 'error', message: `读取组件库失败：${errorText(error)}` } });
      return;
    }
    await this.refreshSelection();
  }

  /** 列表重读后：选中的组件没了就清空；库修订变了就重读详情（详情里的包引用钉在库修订上）。 */
  private async refreshSelection(): Promise<void> {
    const { detail, revision } = this.state;
    const selection = selectedComponent(this.state);
    if (selection === undefined) return;
    if (!this.summary(selection)) {
      await this.select(null);
      return;
    }
    if (detail?.configuration.configurationId === selection && detail.revision === revision) return;
    const seq = ++this.selectSeq;
    if (detail?.configuration.configurationId !== selection) this.patch({ detail: undefined, detailLoading: true });
    await this.fetchSelected(seq, selection);
  }

  private async fetchSelected(seq: number, configurationId: string): Promise<void> {
    try {
      const detail = await this.detailOf(configurationId);
      if (seq !== this.selectSeq) return;
      this.patch({ detail, detailLoading: false });
    } catch (error) {
      if (seq !== this.selectSeq) return;
      this.patch({ detailLoading: false });
      // 可能在重读列表的过程中（refreshSelection），不能等重读，否则互相等待。
      if (error instanceof BackendError && error.status === 409) {
        this.patch({ notice: { level: 'warning', message: CONFLICT_TEXT } });
        void this.reloadAfterChange();
      } else this.patch({ notice: { level: 'error', message: `读取组件失败：${errorText(error)}` } });
    }
  }

  private async fail(prefix: string, error: unknown): Promise<void> {
    if (error instanceof BackendError && error.status === 409) {
      this.patch({ notice: { level: 'warning', message: CONFLICT_TEXT } });
      await this.reloadAfterChange();
      return;
    }
    this.patch({ notice: { level: 'error', message: `${prefix}：${errorText(error)}` } });
  }

  private patch(patch: ComponentPatch): void {
    const next = { ...this.state, ...patch } as Record<string, unknown>;
    for (const key of Object.keys(patch)) if (next[key] === undefined) delete next[key];
    this.state$.set(next as unknown as ComponentState);
  }
}
