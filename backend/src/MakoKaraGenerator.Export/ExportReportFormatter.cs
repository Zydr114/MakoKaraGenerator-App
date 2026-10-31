using System.Globalization;
using System.Text;

namespace MakoKaraGenerator.Export;

/// <summary>把导出报告排成给人看的多行文本（对话框与日志共用一份）。</summary>
/// <remarks>
/// 报告是修问题的第一手材料，所以这里刻意写"实际值"而不是"期望值"：
/// 探测到的编码器、探测到的时长差、真实的丢帧数。看不到的地方直接写"未知"。
/// </remarks>
public static class ExportReportFormatter
{
    /// <summary>格式化一份报告。</summary>
    /// <param name="report">报告。</param>
    /// <returns>多行文本。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> 为 <see langword="null"/> 时抛出。</exception>
    public static string Format(ExportReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        StringBuilder text = new();

        text.Append("输出：").AppendLine(report.OutputPath);
        text.Append(CultureInfo.InvariantCulture, $"画面：{report.Width}×{report.Height} @ {report.FrameRate:0.###} fps").AppendLine();
        text.Append(CultureInfo.InvariantCulture, $"时长：{Seconds(report.VideoDurationUs)} 秒（{report.FramesWritten} 帧）").AppendLine();
        text.Append("编码：").Append(report.ContainerName ?? "未知").Append(" / ")
            .Append(report.VideoCodecName ?? "未知").Append(" / ")
            .AppendLine(report.AudioCodecName ?? "无音轨");

        if (report.EncoderSettings is { Length: > 0 } settings)
        {
            text.Append("设置：").AppendLine(settings);
        }

        if (report.AudioSourcePath is { Length: > 0 } audioPath)
        {
            text.Append("音频：").Append(Path.GetFileName(audioPath))
                .Append(report.AudioCopied ? "（复制，无损）" : "（重编码）").AppendLine();
        }

        if (report.AudioDurationUs is { } audioUs)
        {
            long delta = report.AudioVideoDeltaUs ?? 0;

            text.Append(CultureInfo.InvariantCulture,
                $"音视频差：{delta / 1000.0:+0;-0;0} ms（视频 {Seconds(report.VideoDurationUs)} 秒 / 音频 {Seconds(audioUs)} 秒）")
                .AppendLine();
        }

        if (report.OutputDurationUs is { } outputUs)
        {
            text.Append(CultureInfo.InvariantCulture, $"产物时长：{Seconds(outputUs)} 秒").AppendLine();
        }

        if (report.TokenCount > 0)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"逐字时间：{report.TokenCount - report.UnconfirmedTokenCount} / {report.TokenCount} 已确认")
                .AppendLine();
        }

        text.Append(CultureInfo.InvariantCulture,
            $"耗时：{report.Elapsed.TotalSeconds:0.0} 秒").AppendLine();
        text.Append(CultureInfo.InvariantCulture,
            $"大小：{report.OutputByteSize / 1024d / 1024d:0.00} MB").AppendLine();

        if (report.FailedFrames > 0 || report.DroppedFrames > 0)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"丢帧：渲染失败 {report.FailedFrames} 帧，编码器丢帧 {report.DroppedFrames} 帧").AppendLine();
        }

        text.Append("校验：").AppendLine(report.IsVerified ? "通过（能探测到画面，时长符合预期）" : "未通过");

        if (!report.MissingResources.IsEmpty)
        {
            text.AppendLine().AppendLine("缺失资源：");

            foreach (string item in report.MissingResources)
            {
                text.Append("  - ").AppendLine(item);
            }
        }

        if (!report.Warnings.IsEmpty)
        {
            text.AppendLine().AppendLine("提示：");

            foreach (string item in report.Warnings)
            {
                text.Append("  - ").AppendLine(item);
            }
        }

        return text.ToString();
    }

    private static double Seconds(long microseconds) => microseconds / 1_000_000d;
}
