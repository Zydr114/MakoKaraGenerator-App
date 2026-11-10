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
    public void 自身BehindText精灵异处采样排除自身且真实位置仍显示()
    {
        var request = OwnedRequest(); var binding = Bind("same", Line(request));
        var handle = new Fx.ResourceCatalog("sprites", "sha256:" + new string('a', 64), [], [], ["spark.png"]).GetImage("spark.png");
        using var bitmap = new SKBitmap(4, 4); bitmap.Erase(SKColors.Red); using var image = SKImage.FromBitmap(bitmap);
        using var input = SkiaGraphSurface.Borrow(image, new(new(0, 0, 4, 4), GraphCoordinateSpace.Canvas, SceneMatrix.Identity, 1, 1), ShaderSurfaceKind.Color);
        var spot = OwnedLineSpot(request); using var graph = OwnedProbe("below", spot);
        using var resources = new SceneImageEffectResources([Program("first", graph)], null, [], null, [KeyValuePair.Create(handle, input)]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        var prepared = request.PreparedFrame!.WithOverlayOutput([new Fx.SpriteBatch(binding.InstanceId, new("spark"), Fx.OverlayLayer.BehindText,
            [new(handle, new(20, 0, 0, 20, 20, 20), new(1, 1, 1, 1))])], []).WithImageEffects([binding]);
        using var actual = renderer.Render(request with { PreparedFrame = prepared });
        OwnedStripeNear(actual, spot, 0, 0, 0, 0);
        Assert.True(OwnedPixel(actual, 30, 20)[2] > 240);
        Assert.Empty(actual.ImageEffectDiagnostics);
    }
}
