using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class ImageGraphCompositionChainTests
{
    [Fact]
    public void G0_Host_独立宿主无需作者Graph计划也能保持闭合payload像素()
    {
        using var device = new CpuSkiaRenderDevice();
        using var host = new SceneCompositionProgram(device, GraphExecutionBudget.Default);
        using var budget = new ImageSurfaceBudgetSession(); using var frame = budget.BeginFrame();
        int passes = 0; using var scheduling = host.BeginSceneFrame(frame, () => passes++);
        using var target = new Surface(SKColors.Transparent); using var below = new Surface(SKColors.Blue);
        using var mask = new Surface(new(0, 0, 0, 204), ShaderSurfaceKind.Coverage); using var graph = Graph(true);
        using var first = graph.Execute(Frame(), Inputs(target.Value, mask.Value));
        using var second = graph.Execute(Frame(), Inputs(target.Value, mask.Value));
        using var a = host.CaptureCompositionPayload([first], frame);
        using var b = host.CaptureCompositionPayload([second], frame);
        using var children = host.ComposePayloads([a, b], frame);
        using var actual = host.CompositePayload(children, below.Value, frame);
        using var referenceA = graph.CaptureCompositionPayload([first]); using var referenceB = graph.CaptureCompositionPayload([second]);
        using var referenceChildren = graph.ComposePayloads([referenceA, referenceB]);
        using var expected = graph.CompositePayload(referenceChildren, below.Value);
        Assert.Equal(Bytes(expected), Bytes(actual)); Assert.True(passes >= 7);
        Assert.InRange(Pixel(children.Erasure)[3], (byte)243, (byte)246);
    }

    [Fact]
    public void G0_Host_同父持续floor排除旧普通alpha而完整父边界保留该alpha()
    {
        using var device = new CpuSkiaRenderDevice(); using var host = new SceneCompositionProgram(device, GraphExecutionBudget.Default);
        using var budget = new ImageSurfaceBudgetSession(); using var frame = budget.BeginFrame();
        using var input = new Surface(SKColors.Red); using var childColor = new Surface(new(255, 0, 0, 51));
        using var firstGraph = Graph(amount: .8); using var lastGraph = Graph(amount: .5);
        using var first = firstGraph.Execute(Frame(), Inputs(input.Value));
        using var last = lastGraph.Execute(Frame(), Inputs(first.Output));
        using var child = host.CaptureCompositionPayload(childColor.Value, Frame(), frame);
        using var own = host.CaptureCompositionPayload([first, last], frame);
        using var correct = host.InheritCompositionErasure(own, child, frame);
        using var persistent = host.CapturePersistentFloor([first, last], child, frame, default);
        Assert.InRange(Pixel(correct.Erasure)[3], (byte)100, (byte)104);
        Assert.InRange(Pixel(persistent.Erasure)[3], (byte)50, (byte)52);
        using var firstOwn = host.CaptureCompositionPayload([first], frame);
        using var wrongPrevious = host.InheritCompositionErasure(firstOwn, child, frame);
        using var wrong = host.InheritCompositionErasure(own, wrongPrevious, frame);
        Assert.InRange(Pixel(wrong.Erasure)[3], (byte)202, (byte)206);
    }

    [Fact]
    public void G0_Host_产物保留固定设备寿命直到两个表面全部释放()
    {
        var device = new CpuSkiaRenderDevice(); using var lifetime = new SkiaRenderDeviceLifetime(device);
        using var host = new SceneCompositionProgram(device, GraphExecutionBudget.Default, lifetime);
        using var budget = new ImageSurfaceBudgetSession(); using var frame = budget.BeginFrame();
        using var source = new Surface(SKColors.Red);
        using var payload = host.CaptureCompositionPayload(source.Value, Frame(), frame);
        host.Dispose(); lifetime.Dispose();
        using (device.CreateSurface(new(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul))) { }
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(payload.Color));
        payload.Dispose();
        Assert.Throws<ObjectDisposedException>(() => device.CreateSurface(new(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul)));
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }

    [Fact]
    public void G0_Host_中途取消与预算失败不留下部分payload或借用账()
    {
        using var device = new CpuSkiaRenderDevice(); using var host = new SceneCompositionProgram(device, GraphExecutionBudget.Default);
        using var budget = new ImageSurfaceBudgetSession(); using var frame = budget.BeginFrame();
        using var source = new Surface(SKColors.Red); using var cancellation = new CancellationTokenSource();
        int passes = 0;
        using (host.BeginSceneFrame(frame, () => { if (++passes == 2) cancellation.Cancel(); }))
            Assert.Throws<OperationCanceledException>(() => host.CaptureCompositionPayload(source.Value, Frame(), frame, cancellationToken: cancellation.Token));
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
        using var tinyBudget = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 2 }); using var tinyFrame = tinyBudget.BeginFrame();
        Assert.Throws<ImageSurfaceBudgetException>(() => host.CaptureCompositionPayload(source.Value, Frame(), tinyFrame));
        Assert.Equal(0, tinyBudget.Usage.ActiveSurfaces); Assert.Equal(0, tinyBudget.Usage.EstimatedBytes);
    }
}
