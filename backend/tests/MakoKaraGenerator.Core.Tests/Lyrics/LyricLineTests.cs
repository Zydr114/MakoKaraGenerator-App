using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

/// <summary><see cref="LyricLine"/> 的元素管理、默认值与结构相等性测试。</summary>
public sealed class LyricLineTests
{
    private static readonly LyricToken[] ThreeTokens =
    [
        LyricToken.Create("token-001", "你", new TimeRange(1_200_000, 1_700_000), TimingStatus.Confirmed),
        LyricToken.Create("token-002", "好", new TimeRange(1_700_000, 2_300_000), TimingStatus.Confirmed),
        LyricToken.Create("token-003", "吗", new TimeRange(2_300_000, 3_400_000), TimingStatus.Estimated),
    ];

    [Fact]
    public void 未拆分的行没有元素()
    {
        LyricLine line = LyricLine.Create("line-001", "你好吗", new TimeRange(1_200_000, 3_400_000), TimingStatus.Edited);

        Assert.False(line.IsTokenized);
        Assert.Empty(line.Tokens);
        Assert.Equal(2_200_000, line.DurationUs);
    }

    [Fact]
    public void 拆分后的行保留整行文本作为布局依据()
    {
        // docs/development.md §9.4：必须用整行文本做一次整形，再在稳定布局上做逐字裁切。
        LyricLine line = LyricLine.Create(
            "line-001",
            "你好吗",
            new TimeRange(1_200_000, 3_400_000),
            TimingStatus.Edited,
            ThreeTokens);

        Assert.True(line.IsTokenized);
        Assert.Equal(ThreeTokens, line.Tokens);
        Assert.Equal("你好吗", line.Text);
        Assert.Equal(string.Concat(line.Tokens.Select(static t => t.Text)), line.Text);
    }

    [Fact]
    public void 元素列表按给定顺序保留()
    {
        LyricLine line = LyricLine.Create("line-001", "你好吗", tokens: ThreeTokens);

        Assert.Equal(["token-001", "token-002", "token-003"], line.Tokens.Select(static t => t.Id));
    }

    [Fact]
    public void 行内元素标识重复被拒绝()
    {
        LyricToken[] duplicated =
        [
            LyricToken.Create("token-001", "你"),
            LyricToken.Create("token-001", "好"),
        ];

        Assert.Throws<ArgumentException>(() => LyricLine.Create("line-001", "你好", tokens: duplicated));
    }

    [Fact]
    public void 元素默认值被规范化为空序列()
    {
        // 未初始化的 ImmutableArray 直接枚举会抛异常，取值时必须规范化。
        LyricLine line = LyricLine.Create("line-001", "你好吗", tokens: ThreeTokens);
        LyricLine normalized = line with { Tokens = default };

        Assert.Empty(normalized.Tokens);
        Assert.False(normalized.IsTokenized);
    }

    [Fact]
    public void 规范化后的空序列与显式空序列相等()
    {
        LyricLine fromDefault = LyricLine.Create("line-001", "你好吗") with { Tokens = default };
        LyricLine fromEmpty = LyricLine.Create("line-001", "你好吗") with { Tokens = ImmutableArray<LyricToken>.Empty };

        Assert.Equal(fromEmpty, fromDefault);
        Assert.Equal(fromEmpty.GetHashCode(), fromDefault.GetHashCode());
    }

    [Fact]
    public void 结构相等性比较元素内容而不是数组引用()
    {
        LyricLine first = LyricLine.Create("line-001", "你好吗", tokens: ThreeTokens);
        LyricLine second = LyricLine.Create("line-001", "你好吗", tokens: ThreeTokens);

        // 两份元素数组是不同实例，相等性必须按内容判定，否则往返测试与缓存失效都会失效。
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void 元素内容不同则行不相等()
    {
        LyricLine first = LyricLine.Create("line-001", "你好吗", tokens: ThreeTokens);

        LyricLine replaced = first with
        {
            Tokens = [ThreeTokens[0], ThreeTokens[1], ThreeTokens[2] with { Text = "呀" }],
        };

        Assert.NotEqual(first, replaced);
    }

    [Fact]
    public void 行样式改变时行与文档不再相等()
    {
        LyricLine original = LyricLine.Create("line-001", "你好吗", tokens: ThreeTokens);
        LyricLine styled = original with
        {
            StyleOverride = LyricStyleOverride.Empty with { SungColor = new RgbaColor(234, 55, 77) },
        };

        Assert.NotEqual(original, styled);
        Assert.NotEqual(LyricsDocument.Create([original]), LyricsDocument.Create([styled]));
    }

    [Fact]
    public void 元素顺序不同则行不相等()
    {
        LyricLine first = LyricLine.Create("line-001", "你好吗", tokens: ThreeTokens);

        LyricLine reordered = first with
        {
            Tokens = [ThreeTokens[1], ThreeTokens[0], ThreeTokens[2]],
        };

        Assert.NotEqual(first, reordered);
    }

    [Fact]
    public void 行级状态与时间必须自洽()
    {
        Assert.Throws<ArgumentException>(
            () => LyricLine.Create("line-001", "你好吗", null, TimingStatus.Confirmed));

        Assert.Throws<ArgumentException>(
            () => LyricLine.Create("line-001", "你好吗", new TimeRange(0, 1_000), TimingStatus.Missing));
    }

    [Fact]
    public void 空白行标识被拒绝()
    {
        Assert.Throws<ArgumentException>(() => LyricLine.Create(" ", "你好吗"));
    }

    [Fact]
    public void 没有时间的行无法取得时长()
    {
        LyricLine line = LyricLine.Create("line-001", "你好吗");

        Assert.Throws<InvalidOperationException>(() => line.DurationUs);
    }

    [Fact]
    public void with_表达式产生新的不可变行()
    {
        LyricLine original = LyricLine.Create(
            "line-001",
            "你好吗",
            new TimeRange(1_200_000, 3_400_000),
            TimingStatus.Edited,
            ThreeTokens);

        LyricLine edited = original with { TimingStatus = TimingStatus.Confirmed };

        Assert.Equal(TimingStatus.Edited, original.TimingStatus);
        Assert.Equal(TimingStatus.Confirmed, edited.TimingStatus);
        Assert.Equal(original.Tokens, edited.Tokens);
    }
}
