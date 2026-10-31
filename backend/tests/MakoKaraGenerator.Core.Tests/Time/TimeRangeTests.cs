using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Time;

/// <summary><see cref="TimeRange"/> 的约束、进度与集合运算测试（docs/development.md §5.3）。</summary>
public sealed class TimeRangeTests
{
    [Fact]
    public void 合法区间可以被创建()
    {
        TimeRange range = TimeRange.Create(1_200_000, 3_400_000);

        Assert.True(range.IsValid);
        Assert.Equal(1_200_000, range.StartUs);
        Assert.Equal(3_400_000, range.EndUs);
        Assert.Equal(2_200_000, range.DurationUs);
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(100, 100)]
    [InlineData(200, 100)]
    [InlineData(100, 0)]
    public void 非法区间被拒绝(long startUs, long endUs)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeRange.Create(startUs, endUs));
    }

    [Fact]
    public void 非法区间仍然可以被表示以便校验器报告()
    {
        // 关键设计：构造函数不抛异常，否则无法把非法的项目文件读进内存并给出可读诊断。
        TimeRange range = new(200, 100);

        Assert.False(range.IsValid);
        Assert.False(range.TryGetDurationUs(out long durationUs));
        Assert.Equal(0, durationUs);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 101)]
    public void 合法数值对同时被两种判定接受(long startUs, long endUs)
    {
        Assert.True(TimeRange.IsValidPair(startUs, endUs));
        Assert.True(new TimeRange(startUs, endUs).IsValid);
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(100, 100)]
    [InlineData(100, 99)]
    public void 非法数值对同时被两种判定拒绝(long startUs, long endUs)
    {
        Assert.False(TimeRange.IsValidPair(startUs, endUs));
        Assert.False(new TimeRange(startUs, endUs).IsValid);
    }

    [Fact]
    public void 尝试创建非法区间会失败()
    {
        Assert.False(TimeRange.TryCreate(100, 100, out _));
        Assert.True(TimeRange.TryCreate(100, 200, out TimeRange range));
        Assert.Equal(new TimeRange(100, 200), range);
    }

    [Fact]
    public void 非法区间无法计算时长()
    {
        TimeRange range = new(200, 100);

        Assert.Throws<InvalidOperationException>(() => range.DurationUs);
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(1_500)]
    [InlineData(1_999)]
    public void 半开区间包含起点与中间时刻(long timeUs)
    {
        TimeRange range = new(1_000, 2_000);

        Assert.True(range.Contains(timeUs));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    [InlineData(2_000)]
    [InlineData(2_001)]
    public void 半开区间不包含终点与区间外(long timeUs)
    {
        TimeRange range = new(1_000, 2_000);

        Assert.False(range.Contains(timeUs));
    }

    [Theory]
    [InlineData(0, 0d)]
    [InlineData(999, 0d)]
    [InlineData(1_000, 0d)]
    [InlineData(1_500, 0.5d)]
    [InlineData(2_000, 1d)]
    [InlineData(5_000, 1d)]
    public void 进度被钳制在零到一之间(long timeUs, double expected)
    {
        TimeRange range = new(1_000, 2_000);

        Assert.Equal(expected, range.ProgressAt(timeUs), 1e-12);
    }

    [Fact]
    public void 非法区间无法计算进度()
    {
        TimeRange range = new(2_000, 1_000);

        Assert.Throws<InvalidOperationException>(() => range.ProgressAt(1_500));
    }

    [Fact]
    public void 相邻区间不算重叠()
    {
        TimeRange first = new(0, 100);
        TimeRange second = new(100, 200);

        Assert.False(first.Overlaps(second));
        Assert.False(second.Overlaps(first));
        Assert.Null(first.Intersect(second));
    }

    [Fact]
    public void 相交区间可以求出交集()
    {
        TimeRange first = new(0, 100);
        TimeRange second = new(50, 200);

        Assert.True(first.Overlaps(second));
        Assert.Equal(new TimeRange(50, 100), first.Intersect(second));
    }

    [Fact]
    public void 包含关系也能求出交集()
    {
        TimeRange outer = new(0, 1_000);
        TimeRange inner = new(200, 300);

        Assert.True(outer.Overlaps(inner));
        Assert.Equal(inner, outer.Intersect(inner));
    }

    [Fact]
    public void 平移保持时长()
    {
        TimeRange range = new(1_000, 2_000);
        TimeRange shifted = range.Shift(500);

        Assert.Equal(new TimeRange(1_500, 2_500), shifted);
        Assert.Equal(range.DurationUs, shifted.DurationUs);
    }

    [Fact]
    public void 平移可以产生需要校验的非法区间()
    {
        // 拖拽操作可能把区间推出合法范围，这里允许表示，由校验器/拖拽边界逻辑负责拦截。
        TimeRange range = new(1_000, 2_000);

        Assert.False(range.Shift(-1_500).IsValid);
    }

    [Fact]
    public void 单独修改起点或终点()
    {
        TimeRange range = new(1_000, 2_000);

        Assert.Equal(new TimeRange(1_200, 2_000), range.WithStart(1_200));
        Assert.Equal(new TimeRange(1_000, 2_500), range.WithEnd(2_500));
    }

    [Fact]
    public void 相等性按值比较()
    {
        Assert.Equal(new TimeRange(1, 2), new TimeRange(1, 2));
        Assert.NotEqual(new TimeRange(1, 2), new TimeRange(1, 3));
        Assert.Equal(new TimeRange(1, 2).GetHashCode(), new TimeRange(1, 2).GetHashCode());
    }

    [Fact]
    public void 文本形式便于诊断()
    {
        Assert.Equal("[1000, 2000)", new TimeRange(1_000, 2_000).ToString());
    }
}
