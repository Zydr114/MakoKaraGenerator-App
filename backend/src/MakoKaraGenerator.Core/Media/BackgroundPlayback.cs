using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Media;

/// <summary>
/// 背景视频的时间映射：工程时间 → 视频内的源时间。
/// </summary>
/// <remarks>
/// 抽成纯函数是为了能被单测盯住：这是"视频短于音频时怎么办"这条规则<b>唯一</b>的实现，
/// 预览与导出都走它（渲染器不自己算）。
/// </remarks>
public static class BackgroundPlayback
{
    /// <summary>把工程时间映射成视频里的源时间。</summary>
    /// <param name="projectTimeUs">工程时间（微秒）。</param>
    /// <param name="videoDurationUs">视频时长（微秒）；未知或非正数时按 0 处理。</param>
    /// <param name="mode">时间策略。</param>
    /// <returns>应当解码的源时间（微秒），一定落在 <c>[0, videoDurationUs)</c> 内。</returns>
    public static long ResolveSourceTimeUs(long projectTimeUs, long videoDurationUs, BackgroundPlaybackMode mode)
    {
        if (projectTimeUs <= 0 || videoDurationUs <= 0)
        {
            return 0;
        }

        return mode switch
        {
            // 停在最后一帧：夹到"最后一微秒"，解码器给出的就是最后一帧。
            BackgroundPlaybackMode.FreezeLastFrame => Math.Min(projectTimeUs, videoDurationUs - 1),

            // 循环：对时长取模。走到正好等于时长时回到 0（否则会要求解码一个不存在的时刻）。
            _ => projectTimeUs % videoDurationUs,
        };
    }
}
