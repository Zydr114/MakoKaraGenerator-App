using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    /// <summary>捕获真实摆放的背景媒体或工程底色；不包含歌词、提示或另一背景层。</summary>
    /// <remarks>缺失媒体及透明输出返回 null，不填透明替代图。显式 Canvas 框、密度、色彩及共享账与目标捕获一致。</remarks>
    public SkiaGraphSurface? CaptureSceneBackground(RenderRequest request, GraphInputSource source, GraphRect bounds,
        GraphExecutionBudget budget, ImageSurfaceBudgetFrame? surfaceBudget = null)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(budget);
        ObjectDisposedException.ThrowIf(_disposed, this); request.CancellationToken.ThrowIfCancellationRequested();
        PreparedRenderFrame.ValidateRequest(request); ValidateNativePreparedFrame(request.PreparedFrame);
        if (source is not (GraphInputSource.BackgroundMedia or GraphInputSource.BackgroundColor))
            throw new PreparedFrameException("UnsupportedBackgroundCaptureSource", "只允许真实媒体或底色；下层快照必须由场景调度器提供。");
        if (request.TransparentBackground) return null;
        var frame = source == GraphInputSource.BackgroundMedia ? ResolveBackgroundFrame(request) : null;
        if (source == GraphInputSource.BackgroundMedia && frame is null) return null;
        var composition = request.Project.Composition;
        return RasterSceneSurface(request, bounds, budget, ShaderSurfaceKind.Color, surfaceBudget, "CaptureSceneBackground", (canvas, _, _) =>
        {
            canvas.ClipRect(new(0, 0, composition.CanvasWidth, composition.CanvasHeight));
            if (frame is not null) DrawBackgroundFrame(canvas, frame, request.Project.Background, composition);
            else
            {
                using var paint = new SKPaint { Color = ToSkColor(composition.BackgroundColor), BlendMode = SKBlendMode.Src };
                canvas.DrawRect(new(0, 0, composition.CanvasWidth, composition.CanvasHeight), paint);
            }
        });
    }

    private SkiaGraphSurface RasterSceneSurface(RenderRequest request, GraphRect bounds, GraphExecutionBudget budget,
        ShaderSurfaceKind kind, ImageSurfaceBudgetFrame? surfaceBudget, string operation, Action<SKCanvas, ImageSurfaceBudgetFrame, long> draw,
        Action<long>? validateAllocation = null)
    {
        var actualTarget = request.ResolveTarget(); var composition = request.Project.Composition;
        double dx = actualTarget.ScaleXOf(composition), dy = actualTarget.ScaleYOf(composition);
        if (!bounds.IsValid || !double.IsFinite(dx) || !double.IsFinite(dy) || dx <= 0 || dy <= 0)
            throw new PreparedFrameException("InvalidCaptureBounds", "捕获框和实际密度必须有限且非空。");
        double left = Math.Floor(bounds.Left * dx), top = Math.Floor(bounds.Top * dy), right = Math.Ceiling(bounds.Right * dx), bottom = Math.Ceiling(bounds.Bottom * dy);
        double width = right - left, height = bottom - top;
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(width) || !double.IsFinite(height)
            || width <= 0 || height <= 0 || width > budget.MaximumPixelDimension || height > budget.MaximumPixelDimension
            || budget.MaximumSurfaces < 1 || width * height * 4 > budget.MaximumBytes)
            throw new PreparedFrameException("CaptureBudgetExceeded", "实际捕获尺寸／产物字节超过明确预算，不缩图或夹值。");
        var shape = new GraphRect(left / dx, top / dy, right / dx, bottom / dy);
        if (!shape.IsValid || width > int.MaxValue / 4)
            throw new PreparedFrameException("UnsupportedNativeCaptureBounds", "捕获框或实际像素行超出后端表示范围。");
        var local = CaptureMatrix(new(dx, 0, 0, dy, -left, -top));
        using var ownFrame = surfaceBudget is null ? _surfaceBudget.BeginFrame() : null;
        var resources = surfaceBudget ?? ownFrame!;
        long pixelBytes = checked((long)width * (long)height * 4);
        validateAllocation?.Invoke(pixelBytes);
        ImageSurfaceReservation? reservation = resources.Reserve(1, pixelBytes, operation);
        try
        {
            using var srgb = SKColorSpace.CreateSrgb();
            using var surface = _device.CreateSurface(new SKImageInfo(checked((int)width), checked((int)height), SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
            var canvas = surface.Canvas; canvas.ClipRect(new(0, 0, (float)width, (float)height)); canvas.Clear(SKColors.Transparent); canvas.SetMatrix(local);
            request.CancellationToken.ThrowIfCancellationRequested(); draw(canvas, resources, pixelBytes); request.CancellationToken.ThrowIfCancellationRequested();
            _device.FlushAndSubmit(surface);
            var image = surface.Snapshot() ?? throw new PreparedFrameException("CaptureSurfaceUnavailable", "无法固定捕获产物。");
            try
            {
                var result = SkiaGraphSurface.Own(image, new(shape, GraphCoordinateSpace.Canvas, SceneMatrix.Identity, dx, dy), kind, _device, reservation, _deviceLifetime?.Acquire());
                reservation = null; return result;
            }
            catch { image.Dispose(); throw; }
        }
        finally { reservation?.Dispose(); }
    }
}
