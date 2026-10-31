using System.Runtime.InteropServices;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

[CollectionDefinition("Image pixel readback pool", DisableParallelization = true)]
public sealed class ImagePixelReadbackPoolGroup { }

[Collection("Image pixel readback pool")]
public sealed class ImagePixelReadbackPoolTests
{
    private static readonly GraphSurfaceDescriptor Descriptor = new(new(0, 0, 4, 4), GraphCoordinateSpace.Canvas, SceneMatrix.Identity, 1, 1);
    private static readonly ImageGraphFrameContext Frame = new(0, 4, 4, Descriptor.Bounds, 1, 1, ParameterValues.Empty, false);

    [Fact]
    public void RetentionCapsBufferCountAndTotalBytesIndependently()
    {
        using var prior = new EmptyPool();
        var small = Enumerable.Range(0, 4).Select(_ => ImagePixelReadbackPool.Rent(64)).ToArray();
        foreach (var lease in small) lease.Dispose();
        Assert.Equal(new(3, 192), ImagePixelReadbackPool.Usage);
        using var smallBuffers = new EmptyPool();
        var large = Enumerable.Range(0, 3).Select(_ => ImagePixelReadbackPool.Rent(ImagePixelReadbackPool.MaximumBufferBytes)).ToArray();
        foreach (var lease in large) lease.Dispose();
        Assert.Equal(new(2, ImagePixelReadbackPool.MaximumPooledBytes), ImagePixelReadbackPool.Usage);
    }

    [Fact]
    public void AboveRetentionLimitStillAllocatesExactRequestedLengthAndNeverRetains()
    {
        using var prior = new EmptyPool();
        int length = ImagePixelReadbackPool.MaximumBufferBytes + 1;
        using var lease = ImagePixelReadbackPool.Rent(length);
        Assert.Equal(length, lease.Buffer.Length);
        Assert.Equal(length, lease.Length);
        lease.Buffer[0] = 19; lease.Buffer[length - 1] = 37;
        Assert.Equal(19, lease.Buffer[0]); Assert.Equal(37, lease.Buffer[length - 1]);
        lease.Dispose();
        Assert.Equal(new(0, 0), ImagePixelReadbackPool.Usage);
    }

    [Fact]
    public void LeaseReusesStorageWithExplicitLengthAndReturnsItOnlyOnce()
    {
        using var prior = new EmptyPool();
        using var first = ImagePixelReadbackPool.Rent(128);
        byte[] storage = first.Buffer;
        first.Dispose(); first.Dispose();
        Assert.Equal(new(1, 128), ImagePixelReadbackPool.Usage);
        Assert.Throws<ObjectDisposedException>(() => first.Buffer);
        using var shortLease = ImagePixelReadbackPool.Rent(4);
        Assert.Same(storage, shortLease.Buffer);
        Assert.Equal(4, shortLease.Length);
        Assert.Equal(128, shortLease.Buffer.Length);
        Assert.Equal(new(0, 0), ImagePixelReadbackPool.Usage);
        using var other = ImagePixelReadbackPool.Rent(4);
        Assert.NotSame(storage, other.Buffer);
    }

    [Fact]
    public void ParallelReturnsRemainWithinBothRetentionCaps()
    {
        using var prior = new EmptyPool();
        var leases = Enumerable.Range(0, 8).Select(_ => ImagePixelReadbackPool.Rent(8 * 1024 * 1024)).ToArray();
        Parallel.ForEach(leases, lease => { lease.Dispose(); lease.Dispose(); });
        Assert.Equal(new(3, 24L * 1024 * 1024), ImagePixelReadbackPool.Usage);
    }

    [Fact]
    public void ReusedLargerStorageDoesNotScanStaleTailPixels()
    {
        using var prior = new EmptyPool();
        using var prime = ImagePixelReadbackPool.Rent(256);
        Array.Fill(prime.Buffer, (byte)255);
        for (int index = 3; index < prime.Buffer.Length; index += 4) prime.Buffer[index] = 0;
        byte[] storage = prime.Buffer;
        prime.Dispose();
        using var device = new PoolDevice();
        using var source = Surface(device);
        using var graph = Graph(device);
        using var result = graph.Execute(Frame, Inputs(source));
        Assert.Equal(1, device.Reads);
        using var returned = ImagePixelReadbackPool.Rent(64);
        Assert.Same(storage, returned.Buffer);
        Assert.Equal(64, returned.Length);
        Assert.Equal(255, returned.Buffer[66]); Assert.Equal(0, returned.Buffer[67]);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("throw")]
    [InlineData("invalid")]
    [InlineData("cancel")]
    public void ReadFailureInvalidPixelsAndCancellationReturnTheLeaseBeforeRetry(string failure)
    {
        using var prior = new EmptyPool();
        using var prime = ImagePixelReadbackPool.Rent(64);
        byte[] storage = prime.Buffer; prime.Dispose();
        using var device = new PoolDevice();
        using var source = Surface(device);
        using var graph = Graph(device);
        using var cancel = new CancellationTokenSource();
        device.BeforeRead = (_, _) =>
        {
            if (failure == "throw") throw new InvalidOperationException("readback test failure");
            return failure != "unavailable";
        };
        device.AfterRead = destination =>
        {
            if (failure == "invalid") { Marshal.WriteByte(destination, 2, 255); Marshal.WriteByte(destination, 3, 0); }
            if (failure == "cancel") cancel.Cancel();
        };
        if (failure == "cancel") Assert.Throws<OperationCanceledException>(() => graph.Execute(Frame, Inputs(source), cancel.Token));
        else if (failure == "throw") Assert.Throws<InvalidOperationException>(() => graph.Execute(Frame, Inputs(source)));
        else Assert.Equal(failure == "invalid" ? "GraphColorNotPremultiplied" : "GraphPixelsUnavailable",
            Assert.Throws<ImageGraphException>(() => graph.Execute(Frame, Inputs(source))).Code);
        Assert.Equal(new(1, 64), ImagePixelReadbackPool.Usage);
        using (var returned = ImagePixelReadbackPool.Rent(64)) Assert.Same(storage, returned.Buffer);
        device.BeforeRead = null; device.AfterRead = null;
        using var retry = graph.Execute(Frame, Inputs(source));
        Assert.Equal(2, device.Reads);
        Assert.Equal(new(1, 64), ImagePixelReadbackPool.Usage);
    }

    [Fact]
    public void MaskedSecondReadFailureReturnsBothLeasesAndKeepsReadOrder()
    {
        using var device = new PoolDevice();
        using var graph = Graph(device, shader: true);
        using var result = graph.Execute(Frame, new Dictionary<string, SkiaGraphInput>());
        using var region = Surface(device, ShaderSurfaceKind.Coverage);
        using var prior = new EmptyPool();
        int firstRead = device.Reads + 1;
        var handles = new List<nint>();
        device.BeforeRead = (image, read) => { handles.Add(image.Handle); return read != firstRead + 1; };
        var error = Assert.Throws<ImageGraphException>(() => graph.ApplyMaterialCoverage(result, region, EffectMaterialCoverageMode.Masked));
        Assert.Equal("GraphPixelsUnavailable", error.Code);
        Assert.Equal("material.region", error.Field);
        Assert.Equal(new[] { result.Output.Image.Handle, region.Image.Handle }, handles);
        Assert.Equal(new(2, 128), ImagePixelReadbackPool.Usage);
        device.BeforeRead = null;
        using var retry = graph.ApplyMaterialCoverage(result, region, EffectMaterialCoverageMode.Masked);
        Assert.Equal(SkiaTestReadback.Pixels(result.Output.Image), SkiaTestReadback.Pixels(retry.Output.Image));
    }

    [Fact]
    public void GraphReadAbovePoolLimitSucceedsWithTheOriginalPixelsAndBudgets()
    {
        using var prior = new EmptyPool();
        using var device = new PoolDevice();
        var descriptor = Descriptor with { Bounds = new(0, 0, 2049, 2048) };
        using var source = Surface(device, descriptor: descriptor);
        using var graph = Graph(device);
        var context = Frame with { CanvasWidth = 2049, CanvasHeight = 2048, TargetBounds = descriptor.Bounds };
        using var result = graph.Execute(context, Inputs(source));
        Assert.Same(source, result.Output);
        Assert.Equal(1, device.Reads);
        Assert.Equal(2049L * 2048 * 4, result.PeakBytes);
        Assert.Equal(new(0, 0), ImagePixelReadbackPool.Usage);
    }

    private static Dictionary<string, SkiaGraphInput> Inputs(SkiaGraphSurface surface)
        => new() { ["target"] = new(surface, GraphInputSource.TargetColor) };

    private static SkiaGraphSurface Surface(PoolDevice device, ShaderSurfaceKind kind = ShaderSurfaceKind.Color,
        GraphSurfaceDescriptor? descriptor = null)
    {
        descriptor ??= Descriptor;
        using var srgb = SKColorSpace.CreateSrgb();
        using var native = device.CreateSurface(new((int)descriptor.Bounds.Width, (int)descriptor.Bounds.Height,
            SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
        native.Canvas.Clear(SKColors.Red);
        // 外部借用的真实图像由 lease 之外的拥有包装维持；本例Own首次无验证凭据。
        return SkiaGraphSurface.Own(native.Snapshot(), descriptor, kind, device);
    }

    private static CompiledImageGraph Graph(PoolDevice device, bool shader = false)
    {
        List<GraphInputDefinition> inputs = shader ? [] : [new("target", GraphInputSource.TargetColor, ShaderSurfaceKind.Color,
            true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)];
        List<ImageGraphNode> nodes = shader ? [new ImageGraphNode.RuntimeShader("output", "program", ShaderSurfaceKind.Color, [], [],
            new(new GraphExpansion.Literal(0), 0, 0, GraphEdgeMode.Transparent))] : [];
        List<KeyValuePair<string, ShaderProgramDefinition>> programs = shader
            ? [new("program", new("test.sksl", "half4 main(float2 p) { return half4(1,0,0,1); }", ShaderProgramDefinition.RasterProfile, [], []))] : [];
        var definition = new ImageGraphDefinition("readback-pool", ImageGraphDefinition.CurrentVersion, inputs, [], nodes,
            shader ? GraphReference.Node("output") : GraphReference.Input("target"), GraphCoordinateSpace.Canvas, 0,
            GraphAnchorMode.ImageOnly, GraphCompositeMode.SourceOver);
        return CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [], programs), device);
    }

    private sealed class EmptyPool : IDisposable
    {
        private readonly List<ImagePixelReadbackLease> _prior = [];
        internal EmptyPool()
        {
            while (ImagePixelReadbackPool.Usage.Buffers > 0) _prior.Add(ImagePixelReadbackPool.Rent(1));
            Assert.Equal(new(0, 0), ImagePixelReadbackPool.Usage);
        }
        public void Dispose() { foreach (var lease in _prior) lease.Dispose(); }
    }

    private sealed class PoolDevice : ISkiaRenderDevice
    {
        private readonly CpuSkiaRenderDevice _inner = new();
        public int Reads { get; private set; }
        public Func<SKImage, int, bool>? BeforeRead { get; set; }
        public Action<IntPtr>? AfterRead { get; set; }
        public SkiaRenderDeviceInfo Info => _inner.Info;
        public object SynchronizationRoot => _inner.SynchronizationRoot;
        public SKSurface CreateSurface(SKImageInfo imageInfo) => _inner.CreateSurface(imageInfo);
        public void FlushAndSubmit(SKSurface surface) => _inner.FlushAndSubmit(surface);
        public SKImage? RasterizeImage(SKImage image) => _inner.RasterizeImage(image);
        public bool ReadPixels(SKSurface surface, SKImageInfo imageInfo, IntPtr destination, int rowBytes)
            => _inner.ReadPixels(surface, imageInfo, destination, rowBytes);
        public bool ReadPixels(SKImage image, SKImageInfo imageInfo, IntPtr destination, int rowBytes)
        {
            Reads++;
            if (BeforeRead?.Invoke(image, Reads) == false) return false;
            bool result = _inner.ReadPixels(image, imageInfo, destination, rowBytes);
            AfterRead?.Invoke(destination);
            return result;
        }
        public void Dispose() => _inner.Dispose();
    }
}
