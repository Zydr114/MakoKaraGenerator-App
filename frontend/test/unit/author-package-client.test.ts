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
});
