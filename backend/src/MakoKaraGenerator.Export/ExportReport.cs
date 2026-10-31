using System.Collections.Immutable;

namespace MakoKaraGenerator.Export;

/// <summary>一次导出的报告（docs/development.md §12.4 要求的内容）。</summary>
/// <remarks>
/// 报告是"这次导出到底发生了什么"的唯一可信记录：界面显示它、日志留它。
/// 因此这里只放<b>实测或可推算</b>的事实，不放"应该会怎样"的推测；
/// 探测不到的地方留空并在 <see cref="Warnings"/> 里说清楚。
/// </remarks>
public sealed record ExportReport
{
    /// <summary>按消费顺序保留全部实际运行报告及发生身份；Warnings继续仅作去重汇总。</summary>
    public ImmutableArray<Rendering.RuntimeDiagnosticOccurrence> DiagnosticOccurrences { get; init; } = [];
    /// <summary>输出文件路径。</summary>
    public required string OutputPath { get; init; }

    /// <summary>产物的容器名（来自探测，例如 <c>mov,mp4,m4a,…</c>）。</summary>
    public string? ContainerName { get; init; }

    /// <summary>产物里视频流的编码器名（例如 <c>h264</c>）。</summary>
    public string? VideoCodecName { get; init; }

    /// <summary>产物里音频流的编码器名（例如 <c>aac</c>）；没有音轨时为 <see langword="null"/>。</summary>
    public string? AudioCodecName { get; init; }

    /// <summary>画布宽度。</summary>
    public int Width { get; init; }

    /// <summary>画布高度。</summary>
    public int Height { get; init; }

    /// <summary>帧率。</summary>
    public double FrameRate { get; init; }

    /// <summary>视频时长（按写出的帧数与帧率算）。</summary>
    public long VideoDurationUs { get; init; }

    /// <summary>源音频时长；没有音频或探测不到时为 <see langword="null"/>。</summary>
    public long? AudioDurationUs { get; init; }

    /// <summary>产物自身的时长（探测得到）。</summary>
    public long? OutputDurationUs { get; init; }

    /// <summary>音视频时长差（视频减音频，带符号）。</summary>
    public long? AudioVideoDeltaUs { get; init; }

    /// <summary>写出的帧数。</summary>
    public int FramesWritten { get; init; }

    /// <summary>编码器报告的丢帧数。</summary>
    public int DroppedFrames { get; init; }

    /// <summary>渲染失败的帧数。</summary>
    /// <remarks>单帧渲染异常不会中断导出：那一帧记为失败、继续往下，最后在报告里说明。</remarks>
    public int FailedFrames { get; init; }

    /// <summary>仍为估算或未确认的逐字时间元素数量。</summary>
    public int UnconfirmedTokenCount { get; init; }

    /// <summary>元素总数。</summary>
    public int TokenCount { get; init; }

    /// <summary>使用的质量档位。</summary>
    public ExportQuality? Quality { get; init; }

    /// <summary>使用的编码器及参数（便于复现）。</summary>
    public string? EncoderSettings { get; init; }

    /// <summary>音频是否直接复制（复制＝无损且快，重编码＝容器不接受源编码）。</summary>
    public bool AudioCopied { get; init; }

    /// <summary>参与导出的音频源路径。</summary>
    public string? AudioSourcePath { get; init; }

    /// <summary>缺失的资源（例如工程记录的音频文件找不到）。</summary>
    public ImmutableArray<string> MissingResources { get; init; } = [];

    /// <summary>警告与提示（编码器 stderr 里的关键行、时长差提示等）。</summary>
    public ImmutableArray<string> Warnings { get; init; } = [];

    /// <summary>整个导出耗时。</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>产物字节数。</summary>
    public long OutputByteSize { get; init; }

    /// <summary>产物是否通过了导出后的校验（能探测、含视频流、时长接近预期）。</summary>
    public bool IsVerified { get; init; }
}
