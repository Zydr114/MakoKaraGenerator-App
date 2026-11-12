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
        var view = baseline.Unsung.Glow; Assert.NotNull(view); Assert.Equal(12, view.BlurRadiusPx); Assert.Equal(3, view.SpreadPx); Assert.Same(view, baseline.Sung.Glow);
        Assert.Equal(180 / 255d, view.Color.Alpha);
        var update = TestStyle.Update(Fx.MaterialValue.Solid(new(1, 0, 0, 0)), Fx.MaterialValue.Solid(new(0, 1, 0, .5)),
            strokes: [], shadow: new Fx.ShadowUpdate(null), opacityFactor: .5);
        Assert.Same(view, update.Apply(baseline).Unsung.Glow); Assert.Same(view, update.Apply(baseline).Sung.Glow);
        Assert.Same(view, update.ApplyMaterials(baseline).Unsung.Glow);
        Assert.Same(view, update.Then(new(opacityFactor: .5)).Apply(baseline).Unsung.Glow);
        var projected = StaticStyleProjection.Apply(source, update);
        Assert.Same(glow, projected.Glow); Assert.Equal(source.Opacity * .5f, projected.Opacity);
        Assert.Same(glow, StaticStyleProjection.ApplyDrawingStyle(source, update).Glow);
        Assert.Null(StaticStyleProjection.View(source with { Glow = null }).Unsung.Glow);
    }
}
