using System.Text;
using System.Xml.Linq;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Export;
using MakoKaraGenerator.Core.Lyrics.Import;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

public sealed class TtmlBoundaryTests
{
    private static LyricsImportResult Import(string xml)
    {
        using MemoryStream input = new(Encoding.UTF8.GetBytes(xml));
        return new TtmlImporter().Import(input, new LyricsImportContext { FileName = "boundary.ttml" });
    }

    [Theory]
    [InlineData("mkg:profile=\"unknown\"")]
    [InlineData("mkg:version=\"1\"")]
    [InlineData("itunes:timing=\"Unknown\"")]
    [InlineData("itunes:timing=\"Word\" mkg:timing-profile=\"ttml2-media\"")]
    public void Unknown_or_conflicting_profile_never_downgrades_to_success(string attributes)
    {
        LyricsImportResult result = Import("<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:mkg=\"urn:makokara:ttml\" xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" "
            + attributes + "><body><div><p begin=\"1s\" dur=\"1s\">正文</p></div></body></tt>");
        Assert.Empty(result.Document.Lines);
        Assert.Contains(result.Report.Issues, issue => issue.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Merely_declaring_apple_namespace_keeps_standard_profile_and_rounding_is_reported()
    {
        LyricsImportResult result = Import("<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\"><body><div begin=\"1s\"><p begin=\"0.0000005s\" dur=\"1s\">精</p></div></body></tt>");
        Assert.Equal(1_000_001L, Assert.Single(result.Document.Lines).Time!.Value.StartUs);
        Assert.Contains("ttml2-media", result.Report.SourceFormat, StringComparison.Ordinal);
        Assert.Contains(result.Report.Issues, issue => issue.Message.Contains("精度损失", StringComparison.Ordinal));
    }

    [Fact]
    public void Metadata_never_becomes_body_text_and_unknown_attributes_are_reported()
    {
        LyricsImportResult result = Import("<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\" xmlns:u=\"urn:unknown\"><head><metadata><ttm:agent xml:id=\"v1\"><ttm:name>歌手</ttm:name></ttm:agent></metadata></head><body><div><p begin=\"1s\" dur=\"1s\" u:secret=\"x\"><ttm:agent>不应成为歌词</ttm:agent><span begin=\"0s\" dur=\"1s\">正文</span></p></div></body></tt>");
        Assert.Equal("正文", Assert.Single(result.Document.Lines).Text);
        Assert.Contains(result.Report.DiscardedFeatures, feature => feature.Contains("agent", StringComparison.Ordinal));
        Assert.Contains(result.Report.DiscardedFeatures, feature => feature.Contains("属性", StringComparison.Ordinal));
    }

    [Fact]
    public void Ruby_outside_base_time_retains_standard_interval_without_project_extensions()
    {
        LyricToken token = LyricToken.Create("token", "字", new TimeRange(1_100_000, 1_200_000), TimingStatus.Imported);
        RubyAnnotation ruby = new() { Id = "ruby", BaseTokenId = token.Id, Text = "じ", Segments = [RubyTextSegment.Create("segment", "じ", new TimeRange(1_700_000, 1_900_000), TimingStatus.Edited)] };
        LyricsDocument source = LyricsDocument.Create([LyricLine.Create("line", "字", new TimeRange(1_000_000, 2_000_000), TimingStatus.Imported, [token]) with { RubyAnnotations = [ruby] }]);
        using MemoryStream output = new();
        new TtmlProjectExporter().Export(source, output, LyricsExportOptions.Default);
        XDocument xml = XDocument.Parse(Encoding.UTF8.GetString(output.ToArray()));
        XNamespace styling = "http://www.w3.org/ns/ttml#styling";
        XElement baseSpan = xml.Descendants().Single(element => (string?)element.Attribute(styling + "ruby") == "base");
        XElement textSpan = xml.Descendants().Single(element => (string?)element.Attribute(styling + "ruby") == "text");
        Assert.Equal("0.1s", (string?)baseSpan.Attribute("begin"));
        Assert.Equal("0.2s", (string?)baseSpan.Attribute("end"));
        Assert.Equal("0.7s", (string?)textSpan.Attribute("begin"));
        Assert.Equal("0.9s", (string?)textSpan.Attribute("end"));
        Assert.All(textSpan.Ancestors().Where(element => element.Name.LocalName == "span"), element => Assert.Null(element.Attribute("end")));
        foreach (XAttribute attribute in xml.Descendants().Attributes().Concat(xml.Root!.Attributes()).Where(attribute => attribute.Name.NamespaceName == "urn:makokara:ttml").Distinct().ToArray()) attribute.Remove();
        LyricsImportResult standard = Import(xml.ToString(SaveOptions.DisableFormatting));
        Assert.Equal(new TimeRange(1_700_000, 1_900_000), standard.Document.Lines[0].RubyAnnotations[0].Segments[0].Time);
        Assert.Equal(token.Time, standard.Document.Lines[0].Tokens[0].Time);
    }

    [Fact]
    public void Overlong_output_and_report_are_rejected_before_publishing_any_bytes()
    {
        using MemoryStream output = new();
        LyricsDocument source = LyricsDocument.Create([LyricLine.Create("line", new string('&', 500_000), tokens: [LyricToken.Create("token", new string('&', 500_000))])]);
        Assert.Throws<IOException>(() => new TtmlProjectExporter().Export(source, output, LyricsExportOptions.Default));
        Assert.Equal(0, output.Length);
        LyricsDocument manyIssues = LyricsDocument.Create(Enumerable.Range(0, 2000).Select(index => LyricLine.Create("line-" + index, "无时间")));
        Assert.Throws<IOException>(() => new TtmlAmllExporter().Export(manyIssues, output, LyricsExportOptions.Default));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void Duplicate_identity_is_rejected_by_project_export_before_writing()
    {
        LyricsDocument source = LyricsDocument.Create([LyricLine.Create("same", "字", tokens: [LyricToken.Create("same", "字")])]);
        using MemoryStream output = new();
        Assert.Throws<ArgumentException>(() => new TtmlProjectExporter().Export(source, output, LyricsExportOptions.Default));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void Amll_report_counts_written_skipped_and_dropped_ruby_timing()
    {
        LyricToken token = LyricToken.Create("token", "字");
        RubyAnnotation ruby = new() { Id = "ruby", BaseTokenId = token.Id, Text = "じ", Segments = [RubyTextSegment.Create("segment", "じ", new TimeRange(-2, -1), TimingStatus.Invalid)] };
        LyricLine line = LyricLine.Create("line", "字", new TimeRange(0, 1_000_000), TimingStatus.Imported, [token]) with { RubyAnnotations = [ruby] };
        LyricsDocument source = LyricsDocument.Create([line, line with { Id = "missing-line", Time = null, TimingStatus = TimingStatus.Missing }]);
        using MemoryStream output = new();
        LyricsExportReport report = new TtmlAmllExporter().Export(source, output, LyricsExportOptions.Default);
        Assert.Equal(1, report.WrittenRubySegmentCount);
        Assert.Equal(1, report.SkippedRubySegmentCount);
        Assert.Equal(1, report.DroppedRubySegmentTimeCount);
        Assert.Contains(report.Plan.Losses, loss => loss.Feature == "无效注音片段时间" && loss.Message.Contains("segment=segment", StringComparison.Ordinal));
    }
}
