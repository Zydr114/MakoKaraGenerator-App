// 缺背景兜底（合同 1.49.2 预览、1.51.0 导出与测量；路线 §2 第 9、11 条）：图效果或材质需要背景，
// 而输出是透明的或这一帧没有可用的背景媒体时，后端按兜底外观绘制并给 warning。编辑与导出都只提示，不阻止。
// 纯函数，不依赖 React。

import type { BackendDiagnostic, ExportReport } from '../../protocol';

/** 两种兜底诊断：不透明输出缺背景媒体、透明输出没有背景。 */
export const BACKGROUND_FALLBACK_CODES: readonly string[] = ['BackgroundMediaUnavailable', 'TransparentBackgroundDependency'];

export function isBackgroundFallback(diagnostic: Pick<BackendDiagnostic, 'code'>): boolean {
  return BACKGROUND_FALLBACK_CODES.includes(diagnostic.code);
}

/** 一条兜底诊断的一行：“材质 makokara.glass-ink · glass：背景媒体不可用……”。 */
export function backgroundFallbackLine(diagnostic: BackendDiagnostic): string {
  const resource = diagnostic.material?.material ?? diagnostic.imageEffect?.graph;
  const kind = diagnostic.originKind === 'imageEffect' || diagnostic.imageEffect ? '图效果' : '材质';
  return `${kind}${resource ? ` ${resource.packageId} · ${resource.exportId}` : ''}：${diagnostic.message}`;
}

/** 预览这一帧的缺背景提示；没有兜底诊断时为 undefined。 */
export function previewFallbackText(diagnostics: readonly Pick<BackendDiagnostic, 'code'>[]): string | undefined {
  const count = diagnostics.filter(isBackgroundFallback).length;
  if (count === 0) return undefined;
  return `缺背景：这一帧有 ${count} 处图效果或材质需要背景媒体，但没有可用的背景（未设置、文件丢失或该时刻无帧），按兜底外观显示。可以在工程设置里设置背景图片或视频；导出时同样按兜底外观处理。`;
}

/**
 * 导出报告里的兜底摘要：几处、涉及多少帧、是否截断；没有兜底时为 undefined。
 * 1.51.0 之前登记的报告没有这三项，按没有兜底理解。
 */
export function exportFallbackText(report: Pick<ExportReport, 'fallbackDiagnostics' | 'fallbackFrameCount' | 'fallbackDiagnosticsTruncated' | 'expectedFrames'>): string | undefined {
  const list = report.fallbackDiagnostics ?? [];
  const frames = report.fallbackFrameCount ?? 0;
  const truncated = !!report.fallbackDiagnosticsTruncated;
  if (list.length === 0 && frames === 0 && !truncated) return undefined;
  const reason = list.some((d) => d.code === 'TransparentBackgroundDependency') ? '透明输出没有背景' : '没有可用的背景媒体';
  return `${reason}：${list.length} 处${truncated ? '以上' : ''}图效果或材质按兜底外观导出，涉及 ${frames}/${report.expectedFrames} 帧。${truncated ? '列表太长，后端只保留了前面的部分。' : ''}`;
}
