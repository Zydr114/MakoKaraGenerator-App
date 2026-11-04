using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>一次最终几何投影的累计剪交工作账；所有目标共享，达到上限后不分配后续片段。</summary>
public sealed class TextGeometryProjectionBudget
{
    /// <summary>全部目标与各层剪交产生的累计顶点硬上限。</summary>
    public const int MaximumVertices = 65_536;
    /// <summary>已经准入的片段顶点数。</summary>
    public int Vertices { get; private set; }
    internal void Reserve(int count)
    {
        if (count < 0 || count > MaximumVertices - Vertices)
            throw new EffectOutputException("WarpGeometryBudgetExceeded", "文字三角剪交累计超过 65536 个顶点。");
        Vertices = checked(Vertices + count);
    }
}

public sealed partial class TextGeometryMapping
{
    /// <summary>投影整个矩形与每层网格三角的全部剪交顶点；不能用四个角替代弯曲范围。</summary>
    public ImmutableArray<Point2> MapRectangle(Rect2 rectangle, TextGeometryProjectionBudget? budget = null)
    {
        budget ??= new();
        List<ImmutableArray<Point2>> pieces = [Corners(rectangle)];
        budget.Reserve(4);
        foreach (var step in Steps)
        {
            if (step is Affine affine)
            {
                pieces = [.. pieces.Select(piece => piece.Select(point => Apply(affine.Matrix, point)).ToImmutableArray())];
                continue;
            }
            var warp = ((Grid)step).Warp;
            List<ImmutableArray<Point2>> projected = [];
            foreach (var piece in pieces)
            {
                foreach (var point in piece)
                    if (!Contains(warp.Domain, point))
                        throw new EffectOutputException("WarpOutsideDomain", "网格定义域必须包含全部子层输出及实际文字区域。");
                for (int index = 0; index < warp.TriangleIndices.Length; index += 3)
                {
                    int a = warp.TriangleIndices[index], b = warp.TriangleIndices[index + 1], c = warp.TriangleIndices[index + 2];
                    var sa = warp.SourceVertices[a]; var sb = warp.SourceVertices[b]; var sc = warp.SourceVertices[c];
                    if (!Overlaps(piece, sa, sb, sc)) continue;
                    var clipped = Clip(Clip(Clip(piece, sa, sb), sb, sc), sc, sa);
                    if (clipped.Length < 3 || SignedArea(clipped) == 0) continue;
                    budget.Reserve(clipped.Length);
                    var da = warp.DestinationVertices[a]; var db = warp.DestinationVertices[b]; var dc = warp.DestinationVertices[c];
                    double denominator = Cross(sa, sb, sc);
                    projected.Add([.. clipped.Select(point =>
                    {
                        double u = Cross(sa, point, sc) / denominator, v = Cross(sa, sb, point) / denominator;
                        return new Point2(da.X + u * (db.X - da.X) + v * (dc.X - da.X),
                            da.Y + u * (db.Y - da.Y) + v * (dc.Y - da.Y));
                    })]);
                }
            }
            pieces = projected;
        }
        return [.. pieces.SelectMany(static piece => piece)];
    }

    /// <summary>所有三角 Jacobian 与后续 affine 的保守最大放大量；用于分配前确定局部像素密度。</summary>
    public double MaximumScale()
    {
        double scale = 1;
        foreach (var step in Steps)
        {
            double next;
            if (step is Affine affine) next = Norm(affine.Matrix.A, affine.Matrix.B, affine.Matrix.C, affine.Matrix.D);
            else
            {
                var warp = ((Grid)step).Warp; next = 0;
                for (int index = 0; index < warp.TriangleIndices.Length; index += 3)
                {
                    var a = warp.SourceVertices[warp.TriangleIndices[index]];
                    var b = warp.SourceVertices[warp.TriangleIndices[index + 1]];
                    var c = warp.SourceVertices[warp.TriangleIndices[index + 2]];
                    var point = new Point2((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3);
                    var x = warp.MapVectorAt(point, new(1, 0)); var y = warp.MapVectorAt(point, new(0, 1));
                    next = Math.Max(next, Norm(x.X, x.Y, y.X, y.Y));
                }
            }
            scale *= next;
            if (!double.IsFinite(scale) || scale <= 0) throw Invalid("文字映射放大量超出有限范围。");
        }
        return scale;
    }

    private static double Norm(double a, double b, double c, double d)
        => Math.Sqrt(Math.Max(Math.Abs(a) + Math.Abs(b), Math.Abs(c) + Math.Abs(d))
            * Math.Max(Math.Abs(a) + Math.Abs(c), Math.Abs(b) + Math.Abs(d)));
    private static ImmutableArray<Point2> Corners(Rect2 rect)
        => [new(rect.Left, rect.Top), new(rect.Right, rect.Top), new(rect.Right, rect.Bottom), new(rect.Left, rect.Bottom)];
    private static bool Contains(Rect2 rect, Point2 point)
        => point.X >= rect.Left && point.X <= rect.Right && point.Y >= rect.Top && point.Y <= rect.Bottom;
    private static bool Overlaps(ImmutableArray<Point2> polygon, Point2 a, Point2 b, Point2 c)
        => polygon.Min(static point => point.X) <= Math.Max(a.X, Math.Max(b.X, c.X))
            && polygon.Max(static point => point.X) >= Math.Min(a.X, Math.Min(b.X, c.X))
            && polygon.Min(static point => point.Y) <= Math.Max(a.Y, Math.Max(b.Y, c.Y))
            && polygon.Max(static point => point.Y) >= Math.Min(a.Y, Math.Min(b.Y, c.Y));
    private static ImmutableArray<Point2> Clip(ImmutableArray<Point2> polygon, Point2 a, Point2 b)
    {
        if (polygon.IsEmpty) return [];
        var result = ImmutableArray.CreateBuilder<Point2>();
        var previous = polygon[^1]; double before = Cross(a, b, previous);
        foreach (var current in polygon)
        {
            double after = Cross(a, b, current);
            if ((before >= 0) != (after >= 0))
            {
                double amount = before / (before - after);
                result.Add(new(previous.X + amount * (current.X - previous.X), previous.Y + amount * (current.Y - previous.Y)));
            }
            if (after >= 0) result.Add(current);
            previous = current; before = after;
        }
        return result.ToImmutable();
    }
    private static double Cross(Point2 a, Point2 b, Point2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
    private static double SignedArea(ImmutableArray<Point2> polygon)
    {
        double area = 0;
        for (int index = 0; index < polygon.Length; index++)
        {
            var a = polygon[index]; var b = polygon[(index + 1) % polygon.Length]; area += a.X * b.Y - a.Y * b.X;
        }
        return area;
    }
}
