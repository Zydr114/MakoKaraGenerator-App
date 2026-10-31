using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

/// <summary>
/// 切分规则与按规则重切的测试（ADR-0046）。
/// </summary>
public sealed class TokenSplitTests
{
    private static readonly string[] Samples =
    [
        "夜空中最亮的星",
        "Hello world 你好",
        "你好，世界！",
        "🎵sing 在一起🎶",
        "",
    ];

    private static Func<string> Ids()
    {
        int next = 0;

        return () => $"token-s{++next:D4}";
    }

    private static LyricLine Line(string text, TimeRange? time = null, params LyricToken[] tokens) =>
        LyricLine.Create("line-0001", text, time, time is null ? TimingStatus.Missing : TimingStatus.Edited, tokens);

    [Theory]
    [InlineData("夜空中最亮的星", TokenSplitRule.Mixed, 7)]
    [InlineData("Hello world", TokenSplitRule.Mixed, 2)]
    [InlineData("Hello world 你好", TokenSplitRule.Mixed, 4)]
    [InlineData("你好", TokenSplitRule.PerTextElement, 2)]
    [InlineData("Hello world 你好", TokenSplitRule.LatinWords, 3)]
    [InlineData("Hello world", TokenSplitRule.ByWhitespace, 2)]
    [InlineData("你好，世界", TokenSplitRule.ByPunctuation, 2)]
    [InlineData("夜空中最亮的星", TokenSplitRule.None, 1)]
    public void 规则切出预期的段数(string text, TokenSplitRule rule, int expected)
    {
        Assert.Equal(expected, LyricTokenSplitter.Split(text, rule).Length);
    }

    [Fact]
    public void 任何规则都必须保持文本完整()
    {
        foreach (string sample in Samples)
        {
            foreach (TokenSplitRule rule in Enum.GetValues<TokenSplitRule>())
            {
                string joined = string.Concat(LyricTokenSplitter.Split(sample, rule));

                Assert.Equal(sample, joined);
            }
        }
    }

    [Fact]
    public void 混合规则把标点与空白留给前一个元素()
    {
        // CJK 逐字：每个字一段；标点依附前一个字，不单独成段。
        Assert.Equal(["你", "好，", "世", "界"], LyricTokenSplitter.Split("你好，世界", TokenSplitRule.Mixed).ToArray());

        // 拉丁按词：段尾带着空格，下一次从词首开新段。
        Assert.Equal(["Hello ", "world"], LyricTokenSplitter.Split("Hello world", TokenSplitRule.Mixed).ToArray());
    }

    [Fact]
    public void emoji_不会被拆坏()
    {
        ImmutableArray<string> segments = LyricTokenSplitter.Split("🎵sing", TokenSplitRule.Mixed);

        Assert.Equal(2, segments.Length);
        Assert.Equal("🎵", segments[0]);
    }

    [Fact]
    public void 拆分已定时的元素时第一段保留原区间其余未定时()
    {
        TimeRange range = TimeRange.Create(0L, 3_000_000L);
        LyricLine line = Line("最亮的", range, LyricToken.Create("token-0001", "最亮的", range, TimingStatus.Edited));

        LyricTokenSplitResult result = LyricLineTokenSplitter.Apply(line, TokenSplitRule.Mixed, estimateTimes: false, Ids());

        Assert.Equal(3, result.Line.Tokens.Length);
        Assert.Equal(0L, result.Line.Tokens[0].Time!.Value.StartUs);
        Assert.Equal(3_000_000L, result.Line.Tokens[0].Time!.Value.EndUs);
        Assert.Equal(TimingStatus.Edited, result.Line.Tokens[0].TimingStatus);
        Assert.Null(result.Line.Tokens[1].Time);
        Assert.Equal(TimingStatus.Missing, result.Line.Tokens[1].TimingStatus);
        Assert.Equal(1, result.PreservedCount);
        Assert.Equal(2, result.CreatedCount);
        Assert.Equal(0, result.EstimatedCount);
        Assert.Equal("最亮的", string.Concat(result.Line.Tokens.Select(token => token.Text)));
    }

    [Fact]
    public void 勾选估算时按文本长度比例分配并标为估算()
    {
        TimeRange range = TimeRange.Create(0L, 3_000_000L);
        LyricLine line = Line("最亮的", range, LyricToken.Create("token-0001", "最亮的", range, TimingStatus.Confirmed));

        LyricTokenSplitResult result = LyricLineTokenSplitter.Apply(line, TokenSplitRule.Mixed, estimateTimes: true, Ids());

        Assert.Equal(3, result.Line.Tokens.Length);
        Assert.Equal(0L, result.Line.Tokens[0].Time!.Value.StartUs);
        Assert.Equal(1_000_000L, result.Line.Tokens[0].Time!.Value.EndUs);
        Assert.Equal(1_000_000L, result.Line.Tokens[1].Time!.Value.StartUs);
        Assert.Equal(2_000_000L, result.Line.Tokens[1].Time!.Value.EndUs);
        Assert.Equal(2_000_000L, result.Line.Tokens[2].Time!.Value.StartUs);
        Assert.Equal(3_000_000L, result.Line.Tokens[2].Time!.Value.EndUs);
        Assert.All(result.Line.Tokens, token => Assert.Equal(TimingStatus.Estimated, token.TimingStatus));
        Assert.Equal(3, result.EstimatedCount);
    }

    [Fact]
    public void 行级时间加上无逐字时间时估算覆盖每个新元素()
    {
        TimeRange range = TimeRange.Create(1_000_000L, 3_000_000L);
        LyricLine line = Line(
            "Hello world",
            range,
            LyricToken.Create("token-0001", "H", timingStatus: TimingStatus.Missing),
            LyricToken.Create("token-0002", "e", timingStatus: TimingStatus.Missing),
            LyricToken.Create("token-0003", "l", timingStatus: TimingStatus.Missing),
            LyricToken.Create("token-0004", "l", timingStatus: TimingStatus.Missing),
            LyricToken.Create("token-0005", "o", timingStatus: TimingStatus.Missing),
            LyricToken.Create("token-0006", " ", timingStatus: TimingStatus.Missing),
            LyricToken.Create("token-0007", "w", timingStatus: TimingStatus.Missing),
            LyricToken.Create("token-0008", "o", timingStatus: TimingStatus.Missing),
            LyricToken.Create("token-0009", "r", timingStatus: TimingStatus.Missing),
            LyricToken.Create("token-0010", "l", timingStatus: TimingStatus.Missing),
            LyricToken.Create("token-0011", "d", timingStatus: TimingStatus.Missing));

        LyricTokenSplitResult result = LyricLineTokenSplitter.Apply(line, TokenSplitRule.Mixed, estimateTimes: true, Ids());

        Assert.Equal(["Hello ", "world"], result.Line.Tokens.Select(token => token.Text).ToArray());
        Assert.All(result.Line.Tokens, token => Assert.Equal(TimingStatus.Estimated, token.TimingStatus));
        Assert.Equal(range.StartUs, result.Line.Tokens[0].Time!.Value.StartUs);
        Assert.Equal(range.EndUs, result.Line.Tokens[^1].Time!.Value.EndUs);
        Assert.Equal(2, result.EstimatedCount);
    }

    [Fact]
    public void 切分不改动行的时间与文本()
    {
        TimeRange range = TimeRange.Create(1_000_000L, 4_000_000L);
        LyricLine line = Line("你好", range, LyricToken.Create("token-0001", "你好", range, TimingStatus.Imported));

        LyricTokenSplitResult result = LyricLineTokenSplitter.Apply(line, TokenSplitRule.PerTextElement, estimateTimes: false, Ids());

        Assert.Equal(range.StartUs, result.Line.Time!.Value.StartUs);
        Assert.Equal(range.EndUs, result.Line.Time!.Value.EndUs);
        Assert.Equal("你好", result.Line.Text);
    }

    [Fact]
    public void 完全没变化时返回原实例()
    {
        TimeRange first = TimeRange.Create(0L, 1_000_000L);
        TimeRange second = TimeRange.Create(1_000_000L, 2_000_000L);
        LyricLine line = Line(
            "你好",
            TimeRange.Create(0L, 2_000_000L),
            LyricToken.Create("token-0001", "你", first, TimingStatus.Confirmed),
            LyricToken.Create("token-0002", "好", second, TimingStatus.Confirmed));

        LyricTokenSplitResult result = LyricLineTokenSplitter.Apply(line, TokenSplitRule.Mixed, estimateTimes: false, Ids());

        Assert.Same(line, result.Line);
    }

    [Fact]
    public void 合并两个元素时取区间并集并保持共同状态()
    {
        TimeRange first = TimeRange.Create(0L, 1_000_000L);
        TimeRange second = TimeRange.Create(1_000_000L, 2_000_000L);
        LyricLine line = Line(
            "你好",
            TimeRange.Create(0L, 2_000_000L),
            LyricToken.Create("token-0001", "你", first, TimingStatus.Confirmed),
            LyricToken.Create("token-0002", "好", second, TimingStatus.Confirmed));

        LyricTokenSplitResult result = LyricLineTokenSplitter.Apply(line, TokenSplitRule.LatinWords, estimateTimes: false, Ids());

        Assert.Single(result.Line.Tokens);
        Assert.Equal(0L, result.Line.Tokens[0].Time!.Value.StartUs);
        Assert.Equal(2_000_000L, result.Line.Tokens[0].Time!.Value.EndUs);
        Assert.Equal(TimingStatus.Confirmed, result.Line.Tokens[0].TimingStatus);
    }

    [Fact]
    public void 合并多个来源时状态不同则标为估算()
    {
        LyricLine line = Line(
            "你好",
            TimeRange.Create(0L, 2_000_000L),
            LyricToken.Create("token-0001", "你", TimeRange.Create(0L, 1_000_000L), TimingStatus.Confirmed),
            LyricToken.Create("token-0002", "好", TimeRange.Create(1_000_000L, 2_000_000L), TimingStatus.Imported));

        LyricTokenSplitResult result = LyricLineTokenSplitter.Apply(line, TokenSplitRule.LatinWords, estimateTimes: false, Ids());

        Assert.Equal(TimingStatus.Estimated, result.Line.Tokens[0].TimingStatus);
    }

    [Fact]
    public void 文档级切分只处理指定的行并给出计数()
    {
        LyricsDocument document = LyricsDocument.Create(
        [
            LyricLine.Create("line-0001", "第一句", TimeRange.Create(0L, 1_000_000L), TimingStatus.Imported),
            LyricLine.Create("line-0002", "第二句", TimeRange.Create(1_000_000L, 2_000_000L), TimingStatus.Imported),
        ]);

        LyricsTokenSplitSummary summary = LyricsDocumentTokenSplitter.Apply(
            document,
            TokenSplitRule.PerTextElement,
            estimateTimes: false,
            lineIndices: [1]);

        Assert.Equal(1, summary.ChangedLineCount);
        Assert.Equal(3, summary.CreatedCount);
        Assert.Equal(0, summary.EstimatedCount);
        Assert.Empty(summary.Document.Lines[0].Tokens);
        Assert.Equal(3, summary.Document.Lines[1].Tokens.Length);
    }

    [Fact]
    public void 切分产生的新标识不与现有标识冲突()
    {
        LyricsDocument document = LyricsDocument.Create(
        [
            LyricLine.Create(
                "line-0001",
                "你好",
                TimeRange.Create(0L, 2_000_000L),
                TimingStatus.Imported,
                [LyricToken.Create("token-0001", "你好", TimeRange.Create(0L, 2_000_000L), TimingStatus.Imported)]),
        ]);

        LyricsTokenSplitSummary summary = LyricsDocumentTokenSplitter.Apply(document, TokenSplitRule.PerTextElement, estimateTimes: false);

        Assert.Equal(2, summary.Document.Lines[0].Tokens.Length);
        Assert.Equal("token-0001", summary.Document.Lines[0].Tokens[0].Id);
        Assert.Equal("token-0002", summary.Document.Lines[0].Tokens[1].Id);
        Assert.Equal(2, summary.Document.Lines[0].Tokens.Select(token => token.Id).Distinct(StringComparer.Ordinal).Count());
    }
}
