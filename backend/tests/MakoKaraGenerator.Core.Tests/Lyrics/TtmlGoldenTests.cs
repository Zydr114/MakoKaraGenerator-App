using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Export;
using MakoKaraGenerator.Core.Lyrics.Import;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

public sealed class TtmlGoldenTests
{
    private static LyricsImportResult Fixture(string name)
    {
        using Stream input = typeof(TtmlGoldenTests).Assembly.GetManifestResourceStream("MakoKaraGenerator.Core.Tests.Lyrics.Fixtures." + name + ".ttml")!;
        return new TtmlImporter().Import(input, new LyricsImportContext { FileName = name + ".ttml" });
    }

    [Fact]
    public void Standard_sequence_golden_has_exact_absolute_microseconds()
    {
        LyricsImportResult result = Fixture("standard-sequence");
        Assert.Equal([4_000_000L, 6_000_000L], result.Document.Lines.Select(line => line.Time!.Value.StartUs));
        Assert.Equal(7_001_000L, result.Document.Lines[1].Time!.Value.EndUs);
        RubyAnnotation ruby = Assert.Single(result.Document.Lines[0].RubyAnnotations);
        Assert.Equal([4_100_000L, 4_400_000L], ruby.Segments.Select(segment => segment.Time!.Value.StartUs));
        Assert.Equal([4_400_000L, 4_600_000L], ruby.Segments.Select(segment => segment.Time!.Value.EndUs));
    }

    [Fact]
    public void Project_golden_preserves_large_raw_values_branch_links_and_invalid_status_with_diagnostic()
    {
        LyricsImportResult first = Fixture("project-v1");
        Assert.DoesNotContain(first.Report.Issues, issue => issue.Severity == IssueSeverity.Error);
        Assert.Equal(9_007_199_254_740_993L, first.Document.Lines[0].Time!.Value.StartUs);
        Assert.Equal("权威正文", first.Document.Lines[0].Text);
        Assert.Equal("main-line", first.Document.Branches[1].Lines[0].PrimaryLineId);
        Assert.Equal(TimingStatus.Invalid, first.Document.Lines[0].RubyAnnotations[0].Segments[0].TimingStatus);
        Assert.Contains(first.Report.Issues, issue => issue.Severity == IssueSeverity.Warning && issue.Message.Contains("Invalid", StringComparison.Ordinal));
        using MemoryStream output = new();
        new TtmlProjectExporter().Export(first.Document, output, LyricsExportOptions.Default);
        output.Position = 0;
        LyricsImportResult second = new TtmlImporter().Import(output, new LyricsImportContext { FileName = "golden-roundtrip.ttml" });
        Assert.Equal(first.Document, second.Document);
    }
}
