using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task 真实HTTP本句微调读取清除撤销重做精确修订与严格数值()
    {
        await using Fixture f = await Fixture.Create();
        string path = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}";
        var lyrics = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(path + "/lyrics/window?branchId=primary&expectedRevision=0", Json))!;
        string line = Assert.Single(lyrics.Lines).Id;
        string read = path + "/orchestration/line-local-transform?branchId=primary&lineId=" + Uri.EscapeDataString(line) + "&expectedRevision=";
        async Task<ProjectCommandResponse> Send(string revision, ProjectCommandDto command)
        {
            RequestMetadataDto metadata = f.Metadata();
            using var response = await f.Http.PostAsJsonAsync(path + "/commands",
                new ProjectCommandRequest(revision, command, metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc), Json);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        }
        using (var initial = JsonDocument.Parse(await f.Http.GetStringAsync(read + "0")))
            Assert.Equal(JsonValueKind.Null, initial.RootElement.GetProperty("transform").ValueKind);
        var transform = new ProjectLineLocalTransformDto(.2, -.1, 20, 1.25);
        var changed = await Send("0", new SetLineLocalTransformCommandDto("primary", line, transform));
        Assert.True(changed.Changed); Assert.Equal("1", changed.ProjectRevision); Assert.Equal(transform, changed.LineLocalTransformResult!.Transform);
        Assert.Equal(transform, (await f.Http.GetFromJsonAsync<ProjectLineLocalTransformResponse>(read + "1", Json))!.Transform);
        using (var stale = await f.Http.GetAsync(read + "0")) await Error(stale, HttpStatusCode.Conflict, BackendErrorCode.RevisionConflict);
        await Send("1", new("history.undo"));
        var noop = await Send("2", new SetLineLocalTransformCommandDto("primary", line, null));
        Assert.False(noop.Changed); Assert.True(noop.CanRedo); Assert.Null(noop.LineLocalTransformResult!.Transform);
        await Send("2", new("history.redo"));
        Assert.Equal(transform, (await f.Http.GetFromJsonAsync<ProjectLineLocalTransformResponse>(read + "3", Json))!.Transform);
        var cleared = await Send("3", new SetLineLocalTransformCommandDto("primary", line, null));
        Assert.True(cleared.Changed); Assert.Equal("4", cleared.ProjectRevision);
        using (var clearedJson = JsonDocument.Parse(await f.Http.GetStringAsync(read + "4")))
            Assert.Equal(JsonValueKind.Null, clearedJson.RootElement.GetProperty("transform").ValueKind);
        var after = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(path + "/lyrics/window?branchId=primary&expectedRevision=4", Json))!;
        Assert.Equal(JsonSerializer.Serialize(lyrics.Lines, Json), JsonSerializer.Serialize(after.Lines, Json));
        foreach (string raw in new[]
        {
            "{\"type\":\"orchestration.setLineLocalTransform\",\"branchId\":\"primary\",\"lineId\":" + JsonSerializer.Serialize(line) + "}",
            "{\"type\":\"orchestration.setLineLocalTransform\",\"branchId\":\"primary\",\"lineId\":" + JsonSerializer.Serialize(line) + ",\"transform\":{\"offsetXRatio\":0,\"offsetYRatio\":0,\"rotationDegrees\":0,\"scale\":1e400}}",
            "{\"type\":\"orchestration.setLineLocalTransform\",\"branchId\":\"primary\",\"lineId\":" + JsonSerializer.Serialize(line) + ",\"transform\":{\"offsetXRatio\":0,\"offsetYRatio\":0,\"rotationDegrees\":0,\"scale\":0}}",
        })
        {
            var metadata = f.Metadata();
            string request = JsonSerializer.Serialize(new { expectedRevision = "4", command = JsonSerializer.Deserialize<JsonElement>(raw),
                requestId = metadata.RequestId, hostInstanceId = metadata.HostInstanceId, validUntilUtc = metadata.ValidUntilUtc }, Json);
            using var rejected = await f.Http.PostAsync(path + "/commands", new StringContent(request, Encoding.UTF8, "application/json"));
            await Error(rejected, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        }
        var targetMetadata = f.Metadata();
        using var wrongTarget = await f.Http.PostAsJsonAsync(path + "/commands", new ProjectCommandRequest("4",
            new SetLineLocalTransformCommandDto("missing", line, null), targetMetadata.RequestId, targetMetadata.HostInstanceId, targetMetadata.ValidUntilUtc), Json);
        await Error(wrongTarget, HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandRejected);
        using var duplicate = await f.Http.GetAsync(read + "4&lineId=duplicate");
        await Error(duplicate, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
    }
}
