using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>
/// 合成设置（画布尺寸、帧率、底色、输出时长）的编辑函数。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="LyricStyleEditor" /> 同一套路：<b>纯函数</b>，不改传入的工程，只返回新实例；
/// 并且<b>没有实际变化时必须返回原实例</b>——"有没有未保存改动"的判据是引用比较
/// （ADR-0025），凭空造一个新实例等于把工程弄脏。
/// </para>
/// <para>
/// 这里<b>不做取值范围校验</b>：按 ADR-0006，领域模型允许表示非法数据，
/// 逐项诊断归校验器（<c>ProjectValidator</c>）。界面在提交前自己拦非法输入，
/// 那属于界面职责而不是模型职责。
/// </para>
/// </remarks>
public static class CompositionEditor
{
    /// <summary>设置画布尺寸。</summary>
    /// <param name="project">原工程。</param>
    /// <param name="canvasWidth">画布宽度（像素）。</param>
    /// <param name="canvasHeight">画布高度（像素）。</param>
    /// <returns>新工程；尺寸没变时返回原实例。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    public static Project SetCanvasSize(Project project, int canvasWidth, int canvasHeight)
    {
        ArgumentNullException.ThrowIfNull(project);

        return Replace(
            project,
            project.Composition with { CanvasWidth = canvasWidth, CanvasHeight = canvasHeight });
    }

    /// <summary>设置输出帧率。</summary>
    /// <param name="project">原工程。</param>
    /// <param name="frameRate">输出帧率。</param>
    /// <returns>新工程；帧率没变时返回原实例。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    public static Project SetFrameRate(Project project, FrameRate frameRate)
    {
        ArgumentNullException.ThrowIfNull(project);

        return Replace(project, project.Composition with { FrameRate = frameRate });
    }

    /// <summary>设置画布底色。</summary>
    /// <param name="project">原工程。</param>
    /// <param name="color">底色。</param>
    /// <returns>新工程；颜色没变时返回原实例。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    public static Project SetBackgroundColor(Project project, RgbaColor color)
    {
        ArgumentNullException.ThrowIfNull(project);

        return Replace(project, project.Composition with { BackgroundColor = color });
    }

    /// <summary>设置输出时长。</summary>
    /// <param name="project">原工程。</param>
    /// <param name="outputDurationUs">输出时长（微秒）；<see langword="null"/> 表示"尚未确定"。</param>
    /// <returns>新工程；时长没变时返回原实例。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    /// <remarks>
    /// 允许设回 <see langword="null"/>：那是"未确定"这个真实状态，不是错误。
    /// 导出前仍为 <see langword="null"/> 会被校验器报成阻断性错误，而不是在这里猜一个值。
    /// </remarks>
    public static Project SetOutputDuration(Project project, long? outputDurationUs)
    {
        ArgumentNullException.ThrowIfNull(project);

        return Replace(project, project.Composition with { OutputDurationUs = outputDurationUs });
    }

    /// <summary>歌词里最后一个已知时间的终点。</summary>
    /// <param name="project">工程。</param>
    /// <returns>末点（微秒）；歌词里没有任何已知时间时为 <see langword="null"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    /// <remarks>
    /// 行时间与元素时间都看：整行歌词可能只有行级时间（普通 LRC），也可能细化到逐字。
    /// <b>缺失时间与非法区间一律跳过</b>——不得为了让"按歌词末尾"有结果而替用户猜一个位置
    /// （docs/development.md §19）。一行都没有时返回 <see langword="null"/>，
    /// 由界面把按钮置灰，而不是给个 0。
    /// </remarks>
    public static long? LyricsEndUs(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);

        long? end = null;

        foreach (LyricBranch branch in project.Lyrics.Branches)
        {
            if (branch.TimingMode == LyricBranchTimingMode.None) continue;
            foreach (LyricLine line in branch.Lines)
            {
                if (branch.TimingMode == LyricBranchTimingMode.FollowPrimary && line.PrimaryLineId is not null) continue;
                end = Latest(end, line.Time);
                foreach (LyricToken token in line.Tokens) end = Latest(end, token.Time);
            }
        }

        return end;
    }

    /// <summary>解析界面上输入的画布边长。</summary>
    /// <param name="text">输入文本，前后空白会被忽略。</param>
    /// <param name="dimension">解析出的边长。</param>
    /// <returns>是正整数时为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 只接受正整数：非数字、负数、零、小数、带千分位或十六进制的写法都返回
    /// <see langword="false"/>，由界面报错并保留原值，绝不静默取整或猜测
    /// （docs/development.md §19）。奇偶性<b>不</b>在这里拦：奇数是合法工程数据，
    /// 只是 H.264 导不出来，界面会另外提示。
    /// </remarks>
    public static bool TryParseCanvasDimension(string? text, out int dimension)
    {
        dimension = 0;

        return !string.IsNullOrWhiteSpace(text)
            && int.TryParse(
                text.AsSpan().Trim(),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out dimension)
            && dimension > 0;
    }

    private static Project Replace(Project project, Composition next) =>
        next == project.Composition ? project : project with { Composition = next };

    private static long? Latest(long? current, TimeRange? candidate)
    {
        if (candidate is not { } range || !range.IsValid)
        {
            return current;
        }

        return current is { } value && value >= range.EndUs ? current : range.EndUs;
    }
}
