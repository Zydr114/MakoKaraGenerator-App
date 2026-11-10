using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class MainCanvasImageEffectsTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 同实例向量后置图元采样下层时排除自身(bool trail)
    {
        var request = OwnedRequest(); var binding = Bind("same", Line(request));
        Fx.OverlayBatch overlay = trail
            ? new Fx.TrailBatch(binding.InstanceId, new("path"), Fx.OverlayLayer.BehindText, [new([new(new(20, 20), 5, new(1, 0, 0, 1)), new(new(40, 20), 5, new(1, 0, 0, 1))])])
            : new Fx.SegmentBatch(binding.InstanceId, new("path"), Fx.OverlayLayer.BehindText, [new(new(20, 20), new(40, 20), 5, new(1, 0, 0, 1))]);
        var prepared = request.PreparedFrame!.WithOverlayOutput([overlay], []);
        var spot = OwnedLineSpot(request); using var graph = OwnedProbe("below", spot);
        using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), graph, Whole, Bounds)]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        using var actual = renderer.Render(request with { PreparedFrame = prepared.WithImageEffects([binding]) });
        OwnedStripeNear(actual, spot, 0, 0, 0, 0);
        Assert.True(OwnedPixel(actual, 30, 20)[2] > 240);
        Assert.Empty(actual.ImageEffectDiagnostics);
    }
}
