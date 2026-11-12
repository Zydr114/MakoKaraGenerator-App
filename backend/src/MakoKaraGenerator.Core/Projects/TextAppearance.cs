namespace MakoKaraGenerator.Core.Projects;

/// <summary>一态的完整外观：填充、描边、阴影、辉光（P3 设计 §4）。</summary>
public sealed record TextAppearance
{
    /// <summary>填充材质。</summary>
    public FillSpec Fill { get; init; } = new();

    /// <summary>多重描边，由内向外；空表为无描边。</summary>
    public StrokeLayers Strokes { get; init; } = StrokeLayers.Empty;

    /// <summary>阴影；为空表示关闭。</summary>
    public ShadowSpec? Shadow { get; init; }

    /// <summary>辉光；为空表示关闭。</summary>
    public GlowSpec? Glow { get; init; }

    /// <summary>所有成员是否有效。</summary>
    public bool IsValid => Fill is { IsValid: true } && Strokes is { IsValid: true }
        && (Shadow is null || Shadow.IsValid) && (Glow is null || Glow.IsValid);
}

/// <summary>已唱一态的外观：填充必填，描边、阴影、辉光可以跟随未唱（D-P3-6）。</summary>
public sealed record SungAppearance
{
    /// <summary>填充材质。</summary>
    public FillSpec Fill { get; init; } = new();

    /// <summary>多重描边；默认跟随未唱。</summary>
    public Followable<StrokeLayers> Strokes { get; init; }

    /// <summary>阴影；默认跟随未唱，显式 <see langword="null"/> 为关闭。</summary>
    public Followable<ShadowSpec?> Shadow { get; init; }

    /// <summary>辉光；默认跟随未唱，显式 <see langword="null"/> 为关闭。</summary>
    public Followable<GlowSpec?> Glow { get; init; }

    /// <summary>所有成员是否有效；显式描边不能为 null。</summary>
    public bool IsValid => Fill is { IsValid: true }
        && (Strokes.FollowsUnsung || Strokes.Value is { IsValid: true })
        && (Shadow.FollowsUnsung || Shadow.Value is null || Shadow.Value.IsValid)
        && (Glow.FollowsUnsung || Glow.Value is null || Glow.Value.IsValid);

    /// <summary>展开跟随，得到已唱的完整外观。</summary>
    /// <param name="unsung">同一份样式的未唱外观。</param>
    /// <returns>已唱外观。</returns>
    public TextAppearance Resolve(TextAppearance unsung)
    {
        ArgumentNullException.ThrowIfNull(unsung);
        return new()
        {
            Fill = Fill,
            Strokes = Strokes.Resolve(unsung.Strokes) ?? StrokeLayers.Empty,
            Shadow = Shadow.Resolve(unsung.Shadow),
            Glow = Glow.Resolve(unsung.Glow),
        };
    }

    /// <summary>描边、阴影、辉光全部跟随，只给定填充。</summary>
    /// <param name="fill">已唱填充。</param>
    /// <returns>已唱外观。</returns>
    public static SungAppearance Following(FillSpec fill) => new() { Fill = fill };

    /// <summary>把一份完整外观全部显式写成已唱（不跟随）。</summary>
    /// <param name="appearance">完整外观。</param>
    /// <returns>已唱外观。</returns>
    public static SungAppearance Explicit(TextAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return new()
        {
            Fill = appearance.Fill,
            Strokes = Followable.Set(appearance.Strokes),
            Shadow = Followable.Set<ShadowSpec?>(appearance.Shadow),
            Glow = Followable.Set<GlowSpec?>(appearance.Glow),
        };
    }
}
