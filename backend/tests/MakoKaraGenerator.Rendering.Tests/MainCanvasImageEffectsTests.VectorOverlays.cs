using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class MainCanvasImageEffectsTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 同实例向量后置图元同样拒绝读取自身下层(bool trail)
    {
        var request = Request(Snapshot()); var binding = Bind("same", Line(request));
        Fx.OverlayBatch overlay = trail
            ? new Fx.TrailBatch(binding.InstanceId, new("path"), Fx.OverlayLayer.BehindText, [new([new(new(20, 20), 5, new(1, 0, 0, 1)), new(new(40, 20), 5, new(1, 0, 0, 1))])])
            : new Fx.SegmentBatch(binding.InstanceId, new("path"), Fx.OverlayLayer.BehindText, [new(new(20, 20), new(40, 20), 5, new(1, 0, 0, 1))]);
        var prepared = request.PreparedFrame!.WithOverlayOutput([overlay], []);
        var definition = new ImageGraphDefinition("below", ImageGraphDefinition.CurrentVersion,
            [new("below", GraphInputSource.BackdropBelow, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)], [], [],
            GraphReference.Input("below"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly, GraphCompositeMode.SourceOver);
        using var graph = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [], []));
        using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), graph, Whole, Bounds)]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        var error = Assert.Throws<EffectFailureException>(() => renderer.Render(request with { PreparedFrame = prepared.WithImageEffects([binding]) }));
        Assert.Equal("ImageEffectOwnedBackdropPending", error.Code); Assert.Equal(binding.InstanceId, error.InstanceId);
    }
}
