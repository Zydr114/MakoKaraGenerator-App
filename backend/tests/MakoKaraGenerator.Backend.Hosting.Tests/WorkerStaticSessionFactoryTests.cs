using System.Diagnostics;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class WorkerStaticSessionFactoryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task 传输完整实际诊断字段保留发生身份和游标不重复()
    {
        var occurrence = StaticWorkerContractTests.Occurrence(1);
        occurrence = occurrence with { Diagnostic = occurrence.Diagnostic with { Package = new("package", "sha256:" + new string('a', 64), "export") } };
        using Probe probe = await Probe.CreateAsync("valid", new([occurrence], false));
        ProjectSnapshot snapshot = Snapshot();
        await using IRenderFrameSession session = await probe.Factory().CreateAsync(snapshot);
        var cursor = new RuntimeDiagnosticCursor();
        using RenderFrame first = await session.RenderAsync(RenderRequest.At(snapshot, 42));
        RuntimeDiagnosticOccurrence actual = Assert.Single(first.DiagnosticSnapshot.ReadAll());
        Assert.Equal(occurrence.Epoch, actual.Epoch); Assert.Equal(occurrence.Sequence, actual.Sequence);
        Assert.Equal(occurrence.Diagnostic.InstanceId, actual.Diagnostic.InstanceId.Value);
        Assert.Equal(occurrence.Diagnostic.ArtifactHash, actual.Diagnostic.ArtifactHash);
        Assert.Equal(occurrence.Diagnostic.Stage, actual.Diagnostic.Stage);
        Assert.Equal(occurrence.Diagnostic.TimeUs, actual.Diagnostic.TimeUs);
        Assert.Equal(DiagnosticSeverity.Warning, actual.Diagnostic.Diagnostic.Severity);
        Assert.Equal(occurrence.Diagnostic.Code, actual.Diagnostic.Diagnostic.Code);
        Assert.Equal(occurrence.Diagnostic.Message, actual.Diagnostic.Diagnostic.Message);
        Assert.Equal(new SourceLocation("src/code.cs", 2, 3), actual.Diagnostic.Diagnostic.Location);
        Assert.Equal(new EffectPackageIdentity("package", "sha256:" + new string('a', 64), "export"), actual.Diagnostic.PackageIdentity);
        Assert.Single(cursor.ReadNew(first.DiagnosticSnapshot));
        using RenderFrame second = await session.RenderAsync(RenderRequest.At(snapshot, 43));
        Assert.Equal(first.DiagnosticSnapshot.ReadAll().ToArray(), second.DiagnosticSnapshot.ReadAll().ToArray());
        Assert.Empty(cursor.ReadNew(second.DiagnosticSnapshot));
        int pid = await probe.PidAsync();
        await session.DisposeAsync();
        Assert.False(Alive(pid));
        output.WriteLine($"Static diagnostics transport probe PID={pid}; complete occurrence metadata preserved and closed.");
    }

    [Theory]
    [InlineData("session")]
    [InlineData("source")]
    [InlineData("sequence")]
    [InlineData("request-id")]
    [InlineData("time")]
    [InlineData("transparent")]
    [InlineData("quality")]
    [InlineData("size")]
    [InlineData("missing-diagnostics")]
    [InlineData("error")]
    [InlineData("worker-error")]
    public async Task 错配或晚到像素与缺失Error诊断使会话失败并释放Worker(string mode)
    {
        using Probe probe = await Probe.CreateAsync(mode, new([], false));
        ProjectSnapshot snapshot = Snapshot();
        await using IRenderFrameSession session = await probe.Factory().CreateAsync(snapshot);
        int pid = await probe.PidAsync();
        RenderSessionFailureException exception = await Assert.ThrowsAsync<RenderSessionFailureException>(() => session.RenderAsync(RenderRequest.At(snapshot, 42)).AsTask());
        if (mode is "error" or "worker-error")
        {
            Assert.Equal("WorkerRuntimeDiagnosticError", exception.Code);
            Assert.True(exception.DiagnosticSnapshot!.HasError);
            RuntimeDiagnosticOccurrence actual = Assert.Single(exception.DiagnosticSnapshot.ReadAll());
            Assert.Equal(new Guid("0c95dc85-89bb-4536-908a-4f59347b49f6"), actual.Epoch);
            Assert.Equal(1, actual.Sequence);
            Assert.Equal("instance", actual.Diagnostic.InstanceId.Value);
            Assert.Equal("Failure", actual.Diagnostic.Diagnostic.Code);
            Assert.Equal("真实错误", actual.Diagnostic.Diagnostic.Message);
        }
        else Assert.Null(exception.DiagnosticSnapshot);
        Assert.IsType<WorkerProtocolException>(exception.InnerException);
        Assert.False(Alive(pid));
        var failed = await Assert.ThrowsAsync<PreparedFrameException>(() => session.RenderAsync(RenderRequest.At(snapshot, 43)).AsTask());
        Assert.Equal("WorkerSessionFailed", failed.Code);
        output.WriteLine($"Static invalid {mode} probe PID={pid}; rejected and released.");
    }

    [Fact]
    public async Task Deadline释放当前静态会话并允许同工厂重新建立()
    {
        using Probe probe = await Probe.CreateAsync("timeout", new([], false));
        var factory = probe.Factory(TimeSpan.FromSeconds(2));
        ProjectSnapshot snapshot = Snapshot();
        await using IRenderFrameSession first = await factory.CreateAsync(snapshot);
        int pid = await probe.PidAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.RenderAsync(RenderRequest.At(snapshot, 42)).AsTask());
        Assert.False(Alive(pid));
        await using IRenderFrameSession second = await factory.CreateAsync(snapshot);
        int restarted = await probe.PidAsync();
        Assert.NotEqual(pid, restarted);
        using RenderFrame actual = await second.RenderAsync(RenderRequest.At(snapshot, 42));
        Assert.Equal(8 * 4 * 4, actual.PixelMemory.Length);
        await second.DisposeAsync();
        Assert.False(Alive(restarted));
        output.WriteLine($"Static deadline PID={pid} released; factory recreated PID={restarted} and closed.");
    }

    [Fact]
    public async Task 布局试样未知质量错快照和越预算拒绝后仍可使用当前会话()
    {
        using Probe probe = await Probe.CreateAsync("valid", new([], false));
        ProjectSnapshot snapshot = Snapshot();
        await using IRenderFrameSession session = await probe.Factory().CreateAsync(snapshot);
        var layout = await Assert.ThrowsAsync<PreparedFrameException>(() => session.RenderAsync(RenderRequest.At(snapshot, 42) with { LayoutLineId = "row" }).AsTask());
        Assert.Equal("WorkerRequestUnsupported", layout.Code);
        var quality = await Assert.ThrowsAsync<PreparedFrameException>(() => session.RenderAsync(RenderRequest.At(snapshot, 42) with { Quality = (RenderQuality)999 }).AsTask());
        Assert.Equal("WorkerRequestUnsupported", quality.Code);
        var prepared = new PreparedRenderFrame(snapshot, 42, OutputMode.Opaque, RenderTarget.FromComposition(snapshot.Composition),
            LyricSceneSampler.SampleFrame(snapshot, 42), []);
        var plan = await Assert.ThrowsAsync<PreparedFrameException>(() => session.RenderAsync(RenderRequest.At(snapshot, 42) with { PreparedFrame = prepared }).AsTask());
        Assert.Equal("WorkerRequestUnsupported", plan.Code);
        var identity = await Assert.ThrowsAsync<PreparedFrameException>(() => session.RenderAsync(RenderRequest.At(snapshot with { }, 42)).AsTask());
        Assert.Equal("WorkerSessionInputsMismatch", identity.Code);
        var budget = await Assert.ThrowsAsync<WorkerProtocolException>(() => session.RenderAsync(RenderRequest.At(snapshot, 42) with { Target = new() { PixelWidth = 1920, PixelHeight = 1080 } }).AsTask());
        Assert.Equal("WorkerFrameBudgetExceeded", budget.Code);
        using RenderFrame valid = await session.RenderAsync(RenderRequest.At(snapshot, 42));
        Assert.Equal(8, valid.Width);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("deleted")]
    [InlineData("nonexecutable")]
    public async Task 缺失删除或不可执行Worker的open失败保中立Unavailable且不回退Runtime(string mode)
    {
        using TemporaryDirectory temp = new();
        string path = temp.FilePath("worker-apphost");
        if (mode != "missing") File.Copy(WorkerApphost(), path);
        var factory = new WorkerRenderFrameSessionFactory(new(path));
        if (mode == "deleted") File.Delete(path);
        if (mode == "nonexecutable")
        {
            Assert.True(OperatingSystem.IsLinux(), "Executable permission regression is the bounded Linux exit.");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        var error = await Assert.ThrowsAsync<RenderSessionFailureException>(() => factory.CreateAsync(Snapshot()).AsTask());
        Assert.Equal("WorkerUnavailable", error.Code);
        Assert.Null(error.DiagnosticSnapshot);
        Assert.NotNull(error.InnerException);
        Assert.DoesNotContain(path, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("open-malformed")]
    [InlineData("open-error")]
    public async Task Open坏回执不伪造诊断且实际Error快照完整进入中立失败(string mode)
    {
        var occurrence = StaticWorkerContractTests.Occurrence(1, "Error");
        occurrence = occurrence with { Diagnostic = occurrence.Diagnostic with { Package = new("package", "sha256:" + new string('a', 64), "export") } };
        using Probe probe = await Probe.CreateAsync(mode, new([occurrence], true));
        RenderSessionFailureException error = await Assert.ThrowsAsync<RenderSessionFailureException>(() => probe.Factory().CreateAsync(Snapshot()).AsTask());
        Assert.IsType<WorkerProtocolException>(error.InnerException);
        int pid = await probe.PidAsync();
        Assert.False(Alive(pid));
        if (mode == "open-malformed")
        {
            Assert.Equal("WorkerProtocolInvalid", error.Code);
            Assert.Null(error.DiagnosticSnapshot);
        }
        else
        {
            Assert.Equal("WorkerOpenDiagnosticError", error.Code);
            RuntimeDiagnosticOccurrence actual = Assert.Single(error.DiagnosticSnapshot!.ReadAll());
            Assert.Equal(occurrence.Epoch, actual.Epoch); Assert.Equal(occurrence.Sequence, actual.Sequence);
            Assert.Equal(occurrence.Diagnostic.InstanceId, actual.Diagnostic.InstanceId.Value);
            Assert.Equal(occurrence.Diagnostic.ArtifactHash, actual.Diagnostic.ArtifactHash);
            Assert.Equal(occurrence.Diagnostic.Stage, actual.Diagnostic.Stage);
            Assert.Equal(occurrence.Diagnostic.TimeUs, actual.Diagnostic.TimeUs);
            Assert.Equal(occurrence.Diagnostic.Code, actual.Diagnostic.Diagnostic.Code);
            Assert.Equal(occurrence.Diagnostic.Message, actual.Diagnostic.Diagnostic.Message);
            Assert.Equal(new SourceLocation("src/code.cs", 2, 3), actual.Diagnostic.Diagnostic.Location);
            Assert.Equal(new EffectPackageIdentity("package", "sha256:" + new string('a', 64), "export"), actual.Diagnostic.PackageIdentity);
            Assert.True(error.DiagnosticSnapshot.HasError);
        }
        output.WriteLine($"Static {mode} probe PID={pid}; open rejected, stable code={error.Code}, actual snapshot={error.DiagnosticSnapshot is not null}, process released.");
    }

    private static string WorkerApphost()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MakoKaraGenerator.slnx"))) root = root.Parent;
        string path = Path.Combine(root!.FullName, "src", "MakoKaraGenerator.Backend.Worker", "bin", "Debug", "net10.0", "MakoKaraGenerator.Backend.Worker" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        Assert.True(File.Exists(path)); return path;
    }

    private static ProjectSnapshot Snapshot() => ProjectSnapshot.FromProject(Project.Create("transport fixture",
        Composition.Create(8, 4, FrameRate.Fps30, outputDurationUs: 1_000_000), LyricsDocument.Create([])));

    private static bool Alive(int pid)
    {
        try { using Process process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private sealed class Probe : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly TemporaryDirectory _temp = new();
        private readonly string _mode;
        private Probe(string mode) { _mode = mode; }
        public static async Task<Probe> CreateAsync(string mode, WorkerDiagnosticsSnapshotDto diagnostics)
        {
            Probe probe = new(mode);
            await File.WriteAllTextAsync(probe._temp.FilePath("probe.py"), Source);
            await File.WriteAllTextAsync(probe._temp.FilePath("diagnostics.json"), JsonSerializer.Serialize(diagnostics, JsonOptions));
            return probe;
        }
        public WorkerRenderFrameSessionFactory Factory(TimeSpan? timeout = null) => new(new("python3",
            [_temp.FilePath("probe.py"), _mode, _temp.FilePath("diagnostics.json"), _temp.FilePath("pid"), _temp.FilePath("attempt")], TimeSpan.FromMilliseconds(80)), timeout);
        public async Task<int> PidAsync() => int.Parse(await File.ReadAllTextAsync(_temp.FilePath("pid")), System.Globalization.CultureInfo.InvariantCulture);
        public void Dispose() => _temp.Dispose();

        // Deliberate IPC corruption/lifecycle fixture only; real pixels are compared
        // to ProjectRenderSessionFactory in WorkerStaticSessionIntegrationTests.
        private const string Source = """
            import base64, copy, hashlib, json, os, struct, sys, time
            mode, diagnostics_file, pid_file, attempt = sys.argv[1:]
            with open(pid_file, 'w') as output: output.write(str(os.getpid()))
            with open(diagnostics_file) as source: diagnostics = json.load(source)
            def send(value):
                body = json.dumps(dict(protocolVersion='worker-1', delayMilliseconds=0, cancelled=False, **value)).encode()
                sys.stdout.buffer.write(struct.pack('<i', len(body)) + body)
                sys.stdout.buffer.flush()
            while True:
                prefix = sys.stdin.buffer.read(4)
                if not prefix: break
                message = json.loads(sys.stdin.buffer.read(struct.unpack('<i', prefix)[0]))
                kind = message['kind']
                if kind == 'hello':
                    send(dict(kind='hello', requestId=message['requestId'], payload=''))
                elif kind == 'shutdown': break
                elif kind in ('static-session-open-v1', 'static-session-close-v1'):
                    value = dict(kind='static-session-result-v1', requestId=message['requestId'], inputSha256=message['inputSha256'], sessionId=message['sessionId'], sourceSha256=message['sourceSha256'], frameSequence=message['frameSequence'], payload='')
                    if kind == 'static-session-open-v1' and mode == 'open-malformed': value['requestId'] = 'previous-open'
                    if kind == 'static-session-open-v1' and mode == 'open-error':
                        value['kind'] = 'error'
                        value['errorCode'] = 'WorkerOpenDiagnosticError'
                        value['error'] = 'Actual Error during open'
                        value['diagnostics'] = copy.deepcopy(diagnostics)
                    send(value)
                elif kind == 'static-session-render-v1':
                    if mode == 'timeout' and not os.path.exists(attempt):
                        open(attempt, 'w').close()
                        time.sleep(60)
                    payload = base64.b64decode(message['payload'])
                    width, height, timestamp = struct.unpack_from('<iiq', payload, 56)
                    pixels = bytes([3, 2, 1, 255]) * width * height
                    value = dict(kind='static-session-frame-v1', requestId=message['requestId'], inputSha256=message['inputSha256'], sessionId=message['sessionId'], sourceSha256=message['sourceSha256'], frameSequence=message['frameSequence'], frameTransparentBackground=payload[5] == 1, frameQuality=payload[6], frameWidth=width, frameHeight=height, frameStride=width*4, frameTimeUs=timestamp, frameFormat='bgra8888-premultiplied', payload=base64.b64encode(pixels).decode(), outputSha256=hashlib.sha256(pixels).hexdigest(), diagnostics=copy.deepcopy(diagnostics))
                    if mode == 'session': value['sessionId'] = '0' * 32
                    if mode == 'source': value['sourceSha256'] = '0' * 64
                    if mode == 'sequence': value['frameSequence'] = 0
                    if mode == 'request-id': value['requestId'] = 'previous-frame'
                    if mode == 'time': value['frameTimeUs'] += 1
                    if mode == 'transparent': value['frameTransparentBackground'] = not value['frameTransparentBackground']
                    if mode == 'quality': value['frameQuality'] = 0
                    if mode == 'size': value['frameStride'] = 2147483647
                    if mode == 'missing-diagnostics': del value['diagnostics']
                    if mode in ('error', 'worker-error'): value['diagnostics'] = dict(hasError=True, occurrences=[dict(epoch='0c95dc85-89bb-4536-908a-4f59347b49f6', sequence=1, diagnostic=dict(instanceId='instance', artifactHash='artifact', stage='Render', timeUs=timestamp, severity='Error', code='Failure', message='真实错误', location=None, package=None))])
                    if mode == 'worker-error':
                        value['kind'] = 'error'
                        value['error'] = 'Actual reported Error'
                        value['errorCode'] = 'WorkerRuntimeDiagnosticError'
                        value['payload'] = ''
                        del value['outputSha256']
                    send(value)
            """;
    }
}
