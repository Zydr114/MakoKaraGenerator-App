using System.Text;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Import;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

public sealed class TtmlTimingSafetyTests
{
    private static LyricsImportResult Import(string body, string parameters = "", LyricsImportPreparationGuard? guard = null)
    {
        using MemoryStream input = new(Encoding.UTF8.GetBytes("<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:ttp=\"http://www.w3.org/ns/ttml#parameter\" " + parameters + "><body>" + body + "</body></tt>"));
        return new TtmlImporter().Import(input, new LyricsImportContext { FileName = "timing.ttml", PreparationGuard = guard });
    }

    [Theory]
    [InlineData("2s", "3s")]
    [InlineData("00:00:02", "00:00:03")]
    [InlineData("2000ms", "3000ms")]
    public void Standard_clock_and_offset_are_parent_relative(string begin, string end)
    {
        LyricsImportResult result = Import("<div begin=\"5s\"><p begin=\"10s\" dur=\"4s\"><span begin=\"" + begin + "\" end=\"" + end + "\">你</span></p></div>");
        LyricLine line = Assert.Single(result.Document.Lines);
        Assert.Equal(15_000_000L, line.Time!.Value.StartUs);
        Assert.Equal(19_000_000L, line.Time.Value.EndUs);
        Assert.Equal(17_000_000L, Assert.Single(line.Tokens).Time!.Value.StartUs);
        Assert.Equal(18_000_000L, line.Tokens[0].Time!.Value.EndUs);
        Assert.Empty(result.Report.DiscardedFeatures);
        Assert.Contains("ttml2-media", result.Report.SourceFormat, StringComparison.Ordinal);
    }

    [Fact]
    public void Seq_uses_previous_active_end_par_uses_parent_begin_and_clips_to_parent()
    {
        LyricsImportResult result = Import("<div begin=\"10s\" dur=\"5s\" timeContainer=\"seq\">"
            + "<p begin=\"1s\" dur=\"1s\">甲</p><p dur=\"2s\" timeContainer=\"par\"><span begin=\"1s\" dur=\"9s\">乙</span></p>"
            + "<p dur=\"4s\">丙</p></div>");
        Assert.Equal([11_000_000L, 12_000_000L, 14_000_000L], result.Document.Lines.Select(line => line.Time!.Value.StartUs));
        Assert.Equal([12_000_000L, 14_000_000L, 15_000_000L], result.Document.Lines.Select(line => line.Time!.Value.EndUs));
        Assert.Equal(13_000_000L, result.Document.Lines[1].Tokens[0].Time!.Value.StartUs);
        Assert.Equal(14_000_000L, result.Document.Lines[1].Tokens[0].Time!.Value.EndUs);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n  ")]
    public void Seq_implicit_structural_duration_ignores_XML_indentation_but_preserves_body_text(string indentation)
    {
        LyricsImportResult result = Import("<div timeContainer=\"seq\">" + indentation
            + "<div>" + indentation + "<p dur=\"1s\" xml:space=\"preserve\"> 一 </p>" + indentation + "</div>" + indentation
            + "<div>" + indentation + "<p dur=\"1s\">二</p>" + indentation + "</div>" + indentation + "</div>");
        Assert.DoesNotContain(result.Report.Issues, issue => issue.Severity == IssueSeverity.Error);
        Assert.Equal([0L, 1_000_000L], result.Document.Lines.Select(line => line.Time!.Value.StartUs));
        Assert.Equal([1_000_000L, 2_000_000L], result.Document.Lines.Select(line => line.Time!.Value.EndUs));
        Assert.Equal(" 一 ", result.Document.Lines[0].Text);
        Assert.Equal("二", result.Document.Lines[1].Text);
    }

    [Fact]
    public void Explicit_end_and_duration_use_earliest_end_and_par_derives_implicit_duration()
    {
        LyricsImportResult result = Import("<div begin=\"2s\"><p begin=\"1s\"><span begin=\"1s\" end=\"4s\" dur=\"1s\">甲</span><span begin=\"3s\" dur=\"2s\">乙</span></p></div>");
        LyricLine line = Assert.Single(result.Document.Lines);
        Assert.Equal(3_000_000L, line.Time!.Value.StartUs);
        Assert.Equal(8_000_000L, line.Time.Value.EndUs);
        Assert.Equal(5_000_000L, line.Tokens[0].Time!.Value.EndUs);
    }

    [Fact]
    public void Frame_multiplier_tick_rate_and_subframes_are_resolved()
    {
        LyricsImportResult result = Import("<div><p begin=\"30f\" dur=\"1s\"><span begin=\"10t\" end=\"20t\">时</span></p>"
            + "<p begin=\"00:00:02:15.1\" dur=\"1s\">帧</p></div>",
            "ttp:frameRate=\"30\" ttp:frameRateMultiplier=\"1000 1001\" ttp:tickRate=\"100\" ttp:subFrameRate=\"2\"");
        Assert.Equal(1_001_000L, result.Document.Lines[0].Time!.Value.StartUs);
        Assert.Equal(1_101_000L, result.Document.Lines[0].Tokens[0].Time!.Value.StartUs);
        Assert.Equal(1_201_000L, result.Document.Lines[0].Tokens[0].Time!.Value.EndUs);
        Assert.Equal(2_517_183L, result.Document.Lines[1].Time!.Value.StartUs);
    }

    [Fact]
    public void Large_integer_microseconds_and_nested_submicrosecond_offsets_do_not_use_double()
    {
        LyricsImportResult result = Import("<div begin=\"9007199254.740991s\"><p begin=\"0.000001s\" dur=\"0.000003s\">精</p></div>");
        Assert.Equal(9_007_199_254_740_992L, Assert.Single(result.Document.Lines).Time!.Value.StartUs);
        Assert.Equal(9_007_199_254_740_995L, result.Document.Lines[0].Time!.Value.EndUs);
    }

    [Fact]
    public void Explicit_amll_marker_uses_absolute_timestamps_and_short_clock()
    {
        LyricsImportResult result = Import("<div><p begin=\"10s\" end=\"12s\"><span begin=\"00:10.100\" end=\"10.5\">甲</span></p></div>",
            "xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" itunes:timing=\"Word\"");
        Assert.Equal(10_100_000L, Assert.Single(Assert.Single(result.Document.Lines).Tokens).Time!.Value.StartUs);
        Assert.Contains("amll-absolute", result.Report.SourceFormat, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-1s", "")]
    [InlineData("1e4s", "")]
    [InlineData("00:60:00", "")]
    [InlineData("00:00:01:30", "ttp:frameRate=\"30\"")]
    [InlineData("9223372036855s", "")]
    [InlineData("1s", "ttp:tickRate=\"0\"")]
    [InlineData("1s", "ttp:frameRateMultiplier=\"1000 0\"")]
    [InlineData("1s", "ttp:timeBase=\"clock\"")]
    public void Invalid_or_unsupported_time_fails_with_error_instead_of_fabricated_timing(string begin, string parameters)
    {
        LyricsImportResult result = Import("<div><p begin=\"" + begin + "\" dur=\"1s\">错</p></div>", parameters);
        Assert.Empty(result.Document.Lines);
        Assert.Contains(result.Report.Issues, issue => issue.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Untimed_ruby_segment_keeps_missing_even_with_timed_ancestors()
    {
        LyricsImportResult result = Import("<div><p begin=\"2s\" dur=\"3s\"><span begin=\"0s\" dur=\"3s\"><span tts:ruby=\"container\">"
            + "<span tts:ruby=\"base\">所</span><span tts:ruby=\"textContainer\"><span tts:ruby=\"text\">しょ</span>"
            + "<span tts:ruby=\"text\" begin=\"0.5s\" dur=\"0.2s\">ん</span></span></span></span></p></div>",
            "xmlns:tts=\"http://www.w3.org/ns/ttml#styling\"");
        RubyAnnotation ruby = Assert.Single(result.Document.Lines[0].RubyAnnotations);
        Assert.Null(ruby.Segments[0].Time);
        Assert.Equal(TimingStatus.Missing, ruby.Segments[0].TimingStatus);
        Assert.Equal(2_500_000L, ruby.Segments[1].Time!.Value.StartUs);
    }

    [Fact]
    public void Unknown_namespace_sidecar_and_agent_are_reported_without_entering_lyric_text()
    {
        LyricsImportResult result = Import("<div><p begin=\"1s\" dur=\"2s\" ttm:agent=\"v1\"><u:wrapper><span begin=\"0s\" dur=\"1s\">正文</span></u:wrapper></p></div>",
            "xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\" xmlns:u=\"urn:unknown\"");
        Assert.Equal("正文", Assert.Single(result.Document.Lines).Text);
        Assert.Contains(result.Report.DiscardedFeatures, feature => feature.Contains("未知命名空间", StringComparison.Ordinal));
        Assert.Contains(result.Report.DiscardedFeatures, feature => feature.Contains("agent", StringComparison.Ordinal));
    }

    [Fact]
    public void Budget_is_charged_before_tree_allocation()
    {
        Assert.Throws<LyricsImportPreparationLimitException>(() => Import("<div><p begin=\"1s\" dur=\"1s\">正文</p></div>", guard: new LyricsImportPreparationGuard(100)));
    }

    [Fact]
    public void Dtd_and_entities_are_prohibited_and_well_formed_prefix_is_reported()
    {
        const string xml = "<!DOCTYPE tt [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><tt xmlns=\"http://www.w3.org/ns/ttml\"><body><p begin=\"0s\" dur=\"1s\">&xxe;</p></body></tt>";
        using MemoryStream input = new(Encoding.UTF8.GetBytes(xml));
        LyricsImportResult result = new TtmlImporter().Import(input, new LyricsImportContext { FileName = "xxe.ttml" });
        Assert.Empty(result.Document.Lines);
        Assert.Contains(result.Report.Issues, issue => issue.Severity == IssueSeverity.Error);
        Assert.DoesNotContain(result.Report.Issues, issue => issue.RawText.Contains("root:", StringComparison.Ordinal));
    }
}
