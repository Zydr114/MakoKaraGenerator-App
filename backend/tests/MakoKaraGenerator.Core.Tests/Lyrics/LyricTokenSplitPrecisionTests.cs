using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

public sealed class LyricTokenSplitPrecisionTests
{
    [Theory]
    [InlineData(9_007_199_254_740_995L, 4_503_599_627_370_498L)]
    [InlineData(long.MaxValue, 4_611_686_018_427_387_904L)]
    [InlineData(5L, 2L)] [InlineData(7L, 4L)]
    public void 规则估算整数比例保持大区间和偶数舍入(long end, long cut)
    {
        var range = TimeRange.Create(0, end);
        var line = LyricLine.Create("line", "甲乙", range, TimingStatus.Imported,
            [LyricToken.Create("original", "甲乙", range, TimingStatus.Imported)]);
        int id = 0;
        var result = LyricLineTokenSplitter.Apply(line, TokenSplitRule.PerTextElement, true, () => $"new-{++id}");
        Assert.Equal(TimeRange.Create(0, cut), result.Line.Tokens[0].Time);
        Assert.Equal(TimeRange.Create(cut, end), result.Line.Tokens[1].Time);
        Assert.All(result.Line.Tokens, token => Assert.Equal(TimingStatus.Estimated, token.TimingStatus));
        Assert.Equal(2, result.CreatedCount); Assert.Equal(2, result.EstimatedCount);
    }

    [Theory]
    [InlineData(9_007_199_254_740_993L, 3_002_399_751_580_331L)]
    [InlineData(long.MaxValue, 3_074_457_345_618_258_602L)]
    public void 三等分大微秒区间不经过浮点比例(long end, long cut)
    {
        var range = TimeRange.Create(0, end);
        var line = LyricLine.Create("line", "甲乙丙", range, TimingStatus.Imported,
            [LyricToken.Create("original", "甲乙丙", range, TimingStatus.Imported)]);
        int id = 0;
        var result = LyricLineTokenSplitter.Apply(line, TokenSplitRule.PerTextElement, true, () => $"new-{++id}");
        Assert.Equal(TimeRange.Create(0, cut), result.Line.Tokens[0].Time);
        Assert.Equal(cut, result.Line.Tokens[1].Time!.Value.StartUs);
        Assert.Equal(end, result.Line.Tokens[^1].Time!.Value.EndUs);
        Assert.Equal(range, result.Line.Time);
        Assert.Equal("甲乙丙", result.Line.Text);
    }
}
