using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task HTTP显式采用查询历史清除与新分析profile接入且清除恢复旧profile()
    {
        await using Fixture f = await Fixture.Create(analysisFixture: true, exportFixture: true);
        BackendCapabilitiesResponse capabilities = (await f.Http.GetFromJsonAsync<BackendCapabilitiesResponse>("/api/v1/capabilities", Json))!;
        Assert.Contains("project-sessions.commands.audio.analysis.apply", capabilities.Features);
        Assert.Contains("project-sessions.commands.audio.analysis.clear", capabilities.Features);
        JobResponse job = await CompleteAnalysis(f);
        string command = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/commands";
        ProjectCommandRequest input = AnalysisCommand(f, "0", new("audio.analysis.apply", AnalysisJobId: job.JobId));
        using HttpResponseMessage applied = await f.Http.PostAsJsonAsync(command, input, Json);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode); ProjectCommandResponse accepted = (await applied.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        Assert.True(accepted.Changed); Assert.Equal("1", accepted.ProjectRevision); Assert.Equal("audio.analysis", accepted.UndoName);
        ProjectSessionSummaryResponse summary = (await f.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>($"/api/v1/project-sessions/{f.Project.ProjectSessionId}", Json))!;
        Assert.Equal(job.AnalysisResult!.ContentHash, summary.AudioAnalysis!.ContentHash); Assert.Equal("available", summary.AudioAnalysis.Status);
        Assert.Equal("384", summary.AudioAnalysis.SampleCount); Assert.Equal(job.AnalysisResult.Revision, summary.AudioAnalysis.Revision);
        using HttpResponseMessage repeated = await f.Http.PostAsJsonAsync(command, input, Json);
        ProjectCommandResponse retry = (await repeated.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        Assert.Equal(accepted.ProjectRevision, retry.ProjectRevision); Assert.Equal(accepted.EventCursor, retry.EventCursor);
        Assert.Equal(accepted.ChangeSet.Areas, retry.ChangeSet.Areas);
        ValidationTaskCapabilitiesDto validationCapabilities = (await f.Http.GetFromJsonAsync<ValidationTaskCapabilitiesDto>("/api/v1/validation-capabilities", Json))!;
        ExportTaskCapabilitiesDto exportCapabilities = (await f.Http.GetFromJsonAsync<ExportTaskCapabilitiesDto>("/api/v1/export-capabilities", Json))!;
        PreviewRuntimeInfoResponse previewCapabilities = (await f.Http.GetFromJsonAsync<PreviewRuntimeInfoResponse>("/api/v1/runtime-info", Json))!;
        Assert.Contains(BackendSnapshotProfiles.StaticLyricsSceneAnalysis, validationCapabilities.SourceProfiles!);
        Assert.Contains(BackendSnapshotProfiles.StaticLyricsSceneAnalysis, exportCapabilities.SourceProfiles!);
        Assert.Contains(BackendSnapshotProfiles.StaticLyricsSceneAnalysis, previewCapabilities.SourceProfiles!);
        using HttpResponseMessage preview = await f.Http.PostAsJsonAsync("/api/v1/preview-sessions",
            f.CreateRequest() with { Source = f.Source with { SourceRevision = "1" } }, Json);
        Assert.Equal(HttpStatusCode.Created, preview.StatusCode);
        using HttpResponseMessage validation = await f.Http.PostAsJsonAsync($"/api/v1/project-sessions/{f.Project.ProjectSessionId}/validation-jobs", new ValidationJobRequest("1", 1, f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.Accepted, validation.StatusCode); JobResponse validationJob = (await validation.Content.ReadFromJsonAsync<JobResponse>(Json))!;
        Assert.Equal(BackendSnapshotProfiles.StaticLyricsSceneAnalysis, validationJob.Input!.SourceProfile);
        f.Validation.Release.TrySetResult();
        await WaitUntil(() => f.Projects.ReadJob(f.Client.ClientId, validationJob.JobId).Response!.State == "succeeded");
        using HttpResponseMessage export = await f.Http.PostAsJsonAsync($"/api/v1/project-sessions/{f.Project.ProjectSessionId}/export-jobs", new ExportJobRequest("1", IncludeAudio: false, Metadata: f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.Accepted, export.StatusCode); JobResponse exportJob = (await export.Content.ReadFromJsonAsync<JobResponse>(Json))!;
        Assert.Equal(BackendSnapshotProfiles.StaticLyricsSceneAnalysis, exportJob.Input!.SourceProfile);
        Assert.Equal(validationJob.Input.SourceSha256, exportJob.Input.SourceSha256);
        f.Exporter.Release.TrySetResult();
        await WaitUntil(() => f.Projects.ReadJob(f.Client.ClientId, exportJob.JobId).Response!.State == "succeeded");
        using HttpResponseMessage cleared = await f.Http.PostAsJsonAsync(command, AnalysisCommand(f, "1", new("audio.analysis.clear")), Json);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode); Assert.True((await cleared.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!.Changed);
        Assert.Null((await f.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>($"/api/v1/project-sessions/{f.Project.ProjectSessionId}", Json))!.AudioAnalysis);
        using HttpResponseMessage restored = await f.Http.PostAsJsonAsync("/api/v1/preview-sessions",
            f.CreateRequest() with { Source = f.Source with { SourceRevision = "2" } }, Json);
        Assert.Equal(HttpStatusCode.Created, restored.StatusCode);
        using HttpResponseMessage undo = await f.Http.PostAsJsonAsync(command, AnalysisCommand(f, "2", new("history.undo")), Json);
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode); Assert.Equal("3", (await undo.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!.ProjectRevision);
        Assert.Equal(summary.AudioAnalysis, (await f.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>($"/api/v1/project-sessions/{f.Project.ProjectSessionId}", Json))!.AudioAnalysis);
    }

    [Fact]
    public async Task HTTP新分析快照扣除预览头部后的净预算明确413且没有会话()
    {
        await using Fixture f = await Fixture.Create(analysisFixture: true); JobResponse job = await CompleteAnalysis(f);
        using HttpResponseMessage applied = await f.Http.PostAsJsonAsync($"/api/v1/project-sessions/{f.Project.ProjectSessionId}/commands",
            AnalysisCommand(f, "0", new("audio.analysis.apply", AnalysisJobId: job.JobId)), Json);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        ProjectWorkspace workspace = (ProjectWorkspace)typeof(ProjectSession).GetProperty("Workspace", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Project)!;
        var source = workspace.Project with { Name = "x" };
        int original = StaticProjectSnapshotCodec.Encode(ProjectSnapshot.FromProject(source), BackendSnapshotProfiles.StaticLyricsSceneAnalysis).Length;
        int target = StaticRenderWorkerContract.MaximumSnapshotBytes + 1;
        workspace.Replace(source with { Name = new string('x', target - original + 1) });
        Assert.Equal(target, StaticProjectSnapshotCodec.Encode(ProjectSnapshot.FromProject(workspace.Project), BackendSnapshotProfiles.StaticLyricsSceneAnalysis).Length);
        using HttpResponseMessage rejected = await f.Http.PostAsJsonAsync("/api/v1/preview-sessions",
            f.CreateRequest() with { Source = f.Source with { SourceRevision = "1" } }, Json);
        await Error(rejected, HttpStatusCode.RequestEntityTooLarge, BackendErrorCode.ResourceTooLarge);
        Assert.Equal(0, f.Previews.Count); Assert.Equal(0, f.Factory.Created);
    }

    [Fact]
    public async Task HTTP创建在零修订固定AudioMedia且采用不另改来源或历史()
    {
        await using Fixture f = await Fixture.Create(analysisFixture: true);
        StoredAsset lyric = await f.Resources.UploadAsync(f.Client.ClientId, "created.lrc", "text/plain", new MemoryStream("[00:00.00]created\n"u8.ToArray()));
        StoredAsset audio = f.Resources.GetAuthorized(f.Client.ClientId, f.Project.AudioAssetId);
        using HttpResponseMessage created = await f.Http.PostAsJsonAsync("/api/v1/project-sessions",
            new CreateProjectSessionRequest("created", audio.AssetId, lyric.AssetId, new(640, 360, OutputDurationUs: 3000000), Metadata: f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        ProjectSessionSummaryResponse summary = (await created.Content.ReadFromJsonAsync<ProjectSessionSummaryResponse>(Json))!;
        Assert.Equal("0", summary.ProjectRevision); Assert.True(summary.IsDirty); Assert.False(summary.HasPath); Assert.Null(summary.AudioAnalysis);
        Assert.True(f.Projects.TryGet(f.Client.ClientId, summary.ProjectSessionId, out ProjectSession project));
        ProjectWorkspace workspace = (ProjectWorkspace)typeof(ProjectSession).GetProperty("Workspace", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(project)!;
        Assert.Equal(audio.Sha256, workspace.Project.AudioMedia!.Hash); Assert.Equal(audio.Size, workspace.Project.AudioMedia.ByteLength);
        Assert.Equal(audio.FileName, workspace.Project.AudioMedia.OriginalName); Assert.Equal(audio.StoragePath, workspace.Project.AudioPath);
        Assert.Equal(0, workspace.History.UndoCount);
    }

    [Theory]
    [InlineData("{\"type\":\"audio.analysis.apply\"}")]
    [InlineData("{\"type\":\"audio.analysis.apply\",\"analysisJobId\":\"job\",\"startUs\":\"0\"}")]
    [InlineData("{\"type\":\"audio.analysis.clear\",\"analysisJobId\":\"job\"}")]
    [InlineData("{\"type\":\"history.undo\",\"analysisJobId\":\"job\"}")]
    [InlineData("{\"type\":\"audio.analysis.apply\",\"analysisJobId\":\"job\",\"path\":\"/private/result.json\"}")]
    public async Task HTTP分析命令拒绝混带字段未知成员与裸路径(string command)
    {
        await using Fixture f = await Fixture.Create(analysisFixture: true); RequestMetadataDto metadata = f.Metadata();
        string body = System.Text.Json.JsonSerializer.Serialize(new { expectedRevision = "0", requestId = metadata.RequestId, hostInstanceId = metadata.HostInstanceId, validUntilUtc = metadata.ValidUntilUtc }, Json);
        body = body[..^1] + ",\"command\":" + command + "}";
        using StringContent content = new(body, System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await f.Http.PostAsync($"/api/v1/project-sessions/{f.Project.ProjectSessionId}/commands", content);
        await Error(response, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        Assert.Equal("0", (await f.Projects.ReadSummaryAsync(f.Client.ClientId, f.Project.ProjectSessionId))!.ProjectRevision);
    }

    [Fact]
    public async Task HTTP已采用结果仍检查实际音频损坏且错误和无变化不改历史()
    {
        await using Fixture f = await Fixture.Create(analysisFixture: true); JobResponse job = await CompleteAnalysis(f);
        string command = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/commands";
        using HttpResponseMessage first = await f.Http.PostAsJsonAsync(command, AnalysisCommand(f, "0", new("audio.analysis.apply", AnalysisJobId: job.JobId)), Json);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        ProjectSessionSummaryResponse before = (await f.Projects.ReadSummaryAsync(f.Client.ClientId, f.Project.ProjectSessionId))!;
        StoredAsset asset = f.Resources.GetAuthorized(f.Client.ClientId, f.Project.AudioAssetId); byte[] good = await File.ReadAllBytesAsync(asset.StoragePath);
        byte[] damaged = [.. good]; damaged[^1] ^= 1; await File.WriteAllBytesAsync(asset.StoragePath, damaged);
        using HttpResponseMessage invalid = await f.Http.PostAsJsonAsync(command, AnalysisCommand(f, "1", new("audio.analysis.apply", AnalysisJobId: job.JobId)), Json);
        await Error(invalid, HttpStatusCode.InternalServerError, BackendErrorCode.TaskExecutionFailed);
        Assert.Equal(before, await f.Projects.ReadSummaryAsync(f.Client.ClientId, f.Project.ProjectSessionId));
        await File.WriteAllBytesAsync(asset.StoragePath, good);
        using HttpResponseMessage same = await f.Http.PostAsJsonAsync(command, AnalysisCommand(f, "1", new("audio.analysis.apply", AnalysisJobId: job.JobId)), Json);
        Assert.Equal(HttpStatusCode.OK, same.StatusCode); Assert.False((await same.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!.Changed);
        Assert.Equal(before, await f.Projects.ReadSummaryAsync(f.Client.ClientId, f.Project.ProjectSessionId));
    }

    private static ProjectCommandRequest AnalysisCommand(Fixture fixture, string revision, ProjectCommandDto command)
    { RequestMetadataDto metadata = fixture.Metadata(); return new(revision, command, metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc); }
    private static async Task<JobResponse> CompleteAnalysis(Fixture fixture)
    {
        using HttpResponseMessage accepted = await fixture.Http.PostAsJsonAsync($"/api/v1/project-sessions/{fixture.Project.ProjectSessionId}/analysis-jobs", new AnalysisJobRequest("0", fixture.Metadata()), Json);
        JobResponse job = (await accepted.Content.ReadFromJsonAsync<JobResponse>(Json))!;
        await fixture.Analyzer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8)); fixture.Analyzer.Release.TrySetResult();
        await WaitUntil(() => fixture.Projects.ReadJob(fixture.Client.ClientId, job.JobId).Response!.State == "succeeded");
        return fixture.Projects.ReadJob(fixture.Client.ClientId, job.JobId).Response!;
    }
}
