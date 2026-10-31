namespace MakoKaraGenerator.Scripting;

/// <summary>sRGB 直乘颜色；分量在 [0,1]，与几何／原始数据向量不同。</summary>
public readonly record struct ColorRgba
{
    /// <summary>创建颜色；拒绝非有限或越界分量，不夹值、不预乘。</summary>
    public ColorRgba(double red, double green, double blue, double alpha)
    {
        Unit(red); Unit(green); Unit(blue); Unit(alpha);
        Red = red; Green = green; Blue = blue; Alpha = alpha;
    }
    /// <summary>sRGB 红分量。</summary>
    public double Red { get; }
    /// <summary>sRGB 绿分量。</summary>
    public double Green { get; }
    /// <summary>sRGB 蓝分量。</summary>
    public double Blue { get; }
    /// <summary>独立不透明度，未乘入 RGB。</summary>
    public double Alpha { get; }
    /// <summary>显式按系数调制 RGB，保留 alpha；结果越界仍报告错误。</summary>
    public ColorRgba MultiplyRgb(double factor)
    {
        Point2.Finite(factor); ArgumentOutOfRangeException.ThrowIfNegative(factor);
        return new(Red * factor, Green * factor, Blue * factor, Alpha);
    }
    internal static void Unit(double value)
    {
        Point2.Finite(value);
        if (value is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(value), "分量必须在 [0,1]。");
    }
}
