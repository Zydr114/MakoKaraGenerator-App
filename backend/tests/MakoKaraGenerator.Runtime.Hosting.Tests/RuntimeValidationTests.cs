using MakoKaraGenerator.Scripting.Compilation;
using System.Buffers;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Runtime.Hosting.Authoring;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Runtime.Hosting.Tests;

public sealed class RuntimeValidationTests
{
    [Fact]
    public async Task 纯执行器接受固定尺寸与整数帧计划且完整发生不受汇总去重影响()
    {
        var scene = TemplateSampleScene.WorkshopScene(false, null);
        var snapshot = ProjectSnapshot.FromProject(scene with { Composition = scene.Composition with
        { CanvasWidth = 320, CanvasHeight = 180, FrameRate = new(24, 1) } });
        var factory = new RecordingFactory();
        var progress = new List<RuntimeValidationProgress>();
        var result = await new RuntimeFrameValidator(factory).ValidateAsync(snapshot, new(3), new InlineProgress(progress.Add));
        Assert.True(result.Succeeded); Assert.Equal(3, result.OpaqueFrames); Assert.Equal(3, result.TransparentFrames);
        Assert.Equal(2, factory.Created); Assert.Equal(2, factory.Disposed); Assert.Equal(6, factory.Frames.Count);
        Assert.Equal(new long[] { 0, 41_666, 83_333, 0, 41_666, 83_333 }, factory.Requests.Select(value => value.TimeUs));
        Assert.All(factory.Frames, frame => Assert.True(frame.IsDisposed));
        Assert.Equal(12, result.Diagnostics.Length); Assert.Equal(2, result.Issues.Length);
        Assert.All(result.Issues, issue => { Assert.Equal("same text", issue.Message); Assert.Equal(0, issue.TimeUs); });
        Assert.Equal(6, result.Diagnostics.Count(value => value.Transparent));
        Assert.Equal(12, result.Diagnostics.Select(value => (value.Occurrence.Epoch, value.Occurrence.Sequence)).Distinct().Count());
        Assert.Equal(6, progress[^1].CompletedFrames); Assert.Equal(6, progress[^1].TotalFrames);
    }

    [Fact]
    public async Task 无GUI作者服务使用自有媒体和各360帧并在会话释放后删除目录()
    {
        string? directory = null; using TemporaryDirectory temp = new();
        var factory = new RecordingFactory { OnDispose = () => Assert.True(Directory.Exists(directory)) };
        var package = MixedEffectPackageFixture.Contents(); var configuration = ScriptConfiguration.Create("sample", "neutral", package, "controller");
        var target = ScriptSampleBuilder.Targets(configuration, TemplateMaterialSlot.UnsungFill)[0];
        var request = new ScriptSampleRequest(configuration, target, TemplateMaterialSlot.UnsungFill);
        var result = await new ScriptSampleValidator(factory, (path, _) =>
        {
            directory = path; Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "owned"), "owned");
            return Task.FromResult(new TemplateSampleResources("own.wav", "own.png", null));
        }).ValidateAsync(request);
        Assert.True(result.Succeeded); Assert.Equal(360, result.OpaqueFrames); Assert.Equal(360, result.TransparentFrames);
        Assert.Equal(720, factory.Frames.Count); Assert.All(factory.Frames, frame => Assert.True(frame.IsDisposed));
        Assert.False(Directory.Exists(directory)); Assert.Null(configuration.FixedSeed);
        Assert.All(factory.Requests, render => Assert.All(render.Project.Effects.Instances, instance => Assert.Equal(ScriptSampleBuilder.Seed, instance.Seed)));
    }

    [Fact]
    public void 作者与模板工坊试样反复构建得到相同实例身份与种子()
    {
        // M13 作者链探针发现：试样每次从空场景正式应用，曾得到新 Guid 实例身份（模板无固定种子时还抽新种子），
        // 同一草稿每次刷新预览粒子散布都不同。两次构建的实例身份与种子须完全相同。
        var package = MixedEffectPackageFixture.Contents(); var configuration = ScriptConfiguration.Create("sample", "neutral", package, "controller");
        var request = new ScriptSampleRequest(configuration, ScriptSampleBuilder.Targets(configuration, TemplateMaterialSlot.UnsungFill)[0],
            TemplateMaterialSlot.UnsungFill);
        TemplateSampleResources resources = new("own.wav", "own.png", null);
        static (string Id, ulong Seed)[] Identities(ProjectSnapshot snapshot) => [.. snapshot.Effects.Instances.Select(item => (item.Id, item.Seed))];
        var author = Identities(ScriptSampleBuilder.Build(request, resources));
        Assert.NotEmpty(author);
        Assert.Equal(author, Identities(ScriptSampleBuilder.Build(request, resources)));

        var template = MakoKaraGenerator.Core.Projects.StyleTemplate.Create("unseeded", "无固定种子", MakoKaraGenerator.Core.Projects.StyleTemplateKind.Composite,
            MakoKaraGenerator.Core.Projects.LyricStyle.Default, [], [MakoKaraGenerator.Core.Projects.TemplateEffectRecipe.FromConfiguration("recipe", configuration)], [package]);
        Assert.Null(template.EffectRecipes[0].FixedSeed);
        var scene = TemplateSampleScene.WorkshopScene(true, null);
        var target = TemplateSampleScene.Targets(scene, template)[0];
        var workshop = Identities(ProjectSnapshot.FromProject(TemplateSampleScene.ApplyWorkshop(scene, template, target, null)));
        Assert.NotEmpty(workshop);
        Assert.Equal(workshop, Identities(ProjectSnapshot.FromProject(TemplateSampleScene.ApplyWorkshop(scene, template, target, null))));
        Assert.All(workshop, item => Assert.Equal(ScriptSampleBuilder.Seed, item.Seed));
    }

    [Fact]
    public async Task 纯执行器真实编译固定包并保留完整源码失败来源()
    {
        using TemporaryDirectory temp = new();
        var package = MixedEffectPackageFixture.Contents(controllerSource: "public sealed class PackEffect { invalid !!! }");
        var configuration = ScriptConfiguration.Create("broken", "neutral", package, "controller");
        var snapshot = ScriptSampleBuilder.Build(new(configuration,
            ScriptSampleBuilder.Targets(configuration, TemplateMaterialSlot.UnsungFill)[0], TemplateMaterialSlot.UnsungFill), new("own.wav", "own.png", null));
        var result = await new RuntimeFrameValidator(Factory(temp.FilePath("cache"))).ValidateAsync(snapshot, new(3));
        Assert.False(result.Succeeded); Assert.Equal(0, result.OpaqueFrames); Assert.NotEmpty(result.Issues);
        Assert.All(result.Issues, issue =>
        {
            Assert.NotNull(issue.Compilation); Assert.Equal(package.PackageId, issue.Compilation.PackageId);
            Assert.Equal(package.Revision, issue.Compilation.Revision); Assert.Equal("src/PackEffect.cs", issue.Compilation.Source.Path);
            Assert.True(issue.Compilation.Source.Line > 0); Assert.True(issue.IsError); Assert.Null(issue.Message);
        });
    }

    [Fact]
    public async Task 纯执行器渲染真实控制器和原生图的两种输出()
    {
        using TemporaryDirectory temp = new(); var package = MixedEffectPackageFixture.Contents();
        var configuration = ScriptConfiguration.Create("real", "neutral", package, "controller");
        var snapshot = ScriptSampleBuilder.Build(new(configuration,
            ScriptSampleBuilder.Targets(configuration, TemplateMaterialSlot.UnsungFill)[0], TemplateMaterialSlot.UnsungFill), new("own.wav", "own.png", null));
        // This fixed snapshot intentionally uses a color backdrop, independent of media preparation.
        snapshot = snapshot with { Background = Core.Projects.BackgroundSource.Color };
        var result = await new RuntimeFrameValidator(Factory(temp.FilePath("cache"))).ValidateAsync(snapshot, new(3));
        Assert.True(result.Succeeded, string.Join('\n', result.Issues)); Assert.Equal(3, result.OpaqueFrames); Assert.Equal(3, result.TransparentFrames);
        string artifact = Assert.Single(Directory.EnumerateDirectories(temp.FilePath("cache")),
            path => Path.GetFileName(path) != ".locks");
        Assert.Collection(Directory.EnumerateFiles(artifact).Select(Path.GetFileName).Order(StringComparer.Ordinal),
            name => Assert.Equal("build.json", name), name => Assert.Equal("effect.dll", name), name => Assert.Equal("effect.pdb", name));
    }

    [Fact]
    public async Task 作者媒体关闭共享同一等待任务且仅删除自己的目录()
    {
        using TemporaryDirectory temp = new(); string neighbor = temp.FilePath("neighbor"); Directory.CreateDirectory(neighbor);
        await using var lease = new TemplateSampleLease(temp.Path); var release = new TaskCompletionSource<TemplateSampleResources>();
        var loading = lease.LoadAsync((path, _) => { Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "owned"), "owned"); return release.Task; }, default);
        Task first = lease.DisposeAsync().AsTask(); Task second = lease.DisposeAsync().AsTask();
        try
        {
            Assert.Same(first, second); Assert.False(first.IsCompleted); Assert.True(Directory.Exists(lease.DirectoryPath));
            Assert.NotSame(first, await Task.WhenAny(first, Task.Delay(100)));
        }
        finally
        {
            release.TrySetResult(new("own.wav", "own.png", null)); await loading; await first;
        }
        Assert.False(Directory.Exists(lease.DirectoryPath)); Assert.True(Directory.Exists(neighbor));
        Assert.Throws<ObjectDisposedException>(() => { _ = lease.LoadAsync(); });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 纯执行器取消等待忽略取消的帧返回并释放后结束(bool transparent)
    {
        var snapshot = ProjectSnapshot.FromProject(TemplateSampleScene.WorkshopScene(false, null));
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var factory = new RecordingFactory { BeforeRender = request =>
        { if (request.TransparentBackground == transparent) { entered.TrySetResult(); return release.Task; } return Task.CompletedTask; } };
        using CancellationTokenSource cancellation = new();
        Task pending = new RuntimeFrameValidator(factory).ValidateAsync(snapshot, new(3), cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancellation.Cancel();
        Assert.False(pending.IsCompleted); release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(transparent ? 2 : 1, factory.Disposed); Assert.All(factory.Frames, frame => Assert.True(frame.IsDisposed));
    }

    private static ProjectRenderSessionFactory Factory(string cache) => new(new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), cache), new FfmpegVideoFrameProvider(), ReplayReason.Initial);
    private sealed class InlineProgress(Action<RuntimeValidationProgress> report) : IProgress<RuntimeValidationProgress>
    { public void Report(RuntimeValidationProgress value) => report(value); }

    [Fact]
    public async Task 正式验证保留帧主失败且释放失败仅附加到原异常()
    {
        var primary = new PreparedFrameException("actual-frame-code", "actual frame failure");
        var cleanup = new IOException("cleanup only");
        var factory = new RecordingFactory { BeforeRender = _ => Task.FromException(primary), OnDispose = () => throw cleanup };
        var result = await new RuntimeFrameValidator(factory).ValidateAsync(Snapshot(), new(1));
        Assert.False(result.Succeeded); Assert.Equal("actual-frame-code", Assert.Single(result.Issues).Code);
        Assert.Same(cleanup, primary.Data["RenderSessionCleanupFailure"]); Assert.Equal(1, factory.DisposeAttempts);
    }

    [Fact]
    public async Task 正式验证保留取消而非以释放失败改成普通失败报告()
    {
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        var primary = new OperationCanceledException(cancellation.Token);
        var cleanup = new IOException("cleanup failed");
        var factory = new RecordingFactory { BeforeRender = _ => Task.FromException(primary), OnDispose = () => throw cleanup };
        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() => new RuntimeFrameValidator(factory).ValidateAsync(Snapshot(), new(1)));
        Assert.Same(primary, actual); Assert.Same(cleanup, actual.Data["RenderSessionCleanupFailure"]); Assert.Equal(1, factory.DisposeAttempts);
    }

    [Fact]
    public async Task 正式验证仅释放失败仍须报告失败且不创建第二会话()
    {
        var factory = new RecordingFactory { OnDispose = () => throw new IOException("cleanup-only") };
        var result = await new RuntimeFrameValidator(factory).ValidateAsync(Snapshot(), new(1));
        Assert.False(result.Succeeded); Assert.Equal(1, result.OpaqueFrames); Assert.Equal(0, result.TransparentFrames);
        Assert.Contains(result.Issues, value => value.Code == nameof(IOException) && value.IsError); Assert.Equal(1, factory.Created);
    }

    [Fact]
    public async Task 中立失败保实际发生身份与来源并禁止成功报告()
    {
        var occurrence = new RuntimeDiagnosticOccurrence(Guid.NewGuid(), 1, new(new("actual-instance"), new string('b', 64), "Evaluate", 17,
            new(DiagnosticSeverity.Error, "actual-error", "actual message", new("src/effect.cs", 2, 3)))
        { PackageIdentity = new("pkg", "sha256:" + new string('c', 64), "main") });
        var actual = RuntimeDiagnosticSnapshot.FromOccurrences([occurrence]);
        var failure = new RenderSessionFailureException("actual-error", "failed", actual);
        var factory = new RecordingFactory { BeforeRender = _ => Task.FromException(failure) };
        var result = await new RuntimeFrameValidator(factory).ValidateAsync(Snapshot(), new(1));
        Assert.False(result.Succeeded); Assert.Equal(0, result.OpaqueFrames); Assert.Equal(occurrence, Assert.Single(result.Diagnostics).Occurrence);
        Assert.Equal("actual-error", Assert.Single(result.Issues).Code); Assert.Equal(1, factory.Disposed);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task 累计预算在加入集合前拒绝且释放原会话(bool occurrenceBudget)
    {
        int inspected = 0;
        var budget = occurrenceBudget ? new RuntimeValidationBudget(256, 3, inspectDiagnostic: _ => inspected++)
            : new RuntimeValidationBudget(1, 512, inspectIssue: _ => inspected++);
        var factory = new RecordingFactory();
        await Assert.ThrowsAsync<RuntimeValidationBudgetExceededException>(() => new RuntimeFrameValidator(factory).ValidateAsync(Snapshot(), new(3), null, budget));
        Assert.Equal(occurrenceBudget ? 3 : 1, inspected); Assert.All(factory.Frames, value => Assert.True(value.IsDisposed));
        Assert.Equal(occurrenceBudget ? 1 : 2, factory.Disposed);
    }

    [Fact]
    public async Task 宿主同步预算检查失败保主失败并等待释放()
    {
        var failure = new RuntimeValidationBudgetExceededException(); var cleanup = new IOException("cleanup");
        var factory = new RecordingFactory { OnDispose = () => throw cleanup };
        var budget = new RuntimeValidationBudget(256, 512, inspectDiagnostic: _ => throw failure);
        var actual = await Assert.ThrowsAsync<RuntimeValidationBudgetExceededException>(() => new RuntimeFrameValidator(factory).ValidateAsync(Snapshot(), new(3), null, budget));
        Assert.Same(failure, actual); Assert.Same(cleanup, actual.Data["RenderSessionCleanupFailure"]);
        Assert.Equal(1, factory.DisposeAttempts); Assert.True(Assert.Single(factory.Frames).IsDisposed);
    }

    private static ProjectSnapshot Snapshot() => ProjectSnapshot.FromProject(TemplateSampleScene.WorkshopScene(false, null));
    private sealed class RecordingFactory : IRenderFrameSessionFactory
    {
        public int Created { get; private set; }
        public int Disposed { get; private set; }
        public int DisposeAttempts { get; private set; }
        public List<RenderFrame> Frames { get; } = [];
        public List<RenderRequest> Requests { get; } = [];
        public Action? OnDispose { get; init; }
        public Func<RenderRequest, Task>? BeforeRender { get; init; }
        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
        { Created++; return ValueTask.FromResult<IRenderFrameSession>(new Session(this)); }
        private sealed class Session(RecordingFactory owner) : IRenderFrameSession
        {
            private readonly RuntimeDiagnosticBuffer _diagnostics = new();
            public async ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
            {
                owner.Requests.Add(request); if (owner.BeforeRender is { } before) await before(request);
                var diagnostic = new RuntimeDiagnostic(new("actual"), new(new string('a', 64)), "Evaluate", request.TimeUs,
                    new(DiagnosticSeverity.Warning, "Notice", "same text"));
                _diagnostics.Add(diagnostic); _diagnostics.Add(diagnostic);
                var target = request.ResolveTarget();
                var frame = new RenderFrame(ArrayPool<byte>.Shared.Rent(target.PixelWidth * target.PixelHeight * 4), target,
                    PixelFormat.Bgra8888Premultiplied, request.TimeUs, pooledBuffer: true) { DiagnosticSnapshot = _diagnostics.Snapshot() };
                owner.Frames.Add(frame); return frame;
            }
            public ValueTask DisposeAsync() { owner.DisposeAttempts++; owner.OnDispose?.Invoke(); owner.Disposed++; return ValueTask.CompletedTask; }
        }
    }
}
