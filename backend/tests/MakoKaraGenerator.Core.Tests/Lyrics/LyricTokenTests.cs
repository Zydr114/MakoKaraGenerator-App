using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

/// <summary><see cref="LyricToken"/> 的一致性规则测试。</summary>
public sealed class LyricTokenTests
{
    [Fact]
    public void 缺失时间的状态不需要区间()
    {
        LyricToken token = LyricToken.Create("token-001", "你");

        Assert.Equal(TimingStatus.Missing, token.TimingStatus);
        Assert.Null(token.Time);
        Assert.True(token.IsTimeStatusConsistent);
    }

    [Theory]
    [InlineData(TimingStatus.Imported)]
    [InlineData(TimingStatus.Estimated)]
    [InlineData(TimingStatus.Edited)]
    [InlineData(TimingStatus.Confirmed)]
    public void 有时间的状态必须带合法区间(TimingStatus status)
    {
        LyricToken token = LyricToken.Create("token-001", "你", new TimeRange(1_000, 2_000), status);

        Assert.Equal(status, token.TimingStatus);
        Assert.Equal(1_000, token.DurationUs);
        Assert.True(token.IsTimeStatusConsistent);
    }

    [Theory]
    [InlineData(TimingStatus.Imported)]
    [InlineData(TimingStatus.Estimated)]
    [InlineData(TimingStatus.Edited)]
    [InlineData(TimingStatus.Confirmed)]
    public void 有状态但没有区间被拒绝(TimingStatus status)
    {
        Assert.Throws<ArgumentException>(() => LyricToken.Create("token-001", "你", null, status));
    }

    [Fact]
    public void 缺失状态不能带区间()
    {
        Assert.Throws<ArgumentException>(
            () => LyricToken.Create("token-001", "你", new TimeRange(1_000, 2_000), TimingStatus.Missing));
    }

    [Fact]
    public void 非法状态必须带一个非法区间()
    {
        LyricToken token = LyricToken.Create("token-001", "你", new TimeRange(2_000, 1_000), TimingStatus.Invalid);

        Assert.True(token.IsTimeStatusConsistent);
        Assert.Throws<ArgumentException>(
            () => LyricToken.Create("token-002", "你", new TimeRange(1_000, 2_000), TimingStatus.Invalid));
    }

    [Fact]
    public void 空标识被拒绝()
    {
        Assert.Throws<ArgumentException>(() => LyricToken.Create("  ", "你"));
        Assert.Throws<ArgumentException>(() => LyricToken.Create(string.Empty, "你"));
    }

    [Fact]
    public void 空文本允许但由校验器负责报告()
    {
        // 编辑过程中用户可能暂时清空文本，这里不阻断，避免无法表达中间状态。
        LyricToken token = LyricToken.Create("token-001", string.Empty);

        Assert.Equal(string.Empty, token.Text);
    }

    [Fact]
    public void 文本为_null_被拒绝()
    {
        Assert.Throws<ArgumentNullException>(() => LyricToken.Create("token-001", null!));
    }

    [Fact]
    public void 没有时间的元素无法取得时长()
    {
        LyricToken token = LyricToken.Create("token-001", "你");

        Assert.Throws<InvalidOperationException>(() => token.DurationUs);
    }

    [Fact]
    public void 时间与状态组合的真值表()
    {
        TimeRange valid = new(1_000, 2_000);
        TimeRange invalid = new(2_000, 1_000);

        Assert.True(LyricToken.IsTimeStatusPairConsistent(null, TimingStatus.Missing));
        Assert.False(LyricToken.IsTimeStatusPairConsistent(valid, TimingStatus.Missing));

        Assert.True(LyricToken.IsTimeStatusPairConsistent(valid, TimingStatus.Confirmed));
        Assert.False(LyricToken.IsTimeStatusPairConsistent(null, TimingStatus.Confirmed));
        Assert.False(LyricToken.IsTimeStatusPairConsistent(invalid, TimingStatus.Confirmed));

        Assert.True(LyricToken.IsTimeStatusPairConsistent(invalid, TimingStatus.Invalid));
        Assert.False(LyricToken.IsTimeStatusPairConsistent(null, TimingStatus.Invalid));
        Assert.False(LyricToken.IsTimeStatusPairConsistent(valid, TimingStatus.Invalid));
    }

    [Fact]
    public void 相等性按值比较()
    {
        LyricToken first = LyricToken.Create("token-001", "你", new TimeRange(1_000, 2_000), TimingStatus.Confirmed);
        LyricToken second = LyricToken.Create("token-001", "你", new TimeRange(1_000, 2_000), TimingStatus.Confirmed);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, second with { Text = "好" });
        Assert.NotEqual(first, second with { TimingStatus = TimingStatus.Estimated });
        Assert.NotEqual(first, second with { Time = new TimeRange(1_000, 3_000) });
    }

    [Fact]
    public void with_表达式产生新实例且不影响原实例()
    {
        LyricToken original = LyricToken.Create("token-001", "你", new TimeRange(1_000, 2_000), TimingStatus.Confirmed);
        LyricToken edited = original with { TimingStatus = TimingStatus.Edited };

        Assert.Equal(TimingStatus.Confirmed, original.TimingStatus);
        Assert.Equal(TimingStatus.Edited, edited.TimingStatus);
        Assert.Equal(original.Time, edited.Time);
    }
}
