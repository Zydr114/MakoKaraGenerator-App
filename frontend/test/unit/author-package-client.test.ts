import { afterEach, describe, expect, it } from 'vitest';
import { MakoClient, MemoryIdentityStore, staticBootstrapProvider } from '../../src/client';
import type { AuthorPackageFilesInput } from '../../src/protocol/author-package-files';
import { createFakeFetch, installFakeBackend, json } from '../support/fake-fetch';
import { OpenApiContract } from '../support/openapi';

const clients: MakoClient[] = [];
afterEach(() => { for (const client of clients.splice(0)) client.dispose(); });

describe('作者完整包文件客户端', () => {
  it('GET 固定两种修订，PUT 保留 manifest/SVG/descriptor 原文与完整文本集合并符合正式 schema', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await MakoClient.connect(staticBootstrapProvider({ baseUrl: 'http://127.0.0.1:4000', bootstrapToken: 'boot-token' }),
      { fetch: fake.fetch, identityStore: new MemoryIdentityStore(), environment: null });
    clients.push(client);
    const draft = `draft-${'a'.repeat(32)}`;
    const revision = `sha256:${'1'.repeat(64)}`;
    const source = `sha256:${'2'.repeat(64)}`;
    fake.route('GET', /\/package-files$/, () => json(200, {}));
    fake.route('PUT', /\/package-files$/, () => json(200, {}));
    await client.drafts.packageFiles(draft, revision, source);
    expect(fake.calls.at(-1)!.url.searchParams.get('expectedDraftRevision')).toBe(revision);
    expect(fake.calls.at(-1)!.url.searchParams.get('sourceRevision')).toBe(source);
    const input: AuthorPackageFilesInput = {
      manifestJson: '{"durationUs":9223372036854775807}',
      textFiles: [{ path: 'assets/main.svg', text: '<svg><path d="M0 0H1"/></svg>' },
        { path: 'resources/main.json', text: '{"svgResourceVersion":1}' }, { path: 'src/A.cs', text: 'class A {}' }],
    };
    await client.drafts.savePackageFiles(draft, revision, input);
    const call = fake.calls.at(-1)!;
    expect(call.method).toBe('PUT');
    expect(call.body).toMatchObject({ expectedDraftRevision: revision, ...input });
    expect(OpenApiContract.load().validateNamed('AuthorPackageFilesRequest', call.body)).toEqual([]);
    expect(call.url.searchParams.has('metadata')).toBe(false);
    expect(call.url.toString()).not.toContain('token');
  });

  it('替换检查是无回执的 POST，保存可带 replacedFiles 与 baseSourceRevision，两者都符合正式 schema', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await MakoClient.connect(staticBootstrapProvider({ baseUrl: 'http://127.0.0.1:4000', bootstrapToken: 'boot-token' }),
      { fetch: fake.fetch, identityStore: new MemoryIdentityStore(), environment: null });
    clients.push(client);
    const draft = `draft-${'a'.repeat(32)}`;
    const revision = `sha256:${'1'.repeat(64)}`;
    const source = `sha256:${'2'.repeat(64)}`;
    const assetId = `asset-${'e'.repeat(32)}`;
    const inspection = { draftId: draft, draftRevision: revision, sourceRevision: source, path: 'assets/spark.png', role: 'image',
      current: { format: 'png', sizeBytes: 120, sha256: 'c'.repeat(64), width: 8, height: 8 },
      replacement: { format: 'png', sizeBytes: 240, sha256: 'd'.repeat(64), width: 16, height: 12 },
      warnings: [{ code: 'imageSizeChanged', message: '尺寸从 8×8 变为 16×12。' }], protocolVersion: '1' };
    expect(OpenApiContract.load().validateNamed('AuthorPackageFileReplacementInspectionResponse', inspection)).toEqual([]);
    fake.route('POST', /\/package-file-replacement-inspection$/, () => json(200, inspection));
    fake.route('PUT', /\/package-files$/, () => json(200, {}));
    const result = await client.drafts.inspectPackageFileReplacement(draft, revision, { sourceRevision: source, path: 'assets/spark.png', assetId });
    expect(result.warnings[0]!.code).toBe('imageSizeChanged');
    const inspect = fake.calls.at(-1)!;
    expect(inspect.method).toBe('POST');
    expect(inspect.url.pathname).toBe(`/api/v1/drafts/${draft}/package-file-replacement-inspection`);
    expect(inspect.body).toEqual({ expectedDraftRevision: revision, sourceRevision: source, path: 'assets/spark.png', assetId });
    expect(OpenApiContract.load().validateNamed('AuthorPackageFileReplacementInspectionRequest', inspect.body)).toEqual([]);

    const input: AuthorPackageFilesInput = { manifestJson: '{}', textFiles: [], baseSourceRevision: source,
      replacedFiles: [{ path: 'assets/spark.png', assetId }] };
    await client.drafts.savePackageFiles(draft, revision, input);
    const save = fake.calls.at(-1)!;
    expect(save.body).toMatchObject({ expectedDraftRevision: revision, ...input });
    expect(OpenApiContract.load().validateNamed('AuthorPackageFilesRequest', save.body)).toEqual([]);
  });
});
