using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Projects;

/// <summary><see cref="Composition"/> 的默认值与取值范围测试。</summary>
public sealed class CompositionTests
{
    [Fact]
    public void 默认设置是_1080p_三十帧()
    {
        Composition composition = new();

        Assert.Equal(1920, composition.CanvasWidth);
        Assert.Equal(1080, composition.CanvasHeight);
        Assert.Equal(FrameRate.Fps30, composition.FrameRate);
        Assert.Equal(RgbaColor.Black, composition.BackgroundColor);
        Assert.Null(composition.OutputDurationUs);
        Assert.True(composition.IsValid);
    }

    [Fact]
    public void 输出时长未确定时仍然有效()
    {
        // 新建项目还没有音频，时长未知属于正常状态；导出前才由校验器阻断。
        Assert.True(new Composition { OutputDurationUs = null }.IsValid);
    }

    [Fact]
    public void 输出时长必须为正()
    {
        Assert.True(new Composition { OutputDurationUs = 1 }.IsValid);
        Assert.False(new Composition { OutputDurationUs = 0 }.IsValid);
        Assert.False(new Composition { OutputDurationUs = -1 }.IsValid);
    }

    [Fact]
    public void 画布尺寸必须为正()
    {
        Assert.False(new Composition { CanvasWidth = 0 }.IsValid);
        Assert.False(new Composition { CanvasWidth = -1920 }.IsValid);
        Assert.False(new Composition { CanvasHeight = 0 }.IsValid);
        Assert.False(new Composition { CanvasHeight = -1080 }.IsValid);
    }

    [Fact]
    public void 创建时校验画布尺寸()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Composition.Create(0, 1080, FrameRate.Fps30));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Composition.Create(1920, -1, FrameRate.Fps30));
    }

    [Fact]
    public void 创建时校验输出时长()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Composition.Create(1920, 1080, FrameRate.Fps30, outputDurationUs: 0));
    }

    [Fact]
    public void 创建时可以使用竖屏画布与_NTSC_帧率()
    {
        Composition composition = Composition.Create(
            1080,
            1920,
            FrameRate.NtscVideo,
            RgbaColor.Transparent,
            TimeUs.FromSeconds(90));

        Assert.Equal(1080, composition.CanvasWidth);
        Assert.Equal(1920, composition.CanvasHeight);
        Assert.Equal(FrameRate.NtscVideo, composition.FrameRate);
        Assert.Equal(RgbaColor.Transparent, composition.BackgroundColor);
        Assert.Equal(90_000_000, composition.OutputDurationUs);
        Assert.True(composition.IsValid);
    }

    [Fact]
    public void 未指定背景色时使用不透明黑()
    {
        Composition composition = Composition.Create(1920, 1080, FrameRate.Fps30);

        Assert.Equal(RgbaColor.Black, composition.BackgroundColor);
    }
}
