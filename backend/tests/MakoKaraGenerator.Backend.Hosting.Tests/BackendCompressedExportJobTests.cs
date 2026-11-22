using System.Reflection;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed partial class BackendExportJobTests
{
    private static BackendExportCapabilities AllExportProfiles() => new(true, true, true,
        AudioProfiles: [BackendAudioInputProfiles.Pcm16Wave, BackendAudioInputProfiles.Mp3, BackendAudioInputProfiles.Flac,
            BackendAudioInputProfiles.Aac, BackendAudioInputProfiles.Mov, BackendAudioInputProfiles.Ogg],
        ExecutionProfiles: [WorkerExportContract.ExecutionProfile, WorkerExportContract.CompressedExecutionProfile]);

    [Theory]
    [InlineData(BackendAudioInputProfiles.Mp3)]
    [InlineData(BackendAudioInputProfiles.Flac)]
    public async Task 压缩候选由固定bytes授格式不凭准备时长且接受后工程变更不改捕获来源(string profile)
    {
        await using Fixture f = await Fixture.Create(capabilities: AllExportProfiles());
        string id = await f.Project("a", audioBytes: Candidate(profile));
        Assert.True(f.Registry.TryGet("a", id, out ProjectSession session));
        ProjectWorkspace workspace = ExportWorkspace(session); ProjectSnapshot snapshot = ProjectSnapshot.FromProject(workspace.Project);
        string expectedSha = WorkerProtocol.ComputeSha256(StaticProjectSnapshotCodec.Encode(snapshot));
        JobResponse job = await f.Start(id); Call call = await f.Exporter.Next();
        Assert.Equal(profile, call.Input.Audio!.AudioProfile); Assert.Equal(profile, job.ExportInput!.Audio!.AudioProfile);
        Assert.Equal(WorkerExportContract.CompressedExecutionProfile, job.ExportInput.ExecutionProfile);
        Assert.Equal(expectedSha, job.Input!.SourceSha256); Assert.Equal(snapshot, call.Snapshot);
        Assert.Equal(6, f.Registry.ExportCapabilities.AudioProfiles.Length); Assert.Equal(2, f.Registry.ExportCapabilities.ExecutionProfiles!.Length);
        Assert.Null(f.Registry.ExportCapabilities.MaxCompressedAudioDurationUs);
        Assert.Equal(WorkerExportContract.CompressedAudioDurationBasis, f.Registry.ExportCapabilities.CompressedAudioDurationBasis);
        workspace.Replace(workspace.Project with { AudioPath = "/untrusted/edited", Name = "after acceptance" });
        call.Release.TrySetResult(); Assert.Equal("succeeded", (await f.Terminal("a", job.JobId)).State);
        Assert.Equal(snapshot.AudioPath, call.Snapshot.AudioPath); Assert.NotEqual(workspace.Project.AudioPath, call.Input.Audio.Path);
        byte[] original = f.Registry.ReadExportResult("a", job.JobId).Response!;
        await f.Restart();
        Assert.Equal(original, f.Registry.ReadExportResult("new-bootstrap", job.JobId, true).Response); Assert.Equal(1, f.Exporter.Calls);
    }

    [Fact]
    public async Task 旧受信接缝缺字段仅WAV不借默认值开放压缩且原WAV仍可成功()
    {
        await using Fixture f = await Fixture.Create(); string compressed = await f.Project("a", audioBytes: Candidate(BackendAudioInputProfiles.Mp3));
        Assert.Equal([BackendAudioInputProfiles.Pcm16Wave], f.Registry.ExportCapabilities.AudioProfiles);
        Assert.Equal([WorkerExportContract.ExecutionProfile], f.Registry.ExportCapabilities.ExecutionProfiles!);
        Assert.Equal(BackendErrorCode.TaskUnavailable, (await f.Registry.StartExportAsync("a", compressed, new("0", Metadata: f.Meta()))).Error!.Code);
        Assert.Equal(0, f.Exporter.Calls); Assert.Empty(Directory.EnumerateFiles(Path.Combine(f.State, "jobs")));
        string wave = await f.Project("a"); JobResponse job = await f.Start(wave); Call call = await f.Exporter.Next();
        Assert.Equal(BackendAudioInputProfiles.Pcm16Wave, call.Input.Audio!.AudioProfile); Assert.Null(job.ExportInput!.Audio!.AudioProfile);
        Assert.Null(job.ExportInput.ExecutionProfile);
        call.Release.TrySetResult(); Assert.Equal("succeeded", (await f.Terminal("a", job.JobId)).State);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missingExecution")]
    public async Task 声明格式未知重复或与执行profile错配禁用不虚报能力(string mode)
    {
        var caps = mode switch
        {
            "unknown" => AllExportProfiles() with { AudioProfiles = ["future"] },
            "duplicate" => AllExportProfiles() with { AudioProfiles = [BackendAudioInputProfiles.Mp3, BackendAudioInputProfiles.Mp3] },
            _ => AllExportProfiles() with { ExecutionProfiles = [WorkerExportContract.ExecutionProfile] },
        };
        await using Fixture f = await Fixture.Create(capabilities: caps);
        Assert.False(f.Registry.ExportCapabilities.Enabled); Assert.Empty(f.Registry.ExportCapabilities.AudioProfiles); Assert.Empty(f.Registry.ExportCapabilities.ExecutionProfiles!);
        string id = await f.Project("a");
        Assert.Equal(BackendErrorCode.TaskUnavailable, (await f.Registry.StartExportAsync("a", id, new("0", Metadata: f.Meta()))).Error!.Code);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task 固定压缩源实际字节篡改在接受前或执行排水后拒绝且无媒体或假失败报告(bool afterAccepted, bool runtimeFailure)
    {
        await using Fixture f = await Fixture.Create(capabilities: AllExportProfiles());
        string id = await f.Project("a", audioBytes: Candidate(BackendAudioInputProfiles.Mp3));
        Assert.True(f.Registry.TryGet("a", id, out ProjectSession session)); StoredAsset asset = f.Resources.GetAuthorized("a", session.AudioAssetId);
        JobResponse? job = null; Call? call = null;
        if (runtimeFailure) f.Exporter.Failure = new("ActualRuntimeFailure", "actual primary", RuntimeDiagnosticSnapshot.FromArray([
            new(new("actual-instance"), "actual-artifact", "render", 0, new(DiagnosticSeverity.Error, "ActualError", "actual"))]));
        if (afterAccepted) { job = await f.Start(id); call = await f.Exporter.Next(); }
        byte[] damaged = await File.ReadAllBytesAsync(asset.StoragePath); damaged[^1] ^= 1; await File.WriteAllBytesAsync(asset.StoragePath, damaged);
        if (afterAccepted)
        {
            call!.Release.TrySetResult(); JobResponse terminal = await f.Terminal("a", job!.JobId);
            Assert.Equal("failed", terminal.State); Assert.Null(terminal.ExportResult); Assert.Null(terminal.FailureReportRef); Assert.Equal(1, f.Exporter.Drained);
            if (runtimeFailure) { Assert.Equal("ActualRuntimeFailure", terminal.FailureCode); Assert.True(f.Exporter.Failure!.Data.Contains("ExportFailureReportFailure")); }
        }
        else
        {
            Assert.False((await f.Registry.StartExportAsync("a", id, new("0", Metadata: f.Meta()))).Accepted);
            Assert.Equal(0, f.Exporter.Calls); Assert.Empty(Directory.EnumerateFiles(Path.Combine(f.State, "jobs")));
        }
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.State, "export-artifacts"), "artifact-*"));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.State, "export-artifacts", ".staging")));
    }

    [Fact]
    public async Task 纯视频不读取丢失压缩源也不授音频能力()
    {
        await using Fixture f = await Fixture.Create(capabilities: AllExportProfiles());
        string id = await f.Project("a", audioBytes: Candidate(BackendAudioInputProfiles.Flac));
        Assert.True(f.Registry.TryGet("a", id, out ProjectSession session)); File.Delete(f.Resources.GetAuthorized("a", session.AudioAssetId).StoragePath);
        JobResponse job = (await f.Registry.StartExportAsync("a", id, new("0", IncludeAudio: false, Metadata: f.Meta()))).Response!;
        Call call = await f.Exporter.Next(); Assert.Null(call.Input.Audio); Assert.Null(job.ExportInput!.Audio); Assert.NotNull(call.Snapshot.AudioMedia);
        call.Release.TrySetResult(); Assert.Equal("succeeded", (await f.Terminal("a", job.JobId)).State);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("pureVideoAudio")]
    [InlineData("missingAudio")]
    [InlineData("hash")]
    [InlineData("size")]
    [InlineData("missingExecution")]
    [InlineData("unknownExecution")]
    [InlineData("missingAudioProfile")]
    [InlineData("waveWithCompressedExecution")]
    public async Task 旧新恢复只读明确音频metadata白名单未知与非法组合拒绝(string mode)
    {
        await using Fixture f = await Fixture.Create(capabilities: AllExportProfiles());
        string id = await f.Project("a", audioBytes: Candidate(BackendAudioInputProfiles.Mp3));
        JobResponse job = await f.Start(id); (await f.Exporter.Next()).Release.TrySetResult(); await f.Terminal("a", job.JobId);
        string path = Path.Combine(f.State, "jobs", job.JobId + ".json");
        JsonNode ledger = JsonNode.Parse(await File.ReadAllTextAsync(path))!; JsonNode input = ledger["Job"]!["ExportInput"]!;
        switch (mode)
        {
            case "unknown": input["Audio"]!["AudioProfile"] = "future"; break;
            case "pureVideoAudio": input["IncludeAudio"] = false; break;
            case "missingAudio": input["Audio"] = null; break;
            case "hash": input["Audio"]!["Sha256"] = "bad"; break;
            case "size": input["Audio"]!["SizeBytes"] = 1; break;
            case "missingExecution": input.AsObject().Remove("ExecutionProfile"); break;
            case "unknownExecution": input["ExecutionProfile"] = "future"; break;
            case "missingAudioProfile": input["Audio"]!.AsObject().Remove("AudioProfile"); break;
            case "waveWithCompressedExecution": input["Audio"]!["AudioProfile"] = BackendAudioInputProfiles.Pcm16Wave; break;
        }
        await File.WriteAllTextAsync(path, ledger.ToJsonString()); await f.Restart();
        Assert.Equal(BackendErrorCode.TaskExecutionFailed, f.Registry.ReadExportResult("new-bootstrap", job.JobId, true).Error!.Code); Assert.Equal(1, f.Exporter.Calls);
    }

    private static byte[] Candidate(string profile) => profile == BackendAudioInputProfiles.Mp3
        ? BackendAudioInputClassifierTests.Mp3() : BackendAudioInputClassifierTests.Flac(1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 分类读取与释放双故障保原primary且成功分类释放失败不能接受(bool failRead)
    {
        byte[] bytes = Candidate(BackendAudioInputProfiles.Mp3);
        IOException readFailure = new("actual read failure"), cleanupFailure = new("actual dispose failure");
        using var stream = new ExportClassificationStream(bytes, failRead ? readFailure : null, cleanupFailure);
        bool released = false;
        var asset = new StoredAsset("asset-owned", "client", "song", "audio/mpeg", bytes.Length, WorkerProtocol.ComputeSha256(bytes),
            "/owned/audio", DateTimeOffset.UtcNow.AddMinutes(1));
        var lease = new ResourceReadLease(asset, stream, new ExportClassificationReference(() => released = true));
        MethodInfo classify = typeof(ProjectSessionRegistry).GetMethod("ClassifyExportAudio", BindingFlags.Static | BindingFlags.NonPublic)!;
        try
        {
            TargetInvocationException wrapped = Assert.Throws<TargetInvocationException>(() => classify.Invoke(null, [lease]));
            Assert.Same(failRead ? readFailure : cleanupFailure, wrapped.InnerException); Assert.True(released);
            if (failRead) Assert.Same(cleanupFailure, readFailure.Data["ExportAudioReadCleanupFailure"]);
        }
        finally { stream.DisableCleanupFailure(); }
    }

    private sealed class ExportClassificationReference(Action dispose) : IDisposable
    { public void Dispose() => dispose(); }
    private sealed class ExportClassificationStream(byte[] bytes, IOException? readFailure, IOException cleanupFailure) : MemoryStream(bytes)
    {
        private bool _failCleanup = true;
        public override int Read(Span<byte> buffer) => readFailure is null ? base.Read(buffer) : throw readFailure;
        protected override void Dispose(bool disposing)
        { base.Dispose(disposing); if (disposing && _failCleanup) throw cleanupFailure; }
        internal void DisableCleanupFailure() => _failCleanup = false;
    }

    [Fact]
    public async Task 压缩成功报告源时长超300秒在Host提交前拒绝而不相信Worker标记()
    {
        await using Fixture f = await Fixture.Create(new BackendExportOptions { MaxCompressedAudioDurationUs = 300_000_000 }, AllExportProfiles());
        string id = await f.Project("a", audioBytes: Candidate(BackendAudioInputProfiles.Mp3));
        f.Exporter.SourceAudioDuration = "300000001";
        JobResponse job = await f.Start(id); (await f.Exporter.Next()).Release.TrySetResult();
        JobResponse terminal = await f.Terminal("a", job.JobId);
        Assert.Equal("failed", terminal.State); Assert.Null(terminal.ExportResult); Assert.Null(terminal.FailureReportRef);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.State, "export-artifacts"), "artifact-*"));
    }

    [Fact]
    public async Task 压缩恢复已登记报告即使hash与size匹配仍须遵守300秒源预算()
    {
        await using Fixture f = await Fixture.Create(new BackendExportOptions { MaxCompressedAudioDurationUs = 300_000_000 }, AllExportProfiles());
        string id = await f.Project("a", audioBytes: Candidate(BackendAudioInputProfiles.Flac));
        JobResponse job = await f.Start(id); (await f.Exporter.Next()).Release.TrySetResult();
        JobResponse terminal = await f.Terminal("a", job.JobId); Assert.Equal("succeeded", terminal.State);
        string reportPath = Path.Combine(f.State, "export-artifacts", terminal.ExportResult!.Artifact.ArtifactId, "report.json");
        JsonNode report = JsonNode.Parse(await File.ReadAllBytesAsync(reportPath))!; report["audioDurationUs"] = "300000001";
        byte[] reportBytes = System.Text.Encoding.UTF8.GetBytes(report.ToJsonString()); await File.WriteAllBytesAsync(reportPath, reportBytes);
        string ledgerPath = Path.Combine(f.State, "jobs", job.JobId + ".json"); JsonNode ledger = JsonNode.Parse(await File.ReadAllBytesAsync(ledgerPath))!;
        JsonNode reference = ledger["Job"]!["ExportResult"]!["Report"]!; reference["SizeBytes"] = reportBytes.Length; reference["Sha256"] = WorkerProtocol.ComputeSha256(reportBytes);
        await File.WriteAllTextAsync(ledgerPath, ledger.ToJsonString()); await f.Restart();
        Assert.Equal(BackendErrorCode.TaskExecutionFailed, f.Registry.ReadExportResult("new-bootstrap", job.JobId, true).Error!.Code);
        Assert.Equal(1, f.Exporter.Calls);
    }

    private static ProjectWorkspace ExportWorkspace(ProjectSession session) => session.Workspace;
}
