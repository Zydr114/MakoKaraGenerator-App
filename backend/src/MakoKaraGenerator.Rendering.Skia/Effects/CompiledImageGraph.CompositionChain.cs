using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class CompiledImageGraph
{
    /// <summary>一次合成同目标的有序图链；最后颜色保留前面每个实际替换范围，旁路不新增替换。</summary>
    /// <remarks>调用者提供依次读取前一 TargetColor 的存活结果，最后结果必须属于本编译图；借用所有结果和下层。</remarks>
    public SkiaGraphSurface CompositeChain(IReadOnlyList<ImageGraphResult> results, SkiaGraphSurface below,
        ImageSurfaceBudgetFrame? surfaceBudget = null, double opacity = 1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(results); ArgumentNullException.ThrowIfNull(below);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); cancellationToken.ThrowIfCancellationRequested();
            var last = ValidateCompositionChain(results, opacity, cancellationToken);
            var context = last.Context;
            var masks = CompositionMasks(results);
            if (masks.Length == 0) return Composite(last, below, surfaceBudget, opacity, cancellationToken);
            ValidateSurface(below, ShaderSurfaceKind.Color, "composite.below", cancellationToken);
            if (!below.Descriptor.Bounds.Contains(last.Output.Descriptor.Bounds)
                || masks.Any(mask => !below.Descriptor.Bounds.Contains(mask.Descriptor.Bounds)))
                throw Error("GraphCompositeBoundsExceeded", "composite.chain", "下层框不能裁掉颜色或任何历史替换范围。");
            foreach (var mask in masks) ValidateSurface(mask, ShaderSurfaceKind.Coverage, "composite.chain.coverage", cancellationToken);
            using var ownFrame = surfaceBudget is null ? _surfaceBudget.BeginFrame() : null;
            var resources = surfaceBudget ?? ownFrame!;
            var inputs = masks.Append(last.Output).Append(below).DistinctBy(surface => surface.Image.Handle).ToArray();
            using var borrowed = resources.BorrowInputs(inputs);
            long inputBytes = inputs.Sum(Bytes);
            SkiaGraphSurface accumulated = masks[0]; SkiaGraphSurface? ownedUnion = null;
            try
            {
                for (int i = 1; i < masks.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var a = accumulated.Descriptor.Bounds; var b = masks[i].Descriptor.Bounds;
                    var shape = Aligned(new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom)), context);
                    CheckBudget(inputs.Length + (ownedUnion is null ? 1 : 2), checked(inputBytes + (ownedUnion is null ? 0 : Bytes(ownedUnion)) + SizeBytes(shape, context)));
                    using var left = InputShader(accumulated, GraphEdgeMode.Transparent); using var right = InputShader(masks[i], GraphEdgeMode.Transparent);
                    using var union = _coverageUnion.CreateShader(UniformValues.Empty, new Dictionary<string, RuntimeShaderInput>
                        { ["a"] = new(left, ShaderSurfaceKind.Coverage), ["b"] = new(right, ShaderSurfaceKind.Coverage) }, cancellationToken);
                    var next = Raster(shape, context, ShaderSurfaceKind.Coverage, canvas => Draw(canvas, shape, union), resources, cancellationToken);
                    ownedUnion?.Dispose(); ownedUnion = next; accumulated = next;
                }
                var outputBounds = Aligned(below.Descriptor.Bounds, context);
                CheckBudget(inputs.Length + (ownedUnion is null ? 1 : 2), checked(inputBytes + (ownedUnion is null ? 0 : Bytes(ownedUnion)) + SizeBytes(outputBounds, context)));
                using var output = InputShader(last.Output, GraphEdgeMode.Transparent); using var background = InputShader(below, GraphEdgeMode.Transparent);
                using var coverage = InputShader(accumulated, GraphEdgeMode.Transparent);
                using var shader = _chainReplace.CreateShader(new([new("opacity", UniformValue.Float(opacity)), new("sourceOver", UniformValue.Int(IsReplacement(last) ? 0 : 1))]),
                    new Dictionary<string, RuntimeShaderInput> { ["result"] = new(output, ShaderSurfaceKind.Color), ["below"] = new(background, ShaderSurfaceKind.Color), ["coverage"] = new(coverage, ShaderSurfaceKind.Coverage) }, cancellationToken);
                return Raster(outputBounds, context, ShaderSurfaceKind.Color, canvas => Draw(canvas, outputBounds, shader), resources, cancellationToken);
            }
            finally { ownedUnion?.Dispose(); }
        }
    }

    private static bool IsReplacement(ImageGraphResult result) => result.Diagnostics.IsEmpty
        && result.Plan.Definition.Composite == GraphCompositeMode.ReplaceCoveredBackdrop;

    private ImageGraphResult ValidateCompositionChain(IReadOnlyList<ImageGraphResult> results, double opacity, CancellationToken cancellationToken)
    {
        if (results.Count == 0 || results.Any(result => result is null))
            throw Error("GraphCompositeChainEmpty", "composite.chain", "链必须包含存活的有序结果。");
        var last = results[^1];
        if (!ReferenceEquals(last.Plan, Plan)) throw Error("GraphResultMismatch", "composite.chain", "最后产物不属于固定图代次。");
        ValidateCompositionOpacity(opacity);
        foreach (var result in results)
        {
            ValidateCompositionContext(result.Context, last.Context);
            ValidateSurface(result.Output, ShaderSurfaceKind.Color, "composite.chain.output", cancellationToken);
        }
        return last;
    }
    private void ValidateCompositionContext(ImageGraphFrameContext actual, ImageGraphFrameContext expected)
    {
        if (actual.TimeUs != expected.TimeUs || actual.CanvasWidth != expected.CanvasWidth || actual.CanvasHeight != expected.CanvasHeight
            || actual.DensityX != expected.DensityX || actual.DensityY != expected.DensityY || actual.Transparent != expected.Transparent)
            throw Error("GraphCompositeChainContextMismatch", "composite.chain", "不能串接不同帧／画布／密度／输出模式。");
    }
    private void ValidateCompositionOpacity(double opacity)
    {
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1)
            throw Error("InvalidGraphOpacity", "composite.chain", "透明度必须在 [0,1]。");
        _ = Native(opacity); // 不将非零 double 下溢静默写成 float32 的零。
    }
    private SkiaGraphSurface[] CompositionMasks(IReadOnlyList<ImageGraphResult> results)
        => results.Where(IsReplacement).Select(result => result.Coverage
            ?? throw Error("GraphCompositeCoverageMissing", "composite.chain", "已执行替换的覆盖不存在。"))
            .DistinctBy(mask => mask.Image.Handle).ToArray();
}
