using System.Reflection;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Rendering.Skia.Effects.Svg;
using MakoKaraGenerator.Text;
using SkiaSharp;
using global::Svg.Skia;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class InlineSvgAndPipelineLifecycleTests
{
    private static string InlineSvg(int id)
        => $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {24 + id} 24\"><rect x=\"2\" y=\"2\" width=\"20\" height=\"20\" fill=\"#ffffff\"/></svg>";

    [Fact]
    public void 内联Svg缓存租约在淘汰期间保活并在租约归零时确定释放原生句柄()
    {
        string firstXml = InlineSvg(100_000);
        SkiaRenderPipeline.CachedInlineSvg firstEntry;

        using (var firstLease = SkiaRenderPipeline.ResolveInlineSvg(firstXml))
        {
            firstEntry = firstLease.Entry;
            using (var secondLease = SkiaRenderPipeline.ResolveInlineSvg(firstXml))
            {
                Assert.Same(firstEntry, secondLease.Entry);
            }

            // 插入超过 64 个不同内联 SVG，触发 firstEntry 从缓存淘汰。
            for (int i = 1; i <= 70; i++)
            {
                using var lease = SkiaRenderPipeline.ResolveInlineSvg(InlineSvg(100_000 + i));
                Assert.False(lease.Entry.IsDisposed);
            }

            // 只要绘制租约尚未释放，被淘汰条目仍安全存活，不会发生并发 UAF。
            Assert.False(firstEntry.IsDisposed);
            Assert.NotEqual(IntPtr.Zero, firstEntry.UnitPicture.Handle);
            Assert.NotEqual(IntPtr.Zero, firstEntry.SilhouettePath.Handle);
        }

        // 租约释放后引用计数归零，SKPicture 与 SKPath 均已确定释放。
        Assert.True(firstEntry.IsDisposed);
        Assert.Equal(IntPtr.Zero, firstEntry.UnitPicture.Handle);
        Assert.Equal(IntPtr.Zero, firstEntry.SilhouettePath.Handle);
    }

    [Fact]
    public async Task 内联Svg并发解析同一源码竞争弃用项安全释放且租约均可用()
    {
        string sharedXml = $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 32 32\"><circle cx=\"16\" cy=\"16\" r=\"10\" id=\"race-{Guid.NewGuid():N}\" fill=\"#ff0000\"/></svg>";
        using var start = new ManualResetEventSlim(false);
        Task<SkiaRenderPipeline.InlineSvgLease>[] tasks =
        [
            .. Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                start.Wait();
                return SkiaRenderPipeline.ResolveInlineSvg(sharedXml);
            })),
        ];

        start.Set();
        SkiaRenderPipeline.InlineSvgLease[] leases = await Task.WhenAll(tasks);
        try
        {
            var winner = leases[0].Entry;
            foreach (var lease in leases)
            {
                Assert.Same(winner, lease.Entry);
                Assert.False(lease.Entry.IsDisposed);
                Assert.NotEqual(IntPtr.Zero, lease.Entry.UnitPicture.Handle);
            }
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
        }
    }

    [Fact]
    public void 内联Svg编译器启用全部安全开关并正确释放默认Srgb颜色空间()
    {
        var budget = SvgResourceBudget.Default;
        var (safeXml, vw, vh) = SvgStaticCompiler.CompileInline(InlineSvg(1), budget, CancellationToken.None);
        SvgNativePictureCompiler compiler = new(safeXml, vw, vh, "inline-test.svg", budget, CancellationToken.None);
        using (compiler)
        {
            var settings = (SKSvgSettings)typeof(SvgNativePictureCompiler)
                .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(compiler)!;

            Assert.False(settings.EnableSvgFonts);
            Assert.False(settings.EnableTextReferences);
            Assert.False(settings.EnableJavaScript);
            Assert.False(settings.EnableExternalJavaScript);
            Assert.NotNull(settings.TypefaceProviders);
            Assert.Empty(settings.TypefaceProviders!);
            Assert.NotNull(settings.Srgb);
            Assert.NotEqual(IntPtr.Zero, settings.Srgb.Handle);

            var (picture, silhouette, aspect) = compiler.CompileInline();
            using (picture)
            using (silhouette)
            {
                Assert.True(aspect > 0f);
                Assert.False(silhouette.IsEmpty);
            }
        }

        Assert.Throws<ObjectDisposedException>(() => compiler.CompileInline());
    }

    [Fact]
    public void 管线不释放外部传入的BackgroundResolver()
    {
        using var externalResolver = new TrackingBackgroundResolver();
        using (var pipeline = new SkiaRenderPipeline(backgroundResolver: externalResolver))
        {
            Assert.Equal(0, externalResolver.DisposeCount);
        }

        Assert.Equal(0, externalResolver.DisposeCount);
    }

    [Fact]
    public void 管线DisposeCore异常隔离确保几何与设备生命周期必然释放且标记已释放()
    {
        using var innerDevice = new CpuSkiaRenderDevice();
        var throwingDevice = new ThrowingDisposeDevice(innerDevice);
        var layoutEngine = new SkiaTextLayoutEngine();
        var pipeline = new SkiaRenderPipeline(
            layoutEngine: layoutEngine,
            backgroundResolver: null,
            surfaceBudget: null,
            imageEffects: null,
            ownsLayoutEngine: true,
            device: throwingDevice,
            ownsDevice: true);

        // 注入一个会抛出异常的自有 BackgroundResolver，验证后续 _geometry 与 _deviceLifetime 仍必被释放。
        var resolverFailure = new InvalidOperationException("resolver dispose fault");
        typeof(SkiaRenderPipeline)
            .GetField("_backgroundResolver", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(pipeline, new ThrowingBackgroundResolver(resolverFailure));

        var error = Assert.Throws<InvalidOperationException>(() => pipeline.Dispose());
        Assert.Same(resolverFailure, error);
        Assert.True(throwingDevice.DisposeCalled);
        Assert.Throws<ObjectDisposedException>(() => layoutEngine.Shape("test", TextLayoutOptions.Default));

        // 再次 Dispose 应因 _disposed == true 保持幂等，不再抛异常。
        pipeline.Dispose();
    }

    private sealed class TrackingBackgroundResolver : IBackgroundFrameSourceResolver
    {
        public int DisposeCount { get; private set; }
        public IBackgroundFrameSource? Resolve(BackgroundSource background) => null;
        public void Dispose() => DisposeCount++;
    }

    private sealed class ThrowingBackgroundResolver(Exception error) : IBackgroundFrameSourceResolver
    {
        public IBackgroundFrameSource? Resolve(BackgroundSource background) => null;
        public void Dispose() => throw error;
    }

    private sealed class ThrowingDisposeDevice(ISkiaRenderDevice inner) : ISkiaRenderDevice
    {
        public bool DisposeCalled { get; private set; }
        public object SynchronizationRoot => inner.SynchronizationRoot;
        public SkiaRenderDeviceInfo Info => inner.Info;
        public PreviewDeviceIdentity PreviewIdentity => inner.PreviewIdentity;
        public SKSurface CreateSurface(SKImageInfo imageInfo) => inner.CreateSurface(imageInfo);
        public void FlushAndSubmit(SKSurface surface) => inner.FlushAndSubmit(surface);
        public SKImage? RasterizeImage(SKImage image) => inner.RasterizeImage(image);
        public bool ReadPixels(SKSurface surface, SKImageInfo imageInfo, IntPtr destination, int rowBytes)
            => inner.ReadPixels(surface, imageInfo, destination, rowBytes);
        public bool ReadPixels(SKImage image, SKImageInfo imageInfo, IntPtr destination, int rowBytes)
            => inner.ReadPixels(image, imageInfo, destination, rowBytes);
        public void Dispose()
        {
            DisposeCalled = true;
            throw new InvalidOperationException("device dispose fault");
        }
    }
}
