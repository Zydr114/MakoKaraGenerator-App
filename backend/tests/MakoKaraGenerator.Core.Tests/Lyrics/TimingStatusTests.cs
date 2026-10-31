using MakoKaraGenerator.Core.Lyrics;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

/// <summary><see cref="TimingStatus"/> 的语义与序列化稳定性测试（docs/development.md §5.4）。</summary>
public sealed class TimingStatusTests
{
    [Fact]
    public void 枚举数值固定以保证持久化稳定()
    {
        // 数值一旦变动就会让旧项目文件里的数字含义错位，必须显式钉住。
        Assert.Equal(0, (int)TimingStatus.Missing);
        Assert.Equal(1, (int)TimingStatus.Imported);
        Assert.Equal(2, (int)TimingStatus.Estimated);
        Assert.Equal(3, (int)TimingStatus.Edited);
        Assert.Equal(4, (int)TimingStatus.Confirmed);
        Assert.Equal(5, (int)TimingStatus.Invalid);
    }

    [Theory]
    [InlineData(TimingStatus.Missing)]
    [InlineData(TimingStatus.Imported)]
    [InlineData(TimingStatus.Estimated)]
    public void 缺失导入或估算都属于未确认(TimingStatus status)
    {
        Assert.True(status.IsUnconfirmed());
        Assert.False(status.IsResolved());
    }

    [Theory]
    [InlineData(TimingStatus.Edited)]
    [InlineData(TimingStatus.Confirmed)]
    [InlineData(TimingStatus.Invalid)]
    public void 已修改已确认和非法都不算未确认(TimingStatus status)
    {
        Assert.False(status.IsUnconfirmed());
    }

    [Theory]
    [InlineData(TimingStatus.Edited)]
    [InlineData(TimingStatus.Confirmed)]
    public void 已修改与已确认可以视为精确值(TimingStatus status)
    {
        Assert.True(status.IsResolved());
    }

    [Fact]
    public void 导入状态不算已确认()
    {
        // 导入的逐字时间可能来自自动对齐，不得当作精确数据展示（docs/development.md §5.4）。
        Assert.True(TimingStatus.Imported.IsUnconfirmed());
        Assert.False(TimingStatus.Imported.IsResolved());
    }

    [Fact]
    public void 只有缺失状态不需要时间区间()
    {
        foreach (TimingStatus status in Enum.GetValues<TimingStatus>())
        {
            if (status == TimingStatus.Missing)
            {
                Assert.False(status.RequiresTimeRange());
            }
            else
            {
                Assert.True(status.RequiresTimeRange());
            }
        }
    }

    [Fact]
    public void 每个状态都有中文说明()
    {
        foreach (TimingStatus status in Enum.GetValues<TimingStatus>())
        {
            string name = status.ToDisplayName();

            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.NotEqual("未知", name);
        }
    }
}
