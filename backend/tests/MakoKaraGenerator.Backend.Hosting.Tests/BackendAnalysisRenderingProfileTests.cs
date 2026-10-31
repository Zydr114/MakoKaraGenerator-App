using System.Reflection;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed partial class BackendAnalysisAdoptionTests
{
    [Theory]
    [InlineData("available")]
    [InlineData("failed")]
    [InlineData("noStream")]
    public async Task 三种已采用分析使用新profile固定完整来源且后续清除不影响任务与恢复报告(string state)
    {
        await using Fixture f = new(); ProjectSession project = await f.Project(timed: true); string revision = "0";
        if (state == "available")
        {
            JobResponse analyzed = await f.Complete(project);
            Assert.True((await f.Command(project, "0", new("audio.analysis.apply", AnalysisJobId: analyzed.JobId))).Accepted); revision = "1";
        }
        else
        {
            var original = Workspace(project).Project;
            Workspace(project).Replace(original with { AudioAnalysis = state == "failed"
                ? ProjectAudioAnalysis.Failed("failure", "fixed failure") : ProjectAudioAnalysis.NoStream("noStream", "fixed absence") });
        }
        ProjectSnapshot expected = ProjectSnapshot.FromProject(Workspace(project).Project);
        ProfileValidation validator = new(); ProfileExport exporter = new();
        f.Registry.ConfigureValidationTasks(validator); f.Registry.ConfigureExportTasks(exporter, new(true, true, true));
        Assert.Equal(BackendSnapshotProfiles.StaticLyricsScene, f.Registry.ValidationCapabilities.SourceProfile);
        Assert.Equal([BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis, BackendSnapshotProfiles.StaticLyricsScenePresets], f.Registry.ValidationCapabilities.SourceProfiles!);
        Assert.Equal([BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis], f.Registry.ExportCapabilities.SourceProfiles!);
        JobResponse validation = (await f.Registry.StartValidationAsync("a", project.ProjectSessionId, new(revision, 1, f.Meta()))).Response!;
        await validator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        var exportAdmission = await f.Registry.StartExportAsync("a", project.ProjectSessionId, new(revision, IncludeAudio: false, Metadata: f.Meta()));
        Assert.True(exportAdmission.Accepted, exportAdmission.Error?.Code + ": " + exportAdmission.Error?.Message);
        JobResponse export = exportAdmission.Response!;
        string sha = WorkerProtocol.ComputeSha256(StaticProjectSnapshotCodec.Encode(expected, BackendSnapshotProfiles.StaticLyricsSceneAnalysis));
        Assert.Equal(BackendSnapshotProfiles.StaticLyricsSceneAnalysis, validation.Input!.SourceProfile);
        Assert.Equal(BackendSnapshotProfiles.StaticLyricsSceneAnalysis, export.Input!.SourceProfile);
        Assert.Equal(sha, validation.Input.SourceSha256); Assert.Equal(sha, export.Input.SourceSha256);
        Assert.Same(expected.AudioAnalysis, validator.Snapshot!.AudioAnalysis); Assert.Same(expected.AudioAnalysis, exporter.Prepared!.AudioAnalysis);
        Assert.True((await f.Command(project, revision, new("audio.analysis.clear"))).Response!.Changed);
        Assert.Null(Workspace(project).Project.AudioAnalysis);
        validator.Release.TrySetResult();
        Assert.Equal("succeeded", (await f.Terminal(validation.JobId)).State);
        Assert.Equal("succeeded", (await f.Terminal(export.JobId)).State);
        Assert.Same(expected.AudioAnalysis, exporter.Executed!.AudioAnalysis);
        byte[] validationBytes = f.Registry.ReadValidationResult("a", validation.JobId).Response!;
        byte[] exportBytes = f.Registry.ReadExportResult("a", export.JobId).Response!;
        Assert.NotEmpty(validationBytes); Assert.NotEmpty(exportBytes);
        await f.Restart();
        f.Registry.ConfigureValidationTasks(new ProfileValidation()); f.Registry.ConfigureExportTasks(new ProfileExport(), new(true, true, true));
        Assert.Equal(validationBytes, f.Registry.ReadRecoveredValidationResult("a", validation.JobId).Response);
        Assert.Equal(exportBytes, f.Registry.ReadExportResult("a", export.JobId, recovered: true).Response);
    }

    [Fact]
    public async Task 新分析快照扣除验证头部后的净预算在接受任务前拒绝()
    {
        await using Fixture f = new(); ProjectSession project = await f.Project(timed: true);
        var workspace = Workspace(project); var source = workspace.Project;
        source = source with { Name = "x", AudioAnalysis = ProjectAudioAnalysis.Available(new(source.AudioMedia!.Hash,
            new string('b', 64), 384, [new(0, .2, .3, null, 0)])) };
        int original = StaticProjectSnapshotCodec.Encode(ProjectSnapshot.FromProject(source), BackendSnapshotProfiles.StaticLyricsSceneAnalysis).Length;
        int target = WorkerValidationContract.MaximumSnapshotBytes + 1;
        source = source with { Name = new string('x', target - original + 1) }; workspace.Replace(source);
        Assert.Equal(target, StaticProjectSnapshotCodec.Encode(ProjectSnapshot.FromProject(source), BackendSnapshotProfiles.StaticLyricsSceneAnalysis).Length);
        ProfileValidation validator = new(); f.Registry.ConfigureValidationTasks(validator);
        var result = await f.Registry.StartValidationAsync("a", project.ProjectSessionId, new("0", 1, f.Meta()));
        Assert.Equal(BackendErrorCode.ResourceTooLarge, result.Error!.Code);
        Assert.False(validator.Entered.Task.IsCompleted); Assert.Empty((await f.Registry.ReadClientStateAsync("a")).Jobs!);
    }

    [Fact]
    public async Task 实际导出metadata超预算在注册与spawn之前拒绝并撤候选释放预留()
    {
        await using Fixture f = new(); ProjectSession project = await f.Project(timed: true);
        string unicodeTool = "/" + new string('音', 4095);
        WorkerBackendExportExecutor executor = new("/no-worker-may-start", unicodeTool, unicodeTool);
        f.Registry.ConfigureExportTasks(executor, new(true, true, true));
        ProjectSessionSummaryResponse before = (await f.Registry.ReadSummaryAsync("a", project.ProjectSessionId))!;
        var result = await f.Registry.StartExportAsync("a", project.ProjectSessionId, new("0", IncludeAudio: false, Metadata: f.Meta()));
        Assert.False(result.Accepted); Assert.Equal(BackendErrorCode.ResourceTooLarge, result.Error!.Code);
        Assert.Empty((await f.Registry.ReadClientStateAsync("a")).Jobs!);
        Assert.Equal(before, await f.Registry.ReadSummaryAsync("a", project.ProjectSessionId));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.State, "export-artifacts", ".staging")));
    }

    [Fact]
    public async Task 未知来源profile不能伪装成可读取正式或导出报告()
    {
        await using Fixture f = new(); ProjectSession project = await f.Project(timed: true);
        ProfileValidation validator = new(); validator.Release.TrySetResult();
        f.Registry.ConfigureValidationTasks(validator); f.Registry.ConfigureExportTasks(new ProfileExport(), new(true, true, true));
        JobResponse validation = await f.Terminal((await f.Registry.StartValidationAsync("a", project.ProjectSessionId, new("0", 1, f.Meta()))).Response!.JobId);
        var exportAdmission = await f.Registry.StartExportAsync("a", project.ProjectSessionId, new("0", IncludeAudio: false, Metadata: f.Meta()));
        Assert.True(exportAdmission.Accepted, exportAdmission.Error?.Code + ": " + exportAdmission.Error?.Message);
        JobResponse export = await f.Terminal(exportAdmission.Response!.JobId);
        Assert.Equal(BackendSnapshotProfiles.StaticLyricsScene, validation.Input!.SourceProfile);
        Assert.Equal(BackendSnapshotProfiles.StaticLyricsScene, export.Input!.SourceProfile);
        ChangeJobSourceProfile(f.Registry, validation.JobId); ChangeJobSourceProfile(f.Registry, export.JobId);
        Assert.Equal(BackendErrorCode.TaskExecutionFailed, f.Registry.ReadValidationResult("a", validation.JobId).Error!.Code);
        Assert.Equal(BackendErrorCode.TaskExecutionFailed, f.Registry.ReadExportResult("a", export.JobId).Error!.Code);
    }

    private static void ChangeJobSourceProfile(ProjectSessionRegistry registry, string id)
    {
        object jobs = typeof(ProjectSessionRegistry).GetField("_jobs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(registry)!;
        object job = jobs.GetType().GetProperty("Item")!.GetValue(jobs, [id])!;
        FieldInfo field = job.GetType().GetField("_response", BindingFlags.NonPublic | BindingFlags.Instance)!;
        JobResponse value = (JobResponse)field.GetValue(job)!;
        field.SetValue(job, value with { Input = value.Input! with { SourceProfile = "unknown-profile" } });
    }

    private sealed class ProfileValidation : IBackendValidationExecutor
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ProjectSnapshot? Snapshot;
        public async Task<ValidationReportDto> ExecuteAsync(ProjectSnapshot snapshot, int framesPerOutput,
            IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default)
        { Snapshot = snapshot; Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return new(true, framesPerOutput, framesPerOutput, [], []); }
    }

    private sealed class ProfileExport : IBackendExportExecutor
    {
        internal ProjectSnapshot? Prepared, Executed;
        public Task<BackendExportCapabilities> ProbeAsync(CancellationToken cancellationToken = default) => Task.FromResult(new BackendExportCapabilities(true, true, true));
        public void ValidateInput(ProjectSnapshot snapshot, BackendExportExecutionInput input) => Prepared = snapshot;
        public async Task<ExportReportDto> ExecuteAsync(ProjectSnapshot snapshot, BackendExportExecutionInput input,
            IProgress<BackendJobProgressDto>? progress = null, CancellationToken cancellationToken = default)
        {
            Executed = snapshot; byte[] bytes = "fixed profile export seam"u8.ToArray(); await File.WriteAllBytesAsync(input.CandidatePath, bytes, cancellationToken);
            var c = snapshot.Composition; var plan = MakoKaraGenerator.Core.Time.CompositionFramePlan.Create(c.OutputDurationUs!.Value, c.FrameRate);
            string duration = plan.VideoDurationUs.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new(input.Format, false, plan.TotalFrames, plan.TotalFrames, plan.TotalFrames, 0, 0, c.CanvasWidth, c.CanvasHeight,
                c.FrameRate.Numerator, c.FrameRate.Denominator, duration, duration, null, "h264", null, "yuv420p", false,
                bytes.Length, WorkerProtocol.ComputeSha256(bytes), [], [], true, null, true);
        }
    }
}
