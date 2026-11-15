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
        // 标记按整体切换待唱／已过，只取当前一态的装饰（P3 设计 §4）。
        bool isTextShape = mark.Shape.Kind == CountdownShapeKind.Text;
        var pendingStyle = effectiveStyle.ForState(false) with { Opacity = 1 };
        var elapsedStyle = effectiveStyle.ForState(true) with { Opacity = 1 };
        var style = mark.IsElapsed ? elapsedStyle : pendingStyle;
        double opacity = capture?.IsCoverage == true || capture?.SuppressCommonOpacity == true ? 1 : mark.Opacity * effectiveStyle.Opacity;
        var options = LayoutOptionsFor(pendingStyle);
        Text.ShapedLine? shaped = isTextShape ? _geometry.GetLayout(mark.Text, options) : null;
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
            var sourceBounds = isTextShape
                ? TextSourceBounds(shaped!, (float)geometry.OriginX, (float)geometry.BaselineY, default)
                : new MakoKaraGenerator.Scripting.Rect2(
                    geometry.TopLeft.X,
                    geometry.TopLeft.Y,
                    Math.Max(geometry.TopLeft.X + 1d, geometry.BottomRight.X),
                    Math.Max(geometry.TopLeft.Y + 1d, geometry.BottomRight.Y));
            var bounds = PreparedRenderFrame.WarpDecoratedBounds(sourceBounds, style);
            DrawWarpedTextLayer(canvas, request, new MakoKaraGenerator.Scripting.V2.TextTarget.Mark(target), warpMapping,
                bounds, resources ?? capture?.Budget, capture, Draw);
        }
        else Draw(canvas, capture);
        void Draw(SKCanvas source, CapturePaint? sourceCapture)
        {
            if (isTextShape && mark.Animation.Mode == CountdownAnimationMode.DiscreteSteps)
            {
                DrawSingleState(source, sourceCapture, mark.IsElapsed);
                return;
            }

            switch (mark.Animation.Mode)
            {
                case CountdownAnimationMode.DiscreteSteps:
                    DrawSingleState(source, sourceCapture, mark.IsElapsed);
                    break;

                case CountdownAnimationMode.BlinkThenWipeFill:
                {
                    double p = Math.Clamp(mark.WipeProgress, 0d, 1d);
                    if (p <= 0.0001d)
                    {
                        DrawSingleState(source, sourceCapture, sungPass: false);
                        break;
                    }
                    if (p >= 0.9999d)
                    {
                        DrawSingleState(source, sourceCapture, sungPass: true);
                        break;
                    }
                    float pad = Math.Max(LayoutDecorationExtent(pendingStyle), LayoutDecorationExtent(elapsedStyle));
                    float x0 = (float)geometry.TopLeft.X - pad;
                    float x1 = (float)geometry.BottomRight.X + pad;
                    float span = Math.Max(1f, x1 - x0);
                    float feather = Math.Max(0.5f, (float)mark.Animation.FeatherRatio * span);
                    float split = x0 + (float)p * span;
                    DrawMaskedWipePass(source, split, feather, keepLeft: true, c => DrawSingleState(c, sourceCapture, sungPass: true));
                    DrawMaskedWipePass(source, split, feather, keepLeft: false, c => DrawSingleState(c, sourceCapture, sungPass: false));
                    break;
                }

                case CountdownAnimationMode.BlinkThenErase:
                {
                    double p = Math.Clamp(mark.WipeProgress, 0d, 1d);
                    if (p >= 0.9999d)
                        break;
                    if (p <= 0.0001d)
                    {
                        DrawSingleState(source, sourceCapture, sungPass: false);
                        break;
                    }
                    float pad = LayoutDecorationExtent(pendingStyle);
                    float x0 = (float)geometry.TopLeft.X - pad;
                    float x1 = (float)geometry.BottomRight.X + pad;
                    float span = Math.Max(1f, x1 - x0);
                    float feather = Math.Max(0.5f, (float)mark.Animation.FeatherRatio * span);
                    float split = x0 + (float)p * span;
                    DrawMaskedWipePass(source, split, feather, keepLeft: false, c => DrawSingleState(c, sourceCapture, sungPass: false));
                    break;
                }
            }
        }

        void DrawSingleState(SKCanvas source, CapturePaint? sourceCapture, bool sungPass)
        {
            if (isTextShape)
            {
                var stateStyle = sungPass ? elapsedStyle : pendingStyle;
                DrawLineGlow(source, shaped!, options, (float)geometry.OriginX, (float)geometry.BaselineY,
                    stateStyle, request.Quality != RenderQuality.Draft, capture: sourceCapture);
                var fill = sungPass ? stateStyle.SungFill : stateStyle.UnsungFill;
                DrawStyledLine(source, shaped!, options, (float)geometry.OriginX, (float)geometry.BaselineY,
                    fill.Color, fill.Gradient, stateStyle, composition, request.Quality != RenderQuality.Draft,
                    capture: sourceCapture, sungPass: sungPass);
            }
            else
            {
                DrawCountdownShapeState(source, request, mark, effectiveStyle, sungPass, sourceCapture);
            }
        }
        RestoreAnimationLayer(canvas, layer);
        canvas.Restore();
    }

    private static void DrawMaskedWipePass(SKCanvas canvas, float splitX, float feather, bool keepLeft, Action<SKCanvas> draw)
    {
        float half = Math.Max(0.25f, feather * 0.5f);
        SKColor[] colors = keepLeft ? [SKColors.White, SKColors.Transparent] : [SKColors.Transparent, SKColors.White];
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(splitX - half, 0f),
            new SKPoint(splitX + half, 0f),
            colors,
            [0f, 1f],
            SKShaderTileMode.Clamp);
        using var maskPaint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.DstIn };
        int layer = canvas.SaveLayer();
        draw(canvas);
        canvas.DrawPaint(maskPaint);
        canvas.RestoreToCount(layer);
    }
}
