using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Processes;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Runtime.Hosting.Tests;

public sealed class WorkerPackageSessionIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task 真实包会话活动取消后同Worker自动重建并交付正确新帧()
    {
        PackageWorkerRuntimeFixture.AssertFixedFont();
        ProjectSnapshot basis = PackageWorkerRuntimeFixture.CreateScale(BarrierSource());
        ProjectSnapshot source = PackageWorkerRuntimeFixture.Fingerprint(basis with
        { Composition = basis.Composition with { OutputDurationUs = 180_000_000 } });
        var factory = new WorkerPackageRenderFrameSessionFactory(new(PackageWorkerRuntimeFixture.WorkerApphost(),
            cancellationGracePeriod: TimeSpan.FromSeconds(5)) { RenderBackend = "gpu", ScriptCompilationService = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")) }, TimeSpan.FromSeconds(30));
        await using IRenderFrameSession session = await factory.CreateAsync(source);
        var proxy = (WorkerProcessProxy)session.GetType().GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Single(field => field.FieldType == typeof(WorkerProcessProxy)).GetValue(session)!;
        int pid = Assert.IsType<int>(proxy.ProcessId);
        ulong baselineTicks = ReadCpuTicks(pid);
        using CancellationTokenSource cancel = new();
        Task<RenderFrame> pending = session.RenderAsync(Request(source, 179_000_000), cancel.Token).AsTask();
        await WaitForCpuAdvance(pid, baselineTicks, pending);
        cancel.Cancel();
        OperationCanceledException cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal("WorkerCooperativeCancellationException", cancelled.GetType().Name);
        Assert.Equal(pid, proxy.ProcessId);
        using RenderFrame actual = await session.RenderAsync(Request(source, 100_000));
        using TemporaryDirectory temp = new();
        using FfmpegVideoFrameProvider videos = new();
        var runtime = new ProjectRenderSessionFactory(new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"),
            temp.FilePath("cache")), videos, renderBackend: "vulkan");
        await using IRenderFrameSession expected = await runtime.CreateAsync(source);
        using RenderFrame reference = await expected.RenderAsync(Request(source, 100_000));
        Assert.Equal(reference.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
        Assert.Equal(pid, proxy.ProcessId);
        await session.DisposeAsync();
        Assert.Null(proxy.ProcessId);
    }

    [Fact]
    public async Task 真实包Worker软取消后同进程重建会话且恢复帧与新Runtime一致()
    {
        PackageWorkerRuntimeFixture.AssertFixedFont();
        ProjectSnapshot basis = PackageWorkerRuntimeFixture.CreateScale(BarrierSource());
        ProjectSnapshot source = PackageWorkerRuntimeFixture.Fingerprint(basis with
        { Composition = basis.Composition with { OutputDurationUs = 180_000_000 } });
        using TemporaryDirectory temp = new();
        using FfmpegVideoFrameProvider videos = new();
        var direct = new ProjectRenderSessionFactory(new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"),
            temp.FilePath("cache")), videos, renderBackend: "vulkan");
        await using IRenderFrameSession expected = await direct.CreateAsync(source);
        await using WorkerProcessProxy proxy = new(new(PackageWorkerRuntimeFixture.WorkerApphost(), cancellationGracePeriod: TimeSpan.FromSeconds(5))
            { RenderBackend = "gpu", ScriptCompilationService = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")) });
        byte[] bytes = PackageProjectSnapshotCodec.Encode(source);
        StaticWorkerSessionHandle old = await proxy.OpenPackageSessionAsync(bytes, TimeSpan.FromSeconds(30));
        int pid = Assert.IsType<int>(proxy.ProcessId);
        ulong baselineTicks = ReadCpuTicks(pid);
        using CancellationTokenSource cancel = new();
        Task<WorkerMessage> inFlight = proxy.RenderPackageSessionAsync(new(old.SessionId, old.SourceSha256,
            320, 180, 179_000_000, false, 1), TimeSpan.FromSeconds(30), cancel.Token);
        await WaitForCpuAdvance(pid, baselineTicks, inFlight);
        cancel.Cancel();
        OperationCanceledException cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlight);
        Assert.Equal("WorkerCooperativeCancellationException", cancelled.GetType().Name);
        Assert.Equal(pid, proxy.ProcessId);
        await proxy.ClosePackageSessionAsync(old, 2, TimeSpan.FromSeconds(20));
        StaticWorkerSessionHandle fresh = await proxy.OpenPackageSessionAsync(bytes, TimeSpan.FromSeconds(30));
        Assert.Equal(pid, proxy.ProcessId);
        Assert.NotEqual(old.SessionId, fresh.SessionId);
        WorkerMessage actual = await proxy.RenderPackageSessionAsync(new(fresh.SessionId, fresh.SourceSha256,
            320, 180, 100_000, false, 1), TimeSpan.FromSeconds(20));
        using RenderFrame reference = await expected.RenderAsync(Request(source, 100_000));
        Assert.Equal(reference.PixelMemory.ToArray(), actual.Payload);
        Assert.Equal(100_000L, actual.FrameTimeUs);
        await proxy.ClosePackageSessionAsync(fresh, 2, TimeSpan.FromSeconds(20));
        List<long> residentBytes = [];
        for (int cycle = 0; cycle < 8; cycle++)
        {
            StaticWorkerSessionHandle reopened = await proxy.OpenPackageSessionAsync(bytes, TimeSpan.FromSeconds(30));
            Assert.Equal(pid, proxy.ProcessId);
            WorkerMessage frame = await proxy.RenderPackageSessionAsync(new(reopened.SessionId, reopened.SourceSha256,
                320, 180, 100_000, false, 1), TimeSpan.FromSeconds(20));
            Assert.Equal(reference.PixelMemory.ToArray(), frame.Payload);
            await proxy.ClosePackageSessionAsync(reopened, 2, TimeSpan.FromSeconds(20));
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            residentBytes.Add(process.WorkingSet64);
        }
        using (var process = System.Diagnostics.Process.GetProcessById(pid))
            output.WriteLine($"Worker PID={pid}; start={process.StartTime.ToUniversalTime():O}; source={old.SourceSha256}; working-set bytes after successive session close: {string.Join(", ", residentBytes)}");
        await proxy.StopAsync();
        Assert.Null(proxy.ProcessId);
    }

    private static string BarrierSource() => """
        using System;
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;
        public sealed class PackEffect : IPureKaraokeEffect
        {
            public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
            public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
            {
                double peak = frame.Parameters.GetNumber("peak");
                if (frame.TimeUs == 179_000_000)
                {
                    double acc = 0;
                    for (long i = 0; i < 60_000_000L; i++)
                        acc += Math.Sin(i * 0.001);
                    if (double.IsNaN(acc)) peak = 1.0;
                }
                foreach (var token in frame.Presentations.TokensInBinding)
                    foreach (var geometry in frame.Geometry)
                        if (geometry.WithRuby == token.WithRuby)
                        {
                            output.AppendTransform(token.WithRuby, Affine2.ScaleAt(peak, geometry.BaseGeometry.AdvanceBounds.Center));
                            break;
                        }
            }
        }
        """;

    private static async Task WaitForCpuAdvance(int pid, ulong baselineTicks, Task pending)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        while (ReadCpuTicks(pid) < baselineTicks + 4)
        {
            if (pending.IsCompleted) throw new InvalidOperationException("Worker 渲染在 CPU 步进观测前结束。", pending.Exception);
            await Task.Delay(10, timeout.Token);
        }
        Assert.False(pending.IsCompleted);
    }

    private static ulong ReadCpuTicks(int pid) => RuntimeTestPlatformHelpers.ReadCpuTicks(pid);

    [Theory]
    [InlineData("compile")]
    [InlineData("compile-source")]
    public async Task 真实编译失败完整源诊断逐字段有序等价且无像素并可恢复(string kind)
    {
        PackageWorkerRuntimeFixture.AssertFixedFont();
        using TemporaryDirectory temp = new(); using FfmpegVideoFrameProvider videos = new();
        var runtime = new ProjectRenderSessionFactory(new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), temp.FilePath("cache")), videos, renderBackend: "vulkan");
        var factory = new WorkerPackageRenderFrameSessionFactory(new(PackageWorkerRuntimeFixture.WorkerApphost()) { RenderBackend = "gpu" }, TimeSpan.FromSeconds(30));
        ProjectSnapshot invalid = PackageWorkerRuntimeFixture.DiagnosticSource(kind);
        ProjectEffectCompilationException expected = await Assert.ThrowsAsync<ProjectEffectCompilationException>(() => runtime.CreateAsync(invalid).AsTask());
        RenderSessionFailureException error = await Assert.ThrowsAsync<RenderSessionFailureException>(() => factory.CreateAsync(invalid).AsTask());
        Assert.Equal("WorkerPackageCompilationFailed", error.Code);
        Assert.Null(error.DiagnosticSnapshot);
        Assert.NotEmpty(expected.Diagnostics);
        Assert.Equal(expected.Diagnostics.Select(d => new RenderSourceDiagnostic(d.PackageId, d.Revision, d.InstanceId, d.ExportId,
            d.Source.Code, d.Source.Message, d.Source.IsError, d.Source.Path, d.Source.Line, d.Source.Column)).ToArray(), error.SourceDiagnostics.ToArray());
        Assert.All(error.SourceDiagnostics, d =>
        {
            Assert.Equal("src/PackEffect.cs", d.Path); Assert.True(d.Line > 0); Assert.True(d.Column > 0);
            if (kind == "compile-source") { Assert.Null(d.InstanceId); Assert.Null(d.ExportId); }
            else { Assert.Equal("actual-graph", d.InstanceId); Assert.Equal("controller", d.ExportId); }
        });
        ProjectSnapshot healthy = PackageWorkerRuntimeFixture.Create("scale");
        await using IRenderFrameSession session = await factory.CreateAsync(healthy);
        using RenderFrame actual = await session.RenderAsync(Request(healthy, 100_000));
        Assert.Empty(actual.DiagnosticSnapshot.ReadAll());
        Assert.False(actual.DiagnosticSnapshot.HasError);
        output.WriteLine($"Actual {kind} complete source diagnostics={error.SourceDiagnostics.Length}; no session/frame published, healthy recovery passed.");
    }

    [Theory]
    [InlineData("image")]
    [InlineData("material")]
    [InlineData("script-material")]
    public async Task 真实图片效果与两类材质诊断完整保留且不透明与透明帧像素等价(string kind)
    {
        PackageWorkerRuntimeFixture.AssertFixedFont();
        using TemporaryDirectory temp = new(); using FfmpegVideoFrameProvider videos = new();
        ProjectSnapshot source = PackageWorkerRuntimeFixture.DiagnosticSource(kind);
        var runtime = new ProjectRenderSessionFactory(new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), temp.FilePath("cache")), videos, renderBackend: "vulkan");
        await using IRenderFrameSession direct = await runtime.CreateAsync(source);
        var factory = new WorkerPackageRenderFrameSessionFactory(new(PackageWorkerRuntimeFixture.WorkerApphost()) { RenderBackend = "gpu" }, TimeSpan.FromSeconds(30));
        await using IRenderFrameSession session = await factory.CreateAsync(source);
        foreach (bool transparent in new[] { false, true })
        foreach (long time in new long[] { 100_000, 200_000, 0, 500_000 })
        {
            using RenderFrame expected = await direct.RenderAsync(Request(source, time, transparent));
            using RenderFrame actual = await session.RenderAsync(Request(source, time, transparent));
            Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
            Assert.Equal(expected.ImageEffectDiagnostics.ToArray(), actual.ImageEffectDiagnostics.ToArray());
            Assert.Equal(expected.MaterialDiagnostics.ToArray(), actual.MaterialDiagnostics.ToArray());
            Assert.Equal(expected.DiagnosticSnapshot.ReadAll().Select(value => (value.Sequence, value.Diagnostic)),
                actual.DiagnosticSnapshot.ReadAll().Select(value => (value.Sequence, value.Diagnostic)));
            if (!transparent)
            {
                Assert.Empty(actual.ImageEffectDiagnostics);
                Assert.Empty(actual.MaterialDiagnostics);
            }
            else if (kind == "image")
            {
                Assert.NotEmpty(actual.ImageEffectDiagnostics); Assert.Empty(actual.MaterialDiagnostics);
                Assert.All(actual.ImageEffectDiagnostics, d => { Assert.Equal("background", d.InputId); Assert.Equal("TransparentBackgroundDependency", d.Code); });
            }
            else
            {
                Assert.NotEmpty(actual.MaterialDiagnostics); Assert.Empty(actual.ImageEffectDiagnostics);
                Assert.All(actual.MaterialDiagnostics, d =>
                {
                    Assert.Equal("background", d.InputId); Assert.Equal("TransparentBackgroundDependency", d.Code);
                    if (kind == "material")
                    {
                        PreparedMaterialOwner.Source owner = Assert.IsType<PreparedMaterialOwner.Source>(d.Owner);
                        Assert.True(owner.Reference.Path is "lyricStyle.unsung.fill" or "lyricStyle.sung.fill", owner.Reference.Path);
                        Assert.Equal("fill", owner.Reference.Region);
                        Assert.Equal(new MaterialSpec.PackageMaterial(source.Effects.Packages[0].PackageId,
                            source.Effects.Packages[0].Revision, "ink", EffectParameterOverrides.Parse("{\"amount\":0.3}")), owner.Reference.Material);
                        Assert.Equal("{\"amount\":0.3}", owner.Reference.Material.Parameters.Json);
                        Assert.Equal(owner.Reference.Path.EndsWith(".sung.fill", StringComparison.Ordinal) ? PreparedMaterialSlot.SungFill : PreparedMaterialSlot.UnsungFill, owner.Slot);
                    }
                    else
                    {
                        PreparedMaterialOwner.Script owner = Assert.IsType<PreparedMaterialOwner.Script>(d.Owner);
                        Assert.Equal("actual-graph", owner.InstanceId.Value); Assert.False(string.IsNullOrWhiteSpace(owner.ArtifactHash));
                        Assert.Equal(new EffectPackageIdentity(source.Effects.Packages[0].PackageId, source.Effects.Packages[0].Revision, "controller"), owner.PackageIdentity);
                    }
                });
            }
        }
        output.WriteLine($"Actual {kind}: 8 opaque/transparent frames including backward seek, complete ordered image/material diagnostics and byteexact pixels passed.");
    }

    [Fact]
    public async Task 真实Runtime作者Error诊断完整传输并禁止发布像素()
    {
        PackageWorkerRuntimeFixture.AssertFixedFont();
        using TemporaryDirectory temp = new(); using FfmpegVideoFrameProvider videos = new();
        var runtime = new ProjectRenderSessionFactory(new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), temp.FilePath("cache")), videos, renderBackend: "vulkan");
        var factory = new WorkerPackageRenderFrameSessionFactory(new(PackageWorkerRuntimeFixture.WorkerApphost()) { RenderBackend = "gpu" }, TimeSpan.FromSeconds(30));
        ProjectSnapshot source = PackageWorkerRuntimeFixture.RuntimeError();
        await using IRenderFrameSession direct = await runtime.CreateAsync(source);
        EffectFailureException expected = await Assert.ThrowsAsync<EffectFailureException>(() => direct.RenderAsync(Request(source, 100_000)).AsTask());
        await using IRenderFrameSession session = await factory.CreateAsync(source);
        RenderSessionFailureException error = await Assert.ThrowsAsync<RenderSessionFailureException>(() => session.RenderAsync(Request(source, 100_000)).AsTask());
        Assert.Equal("ScriptExecutionFailed", error.Code);
        Assert.Equal(expected.Code, error.Code);
        Assert.Contains("真实固定包作者错误🎵", expected.InnerException!.Message, StringComparison.Ordinal);
        Assert.Empty(error.DiagnosticSnapshot!.ReadAll());
        Assert.False(error.DiagnosticSnapshot.HasError);
        PreparedFrameException stale = await Assert.ThrowsAsync<PreparedFrameException>(() => session.RenderAsync(Request(source, 200_000)).AsTask());
        Assert.Equal("WorkerPackageSessionFailed", stale.Code);
    }

    [Theory]
    [InlineData("scale", false)] [InlineData("scale", true)]
    [InlineData("graph", false)] [InlineData("graph", true)]
    [InlineData("sprite", false)] [InlineData("sprite", true)]
    [InlineData("material", false)] [InlineData("material", true)]
    public async Task 固定包真实持久Worker与直接Runtime逐帧像素诊断等价且效果实际贡献像素(string kind, bool transparent)
    {
        Assert.True(ProcessGuards.TryEnableChildReaping(), "This bounded process isolation exit requires process guards.");
        PackageWorkerRuntimeFixture.AssertFixedFont();
        using TemporaryDirectory temp = new(); using FfmpegVideoFrameProvider videos = new();
        var runtime = new ProjectRenderSessionFactory(new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), temp.FilePath("cache")), videos, renderBackend: "vulkan");
        ProjectSnapshot source = PackageWorkerRuntimeFixture.Create(kind);
        ProjectSnapshot removed = PackageWorkerRuntimeFixture.WithoutContribution(source, kind);
        byte[] bytes = PackageProjectSnapshotCodec.Encode(source);
        await using WorkerProcessProxy proxy = new(new(PackageWorkerRuntimeFixture.WorkerApphost()) { RenderBackend = "gpu" });
        StaticWorkerSessionHandle handle = await proxy.OpenPackageSessionAsync(bytes, TimeSpan.FromSeconds(30));
        int pid = Assert.IsType<int>(proxy.ProcessId);
        long startIdentity = RuntimeTestPlatformHelpers.GetStartIdentityUtc(pid);
        OwnedProcessGroup? group = null;
        if (OperatingSystem.IsLinux())
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(pid);
            group = OwnedProcessGroup.TryCapture(process); Assert.NotNull(group);
        }
        await using IRenderFrameSession expectedSession = await runtime.CreateAsync(source);
        await using IRenderFrameSession counterfactual = await runtime.CreateAsync(removed);
        var expectedCursor = new RuntimeDiagnosticCursor(); var actualCursor = new RuntimeDiagnosticCursor();
        long sequence = 0;
        foreach (long time in new long[] { 0, 100_000, 300_000, 500_000, 100_000, 500_000 })
        {
            var request = Request(source, time, transparent);
            using RenderFrame expected = await expectedSession.RenderAsync(request);
            using RenderFrame plain = await counterfactual.RenderAsync(Request(removed, time, transparent));
            WorkerMessage actual = await proxy.RenderPackageSessionAsync(new(handle.SessionId, handle.SourceSha256,
                320, 180, time, transparent, ++sequence, (int)request.Quality), TimeSpan.FromSeconds(20));
            Assert.Equal(pid, proxy.ProcessId);
            Assert.Equal(handle.SessionId.ToString("N"), actual.SessionId); Assert.Equal(handle.SourceSha256, actual.SourceSha256);
            Assert.Equal(sequence, actual.FrameSequence); Assert.Equal(time, actual.FrameTimeUs);
            Assert.Equal(320, actual.FrameWidth); Assert.Equal(180, actual.FrameHeight); Assert.Equal(1280, actual.FrameStride);
            Assert.Equal("bgra8888-premultiplied", actual.FrameFormat); Assert.Equal((int)request.Quality, actual.FrameQuality);
            Assert.Equal(expected.PixelMemory.ToArray(), actual.Payload);
            Assert.False(plain.PixelMemory.Span.SequenceEqual(actual.Payload), $"{kind} at {time} did not contribute pixels.");
            RuntimeDiagnosticSnapshot diagnostics = DecodeDiagnostics(Assert.IsType<WorkerDiagnosticsSnapshotDto>(actual.Diagnostics));
            Assert.Equal(expected.DiagnosticSnapshot.HasError, diagnostics.HasError);
            Assert.Equal(expected.DiagnosticSnapshot.ReadAll().Select(value => (value.Sequence, value.Diagnostic)),
                diagnostics.ReadAll().Select(value => (value.Sequence, value.Diagnostic)));
            Assert.Equal(expectedCursor.ReadNew(expected.DiagnosticSnapshot).Select(value => value.Diagnostic),
                actualCursor.ReadNew(diagnostics).Select(value => value.Diagnostic));
            if (kind != "material") Assert.Empty(diagnostics.ReadAll());
        }
        await proxy.ClosePackageSessionAsync(handle, ++sequence, TimeSpan.FromSeconds(20));
        StaticWorkerSessionHandle reopened = await proxy.OpenPackageSessionAsync(bytes, TimeSpan.FromSeconds(30));
        Assert.Equal(pid, proxy.ProcessId); Assert.NotEqual(handle.SessionId, reopened.SessionId);
        Assert.Equal(handle.SourceSha256, reopened.SourceSha256);
        WorkerMessage repeated = await proxy.RenderPackageSessionAsync(new(reopened.SessionId, reopened.SourceSha256,
            320, 180, 300_000, transparent, 1), TimeSpan.FromSeconds(20));
        using RenderFrame repeatedExpected = await expectedSession.RenderAsync(Request(source, 300_000, transparent));
        Assert.Equal(repeatedExpected.PixelMemory.ToArray(), repeated.Payload);
        await proxy.ClosePackageSessionAsync(reopened, 2, TimeSpan.FromSeconds(20));
        await proxy.StopAsync(); Assert.Null(proxy.ProcessId);
        if (group is not null) Assert.Empty(group.ObserveMembers());
        Assert.False(RuntimeTestPlatformHelpers.IsProcessAlive(pid, startIdentity));
        if (kind == "material")
        {
            Assert.Empty(source.Effects.Instances); Assert.Null(source.Effects.InputsFingerprint);
            Assert.NotNull(source.EffectInputsFingerprint); Assert.False(Directory.Exists(temp.FilePath("cache")));
        }
        output.WriteLine($"Actual {kind}/{transparent} Worker={pid}; 6 frames, seek, complete diagnostics, close/reopen and process release passed.");
    }

    internal static RenderRequest Request(ProjectSnapshot snapshot, long time, bool transparent = false) => RenderRequest.At(snapshot, time) with
    { Target = new() { PixelWidth = 320, PixelHeight = 180 }, TransparentBackground = transparent, Quality = RenderQuality.Standard };

    // Independently reconstruct all transported fields. Epochs remain the actual
    // Worker identities; comparing to a direct session ignores only those GUIDs.
    private static RuntimeDiagnosticSnapshot DecodeDiagnostics(WorkerDiagnosticsSnapshotDto snapshot) => RuntimeDiagnosticSnapshot.FromOccurrences(
        snapshot.Occurrences.Select(value => new RuntimeDiagnosticOccurrence(value.Epoch, value.Sequence,
            new(new(value.Diagnostic.InstanceId), value.Diagnostic.ArtifactHash, value.Diagnostic.Stage, value.Diagnostic.TimeUs,
                new(Enum.Parse<DiagnosticSeverity>(value.Diagnostic.Severity), value.Diagnostic.Code, value.Diagnostic.Message,
                    value.Diagnostic.Location is { } location ? new(location.Path, location.Line, location.Column) : null))
            { PackageIdentity = value.Diagnostic.Package is { } package ? new(package.PackageId, package.Revision, package.ExportId) : null })));
}
