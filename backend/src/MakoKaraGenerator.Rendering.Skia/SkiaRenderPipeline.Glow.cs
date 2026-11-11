using System.Collections.Immutable;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Text;
using SkiaSharp;
using static MakoKaraGenerator.Rendering.Skia.SkiaSceneGeometry;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    // 外发光先聚合真实字形，再过滤一次；与填充、材质 alpha 和唱词切分无关。
    private void DrawLineGlow(SKCanvas canvas, ShapedLine shaped, TextLayoutOptions options,
        float origin, float baseline, LyricStyle style, bool antialias,
        ImmutableArray<int> units = default, CapturePaint? capture = null, bool ruby = false)
    {
        if (capture?.IncludesGlyph(ruby) == false || capture is { Part: not null, GlowOnly: false }
            || capture?.RequestedSungPass == true) return;
        if (capture is not null) style = capture.Style(style);
        if (style.Glow is not { IsEnabled: true } glow || style.Opacity == 0 || units is { IsDefault: false, IsEmpty: true }) return;
        ValidateGlow(glow);
        using var outerReservation = capture?.Layer("CaptureGlowExteriorLayer");
        capture?.MarkDrawn(canvas);
        int exterior = canvas.SaveLayer();
        using var maskReservation = capture?.Layer("CaptureGlowMaskLayer");
        using var blur = glow.BlurRadiusPx > 0 ? SKImageFilter.CreateBlur(glow.BlurRadiusPx, glow.BlurRadiusPx) : null;
        if (glow.BlurRadiusPx > 0 && blur is null)
            throw new PreparedFrameException("GlowFilterUnavailable", "无法创建外发光 Gaussian 滤镜，不能丢弃装饰。");
        using var tint = SKColorFilter.CreateBlendMode(ToSkColorWithOpacity(glow.Color, glow.Opacity * style.Opacity), SKBlendMode.SrcIn);
        using var paint = new SKPaint { ImageFilter = blur, ColorFilter = tint };
        int mask = canvas.SaveLayer(paint);
        DrawMask(true);
        canvas.RestoreToCount(mask);
        DrawMask(false);
        canvas.RestoreToCount(exterior);

        void DrawMask(bool expanded)
        {
            canvas.Save();
            _geometry.LayoutEngine.DrawLine(canvas, shaped, options, origin, baseline, SKColors.White, antialias,
                expanded && glow.SpreadPx > 0 ? SKPaintStyle.StrokeAndFill : SKPaintStyle.Fill,
                expanded ? glow.SpreadPx * 2 : 0, strokeJoin: SKStrokeJoin.Round,
                blendMode: expanded ? SKBlendMode.SrcOver : SKBlendMode.DstOut, drawUnitIndices: units);
            canvas.Restore();
        }
    }
}
