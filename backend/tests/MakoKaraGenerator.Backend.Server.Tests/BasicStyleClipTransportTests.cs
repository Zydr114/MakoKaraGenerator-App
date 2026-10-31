using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task 真实HTTP完整片段成员扩缩拆合回执重试单次历史及静态解释()
    {
        await using Fixture f = await Fixture.Create();
        var workspace = (ProjectWorkspace)typeof(ProjectSession).GetProperty("Workspace", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Project)!;
        var document = LyricsDocument.Create(Enumerable.Range(0, 4).Select(index => LyricLine.Create("member/" + index, "字",
            new TimeRange(index * 1000000L, (index + 1) * 1000000L), TimingStatus.Imported,
            [LyricToken.Create("glyph/" + index, "字", new TimeRange(index * 1000000L, (index + 1) * 1000000L), TimingStatus.Imported)])));
        var initial = workspace.Project with { Lyrics = document };
        workspace.Replace(initial with { LyricPresentation = initial.LyricPresentation.WithDefaultPrograms(document) });
        string path = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}";
        async Task<ProjectCommandResponse> Send(string revision, ProjectCommandDto command)
        {
            var metadata = f.Metadata();
            using var response = await f.Http.PostAsJsonAsync(path + "/commands", new ProjectCommandRequest(revision, command,
                metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc), Json);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        }
        var created = await Send("0", new CreateOrchestrationClipCommandDto("primary", "clip/transport", null, ["member/1", "member/2"], LyricSceneIds.Regular("primary")));
        var expanded = await Send("1", new SetOrchestrationClipMembersCommandDto("primary", "clip/transport", ["member/0", "member/1", "member/2", "member/3"]));
        Assert.Equal("2", expanded.ProjectRevision); Assert.Equal(4, Assert.Single(expanded.ClipMembersResult!.Clips).LineIds.Length);
        var split = await Send("2", new SplitOrchestrationClipCommandDto("primary", "clip/transport", "member/2", "clip/right"));
        Assert.Equal("3", split.ProjectRevision); Assert.Equal(2, split.ClipMembersResult!.Clips.Length);
        Assert.All(split.ClipMembersResult.Clips, clip => Assert.NotNull(clip.ScheduleContext));
        var model = (await f.Http.GetFromJsonAsync<ProjectOrchestrationResponse>(path + "/orchestration?expectedRevision=3", Json))!;
        Assert.Equal(2, model.Programs.Single(item => item.BranchId == "primary").Clips!.Length);
        string query = "/resolved-style?expectedRevision=3&branchId=primary&lineId=member%2F2&tokenId=glyph%2F2&clipId=clip%2Fright";
        var style = (await f.Http.GetFromJsonAsync<ProjectResolvedStyleResponse>(path + query, Json))!;
        Assert.Equal("clip/right", style.Context.ClipId); Assert.Equal("glyph/2", style.Context.TokenId);
        Assert.Equal("staticPresetHierarchy", style.Resolution); Assert.False(style.FinalPixelAppearance);
        Assert.Equal(21, style.Sources.Length); Assert.Equal("container", style.Context.ContextKind);
        var merged = await Send("3", new MergeOrchestrationClipsCommandDto("primary", ["clip/transport", "clip/right"], "clip/transport"));
        Assert.Equal("4", merged.ProjectRevision); Assert.Equal(["clip/right"], merged.ClipMembersResult!.RemovedClipIds);
        Assert.Equal(4, workspace.History.UndoCount);
        await Send("4", new("history.undo"));
        var noop = await Send("5", new SetOrchestrationClipMembersCommandDto("primary", "clip/transport", ["member/0", "member/1"]));
        Assert.False(noop.Changed); Assert.True(noop.CanRedo); Assert.Equal("5", noop.ProjectRevision);
        await Send("5", new("history.redo")); Assert.Equal(4, workspace.History.UndoCount);
        using var stale = await f.Http.GetAsync(path + query); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task 真实HTTP静态解释严格查询目标上下文认证及跨client保护()
    {
        await using Fixture f = await Fixture.Create();
        var workspace = (ProjectWorkspace)typeof(ProjectSession).GetProperty("Workspace", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Project)!;
        string line = workspace.Project.Lyrics.Lines[0].Id;
        string path = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/resolved-style";
        string query = "?expectedRevision=0&branchId=primary&lineId=" + Uri.EscapeDataString(line);
        using var response = await f.Http.GetAsync(path + query); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("context").GetProperty("tokenId").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("context").GetProperty("clipId").ValueKind);
        foreach (string invalid in new[] { "?expectedRevision=0&branchId=primary", query + "&unknown=1", query + "&lineId=" + Uri.EscapeDataString(line),
            query + "&tokenId=", query + "&nodeId=arbitrary", query + "&clipId=wrong" })
        { using var bad = await f.Http.GetAsync(path + invalid); Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode); }
        using var absent = await f.Http.GetAsync(path + "?expectedRevision=0&branchId=primary&lineId=absent"); Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        var other = f.Clients.Register(f._security.BootstrapToken, new()).Response!;
        using var request = new HttpRequestMessage(HttpMethod.Get, path + query);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken); request.Headers.Add("X-Mako-Client-Id", other.ClientId);
        using var denied = await f.Http.SendAsync(request); Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        f.Http.DefaultRequestHeaders.Authorization = null;
        using var unauthorized = await f.Http.GetAsync(path + query); Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(0, workspace.History.UndoCount);
    }
}
