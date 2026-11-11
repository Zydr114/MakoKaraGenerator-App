using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering.Skia;

// 描边、阴影与外发光画到字框之外的范围：排版留白、元素互补裁剪与中间表面区域都由这里得出。
public sealed partial class SkiaSceneGeometry
{
    /// <summary>按元素覆盖解析出的样式。</summary>
    internal static LyricStyle ResolveElementStyle(LyricStyle lineStyle, LyricStyleOverride overlay) =>
        StaticStyleProjection.DrawingSeed(LyricStyleResolver.Resolve(lineStyle, preset: null, lineOverride: null, tokenOverride: overlay).Style);

    /// <summary>元素区域要连同装饰一起占用的外扩量（描边外半宽、阴影偏移与模糊）。</summary>
    /// <param name="lineStyle">整行样式。</param>
    /// <param name="overlay">元素覆盖。</param>
    /// <returns>像素数。</returns>
    /// <remarks>
    /// 描边与阴影会画到字框之外：只按字框排除时，整行那一遍仍会把元素的描边外沿画出来，
    /// 元素即使明确关闭了描边也擦不掉它（实测表现为字框边界上一列基线颜色）。
    /// 这里取"整行样式"与"元素样式"两者外扩量的较大者，因为整行那一遍画的是整行样式，
    /// 而元素那一遍要能覆盖同一片区域。
    /// </remarks>
    internal static float ElementPadding(LyricStyle lineStyle, LyricStyleOverride overlay) =>
        MathF.Max(DecorationExtent(lineStyle), DecorationExtent(ResolveElementStyle(lineStyle, overlay)));

    /// <summary>一个样式会画到字框之外的像素数。</summary>
    internal static float DecorationExtent(LyricStyle style)
    {
        StrokeSpec? strokeSpec = LyricStyleEffects.GetStroke(style);
        ShadowSpec? shadowSpec = EffectiveShadow(style);
        float stroke = strokeSpec is { IsEnabled: true } ? MathF.Max(strokeSpec.WidthPx, 0f) : 0f;
        float shadow = shadowSpec is { IsEnabled: true }
            ? MathF.Max(MathF.Abs(shadowSpec.OffsetXPx), MathF.Abs(shadowSpec.OffsetYPx)) + MathF.Max(shadowSpec.BlurRadiusPx, 0f) : 0f;
        float legacy = MathF.Min(stroke + shadow + 2f, 64f);
        return style.Glow is { IsEnabled: true } glow ? Math.Max(legacy, (float)(ValidateGlow(glow) + 2)) : legacy;
    }

    /// <summary>取得样式实际使用的阴影描述。</summary>
    internal static ShadowSpec? EffectiveShadow(LyricStyle style)
    {
        return style.Shadow;
    }

    /// <summary>外发光的外扩量（扩张加三倍模糊半径）；数值超出后端有限像素范围时拒绝。</summary>
    internal static double ValidateGlow(GlowSpec glow)
    {
        double extent = (double)glow.SpreadPx + 3d * glow.BlurRadiusPx;
        // Skia 的滤镜和描边最终使用整数像素边界；不允许 float 转换或内部边界溢出。
        if (!glow.IsValid || !float.IsFinite((float)extent) || extent > int.MaxValue / 4d
            || !float.IsFinite(glow.SpreadPx * 2))
            throw new PreparedFrameException("UnsupportedNativeStyleValues", "外发光扩张或 Gaussian 范围超出后端有限像素范围，不能夹值。");
        return extent;
    }
}
