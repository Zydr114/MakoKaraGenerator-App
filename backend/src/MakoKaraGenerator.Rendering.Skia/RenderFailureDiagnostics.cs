namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>从真实异常链提取可跨进程的结构诊断；不转发异常文本。</summary>
public static class RenderFailureDiagnostics
{
    /// <summary>提取真实源材质来源及其实际表面预算根因；没有源材质失败时返回空。</summary>
    /// <param name="failure">实际失败异常。</param><returns>真实源材质失败的安全结构值。</returns>
    public static RenderSourceMaterialFailure? ExtractSourceMaterial(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        SourceMaterialFailureException? source = null;
        for (Exception? current = failure; current is not null; current = current.InnerException)
            if (current is SourceMaterialFailureException actual) { source = actual; break; }
        if (source is null) return null;
        RenderImageSurfaceBudget? budget = null;
        for (Exception? current = source.InnerException; current is not null; current = current.InnerException)
            if (current is ImageSurfaceBudgetException actual)
            {
                budget = new(actual.CurrentSurfaces, actual.AdditionalSurfaces, actual.MaximumSurfaces,
                    actual.CurrentBytes, actual.AdditionalBytes, actual.MaximumBytes);
                break;
            }
        return new(source.Owner, source.Stage, source.TimeUs, source.Code, budget);
    }
}
