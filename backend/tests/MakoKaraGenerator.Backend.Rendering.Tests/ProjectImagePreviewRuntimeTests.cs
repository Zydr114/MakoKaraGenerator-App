using MakoKaraGenerator.Scripting.Compilation;
using System.Security.Cryptography;
using System.Text;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Backend.Rendering.Tests;

public sealed class ProjectImagePreviewRuntimeTests
{
    [Theory]
    [InlineData(false, false, BackgroundFit.Contain)]
    [InlineData(false, false, BackgroundFit.Cover)]
    [InlineData(false, true, BackgroundFit.Original)]
    [InlineData(true, false, BackgroundFit.Contain)]
    [InlineData(true, false, BackgroundFit.Cover)]
    [InlineData(true, true, BackgroundFit.Original)]
    public async Task 固定图片静态及包预览与原Runtime同帧完整像素一致且独立会话不互相释放(
        bool package, bool transparent, BackgroundFit fit)
    {
        using TemporaryDirectory temp = new(); using FfmpegVideoFrameProvider videos = new();
        byte[] imageBytes = Image(); string path = temp.FilePath("background.png");
        File.WriteAllBytes(path, imageBytes);
        ProjectSnapshot source = (package ? PackageRuntimeFixture.Create("scale") : StaticSnapshotFixture.Create()) with
        {
            Background = BackgroundSource.ForImage(path, fit) with
            { Media = new(Convert.ToHexStringLower(SHA256.HashData(imageBytes)), imageBytes.Length, "background.png") },
        };
        var decoded = ProjectImagePreviewSnapshotCodec.Decode(ProjectImagePreviewSnapshotCodec.Encode(source, imageBytes));
        string references = Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0");
        var originalFactory = new ProjectRenderSessionFactory(new ScriptCompiler(references, temp.FilePath("original-cache")), videos, ReplayReason.Initial, renderBackend: package ? "vulkan" : null);
        List<CountingResolver> resolvers = [];
        var fixedFactory = new ProjectRenderSessionFactory(new ScriptCompiler(references, temp.FilePath("fixed-cache")), videos, ReplayReason.Initial, () =>
        {
            var resolver = new CountingResolver(new MemoryImageBackgroundFrameSourceResolver(decoded.LogicalPath, decoded.ImageBytes));
            resolvers.Add(resolver); return resolver;
        }, renderBackend: package ? "vulkan" : null);
        await using var original = await originalFactory.CreateAsync(source);
        await using var actual = await fixedFactory.CreateAsync(decoded.Snapshot);
        await using var survivor = await fixedFactory.CreateAsync(decoded.Snapshot);
        ProjectSnapshot noImage = source with { Background = BackgroundSource.Color };
        await using var counterfactual = await originalFactory.CreateAsync(noImage);
        foreach (long time in new long[] { 0, 100_000, 300_000, 100_000 })
        {
            using var expectedFrame = await original.RenderAsync(Request(source, time, transparent));
            using var actualFrame = await actual.RenderAsync(Request(decoded.Snapshot, time, transparent));
            using var plainFrame = await counterfactual.RenderAsync(Request(noImage, time, transparent));
            Assert.Equal(expectedFrame.PixelMemory.ToArray(), actualFrame.PixelMemory.ToArray());
            if (transparent) Assert.Equal(expectedFrame.PixelMemory.ToArray(), plainFrame.PixelMemory.ToArray());
            else Assert.False(expectedFrame.PixelMemory.Span.SequenceEqual(plainFrame.PixelMemory.Span));
        }
        Assert.Equal(2, resolvers.Count);
        await actual.DisposeAsync(); Assert.Equal(1, resolvers[0].Disposals); Assert.Equal(0, resolvers[1].Disposals);
        File.Delete(path);
        using var remaining = await survivor.RenderAsync(Request(decoded.Snapshot, 300_000, transparent));
        Assert.NotEmpty(remaining.PixelMemory.ToArray());
        await survivor.DisposeAsync(); Assert.Equal(1, resolvers[1].Disposals);
    }

    [Fact]
    public async Task 包编译失败和创建期间取消均释放会话独占resolver且预取消不创建()
    {
        using TemporaryDirectory temp = new(); using FfmpegVideoFrameProvider videos = new();
        string references = Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0");
        List<CountingResolver> created = [];
        var factory = new ProjectRenderSessionFactory(new ScriptCompiler(references, temp.FilePath("cache")), videos, ReplayReason.Initial, () =>
        { var value = new CountingResolver(new BackgroundFrameSourceResolver()); created.Add(value); return value; }, renderBackend: "vulkan");
        ProjectSnapshot snapshot = PackageRuntimeFixture.Create("scale");
        var files = EffectPackageFixture.Files();
        files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes("public sealed class PackEffect { invalid !!! }");
        var broken = EffectPackageContents.Create(EffectPackageFixture.Bytes(EffectPackageFixture.Manifest(files)),
            EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
        var old = Assert.Single(snapshot.Effects.Packages);
        ProjectSnapshot invalid = PackageSnapshotContractFixture.Fingerprint(snapshot with
        {
            Effects = new([broken], [.. snapshot.Effects.Instances.Select(value => value with { Revision = broken.Revision })], snapshot.Effects.Budgets),
        });
        Assert.NotEqual(old.Revision, broken.Revision);
        await Assert.ThrowsAsync<ProjectEffectCompilationException>(() => factory.CreateAsync(invalid).AsTask());
        Assert.Equal(1, Assert.Single(created).Disposals);
        created.Clear();
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.CreateAsync(snapshot, cancellation.Token).AsTask());
        Assert.Empty(created);
        using CancellationTokenSource midway = new();
        var cancellingFactory = new ProjectRenderSessionFactory(new ScriptCompiler(references, temp.FilePath("cache")), videos, ReplayReason.Initial, () =>
        { var value = new CountingResolver(new BackgroundFrameSourceResolver()); created.Add(value); midway.Cancel(); return value; }, renderBackend: "vulkan");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancellingFactory.CreateAsync(snapshot, midway.Token).AsTask());
        Assert.Equal(1, Assert.Single(created).Disposals);
    }

    [Fact]
    public async Task 损坏图片透明会话在创建时失败且不依赖render是否绘制背景()
    {
        using TemporaryDirectory temp = new(); using FfmpegVideoFrameProvider videos = new();
        byte[] bytes = [1, 2, 3]; string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var snapshot = StaticSnapshotFixture.Create() with
        { Background = BackgroundSource.ForImage("data/media/" + hash + ".media") with { Media = new(hash, bytes.Length, "bad.png") } };
        var factory = new ProjectRenderSessionFactory(new FixedScriptArtifactProvider(new ScriptCompilationIdentity("static-unused", "static-unused", new string('0', 64), new string('0', 64), "static-unused", "static-unused"), []), videos, ReplayReason.Initial,
            () => new MemoryImageBackgroundFrameSourceResolver(snapshot.Background.Path!, bytes));
        var error = await Assert.ThrowsAsync<ImageBackgroundDecodeException>(() => factory.CreateAsync(snapshot).AsTask());
        Assert.Equal("PackageImageDecodeFailed", error.Code);
        Assert.False(Directory.Exists(temp.FilePath("cache")));
    }

    private static RenderRequest Request(ProjectSnapshot snapshot, long time, bool transparent)
        => RenderRequest.At(snapshot, time) with { Target = new() { PixelWidth = 320, PixelHeight = 180 }, TransparentBackground = transparent };

    private static byte[] Image()
    {
        using var bitmap = new SKBitmap(31, 19);
        using (SKCanvas canvas = new(bitmap))
        {
            canvas.Clear(new(220, 30, 80, 100));
            using SKPaint paint = new() { Color = new(20, 210, 140, 230) };
            canvas.DrawRect(0, 0, 12, 19, paint);
        }
        using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100); return encoded.ToArray();
    }

    private sealed class CountingResolver(IBackgroundFrameSourceResolver inner) : IBackgroundFrameSourceResolver
    {
        public int Disposals { get; private set; }
        public IBackgroundFrameSource? Resolve(BackgroundSource background) => inner.Resolve(background);
        public IBackgroundFrameSource? Resolve(BackgroundSource background, CancellationToken cancellationToken) => inner.Resolve(background, cancellationToken);
        public void Dispose() { Disposals++; inner.Dispose(); }
    }
}
