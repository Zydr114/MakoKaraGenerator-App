using System.Diagnostics;
using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;
using SkiaSharp;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class RuntimeValidationWorkerTests
{
    [Theory]
    [InlineData("color")]
    [InlineData("image")]
    [InlineData("video")]
    public async Task 实际Worker统一输入执行双输出四帧并保完整报告(string kind)
    {
        using TemporaryDirectory directory = new();
        byte[] source = await Source(directory, kind);
        var executor = new WorkerBackendValidationExecutor(WorkerPath());
        Assert.True(executor.RuntimeValidationAvailable);
        var progress = new List<BackendJobProgressDto>();
        ValidationReportDto report = await executor.ExecuteRuntimeAsync(source, 4, new ProgressSink(progress));
        Assert.True(report.Succeeded);
        Assert.Equal(4, report.OpaqueFrames); Assert.Equal(4, report.TransparentFrames);
        Assert.Empty(report.Issues); Assert.Empty(report.Diagnostics);
        Assert.Equal(8, progress[^1].CompletedFrames);
        Assert.Equal(report, WorkerValidationContract.DecodeReport(WorkerValidationContract.EncodeReport(report, 4), 4));
    }

    [Fact]
    public async Task 实际Worker验证固定大字体目录闭包而Host只校验输入结构()
    {
        var package = PackageFontFixture.Contents();
        Assert.True(package.Files.Single(file => file.Role == "font").Content.Length > ProjectRuntimeInputCodec.MaximumBytes);
        var project = Project.Create("runtime-font-validation", Composition.Create(16, 8, new FrameRate(4, 1), outputDurationUs: 1_000_000),
            LyricsDocument.Create([])) with { Effects = new([package]) };
        using var lease = BackendProjectRuntimeInputLease.CreateDetached(ProjectSnapshot.FromProject(project));
        var preparation = await lease.PrepareAsync();
        Assert.Null(preparation.Error);
        using var prepared = Assert.IsType<BackendPreparedProjectRuntimeInput>(preparation.Response);
        Assert.Equal(ProjectRuntimeInputCodec.Profile, prepared.SourceProfile);
        Assert.True(prepared.SourceBytes.Length < 64 * 1024);
        var executor = new WorkerBackendValidationExecutor(WorkerPath());
        Assert.True(executor.RuntimeValidationAvailable);
        var report = await executor.ExecuteRuntimeAsync(prepared.SourceBytes, 4);
        Assert.True(report.Succeeded);
        Assert.Equal(4, report.OpaqueFrames);
        Assert.Equal(4, report.TransparentFrames);
    }

    [Theory]
    [InlineData(0)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(28)]
    public void 独立任务标记版本与保留位拒绝旧合同或畸形输入(int index)
    {
        byte[] source = [1, 2, 3];
        byte[] encoded = RuntimeValidationWorkerContract.EncodeInput(Guid.NewGuid(), 4, source);
        Assert.Equal(source, RuntimeValidationWorkerContract.DecodeInput(encoded).SnapshotBytes);
        encoded[index] = 9;
        Assert.Equal("WorkerRuntimeValidationInputRejected", Assert.Throws<WorkerProtocolException>(() =>
            RuntimeValidationWorkerContract.DecodeInput(encoded)).Code);
        Assert.Throws<WorkerProtocolException>(() => WorkerValidationContract.DecodeInput(encoded));
    }

    [Theory]
    [InlineData(RuntimeValidationWorkerContract.CapabilitiesKind)]
    [InlineData(RuntimeValidationWorkerContract.RequestKind)]
    public void 统一验证私有输入必须携带且核对完整payloadSHA(string kind)
    {
        byte[] payload = [1, 2, 3];
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.Validate(new(kind, "request", payload: payload)));
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.Validate(new(kind, "request", new string('0', 64), payload)));
        WorkerProtocol.Validate(new(kind, "request", WorkerProtocol.ComputeSha256(payload), payload));
    }

    private static async Task<byte[]> Source(TemporaryDirectory directory, string kind)
    {
        var project = Project.Create("runtime-validation", Composition.Create(16, 8, new FrameRate(4, 1), outputDurationUs: 1_000_000), LyricsDocument.Create([]));
        if (kind == "color") return ProjectRuntimeInputCodec.Encode(ProjectSnapshot.FromProject(project), null);
        string path = directory.FilePath(kind == "image" ? "image.png" : "video.mp4");
        if (kind == "image")
        {
            using SKBitmap bitmap = new(16, 8); bitmap.Erase(SKColors.Coral);
            using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(path, png.ToArray());
        }
        else
        {
            ProcessStartInfo start = new("ffmpeg") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i", "testsrc2=size=16x8:rate=4", "-t", "1", "-c:v", "mpeg4", "-an", "-y", path })
                start.ArgumentList.Add(argument);
            using Process process = Process.Start(start)!;
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode); Assert.Equal("", await error);
        }
        byte[] content = await File.ReadAllBytesAsync(path);
        MediaFileRevision media = new(Convert.ToHexStringLower(SHA256.HashData(content)), content.Length, Path.GetFileName(path));
        var background = new BackgroundSource { Kind = kind == "image" ? BackgroundKind.Image : BackgroundKind.Video,
            Path = path, Media = media, Fit = BackgroundFit.Contain, Playback = BackgroundPlaybackMode.FreezeLastFrame };
        var snapshot = ProjectSnapshot.FromProject(project with { Background = background });
        return ProjectRuntimeInputCodec.Encode(snapshot, new(background.Kind, media.RelativePath, path, media.Hash,
            media.ByteLength, media.OriginalName, background.Fit, background.Playback));
    }

    private static string WorkerPath()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MakoKaraGenerator.slnx"))) root = root.Parent;
        string path = Path.Combine(root!.FullName, "src/MakoKaraGenerator.Backend.Worker/bin/Debug/net10.0/MakoKaraGenerator.Backend.Worker");
        Assert.True(File.Exists(path), "Build Debug Worker before executing the real Worker test."); return path;
    }
    private sealed class ProgressSink(List<BackendJobProgressDto> values) : IProgress<BackendJobProgressDto>
    { public void Report(BackendJobProgressDto value) => values.Add(value); }
}
