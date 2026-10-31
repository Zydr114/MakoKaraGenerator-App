// 连真实 Server 的渲染设备选择（合同 1.39.0，规范 §25；渲染设备设计 §4 第 5 步）。
// 检测结果的形状；GPU 一次性帧与帧元数据回显；同一会话 CPU→GPU→CPU 换设备、相同设备 no-op、旧代次冲突；
// 正式验证任务固定设备并在输入与报告里回显。GPU 不可用的机器上，改为确认显式 GPU 明确失败、不回退 CPU。

import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { MakoClient, MemoryIdentityStore } from '../../src/client';
import { RENDER_BACKENDS, micros, nextSequence, type ProjectSessionSummary, type RenderBackendsResponse } from '../../src/protocol';
import { ContractRecorder, startTestBackend, type TestBackend } from '../support/backend';

let backend: TestBackend;
let client: MakoClient;
const recorder = new ContractRecorder();
let project: ProjectSessionSummary;
let backends: RenderBackendsResponse;
const size = { width: 320, height: 180 };

function wav(seconds: number, sampleRate = 8000): Uint8Array<ArrayBuffer> {
  const samples = seconds * sampleRate;
  const buffer = new ArrayBuffer(44 + samples * 2);
  const view = new DataView(buffer);
  const ascii = (offset: number, text: string) => [...text].forEach((c, i) => view.setUint8(offset + i, c.charCodeAt(0)));
  ascii(0, 'RIFF');
  view.setUint32(4, 36 + samples * 2, true);
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
  view.setUint32(40, samples * 2, true);
  for (let i = 0; i < samples; i++) view.setInt16(44 + i * 2, Math.round(Math.sin(i / 4) * 6000), true);
  return new Uint8Array(buffer);
}

const gpuAvailable = () => backends.backends.find((b) => b.renderBackend === 'gpu')?.available === true;
const source = () => ({ kind: 'project' as const, sourceId: project.projectSessionId, sourceRevision: client.projects.knownRevision(project.projectSessionId) ?? project.projectRevision });

beforeAll(async () => {
  backend = await startTestBackend('render-backend');
  client = await MakoClient.connect(backend.provider, {
    identityStore: new MemoryIdentityStore(),
    environment: null,
    onExchange: recorder.observer,
  });
  const audio = await client.assets.upload(new Blob([wav(3)]), { fileName: '设备测试.wav' });
  const lyrics = await client.assets.upload(new Blob(['[00:00.20]渲染设备\n[00:01.50]GPU と CPU\n']), { fileName: '设备测试.lrc' });
  // 小画布、显式输出时长：正式验证要求有输出时长。
  project = await client.projects.create({
    name: '设备测试',
    audioAssetId: audio.assetId,
    lyricsAssetId: lyrics.assetId,
    composition: { canvasWidth: 320, canvasHeight: 180, frameRateNumerator: 30, frameRateDenominator: 1, outputDurationUs: 1_000_000 },
  });
});

afterAll(async () => {
  client?.dispose();
  await backend?.stop();
});

describe('渲染设备（真实 Server）', () => {
  it('检测结果：默认是 cpu 或 gpu，CPU、GPU 各一项；可用的有设备摘要，不可用的有 field=renderBackend 的原因', async () => {
    backends = await client.preview.renderBackends();
    expect(RENDER_BACKENDS).toContain(backends.defaultBackend);
    expect(backends.backends.map((b) => b.renderBackend).sort()).toEqual(['cpu', 'gpu']);
    for (const item of backends.backends) {
      expect(item.api).toBe(item.renderBackend === 'gpu' ? 'vulkan' : 'raster');
      if (item.available) expect(item.device?.device).toBeTruthy();
      else expect(item.unavailableReason).toMatchObject({ field: 'renderBackend' });
    }
    expect(backends.backends.find((b) => b.renderBackend === 'cpu')?.available).toBe(true);
    if (!gpuAvailable()) console.warn('[注意] 本机 GPU 不可用，GPU 用例改为确认明确失败');
  });

  it('显式 GPU 的一次性帧：会话与帧都回显 gpu；GPU 不可用时明确失败，不出 CPU 帧', async () => {
    const request = { projectSessionId: project.projectSessionId, sourceRevision: source().sourceRevision, timeUs: micros(500_000), size, renderBackend: 'gpu' as const };
    if (!gpuAvailable()) {
      await expect(client.preview.renderStill(request)).rejects.toMatchObject({ field: 'renderBackend' });
      return;
    }
    const still = await client.preview.renderStill(request);
    expect(still.verified).toBe(true);
    expect(still.preview.renderBackend).toBe('gpu');
    expect(still.frame.renderBackend).toBe('gpu');
  });

  it('已有预览换设备：CPU→GPU 换新代次并出 GPU 帧，相同设备 no-op，旧代次冲突，再换回 CPU', async (ctx) => {
    if (!gpuAvailable()) ctx.skip();
    const created = await client.preview.create({ source: source(), size, alphaMode: 'opaque', renderBackend: 'cpu' });
    expect(created.renderBackend).toBe('cpu');
    const id = created.previewId;

    const toGpu = await client.preview.setRenderBackend(id, { expectedPreviewGeneration: created.previewGeneration, renderBackend: 'gpu' });
    expect(toGpu.renderBackend).toBe('gpu');
    expect(BigInt(toGpu.previewGeneration)).toBeGreaterThan(BigInt(created.previewGeneration));

    const same = await client.preview.setRenderBackend(id, { expectedPreviewGeneration: toGpu.previewGeneration, renderBackend: 'gpu' });
    expect(same).toMatchObject({ renderBackend: 'gpu', previewGeneration: toGpu.previewGeneration });

    await expect(client.preview.setRenderBackend(id, { expectedPreviewGeneration: created.previewGeneration, renderBackend: 'cpu' })).rejects.toMatchObject({
      code: 'previewGenerationConflict',
    });

    const requestSeq = nextSequence(undefined);
    const sourceRevision = toGpu.source.sourceRevision;
    const accepted = await client.preview.requestFrame(id, { expectedSourceRevision: sourceRevision, previewGeneration: toGpu.previewGeneration, requestSeq, timeUs: micros(500_000), intent: 'playback' });
    const frame = await client.preview.waitForFrame(id, { generation: accepted.previewGeneration, sourceRevision, requestSeq });
    expect(frame.renderBackend).toBe('gpu');

    const back = await client.preview.setRenderBackend(id, { expectedPreviewGeneration: accepted.previewGeneration, renderBackend: 'cpu' });
    expect(back.renderBackend).toBe('cpu');
    await client.preview.close(id, { expectedPreviewGeneration: back.previewGeneration, expectedSourceRevision: sourceRevision });
  });

  it('正式验证：任务在接受时固定设备，输入与报告都回显', async () => {
    const chosen = gpuAvailable() ? 'gpu' : 'cpu';
    const accepted = await client.projects.validate(project.projectSessionId, { framesPerOutput: 2, renderBackend: chosen });
    const job = await client.jobs.waitForTerminal(accepted.jobId, { timeoutMs: 120_000 });
    expect(job.state, job.error?.message ?? job.failureCode).toBe('succeeded');
    expect(job.input?.renderBackend).toBe(chosen);
    const report = await client.jobs.validationResult(job.jobId);
    expect(report.renderBackend).toBe(chosen);
  });

  it('JSON 响应符合冻结 OpenAPI', () => {
    expect(recorder.issues).toEqual([]);
    expect([...recorder.validated].some((v) => v.includes('/render-backends 200'))).toBe(true);
  });
});
