namespace MakoKaraGenerator.Core.Projects;

/// <summary>阴影对象的部分覆盖。</summary>
public sealed record ShadowSpecOverride
{
    /// <summary>颜色覆盖。</summary>
    public RgbaColor? Color { get; init; }
    /// <summary>渐变覆盖；显式设置 <see langword="null"/> 表示关闭渐变回到纯色。</summary>
    public OptionalValue<GradientSpec?> Gradient { get; init; }
    /// <summary>效果不透明度覆盖。</summary>
    public float? Opacity { get; init; }
    /// <summary>方向覆盖。</summary>
    public double? AngleDegrees { get; init; }
    /// <summary>距离覆盖。</summary>
    public float? DistancePx { get; init; }
    /// <summary>模糊半径覆盖。</summary>
    public float? BlurRadiusPx { get; init; }
    /// <summary>外扩张距离覆盖。</summary>
    public float? SpreadPx { get; init; }
    /// <summary>是否没有成员覆盖。</summary>
    public bool IsEmpty => Color is null && !Gradient.IsSet && Opacity is null && AngleDegrees is null && DistancePx is null && BlurRadiusPx is null && SpreadPx is null;
    /// <summary>是否明确替换全部成员。</summary>
    public bool IsComplete => Color is not null && Gradient.IsSet && Opacity is not null && AngleDegrees is not null && DistancePx is not null && BlurRadiusPx is not null && SpreadPx is not null;
    /// <summary>覆盖成员是否有效。</summary>
    public bool IsValid => (!Gradient.IsSet || Gradient.Value is null || Gradient.Value.IsValid)
        && (Opacity is null || float.IsFinite(Opacity.Value) && Opacity is >= 0 and <= 1)
        && (AngleDegrees is null || double.IsFinite(AngleDegrees.Value))
        && (DistancePx is null || float.IsFinite(DistancePx.Value) && DistancePx >= 0)
        && (BlurRadiusPx is null || float.IsFinite(BlurRadiusPx.Value) && BlurRadiusPx >= 0)
        && (SpreadPx is null || float.IsFinite(SpreadPx.Value) && SpreadPx >= 0);
    /// <summary>把部分覆盖合并到上层阴影。</summary>
    /// <param name="current">上层阴影。</param>
    /// <returns>完整阴影。</returns>
    public ShadowSpec Apply(ShadowSpec? current) => (current ?? new ShadowSpec { Color = new RgbaColor(0, 0, 0, 0), DistancePx = 0, BlurRadiusPx = 0, SpreadPx = 0 }) with
    {
        Color = Color ?? current?.Color ?? new RgbaColor(0, 0, 0, 0),
        Gradient = Gradient.IsSet ? Gradient.Value : current?.Gradient,
        Opacity = Opacity ?? current?.Opacity ?? 1,
        AngleDegrees = AngleDegrees ?? current?.AngleDegrees ?? 0,
        DistancePx = DistancePx ?? current?.DistancePx ?? 0,
        BlurRadiusPx = BlurRadiusPx ?? current?.BlurRadiusPx ?? 0,
        SpreadPx = SpreadPx ?? current?.SpreadPx ?? 0,
    };
    /// <summary>从完整阴影建立全部成员覆盖。</summary>
    /// <param name="shadow">完整阴影。</param>
    /// <returns>覆盖对象。</returns>
    public static ShadowSpecOverride FromSpec(ShadowSpec shadow) => new()
    {
        Color = shadow.Color, Gradient = OptionalValue.Set<GradientSpec?>(shadow.Gradient), Opacity = shadow.Opacity,
        AngleDegrees = shadow.AngleDegrees, DistancePx = shadow.DistancePx, BlurRadiusPx = shadow.BlurRadiusPx, SpreadPx = shadow.SpreadPx,
    };
}
