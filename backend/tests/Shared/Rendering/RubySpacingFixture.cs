using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;

internal static class RubySpacingFixture
{
    internal static ProjectSnapshot Create(bool timed = true, RubyAlignment alignment = RubyAlignment.Center, int width = 640)
    {
        string[] readings = ["ながいよみかた", "となりのよみ", "さいごのよみ"];
        var tokens = Enumerable.Range(0, 3).Select(i => LyricToken.Create("token-" + i, "歌", new(400_000 + i * 100_000, 500_000 + i * 100_000), TimingStatus.Imported)).ToImmutableArray();
        var rubies = Enumerable.Range(0, 3).Select(i => {
            var ruby = RubyAnnotation.Create("ruby-" + i, tokens[i].Id, readings[i]) with { Alignment = alignment };
            if (!timed) return ruby;
            return ruby with { Segments = [RubyTextSegment.Create("early-" + i, readings[i][..2], new(100_000, 300_000), TimingStatus.Imported),
                RubyTextSegment.Create("late-" + i, readings[i][2..], new(700_000, 900_000), TimingStatus.Edited)] };
        }).ToImmutableArray();
        var line = LyricLine.Create("line", "歌歌歌", new(0, 1_000_000), TimingStatus.Imported, tokens) with { RubyAnnotations = rubies };
        return ProjectSnapshot.FromProject(Project.Create("Ruby spacing acceptance",
            Composition.Create(width, 240, FrameRate.Fps30, outputDurationUs: 1_000_000), LyricsDocument.Create([line]),
            lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 48,
                Stroke = null, Shadow = null, UnsungFill = new() { Color = new(30, 60, 240, 255) },
                SungFill = new() { Color = new(240, 40, 30, 255) } }));
    }
}
