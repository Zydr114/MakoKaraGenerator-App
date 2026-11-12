using System.Collections.Immutable;
using MakoKaraGenerator.Core.Internal;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>渐变类型。</summary>
public enum GradientKind
{
    /// <summary>沿一个方向插值。</summary>
    Linear = 0,

    /// <summary>从中心向外插值。</summary>
    Radial = 1,
}

/// <summary>渐变取色时使用的坐标空间。</summary>
public enum GradientCoordinateSpace
{
    /// <summary>以整个画布为坐标空间。</summary>
    Canvas = 0,

    /// <summary>以当前歌词行为坐标空间。</summary>
    Line = 1,

    /// <summary>按单字：每个字簇以自己的字框（字簇宽度 × 字体上升部到下降部）为坐标空间（D-P3-4）。</summary>
    Glyph = 2,
}

/// <summary>渐变颜色插值空间。</summary>
public enum GradientColorSpace
{
    /// <summary>在线性 RGB 通道中插值，当前默认值。</summary>
    LinearRgb = 0,

    /// <summary>在 HSV 空间中插值。</summary>
    Hsv = 1,

    /// <summary>在感知色空间中插值；渲染后端可在接入时启用。</summary>
    Oklch = 2,
}

/// <summary>色相插值方向。</summary>
public enum GradientHuePath
{
    /// <summary>沿色环较短方向移动，默认值。</summary>
    Shorter = 0,

    /// <summary>沿色环较长方向移动。</summary>
    Longer = 1,

    /// <summary>始终增加色相角。</summary>
    Increasing = 2,

    /// <summary>始终减少色相角。</summary>
    Decreasing = 3,
}

/// <summary>一个渐变停止点。</summary>
/// <param name="Offset">停止点位置，范围为 0 到 1。</param>
/// <param name="Color">停止点颜色。</param>
public readonly record struct GradientStop(double Offset, RgbaColor Color)
{
    /// <summary>停止点数据是否有效。</summary>
    public bool IsValid => double.IsFinite(Offset) && Offset is >= 0d and <= 1d;

    /// <summary>创建一个已校验的停止点。</summary>
    /// <param name="offset">停止点位置，范围为 0 到 1。</param>
    /// <param name="color">停止点颜色。</param>
    /// <returns>新的停止点。</returns>
    /// <exception cref="ArgumentOutOfRangeException">位置不是有限数或不在 0 到 1 之间。</exception>
    public static GradientStop Create(double offset, RgbaColor color)
    {
        if (!double.IsFinite(offset) || offset is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "渐变停止点必须落在 0 到 1 之间。");
        }

        return new GradientStop(offset, color);
    }
}

/// <summary>
/// 可复用的渐变描述。它只描述取色参数，不包含 Skia 或 Avalonia 类型。
/// </summary>
public sealed record GradientSpec
{
    private ImmutableArray<GradientStop> _stops;

    /// <summary>渐变类型。</summary>
    public GradientKind Kind { get; init; } = GradientKind.Linear;

    /// <summary>渐变方向角，单位为度；线性渐变从此角度沿画布顺时针方向取色。</summary>
    public double AngleDegrees { get; init; }

    /// <summary>径向渐变中心的归一化横坐标；线性渐变忽略此值。</summary>
    public double CenterX { get; init; } = 0.5d;

    /// <summary>径向渐变中心的归一化纵坐标；线性渐变忽略此值。</summary>
    public double CenterY { get; init; } = 0.5d;

    /// <summary>径向渐变的归一化横向半径；线性渐变忽略此值。</summary>
    public double RadiusX { get; init; } = 0.5d;

    /// <summary>径向渐变的归一化纵向半径；线性渐变忽略此值。</summary>
    public double RadiusY { get; init; } = 0.5d;

    /// <summary>渐变使用的坐标空间。</summary>
    public GradientCoordinateSpace CoordinateSpace { get; init; } = GradientCoordinateSpace.Line;

    /// <summary>颜色插值空间。</summary>
    public GradientColorSpace ColorSpace { get; init; } = GradientColorSpace.LinearRgb;

    /// <summary>色相插值路径。</summary>
    public GradientHuePath HuePath { get; init; } = GradientHuePath.Shorter;

    /// <summary>停止点序列，必须按位置递增且至少包含两个点。</summary>
    public ImmutableArray<GradientStop> Stops
    {
        get => _stops.IsDefault ? ImmutableArray<GradientStop>.Empty : _stops;
        init => _stops = value;
    }

    /// <summary>一个可用于渲染的渐变描述是否有效。</summary>
    public bool IsValid
    {
        get
        {
            if (!Enum.IsDefined(Kind) || !Enum.IsDefined(CoordinateSpace)
                || !Enum.IsDefined(ColorSpace) || !Enum.IsDefined(HuePath)
                || !double.IsFinite(AngleDegrees)
                || !double.IsFinite(CenterX) || !double.IsFinite(CenterY)
                || !double.IsFinite(RadiusX) || !double.IsFinite(RadiusY)
                || Stops.Length < 2)
            {
                return false;
            }

            if (Kind == GradientKind.Radial
                && (CenterX is < 0d or > 1d || CenterY is < 0d or > 1d || RadiusX <= 0d || RadiusY <= 0d))
            {
                return false;
            }

            double previous = -1d;

            foreach (GradientStop stop in Stops)
            {
                if (!stop.IsValid || stop.Offset < previous)
                {
                    return false;
                }

                previous = stop.Offset;
            }

            return Stops[0].Offset == 0d && Stops[^1].Offset == 1d;
        }
    }

    /// <summary>创建一个已校验的渐变描述。</summary>
    /// <param name="stops">按位置递增的停止点，首尾必须是 0 和 1。</param>
    /// <param name="kind">渐变类型。</param>
    /// <param name="angleDegrees">方向角。</param>
    /// <param name="coordinateSpace">坐标空间。</param>
    /// <param name="colorSpace">插值空间。</param>
    /// <param name="huePath">色相路径。</param>
    /// <param name="centerX">径向中心横坐标。</param>
    /// <param name="centerY">径向中心纵坐标。</param>
    /// <param name="radiusX">径向横向半径。</param>
    /// <param name="radiusY">径向纵向半径。</param>
    /// <returns>新的渐变描述。</returns>
    /// <exception cref="ArgumentException">停止点序列无效时抛出。</exception>
    public static GradientSpec Create(
        IEnumerable<GradientStop> stops,
        GradientKind kind = GradientKind.Linear,
        double angleDegrees = 0d,
        GradientCoordinateSpace coordinateSpace = GradientCoordinateSpace.Line,
        GradientColorSpace colorSpace = GradientColorSpace.LinearRgb,
        GradientHuePath huePath = GradientHuePath.Shorter,
        double centerX = 0.5d,
        double centerY = 0.5d,
        double radiusX = 0.5d,
        double radiusY = 0.5d)
    {
        ArgumentNullException.ThrowIfNull(stops);

        GradientSpec result = new()
        {
            Kind = kind,
            AngleDegrees = angleDegrees,
            CenterX = centerX,
            CenterY = centerY,
            RadiusX = radiusX,
            RadiusY = radiusY,
            CoordinateSpace = coordinateSpace,
            ColorSpace = colorSpace,
            HuePath = huePath,
            Stops = [.. stops],
        };

        return result.IsValid
            ? result
            : throw new ArgumentException("渐变停止点必须至少有两个、按位置递增，且首尾位置为 0 和 1。", nameof(stops));
    }

    /// <summary>按停止点内容比较两个渐变，而不是比较数组引用。</summary>
    /// <param name="other">另一个渐变。</param>
    /// <returns>所有参数和停止点内容相同时为 <see langword="true"/>。</returns>
    public bool Equals(GradientSpec? other)
    {
        if (other is null)
        {
            return false;
        }

        return Kind == other.Kind
            && AngleDegrees.Equals(other.AngleDegrees)
            && CenterX.Equals(other.CenterX)
            && CenterY.Equals(other.CenterY)
            && RadiusX.Equals(other.RadiusX)
            && RadiusY.Equals(other.RadiusY)
            && CoordinateSpace == other.CoordinateSpace
            && ColorSpace == other.ColorSpace
            && HuePath == other.HuePath
            && SequenceEquality.ContentEquals(Stops, other.Stops);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Kind);
        hash.Add(AngleDegrees);
        hash.Add(CenterX);
        hash.Add(CenterY);
        hash.Add(RadiusX);
        hash.Add(RadiusY);
        hash.Add(CoordinateSpace);
        hash.Add(ColorSpace);
        hash.Add(HuePath);
        SequenceEquality.AddContentHash(ref hash, Stops);
        return hash.ToHashCode();
    }
}
