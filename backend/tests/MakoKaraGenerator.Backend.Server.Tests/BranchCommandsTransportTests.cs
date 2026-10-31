using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Lyrics;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task HTTP来源新增配置删除返回真实选择且语言显式空清除()
    {
        await using Fixture f = await Fixture.Create();
        using HttpResponseMessage add = await PostRawEditing(f, "0", """{"type":"lyrics.addTranslationBranch","name":" 翻译 ","language":"zh-CN"}""");
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        var added = (await add.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        string branch = added.BranchResult!.SelectedBranchId;
        string configure = JsonSerializer.Serialize(new { type = "lyrics.configureBranch", branchId = branch, name = " 原名 ", language = (string?)null,
            role = "annotation", timingMode = "none" }, Json);
        using HttpResponseMessage configured = await PostRawEditing(f, "1", configure);
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        string configuredBody = await configured.Content.ReadAsStringAsync();
        Assert.DoesNotContain("branchResult", configuredBody, StringComparison.Ordinal);
        var sources = (await f.Http.GetFromJsonAsync<ProjectLyricBranchesResponse>(
            $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/lyrics/branches?expectedRevision=2", Json))!;
        var source = sources.Branches.Single(item => item.Id == branch);
        Assert.Null(source.Language); Assert.Equal(" 原名 ", source.Name); Assert.Equal(ProjectLyricBranchRole.Annotation, source.Role);
        using HttpResponseMessage removed = await PostRawEditing(f, "2", JsonSerializer.Serialize(new { type = "lyrics.removeBranch", branchId = branch }, Json));
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal("primary", (await removed.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!.BranchResult!.SelectedBranchId);
    }

    [Theory]
    [InlineData("""{"type":"lyrics.addTranslationBranch"}""")]
    [InlineData("""{"type":"lyrics.configureBranch","branchId":"primary","name":"主","role":"primary","timingMode":"independent"}""")]
    [InlineData("""{"type":"lyrics.configureBranch","branchId":"primary","name":"主","language":null,"role":0,"timingMode":"independent"}""")]
    [InlineData("""{"type":"lyrics.configureBranch","branchId":"primary","name":"主","language":null,"role":"0","timingMode":"independent"}""")]
    [InlineData("""{"type":"lyrics.configureBranch","branchId":"primary","name":"主","language":null,"role":"unknown","timingMode":"independent"}""")]
    [InlineData("""{"type":"lyrics.configureBranch","branchId":"primary","name":"主","language":null,"role":"primary","timingMode":1}""")]
    [InlineData("""{"type":"lyrics.removeBranch","branchId":"primary","name":"foreign"}""")]
    [InlineData("""{"type":"lyrics.linkPrimaryLine","branchId":"translation","lineId":"line"}""")]
    [InlineData("""{"type":"lyrics.linkPrimaryLine","branchId":"translation","lineId":"line","primaryLineId":null,"primaryLineId":null}""")]
    public async Task HTTP来源类型拒绝遗漏显式空字段数字枚举未知重复和外来字段(string body)
    {
        await using Fixture f = await Fixture.Create();
        using HttpResponseMessage response = await PostRawEditing(f, "0", body);
        await Error(response, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        Assert.Equal("0", (await f.Projects.ReadSummaryAsync(f.Client.ClientId, f.Project.ProjectSessionId))!.ProjectRevision);
    }

    [Fact]
    public async Task HTTP删除主来源有稳定业务原因且超大新名称明确413()
    {
        await using Fixture f = await Fixture.Create();
        using HttpResponseMessage remove = await PostRawEditing(f, "0", """{"type":"lyrics.removeBranch","branchId":"primary"}""");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, remove.StatusCode);
        var error = (await remove.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!;
        Assert.Equal("PrimaryBranchProtected", error.FailureCode); Assert.Equal("command.branchId", error.Field);
        string huge = JsonSerializer.Serialize(new { type = "lyrics.addTranslationBranch", name = new string('汉', 180000) }, Json);
        using HttpResponseMessage oversized = await PostRawEditing(f, "0", huge);
        await Error(oversized, HttpStatusCode.RequestEntityTooLarge, BackendErrorCode.ResourceTooLarge);
        var summary = (await f.Projects.ReadSummaryAsync(f.Client.ClientId, f.Project.ProjectSessionId))!;
        Assert.Equal("0", summary.ProjectRevision); Assert.False(summary.CanUndo);
    }

    [Fact]
    public async Task HTTP关联与显式空解除保留FollowPrimary原时间并可无变化()
    {
        await using Fixture f = await Fixture.Create();
        ProjectWorkspace workspace = (ProjectWorkspace)typeof(ProjectSession).GetProperty("Workspace", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Project)!;
        var original = workspace.Project;
        string primaryLine = original.Lyrics.Lines[0].Id;
        typeof(ProjectWorkspace).GetProperty("Project")!.SetValue(workspace, original with
        {
            Lyrics = original.Lyrics with { Branches = original.Lyrics.Branches.Add(LyricBranch.Create("translation", "翻译", LyricBranchRole.Translation,
                [LyricLine.Create("translated", "译文")], timingMode: LyricBranchTimingMode.FollowPrimary)) },
        });
        string link = JsonSerializer.Serialize(new { type = "lyrics.linkPrimaryLine", branchId = "translation", lineId = "translated", primaryLineId = primaryLine }, Json);
        using HttpResponseMessage linked = await PostRawEditing(f, "0", link);
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        string path = $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/lyrics/window?branchId=translation&expectedRevision=1";
        var window = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(path, Json))!;
        Assert.Equal(primaryLine, window.Lines[0].PrimaryLineId); Assert.Null(window.Lines[0].Time.StartUs);
        using HttpResponseMessage unlinked = await PostRawEditing(f, "1", """{"type":"lyrics.linkPrimaryLine","branchId":"translation","lineId":"translated","primaryLineId":null}""");
        Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        using HttpResponseMessage noop = await PostRawEditing(f, "2", """{"type":"lyrics.linkPrimaryLine","branchId":"translation","lineId":"translated","primaryLineId":null}""");
        Assert.Equal(HttpStatusCode.OK, noop.StatusCode);
        var result = (await noop.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        Assert.False(result.Changed); Assert.Null(result.BranchResult); Assert.Equal("2", result.ProjectRevision);
    }
}
