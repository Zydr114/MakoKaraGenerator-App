using System.Collections.Immutable;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Text;
using SkiaSharp;
using static MakoKaraGenerator.Rendering.Skia.SkiaSceneGeometry;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private void DrawStyledLine(
        SKCanvas canvas,
        ShapedLine shaped,
        TextLayoutOptions options,
        float originX,
        float baselineY,
        RgbaColor color,
        GradientSpec? gradient,
        LyricStyle style,
        Composition composition,
        bool antialias,
        ImmutableArray<int> drawUnitIndices = default, CapturePaint? capture = null, bool sungPass = false, bool rubyGlyph = false,
        LineSpan? lineSpan = null)
    {
        if (capture?.IncludesGlyph(rubyGlyph) == false || capture?.GlowOnly == true) return;
        if (capture?.RequestedSungPass is { } requested && requested != sungPass) return;
        if (capture?.SungOnly == true && !sungPass) return;
        // 每一遍只读本态的描边、阴影（P3 设计 §4）；已唱全部跟随时就是原样式。
        style = style.ForState(sungPass);
        // 阴影以整体轮廓（字形加本态全部描边）为来源（D-P3-9）：轮廓取捕获按部件裁掉描边之前的本态描边。
        StrokeLayers outline = style.Unsung.Strokes;
        if (capture is not null)
        {
            style = capture.Style(style);
            var stateFill = sungPass ? style.SungFill : style.UnsungFill; color = stateFill.Color; gradient = stateFill.Gradient;
        }
        if (capture?.MaterialFallback is not null && gradient is not null)
        {
            ValidateNativeGradient(gradient, shaped, style, composition, lineSpan);
        }
        // 不支持的渐变组合明确拒绝使用，保持纯色基色；绝不伪装成旧端点渐变。
        var device = canvas.TotalMatrix;
        using var fill = CreateTextShading(gradient, shaped, originX, baselineY, style, composition, device, lineSpan);
        if (capture?.MaterialFallback is not null && gradient is not null && fill.IsEmpty)
            throw new PreparedFrameException("PackageMaterialFallbackUnsupported", "不能将材质基础渐变静默降级为停止点颜色。");

        ShadowSpec? shadowSpec = EffectiveShadow(style);
        bool outlined = outline.IsEnabled;
        bool customShadow = shadowSpec is { IsEnabled: true }
            && (shadowSpec.Gradient is not null || shadowSpec.SpreadPx > 0f || shadowSpec.BlurRadiusPx == 0f);
        if (customShadow)
        {
            DrawCustomShadow(canvas, shaped, options, originX, baselineY, shadowSpec!, outline, style, composition, device, antialias,
                drawUnitIndices, capture, lineSpan);
            if (capture?.ShadowOnly == true) return;
        }
        // 无描边且为经典纯色模糊阴影时仍挂在填充那一笔的滤镜上（像素与 P3 之前相同）；有描边时先单独画整体轮廓的阴影，再画描边与填充。
        using SKImageFilter? shadow = customShadow ? null : CreateTextShadow(shadowSpec, style.Opacity, capture?.ShadowOnly == true || outlined);
        if (!customShadow && outlined)
        {
            if (capture?.ShadowOnly == true && shadow is null && shadowSpec is { IsEnabled: true })
                throw new PreparedFrameException("CaptureShadowFilterUnavailable", "无法创建真实 shadow-only 滤镜，不能当作没有阴影。");
            if (shadow is not null)
            {
                capture?.MarkDrawn(canvas);
                DrawOutline(canvas, shaped, options, originX, baselineY, outline.TotalWidthPx, ToSkStrokeJoin(outline.OuterJoin),
                    SKColors.White, antialias, drawUnitIndices, SKBlendMode.SrcOver, shadow);
            }
            if (capture?.ShadowOnly == true) return;
        }

        // 扫字线高亮在本态填充与各圈描边画完后各叠一遍（P3 设计 §10.3）；没有峰时绘制与之前完全相同。
        var peaks = HighlightPeaks(rubyGlyph, capture);
        if (style.Unsung.Strokes is { IsEnabled: true } strokes)
            DrawStrokes(canvas, shaped, options, originX, baselineY, strokes, style, composition, device, antialias, drawUnitIndices, capture, lineSpan, peaks);

        if (capture?.StrokeOnly == true) return;
        DrawFill();

        void DrawFill()
        {
            if (capture?.MaterialFallback is not null || capture is { IsCoverage: true, Part: not null })
            {
                // Raw 材质区域与基础外观使用同一离屏字形路径，避免 GPU 对直接和 SaveLayer 字形采用不同栅格原点。
                using var materialLayerReservation = capture.Layer("CaptureMaterialFallbackLayer");
                capture.MarkDrawn(canvas);
                int materialLayer = canvas.SaveLayer();
                _geometry.LayoutEngine.DrawLine(canvas, shaped, options, originX, baselineY, SKColors.White, antialias,
                    drawUnitIndices: drawUnitIndices, unitShader: fill.UnitShader);
                if (fill.UnitShader is null)
                {
                    using var regionColor = new SKPaint { BlendMode = SKBlendMode.SrcIn, Shader = fill.Shared,
                        Color = fill.Shared is null ? ToSkColorWithOpacity(color, 1) : SKColors.White };
                    canvas.DrawPaint(regionColor);
                }
                canvas.RestoreToCount(materialLayer); return;
            }
            if (capture?.ShadowOnly == true && shadow is null)
            {
                if (shadowSpec is { IsEnabled: true }) throw new PreparedFrameException("CaptureShadowFilterUnavailable", "无法创建真实 shadow-only 滤镜，不能当作没有阴影。");
                return;
            }
            capture?.MarkDrawn(canvas);
            _geometry.LayoutEngine.DrawLine(
                canvas, shaped, options, originX, baselineY,
                // shader 色标已包含样式 alpha；画笔不能再乘历史纯色 alpha 或样式 alpha。
                fill.IsEmpty ? ToSkColorWithOpacity(color, style.Opacity) : SKColors.White, antialias,
                SKPaintStyle.Fill, 0f, fill.Shared, outlined ? null : shadow, drawUnitIndices: drawUnitIndices, unitShader: fill.UnitShader);
            ApplyHighlight(canvas, peaks, originX, (shader, blender) => _geometry.LayoutEngine.DrawLine(canvas, shaped, options, originX, baselineY,
                SKColors.White, antialias, shader: shader, drawUnitIndices: drawUnitIndices, blender: blender));
        }
    }

    /// <summary>
    /// 在一张层里画多重描边（P3 设计 §6.2）：由外向内，画笔宽度取到该层外缘的累计宽度的两倍；最外层普通叠加，
    /// 内层用替换混合覆盖外层的内侧（半透明内圈不透出外圈颜色，边缘仍有抗锯齿）；最后用字形减去内部。
    /// 外圈描边部件只画第 2 层起的各圈，并减去最内层占据的范围。单层时与 P3 之前的画法逐像素相同。
    /// </summary>
    private void DrawStrokes(SKCanvas canvas, ShapedLine shaped, TextLayoutOptions options, float originX, float baselineY,
        StrokeLayers strokes, LyricStyle style, Composition composition, SKMatrix device, bool antialias,
        ImmutableArray<int> drawUnitIndices, CapturePaint? capture, LineSpan? lineSpan,
        IReadOnlyList<HighlightPeak> peaks)
    {
        int first = capture?.OuterStrokeOnly == true ? 1 : 0;
        if (strokes.Count <= first) return;
        var reach = new float[strokes.Count];
        for (int index = 0; index < strokes.Count; index++) reach[index] = (index == 0 ? 0f : reach[index - 1]) + strokes[index].WidthPx;
        var shadings = new TextShading?[strokes.Count];
        try
        {
            for (int index = first; index < strokes.Count; index++)
            {
                var spec = strokes[index];
                if (spec.Gradient is { } strokeGradient)
                {
                    if (!GradientSupport.IsSupported(strokeGradient))
                        throw new PreparedFrameException("UnsupportedEffectGradient", "描边渐变尚未接入后端：" + GradientSupport.UnsupportedReason(strokeGradient));
                    ValidateNativeGradient(strokeGradient, shaped, style, composition, lineSpan);
                }
                shadings[index] = CreateTextShading(spec.Gradient, shaped, originX, baselineY, style, composition, device, lineSpan);
                if (spec.Gradient is not null && shadings[index]!.IsEmpty)
                    throw new PreparedFrameException("GradientShaderUnavailable", "无法创建实际描边渐变，不能改用纯色或隐藏描边。");
            }
            // 材质基础外观（只有最内层一圈）先画白色轮廓再按区域上色；按单字时每个字簇的颜色不同，改为直接带各自的着色器绘制。
            bool regionColored = capture?.MaterialFallback is not null && shadings[first]!.UnitShader is null;
            using var strokeReservation = capture?.Layer("CaptureStrokeLayer");
            // 在独立图层画外扩轮廓，再用不透明字形遮罩减去内部。
            // 遮罩不读取填充透明度，因此透明填充也不会露出内半边描边。
            capture?.MarkDrawn(canvas);
            int strokeLayer = canvas.SaveLayer();
            for (int index = strokes.Count - 1; index >= first; index--)
            {
                var spec = strokes[index]; var shading = shadings[index]!;
                _geometry.LayoutEngine.DrawLine(
                    canvas, shaped, options, originX, baselineY,
                    regionColored || !shading.IsEmpty ? SKColors.White : ToSkColorWithOpacity(spec.Color ?? RgbaColor.Transparent, style.Opacity), antialias,
                    SKPaintStyle.Stroke, reach[index] * 2f,
                    shader: regionColored ? null : shading.Shared,
                    strokeJoin: ToSkStrokeJoin(spec.Join),
                    blendMode: index == strokes.Count - 1 ? SKBlendMode.SrcOver : SKBlendMode.Src,
                    drawUnitIndices: drawUnitIndices, unitShader: shading.UnitShader);
            }
            // Raw 覆盖减除与 GlyphCoverage 同样的白色字形；锁定后端的黑／白抗锯齿边缘并不完全相同。
            // 常规 TargetColor 保留原黑色减除，确保已有预览和捕获像素一致。
            var cut = capture?.IsCoverage == true || capture?.MaterialFallback is not null ? SKColors.White : SKColors.Black;
            if (first == 0)
                _geometry.LayoutEngine.DrawLine(canvas, shaped, options, originX, baselineY, cut, antialias,
                    blendMode: SKBlendMode.DstOut, drawUnitIndices: drawUnitIndices);
            else
                DrawOutline(canvas, shaped, options, originX, baselineY, strokes[0].WidthPx, ToSkStrokeJoin(strokes[0].Join),
                    cut, antialias, drawUnitIndices, SKBlendMode.DstOut);
            // 描边层里只有描边：高亮直接画满峰的范围，混合器保持目标 α，没有描边的地方不变（P3 设计 §10.3）。
            if (!regionColored)
                ApplyHighlight(canvas, peaks, originX, (shader, blender) =>
                {
                    using var paint = new SKPaint { Shader = shader, Blender = blender };
                    canvas.DrawRect(canvas.LocalClipBounds, paint);
                });
            if (regionColored)
            {
                var shared = shadings[first]!.Shared;
                using var regionColor = new SKPaint { BlendMode = SKBlendMode.SrcIn, Shader = shared,
                    Color = shared is null ? ToSkColorWithOpacity(strokes[first].Color ?? RgbaColor.Transparent, 1) : SKColors.White };
                canvas.DrawPaint(regionColor);
            }
            canvas.RestoreToCount(strokeLayer);
        }
        finally
        {
            foreach (var shading in shadings) shading?.Dispose();
        }
    }

    /// <summary>绘制硬边（blur=0）、外扩张或渐变阴影；以整体轮廓（字形加全部描边再外扩 SpreadPx）为来源。</summary>
    private void DrawCustomShadow(SKCanvas canvas, ShapedLine shaped, TextLayoutOptions options, float originX, float baselineY,
        ShadowSpec shadowSpec, StrokeLayers outline, LyricStyle style, Composition composition, SKMatrix device, bool antialias,
        ImmutableArray<int> drawUnitIndices, CapturePaint? capture, LineSpan? lineSpan)
    {
        float effectiveOpacity = style.Opacity * shadowSpec.Opacity;
        if (effectiveOpacity <= 0f || drawUnitIndices is { IsDefault: false, IsEmpty: true }) return;
        bool spread = shadowSpec.SpreadPx > 0f;
        float radius = (outline.IsEnabled ? outline.TotalWidthPx : 0f) + (spread ? shadowSpec.SpreadPx : 0f);
        if (!float.IsFinite(radius) || !float.IsFinite(radius * 2f))
            throw new PreparedFrameException("UnsupportedNativeStyleValues", "阴影外扩张超出后端有限像素范围，不能夹值。");
        SKStrokeJoin join = spread ? SKStrokeJoin.Round : ToSkStrokeJoin(outline.OuterJoin);

        using var blur = shadowSpec.BlurRadiusPx > 0f ? SKImageFilter.CreateBlur(shadowSpec.BlurRadiusPx, shadowSpec.BlurRadiusPx) : null;
        if (shadowSpec.BlurRadiusPx > 0f && blur is null)
            throw new PreparedFrameException("CaptureShadowFilterUnavailable", "无法创建阴影模糊滤镜，不能当作没有阴影。");

        if (shadowSpec.Gradient is { } shadowGradient)
        {
            if (!GradientSupport.IsSupported(shadowGradient))
                throw new PreparedFrameException("UnsupportedEffectGradient", "阴影渐变尚未接入后端：" + GradientSupport.UnsupportedReason(shadowGradient));
            ValidateNativeGradient(shadowGradient, shaped, style, composition, lineSpan);
            byte alphaByte = ToOpacityByte(effectiveOpacity);
            bool needsLayer = blur is not null || alphaByte < 255;
            using var shading = CreateTextShading(shadowGradient, shaped, originX, baselineY, style, composition, device, lineSpan,
                opacityOverride: needsLayer ? 1f : effectiveOpacity);
            if (shading.IsEmpty)
                throw new PreparedFrameException("GradientShaderUnavailable", "无法创建实际阴影渐变，不能改用纯色或隐藏阴影。");

            capture?.MarkDrawn(canvas);
            if (needsLayer)
            {
                using var layerReservation = capture?.Layer("CaptureShadowLayer");
                using var layerPaint = new SKPaint { ImageFilter = blur, Color = SKColors.White.WithAlpha(alphaByte) };
                int layer = canvas.SaveLayer(layerPaint);
                canvas.Translate(shadowSpec.OffsetXPx, shadowSpec.OffsetYPx);
                DrawOutline(canvas, shaped, options, originX, baselineY, radius, join, SKColors.White, antialias,
                    drawUnitIndices, SKBlendMode.SrcOver, shader: shading.Shared, unitShader: shading.UnitShader);
                canvas.RestoreToCount(layer);
            }
            else
            {
                int saved = canvas.Save();
                canvas.Translate(shadowSpec.OffsetXPx, shadowSpec.OffsetYPx);
                DrawOutline(canvas, shaped, options, originX, baselineY, radius, join, SKColors.White, antialias,
                    drawUnitIndices, SKBlendMode.SrcOver, shader: shading.Shared, unitShader: shading.UnitShader);
                canvas.RestoreToCount(saved);
            }
            return;
        }

        SKColor solidColor = ToSkColorWithOpacity(shadowSpec.Color, effectiveOpacity);
        if (solidColor.Alpha == 0) return;
        capture?.MarkDrawn(canvas);
        bool needsSolidLayer = blur is not null || solidColor.Alpha < 255;
        if (needsSolidLayer)
        {
            using var layerReservation = capture?.Layer("CaptureShadowLayer");
            using var tint = SKColorFilter.CreateBlendMode(solidColor, SKBlendMode.SrcIn);
            using var layerPaint = new SKPaint { ImageFilter = blur, ColorFilter = tint };
            int layer = canvas.SaveLayer(layerPaint);
            canvas.Translate(shadowSpec.OffsetXPx, shadowSpec.OffsetYPx);
            DrawOutline(canvas, shaped, options, originX, baselineY, radius, join, SKColors.White, antialias,
                drawUnitIndices, SKBlendMode.SrcOver);
            canvas.RestoreToCount(layer);
        }
        else
        {
            int saved = canvas.Save();
            canvas.Translate(shadowSpec.OffsetXPx, shadowSpec.OffsetYPx);
            DrawOutline(canvas, shaped, options, originX, baselineY, radius, join, solidColor, antialias,
                drawUnitIndices, SKBlendMode.SrcOver);
            canvas.RestoreToCount(saved);
        }
    }

    /// <summary>画字形向外扩 <paramref name="radius"/> 的实心轮廓（整体轮廓或某一圈的外缘）；半径为 0 时就是字形本身。</summary>
    private void DrawOutline(SKCanvas canvas, ShapedLine shaped, TextLayoutOptions options, float originX, float baselineY,
        float radius, SKStrokeJoin join, SKColor color, bool antialias, ImmutableArray<int> drawUnitIndices,
        SKBlendMode blendMode, SKImageFilter? imageFilter = null, SKShader? shader = null, Func<ShapedDrawUnit, SKShader?>? unitShader = null)
        => _geometry.LayoutEngine.DrawLine(canvas, shaped, options, originX, baselineY, color, antialias,
            radius > 0f ? SKPaintStyle.StrokeAndFill : SKPaintStyle.Fill, radius * 2f, shader: shader, imageFilter: imageFilter,
            strokeJoin: join, blendMode: blendMode, drawUnitIndices: drawUnitIndices, unitShader: unitShader);

    private static SKStrokeJoin ToSkStrokeJoin(StrokeJoin join) => join switch
    {
        StrokeJoin.Miter => SKStrokeJoin.Miter,
        StrokeJoin.Bevel => SKStrokeJoin.Bevel,
        _ => SKStrokeJoin.Round,
    };

    private static SKImageFilter? CreateTextShadow(ShadowSpec? spec, float styleOpacity, bool shadowOnly)
    {
        if (spec is not { IsEnabled: true }) return null;
        var color = ToSkColorWithOpacity(spec.Color, styleOpacity * spec.Opacity);
        return shadowOnly
            ? SKImageFilter.CreateDropShadowOnly(spec.OffsetXPx, spec.OffsetYPx, spec.BlurRadiusPx, spec.BlurRadiusPx, color)
            : SKImageFilter.CreateDropShadow(spec.OffsetXPx, spec.OffsetYPx, spec.BlurRadiusPx, spec.BlurRadiusPx, color);
    }
}
