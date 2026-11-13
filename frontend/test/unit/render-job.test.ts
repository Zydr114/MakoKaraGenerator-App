import { describe, expect, it } from 'vitest';
import { diagnosticText } from '../../src/editor/model/render-job';

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
