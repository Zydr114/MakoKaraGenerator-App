using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class CompiledImageGraph
{
    // 命中只省略确定性的 native 作者节点；固定帧、参数、uniform、形状和原图峰值准入照常执行。
    // 当前缓存仍占真实账，保守地额外检查完整原峰值；不足则调用方释放它并走原 Execute。
    internal bool CanReuseMaterialAuthorResult(ImageGraphFrameContext frame, ImageGraphResult result,
        ImageSurfaceBudgetFrame resources, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(result.Plan, Plan) || !MaterialAuthorGraphCache.MatchesFrame(this, result.Context, frame) || !Plan.Definition.Inputs.IsEmpty
                || Plan.Definition.CompositeCoverage is not null || result.Coverage is not null || !result.Diagnostics.IsEmpty)
                return false;
            ValidateFrame(frame); ImageGraphBindings.ValidateParameters(Plan, frame.Parameters);
            MaterialUniformValidation.Validate(Plan, frame.Uniforms);
            var active = ActiveReferences(Plan.Definition.Output, null);
            var ordered = Plan.Nodes.Where(node => active.Contains(GraphReference.Node(node.Id))).ToArray();
            var shapes = Shapes(frame, ordered);
            var peaks = Preflight(frame, [], ordered, shapes, Uses(ordered, Plan.Definition.Output, null));
            try { resources.CheckAdditional(peaks.Surfaces, peaks.Bytes, "ExecuteImageGraphPeak"); }
            catch (ImageSurfaceBudgetException) { return false; }
            return true;
        }
    }

    internal ImageGraphResult MaterialFallback(ImageGraphFrameContext frame, SkiaGraphSurface fallback, SkiaGraphSurface region,
        System.Collections.Immutable.ImmutableArray<ImageGraphBypassDiagnostic> diagnostics, ImageSurfaceBudgetFrame? resources, CancellationToken cancellationToken, double opacity = 1)
    {
        using var original = new ImageGraphResult(Plan, fallback, null, false, false, frame, diagnostics, 1, Bytes(fallback), releaseDeviceLease: AcquireDeviceLease());
        // 宿主基础外观已经覆盖，与被跳过材质的声明模式无关。
        return ApplyMaterialCoverage(original, region, EffectMaterialCoverageMode.Masked, opacity, resources, cancellationToken);
    }
    /// <summary>按材质导出的明确模式约束颜色与替换覆盖；返回独立产物，不夺取输入／原结果所有权。</summary>
    /// <remarks>unmasked 各乘一次 raw 区域；masked 验证实际像素后复制，绝不二次覆盖或修正作者输出。</remarks>
    public ImageGraphResult ApplyMaterialCoverage(ImageGraphResult result, SkiaGraphSurface region,
        EffectMaterialCoverageMode mode, ImageSurfaceBudgetFrame? surfaceBudget = null, CancellationToken cancellationToken = default)
        => ApplyMaterialCoverage(result, region, mode, 1, surfaceBudget, cancellationToken);
    /// <summary>在严格区域验证后各一次应用局部样式alpha；不以alpha隐藏越界输出。</summary>
    public ImageGraphResult ApplyMaterialCoverage(ImageGraphResult result, SkiaGraphSurface region,
        EffectMaterialCoverageMode mode, double opacity, ImageSurfaceBudgetFrame? surfaceBudget = null, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1 || opacity != 0 && (float)opacity == 0)
            throw new PreparedFrameException("InvalidMaterialOpacity", "材质局部透明度必须是后端可表示的[0,1]。");
        ArgumentNullException.ThrowIfNull(result); ArgumentNullException.ThrowIfNull(region);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(result.Plan, Plan)) throw Error("GraphResultMismatch", "material", "材质产物不属于固定图代次。");
            if (!Enum.IsDefined(mode)) throw Error("InvalidMaterialCoverageMode", "material.coverageMode", "覆盖模式无效。");
            ValidateFrame(result.Context); ValidateSurface(region, ShaderSurfaceKind.Coverage, "material.region", cancellationToken);
            ValidateSurface(result.Output, ShaderSurfaceKind.Color, "material.output", cancellationToken);
            ValidateDensity(region); ValidateDensity(result.Output);
            if (result.Coverage is { } coverage)
            { ValidateSurface(coverage, ShaderSurfaceKind.Coverage, "material.coverage", cancellationToken); ValidateDensity(coverage); }
            if (mode == EffectMaterialCoverageMode.Masked)
            {
                ValidateInsideRegion(result.Output, region, "material.output", cancellationToken);
                if (result.Coverage is { } mask) ValidateInsideRegion(mask, region, "material.coverage", cancellationToken);
            }
            var bounds = Aligned(region.Descriptor.Bounds, result.Context);
            using var ownFrame = surfaceBudget is null ? _surfaceBudget.BeginFrame() : null;
            var resources = surfaceBudget ?? ownFrame!;
            var inputs = (result.Coverage is { } erase ? new[] { result.Output, region, erase } : [result.Output, region])
                .DistinctBy(surface => surface.Image.Handle).ToArray();
            using var borrowed = resources.BorrowInputs(inputs);
            int count = inputs.Length + (result.Coverage is null ? 1 : 2);
            long bytes = checked(inputs.Sum(Bytes) + SizeBytes(bounds, result.Context) * (result.Coverage is null ? 1 : 2));
            CheckBudget(count, bytes); resources.CheckAdditional(result.Coverage is null ? 1 : 2,
                checked(SizeBytes(bounds, result.Context) * (result.Coverage is null ? 1 : 2)), "MaterialCoveragePeak");
            SkiaGraphSurface? color = null, finalCoverage = null;
            try
            {
                using var source = InputShader(result.Output, GraphEdgeMode.Transparent);
                using var area = InputShader(region, GraphEdgeMode.Transparent);
                if (mode == EffectMaterialCoverageMode.Unmasked)
                {
                    using var shader = _mask.CreateShader(UniformValues.Empty, new Dictionary<string, RuntimeShaderInput>
                        { ["color"] = new(source, ShaderSurfaceKind.Color), ["coverage"] = new(area, ShaderSurfaceKind.Coverage) }, cancellationToken);
                    color = Raster(bounds, result.Context, ShaderSurfaceKind.Color, canvas => Draw(canvas, bounds, shader, opacity: opacity), resources, cancellationToken);
                }
                else color = Raster(bounds, result.Context, ShaderSurfaceKind.Color, canvas => Draw(canvas, bounds, source, opacity: opacity), resources, cancellationToken);
                if (result.Coverage is { } originalCoverage)
                {
                    using var original = InputShader(originalCoverage, GraphEdgeMode.Transparent);
                    if (mode == EffectMaterialCoverageMode.Unmasked)
                    {
                        using var shader = _payloadPrograms.CoverageProduct.CreateShader(UniformValues.Empty, new Dictionary<string, RuntimeShaderInput>
                            { ["first"] = new(original, ShaderSurfaceKind.Coverage), ["region"] = new(area, ShaderSurfaceKind.Coverage) }, cancellationToken);
                        finalCoverage = Raster(bounds, result.Context, ShaderSurfaceKind.Coverage, canvas => Draw(canvas, bounds, shader, opacity: opacity), resources, cancellationToken);
                    }
                    else finalCoverage = Raster(bounds, result.Context, ShaderSurfaceKind.Coverage, canvas => Draw(canvas, bounds, original, opacity: opacity), resources, cancellationToken);
                }
                var output = new ImageGraphResult(Plan, color, finalCoverage, true, finalCoverage is not null, result.Context,
                    result.Diagnostics, Math.Max(count, result.PeakSurfaces), Math.Max(bytes, result.PeakBytes), releaseDeviceLease: AcquireDeviceLease());
                color = null; finalCoverage = null; return output;
            }
            finally { color?.Dispose(); finalCoverage?.Dispose(); }

            void ValidateDensity(SkiaGraphSurface surface)
            {
                if (surface.Descriptor.DensityX != result.Context.DensityX || surface.Descriptor.DensityY != result.Context.DensityY)
                    throw Error("MaterialRegionGeometryMismatch", "material.region", "区域与材质帧必须使用同一真实密度。");
            }
        }
    }

    private void ValidateInsideRegion(SkiaGraphSurface surface, SkiaGraphSurface region, string field, CancellationToken cancellationToken)
    {
        using var performance = PerformanceTelemetry.Measure(PerformancePhase.Validation);
        using var pixels = ReadImagePixels(surface, field);
        using var mask = ReadImagePixels(region, "material.region");
        var d = surface.Descriptor; var r = region.Descriptor;
        double offsetX = (d.Bounds.Left - r.Bounds.Left) * r.DensityX, offsetY = (d.Bounds.Top - r.Bounds.Top) * r.DensityY;
        if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY) || Math.Abs(offsetX - Math.Round(offsetX)) > 1e-6 || Math.Abs(offsetY - Math.Round(offsetY)) > 1e-6)
            throw Error("MaterialRegionGeometryMismatch", field, "区域与输出须共享像素网格，不能重新采样来掩盖越界。");
        int pixelRowBytes = checked(surface.Image.Width * 4), maskRowBytes = checked(region.Image.Width * 4);
        ReadOnlySpan<byte> pixelBytes = pixels.Buffer.AsSpan(0, checked(pixelRowBytes * surface.Image.Height));
        ReadOnlySpan<byte> maskBytes = mask.Buffer.AsSpan(0, checked(maskRowBytes * region.Image.Height));
        for (int y = 0; y < surface.Image.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double my = y + Math.Round(offsetY);
            for (int x = 0; x < surface.Image.Width; x++)
            {
                double mx = x + Math.Round(offsetX);
                int allowed = mx >= 0 && mx < region.Image.Width && my >= 0 && my < region.Image.Height
                    ? maskBytes[checked((int)my * maskRowBytes + (int)mx * 4 + 3)] : 0;
                int alpha = pixelBytes[y * pixelRowBytes + x * 4 + 3];
                if (alpha > (allowed == 0 ? 0 : allowed + 1))
                    throw Error("MaterialOutputOutsideRegion", field, "已覆盖材质超出实际绑定区域；请修正覆盖声明或图输出，不能二次乘蒙版。");
            }
        }
    }
}
