using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

[CollectionDefinition("Skia telemetry", DisableParallelization = true)]
public sealed class SkiaTelemetryGroup { }

[Collection("Skia telemetry")]
public sealed class SkiaPerformanceTelemetryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualGraphReadbacksKeepPixelsAndDiagnosticsAndReportEachRequest(bool gpu)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        var backend = gpu ? SkiaRenderBackend.Vulkan : SkiaRenderBackend.Cpu;
        PerformanceTelemetry.Configure(null);
        var baseline = Render(backend);
        ConcurrentBag<string> lines = [];
        PerformanceTelemetry.Configure(lines.Add, detailEvery: 1, intervalSeconds: 3600, maxEventsPerSecond: 100);
        (byte[] Pixels, string[] Diagnostics) actual;
        try
        {
            // Calls outside a frame must not be attached to the next frame.
            Render(backend);
            using var scope = PerformanceTelemetry.BeginFrame("skia-test", gpu ? "gpu" : "cpu", 1, 16, 16);
            actual = Render(backend);
            scope.SetOutcome("ok");
        }
        finally { await PerformanceTelemetry.ShutdownAsync(); }

        Assert.Equal(baseline.Pixels, actual.Pixels);
        Assert.Equal(baseline.Diagnostics, actual.Diagnostics);
        string line = Assert.Single(lines, item => item.Contains("\"Kind\":\"frame\"", StringComparison.Ordinal));
        var record = JsonSerializer.Deserialize<PerformanceEvent>(line[PerformanceTelemetry.Prefix.Length..])!;
        Assert.Equal(6, record.ImageReads);
        Assert.Equal(6 * 16 * 16 * 4, record.ImageBytes);
        Assert.Equal(1, record.SurfaceReads);
        Assert.Equal(16 * 16 * 4, record.SurfaceBytes);
        Assert.True(record.GraphMs > 0);
        Assert.True(record.RasterMs > 0);
        Assert.True(record.ValidationMs > 0);
        Assert.True(record.ReadbackMs > 0);
        Assert.Equal(0, record.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidGraphOutputKeepsTheSameFailureWithTelemetryEnabled(bool gpu)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        var backend = gpu ? SkiaRenderBackend.Vulkan : SkiaRenderBackend.Cpu;
        PerformanceTelemetry.Configure(null);
        var baseline = InvalidGraph(backend);
        ConcurrentBag<string> lines = [];
        PerformanceTelemetry.Configure(lines.Add, detailEvery: 1, intervalSeconds: 3600, maxEventsPerSecond: 100);
        (string Code, string Field) actual;
        try
        {
            using var frame = PerformanceTelemetry.BeginFrame("error-test", gpu ? "gpu" : "cpu", 2, 16, 16);
            actual = InvalidGraph(backend);
        }
        finally { await PerformanceTelemetry.ShutdownAsync(); }
        Assert.Equal(baseline, actual);
        Assert.Equal("GraphColorNotPremultiplied", actual.Code);
        string line = Assert.Single(lines, item => item.Contains("\"Kind\":\"frame\"", StringComparison.Ordinal));
        var record = JsonSerializer.Deserialize<PerformanceEvent>(line[PerformanceTelemetry.Prefix.Length..])!;
        Assert.Equal(1, record.Errors);
        Assert.Equal(1, record.ImageReads);
        Assert.True(record.GraphMs > 0);
        Assert.True(record.ValidationMs > 0);
    }

    private static (string Code, string Field) InvalidGraph(SkiaRenderBackend backend)
    {
        using var device = SkiaRenderDeviceFactory.Create(new(backend));
        var node = new ImageGraphNode.RuntimeShader("output", "program", ShaderSurfaceKind.Color, [], [],
            new(new GraphExpansion.Literal(0), 0, 0, GraphEdgeMode.Transparent));
        var definition = new ImageGraphDefinition("invalid-telemetry-test", ImageGraphDefinition.CurrentVersion, [], [], [node],
            GraphReference.Node("output"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ImageOnly, GraphCompositeMode.SourceOver);
        var shader = new ShaderProgramDefinition("test.sksl", "half4 main(float2 p) { return half4(1,0,0,0); }",
            ShaderProgramDefinition.RasterProfile, [], []);
        using var graph = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [], [new("program", shader)]), device);
        var frame = new ImageGraphFrameContext(0, 16, 16, new(0, 0, 16, 16), 1, 1, ParameterValues.Empty, false);
        var error = Assert.Throws<ImageGraphException>(() => graph.Execute(frame, new Dictionary<string, SkiaGraphInput>()));
        return (error.Code, error.Field);
    }

    private static (byte[] Pixels, string[] Diagnostics) Render(SkiaRenderBackend backend)
    {
        using var device = SkiaRenderDeviceFactory.Create(new(backend));
        using var srgb = SKColorSpace.CreateSrgb();
        var info = new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul, srgb);
        using var native = device.CreateSurface(info);
        native.Canvas.Clear(SKColors.White);
        device.FlushAndSubmit(native);
        using var region = SkiaGraphSurface.Own(native.Snapshot(),
            new(new(0, 0, 16, 16), GraphCoordinateSpace.Canvas, SceneMatrix.Identity, 1, 1), ShaderSurfaceKind.Coverage, device);
        var node = new ImageGraphNode.RuntimeShader("output", "program", ShaderSurfaceKind.Color, [], [],
            new(new GraphExpansion.Literal(0), 0, 0, GraphEdgeMode.Transparent));
        var definition = new ImageGraphDefinition("telemetry-test", ImageGraphDefinition.CurrentVersion, [], [], [node],
            GraphReference.Node("output"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ImageOnly, GraphCompositeMode.SourceOver);
        var shader = new ShaderProgramDefinition("test.sksl", "half4 main(float2 p) { return half4(.25,0,0,.5); }",
            ShaderProgramDefinition.RasterProfile, [], []);
        using var graph = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [], [new("program", shader)]), device);
        var frame = new ImageGraphFrameContext(0, 16, 16, region.Descriptor.Bounds, 1, 1, ParameterValues.Empty, false);
        using var raw = graph.Execute(frame, new Dictionary<string, SkiaGraphInput>());
        using var result = graph.ApplyMaterialCoverage(raw, region, EffectMaterialCoverageMode.Masked);
        var nullInfo = info.WithColorSpace(null);
        byte[] pixels = Read(device, result.Output.Image, nullInfo, owned: true);
        // Explicit conversion exercises fallback without double counting.
        using var linear = SKColorSpace.CreateSrgbLinear();
        Read(device, result.Output.Image, info.WithColorSpace(linear), owned: true);
        byte[] surfacePixels = new byte[info.BytesSize];
        GCHandle pin = GCHandle.Alloc(surfacePixels, GCHandleType.Pinned);
        try { Assert.True(device.ReadPixels(native, info, pin.AddrOfPinnedObject(), info.RowBytes)); }
        finally { pin.Free(); }
        return (pixels, result.Diagnostics.Select(item => item.ToString()).ToArray());
    }

    private static byte[] Read(ISkiaRenderDevice device, SKImage image, SKImageInfo info, bool owned)
    {
        byte[] pixels = new byte[info.BytesSize];
        GCHandle pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            Assert.True(owned ? device.ReadOwnedImagePixels(image, info, pin.AddrOfPinnedObject(), info.RowBytes)
                : device.ReadPixels(image, info, pin.AddrOfPinnedObject(), info.RowBytes));
        }
        finally { pin.Free(); }
        return pixels;
    }
}
