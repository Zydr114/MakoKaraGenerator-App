using System.Runtime.InteropServices;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class ImageGraphValidationDedupTests
{
    private static readonly GraphSurfaceDescriptor Descriptor = new(new(0, 0, 4, 4), GraphCoordinateSpace.Canvas, SceneMatrix.Identity, 1, 1);
    private static readonly ImageGraphFrameContext Frame = new(0, 4, 4, Descriptor.Bounds, 1, 1, ParameterValues.Empty, false);

    [Fact]
    public void OwnedSnapshotSharesOneSuccessfulReadAcrossAliasesAndGraphs()
    {
        using var device = new CountingDevice();
        using var source = Owned(device);
        using var alias = source.BorrowAs(ShaderSurfaceKind.Color);
        using var coverage = source.BorrowAs(ShaderSurfaceKind.Coverage);
        using var colorAgain = coverage.BorrowAs(ShaderSurfaceKind.Color);
        using var first = Graph(device);
        using var second = Graph(device);
        Execute(first, source);
        Execute(first, alias);
        Execute(second, colorAgain);
        alias.Dispose();
        Execute(first, source);
        Assert.Equal(1, device.ImageReads);
        Assert.Equal(64, device.ImageReadBytes);
        Assert.Equal(SKColors.Red, Pixel(source));
    }

    [Fact]
    public void RasterRecordsItsFirstSuccessfulScanForLaterConsumers()
    {
        using var device = new CountingDevice();
        using var producer = Graph(device, shader: "half4 main(float2 p) { return half4(.25,0,0,.25); }");
        using var result = producer.Execute(Frame, new Dictionary<string, SkiaGraphInput>());
        Assert.Equal(1, device.ImageReads);
        using var consumer = Graph(device);
        Execute(consumer, result.Output);
        using var alias = result.Output.BorrowAs(ShaderSurfaceKind.Color);
        Execute(consumer, alias);
        Assert.Equal(1, device.ImageReads);
        byte[] pixels = SkiaTestReadback.Pixels(result.Output.Image);
        Assert.All(pixels.Where((_, index) => index % 4 == 2 || index % 4 == 3), value => Assert.InRange(value, (byte)63, (byte)65));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExternalBorrowAndItsAliasesRecheckChangingPixelMemory(bool declaredDevice)
    {
        using var device = new CountingDevice();
        using var graph = Graph(device);
        using var srgb = SKColorSpace.CreateSrgb();
        var info = new SKImageInfo(4, 4, SKColorType.Bgra8888, SKAlphaType.Premul, srgb);
        byte[] pixels = new byte[info.BytesSize];
        for (int index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, pin.AddrOfPinnedObject(), info.RowBytes);
            using var image = SKImage.FromPixels(pixmap);
            using var source = declaredDevice ? SkiaGraphSurface.Borrow(image, Descriptor, ShaderSurfaceKind.Color, device)
                : SkiaGraphSurface.Borrow(image, Descriptor, ShaderSurfaceKind.Color);
            using var alias = source.BorrowAs(ShaderSurfaceKind.Color);
            Execute(graph, source); Execute(graph, alias);
            Assert.Equal(2, device.ImageReads);
            pixels[2] = 255; pixels[3] = 0;
            Assert.Equal("GraphColorNotPremultiplied", Failure(() => Execute(graph, alias)).Code);
            Assert.Equal(3, device.ImageReads);
            pixels[3] = 255;
            Execute(graph, source);
            Assert.Equal(4, device.ImageReads);
            source.Dispose(); alias.Dispose();
            Assert.NotEqual(nint.Zero, image.Handle);
        }
        finally { pin.Free(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedPixelReadOrPremultiplicationNeverProducesSuccessfulReceipt(bool unavailable)
    {
        using var device = new CountingDevice { FailNextImageRead = unavailable, CorruptNextImageRead = !unavailable };
        using var source = Owned(device);
        using var graph = Graph(device);
        Assert.Equal(unavailable ? "GraphPixelsUnavailable" : "GraphColorNotPremultiplied", Failure(() => Execute(graph, source)).Code);
        Assert.Equal(1, device.ImageReads);
        Execute(graph, source); Execute(graph, source);
        Assert.Equal(2, device.ImageReads);
    }

    [Fact]
    public void CancellationDuringReadRequiresFreshReadOnRetryAndCachedInputStillCancels()
    {
        using var device = new CountingDevice();
        using var source = Owned(device);
        using var graph = Graph(device);
        using var cancellation = new CancellationTokenSource();
        device.AfterImageRead = () => { device.AfterImageRead = null; cancellation.Cancel(); };
        Assert.Throws<OperationCanceledException>(() => Execute(graph, source, cancellation.Token));
        Assert.Equal(1, device.ImageReads);
        Execute(graph, source); Execute(graph, source);
        Assert.Equal(2, device.ImageReads);
        Assert.Throws<OperationCanceledException>(() => Execute(graph, source, cancellation.Token));
        Assert.Equal(2, device.ImageReads);
    }

    [Fact]
    public void OwnerReleaseInvalidatesAliasesWithoutHoldingImageBudgetOrDeviceLease()
    {
        using var device = new CountingDevice();
        using var lifetime = new SkiaRenderDeviceLifetime(device);
        using var budget = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 1, MaximumBytes = 64 });
        using var frame = budget.BeginFrame();
        using var source = Owned(device, frame.Reserve(1, 64, "validation-lifetime"), lifetime.Acquire());
        using var alias = source.BorrowAs(ShaderSurfaceKind.Color);
        using var graph = Graph(device);
        Execute(graph, source);
        lifetime.Dispose();
        Assert.Equal(0, device.Disposals);
        Assert.Equal(64, budget.Usage.EstimatedBytes);
        source.Dispose();
        Assert.Equal(1, device.Disposals);
        Assert.Equal(0, budget.Usage.EstimatedBytes);
        Assert.Equal(0, budget.Usage.ActiveSurfaces);
        Assert.False(alias.HasValidatedColorPixels(device));
        Assert.Equal(nint.Zero, alias.Image.Handle);
        Assert.Equal("InvalidGraphSurface", Failure(() => Execute(graph, alias)).Code);
        Assert.Equal(1, device.ImageReads);
        alias.Dispose(); source.Dispose(); lifetime.Dispose();
        Assert.Equal(1, device.Disposals);
    }

    [Fact]
    public void PublicImageDisposalStillFailsTheOriginalSurfaceChecks()
    {
        using var device = new CountingDevice();
        using var source = Owned(device);
        using var graph = Graph(device);
        Execute(graph, source);
        source.Image.Dispose();
        Assert.Equal("InvalidGraphSurface", Failure(() => Execute(graph, source)).Code);
        Assert.Equal(1, device.ImageReads);
    }

    [Fact]
    public void CrossDeviceConsumersRecheckAndConvertedOutputsHaveIndependentReceipts()
    {
        using var firstDevice = new CountingDevice();
        using var secondDevice = new CountingDevice();
        using var source = Owned(firstDevice);
        using var first = Graph(firstDevice);
        using var second = Graph(secondDevice);
        Execute(first, source);
        Execute(second, source); Execute(second, source);
        Assert.Equal(3, firstDevice.ImageReads);
        using var copier = Graph(secondDevice, copy: true);
        using var result = copier.Execute(Frame, Inputs(source));
        Assert.Equal(4, firstDevice.ImageReads);
        Assert.Equal(1, firstDevice.Rasterizations);
        Assert.Equal(1, secondDevice.ImageReads);
        Execute(second, result.Output);
        Assert.Equal(1, secondDevice.ImageReads);
        Execute(first, result.Output); Execute(first, result.Output);
        Assert.Equal(3, secondDevice.ImageReads);
        Assert.Equal(SKColors.Red, Pixel(result.Output));
    }

    [Fact]
    public void ValidatedInputsStillEnforceDimensionAndBothGraphAndSharedBudgets()
    {
        using var device = new CountingDevice();
        using var source = Owned(device);
        using var exact = Graph(device, budget: new() { MaximumSurfaces = 1, MaximumBytes = 64, MaximumPixelDimension = 4 });
        Execute(exact, source);
        using var narrow = Graph(device, budget: new() { MaximumPixelDimension = 3 });
        Assert.Equal("GraphSurfaceDimensionExceeded", Failure(() => Execute(narrow, source)).Code);
        using var tooSmall = Graph(device, budget: new() { MaximumBytes = 63 });
        Assert.Equal("GraphSurfaceBudgetExceeded", Failure(() => Execute(tooSmall, source)).Code);
        using var sharedBudget = new ImageSurfaceBudgetSession(new() { MaximumBytes = 63 });
        using var frame = sharedBudget.BeginFrame();
        var error = Assert.Throws<ImageSurfaceBudgetException>(() => exact.Execute(Frame, Inputs(source), frame));
        Assert.Equal("BorrowGraphInput", error.Operation);
        Assert.Equal(0, sharedBudget.Usage.EstimatedBytes);
        Assert.Equal(1, device.ImageReads);
    }

    [Fact]
    public void KindGeometryAndSrgbChecksRemainBeforePixelReuse()
    {
        using var device = new CountingDevice();
        using var source = Owned(device);
        using var graph = Graph(device);
        Execute(graph, source);
        using var wrongKind = source.BorrowAs(ShaderSurfaceKind.Coverage);
        Assert.Equal("InvalidGraphSurface", Failure(() => Execute(graph, wrongKind)).Code);
        using var wrongGeometry = SkiaGraphSurface.Borrow(source.Image, Descriptor with { DensityX = 2 }, ShaderSurfaceKind.Color, device);
        Assert.Equal("InvalidGraphSurface", Failure(() => Execute(graph, wrongGeometry)).Code);
        using var linear = Owned(device, linear: true);
        Assert.Equal("GraphColorSpaceMismatch", Failure(() => Execute(graph, linear)).Code);
        Assert.Equal(1, device.ImageReads);
    }

    [Fact]
    public void NewImagesAndDifferentPlansCannotReuseAnEarlierSuccessForIllegalPixels()
    {
        using var device = new CountingDevice();
        using var good = Graph(device, shader: "half4 main(float2 p) { return half4(.25,0,0,.25); }");
        using var bad = Graph(device, shader: "half4 main(float2 p) { return p.x < 1 && p.y < 1 ? half4(1,0,0,.25) : half4(.25,0,0,.25); }");
        using var first = good.Execute(Frame, new Dictionary<string, SkiaGraphInput>());
        using var alias = first.Output.BorrowAs(ShaderSurfaceKind.Color);
        using var consumer = Graph(device);
        Execute(consumer, alias);
        Assert.Equal(1, device.ImageReads);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var error = Failure(() => bad.Execute(Frame, new Dictionary<string, SkiaGraphInput>()));
            Assert.Equal("GraphColorNotPremultiplied", error.Code);
            Assert.Equal("output", error.Field);
        }
        Assert.Equal(3, device.ImageReads);
        using var next = good.Execute(Frame, new Dictionary<string, SkiaGraphInput>());
        Assert.Equal(4, device.ImageReads);
        Assert.Equal(SkiaTestReadback.Pixels(first.Output.Image), SkiaTestReadback.Pixels(next.Output.Image));
    }

    [Fact]
    public void OwningSnapshotAloneDoesNotProveColorPixelsValid()
    {
        using var device = new CountingDevice();
        using var source = Owned(device, kind: ShaderSurfaceKind.Coverage);
        using var alias = source.BorrowAs(ShaderSurfaceKind.Color);
        using var graph = Graph(device);
        Assert.Equal(0, device.ImageReads);
        Execute(graph, alias); Execute(graph, alias);
        Assert.Equal(1, device.ImageReads);
    }

    private static void Execute(CompiledImageGraph graph, SkiaGraphSurface source, CancellationToken cancellationToken = default)
    { using var result = graph.Execute(Frame, Inputs(source), cancellationToken); }

    private static Dictionary<string, SkiaGraphInput> Inputs(SkiaGraphSurface source)
        => new() { ["target"] = new(source, GraphInputSource.TargetColor) };

    private static ImageGraphException Failure(Action action) => Assert.Throws<ImageGraphException>(action);

    private static SKColor Pixel(SkiaGraphSurface source)
    { byte[] bytes = SkiaTestReadback.Pixels(source.Image); return new(bytes[2], bytes[1], bytes[0], bytes[3]); }

    private static SkiaGraphSurface Owned(CountingDevice device, ImageSurfaceReservation? reservation = null,
        Action? releaseDeviceLease = null, bool linear = false, ShaderSurfaceKind kind = ShaderSurfaceKind.Color)
    {
        using var colorSpace = linear ? SKColorSpace.CreateSrgbLinear() : SKColorSpace.CreateSrgb();
        using var native = device.CreateSurface(new SKImageInfo(4, 4, SKColorType.Bgra8888, SKAlphaType.Premul, colorSpace));
        native.Canvas.Clear(SKColors.Red);
        return SkiaGraphSurface.Own(native.Snapshot(), Descriptor, kind, device, reservation, releaseDeviceLease);
    }

    private static CompiledImageGraph Graph(CountingDevice device, bool copy = false, string? shader = null, GraphExecutionBudget? budget = null)
    {
        GraphNodeBounds bounds = new(new GraphExpansion.Literal(0), 0, 0, GraphEdgeMode.Transparent);
        List<GraphInputDefinition> inputs = shader is null ? [new("target", GraphInputSource.TargetColor, ShaderSurfaceKind.Color,
            true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)] : [];
        List<ImageGraphNode> nodes = shader is not null ? [new ImageGraphNode.RuntimeShader("output", "program", ShaderSurfaceKind.Color, [], [], bounds)]
            : copy ? [new ImageGraphNode.Transform("output", GraphReference.Input("target"), SceneMatrix.Identity, Descriptor.Bounds, bounds)] : [];
        List<KeyValuePair<string, ShaderProgramDefinition>> programs = shader is null ? []
            : [new("program", new("test.sksl", shader, ShaderProgramDefinition.RasterProfile, [], []))];
        var definition = new ImageGraphDefinition("validation-dedup", ImageGraphDefinition.CurrentVersion, inputs, [], nodes,
            nodes.Count == 0 ? GraphReference.Input("target") : GraphReference.Node("output"), GraphCoordinateSpace.Canvas,
            0, GraphAnchorMode.ImageOnly, GraphCompositeMode.SourceOver);
        return CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [], programs, budget), device);
    }

    private sealed class CountingDevice : ISkiaRenderDevice
    {
        private readonly CpuSkiaRenderDevice _inner = new();
        private bool _disposed;
        public int ImageReads { get; private set; }
        public long ImageReadBytes { get; private set; }
        public int Rasterizations { get; private set; }
        public int Disposals { get; private set; }
        public bool FailNextImageRead { get; set; }
        public bool CorruptNextImageRead { get; set; }
        public Action? AfterImageRead { get; set; }
        public SkiaRenderDeviceInfo Info => _inner.Info;
        public object SynchronizationRoot => _inner.SynchronizationRoot;
        public SKSurface CreateSurface(SKImageInfo imageInfo) => _inner.CreateSurface(imageInfo);
        public void FlushAndSubmit(SKSurface surface) => _inner.FlushAndSubmit(surface);
        public SKImage? RasterizeImage(SKImage image) { Rasterizations++; return _inner.RasterizeImage(image); }
        public bool ReadPixels(SKSurface surface, SKImageInfo imageInfo, IntPtr destination, int rowBytes)
            => _inner.ReadPixels(surface, imageInfo, destination, rowBytes);
        public bool ReadPixels(SKImage image, SKImageInfo imageInfo, IntPtr destination, int rowBytes)
        {
            ImageReads++; ImageReadBytes += checked((long)rowBytes * imageInfo.Height);
            if (FailNextImageRead) { FailNextImageRead = false; return false; }
            bool result = _inner.ReadPixels(image, imageInfo, destination, rowBytes);
            if (CorruptNextImageRead)
            { CorruptNextImageRead = false; Marshal.WriteByte(destination, 2, 255); Marshal.WriteByte(destination, 3, 0); }
            AfterImageRead?.Invoke();
            return result;
        }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            Disposals++; _inner.Dispose();
        }
    }
}
