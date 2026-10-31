using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class CompiledPackageGraphExports
{
    /// <summary>建立帧外真实材质程序；借用本包，显式指定捕获框。</summary>
    public SceneMaterialProgram CreateMaterialProgram(MaterialHandle handle, GraphRect bounds)
    {
        var export = GetMaterialDefinition(handle);
        return new(handle, this, _definition.ImageGraphs[export.GraphPath], bounds);
    }
    /// <summary>读取本固定修订的完整材质定义；不是猜测图导出或文件路径。</summary>
    public EffectMaterialDefinition GetMaterialDefinition(MaterialHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); ArgumentNullException.ThrowIfNull(handle);
        if (!Resources.Contains(handle)) throw new EffectPackageException("InvalidMaterialResource", "exports", "材质句柄不属于本固定导出修订。");
        return _definition.GetMaterial(handle.Identity.ExportId);
    }

    /// <summary>执行真实材质导出并限制在 raw 区域；TargetColor 强制使用宿主生成的包内基础外观。</summary>
    /// <remarks>fallback 已采用同一 glyph／区域生成，且未烘入共同 alpha；本接口不生成字形或启用工程绑定。
    /// 输入借用，返回独立结果；透明背景依赖跳过整张图并使用 fallback，不采用图的普通原输入旁路。</remarks>
    public ImageGraphResult ExecuteMaterial(MaterialHandle handle, SceneCaptureRegion regionKind, ImageGraphFrameContext frame,
        SkiaGraphSurface region, SkiaGraphSurface fallback, IReadOnlyDictionary<string, SkiaGraphInput> inputs,
        ImageSurfaceBudgetFrame? surfaceBudget = null, CancellationToken cancellationToken = default)
        => ExecuteMaterial(handle, regionKind, frame, region, fallback, inputs, 1, surfaceBudget, cancellationToken);
    /// <summary>执行真实材质并在覆盖验证之后对颜色和擦除量各应用一次局部样式透明度。</summary>
    public ImageGraphResult ExecuteMaterial(MaterialHandle handle, SceneCaptureRegion regionKind, ImageGraphFrameContext frame,
        SkiaGraphSurface region, SkiaGraphSurface fallback, IReadOnlyDictionary<string, SkiaGraphInput> inputs, double opacity,
        ImageSurfaceBudgetFrame? surfaceBudget = null, CancellationToken cancellationToken = default)
        => ExecuteMaterialCore(handle, regionKind, frame, region, fallback, inputs, opacity, surfaceBudget, null, cancellationToken);

    internal ImageGraphResult ExecuteMaterialShared(MaterialHandle handle, SceneCaptureRegion regionKind, ImageGraphFrameContext frame,
        SkiaGraphSurface region, SkiaGraphSurface fallback, IReadOnlyDictionary<string, SkiaGraphInput> inputs, double opacity,
        ImageSurfaceBudgetFrame surfaceBudget, MaterialAuthorGraphCache authorCache, CancellationToken cancellationToken)
    {
        try { return ExecuteMaterialCore(handle, regionKind, frame, region, fallback, inputs, opacity, surfaceBudget, authorCache, cancellationToken); }
        catch { authorCache.Clear(); throw; }
    }

    private ImageGraphResult ExecuteMaterialCore(MaterialHandle handle, SceneCaptureRegion regionKind, ImageGraphFrameContext frame,
        SkiaGraphSurface region, SkiaGraphSurface fallback, IReadOnlyDictionary<string, SkiaGraphInput> inputs, double opacity,
        ImageSurfaceBudgetFrame? surfaceBudget, MaterialAuthorGraphCache? authorCache, CancellationToken cancellationToken)
    {
        using var performance = PerformanceTelemetry.Measure(PerformancePhase.Material);
        ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(fallback); ArgumentNullException.ThrowIfNull(inputs);
        cancellationToken.ThrowIfCancellationRequested(); var export = GetMaterialDefinition(handle);
        string allowed = regionKind switch
        { SceneCaptureRegion.Fill => "fill", SceneCaptureRegion.Stroke => "stroke", _ => throw new EffectPackageException("PackageMaterialRegionInvalid", "exports." + export.Id, "实际材质绑定须是单一 fill／stroke 区域。") };
        if (!export.Regions.Contains(allowed, StringComparer.Ordinal))
            throw new EffectPackageException("PackageMaterialRegionInvalid", "exports." + export.Id + ".regions", "材质未声明此实际绑定区域。");
        if (export.Outputs.Any(output => !output.Requires.IsEmpty || !output.DependsOn.IsEmpty))
            throw new EffectPackageException("PackageOutputPlanPending", "exports." + export.Id + ".outputs", "材质跨输出执行计划尚未接通。");
        if (!frame.Parameters.Ids.SequenceEqual(export.ParameterIds.Order(StringComparer.Ordinal)))
            throw new EffectPackageException("PackageParameterSetMismatch", "exports." + export.Id + ".parameters", "材质参数集合须完整匹配导出。");
        frame.Parameters.ValidateResources(Resources);
        var graph = _graphs[export.GraphPath];
        MaterialUniformValidation.Validate(graph.Plan, frame.Uniforms);
        if (graph.Plan.Definition.AnchorMode != GraphAnchorMode.ColorOnly)
            throw new EffectPackageException("PackageGraphMappingPending", "exports." + export.Id, "材质的实际映射协议尚未接通。");
        ImageGraphBindings.ValidateParameters(_definition.Parameters.Where(parameter => export.ParameterIds.Contains(parameter.Id, StringComparer.Ordinal))
            .Select(parameter => parameter.Constraint), graph.Plan.Definition.Id, frame.Parameters);
        foreach (string id in inputs.Keys)
            if (!graph.Plan.Definition.Inputs.Any(input => input.Id == id))
                throw new ImageGraphException("GraphInputUnknown", graph.Plan.Definition.Id, "inputs." + id, "材质输入未声明，透明跳过也不能掩盖协议错误。");
        var declarations = graph.ActiveInputs(frame with { Transparent = false });
        var backgrounds = declarations.Where(input => input.BackgroundRequirement != GraphBackgroundRequirement.None).ToArray();
        if (frame.Transparent && backgrounds.Length > 0)
            return graph.MaterialFallback(frame, fallback, region,
                [.. backgrounds.Select(input => new ImageGraphBypassDiagnostic(graph.Plan.Definition.Id, input.Id, "TransparentBackgroundDependency"))],
                surfaceBudget, cancellationToken, opacity);
        // 所有 TargetColor 端口都读同一包基础外观；不能从调用者旧样式输入产生反馈。
        var resolved = inputs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var input in declarations)
        {
            var source = input.ChoiceParameter is { } choice ? input.Choices[frame.Parameters.GetEnum(choice)] : input.Source;
            if (source == GraphInputSource.TargetColor) resolved[input.Id] = new(fallback, source);
        }
        ImageGraphResult? result = authorCache?.Take(graph, frame);
        try
        {
            if (result is not null && !graph.CanReuseMaterialAuthorResult(frame, result, surfaceBudget!, cancellationToken))
            { result.Dispose(); result = null; }
            result ??= graph.Execute(frame, resolved, surfaceBudget, cancellationToken);
            var applied = graph.ApplyMaterialCoverage(result, region, export.CoverageMode, opacity, surfaceBudget, cancellationToken);
            try { if (authorCache?.TryStore(graph, result) == true) result = null; }
            catch { applied.Dispose(); throw; }
            return applied;
        }
        finally { result?.Dispose(); }
    }
}
