using System.Diagnostics;
using System.Globalization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Scripting;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Runtime.Hosting.Tests;

public sealed class WorkerValidationIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public void 真实Worker能力探针成功后缓存package_validation资格()
    {
        var executor = new WorkerBackendValidationExecutor(Options());
        Assert.True(executor.PackageValidationAvailable);
        Assert.True(executor.PackageValidationAvailable);
    }

    [Theory]
    [InlineData(320, 180)] [InlineData(1920, 1080)]
    public async Task 真实Worker双输出固定计划与原Runtime正式报告逐字段相同且释放后可再创建(int width, int height)
    {
        using TemporaryDirectory temp = new();
        var snapshot = Snapshot(width, height);
        var runtimeProgress = new List<RuntimeValidationProgress>();
        var expected = await new RuntimeFrameValidator(new ProjectRenderSessionFactory(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"),
            temp.FilePath("unused-cache"), new FfmpegVideoFrameProvider(), ReplayReason.Initial)).ValidateAsync(snapshot, new(3), new RuntimeProgress(runtimeProgress.Add));
        Assert.True(expected.Succeeded, string.Join('\n', expected.Issues));
        var progress = new List<BackendJobProgressDto>();
        var executor = new WorkerBackendValidationExecutor(Options());
        var actual = await executor.ExecuteAsync(snapshot, 3, new JobProgress(progress.Add));
        Assert.Equal(expected.Succeeded, actual.Succeeded); Assert.Equal(expected.OpaqueFrames, actual.OpaqueFrames);
        Assert.Equal(expected.TransparentFrames, actual.TransparentFrames);
        Assert.Equal(expected.Issues.Select(value => new ValidationIssueDto(value.Transparent, value.TimeUs.ToString(CultureInfo.InvariantCulture),
            value.Code, value.Message, value.IsError, value.Location is null ? null : new(value.Location.Path, value.Location.Line, value.Location.Column), value.SampleFailureCode)), actual.Issues);
        Assert.Empty(expected.Diagnostics); Assert.Empty(actual.Diagnostics); Assert.NotEmpty(progress);
        foreach (var value in progress)
            Assert.Contains(runtimeProgress, expectedValue => value.CompletedFrames == expectedValue.CompletedFrames && value.TotalFrames == expectedValue.TotalFrames
                && value.TimeUs == expectedValue.TimeUs.ToString(CultureInfo.InvariantCulture) && value.Transparent == expectedValue.Transparent);
        Assert.Equal(6, progress[^1].CompletedFrames); Assert.True(progress[^1].Transparent);
        Assert.False(Directory.Exists(temp.FilePath("unused-cache")), "Static validation unexpectedly created script artifacts.");
        var recreated = await executor.ExecuteAsync(snapshot, 1);
        Assert.True(recreated.Succeeded); Assert.Equal(1, recreated.OpaqueFrames); Assert.Equal(1, recreated.TransparentFrames);
        if (width == 1920) Assert.True((long)width * height * 4 > WorkerProtocol.MaxPayloadBytes);
        output.WriteLine($"Real Worker {width}x{height}, plan3 each output, matching runtime report; recreated plan1 each output.");
    }

    [Fact]
    public async Task 真实FullHD正式任务收到实际帧进度后取消排水且代理继续执行新固定计划()
    {
        var snapshot = Snapshot(1920, 1080) with { Composition = Snapshot(1920, 1080).Composition with { FrameRate = FrameRate.Fps30, OutputDurationUs = 30_000_000 } };
        await using WorkerProcessProxy proxy = new(Options());
        using CancellationTokenSource cancellation = new();
        int completed = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => proxy.ValidateStaticProjectAsync(StaticProjectSnapshotCodec.Encode(snapshot), 600,
            TimeSpan.FromSeconds(60), new JobProgress(value => { completed = value.CompletedFrames; cancellation.Cancel(); }), cancellation.Token));
        Assert.True(completed is > 0 and < 1200);
        var result = await proxy.ValidateStaticProjectAsync(StaticProjectSnapshotCodec.Encode(snapshot), 1, TimeSpan.FromSeconds(60));
        Assert.True(result.Succeeded); int pid = Assert.IsType<int>(proxy.ProcessId);
        await proxy.StopAsync();
        Assert.Null(proxy.ProcessId); Assert.False(Alive(pid));
        output.WriteLine($"Real FullHD cancellation after {completed} actual frames; reused Worker PID={pid}, stopped=true.");
    }

    private static ProjectSnapshot Snapshot(int width, int height)
    {
        var project = LeadInFixture.Create(slots: true, crossClip: true);
        project = project with { Composition = project.Composition with { CanvasWidth = width, CanvasHeight = height,
            FrameRate = new(1, 12), OutputDurationUs = 36_000_000 }, AudioPath = "/not-authorized-validation-audio.wav" };
        return ProjectSnapshot.FromProject(project);
    }
    private static WorkerProcessOptions Options()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MakoKaraGenerator.slnx"))) root = root.Parent;
        string dll = Path.Combine(root!.FullName, "src", "MakoKaraGenerator.Backend.Worker", "bin", "Debug", "net10.0", "MakoKaraGenerator.Backend.Worker.dll");
        Assert.True(File.Exists(dll)); return new("dotnet", [dll]);
    }
    private static bool Alive(int pid)
    { try { using Process process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private sealed class RuntimeProgress(Action<RuntimeValidationProgress> report) : IProgress<RuntimeValidationProgress>
    { public void Report(RuntimeValidationProgress value) => report(value); }
    private sealed class JobProgress(Action<BackendJobProgressDto> report) : IProgress<BackendJobProgressDto>
    { public void Report(BackendJobProgressDto value) => report(value); }
}
