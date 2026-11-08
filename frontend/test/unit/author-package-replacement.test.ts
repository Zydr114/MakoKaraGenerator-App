import { describe, expect, it } from 'vitest';
import { BackendError } from '../../src/client';
import type { JobResponse } from '../../src/protocol';
import type { AuthorPackageFileReplacementInspection, AuthorPackageFilesInput } from '../../src/protocol/author-package-files';
import type { FullDraft } from '../../src/editor/library/author-draft-store';
import { DraftSourceModel, type DraftSourcePort } from '../../src/editor/library/draft-source';

const BASE = 'sha256:base';
const IMAGE = 'images/spark.png';
const SVG_PATH = 'assets/main.svg';

function draft(): FullDraft {
  return { draftId: 'd1', draftRevision: 'r1', packageId: 'demo', name: 'Demo', exportId: 'main',
    packageVersion: 3, sdkVersion: '2.0.0', baseSourceRevision: BASE, currentSourceRevision: BASE,
    canCompile: false, canAdopt: true, protocolVersion: '1', sampleOptions: { samples: [], materialSlots: [], defaultSampleId: 'workshop-single-line-v1' },
    configuration: { name: 'Demo', packageId: 'demo', packageRevision: BASE, sourceRevision: BASE, exportId: 'main',
      parametersJson: '{}', relativeTarget: 'application', readScope: 'boundSources', overlayLayer: 'package', kind: 'Effect', bindingKinds: ['project'] } };
}

function deferred(): { promise: Promise<void>; resolve: () => void } {
  let resolve!: () => void;
  return { promise: new Promise<void>((done) => { resolve = done; }), resolve };
}

/** 假后端：每个修订记 SVG 文本和图片摘要；上传按文件名决定检查结果。 */
function fakePort() {
  let server = draft();
  let revision = 1;
  let sequence = 0;
  const packages = new Map<string, { svg: string; imageSha: string }>([[BASE, { svg: '<svg/>', imageSha: 'base-sha' }]]);
  const uploads = new Map<string, string>();
  const submitted: AuthorPackageFilesInput[] = [];
  const inspected: string[] = [];
  let gate: Promise<void> | undefined;
  const port: DraftSourcePort = {
    current: () => server, isV2: () => true, savedRevisions: () => [], rememberSaved: () => {},
    refresh: async () => server,
    readPackage: async (sent, source) => {
      const data = packages.get(source);
      if (!data) throw new Error('missing');
      return { draftId: 'd1', draftRevision: sent.draftRevision, sourceRevision: source, packageId: 'demo', packageRevision: source,
        manifestJson: '{"packageId":"demo"}', protocolVersion: '1', files: [
          { path: SVG_PATH, role: 'svg', editable: true, text: data.svg, sizeBytes: data.svg.length, sha256: 'svg-sha' },
          { path: IMAGE, role: 'image', editable: false, sizeBytes: 120, sha256: data.imageSha },
          { path: 'video/clip.mp4', role: 'asset', editable: false, sizeBytes: 999, sha256: 'video-sha' },
        ] };
    },
    savePackage: async (_sent, input) => {
      submitted.push(structuredClone(input));
      const base = packages.get(input.baseSourceRevision ?? server.currentSourceRevision)!;
      const replaced = input.replacedFiles?.find((file) => file.path === IMAGE);
      const source = `sha256:saved-${++sequence}`;
      packages.set(source, { svg: input.textFiles.find((file) => file.path === SVG_PATH)!.text,
        imageSha: replaced ? `sha-of-${replaced.assetId}` : base.imageSha });
      server = { ...server, draftRevision: `r${++revision}`, currentSourceRevision: source };
      return server;
    },
    uploadAsset: async (_file, fileName) => {
      const assetId = `asset-${uploads.size + 1}`;
      uploads.set(assetId, fileName);
      return assetId;
    },
    inspectReplacement: async (sent, input): Promise<AuthorPackageFileReplacementInspection> => {
      inspected.push(`${input.sourceRevision} ${input.path} ${input.assetId}`);
      if (gate) await gate;
      const name = uploads.get(input.assetId)!;
      if (name.endsWith('.jpg'))
        throw new BackendError(400, { code: 'invalidRequest', failureCode: 'AuthorReplacementFormatMismatch', field: 'assetId',
          message: '新文件是 JPEG 图片，原文件是 PNG 图片；只能换成同一格式的文件。', protocolVersion: '1' } as never);
      return { draftId: 'd1', draftRevision: sent.draftRevision, sourceRevision: input.sourceRevision, path: input.path, role: 'image',
        current: { format: 'png', sizeBytes: 120, sha256: 'b'.repeat(64), width: 8, height: 8 },
        replacement: { format: 'png', sizeBytes: 240, sha256: 'c'.repeat(64), width: 16, height: 12 },
        warnings: [{ code: 'imageSizeChanged', message: '尺寸从 8×8 变为 16×12。' }], protocolVersion: '1' };
    },
    read: async () => { throw new Error('legacy sources must not run'); },
    save: async () => { throw new Error('legacy save must not run'); },
    adopt: async () => {},
    use: async () => { throw new Error('package save must not auto-use'); },
    compile: async () => ({ jobId: 'j1', kind: 'author-compilation', state: 'queued', canCancel: false }) as JobResponse,
    waitJob: async () => ({ jobId: 'j1', kind: 'author-compilation', state: 'succeeded', canCancel: false }) as JobResponse,
    cancelJob: async () => {},
    compilationResult: async () => { throw new Error('no compile'); },
    lastCompilation: async () => undefined,
  };
  return { port, submitted, inspected, packages, hold: () => { const d = deferred(); gate = d.promise; return () => { gate = undefined; d.resolve(); }; } };
}

const png = () => new Blob([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], { type: 'image/png' });

describe('作者草稿替换包内素材', () => {
  it('只有图片、字体可整份替换，SVG 走文本，其余二进制和 manifest 不行', async () => {
    const fake = fakePort(); const model = new DraftSourceModel(fake.port); await model.load();
    const kind = (path: string) => model.replaceKind(model.state.files.find((file) => file.path === path)!);
    expect([kind('manifest.json'), kind(SVG_PATH), kind(IMAGE), kind('video/clip.mp4')]).toEqual([undefined, 'svg', 'binary', undefined]);
    expect(model.replaceKind({ role: 'font', editable: false })).toBe('binary');
  });

  it('图片上传并检查后待保存，保存提交 replacedFiles 与所看修订，新修订读回后清掉待替换', async () => {
    const fake = fakePort(); const model = new DraftSourceModel(fake.port); await model.load();
    expect(await model.replaceFile(IMAGE, png(), 'bigger.png')).toBe(true);
    expect(fake.inspected).toEqual([`${BASE} ${IMAGE} asset-1`]);
    const pending = model.state.files.find((file) => file.path === IMAGE)!.replacement!;
    expect(pending).toMatchObject({ fileName: 'bigger.png', assetId: 'asset-1' });
    expect(pending.inspection.warnings.map((warning) => warning.code)).toEqual(['imageSizeChanged']);
    expect(model.dirty).toBe(true);

    const result = await model.save();
    expect(result.kind).toBe('saved');
    expect(fake.submitted[0]).toMatchObject({ baseSourceRevision: BASE, replacedFiles: [{ path: IMAGE, assetId: 'asset-1' }] });
    const image = model.state.files.find((file) => file.path === IMAGE)!;
    expect(image.replacement).toBeUndefined();
    expect(image.sha256).toBe('sha-of-asset-1');
    expect(model.dirty).toBe(false);
  });

  it('格式不同被服务拒绝时只显示原因，不记待替换；撤销替换恢复干净状态', async () => {
    const fake = fakePort(); const model = new DraftSourceModel(fake.port); await model.load();
    expect(await model.replaceFile(IMAGE, png(), 'photo.jpg')).toBe(false);
    expect(model.state.replaceError).toEqual({ path: IMAGE, message: expect.stringContaining('只能换成同一格式') });
    expect(model.dirty).toBe(false);
    expect(await model.replaceFile(IMAGE, png(), 'ok.png')).toBe(true);
    expect(model.state.replaceError).toBeUndefined();
    model.revertReplacement(IMAGE);
    expect(model.dirty).toBe(false);
    expect((await model.save()).kind).toBe('unchanged');
    expect(fake.submitted).toHaveLength(0);
  });

  it('检查期间读进别的修订时丢掉结果；检查期间不能保存', async () => {
    const fake = fakePort(); const model = new DraftSourceModel(fake.port); await model.load();
    model.edit(SVG_PATH, '<svg id="saved"/>'); await model.save();
    const release = fake.hold();
    const replacing = model.replaceFile(IMAGE, png(), 'late.png');
    await Promise.resolve();
    expect((await model.save()).kind).toBe('busy');
    expect(await model.load(BASE)).toBe(true);
    release();
    expect(await replacing).toBe(false);
    expect(model.state.files.some((file) => file.replacement)).toBe(false);
    expect(model.state.replaceError).toBeUndefined();
  });

  it('查看旧修订替换后保存，不可编辑文件取自所看修订', async () => {
    const fake = fakePort(); const model = new DraftSourceModel(fake.port); await model.load();
    expect(await model.replaceFile(IMAGE, png(), 'first.png')).toBe(true);
    await model.save();
    expect(await model.load(BASE)).toBe(true);
    model.edit(SVG_PATH, '<svg id="from-base"/>');
    expect((await model.save()).kind).toBe('saved');
    expect(fake.submitted[1]).toMatchObject({ baseSourceRevision: BASE });
    expect(fake.submitted[1]!.replacedFiles).toBeUndefined();
    expect(model.state.files.find((file) => file.path === IMAGE)!.sha256).toBe('base-sha');
  });

  it('SVG 按严格 UTF-8 读成文本放进编辑器；非 UTF-8 或不是 SVG 时拒绝', async () => {
    const fake = fakePort(); const model = new DraftSourceModel(fake.port); await model.load();
    expect(await model.replaceFile(SVG_PATH, new Blob(['\uFEFF<svg id="new"/>']), 'new.svg')).toBe(true);
    expect(model.state.files.find((file) => file.path === SVG_PATH)!.text).toBe('<svg id="new"/>');
    expect(fake.inspected).toHaveLength(0);
    expect(await model.replaceFile(SVG_PATH, new Blob([new Uint8Array([0x3c, 0xff, 0xfe])]), 'bad.svg')).toBe(false);
    expect(model.state.replaceError?.message).toContain('不是 UTF-8');
    expect(await model.replaceFile(SVG_PATH, new Blob(['<html/>']), 'page.svg')).toBe(false);
    expect(model.state.replaceError?.message).toContain('没有 <svg>');
    expect(model.state.files.find((file) => file.path === SVG_PATH)!.text).toBe('<svg id="new"/>');
  });
});
