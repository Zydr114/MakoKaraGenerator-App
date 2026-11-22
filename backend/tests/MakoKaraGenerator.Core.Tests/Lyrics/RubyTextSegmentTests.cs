using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

public sealed class RubyTextSegmentTests
{
    [Fact]
    public void 注音工厂为旧文本建立单个无时间兼容片段()
    {
        RubyAnnotation ruby = RubyAnnotation.Create("ruby-1", "token-1", "せん");

        Assert.Equal("せん", ruby.Text);
        Assert.Equal(["ruby-1-segment-1"], ruby.Segments.Select(segment => segment.Id));
        Assert.Equal(TimingStatus.Missing, ruby.Segments[0].TimingStatus);
        Assert.Null(ruby.Segments[0].Time);
    }

    [Fact]
    public void 多个注音片段保持独立时间和拼接投影()
    {
        RubyAnnotation ruby = RubyAnnotation.Create("ruby-1", "token-1", "せん") with
        {
            Segments =
            [
                RubyTextSegment.Create("segment-1", "せ", new TimeRange(10, 20), TimingStatus.Imported),
                RubyTextSegment.Create("segment-2", "ん", new TimeRange(20, 35), TimingStatus.Edited),
            ],
        };

        Assert.True(ruby.IsValid);
        Assert.Equal("せん", string.Concat(ruby.Segments.Select(segment => segment.Text)));
        Assert.Equal(new TimeRange(10, 20), ruby.Segments[0].Time);
        Assert.Equal(new TimeRange(20, 35), ruby.Segments[1].Time);
    }
}
