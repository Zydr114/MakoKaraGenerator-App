using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task HTTP导入信息检测歧义保完整报告预览且现有导入无需计划票据()
    {
        await using Fixture f = await Fixture.Create();
        var asset = await f.Resources.UploadAsync(f.Client.ClientId, "song.lrc", "text/plain",
            new MemoryStream("[00:01.00]<00:01.00>你好<00:02.00>世界\n"u8.ToArray()));
        using var response = await f.Http.PostAsJsonAsync("/api/v1/lyrics-import-plans", new LyricsImportPlanRequest(asset.AssetId, new(EncodingName: "utf-8")), Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var plan = (await response.Content.ReadFromJsonAsync<LyricsImportPlanResponse>(Json))!;
        Assert.Null(plan.PreparationError);
        Assert.True(plan.IsFormatAmbiguous);
        Assert.Contains(plan.FormatCandidates, item => item.ImporterId == "lrc.enhanced");
        Assert.Contains(plan.FormatCandidates, item => item.ImporterId == "lrc.plain");
        Assert.Equal("utf-8", plan.EncodingName);
        Assert.Equal(plan.Report!.LineCount, plan.PreviewLines.Length);
        Assert.Equal("你好世界", Assert.Single(plan.PreviewLines).Text);
        Assert.Equal("0", (await f.Projects.ReadSummaryAsync(f.Client.ClientId, f.Project.ProjectSessionId))!.ProjectRevision);
        using var imported = await PostRawEditing(f, "0", JsonSerializer.Serialize(new
        {
            type = "lyrics.importAsset", assetId = asset.AssetId,
            destination = new { kind = "existing", branchId = "primary" },
        }, Json));
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
    }

    [Fact]
    public async Task HTTP导入信息空源及不支持格式可见失败且资源必须属于当前客户端()
    {
        await using Fixture f = await Fixture.Create();
        var empty = await f.Resources.UploadAsync(f.Client.ClientId, "empty.txt", "text/plain", new MemoryStream(" \n\t"u8.ToArray()));
        using var emptyResponse = await f.Http.PostAsJsonAsync("/api/v1/lyrics-import-plans", new LyricsImportPlanRequest(empty.AssetId), Json);
        Assert.Equal(HttpStatusCode.OK, emptyResponse.StatusCode);
        var emptyPlan = (await emptyResponse.Content.ReadFromJsonAsync<LyricsImportPlanResponse>(Json))!;
        Assert.Equal("EmptyImport", emptyPlan.PreparationError!.FailureCode);
        Assert.NotNull(emptyPlan.Report);
        var bad = await f.Resources.UploadAsync(f.Client.ClientId, "unknown.xyz", "application/octet-stream", new MemoryStream([0, 0, 1, 2]));
        using var badResponse = await f.Http.PostAsJsonAsync("/api/v1/lyrics-import-plans", new LyricsImportPlanRequest(bad.AssetId), Json);
        Assert.Equal(HttpStatusCode.OK, badResponse.StatusCode);
        var badPlan = (await badResponse.Content.ReadFromJsonAsync<LyricsImportPlanResponse>(Json))!;
        Assert.Equal("UnsupportedLyricsFormat", badPlan.PreparationError!.FailureCode);
        var other = await f.Resources.UploadAsync("other", "other.txt", "text/plain", new MemoryStream("私有"u8.ToArray()));
        using var denied = await f.Http.PostAsJsonAsync("/api/v1/lyrics-import-plans", new LyricsImportPlanRequest(other.AssetId), Json);
        Assert.False(denied.IsSuccessStatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"assetId":"x","assetId":"y"}""")]
    [InlineData("""{"assetId":"x","options":null}""")]
    [InlineData("""{"assetId":"x","options":{"splitRule":0}}""")]
    [InlineData("""{"assetId":"x","options":{"estimateSplitTimes":null}}""")]
    [InlineData("""{"assetId":"x","options":{"sourceBranchId":null}}""")]
    [InlineData("""{"assetId":"x","options":{"sourceBranchId":" "}}""")]
    [InlineData("""{"assetId":"x","options":{"extra":true}}""")]
    [InlineData("""{"assetId":"x","extra":true}""")]
    public async Task HTTP导入信息复用严格options并拒绝混合重复未知字段(string input)
    {
        await using Fixture f = await Fixture.Create();
        using var response = await f.Http.PostAsync("/api/v1/lyrics-import-plans", new StringContent(input, Encoding.UTF8, "application/json"));
        await Error(response, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
    }
}
