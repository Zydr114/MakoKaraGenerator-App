using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Runtime.Hosting.Tests;

public sealed class SvgRuntimePackageIntegrationTests
{
    [Theory]
    [Trait("Category", "GpuSmoke")]
    [InlineData("path", false)] [InlineData("path", true)]
    [InlineData("gradient-clip-use", false)] [InlineData("gradient-clip-use", true)]
    [InlineData("image", false)] [InlineData("image", true)]
    [InlineData("filter", false)] [InlineData("filter", true)]
    public async Task 四类真实v3SVG包由Declare到RuntimeFactory及Vulkan产生有限像素(string kind, bool atlas)
    {
        if (!SvgRuntimePackageFixture.GpuRequested) return;
        using TemporaryDirectory temp = new(); using FfmpegVideoFrameProvider videos = new();
        var package = SvgRuntimePackageFixture.Package(kind, atlas);
        var snapshot = SvgRuntimePackageFixture.Snapshot(package);
        var compiler = Compiler(temp.FilePath("cache"));
        await using var session = await Factory(compiler, videos).CreateAsync(snapshot);
        Assert.True(compiler.CompilationCount > 0);
        using var frame = await session.RenderAsync(SvgRuntimePackageFixture.Request(snapshot, 0));
        AssertPixels(frame, kind, 0);
        Assert.Empty(frame.ImageEffectDiagnostics); Assert.Empty(frame.MaterialDiagnostics);
        Assert.DoesNotContain(frame.DiagnosticSnapshot.ReadAll(), entry => entry.Diagnostic.Diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [Trait("Category", "GpuSmoke")]
    [InlineData(false)] [InlineData(true)]
    public async Task 真实固定SVG在乱序重复导出重开和取消恢复后逐字节一致(bool atlas)
    {
        if (!SvgRuntimePackageFixture.GpuRequested) return;
        using TemporaryDirectory temp = new(); using FfmpegVideoFrameProvider videos = new();
        var snapshot = SvgRuntimePackageFixture.Snapshot(SvgRuntimePackageFixture.Package("gradient-clip-use", atlas));
        var compiler = Compiler(temp.FilePath("cache")); var factory = Factory(compiler, videos);
        await using var session = await factory.CreateAsync(snapshot);
        int compilationCount = compiler.CompilationCount;
        Dictionary<long, byte[]> expected = [];
        foreach (long time in new long[] { 1_250_000, 600_250_000, 0, 180_125_000, 1_250_000, 0, 600_250_000 })
        {
            using var actual = await session.RenderAsync(SvgRuntimePackageFixture.Request(snapshot, time));
            AssertPixels(actual, "gradient-clip-use", time);
            if (expected.TryGetValue(time, out var previous)) Assert.Equal(previous, actual.PixelMemory.ToArray());
            else expected.Add(time, actual.PixelMemory.ToArray());
        }
        Assert.NotEqual(expected[0], expected[180_125_000]);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        foreach (bool requestToken in new[] { false, true })
        {
            var request = SvgRuntimePackageFixture.Request(snapshot, 1_250_000);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RenderAsync(
                requestToken ? request with { CancellationToken = cancelled.Token } : request,
                requestToken ? default : cancelled.Token).AsTask());
            using var recovered = await session.RenderAsync(SvgRuntimePackageFixture.Request(snapshot, 1_250_000));
            Assert.Equal(expected[1_250_000], recovered.PixelMemory.ToArray());
        }
        await using var reopened = await Factory(compiler, videos, reason: ReplayReason.Export).CreateAsync(snapshot);
        using var direct = await reopened.RenderAsync(SvgRuntimePackageFixture.Request(snapshot, 1_250_000));
        Assert.Equal(expected[1_250_000], direct.PixelMemory.ToArray());
        Assert.Equal(compilationCount, compiler.CompilationCount);
        await session.DisposeAsync();
        using var surviving = await reopened.RenderAsync(SvgRuntimePackageFixture.Request(snapshot, 1_250_000));
        Assert.Equal(expected[1_250_000], surviving.PixelMemory.ToArray());
    }

    [Theory]
    [Trait("Category", "GpuSmoke")]
    [InlineData(false)] [InlineData(true)]
    public async Task 相同包逻辑身份的SVG字节修订各自持有资源关闭旧会话不破坏新会话(bool atlas)
    {
        if (!SvgRuntimePackageFixture.GpuRequested) return;
        using TemporaryDirectory temp = new(); using FfmpegVideoFrameProvider videos = new();
        var oldPackage = SvgRuntimePackageFixture.Package("path", atlas);
        var newPackage = SvgRuntimePackageFixture.Package("path", atlas,
            "<path d='M10 10 H90 V90 H10 Z' fill='#20b0e0' stroke='#2040e0' stroke-width='2'/>");
        Assert.Equal(oldPackage.PackageId, newPackage.PackageId); Assert.NotEqual(oldPackage.Revision, newPackage.Revision);
        var oldSnapshot = SvgRuntimePackageFixture.Snapshot(oldPackage); var newSnapshot = SvgRuntimePackageFixture.Snapshot(newPackage);
        var factory = Factory(Compiler(temp.FilePath("cache")), videos);
        await using var oldSession = await factory.CreateAsync(oldSnapshot);
        await using var newSession = await factory.CreateAsync(newSnapshot);
        using var old = await oldSession.RenderAsync(SvgRuntimePackageFixture.Request(oldSnapshot, 0));
        using var current = await newSession.RenderAsync(SvgRuntimePackageFixture.Request(newSnapshot, 0));
        PixelAt(old, 0, .5, .5, 224, 64, 32, 255); PixelAt(current, 0, .5, .5, 32, 176, 224, 255);
        Assert.NotEqual(old.PixelMemory.ToArray(), current.PixelMemory.ToArray());
        var mismatch = await Assert.ThrowsAsync<PreparedFrameException>(() => oldSession.RenderAsync(SvgRuntimePackageFixture.Request(newSnapshot, 0)).AsTask());
        Assert.Equal("RenderSessionInputsMismatch", mismatch.Code);
        await oldSession.DisposeAsync();
        using var surviving = await newSession.RenderAsync(SvgRuntimePackageFixture.Request(newSnapshot, 0));
        Assert.Equal(current.PixelMemory.ToArray(), surviving.PixelMemory.ToArray());
    }

    [Theory]
    [Trait("Category", "GpuSmoke")]
    [InlineData(false)] [InlineData(true)]
    public async Task 生产整体Tint只乘一次且native失败属于真实作者并可恢复同会话(bool atlas)
    {
        if (!SvgRuntimePackageFixture.GpuRequested) return;
        using TemporaryDirectory temp = new(); using FfmpegVideoFrameProvider videos = new();
        var package = SvgRuntimePackageFixture.Package("overlap", atlas);
        var snapshot = SvgRuntimePackageFixture.Snapshot(package, new { alpha = .5, red = .5, failAtUs = SvgRuntimePackageFixture.FailureTimeUs });
        var factory = Factory(Compiler(temp.FilePath("cache")), videos);
        await using var session = await factory.CreateAsync(snapshot);
        using var before = await session.RenderAsync(SvgRuntimePackageFixture.Request(snapshot, 0));
        foreach (double x in new[] { .15, .5, .85 }) PixelAt(before, 0, x, .5, 100, 100, 50, 128);
        var error = await Assert.ThrowsAsync<EffectFailureException>(() => session.RenderAsync(SvgRuntimePackageFixture.Request(snapshot,
            SvgRuntimePackageFixture.FailureTimeUs)).AsTask());
        Assert.Equal("UnsupportedNativeOverlay", error.Code); Assert.Equal("RenderOverlays", error.Stage);
        Assert.Equal(SvgRuntimePackageFixture.FailureTimeUs, error.TimeUs); Assert.Equal(new EffectInstanceId(SvgRuntimePackageFixture.InstanceId), error.InstanceId);
        Assert.False(string.IsNullOrWhiteSpace(error.ArtifactHash));
        Assert.Equal(new EffectPackageIdentity(package.PackageId, package.Revision, SvgRuntimePackageFixture.ExportId), error.PackageIdentity);
        using var after = await session.RenderAsync(SvgRuntimePackageFixture.Request(snapshot, 0));
        Assert.Equal(before.PixelMemory.ToArray(), after.PixelMemory.ToArray());
        await using var reopened = await factory.CreateAsync(snapshot);
        using var fresh = await reopened.RenderAsync(SvgRuntimePackageFixture.Request(snapshot, 0));
        Assert.Equal(before.PixelMemory.ToArray(), fresh.PixelMemory.ToArray());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 预取消创建不发布缓存且CPU生产拒绝真实SVG包保留未启用路径(bool atlas)
    {
        using TemporaryDirectory temp = new(); using FfmpegVideoFrameProvider videos = new();
        var package = SvgRuntimePackageFixture.Package("path", atlas); var snapshot = SvgRuntimePackageFixture.Snapshot(package);
        string cache = temp.FilePath("cancelled-cache"); var compiler = Compiler(cache);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Factory(compiler, videos).CreateAsync(snapshot, cancelled.Token).AsTask());
        Assert.Equal(0, compiler.CompilationCount); Assert.False(Directory.Exists(cache));
        var error = await Assert.ThrowsAsync<EffectPackageException>(() => Factory(compiler, videos, backend: "cpu").CreateAsync(snapshot).AsTask());
        Assert.Equal("PackageBackendUnsupported", error.Code); Assert.Equal(0, compiler.CompilationCount);
        var disabled = SvgRuntimePackageFixture.Snapshot(package, enabled: false);
        await using var cpu = await Factory(compiler, videos, backend: "cpu").CreateAsync(disabled);
        using var frame = await cpu.RenderAsync(SvgRuntimePackageFixture.Request(disabled, 0));
        Assert.All(frame.PixelMemory.ToArray(), value => Assert.Equal(0, value)); Assert.Equal(0, compiler.CompilationCount);
    }

    [Theory]
    [Trait("Category", "GpuSmoke")]
    [InlineData(false)] [InlineData(true)]
    public async Task 固定SVG准备像素超预算失败后同组合根可打开健康新会话(bool atlas)
    {
        if (!SvgRuntimePackageFixture.GpuRequested) return;
        using TemporaryDirectory temp = new(); using FfmpegVideoFrameProvider videos = new();
        string kind = atlas ? "path" : "image";
        var package = SvgRuntimePackageFixture.Package(kind, atlas);
        var factory = Factory(Compiler(temp.FilePath("cache")), videos);
        // Vector passes the existing 16-byte role:image and then rejects its distinct data image.
        // Atlas has no ordinary image; its first complete content raster exceeds this same budget.
        var rejected = SvgRuntimePackageFixture.Snapshot(package, surfaceMemoryBytes: 24);
        var error = await Assert.ThrowsAsync<ImageSurfaceBudgetException>(() => factory.CreateAsync(rejected).AsTask());
        Assert.Equal(atlas ? "PrepareSvgAtlasContent" : "PrepareSvgEmbeddedImage", error.Operation);
        var healthy = SvgRuntimePackageFixture.Snapshot(package);
        await using var recovered = await factory.CreateAsync(healthy);
        using var frame = await recovered.RenderAsync(SvgRuntimePackageFixture.Request(healthy, 0)); AssertPixels(frame, kind, 0);
    }

    private static ScriptCompiler Compiler(string cache)
        => new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), cache);
    private static ProjectRenderSessionFactory Factory(ScriptCompiler compiler, IVideoFrameProvider videos,
        string backend = "vulkan", ReplayReason reason = ReplayReason.Initial)
        => new(compiler, videos, reason, renderBackend: backend);

    private static void AssertPixels(RenderFrame frame, string kind, long timeUs)
    {
        Assert.Equal(320, frame.Width); Assert.Equal(180, frame.Height);
        Assert.True(frame.TryGetPixel(5, 5, out var outside)); Assert.Equal(0, outside.Alpha);
        Assert.True(frame.PixelMemory.ToArray().Where((_, index) => index % 4 == 3).Count(alpha => alpha > 0) > 100);
        if (kind == "path") { PixelAt(frame, timeUs, .5, .5, 224, 64, 32, 255); PixelAt(frame, timeUs, .02, .02, 0, 0, 0, 0); }
        if (kind == "gradient-clip-use")
        {
            PixelAt(frame, timeUs, .02, .02, 0, 0, 0, 0);
            var center = PixelAt(frame, timeUs, .5, .5); Assert.InRange(center.Alpha, 185, 195);
            var left = PixelAt(frame, timeUs, .3, .5); var right = PixelAt(frame, timeUs, .7, .5);
            Assert.True(left.Red > right.Red); Assert.True(left.Blue < right.Blue);
        }
        if (kind == "image") { PixelAt(frame, timeUs, .25, .5, 240, 120, 40, 255); PixelAt(frame, timeUs, .75, .5, 20, 180, 80, 255); }
        if (kind == "filter")
        {
            Assert.True(PixelAt(frame, timeUs, .5, .5).Alpha > 240);
            Assert.True(PixelAt(frame, timeUs, .22, .5).Alpha > 0);
        }
    }
    private static RgbaColor PixelAt(RenderFrame frame, long timeUs, double x, double y)
    {
        var point = SvgRuntimePackageFixture.Placement(timeUs).Map(new(x, y));
        Assert.True(frame.TryGetPixel((int)point.X, (int)point.Y, out var pixel)); return pixel;
    }
    private static void PixelAt(RenderFrame frame, long timeUs, double x, double y, int red, int green, int blue, int alpha)
    {
        var pixel = PixelAt(frame, timeUs, x, y);
        Assert.InRange(Math.Abs(pixel.Red * pixel.Alpha - red * alpha), 0, 510);
        Assert.InRange(Math.Abs(pixel.Green * pixel.Alpha - green * alpha), 0, 510);
        Assert.InRange(Math.Abs(pixel.Blue * pixel.Alpha - blue * alpha), 0, 510);
        Assert.InRange(Math.Abs(pixel.Alpha - alpha), 0, 2);
    }
}
