using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task HTTP背景上传编辑撤销重做未保存图片读取和保存重开资源授权一致()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using Fixture f = await Fixture.Create();
        string route = "/api/v1/project-sessions/" + f.Project.ProjectSessionId;
        await BasicMediaCommand(f, "0", new ClearProjectAudioCommandDto());
        await BasicMediaCommand(f, "1", new SetBackgroundFitCommandDto("original"));
        byte[] imageBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==");
        StoredAsset image = await BackgroundUpload(f, "background.png", "image/png", imageBytes);
        await BasicMediaCommand(f, "2", new SetBackgroundAssetCommandDto(image.AssetId, "image"));
        await BasicMediaCommand(f, "3", new SetBackgroundPlaybackCommandDto("freezeLastFrame"));
        ProjectSettingsResponse settings = (await f.Http.GetFromJsonAsync<ProjectSettingsResponse>(route + "/settings?expectedRevision=4", Json))!;
        Assert.Equal(new("image", "original", "freezeLastFrame", "media:" + image.Sha256), settings.Background);
        var captured = await f.Project.CapturePreviewInputAsync(f.Client.ClientId, "4", f.Factory, null, true);
        Assert.Null(captured.Error);
        using (captured.ImageSource!) Assert.Equal(imageBytes, (await captured.ImageSource!.ReadAsync()).Response);
        Assert.False((await BasicMediaCommand(f, "4", new SetBackgroundAssetCommandDto(image.AssetId, "image"))).Changed);
        await BasicMediaCommand(f, "4", new ClearBackgroundCommandDto());
        Assert.Contains("background", (await BasicMediaCommand(f, "5", new ProjectCommandDto("history.undo"))).ChangeSet.Areas);
        Assert.True((await BasicMediaCommand(f, "6", new SetBackgroundAssetCommandDto(image.AssetId, "image"))).CanRedo);
        Assert.False((await BasicMediaCommand(f, "6", new SetBackgroundAssetCommandDto(image.AssetId, "image"))).Changed);
        await BasicMediaCommand(f, "6", new ProjectCommandDto("history.redo"));
        await BasicMediaCommand(f, "7", new ProjectCommandDto("history.undo"));
        string videoPath = f._directory.FilePath("background.mov");
        await BasicFfmpeg(["-v", "error", "-f", "lavfi", "-i", "color=c=blue:s=16x16:r=1:d=1", "-an", "-c:v", "mpeg4", videoPath]);
        byte[] videoBytes = await File.ReadAllBytesAsync(videoPath);
        StoredAsset video = await BackgroundUpload(f, "background.mov", "video/quicktime", videoBytes);
        await BasicMediaCommand(f, "8", new SetBackgroundAssetCommandDto(video.AssetId, "video", "cover"));
        settings = (await f.Http.GetFromJsonAsync<ProjectSettingsResponse>(route + "/settings?expectedRevision=9", Json))!;
        Assert.Equal(new("video", "cover", "freezeLastFrame", "media:" + video.Sha256), settings.Background);
        ProjectResourcesResponse resources = (await f.Http.GetFromJsonAsync<ProjectResourcesResponse>(route + "/resources?expectedRevision=9", Json))!;
        ProjectResourceDto background = Assert.Single(resources.Resources);
        Assert.Equal(video.AssetId, background.AssetId); Assert.Equal("prepared", background.PreparationStatus);
        Assert.Equal(videoBytes, await f.Http.GetByteArrayAsync(background.ContentUrl));
        f.Clock.Advance(10); f.Resources.CollectExpired();
        Assert.True(f.Resources.TryGetAuthorized(f.Client.ClientId, image.AssetId, out _));
        Assert.True(f.Resources.TryGetAuthorized(f.Client.ClientId, video.AssetId, out _));
        FileHandleResponse handle = (await f.Projects.CreateFileHandleAsync(f.Client.ClientId, new("preview-test-root", "background-save", Metadata: f.Metadata()))).Response!;
        using HttpResponseMessage saved = await f.Http.PostAsJsonAsync(route + "/save-jobs", new SaveProjectRequest("9", handle.HandleId, f.Metadata()), Json);
        await BasicMediaTerminal(f, (await saved.Content.ReadFromJsonAsync<JobResponse>(Json))!);
        using HttpResponseMessage closed = await f.Http.PostAsJsonAsync(route + "/close", new CloseProjectRequest("9", Metadata: f.Metadata()), Json);
        await BasicMediaTerminal(f, (await closed.Content.ReadFromJsonAsync<JobResponse>(Json))!);
        f.Clock.Advance(10); f.Resources.CollectExpired();
        Assert.False(f.Resources.TryGetAuthorized(f.Client.ClientId, image.AssetId, out _));
        Assert.False(f.Resources.TryGetAuthorized(f.Client.ClientId, video.AssetId, out _));
        using HttpResponseMessage opened = await f.Http.PostAsJsonAsync("/api/v1/project-sessions/open-jobs", new OpenProjectSessionRequest(handle.HandleId, Metadata: f.Metadata()), Json);
        JobResponse reopened = await BasicMediaTerminal(f, (await opened.Content.ReadFromJsonAsync<JobResponse>(Json))!);
        string reopenedRoute = "/api/v1/project-sessions/" + reopened.ProjectSessionId;
        settings = (await f.Http.GetFromJsonAsync<ProjectSettingsResponse>(reopenedRoute + "/settings?expectedRevision=0", Json))!;
        Assert.Equal(new("video", "cover", "freezeLastFrame", "media:" + video.Sha256), settings.Background);
        resources = (await f.Http.GetFromJsonAsync<ProjectResourcesResponse>(reopenedRoute + "/resources?expectedRevision=0", Json))!;
        background = Assert.Single(resources.Resources); Assert.Equal("available", background.Status);
        Assert.Equal(videoBytes, await f.Http.GetByteArrayAsync(background.ContentUrl));
        using HttpResponseMessage noOp = await f.Http.PostAsJsonAsync(reopenedRoute + "/commands", BasicMediaRequest(f, "0", new SetBackgroundAssetCommandDto(background.AssetId, "video")), Json);
        Assert.Equal(HttpStatusCode.OK, noOp.StatusCode);
        Assert.False((await noOp.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!.Changed);
    }

    [Fact]
    public async Task HTTP背景命令严格字段和跨client素材拒绝均不改变工程()
    {
        await using Fixture f = await Fixture.Create();
        foreach (string command in new[] {
            "{\"type\":\"composition.clearBackground\",\"assetId\":null}",
            "{\"type\":\"composition.setBackgroundFit\",\"fit\":1}",
            "{\"type\":\"composition.setBackgroundFit\",\"fit\":\"1\"}",
            "{\"type\":\"composition.setBackgroundPlayback\",\"playback\":\"FreezeLastFrame\"}",
            "{\"type\":\"composition.setBackgroundAsset\",\"assetId\":\"x\",\"kind\":\"image\",\"fit\":null}" })
        {
            using HttpResponseMessage invalid = await PostRawEditing(f, "0", command);
            await Error(invalid, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        }
        StoredAsset foreign = await f.Resources.UploadAsync("other", "x.png", "image/png", new MemoryStream([1, 2, 3]));
        using HttpResponseMessage denied = await PostRawEditing(f, "0", $$"""{"type":"composition.setBackgroundAsset","assetId":"{{foreign.AssetId}}","kind":"image"}""");
        await Error(denied, HttpStatusCode.Unauthorized, BackendErrorCode.ResourceUnauthorized);
        ProjectSettingsResponse state = (await f.Http.GetFromJsonAsync<ProjectSettingsResponse>("/api/v1/project-sessions/" + f.Project.ProjectSessionId + "/settings?expectedRevision=0", Json))!;
        Assert.Equal("0", state.ProjectRevision); Assert.Equal("color", state.Background!.Kind);
    }

    private static async Task<StoredAsset> BackgroundUpload(Fixture f, string name, string mime, byte[] bytes)
    {
        using ByteArrayContent content = new(bytes); content.Headers.ContentType = new(mime);
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/assets") { Content = content };
        RequestMetadataDto meta = f.Metadata();
        request.Headers.Add("X-Mako-Asset-Name", name); request.Headers.Add("X-Mako-Request-Id", meta.RequestId);
        request.Headers.Add("X-Mako-Host-Instance-Id", meta.HostInstanceId);
        request.Headers.Add("X-Mako-Valid-Until-Utc", meta.ValidUntilUtc!.Value.ToString("O", CultureInfo.InvariantCulture));
        using HttpResponseMessage response = await f.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        AssetResponse asset = (await response.Content.ReadFromJsonAsync<AssetResponse>(Json))!;
        return f.Resources.GetAuthorized(f.Client.ClientId, asset.AssetId);
    }
}
