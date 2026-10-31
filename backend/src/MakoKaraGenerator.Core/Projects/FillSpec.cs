namespace MakoKaraGenerator.Core.Projects;

/// <summary>歌词填充只持有一个真实材质；颜色／渐变属性是源码访问外观。</summary>
public sealed record FillSpec
{
    /// <summary>唯一完整材质。</summary>
    public MaterialSpec Material { get; init; } = new MaterialSpec.Solid(RgbaColor.Transparent);
    /// <summary>静态颜色访问；渐变取首色标，包材质拒绝伪造纯色。</summary>
    public RgbaColor Color { get => Material.RepresentativeColor; init => Material = Material.WithColor(value); }
    /// <summary>静态渐变访问；明确关闭得到首色标纯色。</summary>
    public GradientSpec? Gradient { get => (Material as MaterialSpec.Gradient)?.Value; init => Material = Material.WithGradient(value); }
    /// <summary>默认未唱材质。</summary>
    public static FillSpec DefaultUnsung { get; } = new() { Material = new MaterialSpec.Solid(new(0x4A, 0x50, 0x60)) };
    /// <summary>默认已唱材质。</summary>
    public static FillSpec DefaultSung { get; } = new() { Material = new MaterialSpec.Solid(new(0x4C, 0x8D, 0xFF)) };
    /// <summary>结构是否有效。</summary>
    public bool IsValid => Material is { IsValid: true };
}

/// <summary>材质整体覆盖；缺省继承，显式null无效。</summary>
public sealed record FillSpecOverride
{
    /// <summary>唯一完整材质覆盖。</summary>
    public OptionalValue<MaterialSpec?> Material { get; init; }
    /// <summary>静态源码颜色访问外观；写入完整solid，缺省恢复材质继承。</summary>
    public OptionalValue<RgbaColor?> Color
    {
        get => Material.IsSet ? OptionalValue.Set<RgbaColor?>(Material.Value?.RepresentativeColor) : default;
        init => Material = value.IsSet ? OptionalValue.Set<MaterialSpec?>(value.Value is { } color ? new MaterialSpec.Solid(color) : null) : default;
    }
    /// <summary>静态源码渐变访问；无基准的关闭操作必须改为明确solid。</summary>
    public OptionalValue<GradientSpec?> Gradient
    {
        get => Material.IsSet ? OptionalValue.Set<GradientSpec?>((Material.Value as MaterialSpec.Gradient)?.Value) : default;
        init => Material = !value.IsSet ? default : OptionalValue.Set<MaterialSpec?>(value.Value is { } gradient ? new MaterialSpec.Gradient(gradient)
            : Material.Value is { } current ? current.WithGradient(null) : throw new ArgumentException("关闭渐变需要明确的纯色材质。"));
    }
    /// <summary>没有材质覆盖。</summary>
    public bool IsEmpty => !Material.IsSet;
}
