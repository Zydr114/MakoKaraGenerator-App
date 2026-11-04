using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class TextGeometryMappingMeshTests
{
    [Fact]
    public void 非线性网格投影保留源坐标并以精确三角映射目标坐标()
    {
        var warp = new GridWarp(new(0, 0, 2, 2), 2, 2,
            [new(0, 0), new(1, 0), new(2, 0),
             new(0, 1), new(1.25, 1.5), new(2, 1),
             new(0, 2), new(1, 2), new(2, 2)]);
        var mapping = new TextGeometryMapping([new TextGeometryMapping.Grid(warp)]);

        var mesh = mapping.ProjectRectangle(new(0, 0, 2, 2));

        Assert.Equal(24, mesh.VertexCount);
        Assert.Equal(8, mesh.TriangleCount);
        Assert.Equal(mesh.VertexCount, mesh.TriangleIndices.Length);
        for (int index = 0; index < mesh.VertexCount; index++)
        {
            var expected = warp.MapPoint(mesh.SourceVertices[index]);
            Assert.Equal(expected.X, mesh.DestinationVertices[index].X, 12);
            Assert.Equal(expected.Y, mesh.DestinationVertices[index].Y, 12);
        }
        Assert.Contains(mesh.SourceVertices, static point => point == new Point2(1, 1));
        Assert.Contains(mesh.DestinationVertices, static point => point == new Point2(1.25, 1.5));
    }

    [Fact]
    public void 多级网格与仿射反射保持源UV对应并复用链式映射语义()
    {
        var first = new GridWarp(new(0, 0, 1, 1), 1, 1,
            [new(0, 0), new(2, 0), new(0, 1), new(2, 1)]);
        var second = new GridWarp(new(2, 0, 4, 1), 1, 1,
            [new(2, 0), new(3, 0), new(2, 1), new(3, 1)]);
        var mapping = new TextGeometryMapping([
            new TextGeometryMapping.Grid(first),
            new TextGeometryMapping.Affine(new(-1, 0, 0, 1, 4, 0)),
            new TextGeometryMapping.Grid(second),
            new TextGeometryMapping.Affine(new(1, 0, 0, -1, 0, 10))]);

        var mesh = mapping.ProjectRectangle(new(0, 0, 1, 1));

        Assert.Equal(12, mesh.VertexCount);
        Assert.Equal(4, mesh.TriangleCount);
        for (int index = 0; index < mesh.VertexCount; index++)
        {
            var expected = mapping.MapPoint(mesh.SourceVertices[index]);
            Assert.Equal(expected.X, mesh.DestinationVertices[index].X, 12);
            Assert.Equal(expected.Y, mesh.DestinationVertices[index].Y, 12);
        }
        Assert.Contains(mesh.SourceVertices, static point => point == new Point2(0, 0));
        Assert.Contains(mesh.DestinationVertices, static point => point == new Point2(3, 10));
        Assert.Contains(mesh.DestinationVertices, static point => point == new Point2(2, 9));
    }

    [Fact]
    public void 定义域检查发生在每层当前坐标且保留现有错误码()
    {
        var bounded = new TextGeometryMapping([new TextGeometryMapping.Grid(IdentityGrid())]);
        Assert.Equal("WarpOutsideDomain", Assert.Throws<EffectOutputException>(() =>
            bounded.ProjectRectangle(new(-.1, 0, .5, .5))).Code);

        var mapping = new TextGeometryMapping([
            new TextGeometryMapping.Affine(Affine2.Translation(2, 0)),
            new TextGeometryMapping.Grid(IdentityGrid())]);
        Assert.Equal("WarpOutsideDomain", Assert.Throws<EffectOutputException>(() =>
            mapping.ProjectRectangle(new(0, 0, .5, .5))).Code);
    }

    [Fact]
    public void 共享预算在追加后续网格片段前拒绝超限()
    {
        var warp = IdentityGrid(32, 32);
        var mapping = new TextGeometryMapping([new TextGeometryMapping.Grid(warp)]);
        var budget = new TextGeometryProjectionBudget();

        EffectOutputException? failure = null;
        for (int count = 0; count < 100 && failure is null; count++)
        {
            try { mapping.ProjectRectangle(warp.Domain, budget); }
            catch (EffectOutputException exception) { failure = exception; }
        }
        Assert.NotNull(failure);
        Assert.Equal("WarpGeometryBudgetExceeded", failure.Code);
        Assert.True(budget.Vertices > TextGeometryProjectionBudget.MaximumVertices - 20_000);
    }

    [Fact]
    public void 已取消的投影不占预算且同一预算可由未取消的请求恢复()
    {
        var warp = IdentityGrid();
        var mapping = new TextGeometryMapping([new TextGeometryMapping.Grid(warp)]);
        var budget = new TextGeometryProjectionBudget();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        var failure = Assert.Throws<OperationCanceledException>(() =>
            mapping.ProjectRectangle(warp.Domain, budget, canceled.Token));

        Assert.Equal(canceled.Token, failure.CancellationToken);
        Assert.Equal(0, budget.Vertices);

        var recovered = mapping.ProjectRectangle(warp.Domain, budget, CancellationToken.None);
        var fresh = mapping.ProjectRectangle(warp.Domain);
        Assert.Equal(2, recovered.TriangleCount);
        Assert.Equal(fresh.SourceVertices.ToArray(), recovered.SourceVertices.ToArray());
        Assert.Equal(fresh.DestinationVertices.ToArray(), recovered.DestinationVertices.ToArray());
        Assert.Equal(fresh.TriangleIndices.ToArray(), recovered.TriangleIndices.ToArray());
        Assert.True(budget.Vertices > 0);
    }

    [Fact]
    public void 网格容纳十六位最大顶点编号而拒绝越界索引()
    {
        var points = ImmutableArray.CreateRange(Enumerable.Repeat(new Point2(0, 0), ushort.MaxValue + 1));
        var accepted = new TextGeometryProjectionMesh(points, points,
            [(ushort)0, ushort.MaxValue, (ushort)1]);
        Assert.Equal(ushort.MaxValue, accepted.TriangleIndices[1]);
        Assert.Throws<ArgumentException>(() => new TextGeometryProjectionMesh(
            [new Point2(0, 0)], [new Point2(0, 0)], [1]));
        var tooMany = points.Add(new Point2(0, 0));
        Assert.Equal("WarpGeometryBudgetExceeded", Assert.Throws<EffectOutputException>(() =>
            new TextGeometryProjectionMesh(tooMany, tooMany, [])).Code);
    }

    private static GridWarp IdentityGrid(int columns = 1, int rows = 1)
    {
        var domain = new Rect2(0, 0, 1, 1);
        var vertices = ImmutableArray.CreateBuilder<Point2>((columns + 1) * (rows + 1));
        for (int row = 0; row <= rows; row++)
        for (int column = 0; column <= columns; column++)
            vertices.Add(new(domain.Left + domain.Width * column / columns,
                domain.Top + domain.Height * row / rows));
        return new GridWarp(domain, columns, rows, vertices.MoveToImmutable());
    }
}
