using MakoKaraGenerator.Scripting.Compilation;
using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Runtime.Hosting.Tests;

public sealed class WorkerStaticSessionIntegrationTests
{
    [Fact]
    public async Task 真实静态会话调用前取消后同Worker仍可交付正确帧()
    {
        ProjectSnapshot snapshot = Snapshot();
        var factory = new WorkerRenderFrameSessionFactory(Options() with { CancellationGracePeriod = TimeSpan.FromSeconds(5) });
        await using IRenderFrameSession session = await factory.CreateAsync(snapshot);
        var proxy = (WorkerProcessProxy)session.GetType().GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Single(field => field.FieldType == typeof(WorkerProcessProxy)).GetValue(session)!;
        int pid = Assert.IsType<int>(proxy.ProcessId);
        using CancellationTokenSource cancel = new();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RenderAsync(
            RenderRequest.At(snapshot, 12_500_000) with
            { Target = new() { PixelWidth = 1024, PixelHeight = 1024 } }, cancel.Token).AsTask());
        Assert.Equal(pid, proxy.ProcessId);
        RenderRequest request = RenderRequest.At(snapshot, 12_500_000) with
        { Target = new() { PixelWidth = 320, PixelHeight = 180 }, TransparentBackground = true };
        using RenderFrame actual = await session.RenderAsync(request);
        using TemporaryDirectory temp = new();
        using FfmpegVideoFrameProvider videos = new();
        var runtime = new ProjectRenderSessionFactory(new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"),
            temp.FilePath("cache")), videos, ReplayReason.Initial);
        await using IRenderFrameSession expected = await runtime.CreateAsync(snapshot);
        using RenderFrame reference = await expected.RenderAsync(request);
        Assert.Equal(reference.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
        Assert.Equal(pid, proxy.ProcessId);
        await session.DisposeAsync();
        Assert.Null(proxy.ProcessId);
    }

    [Fact]
    public async Task 真实静态Worker未发送取消后同进程重开且新帧与Runtime一致()
    {
        ProjectSnapshot snapshot = Snapshot();
        byte[] bytes = StaticProjectSnapshotCodec.Encode(snapshot);
        await using WorkerProcessProxy proxy = new(Options() with { CancellationGracePeriod = TimeSpan.FromSeconds(5) });
        StaticWorkerSessionHandle old = await proxy.OpenStaticSessionAsync(bytes, TimeSpan.FromSeconds(20));
        int pid = Assert.IsType<int>(proxy.ProcessId);
        using (CancellationTokenSource notStarted = new())
        {
            notStarted.Cancel();
            OperationCanceledException skipped = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => proxy.RenderStaticSessionAsync(
                new(old.SessionId, old.SourceSha256, 320, 180, 12_500_000, false, 1), TimeSpan.FromSeconds(20), notStarted.Token));
            Assert.Equal("WorkerInvocationNotStartedException", skipped.GetType().Name);
            Assert.Equal(pid, proxy.ProcessId);
        }
        await proxy.CloseStaticSessionAsync(old, 2, TimeSpan.FromSeconds(20));
        StaticWorkerSessionHandle fresh = await proxy.OpenStaticSessionAsync(bytes, TimeSpan.FromSeconds(20));
        Assert.NotEqual(old.SessionId, fresh.SessionId);
        Assert.Equal(pid, proxy.ProcessId);
        WorkerMessage actual = await proxy.RenderStaticSessionAsync(new(fresh.SessionId, fresh.SourceSha256,
            320, 180, 12_500_000, true, 1), TimeSpan.FromSeconds(20));
        using TemporaryDirectory temp = new();
        using FfmpegVideoFrameProvider videos = new();
        var runtime = new ProjectRenderSessionFactory(new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"),
            temp.FilePath("cache")), videos, ReplayReason.Initial);
        await using IRenderFrameSession expected = await runtime.CreateAsync(snapshot);
        using RenderFrame frame = await expected.RenderAsync(RenderRequest.At(snapshot, 12_500_000) with
        { Target = new() { PixelWidth = 320, PixelHeight = 180 }, TransparentBackground = true });
        Assert.Equal(frame.PixelMemory.ToArray(), actual.Payload);
        await proxy.CloseStaticSessionAsync(fresh, 2, TimeSpan.FromSeconds(20));
        await proxy.StopAsync();
        Assert.Null(proxy.ProcessId);
    }

    [Fact]
    public async Task 持久Worker实际消费纯静态预设快照并与Runtime逐字节相等()
    {
        using TemporaryDirectory temp = new();
        ProjectSnapshot source = Snapshot();
        var template = StyleTemplate.Create("worker-preset", "Worker固定", StyleTemplateKind.Line,
            source.LyricStyle with { Opacity = .4f }, [LyricStyleProperty.Opacity]);
        source = source with
        {
            PresetApplications = new([new PresetApplication("project-preset",
                new(ProjectEffectBindingKind.Project), template)])
        };
        Assert.Equal(StaticProjectSnapshotCodec.PresetsProfile, StaticProjectSnapshotCodec.SelectProfile(source));
        var worker = new WorkerRenderFrameSessionFactory(Options());
        var runtime = new ProjectRenderSessionFactory(new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), temp.FilePath("unused-cache")), new FfmpegVideoFrameProvider(), ReplayReason.Initial);
        await using IRenderFrameSession actual = await worker.CreateAsync(source);
        await using IRenderFrameSession expected = await runtime.CreateAsync(source);
        RenderRequest request = RenderRequest.At(source, 12_500_000) with
        { Target = new() { PixelWidth = 320, PixelHeight = 180 }, TransparentBackground = true };
        using RenderFrame actualFrame = await actual.RenderAsync(request);
        using RenderFrame expectedFrame = await expected.RenderAsync(request);
        Assert.Equal(expectedFrame.PixelMemory.ToArray(), actualFrame.PixelMemory.ToArray());
        Assert.Equal(expectedFrame.DiagnosticSnapshot.ReadAll().ToArray(), actualFrame.DiagnosticSnapshot.ReadAll().ToArray());
        Assert.False(Directory.Exists(temp.FilePath("unused-cache")), "纯静态预设 Worker 不应编译脚本。");
    }

    [Theory]
    [InlineData(false, RenderQuality.Draft)]
    [InlineData(true, RenderQuality.Draft)]
    [InlineData(false, RenderQuality.Standard)]
    [InlineData(true, RenderQuality.Standard)]
    [InlineData(false, RenderQuality.Final)]
    [InlineData(true, RenderQuality.Final)]
    public async Task 持久Worker静态歌词场景连续三时刻和寻址与真实Runtime逐字节相等(bool transparent, RenderQuality quality)
    {
        using TemporaryDirectory temp = new();
        using SKTypeface font = SKTypeface.FromFamilyName(SkiaTextLayoutEngine.DefaultFamilyName);
        Assert.Equal(SkiaTextLayoutEngine.DefaultFamilyName, font.FamilyName);
        ProjectSnapshot snapshot = Snapshot();
        var worker = new WorkerRenderFrameSessionFactory(Options());
        var runtime = new ProjectRenderSessionFactory(new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), temp.FilePath("unused-cache")), new FfmpegVideoFrameProvider(), ReplayReason.Initial);
        await using IRenderFrameSession actualSession = await worker.CreateAsync(snapshot);
        await using IRenderFrameSession expectedSession = await runtime.CreateAsync(snapshot);
        HashSet<string> hashes = [];
        foreach (long time in new long[] { 11_600_000, 12_500_000, 15_500_000, 12_500_000 })
        {
            RenderRequest request = RenderRequest.At(snapshot, time) with
            { Target = new() { PixelWidth = 320, PixelHeight = 180 }, TransparentBackground = transparent, Quality = quality };
            using RenderFrame expected = await expectedSession.RenderAsync(request);
            using RenderFrame actual = await actualSession.RenderAsync(request);
            Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
            Assert.Equal(expected.DiagnosticSnapshot.ReadAll().ToArray(), actual.DiagnosticSnapshot.ReadAll().ToArray());
            Assert.Equal(expected.DiagnosticSnapshot.HasError, actual.DiagnosticSnapshot.HasError);
            Assert.Equal(time, actual.TimeUs);
            Assert.Equal(request.ResolveTarget(), actual.Target);
            hashes.Add(Convert.ToHexString(SHA256.HashData(actual.PixelMemory.Span)));
        }
        Assert.True(hashes.Count >= 2, "Fixture did not exercise changing lyric/scene pixels.");
        Assert.False(Directory.Exists(temp.FilePath("unused-cache")), "Static profile unexpectedly compiled scripts.");
        await actualSession.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => actualSession.RenderAsync(RenderRequest.At(snapshot, 12_500_000)).AsTask());
        await using IRenderFrameSession recreated = await worker.CreateAsync(snapshot);
        RenderRequest seek = RenderRequest.At(snapshot, 12_500_000) with
        { Target = new() { PixelWidth = 320, PixelHeight = 180 }, TransparentBackground = transparent, Quality = quality };
        using RenderFrame reopened = await recreated.RenderAsync(seek);
        using RenderFrame baseline = await expectedSession.RenderAsync(seek);
        Assert.Equal(baseline.PixelMemory.ToArray(), reopened.PixelMemory.ToArray());
    }

    [Theory]
    [InlineData("session")]
    [InlineData("hash")]
    [InlineData("sequence")]
    public async Task 同一真实Worker关闭后可重新open且旧会话与源SHA和序号不能复用(string mismatch)
    {
        await using WorkerProcessProxy proxy = new(Options());
        byte[] bytes = StaticProjectSnapshotCodec.Encode(Snapshot());
        StaticWorkerSessionHandle first = await proxy.OpenStaticSessionAsync(bytes, TimeSpan.FromSeconds(20));
        int processId = Assert.IsType<int>(proxy.ProcessId);
        for (int sequence = 1; sequence <= 3; sequence++)
        {
            WorkerMessage result = await proxy.RenderStaticSessionAsync(new(first.SessionId, first.SourceSha256,
                320, 180, 11_500_000 + sequence * 1_000_000, false, sequence), TimeSpan.FromSeconds(20));
            Assert.NotNull(result.Diagnostics);
            Assert.Equal((int)RenderQuality.Standard, result.FrameQuality);
            Assert.Equal(processId, proxy.ProcessId);
        }
        await proxy.CloseStaticSessionAsync(first, 4, TimeSpan.FromSeconds(20));
        StaticWorkerSessionHandle second = await proxy.OpenStaticSessionAsync(bytes, TimeSpan.FromSeconds(20));
        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal(first.SourceSha256, second.SourceSha256);
        Assert.Equal(processId, proxy.ProcessId);
        WorkerMessage fresh = await proxy.RenderStaticSessionAsync(new(second.SessionId, second.SourceSha256,
            320, 180, 12_500_000, true, 1), TimeSpan.FromSeconds(20));
        Assert.Equal(second.SessionId.ToString("N"), fresh.SessionId);
        StaticWorkerFrameRequest invalid = mismatch switch
        {
            "session" => new(first.SessionId, first.SourceSha256, 320, 180, 12_500_000, true, 5),
            "hash" => new(second.SessionId, new string('0', 64), 320, 180, 12_500_000, true, 2),
            "sequence" => new(second.SessionId, second.SourceSha256, 320, 180, 12_500_000, true, 1),
            _ => throw new ArgumentException(mismatch),
        };
        WorkerProtocolException stale = await Assert.ThrowsAsync<WorkerProtocolException>(() => proxy.RenderStaticSessionAsync(invalid, TimeSpan.FromSeconds(20)));
        Assert.Equal(mismatch == "sequence" ? "WorkerRequestOutOfOrder" : "WorkerSessionConflict", stale.Code);
        Assert.Null(proxy.ProcessId);
        StaticWorkerSessionHandle recovered = await proxy.OpenStaticSessionAsync(bytes, TimeSpan.FromSeconds(20));
        Assert.NotEqual(second.SessionId, recovered.SessionId);
        await proxy.CloseStaticSessionAsync(recovered, 1, TimeSpan.FromSeconds(20));
        await proxy.StopAsync();
    }

    private static ProjectSnapshot Snapshot()
    {
        var project = LeadInFixture.Create(slots: true, crossClip: true);
        var gradient = new GradientSpec { AngleDegrees = 36, Stops = [new(0, new(255, 100, 30, 160)), new(1, new(90, 30, 255, 190))] };
        project = project with
        {
            Composition = project.Composition with { BackgroundColor = new(20, 35, 55, 160) },
            AudioPath = "/worker-must-not-read-missing-audio.wav",
            EnterAnimation = new() { DurationUs = 150_000, StartOpacity = .5f, StartOffsetPx = 7 },
            LyricStyle = project.LyricStyle with
            {
                UnsungFill = new() { Material = new MaterialSpec.Gradient(gradient) },
                SungFill = new() { Material = new MaterialSpec.Solid(new(255, 100, 70, 210)) },
                Stroke = new() { Material = new MaterialSpec.Gradient(gradient with { AngleDegrees = 90 }), WidthPx = 2 },
                Shadow = new() { Color = new(0, 0, 0, 100), AngleDegrees = 17, DistancePx = 3, BlurRadiusPx = 2 },
            },
        };
        return ProjectSnapshot.FromProject(project);
    }

    private static WorkerProcessOptions Options()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MakoKaraGenerator.slnx"))) root = root.Parent;
        string dll = Path.Combine(root!.FullName, "src", "MakoKaraGenerator.Backend.Worker", "bin", "Debug", "net10.0", "MakoKaraGenerator.Backend.Worker.dll");
        Assert.True(File.Exists(dll));
        return new("dotnet", [dll]);
    }
}
