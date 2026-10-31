using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Scripting.Projection;
using RenderingProgress = MakoKaraGenerator.Rendering.ProgressSource;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class TimedRubyProjectionTests
{
    [Fact]
    public void 只读目录完整保留Unicode片段且独立演唱性能不继承基础字或缺失时间()
    {
        var snapshot = TimedRubyFixture.Create([
            RubyTextSegment.Create("early", "e\u0301", new(100_000, 300_000), TimingStatus.Imported),
            RubyTextSegment.Create("missing", "👩‍💻"),
            RubyTextSegment.Create("overlap", "せ", new(100_000, 500_000), TimingStatus.Confirmed)]);
        var line = snapshot.Lyrics.Lines[0]; var annotation = line.RubyAnnotations[0];
        var original = annotation.Segments.ToArray();
        var catalog = ContentProjection.Create(snapshot);
        var ruby = Assert.Single(catalog.Ruby);
        Assert.Equal(annotation.Text, ruby.Text);
        Assert.Equal(original.Select(segment => segment.Id), ruby.Segments.Select(segment => segment.Id));
        Assert.Equal(original.Select(segment => segment.Text), ruby.Segments.Select(segment => segment.Text));
        Assert.Equal(SourceTimingStatus.Confirmed, ruby.Segments[2].TimingStatus);
        Assert.DoesNotContain(typeof(SourceRubySegment).GetProperties(), property => property.PropertyType.Assembly == typeof(RubyTextSegment).Assembly);
        var sample = Rendering.LyricSampler.SampleLine(line, 200_000);
        Assert.Equal(0d, sample.Tokens[0].Progress);
        Assert.Equal(RenderingProgress.FromToken, sample.RubySegments[1].Source);
        Assert.Equal(sample.Tokens[0].Progress, sample.RubySegments[1].Progress);
        var early = ContentProjection.RubySegmentPerformance(original[0], sample.RubySegments[0], 200_000);
        var missing = ContentProjection.RubySegmentPerformance(original[1], sample.RubySegments[1], 200_000);
        var overlap = ContentProjection.RubySegmentPerformance(original[2], sample.RubySegments[2], 200_000);
        Assert.Equal(.5d, early.Progress); Assert.True(early.IsCurrent);
        Assert.Equal(100_000, early.ElapsedUs); Assert.Equal(200_000, early.DurationUs);
        Assert.Equal(.25d, overlap.Progress); Assert.True(overlap.IsCurrent);
        Assert.Null(missing.TimeRange); Assert.Null(missing.Progress); Assert.Null(missing.ElapsedUs);
        Assert.Equal(ProgressSource.Missing, missing.ProgressSource);
        var changed = ruby with { Segments = ruby.Segments.SetItem(0, ruby.Segments[0] with { Text = "edited view" }) };
        Assert.NotEqual(changed.Segments[0].Text, annotation.Segments[0].Text);
        Assert.Equal(original, annotation.Segments.ToArray());
        Assert.Equal("e\u0301", catalog.GetRuby(ruby.Id).Segments[0].Text);
        Assert.Equal(RenderingProgress.FromRubySegment, sample.RubySegments[0].Source);
    }
}
