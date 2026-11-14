import { describe, expect, it } from 'vitest';
import { BackendError } from '../../src/client';
import type { EffectPackageInspectResponse, JobResponse } from '../../src/protocol';
import type { AuthorPackageFilesInput } from '../../src/protocol/author-package-files';
import { exactDraftPackage, type FullDraft } from '../../src/editor/library/author-draft-store';
import { DraftSourceModel, sourceLanguage, type DraftSourcePort } from '../../src/editor/library/draft-source';
import { compileRows } from '../../src/editor/model/compile-diagnostics';

const BASE = 'sha256:base';
const MANIFEST = '{"packageId":"svg.demo","durationUs":9223372036854775807}';
const SVG = '<svg><path d="M0 0H1"/></svg>';
const initialFiles = [
  { path: 'src/A.cs', role: 'csharp', text: 'class A {}' },
  { path: 'assets/main.svg', role: 'svg', text: SVG },
  { path: 'resources/main.json', role: 'svgResource', text: '{"svgResourceVersion":1}' },
  { path: 'graph/main.json', role: 'graph', text: '{}' },
  { path: 'shader/main.sksl', role: 'shader', text: 'half4 main() { return half4(1); }' },
  { path: 'shader/program.json', role: 'shaderProgram', text: '{}' },
];

function draft(): FullDraft {
  return { draftId: 'd1', draftRevision: 'r1', packageId: 'svg.demo', name: 'SVG', exportId: 'svg',
    packageVersion: 3, sdkVersion: '2.0.0', baseSourceRevision: BASE, currentSourceRevision: BASE,
    canCompile: true, canAdopt: true, protocolVersion: '1', sampleOptions: { samples: [], materialSlots: [], defaultSampleId: 'workshop-single-line-v1' },
    configuration: { name: 'SVG', packageId: 'svg.demo', packageRevision: BASE, sourceRevision: BASE, exportId: 'svg',
      parametersJson: '{}', relativeTarget: 'application', readScope: 'boundSources', overlayLayer: 'package', kind: 'Effect', bindingKinds: ['project'] } };
}

function fakePackagePort() {
  let server = draft();
  let local = server;
  let revision = 1;
  let sequence = 0;
  const calls: string[] = [];
  const submitted: AuthorPackageFilesInput[] = [];
  const packages = new Map<string, { manifestJson: string; files: typeof initialFiles }>([[BASE, { manifestJson: MANIFEST, files: structuredClone(initialFiles) }]]);
  let target = BASE;
  const compiled = new Set<string>();
  const check = (sent: FullDraft) => { if (sent.draftRevision !== server.draftRevision) throw new BackendError(409, { code: 'revisionConflict', message: 'stale' } as never); };
  const port: DraftSourcePort = {
    current: () => local, savedRevisions: () => [], rememberSaved: () => {},
    refresh: async () => { calls.push('refresh'); local = server; return local; },
    readPackage: async (sent, source) => {
      check(sent); calls.push(`package ${source}`);
      const data = packages.get(source); if (!data) throw new Error('missing');
      return { draftId: 'd1', draftRevision: sent.draftRevision, sourceRevision: source, packageId: 'svg.demo', packageRevision: source,
        manifestJson: data.manifestJson, files: [...data.files.map((file) => ({ ...file, editable: true, sizeBytes: file.text.length, sha256: 'text-hash' })),
          { path: 'images/keep.png', role: 'image', editable: false, sizeBytes: 123, sha256: 'fixed-hash' }], protocolVersion: '1' };
    },
    savePackage: async (sent, input) => {
      check(sent); calls.push(`save ${sent.draftRevision}`); submitted.push(structuredClone(input));
      const source = `sha256:saved-${++sequence}`;
      // A real server reserializes manifest and derives its hashes; it may not echo submitted text.
      packages.set(source, { manifestJson: input.manifestJson + '\n', files: input.textFiles.map((file) => ({ ...file,
        role: initialFiles.find((known) => known.path === file.path)!.role })) });
      server = { ...server, draftRevision: `r${++revision}`, currentSourceRevision: source };
      local = server;
      return server;
    },
    read: async () => { throw new Error('legacy sources must not run'); },
    save: async () => { throw new Error('legacy save must not run'); },
    adopt: async (sent, source) => {
      check(sent); calls.push(`adopt ${source}`);
      if (!compiled.has(source)) throw new Error('compile required');
      server = { ...server, draftRevision: `r${++revision}`, adoptedSourceRevision: source,
        configuration: { ...server.configuration, sourceRevision: source, packageRevision: source } };
    },
    compile: async (_sent, source) => { target = source!; calls.push(`compile ${source}`); return { jobId: 'j1', kind: 'author-compilation', state: 'queued', canCancel: false } as JobResponse; },
    waitJob: async () => ({ jobId: 'j1', kind: 'author-compilation', state: 'succeeded', authorCompilationResult: {}, canCancel: false }) as JobResponse,
    cancelJob: async () => {},
    compilationResult: async () => {
      const failed = packages.get(target)!.files.some((file) => file.text.includes('#missing'));
      if (!failed) compiled.add(target);
      return { draftId: 'd1', draftRevision: local.draftRevision, sourceRevision: target, succeeded: !failed, stale: false,
        artifactHash: failed ? 'none' : 'real-artifact', diagnostics: failed
          ? [{ code: 'PackageSvgReferenceMissing', message: 'missing', isError: true, path: 'assets/main.svg', line: 0, column: 0 }] : [], protocolVersion: '1' };
    },
    lastCompilation: async () => undefined,
  };
  return { port, calls, submitted, touch: () => { server = { ...server, draftRevision: `r${++revision}` }; },
    changeSource: () => { server = { ...server, draftRevision: `r${++revision}`, currentSourceRevision: 'sha256:elsewhere' }; },
    noCSharp: () => { server = { ...server, canCompile: false }; local = server;
      const current = packages.get(BASE)!; packages.set(BASE, { ...current, files: current.files.filter((file) => file.role !== 'csharp') }); },
    current: () => server };
}

describe('完整作者包编辑', () => {
  it('载入 manifest 和所有 role，二进制只读，文件切换保留 dirty 文本与视图', async () => {
    const fake = fakePackagePort(); const model = new DraftSourceModel(fake.port);
    await model.load();
    expect(model.state.packageFiles).toBe(true);
    expect(model.state.files.map((file) => file.role)).toEqual(['manifest', ...initialFiles.map((file) => file.role), 'image']);
    expect(model.state.files[0]!.text).toBe(MANIFEST);
    model.edit('assets/main.svg', SVG + '<!-- local -->');
    model.keepViewState('assets/main.svg', { cursor: 3 });
    model.select('images/keep.png'); model.edit('images/keep.png', 'tamper');
    expect(model.state.files.at(-1)!.text).toBe('');
    model.select('assets/main.svg');
    expect(model.state.files.find((file) => file.path === 'assets/main.svg')!.text).toContain('local');
    expect(model.viewState('assets/main.svg')).toEqual({ cursor: 3 });
  });

  it('保存完整文本集合，规范化 manifest，不采用，并保留保存期间继续输入', async () => {
    const fake = fakePackagePort(); const model = new DraftSourceModel(fake.port); await model.load();
    model.edit('assets/main.svg', '<svg id="saved"/>');
    const saving = model.save(); model.edit('assets/main.svg', '<svg id="newer"/>');
    const result = await saving;
    expect(result.kind).toBe('saved');
    expect(fake.submitted[0]!.manifestJson).toBe(MANIFEST);
    expect(fake.submitted[0]!.textFiles.map((file) => file.path)).toEqual(initialFiles.map((file) => file.path));
    expect(model.state.files.find((file) => file.path === 'assets/main.svg')).toMatchObject({ saved: '<svg id="saved"/>', text: '<svg id="newer"/>' });
    expect(model.state.files[0]!.saved).toBe(MANIFEST + '\n');
    expect(model.dirty).toBe(true);
    expect(fake.current().configuration.sourceRevision).toBe(BASE);
    expect(fake.calls.some((call) => call.startsWith('adopt'))).toBe(false);
  });

  it('配置 CAS 漂移可以重试；别处新源码不能重试覆盖，本地文本和视图完整保留', async () => {
    const fake = fakePackagePort(); const model = new DraftSourceModel(fake.port); await model.load();
    model.edit('assets/main.svg', '<svg/>'); fake.touch();
    expect((await model.save()).kind).toBe('saved');
    expect(fake.calls.filter((call) => call.startsWith('save'))).toEqual(['save r2']);
    model.edit('assets/main.svg', '<svg id="dirty"/>'); model.keepViewState('assets/main.svg', { cursor: 7 }); fake.changeSource();
    expect((await model.save()).kind).toBe('failed');
    expect(fake.submitted).toHaveLength(1);
    expect(model.state.files.find((file) => file.path === 'assets/main.svg')!.text).toContain('dirty');
    expect(model.viewState('assets/main.svg')).toEqual({ cursor: 7 });
    expect(model.state.error).toContain('最新源码已由其他操作改变');
  });

  it('失败资源报告固定 source；零行列只选文件；修复编译后显式采用', async () => {
    const fake = fakePackagePort(); const model = new DraftSourceModel(fake.port); await model.load();
    model.edit('assets/main.svg', '<svg><use href="#missing"/></svg>'); await model.compile();
    const failed = model.state.compilation!;
    expect(failed.succeeded).toBe(false); expect(failed.artifactHash).toBe('none');
    expect(fake.current().configuration.sourceRevision).toBe(BASE);
    expect(compileRows(failed.diagnostics)[0]!.location).toBe('assets/main.svg');
    expect(await model.goTo(failed.sourceRevision, { path: 'assets/main.svg', line: 0, column: 0 })).toBe(true);
    expect(model.state.active).toBe('assets/main.svg'); expect(model.state.reveal).toBeUndefined();
    expect(await model.goTo(failed.sourceRevision, { path: 'resources/main.json.viewport', line: 0, column: 0 })).toBe(true);
    model.edit('assets/main.svg', SVG); await model.compile();
    expect(model.state.compilation!.succeeded).toBe(true);
    expect(fake.current().configuration.sourceRevision).toBe(BASE);
    expect((await model.adopt(model.state.compilation!.sourceRevision)).kind).toBe('adopted');
    expect(fake.current().configuration.sourceRevision).toBe(model.state.compilation!.sourceRevision);
  });

  it('无 C# 包仍能载入和保存，不能伪造编译请求；高亮按 role', async () => {
    const fake = fakePackagePort(); fake.noCSharp(); const model = new DraftSourceModel(fake.port); await model.load();
    model.edit('graph/main.json', '{"changed":true}'); expect((await model.save()).kind).toBe('saved');
    await model.compile(); expect(fake.calls.some((call) => call.startsWith('compile'))).toBe(false);
    expect(model.state.status).toBe('ready');
    expect(['csharp','shader','shaderProgram','graph','svg','svgResource','manifest','future'].map((role) => sourceLanguage({ role })))
      .toEqual(['csharp','shader','json','json','xml','json','json','plain']);
  });

  it('来源组件 metadata 只有精确在用修订匹配时才能驱动参数控件', () => {
    const current = draft();
    const metadata = { packageId: current.packageId, packageRevision: BASE } as EffectPackageInspectResponse;
    expect(exactDraftPackage(current, metadata)).toBe(metadata);
    expect(exactDraftPackage({ ...current, configuration: { ...current.configuration, sourceRevision: 'sha256:new' } }, metadata)).toBeUndefined();
  });
});
