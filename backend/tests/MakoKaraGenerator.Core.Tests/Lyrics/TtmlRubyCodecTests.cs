using System.Text;
using System.Xml.Linq;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Export;
using MakoKaraGenerator.Core.Lyrics.Import;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

public sealed class TtmlRubyCodecTests
{
    [Fact]
    public void Importer_reads_all_divs_and_preserves_timed_ruby_segments()
    {
        const string source = "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:mkg=\"urn:makokara:ttml\" mkg:timing-profile=\"amll-absolute\" xmlns:tts=\"http://www.w3.org/ns/ttml#styling\"><body>"
            + "<div><p begin=\"1s\" end=\"3s\"><span begin=\"1s\" end=\"3s\"><span tts:ruby=\"container\"><span tts:ruby=\"base\">所</span><span tts:ruby=\"textContainer\"><span tts:ruby=\"text\" begin=\"1.1s\" end=\"1.4s\">しょ</span><span tts:ruby=\"text\" begin=\"1.5s\" end=\"1.8s\">ん</span></span></span></span></p></div>"
            + "<div><p begin=\"4s\" end=\"5s\"><span begin=\"4s\" end=\"5s\">別</span></p></div></body></tt>";
        using MemoryStream input = new(Encoding.UTF8.GetBytes(source));
        LyricsImportResult result = new TtmlImporter().Import(input, new LyricsImportContext { FileName = "ruby.ttml" });

        Assert.Equal(2, result.Document.Lines.Length);
        RubyAnnotation ruby = Assert.Single(result.Document.Lines[0].RubyAnnotations);
        Assert.Equal("しょん", ruby.Text);
        Assert.Equal(2, ruby.Segments.Length);
        Assert.Equal(1_100_000L, ruby.Segments[0].Time!.Value.StartUs);
        Assert.Equal(1_800_000L, ruby.Segments[1].Time!.Value.EndUs);
    }

    [Fact]
    public void Exporter_writes_project_ruby_tree_and_amll_reports_other_branches()
    {
        LyricToken token = LyricToken.Create("token", "所", TimeRange.Create(1_000_000, 3_000_000), TimingStatus.Imported);
        LyricLine line = LyricLine.Create("line", "所", TimeRange.Create(1_000_000, 3_000_000), TimingStatus.Imported, [token]) with
        {
            RubyAnnotations = [new RubyAnnotation
            {
                Id = "ruby", BaseTokenId = "token", Text = "しょん", Segments =
                [RubyTextSegment.Create("seg1", "しょ", TimeRange.Create(1_100_000, 1_400_000), TimingStatus.Imported), RubyTextSegment.Create("seg2", "ん", TimeRange.Create(1_500_000, 1_800_000), TimingStatus.Imported)]
            }]
        };
        LyricsDocument document = LyricsDocument.Create([line]);
        using MemoryStream output = new();
        LyricsExportReport report = new TtmlExporter(TtmlExportProfile.Amll).Export(document, output, LyricsExportOptions.Default);
        XDocument parsed = XDocument.Parse(Encoding.UTF8.GetString(output.ToArray()));
        XNamespace ttml = "http://www.w3.org/ns/ttml";
        XNamespace tts = "http://www.w3.org/ns/ttml#styling";
        XElement container = Assert.Single(parsed.Descendants(ttml + "p").Single().Elements(ttml + "span"));
        Assert.Equal("container", (string?)container.Attribute(tts + "ruby"));
        XElement baseSpan = Assert.Single(container.Elements(ttml + "span"), element => (string?)element.Attribute(tts + "ruby") == "base");
        Assert.Equal("1s", (string?)baseSpan.Attribute("begin"));
        Assert.Equal(2, parsed.Descendants(ttml + "span").Count(element => (string?)element.Attribute(tts + "ruby") == "text"));
        Assert.Equal(1, report.ExportedLineCount);
        Assert.Contains(report.Plan.Losses, loss => loss.Feature == "项目身份和时间状态");
    }

    [Fact]
    public void Import_export_import_preserves_ruby_segment_times()
    {
        const string source = "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:mkg=\"urn:makokara:ttml\" mkg:timing-profile=\"amll-absolute\" xmlns:tts=\"http://www.w3.org/ns/ttml#styling\"><body><div>"
            + "<p begin=\"1s\" end=\"3s\"><span begin=\"1s\" end=\"3s\"><span tts:ruby=\"container\"><span tts:ruby=\"base\">所</span>"
            + "<span tts:ruby=\"textContainer\"><span tts:ruby=\"text\" begin=\"1.1s\" end=\"1.4s\">しょ</span>"
            + "<span tts:ruby=\"text\" begin=\"1.5s\" end=\"1.8s\">ん</span></span></span></span></p></div></body></tt>";
        LyricsImportResult first;
        using (MemoryStream input = new(Encoding.UTF8.GetBytes(source)))
            first = new TtmlImporter().Import(input, new LyricsImportContext { FileName = "roundtrip.ttml" });

        using MemoryStream output = new();
        LyricsExportReport report = new TtmlProjectExporter().Export(first.Document, output, LyricsExportOptions.Default);
        Assert.False(report.Plan.HasLosses);
        using MemoryStream secondInput = new(output.ToArray());
        LyricsImportResult second = new TtmlImporter().Import(secondInput, new LyricsImportContext { FileName = "roundtrip.ttml" });

        RubyTextSegment[] segments = [.. second.Document.Lines[0].RubyAnnotations[0].Segments];
        Assert.Equal([1_100_000L, 1_500_000L], segments.Select(segment => segment.Time!.Value.StartUs));
        Assert.Equal([1_400_000L, 1_800_000L], segments.Select(segment => segment.Time!.Value.EndUs));
        Assert.Equal(first.Document.Lines[0].Text, second.Document.Lines[0].Text);
    }
}
