// 连真实 Server 的缺背景兜底（合同 1.49.2 预览、1.51.0 导出；P3 第 7 步 C）：
// 未唱填充用 GlassInk 的“媒体背景玻璃”（必需 BackgroundMedia），工程不设背景。
// 预览控制器在出帧后读诊断页给出提示；不透明与透明导出都成功，报告带兜底诊断，提示文字按原因区分。
// 效果包用后端仓库 samples/ScriptEffects 的示例运行时打包；找不到时整个文件跳过并写明原因。

import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import { packSampleEffect, sampleEffectsDir } from '../../scripts/effect-packages.mjs';
import { MakoClient, MemoryIdentityStore } from '../../src/client';
import { backgroundFallbackLine, exportFallbackText } from '../../src/editor/model/background-fallback';
import { withSlotMaterial } from '../../src/editor/model/full-template';
import { PreviewController } from '../../src/editor/preview/preview-controller';
import { isExportFailureReport, type ExportFormat, type ExportReport, type FullTemplateDefinition, type ProjectSessionSummary } from '../../src/protocol';
import { ContractRecorder, startTestBackend, type TestBackend } from '../support/backend';
import { makeWav } from '../support/fixtures';

const samples = sampleEffectsDir();
if (!samples) console.warn('[skipped] 找不到后端仓库的 samples/ScriptEffects（MAKO_BACKEND_REPO），跳过缺背景兜底集成测试。');

let backend: TestBackend;
let client: MakoClient;
let project: ProjectSessionSummary;
let templateId = '';
const recorder = new ContractRecorder();
const id = () => project.projectSessionId;
const current = () => client.projects.knownRevision(id())!;

beforeAll(async () => {
  if (!samples) return;
  backend = await startTestBackend('background-fallback');
  client = await MakoClient.connect(backend.provider, { identityStore: new MemoryIdentityStore(), environment: null, onExchange: recorder.observer, jobPollIntervalMs: 500 });

  const asset = await client.assets.upload(new Blob([packSampleEffect('GlassInk')]), { fileName: 'GlassInk.mkfx' });
  const glass = await client.effectPackages.inspect({ packageAssetId: asset.assetId });
  const builtin = await client.templates.listAll('builtin');
  const classic = (await client.templates.fullDetail('builtin', 'builtin:line-classic', builtin.revision)).definition;
  const definition: FullTemplateDefinition = {
    ...classic,
    name: `集成缺背景-${Date.now().toString(36)}`,
    style: withSlotMaterial(classic.style, 'unsungFill', { kind: 'package', packageId: glass.packageId, revision: glass.packageRevision, exportId: 'glass-media', parametersJson: '{}' }),
    packages: [glass.reference],
  };
  const personal = await client.templates.listAll('personal');
  const created = await client.templates.command('personal', personal.revision, { type: 'create', definitionFormat: 'full', definition });
  templateId = created.templateId;

  const audio = await client.assets.upload(new Blob([makeWav(1)]), { fileName: '缺背景.wav' });
  // 导出要求逐字有时间、定好输出时长；320×180、30 帧／秒、1 秒，导出很快。
  const lrc = await client.assets.upload(new Blob(['[00:00.00]<00:00.00>玻<00:00.30>璃<00:00.60>字<00:00.90>\n']), { fileName: '缺背景.lrc' });
  project = await client.projects.create({
    name: '缺背景',
    audioAssetId: audio.assetId,
    lyricsAssetId: lrc.assetId,
    composition: { canvasWidth: 320, canvasHeight: 180, frameRateNumerator: 30, frameRateDenominator: 1, outputDurationUs: 1_000_000 },
  });
  const containerId = (await client.projects.orchestration(id(), project.projectRevision)).programs[0]!.defaultContainerId;
  await client.projects.command(id(), {
    type: 'presetApplications.apply',
    templateLibraryId: 'personal',
    templateId,
    templateRevision: created.revision,
    targets: [{ kind: 'node', nodeId: containerId, includeDescendants: true }],
  });
});

afterAll(async () => {
  if (client && templateId) {
    const { revision } = await client.templates.listAll('personal');
    await client.templates.command('personal', revision, { type: 'delete', templateId });
  }
  client?.dispose();
  await backend?.stop();
});

async function exportReport(format: ExportFormat): Promise<ExportReport> {
  const accepted = await client.projects.exportVideo(id(), { format, includeAudio: false, ...(format === 'opaqueMp4' && { quality: 'draft' as const }) });
  const job = await client.jobs.waitForTerminal(accepted.jobId, { timeoutMs: 180_000 });
  expect(job.state, job.error?.message ?? job.failureCode).toBe('succeeded');
  const report = await client.jobs.exportResult(job.jobId);
  if (isExportFailureReport(report)) throw new Error(report.failureCode);
  expect(job.exportResult?.fallbackDiagnosticCount).toBe(report.fallbackDiagnostics?.length);
  return report;
}

describe.skipIf(!samples)('缺背景兜底（真实 Server）', () => {
  it('预览：出帧后读到本帧的兜底诊断，给出缺背景提示', async () => {
    const controller = new PreviewController(client, id(), { width: 320, height: 180 });
    try {
      controller.show(500_000, current());
      await vi.waitFor(() => expect(['ready', 'error']).toContain(controller.state$.get().status), { timeout: 60_000, interval: 200 });
      expect(controller.state$.get().error).toBeUndefined();
      await vi.waitFor(() => expect(controller.state$.get().fallback).toMatch(/^缺背景：这一帧有 [1-9]\d* 处/), { timeout: 10_000, interval: 100 });
    } finally {
      controller.dispose();
    }
  });

  it('不透明导出：成功，报告写明没有可用的背景媒体与涉及帧数', async () => {
    const report = await exportReport('opaqueMp4');
    expect(report.fallbackFrameCount).toBeGreaterThan(0);
    expect(report.fallbackDiagnostics?.[0]).toMatchObject({ code: 'BackgroundMediaUnavailable', originKind: 'material', severity: 'warning' });
    expect(report.diagnostics).toEqual([]);
    expect(exportFallbackText(report)).toMatch(new RegExp(`^没有可用的背景媒体：\\d+ 处图效果或材质按兜底外观导出，涉及 ${report.fallbackFrameCount}/${report.expectedFrames} 帧。$`));
    expect(backgroundFallbackLine(report.fallbackDiagnostics![0]!)).toMatch(/^材质 makokara\.glass-ink · glass-media：/);
  });

  it('透明导出：成功，原因是透明输出没有背景', async () => {
    const report = await exportReport('transparentProRes4444');
    expect(report.fallbackDiagnostics?.[0]).toMatchObject({ code: 'TransparentBackgroundDependency', originKind: 'material' });
    expect(exportFallbackText(report)).toMatch(/^透明输出没有背景：/);
  });

  it('JSON 响应符合冻结 OpenAPI', () => {
    expect(recorder.issues).toEqual([]);
    expect([...recorder.validated].some((v) => v.includes('/diagnostics 200'))).toBe(true);
    expect([...recorder.validated].some((v) => v.includes('/export-result 200'))).toBe(true);
  });
});
