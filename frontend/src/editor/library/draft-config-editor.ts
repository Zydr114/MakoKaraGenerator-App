// 作者草稿配置页签的提交与撤销（W8 设计 §2.5）。不依赖 React。
// 控件提交后停 MERGE_MS 合并成一次 PUT；同一时刻只有一条在途，用返回的草稿修订接下一条，期间又改了只提交最新的一份。
// 本地校验有问题时不提交；后端拒绝时记下原因（参数的写在参数下），等下一次改动再提交。
// 撤销与重做是页面内的快照栈：撤销就是把上一份快照再 PUT 一次。包和在用修订不在快照里，换修订不会被撤销带回去。

import { BackendError, ObservableValue } from '../../client';
import type { AuthorDraftConfigurationInput } from '../../protocol';
import { configurationWith, editableOf, rejectionIssue, sameEditable, type ConfigIssue, type EditableConfiguration } from '../model/script-components';
import { errorText } from '../session/editor-session';
import type { FullDraft } from './author-draft-store';

/** 控件提交后等多久再 PUT（这段时间里的改动合并成一次）。 */
const MERGE_MS = 300;

export interface ConfigEditorState {
  /** 页签里显示的值：可能还没提交，或本地校验没通过。 */
  value: EditableConfiguration;
  /** 本地校验的问题。 */
  issues: ConfigIssue[];
  /** 后端没接受的原因；retryable 是网络等与取值无关的失败，保存前会再试。 */
  rejected?: ConfigIssue & { retryable?: boolean };
  /** 有改动等着提交或正在提交。 */
  pending: boolean;
  canUndo: boolean;
  canRedo: boolean;
}

export interface ConfigEditorPort {
  /** 草稿现在的样子（列表里的最新值）。 */
  current(): FullDraft | undefined;
  validate(value: EditableConfiguration): ConfigIssue[];
  /** PUT 配置；成功后新草稿已写回列表。 */
  put(draft: FullDraft, input: AuthorDraftConfigurationInput): Promise<FullDraft>;
  /** 草稿修订过期时重读（写回列表）。 */
  refresh(): Promise<FullDraft>;
}

type StatePatch = { [K in keyof ConfigEditorState]?: ConfigEditorState[K] | undefined };

export class DraftConfigEditor {
  readonly state$: ObservableValue<ConfigEditorState>;
  private undoStack: EditableConfiguration[] = [];
  private redoStack: EditableConfiguration[] = [];
  private timer: ReturnType<typeof setTimeout> | undefined;
  private running: Promise<void> | undefined;
  private disposed = false;

  constructor(
    private readonly port: ConfigEditorPort,
    draft: FullDraft,
    private readonly mergeMs = MERGE_MS,
  ) {
    const value = editableOf(draft.configuration);
    this.state$ = new ObservableValue<ConfigEditorState>({ value, issues: port.validate(value), pending: false, canUndo: false, canRedo: false });
  }

  get state(): ConfigEditorState {
    return this.state$.get();
  }

  /** 页签里还有没进草稿的改动（等着提交、本地有问题或后端没接受）。 */
  get unsettled(): boolean {
    return this.state.pending || this.state.issues.length > 0 || !!this.state.rejected;
  }

  /** 控件提交了一个改动：记一步撤销，停一会儿再提交。 */
  change(next: EditableConfiguration): void {
    if (this.disposed || sameEditable(next, this.state.value)) return;
    this.undoStack.push(this.state.value);
    this.redoStack = [];
    this.set(next);
  }

  undo(): boolean {
    const previous = this.undoStack.pop();
    if (!previous || this.disposed) return false;
    this.redoStack.push(this.state.value);
    this.set(previous);
    return true;
  }

  redo(): boolean {
    const next = this.redoStack.pop();
    if (!next || this.disposed) return false;
    this.undoStack.push(this.state.value);
    this.set(next);
    return true;
  }

  /** 草稿在别处变了（另存为改名、保存源码、采用、自己的 PUT 回来）：页签里没有待提交的改动时跟着显示。 */
  serverChanged(draft: FullDraft): void {
    if (this.disposed || this.timer || this.running || this.unsettled) return;
    const value = editableOf(draft.configuration);
    if (!sameEditable(value, this.state.value)) this.patch({ value, issues: this.port.validate(value) });
  }

  /** 包信息读到了：重新校验，没问题就提交。 */
  revalidate(): void {
    if (this.disposed) return;
    this.patch({ issues: this.port.validate(this.state.value) });
    this.schedule();
  }

  /**
   * 立即提交还没提交的改动并等到提交完（保存到组件库之前用）；网络等原因失败过的再试一次。
   * 返回草稿是不是已经就是页签里的值。
   */
  async flush(): Promise<boolean> {
    clearTimeout(this.timer);
    this.timer = undefined;
    if (this.state.rejected?.retryable) this.patch({ rejected: undefined });
    await this.pump();
    const draft = this.port.current();
    return !!draft && !this.unsettled && sameEditable(editableOf(draft.configuration), this.state.value);
  }

  dispose(): void {
    this.disposed = true;
    clearTimeout(this.timer);
    this.timer = undefined;
  }

  private set(value: EditableConfiguration): void {
    this.patch({ value, issues: this.port.validate(value), rejected: undefined, canUndo: this.undoStack.length > 0, canRedo: this.redoStack.length > 0 });
    this.schedule();
  }

  private schedule(): void {
    clearTimeout(this.timer);
    this.timer = undefined;
    if (this.state.issues.length > 0 || this.state.rejected) {
      this.patch({ pending: !!this.running });
      return;
    }
    this.patch({ pending: true });
    this.timer = setTimeout(() => {
      this.timer = undefined;
      void this.pump();
    }, this.mergeMs);
  }

  private pump(): Promise<void> {
    this.running ??= this.drain().finally(() => {
      this.running = undefined;
      if (!this.timer) this.patch({ pending: false });
    });
    return this.running;
  }

  /** 一条接一条地提交，直到草稿就是页签里的值；本地有问题或后端没接受时停下。 */
  private async drain(): Promise<void> {
    let refreshed = false;
    while (!this.disposed) {
      const { value, issues, rejected } = this.state;
      const draft = this.port.current();
      if (issues.length > 0 || rejected || !draft || sameEditable(editableOf(draft.configuration), value)) return;
      try {
        await this.port.put(draft, configurationWith(draft.configuration, value));
        refreshed = false;
      } catch (error) {
        if (this.disposed) return;
        // 提交期间又改了：新的值也许就没问题，接着提交新的。
        if (this.state.value !== value) continue;
        if (error instanceof BackendError && error.status === 409 && !refreshed) {
          // 草稿修订过期（别处刚改过）：重读后用新修订再提交一次。
          refreshed = true;
          try {
            await this.port.refresh();
            continue;
          } catch (refreshError) {
            error = refreshError;
          }
        }
        if (error instanceof BackendError && (error.status === 400 || error.status === 422)) this.patch({ rejected: rejectionIssue(error.field, error.message) });
        else this.patch({ rejected: { message: `提交配置失败：${errorText(error)}`, retryable: true } });
        return;
      }
    }
  }

  /** 值为 undefined 的键从状态中删除。 */
  private patch(patch: StatePatch): void {
    if (this.disposed) return;
    const next: Record<string, unknown> = { ...this.state };
    for (const [key, value] of Object.entries(patch)) {
      if (value === undefined) delete next[key];
      else next[key] = value;
    }
    this.state$.set(next as unknown as ConfigEditorState);
  }
}
