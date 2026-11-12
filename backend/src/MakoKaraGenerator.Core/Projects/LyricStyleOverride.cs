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
/// <remarks>
/// 结构与 <see cref="LyricStyle"/> 相同：字形、未唱外观、已唱外观、扫字样式与整体。
/// 平面属性只直接读写规范对象，不维护第二份字段。
/// </remarks>
public sealed record LyricStyleOverride
{
    /// <summary>空覆盖。</summary>
    public static LyricStyleOverride Empty { get; } = new();
    /// <summary>字体部分覆盖。</summary>
    public TypographySpecOverride? Typography { get; init; }
    /// <summary>未唱外观部分覆盖。</summary>
    public TextAppearanceOverride? Unsung { get; init; }
    /// <summary>已唱外观部分覆盖。</summary>
    public SungAppearanceOverride? Sung { get; init; }
    /// <summary>扫字样式部分覆盖。</summary>
    public WipeSpecOverride? Wipe { get; init; }
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
        && (Unsung is null || Unsung.IsEmpty) && (Sung is null || Sung.IsEmpty) && (Wipe is null || Wipe.IsEmpty)
        && VerticalCenterRatio is null && HorizontalAlignment is null && Opacity is null && Scale is null;
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
    /// <summary>未唱填充覆盖的源码访问外观。</summary>
    public OptionalValue<FillSpecOverride?> UnsungFill { get => Unsung?.Fill ?? default; init => Unsung = (Unsung ?? new()) with { Fill = value }; }
    /// <summary>已唱填充覆盖的源码访问外观。</summary>
    public OptionalValue<FillSpecOverride?> SungFill { get => Sung?.Fill ?? default; init => Sung = (Sung ?? new()) with { Fill = value }; }
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
    /// <summary>未唱描边整表覆盖的源码访问外观；<see langword="null"/> 为继承，空表为关闭。</summary>
    public StrokeLayers? UnsungStrokes { get => Unsung?.Strokes; init => Unsung = (Unsung ?? new()) with { Strokes = value }; }
    /// <summary>未唱单层描边覆盖的源码访问外观：设置为单层或关闭（空表）。读取时只在整表恰为单层或空表时给出值。</summary>
    public OptionalValue<StrokeSpec?> Stroke
    {
        get => Unsung?.Strokes is { Count: <= 1 } strokes ? OptionalValue.Set<StrokeSpec?>(strokes.Innermost) : default;
        init => UnsungStrokes = value.IsSet ? StrokeLayers.FromStroke(value.Value) : null;
    }
    /// <summary>未唱阴影覆盖的源码访问外观。</summary>
    public OptionalValue<ShadowSpecOverride?> Shadow { get => Unsung?.Shadow ?? default; init => Unsung = (Unsung ?? new()) with { Shadow = value }; }
    /// <summary>未唱辉光覆盖的源码访问外观；空的部分覆盖视为未设置。</summary>
    public OptionalValue<GlowSpecOverride?> Glow
    {
        get => Unsung?.Glow ?? default;
        init => Unsung = (Unsung ?? new()) with { Glow = value is { IsSet: true, Value: { IsEmpty: true } } ? default : value };
    }
    /// <summary>完整未唱阴影覆盖的源码访问外观。</summary>
    public OptionalValue<ShadowSpec?> ShadowSpec
    {
        get => !Shadow.IsSet || Shadow.Value is { IsComplete: false } ? default
            : OptionalValue.Set<ShadowSpec?>(Shadow.Value?.Apply(null));
        init => Shadow = !value.IsSet ? default : OptionalValue.Set<ShadowSpecOverride?>(value.Value is { } spec ? ShadowSpecOverride.FromSpec(spec) : null);
    }
    private static FillSpecOverride Fill(OptionalValue<FillSpecOverride?> value) => value.Value ?? new FillSpecOverride();
}
