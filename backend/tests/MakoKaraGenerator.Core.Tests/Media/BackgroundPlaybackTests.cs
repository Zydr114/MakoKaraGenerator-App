using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Media;

/// <summary>背景视频的时间映射测试（roadmap P1「背景视频完整支持」）。</summary>
/// <remarks>
/// 这是"视频短于音频时怎么办"这条规则的唯一实现处：预览与导出都走它，
/// 所以这里断言的是行为本身，而不是某个渲染器的表现。
/// </remarks>
public sealed class BackgroundPlaybackTests
{
    [Fact]
    public void 循环策略走到结尾后回到开头()
    {
        Assert.Equal(500_000, BackgroundPlayback.ResolveSourceTimeUs(2_500_000, 2_000_000, BackgroundPlaybackMode.Loop));
    }

    [Fact]
    public void 循环策略在正好走完时回到第零帧()
    {
        // 边界：等于时长时不能要求解码"第 2 秒"，那一帧不存在。
        Assert.Equal(0, BackgroundPlayback.ResolveSourceTimeUs(2_000_000, 2_000_000, BackgroundPlaybackMode.Loop));
    }

    [Fact]
    public void 冻结策略走完停在最后一微秒()
    {
        Assert.Equal(1_999_999, BackgroundPlayback.ResolveSourceTimeUs(2_500_000, 2_000_000, BackgroundPlaybackMode.FreezeLastFrame));
    }

    [Fact]
    public void 冻结策略在时长内按原时间走()
    {
        Assert.Equal(1_200_000, BackgroundPlayback.ResolveSourceTimeUs(1_200_000, 2_000_000, BackgroundPlaybackMode.FreezeLastFrame));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void 零时刻与负时刻都取第一帧(long projectTimeUs)
    {
        Assert.Equal(0, BackgroundPlayback.ResolveSourceTimeUs(projectTimeUs, 2_000_000, BackgroundPlaybackMode.Loop));
        Assert.Equal(0, BackgroundPlayback.ResolveSourceTimeUs(projectTimeUs, 2_000_000, BackgroundPlaybackMode.FreezeLastFrame));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void 时长未知时一律取第一帧(long durationUs)
    {
        // 探测失败或还没探测到时长时不能崩，也不能反过来要求"最后一帧"。
        Assert.Equal(0, BackgroundPlayback.ResolveSourceTimeUs(3_000_000, durationUs, BackgroundPlaybackMode.FreezeLastFrame));
        Assert.Equal(0, BackgroundPlayback.ResolveSourceTimeUs(3_000_000, durationUs, BackgroundPlaybackMode.Loop));
    }

    [Fact]
    public void 默认策略是循环()
    {
        Assert.Equal(BackgroundPlaybackMode.Loop, BackgroundSource.ForVideo("/tmp/a.mp4").Playback);
        Assert.Equal(BackgroundPlaybackMode.Loop, new BackgroundSource { Kind = BackgroundKind.Video, Path = "/tmp/a.mp4" }.Playback);
    }
}
