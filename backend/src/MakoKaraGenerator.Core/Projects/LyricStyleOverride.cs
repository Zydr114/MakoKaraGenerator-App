namespace MakoKaraGenerator.Core.Projects;

/// <summary>样式属性的来源层级。</summary>
public enum LyricStyleSource
{
    /// <summary>渲染器的默认值。</summary>
    Default = 0,
    /// <summary>样式预设提供的值。</summary>
    Preset = 1,
    /// <summary>歌词行覆盖提供的值。</summary>
    Line = 2,
    /// <summary>文本元素覆盖提供的值。</summary>
    Token = 3,
    /// <summary>来自歌词分支的覆盖。</summary>
    Branch = 4,
}

/// <summary>规范嵌套样式的部分覆盖。</summary>
/// <remarks>平面属性只直接读写规范对象，不维护第二份字段。</remarks>
public sealed record LyricStyleOverride
{
    /// <summary>空覆盖。</summary>
    public static LyricStyleOverride Empty { get; } = new();
    /// <summary>字体部分覆盖。</summary>
    public TypographySpecOverride? Typography { get; init; }
    /// <summary>未唱填充部分覆盖；整个对象为空是非法数据。</summary>
    public OptionalValue<FillSpecOverride?> UnsungFill { get; init; }
    /// <summary>已唱填充部分覆盖；整个对象为空是非法数据。</summary>
    public OptionalValue<FillSpecOverride?> SungFill { get; init; }
    /// <summary>描边部分覆盖；显式空关闭整个效果。</summary>
    public OptionalValue<StrokeSpecOverride?> Stroke { get; init; }
    /// <summary>阴影部分覆盖；显式空关闭整个效果。</summary>
    public OptionalValue<ShadowSpecOverride?> Shadow { get; init; }
    /// <summary>辉光部分覆盖；显式空关闭整个效果。</summary>
    public OptionalValue<GlowSpecOverride?> Glow
    {
        get => _glow;
        init => _glow = value is { IsSet: true, Value: { IsEmpty: true } } ? default : value;
    }
    private readonly OptionalValue<GlowSpecOverride?> _glow;
    /// <summary>默认排版纵向中心覆盖。</summary>
    public double? VerticalCenterRatio { get; init; }
    /// <summary>水平对齐覆盖。</summary>
    public LyricHorizontalAlignment? HorizontalAlignment { get; init; }
    /// <summary>整体不透明度覆盖。</summary>
    public float? Opacity { get; init; }
    /// <summary>样式基础缩放覆盖。</summary>
    public float? Scale { get; init; }
    /// <summary>是否没有覆盖成员。</summary>
    public bool IsEmpty => (Typography is null || Typography.IsEmpty)
        && IsEmptyFill(UnsungFill) && IsEmptyFill(SungFill)
        && (!Stroke.IsSet || Stroke.Value is { IsEmpty: true })
        && (!Shadow.IsSet || Shadow.Value is { IsEmpty: true })
        && (!Glow.IsSet || Glow.Value is { IsEmpty: true })
        && VerticalCenterRatio is null && HorizontalAlignment is null && Opacity is null && Scale is null;
    private static bool IsEmptyFill(OptionalValue<FillSpecOverride?> fill) => !fill.IsSet || fill.Value is { IsEmpty: true };
    /// <summary>字体 FontFamilyName 覆盖的源码访问外观。</summary>
    public OptionalValue<string?> FontFamilyName { get => Typography?.FontFamilyName ?? default; init => Typography = (Typography ?? new TypographySpecOverride()) with { FontFamilyName = value }; }
    /// <summary>字体回退族名链覆盖的源码访问外观。</summary>
    public System.Collections.Immutable.ImmutableArray<string>? FallbackFamilyNames
    {
        get => Typography?.FallbackFamilyNames;
        init => Typography = (Typography ?? new TypographySpecOverride()) with { FallbackFamilyNames = value };
    }
    /// <summary>字体 FontSizePx 覆盖的源码访问外观。</summary>
    public float? FontSizePx { get => Typography?.FontSizePx; init => Typography = (Typography ?? new TypographySpecOverride()) with { FontSizePx = value }; }
    /// <summary>字体 FontWeight 覆盖的源码访问外观。</summary>
    public int? FontWeight { get => Typography?.FontWeight; init => Typography = (Typography ?? new TypographySpecOverride()) with { FontWeight = value }; }
    /// <summary>字体 IsBold 覆盖的源码访问外观。</summary>
    public bool? IsBold { get => Typography?.IsBold; init => Typography = (Typography ?? new TypographySpecOverride()) with { IsBold = value }; }
    /// <summary>字体 IsItalic 覆盖的源码访问外观。</summary>
    public bool? IsItalic { get => Typography?.IsItalic; init => Typography = (Typography ?? new TypographySpecOverride()) with { IsItalic = value }; }
    /// <summary>字体 LetterSpacingPx 覆盖的源码访问外观。</summary>
    public float? LetterSpacingPx { get => Typography?.LetterSpacingPx; init => Typography = (Typography ?? new TypographySpecOverride()) with { LetterSpacingPx = value }; }
    /// <summary>Unsung 填充颜色覆盖的源码访问外观。</summary>
    public RgbaColor? UnsungColor
    {
        get => UnsungFill.Value?.Color.Value;
        init => UnsungFill = OptionalValue.Set<FillSpecOverride?>(Fill(UnsungFill) with { Color = value is null ? default : OptionalValue.Set<RgbaColor?>(value) });
    }
    /// <summary>Unsung 渐变覆盖的源码访问外观。</summary>
    public OptionalValue<GradientSpec?> UnsungGradientSpec
    {
        get => UnsungFill.Value?.Gradient ?? default;
        init => UnsungFill = OptionalValue.Set<FillSpecOverride?>(Fill(UnsungFill) with { Gradient = value });
    }
    /// <summary>Sung 填充颜色覆盖的源码访问外观。</summary>
    public RgbaColor? SungColor
    {
        get => SungFill.Value?.Color.Value;
        init => SungFill = OptionalValue.Set<FillSpecOverride?>(Fill(SungFill) with { Color = value is null ? default : OptionalValue.Set<RgbaColor?>(value) });
    }
    /// <summary>Sung 渐变覆盖的源码访问外观。</summary>
    public OptionalValue<GradientSpec?> SungGradientSpec
    {
        get => SungFill.Value?.Gradient ?? default;
        init => SungFill = OptionalValue.Set<FillSpecOverride?>(Fill(SungFill) with { Gradient = value });
    }
    /// <summary>完整描边覆盖的源码访问外观。</summary>
    public OptionalValue<StrokeSpec?> StrokeSpec
    {
        get => !Stroke.IsSet || Stroke.Value is { IsComplete: false } ? default
            : OptionalValue.Set<StrokeSpec?>(Stroke.Value?.Apply(null));
        init => Stroke = !value.IsSet ? default : OptionalValue.Set<StrokeSpecOverride?>(value.Value is { } spec ? StrokeSpecOverride.FromSpec(spec) : null);
    }
    /// <summary>描边颜色部分覆盖的源码访问外观。</summary>
    public OptionalValue<RgbaColor?> StrokeColor
    {
        get => Stroke.Value is { IsComplete: false } partial ? partial.Color : default;
        init => Stroke = OptionalValue.Set<StrokeSpecOverride?>((Stroke.Value ?? new StrokeSpecOverride()) with { Color = value });
    }
    /// <summary>描边宽度部分覆盖的源码访问外观。</summary>
    public float? StrokeWidthPx
    {
        get => Stroke.Value is { IsComplete: false } partial ? partial.WidthPx : null;
        init => Stroke = OptionalValue.Set<StrokeSpecOverride?>((Stroke.Value ?? new StrokeSpecOverride()) with { WidthPx = value });
    }
    /// <summary>完整阴影覆盖的源码访问外观。</summary>
    public OptionalValue<ShadowSpec?> ShadowSpec
    {
        get => !Shadow.IsSet || Shadow.Value is { IsComplete: false } ? default
            : OptionalValue.Set<ShadowSpec?>(Shadow.Value?.Apply(null));
        init => Shadow = !value.IsSet ? default : OptionalValue.Set<ShadowSpecOverride?>(value.Value is { } spec ? ShadowSpecOverride.FromSpec(spec) : null);
    }
    /// <summary>阴影颜色部分覆盖的源码访问外观。</summary>
    public OptionalValue<RgbaColor?> ShadowColor
    {
        get => Shadow.Value is { IsComplete: false, Color: { } color } ? OptionalValue.Set<RgbaColor?>(color) : default;
        init => Shadow = !value.IsSet ? OptionalValue.Set<ShadowSpecOverride?>((Shadow.Value ?? new ShadowSpecOverride()) with { Color = null })
            : value.Value is { } color ? OptionalValue.Set<ShadowSpecOverride?>((Shadow.Value ?? new ShadowSpecOverride()) with { Color = color }) : OptionalValue.Set<ShadowSpecOverride?>(null);
    }
    /// <summary>阴影模糊半径部分覆盖的源码访问外观。</summary>
    public float? ShadowBlurPx
    {
        get => Shadow.Value is { IsComplete: false } partial ? partial.BlurRadiusPx : null;
        init => Shadow = OptionalValue.Set<ShadowSpecOverride?>((Shadow.Value ?? new ShadowSpecOverride()) with { BlurRadiusPx = value });
    }
    private static FillSpecOverride Fill(OptionalValue<FillSpecOverride?> value) => value.Value ?? new FillSpecOverride();
}
