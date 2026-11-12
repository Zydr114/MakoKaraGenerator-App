using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>规范渐变类型。</summary>
public enum GradientKind
{
    /// <summary>线性渐变。</summary>
    Linear,
    /// <summary>径向渐变。</summary>
    Radial,
}
/// <summary>渐变坐标空间（参考框）。</summary>
public enum GradientCoordinateSpace
{
    /// <summary>画布逻辑坐标。</summary>
    Canvas,
    /// <summary>整行：参考框为整行宽度 × 字体上升部到下降部。</summary>
    Line,
    /// <summary>按单字：每个字簇以自己的字框（字簇宽度 × 字体上升部到下降部）为参考框；描边同理，相邻字的描边重叠处按绘制顺序覆盖。</summary>
    Glyph,
}
/// <summary>渐变颜色插值空间。</summary>
public enum GradientColorSpace
{
    /// <summary>线性 RGB。</summary>
    LinearRgb,
    /// <summary>HSV。</summary>
    Hsv,
    /// <summary>Oklch。</summary>
    Oklch,
}
/// <summary>HSV 色相的明确方向。</summary>
public enum GradientHuePath
{
    /// <summary>较短方向。</summary>
    Shorter,
    /// <summary>较长方向。</summary>
    Longer,
    /// <summary>色相增加。</summary>
    Increasing,
    /// <summary>色相减少。</summary>
    Decreasing,
}
/// <summary>有限位置及直乘颜色的不可变色标。</summary>
public readonly record struct GradientStop
{
    /// <summary>位置在 [0,1]，不夹值。</summary>
    public GradientStop(double offset, ColorRgba color) { ColorRgba.Unit(offset); Offset = offset; Color = color; }
    /// <summary>归一化位置。</summary>
    public double Offset { get; }
    /// <summary>sRGB 直乘颜色。</summary>
    public ColorRgba Color { get; }
}

/// <summary>完整中立渐变；唯一色标真值，不隐藏另一纯色基色。</summary>
public sealed record GradientValue
{
    /// <summary>固定规范渐变；首尾 0／1、至少两点、非递减，允许同位置的明确硬边。</summary>
    public GradientValue(ImmutableArray<GradientStop> stops, GradientKind kind = GradientKind.Linear, double angleDegrees = 0,
        GradientCoordinateSpace coordinateSpace = GradientCoordinateSpace.Line, GradientColorSpace colorSpace = GradientColorSpace.LinearRgb,
        GradientHuePath huePath = GradientHuePath.Shorter, double centerX = .5, double centerY = .5, double radiusX = .5, double radiusY = .5)
    {
        if (stops.IsDefaultOrEmpty || stops.Length < 2 || stops[0].Offset != 0 || stops[^1].Offset != 1
            || stops.Zip(stops.Skip(1)).Any(pair => pair.First.Offset > pair.Second.Offset))
            throw new ArgumentException("渐变需要原序色标且首尾位置为 0 和 1。", nameof(stops));
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(coordinateSpace) || !Enum.IsDefined(colorSpace) || !Enum.IsDefined(huePath))
            throw new ArgumentOutOfRangeException(nameof(kind));
        Point2.Finite(angleDegrees); Point2.Finite(centerX); Point2.Finite(centerY); Point2.Finite(radiusX); Point2.Finite(radiusY);
        if (kind == GradientKind.Radial && (centerX is < 0 or > 1 || centerY is < 0 or > 1 || radiusX <= 0 || radiusY <= 0))
            throw new ArgumentOutOfRangeException(nameof(radiusX));
        Stops = stops; Kind = kind; AngleDegrees = angleDegrees; CoordinateSpace = coordinateSpace; ColorSpace = colorSpace;
        HuePath = huePath; CenterX = centerX; CenterY = centerY; RadiusX = radiusX; RadiusY = radiusY;
    }
    /// <summary>
    /// 按「颜色＋分界」构造硬边分层渐变：第 i 层颜色占 [分界 i−1, 分界 i)，首层从 0 起、末层到 1 止，
    /// 相邻两层在分界处是位置相同的两个色标（硬边），层内同色。
    /// </summary>
    /// <param name="colors">由起点到终点的各层颜色，至少一层。</param>
    /// <param name="boundaries">层与层之间的分界，个数比颜色少一，严格递增且都在 (0,1) 内。</param>
    /// <param name="kind">类型。</param>
    /// <param name="angleDegrees">线性渐变的画布顺时针方向角，度。</param>
    /// <param name="coordinateSpace">坐标空间。</param>
    /// <param name="centerX">径向中心横坐标。</param>
    /// <param name="centerY">径向中心纵坐标。</param>
    /// <param name="radiusX">径向横半径。</param>
    /// <param name="radiusY">径向纵半径。</param>
    /// <returns>色标为每层首尾各一个的渐变；层内同色，插值空间不影响结果，取线性 RGB。</returns>
    /// <exception cref="ArgumentException">没有颜色、分界个数不对、分界不在 (0,1) 内或未严格递增时抛出。</exception>
    public static GradientValue Bands(ImmutableArray<ColorRgba> colors, ImmutableArray<double> boundaries, GradientKind kind = GradientKind.Linear,
        double angleDegrees = 0, GradientCoordinateSpace coordinateSpace = GradientCoordinateSpace.Line, double centerX = .5, double centerY = .5,
        double radiusX = .5, double radiusY = .5)
    {
        if (colors.IsDefaultOrEmpty) throw new ArgumentException("分层渐变至少需要一层颜色。", nameof(colors));
        if (boundaries.IsDefault || boundaries.Length != colors.Length - 1)
            throw new ArgumentException("分界个数必须比颜色少一。", nameof(boundaries));
        for (int i = 0; i < boundaries.Length; i++)
            if (!double.IsFinite(boundaries[i]) || boundaries[i] <= 0 || boundaries[i] >= 1 || i > 0 && boundaries[i] <= boundaries[i - 1])
                throw new ArgumentException("分界必须严格递增且都在 (0,1) 内。", nameof(boundaries));
        var stops = ImmutableArray.CreateBuilder<GradientStop>(colors.Length * 2);
        for (int i = 0; i < colors.Length; i++)
        {
            stops.Add(new(i == 0 ? 0 : boundaries[i - 1], colors[i]));
            stops.Add(new(i == colors.Length - 1 ? 1 : boundaries[i], colors[i]));
        }
        return new(stops.MoveToImmutable(), kind, angleDegrees, coordinateSpace, GradientColorSpace.LinearRgb, GradientHuePath.Shorter,
            centerX, centerY, radiusX, radiusY);
    }
    /// <summary>固定色标。</summary>
    public ImmutableArray<GradientStop> Stops { get; }
    /// <summary>类型。</summary>
    public GradientKind Kind { get; }
    /// <summary>画布顺时针方向角，度。</summary>
    public double AngleDegrees { get; }
    /// <summary>坐标空间。</summary>
    public GradientCoordinateSpace CoordinateSpace { get; }
    /// <summary>插值空间。</summary>
    public GradientColorSpace ColorSpace { get; }
    /// <summary>色相方向。</summary>
    public GradientHuePath HuePath { get; }
    /// <summary>径向中心横坐标。</summary>
    public double CenterX { get; }
    /// <summary>径向中心纵坐标。</summary>
    public double CenterY { get; }
    /// <summary>径向横半径。</summary>
    public double RadiusX { get; }
    /// <summary>径向纵半径。</summary>
    public double RadiusY { get; }
    /// <summary>内容相等，不比较色标数组引用。</summary>
    public bool Equals(GradientValue? other) => other is not null && Kind == other.Kind && AngleDegrees == other.AngleDegrees
        && CoordinateSpace == other.CoordinateSpace && ColorSpace == other.ColorSpace && HuePath == other.HuePath
        && CenterX == other.CenterX && CenterY == other.CenterY && RadiusX == other.RadiusX && RadiusY == other.RadiusY && Stops.SequenceEqual(other.Stops);
    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new(); hash.Add(Kind); hash.Add(AngleDegrees); hash.Add(CoordinateSpace); hash.Add(ColorSpace); hash.Add(HuePath);
        hash.Add(CenterX); hash.Add(CenterY); hash.Add(RadiusX); hash.Add(RadiusY); foreach (var stop in Stops) hash.Add(stop); return hash.ToHashCode();
    }
}
