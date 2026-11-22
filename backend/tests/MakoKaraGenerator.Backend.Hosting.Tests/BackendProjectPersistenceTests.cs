using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendProjectPersistenceTests
{
    [Fact]
    public async Task 作者工作流配置后草稿状态可读且重复配置被拒绝()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        fixture.Registry.ConfigureAuthorWorkflows(null);
        BackendOperationResult<AuthorDraftResponse> missing = await fixture.Registry.ReadAuthorDraftAsync("client", "draft-" + new string('0', 32));
        Assert.Equal(BackendErrorCode.ResourceNotFound, missing.Error!.Code);
        Assert.Equal(BackendErrorCode.InvalidRequest, (await fixture.Registry.ReadAuthorDraftAsync("client", "draft-missing")).Error!.Code);
        Assert.Throws<InvalidOperationException>(() => fixture.Registry.ConfigureAuthorWorkflows(null));
    }

    [Fact]
    public async Task 保存捕获修订期间可继续编辑且撤销回真正保存实例变干净()
    {
        using Fixture fixture = await Fixture.CreateAsync(blockSave: true);
        string handle = await fixture.HandleAsync("saved.mkgproj");
        await fixture.EditAsync("0", "100000", "900000");
        JobResponse saving = (await fixture.Registry.StartSaveAsync("client", fixture.SessionId,
            new("1", handle, fixture.Meta()))).Response!;
        await fixture.Storage!.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("committing", fixture.Registry.ReadJob("client", saving.JobId).Response!.State);
        Assert.Equal(BackendErrorCode.OperationNotCancellable, fixture.Registry.CancelJob("client", saving.JobId).Error!.Code);
        Assert.True((await fixture.EditAsync("1", "200000", "1000000")).Accepted);
        fixture.Storage.Release.Set();
        JobResponse saved = await fixture.WaitAsync(saving);
        Assert.Equal("succeeded", saved.State); Assert.Equal("1", saved.SavedProjectRevision);
        Assert.Equal("2", saved.CurrentProjectRevision); Assert.True(saved.IsDirty); Assert.True(saved.Result!.HasPath);
        ProjectCommandExecutionResult undo = await fixture.Registry.ExecuteCommandAsync("client", fixture.SessionId,
            fixture.Command("2", new("history.undo")));
        Assert.True(undo.Accepted); Assert.Equal("3", undo.Response!.ProjectRevision); Assert.False(undo.Response.IsDirty);
        Assert.Equal("1", (await fixture.Registry.ReadSummaryAsync("client", fixture.SessionId))!.SavedProjectRevision);
        Assert.Equal(100000, ProjectStore.Load(Path.Combine(fixture.Root, "saved.mkgproj")).Project!.Lyrics.Lines[0].Time!.Value.StartUs);
    }

    [Fact]
    public async Task 保存真实闭包关闭搬移删除上传后重开仍保留完整音频与严格版本门禁()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        string target = Path.Combine(fixture.Root, "saved.mkgproj");
        string handle = await fixture.HandleAsync("saved.mkgproj");
        JobResponse saved = await fixture.WaitAsync((await fixture.Registry.StartSaveAsync("client", fixture.SessionId,
            new("0", handle, fixture.Meta()))).Response!);
        Assert.Equal("succeeded", saved.State); Assert.False(saved.IsDirty);
        string audio = ProjectStore.Load(target).Project!.AudioPath!;
        Assert.Equal(Fixture.AudioBytes, await File.ReadAllBytesAsync(audio));
        JobResponse closed = await fixture.WaitAsync((await fixture.Registry.StartCloseAsync("client", fixture.SessionId,
            new("0", Metadata: fixture.Meta()))).Response!);
        Assert.Equal("succeeded", closed.State); Assert.Null(await fixture.Registry.ReadSummaryAsync("client", fixture.SessionId));
        Directory.Delete(fixture.UploadRoot, true);
        string moved = Path.Combine(fixture.Root, "moved.mkgproj"); Directory.Move(target, moved);
        string movedHandle = await fixture.HandleAsync("moved.mkgproj");
        JobResponse opened = await fixture.WaitAsync((await fixture.Registry.StartOpenAsync("client", new(movedHandle, Metadata: fixture.Meta()))).Response!);
        Assert.Equal("succeeded", opened.State); Assert.False(opened.IsDirty); Assert.True(opened.Result!.HasPath);
        // Open pins the full closure. Removing the original media afterwards cannot break SaveAs.
        Directory.Delete(Path.Combine(moved, "data", "media"), true);
        string copyHandle = await fixture.HandleAsync("copy.mkgproj");
        JobResponse copied = await fixture.WaitAsync((await fixture.Registry.StartSaveAsync("client", opened.ProjectSessionId!,
            new("0", copyHandle, fixture.Meta()))).Response!);
        Assert.Equal("succeeded", copied.State);
        Assert.Equal(Fixture.AudioBytes, await File.ReadAllBytesAsync(ProjectStore.Load(Path.Combine(fixture.Root, "copy.mkgproj")).Project!.AudioPath!));
        await fixture.WaitAsync((await fixture.Registry.StartCloseAsync("client", opened.ProjectSessionId!,
            new("0", Metadata: fixture.Meta()))).Response!);
        JsonObject json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Root, "copy.mkgproj", "project.json")))!.AsObject();
        json["applicationVersion"] = "0.0.0";
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "copy.mkgproj", "project.json"), json.ToJsonString());
        JobResponse rejected = await fixture.WaitAsync((await fixture.Registry.StartOpenAsync("client", new(copyHandle, Metadata: fixture.Meta()))).Response!);
        Assert.Equal("failed", rejected.State); Assert.Equal(BackendErrorCode.ProjectLoadFailed, rejected.Error!.Code);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/../escape")]
    [InlineData("/absolute")]
    [InlineData("a\\..\\escape")]
    public async Task 根授权拒绝越界目录且句柄不能跨客户端(string relative)
    {
        using Fixture fixture = await Fixture.CreateAsync();
        BackendOperationResult<FileHandleResponse> rejected = await fixture.Registry.CreateFileHandleAsync("client", new("projects", relative, Metadata: fixture.Meta()));
        Assert.Equal(BackendErrorCode.PathNotAllowed, rejected.Error!.Code);
        string handle = await fixture.HandleAsync("valid.mkgproj");
        Assert.Equal(BackendErrorCode.ResourceUnauthorized, (await fixture.Registry.StartOpenAsync("other", new(handle, Metadata: fixture.Meta()))).Error!.Code);
    }

    [Fact]
    public async Task 两个写会话明确租约冲突只读允许且关闭后重新取得写租约()
    {
        using Fixture fixture = await Fixture.CreateAsync(); string handle = await fixture.HandleAsync("saved.mkgproj");
        await fixture.WaitAsync((await fixture.Registry.StartSaveAsync("client", fixture.SessionId, new("0", handle, fixture.Meta()))).Response!);
        JobResponse conflict = await fixture.WaitAsync((await fixture.Registry.StartOpenAsync("client", new(handle, Metadata: fixture.Meta()))).Response!);
        Assert.Equal(BackendErrorCode.WriteLeaseConflict, conflict.Error!.Code);
        JobResponse readOnly = await fixture.WaitAsync((await fixture.Registry.StartOpenAsync("client", new(handle, "read", fixture.Meta()))).Response!);
        Assert.Equal("succeeded", readOnly.State);
        Assert.Equal(BackendErrorCode.ReadOnlySession, (await fixture.Registry.StartSaveAsync("client", readOnly.ProjectSessionId!, new("0", Metadata: fixture.Meta()))).Error!.Code);
        await fixture.WaitAsync((await fixture.Registry.StartCloseAsync("client", fixture.SessionId, new("0", Metadata: fixture.Meta()))).Response!);
        JobResponse writable = await fixture.WaitAsync((await fixture.Registry.StartOpenAsync("client", new(handle, Metadata: fixture.Meta()))).Response!);
        Assert.Equal("succeeded", writable.State);
    }

    [Fact]
    public async Task 失败保存保留旧入口和旧租约关闭失败恢复写能力默认拒绝活动任务()
    {
        using Fixture fixture = await Fixture.CreateAsync(blockSave: true);
        string handle = await fixture.HandleAsync("saved.mkgproj");
        JobResponse initialSave = (await fixture.Registry.StartSaveAsync("client", fixture.SessionId, new("0", handle, fixture.Meta()))).Response!;
        fixture.Storage!.Release.Set(); await fixture.WaitAsync(initialSave);
        byte[] original = await File.ReadAllBytesAsync(Path.Combine(fixture.Root, "saved.mkgproj", "project.json"));
        await fixture.EditAsync("0", "100000", "900000"); fixture.Storage.Fail = true;
        JobResponse failure = await fixture.WaitAsync((await fixture.Registry.StartCloseAsync("client", fixture.SessionId,
            new("1", "save", Metadata: fixture.Meta()))).Response!);
        Assert.Equal("failed", failure.State); Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(fixture.Root, "saved.mkgproj", "project.json")));
        Assert.True((await fixture.EditAsync("1", "200000", "1000000")).Accepted);
        fixture.Storage.Release.Reset();
        JobResponse saving = (await fixture.Registry.StartSaveAsync("client", fixture.SessionId, new("2", Metadata: fixture.Meta()))).Response!;
        await Task.Delay(50);
        Assert.Equal(BackendErrorCode.ActiveJobs, (await fixture.Registry.StartCloseAsync("client", fixture.SessionId,
            new("2", "discard", Metadata: fixture.Meta()))).Error!.Code);
        JobResponse closing = (await fixture.Registry.StartCloseAsync("client", fixture.SessionId,
            new("2", "discard", ActiveJobs: "cancelAndWait", Metadata: fixture.Meta()))).Response!;
        Assert.Equal(BackendErrorCode.SessionClosing, (await fixture.EditAsync("2", "300000", "1100000")).Error!.Code);
        fixture.Storage.Release.Set();
        Assert.Equal("succeeded", (await fixture.WaitAsync(saving)).State);
        Assert.Equal("succeeded", (await fixture.WaitAsync(closing)).State);
    }

    [Fact]
    public async Task 停服务保留独立恢复闭包下一宿主恢复为脏且不覆盖原工程()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        string handle = await fixture.HandleAsync("saved.mkgproj");
        await fixture.WaitAsync((await fixture.Registry.StartSaveAsync("client", fixture.SessionId, new("0", handle, fixture.Meta()))).Response!);
        byte[] original = await File.ReadAllBytesAsync(Path.Combine(fixture.Root, "saved.mkgproj", "project.json"));
        await fixture.EditAsync("0", "100000", "900000");
        await fixture.Registry.ShutdownAsync();
        fixture.Restart();
        RecoveryCandidateResponse candidate = Assert.Single(fixture.Registry.ReadRecoveryCandidates("client"));
        JobResponse recovered = await fixture.WaitAsync((await fixture.Registry.StartRecoverAsync("client", new(candidate.RecoveryId, fixture.Meta()))).Response!);
        Assert.Equal("succeeded", recovered.State); Assert.True(recovered.IsDirty);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(fixture.Root, "saved.mkgproj", "project.json")));
        Assert.Empty(fixture.Registry.ReadRecoveryCandidates("client"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        internal static readonly byte[] AudioBytes = [82, 73, 70, 70, 1, 2, 3, 4, 87, 65, 86, 69];
        internal string Root => _directory.FilePath("projects");
        internal string UploadRoot => _directory.FilePath("uploads");
        private string State => _directory.FilePath("profile");
        internal ProjectSessionRegistry Registry { get; private set; } = null!;
        internal string SessionId { get; private set; } = null!;
        internal ControlledStorage? Storage { get; private set; }
        private string _host = "host-one";
        internal static async Task<Fixture> CreateAsync(bool blockSave = false)
        {
            Fixture fixture = new(); Directory.CreateDirectory(fixture.Root); fixture.CreateRegistry(blockSave);
            ResourceStore store = fixture._store!;
            StoredAsset audio = await store.UploadAsync("client", "song.wav", "audio/wav", new MemoryStream(AudioBytes));
            StoredAsset lyrics = await store.UploadAsync("client", "song.lrc", "text/plain", new MemoryStream(Encoding.UTF8.GetBytes("[00:00.00]hello\n")));
            ProjectSessionCreateResult created = await fixture.Registry.CreateAsync("client", new("saved", audio.AssetId, lyrics.AssetId));
            Assert.True(created.Accepted); fixture.SessionId = created.Session!.ProjectSessionId; return fixture;
        }
        private ResourceStore? _store;
        private void CreateRegistry(bool blockSave)
        {
            _store = new(new() { ResourceRoot = UploadRoot });
            Registry = new(_host, _store, new((_, _) => Task.FromResult<LoadedWaveform?>(Sample())));
            Registry.ConfigurePersistence(new() { StateRoot = State, AllowedProjectRoots = new Dictionary<string, string> { ["projects"] = Root } },
                blockSave ? root => Storage = new(root) : null, (_, _) => Task.FromResult<LoadedWaveform?>(Sample()));
        }
        internal void Restart() { _host = "host-two"; CreateRegistry(false); }
        internal RequestMetadataDto Meta() => new(Guid.NewGuid().ToString("N"), _host, DateTimeOffset.UtcNow.AddMinutes(5));
        internal ProjectCommandRequest Command(string revision, ProjectCommandDto command)
        { RequestMetadataDto metadata = Meta(); return new(revision, command, metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc); }
        internal async Task<ProjectCommandExecutionResult> EditAsync(string revision, string start, string end)
        {
            LyricLineDto line = Assert.Single((await Registry.ReadLyricsAsync("client", SessionId, "primary"))!.Lines);
            return await Registry.ExecuteCommandAsync("client", SessionId, Command(revision, new("lyrics.setTimeRange", new("primary", line.Id), start, end)));
        }
        internal async Task<string> HandleAsync(string relative) => (await Registry.CreateFileHandleAsync("client", new("projects", relative, Metadata: Meta()))).Response!.HandleId;
        internal async Task<JobResponse> WaitAsync(JobResponse accepted)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
            while (true)
            {
                JobResponse job = Registry.ReadJob("client", accepted.JobId).Response!;
                if (job.State is "succeeded" or "failed" or "cancelled") return job;
                await Task.Delay(10, timeout.Token);
            }
        }
        public void Dispose() { Storage?.Release.Set(); Registry.Dispose(); Storage?.Dispose(); _directory.Dispose(); }
        private static LoadedWaveform Sample()
        { WaveformPeakBuilder builder = new(); builder.Append(new float[24000]); return new(new(3000000, 8000, 1, "pcm", "wav"), builder.Build(8000)); }
    }
    internal sealed class ControlledStorage(string root) : IProjectSaveStorage, IDisposable
    {
        internal readonly ManualResetEventSlim Release = new();
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Fail;
        public IReadOnlyList<ProjectIssue> WriteProject(ProjectSaveSnapshot snapshot, string directoryPath)
        {
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(8))) throw new IOException("受控保存未释放。");
            if (Fail) { Fail = false; throw new IOException("注入旧入口发布前失败。"); }
            List<ProjectIssue> issues = []; ProjectStore.Save(snapshot.Project, directoryPath, issues); return issues;
        }
        public string WriteBackup(ProjectSaveSnapshot snapshot) => AutoSaveStore.Save(snapshot.Project, AutoSaveStore.ResolveRoot(snapshot.DirectoryPath, root), snapshot.CapturedAtUtc);
        public void PublishRecovery(AutoSaveSession session) => AutoSaveSession.Write(root, session);
        public void ClearRecovery(AutoSaveSession expected) { if (AutoSaveSession.Read(root) == expected) AutoSaveSession.Clear(root); }
        public void Dispose() => Release.Dispose();
    }
}
