// Store metadata 的实际来源、迟到响应和本地值保护。
import { describe, expect, it } from 'vitest';
import { ObservableValue, type MakoClient } from '../../src/client';
import { AuthorDraftStore, type FullDraft } from '../../src/editor/library/author-draft-store';
import type { ComponentLibraryStore } from '../../src/editor/library/component-library-store';
import type { AuthorSourceInspectionResponse } from '../../src/protocol';

const A = 'sha256:a'; const B = 'sha256:b';
function draft(revision = 'r1', source = A): FullDraft {
  return { draftId: 'd1', draftRevision: revision, name: '孤立草稿', packageId: 'demo', exportId: 'fx', baseSourceRevision: A,
    currentSourceRevision: B, canCompile: true, canAdopt: true, protocolVersion: '1', packageVersion: 3, sdkVersion: '2.0.0',
    configuration: { name: '孤立草稿', packageId: 'demo', packageRevision: source, sourceRevision: source, exportId: 'fx',
      parametersJson: '{"ticks":9223372036854775807}', relativeTarget: 'application', readScope: 'boundSources', overlayLayer: 'package', kind: 'Effect', bindingKinds: [] },
    sampleOptions: { samples: [], materialSlots: [], defaultSampleId: 'workshop-single-line-v1' } };
}
function schema(sent: FullDraft, source: string): AuthorSourceInspectionResponse {
  return { draftId: 'd1', draftRevision: sent.draftRevision, sourceRevision: source, packageId: 'demo', packageRevision: source,
    name: '固定源', version: '1', packageVersion: 3, sdkVersion: '2.0.0', shaderProfile: 'svg', structureValid: true,
    parameters: [{ id: 'ticks', type: 'duration', label: '时长', defaultValueJson: '9223372036854775807', options: [] }],
    exports: [{ id: 'fx', kind: 'controller', name: '效果', parameterIds: ['ticks'], bindingKinds: [], declaration: {} }],
    bindingKinds: [], inputs: [], files: [], diagnostics: [], protocolVersion: '1' };
}
function harness() {
  const clientState = new ObservableValue({ hostInstanceId: 'host-a', clientId: 'client-a' });
  const requests: { draftRevision: string; sourceRevision: string; resolve(value: AuthorSourceInspectionResponse): void }[] = [];
  const current = draft();
  const client = { state$: clientState, drafts: { list: async () => ({ drafts: [current], protocolVersion: '1' }),
    inspectSource: (_id: string, draftRevision: string, sourceRevision: string) => new Promise<AuthorSourceInspectionResponse>((resolve) => requests.push({ draftRevision, sourceRevision, resolve })) } } as unknown as MakoClient;
  const components = { state$: new ObservableValue({ revision: 'lib', status: 'ready' }), state: { status: 'ready', selection: null } } as unknown as ComponentLibraryStore;
  return { store: new AuthorDraftStore(client, components), requests, clientState };
}
const settle = async () => { await Promise.resolve(); await Promise.resolve(); await Promise.resolve(); };

describe('作者在用源码 metadata', () => {
  it('没有来源组件也读取精确作者源，失效重试不依赖组件 detail', async () => {
    const h = harness(); await h.store.load();
    expect(h.requests.map(({ draftRevision, sourceRevision }) => [draftRevision, sourceRevision])).toEqual([['r1', A]]);
    h.requests[0]!.resolve(schema(draft(), A)); await settle();
    expect(h.store.state.packages.d1?.sourceRevision).toBe(A);
    expect(h.store.state.sourceMetadata.d1?.status).toBe('ready');
    expect(h.store.configEditor('d1')?.state.issues).toEqual([]);
    h.store.reset('end');
  });

  it('旧源迟到响应和 host/client 变更不能替换新源 metadata 或本地无效配置', async () => {
    const h = harness(); await h.store.load();
    const editor = h.store.configEditor('d1')!;
    editor.change({ ...editor.state.value, name: '本地保留', parametersJson: '[' });
    editor.setInputDraft('configuration/parameter/ticks/时长', { text: '尚未失焦.', base: '9223372036854775807', problem: '格式' });
    h.store.replace(draft('r2', B));
    h.requests[1]!.resolve(schema(draft('r2', B), B)); await settle();
    h.requests[0]!.resolve(schema(draft(), A)); await settle();
    expect(h.store.state.packages.d1?.sourceRevision).toBe(B);
    expect(editor.state.value).toMatchObject({ name: '本地保留', parametersJson: '[' });
    expect(editor.state.canUndo).toBe(false); // source边界隔断历史，原文仍保留。
    expect(editor.state.inputDrafts['configuration/parameter/ticks/时长']?.text).toBe('尚未失焦.');
    h.store.retrySourceMetadata('d1');
    h.clientState.set({ hostInstanceId: 'host-b', clientId: 'client-b' });
    h.requests[2]!.resolve(schema(draft('r2', B), B)); await settle();
    expect(h.store.state.sourceMetadata.d1?.status).toBe('loading');
    expect(editor.state.value.parametersJson).toBe('[');
    h.store.reset('end');
  });
});
