// 精确作者声明读取与完整配置采用的请求形状回归。
import { afterEach, describe, expect, it } from 'vitest';
import { MakoClient, MemoryIdentityStore, staticBootstrapProvider } from '../../src/client';
import type { AuthorPackageConfigurationInput } from '../../src/protocol';
import { createFakeFetch, installFakeBackend, json } from '../support/fake-fetch';

const clients: MakoClient[] = [];
afterEach(() => { for (const client of clients.splice(0)) client.dispose(); });
const ID = `draft-${'a'.repeat(32)}`;
const REV = `sha256:${'1'.repeat(64)}`;
const SOURCE = `sha256:${'2'.repeat(64)}`;

describe('固定作者 metadata 和原子采用请求', () => {
  it('GET 仅带 exact CAS/source 查询且无 body；响应没有可分发 reference', async () => {
    const fake = createFakeFetch(); installFakeBackend(fake);
    const client = await MakoClient.connect(staticBootstrapProvider({ baseUrl: 'http://127.0.0.1:4000', bootstrapToken: 'boot-token' }),
      { fetch: fake.fetch, identityStore: new MemoryIdentityStore(), environment: null }); clients.push(client);
    fake.route('GET', /\/source-inspection$/, () => json(200, { draftId: ID, draftRevision: REV, sourceRevision: SOURCE,
      packageId: 'demo', packageRevision: SOURCE, name: '包', version: '1', packageVersion: 3, sdkVersion: '2.0.0', shaderProfile: 'svg',
      structureValid: true, parameters: [{ id: 'duration', type: 'duration', defaultValueJson: '9223372036854775807', options: [] }],
      exports: [], bindingKinds: [], inputs: [], files: [], diagnostics: [], protocolVersion: '1' }));
    const response = await client.drafts.inspectSource(ID, REV, SOURCE);
    const call = fake.calls.at(-1)!;
    expect([...call.url.searchParams]).toEqual([['expectedDraftRevision', REV], ['sourceRevision', SOURCE]]);
    expect(call.body == null).toBe(true); expect(response).not.toHaveProperty('reference');
    expect(response.parameters[0]?.defaultValueJson).toBe('9223372036854775807');
  });

  it('采用可省略配置保持旧请求，完整配置则逐字携带参数 JSON 与种子', async () => {
    const fake = createFakeFetch(); installFakeBackend(fake);
    const client = await MakoClient.connect(staticBootstrapProvider({ baseUrl: 'http://127.0.0.1:4000', bootstrapToken: 'boot-token' }),
      { fetch: fake.fetch, identityStore: new MemoryIdentityStore(), environment: null }); clients.push(client);
    fake.route('POST', /\/adopt-source-revision$/, () => json(200, {}));
    await client.drafts.adopt(ID, REV, SOURCE);
    expect(fake.calls.at(-1)!.body).not.toHaveProperty('configuration');
    const configuration: AuthorPackageConfigurationInput = { name: '新组件', exportId: 'new', parametersJson: '{"duration":9223372036854775807}',
      relativeTarget: 'application', readScope: 'boundSources', branchInput: null, fixedSeed: '18446744073709551615', overlayLayer: 'package' };
    await client.drafts.adopt(ID, REV, SOURCE, configuration);
    const body = fake.calls.at(-1)!.body as Record<string, unknown>;
    expect(body).toMatchObject({ expectedDraftRevision: REV, sourceRevision: SOURCE, configuration });
    expect(body.configuration).not.toHaveProperty('packageId'); expect(body.configuration).not.toHaveProperty('sourceRevision');
  });
});
