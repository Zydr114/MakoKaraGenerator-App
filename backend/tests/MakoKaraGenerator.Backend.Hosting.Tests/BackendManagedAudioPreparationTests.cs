using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendManagedAudioPreparationTests
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task 确认Worker输入拒绝或无音流的新建保原输入分类字段且同资源可重试(bool noStream, bool withLyrics)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        await using Fixture f = await Fixture.Create();
        using TemporaryDirectory worker = new();
        f.Executor.OnExecute = WorkerAudioPreparationProxyTests.Executor(worker, noStream ? "no-stream" : "input-rejected").ExecuteAsync;
        CreateProjectSessionRequest request = f.Request() with { LyricsAssetId = withLyrics ? f.Lyrics.AssetId : null };
        ProjectSessionCreateResult rejected = await f.Registry.CreateAsync("a", request);
        Assert.False(rejected.Accepted);
        Assert.Equal(noStream ? BackendErrorCode.NoAudioStream : BackendErrorCode.AudioPreparationFailed, rejected.Error!.Code);
        Assert.Equal(nameof(NewProjectPreparationRequest.AudioPath), rejected.Error.Field);
        Assert.Null(rejected.Error.FailureCode);
        Assert.DoesNotContain("controlled", rejected.Error.Message, StringComparison.Ordinal);
        Assert.Empty((await f.Registry.ReadClientStateAsync("a")).Sessions);
        Assert.Equal(0, f.References()); Assert.Empty(f.Pins()); Assert.Equal(0, f.NativeCalls);
        f.Executor.OnExecute = null;
        Assert.True((await f.Registry.CreateAsync("a", request)).Accepted);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task 确认Worker输入拒绝或无音流的打开恢复保原终态并释放候选可重试(bool recover, bool noStream)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        await using Fixture f = await Fixture.Create();
        ProjectSession project = (await f.CreateProject()).Session!;
        string target = recover ? await f.PrepareRecovery() : await f.SavedAndClosed(project);
        using TemporaryDirectory worker = new();
        f.Executor.OnExecute = WorkerAudioPreparationProxyTests.Executor(worker, noStream ? "no-stream" : "input-rejected").ExecuteAsync;
        JobResponse failed = recover ? await f.Recover(target) : await f.Open(target);
        Assert.Equal("failed", failed.State); Assert.Null(failed.Result);
        Assert.Equal(noStream ? BackendErrorCode.AudioPreparationFailed : BackendErrorCode.ProjectLoadFailed, failed.Error!.Code);
        Assert.Null(failed.Error.Field); Assert.Null(failed.Error.FailureCode); Assert.Null(failed.FailureCode);
        JsonElement wire = JsonSerializer.SerializeToElement(failed, WireJson);
        Assert.False(wire.TryGetProperty("failureCode", out _));
        Assert.False(wire.GetProperty("error").TryGetProperty("failureCode", out _));
        Assert.DoesNotContain("controlled", failed.Error.Message, StringComparison.Ordinal);
        Assert.Empty((await f.Registry.ReadClientStateAsync("a")).Sessions);
        Assert.Equal(0, f.References()); Assert.Empty(f.Pins()); Assert.Equal(0, f.NativeCalls);
        if (recover) Assert.Equal(target, Assert.Single(f.Registry.ReadRecoveryCandidates("a")).RecoveryId);
        f.Executor.OnExecute = null;
        JobResponse retried = recover ? await f.Recover(target) : await f.Open(target);
        Assert.Equal("succeeded", retried.State);
        Assert.Single((await f.Registry.ReadClientStateAsync("a")).Sessions);
        if (recover) { Assert.True(retried.IsDirty); Assert.Empty(f.Registry.ReadRecoveryCandidates("a")); }
    }

    [Theory]
    [InlineData(false, "WorkerAudioPreparationTimedOut")]
    [InlineData(true, "WorkerAudioPreparationTimedOut")]
    [InlineData(false, "WorkerAudioPreparationTransportFailed")]
    [InlineData(true, "WorkerAudioPreparationTransportFailed")]
    [InlineData(false, "WorkerAudioPreparationSourceChanged")]
    [InlineData(true, "WorkerAudioPreparationSourceChanged")]
    public async Task 确认Worker打开恢复的已关联错误回执仍保稳定编号且不发布候选(bool recover, string code)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        await using Fixture f = await Fixture.Create();
        ProjectSession project = (await f.CreateProject()).Session!;
        string target = recover ? await f.PrepareRecovery() : await f.SavedAndClosed(project);
        using TemporaryDirectory worker = new();
        f.Executor.OnExecute = WorkerAudioPreparationProxyTests.Executor(worker, code).ExecuteAsync;
        JobResponse failed = recover ? await f.Recover(target) : await f.Open(target);
        Assert.Equal("failed", failed.State); Assert.Equal(BackendErrorCode.TaskExecutionFailed, failed.Error!.Code);
        Assert.Equal(code, failed.FailureCode); Assert.Equal(code, failed.Error.FailureCode);
        Assert.Null(failed.Result); Assert.DoesNotContain("controlled", failed.Error.Message, StringComparison.Ordinal);
        Assert.Empty((await f.Registry.ReadClientStateAsync("a")).Sessions);
        Assert.Equal(0, f.References()); Assert.Empty(f.Pins()); Assert.Equal(0, f.NativeCalls);
        f.Executor.OnExecute = null;
        Assert.Equal("succeeded", (recover ? await f.Recover(target) : await f.Open(target)).State);
    }

    [Fact]
    public async Task 音频准备停止后不能重新配置已取消的停止闸门()
    {
        await using Fixture f = await Fixture.Create(configure: false);
        await f.Registry.StopAudioPreparationAsync();
        Assert.Throws<InvalidOperationException>(() => f.Registry.ConfigureAudioPreparation(null, new(false, false, "stopped")));
    }

    [Fact]
    public async Task 显式Worker准备保持真实duration新建歌词与重开而不调用可信native接缝()
    {
        await using Fixture f = await Fixture.Create();
        ProjectSession project = (await f.CreateProject()).Session!;
        Assert.Equal(3_000_000, project.AudioDurationUs); Assert.Equal(1, f.Executor.Calls); Assert.Equal(0, f.NativeCalls);
        ProjectSessionSummaryResponse summary = (await f.Registry.ReadSummaryAsync("a", project.ProjectSessionId))!;
        Assert.True(summary.IsDirty); Assert.False(summary.HasPath); Assert.Equal("0", summary.ProjectRevision); Assert.False(summary.CanUndo);
        LyricsLinesResponse lyrics = (await f.Registry.ReadLyricsAsync("a", project.ProjectSessionId, "primary"))!;
        Assert.Equal("3000000", Assert.Single(lyrics.Lines).Time.EndUs);
        string handle = await f.SavedAndClosed(project);
        JobResponse opened = await f.Open(handle); Assert.Equal("succeeded", opened.State); Assert.Equal(2, f.Executor.Calls); Assert.Equal(0, f.NativeCalls);
        Assert.True(f.Registry.TryGet("a", opened.ProjectSessionId!, out ProjectSession reopened)); Assert.Equal(project.AudioDurationUs, reopened.AudioDurationUs);
        Assert.Equal("0", opened.Result!.ProjectRevision); Assert.False(opened.IsDirty);
    }

    [Fact]
    public async Task disabledWorker新建与重开明确拒绝且不会回退原native()
    {
        await using Fixture f = await Fixture.Create(configure: false);
        ProjectSession native = (await f.CreateProject()).Session!; Assert.Equal(1, f.NativeCalls);
        string handle = await f.SavedAndClosed(native);
        f.Registry.ConfigureAudioPreparation(null, new(false, false, "old-worker"));
        BackendAudioPreparationCapabilitiesDto cap = f.Registry.AudioPreparationCapabilities;
        Assert.False(cap.Enabled); Assert.Equal("old-worker", cap.DisabledReason); Assert.Equal(500_000, cap.MaxBaseBuckets);
        Assert.Equal(["wav", "mp3", "flac", "aac", "mov", "ogg"], cap.Families);
        ProjectSessionCreateResult rejected = await f.CreateProject(); Assert.False(rejected.Accepted); Assert.Equal(BackendErrorCode.TaskUnavailable, rejected.Error!.Code);
        foreach (CreateProjectSessionRequest malformedRequest in new[]
        {
            f.Request() with { Name = "" },
            f.Request() with { AudioAssetId = "" },
            f.Request() with { LyricsAssetId = "" },
            f.Request() with { Composition = new CompositionDto(CanvasWidth: 0) },
            f.Request() with { SplitRule = int.MaxValue },
        })
        {
            ProjectSessionCreateResult malformed = await f.Registry.CreateAsync("a", malformedRequest);
            Assert.False(malformed.Accepted); Assert.Equal(BackendErrorCode.InvalidRequest, malformed.Error!.Code);
        }
        Assert.Equal(0, f.Executor.Calls); Assert.Equal(0, f.References());
        JobResponse opened = await f.Open(handle); Assert.Equal("failed", opened.State); Assert.Equal(BackendErrorCode.TaskUnavailable, opened.Error!.Code);
        Assert.Equal(1, f.NativeCalls); Assert.Equal(0, f.Executor.Calls); Assert.Empty((await f.Registry.ReadClientStateAsync("a")).Sessions);
        Assert.Equal(0, f.References()); Assert.Empty(f.Pins());
    }

    [Fact]
    public async Task 无音频的新建空工程与仅歌词工程不依赖Worker准备能力且不保留素材引用()
    {
        await using Fixture f = await Fixture.Create(configure: false);
        f.Registry.ConfigureAudioPreparation(null, new(false, false, "old-worker"));
        foreach (string? lyricsAssetId in new[] { null, f.Lyrics.AssetId })
        {
            ProjectSessionCreateResult result = await f.Registry.CreateAsync("a", new("without-audio", LyricsAssetId: lyricsAssetId));
            Assert.True(result.Accepted);
            ProjectSessionSummaryResponse summary = (await f.Registry.ReadSummaryAsync("a", result.Session!.ProjectSessionId))!;
            Assert.Equal("", summary.AudioAssetId); Assert.Equal(0, summary.AudioDurationUs);
            Assert.Equal(lyricsAssetId is null ? 0 : 1, summary.LyricsLineCount);
            Assert.Equal("0", summary.ProjectRevision); Assert.True(summary.IsDirty); Assert.False(summary.CanUndo);
        }
        Assert.Equal(0, f.Executor.Calls); Assert.Equal(0, f.NativeCalls); Assert.Equal(0, f.References()); Assert.Empty(f.Pins());
    }

    [Fact]
    public async Task active超时必须等待实际解码排水且公开稳定失败不冒充用户取消()
    {
        await using Fixture f = await Fixture.Create(timeout: 1); BlockingDecode block = new(); f.Executor.OnExecute = block.Execute;
        Task<ProjectSessionCreateResult> pending = f.CreateProject(); await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        await block.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(8)); Assert.False(pending.IsCompleted); Assert.Equal(2, f.References());
        block.Drain.TrySetResult(); ProjectSessionCreateResult failed = await pending;
        Assert.False(failed.Accepted); Assert.Equal(BackendErrorCode.TaskExecutionFailed, failed.Error!.Code);
        Assert.Equal("WorkerAudioPreparationTimedOut", failed.Error.FailureCode); Assert.Empty((await f.Registry.ReadClientStateAsync("a")).Sessions);
        Assert.Equal(0, f.References()); f.Executor.OnExecute = null; Assert.True((await f.CreateProject()).Accepted);
    }

    [Fact]
    public async Task 排队预算在全局createGate之前生效且排队期限不重置为执行期限()
    {
        await using Fixture f = await Fixture.Create(timeout: 1); BlockingDecode block = new(); f.Executor.OnExecute = block.Execute;
        Task<ProjectSessionCreateResult> running = f.CreateProject(); await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Task<ProjectSessionCreateResult>[] queued = [f.CreateProject(), f.CreateProject(), f.CreateProject(), f.CreateProject()];
        foreach (CreateProjectSessionRequest malformedRequest in new[]
        {
            f.Request() with { Name = "" },
            f.Request() with { AudioAssetId = "" },
            f.Request() with { LyricsAssetId = "" },
            f.Request() with { Composition = new CompositionDto(CanvasWidth: 0) },
            f.Request() with { SplitRule = int.MaxValue },
        })
        {
            ProjectSessionCreateResult malformed = await f.Registry.CreateAsync("a", malformedRequest);
            Assert.False(malformed.Accepted); Assert.Equal(BackendErrorCode.InvalidRequest, malformed.Error!.Code);
        }
        ProjectSessionCreateResult quota = await f.CreateProject(); Assert.Equal(BackendErrorCode.RequestQuotaExceeded, quota.Error!.Code);
        ProjectSessionCreateResult[] expired = await Task.WhenAll(queued).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.All(expired, result => Assert.Equal("WorkerAudioPreparationTimedOut", result.Error!.FailureCode));
        Assert.Equal(1, f.Executor.Calls); Assert.False(running.IsCompleted); block.Drain.TrySetResult();
        Assert.Equal("WorkerAudioPreparationTimedOut", (await running).Error!.FailureCode); Assert.Equal(0, f.References());
        f.Executor.OnExecute = null; Assert.True((await f.CreateProject()).Accepted);
    }

    [Fact]
    public async Task 直接用户取消等实际排水后保留取消异常并释放等待名额()
    {
        await using Fixture f = await Fixture.Create(); BlockingDecode block = new(); f.Executor.OnExecute = block.Execute;
        using CancellationTokenSource caller = new(); Task<ProjectSessionCreateResult> pending = f.CreateProject(caller.Token);
        await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8)); caller.Cancel(); await block.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.False(pending.IsCompleted); block.Drain.TrySetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, f.References()); f.Executor.OnExecute = null; Assert.True((await f.CreateProject()).Accepted);
    }

    [Fact]
    public async Task shutdown在同步create回执排水之前取消准备且等待真实释放()
    {
        await using Fixture f = await Fixture.Create(); BlockingDecode block = new(); f.Executor.OnExecute = block.Execute;
        Task<ProjectSessionCreateResult> pending = f.Registry.CreateAsync("a", f.Request() with { Metadata = f.Meta() });
        await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Task<ProjectSessionCreateResult>[] queued = [f.Registry.CreateAsync("a", f.Request() with { Metadata = f.Meta() }),
            f.Registry.CreateAsync("a", f.Request() with { Metadata = f.Meta() })];
        Task stopping = f.Registry.ShutdownAsync().AsTask();
        await block.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(8)); Assert.False(stopping.IsCompleted); Assert.False(pending.IsCompleted);
        block.Drain.TrySetResult(); await stopping.WaitAsync(TimeSpan.FromSeconds(8)); Assert.False((await pending).Accepted);
        Assert.All(await Task.WhenAll(queued), result => Assert.False(result.Accepted));
        Assert.Equal(0, f.References()); Assert.Equal(0, f.NativeCalls); Assert.Equal(1, f.Executor.Calls);
    }

    [Fact]
    public async Task 同gate停止接收不依赖稍后取消位且Stop仍等全部原拥有排水()
    {
        // This controlled seam models an unset cancellation bit after Stop's admission snapshot;
        // it does not claim a naturally scheduled reproduction of the source race.
        await using Fixture f = await Fixture.Create(); BlockingDecode block = new(); f.Executor.OnExecute = block.Execute;
        Task<ProjectSessionCreateResult> running = f.Registry.CreateAsync("a", f.Request() with { Metadata = f.Meta() });
        await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8)); Task stopping = f.Registry.StopAudioPreparationAsync();
        await block.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(8)); Assert.False(stopping.IsCompleted);
        FieldInfo field = typeof(ProjectSessionRegistry).GetField("_audioPreparationStop", BindingFlags.Instance | BindingFlags.NonPublic)!;
        CancellationTokenSource original = (CancellationTokenSource)field.GetValue(f.Registry)!;
        object gate = typeof(ProjectSessionRegistry).GetField("_audioPreparationGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Registry)!;
        using CancellationTokenSource notYetCancelled = new(); Task<ProjectSessionCreateResult>? attempted = null;
        try
        {
            lock (gate)
            {
                field.SetValue(f.Registry, notYetCancelled);
                attempted = f.CreateProject();
            }
            Assert.True(attempted.IsCompleted, "Stop admission must reject while the cancellation bit is still unset.");
            Assert.Equal(BackendErrorCode.SessionClosing, (await attempted).Error!.Code);
            Assert.Equal(1, f.Executor.Calls); Assert.False(stopping.IsCompleted);
        }
        finally
        {
            notYetCancelled.Cancel(); block.Drain.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(8)); await running.WaitAsync(TimeSpan.FromSeconds(8));
            if (attempted is not null)
                try { await attempted.WaitAsync(TimeSpan.FromSeconds(8)); } catch (OperationCanceledException) { }
            lock (gate) field.SetValue(f.Registry, original);
        }
        Assert.Equal(0, f.References()); Assert.Empty((await f.Registry.ReadClientStateAsync("a")).Sessions);
    }

    [Fact]
    public async Task 已接受create的调用方断连只结束等待且同回执可读真实唯一成功()
    {
        await using Fixture f = await Fixture.Create(); TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously); CancellationToken decodedToken = default;
        f.Executor.OnExecute = async (_, token) => { decodedToken = token; entered.TrySetResult(); await release.Task; return Waveform(); };
        using CancellationTokenSource caller = new(); CreateProjectSessionRequest input = f.Request() with { Metadata = f.Meta() };
        Task<ProjectSessionCreateResult> pending = f.Registry.CreateAsync("a", input, caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(8)); caller.Cancel(); Assert.False(decodedToken.IsCancellationRequested);
        release.TrySetResult();
        ProjectSessionCreateResult completed;
        try { completed = await pending; }
        catch (OperationCanceledException) { completed = await f.Registry.CreateAsync("a", input); }
        Assert.True(completed.Accepted);
        ProjectSessionCreateResult retry = await f.Registry.CreateAsync("a", input); Assert.True(retry.Accepted); Assert.Equal(1, f.Executor.Calls);
        Assert.Single((await f.Registry.ReadClientStateAsync("a")).Sessions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 打开超时与用户取消终态分开且固定候选与写租约能原handle重试(bool userCancel)
    {
        await using Fixture f = await Fixture.Create(timeout: 1); string handle = await f.SavedAndClosed((await f.CreateProject()).Session!);
        BlockingDecode block = new(); f.Executor.OnExecute = block.Execute;
        JobResponse accepted = (await f.Registry.StartOpenAsync("a", new(handle, Metadata: f.Meta()))).Response!;
        await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8)); if (userCancel) Assert.True(f.Registry.CancelJob("a", accepted.JobId).Accepted);
        await block.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(8)); Assert.Equal("running", f.Registry.ReadJob("a", accepted.JobId).Response!.State);
        block.Drain.TrySetResult(); JobResponse terminal = await f.Terminal(accepted.JobId);
        Assert.Equal(userCancel ? "cancelled" : "failed", terminal.State);
        if (!userCancel) { Assert.Equal(BackendErrorCode.TaskExecutionFailed, terminal.Error!.Code); Assert.Equal("WorkerAudioPreparationTimedOut", terminal.FailureCode); }
        Assert.Equal(0, f.References()); Assert.Empty(f.Pins()); Assert.Empty((await f.Registry.ReadClientStateAsync("a")).Sessions);
        f.Executor.OnExecute = null; Assert.Equal("succeeded", (await f.Open(handle)).State);
    }

    [Theory]
    [InlineData("WorkerAudioPreparationBudgetExceeded")]
    [InlineData("WorkerAudioPreparationTransportFailed")]
    [InlineData("WorkerAudioPreparationProtocolInvalid")]
    public async Task 受管理执行失败同步create保稳定安全编号且零发布(string code)
    {
        await using Fixture f = await Fixture.Create(); f.Executor.OnExecute = (_, _) => throw new RenderSessionFailureException(code, "/private/path raw tool detail");
        ProjectSessionCreateResult result = await f.CreateProject(); Assert.Equal(BackendErrorCode.TaskExecutionFailed, result.Error!.Code);
        Assert.Equal(code, result.Error.FailureCode); Assert.DoesNotContain("/private/path", result.Error.Message, StringComparison.Ordinal);
        Assert.Empty((await f.Registry.ReadClientStateAsync("a")).Sessions); Assert.Equal(0, f.References());
    }

    [Fact]
    public async Task Worker准备期间实际字节篡改仍由原Host前后身份门禁拒绝()
    {
        await using Fixture f = await Fixture.Create(); f.Executor.OnExecute = (grant, _) =>
        { byte[] changed = File.ReadAllBytes(grant.Path); changed[^1] ^= 1; File.WriteAllBytes(grant.Path, changed); return Task.FromResult<LoadedWaveform?>(Waveform()); };
        ProjectSessionCreateResult failed = await f.CreateProject(); Assert.Equal(BackendErrorCode.TaskExecutionFailed, failed.Error!.Code);
        Assert.Equal(0, f.NativeCalls); Assert.Equal(0, f.References()); Assert.Empty((await f.Registry.ReadClientStateAsync("a")).Sessions);
    }

    private static LoadedWaveform Waveform()
    { WaveformPeakBuilder builder = new(); builder.Append(new float[24_000]); return new(new(9_000_000, 8000, 6, "pcm_s24le", "wav"), builder.Build(8000)); }
    private sealed class BlockingDecode
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Drain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task<LoadedWaveform?> Execute(BackendAudioPreparationGrant grant, CancellationToken token)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Waveform(); }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                Task completed = await Task.WhenAny(
                    Drain.Task,
                    Task.Delay(TimeSpan.FromSeconds(8), CancellationToken.None)).ConfigureAwait(false);
                throw;
            }
        }
    }
    private sealed class Executor : IBackendAudioPreparationExecutor
    {
        internal int Calls;
        internal Func<BackendAudioPreparationGrant, CancellationToken, Task<LoadedWaveform?>>? OnExecute;
        public void ValidateInput(BackendAudioPreparationGrant grant) { Assert.True(Path.IsPathFullyQualified(grant.Path)); Assert.Equal(64, grant.Sha256.Length); }
        public Task<LoadedWaveform?> ExecuteAsync(BackendAudioPreparationGrant grant, CancellationToken cancellationToken = default)
        { Calls++; return OnExecute?.Invoke(grant, cancellationToken) ?? Task.FromResult<LoadedWaveform?>(Waveform()); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory = new(); internal readonly Executor Executor = new();
        internal ResourceStore Resources = null!; internal ProjectSessionRegistry Registry = null!;
        internal StoredAsset Audio = null!, Lyrics = null!; internal int NativeCalls;
        private string State => _directory.FilePath("state"); private string Root => _directory.FilePath("projects");
        internal static async Task<Fixture> Create(bool configure = true, int timeout = 60)
        {
            Fixture f = new(); Directory.CreateDirectory(f.Root);
            f.Resources = new(new() { ResourceRoot = f._directory.FilePath("resources") });
            f.Audio = await f.Resources.UploadAsync("a", "song.wav", "audio/wav", new MemoryStream([82, 73, 70, 70, 1, 2, 3, 4, 87, 65, 86, 69]));
            f.Lyrics = await f.Resources.UploadAsync("a", "song.lrc", "text/plain", new MemoryStream("[00:00.00]hello\n"u8.ToArray()));
            f.Registry = new("host", f.Resources, new(f.Native));
            f.Registry.ConfigurePersistence(new() { StateRoot = f.State, AutoSaveInterval = TimeSpan.FromHours(1), AllowedProjectRoots = new Dictionary<string, string> { ["projects"] = f.Root } }, loadAudio: f.Native);
            if (configure) f.Registry.ConfigureAudioPreparation(f.Executor, new(true, true), timeout);
            return f;
        }
        private Task<LoadedWaveform?> Native(string path, CancellationToken token) { NativeCalls++; return Task.FromResult<LoadedWaveform?>(Waveform()); }
        internal CreateProjectSessionRequest Request() => new("managed", Audio.AssetId, Lyrics.AssetId);
        internal Task<ProjectSessionCreateResult> CreateProject(CancellationToken token = default) => Registry.CreateAsync("a", Request(), token);
        internal RequestMetadataDto Meta() => new(Guid.NewGuid().ToString("N"), Registry.HostInstanceId, DateTimeOffset.UtcNow.AddMinutes(1));
        internal int References() => ((IDictionary)typeof(ResourceStore).GetField("_references", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Resources)!).Values.Cast<int>().Sum();
        internal string[] Pins() => [.. ((IDictionary)typeof(ProjectSessionRegistry).GetField("_pinnedReservations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Registry)!).Keys.Cast<string>()];
        internal async Task<string> SavedAndClosed(ProjectSession project)
        {
            var created = await Registry.CreateFileHandleAsync("a", new("projects", "saved", Metadata: Meta()));
            Assert.True(created.Accepted, JsonSerializer.Serialize(created.Error, WireJson));
            string handle = created.Response!.HandleId;
            var saved = await Registry.StartSaveAsync("a", project.ProjectSessionId, new("0", handle, Meta()));
            Assert.True(saved.Accepted, JsonSerializer.Serialize(saved.Error, WireJson));
            Assert.Equal("succeeded", (await Terminal(saved.Response!.JobId)).State);
            // A readable terminal state can precede save cleanup and its Completion signal.
            var closed = await Registry.StartCloseAsync("a", project.ProjectSessionId, new("0", ActiveJobs: "wait", Metadata: Meta()));
            Assert.True(closed.Accepted, JsonSerializer.Serialize(closed.Error, WireJson));
            Assert.Equal("succeeded", (await Terminal(closed.Response!.JobId)).State);
            return handle;
        }
        internal async Task<JobResponse> Open(string handle) => await Terminal((await Registry.StartOpenAsync("a", new(handle, Metadata: Meta()))).Response!.JobId);
        internal async Task<string> PrepareRecovery()
        {
            await Registry.ShutdownAsync();
            Registry = new("host", Resources, new(Native));
            Registry.ConfigurePersistence(new() { StateRoot = State, AutoSaveInterval = TimeSpan.FromHours(1),
                AllowedProjectRoots = new Dictionary<string, string> { ["projects"] = Root } }, loadAudio: Native);
            Registry.ConfigureAudioPreparation(Executor, new(true, true));
            return Assert.Single(Registry.ReadRecoveryCandidates("a")).RecoveryId;
        }
        internal async Task<JobResponse> Recover(string candidate) =>
            await Terminal((await Registry.StartRecoverAsync("a", new(candidate, Meta()))).Response!.JobId);
        internal async Task<JobResponse> Terminal(string id)
        { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8)); while (true) { JobResponse job = Registry.ReadJob("a", id).Response!; if (job.State is "succeeded" or "failed" or "cancelled") return job; await Task.Delay(10, timeout.Token); } }
        public async ValueTask DisposeAsync() { await Registry.DisposeAsync(); _directory.Dispose(); }
    }
}
