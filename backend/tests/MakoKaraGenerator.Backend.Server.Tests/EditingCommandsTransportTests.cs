using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task HTTP六个时间命令按严格wire执行并查询撤销重做保持精确微秒()
    {
        await using Fixture f = await Fixture.Create();
        string id = f.Project.ProjectSessionId;
        string lyricsRoute = $"/api/v1/project-sessions/{id}/lyrics/lines?branchId=primary";
        LyricsLinesResponse original = (await f.Http.GetFromJsonAsync<LyricsLinesResponse>(lyricsRoute, Json))!;
        LyricLineDto line = Assert.Single(original.Lines); LyricTokenDto token = line.Tokens[0];
        string target = JsonSerializer.Serialize(new LyricTimingTargetDto("primary", line.Id, token.Id), Json);
        string lineTarget = JsonSerializer.Serialize(new LyricTimingTargetDto("primary", line.Id), Json);
        using HttpResponseMessage resized = await PostRawEditing(f, "0", $$"""{"type":"lyrics.setTimeRange","target":{{lineTarget}},"startUs":"0","endUs":"2000000"}""");
        Assert.Equal(HttpStatusCode.OK, resized.StatusCode);
        string[] commands = [
            $$"""{"type":"lyrics.stampTokenStart","target":{{target}},"playheadUs":"100001"}""",
            $$"""{"type":"lyrics.stampTokenEnd","target":{{target}},"playheadUs":"150007"}""",
            $$"""{"type":"lyrics.nudgeTokenTime","target":{{target}},"frameDelta":1}""",
            $$"""{"type":"lyrics.clearTokenTime","target":{{target}}}""",
            $$"""{"type":"lyrics.setTokenTimes","changes":[{"target":{{target}},"startUs":"200001","endUs":"300007"}]}""",
            $$"""{"type":"lyrics.shiftLine","target":{{lineTarget}},"deltaUs":"123457"}""",
        ];
        LyricTimeDto[] expected = [new("100001", "133334"), new("100001", "150007"), new("133334", "183340"), new(null, null), new("200001", "300007"), new("323458", "423464")];
        for (int index = 0; index < commands.Length; index++)
        {
            using HttpResponseMessage response = await PostRawEditing(f, (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), commands[index]);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            ProjectCommandResponse accepted = (await response.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
            Assert.True(accepted.Changed); Assert.Equal((index + 2).ToString(System.Globalization.CultureInfo.InvariantCulture), accepted.ProjectRevision);
            if (index < 2) Assert.Equal(index == 0, accepted.TimingResult!.EstimatedPlaceholder);
            else Assert.Null(accepted.TimingResult);
            LyricLineDto current = Assert.Single((await f.Http.GetFromJsonAsync<LyricsLinesResponse>(lyricsRoute, Json))!.Lines);
            Assert.Equal(expected[index], current.Tokens[0].Time);
        }
        ProjectHistoryResponse history = (await f.Http.GetFromJsonAsync<ProjectHistoryResponse>($"/api/v1/project-sessions/{id}/history", Json))!;
        Assert.Equal(7, history.UndoCount);
        using HttpResponseMessage undo = await PostRawEditing(f, "7", "{\"type\":\"history.undo\",\"target\":null,\"startUs\":null,\"endUs\":null,\"analysisJobId\":null}");
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        Assert.Equal(expected[4], Assert.Single((await f.Http.GetFromJsonAsync<LyricsLinesResponse>(lyricsRoute, Json))!.Lines).Tokens[0].Time);
        using HttpResponseMessage redo = await PostRawEditing(f, "8", "{\"type\":\"history.redo\"}");
        Assert.Equal(HttpStatusCode.OK, redo.StatusCode);
        Assert.Equal(expected[5], Assert.Single((await f.Http.GetFromJsonAsync<LyricsLinesResponse>(lyricsRoute, Json))!.Lines).Tokens[0].Time);
        BackendCapabilitiesResponse capabilities = (await f.Http.GetFromJsonAsync<BackendCapabilitiesResponse>("/api/v1/capabilities", Json))!;
        foreach (string type in new[] { "stampTokenStart", "stampTokenEnd", "nudgeTokenTime", "clearTokenTime", "setTokenTimes", "shiftLine" })
            Assert.Contains("project-sessions.commands.lyrics." + type, capabilities.Features);
    }

    [Theory]
    [InlineData("{\"type\":\"lyrics.clearTokenTime\",\"analysisJobId\":null}", HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest)]
    [InlineData("{\"type\":\"lyrics.stampTokenStart\",\"playheadUs\":1}", HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest)]
    [InlineData("{\"type\":\"lyrics.nudgeTokenTime\",\"frameDelta\":\"1\"}", HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest)]
    [InlineData("{\"type\":\"lyrics.nudgeTokenTime\",\"frameDelta\":2147483648}", HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest)]
    [InlineData("{\"type\":\"lyrics.shiftLine\",\"deltaUs\":\"1\",\"extra\":true}", HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest)]
    [InlineData("{\"type\":\"lyrics.setTokenTimes\",\"changes\":[{\"extra\":1}]}", HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest)]
    [InlineData("{\"type\":\"lyrics.stampTokenEnd\",\"type\":\"lyrics.stampTokenEnd\"}", HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest)]
    [InlineData("{\"type\":\"lyrics.clearTokenTime\"}", HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest)]
    [InlineData("{\"type\":\"lyrics.setTokenTimes\",\"changes\":[]}", HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest)]
    [InlineData("{\"type\":\"unknown.command\",\"target\":null,\"startUs\":null,\"endUs\":null,\"analysisJobId\":null}", HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandNotSupported)]
    public async Task HTTP新命令错型跨类型字段及未知type返回稳定拒绝而非500(string command, HttpStatusCode status, BackendErrorCode code)
    {
        await using Fixture f = await Fixture.Create();
        using HttpResponseMessage response = await PostRawEditing(f, "0", command);
        await Error(response, status, code);
        ProjectHistoryResponse history = (await f.Http.GetFromJsonAsync<ProjectHistoryResponse>($"/api/v1/project-sessions/{f.Project.ProjectSessionId}/history", Json))!;
        Assert.Equal("0", history.ProjectRevision); Assert.Equal(0, history.UndoCount);
    }

    [Fact]
    public async Task HTTP新命令回执保派生字段同语义规范化重试且改值不能复用()
    {
        await using Fixture f = await Fixture.Create();
        LyricLineDto line = Assert.Single((await f.Projects.ReadLyricsAsync(f.Client.ClientId, f.Project.ProjectSessionId, "primary"))!.Lines);
        string target = JsonSerializer.Serialize(new LyricTimingTargetDto("primary", line.Id, line.Tokens[0].Id), Json);
        string requestId = Guid.NewGuid().ToString("N");
        using HttpResponseMessage first = await PostRawEditing(f, "0", $$"""{"type":"lyrics.stampTokenStart","target":{{target}},"playheadUs":"100001"}""", requestId);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        ProjectCommandResponse accepted = (await first.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        using HttpResponseMessage retry = await PostRawEditing(f, "00", $$"""{"type":"lyrics.stampTokenStart","target":{{target}},"playheadUs":"+0100001"}""", requestId);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(JsonSerializer.Serialize(accepted, Json), JsonSerializer.Serialize(await retry.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json), Json));
        using HttpResponseMessage changed = await PostRawEditing(f, "0", $$"""{"type":"lyrics.stampTokenStart","target":{{target}},"playheadUs":"100002"}""", requestId);
        await Error(changed, HttpStatusCode.Conflict, BackendErrorCode.RequestIdReuse);
        using HttpResponseMessage stale = await PostRawEditing(f, "0", $$"""{"type":"lyrics.clearTokenTime","target":{{target}}}""");
        await Error(stale, HttpStatusCode.Conflict, BackendErrorCode.RevisionConflict);
    }

    [Fact]
    public async Task HTTP新领域拒绝包含稳定原因码而旧命令保持省略()
    {
        await using Fixture f = await Fixture.Create();
        LyricLineDto line = Assert.Single((await f.Projects.ReadLyricsAsync(f.Client.ClientId, f.Project.ProjectSessionId, "primary"))!.Lines);
        string target = JsonSerializer.Serialize(new LyricTimingTargetDto("primary", line.Id, line.Tokens[0].Id), Json);
        using HttpResponseMessage rejected = await PostRawEditing(f, "0", $$"""{"type":"lyrics.stampTokenEnd","target":{{target}},"playheadUs":"100001"}""");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        BackendErrorResponse error = (await rejected.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!;
        Assert.Equal(BackendErrorCode.CommandRejected, error.Code); Assert.Equal("Untimed", error.FailureCode);
        Assert.Equal("command.target", error.Field);
        string missingTarget = JsonSerializer.Serialize(new LyricTimingTargetDto("primary", line.Id, "missing"), Json);
        string lineTarget = JsonSerializer.Serialize(new LyricTimingTargetDto("primary", line.Id), Json);
        var cases = new (string Command, string FailureCode, string Field)[]
        {
            ($$"""{"type":"lyrics.stampTokenStart","target":{{target}},"playheadUs":"3000000"}""", "BeyondContentDuration", "command.playheadUs"),
            ($$"""{"type":"lyrics.clearTokenTime","target":{{missingTarget}}}""", "TargetStale", "command.target"),
            ($$"""{"type":"lyrics.shiftLine","target":{{lineTarget}},"deltaUs":"-1"}""", "DomainValidationFailed", "command.deltaUs"),
            ($$"""{"type":"lyrics.setTokenTimes","changes":[{"target":{{missingTarget}},"startUs":"1","endUs":"2"}]}""", "TargetStale", "command.changes"),
        };
        foreach ((string command, string failureCode, string field) in cases)
            await AssertRejection("0", command, failureCode, field);
        using HttpResponseMessage old = await PostRawEditing(f, "0", $$"""{"type":"lyrics.setTimeRange","target":{{target}},"startUs":"-1","endUs":"1000"}""");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, old.StatusCode);
        Assert.Equal("DomainValidationFailed", (await old.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!.Field);
        Assert.DoesNotContain("failureCode", await old.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using HttpResponseMessage seeded = await PostRawEditing(f, "0", $$"""{"type":"lyrics.setTimeRange","target":{{target}},"startUs":"100000","endUs":"200000"}""");
        Assert.Equal(HttpStatusCode.OK, seeded.StatusCode);
        await AssertRejection("1", $$"""{"type":"lyrics.nudgeTokenTime","target":{{target}},"frameDelta":-4}""", "NudgeOutOfBounds", "command.frameDelta");
        await AssertRejection("1", $$"""{"type":"lyrics.setTokenTimes","changes":[{"target":{{target}},"startUs":"-1","endUs":"1000"}]}""", "DomainValidationFailed", "command.changes");

        async Task AssertRejection(string revision, string command, string failureCode, string field)
        {
            using HttpResponseMessage response = await PostRawEditing(f, revision, command);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            BackendErrorResponse actual = (await response.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!;
            Assert.Equal(BackendErrorCode.CommandRejected, actual.Code);
            Assert.Equal(failureCode, actual.FailureCode);
            Assert.Equal(field, actual.Field);
            Assert.NotEqual(actual.FailureCode, actual.Field);
        }
    }

    private static Task<HttpResponseMessage> PostRawEditing(Fixture f, string revision, string command, string? requestId = null)
    {
        RequestMetadataDto metadata = f.Metadata();
        using JsonDocument parsed = JsonDocument.Parse(command);
        string body = JsonSerializer.Serialize(new { expectedRevision = revision, command = parsed.RootElement,
            requestId = requestId ?? metadata.RequestId, hostInstanceId = metadata.HostInstanceId, validUntilUtc = metadata.ValidUntilUtc }, Json);
        return f.Http.PostAsync($"/api/v1/project-sessions/{f.Project.ProjectSessionId}/commands", new StringContent(body, Encoding.UTF8, "application/json"));
    }
}
