// W8 客户端方法（合同 1.38.0，规范 §21）：作者草稿、个人配置库与草稿试样的请求形状，用冻结 OpenAPI 的请求 schema 核对。
// 真实往返见集成测试 author.test.ts。

import { afterEach, describe, expect, it } from 'vitest';
import { MakoClient, MemoryIdentityStore, staticBootstrapProvider } from '../../src/client';
import { createFakeFetch, installFakeBackend, json, type FakeFetch } from '../support/fake-fetch';
import { OpenApiContract } from '../support/openapi';

const contract = OpenApiContract.load();
const clients: MakoClient[] = [];
afterEach(() => {
  for (const client of clients.splice(0)) client.dispose();
});

async function connect(fake: FakeFetch) {
  const client = await MakoClient.connect(staticBootstrapProvider({ baseUrl: 'http://127.0.0.1:4000', bootstrapToken: 'boot-token' }), {
    fetch: fake.fetch,
    identityStore: new MemoryIdentityStore(),
    environment: null,
  });
  clients.push(client);
  return client;
}

const DRAFT = `draft-${'a'.repeat(32)}`;
const REV = `sha256:${'1'.repeat(64)}`;
const SRC = `sha256:${'2'.repeat(64)}`;
const LIB = 'f'.repeat(64);

function lastBody(fake: FakeFetch, method: string, pattern: RegExp): unknown {
  const call = fake.calls.filter((c) => c.method === method && pattern.test(c.path)).at(-1);
  expect(call, `${method} ${pattern}`).toBeDefined();
  return call!.body;
}

describe('作者草稿', () => {
  it('新建、配置、源码、编译、验证、采用与删除的请求都符合合同', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    fake.route('POST', /\/api\/v1\/drafts$/, () => json(201, {}));
    fake.route('PUT', /\/configuration$/, () => json(200, {}));
    fake.route('POST', /\/source-revision-jobs$/, () => json(200, {}));
    fake.route('POST', /\/compilation-jobs$/, () => json(202, { jobId: 'job-1', kind: 'author-compilation', state: 'queued' }));
    fake.route('POST', /\/validation-jobs$/, () => json(202, { jobId: 'job-2', kind: 'validation', state: 'queued' }));
    fake.route('POST', /\/adopt-source-revision$/, () => json(200, {}));
    fake.route('DELETE', /\/api\/v1\/drafts\/draft-/, () => json(200, {}));

    await client.drafts.create({ configurationId: 'cfg-1', expectedConfigurationRevision: LIB, exportId: 'word-emphasis', name: '逐字强调' });
    expect(contract.validateNamed('AuthorDraftCreateRequest', lastBody(fake, 'POST', /\/drafts$/))).toEqual([]);
    await client.drafts.create({ packageAssetId: 'asset-1', exportId: 'word-emphasis', name: '逐字强调' });
    expect(contract.validateNamed('AuthorDraftCreateRequest', lastBody(fake, 'POST', /\/drafts$/))).toEqual([]);

    await client.drafts.setConfiguration(DRAFT, REV, {
      name: '逐字强调',
      packageId: 'mako.samples.word-emphasis',
      sourceRevision: SRC,
      exportId: 'word-emphasis',
      parametersJson: '{"peakScale":3}',
      relativeTarget: 'tokenFamily',
      readScope: 'boundSources',
      branchInput: null,
      fixedSeed: '18446744073709551615',
      overlayLayer: 'package',
    });
    const configuration = lastBody(fake, 'PUT', /\/configuration$/);
    expect(contract.validateNamed('AuthorDraftConfigurationRequest', configuration)).toEqual([]);
    // 种子与参数保持原文。
    expect((configuration as { configuration: { fixedSeed: string } }).configuration.fixedSeed).toBe('18446744073709551615');

    fake.route('GET', /\/sources$/, () => json(200, {}));
    await client.drafts.sources(DRAFT, REV, SRC);
    const read = fake.calls.at(-1)!.url.searchParams;
    expect([read.get('expectedDraftRevision'), read.get('sourceRevision')]).toEqual([REV, SRC]);

    await client.drafts.saveSources(DRAFT, REV, [{ path: 'src/WordEmphasis.cs', text: 'class A {}' }]);
    expect(contract.validateNamed('AuthorSourceRevisionRequest', lastBody(fake, 'POST', /\/source-revision-jobs$/))).toEqual([]);

    const job = await client.drafts.compile(DRAFT, REV);
    expect(job.kind).toBe('author-compilation');
    const compile = lastBody(fake, 'POST', /\/compilation-jobs$/);
    expect(contract.validateNamed('AuthorCompilationRequest', compile)).toEqual([]);
    expect((compile as { executionMode: string }).executionMode).toBe('async');
    await client.drafts.compile(DRAFT, REV, SRC);
    const exactCompile = lastBody(fake, 'POST', /\/compilation-jobs$/);
    expect(contract.validateNamed('AuthorCompilationRequest', exactCompile)).toEqual([]);
    expect(exactCompile).toMatchObject({ expectedDraftRevision: REV, sourceRevision: SRC, executionMode: 'async' });

    await client.drafts.validate(DRAFT, REV, { sampleId: 'workshop-single-line-v1', targetKey: 'line', materialSlot: null, renderBackend: 'cpu' });
    expect(contract.validateNamed('AuthorValidationJobRequest', lastBody(fake, 'POST', /\/validation-jobs$/))).toEqual([]);

    await client.drafts.adopt(DRAFT, REV, SRC);
    expect(contract.validateNamed('AuthorAdoptSourceRevisionRequest', lastBody(fake, 'POST', /\/adopt-source-revision$/))).toEqual([]);

    await client.drafts.delete(DRAFT, REV);
    expect(contract.validateNamed('AuthorDraftDeleteRequest', lastBody(fake, 'DELETE', /\/drafts\/draft-/))).toEqual([]);
  });

  it('草稿试样写明 previewKind=authorDraft、committable=false', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    fake.route('POST', /\/preview-sessions$/, () => json(201, { previewId: 'p1' }));
    await client.preview.createAuthorDraft({
      draftId: DRAFT,
      draftRevision: REV,
      authorSourceRevision: SRC,
      sample: { sampleId: 'workshop-multi-line-v1', targetKey: 'line', materialSlot: 'unsungFill' },
      size: { width: 640, height: 360 },
    });
    const body = lastBody(fake, 'POST', /\/preview-sessions$/) as Record<string, unknown>;
    expect(contract.validateNamed('PreviewSessionRequest', body)).toEqual([]);
    expect(body).toMatchObject({ previewKind: 'authorDraft', committable: false, source: { kind: 'authorDraft', authorSourceRevision: SRC } });
  });
});

describe('个人配置库', () => {
  it('详情带精确库修订，命令的三种来源都符合合同', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    fake.route('GET', /\/script-configurations\/cfg-1$/, () => json(200, {}));
    fake.route('POST', /\/script-configurations\/commands$/, () => json(200, {}));

    await client.scriptConfigurations.detail('cfg-1', LIB);
    expect(fake.calls.at(-1)!.url.searchParams.get('expectedRevision')).toBe(LIB);

    const base = {
      name: '逐字强调',
      exportId: 'word-emphasis',
      parametersJson: '{}',
      relativeTarget: 'line' as const,
      readScope: 'boundSources' as const,
      branchInput: null,
      fixedSeed: null,
      overlayLayer: 'package' as const,
    };
    const sources = [
      { package: { kind: 'asset' as const, packageId: 'p', packageRevision: 'r', packageAssetId: 'asset-1' } },
      { draftId: DRAFT, expectedDraftRevision: REV, sourceRevision: SRC },
      { configurationId: 'cfg-1', expectedConfigurationRevision: LIB },
    ];
    for (const source of sources) {
      await client.scriptConfigurations.command('missing', { type: 'create', configuration: { ...base, source } });
      expect(contract.validateNamed('ScriptConfigurationCommandRequest', lastBody(fake, 'POST', /\/commands$/))).toEqual([]);
    }
    await client.scriptConfigurations.command(LIB, { type: 'replace', configurationId: 'cfg-1', configuration: { ...base, source: sources[1]! } });
    await client.scriptConfigurations.command(LIB, { type: 'copy', configurationId: 'cfg-1', name: '副本' });
    await client.scriptConfigurations.command(LIB, { type: 'rename', configurationId: 'cfg-1', name: '新名' });
    await client.scriptConfigurations.command(LIB, { type: 'delete', configurationId: 'cfg-1' });
    const commands = fake.calls.filter((c) => c.path.endsWith('/commands')).slice(-4);
    for (const call of commands) expect(contract.validateNamed('ScriptConfigurationCommandRequest', call.body)).toEqual([]);
  });
});
