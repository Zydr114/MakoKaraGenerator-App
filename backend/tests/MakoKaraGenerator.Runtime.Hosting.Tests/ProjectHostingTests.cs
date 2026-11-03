using MakoKaraGenerator.Scripting.Compilation;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.TestSupport;
using SkiaSharp;

namespace MakoKaraGenerator.Runtime.Hosting.Tests;

public sealed class ProjectHostingTests
{
    [Fact]
    public async Task 原Runtime实际使用包内大CJK字体并参与按字符回退()
    {
        using TemporaryDirectory temp = new();
        var package = PackageFontFixture.Contents();
        var font = package.Files.Single(file => file.Role == "font").Content.AsMemory();
        Assert.True(font.Length > 4 * 1024 * 1024);
        using var fixedFonts = new SkiaTextLayoutEngine([font]);
        Assert.Equal(PackageFontFixture.Family, fixedFonts.ResolveFamilyName(PackageFontFixture.Family));
        var shaped = fixedFonts.Shape("歌词", new() { FamilyName = PackageFontFixture.Family, FontSizePx = 40 });
        Assert.All(shaped.DrawUnits, unit => Assert.Equal(PackageFontFixture.Family, unit.FamilyName));
        var fallback = fixedFonts.Shape("歌词", new() { FamilyName = "DejaVu Sans", FontSizePx = 40 });
        Assert.Equal(0, fallback.MissingGlyphCount);
        Assert.All(fallback.DrawUnits, unit => Assert.StartsWith("Mako", unit.FamilyName));
        var source = Snapshot() with { Effects = new([package]), LyricStyle = Snapshot().LyricStyle with { FontFamilyName = PackageFontFixture.Family } };
        using var lease = BackendProjectRuntimeInputLease.CreateDetached(source);
        var preparation = await lease.PrepareAsync();
        Assert.Null(preparation.Error);
        using var prepared = Assert.IsType<BackendPreparedProjectRuntimeInput>(preparation.Response);
        Assert.Equal(ProjectRuntimeInputCodec.Profile, prepared.SourceProfile);
        ProjectRuntimeInputCodec.ValidateSnapshot(prepared.SourceBytes, source);
        string packageDirectory = Assert.Single(ProjectRuntimeInputCodec.ReadPackageGrants(prepared.SourceBytes)).DirectoryPath;
        using (var retained = prepared.AcquireReference())
        {
            prepared.Dispose();
            Assert.True(Directory.Exists(packageDirectory));
        }
        Assert.False(Directory.Exists(packageDirectory));
        await using var session = await Factory(temp.FilePath("cache")).CreateAsync(source);
        using var expectedRenderer = new SkiaRenderPipeline(fixedFonts);
        var request = RenderRequest.At(source, 300_000);
        using var expected = expectedRenderer.Render(request);
        using var actual = await session.RenderAsync(request);
        Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
        Assert.Contains(actual.PixelMemory.ToArray(), value => value != 0);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 效果同帧测量使用原准备帧且普通渲染像素一致(bool layout)
    {
        using TemporaryDirectory temp = new();
        var snapshot = Snapshot(MixedEffectPackageFixture.Contents());
        var factory = new ProjectRenderSessionFactory(new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), temp.FilePath("cache")), new FfmpegVideoFrameProvider(), ReplayReason.Initial, layoutReference: layout);
        await using var session = await factory.CreateAsync(snapshot);
        var request = RenderRequest.At(snapshot, layout ? 0 : 300_000);
        using var measured = await ((ISceneMeasurementSession)session).MeasureAsync(request, layout);
        using var rendered = await session.RenderAsync(request);
        Assert.Equal(rendered.PixelMemory.ToArray(), measured.Frame.PixelMemory.ToArray());
        Assert.NotEmpty(measured.Scene.Elements);
        Assert.NotEmpty(measured.Scene.Lines);
        if (layout) Assert.All(measured.Scene.Lines, line => Assert.StartsWith("__layout_reference__/", line.Frame.Line!.Id));
    }

    private static ProjectRenderSessionFactory Factory(string cache, ReplayReason reason = ReplayReason.Initial) => new(new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), cache), new FfmpegVideoFrameProvider(), reason);

    private static ProjectSnapshot Snapshot(EffectPackageContents? package = null)
    {
        var line = LyricLine.Create("row", "歌词", new(0, 700_000), TimingStatus.Edited,
            [LyricToken.Create("token", "歌词", new(0, 700_000), TimingStatus.Edited)]);
        var project = Project.Create("独立正式宿主", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 700_000),
            LyricsDocument.Create([line]), lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 40, Stroke = null, Shadow = null });
        if (package is not null) project = project with { Effects = new([package], [new()
        {
            Id = "controller", Name = "固定控制器", PackageId = package.PackageId, Revision = package.Revision, ExportId = "controller",
        }]) };
        return ProjectSnapshot.FromProject(project);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 无GUI调用真实固定包编译原生装配且独立会话重放保持全部帧和诊断(bool transparent)
    {
        using TemporaryDirectory temp = new();
        var package = MixedEffectPackageFixture.Contents(controllerSource: MixedEffectPackageFixture.Source.Replace(
            "foreach (var line", "frame.Diagnostics.Report(DiagnosticSeverity.Warning, \"HostingWarning\", \"完整来源\");\n        foreach (var line", StringComparison.Ordinal));
        var snapshot = Snapshot(package); var factory = Factory(temp.FilePath("cache"));
        await using var preview = await factory.CreateAsync(snapshot);
        await using var export = await Factory(temp.FilePath("cache"), ReplayReason.Export).CreateAsync(snapshot);
        using SkiaRenderPipeline baseline = new();
        var cursor = new RuntimeDiagnosticCursor();
        foreach (long time in new long[] { 0, 100_000, 300_000, 500_000, 100_000, 500_000 })
        {
            var request = RenderRequest.At(snapshot, time) with { TransparentBackground = transparent };
            using var actual = await preview.RenderAsync(request); using var expected = await export.RenderAsync(request);
            Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
            Assert.Equal(expected.DiagnosticSnapshot.ReadAll().Select(value => value.Diagnostic), actual.DiagnosticSnapshot.ReadAll().Select(value => value.Diagnostic));
            Assert.Contains(actual.DiagnosticSnapshot.ReadAll(), value => value.Diagnostic.Diagnostic.Code == "HostingWarning"
                && value.Diagnostic.PackageIdentity == new EffectPackageIdentity(package.PackageId, package.Revision, "controller"));
            using var plain = baseline.Render(RenderRequest.At(Snapshot(), time) with { TransparentBackground = transparent });
            Assert.NotEqual(plain.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
            Assert.NotEmpty(cursor.ReadNew(actual.DiagnosticSnapshot));
        }
        string artifact = Assert.Single(Directory.EnumerateDirectories(temp.FilePath("cache")),
            path => Path.GetFileName(path) != ".locks");
        Assert.Collection(Directory.EnumerateFiles(artifact).Select(Path.GetFileName).Order(StringComparer.Ordinal),
            name => Assert.Equal("build.json", name), name => Assert.Equal("effect.dll", name), name => Assert.Equal("effect.pdb", name));
        await preview.DisposeAsync();
        // Closing one session must leave the other's native programs and replay state alive.
        using var surviving = await export.RenderAsync(RenderRequest.At(snapshot, 500_000) with { TransparentBackground = transparent });
        Assert.NotEmpty(surviving.DiagnosticSnapshot.ReadAll());
    }

    [Fact]
    public async Task 静态路径不创建编译缓存且只接受创建时的快照()
    {
        var snapshot = Snapshot();
        await using var session = await new ProjectRenderSessionFactory(new FixedScriptArtifactProvider(new ScriptCompilationIdentity("static-unused", "static-unused", new string('0', 64), new string('0', 64), "static-unused", "static-unused"), []), new FfmpegVideoFrameProvider(), ReplayReason.Initial).CreateAsync(snapshot);
        var request = RenderRequest.At(snapshot, 300_000);
        using SkiaRenderPipeline renderer = new(); using var expected = renderer.Render(request); using var actual = await session.RenderAsync(request);
        Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
        var error = await Assert.ThrowsAsync<PreparedFrameException>(() => session.RenderAsync(request with { Project = snapshot with { } }).AsTask());
        Assert.Equal("RenderSessionInputsMismatch", error.Code);
        await session.DisposeAsync(); await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.RenderAsync(request).AsTask());
    }

    [Fact]
    public async Task 编译失败完整保留固定来源且预取消不发布程序集或会话()
    {
        using TemporaryDirectory temp = new(); string cache = temp.FilePath("cache");
        var package = MixedEffectPackageFixture.Contents(controllerSource: "public sealed class PackEffect { invalid !!! }");
        var error = await Assert.ThrowsAsync<ProjectEffectCompilationException>(() => Factory(cache).CreateAsync(Snapshot(package)).AsTask());
        Assert.NotEmpty(error.Diagnostics);
        Assert.All(error.Diagnostics, value =>
        {
            Assert.Equal(package.PackageId, value.PackageId); Assert.Equal(package.Revision, value.Revision);
            Assert.Equal("controller", value.InstanceId); Assert.Equal("controller", value.ExportId);
            Assert.Equal("src/PackEffect.cs", value.Source.Path); Assert.True(value.Source.IsError);
            Assert.True(value.Source.Line > 0); Assert.True(value.Source.Column > 0);
            Assert.Contains(value.Source.DisplayLabel, error.Message);
        });
        Assert.DoesNotContain(Directory.EnumerateDirectories(cache), path => Path.GetFileName(path) != ".locks");
        Assert.Empty(Directory.EnumerateFiles(cache, "effect.*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(cache, "build.json", SearchOption.AllDirectories));
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        string cancelledCache = temp.FilePath("cancelled-cache");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Factory(cancelledCache).CreateAsync(Snapshot(package), cancellation.Token).AsTask());
        Assert.False(Directory.Exists(cancelledCache));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 两个取消入口都解除进行中的准备并让关闭等待所有权安全点(bool requestToken)
    {
        var snapshot = Snapshot(); var source = new WaitingSource();
        await using var session = new ProjectRenderSession(snapshot, new SkiaRenderPipeline(), source, null, [], [], null, null);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
        var request = RenderRequest.At(snapshot, 300_000);
        Task render = session.RenderAsync(requestToken ? request with { CancellationToken = cancellation.Token } : request,
            requestToken ? default : cancellation.Token).AsTask();
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Task closing = session.DisposeAsync().AsTask();
        Assert.False(closing.IsCompleted); Assert.Equal(0, source.Disposed);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => render.WaitAsync(TimeSpan.FromSeconds(3)));
        await closing.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, source.Disposed);
        await session.DisposeAsync(); Assert.Equal(1, source.Disposed);
    }

    [Fact]
    public async Task 释放继续回收真实图片原生集合和预算并保留首个及全部清理错误()
    {
        using var native = CompiledPackageGraphExports.Compile(EffectPackageDefinition.Read(ImagePackage()));
        var input = new GraphInputDefinition("fixed", GraphInputSource.ResourceImage, ShaderSurfaceKind.Color, true,
            GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas, resourceId: "images/dot.png");
        var surface = native.GetImageInput(input, new(new(0, 0, 8, 8), input.Space, SceneMatrix.Identity, 1, 1)).Surface;
        using var budget = new ImageSurfaceBudgetSession(); var held = budget.HoldFixedImages([surface]);
        List<string> releases = []; var sourceError = new InvalidOperationException("source-cleanup"); var leaseError = new IOException("lease-cleanup");
        var source = new WaitingSource(() => { releases.Add("source"); Assert.Equal(256, budget.Usage.EstimatedBytes); throw sourceError; });
        var lease = new ActionDisposable(() =>
        {
            releases.Add("fixed-images"); Assert.Equal(8, surface.Image.Width); Assert.Equal((8, 8), native.GetImagePixelSize("images/dot.png"));
            held.Dispose(); throw leaseError;
        });
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => ProjectRenderSession.ReleaseAsync(
            source, null, null, lease, [surface], [native], budget).AsTask());
        Assert.Same(sourceError, failure); Assert.Equal(["source", "fixed-images"], releases);
        Assert.Equal(new Exception[] { sourceError, leaseError }, Assert.IsType<Exception[]>(failure.Data["RuntimeCleanupFailures"]));
        Assert.Throws<ObjectDisposedException>(() => surface.Image);
        Assert.Throws<ObjectDisposedException>(() => native.GetImagePixelSize("images/dot.png"));
        Assert.Throws<ObjectDisposedException>(() => budget.BeginFrame()); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }

    private static EffectPackageContents ImagePackage()
    {
        const string path = "images/dot.png"; var contents = MixedEffectPackageFixture.Contents();
        using var bitmap = new SKBitmap(8, 8); bitmap.Erase(new SKColor(240, 120, 20));
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); byte[] bytes = data.ToArray();
        var files = contents.Files.ToDictionary(file => file.Path, file => file.Content.ToArray()); files.Add(path, bytes);
        var manifest = JsonNode.Parse(contents.Manifest.AsSpan())!.AsObject();
        manifest["files"]!.AsArray().Add(new JsonObject { ["path"] = path, ["role"] = "image", ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)) });
        return EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), contents.Profile);
    }

    private sealed class ActionDisposable(Action release) : IDisposable { public void Dispose() => release(); }
    private sealed class WaitingSource(Action? release = null) : IEvaluatedFrameSource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposed { get; private set; }
        public async ValueTask<PreparedRenderFrame> PrepareAsync(long timeUs, OutputMode outputMode, RenderTarget target, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The controlled preparation must be cancelled.");
        }
        public ValueTask DisposeAsync() { Disposed++; release?.Invoke(); return ValueTask.CompletedTask; }
    }
}
