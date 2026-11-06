import { afterEach, describe, expect, it } from 'vitest';
import { MakoClient, MemoryIdentityStore, staticBootstrapProvider } from '../../src/client';
import { createFakeFetch, installFakeBackend, json } from '../support/fake-fetch';
import { OpenApiContract } from '../support/openapi';

const clients: MakoClient[] = [];
afterEach(() => { for (const client of clients.splice(0)) client.dispose(); });

describe('作者修订历史、包下载与草稿认领客户端', () => {
  it('历史分页、下载固定两种修订，认领提交两个发现时的修订并符合正式 schema', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await MakoClient.connect(staticBootstrapProvider({ baseUrl: 'http://127.0.0.1:4000', bootstrapToken: 'boot-token' }),
      { fetch: fake.fetch, identityStore: new MemoryIdentityStore(), environment: null });
    clients.push(client);
    const draft = `draft-${'a'.repeat(32)}`;
    const revision = `sha256:${'1'.repeat(64)}`;
    const source = `sha256:${'2'.repeat(64)}`;

    fake.route('GET', /\/source-revisions$/, () => json(200, { draftId: draft, draftRevision: revision, revisions: [], protocolVersion: '1' }));
    await client.drafts.sourceRevisions(draft, revision);
    expect(fake.calls.at(-1)!.url.searchParams.get('expectedDraftRevision')).toBe(revision);
    expect(fake.calls.at(-1)!.url.searchParams.has('afterSourceRevision')).toBe(false);
    await client.drafts.sourceRevisions(draft, revision, source);
    expect(fake.calls.at(-1)!.url.searchParams.get('afterSourceRevision')).toBe(source);

    fake.route('GET', /\/package$/, () => new Response(new Uint8Array([0x50, 0x4b, 3, 4]), { status: 200, headers: { 'Content-Type': 'application/zip' } }));
    const blob = await client.drafts.downloadPackage(draft, revision, source);
    expect(blob.size).toBe(4);
    expect(fake.calls.at(-1)!.url.searchParams.get('sourceRevision')).toBe(source);

    fake.route('GET', /\/profile\/author-drafts$/, () => json(200, { drafts: [], protocolVersion: '1' }));
    await client.profileDrafts.list();
    expect(fake.calls.at(-1)!.url.search).toBe('');

    fake.route('POST', /\/claim$/, () => json(200, {}));
    const ownership = `sha256:${'3'.repeat(64)}`;
    await client.profileDrafts.claim(draft, revision, ownership);
    const call = fake.calls.at(-1)!;
    expect(call.url.pathname).toBe(`/api/v1/profile/author-drafts/${draft}/claim`);
    expect(call.body).toMatchObject({ expectedDraftRevision: revision, expectedOwnershipRevision: ownership });
    expect(OpenApiContract.load().validateNamed('AuthorDraftClaimRequest', call.body)).toEqual([]);
    expect(call.url.toString()).not.toContain('token');
  });
});
