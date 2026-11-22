using System.Collections;
using System.Reflection;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendFixedAudioPreparationTests
{
    [Theory]
    [InlineData("bytes", false)]
    [InlineData("size", false)]
    [InlineData("link", false)]
    [InlineData("parentLink", false)]
    [InlineData("metadata", false)]
    [InlineData("bytes", true)]
    [InlineData("size", true)]
    [InlineData("link", true)]
    [InlineData("missing", true)]
    public async Task 固定资源准备前后真实变更拒绝发布且引用释放可修复重试(string damage, bool duringDecode)
    {
        if (OperatingSystem.IsWindows() && damage is "link" or "parentLink") return;
        await using Fixture f = await Fixture.Create();
        string cursor = (await f.Registry.ReadClientStateAsync("a")).EventCursor;
        if (duringDecode) f.OnDecode = (path, _) => { Damage(f, path, damage); return Task.FromResult<LoadedWaveform?>(Sample()); };
        else Damage(f, f.Audio.StoragePath, damage);
        try
        {
            ProjectSessionCreateResult rejected = await f.CreateProject();
            Assert.False(rejected.Accepted); Assert.Equal(BackendErrorCode.TaskExecutionFailed, rejected.Error!.Code);
            Assert.Equal(duringDecode ? 1 : 0, f.Decodes); Assert.Equal(0, f.ReferenceCount(f.Audio.AssetId));
            ClientStateResponse state = await f.Registry.ReadClientStateAsync("a"); Assert.Empty(state.Sessions); Assert.Equal(cursor, state.EventCursor);
        }
        finally { await f.Repair(); }
        f.OnDecode = null;
        ProjectSessionCreateResult retry = await f.CreateProject(); Assert.True(retry.Accepted, retry.Error?.Message);
        ProjectSessionSummaryResponse summary = (await f.Registry.ReadSummaryAsync("a", retry.Session!.ProjectSessionId))!;
        Assert.Equal("0", summary.ProjectRevision); Assert.True(summary.IsDirty); Assert.False(summary.CanUndo); Assert.False(summary.CanRedo);
        Assert.Equal(1, f.ReferenceCount(f.Audio.AssetId));
    }

    [Fact]
    public async Task 真正进入注册等待后取消不泄漏候选音频引用且原资源TTL能回收()
    {
        await using Fixture f = await Fixture.Create(); SemaphoreSlim membership = Field<SemaphoreSlim>(f.Registry, "_membershipGate");
        await membership.WaitAsync(); using CancellationTokenSource cancel = new();
        try
        {
            Task<ProjectSessionCreateResult> pending = f.CreateProject(cancel.Token);
            // Preparation retention + candidate session pin proves construction was reached before cancellation.
            await WaitUntil(() => f.ReferenceCount(f.Audio.AssetId) == 2);
            cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(0, f.ReferenceCount(f.Audio.AssetId));
        }
        finally { membership.Release(); }
        Assert.Empty((await f.Registry.ReadClientStateAsync("a")).Sessions);
        f.Clock.Advance(11); Assert.Equal(2, f.Resources.CollectExpired()); Assert.False(File.Exists(f.Audio.StoragePath));
    }

    [Fact]
    public async Task 注册已经加入成员后事件失败保持成功成员及合法sessionPin不回滚()
    {
        await using Fixture f = await Fixture.Create(); bool threw = false;
        f.Clock.BeforeRead = () =>
        {
            if (!threw && Field<IDictionary>(f.Registry, "_sessions").Count == 1)
            { threw = true; throw new IOException("owned event publication failure"); }
        };
        IOException error = await Assert.ThrowsAsync<IOException>(() => f.CreateProject()); Assert.Equal("owned event publication failure", error.Message); Assert.True(threw);
        f.Clock.BeforeRead = null;
        ProjectSessionSummaryResponse retained = Assert.Single((await f.Registry.ReadClientStateAsync("a")).Sessions);
        Assert.Equal("0", retained.ProjectRevision); Assert.False(retained.CanUndo); Assert.Equal(1, f.ReferenceCount(f.Audio.AssetId));
        f.Clock.Advance(11); f.Resources.CollectExpired(); Assert.True(File.Exists(f.Audio.StoragePath));
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("metadata")]
    [InlineData("missing")]
    public async Task 打开准备后实际资源变化在候选发布前拒绝且目录租约能重新打开(string damage)
    {
        await using Fixture f = await Fixture.Create(); string handle = await f.SavedAndClosed();
        f.OnDecode = (path, _) =>
        {
            if (damage == "metadata")
            {
                IDictionary assets = Field<IDictionary>(f.Resources, "_assets");
                StoredAsset current = assets.Values.Cast<StoredAsset>().Single(asset => asset.StoragePath == path);
                assets[current.AssetId] = current with { Sha256 = new string('b', 64) };
            }
            else if (damage == "missing") File.Delete(path);
            else { byte[] bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; File.WriteAllBytes(path, bytes); }
            return Task.FromResult<LoadedWaveform?>(Sample());
        };
        JobResponse failed = await f.Open(handle); Assert.Equal("failed", failed.State); Assert.Equal(BackendErrorCode.TaskExecutionFailed, failed.Error!.Code);
        Assert.Empty((await f.Registry.ReadClientStateAsync("a")).Sessions); Assert.Equal(0, f.TotalReferences()); Assert.Empty(f.Pins());
        f.OnDecode = null; Assert.Equal("succeeded", (await f.Open(handle)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 初始化真实入口失败释放未发布sessionPin与写租约并保原主因或取消(bool cancelAndCleanupFailure)
    {
        if (OperatingSystem.IsWindows() && cancelAndCleanupFailure) return;
        await using Fixture f = await Fixture.Create(); string handle = await f.SavedAndClosed();
        IOException initialization = new("owned initialization failure"); Exception? observed = null; string? protectedRoot = null;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!cancelAndCleanupFailure)
        {
            f.Initialize = root => { entered.TrySetResult(); Assert.Equal(1, f.TotalReferences()); throw initialization; };
        }
        else
        {
            f.OnDecode = async (_, token) =>
            {
                protectedRoot = Path.GetDirectoryName(Assert.Single(f.Pins()))!;
                SetMode(protectedRoot, UnixFileMode.UserRead | UnixFileMode.UserExecute); entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); return Sample(); }
                catch (OperationCanceledException error) { observed = error; throw; }
            };
        }
        JobResponse accepted = (await f.Registry.StartOpenAsync("a", new(handle, Metadata: f.Meta()))).Response!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        if (cancelAndCleanupFailure) f.Registry.CancelJob("a", accepted.JobId);
        JobResponse failed = await f.Terminal(accepted.JobId);
        try
        {
            Assert.Equal(cancelAndCleanupFailure ? "cancelled" : "failed", failed.State);
            Assert.Empty((await f.Registry.ReadClientStateAsync("a")).Sessions); Assert.Equal(0, f.TotalReferences());
            if (cancelAndCleanupFailure)
            {
                Assert.IsAssignableFrom<OperationCanceledException>(observed);
                Assert.True(observed!.Data["OpenPinnedInputCleanupFailure"] is IOException or UnauthorizedAccessException);
                string pin = Assert.Single(f.Pins()); Assert.True(Directory.Exists(pin));
                Assert.True(Record.Exception(() => Directory.Delete(pin, true)) is IOException or UnauthorizedAccessException);
            }
            else { Assert.Equal(BackendErrorCode.ProjectLoadFailed, failed.Error!.Code); Assert.Empty(f.Pins()); }
        }
        finally
        {
            if (protectedRoot is not null)
            {
                SetMode(protectedRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                foreach (string pin in f.Pins()) await InvokeTask(f.Registry, "ReleasePinnedInputAsync", pin);
            }
        }
        f.Initialize = null; f.OnDecode = null;
        Assert.Equal("succeeded", (await f.Open(handle)).State);
    }

    [Fact]
    public async Task 准备读取租约在native接缝前已释放且只留下retentionPin()
    {
        await using Fixture f = await Fixture.Create();
        f.OnDecode = (path, _) =>
        {
            Assert.Equal(1, f.ReferenceCount(f.Audio.AssetId));
            using FileStream exclusive = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.Equal(Fixture.Bytes.Length, exclusive.Length); return Task.FromResult<LoadedWaveform?>(Sample());
        };
        Assert.True((await f.CreateProject()).Accepted); Assert.Equal(1, f.ReferenceCount(f.Audio.AssetId));
    }

    [Fact]
    public async Task 原合法无音轨工程打开不运行decoder也不制造音频metadata或历史()
    {
        await using Fixture f = await Fixture.Create(); string directory = Path.Combine(f.Root, "silent");
        ProjectWorkspace empty = ProjectWorkspace.CreateNew("silent"); ProjectStore.Save(empty.Project, directory);
        string handle = (await f.Registry.CreateFileHandleAsync("a", new("projects", "silent", Metadata: f.Meta()))).Response!.HandleId;
        JobResponse opened = await f.Open(handle); Assert.Equal("succeeded", opened.State); Assert.Equal(0, f.Decodes);
        Assert.True(f.Registry.TryGet("a", opened.ProjectSessionId!, out ProjectSession session));
        ProjectWorkspace workspace = session.Workspace;
        Assert.Null(workspace.Project.AudioPath); Assert.Null(workspace.Project.AudioMedia); Assert.Equal(0, workspace.History.UndoCount);
        Assert.Equal("0", opened.Result!.ProjectRevision); Assert.False(opened.IsDirty); Assert.Equal(0, f.TotalReferences());
    }

    private static void SetMode(string path, UnixFileMode mode)
    { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode); }
    private static void Damage(Fixture f, string path, string damage)
    {
        switch (damage)
        {
            case "bytes": byte[] changed = File.ReadAllBytes(path); changed[^1] ^= 1; File.WriteAllBytes(path, changed); break;
            case "size": File.WriteAllBytes(path, [.. Fixture.Bytes, 1]); break;
            case "missing": File.Delete(path); break;
            case "link": File.Delete(path); string target = path + ".target"; File.WriteAllBytes(target, Fixture.Bytes); File.CreateSymbolicLink(path, target); break;
            case "parentLink": string parent = Path.GetDirectoryName(path)!; Directory.Move(parent, parent + ".target"); Directory.CreateSymbolicLink(parent, parent + ".target"); break;
            case "metadata": Field<IDictionary>(f.Resources, "_assets")[f.Audio.AssetId] = f.Audio with { Sha256 = new string('b', 64) }; break;
        }
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static async Task InvokeTask(object instance, string method, string path) => await (Task)instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, [path])!;
    private static async Task WaitUntil(Func<bool> predicate)
    { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8)); while (!predicate()) await Task.Delay(10, timeout.Token); }
    private static LoadedWaveform Sample()
    { WaveformPeakBuilder peaks = new(); peaks.Append(new float[24000]); return new(new(3000000, 8000, 1, "pcm", "wav"), peaks.Build(8000)); }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow; internal Action? BeforeRead;
        public override DateTimeOffset GetUtcNow() { BeforeRead?.Invoke(); return _now; }
        internal void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal static readonly byte[] Bytes = [82, 73, 70, 70, 1, 2, 3, 4, 87, 65, 86, 69];
        private readonly TemporaryDirectory _directory = new(); internal readonly Clock Clock = new();
        internal string Root => _directory.FilePath("projects"); internal string State => _directory.FilePath("state");
        internal ResourceStore Resources = null!; internal ProjectSessionRegistry Registry = null!; internal StoredAsset Audio = null!; private StoredAsset _lyrics = null!;
        internal Func<string, CancellationToken, Task<LoadedWaveform?>>? OnDecode; internal Func<string, IProjectSaveStorage>? Initialize; internal int Decodes;
        internal static async Task<Fixture> Create()
        {
            Fixture f = new(); Directory.CreateDirectory(f.Root);
            f.Resources = new(new() { ResourceRoot = f._directory.FilePath("uploads"), UnreferencedAssetLifetimeSeconds = 10 }, f.Clock);
            f.Audio = await f.Resources.UploadAsync("a", "song.wav", "audio/wav", new MemoryStream(Bytes));
            f._lyrics = await f.Resources.UploadAsync("a", "song.lrc", "text/plain", new MemoryStream("[00:00.00]hello\n"u8.ToArray()));
            f.Registry = new("fixed-host", f.Resources, new(f.Decode), f.Clock);
            f.Registry.ConfigurePersistence(new() { StateRoot = f.State, AutoSaveInterval = TimeSpan.FromHours(1), AllowedProjectRoots = new Dictionary<string, string> { ["projects"] = f.Root } },
                root => f.Initialize?.Invoke(root) ?? new Storage(root), f.Decode);
            return f;
        }
        private Task<LoadedWaveform?> Decode(string path, CancellationToken token)
        { Decodes++; return OnDecode?.Invoke(path, token) ?? Task.FromResult<LoadedWaveform?>(Sample()); }
        internal Task<ProjectSessionCreateResult> CreateProject(CancellationToken token = default) => Registry.CreateAsync("a", new("fixed", Audio.AssetId, _lyrics.AssetId), token);
        internal RequestMetadataDto Meta() => new(Guid.NewGuid().ToString("N"), "fixed-host", Clock.GetUtcNow().AddMinutes(1));
        internal int ReferenceCount(string id) => Field<Dictionary<string, int>>(Resources, "_references").GetValueOrDefault(id);
        internal int TotalReferences() => Field<Dictionary<string, int>>(Resources, "_references").Values.Sum();
        internal string[] Pins() => [.. Field<Dictionary<string, long>>(Registry, "_pinnedReservations").Keys];
        internal async Task Repair()
        {
            string parent = Path.GetDirectoryName(Audio.StoragePath)!;
            if (new DirectoryInfo(parent).LinkTarget is not null) { Directory.Delete(parent); Directory.Move(parent + ".target", parent); }
            if (new FileInfo(Audio.StoragePath).LinkTarget is not null) File.Delete(Audio.StoragePath);
            await File.WriteAllBytesAsync(Audio.StoragePath, Bytes); Field<IDictionary>(Resources, "_assets")[Audio.AssetId] = Audio;
        }
        internal async Task<string> SavedAndClosed()
        {
            ProjectSessionCreateResult created = await CreateProject();
            Assert.True(created.Accepted, created.Error?.Message);
            ProjectSession project = created.Session!;
            var handleResult = await Registry.CreateFileHandleAsync("a", new("projects", "saved", Metadata: Meta()));
            Assert.True(handleResult.Accepted, handleResult.Error?.Message);
            string handle = handleResult.Response!.HandleId;
            var saved = await Registry.StartSaveAsync("a", project.ProjectSessionId, new("0", handle, Meta()));
            Assert.True(saved.Accepted, saved.Error?.Message);
            Assert.Equal("succeeded", (await Terminal(saved.Response!.JobId)).State);
            // Terminal publication precedes the save's final cleanup and Completion.
            var closed = await Registry.StartCloseAsync("a", project.ProjectSessionId, new("0", ActiveJobs: "wait", Metadata: Meta()));
            Assert.True(closed.Accepted, closed.Error?.Message);
            Assert.Equal("succeeded", (await Terminal(closed.Response!.JobId)).State);
            return handle;
        }
        internal async Task<JobResponse> Open(string handle) => await Terminal((await Registry.StartOpenAsync("a", new(handle, Metadata: Meta()))).Response!.JobId);
        internal async Task<JobResponse> Terminal(string id)
        { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8)); while (true) { JobResponse job = Registry.ReadJob("a", id).Response!; if (job.State is "succeeded" or "failed" or "cancelled") return job; await Task.Delay(10, timeout.Token); } }
        public async ValueTask DisposeAsync() { Clock.BeforeRead = null; await Registry.DisposeAsync(); _directory.Dispose(); }
    }
    private sealed class Storage(string root) : IProjectSaveStorage
    {
        public IReadOnlyList<ProjectIssue> WriteProject(ProjectSaveSnapshot snapshot, string directoryPath)
        { List<ProjectIssue> issues = []; ProjectStore.Save(snapshot.Project, directoryPath, issues); return issues; }
        public string WriteBackup(ProjectSaveSnapshot snapshot) => AutoSaveStore.Save(snapshot.Project, AutoSaveStore.ResolveRoot(snapshot.DirectoryPath, root), snapshot.CapturedAtUtc);
        public void PublishRecovery(AutoSaveSession session) => AutoSaveSession.Write(root, session);
        public void ClearRecovery(AutoSaveSession session) { if (AutoSaveSession.Read(root) == session) AutoSaveSession.Clear(root); }
    }
}
