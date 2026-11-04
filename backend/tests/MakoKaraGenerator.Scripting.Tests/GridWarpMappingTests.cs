using System.Collections.Immutable;
using System.Reflection;

#pragma warning disable CA1806

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class GridWarpMappingTests
{
    [Fact]
    public void 空步骤是全域恒等而有界恒等网格仍检查定义域()
    {
        var identity = TextGeometryMapping.Identity;
        Assert.Empty(identity.Steps); Assert.False(identity.HasWarp); Assert.True(identity.IsIdentity);
        Assert.Equal(new Point2(-500, 300), identity.MapPoint(new(-500, 300)));
        Assert.Equal(new Vector2(4, -3), identity.MapVectorAt(new(-500, 300), new(4, -3)));
        var bounded = new TextGeometryMapping([new TextGeometryMapping.Grid(IdentityGrid())]);
        Assert.True(bounded.HasWarp); Assert.True(bounded.IsIdentity);
        Assert.Equal(new Point2(.25, .75), bounded.MapPoint(new(.25, .75)));
        Assert.Equal("WarpOutsideDomain", Assert.Throws<EffectOutputException>(() => bounded.MapPoint(new(-1, 0))).Code);
        Assert.Equal("WarpOutsideDomain", Assert.Throws<EffectOutputException>(() => bounded.MapVectorAt(new(2, 0), Vector2.Zero)).Code);
        Assert.NotEqual(identity, bounded);
    }

    [Fact]
    public void 无网格预组合完整倾斜反射与平移且向量不带平移()
    {
        Affine2 leaf = new(2, 1, .5, 3, 7, 11), parent = new(-1, .25, 2, 1, -4, 6);
        var mapping = new TextGeometryMapping([new TextGeometryMapping.Affine(leaf), new TextGeometryMapping.Affine(parent)]);
        Assert.False(mapping.HasWarp); Assert.False(mapping.IsIdentity);
        var point = new Point2(3, -2); var expected = parent.Map(leaf.Map(point));
        Assert.Equal(expected, mapping.MapPoint(point));
        // Leaf vector=(7,9), parent vector=(11,10.75).
        Assert.Equal(new Vector2(11, 10.75), mapping.MapVectorAt(point, new(3, 2)));
    }

    [Fact]
    public void 多层映射在各自输入点选三角且链式Jacobian遵守叶到父顺序()
    {
        var first = new GridWarp(new(0, 0, 1, 1), 1, 1,
            [new(0, 0), new(2, 0), new(0, 1), new(3, 2)]);
        var second = new GridWarp(new(10, 0, 14, 4), 1, 1,
            [new(0, 0), new(8, 0), new(0, 4), new(12, 8)]);
        var mapping = new TextGeometryMapping([
            new TextGeometryMapping.Grid(first),
            new TextGeometryMapping.Affine(Affine2.Translation(10, 0)),
            new TextGeometryMapping.Grid(second),
            new TextGeometryMapping.Affine(new(2, 0, 1, 3, 100, -5))]);
        // p=(.25,.75) uses the second tri first: p1=(.75,1).
        // The translated p=(10.75,1) uses the second tri again:
        // p2=(2.25,1.75), then the complete parent affine gives (106.25,.25).
        Assert.Equal(new Point2(106.25, .25), mapping.MapPoint(new(.25, .75)));
        // v=(1,0) -> (3,1) -> (9,4) -> (22,12).
        Assert.Equal(new Vector2(22, 12), mapping.MapVectorAt(new(.25, .75), new(1, 0)));
        var reordered = new TextGeometryMapping([
            new TextGeometryMapping.Affine(Affine2.Translation(10, 0)), new TextGeometryMapping.Grid(first)]);
        Assert.Equal("WarpOutsideDomain", Assert.Throws<EffectOutputException>(() => reordered.MapPoint(new(.25, .75))).Code);
    }

    [Fact]
    public void 等值按有序封闭步骤且调用方修改新数组不改变原值()
    {
        ImmutableArray<TextGeometryMapping.Step> steps = [new TextGeometryMapping.Grid(IdentityGrid()),
            new TextGeometryMapping.Affine(Affine2.Translation(3, 5))];
        var first = new TextGeometryMapping(steps);
        var second = new TextGeometryMapping([new TextGeometryMapping.Grid(IdentityGrid()),
            new TextGeometryMapping.Affine(Affine2.Translation(3, 5))]);
        Assert.True(first == second); Assert.False(first != second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.True(first.Steps[0] == second.Steps[0]);
        Assert.False(first.Equals(null)); Assert.False(first.Equals(new object()));
        steps = steps.SetItem(1, new TextGeometryMapping.Affine(Affine2.Translation(7, 9)));
        Assert.NotEqual(first, new TextGeometryMapping(steps));
        Assert.Equal(new Point2(3.5, 5.5), first.MapPoint(new(.5, .5)));
        Assert.All(typeof(TextGeometryMapping.Step).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic),
            static constructor => Assert.True(constructor.IsFamilyAndAssembly));
        Assert.True(typeof(TextGeometryMapping.Affine).IsSealed); Assert.True(typeof(TextGeometryMapping.Grid).IsSealed);
    }

    [Fact]
    public void default空步骤零矩阵与组合溢出拒绝且不生成无效恒等()
    {
        Invalid(() => new TextGeometryMapping(default));
        Invalid(() => new TextGeometryMapping([null!]));
        Invalid(() => new TextGeometryMapping.Affine(default));
        Assert.Throws<ArgumentNullException>(() => new TextGeometryMapping.Grid(null!));
        Affine2 huge = new(1e150, 0, 0, 1e150, 0, 0);
        Assert.True(huge.TryInverse(out _));
        Invalid(() => new TextGeometryMapping([new TextGeometryMapping.Affine(huge),
            new TextGeometryMapping.Affine(huge)]));
        var far = new TextGeometryMapping.Affine(Affine2.Translation(double.MaxValue, 0));
        Invalid(() => new TextGeometryMapping([far, far]));
        var finite = new TextGeometryMapping([new TextGeometryMapping.Affine(new(2, 0, 0, 2, 0, 0))]);
        Invalid(() => finite.MapPoint(new(double.MaxValue, 0)));
        Invalid(() => finite.MapVectorAt(default, new(double.MaxValue, 0)));
    }

    private static GridWarp IdentityGrid() => new(new(0, 0, 1, 1), 1, 1,
        [new(0, 0), new(1, 0), new(0, 1), new(1, 1)]);
    private static void Invalid(Action action)
        => Assert.Equal("InvalidTextGeometryMapping", Assert.Throws<EffectOutputException>(action).Code);
}
