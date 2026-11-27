using System.Text;
using System.Xml.Linq;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Export;
using MakoKaraGenerator.Core.Lyrics.Import;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Core.Validation;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

public sealed class TtmlProjectRoundTripTests
{
    private static (LyricsImportResult Imported, string Xml) RoundTrip(LyricsDocument source)
    {
        using MemoryStream output = new();
        LyricsExportReport report = new TtmlProjectExporter().Export(source, output, LyricsExportOptions.Default);
        Assert.False(report.Plan.HasLosses);
        string xml = Encoding.UTF8.GetString(output.ToArray());
        output.Position = 0;
        return (new TtmlImporter().Import(output, new LyricsImportContext { FileName = "project.ttml" }), xml);
    }

    [Fact]
    public void Explicit_import_split_applies_to_all_sources_and_reports_complete_document_counts()
    {
        LyricLine main = LyricLine.Create("main-line", "ab");
        LyricLine translation = LyricLine.Create("translated-line", "cd") with { PrimaryLineId = main.Id };
        LyricsDocument document = LyricsDocument.Create([main]) with { Branches =
            [LyricBranch.Create("primary", "主", LyricBranchRole.Primary, [main]),
                LyricBranch.Create("translation", "译", LyricBranchRole.Translation, [translation])] };
        using MemoryStream output = new();
        new TtmlProjectExporter().Export(document, output, LyricsExportOptions.Default);
        output.Position = 0;
        LyricsImportResult result = LyricsImporterRegistry.CreateDefault().Import(output,
            new() { FileName = "split.ttml", SplitRule = TokenSplitRule.PerTextElement });
        Assert.All(result.Document.Branches, branch => Assert.Equal(2, branch.Lines[0].Tokens.Length));
        Assert.Equal(2, result.Report.LineCount);
        Assert.Equal(4, result.Report.TokenCount);
        Assert.Equal(4, result.Report.MissingTokenCount);
        Assert.Equal(2, result.Report.SplitLineCount);
        Assert.Equal(main.Id, result.Document.Branches[1].Lines[0].PrimaryLineId);
        string[] ids = [.. result.Document.EnumerateTokens().Select(pair => pair.Token.Id)];
        Assert.Equal(4, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Multi_branch_project_ttml_split_never_collides_with_secondary_branch_token_ids()
    {
        TimeRange lineTime = new(20_000_000, 25_000_000);
        TimeRange tokenTime = new(22_000_000, 24_000_000);
        LyricToken compound = LyricToken.Create("token-1022", "景色", tokenTime, TimingStatus.Edited);
        RubyAnnotation ruby = new()
        {
            Id = "ruby-1",
            BaseTokenId = compound.Id,
            Text = "けしき",
            RelativeScale = 0.5,
            GapPx = 2,
            Alignment = RubyAlignment.Center,
            Segments = [RubyTextSegment.Create("ruby-segment-1", "けしき", tokenTime, TimingStatus.Edited)],
        };
        LyricLine main = LyricLine.Create("line-0001", "景色", lineTime, TimingStatus.Edited, [compound]) with
        {
            RubyAnnotations = [ruby],
        };
        LyricLine meta = LyricLine.Create("line-1000", "Imaginary Arcadia", new(0, 264_000_000), TimingStatus.Edited,
        [
            LyricToken.Create("token-1023", "Imaginary"),
            LyricToken.Create("token-1024", " "),
            LyricToken.Create("token-1025", "Arcadia"),
        ]);
        LyricsDocument source = new()
        {
            PrimaryBranchId = "primary",
            Branches =
            [
                LyricBranch.Create("primary", "主歌词", LyricBranchRole.Primary, [main]),
                LyricBranch.Create("meta", "Meta", LyricBranchRole.Custom, [meta], timingMode: LyricBranchTimingMode.FollowPrimary),
            ],
        };
        using MemoryStream output = new();
        new TtmlProjectExporter().Export(source, output, LyricsExportOptions.Default);
        output.Position = 0;

        LyricsImportResult result = LyricsImporterRegistry.CreateDefault().Import(output,
            new() { FileName = "arcadia.ttml", SplitRule = TokenSplitRule.Mixed });

        string[] allTokenIds = [.. result.Document.EnumerateTokens().Select(pair => pair.Token.Id)];
        Assert.Equal(5, allTokenIds.Length);
        Assert.Equal(allTokenIds.Length, allTokenIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(["token-1022", "token-1026"], result.Document.Branches[0].Lines[0].Tokens.Select(token => token.Id).ToArray());
        Assert.Equal(["token-1023", "token-1024", "token-1025"], result.Document.Branches[1].Lines[0].Tokens.Select(token => token.Id).ToArray());

        ValidationReport validation = ProjectValidator.Validate(Project.Create("测试工程", lyrics: result.Document), ValidationContext.Open);
        Assert.False(validation.HasErrors, validation.ToSummary());
    }

    [Fact]
    public void Multiple_branches_all_identities_links_statuses_authoritative_text_and_ruby_styles_roundtrip()
    {
        const long start = 9_007_199_254_740_991;
        LyricToken token = LyricToken.Create("tok 身份", "所", new TimeRange(start, start + 100), TimingStatus.Confirmed);
        RubyAnnotation ruby = new()
        {
            Id = "ruby:唯一", BaseTokenId = token.Id, Text = "しょん", Language = null, RelativeScale = 0.625,
            GapPx = 3.75, Alignment = RubyAlignment.End,
            Segments = [RubyTextSegment.Create("せん-1", "しょ", new TimeRange(start + 5, start + 20), TimingStatus.Edited),
                RubyTextSegment.Create("せん-2", "ん")],
        };
        LyricLine main = LyricLine.Create("main-line", "权威正文与元素不相同\n & < >\t", new TimeRange(start, start + 200), TimingStatus.Estimated, [token]) with { RubyAnnotations = [ruby] };
        LyricLine translation = LyricLine.Create("translation-line", "译文") with { PrimaryLineId = main.Id };
        LyricsDocument source = new()
        {
            PrimaryBranchId = "main",
            Branches = [LyricBranch.Create("translation", "译文分支", LyricBranchRole.Translation, [translation], "zh-CN", LyricBranchTimingMode.FollowPrimary),
                LyricBranch.Create("main", "主来源\n日文", LyricBranchRole.Primary, [main], "ja-JP"),
                LyricBranch.Create("empty", "空来源", LyricBranchRole.Annotation, language: "", timingMode: LyricBranchTimingMode.None)],
        };
        (LyricsImportResult imported, string xml) = RoundTrip(source);
        Assert.DoesNotContain(imported.Report.Issues, issue => issue.Severity == IssueSeverity.Error);
        Assert.Equal(source, imported.Document);
        Assert.Contains("9007199254.740991s", xml, StringComparison.Ordinal);
        Assert.Contains("ttml.project-v1", imported.Report.SourceFormat, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TimingStatus.Missing)]
    [InlineData(TimingStatus.Imported)]
    [InlineData(TimingStatus.Estimated)]
    [InlineData(TimingStatus.Edited)]
    [InlineData(TimingStatus.Confirmed)]
    [InlineData(TimingStatus.Invalid)]
    public void Every_time_status_roundtrips_for_lines_tokens_and_ruby_segments(TimingStatus status)
    {
        TimeRange? time = status == TimingStatus.Missing ? null : status == TimingStatus.Invalid ? new TimeRange(-2, -3) : new TimeRange(100, 500);
        LyricToken token = LyricToken.Create("token", "字", time, status);
        LyricLine line = LyricLine.Create("line", "字", time, status, [token]) with
        {
            RubyAnnotations = [new RubyAnnotation { Id = "ruby", BaseTokenId = token.Id, Text = "じ", Language = "ja", Segments = [RubyTextSegment.Create("segment", "じ", time, status)] }],
        };
        LyricsDocument source = LyricsDocument.Create([line]);
        (LyricsImportResult imported, _) = RoundTrip(source);
        Assert.Equal(source, imported.Document);
    }

    [Fact]
    public void Empty_token_collection_and_empty_document_are_preserved()
    {
        LyricsDocument source = LyricsDocument.Create([LyricLine.Create("empty", ""), LyricLine.Create("plain", " 整行\n ")]);
        Assert.Equal(source, RoundTrip(source).Imported.Document);
        Assert.Equal(LyricsDocument.Create(), RoundTrip(LyricsDocument.Create()).Imported.Document);
    }

    [Fact]
    public void Project_extensions_are_namespaced_and_reject_duplicate_id_broken_links_and_unknown_version()
    {
        LyricLine line = LyricLine.Create("line", "字", tokens: [LyricToken.Create("token", "字")]);
        (_, string xml) = RoundTrip(LyricsDocument.Create([line]));
        XNamespace mkg = "urn:makokara:ttml";
        foreach (Action<XDocument> corrupt in new Action<XDocument>[]
        {
            document => document.Root!.SetAttributeValue(mkg + "version", "2"),
            document => document.Descendants().Single(element => element.Attribute(mkg + "token-id") is not null).SetAttributeValue(mkg + "token-id", "line"),
            document => document.Descendants().Single(element => element.Attribute(mkg + "line-id") is not null).SetAttributeValue(mkg + "primary-line-id", " "),
            document => document.Descendants().Single(element => element.Attribute(mkg + "line-id") is not null).SetAttributeValue(mkg + "timing-status", "Confirmed"),
        })
        {
            XDocument document = XDocument.Parse(xml);
            corrupt(document);
            using MemoryStream input = new(Encoding.UTF8.GetBytes(document.ToString()));
            LyricsImportResult result = new TtmlImporter().Import(input, new LyricsImportContext { FileName = "corrupt.ttml" });
            Assert.Empty(result.Document.Lines);
            Assert.Contains(result.Report.Issues, issue => issue.Severity == IssueSeverity.Error);
        }
    }

    [Fact]
    public void Malformed_xml_keeps_complete_paragraph_prefix_and_reports_error()
    {
        const string xml = "<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div><p begin=\"1s\" dur=\"1s\">完整</p><p begin=\"3s\">不完整";
        using MemoryStream input = new(Encoding.UTF8.GetBytes(xml));
        LyricsImportResult result = new TtmlImporter().Import(input, new LyricsImportContext { FileName = "partial.ttml" });
        Assert.Equal("完整", Assert.Single(result.Document.Lines).Text);
        Assert.Contains(result.Report.Issues, issue => issue.Severity == IssueSeverity.Error);
    }
}
