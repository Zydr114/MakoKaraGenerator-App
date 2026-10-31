using System.Collections.Immutable;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Text;
using SkiaSharp;

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
        ImmutableArray<int> drawUnitIndices = default, CapturePaint? capture = null, bool sungPass = false, bool rubyGlyph = false)
    {
        if (capture?.IncludesGlyph(rubyGlyph) == false) return;
        if (capture?.RequestedSungPass is { } requested && requested != sungPass) return;
        if (capture?.SungOnly == true && !sungPass) return;
        if (capture is not null)
        {
            style = capture.Style(style);
            var fill = sungPass ? style.SungFill : style.UnsungFill; color = fill.Color; gradient = fill.Gradient;
        }
        if (capture?.MaterialFallback is not null && gradient is not null)
        {
            ValidateNativeGradient(gradient, shaped.Width, style, composition);
        }
        // 不支持的渐变组合明确拒绝使用，保持纯色基色；绝不伪装成旧端点渐变。
        using SKShader? shader = CreateGradientShader(
            gradient,
            shaped,
            originX,
            baselineY,
            style,
            composition);
        if (capture?.MaterialFallback is not null && gradient is not null && shader is null)
            throw new PreparedFrameException("PackageMaterialFallbackUnsupported", "不能将材质基础渐变静默降级为停止点颜色。");

        ShadowSpec? shadowSpec = EffectiveShadow(style);
        using SKImageFilter? shadow = CreateTextShadow(shadowSpec, style.Opacity, capture?.ShadowOnly == true);

        StrokeSpec? strokeSpec = LyricStyleEffects.GetStroke(style);
        if (strokeSpec is { IsEnabled: true })
        {
            var strokeColor = strokeSpec.Color ?? RgbaColor.Transparent;
            if (strokeSpec.Gradient is { } strokeGradient)
            {
                if (!GradientSupport.IsSupported(strokeGradient))
                    throw new PreparedFrameException("UnsupportedEffectGradient", "描边渐变尚未接入后端：" + GradientSupport.UnsupportedReason(strokeGradient));
                ValidateNativeGradient(strokeGradient, shaped.Width, style, composition);
            }
            using var strokeShader = CreateGradientShader(strokeSpec.Gradient, shaped, originX, baselineY, style, composition);
            if (strokeSpec.Gradient is not null && strokeShader is null)
                throw new PreparedFrameException("GradientShaderUnavailable", "无法创建实际描边渐变，不能改用纯色或隐藏描边。");
            using var strokeReservation = capture?.Layer("CaptureStrokeLayer");
            // 在独立图层画外扩轮廓，再用不透明字形遮罩减去内部。
            // 遮罩不读取填充透明度，因此透明填充也不会露出内半边描边。
            int strokeLayer = canvas.SaveLayer();
            _layoutEngine.DrawLine(
                canvas, shaped, options, originX, baselineY,
                capture?.MaterialFallback is not null || strokeShader is not null ? SKColors.White : ToSkColorWithOpacity(strokeColor, style.Opacity), antialias,
                SKPaintStyle.Stroke, strokeSpec.WidthPx * 2f,
                shader: capture?.MaterialFallback is not null ? null : strokeShader,
                drawUnitIndices: drawUnitIndices,
                strokeJoin: strokeSpec.Join switch
                {
                    StrokeJoin.Miter => SKStrokeJoin.Miter,
                    StrokeJoin.Bevel => SKStrokeJoin.Bevel,
                    _ => SKStrokeJoin.Round,
                });
            // Raw 覆盖减除与 GlyphCoverage 同样的白色字形；锁定后端的黑／白抗锯齿边缘并不完全相同。
            // 常规 TargetColor 保留原黑色减除，确保已有预览和捕获像素一致。
            _layoutEngine.DrawLine(canvas, shaped, options, originX, baselineY,
                capture?.IsCoverage == true || capture?.MaterialFallback is not null ? SKColors.White : SKColors.Black, antialias,
                blendMode: SKBlendMode.DstOut, drawUnitIndices: drawUnitIndices);
            if (capture?.MaterialFallback is not null)
            {
                using var regionColor = new SKPaint { BlendMode = SKBlendMode.SrcIn, Shader = strokeShader,
                    Color = strokeShader is null ? ToSkColorWithOpacity(strokeColor, 1) : SKColors.White };
                canvas.DrawPaint(regionColor);
            }
            canvas.RestoreToCount(strokeLayer);
        }

        if (capture?.StrokeOnly == true) return;
        if (capture?.MaterialFallback is not null || capture is { IsCoverage: true, Part: not null })
        {
            // Raw 材质区域与基础外观使用同一离屏字形路径，避免 GPU 对直接和 SaveLayer 字形采用不同栅格原点。
            using var materialLayerReservation = capture.Layer("CaptureMaterialFallbackLayer");
            int materialLayer = canvas.SaveLayer();
            _layoutEngine.DrawLine(canvas, shaped, options, originX, baselineY, SKColors.White, antialias,
                drawUnitIndices: drawUnitIndices);
            using var regionColor = new SKPaint { BlendMode = SKBlendMode.SrcIn, Shader = shader,
                Color = shader is null ? ToSkColorWithOpacity(color, 1) : SKColors.White };
            canvas.DrawPaint(regionColor); canvas.RestoreToCount(materialLayer); return;
        }
        if (capture?.ShadowOnly == true && shadow is null)
        {
            if (shadowSpec is { IsEnabled: true }) throw new PreparedFrameException("CaptureShadowFilterUnavailable", "无法创建真实 shadow-only 滤镜，不能当作没有阴影。");
            return;
        }
        _layoutEngine.DrawLine(
            canvas, shaped, options, originX, baselineY,
            // shader 色标已包含样式 alpha；画笔不能再乘历史纯色 alpha 或样式 alpha。
            shader is null ? ToSkColorWithOpacity(color, style.Opacity) : SKColors.White, antialias,
            SKPaintStyle.Fill, 0f, shader, shadow, drawUnitIndices: drawUnitIndices);
    }

    private static void ValidateNativeGradient(GradientSpec gradient, float lineWidth, LyricStyle style, Composition composition)
    {
        if (!float.IsFinite((float)(gradient.AngleDegrees * Math.PI / 180)))
            throw new PreparedFrameException("UnsupportedNativeStyleValues", "渐变角度超出后端有限精度范围。");
        if (gradient.Kind != GradientKind.Radial) return;
        double width = gradient.CoordinateSpace == GradientCoordinateSpace.Canvas ? composition.CanvasWidth : Math.Max(1, lineWidth);
        double height = gradient.CoordinateSpace == GradientCoordinateSpace.Canvas ? composition.CanvasHeight : Math.Max(1, style.FontSizePx);
        double rx = gradient.RadiusX * width, ry = gradient.RadiusY * height;
        if (!float.IsFinite((float)rx) || !float.IsFinite((float)ry) || rx < 1e-4 || ry < 1e-4
            || !float.IsFinite((float)(gradient.CenterX * width)) || !float.IsFinite((float)(gradient.CenterY * height)))
            throw new PreparedFrameException("UnsupportedNativeStyleValues", "渐变实际径向中心或半径超出后端范围，不能夹值绘制。");
    }

    /// <summary>取得样式实际使用的阴影描述。</summary>
    private static ShadowSpec? EffectiveShadow(LyricStyle style)
    {
        return style.Shadow;
    }

    private static SKImageFilter? CreateTextShadow(ShadowSpec? spec, float styleOpacity, bool shadowOnly)
    {
        if (spec is not { IsEnabled: true }) return null;
        var color = ToSkColorWithOpacity(spec.Color, styleOpacity * spec.Opacity);
        return shadowOnly
            ? SKImageFilter.CreateDropShadowOnly(spec.OffsetXPx, spec.OffsetYPx, spec.BlurRadiusPx, spec.BlurRadiusPx, color)
            : SKImageFilter.CreateDropShadow(spec.OffsetXPx, spec.OffsetYPx, spec.BlurRadiusPx, spec.BlurRadiusPx, color);
    }
}
