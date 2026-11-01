using MakoKaraGenerator.Core.Projects;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class GlowProjectionTests
{
    [Fact]
    public void 只读辉光基线完整且全部现有外观写入保留它()
    {
        var glow = GlowSpec.Create(new(30, 80, 120, 180), .7f, 12, 3);
        var source = LyricStyle.Default with { Glow = glow };
        var baseline = StaticStyleProjection.View(source);
        Assert.NotNull(baseline.Glow); Assert.Equal(12, baseline.Glow.BlurRadiusPx); Assert.Equal(3, baseline.Glow.SpreadPx);
        Assert.Equal(180 / 255d, baseline.Glow.Color.Alpha);
        var update = new Fx.StyleUpdate(unsung: Fx.MaterialValue.Solid(new(1, 0, 0, 0)), sung: Fx.MaterialValue.Solid(new(0, 1, 0, .5)),
            stroke: new Fx.StrokeUpdate(null), shadow: new Fx.ShadowUpdate(null), opacityFactor: .5);
        Assert.Same(baseline.Glow, update.Apply(baseline).Glow);
        Assert.Same(baseline.Glow, update.ApplyMaterials(baseline).Glow);
        Assert.Same(baseline.Glow, update.Then(new(opacityFactor: .5)).Apply(baseline).Glow);
        var projected = StaticStyleProjection.Apply(source, update);
        Assert.Same(glow, projected.Glow); Assert.Equal(source.Opacity * .5f, projected.Opacity);
        Assert.Same(glow, StaticStyleProjection.ApplyDrawingStyle(source, update).Glow);
        Assert.Null(StaticStyleProjection.View(source with { Glow = null }).Glow);
    }
}
