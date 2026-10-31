// 外观页要知道的模板事实（W7 设计 §2.7）：能用于哪些目标（试样选项里的目标键），要读取哪些来源输入（完整定义里“指定输入”的配方）。
// 按库、模板 ID 与模板修订缓存，整个应用一个；连接重置（Server 重启）后作废。读失败的项在库修订变了或重试时重读。不依赖 React。

import { ObservableValue, type MakoClient } from '../../client';
import type { TemplateLibraryId, TemplateSummary } from '../../protocol';
import { sampleTargetKeys } from '../model/appearance-targets';
import { branchInputNames } from '../model/branch-inputs';
import { errorText } from '../session/editor-session';

export type Fact<T> = { status: 'loading' } | { status: 'ready'; value: T } | { status: 'failed'; error: string; libraryRevision: string };

type Names = readonly string[];
const LOADING: Fact<Names> = { status: 'loading' };
const NONE: Fact<Names> = { status: 'ready', value: [] };

export class TemplateFacts {
  /** 每读到或失败一项加一，界面据此重新取值。 */
  readonly version$ = new ObservableValue(0);
  private readonly targets = new Map<string, Fact<Names>>();
  private readonly inputs = new Map<string, Fact<Names>>();
  private readonly pending = new Map<string, Promise<Names>>();
  /** 每次作废加一；作废前发出的请求回来后不写缓存。 */
  private epoch = 0;

  constructor(private readonly client: MakoClient) {}

  /** 试样选项里所有样本的目标键；还没读过就开始读。 */
  targetKeys(libraryId: TemplateLibraryId, libraryRevision: string, summary: TemplateSummary): Fact<Names> {
    return this.peek(this.targets, 'targets', libraryId, libraryRevision, summary, () => this.readTargets(libraryId, libraryRevision, summary));
  }

  /** 模板要读取的来源输入名；没有效果配方的模板直接是空。还没读过就开始读。 */
  branchInputs(libraryId: TemplateLibraryId, libraryRevision: string, summary: TemplateSummary): Fact<Names> {
    if (!summary.hasEffectRecipes) return NONE;
    return this.peek(this.inputs, 'inputs', libraryId, libraryRevision, summary, () => this.readInputs(libraryId, libraryRevision, summary));
  }

  /** 等读到来源输入名（“更新”分配前用）；读失败时抛出。 */
  async loadBranchInputs(libraryId: TemplateLibraryId, libraryRevision: string, summary: TemplateSummary): Promise<Names> {
    if (!summary.hasEffectRecipes) return [];
    const fact = this.inputs.get(keyOf(libraryId, summary));
    if (fact?.status === 'ready') return fact.value;
    return this.load(this.inputs, 'inputs', libraryId, summary, libraryRevision, () => this.readInputs(libraryId, libraryRevision, summary));
  }

  /** 去掉一个模板读失败的结果，下次取值时重读。 */
  retry(libraryId: TemplateLibraryId, summary: TemplateSummary): void {
    const key = keyOf(libraryId, summary);
    for (const map of [this.targets, this.inputs]) if (map.get(key)?.status === 'failed') map.delete(key);
    this.bump();
  }

  forget(): void {
    this.epoch++;
    this.targets.clear();
    this.inputs.clear();
    this.pending.clear();
    this.bump();
  }

  private peek(map: Map<string, Fact<Names>>, kind: string, libraryId: TemplateLibraryId, libraryRevision: string, summary: TemplateSummary, read: () => Promise<Names>): Fact<Names> {
    const fact = map.get(keyOf(libraryId, summary));
    if (fact && !(fact.status === 'failed' && fact.libraryRevision !== libraryRevision)) return fact;
    void this.load(map, kind, libraryId, summary, libraryRevision, read).catch(() => undefined);
    return map.get(keyOf(libraryId, summary)) ?? LOADING;
  }

  private load(map: Map<string, Fact<Names>>, kind: string, libraryId: TemplateLibraryId, summary: TemplateSummary, libraryRevision: string, read: () => Promise<Names>): Promise<Names> {
    const key = keyOf(libraryId, summary);
    const pendingKey = `${kind}|${key}`;
    const running = this.pending.get(pendingKey);
    if (running) return running;
    const epoch = this.epoch;
    map.set(key, LOADING);
    const promise = read().then(
      (value) => {
        if (epoch === this.epoch) this.settle(map, pendingKey, key, { status: 'ready', value });
        return value;
      },
      (error: unknown) => {
        if (epoch === this.epoch) this.settle(map, pendingKey, key, { status: 'failed', error: errorText(error), libraryRevision });
        throw error;
      },
    );
    this.pending.set(pendingKey, promise);
    return promise;
  }

  private settle(map: Map<string, Fact<Names>>, pendingKey: string, key: string, fact: Fact<Names>): void {
    this.pending.delete(pendingKey);
    map.set(key, fact);
    this.bump();
  }

  private async readTargets(libraryId: TemplateLibraryId, libraryRevision: string, summary: TemplateSummary): Promise<Names> {
    const options = await this.client.templates.previewOptions({
      kind: 'savedTemplate',
      libraryId,
      libraryRevision,
      templateId: summary.templateId,
      templateRevision: summary.templateRevision,
    });
    return sampleTargetKeys(options);
  }

  private async readInputs(libraryId: TemplateLibraryId, libraryRevision: string, summary: TemplateSummary): Promise<Names> {
    const detail = await this.client.templates.fullDetail(libraryId, summary.templateId, libraryRevision);
    return branchInputNames(detail.definition);
  }

  private bump(): void {
    this.version$.update((v) => v + 1);
  }
}

function keyOf(libraryId: TemplateLibraryId, summary: TemplateSummary): string {
  return `${libraryId}|${summary.templateId}|${summary.templateRevision}`;
}
