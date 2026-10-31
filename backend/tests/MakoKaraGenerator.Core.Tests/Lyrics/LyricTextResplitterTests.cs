using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

/// <summary>改行文本时重建逐字元素的测试。</summary>
public sealed class LyricTextResplitterTests
{
    [Fact]
    public void 文本没变时原样返回()
    {
        ImmutableArray<LyricToken> tokens = Tokens(("t1", "夜空", 1_000_000, 3_000_000));

        LyricTextResplitResult result = Resplit("夜空", tokens, "夜空");

        // 文本没变时应当连数组都不重建（ImmutableArray 是值类型，比较元素引用即可）。
        Assert.Equal(tokens.Length, result.Tokens.Length);
        Assert.Same(tokens[0], result.Tokens[0]);
        Assert.Equal(1, result.PreservedCount);
        Assert.Equal(0, result.ResetCount);
    }

    [Fact]
    public void 往中间插入字符时两侧的时间都保留()
    {
        // 这是最常见的场景：打好了"夜空最亮的星"，发现漏了"中"。
        ImmutableArray<LyricToken> tokens = Tokens(
            ("t1", "夜", 1_000_000, 2_000_000),
            ("t2", "空", 2_000_000, 3_000_000),
            ("t3", "最", 3_000_000, 4_000_000),
            ("t4", "亮", 4_000_000, 5_000_000),
            ("t5", "的", 5_000_000, 6_000_000),
            ("t6", "星", 6_000_000, 7_000_000));

        LyricTextResplitResult result = Resplit("夜空最亮的星", tokens, "夜空中最亮的星");

        Assert.Equal("夜空中最亮的星", string.Concat(result.Tokens.Select(static token => token.Text)));
        Assert.Equal(2 + 4, result.PreservedCount);
        Assert.Equal(1, result.ResetCount);

        // 插入的"中"没有时间，且状态如实标成"缺失"。
        LyricToken inserted = result.Tokens[2];

        Assert.Equal("中", inserted.Text);
        Assert.Null(inserted.Time);
        Assert.Equal(TimingStatus.Missing, inserted.TimingStatus);

        // 两侧的时间没有被错位：原来"空"的时间仍在"空"上，"最"的时间仍在"最"上。
        Assert.Equal(2_000_000L, result.Tokens[1].Time!.Value.StartUs);
        Assert.Equal(3_000_000L, result.Tokens[3].Time!.Value.StartUs);
    }

    [Fact]
    public void 改错字只把改动处标成未定时()
    {
        ImmutableArray<LyricToken> tokens = Tokens(
            ("t1", "夜", 1_000_000, 2_000_000),
            ("t2", "空", 2_000_000, 3_000_000),
            ("t3", "星", 3_000_000, 4_000_000));

        LyricTextResplitResult result = Resplit("夜空心", tokens, "夜空中");

        Assert.Equal("夜空中", string.Concat(result.Tokens.Select(static token => token.Text)));

        // 前缀"夜空"保留，末位被改掉的那个字变成未定时。
        Assert.Equal("夜", result.Tokens[0].Text);
        Assert.NotNull(result.Tokens[0].Time);
        Assert.Equal("空", result.Tokens[1].Text);
        Assert.NotNull(result.Tokens[1].Time);
        Assert.Equal("中", result.Tokens[2].Text);
        Assert.Null(result.Tokens[2].Time);
    }

    [Fact]
    public void 在末尾追加字符时原有时间全部保留()
    {
        ImmutableArray<LyricToken> tokens = Tokens(
            ("t1", "夜", 1_000_000, 2_000_000),
            ("t2", "空", 2_000_000, 3_000_000));

        LyricTextResplitResult result = Resplit("夜空", tokens, "夜空中");

        Assert.Equal(2, result.PreservedCount);
        Assert.Equal(1, result.ResetCount);
        Assert.Same(tokens[0], result.Tokens[0]);
        Assert.Null(result.Tokens[2].Time);
    }

    [Fact]
    public void 合并过的多字元素在未改动时保持合并()
    {
        // "夜空"是一个多文本元素合并出来的元素：它没被改动就不该被拆开重来。
        ImmutableArray<LyricToken> tokens = Tokens(
            ("t1", "夜空", 1_000_000, 3_000_000),
            ("t2", "星", 3_000_000, 4_000_000));

        LyricTextResplitResult result = Resplit("夜空星", tokens, "夜空与星");

        Assert.Equal("夜空与星", string.Concat(result.Tokens.Select(static token => token.Text)));
        Assert.Same(tokens[0], result.Tokens[0]);
        Assert.Same(tokens[1], result.Tokens[2]);
        Assert.Null(result.Tokens[1].Time);
    }

    [Fact]
    public void 跨过改动边界的老元素整体让位()
    {
        // "夜空"的一半被改掉：它不能整体保留，只能退成未定时；
        // 但没被改到的"夜"依然要在，不能连字一起丢（这正是分批拼接写法会犯的错）。
        ImmutableArray<LyricToken> tokens = Tokens(
            ("t1", "夜空", 1_000_000, 3_000_000),
            ("t2", "中", 3_000_000, 4_000_000));

        LyricTextResplitResult result = Resplit("夜空中", tokens, "夜里中");

        Assert.Equal("夜里中", string.Concat(result.Tokens.Select(static token => token.Text)));
        Assert.Equal(3, result.Tokens.Length);
        Assert.Same(tokens[1], result.Tokens[2]);
        Assert.NotNull(result.Tokens[2].Time);
        Assert.All(result.Tokens[..2], static token => Assert.Null(token.Time));
    }

    [Fact]
    public void 清空文本会得到没有元素的行()
    {
        ImmutableArray<LyricToken> tokens = Tokens(("t1", "夜", 1_000_000, 2_000_000));

        LyricTextResplitResult result = Resplit("夜", tokens, string.Empty);

        Assert.Empty(result.Tokens);
        Assert.Equal(0, result.PreservedCount);
    }

    [Fact]
    public void 从空文本输入内容时全部是未定时()
    {
        LyricTextResplitResult result = Resplit(string.Empty, [], "夜空");

        Assert.Equal(2, result.Tokens.Length);
        Assert.Equal(0, result.PreservedCount);
        Assert.Equal(2, result.ResetCount);
        Assert.All(result.Tokens, static token => Assert.Equal(TimingStatus.Missing, token.TimingStatus));
    }

    [Fact]
    public void 新元素拿到调用方给的标识且不重复()
    {
        int counter = 0;

        LyricTextResplitResult result = LyricTextResplitter.Resplit(
            string.Empty,
            [],
            "夜空",
            () => $"new-{counter++}");

        Assert.Equal(["new-0", "new-1"], result.Tokens.Select(static token => token.Id).ToArray());
    }

    [Fact]
    public void emoji与组合字符不会被拆坏()
    {
        // 逐字必须按 Unicode 文本元素切（docs §5.2）：🇯🇵 是单个旗帜，不能当两个字符。
        LyricTextResplitResult result = LyricTextResplitter.Resplit(
            string.Empty,
            [],
            "🇯🇵a",
            () => "t");

        Assert.Equal(2, result.Tokens.Length);
        Assert.Equal("🇯🇵", result.Tokens[0].Text);
    }

    private static LyricTextResplitResult Resplit(
        string oldText,
        ImmutableArray<LyricToken> oldTokens,
        string newText) =>
        LyricTextResplitter.Resplit(oldText, oldTokens, newText, () => "new");

    private static ImmutableArray<LyricToken> Tokens(
        params (string Id, string Text, long StartUs, long EndUs)[] tokens) =>
        [.. tokens.Select(static token => LyricToken.Create(
            token.Id,
            token.Text,
            new TimeRange(token.StartUs, token.EndUs),
            TimingStatus.Edited))];
}
