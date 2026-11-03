import { writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { MakoClient, MemoryIdentityStore } from '../../src/client';
import type { EditableLyricLine, LyricsExportFormat } from '../../src/protocol';
import { ContractRecorder, startTestBackend, type TestBackend } from '../support/backend';

const ASS = String.raw`[Script Info]
; Mako LRC Studio: Dialogue lines with Effect fx are the rendered layout; Comment lines with Effect karaoke are the timing source.
[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 1,0:00:00.00,0:00:04.00,Mako-Voice,Voice,0,0,0,fx,{\kf100}海
Dialogue: 2,0:00:00.00,0:00:04.00,Mako-Voice-Ruby,Voice,0,0,0,fx,{\kf50}う{\kf50}み
Comment: 0,0:00:01.00,0:00:03.00,Mako-Voice,Voice,0,0,0,karaoke,{\k50}海|う{\k50}#|み{\k100}へ
`;

let backend: TestBackend;
let client: MakoClient;
const recorder = new ContractRecorder();
const opened: string[] = [];

beforeAll(async () => {
  backend = await startTestBackend('lyrics-compat');
  client = await MakoClient.connect(backend.provider, {
    identityStore: new MemoryIdentityStore(),
    environment: null,
    onExchange: recorder.observer,
    jobPollIntervalMs: 250,
  });
});

afterAll(async () => {
  try {
    for (const id of opened) {
      const accepted = await client.projects.close(id, { policy: 'discard' });
      const closed = await client.jobs.waitForTerminal(accepted.jobId);
      expect(closed.state).toBe('succeeded');
    }
    expect(recorder.issues).toEqual([]);
  } finally {
    client?.dispose();
    await backend?.stop();
    if (backend) {
      // ExchangeRecord 已在 transport 脱敏；私人 bootstrap 不进入验收报告。
      await writeFile(join(backend.profile, 'lyrics-compat-http.json'), JSON.stringify({
        serverStopped: true, exchanges: recorder.records, schemaIssues: recorder.issues,
      }, null, 2) + '\n', { mode: 0o600 });
    }
  }
});

async function create(name: string, lyricsAssetId?: string): Promise<string> {
  const summary = await client.projects.create({
    name,
    ...(lyricsAssetId ? { lyricsAssetId } : {}),
    composition: { canvasWidth: 320, canvasHeight: 180, frameRateNumerator: 30, frameRateDenominator: 1 },
  });
  opened.push(summary.projectSessionId);
  return summary.projectSessionId;
}

async function lines(id: string): Promise<EditableLyricLine[]> {
  return client.projects.allLines(id, 'primary', client.projects.knownRevision(id)!);
}

function expectKaraoke(value: EditableLyricLine[]): void {
  expect(value).toHaveLength(1);
  const line = value[0]!;
  expect(line.text).toBe('海へ');
  expect(line.time).toMatchObject({ startUs: '1000000', endUs: '3000000' });
  expect(line.tokens.map(({ text, time }) => ({ text, time }))).toEqual([
    { text: '海', time: { startUs: '1000000', endUs: '2000000' } },
    { text: 'へ', time: { startUs: '2000000', endUs: '3000000' } },
  ]);
  expect(line.rubyAnnotations).toHaveLength(1);
  const ruby = line.rubyAnnotations[0]!;
  expect(ruby.baseTokenId).toBe(line.tokens[0]!.id);
  expect(ruby.text).toBe('うみ');
  expect(ruby.segments.map(({ text, time }) => ({ text, time }))).toEqual([
    { text: 'う', time: { startUs: '1000000', endUs: '1500000' } },
    { text: 'み', time: { startUs: '1500000', endUs: '2000000' } },
  ]);
}

describe('ASS 和 TTML 兼容性（真实 HTTP）', () => {
  it('上传、规划、新建、替换及撤销重做保留歌词和注音', async () => {
    const inferred = await client.assets.upload(new Blob([ASS]), { fileName: 'lyrics.ass' });
    expect(inferred.contentType).toBe('text/plain');
    const binary = await client.assets.upload(new Blob([ASS]), { fileName: 'lyrics.ass', contentType: 'application/octet-stream' });
    expect(binary.contentType).toBe('application/octet-stream');
    for (const asset of [inferred, binary]) {
      const plan = await client.assets.lyricsImportPlan({ assetId: asset.assetId });
      expect(plan.preparationError).toBeUndefined();
      expect(plan.detectedImporterId).toBe('ass');
      expectKaraoke(plan.previewLines);
    }

    const created = await create('ASS 创建', binary.assetId);
    expectKaraoke(await lines(created));

    const existing = await create('ASS 导入');
    expect(await lines(existing)).toHaveLength(0);
    const result = await client.projects.command(existing, {
      type: 'lyrics.importAsset', assetId: inferred.assetId, destination: { kind: 'existing', branchId: 'primary' },
    });
    expect(result.changed).toBe(true);
    expectKaraoke(await lines(existing));
    await client.projects.command(existing, { type: 'history.undo' });
    expect(await lines(existing)).toHaveLength(0);
    await client.projects.command(existing, { type: 'history.redo' });
    expectKaraoke(await lines(existing));
  });

  it.each(['ttml.project', 'ttml.amll'] as const)('%s 导出并重新导入保留正文和注音片段', async (format: LyricsExportFormat) => {
    const asset = await client.assets.upload(new Blob([ASS]), { fileName: 'source.ass' });
    const original = await create(`导出 ${format}`, asset.assetId);
    expectKaraoke(await lines(original));
    const revision = client.projects.knownRevision(original)!;
    const plan = await client.projects.lyricsExportPlan(original, revision, {
      format, options: {}, ...(format === 'ttml.project' ? { scope: 'document' } : { branchId: 'primary' }),
    });
    const exported = await client.projects.lyricsExport(original, revision, {
      planId: plan.planId, acceptLosses: plan.requiresLossAcceptance,
    });
    expect(exported.report.writtenRubySegmentCount).toBe(2);
    const downloaded = await client.lyricsExports.content(exported.exportId);
    expect(downloaded.blob).not.toBeNull();
    const uploaded = await client.assets.upload(downloaded.blob!, { fileName: `${format}.ttml` });
    const importedPlan = await client.assets.lyricsImportPlan({ assetId: uploaded.assetId });
    expect(importedPlan.preparationError).toBeUndefined();
    expect(importedPlan.detectedImporterId).toBe('ttml');
    expectKaraoke(importedPlan.previewLines);
    const reimported = await create(`往返 ${format}`, uploaded.assetId);
    expectKaraoke(await lines(reimported));
  });

  it('HTTP 响应符合冻结合同', () => {
    expect(recorder.issues).toEqual([]);
  });
});
