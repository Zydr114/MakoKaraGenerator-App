using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed partial class BackendAnalysisAdoptionTests
{
    [Fact]
    public async Task 直接效果包固定无音轨快照且带音轨请求核验实际时长与授权资源()
    {
        await using Fixture f = new(); ProjectSession session = await f.Project(timed: true);
        var workspace = Workspace(session); var package = MixedEffectPackageFixture.Contents();
        workspace.Replace(workspace.Project with
        {
            Effects = new([package], [new()
            {
                Id = "package-instance", Name = "package", PackageId = package.PackageId, Revision = package.Revision,
                ExportId = "controller", Binding = new(ProjectEffectBindingKind.Project), Subscription = new("project"),
            }]),
        });
        var exporter = new ProfileExport();
        f.Registry.ConfigureExportTasks(exporter, new(true, true, true,
            SourceProfiles: [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis, PackageProjectSnapshotCodec.Profile]));
        Assert.Contains(PackageProjectSnapshotCodec.Profile, f.Registry.ExportCapabilities.SourceProfiles!);
        var accepted = await f.Registry.StartExportAsync("a", session.ProjectSessionId,
            new("0", IncludeAudio: false, Metadata: f.Meta()));
        Assert.True(accepted.Accepted, accepted.Error?.Message);
        Assert.Equal(PackageProjectSnapshotCodec.Profile, accepted.Response!.Input!.SourceProfile);
        Assert.NotNull(exporter.Prepared?.Effects);
        JobResponse completed = await f.Terminal(accepted.Response.JobId);
        Assert.Equal("succeeded", completed.State);
        byte[] report = f.Registry.ReadExportResult("a", completed.JobId).Response!;
        using (BackendExportReadLease read = f.Registry.AcquireExportRead("a", completed.JobId).Response!)
            Assert.True(await read.VerifyHashAsync());
        Assert.Empty(f.Registry.ReadExportDiagnostics("a", completed.JobId, null, null).Response!.Diagnostics);
        ProjectSnapshot prepared = exporter.Prepared!, executed = exporter.Executed!;
        string[] jobs = (await f.Registry.ReadClientStateAsync("a")).Jobs!.Select(job => job.JobId).Order().ToArray();
        // 此fixture的加载器报告3s，但实际WAV只有64帧／8000Hz；以真实音轨长度拒绝，
        // 不能恢复已过时的“package一律不支持音轨”门禁。
        var rejected = await f.Registry.StartExportAsync("a", session.ProjectSessionId,
            new("0", IncludeAudio: true, Metadata: f.Meta()));
        Assert.Equal(BackendErrorCode.ExportAudioUnsupported, rejected.Error!.Code);
        Assert.Equal("实际音轨不足计划视频时长。", rejected.Error.Message);
        Assert.Same(prepared, exporter.Prepared); Assert.Same(executed, exporter.Executed);
        Assert.Equal(jobs, (await f.Registry.ReadClientStateAsync("a")).Jobs!.Select(job => job.JobId).Order());

        var cleared = await f.Command(session, "0", new ClearProjectAudioCommandDto());
        Assert.True(cleared.Accepted, cleared.Error?.Message); Assert.True(cleared.Response!.Changed);
        var missing = await f.Registry.StartExportAsync("a", session.ProjectSessionId,
            new(cleared.Response.ProjectRevision, IncludeAudio: true, Metadata: f.Meta()));
        Assert.Equal(BackendErrorCode.ExportAudioUnsupported, missing.Error!.Code);
        Assert.Equal("带音轨导出需要已授权的受管音轨资源。", missing.Error.Message);
        Assert.Same(prepared, exporter.Prepared); Assert.Same(executed, exporter.Executed);
        Assert.Equal(jobs, (await f.Registry.ReadClientStateAsync("a")).Jobs!.Select(job => job.JobId).Order());
    }

    [Theory]
    [InlineData(null)] [InlineData("available")] [InlineData("failed")] [InlineData("noStream")]
    public async Task 纯静态预设和可选分析按实际新能力固定验证导出来源与恢复报告(string? analysis)
    {
        await using Fixture f = new(); ProjectSession session = await f.Project(timed: true);
        var workspace = Workspace(session); var source = workspace.Project;
        var template = StyleTemplate.Create("full-snapshot", "固定定义", StyleTemplateKind.Line,
            source.LyricStyle with { Opacity = .4f }, [LyricStyleProperty.Opacity]);
        source = source with { PresetApplications = new([new PresetApplication("application-id", new(ProjectEffectBindingKind.Project), template)]) };
        if (analysis is not null) source = source with { AudioAnalysis = analysis switch
        {
            "failed" => ProjectAudioAnalysis.Failed("failure", "fixed failure"),
            "noStream" => ProjectAudioAnalysis.NoStream("noStream", "fixed absence"),
            _ => ProjectAudioAnalysis.Available(new(source.AudioMedia!.Hash, new string('b', 64), 384, [new(0, .2, .3, null, 0)])),
        } };
        workspace.Replace(source);
        ProfileValidation validator = new(); ProfileExport exporter = new();
        f.Registry.ConfigureValidationTasks(validator);
        string[] profiles = [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis, BackendSnapshotProfiles.StaticLyricsScenePresets];
        f.Registry.ConfigureExportTasks(exporter, new(true, true, true, SourceProfiles: profiles));
        profiles[2] = "mutated caller array";
        Assert.Contains(BackendSnapshotProfiles.StaticLyricsScenePresets, f.Registry.ExportCapabilities.SourceProfiles!);
        var validationAdmission = await f.Registry.StartValidationAsync("a", session.ProjectSessionId, new("0", 1, f.Meta()));
        Assert.True(validationAdmission.Accepted, validationAdmission.Error?.Message);
        await validator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        var exportAdmission = await f.Registry.StartExportAsync("a", session.ProjectSessionId, new("0", IncludeAudio: false, Metadata: f.Meta()));
        Assert.True(exportAdmission.Accepted, exportAdmission.Error?.Message);
        var validation = validationAdmission.Response!; var export = exportAdmission.Response!;
        var snapshot = ProjectSnapshot.FromProject(source);
        string sha = WorkerProtocol.ComputeSha256(StaticProjectSnapshotCodec.Encode(snapshot, StaticProjectSnapshotCodec.PresetsProfile));
        Assert.Equal(BackendSnapshotProfiles.StaticLyricsScenePresets, validation.Input!.SourceProfile);
        Assert.Equal(validation.Input.SourceProfile, export.Input!.SourceProfile);
        Assert.Equal(sha, validation.Input.SourceSha256); Assert.Equal(sha, export.Input.SourceSha256);
        Assert.Equal(source.PresetApplications, validator.Snapshot!.PresetApplications);
        Assert.Equal(source.PresetApplications, exporter.Prepared!.PresetApplications);
        // Later project edits cannot flatten or mutate the accepted fixed input.
        workspace.Replace(source with { PresetApplications = PresetApplications.Empty });
        validator.Release.TrySetResult();
        Assert.Equal("succeeded", (await f.Terminal(validation.JobId)).State);
        Assert.Equal("succeeded", (await f.Terminal(export.JobId)).State);
        Assert.Equal(source.PresetApplications, exporter.Executed!.PresetApplications);
        byte[] report = f.Registry.ReadValidationResult("a", validation.JobId).Response!;
        byte[] exported = f.Registry.ReadExportResult("a", export.JobId).Response!;
        await f.Restart();
        f.Registry.ConfigureValidationTasks(new ProfileValidation()); f.Registry.ConfigureExportTasks(new ProfileExport(), new(true, true, true));
        Assert.Equal(report, f.Registry.ReadRecoveredValidationResult("a", validation.JobId).Response);
        Assert.Equal(exported, f.Registry.ReadExportResult("a", export.JobId, recovered: true).Response);
    }

    [Fact]
    public async Task 旧导出部署继续原两能力且新预设在任务及候选注册前明确拒绝()
    {
        await using Fixture f = new(); ProjectSession session = await f.Project(timed: true);
        var workspace = Workspace(session); var source = workspace.Project;
        var template = StyleTemplate.Create("static", "static", StyleTemplateKind.Line, source.LyricStyle, [LyricStyleProperty.Opacity]);
        workspace.Replace(source with { PresetApplications = new([new PresetApplication("application", new(ProjectEffectBindingKind.Project), template)]) });
        var exporter = new ProfileExport(); f.Registry.ConfigureExportTasks(exporter, new(true, true, true));
        Assert.Equal([BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis], f.Registry.ExportCapabilities.SourceProfiles!);
        var rejected = await f.Registry.StartExportAsync("a", session.ProjectSessionId, new("0", IncludeAudio: false, Metadata: f.Meta()));
        Assert.Equal(BackendErrorCode.TaskUnavailable, rejected.Error!.Code);
        Assert.Null(exporter.Prepared); Assert.Null(exporter.Executed);
        Assert.Empty((await f.Registry.ReadClientStateAsync("a")).Jobs!);
    }
}
