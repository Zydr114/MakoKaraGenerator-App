using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class ImageGraphCompositionChainTests
{
    [Theory]
    [InlineData(.5)] [InlineData(1)] [InlineData(2)]
    public void 未覆盖材质的颜色和独立替换覆盖各应用一次区域(double density)
    {
        using var color = new Surface(new(255, 0, 0, 128), density: density);
        using var erase = new Surface(new(0, 0, 0, 204), ShaderSurfaceKind.Coverage, density);
        using var region = new Surface(new(0, 0, 0, 128), ShaderSurfaceKind.Coverage, density);
        using var below = new Surface(SKColors.Blue, density: density); using var graph = UnmaskedReplacementGraph();
        using var raw = graph.Execute(Frame(density), Inputs(color.Value, erase.Value));
        using var bounded = graph.ApplyMaterialCoverage(raw, region.Value, EffectMaterialCoverageMode.Unmasked);
        Assert.Equal(new byte[] { 0, 0, 64, 64 }, Pixel(bounded.Output, (int)(8 * density), (int)(8 * density)));
        Assert.InRange(Pixel(bounded.Coverage!, (int)(8 * density), (int)(8 * density))[3], 102, 103);
        using var composite = graph.Composite(bounded, below.Value);
        Assert.InRange(Pixel(composite, (int)(8 * density), (int)(8 * density))[0], 152, 153);
    }

    [Theory]
    [InlineData(.5)] [InlineData(1)] [InlineData(2)]
    public void 已覆盖材质复制原边缘而不使抗锯齿二次变淡(double density)
    {
        using var color = new Surface(new(255, 0, 0, 64), density: density);
        using var mask = new Surface(new(0, 0, 0, 128), ShaderSurfaceKind.Coverage, density);
        using var graph = Graph(true); using var raw = graph.Execute(Frame(density), Inputs(color.Value, mask.Value));
        using var bounded = graph.ApplyMaterialCoverage(raw, mask.Value, EffectMaterialCoverageMode.Masked);
        Assert.Equal(Bytes(raw.Output), Bytes(bounded.Output)); Assert.Equal(Bytes(raw.Coverage!), Bytes(bounded.Coverage!));
        using var wrong = graph.ApplyMaterialCoverage(raw, mask.Value, EffectMaterialCoverageMode.Unmasked);
        Assert.InRange(Pixel(wrong.Output, (int)(8 * density), (int)(8 * density))[3], 15, 17);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 已覆盖声明超出真实颜色或替换区域明确失败(bool coverage)
    {
        using var color = new Surface(coverage ? SKColors.Transparent : SKColors.Red);
        using var erase = new Surface(SKColors.White, ShaderSurfaceKind.Coverage);
        using var region = new Surface(new(0, 0, 0, 128), ShaderSurfaceKind.Coverage);
        using var graph = Graph(coverage); using var raw = graph.Execute(Frame(), Inputs(color.Value, coverage ? erase.Value : null));
        var error = Assert.Throws<ImageGraphException>(() => graph.ApplyMaterialCoverage(raw, region.Value, EffectMaterialCoverageMode.Masked));
        Assert.Equal("MaterialOutputOutsideRegion", error.Code);
        Assert.Equal(coverage ? "material.coverage" : "material.output", error.Field);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void 已覆盖模式不能在零区域泄漏一个像素字节(int alpha)
    {
        using var color = new Surface(new(255, 0, 0, (byte)alpha)); using var region = new Surface(SKColors.Transparent, ShaderSurfaceKind.Coverage);
        using var graph = Graph(); using var raw = graph.Execute(Frame(), Inputs(color.Value));
        if (alpha == 0) { using var result = graph.ApplyMaterialCoverage(raw, region.Value, EffectMaterialCoverageMode.Masked); Assert.Equal(new byte[4], Pixel(result.Output)); }
        else Assert.Equal("MaterialOutputOutsideRegion", Assert.Throws<ImageGraphException>(() => graph.ApplyMaterialCoverage(raw, region.Value, EffectMaterialCoverageMode.Masked)).Code);
    }

    [Fact]
    public void 区域产物独立拥有并在原图结果释放后保持可用()
    {
        using var color = new Surface(SKColors.Red); using var region = new Surface(new(0, 0, 0, 128), ShaderSurfaceKind.Coverage);
        using var graph = Graph(); var raw = graph.Execute(Frame(), Inputs(color.Value));
        using var result = graph.ApplyMaterialCoverage(raw, region.Value, EffectMaterialCoverageMode.Unmasked);
        raw.Dispose(); graph.Dispose(); Assert.Equal(new byte[] { 0, 0, 128, 128 }, Pixel(result.Output));
        result.Dispose(); result.Dispose(); Assert.True(color.Value.Image.Handle != nint.Zero);
    }

    [Fact]
    public void 区域代次密度和取消保持严格错误语义()
    {
        using var color = new Surface(SKColors.Red); using var region = new Surface(SKColors.White, ShaderSurfaceKind.Coverage);
        using var dense = new Surface(SKColors.White, ShaderSurfaceKind.Coverage, 2);
        using var graph = Graph(); using var other = Graph(); using var raw = graph.Execute(Frame(), Inputs(color.Value));
        Assert.Equal("GraphResultMismatch", Assert.Throws<ImageGraphException>(() => other.ApplyMaterialCoverage(raw, region.Value, EffectMaterialCoverageMode.Masked)).Code);
        Assert.Equal("MaterialRegionGeometryMismatch", Assert.Throws<ImageGraphException>(() => graph.ApplyMaterialCoverage(raw, dense.Value, EffectMaterialCoverageMode.Unmasked)).Code);
        Assert.Equal("InvalidMaterialCoverageMode", Assert.Throws<ImageGraphException>(() => graph.ApplyMaterialCoverage(raw, region.Value, (EffectMaterialCoverageMode)99)).Code);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => graph.ApplyMaterialCoverage(raw, region.Value, EffectMaterialCoverageMode.Unmasked, cancellationToken: cancel.Token));
    }

    [Fact]
    public void 区域限制计入真实共享峰值且预算失败回收()
    {
        using var color = new Surface(SKColors.Red); using var region = new Surface(SKColors.White, ShaderSurfaceKind.Coverage);
        using var graph = Graph(); using var raw = graph.Execute(Frame(), Inputs(color.Value));
        using var tight = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 2 }); using var frame = tight.BeginFrame();
        Assert.Equal("ImageSurfaceBudgetExceeded", Assert.Throws<ImageSurfaceBudgetException>(() => graph.ApplyMaterialCoverage(raw, region.Value, EffectMaterialCoverageMode.Unmasked, frame)).Code);
        Assert.Equal(0, tight.Usage.ActiveSurfaces);
        using var budget = new ImageSurfaceBudgetSession(); using var resources = budget.BeginFrame();
        using var result = graph.ApplyMaterialCoverage(raw, region.Value, EffectMaterialCoverageMode.Unmasked, resources);
        Assert.Equal(1, budget.Usage.ActiveSurfaces); Assert.True(budget.Usage.PeakSurfaces >= 3);
        result.Dispose(); Assert.Equal(0, budget.Usage.ActiveSurfaces);
    }
    private static CompiledImageGraph UnmaskedReplacementGraph() => CompiledImageGraph.Compile(ImageGraphCompiler.Compile(
        new("unmasked", ImageGraphDefinition.CurrentVersion,
            [new("target", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas),
                new("mask", GraphInputSource.GlyphCoverage, ShaderSurfaceKind.Coverage, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)],
            [], [], GraphReference.Input("target"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly,
            GraphCompositeMode.ReplaceCoveredBackdrop, GraphReference.Input("mask")), [], []));

    [Fact]
    public void 区域限制也服从原图局部预算且已覆盖验证拒绝不同像素网格()
    {
        using var color = new Surface(SKColors.Red); using var region = new Surface(SKColors.White, ShaderSurfaceKind.Coverage);
        using var shifted = new Surface(SKColors.White, ShaderSurfaceKind.Coverage, bounds: new(.25, 0, 16.25, 16));
        using var graph = Graph(budget: new() { MaximumSurfaces = 2 }); using var raw = graph.Execute(Frame(), Inputs(color.Value));
        Assert.Equal("GraphSurfaceBudgetExceeded", Assert.Throws<ImageGraphException>(() => graph.ApplyMaterialCoverage(raw, region.Value, EffectMaterialCoverageMode.Unmasked)).Code);
        Assert.Equal("MaterialRegionGeometryMismatch", Assert.Throws<ImageGraphException>(() => graph.ApplyMaterialCoverage(raw, shifted.Value, EffectMaterialCoverageMode.Masked)).Code);
    }
}
