namespace MakoKaraGenerator.Core.Projects;

/// <summary>描边对象的部分覆盖；整个覆盖设置为空才关闭效果。</summary>
public sealed record StrokeSpecOverride
{
    /// <summary>材质整体覆盖；缺省继承，显式null无效。</summary>
    public OptionalValue<MaterialSpec?> Material { get; init; }
    /// <summary>静态颜色写入完整solid；null表示透明solid。</summary>
    public OptionalValue<RgbaColor?> Color
    {
        get => Material.IsSet ? OptionalValue.Set<RgbaColor?>(Material.Value?.RepresentativeColor) : default;
        init => Material = value.IsSet ? OptionalValue.Set<MaterialSpec?>(new MaterialSpec.Solid(value.Value ?? RgbaColor.Transparent)) : default;
    }
    /// <summary>静态渐变访问；关闭需要明确材质基准。</summary>
    public OptionalValue<GradientSpec?> Gradient
    {
        get => Material.IsSet ? OptionalValue.Set<GradientSpec?>((Material.Value as MaterialSpec.Gradient)?.Value) : default;
        init => Material = !value.IsSet ? default : OptionalValue.Set<MaterialSpec?>(value.Value is { } gradient ? new MaterialSpec.Gradient(gradient)
            : Material.Value is { } current ? current.WithGradient(null) : throw new ArgumentException("关闭渐变需要明确纯色材质。"));
    }
    /// <summary>宽度覆盖。</summary>
    public float? WidthPx { get; init; }
    /// <summary>连接方式覆盖。</summary>
    public StrokeJoin? Join { get; init; }
    /// <summary>位置覆盖。</summary>
    public StrokePosition? Position { get; init; }
    /// <summary>是否没有任何成员覆盖。</summary>
    public bool IsEmpty => !Material.IsSet && WidthPx is null && Join is null && Position is null;
    /// <summary>是否明确替换全部成员。</summary>
    public bool IsComplete => Material.IsSet && WidthPx is not null && Join is not null && Position is not null;
    /// <summary>覆盖成员是否有效。</summary>
    public bool IsValid => (WidthPx is null || float.IsFinite(WidthPx.Value) && WidthPx >= 0)
        && (Join is null || Enum.IsDefined(Join.Value)) && (Position is null || Enum.IsDefined(Position.Value))
        && (!Material.IsSet || Material.Value is { IsValid: true });
    /// <summary>把部分覆盖合并到上层描边。</summary>
    /// <param name="current">上层描边。</param>
    /// <returns>完整描边。</returns>
    public StrokeSpec Apply(StrokeSpec? current) => (current ?? new StrokeSpec { Color = null, WidthPx = 0 }) with
    {
        Material = Material.IsSet ? Material.Value ?? throw new ArgumentException("描边材质不能null。") : current?.Material ?? new MaterialSpec.Solid(RgbaColor.Transparent),
        WidthPx = WidthPx ?? current?.WidthPx ?? 0,
        Join = Join ?? current?.Join ?? StrokeJoin.Round,
        Position = Position ?? current?.Position ?? StrokePosition.Outside,
    };
    /// <summary>从完整描边建立全部成员覆盖。</summary>
    /// <param name="stroke">完整描边。</param>
    /// <returns>覆盖对象。</returns>
    public static StrokeSpecOverride FromSpec(StrokeSpec stroke) => new()
    {
        Material = stroke.Material, WidthPx = stroke.WidthPx, Join = stroke.Join, Position = stroke.Position,
    };
}

/// <summary>阴影对象的部分覆盖。</summary>
public sealed record ShadowSpecOverride
{
    /// <summary>颜色覆盖。</summary>
    public RgbaColor? Color { get; init; }
    /// <summary>效果不透明度覆盖。</summary>
    public float? Opacity { get; init; }
    /// <summary>方向覆盖。</summary>
    public double? AngleDegrees { get; init; }
    /// <summary>距离覆盖。</summary>
    public float? DistancePx { get; init; }
    /// <summary>模糊半径覆盖。</summary>
    public float? BlurRadiusPx { get; init; }
    /// <summary>是否没有成员覆盖。</summary>
    public bool IsEmpty => Color is null && Opacity is null && AngleDegrees is null && DistancePx is null && BlurRadiusPx is null;
    /// <summary>是否明确替换全部成员。</summary>
    public bool IsComplete => Color is not null && Opacity is not null && AngleDegrees is not null && DistancePx is not null && BlurRadiusPx is not null;
    /// <summary>覆盖成员是否有效。</summary>
    public bool IsValid => (Opacity is null || float.IsFinite(Opacity.Value) && Opacity is >= 0 and <= 1)
        && (AngleDegrees is null || double.IsFinite(AngleDegrees.Value))
        && (DistancePx is null || float.IsFinite(DistancePx.Value) && DistancePx >= 0)
        && (BlurRadiusPx is null || float.IsFinite(BlurRadiusPx.Value) && BlurRadiusPx >= 0);
    /// <summary>把部分覆盖合并到上层阴影。</summary>
    /// <param name="current">上层阴影。</param>
    /// <returns>完整阴影。</returns>
    public ShadowSpec Apply(ShadowSpec? current) => (current ?? new ShadowSpec { Color = new RgbaColor(0, 0, 0, 0), DistancePx = 0, BlurRadiusPx = 0 }) with
    {
        Color = Color ?? current?.Color ?? new RgbaColor(0, 0, 0, 0),
        Opacity = Opacity ?? current?.Opacity ?? 1,
        AngleDegrees = AngleDegrees ?? current?.AngleDegrees ?? 0,
        DistancePx = DistancePx ?? current?.DistancePx ?? 0,
        BlurRadiusPx = BlurRadiusPx ?? current?.BlurRadiusPx ?? 0,
    };
    /// <summary>从完整阴影建立全部成员覆盖。</summary>
    /// <param name="shadow">完整阴影。</param>
    /// <returns>覆盖对象。</returns>
    public static ShadowSpecOverride FromSpec(ShadowSpec shadow) => new()
    {
        Color = shadow.Color, Opacity = shadow.Opacity, AngleDegrees = shadow.AngleDegrees,
        DistancePx = shadow.DistancePx, BlurRadiusPx = shadow.BlurRadiusPx,
    };
}
