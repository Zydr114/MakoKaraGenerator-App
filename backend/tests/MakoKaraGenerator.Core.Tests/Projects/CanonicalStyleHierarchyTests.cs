using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Projects;

public sealed class CanonicalStyleHierarchyTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 部分描边继承实际父材质和几何且仅空父使用透明零宽(bool hasParent)
    {
        MaterialSpec package = new MaterialSpec.PackageMaterial("pkg", "sha256:" + new string('b', 64), "material");
        StrokeSpec? parent = hasParent ? new() { Material = package, WidthPx = 7, Join = StrokeJoin.Bevel } : null;
        var overlay = new StrokeSpecOverride { Join = StrokeJoin.Miter };
        StrokeSpec result = overlay.Apply(parent);
        Assert.Equal(StrokeJoin.Miter, result.Join);
        Assert.Equal(hasParent ? 7 : 0, result.WidthPx);
        Assert.Equal(hasParent ? package : new MaterialSpec.Solid(RgbaColor.Transparent), result.Material);
        Assert.False(overlay.Material.IsSet); Assert.Null(overlay.WidthPx);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 部分阴影继承实际父参数且空父不会引入可见默认阴影(bool hasParent)
    {
        ShadowSpec? parent = hasParent ? new() { Color = RgbaColor.White, AngleDegrees = 62, DistancePx = 7, BlurRadiusPx = 8 } : null;
        var result = new ShadowSpecOverride { Opacity = .4f }.Apply(parent);
        Assert.Equal(.4f, result.Opacity); Assert.Equal(hasParent ? RgbaColor.White : RgbaColor.Transparent, result.Color);
        Assert.Equal(hasParent ? 62 : 0, result.AngleDegrees); Assert.Equal(hasParent ? 7 : 0, result.DistancePx);
        Assert.Equal(hasParent ? 8 : 0, result.BlurRadiusPx);
    }

    [Fact]
    public void 四层规范继承不读取包颜色兼容外观并保未选参数()
    {
        MaterialSpec material = new MaterialSpec.PackageMaterial("pkg", "sha256:" + new string('b', 64), "material");
        var baseline = LyricStyle.Default with { UnsungFill = new() { Material = material }, Stroke = new() { Material = material, WidthPx = 9, Join = StrokeJoin.Bevel } };
        var result = LyricStyleResolver.Resolve(baseline,
            branchOverride: new() { Typography = new() { FontSizePx = 95 } },
            lineOverride: new() { Stroke = OptionalValue.Set<StrokeSpecOverride?>(new() { WidthPx = 6 }) },
            tokenOverride: new() { Opacity = .3f });
        Assert.Same(material, result.Style.UnsungFill.Material); Assert.Same(material, result.Style.Stroke!.Material);
        Assert.Equal(StrokeJoin.Bevel, result.Style.Stroke.Join); Assert.Equal(6, result.Style.Stroke.WidthPx);
        Assert.Equal(95, result.Style.Typography.FontSizePx); Assert.Equal(.3f, result.Style.Opacity);
        Assert.Equal(LyricStyleSource.Branch, result.SourceOf(LyricStyleProperty.FontSizePx));
        Assert.Equal(LyricStyleSource.Line, result.SourceOf(LyricStyleProperty.StrokeWidthPx));
        Assert.Equal(LyricStyleSource.Token, result.SourceOf(LyricStyleProperty.Opacity));
    }
}
