using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task HTTP粘贴明确新翻译来源选择读窗口且只携带实际反馈()
    {
        await using Fixture f=await Fixture.Create();
        using var response=await PostRawEditing(f,"0","""{"type":"lyrics.appendText","text":" 保留 👩‍🚀 é \n第二行","destination":{"kind":"newTranslation","name":" 翻译 "}}""");
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var body=(await response.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        Assert.NotNull(body.ImportResult); Assert.Equal(2,body.ImportResult.LineCount); Assert.Null(body.ImportResult.Report);
        Assert.DoesNotContain("report",await response.Content.ReadAsStringAsync(),StringComparison.Ordinal);
        string id=body.ImportResult.SelectedBranchId;
        var branches=(await f.Http.GetFromJsonAsync<ProjectLyricBranchesResponse>($"/api/v1/project-sessions/{f.Project.ProjectSessionId}/lyrics/branches?expectedRevision=1",Json))!;
        Assert.Equal(ProjectLyricTimingMode.FollowPrimary,branches.Branches.Single(item=>item.Id==id).TimingMode);
        var window=(await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>($"/api/v1/project-sessions/{f.Project.ProjectSessionId}/lyrics/window?branchId={id}&expectedRevision=1",Json))!;
        Assert.Equal(body.ImportResult.SelectedLineId,window.Lines[0].Id); Assert.Equal(" 保留 👩‍🚀 é ",window.Lines[0].Text);
    }

    [Fact]
    public async Task HTTP固定文件再次导入完整报告与空options默认保持并可读取新行()
    {
        await using Fixture f=await Fixture.Create();
        var asset=await f.Resources.UploadAsync(f.Client.ClientId,"song.srt","application/octet-stream",new MemoryStream("1\n00:00:01,000 --> 00:00:03,000\n完整歌词\n"u8.ToArray()));
        string command=JsonSerializer.Serialize(new {type="lyrics.importAsset",assetId=asset.AssetId,destination=new {kind="newBranch",name=" 文件 ",role="custom"},options=new {importerId=(string?)null,encodingName=(string?)null}},Json);
        using var response=await PostRawEditing(f,"0",command); Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        using var body=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var report=body.RootElement.GetProperty("importResult").GetProperty("report"); Assert.Equal("srt",report.GetProperty("importerId").GetString());
        Assert.Equal("0",report.GetProperty("offsetUs").GetString()); Assert.True(report.TryGetProperty("discardedFeatures",out _));
        var result=(await response.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        var window=(await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>($"/api/v1/project-sessions/{f.Project.ProjectSessionId}/lyrics/window?branchId={result.ImportResult!.SelectedBranchId}&expectedRevision=1",Json))!;
        Assert.Equal("完整歌词",Assert.Single(window.Lines).Text);
    }

    [Theory]
    [InlineData("""{"type":"lyrics.importAsset","assetId":"asset-x","destination":{"kind":"existing","branchId":"primary"},"options":null}""")]
    [InlineData("""{"type":"lyrics.importAsset","assetId":"asset-x","destination":{"kind":"newBranch","name":"x","role":"primary"}}""")]
    [InlineData("""{"type":"lyrics.importAsset","assetId":"asset-x","destination":{"kind":"newTranslation","name":"x"}}""")]
    [InlineData("""{"type":"lyrics.appendText","text":"x","destination":{"kind":"newBranch","name":"x","role":"translation"}}""")]
    [InlineData("""{"type":"lyrics.appendText","text":"x","destination":{"kind":"existing","branchId":"primary","name":"x"}}""")]
    [InlineData("""{"type":"lyrics.appendText","text":"x","destination":{"kind":"existing","branchId":"primary","branchId":"primary"}}""")]
    [InlineData("""{"type":"lyrics.appendText","text":"x","destination":null}""")]
    [InlineData("""{"type":"lyrics.appendText","text":null,"destination":{"kind":"existing","branchId":"primary"}}""")]
    [InlineData("""{"type":"lyrics.importAsset","assetId":"asset-x","destination":{"kind":"existing","branchId":"primary"},"options":{"splitRule":0}}""")]
    [InlineData("""{"type":"lyrics.importAsset","assetId":"asset-x","destination":{"kind":"existing","branchId":"primary"},"options":{"splitRule":null}}""")]
    [InlineData("""{"type":"lyrics.importAsset","assetId":"asset-x","destination":{"kind":"existing","branchId":"primary"},"options":{"estimateSplitTimes":null}}""")]
    [InlineData("""{"type":"lyrics.importAsset","assetId":"asset-x","destination":{"kind":"existing","branchId":"primary"},"options":{"estimateSplitTimes":"false"}}""")]
    [InlineData("""{"type":"lyrics.importAsset","assetId":"asset-x","destination":{"kind":"existing","branchId":"primary"},"options":{"surprise":true}}""")]
    public async Task HTTP导入目标严格判别联合与options拒绝空对象以外非法形状(string command)
    {
        await using Fixture f=await Fixture.Create(); using var response=await PostRawEditing(f,"0",command);
        await Error(response,HttpStatusCode.BadRequest,BackendErrorCode.InvalidRequest);
        Assert.Equal("0",(await f.Projects.ReadSummaryAsync(f.Client.ClientId,f.Project.ProjectSessionId))!.ProjectRevision);
    }

    [Fact]
    public async Task HTTP追加大单行预算413且空文字沿原稳定失败()
    {
        await using Fixture f=await Fixture.Create();
        using var oversized=await PostRawEditing(f,"0",JsonSerializer.Serialize(new {type="lyrics.appendText",text=new string('x',20000),destination=new {kind="existing",branchId="primary"}},Json));
        await Error(oversized,HttpStatusCode.RequestEntityTooLarge,BackendErrorCode.ResourceTooLarge);
        using var empty=await PostRawEditing(f,"0","""{"type":"lyrics.appendText","text":" \n\t","destination":{"kind":"existing","branchId":"primary"}}""");
        var error=(await empty.Content.ReadFromJsonAsync<BackendErrorResponse>(Json))!;
        Assert.Equal(BackendErrorCode.EmptyLyrics,error.Code); Assert.Equal("EmptyText",error.FailureCode); Assert.Equal("command.text",error.Field);
        Assert.Equal("0",(await f.Projects.ReadSummaryAsync(f.Client.ClientId,f.Project.ProjectSessionId))!.ProjectRevision);
    }
}
