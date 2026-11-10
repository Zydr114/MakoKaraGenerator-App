using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class MainCanvasImageEffectsTests
{
    private static ProjectSnapshot WithoutRuby(ProjectSnapshot snapshot)
    {
        var branch = snapshot.Lyrics.Branches[0];
        return snapshot with { Lyrics = snapshot.Lyrics.ReplaceBranch(branch with
        {
            Lines = [.. branch.Lines.Select(line => line with { RubyAnnotations = [] })],
        }) };
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void EmptyRubyCapturesPreservePixelsAndPhysicalSurfaceAdmissions(bool decorated, bool transparent)
    {
        var request = Request(WithoutRuby(Snapshot(decorated)), transparent);
        using var graph = Tint("first");
        using var resources = new SceneImageEffectResources([Program("first", graph)]);
        using var device = new CountingDevice();
        using var renderer = new SkiaRenderPipeline(null, null, null, resources, false, device, false);
        using var actual = renderer.Render(Bindings(request, Bind("effect", Line(request))));
        Close(WithBackground(ExpectedParts(request, Whole, graph), transparent), actual.Pixels.ToArray());

        int noRubyReads = device.ImageReads;
        int noRubySurfaces = device.Surfaces;
        var rubyRequest = Request(Snapshot(decorated), transparent);
        using var rubyDevice = new CountingDevice();
        using var rubyRenderer = new SkiaRenderPipeline(null, null, null, resources, false, rubyDevice, false);
        using var rubyActual = rubyRenderer.Render(Bindings(rubyRequest, Bind("effect", Line(rubyRequest))));
        Close(WithBackground(ExpectedParts(rubyRequest, Whole, graph), transparent), rubyActual.Pixels.ToArray());
        Assert.Equal(3, rubyDevice.ImageReads - noRubyReads);
        Assert.Equal(noRubySurfaces, rubyDevice.Surfaces);
        Assert.True(device.ImageReadDestinations.Count >= 4);
        Assert.Single(device.ImageReadDestinations.Take(3).Distinct());
    }

    private static byte[] WithBackground(byte[] pixels, bool transparent)
    {
        if (transparent) return pixels;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int inverse = 255 - pixels[i + 3];
            pixels[i] = (byte)(pixels[i] + (60 * inverse + 127) / 255);
            pixels[i + 1] = (byte)(pixels[i + 1] + (40 * inverse + 127) / 255);
            pixels[i + 2] = (byte)(pixels[i + 2] + (25 * inverse + 127) / 255);
            pixels[i + 3] = 255;
        }
        return pixels;
    }

    [Fact]
    public void EmptyRubyStillRequiresCaptureBudgetAndReleasesReservations()
    {
        var request = Request(WithoutRuby(Snapshot(false)));
        using var graph = Tint("first");
        using var resources = new SceneImageEffectResources([Program("first", graph)]);
        using var budget = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 3 });
        using var renderer = new SkiaRenderPipeline(null, null, budget, resources);
        var error = Assert.Throws<EffectFailureException>(() => renderer.Render(Bindings(request, Bind("budget", Line(request)))));
        Assert.Equal("ImageSurfaceBudgetExceeded", error.Code);
        var budgetError = Assert.IsType<ImageSurfaceBudgetException>(error.InnerException);
        Assert.Equal("CaptureSceneColorPart", budgetError.Operation);
        Assert.Equal(3, budgetError.CurrentSurfaces);
        Assert.Equal(1, budgetError.AdditionalSurfaces);
        Assert.Equal(0, budget.Usage.ActiveSurfaces);
        Assert.Equal(0, budget.Usage.EstimatedBytes);
    }

    [Fact]
    public void EmptyRubyPreservesExactSurfaceLimitBoundary()
    {
        var request = Request(WithoutRuby(Snapshot(false)));
        using var graph = Tint("first");
        using var resources = new SceneImageEffectResources([Program("first", graph)]);
        // The same fixture with alpha reads forced measured failure at six and success at seven.
        using var belowBudget = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 6 });
        using var belowRenderer = new SkiaRenderPipeline(null, null, belowBudget, resources);
        var failure = Assert.Throws<EffectFailureException>(() => belowRenderer.Render(Bindings(request, Bind("below", Line(request)))));
        Assert.Equal("ImageSurfaceBudgetExceeded", failure.Code);
        Assert.Equal(0, belowBudget.Usage.ActiveSurfaces);
        Assert.Equal(0, belowBudget.Usage.EstimatedBytes);
        using var exactBudget = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 7 });
        using var exactRenderer = new SkiaRenderPipeline(null, null, exactBudget, resources);
        using var exact = exactRenderer.Render(Bindings(request, Bind("exact", Line(request))));
        Close(ExpectedParts(request, Whole, graph), exact.Pixels.ToArray());
    }

    // O4：关闭检查时空部件判定不读回；未发起绘制的部件仍共享空表面（保留 6／7 精确边界），已绘制的装饰不被当作空。
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 关闭像素检查时空原始部件不读回_未绘制部件仍共享空表面且装饰不丢(bool decorated)
    {
        var request = Request(WithoutRuby(Snapshot(decorated)));
        using var graph = Tint("first");
        using var resources = new SceneImageEffectResources([Program("first", graph)]);
        using var onDevice = new CountingDevice();
        using var onRenderer = new SkiaRenderPipeline(null, null, null, resources, false, onDevice, false);
        using var on = onRenderer.Render(Bindings(request, Bind("on", Line(request))));
        Assert.True(onDevice.ImageReads > 0);

        using var offDevice = new CountingDevice();
        using var offGraph = CompiledImageGraph.Compile(graph.Plan, offDevice, false);
        using var offResources = new SceneImageEffectResources([Program("first", offGraph)]);
        using var offRenderer = new SkiaRenderPipeline(null, null, null, offResources, false, offDevice, false, graphPixelChecks: false);
        using var off = offRenderer.Render(Bindings(request, Bind("off", Line(request))));
        Assert.Equal(0, offDevice.ImageReads);
        Assert.Equal(on.Pixels.ToArray(), off.Pixels.ToArray());
        if (decorated) return;

        using var belowBudget = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 6 });
        using var belowRenderer = new SkiaRenderPipeline(null, null, belowBudget, offResources, false, offDevice, false, graphPixelChecks: false);
        Assert.Equal("ImageSurfaceBudgetExceeded", Assert.Throws<EffectFailureException>(
            () => belowRenderer.Render(Bindings(request, Bind("below", Line(request))))).Code);
        using var exactBudget = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 7 });
        using var exactRenderer = new SkiaRenderPipeline(null, null, exactBudget, offResources, false, offDevice, false, graphPixelChecks: false);
        using var exact = exactRenderer.Render(Bindings(request, Bind("exact", Line(request))));
        Assert.Equal(on.Pixels.ToArray(), exact.Pixels.ToArray());
        Assert.Equal(0, offDevice.ImageReads);
    }

    [Fact]
    public void AlphaReadbackFailureDoesNotPoisonTheNextRender()
    {
        var request = Request(WithoutRuby(Snapshot(false)));
        using var graph = Tint("first");
        using var resources = new SceneImageEffectResources([Program("first", graph)]);
        using var budget = new ImageSurfaceBudgetSession();
        using var device = new CountingDevice { FailNextImageRead = true };
        using var renderer = new SkiaRenderPipeline(null, null, budget, resources, false, device, false);
        Assert.Throws<EffectFailureException>(() => renderer.Render(Bindings(request, Bind("failure", Line(request)))));
        Assert.Equal(0, budget.Usage.ActiveSurfaces);
        Assert.Equal(0, budget.Usage.EstimatedBytes);
        using var actual = renderer.Render(Bindings(request, Bind("recovery", Line(request))));
        Close(WithBackground(ExpectedParts(request, Whole, graph), true), actual.Pixels.ToArray());
        Assert.True(device.ImageReads >= 5);
    }

    [Fact]
    public void AlphaScanCancellationReleasesTheFrameAndNextRenderRecovers()
    {
        var request = Request(WithoutRuby(Snapshot(false)));
        using var graph = Tint("first");
        using var resources = new SceneImageEffectResources([Program("first", graph)]);
        using var budget = new ImageSurfaceBudgetSession();
        using var cancel = new CancellationTokenSource();
        using var device = new CountingDevice { AfterImageRead = cancel.Cancel };
        using var renderer = new SkiaRenderPipeline(null, null, budget, resources, false, device, false);
        Assert.Throws<OperationCanceledException>(() => renderer.Render(Bindings(request with { CancellationToken = cancel.Token },
            Bind("cancel", Line(request)))));
        Assert.Equal(0, budget.Usage.ActiveSurfaces);
        Assert.Equal(0, budget.Usage.EstimatedBytes);
        device.AfterImageRead = null;
        using var actual = renderer.Render(Bindings(request, Bind("recovery", Line(request))));
        Close(WithBackground(ExpectedParts(request, Whole, graph), true), actual.Pixels.ToArray());
    }

    [Fact]
    public void OversizedAlphaReadbacksAreNotRetainedAcrossOriginalParts()
    {
        var request = Request(WithoutRuby(Snapshot(false)), transparent: true, width: 4097, height: 1024);
        using var graph = Tint("first");
        using var resources = new SceneImageEffectResources([Program("first", graph)]);
        using var device = new CountingDevice();
        // 要整屏大小（超过 16 MiB）的部件表面，关闭对象区域收紧（O7／第 12 条）。
        using var renderer = new SkiaRenderPipeline(null, null, null, resources, false, device, false) { TightObjectRegions = false };
        using var actual = renderer.Render(Bindings(request, Bind("oversized", Line(request))));
        int bytes = checked(4097 * 1024 * 4);
        Assert.True(bytes > 16 * 1024 * 1024);
        Assert.True(device.ImageReadLengths.Count >= 4);
        Assert.All(device.ImageReadLengths.Take(3), length => Assert.Equal(bytes, length));
    }

    [Fact]
    public void OriginalAlphaReadbackRetainsOnlyBoundedBufferAndScansValidRows()
    {
        using var buffer = new SkiaRenderPipeline.OriginalAlphaReadbackBuffer();
        var max = buffer.Get(16 * 1024 * 1024);
        Assert.Same(max, buffer.Get(8));
        max[3] = 0;
        max[7] = 255;
        Assert.True(SkiaRenderPipeline.IsEmptyOriginalPixels(max.AsSpan(0, 4), 4, 1, CancellationToken.None));
        Assert.False(SkiaRenderPipeline.IsEmptyOriginalPixels(max.AsSpan(0, 8), 4, 2, CancellationToken.None));
        var oversized = buffer.Get(16 * 1024 * 1024 + 1);
        Assert.NotSame(oversized, buffer.Get(16 * 1024 * 1024 + 1));
        var fresh = buffer.Get(8);
        Assert.NotSame(max, fresh);
        buffer.Dispose();
        Assert.NotSame(fresh, buffer.Get(8));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => SkiaRenderPipeline.IsEmptyOriginalPixels(fresh, 4, 2, cancelled.Token));
    }

    private sealed class CountingDevice : ISkiaRenderDevice
    {
        private readonly CpuSkiaRenderDevice _inner = new();
        public int ImageReads { get; private set; }
        public int Surfaces { get; private set; }
        public bool FailNextImageRead { get; set; }
        public Action? AfterImageRead { get; set; }
        public List<IntPtr> ImageReadDestinations { get; } = [];
        public List<int> ImageReadLengths { get; } = [];
        public SkiaRenderDeviceInfo Info => _inner.Info;
        public object SynchronizationRoot => _inner.SynchronizationRoot;
        public SKSurface CreateSurface(SKImageInfo info) { Surfaces++; return _inner.CreateSurface(info); }
        public void FlushAndSubmit(SKSurface surface) => _inner.FlushAndSubmit(surface);
        public SKImage? RasterizeImage(SKImage image) => _inner.RasterizeImage(image);
        public bool ReadPixels(SKSurface surface, SKImageInfo info, IntPtr destination, int rowBytes)
            => _inner.ReadPixels(surface, info, destination, rowBytes);
        public bool ReadPixels(SKImage image, SKImageInfo info, IntPtr destination, int rowBytes)
        {
            ImageReads++;
            if (FailNextImageRead) { FailNextImageRead = false; return false; }
            bool result = _inner.ReadPixels(image, info, destination, rowBytes);
            if (result)
            {
                ImageReadDestinations.Add(destination);
                ImageReadLengths.Add(checked(rowBytes * info.Height));
                AfterImageRead?.Invoke();
            }
            return result;
        }
        public void Dispose() => _inner.Dispose();
    }
}
