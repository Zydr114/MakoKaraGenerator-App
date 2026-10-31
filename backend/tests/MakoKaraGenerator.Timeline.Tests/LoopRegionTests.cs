namespace MakoKaraGenerator.Timeline.Tests;

/// <summary>循环选区几何规则的测试（roadmap §18.4 的「循环选区」）。</summary>
/// <remarks>
/// 这里守的是"哪些状态算有效"：两端由用户分别设置，所以"入点晚于出点"真实可到达，
/// 必须能如实表示并识别出来，而不是在几何层偷偷交换两端。
/// </remarks>
public sealed class LoopRegionTests
{
    [Fact]
    public void 拖拽两个方向都给出有序选区()
    {
        Assert.Equal(new LoopRegion(1_000_000, 3_000_000), LoopRegion.FromDrag(1_000_000, 3_000_000));
        Assert.Equal(new LoopRegion(1_000_000, 3_000_000), LoopRegion.FromDrag(3_000_000, 1_000_000));
    }

    [Fact]
    public void 入点早于出点才有效()
    {
        Assert.True(new LoopRegion(0, 1).IsValid);
        Assert.True(new LoopRegion(1_000, 2_000).IsValid);

        // 两点重合、顺序相反、负起点都不算有效选区。
        Assert.False(new LoopRegion(5_000, 5_000).IsValid);
        Assert.False(new LoopRegion(5_000, 1_000).IsValid);
        Assert.False(new LoopRegion(-1, 1_000).IsValid);
    }

    [Fact]
    public void 选区长度在无效时不报假数()
    {
        Assert.Equal(2_000_000, new LoopRegion(1_000_000, 3_000_000).DurationUs);

        // 顺序相反时不能返回负数：那不是"长度为负的选区"，而是没有选区。
        Assert.Equal(0L, new LoopRegion(3_000_000, 1_000_000).DurationUs);
    }

    [Fact]
    public void 到达出点才算循环该回头()
    {
        LoopRegion region = new(1_000_000, 3_000_000);

        Assert.False(region.HasReachedEnd(1_000_000));
        Assert.False(region.HasReachedEnd(2_999_999));
        Assert.True(region.HasReachedEnd(3_000_000));
        Assert.True(region.HasReachedEnd(9_000_000));
    }

    [Fact]
    public void 无效选区永远不回头()
    {
        // 顺序相反的选区不能"因为位置越过了较大的那个数"就开始循环。
        Assert.False(new LoopRegion(3_000_000, 1_000_000).HasReachedEnd(9_000_000));
    }
}
