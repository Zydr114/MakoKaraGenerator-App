namespace MakoKaraGenerator.Core.Projects;

/// <summary>静态文字辉光；在固定字形的圆角外扩轮廓上应用高斯模糊。</summary>
public sealed record GlowSpec
{
    /// <summary>辉光颜色。</summary>
    public RgbaColor Color { get; init; } = RgbaColor.White;
    /// <summary>辉光自身透明度，范围为 0 到 1。</summary>
    public float Opacity { get; init; } = 1f;
    /// <summary>高斯模糊 sigma，单位为画布像素。</summary>
    public float BlurRadiusPx { get; init; } = 8f;
    /// <summary>字形轮廓以 round 连接方式向外扩张的画布像素数。</summary>
    public float SpreadPx { get; init; }
    /// <summary>所有数值是否有限且处于有效范围。</summary>
    public bool IsValid => float.IsFinite(Opacity) && Opacity is >= 0f and <= 1f
        && float.IsFinite(BlurRadiusPx) && BlurRadiusPx >= 0f
        && float.IsFinite(SpreadPx) && SpreadPx >= 0f;
    /// <summary>辉光是否具有可绘制的颜色、透明度和外扩或模糊。</summary>
    public bool IsEnabled => IsValid && Color.Alpha > 0 && Opacity > 0f && (BlurRadiusPx > 0f || SpreadPx > 0f);
    /// <summary>建立并校验静态辉光。</summary>
    /// <param name="color">辉光颜色。</param>
    /// <param name="opacity">自身透明度。</param>
    /// <param name="blurRadiusPx">高斯 sigma。</param>
    /// <param name="spreadPx">圆角轮廓外扩距离。</param>
    /// <returns>有效辉光对象。</returns>
    public static GlowSpec Create(RgbaColor color, float opacity = 1f, float blurRadiusPx = 8f, float spreadPx = 0f)
    {
        GlowSpec result = new() { Color = color, Opacity = opacity, BlurRadiusPx = blurRadiusPx, SpreadPx = spreadPx };
        return result.IsValid ? result : throw new ArgumentOutOfRangeException(nameof(opacity), "辉光透明度必须在0到1之间，sigma和外扩必须是非负有限数。");
    }
}
