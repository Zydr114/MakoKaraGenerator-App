using System.Net;
using System.Net.Http.Json;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task 编辑候选不修改工程且可换源请求原帧和拒绝过期代次()
    {
        await using Fixture f = await Fixture.Create();
        string path = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/edit-previews";
        var before = (await f.Projects.ReadSummaryAsync(f.Client.ClientId, f.Project.ProjectSessionId))!;
        var input = new ProjectEditPreviewRequest("0", [new SetCanvasSizeCommandDto(777, 444)], new(1, 1), Metadata: f.Metadata());
        using var created = await f.Http.PostAsJsonAsync(path, input, Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var preview = (await created.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        Assert.Equal("editCandidate", preview.PreviewKind); Assert.False(preview.Committable);
        Assert.Equal("0", preview.EditCandidate!.BaseProjectRevision);
        Assert.Equal(preview.Source.SourceRevision, preview.EditCandidate.CandidateRevision);
        Assert.Equal("composition.setCanvasSize", Assert.Single(preview.EditCandidate.EditResults).Type);
        using var frame = await f.Http.PostAsJsonAsync($"/api/v1/preview-sessions/{preview.PreviewId}/frame-requests",
            f.FrameRequest(preview, 1, 0), Json);
        Assert.Equal(HttpStatusCode.Accepted, frame.StatusCode);
        (await f.Factory.NextRender()).Release.SetResult();
        var ready = await f.WaitReady(preview.PreviewId);
        Assert.Equal(preview.Source.SourceRevision, ready.Session.LatestFrame!.SourceRevision);
        using var png = await f.Http.GetAsync(ready.Session.LatestFrame.Content.ContentUrl);
        Assert.Equal(HttpStatusCode.OK, png.StatusCode);
        var after = (await f.Projects.ReadSummaryAsync(f.Client.ClientId, f.Project.ProjectSessionId))!;
        Assert.Equal(before.ProjectRevision, after.ProjectRevision); Assert.Equal(before.IsDirty, after.IsDirty);
        Assert.Equal(before.CanUndo, after.CanUndo); Assert.Equal(before.CanRedo, after.CanRedo);
        var replacement = input with { PreviewId = preview.PreviewId, ExpectedPreviewGeneration = ready.Session.PreviewGeneration,
            Edits = [new SetCanvasSizeCommandDto(888, 555)], Metadata = f.Metadata() };
        using var replaced = await f.Http.PostAsJsonAsync(path, replacement, Json);
        Assert.Equal(HttpStatusCode.Created, replaced.StatusCode);
        var next = (await replaced.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        Assert.Equal(preview.PreviewId, next.PreviewId); Assert.NotEqual(preview.Source.SourceRevision, next.Source.SourceRevision);
        Assert.NotEqual(ready.Session.PreviewGeneration, next.PreviewGeneration);
        using var stale = await f.Http.PostAsJsonAsync(path, replacement with { Metadata = f.Metadata() }, Json);
        await Error(stale, HttpStatusCode.Conflict, BackendErrorCode.PreviewGenerationConflict);
        using var oldFrame = await f.Http.PostAsJsonAsync($"/api/v1/preview-sessions/{next.PreviewId}/frame-requests",
            f.FrameRequest(ready.Session, 2, 0), Json);
        await Error(oldFrame, HttpStatusCode.Conflict, BackendErrorCode.PreviewGenerationConflict);
        using var close = await f.Http.PostAsJsonAsync($"/api/v1/preview-sessions/{next.PreviewId}/close",
            new ClosePreviewSessionRequest(next.PreviewGeneration, next.Source.SourceRevision, f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.OK, close.StatusCode); Assert.Equal(0, f.Previews.Count);
    }
}
