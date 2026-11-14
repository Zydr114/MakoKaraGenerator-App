// 作者草稿（W8 设计 §2.5、§2.8）：配置页签的校验、合并提交、撤销与重做、后端拒绝；草稿试样换输入与失败文字。

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { BackendError, PreviewFailedError, type MakoClient } from '../../src/client';
import { type FullDraft } from '../../src/editor/library/author-draft-store';
import { DraftConfigEditor } from '../../src/editor/library/draft-config-editor';
import { authorSampleFailure, TemplateWorkshop } from '../../src/editor/library/template-workshop';
import type { AuthorSampleSource } from '../../src/editor/library/template-thumbnails';
import { configurationIssues, editableOf, rejectionIssue, withExport, type EditableConfiguration } from '../../src/editor/model/script-components';
import type { AuthorDraftConfigurationInput, BackendDiagnostic, EffectPackageExport, EffectPackageInspectResponse } from '../../src/protocol';

const WORD: EffectPackageExport = {
  id: 'word',
  kind: 'controller',
  name: '逐字强调',
  parameterIds: ['peakScale'],
  bindingKinds: ['lines', 'tokens'],
  declaration: { outputs: [{ id: 'scale', kind: 'Scale', target: 'tokenFamily' }] },
};
const SHAKE: EffectPackageExport = { id: 'shake', kind: 'controller', name: '整行抖动', parameterIds: [], bindingKinds: ['lines'], declaration: { outputs: [{ id: 'o', kind: 'Offset', target: 'line' }] } };

const PKG: EffectPackageInspectResponse = {
  reference: { kind: 'asset', packageId: 'demo.fx', packageRevision: 'sha256:fx1', packageAssetId: 'asset-1' },
  packageId: 'demo.fx',
  packageRevision: 'sha256:fx1',
  name: '演示包',
  version: '1.0.0',
  manifest: {},
  manifestJson: '{}',
  structureValid: true,
  parameters: [{ id: 'peakScale', type: 'number', label: '放大比例', defaultValueJson: '1.5', minimumJson: '1', maximumJson: '4', options: [] }],
  exports: [WORD, SHAKE],
  bindingKinds: ['lines', 'tokens'],
  inputs: [],
  files: [],
  diagnostics: [],
  protocolVersion: '1',
};

function draft(revision: string, patch: Partial<FullDraft['configuration']> = {}): FullDraft {
  return {
    draftId: 'd1',
    draftRevision: revision,
    name: patch.name ?? '逐字强调',
    packageId: 'demo.fx',
    baseSourceRevision: 'sha256:fx1',
    currentSourceRevision: 'sha256:fx1',
    exportId: 'word',
    canCompile: true,
    canAdopt: true,
    configuration: {
      name: '逐字强调',
      packageId: 'demo.fx',
      packageRevision: 'sha256:fx1',
      sourceRevision: 'sha256:fx1',
      exportId: 'word',
      parametersJson: '{}',
      relativeTarget: 'tokenFamily',
      readScope: 'boundSources',
      overlayLayer: 'package',
      kind: 'Effect',
      bindingKinds: ['lines', 'tokens'],
      ...patch,
    },
    sampleOptions: { samples: [], materialSlots: [], defaultSampleId: 'workshop-single-line-v1' },
    protocolVersion: '1',
  };
}

describe('配置页签的本地校验', () => {
  const value = editableOf(draft('r1').configuration);

  it('名称、参数越界、种子、指定输入要有输入名；参数的问题带参数 ID', () => {
    expect(configurationIssues(value, 'Effect', PKG)).toEqual([]);
    expect(configurationIssues({ ...value, name: ' ', parametersJson: '{"peakScale":9}', fixedSeed: '-1', readScope: 'branchInput', branchInput: '' }, 'Effect', PKG)).toEqual([
      { field: 'name', message: '名称不能为空。' },
      { parameterId: 'peakScale', message: expect.stringMatching(/^放大比例：/) },
      { field: 'branchInput', message: '要填输入名。' },
      { field: 'fixedSeed', message: expect.stringMatching(/种子/) },
    ]);
  });

  it('作用于只看与模板用途无关的规则；没有图元输出时叠放层只能随包；材质只查名称和参数', () => {
    expect(configurationIssues({ ...value, exportId: 'shake', relativeTarget: 'tokenFamily' }, 'Effect', PKG)).toEqual([{ field: 'relativeTarget', message: '“整行抖动”不能作用于文本单元。' }]);
    expect(configurationIssues({ ...value, relativeTarget: 'application' }, 'Effect', PKG)).toEqual([]);
    expect(configurationIssues({ ...value, overlayLayer: 'aboveText' }, 'Effect', PKG)).toEqual([{ field: 'overlayLayer', message: '“逐字强调”没有图元输出，叠放层只能“随包”。' }]);
    expect(configurationIssues({ ...value, overlayLayer: 'aboveText', fixedSeed: 'x' }, 'Material', PKG)).toEqual([]);
    // 包信息还没读到：参数只查是不是 JSON 对象。
    expect(configurationIssues({ ...value, parametersJson: '{"peakScale":9}' }, 'Effect', undefined)).toEqual([]);
    expect(configurationIssues({ ...value, parametersJson: '[' }, 'Effect', undefined)).toEqual([{ message: '参数不是合法的 JSON 对象。' }]);
  });

  it('换导出：参数清空，作用于不行时取新导出的默认；后端拒绝的 parameters.<参数> 写在参数下', () => {
    expect(withExport({ ...value, parametersJson: '{"peakScale":3}' }, 'Effect', SHAKE)).toMatchObject({ exportId: 'shake', parametersJson: '{}', relativeTarget: 'line' });
    expect(rejectionIssue('parameters.peakScale', '越界')).toEqual({ parameterId: 'peakScale', message: '越界' });
    expect(rejectionIssue('parameters.ease.points', '折线不对')).toEqual({ parameterId: 'ease', message: '折线不对' });
    expect(rejectionIssue('configuration', '源材质不创建控制器实例')).toEqual({ field: 'configuration', message: '源材质不创建控制器实例' });
  });
});

describe('配置页签的提交', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  /** 假草稿：每次 PUT 成功修订加一；fail 给出下一次 PUT 的错误；hold 时 PUT 挂起等放行。 */
  function harness(options: { pkg?: EffectPackageInspectResponse } = {}) {
    let current = draft('r1');
    let revision = 1;
    const puts: { revision: string; input: AuthorDraftConfigurationInput }[] = [];
    const failures: unknown[] = [];
    let held: (() => void) | undefined;
    let hold = false;
    const editor = new DraftConfigEditor(
      {
        current: () => current,
        validate: (value) => configurationIssues(value, 'Effect', options.pkg ?? PKG),
        put: async (d, input) => {
          puts.push({ revision: d.draftRevision, input });
          if (hold) await new Promise<void>((resolve) => (held = resolve));
          const failure = failures.shift();
          if (failure) throw failure;
          if (d.draftRevision !== current.draftRevision) throw new BackendError(409, { code: 'revisionConflict', message: '草稿修订过期' } as never);
          current = { ...draft(`r${++revision}`, { ...input, kind: 'Effect', bindingKinds: [], packageRevision: input.sourceRevision } as never) };
          editor.serverChanged(current);
          return current;
        },
        refresh: async () => current,
      },
      current,
    );
    return {
      editor,
      puts,
      current: () => current,
      setCurrent: (d: FullDraft) => void (current = d),
      fail: (error: unknown) => failures.push(error),
      hold: (on: boolean) => void (hold = on),
      release: () => held?.(),
    };
  }
  const v = (h: ReturnType<typeof harness>, patch: Partial<EditableConfiguration>) => ({ ...h.editor.state.value, ...patch });

  it('300 毫秒内的改动合并成一次 PUT；在途时再改，用返回的修订接着提交最新的一份', async () => {
    const h = harness();
    h.editor.change(v(h, { parametersJson: '{"peakScale":2}' }));
    h.editor.change(v(h, { name: '强调' }));
    expect(h.editor.state.pending).toBe(true);
    await vi.advanceTimersByTimeAsync(299);
    expect(h.puts).toHaveLength(0);
    await vi.advanceTimersByTimeAsync(1);
    expect(h.puts).toHaveLength(1);
    expect(h.puts[0]!.input).toMatchObject({ name: '强调', parametersJson: '{"peakScale":2}', sourceRevision: 'sha256:fx1', branchInput: null, fixedSeed: null });

    h.hold(true);
    h.editor.change(v(h, { parametersJson: '{"peakScale":3}' }));
    await vi.advanceTimersByTimeAsync(300);
    h.editor.change(v(h, { parametersJson: '{"peakScale":3.5}' }));
    h.editor.change(v(h, { parametersJson: '{"peakScale":4}' }));
    h.hold(false);
    h.release();
    await vi.advanceTimersByTimeAsync(300);
    expect(h.puts.map((p) => [p.revision, p.input.parametersJson])).toEqual([
      ['r1', '{"peakScale":2}'],
      ['r2', '{"peakScale":3}'],
      ['r3', '{"peakScale":4}'],
    ]);
    expect(h.editor.state.pending).toBe(false);
    expect(h.current().configuration.parametersJson).toBe('{"peakScale":4}');
  });

  it('本地有问题时不提交；后端拒绝时记下原因、不再重试，下一次改动再提交', async () => {
    const h = harness();
    h.editor.change(v(h, { parametersJson: '{"peakScale":9}' }));
    await vi.advanceTimersByTimeAsync(300);
    expect(h.puts).toHaveLength(0);
    expect(h.editor.state.issues[0]).toMatchObject({ parameterId: 'peakScale' });
    expect(await h.editor.flush()).toBe(false);

    h.fail(new BackendError(400, { code: 'invalidRequest', message: '曲线进度必须在 [0,1]。', field: 'parameters.peakScale' } as never));
    h.editor.change(v(h, { parametersJson: '{"peakScale":2}' }));
    await vi.advanceTimersByTimeAsync(300);
    expect(h.editor.state.rejected).toEqual({ parameterId: 'peakScale', message: '曲线进度必须在 [0,1]。' });
    await vi.advanceTimersByTimeAsync(1000);
    expect(h.puts).toHaveLength(1);

    h.editor.change(v(h, { parametersJson: '{"peakScale":2.5}' }));
    await vi.advanceTimersByTimeAsync(300);
    expect(h.editor.state.rejected).toBeUndefined();
    expect(h.puts).toHaveLength(2);
  });

  it('草稿修订过期时重读再提交一次；网络失败在保存前再试', async () => {
    const h = harness();
    h.setCurrent(draft('r9'));
    h.fail(new BackendError(409, { code: 'revisionConflict', message: '过期' } as never));
    h.editor.change(v(h, { name: '强调' }));
    await vi.advanceTimersByTimeAsync(300);
    expect(h.puts.map((p) => p.revision)).toEqual(['r9', 'r9']);
    expect(h.current().configuration.name).toBe('强调');

    h.fail(new TypeError('Failed to fetch'));
    h.editor.change(v(h, { name: '强调 2' }));
    await vi.advanceTimersByTimeAsync(300);
    expect(h.editor.state.rejected).toMatchObject({ retryable: true });
    expect(await h.editor.flush()).toBe(true);
    expect(h.current().configuration.name).toBe('强调 2');
  });

  it('schema 边界清旧历史；新普通撤销/重做保持当前在用来源', async () => {
    const h = harness();
    h.editor.change(v(h, { parametersJson: '{"peakScale":2}' }));
    await vi.advanceTimersByTimeAsync(300);
    // 采用新 schema 后明确建立配置历史边界，旧参数快照不能在新源上重放。
    h.setCurrent(draft('r5', { parametersJson: '{}', sourceRevision: 'sha256:fx2' }));
    h.editor.schemaBoundary(h.current());
    expect(h.editor.undo()).toBe(false);
    h.editor.change(v(h, { parametersJson: '{"peakScale":2}' }));
    await vi.advanceTimersByTimeAsync(300);
    expect(h.editor.undo()).toBe(true);
    expect(h.editor.state).toMatchObject({ canUndo: false, canRedo: true });
    await vi.advanceTimersByTimeAsync(300);
    expect(h.puts.at(-1)!.input).toMatchObject({ parametersJson: '{}', sourceRevision: 'sha256:fx2' });
    expect(h.editor.redo()).toBe(true);
    await vi.advanceTimersByTimeAsync(300);
    expect(h.puts.at(-1)!.input.parametersJson).toBe('{"peakScale":2}');
    expect(h.editor.undo() && h.editor.redo() && h.editor.redo()).toBe(false);
  });

  it('别处改了草稿：页签空闲时跟着显示，有待提交的改动时不覆盖', async () => {
    const h = harness();
    h.editor.serverChanged(draft('r2', { name: '另存的名字' }));
    expect(h.editor.state.value.name).toBe('另存的名字');
    h.editor.change(v(h, { parametersJson: '{"peakScale":2}' }));
    h.editor.serverChanged(draft('r3', { name: '别处' }));
    expect(h.editor.state.value).toMatchObject({ name: '另存的名字', parametersJson: '{"peakScale":2}' });
  });
});

describe('草稿试样', () => {
  const author = (revision: string, slots: AuthorSampleSource['options']['materialSlots'] = []): AuthorSampleSource => ({
    kind: 'authorDraft',
    draftId: 'd1',
    draftRevision: revision,
    authorSourceRevision: 'sha256:fx1',
    options: {
      samples: [{ sampleId: 'workshop-single-line-v1', multiLine: false, durationUs: '12000000', targets: ['line', 'token'].map((key) => ({ key, binding: { kind: key, ids: [], includeDescendants: false } })) }],
      materialSlots: slots,
      defaultSampleId: 'workshop-single-line-v1',
    },
  });

  function fakeClient(failure?: PreviewFailedError) {
    const log = { creates: [] as unknown[], replaces: [] as unknown[], closes: [] as string[] };
    let generation = 1;
    let fail = failure;
    const client = {
      preview: {
        createAuthorDraft: async (input: unknown) => {
          log.creates.push(input);
          return { previewId: `v${log.creates.length}`, previewGeneration: '1', source: { sourceRevision: 'r' } };
        },
        replaceInput: async (previewId: string, input: { expectedPreviewGeneration: string; source: unknown }) => {
          log.replaces.push({ previewId, ...input });
          return { previewGeneration: String(++generation), source: { sourceRevision: 'r' } };
        },
        requestFrame: async () => ({ previewGeneration: String(generation) }),
        waitForFrame: async (previewId: string, expect: { requestSeq: string }) => {
          if (fail) {
            const error = fail;
            fail = undefined;
            throw error;
          }
          return { previewId, requestSeq: expect.requestSeq, content: { sha256: `${previewId}#${generation}` } };
        },
        fetchFrame: async (frame: { content: { sha256: string } }) => ({ blob: new Blob([frame.content.sha256]), sha256: frame.content.sha256, verified: true }),
        diagnostics: async () => ({ diagnostics: COMPILE }),
        closeBestEffort: async (previewId: string) => void log.closes.push(previewId),
      },
    } as unknown as MakoClient;
    return { client, log, failNext: (error: PreviewFailedError) => void (fail = error) };
  }

  const settle = () => new Promise((r) => setTimeout(r, 0));

  it('每个草稿一个会话：草稿修订、目标、材质部位变了用 PUT input 换输入；材质草稿默认未唱填充；换草稿关掉旧会话', async () => {
    const { client, log } = fakeClient();
    const workshop = new TemplateWorkshop(client, undefined, 0);
    workshop.show(author('dr1', ['unsungFill', 'sungFill', 'stroke']), 'draft|d1');
    await settle();
    expect(log.creates).toEqual([
      expect.objectContaining({ draftId: 'd1', draftRevision: 'dr1', authorSourceRevision: 'sha256:fx1', sample: { sampleId: 'workshop-single-line-v1', targetKey: 'line', materialSlot: 'unsungFill' } }),
    ]);
    expect(workshop.state.frame).toMatchObject({ status: 'ready', sha256: 'v1#1' });

    workshop.show(author('dr2', ['unsungFill', 'sungFill', 'stroke']), 'draft|d1');
    await settle();
    workshop.setTarget('token');
    await settle();
    workshop.setMaterialSlot('stroke');
    await settle();
    expect(log.creates).toHaveLength(1);
    expect(log.replaces).toEqual([
      expect.objectContaining({ previewId: 'v1', expectedPreviewGeneration: '1', source: expect.objectContaining({ kind: 'authorDraft', sourceRevision: 'dr2' }) }),
      expect.objectContaining({ expectedPreviewGeneration: '2', source: expect.objectContaining({ templateSample: { sampleId: 'workshop-single-line-v1', targetKey: 'token', materialSlot: 'unsungFill' } }) }),
      expect.objectContaining({ source: expect.objectContaining({ templateSample: expect.objectContaining({ materialSlot: 'stroke' }) }) }),
    ]);
    expect(workshop.state.frame).toMatchObject({ status: 'ready', sha256: 'v1#4' });

    workshop.show({ ...author('x1'), draftId: 'd2' }, 'draft|d2');
    await settle();
    expect(log.closes).toEqual(['v1']);
    // 新草稿也有这个目标就留着；效果草稿没有材质部位。
    expect(log.creates[1]).toMatchObject({ draftId: 'd2', sample: { targetKey: 'token' } });
    expect(log.creates[1]).not.toHaveProperty('sample.materialSlot');
    workshop.dispose();
  });

  it('会话失败：读诊断写明编译失败，关掉会话，下次取帧重建', async () => {
    const { client, log } = fakeClient(new PreviewFailedError(undefined, 'WorkerPackageCompilationFailed'));
    const workshop = new TemplateWorkshop(client, undefined, 0);
    workshop.show(author('dr1'), 'draft|d1');
    await settle();
    expect(workshop.state.frame).toMatchObject({ status: 'error', failureCode: 'WorkerPackageCompilationFailed' });
    expect(workshop.state.frame!.message!.split('\n')).toEqual(['脚本编译失败', "CS0103 src/WordEmphasis.cs:10:16 The name 'contxt' does not exist"]);
    expect(log.closes).toEqual(['v1']);
    workshop.seek(1_000_000);
    await settle();
    expect(log.creates).toHaveLength(2);
    expect(workshop.state.frame).toMatchObject({ status: 'ready' });
    workshop.dispose();
  });

  it('成功之后失败：照旧带着上一张成功帧的修订与时刻，状态仍是失败；再成功后不再带旧帧；换草稿清掉', async () => {
    const { client, failNext } = fakeClient();
    const workshop = new TemplateWorkshop(client, undefined, 0);
    workshop.show(author('dr1'), 'draft|d1');
    await settle();
    expect(workshop.state.frame).toMatchObject({ status: 'ready', sha256: 'v1#1', revision: 'sha256:fx1', timeUs: 3_000_000 });
    expect(workshop.state.frame).not.toHaveProperty('lastGood');

    failNext(new PreviewFailedError(undefined, 'ScriptExecutionFailed'));
    workshop.seek(1_000_000);
    await settle();
    expect(workshop.state.frame).toMatchObject({ status: 'error', failureCode: 'ScriptExecutionFailed', lastGood: { sha256: 'v1#1', revision: 'sha256:fx1', timeUs: 3_000_000 } });
    expect(workshop.state.frame).not.toHaveProperty('frame');

    workshop.seek(2_000_000);
    expect(workshop.state.frame).toMatchObject({ status: 'error' });
    await settle();
    expect(workshop.state.frame).toMatchObject({ status: 'ready', timeUs: 2_000_000 });
    expect(workshop.state.frame).not.toHaveProperty('lastGood');

    failNext(new PreviewFailedError(undefined, 'ScriptExecutionFailed'));
    workshop.show({ ...author('x1'), draftId: 'd2' }, 'draft|d2');
    await settle();
    expect(workshop.state.frame).toMatchObject({ status: 'error' });
    expect(workshop.state.frame).not.toHaveProperty('lastGood');
    workshop.dispose();
  });

  it('失败时按页读完预览诊断', async () => {
    const { client } = fakeClient(new PreviewFailedError(undefined, 'WorkerPackageCompilationFailed'));
    const cursors: (string | undefined)[] = [];
    const pages = [
      { diagnostics: [COMPILE[0]!], hasMore: true, nextCursor: 'c1' },
      { diagnostics: [{ ...COMPILE[0]!, source: source(12, 'a') }], hasMore: true, nextCursor: 'c2' },
      { diagnostics: [{ ...COMPILE[0]!, source: source(14, 'a') }], hasMore: false },
    ];
    (client.preview as unknown as { diagnostics: unknown }).diagnostics = async (_id: string, cursor?: string) => {
      cursors.push(cursor);
      return pages[cursors.length - 1];
    };
    const workshop = new TemplateWorkshop(client, undefined, 0);
    workshop.show(author('dr1'), 'draft|d1');
    await settle();
    await settle();
    expect(cursors).toEqual([undefined, 'c1', 'c2']);
    expect(workshop.state.frame!.message!.split('\n')).toHaveLength(4);
    workshop.dispose();
  });

  it('运行出错跟上诊断消息，超时写明，其他沿用模板试样的文字', () => {
    const runtime: BackendDiagnostic = { code: 'ScriptExecutionFailed', severity: 'error', message: '脚本抛出 InvalidOperationException', originKind: 'runtime' };
    expect(authorSampleFailure(new PreviewFailedError(undefined, 'ScriptExecutionFailed'), [runtime]).message).toBe('脚本运行出错：脚本抛出 InvalidOperationException');
    const opaqueFailure = new PreviewFailedError({ code: 'internalError', message: '预览渲染失败。' } as never, 'ScriptExecutionFailed');
    expect(authorSampleFailure(opaqueFailure, []).message).toBe('脚本运行出错：预览渲染失败。\n预览未返回详细诊断，请到验证页签运行检查。');
    expect(authorSampleFailure(new PreviewFailedError(undefined, 'WorkerPackageTimedOut'), []).message).toBe('脚本运行超时（约 25 秒没有返回）');
    expect(authorSampleFailure(new PreviewFailedError({ code: 'x', message: '别的' } as never, 'Other'), []).message).toBe('试样失败：别的');
  });
});

/** 同一个编译错误按效果实例重复两条，去重后一条。 */
const source = (line: number, instanceId: string) => ({
  packageId: 'demo.fx',
  revision: 'sha256:fx2',
  instanceId,
  code: 'CS0103',
  message: "The name 'contxt' does not exist",
  isError: true,
  path: 'src/WordEmphasis.cs',
  line,
  column: 16,
});
const COMPILE: BackendDiagnostic[] = [
  { code: 'CS0103', severity: 'error', message: '', originKind: 'compileSource', source: source(10, 'a') },
  { code: 'CS0103', severity: 'error', message: '', originKind: 'compileSource', source: source(10, 'b') },
];
