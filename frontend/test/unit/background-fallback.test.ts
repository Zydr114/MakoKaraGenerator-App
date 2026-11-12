// 缺背景兜底的提示文字（P3 第 7 步 C）：预览只数本帧的两种兜底码，导出摘要按报告三项给出原因、帧数与截断。

import { describe, expect, it } from 'vitest';
import { backgroundFallbackLine, exportFallbackText, previewFallbackText } from '../../src/editor/model/background-fallback';
import type { BackendDiagnostic } from '../../src/protocol';

const resource = { packageId: 'makokara.glass-ink', revision: '2', exportId: 'glass' };
const material: BackendDiagnostic = {
  severity: 'warning',
  code: 'BackgroundMediaUnavailable',
  message: '背景媒体不可用',
  originKind: 'material',
  material: { owner: { kind: 'source' }, material: resource, inputId: 'background', code: 'BackgroundMediaUnavailable' },
  imageEffect: null,
} as BackendDiagnostic;
const graph: BackendDiagnostic = {
  severity: 'warning',
  code: 'TransparentBackgroundDependency',
  message: '透明输出没有背景',
  originKind: 'imageEffect',
  imageEffect: { instanceId: 'e1', artifactHash: 'h', graph: { ...resource, exportId: 'blur' }, inputId: 'bg', code: 'TransparentBackgroundDependency' },
  material: null,
} as BackendDiagnostic;

describe('缺背景兜底提示', () => {
  it('逐条写明图效果或材质与包资源；原样报告里另一槽位为 null 也能读', () => {
    expect(backgroundFallbackLine(material)).toBe('材质 makokara.glass-ink · glass：背景媒体不可用');
    expect(backgroundFallbackLine(graph)).toBe('图效果 makokara.glass-ink · blur：透明输出没有背景');
  });

  it('预览只数两种兜底码，没有时不提示', () => {
    expect(previewFallbackText([{ code: 'GraphRuntimeWarning' }])).toBeUndefined();
    expect(previewFallbackText([material, { code: 'GraphRuntimeWarning' }, graph])).toContain('这一帧有 2 处');
  });

  it('导出摘要：旧报告与零兜底不提示；透明输出、帧数与截断写明', () => {
    expect(exportFallbackText({ expectedFrames: 240 })).toBeUndefined();
    expect(exportFallbackText({ expectedFrames: 240, fallbackDiagnostics: [], fallbackFrameCount: 0, fallbackDiagnosticsTruncated: false })).toBeUndefined();
    expect(exportFallbackText({ expectedFrames: 240, fallbackDiagnostics: [material], fallbackFrameCount: 12, fallbackDiagnosticsTruncated: false })).toBe(
      '没有可用的背景媒体：1 处图效果或材质按兜底外观导出，涉及 12/240 帧。',
    );
    const text = exportFallbackText({ expectedFrames: 240, fallbackDiagnostics: [graph], fallbackFrameCount: 240, fallbackDiagnosticsTruncated: true });
    expect(text).toMatch(/^透明输出没有背景：1 处以上/);
    expect(text).toContain('只保留了前面的部分');
  });
});
