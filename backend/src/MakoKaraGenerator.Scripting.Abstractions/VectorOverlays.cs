using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>世界逻辑坐标的平端线段；不再应用文字矩阵。</summary>
public readonly record struct Segment
{
    /// <summary>创建正有限宽度；零长线段合法但没有面积。</summary>
    public Segment(Point2 start, Point2 end, double width, ColorRgba color)
    {
        Start = start; End = end; Width = width; Color = color;
        if (!IsValid) throw new ArgumentOutOfRangeException(nameof(width), "线段宽度或外边界无效。");
    }
    /// <summary>起点。</summary>
    public Point2 Start { get; }
    /// <summary>终点。</summary>
    public Point2 End { get; }
    /// <summary>逻辑像素全宽。</summary>
    public double Width { get; }
    /// <summary>sRGB 直乘颜色。</summary>
    public ColorRgba Color { get; }
    /// <summary>default 线段无效。</summary>
    public bool IsValid => Width > 0 && VectorOverlayValidation.Point(Start, Width) && VectorOverlayValidation.Point(End, Width);
}

/// <summary>带状拖尾的一个世界截面；零宽允许尖端。</summary>
public readonly record struct TrailPoint
{
    /// <summary>创建有限非负截面宽度与直乘颜色。</summary>
    public TrailPoint(Point2 position, double width, ColorRgba color)
    {
        Position = position; Width = width; Color = color;
        if (!IsValid) throw new ArgumentOutOfRangeException(nameof(width), "拖尾截面宽度或外边界无效。");
    }
    /// <summary>截面中心。</summary>
    public Point2 Position { get; }
    /// <summary>左右截面之间的逻辑像素全宽。</summary>
    public double Width { get; }
    /// <summary>sRGB 直乘顶点颜色。</summary>
    public ColorRgba Color { get; }
    /// <summary>零宽有效；拒绝非有限外边界。</summary>
    public bool IsValid => VectorOverlayValidation.Point(Position, Width);
}

/// <summary>不可变带状拖尾；重复位置取最后截面，严格反向断带。</summary>
public sealed record Trail
{
    /// <summary>创建至少两个有序截面；不重排或夹值。</summary>
    public Trail(ImmutableArray<TrailPoint> points)
    {
        if (points.IsDefault || points.Length < 2 || points.Any(static point => !point.IsValid))
            throw new ArgumentException("拖尾须有至少两个有效截面。", nameof(points));
        Points = points;
    }
    /// <summary>世界截面，宽度和颜色由作者纯求值决定。</summary>
    public ImmutableArray<TrailPoint> Points { get; }
}

/// <summary>不可变平端线段批次。</summary>
public sealed record SegmentBatch(EffectInstanceId InstanceId, OutputId Id, OverlayLayer Layer, ImmutableArray<Segment> Segments)
    : OverlayBatch(InstanceId, Id, Layer)
{
    /// <inheritdoc/>
    public override bool IsValid => !Segments.IsDefault && Segments.All(static segment => segment.IsValid);
    /// <inheritdoc/>
    public override long ItemCount => Segments.Length;
}

/// <summary>不可变带状拖尾批次。</summary>
public sealed record TrailBatch(EffectInstanceId InstanceId, OutputId Id, OverlayLayer Layer, ImmutableArray<Trail> Trails)
    : OverlayBatch(InstanceId, Id, Layer)
{
    /// <inheritdoc/>
    public override bool IsValid => !Trails.IsDefault && Trails.All(static trail => trail is not null);
    /// <inheritdoc/>
    public override long ItemCount => Trails.Sum(static trail => (long)trail.Points.Length);
}

internal static class VectorOverlayValidation
{
    public static bool Point(Point2 point, double width) => double.IsFinite(width) && width >= 0
        && double.IsFinite(point.X - width / 2) && double.IsFinite(point.X + width / 2)
        && double.IsFinite(point.Y - width / 2) && double.IsFinite(point.Y + width / 2);
}
