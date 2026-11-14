using System.Collections.Immutable;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class VectorOverlayContractTests
{
    private static Segment Line => new(new(10, 20), new(30, 40), 4, new(1, 0, 0, .5));
    private static Trail Ribbon => new([new(new(10, 20), 0, default), new(new(30, 40), 4, new(1, 0, 0, .5))]);
    private static ImmutableArray<string> Capabilities => [CapabilityNames.VisualsDots, CapabilityNames.VisualsSegments, CapabilityNames.VisualsTrails];
    private static ImmutableArray<VisualOutputDeclaration> Declarations =>
    [
        new(new("z"), VisualPrimitiveKind.Trails, OverlayLayer.AboveText),
        new(new("a"), VisualPrimitiveKind.Segments, OverlayLayer.BehindText),
        new(new("b"), VisualPrimitiveKind.Dots, OverlayLayer.AboveText),
        new(new("line"), VisualPrimitiveKind.Segments, OverlayLayer.AboveText),
        new(new("trail"), VisualPrimitiveKind.Trails, OverlayLayer.AboveText),
        new(new("dot"), VisualPrimitiveKind.Dots, OverlayLayer.AboveText),
        new(new("s"), VisualPrimitiveKind.Segments, OverlayLayer.AboveText),
        new(new("t"), VisualPrimitiveKind.Trails, OverlayLayer.AboveText),
        new(new("late"), VisualPrimitiveKind.Trails, OverlayLayer.AboveText),
    ];
    [Fact]
    public void 三类输出共用名字且ordinal排序不静默丢弃非圆点()
    {
        VisualOutput writer = new(new("fx"), Capabilities, Declarations, ResourceCatalog.Empty);
        writer.DrawTrails(new("z"), [Ribbon], OverlayLayer.AboveText);
        writer.DrawSegments(new("a"), [Line], OverlayLayer.BehindText);
        writer.DrawDots(new("b"), [], OverlayLayer.AboveText);
        Assert.Equal("DuplicateOverlayBatch", Assert.Throws<EffectOutputException>(() => writer.DrawTrails(new("z"), [], OverlayLayer.AboveText)).Code);
        var output = writer.Complete(); Assert.Equal((string[])["a", "b", "z"], output.Select(batch => batch.Id.Value));
        Assert.IsType<SegmentBatch>(output[0]); Assert.Equal(2, Assert.IsType<TrailBatch>(output[2]).ItemCount);
        Assert.Equal("InvalidVisualOutputStage", Assert.Throws<EffectOutputException>(() => writer.DrawTrails(new("late"), [], OverlayLayer.AboveText)).Code);
    }
    [Fact]
    public void 线段和带状点全部计入跨类型预算包括透明零面积()
    {
        VisualOutput exact = new(new("fx"), Capabilities, Declarations, ResourceCatalog.Empty, new(3));
        exact.DrawSegments(new("line"), [new(new(0, 0), new(0, 0), 1, default)], OverlayLayer.AboveText);
        exact.DrawTrails(new("trail"), [Ribbon], OverlayLayer.AboveText);
        Assert.Equal(3, exact.Complete().Sum(batch => batch.ItemCount));

        VisualOutput writer = new(new("fx"), Capabilities, Declarations, ResourceCatalog.Empty, new(3));
        writer.DrawSegments(new("line"), [new(new(0, 0), new(0, 0), 1, default)], OverlayLayer.AboveText);
        writer.DrawTrails(new("trail"), [Ribbon], OverlayLayer.AboveText);
        var error = Assert.Throws<EffectOutputException>(() => writer.DrawDots(new("dot"), [new(new(0, 0), 1, default)], OverlayLayer.AboveText));
        Assert.Equal("VisualOutputBudgetExceeded", error.Code);
        Assert.Equal("VisualOutputBudgetExceeded", Assert.Throws<EffectOutputException>(() => writer.Complete()).Code);
    }
    [Fact]
    public void 各图元要求能力有效数组和有限几何但允许零宽尖端()
    {
        Assert.False(default(Segment).IsValid);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Segment(new(0, 0), new(1, 1), 0, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrailPoint(new(0, 0), -1, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrailPoint(new(double.MaxValue, 0), double.MaxValue, default));
        Assert.True(new TrailPoint(new(0, 0), 0, default).IsValid);
        Assert.Throws<ArgumentException>(() => new Trail(default)); Assert.Throws<ArgumentException>(() => new Trail([new(new(0, 0), 0, default)]));
        VisualOutput none = new(new("fx"), [], Declarations, ResourceCatalog.Empty);
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => none.DrawTrails(new("t"), [], OverlayLayer.AboveText)).Code);
        VisualOutput writer = new(new("fx"), Capabilities, Declarations, ResourceCatalog.Empty);
        Assert.Equal("InvalidOverlay", Assert.Throws<EffectOutputException>(() => writer.DrawSegments(new("s"), [default], OverlayLayer.AboveText)).Code);
        Assert.Throws<EffectOutputException>(() => writer.DrawSegments(new("s"), default, OverlayLayer.AboveText));
        Assert.Throws<EffectOutputException>(() => writer.DrawTrails(new("t"), default, OverlayLayer.AboveText));
        Assert.Throws<EffectOutputException>(() => writer.DrawTrails(new("t"), [null!], OverlayLayer.AboveText));
        Assert.Throws<EffectOutputException>(() => writer.DrawTrails(default, [], OverlayLayer.AboveText));
    }
}
