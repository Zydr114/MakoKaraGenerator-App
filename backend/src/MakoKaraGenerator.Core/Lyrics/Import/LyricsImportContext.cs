using System.Text;

namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>
/// 一次导入的外部条件（与格式无关的部分）。
/// </summary>
/// <remarks>
/// 这些值都会影响导入结果，因此必须显式传入而不是让导入器去猜：
/// 例如"最后一行歌词的终点"需要音频时长，而 LRC 本身没有这个信息
/// （docs/development.md §7.2 要求不把推导值伪装成实测值，因此推导会进报告）。
/// </remarks>
public sealed record LyricsImportContext
{
    /// <summary>可选累计准备配额；为空时保持原解析行为与无配额桌面入口。</summary>
    public LyricsImportPreparationGuard? PreparationGuard { get; init; }

    /// <summary>源文件名（含扩展名）。</summary>
    public required string FileName { get; init; }

    /// <summary>音频总时长（微秒）；未知时为 <see langword="null" />。</summary>
    public long? MediaDurationUs { get; init; }

    /// <summary>无法推导行终点时使用的兜底时长（微秒），默认 4 秒。</summary>
    public long FallbackLineDurationUs { get; init; } = 4_000_000L;

    /// <summary>歌词语言标记（可选，写入文档）。</summary>
    public string? Language { get; init; }

    /// <summary>强制使用的文本编码；为 <see langword="null" /> 时自动检测（BOM → UTF-8 → GB18030 → 近似）。</summary>
    public Encoding? EncodingOverride { get; init; }

    /// <summary>导入后按什么规则重新切分文本元素；<see cref="TokenSplitRule.None" /> 表示保持格式原有的粒度。</summary>
    public TokenSplitRule SplitRule { get; init; } = TokenSplitRule.None;

    /// <summary>
    /// 切分时是否顺带按比例估算时间。
    /// </summary>
    /// <remarks>
    /// 默认 <see langword="false" />：切分不猜时间，新元素一律是"未定时"，
    /// 由用户自己打点（docs/development.md §19）。勾选后产生的每个时间都会标
    /// <see cref="TimingStatus.Estimated" /> 并写进导入报告。
    /// </remarks>
    public bool EstimateSplitTimes { get; init; }
}
