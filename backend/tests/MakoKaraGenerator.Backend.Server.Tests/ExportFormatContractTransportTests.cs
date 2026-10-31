using System.Net;
using System.Net.Http.Json;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Theory]
    [InlineData("draft")] [InlineData("standard")] [InlineData("high")]
    public async Task 真实HTTP透明导出仅接受合同ProRes名称且质量送入固定任务(string quality)
    {
        await using Fixture f = await Fixture.Create(exportFixture: true);
        string route = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/export-jobs";
        var capabilities = (await f.Http.GetFromJsonAsync<ExportTaskCapabilitiesDto>("/api/v1/export-capabilities", Json))!;
        Assert.Equal(["opaqueMp4", "transparentProRes4444"], capabilities.Formats);
        Assert.Equal(["draft", "standard", "high"], capabilities.Qualities!);
        using var invalidQuality = await f.Http.PostAsJsonAsync(route, new ExportJobRequest("0", IncludeAudio: false, Metadata: f.Metadata(), Quality: "ultra"), Json);
        await Error(invalidQuality, HttpStatusCode.UnprocessableEntity, BackendErrorCode.ExportInputRejected);
        foreach (string format in new[] { "transparentMov", "TransparentProRes4444", "mov" })
        {
            using var rejected = await f.Http.PostAsJsonAsync(route, new ExportJobRequest("0", format, false, f.Metadata()), Json);
            await Error(rejected, HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandNotSupported);
        }
        Assert.Equal(0, f.Exporter.Calls);
        var before = (await f.Http.GetFromJsonAsync<ClientStateResponse>($"/api/v1/clients/{f.Client.ClientId}/state", Json))!;
        Assert.Empty(before.Jobs ?? []);
        ExportJobRequest request = new("0", "transparentProRes4444", false, f.Metadata(), quality);
        using var accepted = await f.Http.PostAsJsonAsync(route, request, Json);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var job = (await accepted.Content.ReadFromJsonAsync<JobResponse>(Json))!;
        Assert.Equal("transparentProRes4444", job.ExportInput!.Format);
        Assert.Equal(quality, job.ExportInput.Quality);
        using var replay = await f.Http.PostAsJsonAsync(route, request, Json);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal(job.JobId, (await replay.Content.ReadFromJsonAsync<JobResponse>(Json))!.JobId);
        await f.Exporter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal("transparentProRes4444", f.Exporter.Input!.Format);
        Assert.Equal(quality, f.Exporter.Input.Quality);
        Assert.EndsWith(".mov", f.Exporter.Input.CandidatePath, StringComparison.Ordinal);
        // This fixture establishes HTTP admission and dispatch; it deliberately cancels before producing media.
        using var cancelled = await f.Http.PostAsJsonAsync($"/api/v1/jobs/{job.JobId}/cancel", f.Metadata(), Json);
        Assert.True(cancelled.IsSuccessStatusCode, await cancelled.Content.ReadAsStringAsync());
        await WaitUntil(() => f.Projects.ReadJob(f.Client.ClientId, job.JobId).Response!.State == "cancelled");
        Assert.Equal(1, f.Exporter.Calls);
        var after = (await f.Http.GetFromJsonAsync<ClientStateResponse>($"/api/v1/clients/{f.Client.ClientId}/state", Json))!;
        Assert.Equal(job.JobId, Assert.Single(after.Jobs!).JobId);
        var summary = (await f.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>($"/api/v1/project-sessions/{f.Project.ProjectSessionId}", Json))!;
        Assert.Equal("0", summary.ProjectRevision); Assert.False(summary.CanUndo);
    }
}
