using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task HTTP十一内容设置命令真实读模型反馈及撤销重做保持一致()
    {
        await using Fixture f = await Fixture.Create();
        string session = f.Project.ProjectSessionId;
        ProjectLyricsWindowResponse original = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(
            $"/api/v1/project-sessions/{session}/lyrics/window?branchId=primary&expectedRevision=0&limit=1", Json))!;
        ProjectLyricLineDto line = Assert.Single(original.Lines);
        string target = JsonSerializer.Serialize(new LyricTimingTargetDto("primary", line.Id), Json);
        string tokenTarget = JsonSerializer.Serialize(new LyricTimingTargetDto("primary", line.Id, line.Tokens[0].Id), Json);
        string[] firstCommands = [
            $$"""{"type":"lyrics.setLineText","target":{{target}},"text":"hello!"}""",
            $$"""{"type":"lyrics.setRubyText","target":{{tokenTarget}},"text":"ハロー"}""",
            $$"""{"type":"lyrics.addLine","branchId":"primary","afterLineId":"{{line.Id}}"}""",
            $$"""{"type":"lyrics.duplicateLine","target":{{target}}}""",
        ];
        string? addedId = null;
        for (int index = 0; index < firstCommands.Length; index++)
        {
            using HttpResponseMessage response = await PostRawEditing(f, index.ToString(CultureInfo.InvariantCulture), firstCommands[index]);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            ProjectCommandResponse result = (await response.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
            Assert.True(result.Changed); Assert.True(result.ChangeSet.RequiresFullRefresh); Assert.NotNull(result.Feedback);
            if (index == 2) { addedId = result.Feedback.CreatedLineId; Assert.True(result.Feedback.Selection!.HasSelection); }
            if (index == 3) Assert.NotEqual(addedId, result.Feedback.CreatedLineId);
        }
        string addedTarget = JsonSerializer.Serialize(new LyricTimingTargetDto("primary", addedId!), Json);
        using HttpResponseMessage remove = await PostRawEditing(f, "4", $$"""{"type":"lyrics.removeLine","target":{{addedTarget}}}""");
        Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
        Assert.Equal(addedId, (await remove.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!.Feedback!.DeletedLineId);
        string[] settings = [
            "{\"type\":\"composition.setCanvasSize\",\"width\":641,\"height\":361}",
            "{\"type\":\"composition.setFrameRate\",\"numerator\":60000,\"denominator\":2002}",
            "{\"type\":\"composition.setBackgroundColor\",\"red\":11,\"green\":22,\"blue\":33,\"alpha\":44}",
            "{\"type\":\"composition.setOutputDuration\",\"durationUs\":\"4000007\"}",
            "{\"type\":\"composition.useLyricsEnd\"}",
            "{\"type\":\"composition.useAudioDuration\"}",
        ];
        for (int index = 0; index < settings.Length; index++)
        {
            using HttpResponseMessage response = await PostRawEditing(f, (index + 5).ToString(CultureInfo.InvariantCulture), settings[index]);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            ProjectCommandResponse result = (await response.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
            Assert.True(result.Changed); Assert.Null(result.Feedback);
        }
        ProjectSettingsResponse current = (await f.Http.GetFromJsonAsync<ProjectSettingsResponse>(
            $"/api/v1/project-sessions/{session}/settings?expectedRevision=11", Json))!;
        Assert.Equal(641, current.CanvasWidth); Assert.Equal(361, current.CanvasHeight);
        Assert.Equal(30000, current.FrameRateNumerator); Assert.Equal(1001, current.FrameRateDenominator);
        Assert.Equal((byte)44, current.BackgroundAlpha); Assert.Equal("3000000", current.OutputDurationUs);
        ProjectLyricsWindowResponse lyrics = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(
            $"/api/v1/project-sessions/{session}/lyrics/window?branchId=primary&expectedRevision=11", Json))!;
        Assert.Equal("hello!", lyrics.Lines[0].Text); Assert.Equal("ハロー", lyrics.Lines[0].RubyAnnotations[0].Text);
        ProjectHistoryResponse history = (await f.Http.GetFromJsonAsync<ProjectHistoryResponse>($"/api/v1/project-sessions/{session}/history", Json))!;
        Assert.Equal(11, history.UndoCount);
        using HttpResponseMessage undo = await PostRawEditing(f, "11", "{\"type\":\"history.undo\"}"); Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        Assert.Equal("6000000", (await f.Http.GetFromJsonAsync<ProjectSettingsResponse>($"/api/v1/project-sessions/{session}/settings?expectedRevision=12", Json))!.OutputDurationUs);
        using HttpResponseMessage redo = await PostRawEditing(f, "12", "{\"type\":\"history.redo\"}"); Assert.Equal(HttpStatusCode.OK, redo.StatusCode);
        Assert.Equal("3000000", (await f.Http.GetFromJsonAsync<ProjectSettingsResponse>($"/api/v1/project-sessions/{session}/settings?expectedRevision=13", Json))!.OutputDurationUs);
    }

    [Theory]
    [InlineData("{\"type\":\"composition.setOutputDuration\"}")]
    [InlineData("{\"type\":\"composition.setOutputDuration\",\"durationUs\":123}")]
    [InlineData("{\"type\":\"lyrics.setLineText\",\"text\":null}")]
    [InlineData("{\"type\":\"composition.setCanvasSize\",\"width\":\"640\",\"height\":360}")]
    [InlineData("{\"type\":\"composition.setBackgroundColor\",\"red\":1,\"green\":2,\"blue\":3}")]
    [InlineData("{\"type\":\"composition.setBackgroundColor\",\"red\":1,\"green\":2,\"blue\":3,\"alpha\":256}")]
    [InlineData("{\"type\":\"lyrics.addLine\",\"branchId\":\"primary\",\"target\":null}")]
    [InlineData("{\"type\":\"composition.useAudioDuration\",\"durationUs\":null}")]
    public async Task HTTP新内容命令缺失可空必填字段跨type字段和错型400且不改变工程(string command)
    {
        await using Fixture f = await Fixture.Create();
        using HttpResponseMessage result = await PostRawEditing(f, "0", command);
        await Error(result, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        Assert.Equal("0", (await f.Projects.ReadHistoryAsync(f.Client.ClientId, f.Project.ProjectSessionId))!.ProjectRevision);
    }

    [Fact]
    public async Task HTTP时长显式null有意清空而同意图重试回放且负值422原因稳定()
    {
        await using Fixture f = await Fixture.Create();
        string id = Guid.NewGuid().ToString("N");
        using HttpResponseMessage clear = await PostRawEditing(f, "0", "{\"type\":\"composition.setOutputDuration\",\"durationUs\":null}", id);
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        ProjectCommandResponse original = (await clear.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!; Assert.True(original.Changed);
        using HttpResponseMessage repeated = await PostRawEditing(f, "0", "{\"type\":\"composition.setOutputDuration\",\"durationUs\":null}", id);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(JsonSerializer.Serialize(original, Json), JsonSerializer.Serialize(await repeated.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json), Json));
        ProjectSettingsResponse settings = (await f.Http.GetFromJsonAsync<ProjectSettingsResponse>(
            $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/settings?expectedRevision=1", Json))!;
        Assert.Null(settings.OutputDurationUs);
        using HttpResponseMessage invalid = await PostRawEditing(f, "1", "{\"type\":\"composition.setOutputDuration\",\"durationUs\":\"-1\"}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        BackendErrorResponse error = (await invalid.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!;
        Assert.Equal("InvalidOutputDuration", error.FailureCode); Assert.Equal("command.durationUs", error.Field);
    }

    [Fact]
    public async Task HTTP超过单行最终投影或原文字段预算413且修订历史保持原值()
    {
        await using Fixture f = await Fixture.Create();
        LyricLineDto line = Assert.Single((await f.Projects.ReadLyricsAsync(f.Client.ClientId, f.Project.ProjectSessionId, "primary"))!.Lines);
        string target = JsonSerializer.Serialize(new LyricTimingTargetDto("primary", line.Id), Json);
        foreach (int size in new[] { 19000, ProjectReadModelLimits.MaxResponseBytes + 1 })
        {
            string text = JsonSerializer.Serialize(new string('x', size));
            using HttpResponseMessage rejected = await PostRawEditing(f, "0", $$"""{"type":"lyrics.setLineText","target":{{target}},"text":{{text}}}""");
            await Error(rejected, HttpStatusCode.RequestEntityTooLarge, BackendErrorCode.ResourceTooLarge);
            ProjectHistoryResponse history = (await f.Projects.ReadHistoryAsync(f.Client.ClientId, f.Project.ProjectSessionId))!;
            Assert.Equal("0", history.ProjectRevision); Assert.Equal(0, history.UndoCount);
        }
        ProjectLyricsWindowResponse original = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(
            $"/api/v1/project-sessions/{f.Project.ProjectSessionId}/lyrics/window?branchId=primary&expectedRevision=0&limit=1", Json))!;
        Assert.Equal(line.Text, original.Lines[0].Text);
    }
}
