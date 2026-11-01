namespace MakoKaraGenerator.Core.Projects;

/// <summary>歌词的规范样式；位置和动画由行与分支另行管理。</summary>
/// <remarks>平面属性只提供源码访问外观，直接读写嵌套对象，不存储第二份值。</remarks>
public sealed record LyricStyle
{
    /// <summary>字体排版参数。</summary>
    public TypographySpec Typography { get; init; } = TypographySpec.Default;
    /// <summary>未唱填充。</summary>
    public FillSpec UnsungFill { get; init; } = FillSpec.DefaultUnsung;
    /// <summary>已唱填充。</summary>
    public FillSpec SungFill { get; init; } = FillSpec.DefaultSung;
    /// <summary>描边；为空表示关闭。</summary>
    public StrokeSpec? Stroke { get; init; }
    /// <summary>阴影；为空表示关闭。</summary>
    public ShadowSpec? Shadow { get; init; }
    /// <summary>静态辉光；为空表示关闭。</summary>
    public GlowSpec? Glow { get; init; }
    /// <summary>默认排版的纵向中心比例。</summary>
    public double VerticalCenterRatio { get; init; } = .5d;
    /// <summary>文字水平对齐方式。</summary>
    public LyricHorizontalAlignment HorizontalAlignment { get; init; } = LyricHorizontalAlignment.Center;
    /// <summary>整体不透明度。</summary>
    public float Opacity { get; init; } = 1f;
    /// <summary>样式基础缩放。</summary>
    public float Scale { get; init; } = 1f;
    /// <summary>默认样式。</summary>
    public static LyricStyle Default { get; } = new();
    /// <summary>所有对象、枚举和数值是否有效。</summary>
    public bool IsValid => Typography is { IsValid: true }
        && UnsungFill is { IsValid: true } && SungFill is { IsValid: true }
        && (Stroke is null || Stroke.IsValid) && (Shadow is null || Shadow.IsValid) && (Glow is null || Glow.IsValid)
        && Enum.IsDefined(HorizontalAlignment) && double.IsFinite(VerticalCenterRatio)
        && VerticalCenterRatio is >= 0d and <= 1d && float.IsFinite(Opacity)
        && Opacity is >= 0f and <= 1f && float.IsFinite(Scale) && Scale > 0f;
    /// <summary>字体对象的 FontFamilyName 源码访问外观。</summary>
    public string? FontFamilyName { get => Typography.FontFamilyName; init => Typography = Typography with { FontFamilyName = value }; }
    /// <summary>字体回退族名链的源码访问外观。</summary>
    public System.Collections.Immutable.ImmutableArray<string> FallbackFamilyNames
    {
        get => Typography.FallbackFamilyNames;
        init => Typography = Typography with { FallbackFamilyNames = value };
    }
    /// <summary>字体对象的 FontSizePx 源码访问外观。</summary>
    public float FontSizePx { get => Typography.FontSizePx; init => Typography = Typography with { FontSizePx = value }; }
    /// <summary>字体对象的 FontWeight 源码访问外观。</summary>
    public int FontWeight { get => Typography.FontWeight; init => Typography = Typography with { FontWeight = value }; }
    /// <summary>字体对象的 IsBold 源码访问外观。</summary>
    public bool IsBold { get => Typography.IsBold; init => Typography = Typography with { IsBold = value }; }
    /// <summary>字体对象的 IsItalic 源码访问外观。</summary>
    public bool IsItalic { get => Typography.IsItalic; init => Typography = Typography with { IsItalic = value }; }
    /// <summary>字体对象的 LetterSpacingPx 源码访问外观。</summary>
    public float LetterSpacingPx { get => Typography.LetterSpacingPx; init => Typography = Typography with { LetterSpacingPx = value }; }
    /// <summary>Unsung 填充基色的源码访问外观。</summary>
    public RgbaColor UnsungColor { get => UnsungFill.Color; init => UnsungFill = UnsungFill with { Color = value }; }
    /// <summary>Unsung 填充渐变的源码访问外观。</summary>
    public GradientSpec? UnsungGradientSpec { get => UnsungFill.Gradient; init => UnsungFill = UnsungFill with { Gradient = value }; }
    /// <summary>Sung 填充基色的源码访问外观。</summary>
    public RgbaColor SungColor { get => SungFill.Color; init => SungFill = SungFill with { Color = value }; }
    /// <summary>Sung 填充渐变的源码访问外观。</summary>
    public GradientSpec? SungGradientSpec { get => SungFill.Gradient; init => SungFill = SungFill with { Gradient = value }; }
    /// <summary>描边对象的源码访问外观。</summary>
    public StrokeSpec? StrokeSpec { get => Stroke; init => Stroke = value; }
    /// <summary>描边颜色的源码访问外观。</summary>
    public RgbaColor? StrokeColor { get => Stroke?.Color; init => Stroke = (Stroke ?? new StrokeSpec { Color = null, WidthPx = 0f }) with { Color = value }; }
    /// <summary>描边宽度的源码访问外观。</summary>
    public float StrokeWidthPx { get => Stroke?.WidthPx ?? 0f; init => Stroke = (Stroke ?? new StrokeSpec { Color = null, WidthPx = 0f }) with { WidthPx = value }; }
    /// <summary>阴影对象的源码访问外观。</summary>
    public ShadowSpec? ShadowSpec { get => Shadow; init => Shadow = value; }
    /// <summary>阴影颜色的源码访问外观；为空关闭阴影。</summary>
    public RgbaColor? ShadowColor { get => Shadow?.Color; init => Shadow = value is { } color ? (Shadow ?? EmptyShadow()) with { Color = color } : null; }
    /// <summary>阴影模糊半径的源码访问外观。</summary>
    public float ShadowBlurPx { get => Shadow?.BlurRadiusPx ?? 0f; init => Shadow = (Shadow ?? EmptyShadow()) with { BlurRadiusPx = value }; }

    private static ShadowSpec EmptyShadow() => new() { Color = new RgbaColor(0, 0, 0, 0), DistancePx = 0, AngleDegrees = 0, BlurRadiusPx = 0 };

    /// <summary>创建并校验一份样式。</summary>
    /// <param name="fontSizePx">字号。</param>
    /// <param name="fontFamilyName">字体族。</param>
    /// <param name="unsungColor">未唱基色。</param>
    /// <param name="sungColor">已唱基色。</param>
    /// <param name="verticalCenterRatio">纵向中心比例。</param>
    /// <returns>新样式。</returns>
    public static LyricStyle Create(float fontSizePx, string? fontFamilyName = null,
        RgbaColor? unsungColor = null, RgbaColor? sungColor = null, double verticalCenterRatio = .5d)
    {
        LyricStyle style = new()
        {
            Typography = new TypographySpec { FontFamilyName = fontFamilyName, FontSizePx = fontSizePx },
            UnsungFill = new FillSpec { Color = unsungColor ?? FillSpec.DefaultUnsung.Color },
            SungFill = new FillSpec { Color = sungColor ?? FillSpec.DefaultSung.Color },
            VerticalCenterRatio = verticalCenterRatio,
        };
        return style.IsValid ? style : throw new ArgumentOutOfRangeException(nameof(fontSizePx), "样式参数无效。");
    }
}
