// 独立采用候选、完整配置历史与未提交原文保护。
import { describe, expect, it } from 'vitest';
import { BackendError } from '../../src/client';
import { DraftAdoptionModel, adoptionIssues, type DraftAdoptionPort } from '../../src/editor/library/draft-adoption';
import { DraftSourceModel, snapshotConfiguration, type DraftSourcePort } from '../../src/editor/library/draft-source';
import { DraftConfigEditor } from '../../src/editor/library/draft-config-editor';
import { editableOf } from '../../src/editor/model/script-components';
import type { FullDraft } from '../../src/editor/library/author-draft-store';
import type { AuthorPackageConfigurationInput, AuthorSourceInspectionResponse } from '../../src/protocol';

const RAW = '{"ticks":9223372036854775807}';
const A = 'sha256:a'; const B = 'sha256:b';
function draft(revision = 'r1', source = A): FullDraft {
  return { draftId: 'd1', draftRevision: revision, packageId: 'demo', name: '组件', exportId: 'fx',
    baseSourceRevision: A, currentSourceRevision: B, canAdopt: true, canCompile: true, packageVersion: 3, sdkVersion: '2.0.0', protocolVersion: '1',
    configuration: { name: '组件', packageId: 'demo', packageRevision: source, sourceRevision: source, exportId: 'fx', parametersJson: RAW,
      relativeTarget: 'application', readScope: 'boundSources', overlayLayer: 'package', kind: 'Effect', bindingKinds: [] },
    sampleOptions: { samples: [], materialSlots: [], defaultSampleId: 'workshop-single-line-v1' } };
}
function metadata(sent: FullDraft, source = B): AuthorSourceInspectionResponse {
  return { draftId: sent.draftId, draftRevision: sent.draftRevision, sourceRevision: source, packageId: 'demo', packageRevision: source,
    name: '包', version: '1', packageVersion: 3, sdkVersion: '2.0.0', shaderProfile: 'svg', structureValid: true,
    parameters: [{ id: 'ticks', type: 'duration', label: '时长', defaultValueJson: '9223372036854775807', maximumJson: '9223372036854775807', options: [] }],
    exports: [{ id: 'fx', name: '效果', kind: 'controller', parameterIds: ['ticks'], bindingKinds: [], declaration: {} }],
    bindingKinds: [], inputs: [], files: [{ path: 'src/main.cs', role: 'csharp', size: 1, sha256: 'h' }], diagnostics: [], protocolVersion: '1' };
}
function harness() {
  let current = draft(); let identity = 'host/client'; let failure: unknown; let refreshes = 0;
  const sent: unknown[] = [];
  const port: DraftAdoptionPort = {
    identity: () => identity, current: () => current, settleConfiguration: async () => true,
    refresh: async () => { refreshes++; return current; }, inspect: async (d, revision) => metadata(d, revision), compiled: () => true,
    adopt: async (candidate) => { sent.push(candidate); if (failure) throw failure; return { kind: 'adopted', revision: B }; },
  };
  return { port, model: new DraftAdoptionModel(port), sent, current: () => current, refreshes: () => refreshes,
    changeServer: () => { current = { ...current, draftRevision: 'r2' }; }, changeIdentity: () => { identity = 'other/client'; },
    fail: (error: unknown) => { failure = error; } };
}

describe('独立采用候选', () => {
  it('保留完整参数原文，取消与候选 undo/redo 都不产生任何写入', async () => {
    const h = harness(); await h.model.prepare(B);
    expect(h.model.state.candidate?.value.parametersJson).toBe(RAW);
    expect(h.model.state.candidate?.metadata?.parameters[0]?.maximumJson).toBe('9223372036854775807');
    h.model.change({ ...h.model.state.candidate!.value, name: '本地新名称' });
    h.model.undo(); expect(h.model.state.candidate!.value.name).toBe('组件');
    h.model.redo(); expect(h.model.state.candidate!.value.name).toBe('本地新名称');
    h.model.close(); expect(h.model.state.open).toBe(false);
    await h.model.prepare(B); expect(h.model.state.candidate!.value.name).toBe('本地新名称');
    expect(h.sent).toEqual([]);
  });

  it('未 blur 的无效原文寄存到候选，取消/晚到 metadata 不丢，且阻止采用和候选历史', async () => {
    const h = harness(); await h.model.prepare(B);
    h.model.change({ ...h.model.state.candidate!.value, name: '已提交本地名' });
    const key = 'configuration/parameter/ticks/时长（毫秒）';
    h.model.setInputDraft(key, { text: '尚未完成.', base: '9223372036854775.807', problem: '格式错误' });
    const value = h.model.state.candidate!.value;
    h.model.undo(); expect(h.model.state.candidate!.value).toEqual(value);
    expect((await h.model.submit()).kind).toBe('failed'); expect(h.sent).toEqual([]);
    h.model.close(); await h.model.prepare(B); await h.model.retryMetadata();
    expect(h.model.state.candidate!.inputDrafts[key]?.text).toBe('尚未完成.');
    expect(h.model.hasInputDrafts).toBe(true); expect(h.model.state.candidate!.value).toEqual(value);
    h.model.setInputDraft(key, undefined); h.model.undo();
    expect(h.model.state.candidate!.value.name).toBe('组件');
  });

  it('409 不重读、不重发，所有字段保留；只有明确 recompare 才更新基准', async () => {
    const h = harness(); await h.model.prepare(B);
    h.model.change({ ...h.model.state.candidate!.value, name: '留住' });
    h.fail(new BackendError(409, { code: 'revisionConflict', message: 'CAS' } as never));
    expect((await h.model.submit()).kind).toBe('failed');
    expect(h.model.state.candidate).toMatchObject({ status: 'stale', baseDraftRevision: 'r1', value: { name: '留住', parametersJson: RAW } });
    expect(h.sent).toHaveLength(1); expect(h.refreshes()).toBe(0);
    h.changeServer(); await h.model.recompare();
    expect(h.model.state.candidate).toMatchObject({ status: 'ready', baseDraftRevision: 'r2', value: { name: '留住', parametersJson: RAW } });
    expect(h.sent).toHaveLength(1); expect(h.refreshes()).toBe(1);
  });

  it('422 和网络失败都不关闭对话框或覆盖本地值', async () => {
    for (const failure of [new BackendError(422, { code: 'commandRejected', field: 'configuration.parametersJson', message: '越界' } as never), new TypeError('offline')]) {
      const h = harness(); await h.model.prepare(B); h.fail(failure);
      const value = h.model.state.candidate!.value;
      await h.model.submit();
      expect(h.model.state.open).toBe(true); expect(h.model.state.candidate!.value).toEqual(value);
      expect(h.sent).toHaveLength(1); expect(h.refreshes()).toBe(0);
    }
  });

  it('迟到 target metadata 不能跨 host/client 或草稿 CAS 到达当前候选', async () => {
    const h = harness(); let release!: (response: AuthorSourceInspectionResponse) => void;
    h.port.inspect = async () => new Promise((resolve) => { release = resolve; });
    const preparing = h.model.prepare(B);
    await Promise.resolve(); await Promise.resolve();
    h.changeIdentity(); release(metadata(draft())); await preparing;
    expect(h.model.state.candidate!.status).toBe('stale');
    expect(h.model.state.candidate!.metadata).toBeUndefined();
    expect(h.model.state.candidate!.value.parametersJson).toBe(RAW);
    expect((await h.model.submit()).kind).toBe('failed'); expect(h.sent).toEqual([]);
  });

  it('目标 material 校验显式指出全部不兼容字段，保留原值不静默修正', () => {
    const pkg = metadata(draft()); pkg.exports = [{ id: 'mat', name: '材质', kind: 'material', parameterIds: [], bindingKinds: [], declaration: {} }];
    const value = { ...editableOf(draft().configuration), exportId: 'mat', relativeTarget: 'line' as const, readScope: 'project' as const,
      branchInput: 'keep', fixedSeed: '9', overlayLayer: 'aboveText' as const, parametersJson: '{}' };
    expect(adoptionIssues(value, pkg).map((issue) => issue.field)).toEqual(['relativeTarget', 'readScope', 'branchInput', 'fixedSeed', 'overlayLayer']);
    expect(value).toMatchObject({ branchInput: 'keep', fixedSeed: '9', relativeTarget: 'line' });
  });
});

describe('完整配置采用历史', () => {
  function sourceHarness() {
    let current = draft(); let posts = 0; let failure: unknown;
    const inputs: AuthorPackageConfigurationInput[] = [];
    const revisions: string[] = [];
    const port: DraftSourcePort = {
      current: () => current, isV2: () => true, read: async () => ({ draftId: 'd1', draftRevision: current.draftRevision,
        sourceRevision: A, packageId: 'demo', packageRevision: A, files: [{ path: 'a.cs', text: 'original' }], protocolVersion: '1' }),
      save: async () => { throw new Error('unused'); }, use: async () => { throw new Error('unused'); },
      adopt: async (_sent, source, configuration) => {
        revisions.push(_sent.draftRevision);
        posts++; if (failure) throw failure;
        const input = configuration ?? editableOf(current.configuration); inputs.push({ ...input });
        const { branchInput: _previousBranch, fixedSeed: _previousSeed, ...previous } = current.configuration;
        const { branchInput, fixedSeed, ...fields } = input;
        current = { ...current, draftRevision: `r${posts + 1}`, configuration: { ...previous, ...fields,
          ...(branchInput === null ? {} : { branchInput }), ...(fixedSeed === null ? {} : { fixedSeed }),
          sourceRevision: source, packageRevision: source } };
        return { draftId: 'd1', packageId: 'demo', draftRevision: current.draftRevision, adoptedSourceRevision: source };
      }, refresh: async () => current, savedRevisions: () => [], rememberSaved: () => {},
      compile: async () => { throw new Error('unused'); }, waitJob: async () => { throw new Error('unused'); },
      cancelJob: async () => {}, compilationResult: async () => { throw new Error('unused'); }, lastCompilation: async () => undefined,
    };
    return { model: new DraftSourceModel(port), current: () => current, posts: () => posts, inputs, revisions,
      fail: (error: unknown) => { failure = error; }, editElsewhere: () => { current = { ...current, configuration: { ...current.configuration, name: '后来修改' } }; } };
  }

  it('采用/undo/redo 带完整快照，dirty 文本与编辑器状态逐字保留', async () => {
    const h = sourceHarness(); await h.model.load(); h.model.keepViewState('a.cs', { cursor: 7 }); h.model.edit('a.cs', 'dirty');
    const before = snapshotConfiguration(h.current().configuration); const value = { ...editableOf(before), name: '新配置', parametersJson: '{"ticks":1}' };
    expect((await h.model.adoptCandidate(B, value, 'r1', before)).kind).toBe('adopted');
    expect(h.current().configuration).toMatchObject({ sourceRevision: B, name: '新配置', parametersJson: '{"ticks":1}' });
    expect((await h.model.undoAdoption()).kind).toBe('adopted');
    expect(h.current().configuration).toMatchObject({ sourceRevision: A, name: '组件', parametersJson: RAW });
    expect((await h.model.redoAdoption()).kind).toBe('adopted');
    expect(h.inputs.map((value) => value.parametersJson)).toEqual(['{"ticks":1}', RAW, '{"ticks":1}']);
    expect(h.revisions).toEqual(['r1', 'r2', 'r3']);
    expect(h.model.state.files[0]?.text).toBe('dirty'); expect(h.model.viewState('a.cs')).toEqual({ cursor: 7 });
  });

  it('后来修改任一完整配置字段后阻止 undo；409 不刷新重发', async () => {
    const h = sourceHarness(); const before = snapshotConfiguration(h.current().configuration);
    await h.model.adoptCandidate(B, editableOf(before), 'r1', before); h.editElsewhere();
    expect((await h.model.undoAdoption()).kind).toBe('failed'); expect(h.posts()).toBe(1);
    const other = sourceHarness(); other.fail(new BackendError(409, { code: 'revisionConflict', message: 'CAS' } as never));
    expect((await other.model.adoptCandidate(B, editableOf(before), 'r1', before)).kind).toBe('failed'); expect(other.posts()).toBe(1);
  });

  it('普通配置未 blur 原文也是 unsettled，晚到 metadata/草稿读取不覆盖且不触发 PUT', async () => {
    let current = draft(); let puts = 0;
    const editor = new DraftConfigEditor({ current: () => current, validate: () => [], refresh: async () => current,
      put: async () => { puts++; return current; } }, current);
    const key = 'configuration/parameter/ticks/时长（毫秒）';
    editor.setInputDraft(key, { text: '未完成.', base: '9223372036854775.807', problem: '格式错误' });
    current = { ...draft('r2'), configuration: { ...draft().configuration, name: '迟到名称' } };
    editor.serverChanged(current); editor.revalidate();
    expect(editor.unsettled).toBe(true); expect(await editor.flush()).toBe(false); expect(editor.undo()).toBe(false);
    expect(editor.state.inputDrafts[key]?.text).toBe('未完成.'); expect(editor.state.value.name).toBe('组件'); expect(puts).toBe(0);
    editor.setInputDraft(key, undefined); expect(editor.state.inputDrafts).toEqual({}); editor.dispose();
  });

  it('任意 serverChanged/metadata 重验都不能解除不确定冻结，只有明确比较入口可以', async () => {
    let current = draft();
    const editor = new DraftConfigEditor({ current: () => current, validate: () => [], refresh: async () => current,
      put: async () => { throw new Error('冻结期间不得 PUT'); } }, current);
    editor.adoptionUncertain();
    current = { ...draft('r2', B), configuration: { ...draft('r2', B).configuration, name: '服务器实际采用名' } };
    editor.serverChanged(current); editor.revalidate();
    expect(editor.state.adoptionUncertain).toBe(true); expect(editor.state.value.name).toBe('组件');
    expect(await editor.flush()).toBe(false); expect(() => editor.lockForAdoption()).toThrow();
    editor.confirmAdoptionComparison(current);
    expect(editor.state.adoptionUncertain).toBeUndefined(); expect(editor.state.value.name).toBe('服务器实际采用名');
    expect(await editor.flush()).toBe(true); editor.dispose();
  });

  it('普通配置在 schema 边界清掉旧 undo/redo；metadata 重验保留 rejected 和本地值', async () => {
    let current = draft();
    const editor = new DraftConfigEditor({ current: () => current, validate: () => [], refresh: async () => current,
      put: async (_sent, input) => { current = { ...current, configuration: { ...current.configuration, ...input } as FullDraft['configuration'] }; return current; } }, current, 0);
    editor.change({ ...editor.state.value, name: '普通修改' }); await editor.flush(); expect(editor.state.canUndo).toBe(true);
    const release = editor.lockForAdoption();
    current = draft('r3', B); editor.serverChanged(current); editor.schemaBoundary(current); release();
    expect(editor.state).toMatchObject({ canUndo: false, canRedo: false, value: { parametersJson: RAW } });
    editor.change({ ...editor.state.value, name: '本地保留' });
    const rejected = { message: '拒绝' }; editor.state$.set({ ...editor.state, rejected });
    editor.revalidate(); expect(editor.state.value.name).toBe('本地保留'); expect(editor.state.rejected).toBe(rejected); expect(editor.state.canUndo).toBe(true);
    editor.dispose();
  });
});
