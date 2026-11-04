using System.Collections.Immutable;

#pragma warning disable CA1806

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class GridWarpTests
{
    [Fact]
    public void 恒等规范顶点与最大网格索引共享一个不可变值()
    {
        var domain = new Rect2(-16, -8, 16, 24);
        var original = Vertices(domain, 32, 32);
        var warp = new GridWarp(domain, 32, 32, original);
        Assert.True(warp.IsIdentity); Assert.Equal(1089, warp.SourceVertices.Length);
        Assert.Equal(6144, warp.TriangleIndices.Length);
        Assert.Equal((ushort)1088, warp.TriangleIndices.Max());
        Assert.Equal(warp.SourceVertices.ToArray(), warp.DestinationVertices.ToArray());
        Assert.Equal(domain, warp.Domain); Assert.Equal(domain, warp.DestinationBounds);
        foreach (var point in new Point2[] { new(-16, -8), new(16, -8), new(16, 24), new(-16, 24), new(.75, 13.125) })
        {
            Assert.Equal(point, warp.MapPoint(point));
            Assert.Equal(new Vector2(3, -7), warp.MapVectorAt(point, new(3, -7)));
        }
        Assert.Equal(new ushort[] { 0, 1, 34, 0, 34, 33 }, warp.TriangleIndices.Take(6));
        var edited = original.SetItem(0, new(-17, -8));
        Assert.NotEqual(edited[0], warp.DestinationVertices[0]);
        Assert.Equal(new Point2(-16, -8), warp.DestinationVertices[0]);
    }

    [Fact]
    public void 非平行四边形按TLBR固定三角重心与独立Jacobian映射()
    {
        var warp = BentCell();
        Assert.False(warp.IsIdentity);
        Assert.Equal(new Point2(8, 3.5), warp.MapPoint(new(13, 20.5)));
        Assert.Equal(new Point2(4, 5.5), warp.MapPoint(new(11, 21.5)));
        Assert.Equal(new Vector2(16, 15), warp.MapVectorAt(new(13, 20.5), new(3, 5)));
        Assert.Equal(new Vector2(9, 11.5), warp.MapVectorAt(new(11, 21.5), new(3, 5)));
        Assert.Equal(new Point2(7, 5), warp.MapPoint(new(12, 21)));
        // The diagonal belongs to TL/TR/BR, even though position is continuous.
        Assert.Equal(new Vector2(2, 0), warp.MapVectorAt(new(12, 21), new(1, 0)));
        Assert.Equal(new Point2(13, 8), warp.MapPoint(new(14, 22)));
        Assert.Equal(new Point2(1, 6), warp.MapPoint(new(10, 22)));
    }

    [Fact]
    public void 内部边界属于右下格且最外闭边界属于末格()
    {
        var horizontal = new GridWarp(new(0, 0, 2, 1), 2, 1,
            [new(0, 0), new(1, 0), new(3, 0), new(0, 1), new(1, 1), new(3, 1)]);
        Assert.Equal(new Vector2(2, 0), horizontal.MapVectorAt(new(1, .5), new(1, 0)));
        Assert.Equal(new Vector2(1, 0), horizontal.MapVectorAt(new(double.BitDecrement(1), .5), new(1, 0)));
        Assert.Equal(new Vector2(2, 0), horizontal.MapVectorAt(new(2, 1), new(1, 0)));
        var vertical = new GridWarp(new(0, 0, 1, 2), 1, 2,
            [new(0, 0), new(1, 0), new(0, 1), new(1, 1), new(0, 4), new(1, 4)]);
        Assert.Equal(new Vector2(0, 3), vertical.MapVectorAt(new(.5, 1), new(0, 1)));
        Assert.Equal(new Vector2(0, 1), vertical.MapVectorAt(new(.5, double.BitDecrement(1)), new(0, 1)));
        Assert.Equal(new Point2(1, 4), vertical.MapPoint(new(1, 2)));
    }

    [Fact]
    public void 中心弯曲由规范中心顶点决定而非四角双线性插值()
    {
        var warp = new GridWarp(new(0, 0, 2, 2), 2, 2,
            [new(0, 0), new(1, 0), new(2, 0), new(0, 1), new(1.25, 1.5), new(2, 1), new(0, 2), new(1, 2), new(2, 2)]);
        Assert.Equal(new Point2(1.25, 1.5), warp.MapPoint(new(1, 1)));
        Assert.Equal(new Point2(.625, .75), warp.MapPoint(new(.5, .5)));
        Assert.Equal(new Rect2(0, 0, 2, 2), warp.DestinationBounds);
        Assert.NotEqual(new Point2(1, 1), warp.MapPoint(new(1, 1)));
    }

    [Fact]
    public void 目标float规范化决定等值哈希与实际映射且负零归零()
    {
        var first = new GridWarp(new(0, 0, 1, 1), 1, 1,
            [new(-0d, 0), new(1 + 1e-9, 0), new(0, 1), new(1, 1)]);
        var second = new GridWarp(new(0, 0, 1, 1), 1, 1,
            [new(0, 0), new(1, 0), new(0, 1), new(1 + 2e-9, 1)]);
        Assert.True(first == second); Assert.False(first != second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode()); Assert.True(first.IsIdentity);
        Assert.Equal(0, BitConverter.DoubleToInt64Bits(first.DestinationVertices[0].X));
        Assert.False(first.Equals(null)); Assert.False(first.Equals(new object()));
        var fractional = new GridWarp(new(.1, .2, 2.1, 2.2), 2, 2, Vertices(new(.1, .2, 2.1, 2.2), 2, 2));
        Assert.True(fractional.IsIdentity);
        Assert.Equal((double)(float).1, fractional.Domain.Left);
        Assert.All(fractional.SourceVertices.Concat(fractional.DestinationVertices), point =>
        { Assert.Equal((double)(float)point.X, point.X); Assert.Equal((double)(float)point.Y, point.Y); });
        Assert.Equal(fractional.Domain.Center, fractional.MapPoint(fractional.Domain.Center));
    }

    [Theory]
    [InlineData(9.999, 21)]
    [InlineData(14.001, 21)]
    [InlineData(12, 19.999)]
    [InlineData(12, 22.001)]
    public void 域外位置与向量都具名拒绝且不夹值(double x, double y)
    {
        var warp = BentCell();
        Assert.Equal("WarpOutsideDomain", Assert.Throws<EffectOutputException>(() => warp.MapPoint(new(x, y))).Code);
        Assert.Equal("WarpOutsideDomain", Assert.Throws<EffectOutputException>(() => warp.MapVectorAt(new(x, y), Vector2.Zero)).Code);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(33, 1)]
    [InlineData(1, 33)]
    public void 格数超过硬限或非正具名拒绝(int columns, int rows)
        => Invalid(() => new GridWarp(new(0, 0, 1, 1), columns, rows, []));

    [Fact]
    public void 缺顶点空域float轴坍缩目标溢出三角退化与翻面全部拒绝()
    {
        Invalid(() => new GridWarp(new(0, 0, 1, 1), 1, 1, default));
        Invalid(() => new GridWarp(new(0, 0, 1, 1), 1, 1, []));
        Invalid(() => new GridWarp(default, 1, 1, Vertices(new(0, 0, 1, 1), 1, 1)));
        Invalid(() => new GridWarp(new(100_000_000, 0, 100_000_001, 1), 1, 1,
            [new(0, 0), new(1, 0), new(0, 1), new(1, 1)]));
        Invalid(() => new GridWarp(new(0, 0, 1, 1), 1, 1,
            [new(0, 0), new(1e100, 0), new(0, 1), new(1, 1)]));
        Invalid(() => new GridWarp(new(0, 0, 1, 1), 1, 1,
            [new(0, 0), new(1, 0), new(0, 1), new(2, 0)]));
        Invalid(() => new GridWarp(new(0, 0, 1, 1), 1, 1,
            [new(1, 0), new(0, 0), new(1, 1), new(0, 1)]));
        // Valid double triangles collapse, underflow or overflow in native float.
        Invalid(() => new GridWarp(new(0, 0, 1, 1), 1, 1,
            [new(1, 1), new(1 + 1e-9, 1), new(1, 2), new(2, 2)]));
        Invalid(() => new GridWarp(new(0, 0, 1, 1), 1, 1,
            [new(0, 0), new(1e-30, 0), new(0, 1e-30), new(1e-30, 1e-30)]));
        Invalid(() => new GridWarp(new(0, 0, 1e30, 1e30), 1, 1,
            [new(0, 0), new(1, 0), new(0, 1), new(1, 1)]));
    }

    [Fact]
    public void 每格正面积仍不能绕回交叉简单边界()
    {
        // A radial strip wraps beyond one turn. All ten local triangles have
        // positive area, while its final outer edge crosses the starting seam.
        Point2[] outer = [new(20, 0), new(0, 20), new(-20, 0), new(0, -20), new(20, -4), new(8, 16)];
        var vertices = outer.Concat(outer.Select(static point => new Point2(point.X / 2, point.Y / 2))).ToImmutableArray();
        for (int column = 0; column < 5; column++)
        {
            Assert.True(Area(vertices[column], vertices[column + 1], vertices[column + 7]) > 0);
            Assert.True(Area(vertices[column], vertices[column + 7], vertices[column + 6]) > 0);
        }
        Invalid(() => new GridWarp(new(0, 0, 5, 1), 5, 1, vertices));
    }

    private static GridWarp BentCell() => new(new(10, 20, 14, 22), 1, 1,
        [new(1, 2), new(9, 2), new(1, 6), new(13, 8)]);
    private static void Invalid(Action action) => Assert.Equal("InvalidGridWarp", Assert.Throws<EffectOutputException>(action).Code);
    private static double Area(Point2 a, Point2 b, Point2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
    private static ImmutableArray<Point2> Vertices(Rect2 domain, int columns, int rows)
    {
        var result = ImmutableArray.CreateBuilder<Point2>((columns + 1) * (rows + 1));
        for (int row = 0; row <= rows; row++)
        for (int column = 0; column <= columns; column++)
            result.Add(new(domain.Left + domain.Width * column / columns, domain.Top + domain.Height * row / rows));
        return result.MoveToImmutable();
    }
}
