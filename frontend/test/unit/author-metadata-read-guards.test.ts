// 只读整合审查的窄补充；尚未执行。
import { describe, expect, it } from 'vitest';
import { ObservableValue, type MakoClient } from '../../src/client';
import { AuthorDraftStore, type FullDraft } from '../../src/editor/library/author-draft-store';
import type { ComponentLibraryStore } from '../../src/editor/library/component-library-store';
import type { AuthorSourceInspectionResponse } from '../../src/protocol';

const A = 'sha256:a'; const B = 'sha256:b';
function draft(revision = 'r1', source = A): FullDraft {
  return { draftId: 'd1', draftRevision: revision, name: '组件', packageId: 'demo', exportId: 'fx', baseSourceRevision: A,
    currentSourceRevision: B, canCompile: true, canAdopt: true, protocolVersion: '1', packageVersion: 3, sdkVersion: '2.0.0',
    configuration: { name: '组件', packageId: 'demo', packageRevision: source, sourceRevision: source, exportId: 'fx',
      parametersJson: '{}', relativeTarget: 'application', readScope: 'boundSources', overlayLayer: 'package', kind: 'Effect', bindingKinds: [] },
    sampleOptions: { samples: [], materialSlots: [], defaultSampleId: 'workshop-single-line-v1' } };
}
function schema(revision: string, source: string): AuthorSourceInspectionResponse {
  return { draftId: 'd1', draftRevision: revision, sourceRevision: source, packageId: 'demo', packageRevision: source,
    name: '包', version: '1', packageVersion: 3, sdkVersion: '2.0.0', shaderProfile: 'skia-ganesh-3.119.4-v2', structureValid: true,
    parameters: [], exports: [{ id: 'fx', kind: 'controller', name: '效果', parameterIds: [], bindingKinds: [], declaration: {} }],
    bindingKinds: [], inputs: [], files: [], diagnostics: [], protocolVersion: '1' };
}
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((complete) => { resolve = complete; });
  return { promise, resolve };
}
const settle = async () => { await Promise.resolve(); await Promise.resolve(); await Promise.resolve(); };
function harness() {
  const clientState = new ObservableValue({ hostInstanceId: 'host-a', clientId: 'client-a' });
  let list = async () => ({ drafts: [draft()], protocolVersion: '1' as const });
  let get = async () => draft();
  let inspect = async (revision: string, source: string) => schema(revision, source);
  let puts = 0;
  const client = { state$: clientState, drafts: {
    list: () => list(), get: () => get(), inspectSource: (_id: string, revision: string, source: string) => inspect(revision, source),
    setConfiguration: async () => { puts++; throw new Error('metadata loading 期间不得 PUT'); },
  } } as unknown as MakoClient;
  const components = { state$: new ObservableValue({ revision: 'lib', status: 'ready' }), state: { status: 'ready', selection: null } } as unknown as ComponentLibraryStore;
  return { store: new AuthorDraftStore(client, components), clientState, puts: () => puts,
    setList: (next: typeof list) => { list = next; }, setGet: (next: typeof get) => { get = next; },
    setInspect: (next: typeof inspect) => { inspect = next; } };
}

describe('作者 metadata 整合读取守卫', () => {
  it('同 source 新 CAS 验证期间保留缓存控件和值，立即阻止旧普通队列 PUT', async () => {
    const h = harness(); await h.store.load(); await settle();
    const editor = h.store.configEditor('d1')!;
    editor.change({ ...editor.state.value, name: '本地待提交' });
    const pending = deferred<AuthorSourceInspectionResponse>(); h.setInspect(() => pending.promise);
    h.store.replace(draft('r2'));
    expect(h.store.state.sourceMetadata.d1?.status).toBe('loading');
    expect(h.store.state.packages.d1?.sourceRevision).toBe(A);
    expect(editor.state.issues.some((issue) => issue.message.includes('精确 metadata'))).toBe(true);
    expect(await editor.flush()).toBe(false); expect(h.puts()).toBe(0);
    expect(editor.state.value.name).toBe('本地待提交');
    pending.resolve(schema('r2', A)); await settle();
    expect(h.store.state.sourceMetadata.d1?.status).toBe('ready');
    expect(editor.state.value.name).toBe('本地待提交');
    h.store.reset('end');
  });

  it('明确比较的旧 GET 不能覆盖在途期间已写回的更新 source/CAS', async () => {
    const h = harness(); await h.store.load(); await settle();
    const pending = deferred<FullDraft>(); h.setGet(() => pending.promise);
    const comparison = h.store.readAdoptionComparison('d1');
    h.store.replace(draft('r3', B));
    pending.resolve(draft('r2'));
    await expect(comparison).rejects.toThrow('迟到的读取未覆盖新修订');
    expect(h.store.draft('d1')?.draftRevision).toBe('r3');
    expect(h.store.draft('d1')?.configuration.sourceRevision).toBe(B);
    h.store.reset('end');
  });

  it('晚到列表不回退读取期间的 replace，也不复活已 reset 的旧身份草稿', async () => {
    const h = harness(); await h.store.load(); await settle();
    const first = deferred<{ drafts: FullDraft[]; protocolVersion: '1' }>(); h.setList(() => first.promise);
    const loading = h.store.load(); h.store.replace(draft('r3', B));
    first.resolve({ drafts: [draft()], protocolVersion: '1' }); await loading;
    expect(h.store.draft('d1')?.draftRevision).toBe('r3');
    const second = deferred<{ drafts: FullDraft[]; protocolVersion: '1' }>(); h.setList(() => second.promise);
    const resetting = h.store.load(); h.clientState.set({ hostInstanceId: 'host-b', clientId: 'client-b' }); h.store.reset('身份改变');
    second.resolve({ drafts: [draft()], protocolVersion: '1' }); await resetting;
    expect(h.store.state.drafts).toEqual([]); expect(h.store.state.packages).toEqual({});
    h.store.reset('end');
  });
});
