using System.Collections.Immutable;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>样式编辑器中可以独立开关的绘制效果。</summary>
public enum LyricStyleEffectKind
{
    /// <summary>未唱部分渐变。</summary>
    UnsungGradient,

    /// <summary>已唱部分渐变。</summary>
    SungGradient,

    /// <summary>外部描边。</summary>
    Stroke,

    /// <summary>阴影。</summary>
    Shadow,

    /// <summary>静态文字辉光。</summary>
    Glow,
}

/// <summary>集中维护现有样式效果的启用、关闭和属性范围。</summary>
/// <remarks>
/// 效果开关属于样式语义，不应只存在于某一个 Avalonia 面板里。关闭效果会清除所有绘制参数，
/// 由上层决定是否把这些属性作为本次应用的显式关闭写入覆盖层。
/// </remarks>
public static class LyricStyleEffects
{
    private static readonly ImmutableArray<LyricStyleProperty> UnsungGradientProperties =
    [
        LyricStyleProperty.UnsungGradientSpec,
    ];

    private static readonly ImmutableArray<LyricStyleProperty> SungGradientProperties =
    [
        LyricStyleProperty.SungGradientSpec,
    ];

    private static readonly ImmutableArray<LyricStyleProperty> StrokeProperties =
    [
        LyricStyleProperty.StrokeColor,
        LyricStyleProperty.StrokeWidthPx,
        LyricStyleProperty.StrokeSpec,
    ];

    private static readonly ImmutableArray<LyricStyleProperty> ShadowProperties =
    [
        LyricStyleProperty.ShadowColor,
        LyricStyleProperty.ShadowBlurPx,
        LyricStyleProperty.ShadowSpec,
    ];

    private static readonly ImmutableArray<LyricStyleProperty> GlowProperties =
    [
        LyricStyleProperty.GlowSpec, LyricStyleProperty.GlowColor, LyricStyleProperty.GlowOpacity,
        LyricStyleProperty.GlowBlurRadiusPx, LyricStyleProperty.GlowSpreadPx,
    ];

    /// <summary>读取效果是否拥有足够参数可以绘制。</summary>
    /// <param name="style">要检查的样式。</param>
    /// <param name="effect">效果类型。</param>
    /// <returns>效果可以绘制时为 <see langword="true"/>。</returns>
    public static bool IsEnabled(LyricStyle style, LyricStyleEffectKind effect)
    {
        ArgumentNullException.ThrowIfNull(style);

        return effect switch
        {
            LyricStyleEffectKind.UnsungGradient => style.UnsungFill.Gradient?.IsValid == true,
            LyricStyleEffectKind.SungGradient => style.SungFill.Gradient?.IsValid == true,
            LyricStyleEffectKind.Stroke => GetStroke(style)?.IsEnabled == true,
            LyricStyleEffectKind.Shadow => style.Shadow?.IsEnabled == true,
            LyricStyleEffectKind.Glow => style.Glow?.IsEnabled == true,
            _ => throw UnknownEffect(effect),
        };
    }

    /// <summary>返回效果涉及的样式属性，顺序也是编辑器参数区的顺序。</summary>
    /// <param name="effect">效果类型。</param>
    /// <returns>该效果的属性清单。</returns>
    public static IReadOnlyList<LyricStyleProperty> Properties(LyricStyleEffectKind effect) => effect switch
    {
        LyricStyleEffectKind.UnsungGradient => UnsungGradientProperties,
        LyricStyleEffectKind.SungGradient => SungGradientProperties,
        LyricStyleEffectKind.Stroke => StrokeProperties,
        LyricStyleEffectKind.Shadow => ShadowProperties,
        LyricStyleEffectKind.Glow => GlowProperties,
        _ => throw UnknownEffect(effect),
    };

    /// <summary>开启效果并补齐可编辑的初始参数。</summary>
    /// <param name="style">当前样式。</param>
    /// <param name="effect">效果类型。</param>
    /// <returns>带有初始效果参数的新样式。</returns>
    public static LyricStyle Enable(LyricStyle style, LyricStyleEffectKind effect)
    {
        ArgumentNullException.ThrowIfNull(style);

        return effect switch
        {
            LyricStyleEffectKind.UnsungGradient => style with
            {
                UnsungFill = style.UnsungFill with
                {
                    Gradient = style.UnsungFill.Gradient ?? new GradientSpec
                    {
                        Stops = [
                            new GradientStop(0, style.UnsungFill.Color),
                            new GradientStop(1, LiftColor(style.UnsungFill.Color)),
                        ],
                    },
                },
            },
            LyricStyleEffectKind.SungGradient => style with
            {
                SungFill = style.SungFill with
                {
                    Gradient = style.SungFill.Gradient ?? new GradientSpec
                    {
                        Stops = [
                            new GradientStop(0, style.SungFill.Color),
                            new GradientStop(1, LiftColor(style.SungFill.Color)),
                        ],
                    },
                },
            },
            LyricStyleEffectKind.Stroke => EnableStroke(style),
            LyricStyleEffectKind.Shadow => EnableShadow(style),
            LyricStyleEffectKind.Glow => style.Glow?.IsEnabled == true ? style : style with { Glow = new GlowSpec() },
            _ => throw UnknownEffect(effect),
        };
    }

    /// <summary>关闭效果并清除其所有绘制参数。</summary>
    /// <param name="style">当前样式。</param>
    /// <param name="effect">效果类型。</param>
    /// <returns>不再包含该效果参数的新样式。</returns>
    public static LyricStyle Disable(LyricStyle style, LyricStyleEffectKind effect)
    {
        ArgumentNullException.ThrowIfNull(style);

        return effect switch
        {
            LyricStyleEffectKind.UnsungGradient => style with
            {
                UnsungFill = style.UnsungFill with { Gradient = null },
            },
            LyricStyleEffectKind.SungGradient => style with
            {
                SungFill = style.SungFill with { Gradient = null },
            },
            LyricStyleEffectKind.Stroke => style with
            {
                Stroke = null,
            },
            LyricStyleEffectKind.Shadow => style with
            {
                Shadow = null,
            },
            LyricStyleEffectKind.Glow => style with { Glow = null },
            _ => throw UnknownEffect(effect),
        };
    }

    /// <summary>取得实际描边对象；没有完整对象时从当前基础颜色与宽度构造。</summary>
    /// <param name="style">静态样式。</param>
    /// <returns>实际描边描述；没有描边颜色时为空。</returns>
    public static StrokeSpec? GetStroke(LyricStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return style.Stroke;
    }

    private static LyricStyle EnableStroke(LyricStyle style)
    {
        if (IsEnabled(style, LyricStyleEffectKind.Stroke)) return style;
        StrokeSpec stroke = GetStroke(style) ?? new StrokeSpec();
        return style with
        {
            Stroke = stroke with
            {
                Color = stroke.Color ?? RgbaColor.White,
                WidthPx = stroke.WidthPx > 0f ? stroke.WidthPx : 2f,
            },
        };
    }

    private static RgbaColor LiftColor(RgbaColor color)
    {
        static byte Lift(byte channel) => (byte)Math.Min(255, channel + 48);

        return new RgbaColor(Lift(color.Red), Lift(color.Green), Lift(color.Blue), color.Alpha);
    }

    private static LyricStyle EnableShadow(LyricStyle style)
    {
        if (style.Shadow?.IsEnabled == true) return style;
        float x = style.Shadow?.OffsetXPx ?? 0f;
        float y = style.Shadow?.OffsetYPx ?? 0f;
        if (x == 0f && y == 0f) { x = 4f; y = 4f; }
        float distance = MathF.Sqrt(x * x + y * y);
        ShadowSpec shadow = new()
        {
            Color = style.Shadow?.Color ?? new RgbaColor(0, 0, 0, 160),
            Opacity = style.Shadow?.Opacity is > 0f ? style.Shadow.Opacity : 1f,
            AngleDegrees = Math.Atan2(y, x) * 180d / Math.PI,
            DistancePx = style.Shadow?.DistancePx is > 0f ? style.Shadow.DistancePx : distance,
            BlurRadiusPx = style.Shadow?.BlurRadiusPx is > 0f ? style.Shadow.BlurRadiusPx : 8f,
        };
        return style with { Shadow = shadow };
    }

    private static ArgumentOutOfRangeException UnknownEffect(LyricStyleEffectKind effect) =>
        new(nameof(effect), effect, "未知的样式效果。");
}
