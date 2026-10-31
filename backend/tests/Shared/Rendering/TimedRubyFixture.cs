using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;

internal static class TimedRubyFixture
{
    internal static ProjectSnapshot Create(ImmutableArray<RubyTextSegment> segments = default)
    {
        if (segments.IsDefault) segments =
        [RubyTextSegment.Create("early", "せ", new(100_000, 300_000), TimingStatus.Imported),
            RubyTextSegment.Create("late", "ん", new(700_000, 900_000), TimingStatus.Edited)];
        var ruby = RubyAnnotation.Create("ruby", "token", string.Concat(segments.Select(value => value.Text)))
            with { Segments = segments };
        var line = LyricLine.Create("line", "歌", new(0, 1_000_000), TimingStatus.Imported,
            [LyricToken.Create("token", "歌", new(400_000, 600_000), TimingStatus.Imported)])
            with { RubyAnnotations = [ruby] };
        return ProjectSnapshot.FromProject(Project.Create("Timed ruby acceptance",
            Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 1_000_000), LyricsDocument.Create([line]),
            lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 48,
                Stroke = null, Shadow = null, UnsungFill = new() { Color = new(30, 60, 240, 255) },
                SungFill = new() { Color = new(240, 40, 30, 255) } }));
    }
}
