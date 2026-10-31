namespace MakoKaraGenerator.Export;

/// <summary>导出流程所处的阶段。</summary>
/// <remarks>
/// 与 docs/development.md §12.4 的流程一一对应，界面因此能说清"现在在干什么"，
/// 而不是只有一个永远停在 99% 的进度条。
/// </remarks>
public enum ExportStage
{
    /// <summary>校验工程。</summary>
    Validating,

    /// <summary>探测音频与背景。</summary>
    Probing,

    /// <summary>逐帧渲染并写入编码器。</summary>
    Rendering,

    /// <summary>合并音频并完成封装。</summary>
    Muxing,

    /// <summary>校验产物。</summary>
    Verifying,

    /// <summary>进入短暂的最终文件提交；此后不再接受取消。</summary>
    Committing,

    /// <summary>已完成。</summary>
    Completed,
}

/// <summary>一次导出的进度快照。</summary>
/// <param name="Stage">当前阶段。</param>
/// <param name="FramesWritten">已写入编码器的帧数。</param>
/// <param name="TotalFrames">总帧数。</param>
/// <param name="Elapsed">已用时间。</param>
public sealed record ExportProgress(
    ExportStage Stage,
    int FramesWritten,
    int TotalFrames,
    TimeSpan Elapsed)
{
    /// <summary>完成比例，范围 0 到 1。</summary>
    /// <remarks>
    /// 只有渲染阶段按帧数算；合并、校验与完成视为已经走完整条流程——
    /// 界面若把它们算成 0，进度条会在最后一刻倒退。
    /// </remarks>
    public double Fraction => Stage switch
    {
        ExportStage.Validating or ExportStage.Probing => 0d,
        ExportStage.Rendering => TotalFrames <= 0
            ? 0d
            : Math.Clamp((double)FramesWritten / TotalFrames, 0d, 1d),
        _ => 1d,
    };

    /// <summary>按已用时间线性外推的剩余时间；样本不足时为 <see langword="null"/>。</summary>
    public TimeSpan? EstimatedRemaining
    {
        get
        {
            double fraction = Fraction;

            // 少于 5% 时的外推几乎全是噪声（第一帧还要建缓存、加载字体）。
            if (Stage != ExportStage.Rendering || fraction < 0.05d)
            {
                return null;
            }

            if (fraction >= 1d)
            {
                return TimeSpan.Zero;
            }

            double totalMs = Elapsed.TotalMilliseconds / fraction;

            return TimeSpan.FromMilliseconds(Math.Max(0d, totalMs - Elapsed.TotalMilliseconds));
        }
    }
}
