using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

/// <summary>歌词行复制规则的测试（docs/development.md §4.1 P0：新建、删除、复制、合并、拆分）。</summary>
/// <remarks>
/// 两条最要紧的：①时间整体后移，使副本紧跟在原行之后且不与原行重叠；
/// ②"没有时间"的行复制后仍然没有时间——不许为了让副本有个位置而编造时间。
/// </remarks>
public sealed class LyricLineDuplicatorTests
{
    [Fact]
    public void 副本紧跟在原行之后()
    {
        LyricLine source = Line("l1", 4_000_000, 6_000_000);

        LyricLine copy = LyricLineDuplicator.Duplicate(source, "line-1000", TokenIds());

        Assert.Equal(new TimeRange(6_000_000, 8_000_000), copy.Time);
        Assert.Equal(source.TimingStatus, copy.TimingStatus);
    }

    [Fact]
    public void 逐字时间一起平移且相对位置不变()
    {
        LyricLine source = Line(
            "l1",
            4_000_000,
            6_000_000,
            LyricToken.Create("t1", "夜", new TimeRange(4_000_000, 4_500_000), TimingStatus.Edited),
            LyricToken.Create("t2", "空", new TimeRange(4_500_000, 6_000_000), TimingStatus.Edited));

        LyricLine copy = LyricLineDuplicator.Duplicate(source, "line-1000", TokenIds());

        Assert.Equal(2, copy.Tokens.Length);
        Assert.Equal(new TimeRange(6_000_000, 6_500_000), copy.Tokens[0].Time);
        Assert.Equal(new TimeRange(6_500_000, 8_000_000), copy.Tokens[1].Time);
    }

    [Fact]
    public void 没有时间的行复制后仍然没有时间()
    {
        // 关键回归：普通 LRC 导入后行与元素常常都没有时间，
        // 这时给副本编一个"看起来合理"的时间是最糟的处理方式。
        LyricLine source = LyricLine.Create(
            "l1",
            "夜空",
            timingStatus: TimingStatus.Missing,
            tokens: [LyricToken.Create("t1", "夜空", timingStatus: TimingStatus.Missing)]);

        LyricLine copy = LyricLineDuplicator.Duplicate(source, "line-1000", TokenIds());

        Assert.Null(copy.Time);
        Assert.Null(copy.Tokens[0].Time);
        Assert.Equal(TimingStatus.Missing, copy.TimingStatus);
        Assert.Equal(TimingStatus.Missing, copy.Tokens[0].TimingStatus);
    }

    [Fact]
    public void 时间非法的行原样保留()
    {
        LyricLine source = LyricLine.Create(
            "l1",
            "夜空",
            new TimeRange(6_000_000, 4_000_000),
            TimingStatus.Invalid,
            [LyricToken.Create("t1", "夜空", new TimeRange(6_000_000, 4_000_000), TimingStatus.Invalid)]);

        LyricLine copy = LyricLineDuplicator.Duplicate(source, "line-1000", TokenIds());

        Assert.Equal(new TimeRange(6_000_000, 4_000_000), copy.Time);
        Assert.Equal(TimingStatus.Invalid, copy.TimingStatus);
    }

    [Fact]
    public void 文本切分与样式覆盖都照搬()
    {
        LyricLine source = Line("l1", 0, 1_000_000) with
        {
            StyleOverride = new LyricStyleOverride { FontSizePx = 96f },
        };

        LyricLine copy = LyricLineDuplicator.Duplicate(source, "line-1000", TokenIds());

        Assert.Equal("夜空", copy.Text);
        Assert.Equal(source.Tokens.Select(token => token.Text), copy.Tokens.Select(token => token.Text));
        Assert.Equal(96f, copy.StyleOverride?.FontSizePx);
    }

    [Fact]
    public void 每个元素都用新的标识()
    {
        LyricLine source = Line(
            "l1",
            0,
            1_000_000,
            LyricToken.Create("t1", "夜", new TimeRange(0, 500_000), TimingStatus.Edited),
            LyricToken.Create("t2", "空", new TimeRange(500_000, 1_000_000), TimingStatus.Edited));

        int calls = 0;
        LyricLine copy = LyricLineDuplicator.Duplicate(source, "line-1000", () => $"token-{9000 + calls++}");

        Assert.Equal("line-1000", copy.Id);
        Assert.Equal(2, calls);
        Assert.Equal(["token-9000", "token-9001"], copy.Tokens.Select(token => token.Id));
    }

    [Fact]
    public void 复制不会修改原行()
    {
        LyricLine source = Line("l1", 4_000_000, 6_000_000);

        _ = LyricLineDuplicator.Duplicate(source, "line-1000", TokenIds());

        Assert.Equal("l1", source.Id);
        Assert.Equal(new TimeRange(4_000_000, 6_000_000), source.Time);
    }

    [Fact]
    public void 参数不合法时直接报错()
    {
        LyricLine source = Line("l1", 0, 1_000_000);

        Assert.Throws<ArgumentNullException>(() => LyricLineDuplicator.Duplicate(source, "line-1", null!));
        Assert.Throws<ArgumentNullException>(() => LyricLineDuplicator.Duplicate(null!, "line-1", TokenIds()));
        Assert.Throws<ArgumentException>(() => LyricLineDuplicator.Duplicate(source, "  ", TokenIds()));
    }

    private static Func<string> TokenIds()
    {
        int next = 1000;

        return () => $"token-{next++}";
    }

    private static LyricLine Line(
        string id,
        long startUs,
        long endUs,
        params LyricToken[] tokens) => LyricLine.Create(
            id,
            "夜空",
            new TimeRange(startUs, endUs),
            TimingStatus.Edited,
            tokens.Length > 0
                ? [.. tokens]
                : [LyricToken.Create("t1", "夜空", new TimeRange(startUs, endUs), TimingStatus.Edited)]);
}
