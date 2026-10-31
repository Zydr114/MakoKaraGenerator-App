using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class VisualWriterContractTests
{
    private static Dot Dot => new(new(10, 20), 3, new(.2, .4, .6, .5));
    [Fact]
    public void 圆点必须正有限且默认区间和圆点不能冒充有效输出()
    {
        Assert.False(default(Dot).IsValid); Assert.False(default(VisualSimulationStep).IsValid);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Dot(new(0, 0), 0, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Dot(new(double.MaxValue, 0), double.MaxValue, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VisualSimulationStep(1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VisualSimulationStep(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VisualOutputBudget(0));
        Assert.Equal(100_000, VisualOutputBudget.Default.DrawItemLimit);
        Assert.Equal(long.MaxValue, new VisualSimulationStep(0, long.MaxValue).DurationUs);
    }
    [Fact]
    public void writer要求声明能力稳定身份有效数组与唯一批次()
    {
        VisualWriter none = new(new("fx"), EffectCapabilities.None);
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => none.Overlay.DrawDots(new("dot"), [Dot], OverlayLayer.AboveText)).Code);
        VisualWriter writer = new(new("fx"), EffectCapabilities.Overlay);
        foreach (var invalid in new[] { ImmutableArray<Dot>.Empty.Add(default), default })
            Assert.Equal("InvalidOverlay", Assert.Throws<EffectOutputException>(() => writer.Overlay.DrawDots(new("dot"), invalid, OverlayLayer.AboveText)).Code);
        Assert.Equal("InvalidOverlay", Assert.Throws<EffectOutputException>(() => writer.Overlay.DrawDots(default, [], OverlayLayer.AboveText)).Code);
        Assert.Equal("InvalidOverlay", Assert.Throws<EffectOutputException>(() => writer.Overlay.DrawDots(new("dot"), [], (OverlayLayer)99)).Code);
        writer.Overlay.DrawDots(new("dot"), [Dot], OverlayLayer.AboveText);
        Assert.Equal("DuplicateOverlayBatch", Assert.Throws<EffectOutputException>(() => writer.Overlay.DrawDots(new("dot"), [], OverlayLayer.BehindText)).Code);
        Assert.Equal(new EffectInstanceId("fx"), Assert.Single(writer.Complete()).InstanceId);
    }
    [Fact]
    public void 批次ordinal排序数组顺序保持且离开阶段不能继续写()
    {
        VisualWriter writer = new(new("fx"), EffectCapabilities.Overlay);
        Dot second = new(new(30, 40), 5, new(.8, .6, .4, 1));
        writer.Overlay.DrawDots(new("z"), [Dot, second], OverlayLayer.BehindText);
        writer.Overlay.DrawDots(new("a"), [], OverlayLayer.AboveText);
        var output = writer.Complete(); Assert.Equal((string[])["a", "z"], output.Select(batch => batch.Id.Value));
        Assert.Equal((Dot[])[Dot, second], output[1].Dots.ToArray());
        Assert.Equal("InvalidVisualOutputStage", Assert.Throws<EffectOutputException>(() => writer.Overlay.DrawDots(new("b"), [], OverlayLayer.AboveText)).Code);
        Assert.Throws<EffectOutputException>(() => writer.Complete());
        VisualWriter failed = new(new("fx"), EffectCapabilities.Overlay); failed.Invalidate(); failed.Invalidate();
        Assert.Throws<EffectOutputException>(() => failed.Complete());
    }
    [Fact]
    public void 预算覆盖全部批次不裁剪且视觉writer没有文字输出入口()
    {
        VisualWriter writer = new(new("fx"), EffectCapabilities.Overlay, new(2));
        writer.Overlay.DrawDots(new("a"), [Dot], OverlayLayer.AboveText);
        var error = Assert.Throws<EffectOutputException>(() => writer.Overlay.DrawDots(new("b"), [Dot, Dot], OverlayLayer.AboveText));
        Assert.Equal("VisualOutputBudgetExceeded", error.Code); Assert.Contains("3", error.Message); Assert.Contains("2", error.Message);
        Assert.Single(writer.Complete());
        Assert.Null(typeof(VisualWriter).GetProperty("Style")); Assert.Null(typeof(VisualWriter).GetProperty("Transforms"));
        Assert.Null(typeof(VisualContext).GetProperty("GeometryQueries"));
    }
}
