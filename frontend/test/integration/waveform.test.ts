// 连真实 Server 的后端波形（G11，合同 1.7.0，规范 §6.10；W2 设计 §2.3、§5 第 5 条）。
// 用一段包络变化的单声道 PCM16 WAV：后端各层峰值与直接从 PCM 算出的值一致；
// BackendPeakSource 按块懒加载后的列峰值与浏览器金字塔（W1 自算）一致；过期 waveformId 404，前端据此退回浏览器解码。

import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { BackendError, MakoClient, MemoryIdentityStore } from '../../src/client';
import { BackendPeakSource } from '../../src/editor/model/backend-peaks';
import { buildPyramid, columnPeaks } from '../../src/editor/model/peaks';
import { WAVEFORM_WINDOW_MAX_BUCKETS, type ProjectSessionSummary, type ProjectWaveformAvailable } from '../../src/protocol';
import { ContractRecorder, startTestBackend, type TestBackend } from '../support/backend';

let backend: TestBackend;
let client: MakoClient;
const recorder = new ContractRecorder();
let project: ProjectSessionSummary;
let meta: ProjectWaveformAvailable;

// 70 秒 8 kHz：第 0 层 8750 桶，跨两块。包络是 10 秒一周期的锯齿，外加一段静音。
const SAMPLE_RATE = 8000;
const SECONDS = 70;
const pcm = new Int16Array(SAMPLE_RATE * SECONDS).map((_, i) => {
  const t = i / SAMPLE_RATE;
  if (t >= 30 && t < 32) return 0;
  return Math.round(Math.sin(i / 5) * (2000 + 26000 * ((t % 10) / 10)));
});
const samples = Float32Array.from(pcm, (v) => v / 32768);

function wavOf(data: Int16Array, sampleRate: number): Uint8Array<ArrayBuffer> {
  const buffer = new ArrayBuffer(44 + data.length * 2);
  const view = new DataView(buffer);
  const ascii = (offset: number, text: string) => [...text].forEach((c, i) => view.setUint8(offset + i, c.charCodeAt(0)));
  ascii(0, 'RIFF');
  view.setUint32(4, 36 + data.length * 2, true);
  ascii(8, 'WAVE');
  ascii(12, 'fmt ');
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, 1, true);
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, sampleRate * 2, true);
  view.setUint16(32, 2, true);
  view.setUint16(34, 16, true);
  ascii(36, 'data');
  view.setUint32(40, data.length * 2, true);
  data.forEach((v, i) => view.setInt16(44 + i * 2, v, true));
  return new Uint8Array(buffer);
}

beforeAll(async () => {
  backend = await startTestBackend('waveform');
  client = await MakoClient.connect(backend.provider, {
    identityStore: new MemoryIdentityStore(),
    environment: null,
    onExchange: recorder.observer,
  });
  const audio = await client.assets.upload(new Blob([wavOf(pcm, SAMPLE_RATE)]), { fileName: '波形测试.wav' });
  const lyrics = await client.assets.upload(new Blob(['[00:01.00]波形\n']), { fileName: '波形测试.lrc' });
  project = await client.projects.create({ name: '波形测试', audioAssetId: audio.assetId, lyricsAssetId: lyrics.assetId });
});

afterAll(async () => {
  client?.dispose();
  await backend?.stop();
});

describe('后端波形（真实 Server）', () => {
  it('能力公布两项读取；元数据 available，第 0 层 64 样本一桶，层数与桶数自洽', async () => {
    const { features } = await client.capabilities();
    expect(features).toContain('project-sessions.waveform.read');
    expect(features).toContain('project-sessions.waveform.peaks.read');
    const result = await client.projects.waveform(project.projectSessionId);
    expect(result.available).toBe(true);
    meta = result as ProjectWaveformAvailable;
    expect(meta).toMatchObject({ sampleRate: SAMPLE_RATE, sampleCount: String(pcm.length), baseBucketSamples: 64, waveformChannels: 1, format: 'minmax-f32le-v1' });
    const levels = [...meta.levels].sort((a, b) => Number(a.bucketSamples) - Number(b.bucketSamples));
    expect(Number(levels[0]!.bucketSamples)).toBe(64);
    for (const level of levels) expect(level.bucketCount).toBe(Math.ceil(pcm.length / Number(level.bucketSamples)));
  });

  it('每层的峰值窗口与直接从 PCM 算出的 min／max 一致（第 0 层分两块读取）', async () => {
    for (const level of meta.levels) {
      const width = Number(level.bucketSamples);
      for (let start = 0; start < level.bucketCount; start += WAVEFORM_WINDOW_MAX_BUCKETS) {
        const count = Math.min(WAVEFORM_WINDOW_MAX_BUCKETS, level.bucketCount - start);
        const window = await client.projects.waveformPeaks(project.projectSessionId, { waveformId: meta.waveformId, level: level.level, start, count });
        for (let i = 0; i < count; i++) {
          const from = (start + i) * width;
          const slice = samples.subarray(from, Math.min(samples.length, from + width));
          const lo = slice.reduce((m, v) => Math.min(m, v), Infinity);
          const hi = slice.reduce((m, v) => Math.max(m, v), -Infinity);
          if (Math.abs(window[2 * i]! - lo) > 1e-6 || Math.abs(window[2 * i + 1]! - hi) > 1e-6) {
            throw new Error(`第 ${level.level} 层第 ${start + i} 桶：后端 (${window[2 * i]}, ${window[2 * i + 1]})，PCM (${lo}, ${hi})`);
          }
        }
      }
    }
  });

  it('BackendPeakSource 懒加载后的列峰值与浏览器金字塔一致（整首、12 秒、帧级跨块）', async () => {
    const source = new BackendPeakSource(meta, (request, signal) =>
      client.projects.waveformPeaks(project.projectSessionId, { waveformId: meta.waveformId, ...request }, signal),
    );
    const pyramid = buildPyramid([samples], SAMPLE_RATE);
    // 等下一块到达（或 300 ms 没有新块）。
    const settle = () =>
      new Promise<void>((resolve) => {
        const done = () => {
          off();
          clearTimeout(timer);
          resolve();
        };
        const off = source.subscribe(done);
        const timer = setTimeout(done, 300);
      });
    for (const [startUs, usPerPx, columns] of [
      [-1_000_000, 80_000, 1000],
      [25_000_000, 12_000, 1000],
      [65_000_000, 1_000, 1200],
    ] as const) {
      for (let round = 0; round < 6; round++) {
        source.columns(startUs, usPerPx, columns);
        await settle();
      }
      const actual = source.columns(startUs, usPerPx, columns);
      const expected = columnPeaks(pyramid, startUs, usPerPx, columns);
      for (let c = 0; c < columns; c++) {
        if (Number.isNaN(expected.min[c]!)) {
          expect(Number.isNaN(actual.min[c]!), `列 ${c}`).toBe(true);
          continue;
        }
        expect(Math.abs(actual.min[c]! - expected.min[c]!), `${startUs} 列 ${c} min`).toBeLessThan(1e-6);
        expect(Math.abs(actual.max[c]! - expected.max[c]!), `${startUs} 列 ${c} max`).toBeLessThan(1e-6);
      }
    }
    source.dispose();
  });

  it('过期 waveformId 404 resourceNotFound；越尾窗口 400', async () => {
    const stale = await client.projects
      .waveformPeaks(project.projectSessionId, { waveformId: 'stale-waveform', level: 0, start: 0, count: 1 })
      .catch((e: unknown) => e);
    expect(stale).toBeInstanceOf(BackendError);
    expect(stale).toMatchObject({ status: 404, code: 'resourceNotFound' });
    const level0 = meta.levels.find((l) => Number(l.bucketSamples) === 64)!;
    const past = await client.projects
      .waveformPeaks(project.projectSessionId, { waveformId: meta.waveformId, level: level0.level, start: level0.bucketCount - 1, count: 2 })
      .catch((e: unknown) => e);
    expect(past).toMatchObject({ status: 400, code: 'invalidRequest' });
  });

  it('JSON 响应符合冻结 OpenAPI', () => {
    expect(recorder.issues).toEqual([]);
    expect([...recorder.validated].some((v) => v.includes('/waveform 200'))).toBe(true);
  });
});
