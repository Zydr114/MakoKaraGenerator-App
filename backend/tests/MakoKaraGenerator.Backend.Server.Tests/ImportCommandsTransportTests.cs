using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Export;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task HTTP全部来源导入一次撤销保存重开且旧单来源请求仍可用()
    {
        await using Fixture f = await Fixture.Create();
        byte[] ttml = TwoBranchTtml();
        var asset = await f.Resources.UploadAsync(f.Client.ClientId, "two.ttml", "application/ttml+xml", new MemoryStream(ttml));
        string session = f.Project.ProjectSessionId;
        using var imported = await PostRawEditing(f, "0", JsonSerializer.Serialize(new
        {
            type = "lyrics.importAsset", assetId = asset.AssetId, destination = new { kind = "allSources" },
        }, Json));
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var first = (await imported.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        Assert.Equal("lyrics.importAll", first.UndoName);
        Assert.Equal(2, first.ImportResult!.LineCount);
        Assert.True(first.Changed);
        var branches = (await f.Http.GetFromJsonAsync<ProjectLyricBranchesResponse>(
            $"/api/v1/project-sessions/{session}/lyrics/branches?expectedRevision={first.ProjectRevision}", Json))!;
        Assert.Equal(3, branches.Branches.Length);
        Assert.Equal("primary", branches.PrimaryBranchId);
        ProjectLyricBranchDto addedMain = branches.Branches[1];
        ProjectLyricBranchDto addedTranslation = branches.Branches[2];
        Assert.NotEqual(ProjectLyricBranchRole.Primary, addedMain.Role);
        var mainWindow = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(
            $"/api/v1/project-sessions/{session}/lyrics/window?branchId={addedMain.Id}&expectedRevision={first.ProjectRevision}", Json))!;
        var translationWindow = (await f.Http.GetFromJsonAsync<ProjectLyricsWindowResponse>(
            $"/api/v1/project-sessions/{session}/lyrics/window?branchId={addedTranslation.Id}&expectedRevision={first.ProjectRevision}", Json))!;
        Assert.Equal("权威正文", Assert.Single(mainWindow.Lines).Text);
        Assert.Equal(mainWindow.Lines[0].Id, Assert.Single(translationWindow.Lines).PrimaryLineId);
        Assert.Equal(mainWindow.Lines[0].Tokens[0].Id, Assert.Single(mainWindow.Lines[0].RubyAnnotations).BaseTokenId);

        using var undone = await PostRawEditing(f, first.ProjectRevision, "{\"type\":\"history.undo\"}");
        Assert.Equal(HttpStatusCode.OK, undone.StatusCode);
        var afterUndo = (await undone.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        var restored = (await f.Http.GetFromJsonAsync<ProjectLyricBranchesResponse>(
            $"/api/v1/project-sessions/{session}/lyrics/branches?expectedRevision={afterUndo.ProjectRevision}", Json))!;
        Assert.Single(restored.Branches);
        Assert.Equal("primary", restored.PrimaryBranchId);

        using var again = await PostRawEditing(f, afterUndo.ProjectRevision, JsonSerializer.Serialize(new
        {
            type = "lyrics.importAsset", assetId = asset.AssetId, destination = new { kind = "allSources" },
        }, Json));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var second = (await again.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        string directory = Path.Combine(f._directory.Path, "projects", "all-sources");
        Directory.CreateDirectory(directory);
        using HttpResponseMessage handleResponse = await f.Http.PostAsJsonAsync("/api/v1/file-handles",
            new FileHandleRequest("preview-test-root", "all-sources", Metadata: f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.Created, handleResponse.StatusCode);
        FileHandleResponse handle = (await handleResponse.Content.ReadFromJsonAsync<FileHandleResponse>(Json))!;
        using HttpResponseMessage saveResponse = await f.Http.PostAsJsonAsync($"/api/v1/project-sessions/{session}/save-jobs",
            new SaveProjectRequest(second.ProjectRevision, handle.HandleId, f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.Accepted, saveResponse.StatusCode);
        JobResponse saveJob = (await saveResponse.Content.ReadFromJsonAsync<JobResponse>(Json))!;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        while (true)
        {
            JobResponse current = (await f.Http.GetFromJsonAsync<JobResponse>($"/api/v1/jobs/{saveJob.JobId}", Json, timeout.Token))!;
            if (current.State is "succeeded" or "failed" or "cancelled")
            {
                Assert.True(current.State == "succeeded", JsonSerializer.Serialize(current));
                break;
            }
            await Task.Delay(20, timeout.Token);
        }
        ProjectLoadResult reopened = ProjectStore.Load(directory);
        Assert.NotNull(reopened.Project);
        Assert.Equal(3, reopened.Project!.Lyrics.Branches.Length);
        LyricLine reopenedMain = reopened.Project.Lyrics.Branches[1].Lines[0];
        Assert.Equal("权威正文", reopenedMain.Text);
        Assert.Equal(reopenedMain.Tokens[0].Id, Assert.Single(reopenedMain.RubyAnnotations).BaseTokenId);
        Assert.Equal(reopenedMain.Id, reopened.Project.Lyrics.Branches[2].Lines[0].PrimaryLineId);

        await using Fixture fresh = await Fixture.Create();
        var freshAsset = await fresh.Resources.UploadAsync(fresh.Client.ClientId, "two.ttml", "application/ttml+xml", new MemoryStream(ttml));
        using var selected = await PostRawEditing(fresh, "0", JsonSerializer.Serialize(new
        {
            type = "lyrics.importAsset",
            assetId = freshAsset.AssetId,
            destination = new { kind = "newBranch", name = "只要翻译", role = "translation" },
            options = new { sourceBranchId = "source-translation" },
        }, Json));
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        var selectedBody = (await selected.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        Assert.Equal(1, selectedBody.ImportResult!.LineCount);
        Assert.NotEqual("lyrics.importAll", selectedBody.UndoName);
    }

    private static byte[] TwoBranchTtml()
    {
        LyricToken token = LyricToken.Create("source-token", "字");
        RubyAnnotation ruby = new()
        {
            Id = "source-ruby", BaseTokenId = token.Id, Text = "じ",
            Segments = [RubyTextSegment.Create("source-segment", "じ")],
        };
        LyricLine line = LyricLine.Create("source-main", "权威正文", new(0, 1_000_000), TimingStatus.Imported, [token]) with { RubyAnnotations = [ruby] };
        LyricsDocument source = LyricsDocument.Create([line]) with
        {
            Branches =
            [
                LyricBranch.Create("primary", "源主歌词", LyricBranchRole.Primary, [line]),
                LyricBranch.Create("source-translation", "源翻译", LyricBranchRole.Translation,
                    [LyricLine.Create("source-translation-line", "翻译") with { PrimaryLineId = line.Id }],
                    "zh-CN", LyricBranchTimingMode.FollowPrimary),
            ],
        };
        using MemoryStream output = new();
        new TtmlProjectExporter().Export(source, output, LyricsExportOptions.Default);
        return output.ToArray();
    }

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
