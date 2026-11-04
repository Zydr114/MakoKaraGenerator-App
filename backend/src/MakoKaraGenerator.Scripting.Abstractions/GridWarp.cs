using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>封闭的分片线性文字网格；CPU 映射与 native mesh 共用单精度规范顶点。</summary>
public sealed class GridWarp : IEquatable<GridWarp>
{
    private readonly ImmutableArray<double> _sourceX;
    private readonly ImmutableArray<double> _sourceY;

    /// <summary>创建最多 32×32 格的正向网格；源域与目标顶点只在此处转换为有限 float。</summary>
    /// <param name="domain">目标原有逻辑坐标中的非空源域，不重定位到左上角。</param>
    /// <param name="columns">横向格数，范围为 1 到 32。</param>
    /// <param name="rows">纵向格数，范围为 1 到 32。</param>
    /// <param name="destinationVertices">按行优先排列的目标顶点，数量为 (columns+1)×(rows+1)。</param>
    public GridWarp(Rect2 domain, int columns, int rows, ImmutableArray<Point2> destinationVertices)
    {
        if (columns is < 1 or > 32 || rows is < 1 or > 32)
            throw Invalid("网格每轴需要 1 到 32 格。");
        if (domain.Width <= 0 || domain.Height <= 0 || destinationVertices.IsDefault
            || destinationVertices.Length != (columns + 1) * (rows + 1))
            throw Invalid("网格需要非空源域和完整的行优先目标顶点。");
        Columns = columns; Rows = rows;
        _sourceX = Axis(domain.Left, domain.Right, columns);
        _sourceY = Axis(domain.Top, domain.Bottom, rows);
        Domain = new(_sourceX[0], _sourceY[0], _sourceX[^1], _sourceY[^1]);
        var source = ImmutableArray.CreateBuilder<Point2>(destinationVertices.Length);
        var destination = ImmutableArray.CreateBuilder<Point2>(destinationVertices.Length);
        for (int row = 0; row <= rows; row++)
        for (int column = 0; column <= columns; column++)
        {
            source.Add(new(_sourceX[column], _sourceY[row]));
            var point = destinationVertices[row * (columns + 1) + column];
            destination.Add(new(Canonical(point.X), Canonical(point.Y)));
        }
        SourceVertices = source.MoveToImmutable(); DestinationVertices = destination.MoveToImmutable();
        var indices = ImmutableArray.CreateBuilder<ushort>(columns * rows * 6);
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
        {
            int tl = row * (columns + 1) + column, tr = tl + 1, bl = tl + columns + 1, br = bl + 1;
            AddTriangle(tl, tr, br); AddTriangle(tl, br, bl);
        }
        TriangleIndices = indices.MoveToImmutable();
        ValidateBoundary();
        IsIdentity = SourceVertices.AsSpan().SequenceEqual(DestinationVertices.AsSpan());
        DestinationBounds = new(DestinationVertices.Min(static point => point.X), DestinationVertices.Min(static point => point.Y),
            DestinationVertices.Max(static point => point.X), DestinationVertices.Max(static point => point.Y));

        void AddTriangle(int a, int b, int c)
        {
            ValidateTriangle(SourceVertices[a], SourceVertices[b], SourceVertices[c]);
            ValidateTriangle(DestinationVertices[a], DestinationVertices[b], DestinationVertices[c]);
            indices.Add((ushort)a); indices.Add((ushort)b); indices.Add((ushort)c);
        }
    }

    /// <summary>已规范为 float 的闭源域；消费者必须使用此值，不能重建原 double 域。</summary>
    public Rect2 Domain { get; }
    /// <summary>横向格数。</summary>
    public int Columns { get; }
    /// <summary>纵向格数。</summary>
    public int Rows { get; }
    /// <summary>行优先规范源顶点；逻辑值精确表示其 native float，不是归一化 UV。</summary>
    public ImmutableArray<Point2> SourceVertices { get; }
    /// <summary>行优先规范目标顶点；与源顶点和三角索引共同定义唯一 mesh。</summary>
    public ImmutableArray<Point2> DestinationVertices { get; }
    /// <summary>每格依次 TL/TR/BR、TL/BR/BL 的 UInt16 索引；不得另选对角线。</summary>
    public ImmutableArray<ushort> TriangleIndices { get; }
    /// <summary>全部规范目标顶点的保守边界；子矩形的投影仍须处理其与源三角的交点。</summary>
    public Rect2 DestinationBounds { get; }
    /// <summary>所有规范目标顶点均等于源顶点；仍保留闭源域检查。</summary>
    public bool IsIdentity { get; }

    /// <summary>使用所属三角的重心坐标正向映射；闭外边界合法，域外不夹值或外推。</summary>
    public Point2 MapPoint(Point2 point)
    {
        var cell = Select(point);
        if (IsIdentity) return point;
        int tl = cell.Row * (Columns + 1) + cell.Column, tr = tl + 1, bl = tl + Columns + 1, br = bl + 1;
        var a = DestinationVertices[tl];
        var b = DestinationVertices[cell.First ? tr : br];
        var c = DestinationVertices[cell.First ? br : bl];
        double wb = cell.First ? cell.U - cell.V : cell.U;
        double wc = cell.First ? cell.V : cell.V - cell.U;
        return Point(a.X + wb * (b.X - a.X) + wc * (c.X - a.X),
            a.Y + wb * (b.Y - a.Y) + wc * (c.Y - a.Y));
    }

    /// <summary>使用源点所属三角的 Jacobian 映射向量，不加入平移、不自动单位化。</summary>
    public Vector2 MapVectorAt(Point2 point, Vector2 vector)
    {
        var cell = Select(point);
        if (IsIdentity) return vector;
        int tl = cell.Row * (Columns + 1) + cell.Column, tr = tl + 1, bl = tl + Columns + 1, br = bl + 1;
        var xFrom = DestinationVertices[cell.First ? tl : bl];
        var xTo = DestinationVertices[cell.First ? tr : br];
        var yFrom = DestinationVertices[cell.First ? tr : tl];
        var yTo = DestinationVertices[cell.First ? br : bl];
        double dx = _sourceX[cell.Column + 1] - _sourceX[cell.Column], dy = _sourceY[cell.Row + 1] - _sourceY[cell.Row];
        return Vector((xTo.X - xFrom.X) / dx * vector.X + (yTo.X - yFrom.X) / dy * vector.Y,
            (xTo.Y - xFrom.Y) / dx * vector.X + (yTo.Y - yFrom.Y) / dy * vector.Y);
    }

    /// <summary>比较规范源域、格数和全部规范目标顶点。</summary>
    public bool Equals(GridWarp? other) => ReferenceEquals(this, other) || other is not null
        && Domain == other.Domain && Columns == other.Columns && Rows == other.Rows
        && SourceVertices.AsSpan().SequenceEqual(other.SourceVertices.AsSpan())
        && DestinationVertices.AsSpan().SequenceEqual(other.DestinationVertices.AsSpan());
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is GridWarp other && Equals(other);
    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new(); hash.Add(Domain); hash.Add(Columns); hash.Add(Rows);
        foreach (var point in SourceVertices) hash.Add(point);
        foreach (var point in DestinationVertices) hash.Add(point);
        return hash.ToHashCode();
    }
    /// <summary>比较两个可能为空的规范网格值。</summary>
    public static bool operator ==(GridWarp? left, GridWarp? right) => Equals(left, right);
    /// <summary>比较两个可能为空的规范网格值是否不同。</summary>
    public static bool operator !=(GridWarp? left, GridWarp? right) => !Equals(left, right);

    private readonly record struct Cell(int Column, int Row, double U, double V, bool First);
    private Cell Select(Point2 point)
    {
        if (point.X < Domain.Left || point.X > Domain.Right || point.Y < Domain.Top || point.Y > Domain.Bottom)
            throw new EffectOutputException("WarpOutsideDomain", "坐标超出网格的规范逻辑定义域。");
        int column = Interval(_sourceX, point.X), row = Interval(_sourceY, point.Y);
        double u = (point.X - _sourceX[column]) / (_sourceX[column + 1] - _sourceX[column]);
        double v = (point.Y - _sourceY[row]) / (_sourceY[row + 1] - _sourceY[row]);
        bool first = (point.Y - _sourceY[row]) * (_sourceX[column + 1] - _sourceX[column])
            <= (point.X - _sourceX[column]) * (_sourceY[row + 1] - _sourceY[row]);
        return new(column, row, u, v, first);
    }
    private static int Interval(ImmutableArray<double> axis, double value)
    {
        // upper_bound selects the right/bottom half-open cell on internal edges.
        int low = 0, high = axis.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (axis[middle] <= value) low = middle + 1; else high = middle;
        }
        return Math.Min(low - 1, axis.Length - 2);
    }
    private static ImmutableArray<double> Axis(double start, double end, int cells)
    {
        var result = ImmutableArray.CreateBuilder<double>(cells + 1);
        for (int index = 0; index <= cells; index++)
        {
            double value = Canonical(index == cells ? end : start + (end - start) * index / cells);
            if (index != 0 && value <= result[index - 1]) throw Invalid("源轴转换为 float 后必须严格递增。");
            result.Add(value);
        }
        return result.MoveToImmutable();
    }
    private static double Canonical(double value)
    {
        float result = (float)value;
        if (!float.IsFinite(result)) throw Invalid("网格分量必须能表示为有限 float。");
        return result == 0 ? 0 : result;
    }
    private static void ValidateTriangle(Point2 a, Point2 b, Point2 c)
    {
        // Validate the signed area in native precision after canonicalization.
        // No epsilon, topology repair, reflection or sub-float collapse is allowed.
        float abx = (float)(b.X - a.X), aby = (float)(b.Y - a.Y);
        float acx = (float)(c.X - a.X), acy = (float)(c.Y - a.Y);
        float area = abx * acy - aby * acx;
        if (!float.IsFinite(area) || area <= 0 || Cross(a, b, c) <= 0)
            throw Invalid("规范三角必须有限、正向且不退化。");
    }
    private void ValidateBoundary()
    {
        var boundary = new List<Point2>(2 * (Columns + Rows));
        for (int column = 0; column <= Columns; column++) boundary.Add(DestinationVertices[column]);
        for (int row = 1; row <= Rows; row++) boundary.Add(DestinationVertices[row * (Columns + 1) + Columns]);
        for (int column = Columns - 1; column >= 0; column--) boundary.Add(DestinationVertices[Rows * (Columns + 1) + column]);
        for (int row = Rows - 1; row > 0; row--) boundary.Add(DestinationVertices[row * (Columns + 1)]);
        for (int first = 0; first < boundary.Count; first++)
        {
            var a = boundary[first]; var b = boundary[(first + 1) % boundary.Count];
            var next = boundary[(first + 2) % boundary.Count];
            if (a == b || Cross(a, b, next) == 0 && (b.X - a.X) * (next.X - b.X) + (b.Y - a.Y) * (next.Y - b.Y) <= 0)
                throw Invalid("网格外边界不能重合或反向重叠。");
            for (int second = first + 1; second < boundary.Count; second++)
            {
                if (second == first + 1 || first == 0 && second == boundary.Count - 1) continue;
                if (Intersects(a, b, boundary[second], boundary[(second + 1) % boundary.Count]))
                    throw Invalid("网格外边界必须简单，不能自交或接触非相邻边。");
            }
        }
    }
    private static bool Intersects(Point2 a, Point2 b, Point2 c, Point2 d)
    {
        double ac = Cross(a, b, c), ad = Cross(a, b, d), ca = Cross(c, d, a), cb = Cross(c, d, b);
        if (ac == 0 && OnSegment(a, b, c) || ad == 0 && OnSegment(a, b, d)
            || ca == 0 && OnSegment(c, d, a) || cb == 0 && OnSegment(c, d, b)) return true;
        return (ac > 0 && ad < 0 || ac < 0 && ad > 0) && (ca > 0 && cb < 0 || ca < 0 && cb > 0);
    }
    private static bool OnSegment(Point2 a, Point2 b, Point2 p) => p.X >= Math.Min(a.X, b.X) && p.X <= Math.Max(a.X, b.X)
        && p.Y >= Math.Min(a.Y, b.Y) && p.Y <= Math.Max(a.Y, b.Y);
    private static double Cross(Point2 a, Point2 b, Point2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
    private static Point2 Point(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw Invalid("网格位置运算溢出。");
        return new(x, y);
    }
    private static Vector2 Vector(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw Invalid("网格向量运算溢出。");
        return new(x, y);
    }
    private static EffectOutputException Invalid(string message) => new("InvalidGridWarp", message);
}
