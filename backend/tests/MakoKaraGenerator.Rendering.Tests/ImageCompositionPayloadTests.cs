using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class ImageGraphCompositionChainTests
{
    private static void NearPixels(SkiaGraphSurface expected, SkiaGraphSurface actual, int tolerance = 1)
    {
        var a = Bytes(expected); var b = Bytes(actual); Assert.Equal(a.Length, b.Length);
        Assert.All(a.Zip(b), pair => Assert.InRange(Math.Abs(pair.First - pair.Second), 0, tolerance));
    }

    [Theory]
    [InlineData(.5, 0)] [InlineData(1, .3)] [InlineData(2, 1)]
    public void 可传递产物应用到不同真实背景等价于同目标原图链(double density, double opacity)
    {
        using var target = new Surface(SKColors.Red, density: density);
        using var mask = new Surface(new(0, 0, 0, 204), ShaderSurfaceKind.Coverage, density);
        using var firstGraph = Graph(true); using var lastGraph = Graph(amount: .25);
        using var first = firstGraph.Execute(Frame(density), Inputs(target.Value, mask.Value));
        using var last = lastGraph.Execute(Frame(density), Inputs(first.Output));
        using var payload = lastGraph.CaptureCompositionPayload([first, last], opacity: opacity);
        Assert.InRange(Pixel(payload.Erasure, (int)(8 * density), (int)(8 * density))[3], (int)(204 * opacity) - 1, (int)(204 * opacity) + 1);
        foreach (var background in new[] { SKColors.Blue, SKColors.Transparent, new SKColor(10, 180, 70, 170) })
        {
            using var below = new Surface(background, density: density);
            using var direct = lastGraph.CompositeChain([first, last], below.Value, opacity: opacity);
            using var transferred = lastGraph.CompositePayload(payload, below.Value);
            NearPixels(direct, transferred);
        }
    }

    [Fact]
    public void 两个独立透明替换孩子保留实际96百分比擦除而不是同目标max()
    {
        using var target = new Surface(SKColors.Transparent); using var below = new Surface(SKColors.Blue);
        using var mask = new Surface(new(0, 0, 0, 204), ShaderSurfaceKind.Coverage); using var graph = Graph(true);
        using var first = graph.Execute(Frame(), Inputs(target.Value, mask.Value)); using var second = graph.Execute(Frame(), Inputs(target.Value, mask.Value));
        using var afterFirst = graph.CompositeChain([first], below.Value); using var afterSecond = graph.CompositeChain([second], afterFirst);
        using var a = graph.CaptureCompositionPayload([first]); using var b = graph.CaptureCompositionPayload([second]);
        using var combined = graph.ComposePayloads([a, b]); using var transferred = graph.CompositePayload(combined, below.Value);
        NearPixels(afterSecond, transferred);
        Assert.InRange(Pixel(combined.Erasure)[3], 244, 246);
        Assert.Equal(new byte[4], Pixel(combined.Color));
        using var wrong = graph.CompositeChain([first, second], below.Value);
        Assert.InRange(Pixel(transferred)[0], 9, 11); Assert.Equal((byte)51, Pixel(wrong)[0]);
    }

    [Fact]
    public void 独立普通孩子按真实顺序合成颜色且反向顺序改变结果()
    {
        using var red = new Surface(new(255, 0, 0, 128)); using var green = new Surface(new(0, 255, 0, 128)); using var below = new Surface(SKColors.Blue);
        using var graph = Graph(); using var first = graph.Execute(Frame(), Inputs(red.Value)); using var second = graph.Execute(Frame(), Inputs(green.Value));
        using var afterFirst = graph.Composite(first, below.Value); using var afterSecond = graph.Composite(second, afterFirst);
        using var a = graph.CaptureCompositionPayload([first]); using var b = graph.CaptureCompositionPayload([second]);
        using var combined = graph.ComposePayloads([a, b]); using var transferred = graph.CompositePayload(combined, below.Value);
        NearPixels(afterSecond, transferred);
        using var reversed = graph.ComposePayloads([b, a]); using var reversedResult = graph.CompositePayload(reversed, below.Value);
        Assert.NotEqual(Bytes(transferred), Bytes(reversedResult));
    }

    [Theory]
    [InlineData(.5)] [InlineData(1)] [InlineData(2)]
    public void 嵌套层透明度同时作用于前景和擦除量一次(double density)
    {
        using var target = new Surface(SKColors.Red, density: density); using var below = new Surface(SKColors.Blue, density: density);
        using var mask = new Surface(new(0, 0, 0, 204), ShaderSurfaceKind.Coverage, density); using var graph = Graph(true);
        using var result = graph.Execute(Frame(density), Inputs(target.Value, mask.Value));
        using var child = graph.CaptureCompositionPayload([result], opacity: .5);
        using var parent = graph.ComposePayloads([child], opacity: .5); using var grandparent = graph.ComposePayloads([parent], opacity: .5);
        using var actual = graph.CompositePayload(grandparent, below.Value);
        using var reference = graph.CompositeChain([result], below.Value, opacity: .125);
        NearPixels(reference, actual, 2);
    }

    [Fact]
    public void 不同孩子框合成保留早先覆盖与后来颜色的独立区域()
    {
        using var target = new Surface(SKColors.Transparent, bounds: new(0, 0, 8, 16));
        using var mask = new Surface(SKColors.White, ShaderSurfaceKind.Coverage, bounds: new(0, 0, 8, 16));
        using var green = new Surface(new(0, 255, 0, 128), bounds: new(8, 0, 16, 16)); using var below = new Surface(SKColors.Blue);
        using var replace = Graph(true); using var ordinary = Graph();
        using var first = replace.Execute(Frame(bounds: new(0, 0, 8, 16)), Inputs(target.Value, mask.Value));
        using var second = ordinary.Execute(Frame(bounds: new(8, 0, 16, 16)), Inputs(green.Value));
        using var a = replace.CaptureCompositionPayload([first]); using var b = ordinary.CaptureCompositionPayload([second]);
        using var combined = ordinary.ComposePayloads([a, b]); using var actual = ordinary.CompositePayload(combined, below.Value);
        Assert.Equal(new byte[4], Pixel(actual, 4)); Assert.Equal(new byte[] { 127, 128, 0, 255 }, Pixel(actual, 12));
        Assert.Equal(new GraphRect(0, 0, 16, 16), combined.Color.Descriptor.Bounds);
        Assert.Equal("GraphCompositeBoundsExceeded", Assert.Throws<ImageGraphException>(() => ordinary.CompositePayload(combined, green.Value)).Code);
    }

    [Fact]
    public void 透明旁路不产生额外覆盖且可传递产物仍保留之前替换()
    {
        using var target = new Surface(SKColors.Red); using var below = new Surface(SKColors.Transparent);
        using var mask = new Surface(new(0, 0, 0, 128), ShaderSurfaceKind.Coverage); using var graph = Graph(true); using var glass = Graph(true, glass: true);
        using var first = graph.Execute(Frame(transparent: true), Inputs(target.Value, mask.Value));
        using var bypass = glass.Execute(Frame(transparent: true), Inputs(first.Output));
        using var payload = glass.CaptureCompositionPayload([first, bypass]); using var actual = glass.CompositePayload(payload, below.Value);
        NearPixels(first.Output, actual); Assert.Equal((byte)128, Pixel(payload.Erasure)[3]);
        Assert.Equal("TransparentBackgroundDependency", Assert.Single(bypass.Diagnostics).Reason);
    }

    [Fact]
    public void 产物不依赖原图结果生命周期且释放不夺取输入()
    {
        using var target = new Surface(SKColors.Red); using var mask = new Surface(new(0, 0, 0, 128), ShaderSurfaceKind.Coverage);
        using var graph = Graph(true); using var other = Graph(); using var below = new Surface(SKColors.Blue);
        var result = graph.Execute(Frame(), Inputs(target.Value, mask.Value));
        using var payload = graph.CaptureCompositionPayload([result]); result.Dispose(); graph.Dispose();
        using var actual = other.CompositePayload(payload, below.Value);
        Assert.Equal(new byte[] { 127, 0, 128, 255 }, Pixel(actual));
        payload.Dispose(); payload.Dispose(); Assert.Throws<ObjectDisposedException>(() => payload.Color);
        Assert.Throws<ObjectDisposedException>(() => payload.Erasure);
        Assert.True(target.Value.Image.Handle != nint.Zero); Assert.True(mask.Value.Image.Handle != nint.Zero);
    }

    [Theory]
    [InlineData("time")] [InlineData("density")] [InlineData("mode")]
    public void 独立孩子不能混用帧密度或输出模式(string difference)
    {
        using var target = new Surface(SKColors.Red); using var graph = Graph();
        using var first = graph.Execute(Frame(), Inputs(target.Value));
        using var second = graph.Execute(Frame(density: difference == "density" ? 2 : 1, time: difference == "time" ? 1 : 0, transparent: difference == "mode"), Inputs(target.Value));
        using var a = graph.CaptureCompositionPayload([first]); using var b = graph.CaptureCompositionPayload([second]);
        Assert.Equal("GraphCompositeChainContextMismatch", Assert.Throws<ImageGraphException>(() => graph.ComposePayloads([a, b])).Code);
    }

    [Fact]
    public void 共享账同时保留两张产物超预算失败不留下半份输出()
    {
        using var target = new Surface(SKColors.Red); using var mask = new Surface(new(0, 0, 0, 128), ShaderSurfaceKind.Coverage); using var graph = Graph(true);
        using var result = graph.Execute(Frame(), Inputs(target.Value, mask.Value));
        using var small = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 3, MaximumBytes = 3 * 16 * 16 * 4 }); using var frame = small.BeginFrame();
        Assert.Equal("ImageSurfaceBudgetExceeded", Assert.Throws<ImageSurfaceBudgetException>(() => graph.CaptureCompositionPayload([result], frame)).Code);
        Assert.Equal(0, small.Usage.ActiveSurfaces); Assert.Equal(0, small.Usage.EstimatedBytes);
        using var enough = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 4, MaximumBytes = 4 * 16 * 16 * 4 }); using var large = enough.BeginFrame();
        using var payload = graph.CaptureCompositionPayload([result], large);
        Assert.Equal(2, enough.Usage.ActiveSurfaces); Assert.Equal(4, enough.Usage.PeakSurfaces);
        payload.Dispose(); Assert.Equal(0, enough.Usage.ActiveSurfaces); Assert.Equal(0, enough.Usage.EstimatedBytes);
        Assert.True(result.Output.Image.Handle != nint.Zero);
    }

    [Fact]
    public void 多孩子合成与应用失败归还新产物账但保留借用输入()
    {
        using var target = new Surface(SKColors.Red); using var graph = Graph(); using var result = graph.Execute(Frame(), Inputs(target.Value));
        using var a = graph.CaptureCompositionPayload([result]); using var b = graph.CaptureCompositionPayload([result]);
        using var small = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 5, MaximumBytes = 5 * 16 * 16 * 4 }); using var frame = small.BeginFrame();
        Assert.Equal("ImageSurfaceBudgetExceeded", Assert.Throws<ImageSurfaceBudgetException>(() => graph.ComposePayloads([a, b], frame)).Code);
        Assert.Equal(0, small.Usage.ActiveSurfaces);
        using var tooSmall = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 3, MaximumBytes = 3 * 16 * 16 * 4 }); using var another = tooSmall.BeginFrame();
        Assert.Equal("ImageSurfaceBudgetExceeded", Assert.Throws<ImageSurfaceBudgetException>(() => graph.CompositePayload(a, target.Value, another)).Code);
        Assert.Equal(0, tooSmall.Usage.ActiveSurfaces); Assert.True(a.Color.Image.Handle != nint.Zero); Assert.True(b.Color.Image.Handle != nint.Zero);
    }

    [Fact]
    public void 空产物错误代次取消与非法透明度在申请前失败()
    {
        using var target = new Surface(SKColors.Red); using var graph = Graph(); using var other = Graph(); using var result = graph.Execute(Frame(), Inputs(target.Value));
        using var payload = graph.CaptureCompositionPayload([result]); using var budget = new ImageSurfaceBudgetSession(); using var frame = budget.BeginFrame();
        Assert.Equal("GraphCompositeChainEmpty", Assert.Throws<ImageGraphException>(() => graph.CaptureCompositionPayload([], frame)).Code);
        Assert.Equal("GraphResultMismatch", Assert.Throws<ImageGraphException>(() => other.CaptureCompositionPayload([result], frame)).Code);
        Assert.Equal("GraphCompositionPayloadEmpty", Assert.Throws<ImageGraphException>(() => graph.ComposePayloads([], frame)).Code);
        Assert.Equal("InvalidGraphOpacity", Assert.Throws<ImageGraphException>(() => graph.CaptureCompositionPayload([result], frame, double.NaN)).Code);
        Assert.Equal("InvalidGraphOpacity", Assert.Throws<ImageGraphException>(() => graph.ComposePayloads([payload], frame, -1)).Code);
        Assert.Equal("InvalidGraphOpacity", Assert.Throws<ImageGraphException>(() => graph.CompositePayload(payload, target.Value, frame, 2)).Code);
        Assert.Equal("GraphNativeValueRange", Assert.Throws<ImageGraphException>(() => graph.CaptureCompositionPayload([result], frame, 1e-100)).Code);
        Assert.Equal("GraphNativeValueRange", Assert.Throws<ImageGraphException>(() => graph.ComposePayloads([payload], frame, 1e-100)).Code);
        Assert.Equal("GraphNativeValueRange", Assert.Throws<ImageGraphException>(() => graph.CompositePayload(payload, target.Value, frame, 1e-100)).Code);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => graph.CaptureCompositionPayload([result], frame, cancellationToken: cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => graph.ComposePayloads([payload], frame, cancellationToken: cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => graph.CompositePayload(payload, target.Value, frame, cancellationToken: cancelled.Token));
        Assert.Equal(0, budget.Usage.ActiveSurfaces);
    }

    [Fact]
    public void 相同孩子产物多次出现按顺序绘制但底层借用存储去重()
    {
        using var target = new Surface(new(255, 0, 0, 128)); using var graph = Graph(); using var result = graph.Execute(Frame(), Inputs(target.Value));
        using var payload = graph.CaptureCompositionPayload([result]);
        using var budget = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 6, MaximumBytes = 6 * 16 * 16 * 4 });
        using var frame = budget.BeginFrame(); using var combined = graph.ComposePayloads([payload, payload, payload], frame);
        Assert.InRange(Pixel(combined.Color)[3], 222, 224); Assert.Equal(Pixel(combined.Color)[3], Pixel(combined.Erasure)[3]);
        Assert.Equal(2, budget.Usage.ActiveSurfaces); Assert.Equal(6, budget.Usage.PeakSurfaces);
        frame.Dispose(); Assert.Equal(2, budget.Usage.ActiveSurfaces);
        using var another = budget.BeginFrame(); using var below = new Surface(SKColors.Blue);
        using var actual = graph.CompositePayload(combined, below.Value, another);
        Assert.Equal(3, budget.Usage.ActiveSurfaces); // 存活的前景／擦除与新产物；借用下层已归还。
        actual.Dispose(); combined.Dispose(); Assert.Equal(0, budget.Usage.ActiveSurfaces);
    }

    [Fact]
    public void 第三个孩子峰值失败回收已经合成的前两个孩子产物()
    {
        using var target = new Surface(new(255, 0, 0, 128)); using var graph = Graph(); using var result = graph.Execute(Frame(), Inputs(target.Value));
        using var a = graph.CaptureCompositionPayload([result]); using var b = graph.CaptureCompositionPayload([result]); using var c = graph.CaptureCompositionPayload([result]);
        using var budget = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 9, MaximumBytes = 9 * 16 * 16 * 4 }); using var frame = budget.BeginFrame();
        Assert.Equal("ImageSurfaceBudgetExceeded", Assert.Throws<ImageSurfaceBudgetException>(() => graph.ComposePayloads([a, b, c], frame)).Code);
        Assert.Equal(8, budget.Usage.PeakSurfaces); Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
        Assert.True(a.Color.Image.Handle != nint.Zero); Assert.True(b.Color.Image.Handle != nint.Zero); Assert.True(c.Color.Image.Handle != nint.Zero);
    }

    [Fact]
    public void 单图局部预算同样限制复制产物不靠宽松的会话账绕过()
    {
        using var target = new Surface(SKColors.Red); using var mask = new Surface(new(0, 0, 0, 128), ShaderSurfaceKind.Coverage);
        using var graph = Graph(true, budget: new() { MaximumSurfaces = 3, MaximumBytes = 3 * 16 * 16 * 4 });
        using var result = graph.Execute(Frame(), Inputs(target.Value, mask.Value));
        using var budget = new ImageSurfaceBudgetSession(); using var frame = budget.BeginFrame();
        Assert.Equal("GraphSurfaceBudgetExceeded", Assert.Throws<ImageGraphException>(() => graph.CaptureCompositionPayload([result], frame)).Code);
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }
}
