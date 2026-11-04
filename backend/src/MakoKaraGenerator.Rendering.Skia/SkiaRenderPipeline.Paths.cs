using MakoKaraGenerator.Core.Effects;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private SvgVectorProgram VectorProgram(Fx.VectorVisual visual) => _svgEffects?.GetVector(visual.Vector, _device)
        ?? throw new PreparedFrameException("SvgResourcesRequired", "SVG 图元需要当前场景的固定包资源。");

    private void ValidateNativeVector(Fx.VectorVisual visual)
    {
        ValidateNativeUnitAffine(visual.UnitToWorld);
        var bounds = VectorProgram(visual).Bounds;
        if (!FiniteSvgBounds(bounds))
            throw new PreparedFrameException("UnsupportedNativeOverlay", "SVG Picture 完整绘制边界必须有限且非空。");
        foreach (var point in SvgCorners(bounds)) NativePoint(visual.UnitToWorld.Map(point), 0, positive: false);
    }

    private OverlayImageLeases BorrowSvgInputs(RenderRequest request, ImageSurfaceBudgetFrame frameResources)
    {
        List<IDisposable> leases = [];
        try
        {
            if (request.PreparedFrame is { } frame)
                foreach (var batch in frame.OverlayBatches)
                {
                    request.CancellationToken.ThrowIfCancellationRequested();
                    switch (batch)
                    {
                        case Fx.PathBatch paths:
                            foreach (var vector in paths.Paths.Select(path => path.Vector).Distinct())
                            {
                                request.CancellationToken.ThrowIfCancellationRequested();
                                leases.Add(OverlayOperation(frame, batch, "SvgVector", () => _svgEffects!.BorrowVectorInputs(vector, frameResources, _device)));
                            }
                            break;
                        case Fx.AtlasSpriteBatch atlas:
                            foreach (var sprite in atlas.Sprites.Select(item => item.Sprite).Distinct())
                            {
                                request.CancellationToken.ThrowIfCancellationRequested();
                                leases.Add(OverlayOperation(frame, batch, "SvgAtlas", () => _svgEffects!.GetSprite(sprite, _device).Borrow(frameResources)));
                            }
                            break;
                    }
                }
            return new(leases);
        }
        catch { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); throw; }
    }

    private void DrawPaths(SKCanvas canvas, Fx.PathBatch batch, RenderRequest request,
        ImageSurfaceBudgetFrame resources, SvgRenderWorkBudget work)
    {
        foreach (var visual in batch.Paths)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            var program = VectorProgram(visual);
            Fx.Affine2 physical = SvgPhysicalMatrix(canvas.TotalMatrix, visual.UnitToWorld);
            SKRect bounds = SvgPhysicalBounds(program.Bounds, physical);
            var limits = _surfaceBudget.Limits;
            if (bounds.Width > limits.MaximumPixelDimension || bounds.Height > limits.MaximumPixelDimension)
                throw new PreparedFrameException("SvgRenderBudgetExceeded", "SVG 完整物理绘制区域超过固定边长预算。");
            double density = SvgMaximumScale(physical.A / program.Inspection.Viewport.Width,
                physical.B / program.Inspection.Viewport.Width, physical.C / program.Inspection.Viewport.Height,
                physical.D / program.Inspection.Viewport.Height);
            double filterEstimate = Math.Ceiling(program.Inspection.FilterSurfaceBytes * Math.Max(1, density * density));
            if (!double.IsFinite(filterEstimate) || filterEstimate >= long.MaxValue)
                throw new PreparedFrameException("SvgRenderBudgetExceeded", "SVG filter 的完整仿射物理工作量溢出。");
            long filterBytes = (long)filterEstimate;
            work.Charge(checked(Math.Max(program.RecordedOperations, program.Inspection.EstimatedPictureOperations)
                + program.Inspection.ExpandedPathCommands), filterBytes);

            // The native filter DAG has fixed finite regions. Reserve its conservative output,
            // live inputs and two blur scratch surfaces before DrawPicture can allocate them.
            using var filters = filterBytes > 0
                ? resources.Reserve(checked(program.Inspection.FilterPasses + 2), filterBytes, "RenderSvgFilterScratch") : null;
            bool tintLayer = !SvgWhite(visual.Tint);
            var clip = canvas.DeviceClipBounds;
            double left = Math.Max(Math.Floor(bounds.Left) - 1, clip.Left), top = Math.Max(Math.Floor(bounds.Top) - 1, clip.Top);
            double right = Math.Min(Math.Ceiling(bounds.Right) + 1, clip.Right), bottom = Math.Min(Math.Ceiling(bounds.Bottom) + 1, clip.Bottom);
            if (right - left > limits.MaximumPixelDimension || bottom - top > limits.MaximumPixelDimension)
                throw new PreparedFrameException("SvgRenderBudgetExceeded", "SVG 整体着色裁切后的物理工作面超过固定边长预算。");
            long layerBytes = right > left && bottom > top ? checked((long)(right - left) * (long)(bottom - top) * 4) : 0;
            using var layer = tintLayer && layerBytes > 0 ? resources.Reserve(1, layerBytes, "RenderSvgWholeTint") : null;
            using var filter = tintLayer ? SvgTintFilter(visual.Tint) : null;
            using var paint = tintLayer ? new SKPaint { Color = SKColors.White, ColorFilter = filter } : null;
            canvas.Save();
            try
            {
                canvas.Concat(UnitOverlayMatrix(visual.UnitToWorld));
                // SaveLayer bounds are only a hint. This explicit clip bounds both the layer
                // allocation and actual SVG execution to the preflighted complete Picture region.
                canvas.ClipRect(program.Bounds);
                if (tintLayer) canvas.SaveLayer(program.Bounds, paint);
                try { request.CancellationToken.ThrowIfCancellationRequested(); canvas.DrawPicture(program.Picture); }
                finally { if (tintLayer) canvas.Restore(); }
            }
            finally { canvas.Restore(); }
            request.CancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static SKMatrix UnitOverlayMatrix(Fx.Affine2 matrix)
        => new((float)matrix.A, (float)matrix.C, (float)matrix.Tx, (float)matrix.B, (float)matrix.D, (float)matrix.Ty, 0, 0, 1);
    private static void ValidateNativeUnitAffine(Fx.Affine2 matrix)
    {
        if (new[] { matrix.A, matrix.B, matrix.C, matrix.D, matrix.Tx, matrix.Ty }
            .Any(value => !float.IsFinite((float)value) || value != 0 && (float)value == 0)
            || !SvgNativeAffine(UnitOverlayMatrix(matrix)))
            throw new PreparedFrameException("UnsupportedNativeOverlay", "图元完整仿射矩阵超出后端精度或不可逆。");
        foreach (var point in new Fx.Point2[] { new(0, 0), new(1, 0), new(0, 1), new(1, 1) }) NativePoint(matrix.Map(point), 0, positive: false);
    }
    private static bool FiniteSvgBounds(SKRect bounds)
        => new[] { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom }.All(float.IsFinite) && bounds.Width > 0 && bounds.Height > 0;
    private static Fx.Point2[] SvgCorners(SKRect bounds)
        => [new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top), new(bounds.Left, bounds.Bottom), new(bounds.Right, bounds.Bottom)];
    internal static Fx.Affine2 SvgPhysicalMatrix(SKMatrix canvas, Fx.Affine2 local)
    {
        if (canvas.Persp0 != 0 || canvas.Persp1 != 0 || canvas.Persp2 != 1)
            throw new PreparedFrameException("UnsupportedNativeOverlay", "SVG 物理画布只支持仿射矩阵，不能忽略透视分量。");
        // The native float concat is the matrix DrawPicture/DrawImage will execute. A double
        // composition can stay finite while this actual product overflows or loses rank.
        var physical = SKMatrix.Concat(canvas, UnitOverlayMatrix(local));
        if (!SvgNativeAffine(physical))
            throw new PreparedFrameException("UnsupportedNativeOverlay", "SVG 复合物理仿射矩阵超出后端精度或不可逆。");
        return new(physical.ScaleX, physical.SkewY, physical.SkewX, physical.ScaleY, physical.TransX, physical.TransY);
    }
    private static bool SvgNativeAffine(SKMatrix matrix)
        => new[] { matrix.ScaleX, matrix.SkewY, matrix.SkewX, matrix.ScaleY, matrix.TransX, matrix.TransY, matrix.Persp0, matrix.Persp1, matrix.Persp2 }.All(float.IsFinite)
            && matrix.Persp0 == 0 && matrix.Persp1 == 0 && matrix.Persp2 == 1 && matrix.TryInvert(out var inverse)
            && new[] { inverse.ScaleX, inverse.SkewY, inverse.SkewX, inverse.ScaleY, inverse.TransX, inverse.TransY, inverse.Persp0, inverse.Persp1, inverse.Persp2 }.All(float.IsFinite);
    private static SKRect SvgPhysicalBounds(SKRect local, Fx.Affine2 matrix)
    {
        var points = SvgCorners(local).Select(matrix.Map).ToArray();
        foreach (var point in points) NativePoint(point, 0, positive: false);
        SKRect result = new((float)points.Min(point => point.X), (float)points.Min(point => point.Y),
            (float)points.Max(point => point.X), (float)points.Max(point => point.Y));
        if (!FiniteSvgBounds(result)) throw new PreparedFrameException("UnsupportedNativeOverlay", "SVG 变换后绘制区域低于后端精度或溢出。");
        return result;
    }
    private static double SvgMaximumScale(double a, double b, double c, double d)
    {
        double x = a * a + b * b, y = c * c + d * d, xy = a * c + b * d;
        return Math.Sqrt((x + y + Math.Sqrt((x - y) * (x - y) + 4 * xy * xy)) / 2);
    }
    private static bool SvgWhite(Fx.ColorRgba tint) => tint.Red == 1 && tint.Green == 1 && tint.Blue == 1 && tint.Alpha == 1;
    private static SKColorFilter SvgTintFilter(Fx.ColorRgba tint) => SKColorFilter.CreateColorMatrix([
        (float)tint.Red, 0, 0, 0, 0, 0, (float)tint.Green, 0, 0, 0,
        0, 0, (float)tint.Blue, 0, 0, 0, 0, 0, (float)tint.Alpha, 0]);

    private sealed class SvgRenderWorkBudget(long maximumOperations, long maximumFilterBytes)
    {
        private long _operations, _filterBytes;
        internal void Charge(long operations, long filterBytes)
        {
            if (operations < 0 || filterBytes < 0 || operations > maximumOperations - _operations || filterBytes > maximumFilterBytes - _filterBytes)
                throw new PreparedFrameException("SvgRenderBudgetExceeded", "本帧累计 SVG 展开绘制或物理 filter 工作量超过固定预算。");
            _operations += operations; _filterBytes += filterBytes;
        }
    }
}
