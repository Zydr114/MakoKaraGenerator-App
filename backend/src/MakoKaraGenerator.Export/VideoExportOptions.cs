using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Export;

/// <summary>一次视频导出的选项。</summary>
/// <remarks>
/// <para>
/// <b>不含分辨率与帧率</b>：它们来自工程的合成设置（<c>Composition</c>），
/// 导出不该在这里另立一套尺寸——否则预览看到的构图与成片就不是同一件事了。
/// </para>
/// <para>
/// <b>不含音频路径</b>：音频来自 <c>Project.AudioPath</c>（schema v3 起随工程保存），
/// 由导出流程自己探测，避免界面与引擎各存一份。
/// </para>
/// </remarks>
public sealed record VideoExportOptions
{
    /// <summary>输出文件路径。</summary>
    public required string OutputPath { get; init; }

    /// <summary>质量档位。</summary>
    public ExportQuality Quality { get; init; } = ExportQuality.Standard;

    /// <summary>
    /// 输出格式：不透明成片，或透明歌词视频（roadmap `EXPORT-07`）。
    /// </summary>
    /// <remarks>
    /// 透明档会同时改变两件事：画布从全透明开始、不画背景层，以及改用支持 Alpha 的编码器。
    /// 它<b>不改动工程数据</b>——同一次编辑可以两种格式各导一份。
    /// </remarks>
    public ExportVideoFormat Format { get; init; } = ExportVideoFormat.OpaqueMp4;

    /// <summary>是否合并音轨；为 <see langword="false"/> 时只输出画面。</summary>
    public bool IncludeAudio { get; init; } = true;

    /// <summary>已存在时是否覆盖。</summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>单帧渲染质量。</summary>
    /// <remarks>导出默认用终稿画质：预览的草稿档位只是省时间的开关，不该影响成片。</remarks>
    public RenderQuality RenderQuality { get; init; } = RenderQuality.Final;

    /// <summary>整个导出任务的超时；为 <see langword="null"/> 时用 <see cref="DefaultTimeout"/>。</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>受信任务可明确选择音频编码计划；为 <see langword="null"/> 时保留原源编码自动策略。</summary>
    /// <remarks>此选项不授予音频路径访问能力，也不代替调用方对固定来源和完整解码的验证。</remarks>
    public AudioEncodingPlan? AudioEncodingPlanOverride { get; init; }

    /// <summary>把音轨补静音或截断到完整视频帧计划，默认保留原音轨策略。</summary>
    /// <remarks>调用方须先固定并验证实际音频源；不以静音代替读取或解码失败。</remarks>
    public bool NormalizeAudioDuration { get; init; }

    /// <summary>是否把图效果与材质的兜底诊断写成报告文字警告；默认写。</summary>
    /// <remarks>调用方自行收集结构化兜底诊断时关掉，避免同一件事在文字警告里再出现一次。</remarks>
    public bool FallbackWarnings { get; init; } = true;

    /// <summary>默认超时。</summary>
    /// <remarks>六小时：远大于正常成片所需的几分钟，只用来兜住"编码卡死"这类情况。</remarks>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromHours(6d);
}
