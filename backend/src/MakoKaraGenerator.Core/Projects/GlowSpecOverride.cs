namespace MakoKaraGenerator.Core.Projects;

/// <summary>辉光的部分覆盖；叶字段未设置时继承，外层显式 null 才关闭整个效果。</summary>
public sealed record GlowSpecOverride
{
    /// <summary>颜色覆盖。</summary>
    public RgbaColor? Color { get; init; }
    /// <summary>自身透明度覆盖。</summary>
    public float? Opacity { get; init; }
    /// <summary>高斯 sigma 覆盖，单位为画布像素。</summary>
    public float? BlurRadiusPx { get; init; }
    /// <summary>round 轮廓外扩覆盖，单位为画布像素。</summary>
    public float? SpreadPx { get; init; }
    /// <summary>是否没有任何覆盖叶字段。</summary>
    public bool IsEmpty => Color is null && Opacity is null && BlurRadiusPx is null && SpreadPx is null;
    /// <summary>是否明确替换全部字段。</summary>
    public bool IsComplete => Color is not null && Opacity is not null && BlurRadiusPx is not null && SpreadPx is not null;
    /// <summary>已设置叶字段是否有效。</summary>
    public bool IsValid => (Opacity is null || float.IsFinite(Opacity.Value) && Opacity is >= 0f and <= 1f)
        && (BlurRadiusPx is null || float.IsFinite(BlurRadiusPx.Value) && BlurRadiusPx >= 0f)
        && (SpreadPx is null || float.IsFinite(SpreadPx.Value) && SpreadPx >= 0f);
    /// <summary>合并到上层辉光；无上层时从透明、零 sigma、零外扩种子开始。</summary>
    /// <param name="current">上层辉光。</param>
    /// <returns>逐叶合并后的完整辉光。</returns>
    public GlowSpec Apply(GlowSpec? current) => new()
    {
        Color = Color ?? current?.Color ?? RgbaColor.Transparent,
        Opacity = Opacity ?? current?.Opacity ?? 1f,
        BlurRadiusPx = BlurRadiusPx ?? current?.BlurRadiusPx ?? 0f,
        SpreadPx = SpreadPx ?? current?.SpreadPx ?? 0f,
    };
    /// <summary>建立明确替换全部字段的覆盖。</summary>
    /// <param name="glow">完整辉光。</param>
    /// <returns>完整字段覆盖。</returns>
    public static GlowSpecOverride FromSpec(GlowSpec glow)
    {
        ArgumentNullException.ThrowIfNull(glow);
        return new() { Color = glow.Color, Opacity = glow.Opacity, BlurRadiusPx = glow.BlurRadiusPx, SpreadPx = glow.SpreadPx };
    }
}
