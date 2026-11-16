using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class WorkerPackageSessionFactoryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task 包调用在代理门闩排队时取消仍可复用()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using Probe probe = await Probe.CreateAsync("valid");
        ProjectSnapshot snapshot = Snapshot();
        await using IRenderFrameSession session = await probe.Factory().CreateAsync(snapshot);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var proxy = (WorkerProcessProxy)session.GetType().GetFields(flags).Single(f => f.FieldType == typeof(WorkerProcessProxy)).GetValue(session)!;
        var gate = (SemaphoreSlim)typeof(WorkerProcessProxy).GetField("_callGate", flags)!.GetValue(proxy)!;
        int? pid = proxy.ProcessId;
        await gate.WaitAsync();
        try
        {
            using CancellationTokenSource cancel = new();
            Task<RenderFrame> pending = session.RenderAsync(RenderRequest.At(snapshot, 42), cancel.Token).AsTask();
            Assert.False(pending.IsCompleted);
            cancel.Cancel();
            OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal("WorkerInvocationNotStartedException", error.GetType().Name);
        }
        finally { gate.Release(); }
        using RenderFrame frame = await session.RenderAsync(RenderRequest.At(snapshot, 43));
        Assert.Equal(43, frame.TimeUs);
        Assert.Equal(pid, proxy.ProcessId);
    }

    [Theory]
    [InlineData("cap-engine")]
    [InlineData("cap-codec")]
    [InlineData("cap-profile")]
    [InlineData("cap-sdk")]
    [InlineData("cap-shader")]
    [InlineData("cap-references")]
    [InlineData("cap-reference-count")]
    [InlineData("cap-wrong-reference-count")]
    [InlineData("cap-missing-refs")]
    [InlineData("cap-missing-diagnostics-version")]
    [InlineData("cap-old-diagnostics-version")]
    [InlineData("cap-missing-package-profiles")]
    [InlineData("cap-only-v1")]
    [InlineData("cap-v2-sdk")]
    [InlineData("cap-v2-shader")]
    [InlineData("cap-foreign")]
    [InlineData("cap-foreign-session")]
    [InlineData("cap-missing-output-sha")]
    [InlineData("cap-unsolicited-cancel")]
    [InlineData("no-group")]
    [InlineData("local-empty-refs")]
    [InlineData("local-no-runtime")]
    public async Task 能力或实际隔离不匹配在发送任何包open字节前拒绝并释放Worker(string mode)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using Probe probe = await Probe.CreateAsync(mode);
        RenderSessionFailureException failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() =>
            CreateAndDisposeAsync(probe.Factory()));
        Assert.NotNull(failure.InnerException);
        Assert.Null(failure.DiagnosticSnapshot);
        Assert.False(probe.OpenSeen);
        await probe.AssertAllGoneAsync(output);
        output.WriteLine($"Package capability/guard rejection mode={mode}; no package open was sent; code={failure.Code}.");
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("cap-engine", false)]
    [InlineData("cap-old-diagnostics-version", false)]
    [InlineData("cap-missing-diagnostics-version", false)]
    [InlineData("cap-missing-package-profiles", false)]
    [InlineData("cap-only-v1", false)]
    [InlineData("local-empty-refs", false)]
    public async Task 公共包能力门禁实际握手且不发送包open(string mode, bool supported)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using Probe probe = await Probe.CreateAsync(mode);
        BackendErrorResponse? error = await probe.Factory().EnsureProfileAsync(PackageProjectSnapshotCodec.Profile);
        Assert.Equal(supported, error is null);
        if (!supported)
        {
            Assert.Equal(BackendErrorCode.PreviewUnavailable, error!.Code);
            Assert.Equal("WorkerPackageProfileMismatch", error.FailureCode);
        }
        Assert.False(probe.OpenSeen);
        await probe.AssertAllGoneAsync(output);
    }

    [Theory]
    [InlineData("runtime-error-full")]
    [InlineData("worker-runtime-error-full")]
    [InlineData("runtime-error-full-invalid-graph")]
    [InlineData("worker-runtime-error-full-invalid-source")]
    [InlineData("worker-runtime-error-full-invalid-compilation")]
    public async Task 实际协议失败保完整同帧图效果材质旁报且不发布像素(string mode)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        var p = MixedEffectPackageFixture.Contents(background: true);
        var project = Project.Create("full failure", Composition.Create(8, 4, FrameRate.Fps30, outputDurationUs: 1_000_000), LyricsDocument.Create([])) with
        { Effects = new([p], [new() { Id = "instance", Name = "instance", PackageId = p.PackageId, Revision = p.Revision,
            ExportId = "controller", Binding = new(ProjectEffectBindingKind.Project) }]) };
        ProjectSnapshot snapshot = ProjectSnapshot.FromProject(project);
        ResourceCatalog catalog = new(p.PackageId, p.Revision, ["ink"], ["tint"], []);
        using RenderFrame expected = new(new byte[128], new() { PixelWidth = 8, PixelHeight = 4 }, PixelFormat.Bgra8888Premultiplied, 42)
        {
            ImageEffectDiagnostics = [new(new("instance"), new string('a', 64), catalog.GetGraph("tint"), "background", "TransparentBackgroundDependency")],
            MaterialDiagnostics = [new(new PreparedMaterialOwner.Script(new("instance"), new string('b', 64))
                { PackageIdentity = new(p.PackageId, p.Revision, "controller") }, catalog.GetMaterial("ink"), "background", "TransparentBackgroundDependency")],
        };
        using Probe probe = await Probe.CreateAsync(mode, packageDiagnostics: PackageRenderDiagnosticsMapper.EncodeFrame(expected));
        await using IRenderFrameSession session = await probe.Factory().CreateAsync(snapshot);
        RenderSessionFailureException failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => session.RenderAsync(RenderRequest.At(snapshot, 42)).AsTask());
        if (mode.Contains("invalid", StringComparison.Ordinal))
        {
            Assert.NotEqual("WorkerRuntimeDiagnosticError", failure.Code);
            Assert.Null(failure.DiagnosticSnapshot);
            Assert.Empty(failure.ImageEffectDiagnostics); Assert.Empty(failure.MaterialDiagnostics);
        }
        else
        {
            Assert.Equal("WorkerRuntimeDiagnosticError", failure.Code);
            Assert.True(failure.DiagnosticSnapshot!.HasError);
            Assert.Equal(expected.ImageEffectDiagnostics.ToArray(), failure.ImageEffectDiagnostics.ToArray());
            Assert.Equal(expected.MaterialDiagnostics.ToArray(), failure.MaterialDiagnostics.ToArray());
        }
        await probe.AssertAllGoneAsync(output, 2);
    }

    [Theory]
    [InlineData("open-foreign-frame")]
    [InlineData("open-foreign-delay")]
    [InlineData("open-foreign-validation-count")]
    public async Task Open空回执不能携带像素或其它操作元数据(string mode)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using Probe probe = await Probe.CreateAsync(mode);
        var failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => CreateAndDisposeAsync(probe.Factory()));
        Assert.True(probe.OpenSeen);
        Assert.Null(failure.DiagnosticSnapshot);
        await probe.AssertAllGoneAsync(output);
    }

    [Fact]
    public async Task 能力与打开关闭回执拒绝所有未协商帧字段和伪错误取消形状()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        string[] fields = ["frameWidth", "frameHeight", "frameStride", "frameTimeUs", "frameFormat",
            "frameTransparentBackground", "frameQuality", "diagnostics"];
        foreach (string operation in new[] { "cap", "open", "close" })
        foreach (string field in fields)
        {
            output.WriteLine($"Reject package response shape operation={operation}, foreignField={field}.");
            using Probe probe = await Probe.CreateAsync($"shape:{operation}:{field}");
            if (operation == "close")
            {
                IRenderFrameSession session = await probe.Factory().CreateAsync(Snapshot());
                await Assert.ThrowsAsync<WorkerProtocolException>(() => session.DisposeAsync().AsTask());
            }
            else await Assert.ThrowsAsync<RenderSessionFailureException>(() => CreateAndDisposeAsync(probe.Factory()));
            await probe.AssertAllGoneAsync(output);
        }
        foreach (string operation in new[] { "cap", "open", "close" })
        foreach (string field in new[] { "error-without-message", "error-and-cancel" })
        {
            output.WriteLine($"Reject package response error shape operation={operation}, foreignField={field}.");
            using Probe probe = await Probe.CreateAsync($"shape:{operation}:{field}");
            if (operation == "close")
            {
                IRenderFrameSession session = await probe.Factory().CreateAsync(Snapshot());
                var failure = await Assert.ThrowsAsync<WorkerProtocolException>(() => session.DisposeAsync().AsTask());
                Assert.NotEqual("FakeAuthorFailure", failure.Code);
            }
            else
            {
                var failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => CreateAndDisposeAsync(probe.Factory()));
                Assert.NotEqual("FakeAuthorFailure", failure.Code);
            }
            await probe.AssertAllGoneAsync(output);
        }
    }

    [Theory]
    [InlineData("cap-engine")]
    [InlineData("cap-malformed-json")]
    public async Task 直接proxy能力解码或身份错配立即排水不依赖工厂清理(string mode)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using Probe probe = await Probe.CreateAsync(mode);
        await using WorkerProcessProxy proxy = new(probe.Options());
        await Assert.ThrowsAsync<WorkerProtocolException>(() => proxy.GetPackageCapabilitiesAsync(TimeSpan.FromSeconds(3)));
        await probe.AssertAllGoneAsync(output);
    }

    [Fact]
    public async Task 能力回执后进程退出不能向新进程发送未经其能力验证的包open()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using Probe probe = await Probe.CreateAsync("cap-exit-first");
        Exception? failure = await Record.ExceptionAsync(() => CreateAndDisposeAsync(probe.Factory()));
        Assert.False(probe.UnverifiedOpenSeen);
        Assert.NotNull(failure);
        Assert.IsType<RenderSessionFailureException>(failure);
        await probe.AssertAllGoneAsync(output);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("source")]
    [InlineData("sequence")]
    [InlineData("request-id")]
    [InlineData("input-sha")]
    [InlineData("output-sha")]
    [InlineData("time")]
    [InlineData("transparent")]
    [InlineData("quality")]
    [InlineData("size")]
    [InlineData("missing-diagnostics")]
    [InlineData("missing-package-diagnostics")]
    [InlineData("source-on-frame")]
    [InlineData("unsolicited-cancel")]
    [InlineData("foreign-task")]
    [InlineData("foreign-progress")]
    [InlineData("foreign-error")]
    [InlineData("foreign-error-input")]
    [InlineData("runtime-error")]
    [InlineData("worker-runtime-error")]
    public async Task 不可信帧终止会话并排水Worker和实际同组子进程(string mode)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using Probe probe = await Probe.CreateAsync(mode);
        ProjectSnapshot snapshot = Snapshot();
        await using IRenderFrameSession session = await probe.Factory().CreateAsync(snapshot);
        var failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => session.RenderAsync(RenderRequest.At(snapshot, 42)).AsTask());
        Assert.IsType<WorkerProtocolException>(failure.InnerException);
        if (mode is "foreign-error" or "foreign-error-input") Assert.NotEqual("ForeignFailure", failure.Code);
        if (mode is "runtime-error" or "worker-runtime-error")
        {
            Assert.Equal("WorkerRuntimeDiagnosticError", failure.Code);
            Assert.True(failure.DiagnosticSnapshot!.HasError);
            Assert.Equal("真实错误", Assert.Single(failure.DiagnosticSnapshot.ReadAll()).Diagnostic.Diagnostic.Message);
        }
        else Assert.Null(failure.DiagnosticSnapshot);
        await probe.AssertAllGoneAsync(output, expectedCount: 2);
        var terminal = await Assert.ThrowsAsync<PreparedFrameException>(() => session.RenderAsync(RenderRequest.At(snapshot, 43)).AsTask());
        Assert.Equal("WorkerPackageSessionFailed", terminal.Code);
        output.WriteLine($"Package malformed frame mode={mode}; rejected terminally with code={failure.Code}.");
    }

    [Theory]
    [InlineData("WorkerPackageDiagnosticsBudgetExceeded")]
    [InlineData("WorkerPackageUnavailable")]
    public async Task 预算或能力失败显式保留编号且不伪造Runtime发生(string code)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using Probe probe = await Probe.CreateAsync(code);
        var failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => CreateAndDisposeAsync(probe.Factory()));
        Assert.Equal(code, failure.Code);
        Assert.Null(failure.DiagnosticSnapshot);
        await probe.AssertAllGoneAsync(output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 进入实际render后deadline与用户取消分别保编号与OCE并排水所有观察成员(bool callerCancel)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using Probe probe = await Probe.CreateAsync("hang-once");
        var factory = probe.Factory(TimeSpan.FromSeconds(2));
        ProjectSnapshot snapshot = Snapshot();
        await using IRenderFrameSession first = await factory.CreateAsync(snapshot);
        using CancellationTokenSource caller = new();
        Task<RenderFrame> pending = first.RenderAsync(RenderRequest.At(snapshot, 42), caller.Token).AsTask();
        await probe.WaitForRenderAsync();
        await probe.AssertAllAliveAsync(expectedCount: 2);
        if (callerCancel) caller.Cancel();
        if (callerCancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else Assert.Equal("WorkerPackageTimedOut", (await Assert.ThrowsAsync<RenderSessionFailureException>(() => pending)).Code);
        await probe.AssertAllGoneAsync(output, expectedCount: 2);
        await using IRenderFrameSession second = await factory.CreateAsync(snapshot);
        using RenderFrame frame = await second.RenderAsync(RenderRequest.At(snapshot, 42));
        Assert.Equal(8 * 4 * 4, frame.PixelMemory.Length);
        await second.DisposeAsync();
        await probe.AssertAllGoneAsync(output, expectedCount: 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 严格合作回执后用户取消可复用而deadline仍排水(bool callerCancel)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using Probe probe = await Probe.CreateAsync("cooperative-cancel");
        ProjectSnapshot snapshot = Snapshot();
        await using IRenderFrameSession session = await probe.Factory(TimeSpan.FromSeconds(2)).CreateAsync(snapshot);
        var handleField = session.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single(field => field.FieldType == typeof(StaticWorkerSessionHandle));
        var initial = (StaticWorkerSessionHandle)handleField.GetValue(session)!;
        var prepared = Assert.IsType<RetainedPreparedScriptInput>(initial.PreparedInput);
        using var metadata = JsonDocument.Parse(prepared.Input.Envelope.AsMemory(8));
        string inputRoot = metadata.RootElement.GetProperty("Root").GetString()!;
        Assert.True(Directory.Exists(inputRoot));
        using CancellationTokenSource caller = new();
        Task<RenderFrame> pending = session.RenderAsync(RenderRequest.At(snapshot, 42), caller.Token).AsTask();
        await probe.WaitForRenderAsync();
        await probe.AssertAllAliveAsync(expectedCount: 2);
        if (callerCancel) caller.Cancel();
        if (callerCancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else Assert.Equal("WorkerPackageTimedOut", (await Assert.ThrowsAsync<RenderSessionFailureException>(() => pending)).Code);
        Assert.True(probe.CancellationAcknowledged);
        if (callerCancel)
        {
            await probe.AssertAllAliveAsync(expectedCount: 2);
            using RenderFrame recovered = await session.RenderAsync(RenderRequest.At(snapshot, 43));
            Assert.Equal(43, recovered.TimeUs);
            var reopened = (StaticWorkerSessionHandle)handleField.GetValue(session)!;
            Assert.NotEqual(initial.SessionId, reopened.SessionId);
            Assert.Equal(initial.SourceSha256, reopened.SourceSha256);
            Assert.Same(prepared, reopened.PreparedInput);
            Assert.True(Directory.Exists(inputRoot));
            await session.DisposeAsync();
            Assert.False(Directory.Exists(inputRoot));
            await probe.AssertAllGoneAsync(output, expectedCount: 2);
            return;
        }
        await probe.AssertAllGoneAsync(output, expectedCount: 2);
        Assert.False(Directory.Exists(inputRoot));
        var terminal = await Assert.ThrowsAsync<PreparedFrameException>(() => session.RenderAsync(RenderRequest.At(snapshot, 43)).AsTask());
        Assert.Equal("WorkerPackageSessionFailed", terminal.Code);
    }

    [Theory]
    [InlineData("cooperative-invalid")]
    [InlineData("cooperative-stale")]
    public async Task 包取消回执无效或过期时回收进程组(string mode)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using Probe probe = await Probe.CreateAsync(mode);
        ProjectSnapshot snapshot = Snapshot();
        await using IRenderFrameSession session = await probe.Factory().CreateAsync(snapshot);
        using CancellationTokenSource cancellation = new();
        Task<RenderFrame> pending = session.RenderAsync(RenderRequest.At(snapshot, 42), cancellation.Token).AsTask();
        await probe.WaitForRenderAsync();
        cancellation.Cancel();
        await Assert.ThrowsAsync<RenderSessionFailureException>(() => pending);
        await probe.AssertAllGoneAsync(output, expectedCount: 2);
    }

    [Fact]
    public async Task 调用前取消不回收包Worker且随后可渲染()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using Probe probe = await Probe.CreateAsync("valid");
        ProjectSnapshot snapshot = Snapshot();
        await using IRenderFrameSession session = await probe.Factory().CreateAsync(snapshot);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RenderAsync(RenderRequest.At(snapshot, 42), cancellation.Token).AsTask());
        using RenderFrame frame = await session.RenderAsync(RenderRequest.At(snapshot, 43));
        Assert.Equal(43, frame.TimeUs);
        await probe.AssertAllAliveAsync(expectedCount: 2);
    }

    [Fact]
    public async Task 本地非法请求拒绝后同一固定会话仍可渲染并保完整发生身份()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        var occurrence = StaticWorkerContractTests.Occurrence(1);
        occurrence = occurrence with { Diagnostic = occurrence.Diagnostic with { Package = new("package", "sha256:" + new string('a', 64), "export") } };
        using Probe probe = await Probe.CreateAsync("valid", new([occurrence], false));
        ProjectSnapshot snapshot = Snapshot();
        await using IRenderFrameSession session = await probe.Factory().CreateAsync(snapshot);
        Assert.Equal("WorkerPackageSessionInputsMismatch", (await Assert.ThrowsAsync<PreparedFrameException>(() =>
            session.RenderAsync(RenderRequest.At(snapshot with { }, 42)).AsTask())).Code);
        Assert.Equal("WorkerPackageRequestUnsupported", (await Assert.ThrowsAsync<PreparedFrameException>(() =>
            session.RenderAsync(RenderRequest.At(snapshot, 42) with { LayoutLineId = "line" }).AsTask())).Code);
        Assert.Equal("WorkerPackageRequestUnsupported", (await Assert.ThrowsAsync<PreparedFrameException>(() =>
            session.RenderAsync(RenderRequest.At(snapshot, 42) with { Quality = (RenderQuality)999 }).AsTask())).Code);
        await Assert.ThrowsAsync<WorkerProtocolException>(() => session.RenderAsync(RenderRequest.At(snapshot, 42)
            with { Target = new() { PixelWidth = 1920, PixelHeight = 1080 } }).AsTask());
        using RenderFrame first = await session.RenderAsync(RenderRequest.At(snapshot, 42));
        RuntimeDiagnosticOccurrence actual = Assert.Single(first.DiagnosticSnapshot.ReadAll());
        Assert.Equal(occurrence.Epoch, actual.Epoch); Assert.Equal(occurrence.Sequence, actual.Sequence);
        Assert.Equal(new EffectPackageIdentity("package", "sha256:" + new string('a', 64), "export"), actual.Diagnostic.PackageIdentity);
        Assert.Equal(new SourceLocation("src/code.cs", 2, 3), actual.Diagnostic.Diagnostic.Location);
        var cursor = new RuntimeDiagnosticCursor();
        Assert.Single(cursor.ReadNew(first.DiagnosticSnapshot));
        using RenderFrame second = await session.RenderAsync(RenderRequest.At(snapshot, 43));
        Assert.Empty(cursor.ReadNew(second.DiagnosticSnapshot));
        await session.DisposeAsync();
        await probe.AssertAllGoneAsync(output, expectedCount: 2);
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("image-old-worker", false)]
    [InlineData("cap-engine", false)]
    [InlineData("cap-references", false)]
    [InlineData("image-budget", false)]
    [InlineData("image-base", false)]
    [InlineData("image-profile", false)]
    [InlineData("image-decoder", false)]
    [InlineData("image-missing-budget", false)]
    [InlineData("cap-foreign-session", false)]
    [InlineData("no-group", false)]
    public async Task 图片能力独立握手并拒绝旧包Worker及任何预算基础profile错配(string mode, bool supported)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using Probe probe = await Probe.CreateAsync(mode);
        BackendErrorResponse? error = await probe.Factory().EnsureProfileAsync(BackendSnapshotProfiles.ProjectImagePreview);
        Assert.Equal(supported, error is null);
        if (!supported)
        {
            Assert.Equal(BackendErrorCode.PreviewUnavailable, error!.Code);
            Assert.Equal("WorkerImagePreviewProfileMismatch", error.FailureCode);
        }
        Assert.False(probe.OpenSeen);
        await probe.AssertAllGoneAsync(output);
    }

    [Fact]
    public async Task 图片能力回执后进程退出不得向新进程发送图片open()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using Probe probe = await Probe.CreateAsync("cap-exit-first");
        byte[] bytes = [1, 2, 3];
        var snapshot = ImageSnapshot(bytes);
        IRenderFrameSessionFactory factory = probe.Factory().Bind(snapshot, bytes);
        await Assert.ThrowsAsync<RenderSessionFailureException>(() => factory.CreateAsync(snapshot).AsTask());
        Assert.False(probe.UnverifiedOpenSeen);
        await probe.AssertAllGoneAsync(output);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("session")]
    [InlineData("missing-package-diagnostics")]
    [InlineData("worker-runtime-error")]
    public async Task 图片open使用独立能力且帧沿原完整诊断与严格关联校验(string mode)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using Probe probe = await Probe.CreateAsync(mode);
        byte[] bytes = [1, 2, 3];
        var snapshot = ImageSnapshot(bytes);
        IRenderFrameSessionFactory factory = probe.Factory().Bind(snapshot, bytes);
        bytes[0] = 99;
        await using IRenderFrameSession session = await factory.CreateAsync(snapshot);
        Assert.True(probe.OpenSeen);
        if (mode == "valid")
        {
            using RenderFrame frame = await session.RenderAsync(RenderRequest.At(snapshot, 42));
            Assert.Equal(8 * 4 * 4, frame.PixelMemory.Length);
            Assert.Empty(frame.ImageEffectDiagnostics); Assert.Empty(frame.MaterialDiagnostics);
            await session.DisposeAsync();
        }
        else
            await Assert.ThrowsAsync<RenderSessionFailureException>(() => session.RenderAsync(RenderRequest.At(snapshot, 42)).AsTask());
        await probe.AssertAllGoneAsync(output, expectedCount: 2);
    }

    [Theory]
    [InlineData("deadline")]
    [InlineData("cancel")]
    [InlineData("close")]
    public async Task 图片会话deadline用户取消和活动关闭排水全部实际观察成员(string operation)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using Probe probe = await Probe.CreateAsync("hang-once");
        byte[] bytes = [1, 2, 3];
        var snapshot = ImageSnapshot(bytes);
        var factory = probe.Factory(TimeSpan.FromSeconds(2)).Bind(snapshot, bytes);
        await using IRenderFrameSession session = await factory.CreateAsync(snapshot);
        using CancellationTokenSource caller = new();
        Task<RenderFrame> pending = session.RenderAsync(RenderRequest.At(snapshot, 42), caller.Token).AsTask();
        await probe.WaitForRenderAsync();
        await probe.AssertAllAliveAsync(2);
        if (operation == "cancel") caller.Cancel();
        if (operation == "close") await session.DisposeAsync();
        if (operation == "deadline")
            Assert.Equal("WorkerPackageTimedOut", (await Assert.ThrowsAsync<RenderSessionFailureException>(() => pending)).Code);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await probe.AssertAllGoneAsync(output, 2);
    }

    private static ProjectSnapshot ImageSnapshot(byte[] bytes)
    {
        var media = new MediaFileRevision(WorkerProtocol.ComputeSha256(bytes), bytes.Length, "fixed.png");
        return ProjectSnapshot.FromProject(Project.Create("image IPC", Composition.Create(8, 4, FrameRate.Fps30, outputDurationUs: 1_000_000),
            LyricsDocument.Create([])) with { Background = new() { Kind = BackgroundKind.Image, Path = media.RelativePath, Media = media } });
    }

    private static ProjectSnapshot Snapshot() => ProjectSnapshot.FromProject(Project.Create("package IPC corruption fixture",
        Composition.Create(8, 4, FrameRate.Fps30, outputDurationUs: 1_000_000), LyricsDocument.Create([])));

    private static async Task CreateAndDisposeAsync(WorkerPackageRenderFrameSessionFactory factory)
    {
        await using IRenderFrameSession session = await factory.CreateAsync(Snapshot());
    }

    private static bool SameIdentity(int pid, long start) => HostingTestPlatformHelpers.IsSameProcess(pid, start);

    private sealed class Probe : IDisposable
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly TemporaryDirectory _temp = new();
        private readonly string _mode;
        private Probe(string mode) { _mode = mode; }
        public bool OpenSeen => File.Exists(_temp.FilePath("open"));
        public bool UnverifiedOpenSeen => File.Exists(_temp.FilePath("unverified-open"));
        public bool CancellationAcknowledged => File.Exists(_temp.FilePath("cancel-ack"));
        public static async Task<Probe> CreateAsync(string mode, WorkerDiagnosticsSnapshotDto? diagnostics = null, WorkerPackageDiagnosticsDto? packageDiagnostics = null)
        {
            Probe probe = new(mode);
            string executable = probe._temp.FilePath(OperatingSystem.IsWindows() ? "worker.py" : "worker");
            await File.WriteAllTextAsync(executable, "#!/usr/bin/python3\n" + Source);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string referenceRoot = ReferenceRoot();
            if (mode.StartsWith("local-", StringComparison.Ordinal))
            {
                string references = Path.Combine(probe._temp.Path, "script-refs", "net10.0");
                Directory.CreateDirectory(references);
                if (mode == "local-no-runtime") File.Copy(Path.Combine(referenceRoot, "net10.0", "System.Console.dll"), Path.Combine(references, "System.Console.dll"));
            }
            else HostingTestPlatformHelpers.CreateDirectoryLink(probe._temp.FilePath("script-refs"), referenceRoot);
            await File.WriteAllTextAsync(probe._temp.FilePath("diagnostics.json"), JsonSerializer.Serialize(diagnostics ?? new([], false), Json));
            await File.WriteAllTextAsync(probe._temp.FilePath("capabilities.json"), CapabilitiesJson());
            await File.WriteAllTextAsync(probe._temp.FilePath("package-diagnostics.json"), JsonSerializer.Serialize(packageDiagnostics ?? new(1, [], [], []), Json));
            return probe;
        }
        public WorkerProcessOptions Options() => OperatingSystem.IsWindows()
            ? HostingTestPlatformHelpers.PythonOptions(_temp.FilePath("worker.py"), [_mode, _temp.Path], TimeSpan.FromMilliseconds(80))
            : new(_temp.FilePath("worker"), [_mode, _temp.Path], TimeSpan.FromMilliseconds(80));
        public WorkerPackageRenderFrameSessionFactory Factory(TimeSpan? timeout = null) => new(Options(), timeout);
        public async Task WaitForRenderAsync()
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
            while (!File.Exists(_temp.FilePath("render"))) await Task.Delay(10, timeout.Token);
        }
        public async Task AssertAllAliveAsync(int expectedCount)
        {
            var members = await IdentitiesAsync(); Assert.Equal(expectedCount, members.Length);
            Assert.All(members, member => Assert.True(SameIdentity(member.Pid, member.Start)));
        }
        public async Task AssertAllGoneAsync(ITestOutputHelper output, int expectedCount = 1)
        {
            var members = await IdentitiesAsync(); Assert.Equal(expectedCount, members.Length);
            foreach (var member in members)
            {
                Assert.False(SameIdentity(member.Pid, member.Start));
                output.WriteLine($"Package controlled member PID={member.Pid}, startTicks={member.Start}, gone=true, mode={_mode}.");
            }
        }
        private async Task<(int Pid, long Start)[]> IdentitiesAsync()
        {
            using JsonDocument document = JsonDocument.Parse(await File.ReadAllBytesAsync(_temp.FilePath("identities.json")));
            return [.. document.RootElement.EnumerateArray().Select(value => (value[0].GetInt32(), value[1].GetInt64()))];
        }
        public void Dispose()
        {
            string link = _temp.FilePath("script-refs");
            if (Directory.Exists(link) && new DirectoryInfo(link).LinkTarget is not null)
                Directory.Delete(link, recursive: false);
            _temp.Dispose();
        }

        // Filled from the same actual deployment identity inspected by the Host;
        // only corruption/lifecycle is faked. Real package pixels use Runtime tests.
        private static string CapabilitiesJson()
        {
            string[] referencePaths = [.. Directory.EnumerateFiles(Path.Combine(ReferenceRoot(), "net10.0"), "*.dll")
                .Order(StringComparer.Ordinal), typeof(TokenId).Assembly.Location];
            Assert.Contains(referencePaths, path => Path.GetFileName(path) == "System.Runtime.dll");
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (string path in referencePaths)
            {
                Append(hash, Encoding.UTF8.GetBytes(Path.GetFileName(path)));
                Append(hash, SHA256.HashData(File.ReadAllBytes(path)));
            }
            var profile = ProjectEffectsJson.PackageProfile;
            return JsonSerializer.Serialize(new PackageRenderWorkerCapabilities(StaticProjectSnapshotCodec.EngineIdentity,
                PackageProjectSnapshotCodec.Profile, profile.PackageVersion, profile.SdkVersion, profile.ShaderProfile,
                Convert.ToHexStringLower(hash.GetHashAndReset()), referencePaths.Length,
                SupportedPackageProfiles: WorkerPackageProfileAdmission.Current), Json);
        }

        private static string ReferenceRoot()
        {
            DirectoryInfo? current = new(AppContext.BaseDirectory);
            while (current is not null && !File.Exists(Path.Combine(current.FullName, "MakoKaraGenerator.slnx"))) current = current.Parent;
            Assert.NotNull(current);
            return Path.Combine(current.FullName, "src", "MakoKaraGenerator.Backend.Worker", "bin", "Debug", "net10.0", "script-refs");
        }

        private static void Append(IncrementalHash hash, byte[] bytes)
        {
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length); hash.AppendData(bytes);
        }

        private const string Source = """
            import base64, copy, hashlib, json, os, struct, subprocess, sys, time
            mode, root = sys.argv[1:]
            if mode != 'no-group' and hasattr(os, 'setsid'): os.setsid()
            def path(name): return os.path.join(root, name)
            def identity(pid):
                if sys.platform == 'win32':
                    import ctypes
                    from ctypes import wintypes
                    k32 = ctypes.WinDLL('kernel32', use_last_error=True)
                    h = k32.OpenProcess(0x1000, False, pid)
                    c, e, k, u = wintypes.FILETIME(), wintypes.FILETIME(), wintypes.FILETIME(), wintypes.FILETIME()
                    try:
                        k32.GetProcessTimes(h, ctypes.byref(c), ctypes.byref(e), ctypes.byref(k), ctypes.byref(u))
                        return [pid, (c.dwHighDateTime << 32) | c.dwLowDateTime]
                    finally:
                        if h: k32.CloseHandle(h)
                stat = open('/proc/%d/stat' % pid).read(); fields = stat[stat.rfind(')')+2:].split(); return [pid, int(fields[19])]
            def identities(child=None):
                with open(path('identities.json'), 'w') as out: json.dump([identity(os.getpid())] + ([identity(child.pid)] if child else []), out)
            identities()
            with open(path('diagnostics.json')) as source: diagnostics = json.load(source)
            with open(path('capabilities.json')) as source: caps = json.load(source)
            with open(path('package-diagnostics.json')) as source: package_diagnostics = json.load(source)
            def send(value, payload=b''):
                envelope = dict(protocolVersion='worker-1', payload=base64.b64encode(payload).decode(), delayMilliseconds=0)
                envelope.update(value); body = json.dumps(envelope).encode()
                sys.stdout.buffer.write(struct.pack('<i', len(body)) + body); sys.stdout.buffer.flush()
            def correlated(message, kind):
                value = dict(kind=kind, requestId=message['requestId'], inputSha256=message['inputSha256'])
                for field in ('sessionId','sourceSha256','frameSequence'):
                    if field in message: value[field] = message[field]
                return value
            def shape(reply, operation):
                if not mode.startswith('shape:' + operation + ':'): return
                field = mode.split(':')[2]
                if field == 'diagnostics': reply[field] = dict(occurrences=[], hasError=False)
                elif field == 'frameFormat': reply[field] = 'bgra8888-premultiplied'
                elif field == 'frameTransparentBackground': reply[field] = False
                elif field == 'error-without-message': reply.update(kind='error', errorCode='FakeAuthorFailure')
                elif field == 'error-and-cancel': reply.update(kind='error', errorCode='FakeAuthorFailure', error='fake author', cancelled=True)
                else: reply[field] = 1
            child = None
            capability_seen = False
            pending_render = None
            while True:
                prefix = sys.stdin.buffer.read(4)
                if not prefix: break
                message = json.loads(sys.stdin.buffer.read(struct.unpack('<i', prefix)[0])); kind = message['kind']
                if kind == 'hello':
                    reply = dict(kind='hello', requestId=message['requestId'])
                    if mode != 'no-group': reply['processGroupId'] = os.getpgrp() if hasattr(os, 'getpgrp') else os.getpid()
                    send(reply)
                elif kind == 'shutdown': break
                elif kind == 'cancel' and mode in ('cooperative-cancel', 'cooperative-invalid', 'cooperative-stale'):
                    assert pending_render is not None and message['requestId'] == pending_render['requestId']
                    reply = correlated(pending_render, pending_render['kind'].replace('-render-v1', '-frame-v1'))
                    reply['cancelled'] = True
                    if mode == 'cooperative-invalid': reply['frameWidth'] = 8
                    if mode == 'cooperative-stale': reply['requestId'] = 'previous-frame'
                    open(path('cancel-ack'), 'w').close(); send(reply)
                elif 'capabilities' in kind:
                    cap = copy.deepcopy(caps)
                    if mode == 'cap-engine': cap['engineIdentity'] = 'old-engine'
                    if mode == 'cap-codec': cap['codecProfile'] = 'unknown-codec'
                    if mode == 'cap-profile': cap['packageVersion'] += 1
                    if mode == 'cap-sdk': cap['sdkVersion'] = 'unknown-sdk'
                    if mode == 'cap-shader': cap['shaderProfile'] = 'unknown-shader'
                    if mode == 'cap-references': cap['scriptRefsIdentity'] = '0' * 64
                    if mode == 'cap-reference-count': cap['scriptRefsCount'] = 0
                    if mode == 'cap-wrong-reference-count': cap['scriptRefsCount'] += 1
                    if mode == 'cap-missing-refs': del cap['scriptRefsIdentity']
                    if mode == 'cap-missing-diagnostics-version': del cap['packageDiagnosticsVersion']
                    if mode == 'cap-old-diagnostics-version': cap['packageDiagnosticsVersion'] = 0
                    if mode == 'cap-missing-package-profiles': del cap['supportedPackageProfiles']
                    if mode == 'cap-only-v1': cap['supportedPackageProfiles'] = [{'packageVersion': 2, 'sdkVersion': '1.0.0', 'shaderProfile': 'skia-raster-3.119.4-v1'}]
                    if mode == 'cap-v2-sdk': cap['supportedPackageProfiles'][0]['sdkVersion'] = '2.1.0'
                    if mode == 'cap-v2-shader': cap['supportedPackageProfiles'][0]['shaderProfile'] = 'unknown-shader'
                    if kind == 'project-image-preview-capabilities-v1' and mode != 'image-old-worker':
                        cap = dict(engineIdentity=cap['engineIdentity'],codecProfile='project-image-preview-v1',
                            baseProfiles=['static-lyrics-scene-v1','static-lyrics-scene-analysis-v1','package-lyrics-scene-v1'],
                            imageDecoderProfile='memory-image-bgra-v1',maximumImageBytes=2097152,maximumDimension=4096,
                            maximumDecodedBytes=16777216,maximumSnapshotBytes=4194280,maximumBaseSnapshotBytes=4194304,
                            maximumSourceValues=200000,packageDiagnosticsVersion=1,packageCapabilities=cap)
                        if mode == 'image-budget': cap['maximumImageBytes'] -= 1
                        if mode == 'image-base': cap['baseProfiles'][0] = 'static-lyrics-scene-presets-v1'
                        if mode == 'image-profile': cap['codecProfile'] = 'package-lyrics-scene-v1'
                        if mode == 'image-decoder': cap['imageDecoderProfile'] = 'arbitrary-native-v1'
                        if mode == 'image-missing-budget': del cap['maximumDimension']
                    payload = json.dumps(cap).encode()
                    if mode == 'cap-malformed-json': payload = b'{'
                    reply = correlated(message, kind.replace('-capabilities-v1', '-capabilities-result-v1'))
                    reply['outputSha256'] = hashlib.sha256(payload).hexdigest()
                    if mode == 'cap-foreign': reply['taskId'] = '1' * 32
                    if mode == 'cap-foreign-session': reply['sessionId'] = '1' * 32
                    if mode == 'cap-missing-output-sha': del reply['outputSha256']
                    if mode == 'cap-unsolicited-cancel': reply['cancelled'] = True
                    shape(reply, 'cap')
                    send(reply, payload)
                    capability_seen = True
                    if mode == 'cap-exit-first' and not os.path.exists(path('cap-exited')):
                        open(path('cap-exited'), 'w').close(); os._exit(0)
                elif '-open-' in kind or '-close-' in kind:
                    if '-open-' in kind:
                        open(path('open'), 'w').close()
                        if not capability_seen: open(path('unverified-open'), 'w').close()
                    reply = correlated(message, kind.replace('-open-v1', '-result-v1').replace('-close-v1', '-result-v1'))
                    if kind == 'project-image-preview-session-open-v1': reply['kind'] = 'package-render-session-result-v1'
                    if '-open-' in kind and mode == 'open-foreign-frame': reply['frameWidth'] = 8
                    if '-open-' in kind and mode == 'open-foreign-delay': reply['delayMilliseconds'] = 1
                    if '-open-' in kind and mode == 'open-foreign-validation-count': reply['validationFramesPerOutput'] = 3
                    if mode in ('WorkerPackageDiagnosticsBudgetExceeded','WorkerPackageUnavailable'):
                        reply.update(kind='error', errorCode=mode, error='完整诊断当前不受支持。')
                    shape(reply, 'open' if '-open-' in kind else 'close')
                    send(reply)
                elif '-render-' in kind:
                    if child is None:
                        child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                        identities(child)
                    open(path('render'), 'w').close()
                    if mode in ('cooperative-cancel', 'cooperative-invalid', 'cooperative-stale') and not os.path.exists(path('cancel-ack')):
                        pending_render = message; continue
                    if mode == 'hang-once' and not os.path.exists(path('attempt')):
                        open(path('attempt'), 'w').close(); time.sleep(60)
                    payload = base64.b64decode(message['payload']); width, height, timestamp = struct.unpack_from('<iiq', payload, 56)
                    pixels = bytes([3, 2, 1, 255]) * width * height
                    reply = correlated(message, kind.replace('-render-v1', '-frame-v1'))
                    reply.update(frameTransparentBackground=payload[5] == 1, frameQuality=payload[6], frameWidth=width, frameHeight=height, frameStride=width*4, frameTimeUs=timestamp, frameFormat='bgra8888-premultiplied', outputSha256=hashlib.sha256(pixels).hexdigest(), diagnostics=copy.deepcopy(diagnostics), packageDiagnostics=dict(version=1, source=[], image=[], material=[]))
                    if mode == 'session': reply['sessionId'] = '0' * 32
                    if mode == 'source': reply['sourceSha256'] = '0' * 64
                    if mode == 'sequence': reply['frameSequence'] = 0
                    if mode == 'request-id': reply['requestId'] = 'previous-frame'
                    if mode == 'input-sha': reply['inputSha256'] = '0' * 64
                    if mode == 'output-sha': reply['outputSha256'] = '0' * 64
                    if mode == 'time': reply['frameTimeUs'] += 1
                    if mode == 'transparent': reply['frameTransparentBackground'] = not reply['frameTransparentBackground']
                    if mode == 'quality': reply['frameQuality'] = 0
                    if mode == 'size': reply['frameStride'] = 2147483647
                    if mode == 'missing-diagnostics': del reply['diagnostics']
                    if mode == 'missing-package-diagnostics': del reply['packageDiagnostics']
                    if mode == 'source-on-frame': reply['packageDiagnostics']['source'] = [dict(packageId='p',revision='sha256:'+'a'*64,instanceId=None,exportId=None,code='C',message='error',isError=True,path=None,line=0,column=0)]
                    if mode == 'unsolicited-cancel': reply['cancelled'] = True
                    if mode == 'foreign-task': reply['taskId'] = '1' * 32
                    if mode == 'foreign-progress': reply['progressSequence'] = 1
                    if mode == 'foreign-error': reply.update(kind='error', taskId='1'*32, error='wrong task', errorCode='ForeignFailure')
                    if mode == 'foreign-error-input':
                        reply = correlated(message, 'error')
                        reply.update(inputSha256='0'*64, error='wrong input', errorCode='ForeignFailure'); pixels = b''
                    if mode in ('runtime-error','worker-runtime-error') or '-full' in mode: reply['diagnostics'] = dict(hasError=True, occurrences=[dict(epoch='0c95dc85-89bb-4536-908a-4f59347b49f6', sequence=1, diagnostic=dict(instanceId='instance', artifactHash='artifact', stage='Render', timeUs=timestamp, severity='Error', code='Failure', message='真实错误', location=None, package=None))])
                    if '-full' in mode: reply['packageDiagnostics'] = copy.deepcopy(package_diagnostics)
                    if mode == 'runtime-error-full-invalid-graph': reply['packageDiagnostics']['image'][0]['graph']['exportId'] = 'missing'
                    if mode == 'worker-runtime-error-full-invalid-source': reply['packageDiagnostics']['source'] = [dict(packageId='p',revision='sha256:'+'a'*64,instanceId=None,exportId=None,code='C',message='error',isError=True,path=None,line=0,column=0)]
                    if mode.startswith('worker-runtime-error'):
                        reply.update(kind='error', errorCode='WorkerRuntimeDiagnosticError', error='真实Runtime失败。')
                        del reply['outputSha256']; pixels = b''
                        for field in ('frameTransparentBackground','frameQuality','frameWidth','frameHeight','frameStride','frameTimeUs','frameFormat'): del reply[field]
                        if mode == 'worker-runtime-error': del reply['packageDiagnostics']
                        if mode == 'worker-runtime-error-full-invalid-compilation': reply['errorCode'] = 'WorkerPackageCompilationFailed'
                    send(reply, pixels)
            """;
    }
}
