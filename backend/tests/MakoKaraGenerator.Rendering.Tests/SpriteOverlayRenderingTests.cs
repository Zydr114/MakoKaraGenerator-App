using System.Runtime.InteropServices;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class SpriteOverlayRenderingTests
{
    private static readonly Fx.ImageHandle Handle = new Fx.ResourceCatalog("sprites", "sha256:" + new string('a', 64), [], [], ["atlas.png"]).GetImage("atlas.png");
    private static SKImage Atlas()
    {
        using var color = SKColorSpace.CreateSrgb();
        using SKBitmap bitmap = new(new SKImageInfo(8, 4, SKColorType.Bgra8888, SKAlphaType.Premul, color));
        using SKCanvas canvas = new(bitmap); canvas.Clear(new SKColor(200, 100, 50, 128));
        using SKPaint paint = new() { Color = new(20, 40, 240, 255) }; canvas.DrawRect(4, 0, 4, 4, paint);
        return SKImage.FromBitmap(bitmap);
    }
    [Theory]
    [InlineData(320, 180)] [InlineData(640, 360)] [InlineData(640, 180)]
    public void 裁片旋转镜像与图片和Tint透明度各乘一次且三种密度一致(int width, int height)
    {
        using var image = Atlas(); using var input = SkiaGraphSurface.Borrow(image, new(new(0, 0, 8, 4), GraphCoordinateSpace.Canvas, SceneMatrix.Identity, 1, 1), ShaderSurfaceKind.Color);
        using SceneImageEffectResources resources = new([], null, [], null, [KeyValuePair.Create(Handle, input)]);
        using SkiaRenderPipeline renderer = new(null, null, null, resources); var snapshot = VisualTestFixture.Snapshot();
        var target = new RenderTarget { PixelWidth = width, PixelHeight = height };
        PreparedRenderFrame basis = new(snapshot, 18_001, Fx.OutputMode.Transparent, target, renderer.GetSceneFrame(snapshot, 18_001), []);
        Fx.Sprite left = new(Handle, new(40, 0, 0, 20, 40, 20), new(.5, 1, .5, .5), new(0, 0, .5, 1));
        Fx.Sprite rotated = new(Handle, new(0, 20, -20, 0, 120, 20), new(1, 1, 1, 1), new(.5, 0, 1, 1));
        Fx.Sprite mirror = new(Handle, new(-20, 0, 0, 20, 160, 20), new(1, 1, 1, 1), new(.5, 0, 1, 1));
        var prepared = basis.WithOverlayOutput([new Fx.SpriteBatch(new("fx"), new("image"), Fx.OverlayLayer.AboveText, [left, rotated, mirror])], []);
        using var frame = renderer.Render(RenderRequest.At(snapshot, basis.TimeUs) with { Target = target, PreparedFrame = prepared, TransparentBackground = true });
        using var original = renderer.Render(RenderRequest.At(snapshot, basis.TimeUs) with { Target = target, PreparedFrame = basis, TransparentBackground = true });
        using var srgb = SKColorSpace.CreateSrgb(); using SKBitmap expected = new(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
        byte[] expectedBytes = original.PixelMemory.ToArray(); Marshal.Copy(expectedBytes, 0, expected.GetPixels(), expectedBytes.Length);
        using (SKCanvas canvas = new(expected))
        {
            canvas.Scale((float)target.ScaleXOf(snapshot.Composition), (float)target.ScaleYOf(snapshot.Composition));
            using SKPaint paint = new() { IsAntialias = true, Color = new(100, 100, 25, 64) }; canvas.DrawRect(new(40, 20, 80, 40), paint);
            paint.Color = new(20, 40, 240, 255); canvas.DrawRect(new(100, 20, 120, 40), paint); canvas.DrawRect(new(140, 20, 160, 40), paint);
        }
        Marshal.Copy(expected.GetPixels(), expectedBytes, 0, expectedBytes.Length); var actualBytes = frame.PixelMemory.ToArray();
        for (int index = 0; index < expectedBytes.Length; index++)
            if (index % 4 == 3) Assert.Equal(expectedBytes[index], actualBytes[index]);
            else Assert.InRange(Math.Abs(expectedBytes[index] - actualBytes[index]), 0, 2);
        Pixel(60, 30, 100, 100, 25, 64); Pixel(110, 30, 20, 40, 240, 255); Pixel(150, 30, 20, 40, 240, 255); Pixel(35, 30, 0, 0, 0, 0);
        void Pixel(int x, int y, int red, int green, int blue, int alpha)
        {
            Assert.True(frame.TryGetPixel((int)(x * target.ScaleXOf(snapshot.Composition)), (int)(y * target.ScaleYOf(snapshot.Composition)), out var pixel));
            Assert.InRange(Math.Abs(pixel.Red - red), 0, 2); Assert.InRange(Math.Abs(pixel.Green - green), 0, 2);
            Assert.InRange(Math.Abs(pixel.Blue - blue), 0, 2); Assert.Equal(alpha, pixel.Alpha);
        }
    }
    [Fact]
    public void 独立设备图片可重复作为精灵输入且源图仍由调用者拥有()
    {
        using var sourceDevice = SkiaRenderDeviceFactory.Create();
        using var srgb = SKColorSpace.CreateSrgb();
        using var source = sourceDevice.CreateSurface(new SKImageInfo(8, 4, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
        source.Canvas.Clear(SKColors.Red); sourceDevice.FlushAndSubmit(source);
        using var image = source.Snapshot();
        using var input = SkiaGraphSurface.Borrow(image, new(new(0, 0, 8, 4), GraphCoordinateSpace.Canvas, SceneMatrix.Identity, 1, 1), ShaderSurfaceKind.Color, sourceDevice);
        using SceneImageEffectResources resources = new([], null, [], null, [KeyValuePair.Create(Handle, input)]);
        using SkiaRenderPipeline renderer = new(null, null, null, resources); var snapshot = VisualTestFixture.Snapshot();
        PreparedRenderFrame basis = new(snapshot, 18_001, Fx.OutputMode.Transparent, VisualTestFixture.Target(snapshot), renderer.GetSceneFrame(snapshot, 18_001), []);
        var sprite = new Fx.Sprite(Handle, new(20, 0, 0, 10, 40, 20), new(1, 1, 1, 1));
        var prepared = basis.WithOverlayOutput([new Fx.SpriteBatch(new("fx"), new("image"), Fx.OverlayLayer.AboveText, [sprite])], []);
        for (int i = 0; i < 3; i++)
        {
            using var frame = renderer.Render(RenderRequest.At(snapshot, basis.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true });
            Assert.True(frame.TryGetPixel(50, 25, out var pixel));
            Assert.Equal(255, pixel.Red); Assert.Equal(255, pixel.Alpha);
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, SkiaTestReadback.Pixel(input.Image, 4, 2));
        }
    }

    [Fact]
    public void 缺少或另修订图片与原生精度失败保留作者来源图和组复制保留来源()
    {
        var snapshot = VisualTestFixture.Snapshot(); using SkiaRenderPipeline renderer = new();
        PreparedRenderFrame frame = new(snapshot, 18_001, Fx.OutputMode.Transparent, VisualTestFixture.Target(snapshot), renderer.GetSceneFrame(snapshot, 18_001), []);
        var package = new Fx.EffectPackageIdentity("sprites", Handle.Identity.Revision, "controller");
        PreparedOverlayOwner owner = new(new("fx"), "artifact", package);
        var sprite = new Fx.Sprite(Handle, new(20, 0, 0, 10, 40, 20), new(1, 1, 1, 1));
        var prepared = frame.WithOverlayOutput([new Fx.SpriteBatch(owner.InstanceId, new("image"), Fx.OverlayLayer.AboveText, [sprite])], [], owners: [owner]);
        var copied = prepared.WithGroupOpacities([new(owner.InstanceId, owner.ArtifactHash, new(prepared.FinalGroupGeometry[0].Target, .5))]).WithImageEffects([]);
        Assert.Equal(prepared.OverlayOwners, copied.OverlayOwners); Assert.Equal(prepared.OverlayBatches, copied.OverlayBatches);
        var missing = Assert.Throws<EffectFailureException>(() => renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true }));
        Assert.Equal("SpriteResourcesRequired", missing.Code); Assert.Equal("RenderOverlays", missing.Stage); Assert.Equal(package, missing.PackageIdentity);
        using var image = Atlas(); using var input = SkiaGraphSurface.Borrow(image, new(new(0, 0, 8, 4), GraphCoordinateSpace.Canvas, SceneMatrix.Identity, 1, 1), ShaderSurfaceKind.Color);
        var other = new Fx.ResourceCatalog("sprites", "sha256:" + new string('b', 64), [], [], ["atlas.png"]).GetImage("atlas.png");
        using SceneImageEffectResources resources = new([], null, [], null, [KeyValuePair.Create(other, input)]);
        using SkiaRenderPipeline wrong = new(null, null, null, resources);
        Assert.Equal("SpriteResourceUnavailable", Assert.Throws<EffectFailureException>(() => wrong.Render(RenderRequest.At(snapshot, frame.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true })).Code);
        foreach (var matrix in new Fx.Affine2[] { new(1e100, 0, 0, 1, 0, 0), new(1e-100, 0, 0, 1, 0, 0) })
        {
            var unsupported = frame.WithOverlayOutput([new Fx.SpriteBatch(owner.InstanceId, new("image"), Fx.OverlayLayer.AboveText, [new(Handle, matrix, default)])], [], owners: [owner]);
            Assert.Equal("UnsupportedNativeOverlay", Assert.Throws<EffectFailureException>(() => wrong.Render(RenderRequest.At(snapshot, frame.TimeUs) with { PreparedFrame = unsupported, TransparentBackground = true })).Code);
        }
    }
    [Fact]
    public void 多精灵借用同一图片只计一次帧存储并在成功失败取消后归零()
    {
        using var image = Atlas(); using var input = SkiaGraphSurface.Borrow(image, new(new(0, 0, 8, 4), GraphCoordinateSpace.Canvas, SceneMatrix.Identity, 1, 1), ShaderSurfaceKind.Color);
        using SceneImageEffectResources resources = new([], null, [], null, [KeyValuePair.Create(Handle, input)]);
        using ImageSurfaceBudgetSession budget = new(new() { MaximumSurfaces = 1, MaximumBytes = 128 });
        using SkiaRenderPipeline renderer = new(null, null, budget, resources); var snapshot = VisualTestFixture.Snapshot();
        PreparedRenderFrame frame = new(snapshot, 18_001, Fx.OutputMode.Transparent, VisualTestFixture.Target(snapshot), renderer.GetSceneFrame(snapshot, 18_001), []);
        var sprite = new Fx.Sprite(Handle, new(20, 0, 0, 10, 40, 20), new(1, 1, 1, 1));
        var prepared = frame.WithOverlayOutput([new Fx.SpriteBatch(new("fx"), new("image"), Fx.OverlayLayer.AboveText, [sprite, sprite])], []);
        using var pixels = renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true });
        Assert.Equal(0, budget.Usage.EstimatedBytes); Assert.Equal(128, budget.Usage.PeakBytes); Assert.Equal(1, budget.Usage.PeakSurfaces);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { PreparedFrame = prepared, CancellationToken = cancellation.Token }));
        Assert.Equal(0, budget.Usage.EstimatedBytes);
        using ImageSurfaceBudgetSession low = new(new() { MaximumBytes = 127 }); using SkiaRenderPipeline limited = new(null, null, low, resources);
        Assert.Throws<ImageSurfaceBudgetException>(() => limited.Render(RenderRequest.At(snapshot, frame.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true }));
        Assert.Equal(0, low.Usage.EstimatedBytes);
    }
}
