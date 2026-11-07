using System.Net;
using System.Net.Http.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task 按帧开启检查时帧元数据带投影地址且未开启的帧返回不可用()
    {
        await using Fixture fixture = await Fixture.Create();
        ProjectSession opened = await fixture.OpenNoAudioProject();
        using HttpResponseMessage create = await fixture.Http.PostAsJsonAsync("/api/v1/preview-sessions",
            new PreviewSessionRequest(new("project", opened.ProjectSessionId, "0"), new(1, 1), Metadata: fixture.Metadata()), Json);
        PreviewSessionResponse preview = (await create.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        string route = $"/api/v1/preview-sessions/{preview.PreviewId}/frame-requests";

        using (HttpResponseMessage accepted = await fixture.Http.PostAsJsonAsync(route,
            new PreviewFrameCommandRequest("0", preview.PreviewGeneration, "1", "0", "playback", fixture.Metadata(), Inspect: true), Json))
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        (await fixture.Factory.NextRender()).Release.SetResult();
        PreviewFrameMetadataDto inspected = (await fixture.WaitReady(preview.PreviewId)).Session.LatestFrame!;
        Assert.Equal("/api/v1/preview-frames/" + inspected.FrameId + "/inspection", inspected.InspectionUrl);
        PreviewFrameInspectionResponse body = (await fixture.Http.GetFromJsonAsync<PreviewFrameInspectionResponse>(inspected.InspectionUrl, Json))!;
        Assert.Equal((inspected.FrameId, inspected.PreviewGeneration, inspected.SourceRevision, inspected.TimeUs),
            (body.FrameId, body.PreviewGeneration, body.SourceRevision, body.TimeUs));
        Assert.Equal(new(1920, 1080), body.Inspection.Canvas);
        Assert.Equal(inspected.Content.SizeBytes, body.Host.PngBytes);
        Assert.Equal(40.125, body.Inspection.Cost.SessionOpenMs);

        using (HttpResponseMessage accepted = await fixture.Http.PostAsJsonAsync(route,
            new PreviewFrameCommandRequest("0", preview.PreviewGeneration, "2", "1", "playback", fixture.Metadata()), Json))
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        (await fixture.Factory.NextRender()).Release.SetResult();
        PreviewFrameMetadataDto plain = await WaitFrame(fixture, preview.PreviewId, "2");
        Assert.Null(plain.InspectionUrl);
        using HttpResponseMessage unavailable = await fixture.Http.GetAsync($"/api/v1/preview-frames/{plain.FrameId}/inspection");
        await Error(unavailable, HttpStatusCode.NotFound, BackendErrorCode.PreviewInspectionUnavailable);
        using HttpResponseMessage missing = await fixture.Http.GetAsync("/api/v1/preview-frames/frame-missing/inspection");
        await Error(missing, HttpStatusCode.NotFound, BackendErrorCode.PreviewFrameNotFound);
    }

    private static async Task<PreviewFrameMetadataDto> WaitFrame(Fixture fixture, string previewId, string requestSeq)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            PreviewSessionStateResponse state = (await fixture.Http.GetFromJsonAsync<PreviewSessionStateResponse>(
                $"/api/v1/preview-sessions/{previewId}", Json))!;
            if (state.Session.LatestFrame is { } frame && frame.RequestSeq == requestSeq) return frame;
            await Task.Delay(25);
        }
        throw new TimeoutException("预览帧没有按时发布。");
    }

    private static RenderFrameInspection InspectionSample() => new(new(1920, 1080), false, [], [], [], [], [],
        new(1, 0.5, 0, 0, 0, 0, 0.25, 0, 0, 0, 0, 0, 0, 0, 0, [], 0, new(0, 0, 0, 0, 0), 40.125), new(0, 0, 0, 0, 0));
}
