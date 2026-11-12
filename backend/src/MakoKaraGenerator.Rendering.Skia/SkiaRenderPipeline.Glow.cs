using System.Collections.Immutable;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Text;
using SkiaSharp;
using static MakoKaraGenerator.Rendering.Skia.SkiaSceneGeometry;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    // 外发光先聚合整体轮廓（字形加本态全部描边，D-P3-9），再过滤一次；与填充、材质 alpha 和唱词切分无关。
    // 遮罩是轮廓再外扩扩张量，最后减去轮廓本身：辉光从描边外缘起算，半透明描边下也不透出辉光。
    // 两态辉光相同（含已唱跟随）时，调用方在两遍之前整段画一次（state 为 null）；
    // 不同时改在每一遍的演唱裁剪内各画本态一次（state 为该遍的态），整段那一次不画（P3 设计 §4）。
    private void DrawLineGlow(SKCanvas canvas, ShapedLine shaped, TextLayoutOptions options,
        float origin, float baseline, LyricStyle style, bool antialias,
        ImmutableArray<int> units = default, CapturePaint? capture = null, bool ruby = false, bool? state = null)
    {
        if (capture?.IncludesGlyph(ruby) == false || capture is { Part: not null, GlowOnly: false }
            || capture?.RequestedSungPass == true) return;
        if ((state is null) == style.GlowDiffersByState) return;
        if (state is { } sung) style = style.ForState(sung);
        // 两态辉光与轮廓都相同时整段一次（state 为 null），这时未唱一侧的描边就是两态共同的轮廓。
        StrokeLayers outline = style.Unsung.Strokes;
        float reach = outline.IsEnabled ? outline.TotalWidthPx : 0f;
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
            bool spread = expanded && glow.SpreadPx > 0;
            DrawOutline(canvas, shaped, options, origin, baseline, reach + (spread ? glow.SpreadPx : 0f),
                spread ? SKStrokeJoin.Round : ToSkStrokeJoin(outline.OuterJoin), SKColors.White, antialias, units,
                expanded ? SKBlendMode.SrcOver : SKBlendMode.DstOut);
            canvas.Restore();
        }
    }
}
