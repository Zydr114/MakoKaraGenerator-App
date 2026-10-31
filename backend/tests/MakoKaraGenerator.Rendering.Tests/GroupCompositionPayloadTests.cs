using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class ImageGraphCompositionChainTests
{
    [Fact]
    public void 静态颜色可复制为独立颜色与擦除产物并只应用一次透明度()
    {
        using var source = new Surface(SKColors.Red);
        using var graph = Graph();
        using var payload = graph.CaptureCompositionPayload(source.Value, Frame(), opacity: .5);

        Assert.Equal(new byte[] { 0, 0, 128, 128 }, Pixel(payload.Color));
        Assert.Equal(new byte[] { 0, 0, 0, 128 }, Pixel(payload.Erasure));
        Assert.True(payload.Color.Image.Handle != source.Value.Image.Handle);
        Assert.True(payload.Erasure.Image.Handle != source.Value.Image.Handle);
    }

    [Fact]
    public void 共同透明度分解后逐层合成与完整孩子产物一致()
    {
        using var target = new Surface(SKColors.Transparent);
        using var mask = new Surface(new(0, 0, 0, 204), ShaderSurfaceKind.Coverage);
        using var below = new Surface(SKColors.Blue);
        using var graph = Graph(true);
        using var first = graph.Execute(Frame(), Inputs(target.Value, mask.Value));
        using var second = graph.Execute(Frame(), Inputs(target.Value, mask.Value));
        using var firstPayload = graph.CaptureCompositionPayload([first]);
        using var secondPayload = graph.CaptureCompositionPayload([second]);
        var opacityFrame = graph.ResolveCompositionOpacityLayers([firstPayload, secondPayload], opacity: .5);
        try
        {
            using var resolved = graph.ComposePayloads(opacityFrame);
            using var actual = graph.CompositePayload(resolved, below.Value);
            using var expected = graph.ComposePayloads([firstPayload, secondPayload], opacity: .5);
            using var reference = graph.CompositePayload(expected, below.Value);

            NearPixels(reference, actual, 2);
        }
        finally
        {
            foreach (var layer in opacityFrame) layer.Dispose();
        }
    }

    [Fact]
    public void 父级继承擦除取孩子与自身的逐像素最大值并保留颜色()
    {
        using var red = new Surface(SKColors.Red);
        using var blue = new Surface(new(0, 0, 255, 128));
        using var graph = Graph();
        using var child = graph.CaptureCompositionPayload(red.Value, Frame(), opacity: .5);
        using var own = graph.CaptureCompositionPayload(blue.Value, Frame(), opacity: .25);
        using var inherited = graph.InheritCompositionErasure(own, child);

        Assert.Equal(Pixel(own.Color), Pixel(inherited.Color));
        Assert.True(Pixel(inherited.Erasure)[3] >= Pixel(own.Erasure)[3]);
        Assert.True(Pixel(inherited.Erasure)[3] >= Pixel(child.Erasure)[3]);
    }
}
