using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;
using V2 = MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class WarpPixelRenderingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 网格源表面分配前检查预算并保留作者归属及正常帧恢复(bool byteLimit)
    {
        using var budget = new ImageSurfaceBudgetSession(byteLimit
            ? new() { MaximumBytes = 1 } : new() { MaximumPixelDimension = 1 });
        using var device = new ObservedDevice();
        using var renderer = new SkiaRenderPipeline(null, null, budget, null, false, device, false);
        var snapshot = TextSnapshot();
        var scene = renderer.GetSceneFrame(snapshot, TimeUs);
        var token = scene.TokenGeometries[0];
        var prepared = Prepared(snapshot, scene).WithTextWarps([Binding(new V2.TextTarget.Family(token.WithRuby),
            TranslationWarp(DomainFor(token), 12, 0))]);
        var error = Assert.Throws<EffectFailureException>(() => Render(renderer, snapshot, TimeUs, prepared));
        Assert.Equal(byteLimit ? "ImageSurfaceBudgetExceeded" : "WarpSurfaceBudgetExceeded", error.Code);
        Assert.Equal("warp-pixel-test", error.InstanceId.Value);
        Assert.Equal("warp-pixel-test-artifact", error.ArtifactHash);
        Assert.Equal("RenderTextWarp", error.Stage);
        Assert.Equal(TimeUs, error.TimeUs);
        Assert.Equal(1, device.CreatedSurfaces); // Main output only; rejected warp storage never reaches device.
        Assert.Equal(0, budget.Usage.ActiveSurfaces);
        Assert.Equal(0, budget.Usage.EstimatedBytes);
        using var recovered = Render(renderer, snapshot, TimeUs);
        Assert.Contains(recovered.PixelMemory.ToArray(), value => value != 0);
    }

    [Fact]
    public void 网格源绘制中取消释放表面并允许下一帧完整恢复()
    {
        using var budget = new ImageSurfaceBudgetSession();
        using var cancellation = new CancellationTokenSource();
        using var device = new ObservedDevice();
        using var renderer = new SkiaRenderPipeline(null, null, budget, null, false, device, false);
        var snapshot = TextSnapshot();
        var scene = renderer.GetSceneFrame(snapshot, TimeUs);
        var token = scene.TokenGeometries[0];
        var prepared = Prepared(snapshot, scene).WithTextWarps([Binding(new V2.TextTarget.Family(token.WithRuby),
            TranslationWarp(DomainFor(token), 12, 0))]);
        device.OnCreate = count => { if (count == 2) cancellation.Cancel(); };
        var error = Assert.Throws<OperationCanceledException>(() => renderer.Render(RenderRequest.At(snapshot, TimeUs)
            with { TransparentBackground = true, PreparedFrame = prepared, CancellationToken = cancellation.Token }));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(2, device.CreatedSurfaces);
        Assert.True(budget.Usage.PeakBytes > 0);
        Assert.Equal(0, budget.Usage.ActiveSurfaces);
        Assert.Equal(0, budget.Usage.EstimatedBytes);
        device.OnCreate = null;
        using var recovered = Render(renderer, snapshot, TimeUs, prepared);
        using var repeated = Render(renderer, snapshot, TimeUs, prepared);
        Assert.Equal(recovered.PixelMemory.ToArray(), repeated.PixelMemory.ToArray());
        Assert.Equal(0, budget.Usage.ActiveSurfaces);
        Assert.Equal(0, budget.Usage.EstimatedBytes);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(1, 4)]
    [InlineData(2, 3)]
    public void 输出及局部放大时源纹理保持足够采样密度且像素接近等效仿射(int outputScale, int localScale)
    {
        using var device = new ObservedDevice();
        using var renderer = new SkiaRenderPipeline(null, null, null, null, false, device, false);
        var snapshot = TextSnapshot();
        var scene = renderer.GetSceneFrame(snapshot, TimeUs);
        var token = scene.TokenGeometries[0];
        var target = new RenderTarget { PixelWidth = 320 * outputScale, PixelHeight = 180 * outputScale };
        var scale = Fx.Affine2.ScaleAt(localScale, token.BaseGeometry.AdvanceBounds.Center);
        var translation = Fx.Affine2.Translation(2, 1);
        var prepared = new PreparedRenderFrame(snapshot, TimeUs, Fx.OutputMode.Transparent, target, scene,
            [new(token.WithRuby, scale)]).WithTextWarps([Binding(new V2.TextTarget.Family(token.WithRuby),
                TranslationWarp(DomainFor(token), 2, 1))]);
        var affine = new PreparedRenderFrame(snapshot, TimeUs, Fx.OutputMode.Transparent, target, scene,
            [new(token.WithRuby, scale.Compose(translation))]);
        using var actual = renderer.Render(RenderRequest.At(snapshot, TimeUs) with
            { TransparentBackground = true, Target = target, PreparedFrame = prepared });
        var source = device.SurfaceSizes[1];
        var ink = token.BaseGeometry.InkBounds!.Value;
        Assert.True(source.Width >= Math.Ceiling(ink.Width * outputScale * localScale));
        Assert.True(source.Height >= Math.Ceiling(ink.Height * outputScale * localScale));
        using var expected = renderer.Render(RenderRequest.At(snapshot, TimeUs) with
            { TransparentBackground = true, Target = target, PreparedFrame = affine });
        using var plain = renderer.Render(RenderRequest.At(snapshot, TimeUs) with
            { TransparentBackground = true, Target = target });
        var changed = Difference(plain, expected);
        Assert.True(changed.DifferentPixels > 20);
        var error = Difference(actual, expected);
        Assert.True(error.DifferentPixels <= Math.Max(48, changed.DifferentPixels / 2),
            $"放大网格与等效仿射差异过大：{error.DifferentPixels}/{changed.DifferentPixels}。");
    }

    private sealed class ObservedDevice : ISkiaRenderDevice
    {
        private readonly ISkiaRenderDevice _inner = SkiaRenderDeviceFactory.Create(SkiaRenderBackendOptions.Parse(
            Environment.GetEnvironmentVariable("MAKO_WARP_TEST_BACKEND")));
        public Action<int>? OnCreate { get; set; }
        public int CreatedSurfaces { get; private set; }
        public List<SKImageInfo> SurfaceSizes { get; } = [];
        public object SynchronizationRoot => _inner.SynchronizationRoot;
        public SkiaRenderDeviceInfo Info => _inner.Info;
        public SKSurface CreateSurface(SKImageInfo imageInfo)
        {
            var result = _inner.CreateSurface(imageInfo);
            CreatedSurfaces++;
            SurfaceSizes.Add(imageInfo);
            OnCreate?.Invoke(CreatedSurfaces);
            return result;
        }
        public void FlushAndSubmit(SKSurface surface) => _inner.FlushAndSubmit(surface);
        public SKImage? RasterizeImage(SKImage image) => _inner.RasterizeImage(image);
        public bool ReadPixels(SKSurface surface, SKImageInfo imageInfo, IntPtr destination, int rowBytes)
            => _inner.ReadPixels(surface, imageInfo, destination, rowBytes);
        public bool ReadPixels(SKImage image, SKImageInfo imageInfo, IntPtr destination, int rowBytes)
            => _inner.ReadPixels(image, imageInfo, destination, rowBytes);
        public void Dispose() => _inner.Dispose();
    }
}
