// 作者草稿的源码页签（W8 设计 §2.6）：修订标签、读取与没有 C# 的包、保存修订（no-op、提交期间又改、409 重读、在用修订跟上）、改用修订；
// 编译：诊断的排序去重与位置、保存并编译、取消、跳到诊断所在的修订和文件。

import { describe, expect, it } from 'vitest';
import { BackendError } from '../../src/client';
import type { FullDraft } from '../../src/editor/library/author-draft-store';
import { DraftSourceModel, revisionLabelText, revisionRows, type DraftSourcePort } from '../../src/editor/library/draft-source';
import { compileOutcomeText, compileRows, diagnosticRange } from '../../src/editor/model/compile-diagnostics';
import type { AuthorCompilationDiagnostic, AuthorSourceFile, JobResponse } from '../../src/protocol';

const BASE = 'sha256:base';

function draft(patch: Partial<FullDraft> & { inUse?: string } = {}): FullDraft {
  const { inUse, ...rest } = patch;
  return {
    draftId: 'd1',
    draftRevision: 'r1',
    name: '逐字强调',
    packageId: 'demo.fx',
    baseSourceRevision: BASE,
    currentSourceRevision: BASE,
    exportId: 'word',
    canCompile: true,
    canAdopt: true,
    configuration: {
      name: '逐字强调',
      packageId: 'demo.fx',
      packageRevision: BASE,
      sourceRevision: inUse ?? rest.currentSourceRevision ?? BASE,
      exportId: 'word',
      parametersJson: '{}',
      relativeTarget: 'tokenFamily',
      readScope: 'boundSources',
      overlayLayer: 'package',
      kind: 'Effect',
      bindingKinds: ['lines', 'tokens'],
    },
    sampleOptions: { samples: [], materialSlots: [], defaultSampleId: 'workshop-single-line-v1' },
    protocolVersion: '1',
    ...rest,
  };
}

const conflict = () => new BackendError(409, { code: 'revisionConflict', message: '草稿修订已过期' } as never);
const notFound = () => new BackendError(404, { code: 'resourceNotFound', message: '源码修订不包含 C# 文件' } as never);

/** 假后端：按修订存文件，保存按内容算修订（和最新修订相同是 no-op）；在用修订原来就是最新修订时才跟着换（§0）。 */
function fakePort(options: { files?: Record<string, AuthorSourceFile[]>; stale?: number; diagnostics?: AuthorCompilationDiagnostic[]; jobState?: JobResponse['state'] } = {}) {
  const sources: Record<string, AuthorSourceFile[]> = options.files ?? { [BASE]: [{ path: 'src/A.cs', text: 'class A {}' }] };
  let server = draft();
  let stale = options.stale ?? 0;
  let revisions = 1;
  const calls: string[] = [];
  const remembered: string[][] = [];
  let local = server;
  const check = (sent: FullDraft) => {
    if (stale > 0 || sent.draftRevision !== server.draftRevision) {
      stale = Math.max(0, stale - 1);
      throw conflict();
    }
  };
  const bump = (patch: Partial<FullDraft> & { inUse?: string }) => {
    const { inUse, ...rest } = patch;
    server = { ...server, ...rest, draftRevision: `r${++revisions}`, configuration: { ...server.configuration, sourceRevision: inUse ?? server.configuration.sourceRevision } };
  };
  const port: DraftSourcePort = {
    current: () => local,
    read: async (sent, revision) => {
      calls.push(`read ${sent.draftRevision} ${revision}`);
      check(sent);
      const files = sources[revision];
      if (!files) throw notFound();
      return { draftId: 'd1', draftRevision: sent.draftRevision, sourceRevision: revision, packageId: 'demo.fx', packageRevision: revision, files, protocolVersion: '1' };
    },
    save: async (sent, files) => {
      calls.push(`save ${sent.draftRevision}`);
      check(sent);
      // 内容和最新修订相同是 no-op：草稿修订不变。
      if (JSON.stringify(sources[server.currentSourceRevision]) === JSON.stringify(files))
        return { draftId: 'd1', draftRevision: server.draftRevision, packageId: 'demo.fx', sourceRevision: server.currentSourceRevision, sourcePaths: [], protocolVersion: '1' };
      const revision = `sha256:${files.map((f) => f.text).join('|')}`;
      sources[revision] = files;
      const follow = server.configuration.sourceRevision === server.currentSourceRevision;
      bump({ currentSourceRevision: revision, ...(follow ? { inUse: revision } : {}) });
      return { draftId: 'd1', draftRevision: server.draftRevision, packageId: 'demo.fx', sourceRevision: revision, sourcePaths: files.map((f) => f.path), protocolVersion: '1' };
    },
    adopt: async (sent, revision) => {
      calls.push(`adopt ${sent.draftRevision} ${revision}`);
      check(sent);
      bump({ adoptedSourceRevision: revision, inUse: revision });
      return {};
    },
    use: async (sent, revision) => {
      calls.push(`use ${sent.draftRevision} ${revision}`);
      check(sent);
      bump({ inUse: revision });
      local = server;
      return local;
    },
    refresh: async () => {
      calls.push('refresh');
      local = server;
      return local;
    },
    savedRevisions: () => [],
    rememberSaved: (list) => void remembered.push(list),
    compile: async (sent) => {
      calls.push(`compile ${sent.draftRevision} ${server.currentSourceRevision}`);
      check(sent);
      return { jobId: 'job-1', kind: 'author-compilation', state: 'queued', canCancel: true } as JobResponse;
    },
    waitJob: async (jobId) => {
      const state = options.jobState ?? 'succeeded';
      return { jobId, kind: 'author-compilation', state, canCancel: false, ...(state === 'succeeded' ? { authorCompilationResult: {} } : {}) } as JobResponse;
    },
    cancelJob: async () => calls.push('cancel'),
    compilationResult: async () => {
      const diagnostics = options.diagnostics ?? [];
      return { draftId: 'd1', draftRevision: server.draftRevision, sourceRevision: server.currentSourceRevision, succeeded: !diagnostics.some((d) => d.isError), stale: false, artifactHash: 'h', diagnostics, protocolVersion: '1' };
    },
    lastCompilation: async () => undefined,
  };
  return {
    port,
    calls,
    remembered,
    /** 别处改了草稿（例如配置页签提交），列表里的修订过期了。 */
    touch: () => bump({}),
  };
}

describe('修订列表', () => {
  it('最新在前、原包在后；同一修订合成一行，“本页保存”只标在没有别的身份的行上', () => {
    const d = draft({ currentSourceRevision: 'sha256:c', adoptedSourceRevision: BASE, inUse: 'sha256:b' });
    const rows = revisionRows(d, ['sha256:a', 'sha256:b', 'sha256:c']);
    expect(rows.map((r) => [r.revision, revisionLabelText(r.labels)])).toEqual([
      ['sha256:c', '最新'],
      ['sha256:b', '本页保存 · 在用'],
      ['sha256:a', '本页保存'],
      [BASE, '原包 · 已采用'],
    ]);
    // 刚导入：只有一行。
    expect(revisionRows(draft(), []).map((r) => revisionLabelText(r.labels))).toEqual(['原包 · 最新 · 在用']);
  });
});

describe('源码页签', () => {
  it('打开时读在用修订；在用修订没有 C# 文件（材质、图）时是 none', async () => {
    const fake = fakePort();
    const model = new DraftSourceModel(fake.port);
    expect(await model.load()).toBe(true);
    expect(model.state).toMatchObject({ status: 'ready', revision: BASE, active: 'src/A.cs', generation: 1 });

    const material = new DraftSourceModel(fakePort({ files: {} }).port);
    await material.load();
    expect(material.state.status).toBe('none');
  });

  it('保存修订：提交全部文件，记下本页保存并重读草稿；提交期间又改的文件仍算没保存；没改动时不发请求', async () => {
    const fake = fakePort({ files: { [BASE]: [{ path: 'src/A.cs', text: 'a' }, { path: 'src/B.cs', text: 'b' }] } });
    const model = new DraftSourceModel(fake.port);
    await model.load();
    expect(await model.save()).toEqual({ kind: 'unchanged' });
    expect(fake.calls).toEqual([`read r1 ${BASE}`]);

    model.edit('src/A.cs', 'a2');
    expect(model.dirty).toBe(true);
    const saving = model.save();
    model.edit('src/A.cs', 'a3');
    expect(await saving).toEqual({ kind: 'saved', revision: 'sha256:a2|b', created: true });
    expect(fake.calls.slice(1)).toEqual(['save r1', 'refresh']);
    expect(fake.remembered).toEqual([['sha256:a2|b']]);
    expect(model.state.revision).toBe('sha256:a2|b');
    expect(fake.port.current()!.configuration.sourceRevision).toBe('sha256:a2|b');
    // a3 是保存之后打的，仍然没保存。
    expect(model.state.files.map((f) => [f.text, f.saved])).toEqual([
      ['a3', 'a2'],
      ['b', 'b'],
    ]);

    // 改回已保存的内容就不算改动。
    model.edit('src/A.cs', 'a2');
    expect(model.dirty).toBe(false);
  });

  it('在旧修订上改成和最新修订相同的内容：保存是 no-op，不重读草稿也不记修订', async () => {
    const fake = fakePort({ files: { [BASE]: [{ path: 'src/A.cs', text: 'a' }], 'sha256:old': [{ path: 'src/A.cs', text: 'old' }] } });
    const model = new DraftSourceModel(fake.port);
    await model.load('sha256:old');
    model.edit('src/A.cs', 'a');
    expect(await model.save()).toEqual({ kind: 'unchanged' });
    expect(fake.remembered).toEqual([]);
    expect(fake.calls).not.toContain('refresh');
    expect(model.state.revision).toBe(BASE);
    expect(model.dirty).toBe(false);
  });

  it('草稿修订过期（409）时重读一次再发', async () => {
    const fake = fakePort();
    const model = new DraftSourceModel(fake.port);
    await model.load();
    fake.touch();
    model.edit('src/A.cs', 'class B {}');
    expect((await model.save()).kind).toBe('saved');
    expect(fake.calls.slice(1)).toEqual(['save r1', 'refresh', 'save r2', 'refresh']);
  });

  it('查看别的修订换掉编辑器内容（代次加一）；读不到时保留原内容并写明原因', async () => {
    const fake = fakePort({ files: { [BASE]: [{ path: 'src/A.cs', text: 'base' }], 'sha256:old': [{ path: 'src/A.cs', text: 'old' }] } });
    const model = new DraftSourceModel(fake.port);
    await model.load();
    model.keepViewState('src/A.cs', { cursor: 3 });
    await model.load('sha256:old');
    expect(model.state).toMatchObject({ revision: 'sha256:old', generation: 2 });
    expect(model.state.files[0]!.text).toBe('old');
    expect(model.viewState('src/A.cs')).toBeUndefined();

    await model.load('sha256:gone');
    expect(model.state).toMatchObject({ status: 'ready', revision: 'sha256:old', error: expect.stringMatching(/^读取修订失败/) });
  });

  it('在用修订不是最新修订时（采用了原包）保存：后端不换在用修订，前端再提交配置换过来，采用标记不变', async () => {
    const fake = fakePort({ files: { [BASE]: [{ path: 'src/A.cs', text: 'base' }] } });
    const model = new DraftSourceModel(fake.port);
    await model.load();
    model.edit('src/A.cs', 'v2');
    await model.save();
    await model.adopt(BASE);
    model.edit('src/A.cs', 'v3');
    expect(await model.save()).toEqual({ kind: 'saved', revision: 'sha256:v3', created: true });
    expect(fake.calls.slice(-3)).toEqual(['save r3', 'refresh', 'use r4 sha256:v3']);
    expect(fake.port.current()).toMatchObject({ adoptedSourceRevision: BASE, currentSourceRevision: 'sha256:v3', configuration: { sourceRevision: 'sha256:v3' } });

    // 再改用原包，把内容改成和最新修订相同：不新建修订，但试样改用它。
    await model.adopt(BASE);
    model.edit('src/A.cs', 'v3');
    expect(await model.save()).toEqual({ kind: 'saved', revision: 'sha256:v3', created: false });
    expect(fake.port.current()!.configuration.sourceRevision).toBe('sha256:v3');
  });

  it('改用修订：在用修订换掉后，编辑器没有改动就跟着读进它；有改动时不动编辑器', async () => {
    const fake = fakePort({ files: { [BASE]: [{ path: 'src/A.cs', text: 'base' }] } });
    const model = new DraftSourceModel(fake.port);
    await model.load();
    model.edit('src/A.cs', 'v2');
    await model.save();
    expect(model.state.revision).toBe('sha256:v2');

    expect(await model.adopt(BASE)).toEqual({ kind: 'adopted', revision: BASE });
    expect(fake.port.current()).toMatchObject({ adoptedSourceRevision: BASE, configuration: { sourceRevision: BASE } });
    expect(model.state).toMatchObject({ revision: BASE });
    expect(model.state.files[0]!.text).toBe('base');

    model.edit('src/A.cs', 'draft edit');
    await model.adopt('sha256:v2');
    expect(model.state.revision).toBe(BASE);
    expect(model.state.files[0]!.text).toBe('draft edit');
  });
});

describe('编译诊断', () => {
  const d = (patch: Partial<AuthorCompilationDiagnostic>): AuthorCompilationDiagnostic => ({ code: 'CS0103', message: 'm', isError: true, path: 'src/A.cs', line: 1, column: 1, ...patch });

  it('错误在前，再按路径、行、列；完全相同的只留一条；摘要数错误和警告', () => {
    const rows = compileRows([d({ isError: false, code: 'CS0168', line: 2 }), d({ line: 9 }), d({ line: 3, column: 5 }), d({ line: 3, column: 5 }), { code: 'PackageCSharpMissing', message: 'm', isError: true, line: 0, column: 0 }]);
    expect(rows.map((r) => `${r.code} ${r.location}`)).toEqual(['PackageCSharpMissing ', 'CS0103 src/A.cs:3:5', 'CS0103 src/A.cs:9:1', 'CS0168 src/A.cs:2:1']);
    expect(compileOutcomeText({ succeeded: false, diagnostics: [d({}), d({}), d({ line: 2 }), d({ isError: false })] })).toBe('失败 · 2 个错误、1 个警告');
    expect(compileOutcomeText({ succeeded: true, diagnostics: [] })).toBe('通过');
  });

  it('位置：从那一列标到标识符结尾；行尾往前标一个字符；越界夹到文本里', () => {
    const text = 'class A\n{\n    peak = contxt.Parameters;\n}';
    const at = (line: number, column: number) => {
      const r = diagnosticRange(text, line, column);
      return text.slice(r.from, r.to);
    };
    expect(at(3, 12)).toBe('contxt');
    expect(at(3, 18)).toBe('.');
    expect(at(1, 99)).toBe('A');
    expect(at(99, 1)).toBe('}');
  });
});

describe('编译', () => {
  it('编辑器有改动时先保存再编译最新修订，结果留下；没改动时直接编译', async () => {
    const fake = fakePort({ diagnostics: [{ code: 'CS0103', message: "The name 'contxt' does not exist", isError: true, path: 'src/A.cs', line: 1, column: 1 }] });
    const model = new DraftSourceModel(fake.port);
    await model.load();
    model.edit('src/A.cs', 'contxt');
    expect(await model.compile()).toEqual({ kind: 'saved', revision: 'sha256:contxt', created: true });
    expect(fake.calls.slice(1)).toEqual(['save r1', 'refresh', 'compile r2 sha256:contxt', 'refresh']);
    expect(model.state.compilation).toMatchObject({ sourceRevision: 'sha256:contxt', succeeded: false });
    expect(model.state.compiling).toBeUndefined();

    expect(await model.compile()).toBeUndefined();
    expect(fake.calls.at(-2)).toBe('compile r2 sha256:contxt');
  });

  it('任务被取消时写明，不留结果', async () => {
    const model = new DraftSourceModel(fakePort({ jobState: 'cancelled' }).port);
    await model.load();
    await model.compile();
    expect(model.state).toMatchObject({ compileError: '已取消编译。' });
    expect(model.state.compilation).toBeUndefined();
  });

  it('跳到诊断：编辑器里不是编译的修订时先读进来，再切文件、发出跳转；文件不在时返回 false', async () => {
    const fake = fakePort({
      files: {
        [BASE]: [{ path: 'src/A.cs', text: 'a' }],
        'sha256:new': [
          { path: 'src/A.cs', text: 'a' },
          { path: 'src/B.cs', text: 'b' },
        ],
      },
    });
    const model = new DraftSourceModel(fake.port);
    await model.load();
    expect(await model.goTo('sha256:new', { path: 'src/B.cs', line: 3, column: 7 })).toBe(true);
    expect(model.state).toMatchObject({ revision: 'sha256:new', active: 'src/B.cs', reveal: { path: 'src/B.cs', line: 3, column: 7, nonce: 1 } });
    // 编辑器跳过之后清掉（切回页签不再跳）；序号接着递增，同一个编辑器还认得下一次跳转。
    model.revealed(1);
    expect(model.state.reveal).toBeUndefined();
    await model.goTo('sha256:new', { path: 'src/B.cs', line: 3, column: 7 });
    expect(model.state.reveal?.nonce).toBe(2);
    expect(fake.calls.filter((c) => c.startsWith('read'))).toHaveLength(2);
    expect(await model.goTo('sha256:new', { path: 'src/C.cs', line: 1, column: 1 })).toBe(false);
    // 不给位置（运行时异常没有行号）：只读进修订。
    expect(await model.goTo(BASE)).toBe(true);
    expect(model.state.revision).toBe(BASE);
  });
});
