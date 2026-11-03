using System.Reflection;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Persistence;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed partial class BackendAnalysisAdoptionTests
{
    [Theory]
    [InlineData(BackendAudioInputProfiles.Pcm16Wave, false)]
    [InlineData(BackendAudioInputProfiles.Mp3, false)]
    [InlineData(BackendAudioInputProfiles.Flac, false)]
    [InlineData(BackendAudioInputProfiles.Pcm16Wave, true)]
    [InlineData(BackendAudioInputProfiles.Mp3, true)]
    [InlineData(BackendAudioInputProfiles.Flac, true)]
    public async Task 保存重开或恢复后新分析采用绑定原固定闭包路径而非资源副本(string profile, bool recover)
    {
        await using Fixture f = new(compressed: true);
        byte[]? audio = profile == BackendAudioInputProfiles.Mp3 ? BackendAudioInputClassifierTests.Mp3()
            : profile == BackendAudioInputProfiles.Flac ? BackendAudioInputClassifierTests.Flac() : null;
        ProjectSession original = await f.Project(audioBytes: audio);
        JobResponse oldJob = await f.Complete(original);
        ProjectSession reopened = await ReloadForFreshAnalysis(f, original, recover);
        var workspace = Workspace(reopened); var pinned = workspace.Project;
        StoredAsset asset = f.Resources.GetAuthorized("a", reopened.AudioAssetId);
        Assert.NotEqual(original.AudioAssetId, reopened.AudioAssetId);
        Assert.NotEqual(asset.StoragePath, pinned.AudioPath);
        Assert.NotNull(pinned.AudioPath); Assert.True(Path.IsPathFullyQualified(pinned.AudioPath));
        Assert.Equal(asset.Sha256, pinned.AudioMedia!.Hash); Assert.Equal(asset.Size, pinned.AudioMedia.ByteLength);
        Assert.Equal(await File.ReadAllBytesAsync(asset.StoragePath), await File.ReadAllBytesAsync(pinned.AudioPath!));
        var old = await f.Command(reopened, "0", new("audio.analysis.apply", AnalysisJobId: oldJob.JobId));
        Assert.Equal(recover ? BackendErrorCode.JobNotFound : BackendErrorCode.CommandRejected, old.Error!.Code);
        Assert.Same(pinned, workspace.Project); Assert.Equal(0, workspace.History.UndoCount);

        JobResponse job = await f.Complete(reopened);
        Assert.Equal("succeeded", job.State);
        Assert.Equal(profile == BackendAudioInputProfiles.Pcm16Wave ? null : profile, job.AnalysisInput!.Audio.AudioProfile);
        Assert.Equal(profile == BackendAudioInputProfiles.Pcm16Wave ? BackendAudioInputProfiles.LegacyAnalysisSource
            : BackendAudioInputProfiles.CompressedAnalysisSource, job.AnalysisInput.SourceProfile);
        Assert.Equal(asset.AssetId, job.AnalysisInput.Audio.AssetId); Assert.Equal(asset.Sha256, job.AnalysisInput.Audio.Sha256);
        Assert.Equal(asset.Size, job.AnalysisInput.Audio.SizeBytes); Assert.Equal(reopened.ProjectSessionId, job.AnalysisInput.SourceId);
        var applied = await f.Command(reopened, "0", new("audio.analysis.apply", AnalysisJobId: job.JobId));
        Assert.True(applied.Accepted, applied.Error?.Message); Assert.True(applied.Response!.Changed);
        Assert.Equal("1", applied.Response.ProjectRevision); Assert.Equal(1, workspace.History.UndoCount);
        Assert.Equal(pinned.AudioPath, workspace.Project.AudioPath); Assert.Equal(pinned.AudioMedia, workspace.Project.AudioMedia);
        Assert.Equal(Canonical(asset.Sha256), AudioAnalysisJson.Encode(workspace.Project.AudioAnalysis!.Data!));
        Assert.Equal(job.AnalysisResult!.ContentHash, (await f.Registry.ReadSummaryAsync("a", reopened.ProjectSessionId))!.AudioAnalysis!.ContentHash);
    }

    [Fact]
    public async Task 重开后相同字节不同绝对路径仍拒绝采用并保留历史修订事件()
    {
        await using Fixture f = new(compressed: true);
        ProjectSession original = await f.Project(audioBytes: BackendAudioInputClassifierTests.Flac());
        ProjectSession reopened = await ReloadForFreshAnalysis(f, original, recover: false);
        JobResponse job = await f.Complete(reopened); var workspace = Workspace(reopened); var pinned = workspace.Project;
        string other = Path.Combine(f.Root, "same-bytes.flac"); File.Copy(pinned.AudioPath!, other);
        Assert.Equal(await File.ReadAllBytesAsync(pinned.AudioPath!), await File.ReadAllBytesAsync(other));
        workspace.Replace(pinned with { AudioPath = other }); var changed = workspace.Project;
        var before = (await f.Registry.ReadSummaryAsync("a", reopened.ProjectSessionId))!;
        int undo = workspace.History.UndoCount, redo = workspace.History.RedoCount;
        var rejected = await f.Command(reopened, "0", new("audio.analysis.apply", AnalysisJobId: job.JobId));
        Assert.False(rejected.Accepted); Assert.Equal(BackendErrorCode.CommandRejected, rejected.Error!.Code); Assert.Same(changed, workspace.Project);
        Assert.Null(workspace.Project.AudioAnalysis); Assert.Equal(undo, workspace.History.UndoCount); Assert.Equal(redo, workspace.History.RedoCount);
        Assert.Equal(before, await f.Registry.ReadSummaryAsync("a", reopened.ProjectSessionId));
        workspace.Replace(pinned);
        Assert.True((await f.Command(reopened, "0", new("audio.analysis.apply", AnalysisJobId: job.JobId))).Accepted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/audio.wav")]
    public async Task 缺失或非绝对固定路径不能因当前路径相同而采用(string? path)
    {
        await using Fixture f = new(); ProjectSession project = await f.Project(); JobResponse job = await f.Complete(project);
        var workspace = Workspace(project); workspace.Replace(workspace.Project with { AudioPath = path }); var before = workspace.Project;
        // Simulate invalid construction state; a matching absent path must never count as a prepared source.
        typeof(ProjectSession).GetField("<PreparedAudioSourcePath>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(project, path);
        int undo = workspace.History.UndoCount;
        var rejected = await f.Command(project, "0", new("audio.analysis.apply", AnalysisJobId: job.JobId));
        Assert.False(rejected.Accepted); Assert.Equal(BackendErrorCode.CommandRejected, rejected.Error!.Code); Assert.Same(before, workspace.Project);
        Assert.Null(workspace.Project.AudioAnalysis); Assert.Equal(undo, workspace.History.UndoCount);
        Assert.Equal("0", (await f.Registry.ReadSummaryAsync("a", project.ProjectSessionId))!.ProjectRevision);
    }

    private static async Task<ProjectSession> ReloadForFreshAnalysis(Fixture f, ProjectSession original, bool recover)
    {
        string handle = await f.Handle("fresh-analysis");
        Assert.Equal("succeeded", (await f.Terminal((await f.Registry.StartSaveAsync("a", original.ProjectSessionId, new("0", handle, f.Meta()))).Response!.JobId)).State);
        string projectJson = Path.Combine(f.Root, "fresh-analysis", "project.json"); byte[] saved = await File.ReadAllBytesAsync(projectJson);
        JobResponse loaded;
        if (recover)
        {
            var line = (await f.Registry.ReadLyricsAsync("a", original.ProjectSessionId, "primary"))!.Lines[0];
            Assert.True((await f.Command(original, "0", new("lyrics.setTimeRange", new("primary", line.Id), "100000", "900000"))).Accepted);
            await f.Restart();
            RecoveryCandidateResponse candidate = Assert.Single(f.Registry.ReadRecoveryCandidates("a"));
            loaded = await f.Terminal((await f.Registry.StartRecoverAsync("a", new(candidate.RecoveryId, f.Meta()))).Response!.JobId);
            Assert.True(loaded.IsDirty); Assert.Empty(f.Registry.ReadRecoveryCandidates("a"));
        }
        else
        {
            Assert.Equal("succeeded", (await f.Terminal((await f.Registry.StartCloseAsync("a", original.ProjectSessionId, new("0", ActiveJobs: "wait", Metadata: f.Meta()))).Response!.JobId)).State);
            loaded = await f.Terminal((await f.Registry.StartOpenAsync("a", new(handle, Metadata: f.Meta()))).Response!.JobId);
            Assert.False(loaded.IsDirty);
        }
        Assert.True(loaded.State == "succeeded", System.Text.Json.JsonSerializer.Serialize(loaded));
        Assert.Equal(saved, await File.ReadAllBytesAsync(projectJson));
        Assert.True(f.Registry.TryGet("a", loaded.ProjectSessionId!, out ProjectSession reopened));
        return reopened;
    }

    [Theory]
    [InlineData(BackendAudioInputProfiles.Mp3)]
    [InlineData(BackendAudioInputProfiles.Flac)]
    public async Task 压缩分析显式采用完整canonical清除撤销无变化保存重开仍同原bytes来源(string profile)
    {
        await using Fixture f = new(compressed: true);
        ProjectSession project = await f.Project(audioBytes: profile == BackendAudioInputProfiles.Mp3 ? BackendAudioInputClassifierTests.Mp3() : BackendAudioInputClassifierTests.Flac());
        JobResponse job = await f.Complete(project); Assert.Equal(profile, job.AnalysisInput!.Audio.AudioProfile);
        var applied = await f.Command(project, "0", new("audio.analysis.apply", AnalysisJobId: job.JobId)); Assert.True(applied.Accepted); Assert.True(applied.Response!.Changed);
        byte[] canonical = Canonical(job.AnalysisInput.Audio.Sha256);
        Assert.Equal(canonical, AudioAnalysisJson.Encode(Workspace(project).Project.AudioAnalysis!.Data!)); Assert.Equal(1, Workspace(project).History.UndoCount);
        Assert.True((await f.Command(project, "1", new("audio.analysis.clear"))).Accepted);
        Assert.True((await f.Command(project, "2", new("history.undo"))).Accepted);
        var same = await f.Command(project, "3", new("audio.analysis.apply", AnalysisJobId: job.JobId)); Assert.True(same.Accepted); Assert.False(same.Response!.Changed); Assert.True(same.Response.CanRedo);
        string audio = f.Resources.GetAuthorized("a", project.AudioAssetId).StoragePath;
        byte[] original = await File.ReadAllBytesAsync(audio); byte[] damaged = [.. original]; damaged[^1] ^= 1; await File.WriteAllBytesAsync(audio, damaged);
        Assert.Equal(BackendErrorCode.TaskExecutionFailed, (await f.Command(project, "3", new("audio.analysis.apply", AnalysisJobId: job.JobId))).Error!.Code);
        Assert.Equal(canonical, AudioAnalysisJson.Encode(Workspace(project).Project.AudioAnalysis!.Data!)); Assert.Equal("3", (await f.Registry.ReadSummaryAsync("a", project.ProjectSessionId))!.ProjectRevision);
        await File.WriteAllBytesAsync(audio, original);
        string handle = await f.Handle("compressed");
        JobResponse saved = await f.Terminal((await f.Registry.StartSaveAsync("a", project.ProjectSessionId, new("3", handle, f.Meta()))).Response!.JobId);
        Assert.Equal("succeeded", saved.State);
        Assert.Equal("succeeded", (await f.Terminal((await f.Registry.StartCloseAsync("a", project.ProjectSessionId, new("3", ActiveJobs: "wait", Metadata: f.Meta()))).Response!.JobId)).State);
        var opened = await f.Registry.StartOpenAsync("a", new(handle, Metadata: f.Meta())); JobResponse done = await f.Terminal(opened.Response!.JobId);
        Assert.True(done.State == "succeeded", System.Text.Json.JsonSerializer.Serialize(done));
        Assert.True(f.Registry.TryGet("a", done.ProjectSessionId!, out ProjectSession reopened));
        Assert.Equal(canonical, AudioAnalysisJson.Encode(Workspace(reopened).Project.AudioAnalysis!.Data!));
        Assert.Equal(job.AnalysisInput.Audio.Sha256, Workspace(reopened).Project.AudioMedia!.Hash);
    }
}
