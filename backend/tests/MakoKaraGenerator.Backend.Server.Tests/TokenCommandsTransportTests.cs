using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task HTTP五元素命令实际反馈读模型和一次历史保持一致()
    {
        await using Fixture f = await Fixture.Create(exportFixture: true);
        string session = f.Project.ProjectSessionId;
        ProjectLyricsWindowResponse initial = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(
            $"/api/v1/project-sessions/{session}/lyrics/window?branchId=primary&expectedRevision=0&limit=1", Json))!;
        string id = initial.Lines[0].Id;
        string lineTarget = JsonSerializer.Serialize(new LyricTimingTargetDto("primary", id), Json);
        using HttpResponseMessage prep = await PostRawEditing(f, "0", $$"""{"type":"lyrics.resplitLine","target":{{lineTarget}},"segments":["he","llo"],"estimateTimes":true}""");
        Assert.Equal(HttpStatusCode.OK, prep.StatusCode);
        ProjectCommandResponse prepared = (await prep.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        Assert.True(prepared.Changed); Assert.False(prepared.TokenResult!.Selection!.HasTokenSelection);
        ProjectLyricsWindowResponse split = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(
            $"/api/v1/project-sessions/{session}/lyrics/window?branchId=primary&expectedRevision=1&limit=1", Json))!;
        string tokenTarget = JsonSerializer.Serialize(new LyricTimingTargetDto("primary", id, split.Lines[0].Tokens[0].Id), Json);
        string[] commands = [
            $$"""{"type":"lyrics.splitToken","target":{{tokenTarget}}}""",
            $$"""{"type":"lyrics.mergeToken","target":{{tokenTarget}}}""",
            $$"""{"type":"lyrics.reorderToken","target":{{tokenTarget}},"destinationIndex":1,"reassignTimeSlots":true}""",
            "{\"type\":\"lyrics.resplitBranch\",\"branchId\":\"primary\",\"rule\":\"perTextElement\",\"estimateTimes\":false}",
        ];
        for (int index = 0; index < commands.Length; index++)
        {
            using HttpResponseMessage response = await PostRawEditing(f, (index + 1).ToString(CultureInfo.InvariantCulture), commands[index]);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            ProjectCommandResponse result = (await response.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
            Assert.True(result.Changed); Assert.True(result.ChangeSet.RequiresFullRefresh); Assert.NotNull(result.TokenResult);
            Assert.Null(result.Feedback); Assert.Empty(result.ChangeSet.TokenIds);
        }
        ProjectHistoryResponse history = (await f.Projects.ReadHistoryAsync(f.Client.ClientId, session))!;
        Assert.Equal(5, history.UndoCount); Assert.Equal("lyrics.split-rule", history.UndoName);
        using HttpResponseMessage undo = await PostRawEditing(f, "5", "{\"type\":\"history.undo\"}"); Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        using HttpResponseMessage redo = await PostRawEditing(f, "6", "{\"type\":\"history.redo\"}"); Assert.Equal(HttpStatusCode.OK, redo.StatusCode);
    }

    [Fact]
    public async Task HTTP段数资源拒绝优先于非字符串项而有界错型仍400且无回执写入()
    {
        await using Fixture f = await Fixture.Create();
        foreach (int count in new[] { 20001, 20000 })
        {
            string command = "{\"type\":\"lyrics.resplitLine\",\"segments\":[" + string.Join(',', Enumerable.Repeat("1", count)) + "],\"estimateTimes\":false}";
            using HttpResponseMessage response = await PostRawEditing(f, "0", command);
            BackendErrorCode expected = count > 20000 ? BackendErrorCode.ResourceTooLarge : BackendErrorCode.InvalidRequest;
            await Error(response, count > 20000 ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.BadRequest, expected);
        }
        ProjectHistoryResponse history = (await f.Projects.ReadHistoryAsync(f.Client.ClientId, f.Project.ProjectSessionId))!;
        Assert.Equal("0", history.ProjectRevision); Assert.Equal(0, history.UndoCount);
    }

    [Theory]
    [InlineData("{\"type\":\"lyrics.resplitLine\",\"estimateTimes\":false}")]
    [InlineData("{\"type\":\"lyrics.resplitLine\",\"segments\":[],\"estimateTimes\":null}")]
    [InlineData("{\"type\":\"lyrics.reorderToken\",\"destinationIndex\":0}")]
    [InlineData("{\"type\":\"lyrics.reorderToken\",\"destinationIndex\":\"0\",\"reassignTimeSlots\":false}")]
    [InlineData("{\"type\":\"lyrics.resplitBranch\",\"branchId\":\"primary\",\"rule\":0,\"estimateTimes\":true}")]
    [InlineData("{\"type\":\"lyrics.resplitBranch\",\"branchId\":\"primary\",\"rule\":\"Mixed\",\"estimateTimes\":true}")]
    [InlineData("{\"type\":\"lyrics.resplitBranch\",\"branchId\":\"primary\",\"rule\":\"none\"}")]
    [InlineData("{\"type\":\"lyrics.mergeToken\",\"segments\":null}")]
    public async Task HTTP元素新命令必填bool枚举数组与跨type字段严格400(string command)
    {
        await using Fixture f = await Fixture.Create();
        using HttpResponseMessage response = await PostRawEditing(f, "0", command);
        await Error(response, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        Assert.Equal("0", (await f.Projects.ReadHistoryAsync(f.Client.ClientId, f.Project.ProjectSessionId))!.ProjectRevision);
    }
    [Theory]
    [InlineData("{\"type\":\"lyrics.reorderToken\",\"target\":{\"branchId\":\"primary\",\"lineId\":\"gone\",\"tokenId\":\"gone\"},\"destinationIndex\":0}")]
    [InlineData("{\"type\":\"lyrics.reorderToken\",\"target\":{\"branchId\":\"primary\",\"lineId\":\"gone\",\"tokenId\":\"gone\"},\"destinationIndex\":0,\"reassignTimeSlots\":null}")]
    [InlineData("{\"type\":\"lyrics.resplitLine\",\"target\":{\"branchId\":\"primary\",\"lineId\":\"gone\"},\"segments\":null,\"estimateTimes\":false}")]
    [InlineData("{\"type\":\"lyrics.resplitLine\",\"target\":{\"branchId\":\"primary\",\"lineId\":\"gone\"},\"segments\":[],\"estimateTimes\":null}")]
    [InlineData("{\"type\":\"lyrics.resplitLine\",\"target\":{\"branchId\":\"primary\",\"lineId\":\"gone\"},\"segments\":[null],\"estimateTimes\":false}")]
    [InlineData("{\"type\":\"lyrics.resplitBranch\",\"branchId\":\"gone\",\"estimateTimes\":false}")]
    [InlineData("{\"type\":\"lyrics.resplitBranch\",\"branchId\":\"gone\",\"rule\":null,\"estimateTimes\":false}")]
    [InlineData("{\"type\":\"lyrics.resplitBranch\",\"branchId\":\"gone\",\"rule\":\"unknown\",\"estimateTimes\":false}")]
    [InlineData("{\"type\":\"lyrics.resplitBranch\",\"branchId\":\"gone\",\"rule\":\"none\",\"estimateTimes\":null}")]
    [InlineData("{\"type\":\"lyrics.splitToken\",\"target\":null}")]
    [InlineData("{\"type\":\"lyrics.mergeToken\",\"target\":[]}")]
    [InlineData("{\"type\":\"lyrics.stampTokenStart\",\"playheadUs\":\"0\"}")]
    [InlineData("{\"type\":\"lyrics.clearTokenTime\",\"target\":false}")]
    [InlineData("{\"type\":\"lyrics.setLineText\",\"target\":\"gone\",\"text\":\"x\"}")]
    [InlineData("{\"type\":\"lyrics.removeLine\"}")]
    public async Task HTTP形状校验先于稳定目标领域查找且缺失空值错型一律400(string command)
    {
        await using Fixture f = await Fixture.Create();
        using HttpResponseMessage response = await PostRawEditing(f, "0", command);
        await Error(response, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        ProjectHistoryResponse history = (await f.Projects.ReadHistoryAsync(f.Client.ClientId, f.Project.ProjectSessionId))!;
        Assert.Equal("0", history.ProjectRevision); Assert.Equal(0, history.UndoCount);
    }

}
