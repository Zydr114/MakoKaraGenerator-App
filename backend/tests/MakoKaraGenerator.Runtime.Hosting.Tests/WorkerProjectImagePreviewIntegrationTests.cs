using MakoKaraGenerator.Scripting.Compilation;
using System.Diagnostics;
using System.Globalization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Processes;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using SkiaSharp;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Runtime.Hosting.Tests;

public sealed class WorkerProjectImagePreviewIntegrationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("static")]
    [InlineData("analysis")]
    [InlineData("package")]
    public async Task 图片三种基础来源的真实Worker与原Runtime普通透明逐帧等价且背景实际贡献像素(string kind)
    {
        Assert.True(LinuxProcessGuards.IsSupported);
        PackageWorkerRuntimeFixture.AssertFixedFont();
        using TemporaryDirectory temp = new(); using FfmpegVideoFrameProvider videos = new();
        byte[] bytes = Png();
        ProjectSnapshot snapshot = WithImage(Basis(kind), bytes);
        // The oracle consumes the original file-backed production resolver. The Worker only receives fixed bytes.
        string imagePath = temp.FilePath("original.png");
        await File.WriteAllBytesAsync(imagePath, bytes);
        ProjectSnapshot oracle = PackageWorkerRuntimeFixture.Fingerprint(snapshot with
        { Background = snapshot.Background with { Path = imagePath } });
        ProjectSnapshot plain = PackageWorkerRuntimeFixture.Fingerprint(snapshot with { Background = BackgroundSource.Color });
        var runtime = new ProjectRenderSessionFactory(new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), temp.FilePath("cache")), videos, ReplayReason.Initial, renderBackend: kind == "package" ? "vulkan" : null);
        await using IRenderFrameSession direct = await runtime.CreateAsync(oracle);
        await using IRenderFrameSession counterfactual = await runtime.CreateAsync(plain);
        await using WorkerProcessProxy proxy = new(new(PackageWorkerRuntimeFixture.WorkerApphost()) { RenderBackend = kind == "package" ? "gpu" : null });
        var capability = await proxy.GetImagePreviewCapabilitiesAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(BackendSnapshotProfiles.ProjectImagePreview, capability.CodecProfile);
        StaticWorkerSessionHandle handle = await proxy.OpenImagePreviewSessionAsync(ProjectImagePreviewSnapshotCodec.Encode(snapshot, bytes), TimeSpan.FromSeconds(30));
        int pid = Assert.IsType<int>(proxy.ProcessId);
        using Process process = Process.GetProcessById(pid);
        OwnedProcessGroup group = Assert.IsType<OwnedProcessGroup>(OwnedProcessGroup.TryCapture(process));
        long sequence = 0;
        foreach (bool transparent in new[] { false, true })
        foreach (long time in new long[] { 0, 100_000, 300_000, 100_000 })
        {
            using RenderFrame expected = await direct.RenderAsync(WorkerPackageSessionIntegrationTests.Request(oracle, time, transparent));
            using RenderFrame absent = await counterfactual.RenderAsync(WorkerPackageSessionIntegrationTests.Request(plain, time, transparent));
            WorkerMessage actual = await proxy.RenderPackageSessionAsync(new(handle.SessionId, handle.SourceSha256,
                320, 180, time, transparent, ++sequence), TimeSpan.FromSeconds(20));
            Assert.Equal(expected.PixelMemory.ToArray(), actual.Payload);
            if (!transparent) Assert.False(absent.PixelMemory.Span.SequenceEqual(actual.Payload));
            Assert.Equal(pid, proxy.ProcessId);
            Assert.Equal(WorkerPackageDiagnosticsContract.Version, actual.PackageDiagnostics!.Version);
            Assert.Equal(expected.ImageEffectDiagnostics.ToArray(), new PackageRenderDiagnosticsMapper(snapshot).DecodeFrame(actual.PackageDiagnostics).Image.ToArray());
            Assert.Equal(expected.MaterialDiagnostics.ToArray(), new PackageRenderDiagnosticsMapper(snapshot).DecodeFrame(actual.PackageDiagnostics).Material.ToArray());
            Assert.False(actual.Diagnostics!.HasError);
        }
        await proxy.ClosePackageSessionAsync(handle, ++sequence, TimeSpan.FromSeconds(20));
        // Reopen with the same process proves image session ownership does not depend on package-capability state.
        var reopened = await proxy.OpenImagePreviewSessionAsync(ProjectImagePreviewSnapshotCodec.Encode(snapshot, bytes), TimeSpan.FromSeconds(30));
        Assert.Equal(pid, proxy.ProcessId);
        await proxy.ClosePackageSessionAsync(reopened, 1, TimeSpan.FromSeconds(20));
        await proxy.StopAsync();
        Assert.Empty(group.ObserveMembers());
        Assert.False(Directory.Exists("/proc/" + pid.ToString(CultureInfo.InvariantCulture)));
        output.WriteLine($"Image {kind}: 8 raw BGRA frames byteexact with original Runtime, background counterfactual, close/reopen; Worker {group.Leader} drained.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 绑定图片工厂支持普通与透明静态无包并持有独立bytes(bool transparent)
    {
        byte[] bytes = Png();
        ProjectSnapshot snapshot = WithImage(Basis("static"), bytes);
        var factory = new WorkerPackageRenderFrameSessionFactory(new(PackageWorkerRuntimeFixture.WorkerApphost()) { RenderBackend = "gpu" });
        IRenderFrameSessionFactory bound = factory.Bind(snapshot, bytes);
        Array.Clear(bytes);
        await using IRenderFrameSession session = await bound.CreateAsync(snapshot);
        using RenderFrame frame = await session.RenderAsync(WorkerPackageSessionIntegrationTests.Request(snapshot, 100_000, transparent));
        Assert.Equal(320 * 180 * 4, frame.PixelMemory.Length);
        Assert.Empty(frame.ImageEffectDiagnostics); Assert.Empty(frame.MaterialDiagnostics);
    }

    [Theory]
    [InlineData("corrupt", "PackageImageDecodeFailed")]
    [InlineData("dimension", "PackageImageBudgetExceeded")]
    [InlineData("decoded", "PackageImageBudgetExceeded")]
    public async Task 损坏或解码超预算图片在open拒绝且不产生成功会话(string kind, string code)
    {
        byte[] bytes = kind == "corrupt" ? [1, 2, 3] : kind == "dimension" ? Png(4097, 1) : Png(2049, 2049);
        ProjectSnapshot snapshot = WithImage(Basis("static"), bytes);
        var factory = new WorkerPackageRenderFrameSessionFactory(new(PackageWorkerRuntimeFixture.WorkerApphost()) { RenderBackend = "gpu" });
        IRenderFrameSessionFactory bound = factory.Bind(snapshot, bytes);
        RenderSessionFailureException failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => bound.CreateAsync(snapshot).AsTask());
        Assert.Equal(code, failure.Code);
        Assert.Null(failure.DiagnosticSnapshot);
    }

    [Fact]
    public async Task 图片包编译失败保留完整源诊断且后续健康会话可运行()
    {
        byte[] bytes = Png();
        ProjectSnapshot snapshot = WithImage(PackageWorkerRuntimeFixture.DiagnosticSource("compile"), bytes);
        var factory = new WorkerPackageRenderFrameSessionFactory(new(PackageWorkerRuntimeFixture.WorkerApphost()) { RenderBackend = "gpu" });
        RenderSessionFailureException failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => factory.Bind(snapshot, bytes).CreateAsync(snapshot).AsTask());
        Assert.Equal("WorkerPackageCompilationFailed", failure.Code);
        Assert.NotEmpty(failure.SourceDiagnostics);
        Assert.All(failure.SourceDiagnostics, d => Assert.Equal("src/PackEffect.cs", d.Path));
        ProjectSnapshot healthy = WithImage(Basis("static"), bytes);
        await using IRenderFrameSession session = await factory.Bind(healthy, bytes).CreateAsync(healthy);
        using RenderFrame frame = await session.RenderAsync(WorkerPackageSessionIntegrationTests.Request(healthy, 100_000));
        Assert.False(frame.DiagnosticSnapshot.HasError);
    }

    [Fact]
    public async Task 旧静态与旧包open仍严格拒绝图片envelope()
    {
        byte[] bytes = Png(); ProjectSnapshot snapshot = WithImage(Basis("static"), bytes);
        byte[] envelope = ProjectImagePreviewSnapshotCodec.Encode(snapshot, bytes);
        await using WorkerProcessProxy proxy = new(new(PackageWorkerRuntimeFixture.WorkerApphost()));
        await Assert.ThrowsAsync<WorkerProtocolException>(() => proxy.OpenPackageSessionAsync(envelope, TimeSpan.FromSeconds(30)));
        Assert.Null(proxy.ProcessId);
        await Assert.ThrowsAsync<WorkerProtocolException>(() => proxy.OpenStaticSessionAsync(envelope, TimeSpan.FromSeconds(30)));
        Assert.Null(proxy.ProcessId);
    }

    [Theory]
    [InlineData("static")]
    [InlineData("analysis")]
    [InlineData("package")]
    public async Task 图片验证独立能力返回普通透明完整报告(string kind)
    {
        byte[] bytes = Png();
        ProjectSnapshot snapshot = WithImage(Basis(kind), bytes);
        byte[] envelope = ProjectImagePreviewSnapshotCodec.Encode(snapshot, bytes);
        await using WorkerProcessProxy proxy = new(new(PackageWorkerRuntimeFixture.WorkerApphost()) { RenderBackend = kind == "package" ? "gpu" : null });
        ProjectImageValidationWorkerCapabilities capability = await proxy.GetImageValidationCapabilitiesAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(ProjectImageValidationWorkerContract.ValidationProfile, capability.CodecProfile);
        ValidationReportDto report = await proxy.ValidateImageProjectAsync(envelope, 2, TimeSpan.FromSeconds(30));
        Assert.True(report.Succeeded);
        Assert.Equal(2, report.OpaqueFrames);
        Assert.Equal(2, report.TransparentFrames);
        await proxy.StopAsync();
        Assert.Null(proxy.ProcessId);
    }

    [Theory]
    [InlineData("corrupt", "PackageImageDecodeFailed")]
    [InlineData("dimension", "PackageImageBudgetExceeded")]
    [InlineData("decoded", "PackageImageBudgetExceeded")]
    public async Task 图片验证解码失败仍返回稳定失败报告(string kind, string code)
    {
        byte[] bytes = kind switch
        {
            "corrupt" => [1, 2, 3],
            "dimension" => Png(4097, 1),
            "decoded" => Png(2049, 2049),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        ProjectSnapshot snapshot = WithImage(Basis("static"), bytes);
        byte[] envelope = ProjectImagePreviewSnapshotCodec.Encode(snapshot, bytes);
        await using WorkerProcessProxy proxy = new(new(PackageWorkerRuntimeFixture.WorkerApphost()));
        ValidationReportDto report = await proxy.ValidateImageProjectAsync(envelope, 2, TimeSpan.FromSeconds(30));
        Assert.False(report.Succeeded);
        Assert.Equal(0, report.OpaqueFrames);
        Assert.Equal(0, report.TransparentFrames);
        ValidationIssueDto issue = Assert.Single(report.Issues);
        Assert.Equal(code, issue.Code);
        Assert.True(issue.IsError);
        Assert.Empty(report.Diagnostics);
        await proxy.StopAsync();
        Assert.Null(proxy.ProcessId);
    }

    private static ProjectSnapshot Basis(string kind)
    {
        ProjectSnapshot snapshot = PackageWorkerRuntimeFixture.Create("scale");
        if (kind == "package") return snapshot;
        snapshot = snapshot with { Effects = ProjectEffects.Empty, AudioPath = null, AudioMedia = null, AudioAnalysis = null };
        if (kind == "analysis")
            snapshot = snapshot with { AudioAnalysis = ProjectAudioAnalysis.Failed("ActualAnalysisFailed", "固定失败分析"), AudioPath = "audio.wav",
                AudioMedia = new(new string('a', 64), 44, "audio.wav") };
        return PackageWorkerRuntimeFixture.Fingerprint(snapshot);
    }

    private static ProjectSnapshot WithImage(ProjectSnapshot snapshot, byte[] bytes)
    {
        var media = new MediaFileRevision(WorkerProtocol.ComputeSha256(bytes), bytes.Length, "actual.png");
        return PackageWorkerRuntimeFixture.Fingerprint(snapshot with { Background = new()
        { Kind = BackgroundKind.Image, Path = media.RelativePath, Media = media, Fit = BackgroundFit.Contain, Playback = BackgroundPlaybackMode.FreezeLastFrame } });
    }

    private static byte[] Png(int width = 7, int height = 5)
    {
        using SKBitmap bitmap = new(width, height); bitmap.Erase(new SKColor(240, 20, 120, 180));
        bitmap.SetPixel(0, 0, new(0, 240, 70, 230));
        using SKImage image = SKImage.FromBitmap(bitmap); using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
