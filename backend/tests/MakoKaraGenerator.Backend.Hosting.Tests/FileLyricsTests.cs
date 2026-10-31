using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Export;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class FileLyricsTests
{
    [Theory]
    [InlineData("lrc.plain", false)]
    [InlineData("lrc.enhanced", true)]
    public async Task 固定选中来源计划真实损失原导出器bytes报告一致并且不改变工程(string format, bool bom)
    {
        await using Fixture f = await Fixture.Create();
        var before = await f.Registry.ReadSummaryAsync("client", f.Id);
        LyricsExportOptionsDto options = new("曲名", "艺术家", "专辑", "\r\n", bom, true);
        LyricsExportPlanResponse plan = Accepted(await f.Registry.PlanLyricsExportAsync("client", f.Id, new("0", "secondary", format, options, f.Metadata())));
        Assert.Equal("branch", plan.Scope); Assert.Equal(1, plan.OmittedBranchCount);
        Assert.Equal("secondary", plan.BranchId); Assert.Equal(1, plan.Plan.SkippedLineCount);
        Assert.Contains(plan.AdditionalLosses, loss => loss.Feature == "rubyAnnotations");
        Assert.Contains(plan.AdditionalLosses, loss => loss.Feature == "lineEndTimes");
        Assert.Contains(plan.Plan.Issues, issue => issue.Kind == "LineWithoutTime");
        Assert.True(plan.RequiresLossAcceptance);
        Assert.Equal(BackendErrorCode.CommandRejected, (await f.Registry.ExportLyricsAsync("client", f.Id, new("0", plan.PlanId, false, f.Metadata()))).Error!.Code);
        LyricsExportResponse result = Accepted(await f.Registry.ExportLyricsAsync("client", f.Id, new("0", plan.PlanId, true, f.Metadata())));
        using LyricsExportContentLease content = Accepted(f.Registry.AcquireLyricsExport("client", result.ExportId));
        using MemoryStream expected = new();
        ILyricsExporter exporter = format == "lrc.plain" ? new PlainLrcExporter() : new EnhancedLrcExporter();
        LyricsExportReport report = exporter.Export(LyricsDocument.Create(f.Secondary.Lines, f.Secondary.Language), expected,
            new() { Title = options.Title, Artist = options.Artist, Album = options.Album, NewLine = options.NewLine, WriteByteOrderMark = bom, MillisecondPrecision = true });
        using MemoryStream actual = new(); await content.Stream.CopyToAsync(actual);
        Assert.Equal(expected.ToArray(), actual.ToArray()); Assert.Equal(report.WrittenByteCount, result.SizeBytes);
        Assert.Equal(report.DroppedTokenTimeCount, result.Report.DroppedTokenTimeCount);
        Assert.Equal(report.ExportedLineCount, result.Report.ExportedLineCount);
        Assert.Equal(report.RoundedTimeCount, result.Report.RoundedTimeCount);
        Assert.Equal(before, await f.Registry.ReadSummaryAsync("client", f.Id));
    }

    [Theory]
    [InlineData("ttml.project")]
    [InlineData("ttml.amll")]
    public async Task TTML导出器在固定来源流程中保留格式与注音结构(string format)
    {
        await using Fixture f = await Fixture.Create();
        LyricsExportPlanResponse plan = Accepted(await f.Registry.PlanLyricsExportAsync(
            "client", f.Id, new("0", "secondary", format, new(), f.Metadata())));

        Assert.Equal("branch", plan.Scope);
        Assert.Contains(plan.AdditionalLosses, loss => loss.Feature == "omittedBranches");
        if (format == "ttml.amll") Assert.Contains(plan.Plan.Issues, issue => issue.Kind == "LineWithoutTime");
        else Assert.Empty(plan.Plan.Issues);
        LyricsExportResponse result = Accepted(await f.Registry.ExportLyricsAsync(
            "client", f.Id, new("0", plan.PlanId, true, f.Metadata())));
        using LyricsExportContentLease content = Accepted(f.Registry.AcquireLyricsExport("client", result.ExportId));
        using MemoryStream bytes = new();
        await content.Stream.CopyToAsync(bytes);
        string xml = System.Text.Encoding.UTF8.GetString(bytes.ToArray());

        Assert.Contains("http://www.w3.org/ns/ttml", xml, StringComparison.Ordinal);
        Assert.Contains("tts:ruby=\"textContainer\"", xml, StringComparison.Ordinal);
        Assert.Equal(format, result.Format);
    }

    [Fact]
    public async Task 旧修订计划client及session绑定TTL配额与关闭清理()
    {
        await using Fixture f = await Fixture.Create();
        LyricsExportPlanResponse plan = Accepted(await f.Registry.PlanLyricsExportAsync("client", f.Id, new("0", "primary", "lrc.plain", new(), f.Metadata())));
        ProjectSession sibling = new("project-sibling", "host", "client", ProjectWorkspace.CreateNew("sibling"), "", 0, f.Clock, new(), new("host", timeProvider: f.Clock));
        Assert.True(await f.Registry.RegisterSessionAsync(sibling));
        Assert.Equal(BackendErrorCode.ResourceNotFound, (await f.Registry.ExportLyricsAsync("client", sibling.ProjectSessionId, new("0", plan.PlanId, true, f.Metadata()))).Error!.Code);
        sibling.IsClosed = true; await f.Registry.RemoveClosedSessionAsync(sibling);
        Assert.Equal(BackendErrorCode.SessionNotFound, (await f.Registry.ExportLyricsAsync("other", f.Id, new("0", plan.PlanId, true, f.Metadata()))).Error!.Code);
        var changed = await f.Registry.ExecuteCommandAsync("client", f.Id, new("0", new AddLineCommandDto("primary", null),
            Guid.NewGuid().ToString("N"), "host", f.Clock.GetUtcNow().AddMinutes(1)));
        Assert.True(changed.Accepted);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await f.Registry.ExportLyricsAsync("client", f.Id, new("0", plan.PlanId, true, f.Metadata()))).Error!.Code);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await f.Registry.ExportLyricsAsync("client", f.Id, new("1", plan.PlanId, true, f.Metadata()))).Error!.Code);
        LyricsExportPlanResponse fresh = Accepted(await f.Registry.PlanLyricsExportAsync("client", f.Id, new("1", "secondary", "lrc.enhanced", new(), f.Metadata())));
        LyricsExportResponse exported = Accepted(await f.Registry.ExportLyricsAsync("client", f.Id, new("1", fresh.PlanId, true, f.Metadata())));
        LyricsExportPlanResponse unused = Accepted(await f.Registry.PlanLyricsExportAsync("client", f.Id, new("1", "secondary", "lrc.enhanced", new(), f.Metadata())));
        Assert.Equal(BackendErrorCode.ResourceNotFound, f.Registry.AcquireLyricsExport("other", exported.ExportId).Error!.Code);
        f.Clock.Advance(301);
        Assert.Equal(BackendErrorCode.ResourceExpired, f.Registry.AcquireLyricsExport("client", exported.ExportId).Error!.Code);
        Assert.Equal(BackendErrorCode.ResourceExpired, (await f.Registry.ExportLyricsAsync("client", f.Id, new("1", unused.PlanId, true, f.Metadata()))).Error!.Code);
        for (int i = 0; i < FileLyricsLimits.MaxRetainedPerClient; i++)
            Accepted(await f.Registry.PlanLyricsExportAsync("client", f.Id, new("1", "primary", "lrc.plain", new(), f.Metadata())));
        Assert.Equal(BackendErrorCode.ResourceQuotaExceeded, (await f.Registry.PlanLyricsExportAsync("client", f.Id, new("1", "primary", "lrc.plain", new(), f.Metadata()))).Error!.Code);
        f.Session.IsClosed = true;
        await f.Registry.RemoveClosedSessionAsync(f.Session);
        f.Clock.Advance(301);
        Assert.True(await f.Registry.TryReclaimClientAsync("client"));
    }

    [Fact]
    public async Task 来源及完整报告预算明确拒绝不截断()
    {
        await using Fixture f = await Fixture.Create();
        void SetLines(IEnumerable<LyricLine> lines) => f.ReplaceLyrics(LyricsDocument.Create(lines));
        SetLines(Enumerable.Range(0, FileLyricsLimits.MaxSourceLines + 1).Select(i => LyricLine.Create("line" + i, "x")));
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Registry.PlanLyricsExportAsync("client", f.Id, new("0", "primary", "lrc.plain", new(), f.Metadata()))).Error!.Code);
        SetLines([LyricLine.Create("big", new string('x', FileLyricsLimits.MaxSourceBytes + 1))]);
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Registry.PlanLyricsExportAsync("client", f.Id, new("0", "primary", "lrc.plain", new(), f.Metadata()))).Error!.Code);
        SetLines([new() { Id = "tokens", Text = "x", Tokens = [.. Enumerable.Repeat(LyricToken.Create("t", "x"), FileLyricsLimits.MaxSourceTokens + 1)] }]);
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Registry.PlanLyricsExportAsync("client", f.Id, new("0", "primary", "lrc.plain", new(), f.Metadata()))).Error!.Code);
        SetLines(Enumerable.Range(0, 2000).Select(i => LyricLine.Create("untimed" + i, "x")));
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Registry.PlanLyricsExportAsync("client", f.Id, new("0", "primary", "lrc.plain", new(), f.Metadata()))).Error!.Code);
    }

    [Fact]
    public async Task 输出额外时间标签超过4MiB拒绝且完整计划配额仍能转换为结果()
    {
        await using Fixture f = await Fixture.Create();
        f.ReplaceLyrics(LyricsDocument.Create(Enumerable.Range(0, 10000).Select(i =>
            LyricLine.Create("line" + i, new string('x', 408), new TimeRange(0, 1000000), TimingStatus.Imported))));
        var over = Accepted(await f.Registry.PlanLyricsExportAsync("client", f.Id, new("0", "primary", "lrc.plain", new(MillisecondPrecision: true), f.Metadata())));
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Registry.ExportLyricsAsync("client", f.Id, new("0", over.PlanId, true, f.Metadata()))).Error!.Code);
        f.ReplaceLyrics(LyricsDocument.Create([LyricLine.Create("line", "small", new TimeRange(0, 1000000), TimingStatus.Imported)]));
        LyricsExportPlanResponse last = null!;
        for (int i = 1; i < FileLyricsLimits.MaxRetainedPerClient; i++)
            last = Accepted(await f.Registry.PlanLyricsExportAsync("client", f.Id, new("0", "primary", "lrc.plain", new(), f.Metadata())));
        var result = Accepted(await f.Registry.ExportLyricsAsync("client", f.Id, new("0", last.PlanId, true, f.Metadata())));
        using var read = Accepted(f.Registry.AcquireLyricsExport("client", result.ExportId));
        Assert.True(result.SizeBytes > 0);
    }

    [Fact]
    public async Task 单层目录有序分页真实入口文件范围限制与链接拒绝()
    {
        await using Fixture f = await Fixture.Create();
        Directory.CreateDirectory(Path.Combine(f.Root, "z-empty"));
        Directory.CreateDirectory(Path.Combine(f.Root, "a-project"));
        File.WriteAllText(Path.Combine(f.Root, "a-project", ProjectJson.ProjectFileName), "{}");
        File.WriteAllText(Path.Combine(f.Root, "plain.txt"), "not a directory");
        var page = Accepted(f.Registry.ReadProjectDirectoryEntries("client", "projects", limit: 1));
        Assert.Equal(2, page.TotalCount); Assert.Equal(1, page.NextOffset);
        Assert.Equal(new ProjectDirectoryEntryDto("a-project", "a-project", true), Assert.Single(page.Entries));
        var last = Accepted(f.Registry.ReadProjectDirectoryEntries("client", "projects", offset: 1, limit: 1));
        Assert.Equal("z-empty", Assert.Single(last.Entries).Name); Assert.Null(last.NextOffset);
        Assert.Empty(Accepted(f.Registry.ReadProjectDirectoryEntries("client", "projects", "a-project")).Entries);
        foreach (string path in new[] { "..", "../outside", "/tmp", "a-project/../z-empty", "a-project//child", "C:\\outside" })
            Assert.Equal(BackendErrorCode.PathNotAllowed, f.Registry.ReadProjectDirectoryEntries("client", "projects", path).Error!.Code);
        Assert.Equal(BackendErrorCode.PathNotAllowed, f.Registry.ReadProjectDirectoryEntries("client", "unknown").Error!.Code);
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(Path.Combine(f.Root, "linked"), f.Directory.Path);
            Assert.Equal(BackendErrorCode.PathNotAllowed, f.Registry.ReadProjectDirectoryEntries("client", "projects").Error!.Code);
            Assert.Equal(BackendErrorCode.PathNotAllowed, f.Registry.ReadProjectDirectoryEntries("client", "projects", "linked").Error!.Code);
            System.IO.Directory.Delete(Path.Combine(f.Root, "linked"));
            File.CreateSymbolicLink(Path.Combine(f.Root, "z-empty", ProjectJson.ProjectFileName), Path.Combine(f.Root, "plain.txt"));
            Assert.Equal(BackendErrorCode.PathNotAllowed, f.Registry.ReadProjectDirectoryEntries("client", "projects").Error!.Code);
            File.Delete(Path.Combine(f.Root, "z-empty", ProjectJson.ProjectFileName));
        }
        for (int i = 0; i < FileLyricsLimits.MaxDirectoryEntries; i++) File.WriteAllText(Path.Combine(f.Root, "entry" + i), "");
        Assert.Equal(BackendErrorCode.ResourceQuotaExceeded, f.Registry.ReadProjectDirectoryEntries("client", "projects", limit: 1).Error!.Code);
    }

    private static T Accepted<T>(BackendOperationResult<T> result)
    { Assert.True(result.Accepted, result.Error?.Message); return result.Response!; }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly TemporaryDirectory Directory = new();
        internal readonly Clock Clock = new();
        internal ProjectSessionRegistry Registry = null!;
        internal ProjectSession Session = null!;
        internal LyricBranch Secondary = null!;
        internal string Root => Path.Combine(Directory.Path, "projects");
        internal string Id => Session.ProjectSessionId;
        internal RequestMetadataDto Metadata() => new(Guid.NewGuid().ToString("N"), "host", Clock.GetUtcNow().AddMinutes(1));
        internal void ReplaceLyrics(LyricsDocument lyrics) => typeof(ProjectWorkspace).GetProperty("Project")!
            .SetValue(Session.Workspace, Session.Workspace.Project with { Lyrics = lyrics });
        internal static async Task<Fixture> Create()
        {
            Fixture f = new();
            System.IO.Directory.CreateDirectory(f.Root);
            ResourceStore resources = new(new() { ResourceRoot = Path.Combine(f.Directory.Path, "resources") });
            f.Registry = new("host", resources, NewProjectPreparationService.CreateDefault(), f.Clock);
            f.Registry.ConfigurePersistence(new() { StateRoot = Path.Combine(f.Directory.Path, "state"), AllowedProjectRoots = new Dictionary<string, string> { ["projects"] = f.Root } });
            ProjectWorkspace workspace = ProjectWorkspace.CreateNew("lyrics");
            f.Session = new("project-file-lyrics", "host", "client", workspace, "", 0, f.Clock, new(), new("host", timeProvider: f.Clock));
            f.ReplaceLyrics(LyricsDocument.Create([LyricLine.Create("primary-line", "primary", new TimeRange(0, 1000000), TimingStatus.Imported)]));
            Assert.True(await f.Registry.RegisterSessionAsync(f.Session));
            LyricToken token = LyricToken.Create("token", "secondary", new TimeRange(1234567, 2000000), TimingStatus.Confirmed);
            LyricLine line = LyricLine.Create("timed", "secondary", new TimeRange(1234567, 2000000), TimingStatus.Confirmed, [token])
                with { RubyAnnotations = [RubyAnnotation.Create("ruby", "token", "読み")] };
            f.Secondary = LyricBranch.Create("secondary", "翻译", LyricBranchRole.Translation, [line, LyricLine.Create("untimed", "skip")], "ja");
            f.ReplaceLyrics(f.Session.Workspace.Project.Lyrics with { Branches = f.Session.Workspace.Project.Lyrics.Branches.Add(f.Secondary) });
            return f;
        }
        public async ValueTask DisposeAsync() { await Registry.DisposeAsync(); Directory.Dispose(); }
    }
}
