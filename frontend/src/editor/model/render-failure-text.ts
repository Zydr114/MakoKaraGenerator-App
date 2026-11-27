/** 已知渲染/效果失败码的简明说明；未知码返回 undefined，由调用方保留原错误文案。 */
const KNOWN_RENDER_FAILURE_MESSAGES: Readonly<Record<string, string>> = {
  ImageEffectPartialFusion: '效果叠加范围冲突：后续图像效果只捕获了前序已融合图层的一部分，请调整效果顺序或捕获范围',
  ImageSurfaceBudgetExceeded: '中间渲染表面超出预算限制，请减少同时叠加的重型图像效果或降低预览规格',
  GraphSurfaceBudgetExceeded: '图像效果表面超出预算限制，请减少同时叠加的效果或调整参数',
  GraphTransformBoundsExceeded: '图像效果动态变换超出声明的输出边界',
  GraphTransformSamplingExceeded: '图像效果动态变换采样偏移超出允许范围',
  GraphCompositeBoundsExceeded: '图像效果合成边界超出下层工作范围',
  WorkerRuntimeDiagnosticError: '效果运行产生错误诊断，请检查效果配置或在验证面板查看详情',
};

export function knownRenderFailureText(code: string | undefined | null): string | undefined {
  if (!code) return undefined;
  const text = KNOWN_RENDER_FAILURE_MESSAGES[code];
  return text ? `${text}（${code}）` : undefined;
}
