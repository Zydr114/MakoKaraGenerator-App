// W7 完整模板：无损参数 JSON 与新增客户端方法的请求形状。真实往返见集成测试。

import { afterEach, describe, expect, it } from 'vitest';
import { MakoClient, MemoryIdentityStore, staticBootstrapProvider } from '../../src/client';
import {
  compareJsonNumbers,
  isJsonInteger,
  parseObjectEntries,
  sameJsonValue,
  stringifyObjectEntries,
  withEntry,
} from '../../src/editor/model/json-text';
import { revision } from '../../src/protocol';
import { createFakeFetch, installFakeBackend, json, type FakeFetch } from '../support/fake-fetch';

const INT64_MAX = '9223372036854775807';

describe('无损参数 JSON', () => {
  it('拆开顶层保留值原文，Int64 与小数都不变；拼回的文本一致', () => {
    const text = `{"duration":${INT64_MAX},"amplitude":0.10000000000000000001,"color":{"r":1,"g":0.5,"b":0,"a":1},"name":"a\\"b","on":true,"curve":[0,[1,2]]}`;
    const entries = parseObjectEntries(text);
    expect([...entries]).toEqual([
      ['duration', INT64_MAX],
      ['amplitude', '0.10000000000000000001'],
      ['color', '{"r":1,"g":0.5,"b":0,"a":1}'],
      ['name', '"a\\"b"'],
      ['on', 'true'],
      ['curve', '[0,[1,2]]'],
    ]);
    expect(stringifyObjectEntries(entries)).toBe(text);
    expect(parseObjectEntries(' { } ').size).toBe(0);
    expect(parseObjectEntries('{ "a" : 1 , "b" : [ 1 , 2 ] }').get('b')).toBe('[ 1 , 2 ]');
  });

  it('不是对象、语法错误、键重复都拒绝', () => {
    expect(() => parseObjectEntries('[1]')).toThrow();
    expect(() => parseObjectEntries('{"a":}')).toThrow();
    expect(() => parseObjectEntries('{"a":1,"a":2}')).toThrow(/重复/);
  });

  it('改一个键后按键名排序，删除时去掉该键，其余原文不动', () => {
    const text = `{"z":${INT64_MAX},"a":1}`;
    expect(withEntry(text, 'm', '0.4')).toBe(`{"a":1,"m":0.4,"z":${INT64_MAX}}`);
    expect(withEntry(text, 'z', undefined)).toBe('{"a":1}');
  });

  it('数值比较按十进制原文精确进行', () => {
    expect(compareJsonNumbers(INT64_MAX, '9223372036854775806')).toBe(1);
    expect(compareJsonNumbers('9223372036854775808', INT64_MAX)).toBe(1);
    expect(compareJsonNumbers('0.5', '5e-1')).toBe(0);
    expect(compareJsonNumbers('-0', '0')).toBe(0);
    expect(compareJsonNumbers('-2', '-10')).toBe(1);
    expect(compareJsonNumbers('0.0999', '0.1')).toBe(-1);
    expect(compareJsonNumbers('120', '1.2e2')).toBe(0);
    expect(() => compareJsonNumbers('abc', '1')).toThrow();
  });

  it('整数判断与同值判断', () => {
    expect(isJsonInteger(INT64_MAX)).toBe(true);
    expect(isJsonInteger('1.50e2')).toBe(true);
    expect(isJsonInteger('0.5')).toBe(false);
    expect(isJsonInteger('"1"')).toBe(false);
    expect(sameJsonValue('1.0', '1')).toBe(true);
    expect(sameJsonValue('{"r":1,"a":0.5}', '{"a":0.5,"r":1}')).toBe(true);
    expect(sameJsonValue('"x"', '"y"')).toBe(false);
  });
});

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

const METADATA_KEYS = ['hostInstanceId', 'requestId', 'validUntilUtc'];

describe('W7 客户端方法', () => {
  it('完整详情带 definitionFormat=full；试样选项只发 source', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    fake.route('GET', /\/templates\/detail$/, () => json(200, { definitionFormat: 'full' }));
    fake.route('POST', '/api/v1/template-preview-options', () => json(200, { samples: [] }));

    await client.templates.fullDetail('builtin', 'line/经典', 'a'.repeat(64));
    const detail = fake.calls.find((c) => c.path.endsWith('/detail'))!;
    expect(Object.fromEntries(detail.url.searchParams)).toEqual({ expectedRevision: 'a'.repeat(64), templateId: 'line/经典', definitionFormat: 'full' });

    const source = { kind: 'savedTemplate', libraryId: 'builtin', libraryRevision: 'a'.repeat(64), templateId: 't', templateRevision: 'r' } as const;
    await client.templates.previewOptions(source);
    expect(fake.calls.find((c) => c.path === '/api/v1/template-preview-options')!.body).toEqual({ source });
  });

  it('.mktpl 导出、预检、导入都带回执；归档按 exportId 读取', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    fake.route('POST', /\/template-libraries\/personal\/exports$/, () => json(201, { exportId: 'e 1' }));
    fake.route('POST', /\/template-libraries\/personal\/imports\/preflight$/, () => json(201, { preflightId: 'f1' }));
    fake.route('POST', /\/template-libraries\/personal\/imports$/, () => json(200, { importedTemplates: [] }));
    fake.route('GET', /\/template-transfers\/[^/]+\/archive$/, () => new Response(new Uint8Array([80, 75]), { status: 200, headers: { 'Content-Type': 'application/zip' } }));

    await client.templates.exportArchive('personal', 'missing', ['a', 'b']);
    await client.templates.preflightImport('personal', 'asset-1');
    await client.templates.importArchive('personal', 'b'.repeat(64), 'f1');
    const archive = await client.templates.archive('e 1');

    const body = (suffix: string) => fake.calls.find((c) => c.method === 'POST' && c.path.endsWith(suffix))!.body as Record<string, unknown>;
    expect(Object.keys(body('/exports')).sort()).toEqual(['expectedRevision', 'metadata', 'templateIds']);
    expect(body('/exports').templateIds).toEqual(['a', 'b']);
    expect(Object.keys(body('/preflight')).sort()).toEqual(['assetId', 'metadata']);
    expect(Object.keys(body('/imports')).sort()).toEqual(['expectedRevision', 'metadata', 'preflightId']);
    expect(Object.keys(body('/imports').metadata as object).sort()).toEqual(METADATA_KEYS);
    expect(fake.calls.some((c) => c.path === '/api/v1/template-transfers/e%201/archive')).toBe(true);
    expect(archive.blob?.size).toBe(2);
  });

  it('包检查、字体检查原样发送；解析样式把查询展开', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    fake.route('POST', '/api/v1/effect-packages/inspect', () => json(200, { parameters: [] }));
    fake.route('POST', '/api/v1/fonts/inspect', () => json(200, { elements: [] }));
    fake.route('GET', /\/resolved-style$/, () => json(200, { projectSessionId: 'p1', projectRevision: '5' }));

    await client.effectPackages.inspect({ packageAssetId: 'asset-9' });
    expect(fake.calls.find((c) => c.path.endsWith('/inspect') && c.path.includes('effect'))!.body).toEqual({ packageAssetId: 'asset-9' });

    const font = { text: '朝の光', family: 'Noto Sans CJK JP', fontSizePx: 72, fontWeight: 400, italic: false, fallbackFamilies: null, sessionId: null, expectedRevision: null };
    await client.fonts.inspect(font);
    expect(fake.calls.find((c) => c.path === '/api/v1/fonts/inspect')!.body).toEqual(font);

    await client.projects.resolvedStyle('p1', revision('5'), { branchId: 'primary', lineId: 'l1', clipId: 'c1' });
    const call = fake.calls.find((c) => c.path.endsWith('/resolved-style'))!;
    expect(Object.fromEntries(call.url.searchParams)).toEqual({ expectedRevision: '5', branchId: 'primary', lineId: 'l1', clipId: 'c1' });
  });

  it('候选试看可以用 source 代替库引用四字段', async () => {
    const fake = createFakeFetch();
    installFakeBackend(fake);
    const client = await connect(fake);
    fake.route('POST', /\/preset-application-previews$/, () => json(201, { previewId: 'v1' }));
    const source = { kind: 'draft', draftRevision: 'd1', definitionFormat: 'full', definition: { name: 'x' } } as never;
    await client.preview.createPresetCandidate('p1', {
      source,
      expectedProjectRevision: revision('3'),
      targets: [{ kind: 'clips', branchId: 'primary', clipIds: ['c1'] }],
      size: { width: 640, height: 360 },
    });
    const body = fake.calls.find((c) => c.path.endsWith('/preset-application-previews'))!.body as Record<string, unknown>;
    expect(Object.keys(body).sort()).toEqual(['expectedProjectRevision', 'metadata', 'size', 'source', 'targets']);
  });
});
