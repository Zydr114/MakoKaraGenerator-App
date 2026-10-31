using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class MainCanvasImageEffectsTests
{
    [Fact]
    public void 精灵与正式图效果同时占用同一帧表面预算失败释放全部借用()
    {
        var request = Request(Snapshot()); var binding = Bind("graph", Line(request)); var graphRequest = Bindings(request, binding);
        using var graph = Tint("first");
        var handle = new Fx.ResourceCatalog("sprites", "sha256:" + new string('a', 64), [], [], ["spark.png"]).GetImage("spark.png");
        using var bitmap = new SKBitmap(4, 4); bitmap.Erase(SKColors.Red); using var image = SKImage.FromBitmap(bitmap);
        using var input = SkiaGraphSurface.Borrow(image, new(new(0, 0, 4, 4), GraphCoordinateSpace.Canvas, SceneMatrix.Identity, 1, 1), ShaderSurfaceKind.Color);
        using var resources = new SceneImageEffectResources([Program("first", graph)], null, [], null, [KeyValuePair.Create(handle, input)]);
        using var baselineBudget = new ImageSurfaceBudgetSession();
        using (var baseline = new SkiaRenderPipeline(null, null, baselineBudget, resources)) using (var frame = baseline.Render(graphRequest)) Assert.NotEmpty(frame.Pixels.ToArray());
        int peak = baselineBudget.Usage.PeakSurfaces; Assert.True(peak > 1); Assert.Equal(0, baselineBudget.Usage.ActiveSurfaces);
        using var budget = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = peak });
        using var renderer = new SkiaRenderPipeline(null, null, budget, resources);
        var prepared = graphRequest.PreparedFrame!.WithOverlayOutput([new Fx.SpriteBatch(new("sprites"), new("spark"), Fx.OverlayLayer.AboveText,
            [new(handle, new(20, 0, 0, 20, 20, 20), new(1, 1, 1, 1))])], []);
        var error = Assert.Throws<EffectFailureException>(() => renderer.Render(graphRequest with { PreparedFrame = prepared }));
        Assert.Equal("ImageSurfaceBudgetExceeded", error.Code); Assert.Equal(binding.InstanceId, error.InstanceId);
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }
    [Fact]
    public void 自身BehindText精灵仍拒绝读取自身下层不因新类型漏过门禁()
    {
        var request = Request(Snapshot()); var binding = Bind("same", Line(request));
        var handle = new Fx.ResourceCatalog("sprites", "sha256:" + new string('a', 64), [], [], ["spark.png"]).GetImage("spark.png");
        using var bitmap = new SKBitmap(4, 4); bitmap.Erase(SKColors.Red); using var image = SKImage.FromBitmap(bitmap);
        using var input = SkiaGraphSurface.Borrow(image, new(new(0, 0, 4, 4), GraphCoordinateSpace.Canvas, SceneMatrix.Identity, 1, 1), ShaderSurfaceKind.Color);
        var definition = new ImageGraphDefinition("below", ImageGraphDefinition.CurrentVersion,
            [new("below", GraphInputSource.BackdropBelow, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)], [], [],
            GraphReference.Input("below"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly, GraphCompositeMode.SourceOver);
        using var graph = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [], []));
        using var resources = new SceneImageEffectResources([Program("first", graph)], null, [], null, [KeyValuePair.Create(handle, input)]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        var prepared = request.PreparedFrame!.WithOverlayOutput([new Fx.SpriteBatch(binding.InstanceId, new("spark"), Fx.OverlayLayer.BehindText,
            [new(handle, new(20, 0, 0, 20, 20, 20), new(1, 1, 1, 1))])], []).WithImageEffects([binding]);
        var error = Assert.Throws<EffectFailureException>(() => renderer.Render(request with { PreparedFrame = prepared }));
        Assert.Equal("ImageEffectOwnedBackdropPending", error.Code); Assert.Equal(binding.InstanceId, error.InstanceId);
    }
}
