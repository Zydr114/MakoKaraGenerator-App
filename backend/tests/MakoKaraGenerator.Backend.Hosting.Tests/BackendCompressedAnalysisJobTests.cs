using System.Text.Json.Nodes;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed partial class BackendAnalysisJobTests
{
    private static BackendAnalysisCapabilities AllAnalysisProfiles() => new(true, true,
        AudioProfiles: [BackendAudioInputProfiles.Pcm16Wave, BackendAudioInputProfiles.Mp3, BackendAudioInputProfiles.Flac,
            BackendAudioInputProfiles.Aac, BackendAudioInputProfiles.Mov, BackendAudioInputProfiles.Ogg],
        SourceProfiles: [BackendAudioInputProfiles.LegacyAnalysisSource, BackendAudioInputProfiles.CompressedAnalysisSource]);

    [Theory]
    [InlineData(BackendAudioInputProfiles.Mp3)]
    [InlineData(BackendAudioInputProfiles.Flac)]
    public async Task 压缩候选按固定bytes授格式未知时长完整结果同源且重启只读不自动采用(string profile)
    {
        await using Fixture f = new(capabilities: AllAnalysisProfiles());
        ProjectSession project = await f.Project("a", profile == BackendAudioInputProfiles.Mp3 ? BackendAudioInputClassifierTests.Mp3() : BackendAudioInputClassifierTests.Flac());
        AnalysisTaskCapabilitiesDto cap = f.Registry.AnalysisCapabilities;
        Assert.Equal(AnalysisResultCodec.SourceProfile, cap.SourceProfile); Assert.Equal(6, cap.AudioProfiles.Length); Assert.Equal(2, cap.SourceProfiles!.Length);
        JobResponse job = await f.Start("a", project); Call call = await f.Executor.Next();
        Assert.Equal(profile, call.Grant.AudioProfile); Assert.Equal(profile, job.AnalysisInput!.Audio.AudioProfile);
        Assert.Equal(BackendAudioInputProfiles.CompressedAnalysisSource, job.AnalysisInput.SourceProfile); Assert.Null(job.AnalysisInput.SourceDurationUs);
        byte[] canonical = Canonical(call.Grant.Sha256); call.Release.SetResult(canonical);
        JobResponse done = await f.Terminal("a", job.JobId); Assert.Equal("succeeded", done.State);
        Assert.Equal(call.Grant.Sha256, done.AnalysisResult!.SourceHash); Assert.Null(Workspace(project).Project.AudioAnalysis);
        using (BackendAnalysisReadLease read = f.Registry.AcquireAnalysisRead("a", job.JobId).Response!) Assert.Equal(canonical, await Bytes(read.Stream));
        await f.Restart();
        using BackendAnalysisReadLease recovered = f.Registry.AcquireAnalysisRead("new-bootstrap-client", job.JobId, true).Response!;
        Assert.Equal(canonical, await Bytes(recovered.Stream)); Assert.Equal(1, f.Executor.Calls);
        Assert.Equal(BackendErrorCode.JobNotFound, f.Registry.AcquireAnalysisRead("a", job.JobId).Error!.Code);
    }

    [Fact]
    public async Task 旧受控部署只公布WAV并在接受前拒绝压缩格式不保留任务槽()
    {
        await using Fixture f = new(); ProjectSession compressed = await f.Project("a", BackendAudioInputClassifierTests.Mp3());
        Assert.Equal([BackendAudioInputProfiles.Pcm16Wave], f.Registry.AnalysisCapabilities.AudioProfiles);
        Assert.Equal([BackendAudioInputProfiles.LegacyAnalysisSource], f.Registry.AnalysisCapabilities.SourceProfiles!);
        var rejected = await f.Registry.StartAnalysisAsync("a", compressed.ProjectSessionId, new("0", f.Meta()));
        Assert.Equal(BackendErrorCode.TaskUnavailable, rejected.Error!.Code); Assert.Equal(0, f.Executor.Calls);
        ProjectSession wave = await f.Project("a"); JobResponse job = await f.Start("a", wave); Call call = await f.Executor.Next();
        Assert.Equal(BackendAudioInputProfiles.Pcm16Wave, call.Grant.AudioProfile); Assert.Null(job.AnalysisInput!.Audio.AudioProfile);
        Assert.Equal("8000", job.AnalysisInput.SourceDurationUs); call.Release.SetResult(Canonical(call.Grant.Sha256));
        Assert.Equal("succeeded", (await f.Terminal("a", job.JobId)).State);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missingSource")]
    public async Task 显式部署能力含未知重复或不匹配profile禁用且不虚报数组(string bad)
    {
        string[] audio = bad == "unknown" ? ["future"] : bad == "duplicate"
            ? [BackendAudioInputProfiles.Mp3, BackendAudioInputProfiles.Mp3] : [BackendAudioInputProfiles.Mp3];
        await using Fixture f = new(capabilities: new(true, true, AudioProfiles: audio, SourceProfiles: [BackendAudioInputProfiles.LegacyAnalysisSource]));
        Assert.False(f.Registry.AnalysisCapabilities.Enabled); Assert.Empty(f.Registry.AnalysisCapabilities.AudioProfiles); Assert.Empty(f.Registry.AnalysisCapabilities.SourceProfiles!);
        ProjectSession project = await f.Project("a");
        Assert.Equal(BackendErrorCode.TaskUnavailable, (await f.Registry.StartAnalysisAsync("a", project.ProjectSessionId, new("0", f.Meta()))).Error!.Code);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("audio")]
    [InlineData("missing")]
    [InlineData("duration")]
    public async Task 压缩恢复读取拒绝未知错配或缺失格式及伪造时长而不复活旧任务(string mismatch)
    {
        await using Fixture f = new(capabilities: AllAnalysisProfiles()); ProjectSession project = await f.Project("a", BackendAudioInputClassifierTests.Mp3());
        JobResponse job = await f.Start("a", project); Call call = await f.Executor.Next(); call.Release.SetResult(Canonical(call.Grant.Sha256)); await f.Terminal("a", job.JobId);
        string path = Path.Combine(f.State, "jobs", job.JobId + ".json");
        JsonNode ledger = JsonNode.Parse(await File.ReadAllTextAsync(path))!; JsonNode input = ledger["Job"]!["AnalysisInput"]!;
        switch (mismatch)
        {
            case "source": input["SourceProfile"] = "future"; break;
            case "audio": input["Audio"]!["AudioProfile"] = BackendAudioInputProfiles.Pcm16Wave; break;
            case "missing": input["Audio"]!.AsObject().Remove("AudioProfile"); break;
            case "duration": input["SourceDurationUs"] = "1"; break;
        }
        await File.WriteAllTextAsync(path, ledger.ToJsonString()); await f.Restart();
        Assert.Equal(BackendErrorCode.TaskExecutionFailed, f.Registry.AcquireAnalysisRead("new-bootstrap-client", job.JobId, true).Error!.Code);
        Assert.Equal(1, f.Executor.Calls); Assert.Equal(BackendErrorCode.JobNotFound, f.Registry.AcquireAnalysisRead("a", job.JobId).Error!.Code);
    }
}
