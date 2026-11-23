// 作者草稿整份替换包内图片与字体（合同 1.49.0）的真实 Server 链路：检查、拒绝、保存、下载回读。
// 界面交互由单元测试和浏览器冒烟覆盖，这里只确认服务的真实行为与合同一致。
import { createHash } from 'node:crypto';
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { packSampleEffect, sampleEffectsDir, zipFiles } from '../../scripts/effect-packages.mjs';
import { BackendError, MakoClient, MemoryIdentityStore } from '../../src/client';
import type { AuthorDraft } from '../../src/protocol';
import { ContractRecorder, startTestBackend, type TestBackend } from '../support/backend';
import { makePng } from '../support/fixtures';

const samples = sampleEffectsDir();
if (!samples) console.warn('[skipped] 找不到 tests/Fixtures/ScriptEffectsV2，跳过替换素材集成测试。');
const SERIF = '/usr/share/fonts/liberation/LiberationSerif-Regular.ttf';
const MONO = '/usr/share/fonts/liberation/LiberationMono-Regular.ttf';
const OTF = '/usr/share/fonts/gsfonts/NimbusMonoPS-Regular.otf';
const fonts = [SERIF, MONO, OTF].every(existsSync);
const IMAGE = 'assets/spark.png';
const FONT = 'fonts/a.ttf';
// 1×1 GIF：能解码，但和原来的 PNG 不是同一格式。
const GIF = new Uint8Array([0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 1, 0, 1, 0, 0x80, 0, 0, 0, 0, 0, 0xff, 0xff, 0xff,
  0x21, 0xf9, 4, 1, 0, 0, 0, 0, 0x2c, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0x44, 1, 0, 0x3b]);

let backend: TestBackend;
let client: MakoClient;
const drafts: AuthorDraft[] = [];
const recorder = new ContractRecorder();

const sha = (data: Uint8Array) => createHash('sha256').update(data).digest('hex');
const upload = async (data: Uint8Array, fileName: string) => (await client.assets.upload(new Blob([new Uint8Array(data)]), { fileName })).assetId;
async function rejected(promise: Promise<unknown>): Promise<BackendError> {
  try {
    await promise;
  } catch (error) {
    if (error instanceof BackendError) return error;
    throw error;
  }
  throw new Error('请求应当被拒绝');
}

/** WordEmphasis 多一个字体文件 fonts/a.ttf；manifest 按文本改，避免 Int64 经过 JSON.parse 丢精度。 */
function packFontEffect(): Uint8Array {
  const dir = join(samples!, 'WordEmphasis');
  const list = (path: string): string[] => statSync(path).isDirectory() ? readdirSync(path).sort().flatMap((entry) => list(join(path, entry))) : [path];
  const files = list(dir).map((path) => ({ name: relative(dir, path).split(sep).join('/'), data: readFileSync(path) }))
    .filter((file) => file.name !== 'manifest.json');
  const font = readFileSync(SERIF);
  files.push({ name: FONT, data: font });
  const manifest = readFileSync(join(dir, 'manifest.json'), 'utf8')
    .replace('"packageId": "makokara.v2.word-emphasis"', '"packageId": "makokara.v2.word-emphasis-font"')
    .replace('"files": [', `"files": [\n    { "path": "${FONT}", "role": "font", "sha256": "${sha(font)}" },`);
  if (!manifest.includes(FONT)) throw new Error('WordEmphasis/manifest.json 结构变了');
  return zipFiles([{ name: 'manifest.json', data: Buffer.from(manifest, 'utf8') }, ...files]);
}

async function createDraft(data: Uint8Array, fileName: string, exportId: string): Promise<AuthorDraft> {
  const asset = await upload(data, fileName);
  const draft = await client.drafts.create({ packageAssetId: asset, exportId, name: `替换素材-${Date.now().toString(36)}` });
  drafts.push(draft);
  return draft;
}

beforeAll(async () => {
  if (!samples) return;
  backend = await startTestBackend('author-replacement');
  client = await MakoClient.connect(backend.provider, { identityStore: new MemoryIdentityStore(), environment: null, onExchange: recorder.observer, jobPollIntervalMs: 1000 });
});

afterAll(async () => {
  try {
    for (const draft of drafts) {
      const current = await client.drafts.get(draft.draftId);
      await client.drafts.delete(current.draftId, current.draftRevision);
    }
  } finally {
    client?.dispose();
    await backend?.stop();
  }
});

describe.skipIf(!samples)('作者草稿替换包内素材（真实 Server）', () => {
  it('图片：检查给出尺寸变化，格式不同被拒，保存后清单摘要更新，下载的包读回新字节', async () => {
    let draft = await createDraft(packSampleEffect('SpriteTrail'), 'SpriteTrail.mkfx', 'sprite-trail');
    const capabilities = await client.capabilities();
    expect(capabilities.features).toContain('author.drafts.package-files.replace');
    const before = await client.drafts.packageFiles(draft.draftId, draft.draftRevision, draft.currentSourceRevision);
    const original = before.files.find((file) => file.path === IMAGE)!;
    expect(original).toMatchObject({ role: 'image', editable: false });

    const bigger = makePng(32, 24, [255, 200, 0, 255]);
    const assetId = await upload(bigger, 'bigger.png');
    const inspection = await client.drafts.inspectPackageFileReplacement(draft.draftId, draft.draftRevision,
      { sourceRevision: before.sourceRevision, path: IMAGE, assetId });
    expect(inspection).toMatchObject({ draftId: draft.draftId, draftRevision: draft.draftRevision, sourceRevision: before.sourceRevision,
      path: IMAGE, role: 'image', current: { format: 'png', sha256: original.sha256, width: 16, height: 16 },
      replacement: { format: 'png', sha256: sha(bigger), sizeBytes: bigger.length, width: 32, height: 24 } });
    expect(inspection.warnings.map((warning) => warning.code)).toEqual(['imageSizeChanged']);

    const gif = await upload(GIF, 'dot.gif');
    const mismatch = await rejected(client.drafts.inspectPackageFileReplacement(draft.draftId, draft.draftRevision,
      { sourceRevision: before.sourceRevision, path: IMAGE, assetId: gif }));
    expect([mismatch.status, mismatch.failureCode, mismatch.field]).toEqual([400, 'AuthorReplacementFormatMismatch', 'assetId']);
    const target = await rejected(client.drafts.inspectPackageFileReplacement(draft.draftId, draft.draftRevision,
      { sourceRevision: before.sourceRevision, path: 'README.md', assetId }));
    expect([target.failureCode, target.field]).toEqual(['AuthorReplacementTargetInvalid', 'path']);

    const textFiles = before.files.filter((file) => file.editable).map((file) => ({ path: file.path, text: file.text! }));
    const save = (replacedFiles: { path: string; assetId: string }[]) => client.drafts.savePackageFiles(draft.draftId, draft.draftRevision,
      { manifestJson: before.manifestJson, textFiles, replacedFiles, baseSourceRevision: before.sourceRevision });
    const refused = await rejected(save([{ path: IMAGE, assetId: gif }]));
    expect([refused.failureCode, refused.field]).toEqual(['AuthorReplacementFormatMismatch', 'replacedFiles[0].assetId']);
    expect((await client.drafts.get(draft.draftId)).draftRevision).toBe(draft.draftRevision);

    draft = await save([{ path: IMAGE, assetId }]);
    expect(draft.currentSourceRevision).not.toBe(before.sourceRevision);
    const after = await client.drafts.packageFiles(draft.draftId, draft.draftRevision, draft.currentSourceRevision);
    expect(after.files.find((file) => file.path === IMAGE)).toMatchObject({ sha256: sha(bigger), sizeBytes: bigger.length });
    expect(after.manifestJson).toContain(sha(bigger));
    expect(after.manifestJson).not.toContain(original.sha256);

    const downloaded = new Uint8Array(await (await client.drafts.downloadPackage(draft.draftId, draft.draftRevision, draft.currentSourceRevision)).arrayBuffer());
    const reread = await client.effectPackages.inspect({ packageAssetId: await upload(downloaded, 'replaced.mkfx') });
    expect(reread.structureValid).toBe(true);
    expect(reread.files.find((file) => file.path === IMAGE)).toMatchObject({ sha256: sha(bigger), size: bigger.length });
    expect(recorder.issues).toEqual([]);
  }, 120_000);

  it.skipIf(!fonts)('字体：TrueType 换 TrueType 只提醒家族名变了，换成 OpenType 被拒', async () => {
    let draft = await createDraft(packFontEffect(), 'WordEmphasisFont.mkfx', 'word-emphasis');
    const before = await client.drafts.packageFiles(draft.draftId, draft.draftRevision, draft.currentSourceRevision);
    expect(before.files.find((file) => file.path === FONT)).toMatchObject({ role: 'font', editable: false });

    const mono = readFileSync(MONO);
    const assetId = await upload(mono, 'LiberationMono-Regular.ttf');
    const inspection = await client.drafts.inspectPackageFileReplacement(draft.draftId, draft.draftRevision,
      { sourceRevision: before.sourceRevision, path: FONT, assetId });
    expect(inspection).toMatchObject({ role: 'font', current: { format: 'truetype', familyNames: ['Liberation Serif'] },
      replacement: { format: 'truetype', familyNames: ['Liberation Mono'], sha256: sha(mono) } });
    expect(inspection.warnings.map((warning) => warning.code)).toEqual(['fontFamilyChanged']);

    const otf = await upload(readFileSync(OTF), 'NimbusMonoPS-Regular.otf');
    const mismatch = await rejected(client.drafts.inspectPackageFileReplacement(draft.draftId, draft.draftRevision,
      { sourceRevision: before.sourceRevision, path: FONT, assetId: otf }));
    expect([mismatch.failureCode, mismatch.field]).toEqual(['AuthorReplacementFormatMismatch', 'assetId']);

    const textFiles = before.files.filter((file) => file.editable).map((file) => ({ path: file.path, text: file.text! }));
    draft = await client.drafts.savePackageFiles(draft.draftId, draft.draftRevision,
      { manifestJson: before.manifestJson, textFiles, replacedFiles: [{ path: FONT, assetId }] });
    const after = await client.drafts.packageFiles(draft.draftId, draft.draftRevision, draft.currentSourceRevision);
    expect(after.files.find((file) => file.path === FONT)?.sha256).toBe(sha(mono));
    expect(recorder.issues).toEqual([]);
  }, 120_000);
});
