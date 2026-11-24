namespace MakoKaraGenerator.Core.Projects;

/// <summary>
/// 文字阴影的绘制描述。
/// </summary>
/// <remarks>
/// 阴影方向和距离独立保存。方向以画布坐标的度数表示（0° 向右、90° 向下），
/// 渲染时再换算为水平与垂直偏移，避免编辑器直接修改两个互相耦合的分量。
/// </remarks>
public sealed record ShadowSpec
{
    /// <summary>阴影颜色。</summary>
    public RgbaColor Color { get; init; } = new(0, 0, 0, 160);

    /// <summary>可选渐变；非空时替代纯色 <see cref="Color"/> 着色。</summary>
    public GradientSpec? Gradient { get; init; }

    /// <summary>阴影自身的不透明度，范围为 0 到 1。</summary>
    public float Opacity { get; init; } = 1f;

    /// <summary>阴影方向角，单位为度；0° 向右，90° 向下。</summary>
    public double AngleDegrees { get; init; } = 45d;

    /// <summary>阴影偏移距离（画布像素）。</summary>
    public float DistancePx { get; init; } = 4f;

    /// <summary>阴影模糊半径（画布像素）；为 0 时绘制硬边阴影。</summary>
    public float BlurRadiusPx { get; init; } = 8f;

    /// <summary>整体轮廓向外扩张的画布像素数。</summary>
    public float SpreadPx { get; init; }

    /// <summary>由方向和距离换算出的水平偏移。</summary>
    public float OffsetXPx => (float)(Math.Cos(AngleDegrees * (Math.PI / 180d)) * DistancePx);

    /// <summary>由方向和距离换算出的垂直偏移。</summary>
    public float OffsetYPx => (float)(Math.Sin(AngleDegrees * (Math.PI / 180d)) * DistancePx);

    /// <summary>阴影描述是否有效。</summary>
    public bool IsValid => float.IsFinite(Opacity)
        && Opacity is >= 0f and <= 1f
        && double.IsFinite(AngleDegrees)
        && float.IsFinite(DistancePx)
        && DistancePx >= 0f
        && float.IsFinite(BlurRadiusPx)
        && BlurRadiusPx >= 0f
        && float.IsFinite(SpreadPx)
        && SpreadPx >= 0f
        && (Gradient is null || Gradient.IsValid);

    /// <summary>当前阴影是否有足够参数可以绘制。</summary>
    public bool IsEnabled => IsValid
        && Opacity > 0f
        && (Gradient is not null || Color.Alpha > 0)
        && (DistancePx > 0f || BlurRadiusPx > 0f || SpreadPx > 0f);

    /// <summary>创建并校验一份阴影描述。</summary>
    /// <param name="color">阴影颜色。</param>
    /// <param name="opacity">阴影自身不透明度，范围为 0 到 1。</param>
    /// <param name="angleDegrees">阴影方向角。</param>
    /// <param name="distancePx">阴影偏移距离，必须为非负有限数。</param>
    /// <param name="blurRadiusPx">阴影模糊半径，必须为非负有限数。</param>
    /// <param name="spreadPx">阴影外扩张距离，必须为非负有限数。</param>
    /// <param name="gradient">可选渐变；非空时替代纯色着色。</param>
    /// <returns>新的阴影描述。</returns>
    /// <exception cref="ArgumentOutOfRangeException">任一参数超出有效范围。</exception>
    public static ShadowSpec Create(
        RgbaColor color,
        float opacity = 1f,
        double angleDegrees = 45d,
        float distancePx = 4f,
        float blurRadiusPx = 8f,
        float spreadPx = 0f,
        GradientSpec? gradient = null)
    {
        ShadowSpec result = new()
        {
            Color = color,
            Gradient = gradient,
            Opacity = opacity,
            AngleDegrees = angleDegrees,
            DistancePx = distancePx,
            BlurRadiusPx = blurRadiusPx,
            SpreadPx = spreadPx,
        };

        return result.IsValid
            ? result
            : throw new ArgumentOutOfRangeException(nameof(opacity), "阴影参数必须是有限数，且不透明度在 0 到 1 之间。");
    }
}
