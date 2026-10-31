// W7 第 7 步：来源输入的输入名、初始映射与提交形状，模板事实的缓存（W7 设计 §2.7）。

import { describe, expect, it } from 'vitest';
import type { MakoClient } from '../../src/client';
import { TemplateFacts } from '../../src/editor/appearance/template-facts';
import { branchInputNames, branchInputsField, defaultInputMapping, inputMappingText, missingInputs, sameInputNames } from '../../src/editor/model/branch-inputs';
import type { LyricBranchSummary, TemplateEffectRecipe, TemplateSummary } from '../../src/protocol';

const recipe = (readScope: TemplateEffectRecipe['readScope'], branchInput: string | null, enabled = true): TemplateEffectRecipe => ({
  recipeId: `r-${branchInput}`,
  name: 'x',
  target: 'tokenFamily',
  packageId: 'p',
  revision: 'r',
  exportId: 'e',
  parametersJson: '{}',
  overlayLayer: 'package',
  enabled,
  readScope,
  branchInput,
  fixedSeed: null,
  configurationId: null,
});

const BRANCHES: LyricBranchSummary[] = [
  { id: 'primary', name: '主歌词', role: 'primary', timingMode: 'independent', lineCount: 3 },
  { id: 'zh', name: '中文', role: 'translation', timingMode: 'followPrimary', lineCount: 3 },
  { id: 'note', name: '注释', role: 'custom', timingMode: 'independent', lineCount: 1 },
];

describe('来源输入', () => {
  it('输入名：读取范围为“指定输入”的配方，停用的也算，去重排序', () => {
    expect(branchInputNames({ effectRecipes: [recipe('branchInput', '翻译'), recipe('boundSources', null), recipe('branchInput', '注释', false), recipe('branchInput', '翻译')] })).toEqual(['翻译', '注释']);
    expect(branchInputNames({ effectRecipes: [] })).toEqual([]);
  });

  it('初始映射：保留已选的；按来源名称对上，再按唯一的用途对上；对不上留空', () => {
    expect(defaultInputMapping(['翻译', '注释', '别的'], BRANCHES)).toEqual({ 翻译: 'zh', 注释: 'note', 别的: '' });
    expect(defaultInputMapping(['翻译'], BRANCHES, { 翻译: 'primary' })).toEqual({ 翻译: 'primary' });
    expect(defaultInputMapping(['翻译'], BRANCHES, { 翻译: 'gone' })).toEqual({ 翻译: 'zh' });
    // 两个翻译来源时不猜。
    expect(defaultInputMapping(['翻译'], [...BRANCHES, { ...BRANCHES[1]!, id: 'en', name: 'English' }])).toEqual({ 翻译: '' });
  });

  it('没选好的输入、提交形状、比较与说明', () => {
    expect(missingInputs(['翻译', '别的'], { 翻译: 'zh', 别的: '' }, BRANCHES)).toEqual(['别的']);
    expect(branchInputsField([], {})).toBeUndefined();
    expect(branchInputsField(['翻译'], { 翻译: 'zh', 多余: 'note' })).toEqual({ 翻译: 'zh' });
    expect(sameInputNames(['b', 'a'], ['a', 'b'])).toBe(true);
    expect(sameInputNames(['a'], ['a', 'b'])).toBe(false);
    expect(inputMappingText({ 翻译: 'zh', 注释: 'gone' }, BRANCHES)).toBe('翻译 → 中文、注释 → 已删除的来源');
  });
});

describe('模板事实', () => {
  const summary = (templateId: string, hasEffectRecipes: boolean): TemplateSummary => ({
    templateId,
    templateRevision: `${templateId}-r1`,
    name: templateId,
    kind: 'line',
    properties: ['sungColor'],
    hasEffectRecipes,
    hasPackageMaterials: false,
    hasPackages: hasEffectRecipes,
  });
  const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

  it('目标键与输入名按模板修订读一次；失败后换库修订或重试再读；作废后重读', async () => {
    const calls: string[] = [];
    let fail = true;
    const client = {
      templates: {
        previewOptions: async (source: { templateId: string; libraryRevision: string }) => {
          calls.push(`options ${source.templateId} ${source.libraryRevision}`);
          return { samples: [{ targets: [{ key: 'line' }, { key: 'project' }] }, { targets: [{ key: 'mark' }, { key: 'line' }] }] };
        },
        fullDetail: async (_libraryId: string, templateId: string, revision: string) => {
          calls.push(`detail ${templateId} ${revision}`);
          if (fail) throw new Error('库已改变');
          return { definition: { effectRecipes: [recipe('branchInput', '翻译')] } };
        },
      },
    } as unknown as MakoClient;
    const facts = new TemplateFacts(client);
    const a = summary('a', true);
    expect(facts.targetKeys('personal', 'L1', a)).toEqual({ status: 'loading' });
    facts.targetKeys('personal', 'L1', a);
    await flush();
    expect(facts.targetKeys('personal', 'L2', a)).toEqual({ status: 'ready', value: ['line', 'project', 'mark'] });
    expect(calls).toEqual(['options a L1']);

    expect(facts.branchInputs('personal', 'L1', summary('b', false))).toEqual({ status: 'ready', value: [] });
    facts.branchInputs('personal', 'L1', a);
    await flush();
    expect(facts.branchInputs('personal', 'L1', a)).toMatchObject({ status: 'failed', error: '库已改变' });
    expect(calls).toHaveLength(2);
    fail = false;
    // 换了库修订就重读。
    expect(facts.branchInputs('personal', 'L2', a).status).toBe('loading');
    await flush();
    expect(facts.branchInputs('personal', 'L2', a)).toEqual({ status: 'ready', value: ['翻译'] });
    expect(await facts.loadBranchInputs('personal', 'L2', a)).toEqual(['翻译']);
    expect(calls).toHaveLength(3);

    facts.forget();
    expect(await facts.loadBranchInputs('personal', 'L3', a)).toEqual(['翻译']);
    expect(calls.at(-1)).toBe('detail a L3');
  });
});
