using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class VectorOverlayContractTests
{
    private static Segment Line => new(new(10, 20), new(30, 40), 4, new(1, 0, 0, .5));
    private static Trail Ribbon => new([new(new(10, 20), 0, default), new(new(30, 40), 4, new(1, 0, 0, .5))]);
    [Fact]
    public void 三类输出共用名字且ordinal排序不静默丢弃非圆点()
    {
        VisualWriter writer = new(new("fx"), EffectCapabilities.Overlay);
        writer.Overlay.DrawTrails(new("z"), [Ribbon], OverlayLayer.AboveText);
        writer.Overlay.DrawSegments(new("a"), [Line], OverlayLayer.BehindText);
        writer.Overlay.DrawDots(new("b"), [], OverlayLayer.AboveText);
        Assert.Equal("DuplicateOverlayBatch", Assert.Throws<EffectOutputException>(() => writer.Overlay.DrawDots(new("z"), [], OverlayLayer.BehindText)).Code);
        var output = writer.CompleteOverlay(); Assert.Equal((string[])["a", "b", "z"], output.Select(batch => batch.Id.Value));
        Assert.IsType<SegmentBatch>(output[0]); Assert.Equal(2, Assert.IsType<TrailBatch>(output[2]).ItemCount);
        Assert.Equal("InvalidVisualOutputStage", Assert.Throws<EffectOutputException>(() => writer.Overlay.DrawTrails(new("late"), [], OverlayLayer.AboveText)).Code);
        VisualWriter legacy = new(new("fx"), EffectCapabilities.Overlay); legacy.Overlay.DrawSegments(new("a"), [Line], OverlayLayer.AboveText);
        Assert.Equal("UnsupportedLegacyOverlayOutput", Assert.Throws<EffectOutputException>(() => legacy.Complete()).Code);
    }
    [Fact]
    public void 线段和带状点全部计入跨类型预算包括透明零面积()
    {
        VisualWriter writer = new(new("fx"), EffectCapabilities.Overlay, new(3));
        writer.Overlay.DrawSegments(new("line"), [new(new(0, 0), new(0, 0), 1, default)], OverlayLayer.AboveText);
        writer.Overlay.DrawTrails(new("trail"), [Ribbon], OverlayLayer.AboveText);
        var error = Assert.Throws<EffectOutputException>(() => writer.Overlay.DrawDots(new("dot"), [new(new(0, 0), 1, default)], OverlayLayer.AboveText));
        Assert.Equal("VisualOutputBudgetExceeded", error.Code); Assert.Contains("4", error.Message);
        Assert.Equal(3, writer.CompleteOverlay().Sum(batch => batch.ItemCount));
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
        VisualWriter none = new(new("fx"), EffectCapabilities.None);
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => none.Overlay.DrawTrails(new("t"), [], OverlayLayer.AboveText)).Code);
        VisualWriter writer = new(new("fx"), EffectCapabilities.Overlay);
        Assert.Equal("InvalidOverlay", Assert.Throws<EffectOutputException>(() => writer.Overlay.DrawSegments(new("s"), [default], OverlayLayer.AboveText)).Code);
        Assert.Throws<EffectOutputException>(() => writer.Overlay.DrawSegments(new("s"), default, OverlayLayer.AboveText));
        Assert.Throws<EffectOutputException>(() => writer.Overlay.DrawTrails(new("t"), default, OverlayLayer.AboveText));
        Assert.Throws<EffectOutputException>(() => writer.Overlay.DrawTrails(new("t"), [null!], OverlayLayer.AboveText));
        Assert.Throws<EffectOutputException>(() => writer.Overlay.DrawTrails(default, [], OverlayLayer.AboveText));
    }
    [Fact]
    public void Custom只选择绘制协议不绕过描述校验()
    {
        CursorEmitterDescriptor descriptor = new() { AnchorMode = CursorAnchorMode.Baseline, RatePerSecond = 1, LifetimeUs = 1,
            SizePx = 1, Color = default, SeedStreamKey = "custom", Rendering = ParticleRenderMode.Custom };
        descriptor.Validate(); Assert.Equal(ParticleRenderMode.Dots, (descriptor with { Rendering = ParticleRenderMode.Dots }).Rendering);
        Assert.Equal("InvalidEmitter", Assert.Throws<EffectOutputException>(() => (descriptor with { Rendering = (ParticleRenderMode)99 }).Validate()).Code);
    }
}
