using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>图／材质逐像素检查开关：开启时原诊断不变；关闭时不为检查同步提交或读回，作者错误输出原样保留。</summary>
public sealed class GraphPixelCheckModeTests
{
    private static readonly GraphNodeBounds Bounds = new(new GraphExpansion.Literal(0), 0, 0, GraphEdgeMode.Transparent);
    private static readonly GraphRect Rect = new(0, 0, 16, 16);
    private static readonly ImageGraphFrameContext Frame = new(0, 16, 16, Rect, 1, 1, ParameterValues.Empty, false);

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void 非预乘节点输出_开启时报原诊断_关闭时不读回不提交且原样输出(bool checks)
    {
        using var device = new CountingDevice();
        using var graph = ShaderGraph(device, checks, "half4 main(float2 p) { return half4(1,0,0,.25); }");
        if (checks)
        {
            Assert.Equal("GraphColorNotPremultiplied", Assert.Throws<ImageGraphException>(
                () => graph.Execute(Frame, new Dictionary<string, SkiaGraphInput>())).Code);
            Assert.True(device.ImageReads > 0); Assert.True(device.Submits > 0);
            return;
        }
        using var result = graph.Execute(Frame, new Dictionary<string, SkiaGraphInput>());
        Assert.Equal(0, device.ImageReads); Assert.Equal(0, device.Submits);
        // 不静默修正：BGRA 中 R=255 超过 alpha=64。
        byte[] pixel = SkiaTestReadback.Pixel(result.Output.Image, 8, 8);
        Assert.Equal(255, pixel[2]); Assert.InRange(pixel[3], 63, 65);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void 已覆盖材质越出区域_开启时报原诊断_关闭时不读回(bool checks)
    {
        using var device = new CountingDevice();
        using var color = Borrowed(SKColors.Red, ShaderSurfaceKind.Color);
        using var region = Borrowed(new SKColor(0, 0, 0, 128), ShaderSurfaceKind.Coverage);
        using var graph = PassThroughGraph(device, checks);
        using var raw = graph.Execute(Frame, new Dictionary<string, SkiaGraphInput> { ["target"] = new(color.Value, GraphInputSource.TargetColor) });
        int readsBefore = device.ImageReads;
        if (checks)
        {
            var error = Assert.Throws<ImageGraphException>(() => graph.ApplyMaterialCoverage(raw, region.Value, EffectMaterialCoverageMode.Masked));
            Assert.Equal("MaterialOutputOutsideRegion", error.Code);
            Assert.True(device.ImageReads > readsBefore);
            return;
        }
        using var bounded = graph.ApplyMaterialCoverage(raw, region.Value, EffectMaterialCoverageMode.Masked);
        Assert.Equal(0, device.ImageReads); Assert.Equal(0, device.Submits);
        // Masked 不二次乘区域：越界的作者输出原样复制。
        Assert.Equal(SkiaTestReadback.Pixels(raw.Output.Image), SkiaTestReadback.Pixels(bounded.Output.Image));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void 场景合成产物_关闭时不为检查同步提交或读回_像素不变(bool checks)
    {
        using var device = new CountingDevice();
        using var host = new SceneCompositionProgram(device, GraphExecutionBudget.Default, pixelChecks: checks);
        using var budget = new ImageSurfaceBudgetSession(); using var frame = budget.BeginFrame();
        using var color = Borrowed(new SKColor(255, 0, 0, 128), ShaderSurfaceKind.Color);
        using var payload = host.CaptureCompositionPayload(color.Value, Frame, frame);
        if (checks) { Assert.True(device.Submits > 0); Assert.True(device.ImageReads > 0); }
        else { Assert.Equal(0, device.Submits); Assert.Equal(0, device.ImageReads); }
        Assert.Equal(SkiaTestReadback.Pixels(color.Value.Image), SkiaTestReadback.Pixels(payload.Color.Image));
    }

    private static CompiledImageGraph ShaderGraph(CountingDevice device, bool checks, string shader)
    {
        var definition = new ImageGraphDefinition("pixel-checks", ImageGraphDefinition.CurrentVersion, [], [],
            [new ImageGraphNode.RuntimeShader("output", "program", ShaderSurfaceKind.Color, [], [], Bounds)], GraphReference.Node("output"),
            GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ImageOnly, GraphCompositeMode.SourceOver);
        return CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [],
            [new("program", new("test.sksl", shader, ShaderProgramDefinition.RasterProfile, [], []))]), device, checks);
    }

    private static CompiledImageGraph PassThroughGraph(CountingDevice device, bool checks)
    {
        var definition = new ImageGraphDefinition("pixel-checks-material", ImageGraphDefinition.CurrentVersion,
            [new("target", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)], [],
            [new ImageGraphNode.Transform("output", GraphReference.Input("target"), SceneMatrix.Identity, Rect, Bounds)], GraphReference.Node("output"),
            GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ImageOnly, GraphCompositeMode.SourceOver);
        return CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [], []), device, checks);
    }

    private static BorrowedSurface Borrowed(SKColor color, ShaderSurfaceKind kind)
    {
        using var srgb = SKColorSpace.CreateSrgb();
        using var native = SKSurface.Create(new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
        native.Canvas.Clear(color);
        var image = native.Snapshot();
        return new(image, SkiaGraphSurface.Borrow(image, new(Rect, GraphCoordinateSpace.Canvas, SceneMatrix.Identity, 1, 1), kind));
    }

    private sealed class BorrowedSurface(SKImage image, SkiaGraphSurface value) : IDisposable
    {
        public SkiaGraphSurface Value { get; } = value;
        public void Dispose() { Value.Dispose(); image.Dispose(); }
    }

    /// <summary>委托给实际设备，计数同步提交、不等待提交与图像读回；CPU 与 Vulkan 共用。</summary>
    internal sealed class CountingDevice(ISkiaRenderDevice? inner = null) : ISkiaRenderDevice
    {
        private readonly ISkiaRenderDevice _inner = inner ?? new CpuSkiaRenderDevice();
        public int ImageReads { get; private set; }
        public int Submits { get; private set; }
        public int SubmitsWithoutWait { get; private set; }
        public int Surfaces { get; private set; }
        public object SynchronizationRoot => _inner.SynchronizationRoot;
        public SkiaRenderDeviceInfo Info => _inner.Info;
        public SKSurface CreateSurface(SKImageInfo imageInfo) { Surfaces++; return _inner.CreateSurface(imageInfo); }
        public void FlushAndSubmit(SKSurface surface) { Submits++; _inner.FlushAndSubmit(surface); }
        public void SubmitWithoutWait(SKSurface surface) { SubmitsWithoutWait++; _inner.SubmitWithoutWait(surface); }
        public SKImage? RasterizeImage(SKImage image) => _inner.RasterizeImage(image);
        public SKImage? CreateTextureImage(SKImage image, bool mipmapped) => _inner.CreateTextureImage(image, mipmapped);
        public bool ReadPixels(SKSurface surface, SKImageInfo imageInfo, IntPtr destination, int rowBytes)
            => _inner.ReadPixels(surface, imageInfo, destination, rowBytes);
        public bool ReadPixels(SKImage image, SKImageInfo imageInfo, IntPtr destination, int rowBytes)
        { ImageReads++; return _inner.ReadPixels(image, imageInfo, destination, rowBytes); }
        public bool ReadOwnedImagePixels(SKImage image, SKImageInfo imageInfo, IntPtr destination, int rowBytes)
        { ImageReads++; return _inner.ReadOwnedImagePixels(image, imageInfo, destination, rowBytes); }
        public void Dispose() => _inner.Dispose();
    }
}
