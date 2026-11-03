using System.Text;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Import;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

public sealed class AssMakoCompatibilityTests
{
    private const string Header = """
        [Script Info]
        ; Mako LRC Studio: Dialogue lines with Effect fx are the rendered layout; Comment lines with Effect karaoke are the timing source.
        [Events]
        Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
        """ + "\n";

    [Fact]
    public void MakoSourceIsPreferredAndRubySyllablesRetainTiming()
    {
        var result = Import(Header + """
            Dialogue: 1,0:00:00.00,0:00:04.00,Mako-Voice,Voice,0,0,0,fx,{\kf100}海
            Dialogue: 2,0:00:00.00,0:00:04.00,Mako-Voice-Ruby,Voice,0,0,0,fx,{\kf50}う{\kf50}み
            Comment: 0,0:00:01.00,0:00:03.00,Mako-Voice,Voice,0,0,0,karaoke,{\k50}海|う{\k50}#|み{\k100}へ
            """);

        LyricLine line = Assert.Single(result.Document.Lines);
        Assert.Equal("海へ", line.Text);
        Assert.Equal(2, line.Tokens.Length);
        Assert.Equal(1_000_000, line.Tokens[0].Time!.Value.StartUs);
        Assert.Equal(2_000_000, line.Tokens[0].Time!.Value.EndUs);
        Assert.Equal(2_000_000, line.Tokens[1].Time!.Value.StartUs);
        var ruby = Assert.Single(line.RubyAnnotations);
        Assert.Equal(line.Tokens[0].Id, ruby.BaseTokenId);
        Assert.Equal("うみ", ruby.Text);
        Assert.Equal(2, ruby.Segments.Length);
        Assert.Equal(1_000_000, ruby.Segments[0].Time!.Value.StartUs);
        Assert.Equal(1_500_000, ruby.Segments[0].Time!.Value.EndUs);
        Assert.Equal(1_500_000, ruby.Segments[1].Time!.Value.StartUs);
        Assert.Equal(2_000_000, ruby.Segments[1].Time!.Value.EndUs);
    }

    [Fact]
    public void MakoEmptyKaraokeEventsAdvanceTime()
    {
        var result = Import(Header + """
            Comment: 0,0:00:01.00,0:00:04.00,Mako-Voice,Voice,0,0,0,karaoke,{\k50}{\k50}海|う{\k25}{\k25}#|み{\k50}へ
            """);

        LyricLine line = Assert.Single(result.Document.Lines);
        Assert.Equal("海へ", line.Text);
        Assert.Equal(1_500_000, line.Tokens[0].Time!.Value.StartUs);
        Assert.Equal(2_500_000, line.Tokens[0].Time!.Value.EndUs);
        Assert.Equal(2_250_000, line.RubyAnnotations[0].Segments[1].Time!.Value.StartUs);
    }

    [Fact]
    public void MakoBareContinuationDoesNotBecomeVisibleText()
    {
        var result = Import(Header + """
            Comment: 0,0:00:01.00,0:00:03.00,Mako-Voice,Voice,0,0,0,karaoke,{\k50}海{\k50}#{\k50}へ
            """);

        LyricLine line = Assert.Single(result.Document.Lines);
        Assert.Equal("海へ", line.Text);
        Assert.Equal(2_000_000, line.Tokens[0].Time!.Value.EndUs);
        Assert.Equal(2_000_000, line.Tokens[1].Time!.Value.StartUs);
    }

    [Fact]
    public void MakoBareContinuationExtendsLastRubySegmentAndAdvancesFollowingWord()
    {
        var result = Import(Header + """
            Comment: 0,0:00:01.00,0:00:03.00,Mako-Voice,Voice,0,0,0,karaoke,{\k25}海|う{\k25}#|み{\k50}#{\k50}へ
            """);
        LyricLine line = Assert.Single(result.Document.Lines);
        Assert.Equal("海へ", line.Text);
        Assert.Equal(2_000_000, line.Tokens[1].Time!.Value.StartUs);
        Assert.Equal(2_000_000, line.RubyAnnotations[0].Segments[1].Time!.Value.EndUs);
    }

    [Fact]
    public void NormalDialoguePreservesLiteralPipesAndIgnoresComments()
    {
        var result = Import(Header + """
            Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\k100}A|B
            Comment: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,not a lyric
            """);
        Assert.Equal("A|B", Assert.Single(result.Document.Lines).Text);
    }

    private static LyricsImportResult Import(string content)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(content));
        return new AssImporter().Import(stream, new LyricsImportContext { FileName = "synthetic.ass" });
    }
}
