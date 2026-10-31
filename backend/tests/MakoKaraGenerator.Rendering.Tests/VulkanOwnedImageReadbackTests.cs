using System.Runtime.InteropServices;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class VulkanOwnedImageReadbackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GraphDispatchesOwnedSnapshotsAndBorrowedInputsSeparately(bool owned)
    {
        using var device = new RecordingDevice();
        using var srgb = SKColorSpace.CreateSrgb();
        using var native = device.CreateSurface(new SKImageInfo(2, 2, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
        native.Canvas.Clear(SKColors.Red);
        using var image = native.Snapshot();
        var descriptor = new GraphSurfaceDescriptor(new(0, 0, 2, 2), GraphCoordinateSpace.Canvas, SceneMatrix.Identity, 1, 1);
        using var surface = owned ? SkiaGraphSurface.Own(image, descriptor, ShaderSurfaceKind.Color, device)
            : SkiaGraphSurface.Borrow(image, descriptor, ShaderSurfaceKind.Color, device);
        var definition = new ImageGraphDefinition("readback-routing", ImageGraphDefinition.CurrentVersion,
            [new("target", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)],
            [], [], GraphReference.Input("target"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ImageOnly, GraphCompositeMode.SourceOver);
        using var graph = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [], []), device);
        var frame = new ImageGraphFrameContext(0, 2, 2, descriptor.Bounds, 1, 1, ParameterValues.Empty, false);
        using var result = graph.Execute(frame, new Dictionary<string, SkiaGraphInput> { ["target"] = new(surface, GraphInputSource.TargetColor) });
        Assert.Equal(owned ? 1 : 0, device.OwnedReads);
        Assert.Equal(owned ? 0 : 1, device.BorrowedReads);
    }

    [Fact]
    public void ProvenanceRequiresLiveOwnedSnapshotAndMatchingDevice()
    {
        using var device = SkiaRenderDeviceFactory.Create(new(SkiaRenderBackend.Cpu));
        using var other = SkiaRenderDeviceFactory.Create(new(SkiaRenderBackend.Cpu));
        using var srgb = SKColorSpace.CreateSrgb();
        using var native = device.CreateSurface(new SKImageInfo(2, 2, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
        using var image = native.Snapshot();
        var descriptor = new GraphSurfaceDescriptor(new(0, 0, 2, 2), GraphCoordinateSpace.Canvas, SceneMatrix.Identity, 1, 1);
        using var borrowed = SkiaGraphSurface.Borrow(image, descriptor, ShaderSurfaceKind.Color, device);
        Assert.False(borrowed.HasOwnedImageOn(device));
        using var owned = SkiaGraphSurface.Own(image, descriptor, ShaderSurfaceKind.Color, device);
        using var alias = owned.BorrowAs(ShaderSurfaceKind.Coverage);
        Assert.True(owned.HasOwnedImageOn(device));
        Assert.True(alias.HasOwnedImageOn(device));
        Assert.False(alias.HasOwnedImageOn(other));
        alias.Dispose();
        Assert.False(alias.HasOwnedImageOn(device));
        Assert.True(owned.HasOwnedImageOn(device));
        using var liveAlias = owned.BorrowAs(ShaderSurfaceKind.Coverage);
        owned.Dispose();
        Assert.False(liveAlias.HasOwnedImageOn(device));
    }

    [Fact]
    public void OwnedSrgbSnapshotMatchesRasterReadbackAfterSurfaceRelease()
    {
        if (!GpuTestsEnabled()) return;
        using var device = SkiaRenderDeviceFactory.Create(new(SkiaRenderBackend.Vulkan));
        using var srgb = SKColorSpace.CreateSrgb();
        using var image = Snapshot(device, srgb);
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        int rowBytes = info.RowBytes + 16;
        byte[] expected = Read(device, image, info, rowBytes, owned: false, expectedSuccess: true);
        byte[] actual = Read(device, image, info, rowBytes, owned: true, expectedSuccess: true);
        Assert.Equal(expected, actual);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, actual.AsSpan(0, 4).ToArray());
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, actual.AsSpan(3 * rowBytes, 4).ToArray());
        for (int y = 0; y < info.Height; y++)
            Assert.All(actual.Skip(y * rowBytes + info.RowBytes).Take(16), value => Assert.Equal(0xA5, value));

        Assert.Equal(Read(device, image, info, info.RowBytes - 1, owned: false, expectedSuccess: false),
            Read(device, image, info, info.RowBytes - 1, owned: true, expectedSuccess: false));
    }

    [Fact]
    public void UnsupportedInputsRetainRequestedConversionAndRasterBehavior()
    {
        if (!GpuTestsEnabled()) return;
        using var device = SkiaRenderDeviceFactory.Create(new(SkiaRenderBackend.Vulkan));
        using var srgb = SKColorSpace.CreateSrgb();
        using var linear = SKColorSpace.CreateSrgbLinear();
        using var srgbImage = Snapshot(device, srgb);
        using var linearImage = Snapshot(device, linear);
        using var untaggedImage = Snapshot(device, null);
        using var rgbaImage = Snapshot(device, srgb, SKColorType.Rgba8888);
        using var opaqueImage = Snapshot(device, srgb, alphaType: SKAlphaType.Opaque);
        SKImageInfo[] destinations =
        [
            new(8, 4, SKColorType.Bgra8888, SKAlphaType.Premul, linear),
            new(8, 4, SKColorType.Rgba8888, SKAlphaType.Premul),
            new(8, 4, SKColorType.Bgra8888, SKAlphaType.Unpremul),
            new(7, 4, SKColorType.Bgra8888, SKAlphaType.Premul),
        ];
        foreach (SKImageInfo info in destinations)
            Assert.Equal(Read(device, srgbImage, info, info.RowBytes, false, true),
                Read(device, srgbImage, info, info.RowBytes, true, true));
        var original = new SKImageInfo(8, 4, SKColorType.Bgra8888, SKAlphaType.Premul);
        foreach (SKImage image in new[] { linearImage, untaggedImage, rgbaImage, opaqueImage })
            Assert.Equal(Read(device, image, original, original.RowBytes, false, true),
                Read(device, image, original, original.RowBytes, true, true));

    }

    [Fact]
    public void DisposedDeviceRejectsReadWithoutLiveGpuResources()
    {
        if (!GpuTestsEnabled()) return;
        using var device = SkiaRenderDeviceFactory.Create(new(SkiaRenderBackend.Vulkan));
        using var cpu = SkiaRenderDeviceFactory.Create(new(SkiaRenderBackend.Cpu));
        using var srgb = SKColorSpace.CreateSrgb();
        using var image = Snapshot(cpu, srgb);
        device.Dispose();
        var info = new SKImageInfo(8, 4, SKColorType.Bgra8888, SKAlphaType.Premul);
        Assert.Throws<ObjectDisposedException>(() => Read(device, image, info, info.RowBytes, true, true));
    }

    [Fact]
    public void DeviceLeaseKeepsGpuSnapshotAliveUntilOwnerReleasesIt()
    {
        if (!GpuTestsEnabled()) return;
        using var device = SkiaRenderDeviceFactory.Create(new(SkiaRenderBackend.Vulkan));
        using var lifetime = new SkiaRenderDeviceLifetime(device);
        using var srgb = SKColorSpace.CreateSrgb();
        var descriptor = new GraphSurfaceDescriptor(new(0, 0, 8, 4), GraphCoordinateSpace.Canvas, SceneMatrix.Identity, 1, 1);
        using var image = SkiaGraphSurface.Own(Snapshot(device, srgb), descriptor, ShaderSurfaceKind.Color, device,
            releaseDeviceLease: lifetime.Acquire());
        lifetime.Dispose();
        var info = new SKImageInfo(8, 4, SKColorType.Bgra8888, SKAlphaType.Premul);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Read(device, image.Image, info, info.RowBytes, true, true).AsSpan(0, 4).ToArray());
        image.Dispose();
        using var cpu = SkiaRenderDeviceFactory.Create(new(SkiaRenderBackend.Cpu));
        using var cpuImage = Snapshot(cpu, srgb);
        Assert.Throws<ObjectDisposedException>(() => Read(device, cpuImage, info, info.RowBytes, true, true));
    }

    private static SKImage Snapshot(ISkiaRenderDevice device, SKColorSpace? colorSpace,
        SKColorType colorType = SKColorType.Bgra8888, SKAlphaType alphaType = SKAlphaType.Premul)
    {
        using var surface = device.CreateSurface(new SKImageInfo(8, 4, colorType, alphaType, colorSpace));
        surface.Canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint();
        byte[] alpha = [0, 1, 127, 254, 255];
        for (int x = 0; x < 8; x++)
        {
            paint.Color = new SKColor((byte)(31 * x), (byte)(255 - 27 * x), (byte)(19 * x), alpha[x % alpha.Length]);
            surface.Canvas.DrawRect(x, 0, 1, 4, paint);
        }
        paint.Color = SKColors.Red;
        surface.Canvas.DrawRect(0, 0, 1, 1, paint);
        paint.Color = SKColors.Blue;
        surface.Canvas.DrawRect(0, 3, 1, 1, paint);
        device.FlushAndSubmit(surface);
        return surface.Snapshot();
    }

    private static byte[] Read(ISkiaRenderDevice device, SKImage image, SKImageInfo info, int rowBytes, bool owned, bool expectedSuccess)
    {
        byte[] result = Enumerable.Repeat((byte)0xA5, Math.Max(info.RowBytes, rowBytes) * info.Height).ToArray();
        var pinned = GCHandle.Alloc(result, GCHandleType.Pinned);
        try
        {
            bool success = owned ? device.ReadOwnedImagePixels(image, info, pinned.AddrOfPinnedObject(), rowBytes)
                : device.ReadPixels(image, info, pinned.AddrOfPinnedObject(), rowBytes);
            Assert.Equal(expectedSuccess, success);
            return result;
        }
        finally { pinned.Free(); }
    }

    private static bool GpuTestsEnabled() => Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") == "1";

    private sealed class RecordingDevice : ISkiaRenderDevice
    {
        private readonly CpuSkiaRenderDevice _inner = new();
        public int OwnedReads { get; private set; }
        public int BorrowedReads { get; private set; }
        public SkiaRenderDeviceInfo Info => _inner.Info;
        public SKSurface CreateSurface(SKImageInfo info) => _inner.CreateSurface(info);
        public void FlushAndSubmit(SKSurface surface) => _inner.FlushAndSubmit(surface);
        public SKImage? RasterizeImage(SKImage image) => _inner.RasterizeImage(image);
        public bool ReadPixels(SKSurface surface, SKImageInfo info, IntPtr destination, int rowBytes)
            => _inner.ReadPixels(surface, info, destination, rowBytes);
        public bool ReadPixels(SKImage image, SKImageInfo info, IntPtr destination, int rowBytes)
        {
            BorrowedReads++;
            return _inner.ReadPixels(image, info, destination, rowBytes);
        }
        public bool ReadOwnedImagePixels(SKImage image, SKImageInfo info, IntPtr destination, int rowBytes)
        {
            OwnedReads++;
            return _inner.ReadPixels(image, info, destination, rowBytes);
        }
        public void Dispose() => _inner.Dispose();
    }
}
