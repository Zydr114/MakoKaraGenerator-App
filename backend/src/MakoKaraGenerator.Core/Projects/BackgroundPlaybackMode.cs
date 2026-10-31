namespace MakoKaraGenerator.Core.Projects;

/// <summary>
/// 背景视频与音频对不齐时的时间策略（docs/development.md §12.1、roadmap §18.5）。
/// </summary>
/// <remarks>
/// <para>
/// 音频时间轴是主参考，视频只回答"这一刻显示哪一帧"。因此这里定义的是视频<b>不足</b>时的行为：
/// 视频比输出更长时，多出来的部分根本不会被用到（渲染只走到输出时长），不需要另立"裁切"策略。
/// </para>
/// <para>
/// 映射规则在 <see cref="Media.BackgroundPlayback" />（纯函数，可单测）。
/// </para>
/// </remarks>
public enum BackgroundPlaybackMode
{
    /// <summary>循环播放：走到结尾后回到开头。默认。</summary>
    Loop = 0,

    /// <summary>停在最后一帧：走完就冻结在那里，不再动。</summary>
    FreezeLastFrame = 1,
}
