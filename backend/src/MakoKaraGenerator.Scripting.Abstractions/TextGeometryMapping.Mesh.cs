using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>文字矩形的一次精确分片投影；每个三角同时保留源坐标与最终目标坐标。</summary>
public sealed class TextGeometryProjectionMesh
{
    /// <summary>创建不可变投影网格；索引使用 16 位值，消费者可按片段拆分更大的输入。</summary>
    public TextGeometryProjectionMesh(
        ImmutableArray<Point2> sourceVertices,
        ImmutableArray<Point2> destinationVertices,
        ImmutableArray<ushort> triangleIndices)
    {
        if (sourceVertices.IsDefault || destinationVertices.IsDefault || triangleIndices.IsDefault
            || sourceVertices.Length != destinationVertices.Length
            || triangleIndices.Length % 3 != 0)
            throw new ArgumentException("投影网格需要等长顶点数组和完整的三角索引。", nameof(triangleIndices));
        if (sourceVertices.Length > ushort.MaxValue + 1)
            throw new EffectOutputException("WarpGeometryBudgetExceeded", "文字三角网格顶点超过 16 位索引可表示的范围。");
        foreach (var index in triangleIndices)
            if (index >= sourceVertices.Length)
                throw new ArgumentException("投影网格索引必须指向顶点数组。", nameof(triangleIndices));

        SourceVertices = sourceVertices;
        DestinationVertices = destinationVertices;
        TriangleIndices = triangleIndices;
    }

    /// <summary>每个目标顶点对应的原始矩形坐标，供纹理 UV 或源采样使用。</summary>
    public ImmutableArray<Point2> SourceVertices { get; }
    /// <summary>每个源顶点经过完整映射链后的目标坐标。</summary>
    public ImmutableArray<Point2> DestinationVertices { get; }
    /// <summary>以三元组排列的 16 位顶点索引；最大顶点编号为 65535。</summary>
    public ImmutableArray<ushort> TriangleIndices { get; }
    /// <summary>网格顶点数量。</summary>
    public int VertexCount => SourceVertices.Length;
    /// <summary>网格三角形数量。</summary>
    public int TriangleCount => TriangleIndices.Length / 3;
}

public sealed partial class TextGeometryMapping
{
    /// <summary>
    /// 将矩形精确投影为带源坐标的三角网格。每个 Grid 按其源三角剪交，
    /// 交点的源坐标与目标坐标同步插值；不会以采样点近似弯曲映射。
    /// </summary>
    public TextGeometryProjectionMesh ProjectRectangle(Rect2 rectangle, TextGeometryProjectionBudget? budget = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        budget ??= new();
        var initial = Corners(rectangle)
            .Select(static point => new ProjectionPoint(point, point))
            .ToImmutableArray();
        budget.Reserve(initial.Length);
        List<ImmutableArray<ProjectionPoint>> pieces = [initial];

        foreach (var step in Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (step is Affine affine)
            {
                pieces = [.. pieces.Select(piece => piece
                    .Select(point => new ProjectionPoint(point.Source, Apply(affine.Matrix, point.Current)))
                    .ToImmutableArray())];
                continue;
            }

            var warp = ((Grid)step).Warp;
            List<ImmutableArray<ProjectionPoint>> projected = [];
            foreach (var piece in pieces)
            {
                foreach (var point in piece)
                    if (!Contains(warp.Domain, point.Current))
                        throw new EffectOutputException("WarpOutsideDomain", "网格定义域必须包含全部子层输出及实际文字区域。");

                for (int index = 0; index < warp.TriangleIndices.Length; index += 3)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int a = warp.TriangleIndices[index], b = warp.TriangleIndices[index + 1], c = warp.TriangleIndices[index + 2];
                    var sa = warp.SourceVertices[a];
                    var sb = warp.SourceVertices[b];
                    var sc = warp.SourceVertices[c];
                    if (!Overlaps(piece, sa, sb, sc)) continue;

                    var clipped = Simplify(Clip(Clip(Clip(piece, sa, sb), sb, sc), sc, sa));
                    if (clipped.Length < 3 || SignedArea(clipped) == 0) continue;
                    budget.Reserve(clipped.Length);

                    var da = warp.DestinationVertices[a];
                    var db = warp.DestinationVertices[b];
                    var dc = warp.DestinationVertices[c];
                    double denominator = Cross(sa, sb, sc);
                    projected.Add([.. clipped.Select(point =>
                    {
                        double u = Cross(sa, point.Current, sc) / denominator;
                        double v = Cross(sa, sb, point.Current) / denominator;
                        var destination = new Point2(
                            da.X + u * (db.X - da.X) + v * (dc.X - da.X),
                            da.Y + u * (db.Y - da.Y) + v * (dc.Y - da.Y));
                        return new ProjectionPoint(point.Source, destination);
                    })]);
                }
            }

            pieces = projected;
        }

        var sourceVertices = ImmutableArray.CreateBuilder<Point2>();
        var destinationVertices = ImmutableArray.CreateBuilder<Point2>();
        var triangleIndices = ImmutableArray.CreateBuilder<ushort>();
        foreach (var piece in pieces)
        {
            if (piece.Length < 3) continue;
            int first = destinationVertices.Count;
            foreach (var point in piece)
            {
                sourceVertices.Add(point.Source);
                destinationVertices.Add(point.Current);
            }
            for (int index = 1; index + 1 < piece.Length; index++)
            {
                triangleIndices.Add(ToIndex(first));
                triangleIndices.Add(ToIndex(first + index));
                triangleIndices.Add(ToIndex(first + index + 1));
            }
        }

        var result = new TextGeometryProjectionMesh(
            sourceVertices.ToImmutable(), destinationVertices.ToImmutable(), triangleIndices.ToImmutable());
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private readonly record struct ProjectionPoint(Point2 Source, Point2 Current);

    private static ImmutableArray<ProjectionPoint> Clip(
        ImmutableArray<ProjectionPoint> polygon, Point2 a, Point2 b)
    {
        if (polygon.IsEmpty) return [];
        var result = ImmutableArray.CreateBuilder<ProjectionPoint>();
        var previous = polygon[^1];
        double before = Cross(a, b, previous.Current);
        foreach (var current in polygon)
        {
            double after = Cross(a, b, current.Current);
            if ((before >= 0) != (after >= 0))
            {
                double amount = before / (before - after);
                result.Add(Interpolate(previous, current, amount));
            }
            if (after >= 0) result.Add(current);
            previous = current;
            before = after;
        }
        return result.ToImmutable();
    }

    private static ProjectionPoint Interpolate(ProjectionPoint from, ProjectionPoint to, double amount)
        => new(Interpolate(from.Source, to.Source, amount), Interpolate(from.Current, to.Current, amount));

    private static Point2 Interpolate(Point2 from, Point2 to, double amount)
        => new(from.X + amount * (to.X - from.X), from.Y + amount * (to.Y - from.Y));

    private static bool Overlaps(ImmutableArray<ProjectionPoint> polygon, Point2 a, Point2 b, Point2 c)
        => polygon.Min(static point => point.Current.X) <= Math.Max(a.X, Math.Max(b.X, c.X))
            && polygon.Max(static point => point.Current.X) >= Math.Min(a.X, Math.Min(b.X, c.X))
            && polygon.Min(static point => point.Current.Y) <= Math.Max(a.Y, Math.Max(b.Y, c.Y))
            && polygon.Max(static point => point.Current.Y) >= Math.Min(a.Y, Math.Min(b.Y, c.Y));

    private static double SignedArea(ImmutableArray<ProjectionPoint> polygon)
    {
        double area = 0;
        for (int index = 0; index < polygon.Length; index++)
        {
            var a = polygon[index].Current;
            var b = polygon[(index + 1) % polygon.Length].Current;
            area += a.X * b.Y - a.Y * b.X;
        }
        return area;
    }

    private static ImmutableArray<ProjectionPoint> Simplify(ImmutableArray<ProjectionPoint> polygon)
    {
        if (polygon.Length < 2) return polygon;
        var result = ImmutableArray.CreateBuilder<ProjectionPoint>(polygon.Length);
        foreach (var point in polygon)
            if (result.Count == 0 || result[^1] != point) result.Add(point);
        if (result.Count > 1 && result[0] == result[^1]) result.RemoveAt(result.Count - 1);

        bool removed;
        do
        {
            removed = false;
            for (int index = 0; index < result.Count && result.Count >= 3; index++)
            {
                var previous = result[(index + result.Count - 1) % result.Count].Current;
                var current = result[index].Current;
                var next = result[(index + 1) % result.Count].Current;
                if (Cross(previous, current, next) == 0)
                {
                    result.RemoveAt(index);
                    removed = true;
                    break;
                }
            }
        } while (removed);
        return result.ToImmutable();
    }

    private static ushort ToIndex(int index)
    {
        if ((uint)index > ushort.MaxValue)
            throw new EffectOutputException("WarpGeometryBudgetExceeded", "文字三角网格索引超过 16 位范围。");
        return (ushort)index;
    }
}
