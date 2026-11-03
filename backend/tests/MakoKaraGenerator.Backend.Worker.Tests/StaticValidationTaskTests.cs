using System.Globalization;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Backend.Worker.Tests;

public sealed class StaticValidationTaskTests
{
    [Fact]
    public async Task 适配器直接运行正式验证器双独立Final会话且完整发生逐字段传输()
    {
        var factory = new RecordingFactory(); List<WorkerMessage> progress = [];
        WorkerMessage result = await StaticValidationTask.ExecuteAsync(Request(3), value => { progress.Add(value); return Task.CompletedTask; },
            factory, CancellationToken.None);
        Assert.Equal(2, factory.Created); Assert.Equal(2, factory.Disposed); Assert.Equal(6, factory.Requests.Count);
        Assert.All(factory.Requests, value => { Assert.Equal(RenderQuality.Final, value.Quality); Assert.Null(value.Target); });
        Assert.Equal(new long[] { 0, 41_666, 83_333, 0, 41_666, 83_333 }, factory.Requests.Select(value => value.TimeUs));
        ValidationReportDto report = WorkerValidationContract.DecodeReport(result.Payload, 3);
        Assert.True(report.Succeeded); Assert.Equal(3, report.OpaqueFrames); Assert.Equal(3, report.TransparentFrames);
        Assert.Equal(6, report.Diagnostics.Length); Assert.Equal(2, report.Issues.Length);
        for (int i = 0; i < report.Diagnostics.Length; i++)
        {
            ValidationDiagnosticDto value = report.Diagnostics[i]; var expected = factory.Occurrences[i];
            var actual = value.Diagnostic.Occurrence!;
            Assert.Equal(i >= 3, value.Transparent); Assert.Equal(expected.Epoch, actual.Epoch);
            Assert.Equal(expected.Sequence.ToString(CultureInfo.InvariantCulture), actual.Sequence);
            Assert.Equal(expected.Diagnostic.InstanceId.Value, actual.InstanceId); Assert.Equal(expected.Diagnostic.ArtifactHash, actual.ArtifactHash);
            Assert.Equal(expected.Diagnostic.Stage, actual.Stage); Assert.Equal(expected.Diagnostic.TimeUs.ToString(CultureInfo.InvariantCulture), actual.TimeUs);
            Assert.Equal("Notice", value.Diagnostic.Code); Assert.Equal("warning", value.Diagnostic.Severity); Assert.Equal("actual notice", value.Diagnostic.Message);
            Assert.Equal(new("src/effect.cs", 2, 3), actual.Location); Assert.Equal(new("pkg", "sha256:" + new string('b', 64), "main"), actual.Package);
        }
        Assert.NotEmpty(progress); Assert.Equal(6, progress[^1].ValidationProgress!.CompletedFrames);
        Assert.Equal(4, result.ProgressSequence); Assert.Equal(WorkerProtocol.ComputeSha256(result.Payload), result.OutputSha256);
    }

    [Theory]
    [InlineData("occurrences")] [InlineData("issues")] [InlineData("bytes")] [InlineData("single-huge")]
    public async Task 累计条数或字节预算在报告集合增长之前拒绝并释放会话(string mode)
    {
        var factory = new RecordingFactory { UniqueCode = mode == "issues", Minimal = mode == "occurrences", Message = mode switch
            { "bytes" => new string('x', 12_000), "single-huge" => new string('x', WorkerValidationContract.MaximumReportBytes + 1), _ => "actual notice" } };
        await Assert.ThrowsAsync<RuntimeValidationBudgetExceededException>(() => StaticValidationTask.ExecuteAsync(Request(600), _ => Task.CompletedTask,
            factory, CancellationToken.None));
        Assert.Equal(1, factory.Created); Assert.Equal(1, factory.Disposed);
        if (mode == "occurrences") Assert.Equal(513, factory.Requests.Count);
        else if (mode == "issues") Assert.Equal(257, factory.Requests.Count);
        else Assert.InRange(factory.Requests.Count, 1, 30);
    }

    [Fact]
    public async Task 未协商材质诊断明确typed拒绝且不变为丢字段报告()
    {
        var factory = new RecordingFactory { Material = true };
        var failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => StaticValidationTask.ExecuteAsync(Request(3), _ => Task.CompletedTask,
            factory, CancellationToken.None));
        Assert.Equal("WorkerUnsupportedValidationIssue", failure.Code); Assert.Equal(1, factory.Disposed);
    }

    [Fact]
    public async Task 实际中立Error发生进入失败报告且没有像素或成功替代()
    {
        var actual = RuntimeDiagnosticSnapshot.FromOccurrences([new(Guid.NewGuid(), 1, Diagnostic(0, "error", "actual error", DiagnosticSeverity.Error))]);
        var factory = new RecordingFactory { Failure = new RenderSessionFailureException("actual-error", "failed", actual) };
        WorkerMessage result = await StaticValidationTask.ExecuteAsync(Request(3), _ => Task.CompletedTask, factory, CancellationToken.None);
        ValidationReportDto report = WorkerValidationContract.DecodeReport(result.Payload, 3);
        Assert.False(report.Succeeded); Assert.Equal(0, report.OpaqueFrames); Assert.Equal(0, report.TransparentFrames);
        Assert.Equal("actual-error", Assert.Single(report.Issues).Code);
        var occurrence = Assert.Single(report.Diagnostics).Diagnostic.Occurrence!;
        Assert.Equal(Assert.Single(actual.ReadAll()).Epoch, occurrence.Epoch); Assert.Equal("1", occurrence.Sequence);
        Assert.Equal("actual-instance", occurrence.InstanceId); Assert.Equal("Evaluate", occurrence.Stage);
        Assert.Equal("error", Assert.Single(report.Diagnostics).Diagnostic.Severity); Assert.Equal(1, factory.Disposed);
    }

    [Fact]
    public async Task 包源码编译失败进入带来源诊断的验证报告()
    {
        var package = MixedEffectPackageFixture.Contents(controllerSource: "public sealed class PackEffect { invalid !!! }");
        var project = Project.Create("broken", Composition.Create(8, 4, FrameRate.Fps30, outputDurationUs: 1_000_000),
            LyricsDocument.Create([])) with { Effects = new([package], [new()
            {
                Id = "broken-instance", Name = "broken", PackageId = package.PackageId, Revision = package.Revision,
                ExportId = "controller", Binding = new(ProjectEffectBindingKind.Project), Subscription = new("project"),
            }]) };
        var snapshot = ProjectSnapshot.FromProject(project);
        using TemporaryDirectory temporary = new();
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var definition = EffectPackageDefinition.Read(package);
        var compilationInput = ScriptCompilationInput.Create(EffectPackageSources.Read(package), definition.Controllers.Select(controller => controller.EntryType), compiler.Identity);
        var compilation = await compiler.CompileAsync(compilationInput.Sources, compilationInput.EntryTypes);
        using var prepared = ScriptPreparedInputCodec.Prepare(PackageProjectSnapshotCodec.Encode(snapshot), temporary.Path,
            compiler.Identity, [(compilationInput, compilation)]);
        byte[] bytes = prepared.Envelope; Guid id = Guid.NewGuid();
        byte[] payload = WorkerValidationContract.EncodeInput(id, 1, bytes, packageProfile: true);
        WorkerMessage request = new(WorkerValidationContract.RequestKind, Guid.NewGuid().ToString("N"), WorkerProtocol.ComputeSha256(payload), payload,
            taskId: id.ToString("N"), sourceSha256: WorkerProtocol.ComputeSha256(bytes), validationFramesPerOutput: 1);

        WorkerMessage result = await StaticValidationTask.ExecuteAsync(request, _ => Task.CompletedTask, CancellationToken.None);
        ValidationReportDto report = WorkerValidationContract.DecodeReport(result.Payload, 1);
        Assert.False(report.Succeeded);
        Assert.Equal(0, report.OpaqueFrames); Assert.Equal(0, report.TransparentFrames);
        Assert.Empty(report.Diagnostics); Assert.NotEmpty(report.Issues);
        Assert.All(report.Issues, issue =>
        {
            Assert.True(issue.IsError);
            Assert.Equal("compileSource", issue.Diagnostic?.OriginKind);
            var source = Assert.IsType<BackendSourceDiagnosticDto>(issue.Diagnostic!.Source);
            Assert.Equal(package.PackageId, source.PackageId); Assert.Equal(package.Revision, source.Revision);
            Assert.Equal("src/PackEffect.cs", source.Path); Assert.True(source.Line > 0); Assert.True(source.Column > 0);
            Assert.Equal(issue.Code, source.Code); Assert.True(source.IsError);
        });
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, true)] [InlineData(2, true)] [InlineData(3, false)]
    [InlineData(4, true)] [InlineData(5, false)] [InlineData(6, false)] [InlineData(7, false)]
    public void 完整包汇总诊断严格要求恰一来源payload(int mask, bool accepted)
    {
        const string revision = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var resource = new BackendDiagnosticResourceDto("package", revision, "export");
        var diagnostic = new BackendDiagnosticDto("Actual", "error", "actual", null, OriginKind:
            (mask & 1) != 0 ? "compileSource" : (mask & 2) != 0 ? "imageEffect" : "material",
            Source: (mask & 1) != 0 ? new("package", revision, "instance", "export", "Actual", "actual", true, null, 0, 0) : null,
            ImageEffect: (mask & 2) != 0 ? new("instance", new('a', 64), resource, "input", "Actual") : null,
            Material: (mask & 4) != 0 ? new(new("script", new("instance", new('a', 64), resource)), resource, "input", "Actual") : null);
        var report = new ValidationReportDto(false, 0, 0, [new(false, "0", "Actual", null, true, Diagnostic: diagnostic)], []);
        if (accepted)
        {
            var decoded = WorkerValidationContract.DecodeReport(WorkerValidationContract.EncodeReport(report, 1), 1);
            Assert.Equal(diagnostic, Assert.Single(decoded.Issues).Diagnostic);
        }
        else Assert.Throws<WorkerProtocolException>(() => WorkerValidationContract.EncodeReport(report, 1));
    }

    [Theory]
    [InlineData("wrong-origin")] [InlineData("runtime")]
    public void 包汇总诊断拒绝来源标签不符或混入运行时发生(string mode)
    {
        var diagnostic = new BackendDiagnosticDto("Actual", "error", "actual", null,
            OriginKind: mode == "wrong-origin" ? "material" : "compileSource",
            Source: new("package", "sha256:" + new string('a', 64), "instance", "export", "Actual", "actual", true, null, 0, 0),
            Occurrence: mode == "runtime" ? new(Guid.NewGuid(), "1", "instance", new('a', 64), "Evaluate", "0", null, null) : null);
        var report = new ValidationReportDto(false, 0, 0, [new(false, "0", "Actual", null, true, Diagnostic: diagnostic)], []);
        Assert.Throws<WorkerProtocolException>(() => WorkerValidationContract.EncodeReport(report, 1));
    }

    [Theory]
    [InlineData("source-revision")] [InlineData("image-graph")] [InlineData("material-resource")]
    public void 包诊断typed载荷中的JSON_null稳定拒绝而非异常(string mode)
    {
        var source = new BackendSourceDiagnosticDto("package", mode == "source-revision" ? null! : "sha256:" + new string('a', 64),
            "instance", "export", "Actual", "actual", true, null, 0, 0);
        var resource = new BackendDiagnosticResourceDto("package", "sha256:" + new string('a', 64), "export");
        var image = new BackendImageEffectDiagnosticDto("instance", new('a', 64), mode == "image-graph" ? null! : resource, "input", "Actual");
        var material = new BackendMaterialDiagnosticDto(new("script", new("instance", new('a', 64), resource)),
            mode == "material-resource" ? null! : resource, "input", "Actual");
        var diagnostic = new BackendDiagnosticDto("Actual", "error", "actual", null, OriginKind: mode switch
        { "source-revision" => "compileSource", "image-graph" => "imageEffect", _ => "material" },
            Source: mode == "source-revision" ? source : null, ImageEffect: mode == "image-graph" ? image : null,
            Material: mode == "material-resource" ? material : null);
        var report = new ValidationReportDto(false, 0, 0, [new(false, "0", "Actual", null, true, Diagnostic: diagnostic)], []);
        Assert.Throws<WorkerProtocolException>(() => WorkerValidationContract.EncodeReport(report, 1));
    }

    [Fact]
    public void 旧无typed诊断的汇总问题仍然兼容()
    {
        var report = new ValidationReportDto(false, 0, 0, [new(false, "0", "Actual", null, true)], []);
        Assert.NotNull(WorkerValidationContract.DecodeReport(WorkerValidationContract.EncodeReport(report, 1), 1));
    }

    [Fact]
    public async Task 进度发送失败取消执行且等待Runtime释放和唯一pump终态()
    {
        var factory = new RecordingFactory();
        await Assert.ThrowsAnyAsync<Exception>(() => StaticValidationTask.ExecuteAsync(Request(600), _ => Task.FromException(new IOException("owned pipe failed")),
            factory, CancellationToken.None));
        Assert.Equal(factory.Created, factory.Disposed);
        Assert.InRange(factory.Requests.Count, 1, 1200);
    }

    private static WorkerMessage Request(int frames)
    {
        var snapshot = ProjectSnapshot.FromProject(Project.Create("owned task", Composition.Create(8, 4, new FrameRate(24, 1),
            outputDurationUs: 30_000_000), LyricsDocument.Create([])));
        byte[] bytes = StaticProjectSnapshotCodec.Encode(snapshot); Guid id = Guid.NewGuid();
        byte[] payload = WorkerValidationContract.EncodeInput(id, frames, bytes);
        return new(WorkerValidationContract.RequestKind, Guid.NewGuid().ToString("N"), WorkerProtocol.ComputeSha256(payload), payload,
            taskId: id.ToString("N"), sourceSha256: WorkerProtocol.ComputeSha256(bytes), validationFramesPerOutput: frames);
    }
    private static RuntimeDiagnostic Diagnostic(long time, string code, string message, DiagnosticSeverity severity = DiagnosticSeverity.Warning)
        => new(new("actual-instance"), new string('a', 64), "Evaluate", time, new(severity, code, message, new("src/effect.cs", 2, 3)))
        { PackageIdentity = new("pkg", "sha256:" + new string('b', 64), "main") };
    private sealed class RecordingFactory : IRenderFrameSessionFactory
    {
        public int Created { get; private set; }
        public int Disposed { get; private set; }
        public bool UniqueCode { get; init; }
        public bool Material { get; init; }
        public bool Minimal { get; init; }
        public string Message { get; init; } = "actual notice";
        public Exception? Failure { get; init; }
        public List<RenderRequest> Requests { get; } = [];
        public List<RuntimeDiagnosticOccurrence> Occurrences { get; } = [];
        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
        { Created++; return ValueTask.FromResult<IRenderFrameSession>(new Session(this)); }
        private sealed class Session(RecordingFactory owner) : IRenderFrameSession
        {
            private readonly RuntimeDiagnosticBuffer _diagnostics = new();
            public ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested(); owner.Requests.Add(request);
                if (owner.Failure is { } failure) throw failure;
                _diagnostics.Add(owner.Minimal ? new(new("i"), "", "E", request.TimeUs, new(DiagnosticSeverity.Warning, "n", "m"))
                    : Diagnostic(request.TimeUs, owner.UniqueCode ? "Notice-" + owner.Requests.Count : "Notice", owner.Message));
                var snapshot = _diagnostics.Snapshot(); owner.Occurrences.Add(snapshot.ReadAll().Last());
                var target = request.ResolveTarget();
                var frame = new RenderFrame(new byte[target.PixelWidth * target.PixelHeight * 4], target, PixelFormat.Bgra8888Premultiplied, request.TimeUs)
                {
                    DiagnosticSnapshot = snapshot,
                    MaterialDiagnostics = owner.Material ? [new(new EffectInstanceId("material-instance"), new string('c', 64),
                        new ResourceCatalog("pkg", "sha256:" + new string('b', 64), ["fill"], [], []).GetMaterial("fill"), "input", "Unsupported")] : [],
                };
                return ValueTask.FromResult(frame);
            }
            public ValueTask DisposeAsync() { owner.Disposed++; return ValueTask.CompletedTask; }
        }
    }
}
