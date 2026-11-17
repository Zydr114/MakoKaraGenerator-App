import { describe, expect, it } from 'vitest';
import { diagnosticText, exportReportFacts } from '../../src/editor/model/render-job';
import { micros, type ExportReport } from '../../src/protocol';

const report = (overrides: Partial<ExportReport> = {}): ExportReport => ({
  format: 'opaqueMp4', audioIncluded: true, expectedFrames: 90, decodedFrames: 90, framesWritten: 90,
  droppedFrames: 0, failedFrames: 0, width: 1920, height: 1080, frameRateNumerator: 30, frameRateDenominator: 1,
  derivedVideoDurationUs: micros(3_000_000), actualVideoDurationUs: micros(3_000_000),
  audioDurationUs: micros(1_000_000), videoCodec: 'h264', audioCodec: 'aac', pixelFormat: 'yuv420p',
  hasAlpha: false, mediaSize: 1024, sha256: 'a'.repeat(64), warnings: [], diagnostics: [],
  isVerified: true, actualAudioDurationUs: micros(3_000_000), strictlyVerified: true,
  requestedQuality: 'standard', actualQuality: 'standard', ...overrides,
});

describe('导出音轨报告', () => {
  it('区分原始音轨和成片音轨的实际时长；纯画面不显示源音频', () => {
    const facts = exportReportFacts(report());
    expect(facts.find((fact) => fact.label === '音轨')?.value).toContain('0:03.000');
    expect(facts.find((fact) => fact.label === '源音频')?.value).toBe('0:01.000');
    const silent = exportReportFacts(report({ audioIncluded: false, audioCodec: null, audioDurationUs: null, actualAudioDurationUs: null }));
    expect(silent.find((fact) => fact.label === '音轨')?.value).toBe('没有');
    expect(silent.some((fact) => fact.label === '源音频')).toBe(false);
  });
});

describe('导出源材质失败', () => {
  it('显示真实源、时刻、阶段与结构预算', () => {
    const text = diagnosticText({
      code: 'ImageSurfaceBudgetExceeded', severity: 'error', message: '源材质执行超过中间表面预算，导出已停止。',
      originKind: 'sourceMaterialFailure',
      sourceMaterialFailure: {
        owner: { path: 'lyricStyle.unsung.fill', region: 'fill', slot: 0,
          sourceMaterial: { packageId: 'package', revision: `sha256:${'a'.repeat(64)}`, exportId: 'ink' }, parametersJson: '{}' },
        stage: 'RenderMaterials', timeUs: '930000', code: 'ImageSurfaceBudgetExceeded',
        budget: { currentSurfaces: 62, additionalSurfaces: 4, maximumSurfaces: 64,
          currentBytes: '1000000', additionalBytes: '262144', maximumBytes: '536870912' },
      },
    });
    expect(text).toContain('lyricStyle.unsung.fill · package/ink · RenderMaterials');
    expect(text).toContain('表面 62 + 4 / 64，估算 0.95 + 0.25 / 512.00 MiB');
    expect(text).toContain('0:00.930');
  });
});
