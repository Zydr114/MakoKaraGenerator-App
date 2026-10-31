using CliWrap;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Runtime.Hosting.Tests;

public sealed class WorkerRuntimeExportIntegrationTests
{
    [Fact]
    public async Task 真实Worker颜色背景字体包通过外置完整闭包导出()
    {
        using TemporaryDirectory temp = new();
        Project project = ProjectSnapshotSourceProject.Restore(Snapshot("static"));
        var package = PackageFontFixture.Contents();
        ProjectSnapshot snapshot = ProjectSnapshot.FromProject(project with
        {
            Effects = new([package]),
            LyricStyle = project.LyricStyle with { Typography = project.LyricStyle.Typography with
                { FontFamilyName = PackageFontFixture.Family } }
        });
        using var lease = BackendProjectRuntimeInputLease.CreateDetached(snapshot);
        var preparedResult = await lease.PrepareAsync();
        Assert.True(preparedResult.Accepted, preparedResult.Error?.Message);
        using var prepared = preparedResult.Response!;
        Assert.Null(prepared.Grant);
        Assert.Equal(ProjectRuntimeInputCodec.Profile, prepared.SourceProfile);
        Assert.True(package.Files.Sum(file => file.Content.Length) > StaticRenderWorkerContract.MaximumSnapshotBytes);
        var executor = Executor(); await executor.ProbeAsync();
        string candidate = temp.FilePath("font.mp4");
        ExportReportDto report = await executor.ExecuteAsync(snapshot,
            new("opaqueMp4", false, candidate, null, RuntimeSource: prepared.SourceBytes));
        Assert.True(report.StrictlyVerified);
        Assert.Equal(3, report.DecodedFrames);
        Assert.Equal(WorkerProtocol.ComputeSha256(await File.ReadAllBytesAsync(candidate)), report.Sha256);
    }

    [Theory]
    [InlineData("presets", "opaqueMp4")]
    [InlineData("presets", "transparentProRes4444")]
    [InlineData("advanced", "opaqueMp4")]
    [InlineData("advanced", "transparentProRes4444")]
    public async Task 真实Worker固定预设与高级包配方导出保完整投影且不重复实例(string kind, string format)
    {
        using TemporaryDirectory temp = new();
        ProjectSnapshot snapshot = Snapshot(kind);
        var executor = Executor();
        BackendExportCapabilities caps = await executor.ProbeAsync();
        string profile = kind == "advanced" ? BackendSnapshotProfiles.PackageLyricsScenePresets : BackendSnapshotProfiles.StaticLyricsScenePresets;
        Assert.Contains(profile, caps.SourceProfiles!);
        Assert.Equal(profile, ProjectSnapshotSourceProfile.Select(snapshot));
        var report = await executor.ExecuteAsync(snapshot, new(format, false, temp.FilePath(format == "opaqueMp4" ? "out.mp4" : "out.mov"), null));
        Assert.True(report.StrictlyVerified);
        Assert.Equal(3, report.DecodedFrames);
        Assert.Equal(3, report.FramesWritten);
        Assert.Equal(format != "opaqueMp4", report.HasAlpha);
    }

    [Theory]
    [InlineData("image", "presets", "opaqueMp4", BackgroundPlaybackMode.FreezeLastFrame, BackgroundFit.Contain)]
    [InlineData("image", "advanced", "transparentProRes4444", BackgroundPlaybackMode.FreezeLastFrame, BackgroundFit.Cover)]
    [InlineData("video", "static", "opaqueMp4", BackgroundPlaybackMode.FreezeLastFrame, BackgroundFit.Contain)]
    [InlineData("video", "advanced", "opaqueMp4", BackgroundPlaybackMode.Loop, BackgroundFit.Cover)]
    [InlineData("video", "presets", "transparentProRes4444", BackgroundPlaybackMode.Loop, BackgroundFit.Contain)]
    public async Task 真实Worker统一媒体授权输入导出保原背景fit循环冻结与完整帧(string mediaKind, string sourceKind, string format,
        BackgroundPlaybackMode playback, BackgroundFit fit)
    {
        using TemporaryDirectory temp = new();
        string path = temp.FilePath(mediaKind == "image" ? "source.png" : "source.mp4");
        if (mediaKind == "image")
        {
            using SKBitmap bitmap = new(64, 48); bitmap.Erase(new(210, 20, 70, 255));
            using SKImage image = SKImage.FromBitmap(bitmap); using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(path, data.ToArray());
        }
        else
            await Cli.Wrap("/usr/bin/ffmpeg").WithArguments(["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=64x48:rate=10:duration=0.2",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", path]).ExecuteAsync();
        byte[] bytes = await File.ReadAllBytesAsync(path);
        MediaFileRevision media = new(WorkerProtocol.ComputeSha256(bytes), bytes.Length, Path.GetFileName(path));
        var project = ProjectSnapshotSourceProject.Restore(Snapshot(sourceKind)) with { Background = new()
        { Kind = mediaKind == "image" ? BackgroundKind.Image : BackgroundKind.Video, Path = path, Media = media, Fit = fit, Playback = playback } };
        ProjectSnapshot snapshot = ProjectSnapshot.FromProject(project);
        var grant = new ProjectRuntimeBackgroundGrant(project.Background.Kind, media.RelativePath, path, media.Hash, media.ByteLength,
            media.OriginalName, fit, playback);
        byte[] source = ProjectRuntimeInputCodec.Encode(snapshot, grant);
        var executor = Executor();
        Assert.Contains(ProjectRuntimeInputCodec.Profile, (await executor.ProbeAsync()).SourceProfiles!);
        string candidate = temp.FilePath(format == "opaqueMp4" ? "candidate.mp4" : "candidate.mov");
        ExportReportDto report = await executor.ExecuteAsync(snapshot, new(format, false, candidate, null, RuntimeSource: source));
        Assert.True(report.StrictlyVerified); Assert.Equal(3, report.DecodedFrames); Assert.Equal(3, report.FramesWritten);
        Assert.Equal(WorkerProtocol.ComputeSha256(await File.ReadAllBytesAsync(candidate)), report.Sha256);
        using MemoryStream raw = new();
        await Cli.Wrap("/usr/bin/ffmpeg").WithArguments(["-v", "error", "-i", candidate, "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1"])
            .WithStandardOutputPipe(PipeTarget.ToStream(raw)).ExecuteAsync();
        byte[] actual = raw.ToArray();
        Assert.Equal(3 * 320 * 180 * 4, actual.Length);
        using var videos = new FfmpegVideoFrameProvider();
        var factory = new ProjectRenderSessionFactory(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"),
            temp.FilePath("oracle-cache"), videos, ReplayReason.Initial);
        await using var session = await factory.CreateAsync(snapshot);
        for (int index = 0; index < 3; index++)
        {
            using RenderFrame expected = await session.RenderAsync(RenderRequest.At(snapshot, index * 100_000L) with
            { Quality = RenderQuality.Final, TransparentBackground = format != "opaqueMp4" });
            // Full-frame mean error accounts for the selected lossy media encoding.
            double error = 0;
            for (int offset = 0; offset < expected.PixelMemory.Length; offset++)
                error += Math.Abs(expected.PixelMemory.Span[offset] - actual[index * expected.PixelMemory.Length + offset]);
            Assert.InRange(error / expected.PixelMemory.Length, 0, format == "opaqueMp4" ? 12 : 5);
        }
    }

    [Fact]
    public async Task 真实Worker拒绝授权媒体内容改变且不生成导出候选()
    {
        using TemporaryDirectory temp = new();
        string path = temp.FilePath("source.png");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var media = new MediaFileRevision(WorkerProtocol.ComputeSha256([1, 2, 3]), 3, "source.png");
        ProjectSnapshot snapshot = ProjectSnapshot.FromProject(ProjectSnapshotSourceProject.Restore(Snapshot("static")) with
        { Background = new() { Kind = BackgroundKind.Image, Path = path, Media = media } });
        byte[] source = ProjectRuntimeInputCodec.Encode(snapshot, new(BackgroundKind.Image, media.RelativePath, path, media.Hash, 3,
            media.OriginalName, snapshot.Background.Fit, snapshot.Background.Playback));
        await File.WriteAllBytesAsync(path, [3, 2, 1]);
        var executor = Executor(); await executor.ProbeAsync();
        string candidate = temp.FilePath("out.mp4");
        var error = await Assert.ThrowsAsync<RenderSessionFailureException>(() =>
            executor.ExecuteAsync(snapshot, new("opaqueMp4", false, candidate, null, RuntimeSource: source)));
        Assert.Equal("ProjectRuntimeSourceChanged", error.Code); Assert.False(File.Exists(candidate));
    }

    private static WorkerBackendExportExecutor Executor() => new(new WorkerProcessOptions(PackageWorkerRuntimeFixture.WorkerApphost()),
        "/usr/bin/ffmpeg", "/usr/bin/ffprobe");

    private static ProjectSnapshot Snapshot(string kind)
    {
        ProjectSnapshot basis = PackageWorkerRuntimeFixture.Create("scale");
        Project project = ProjectSnapshotSourceProject.Restore(basis) with
        { Composition = basis.Composition with { OutputDurationUs = 300_000, FrameRate = new FrameRate(10, 1) }, Effects = ProjectEffects.Empty };
        if (kind == "static") return ProjectSnapshot.FromProject(project);
        var package = basis.Effects.Packages[0];
        var recipes = kind == "advanced" ? new[] { new TemplateEffectRecipe("recipe", "固定包配方", TemplateEffectTarget.TokenFamily,
            package.PackageId, package.Revision, "scale", EffectParameterOverrides.Parse("{\"peak\":1.6}"), fixedSeed: ulong.MaxValue) } : [];
        var template = StyleTemplate.Create("preset", "固定预设", StyleTemplateKind.Token, project.LyricStyle,
            [LyricStyleProperty.Opacity], recipes, kind == "advanced" ? [package] : []);
        Assert.True(PresetApplicationsEditor.TryApply(project, template,
            [new ProjectEffectBinding(ProjectEffectBindingKind.Tokens, ids: ["token"])], null, out var applied, out var reason), reason);
        return ProjectSnapshot.FromProject(applied);
    }
}
