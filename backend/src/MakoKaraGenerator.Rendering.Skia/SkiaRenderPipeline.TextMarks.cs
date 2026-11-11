using MakoKaraGenerator.Core.Projects;
using SkiaSharp;
using static MakoKaraGenerator.Rendering.Skia.SkiaSceneGeometry;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private void DrawTextMark(SKCanvas canvas, RenderRequest request, Composition composition, SceneTextMarkSample mark, CapturePaint? capture = null, ImageSurfaceBudgetFrame? resources = null)
    {
        // 全部装饰统一应用一次节点透明度，避免阴影过滤器再次乘入填充透明度。
        MakoKaraGenerator.Scripting.TextMarkTarget target = new(new(mark.NodeId), new(mark.CueId), new(mark.Geometry.BindingId!));
        var prepared = request.PreparedFrame;
        var effectiveStyle = prepared?.MarkStyles.Any(item => item.Target == target) == true
            ? prepared.GetMarkDrawingStyle(target) : mark.Appearance.ToLyricStyle();
        var style = effectiveStyle with { Opacity = 1 };
        double opacity = capture?.IsCoverage == true || capture?.SuppressCommonOpacity == true ? 1 : mark.Opacity * effectiveStyle.Opacity;
        var options = LayoutOptionsFor(style);
        var shaped = _geometry.GetLayout(mark.Text, options);
        var geometry = mark.Geometry;
        var finalMark = prepared?.FinalMarkGeometry.SingleOrDefault(item => item.Target == target);
        var warpMapping = finalMark?.Geometry.Mapping is { } mapped
            ? WithoutWorld(mapped) : null;
        canvas.Save();
        bool layer = opacity < 1;
        using var opacityReservation = layer ? capture?.Layer("CaptureMarkOpacityLayer") : null;
        if (layer)
        {
            using SKPaint paint = new() { Color = SKColors.White.WithAlpha(ToOpacityByte((float)opacity)) };
            canvas.SaveLayer(paint);
        }
        var basis = geometry.WorldMatrix!.Value;
        var matrix = finalMark?.Geometry.BaseWorldMatrix
            ?? new MakoKaraGenerator.Scripting.Affine2(basis.A, basis.B, basis.C, basis.D, basis.Tx, basis.Ty);
        canvas.Concat(new SKMatrix((float)matrix.A, (float)matrix.C, (float)matrix.Tx,
            (float)matrix.B, (float)matrix.D, (float)matrix.Ty, 0, 0, 1));
        if (warpMapping is not null)
        {
            var bounds = PreparedRenderFrame.WarpDecoratedBounds(TextSourceBounds(shaped, (float)geometry.OriginX,
                (float)geometry.BaselineY, default), style);
            DrawWarpedTextLayer(canvas, request, new MakoKaraGenerator.Scripting.V2.TextTarget.Mark(target), warpMapping,
                bounds, resources ?? capture?.Budget, capture, Draw);
        }
        else Draw(canvas, capture);
        void Draw(SKCanvas source, CapturePaint? sourceCapture)
        {
            DrawLineGlow(source, shaped, options, (float)geometry.OriginX, (float)geometry.BaselineY,
                style, request.Quality != RenderQuality.Draft, capture: sourceCapture);
            var fill = mark.IsElapsed ? style.SungFill : style.UnsungFill;
            DrawStyledLine(source, shaped, options, (float)geometry.OriginX, (float)geometry.BaselineY,
                fill.Color, fill.Gradient, style, composition, request.Quality != RenderQuality.Draft,
                capture: sourceCapture, sungPass: mark.IsElapsed);
        }
        RestoreAnimationLayer(canvas, layer);
        canvas.Restore();
    }
}
