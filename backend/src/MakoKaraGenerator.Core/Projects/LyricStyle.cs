namespace MakoKaraGenerator.Core.Projects;

/// <summary>歌词的规范样式；位置和动画由行与分支另行管理。</summary>
/// <remarks>
/// <para>
/// 三层结构（P3 设计 §1、§4）：字形与布局两态共享；外观（填充、描边、阴影、辉光）未唱、已唱各一套，
/// 已唱的描边、阴影、辉光默认跟随未唱；扫字样式两态共享。整体不透明度与缩放两态共享。
/// </para>
/// <para>平面属性只提供源码访问外观，直接读写嵌套对象，不存储第二份值。</para>
/// </remarks>
public sealed record LyricStyle
{
    /// <summary>字体排版参数。</summary>
    public TypographySpec Typography { get; init; } = TypographySpec.Default;
    /// <summary>未唱外观。</summary>
    public TextAppearance Unsung { get; init; } = new() { Fill = FillSpec.DefaultUnsung };
    /// <summary>已唱外观；描边、阴影、辉光默认跟随未唱。</summary>
    public SungAppearance Sung { get; init; } = SungAppearance.Following(FillSpec.DefaultSung);
    /// <summary>扫字样式。</summary>
    public WipeSpec Wipe { get; init; } = WipeSpec.Default;
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
        && Unsung is { IsValid: true } && Sung is { IsValid: true } && Wipe is { IsValid: true }
        && Enum.IsDefined(HorizontalAlignment) && double.IsFinite(VerticalCenterRatio)
        && VerticalCenterRatio is >= 0d and <= 1d && float.IsFinite(Opacity)
        && Opacity is >= 0f and <= 1f && float.IsFinite(Scale) && Scale > 0f;

    /// <summary>展开跟随后的已唱外观。</summary>
    /// <returns>已唱的完整外观。</returns>
    public TextAppearance ResolveSung() => Sung.Resolve(Unsung);

    /// <summary>取一态的完整外观。</summary>
    /// <param name="sung">是否取已唱。</param>
    /// <returns>该态的完整外观。</returns>
    public TextAppearance Appearance(bool sung) => sung ? ResolveSung() : Unsung;

    /// <summary>两态展开后的描边累计宽度的较大值（画布像素，P3 设计 §10.6）。</summary>
    public float MaxStrokeWidthPx => Math.Max(Unsung.Strokes.TotalWidthPx, ResolveSung().Strokes.TotalWidthPx);

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
    /// <summary>未唱填充的源码访问外观。</summary>
    public FillSpec UnsungFill { get => Unsung.Fill; init => Unsung = Unsung with { Fill = value }; }
    /// <summary>已唱填充的源码访问外观。</summary>
    public FillSpec SungFill { get => Sung.Fill; init => Sung = Sung with { Fill = value }; }
    /// <summary>Unsung 填充基色的源码访问外观。</summary>
    public RgbaColor UnsungColor { get => UnsungFill.Color; init => UnsungFill = UnsungFill with { Color = value }; }
    /// <summary>Unsung 填充渐变的源码访问外观。</summary>
    public GradientSpec? UnsungGradientSpec { get => UnsungFill.Gradient; init => UnsungFill = UnsungFill with { Gradient = value }; }
    /// <summary>Sung 填充基色的源码访问外观。</summary>
    public RgbaColor SungColor { get => SungFill.Color; init => SungFill = SungFill with { Color = value }; }
    /// <summary>Sung 填充渐变的源码访问外观。</summary>
    public GradientSpec? SungGradientSpec { get => SungFill.Gradient; init => SungFill = SungFill with { Gradient = value }; }
    /// <summary>未唱单层描边的源码访问外观：读取最内层；写入把未唱描边整表换成这一层，空值关闭。已唱照旧跟随或保持显式值。</summary>
    public StrokeSpec? Stroke { get => Unsung.Strokes.Innermost; init => Unsung = Unsung with { Strokes = StrokeLayers.FromStroke(value) }; }
    /// <summary>未唱阴影的源码访问外观；已唱照旧跟随或保持显式值。</summary>
    public ShadowSpec? Shadow { get => Unsung.Shadow; init => Unsung = Unsung with { Shadow = value }; }
    /// <summary>未唱辉光的源码访问外观；已唱照旧跟随或保持显式值。</summary>
    public GlowSpec? Glow { get => Unsung.Glow; init => Unsung = Unsung with { Glow = value }; }

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
