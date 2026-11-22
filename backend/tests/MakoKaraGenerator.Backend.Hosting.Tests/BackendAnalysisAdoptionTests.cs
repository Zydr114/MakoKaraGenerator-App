using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed partial class BackendAnalysisAdoptionTests
{
    [Fact]
    public async Task 新建固定音频身份且采用允许歌词编辑后明确提交一次历史并发布摘要()
    {
        await using Fixture f = new(); ProjectSession project = await f.Project(); ProjectWorkspace workspace = Workspace(project);
        StoredAsset asset = f.Resources.GetAuthorized("a", project.AudioAssetId);
        Assert.Equal(new MediaFileRevision(asset.Sha256, asset.Size, asset.FileName), workspace.Project.AudioMedia);
        Assert.Equal(asset.StoragePath, workspace.Project.AudioPath);
        var created = (await f.Registry.ReadSummaryAsync("a", project.ProjectSessionId))!;
        Assert.Equal("0", created.ProjectRevision); Assert.True(created.IsDirty); Assert.False(created.HasPath); Assert.Null(created.AudioAnalysis);
        JobResponse job = await f.Complete(project);
        Assert.Null((await f.Registry.ReadSummaryAsync("a", project.ProjectSessionId))!.AudioAnalysis);
        LyricsLinesResponse lines = (await f.Registry.ReadLyricsAsync("a", project.ProjectSessionId, "primary"))!;
        LyricLineDto line = lines.Lines[0]; LyricTokenDto token = line.Tokens[0];
        Assert.True((await f.Command(project, "0", new("lyrics.setTimeRange", new("primary", line.Id, token.Id), "1000", "1000000"))).Accepted);
        var applied = await f.Command(project, "1", new("audio.analysis.apply", AnalysisJobId: job.JobId));
        Assert.True(applied.Accepted, applied.Error?.Message); Assert.True(applied.Response!.Changed); Assert.Equal("2", applied.Response.ProjectRevision);
        Assert.Equal("audio.analysis", applied.Response.UndoName); Assert.True(applied.Response.ChangeSet.RequiresFullRefresh);
        Assert.Equal(["project"], applied.Response.ChangeSet.Areas); Assert.Equal(2, workspace.History.UndoCount);
        var summary = (await f.Registry.ReadSummaryAsync("a", project.ProjectSessionId))!;
        Assert.Equal(job.AnalysisResult!.ContentHash, summary.AudioAnalysis!.ContentHash);
        Assert.Equal("available", summary.AudioAnalysis.Status); Assert.Equal("384", summary.AudioAnalysis.SampleCount);
        Assert.Equal("8000", summary.AudioAnalysis.DurationUs); Assert.Equal("0", summary.AudioAnalysis.OriginUs);
        Assert.Equal(1, summary.AudioAnalysis.PointCount); Assert.Equal(job.AnalysisResult.SourceHash, summary.AudioAnalysis.SourceHash);
        BackendEventResponse changed = f.Registry.ReadEvents("a", created.EventCursor).Events.Last();
        Assert.Equal("project.changed", changed.Type); Assert.Equal(summary.AudioAnalysis, changed.Summary!.AudioAnalysis);
        Assert.Equal(Canonical(asset.Sha256), AudioAnalysisJson.Encode(workspace.Project.AudioAnalysis!.Data!));
    }

    [Fact]
    public async Task 采用清除撤销重做与两类无变化保留完整历史及事件且回执先于修订和文件读取()
    {
        await using Fixture f = new(); ProjectSession project = await f.Project(); JobResponse job = await f.Complete(project);
        ProjectCommandRequest apply = f.Request("0", new("audio.analysis.apply", AnalysisJobId: job.JobId));
        ProjectCommandExecutionResult first = await f.Registry.ExecuteCommandAsync("a", project.ProjectSessionId, apply);
        Assert.True(first.Accepted); Assert.True((await f.Command(project, "1", new("audio.analysis.clear"))).Accepted);
        Assert.True((await f.Command(project, "2", new("history.undo"))).Accepted);
        ProjectWorkspace workspace = Workspace(project); Project adopted = workspace.Project;
        ProjectSessionSummaryResponse before = (await f.Registry.ReadSummaryAsync("a", project.ProjectSessionId))!;
        var same = await f.Command(project, "3", new("audio.analysis.apply", AnalysisJobId: job.JobId));
        Assert.True(same.Accepted); Assert.False(same.Response!.Changed); Assert.Same(adopted, workspace.Project);
        Assert.Equal(before.ProjectRevision, same.Response.ProjectRevision); Assert.Equal(before.EventCursor, same.Response.EventCursor);
        Assert.True(same.Response.CanRedo); Assert.True((await f.Command(project, "3", new("history.redo"))).Accepted);
        Assert.Null((await f.Registry.ReadSummaryAsync("a", project.ProjectSessionId))!.AudioAnalysis);
        Assert.True((await f.Command(project, "4", new("history.undo"))).Accepted);
        Assert.True((await f.Command(project, "5", new("history.undo"))).Accepted);
        before = (await f.Registry.ReadSummaryAsync("a", project.ProjectSessionId))!;
        var empty = await f.Command(project, "6", new("audio.analysis.clear"));
        Assert.True(empty.Accepted); Assert.False(empty.Response!.Changed); Assert.True(empty.Response.CanRedo);
        Assert.Equal(before.EventCursor, empty.Response.EventCursor); Assert.Equal(before.ProjectRevision, empty.Response.ProjectRevision);
        string audio = f.Resources.GetAuthorized("a", project.AudioAssetId).StoragePath; File.Delete(audio);
        ProjectCommandExecutionResult repeated = await f.Registry.ExecuteCommandAsync("a", project.ProjectSessionId, apply);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(first.Response), System.Text.Json.JsonSerializer.Serialize(repeated.Response));
        Assert.Equal("6", (await f.Registry.ReadSummaryAsync("a", project.ProjectSessionId))!.ProjectRevision);
        var reuse = await f.Registry.ExecuteCommandAsync("a", project.ProjectSessionId, apply with { Command = new("audio.analysis.clear") });
        Assert.Equal(BackendErrorCode.RequestIdReuse, reuse.Error!.Code);
    }

    [Theory]
    [InlineData("audio.analysis.apply", null, false)]
    [InlineData("audio.analysis.apply", "", false)]
    [InlineData("audio.analysis.apply", "job", true)]
    [InlineData("audio.analysis.clear", "job", false)]
    [InlineData("audio.analysis.clear", null, true)]
    [InlineData("history.undo", "job", false)]
    [InlineData("history.redo", "job", false)]
    [InlineData("lyrics.setTimeRange", "job", true)]
    public async Task 不相关或缺失字段在读取结果前拒绝且历史不变(string type, string? job, bool timing)
    {
        await using Fixture f = new(); ProjectSession project = await f.Project();
        var command = new ProjectCommandDto(type, timing ? new("primary", "line") : null,
            timing ? "0" : null, timing ? "1" : null, job);
        Project before = Workspace(project).Project;
        Assert.Equal(BackendErrorCode.InvalidRequest, (await f.Command(project, "0", command)).Error!.Code);
        Assert.Same(before, Workspace(project).Project); Assert.Equal(0, Workspace(project).History.UndoCount);
        Assert.Equal("0", (await f.Registry.ReadSummaryAsync("a", project.ProjectSessionId))!.ProjectRevision);
    }

    [Fact]
    public async Task 采用拒绝未成功非分析其他工程其他客户端只读旧修订和过期任务()
    {
        await using Fixture f = new(); ProjectSession project = await f.Project(); ProjectSession other = await f.Project();
        JobResponse running = await f.Start(project); Call call = await f.Executor.Next();
        Assert.Equal(BackendErrorCode.CommandRejected, (await f.Command(project, "0", new("audio.analysis.apply", AnalysisJobId: running.JobId))).Error!.Code);
        call.Release.SetResult(Canonical(call.Grant.Sha256)); JobResponse done = await f.Terminal(running.JobId);
        Assert.Equal(BackendErrorCode.SessionNotFound, (await f.Registry.ExecuteCommandAsync("b", project.ProjectSessionId,
            f.Request("0", new("audio.analysis.apply", AnalysisJobId: done.JobId)))).Error!.Code);
        ProjectSession foreignProject = await f.Project("b");
        Assert.Equal(BackendErrorCode.JobNotFound, (await f.Command(foreignProject, "0", new("audio.analysis.apply", AnalysisJobId: done.JobId), "b")).Error!.Code);
        Assert.Equal(BackendErrorCode.CommandRejected, (await f.Command(other, "0", new("audio.analysis.apply", AnalysisJobId: done.JobId))).Error!.Code);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await f.Command(project, "1", new("audio.analysis.apply", AnalysisJobId: done.JobId))).Error!.Code);
        project.IsReadOnly = true;
        Assert.Equal(BackendErrorCode.ReadOnlySession, (await f.Command(project, "0", new("audio.analysis.apply", AnalysisJobId: done.JobId))).Error!.Code);
        Assert.Equal(BackendErrorCode.ReadOnlySession, (await f.Command(project, "0", new("audio.analysis.clear"))).Error!.Code);
        project.IsReadOnly = false;
        string handle = await f.Handle("saved"); JobResponse save = await f.Terminal((await f.Registry.StartSaveAsync("a", project.ProjectSessionId, new("0", handle, f.Meta()))).Response!.JobId);
        Assert.Equal(BackendErrorCode.CommandRejected, (await f.Command(project, "0", new("audio.analysis.apply", AnalysisJobId: save.JobId))).Error!.Code);
        f.Clock.Advance(3601);
        Assert.Equal(BackendErrorCode.JobNotFound, (await f.Command(project, "0", new("audio.analysis.apply", AnalysisJobId: done.JobId))).Error!.Code);
        Assert.Null(Workspace(project).Project.AudioAnalysis); Assert.Equal(0, Workspace(project).History.UndoCount);
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("size")]
    [InlineData("missing")]
    [InlineData("link")]
    [InlineData("result")]
    public async Task 已登记音频或结果文件损坏拒绝采用且不污染历史(string damage)
    {
        if (damage == "link" && OperatingSystem.IsWindows()) return;
        await using Fixture f = new(); ProjectSession project = await f.Project(); JobResponse job = await f.Complete(project);
        string path = f.Resources.GetAuthorized("a", project.AudioAssetId).StoragePath;
        byte[] original = await File.ReadAllBytesAsync(path);
        if (damage == "bytes") { byte[] bytes = [.. original]; bytes[^1] ^= 1; await File.WriteAllBytesAsync(path, bytes); }
        if (damage == "size") await File.WriteAllBytesAsync(path, [.. original, 0]);
        if (damage == "missing") File.Delete(path);
        if (damage == "link") { File.Delete(path); string target = path + ".target"; await File.WriteAllBytesAsync(target, original); File.CreateSymbolicLink(path, target); }
        if (damage == "result") await File.WriteAllBytesAsync(Path.Combine(f.State, "analysis-results", job.AnalysisResult!.Report.ReportId + ".json"), new byte[job.AnalysisResult.Report.SizeBytes]);
        Project before = Workspace(project).Project; var summary = (await f.Registry.ReadSummaryAsync("a", project.ProjectSessionId))!;
        Assert.Equal(BackendErrorCode.TaskExecutionFailed, (await f.Command(project, "0", new("audio.analysis.apply", AnalysisJobId: job.JobId))).Error!.Code);
        Assert.Same(before, Workspace(project).Project); Assert.Equal(0, Workspace(project).History.UndoCount);
        Assert.Equal(summary, await f.Registry.ReadSummaryAsync("a", project.ProjectSessionId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 准备期间到期与最后读者真实删除失败均在工程变化前拒绝(bool deleteFailure)
    {
        await using Fixture f = new(); ProjectSession project = await f.Project(); JobResponse job = await f.Complete(project);
        Project before = Workspace(project).Project;
        ProjectSessionSummaryResponse summary = (await f.Registry.ReadSummaryAsync("a", project.ProjectSessionId))!;
        string root = Path.Combine(f.State, "analysis-results"); string report = Path.Combine(root, job.AnalysisResult!.Report.ReportId + ".json");
        BackendJobSidecarStore? files = null; bool observed = false;
        Action<string> release = id =>
        {
            Assert.Equal(job.AnalysisResult.Report.ReportId, id); observed = true;
            if (!deleteFailure) { f.Clock.Advance(3601); return; }
            files!.Prune(Array.Empty<string>());
            if (OperatingSystem.IsWindows()) File.SetAttributes(report, FileAttributes.ReadOnly);
            else File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        };
        files = new BackendJobSidecarStore(root, "analysis-", "AnalysisResult", AnalysisResultCodec.MaximumResultBytes, 64L * 1024 * 1024, null, release);
        object results = typeof(ProjectSessionRegistry).GetField("_analysisResults", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Registry)!;
        results.GetType().GetField("_files", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(results, files);
        try
        {
            var result = await f.Command(project, "0", new("audio.analysis.apply", AnalysisJobId: job.JobId));
            Assert.True(observed); Assert.Equal(deleteFailure ? BackendErrorCode.TaskExecutionFailed : BackendErrorCode.JobNotFound, result.Error!.Code);
            Assert.Same(before, Workspace(project).Project); Assert.Equal(0, Workspace(project).History.UndoCount);
            Assert.Equal(summary, await f.Registry.ReadSummaryAsync("a", project.ProjectSessionId));
            if (deleteFailure)
            {
                Assert.True(File.Exists(report));
                Assert.IsAssignableFrom<UnauthorizedAccessException>(Record.Exception(() => File.Delete(report)));
                object entry = ((System.Collections.IDictionary)typeof(BackendJobSidecarStore).GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(files)!)[job.AnalysisResult.Report.ReportId]!;
                Assert.Equal(0, entry.GetType().GetField("Readers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(entry));
                Assert.True((bool)entry.GetType().GetField("Expired", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(entry)!);
            }
        }
        finally
        {
            if (OperatingSystem.IsWindows()) { if (File.Exists(report)) File.SetAttributes(report, FileAttributes.Normal); }
            else File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 当前音频声明不同与登记字节损坏分开拒绝而失败摘要不泄露诊断(bool noStream)
    {
        await using Fixture f = new(); ProjectSession project = await f.Project(); JobResponse job = await f.Complete(project); ProjectWorkspace workspace = Workspace(project);
        Project original = workspace.Project;
        workspace.Replace(original with { AudioMedia = new(new string('c', 64), original.AudioMedia!.ByteLength, "other.wav") });
        Assert.Equal(BackendErrorCode.CommandRejected, (await f.Command(project, "0", new("audio.analysis.apply", AnalysisJobId: job.JobId))).Error!.Code);
        workspace.Replace(original with { AudioPath = "/other/audio.wav" });
        Assert.Equal(BackendErrorCode.CommandRejected, (await f.Command(project, "0", new("audio.analysis.apply", AnalysisJobId: job.JobId))).Error!.Code);
        workspace.Replace(original with { AudioAnalysis = noStream ? ProjectAudioAnalysis.NoStream("failure", "secret path /private/audio.wav")
            : ProjectAudioAnalysis.Failed("failure", "secret path /private/audio.wav") });
        string status = noStream ? "noStream" : "failed";
        Assert.Equal(new ProjectAudioAnalysisSummaryDto(status), (await f.Registry.ReadSummaryAsync("a", project.ProjectSessionId))!.AudioAnalysis);
        Assert.True((await f.Command(project, "0", new("audio.analysis.clear"))).Response!.Changed);
        Assert.True((await f.Command(project, "1", new("history.undo"))).Accepted);
        Assert.Equal(status, (await f.Registry.ReadSummaryAsync("a", project.ProjectSessionId))!.AudioAnalysis!.Status);
    }

    [Fact]
    public async Task 保存重开保持完整规范分析且原任务过期或重启不影响工程数据并不得恢复采用()
    {
        await using Fixture f = new(); ProjectSession project = await f.Project(); JobResponse job = await f.Complete(project);
        Assert.True((await f.Command(project, "0", new("audio.analysis.apply", AnalysisJobId: job.JobId))).Accepted);
        string handle = await f.Handle("adopted");
        JobResponse saved = await f.Terminal((await f.Registry.StartSaveAsync("a", project.ProjectSessionId, new("1", handle, f.Meta()))).Response!.JobId);
        Assert.Equal("succeeded", saved.State); Assert.False(saved.IsDirty);
        byte[] bytes = AudioAnalysisJson.Encode(Workspace(project).Project.AudioAnalysis!.Data!);
        Assert.Equal(bytes, AudioAnalysisJson.Encode(ProjectStore.Load(Path.Combine(f.Root, "adopted")).Project!.AudioAnalysis!.Data!));
        Assert.Equal("succeeded", (await f.Terminal((await f.Registry.StartCloseAsync("a", project.ProjectSessionId, new("1", ActiveJobs: "wait", Metadata: f.Meta()))).Response!.JobId)).State);
        JobResponse opened = await f.Terminal((await f.Registry.StartOpenAsync("a", new(handle, Metadata: f.Meta()))).Response!.JobId);
        Assert.Equal("succeeded", opened.State); Assert.Equal(job.AnalysisResult!.ContentHash, opened.Result!.AudioAnalysis!.ContentHash);
        Assert.True(f.Registry.TryGet("a", opened.ProjectSessionId!, out ProjectSession reopened));
        Assert.Equal(bytes, AudioAnalysisJson.Encode(Workspace(reopened).Project.AudioAnalysis!.Data!));
        Assert.Equal(BackendErrorCode.CommandRejected, (await f.Command(reopened, "0", new("audio.analysis.apply", AnalysisJobId: job.JobId))).Error!.Code);
        await f.Restart(); ProjectSession current = await f.Project();
        using (BackendAnalysisReadLease read = f.Registry.AcquireAnalysisRead("a", job.JobId, recovered: true).Response!)
        { using MemoryStream content = new(); await read.Stream.CopyToAsync(content); Assert.Equal(bytes, content.ToArray()); }
        Assert.Equal(BackendErrorCode.JobNotFound, (await f.Command(current, "0", new("audio.analysis.apply", AnalysisJobId: job.JobId))).Error!.Code);
    }

    private static ProjectWorkspace Workspace(ProjectSession project) => project.Workspace;
    private static byte[] Canonical(string source) => AudioAnalysisJson.Encode(new(source, new string('b', 64), 384, [new(0, .1234567890123456, .5, .25, .8)]));
    private sealed record Call(BackendAudioAnalysisGrant Grant, TaskCompletionSource<byte[]> Release);
    private sealed class Executor : IBackendAnalysisExecutor
    {
        private readonly Channel<Call> _calls = Channel.CreateUnbounded<Call>();
        internal Task<Call> Next() => _calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        public void ValidateInput(BackendAudioAnalysisGrant grant) { }
        public Task<BackendAnalysisCapabilities> ProbeAsync(CancellationToken cancellationToken = default) => Task.FromResult(new BackendAnalysisCapabilities(true, true));
        public async Task<byte[]> ExecuteAsync(BackendAudioAnalysisGrant grant, IProgress<BackendAnalysisProgressDto>? progress = null, CancellationToken cancellationToken = default)
        { TaskCompletionSource<byte[]> ready = new(TaskCreationOptions.RunContinuationsAsynchronously); _calls.Writer.TryWrite(new(grant, ready)); return await ready.Task.WaitAsync(cancellationToken); }
    }
    private sealed class ClockTime : TimeProvider
    { private DateTimeOffset _now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => _now; internal void Advance(int seconds) => _now = _now.AddSeconds(seconds); }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory = new(); internal string State => _directory.FilePath("state"); internal string Root => _directory.FilePath("projects");
        internal readonly Executor Executor = new(); internal readonly ClockTime Clock = new(); internal ProjectSessionRegistry Registry = null!; internal ResourceStore Resources = null!;
        private string _host = "adoption-host-one"; private readonly bool _compressed;
        internal Fixture(bool compressed = false) { _compressed = compressed; Directory.CreateDirectory(Root); CreateRegistry(); }
        private void CreateRegistry()
        {
            Resources = new(new() { ResourceRoot = _directory.FilePath("uploads") }, Clock); WaveformPeakBuilder peaks = new(); peaks.Append(new float[24000]);
            LoadedWaveform sample = new(new(3000000, 8000, 1, "pcm", "wav"), peaks.Build(8000)); Registry = new(_host, Resources, new((_, _) => Task.FromResult<LoadedWaveform?>(sample)), Clock);
            // Compressed contract fixtures are header candidates; actual strict decoding is verified by Worker tests.
            Registry.ConfigurePersistence(new() { StateRoot = State, AllowedProjectRoots = new Dictionary<string, string> { ["projects"] = Root } },
                loadAudio: _compressed ? (_, _) => Task.FromResult<LoadedWaveform?>(sample) : null);
            Registry.ConfigureAnalysisTasks(Executor, _compressed ? new(true, true, AudioProfiles: [BackendAudioInputProfiles.Pcm16Wave, BackendAudioInputProfiles.Mp3, BackendAudioInputProfiles.Flac,
                BackendAudioInputProfiles.Aac, BackendAudioInputProfiles.Mov, BackendAudioInputProfiles.Ogg], SourceProfiles: [BackendAudioInputProfiles.LegacyAnalysisSource, BackendAudioInputProfiles.CompressedAnalysisSource]) : new(true, true));
        }
        internal RequestMetadataDto Meta() => new(Guid.NewGuid().ToString("N"), _host, Clock.GetUtcNow().AddMinutes(1));
        internal ProjectCommandRequest Request(string revision, ProjectCommandDto command)
        { var metadata = Meta(); return new(revision, command, metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc); }
        internal Task<ProjectCommandExecutionResult> Command(ProjectSession project, string revision, ProjectCommandDto command, string client = "a") => Registry.ExecuteCommandAsync(client, project.ProjectSessionId, Request(revision, command));
        internal async Task<ProjectSession> Project(string client = "a", bool timed = false, byte[]? audioBytes = null)
        {
            StoredAsset audio = await Resources.UploadAsync(client, "song.wav", "audio/wav", new MemoryStream(audioBytes ?? Wave()));
            StoredAsset lyrics = await Resources.UploadAsync(client, "song.lrc", "text/plain", new MemoryStream(timed
                ? "[00:00.00]<00:00.00>hello<00:03.00>\n"u8.ToArray() : "[00:00.00]hello\n"u8.ToArray()));
            var result = await Registry.CreateAsync(client, new("adoption", audio.AssetId, lyrics.AssetId, new(640, 360, OutputDurationUs: 3000000), Metadata: Meta()));
            Assert.True(result.Accepted, result.Error?.Message); return result.Session!;
        }
        internal async Task<JobResponse> Start(ProjectSession project) => (await Registry.StartAnalysisAsync("a", project.ProjectSessionId, new("0", Meta()))).Response!;
        internal async Task<JobResponse> Complete(ProjectSession project)
        { JobResponse job = await Start(project); Call call = await Executor.Next(); call.Release.SetResult(Canonical(call.Grant.Sha256)); return await Terminal(job.JobId); }
        internal async Task<JobResponse> Terminal(string id)
        { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8)); while (true) { JobResponse job = Registry.ReadJob("a", id).Response!; if (job.State is "succeeded" or "failed" or "cancelled" or "unknown") return job; await Task.Delay(10, timeout.Token); } }
        internal async Task<string> Handle(string path) => (await Registry.CreateFileHandleAsync("a", new("projects", path, Metadata: Meta()))).Response!.HandleId;
        internal async Task Restart() { await Registry.DisposeAsync(); _host = "adoption-host-two"; CreateRegistry(); }
        public async ValueTask DisposeAsync() { await Registry.DisposeAsync(); _directory.Dispose(); }
        private static byte[] Wave()
        {
            using MemoryStream stream = new(); using BinaryWriter writer = new(stream);
            writer.Write("RIFF"u8); writer.Write(164); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(128); writer.Write(new byte[128]); return stream.ToArray();
        }
    }
}
