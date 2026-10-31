using System.Net;
using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Import;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class FileLyricsScopeTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions OmitNull = new(Json) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    [Theory]
    [InlineData("\"scope\":null,\"branchId\":\"primary\"")]
    [InlineData("\"scope\":\"unknown\",\"branchId\":\"primary\"")]
    [InlineData("\"scope\":\"branch\"")]
    [InlineData("\"branchId\":null")]
    [InlineData("\"scope\":\"document\",\"branchId\":null")]
    [InlineData("\"scope\":\"document\",\"branchId\":\"primary\"")]
    [InlineData("\"scope\":\"document\",\"extra\":true")]
    [InlineData("\"scope\":\"document\",\"scope\":\"document\"")]
    public void 范围联合严格拒绝非法字段组合(string fields)
    {
        string raw = "{\"expectedRevision\":\"0\",\"format\":\"ttml.project\",\"options\":{}," + fields + "}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LyricsExportPlanRequest>(raw, Json));
    }

    [Theory]
    [InlineData("\"branchId\":\"primary\"", "branch", "primary")]
    [InlineData("\"scope\":\"branch\",\"branchId\":\"primary\"", "branch", "primary")]
    [InlineData("\"scope\":\"document\"", "document", null)]
    public void 兼容省略范围以及显式两范围的合法形状(string fields, string scope, string? branch)
    {
        var request = JsonSerializer.Deserialize<LyricsExportPlanRequest>("{\"expectedRevision\":\"0\",\"format\":\"ttml.project\",\"options\":{}," + fields + "}", Json)!;
        Assert.Equal(scope, request.Scope); Assert.Equal(branch, request.BranchId);
        Assert.Equal(request, JsonSerializer.Deserialize<LyricsExportPlanRequest>(JsonSerializer.Serialize(request, Json), Json));
    }

    [Theory]
    [InlineData("lrc.plain")]
    [InlineData("lrc.enhanced")]
    [InlineData("ttml.amll")]
    public void 整文档不允许非项目TTML格式(string format) => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LyricsExportPlanRequest>(
        "{\"expectedRevision\":\"0\",\"scope\":\"document\",\"format\":\"" + format + "\",\"options\":{}}", Json));

    [Fact]
    public async Task 整文档固定全部来源并回读且单来源明确报告省略损失()
    {
        await using Fixture f = await Fixture.Create();
        LyricsExportPlanResponse branch = Accept(await f.Registry.PlanLyricsExportAsync("client", f.Id,
            new("0", "translation", "ttml.project", new(), f.Metadata())));
        Assert.Equal("branch", branch.Scope); Assert.Equal(1, branch.OmittedBranchCount);
        Assert.Contains(branch.AdditionalLosses, item => item.Feature == "omittedBranches");
        Assert.Contains(branch.AdditionalLosses, item => item.Feature == "crossBranchPrimaryLineLinks");
        Assert.True(branch.RequiresLossAcceptance);
        Assert.Equal(BackendErrorCode.CommandRejected, (await f.Registry.ExportLyricsAsync("client", f.Id,
            new("0", branch.PlanId, false, f.Metadata()))).Error!.Code);
        var docRequest = new LyricsExportPlanRequest("0", null, "ttml.project", new(), f.Metadata(), "document");
        var plan = Accept(await f.Registry.PlanLyricsExportAsync("client", f.Id, docRequest));
        Assert.Equal("document", plan.Scope); Assert.Null(plan.BranchId); Assert.Equal(0, plan.OmittedBranchCount);
        Assert.DoesNotContain(plan.AdditionalLosses, item => item.Feature is "omittedBranches" or "crossBranchPrimaryLineLinks");
        var result = Accept(await f.Registry.ExportLyricsAsync("client", f.Id, new("0", plan.PlanId, true, f.Metadata())));
        Assert.Equal("document", result.Scope); Assert.Null(result.BranchId);
        Assert.Contains("\"branchId\":null", JsonSerializer.Serialize(result, OmitNull), StringComparison.Ordinal);
        using var lease = Accept(f.Registry.AcquireLyricsExport("client", result.ExportId));
        var imported = new TtmlImporter().Import(lease.Stream, new() { FileName = "download.ttml" });
        Assert.Equal(f.Document, imported.Document);
        // The retained assessment, source, and scope cannot be changed by a later workspace replacement.
        Assert.Equal(2, result.Report.SourceLineCount);
        Assert.Equal("0", (await f.Registry.ReadSummaryAsync("client", f.Id))!.ProjectRevision);
    }

    [Theory]
    [InlineData("lines")]
    [InlineData("metadata")]
    [InlineData("segments")]
    public async Task 整文档预算遍历非主来源以及空来源元数据(string kind)
    {
        await using Fixture f = await Fixture.Create();
        var secondary = f.Document.Branches[1];
        if (kind == "lines") secondary = secondary with { Lines = [.. Enumerable.Range(0, FileLyricsLimits.MaxSourceLines).Select(i => LyricLine.Create("extra" + i, "x"))] };
        else if (kind == "metadata") secondary = secondary with { Name = new string('x', FileLyricsLimits.MaxSourceBytes + 1), Lines = [] };
        else secondary = secondary with { Lines = [new LyricLine { Id = "segments", Text = "x", Tokens = [LyricToken.Create("base", "x")], RubyAnnotations = [new RubyAnnotation { Id = "ruby", BaseTokenId = "base", Text = new string('x', FileLyricsLimits.MaxSourceTokens), Segments = [.. Enumerable.Range(0, FileLyricsLimits.MaxSourceTokens).Select(i => new RubyTextSegment { Id = "s" + i, Text = "x" })] }] }] };
        f.Replace(f.Document with { Branches = f.Document.Branches.SetItem(1, secondary) });
        var result = await f.Registry.PlanLyricsExportAsync("client", f.Id, new("0", null, "ttml.project", new(), f.Metadata(), "document"));
        Assert.Equal(BackendErrorCode.ResourceTooLarge, result.Error!.Code);
        // The same overlarge secondary branch does not consume the selected primary branch's budget.
        Assert.True((await f.Registry.PlanLyricsExportAsync("client", f.Id, new("0", "primary", "ttml.project", new(), f.Metadata()))).Accepted);
    }

    private static T Accept<T>(BackendOperationResult<T> result) { Assert.True(result.Accepted, result.Error?.ToString()); return result.Response!; }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        internal ProjectSessionRegistry Registry = null!;
        internal ProjectSession Session = null!;
        internal LyricsDocument Document = null!;
        internal string Id => Session.ProjectSessionId;
        internal RequestMetadataDto Metadata() => new(Guid.NewGuid().ToString("N"), Session.HostInstanceId, DateTimeOffset.UtcNow.AddMinutes(1));
        internal void Replace(LyricsDocument document) => typeof(ProjectWorkspace).GetProperty("Project")!.SetValue(Session.Workspace, Session.Workspace.Project with { Lyrics = document });
        internal static async Task<Fixture> Create()
        {
            Fixture f = new();
            f.Registry = new("host", new ResourceStore(new() { ResourceRoot = Path.Combine(f._directory.Path, "resources") }), NewProjectPreparationService.CreateDefault());
            var workspace = ProjectWorkspace.CreateNew("scope");
            f.Session = new("session-scope", "host", "client", workspace, "", 0, TimeProvider.System, new(), new("host"));
            var line = LyricLine.Create("line-primary", "primary", new TimeRange(0, 2_000_000), TimingStatus.Imported);
            var translation = LyricLine.Create("line-translation", "translation", new TimeRange(0, 2_000_000), TimingStatus.Confirmed) with { PrimaryLineId = line.Id };
            f.Document = new() { Branches = [LyricBranch.Create("primary", "主歌词", LyricBranchRole.Primary, [line]), LyricBranch.Create("translation", "翻译", LyricBranchRole.Translation, [translation], "en", LyricBranchTimingMode.FollowPrimary)] };
            f.Replace(f.Document); Assert.True(await f.Registry.RegisterSessionAsync(f.Session));
            return f;
        }
        public async ValueTask DisposeAsync() { await Registry.DisposeAsync(); _directory.Dispose(); }
    }
}
