using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Media;

/// <summary>背景适配几何的测试（三种方式各自的落位规则）。</summary>
public sealed class BackgroundPlacementTests
{
    [Fact]
    public void 完整显示时居中并留边()
    {
        // 200×200 的方图放进 1000×500：按高受限，缩放到 500×500，左右各留 250。
        BackgroundPlacement placement = BackgroundPlacement.Compute(200, 200, 1000, 500, BackgroundFit.Contain);

        AssertRect(placement.Source, 0, 0, 200, 200);
        AssertRect(placement.Destination, 250, 0, 500, 500);
        Assert.False(placement.IsEmpty);
    }

    [Fact]
    public void 完整显示且比例相同时恰好铺满()
    {
        BackgroundPlacement placement = BackgroundPlacement.Compute(2000, 1000, 1000, 500, BackgroundFit.Contain);

        AssertRect(placement.Source, 0, 0, 2000, 1000);
        AssertRect(placement.Destination, 0, 0, 1000, 500);
    }

    [Fact]
    public void 铺满时裁掉超出画布的部分()
    {
        // 同样的方图改成"铺满"：按宽受限，缩放到 1000×1000，纵向只保留中间 500。
        BackgroundPlacement placement = BackgroundPlacement.Compute(200, 200, 1000, 500, BackgroundFit.Cover);

        AssertRect(placement.Source, 0, 50, 200, 100);
        AssertRect(placement.Destination, 0, 0, 1000, 500);
    }

    [Fact]
    public void 原始尺寸小图居中()
    {
        BackgroundPlacement placement = BackgroundPlacement.Compute(100, 100, 1000, 500, BackgroundFit.Original);

        AssertRect(placement.Source, 0, 0, 100, 100);
        AssertRect(placement.Destination, 450, 200, 100, 100);
    }

    [Fact]
    public void 原始尺寸大图裁掉溢出部分()
    {
        BackgroundPlacement placement = BackgroundPlacement.Compute(2000, 1000, 1000, 500, BackgroundFit.Original);

        AssertRect(placement.Source, 500, 250, 1000, 500);
        AssertRect(placement.Destination, 0, 0, 1000, 500);
    }

    [Fact]
    public void 尺寸非正直接报错()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BackgroundPlacement.Compute(0, 100, 1000, 500, BackgroundFit.Contain));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BackgroundPlacement.Compute(100, 100, 1000, 0, BackgroundFit.Contain));
    }

    private static void AssertRect(BackgroundRect actual, float x, float y, float width, float height)
    {
        Assert.Equal(x, actual.X, 3);
        Assert.Equal(y, actual.Y, 3);
        Assert.Equal(width, actual.Width, 3);
        Assert.Equal(height, actual.Height, 3);
    }
}
