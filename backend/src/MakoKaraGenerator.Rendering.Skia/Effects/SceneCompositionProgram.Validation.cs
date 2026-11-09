using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

internal sealed partial class SceneCompositionProgram
{
    private static void ValidateFrame(ImageGraphFrameContext frame)
    {
        if (frame.Parameters == null || frame.Uniforms is null || frame.Transforms is null || frame.TimeUs < 0 || !frame.TargetBounds.IsValid || !Positive(frame.CanvasWidth) || !Positive(frame.CanvasHeight)
            || !Positive(frame.DensityX) || !Positive(frame.DensityY)) throw Error("InvalidGraphFrame", "frame", "固定帧、逻辑尺寸或密度无效。");
    }
    private void ValidateSurface(SkiaGraphSurface surface, ShaderSurfaceKind kind, string field, CancellationToken cancellationToken)
    {
        var image = surface.Image; var d = surface.Descriptor;
        if (image.Handle == nint.Zero || surface.Kind != kind || d.Space != GraphCoordinateSpace.Canvas || d.LocalToCanvas != SceneMatrix.Identity
            || !d.Bounds.IsValid || !Positive(d.DensityX) || !Positive(d.DensityY) || image.AlphaType != SKAlphaType.Premul
            || image.ColorType is not (SKColorType.Bgra8888 or SKColorType.Rgba8888) || Math.Abs(d.Bounds.Width * d.DensityX - image.Width) > 1e-6
            || Math.Abs(d.Bounds.Height * d.DensityY - image.Height) > 1e-6)
            throw Error("InvalidGraphSurface", field, "真实图像、编码、Canvas 几何或密度不符。");
        if (image.Width > _budget.MaximumPixelDimension || image.Height > _budget.MaximumPixelDimension)
            throw Error("GraphSurfaceDimensionExceeded", field, "输入每边像素超预算。");
        if (kind == ShaderSurfaceKind.Color)
        {
            if (image.ColorSpace?.IsSrgb != true) throw Error("GraphColorSpaceMismatch", field, "v1 颜色输入须明确采用 sRGB 编码。");
            if (_pixelChecks) ValidateColorPixels(surface, field, cancellationToken);
        }
    }
    private GraphRect Aligned(GraphRect bounds, ImageGraphFrameContext frame)
    {
        double left = Math.Floor(bounds.Left * frame.DensityX), top = Math.Floor(bounds.Top * frame.DensityY), right = Math.Ceiling(bounds.Right * frame.DensityX), bottom = Math.Ceiling(bounds.Bottom * frame.DensityY);
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(right) || !double.IsFinite(bottom) || right <= left || bottom <= top
            || right - left > _budget.MaximumPixelDimension || bottom - top > _budget.MaximumPixelDimension)
            throw Error("GraphSurfaceDimensionExceeded", "bounds", "实际像素框无效或每边超预算。");
        var aligned = new GraphRect(left / frame.DensityX, top / frame.DensityY, right / frame.DensityX, bottom / frame.DensityY);
        if (!aligned.IsValid) throw Error("InvalidGraphFrame", "bounds", "像素对齐无法保留有限框。"); return aligned;
    }
    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
    private static long Bytes(SkiaGraphSurface surface) => checked((long)surface.Image.Width * surface.Image.Height * 4);
    private static long SizeBytes(GraphRect shape, ImageGraphFrameContext frame) => checked((long)Math.Round(shape.Width * frame.DensityX) * (long)Math.Round(shape.Height * frame.DensityY) * 4);
    private void CheckBudget(int count, long bytes)
    { if (count > _budget.MaximumSurfaces || bytes > _budget.MaximumBytes) throw Error("GraphSurfaceBudgetExceeded", "budget", $"活动表面 {count}/{_budget.MaximumSurfaces}，字节 {bytes}/{_budget.MaximumBytes}"); }
    private static ImageGraphException Error(string code, string field, string message) => new(code, "host/scene-composition", field, message);
    private Action? AcquireDeviceLease() => _deviceLifetime?.Acquire();

    private static bool IsReplacement(ImageGraphResult result) => result.Diagnostics.IsEmpty
        && result.Plan.Definition.Composite == GraphCompositeMode.ReplaceCoveredBackdrop;

    private ImageGraphResult ValidateCompositionChain(IReadOnlyList<ImageGraphResult> results, double opacity, CancellationToken cancellationToken)
    {
        if (results.Count == 0 || results.Any(result => result is null))
            throw Error("GraphCompositeChainEmpty", "composite.chain", "链必须包含存活的有序结果。");
        var last = results[^1];
        ValidateCompositionOpacity(opacity);
        foreach (var result in results)
        {
            ValidateFrame(result.Context);
            if (result.Plan.Definition.Space != GraphCoordinateSpace.Canvas) throw Error("UnsupportedGraphExecutionSpace", "composite.chain", "闭合链必须位于 Canvas。");
            ValidateCompositionContext(result.Context, last.Context);
            ValidateSurface(result.Output, ShaderSurfaceKind.Color, "composite.chain.output", cancellationToken);
        }
        return last;
    }
    private static void ValidateCompositionContext(ImageGraphFrameContext actual, ImageGraphFrameContext expected)
    {
        if (actual.TimeUs != expected.TimeUs || actual.CanvasWidth != expected.CanvasWidth || actual.CanvasHeight != expected.CanvasHeight
            || actual.DensityX != expected.DensityX || actual.DensityY != expected.DensityY || actual.Transparent != expected.Transparent)
            throw Error("GraphCompositeChainContextMismatch", "composite.chain", "不能串接不同帧／画布／密度／输出模式。");
    }
    private static void ValidateCompositionOpacity(double opacity)
    {
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1)
            throw Error("InvalidGraphOpacity", "composite.chain", "透明度必须在 [0,1]。");
        _ = Native(opacity); // 不将非零 double 下溢静默写成 float32 的零。
    }
    private static SkiaGraphSurface[] CompositionMasks(IReadOnlyList<ImageGraphResult> results)
        => results.Where(IsReplacement).Select(result => result.Coverage
            ?? throw Error("GraphCompositeCoverageMissing", "composite.chain", "已执行替换的覆盖不存在。"))
            .DistinctBy(mask => mask.Image.Handle).ToArray();
}
