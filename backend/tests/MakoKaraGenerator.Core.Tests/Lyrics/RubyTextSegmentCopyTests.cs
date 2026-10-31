using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

public sealed class RubyTextSegmentCopyTests
{
    [Fact]
    public void 复制计时注音会为每个片段重新分配标识并平移时间()
    {
        LyricToken token = LyricToken.Create(
            "token-1",
            "所",
            new TimeRange(1_000, 3_000),
            TimingStatus.Edited);
        RubyAnnotation ruby = RubyAnnotation.Create("ruby-1", token.Id, "しょん") with
        {
            Segments =
            [
                RubyTextSegment.Create("segment-1", "しょ", new TimeRange(1_100, 1_800), TimingStatus.Imported),
                RubyTextSegment.Create("segment-2", "ん", new TimeRange(1_800, 2_600), TimingStatus.Edited),
            ],
        };
        LyricLine source = LyricLine.Create(
            "line-1",
            "所",
            new TimeRange(1_000, 3_000),
            TimingStatus.Edited,
            [token]) with
        {
            RubyAnnotations = [ruby],
        };

        int tokenNumber = 10;
        int segmentNumber = 20;
        LyricLine copy = LyricLineDuplicator.Duplicate(
            source,
            "line-2",
            () => "copy-token-" + tokenNumber++,
            () => "copy-segment-" + segmentNumber++);

        RubyAnnotation copiedRuby = Assert.Single(copy.RubyAnnotations);
        Assert.Equal("copy-token-11", copiedRuby.Id);
        Assert.Equal("copy-token-10", copiedRuby.BaseTokenId);
        Assert.Equal(["copy-segment-20", "copy-segment-21"], copiedRuby.Segments.Select(segment => segment.Id));
        Assert.Equal([new TimeRange(3_100, 3_800), new TimeRange(3_800, 4_600)], copiedRuby.Segments.Select(segment => segment.Time));
        Assert.Equal(ruby.Segments.Select(segment => segment.TimingStatus), copiedRuby.Segments.Select(segment => segment.TimingStatus));
        Assert.Equal(copiedRuby.Text, string.Concat(copiedRuby.Segments.Select(segment => segment.Text)));
        HashSet<string> originalIds = ruby.Segments.Select(segment => segment.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(copiedRuby.Segments, segment => Assert.DoesNotContain(segment.Id, originalIds));
    }

    [Fact]
    public void 修改兼容Text会清除旧片段时间并保持投影一致()
    {
        RubyAnnotation ruby = RubyAnnotation.Create("ruby-1", "token-1", "しょん") with
        {
            Segments =
            [RubyTextSegment.Create("segment-1", "しょん", new TimeRange(1_000, 2_000), TimingStatus.Imported)],
        };

        RubyAnnotation changed = ruby with { Text = "かな" };

        RubyTextSegment segment = Assert.Single(changed.Segments);
        Assert.Equal("かな", changed.Text);
        Assert.Equal(changed.Text, string.Concat(changed.Segments.Select(value => value.Text)));
        Assert.Equal("segment-1", segment.Id);
        Assert.Null(segment.Time);
        Assert.Equal(TimingStatus.Missing, segment.TimingStatus);
        Assert.True(changed.IsValid);
    }
}
