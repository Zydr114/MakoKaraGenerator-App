namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>
/// 增强 LRC 的逐字解析策略。
/// </summary>
/// <remarks>
/// 增强 LRC 的逐字标记在工具之间并不统一（docs/development.md §7.2），
/// 因此凡是要"猜"的地方都做成显式选项，并由导入报告说明实际采用的行为。
/// </remarks>
public sealed record EnhancedLrcOptions
{
    /// <summary>默认策略。</summary>
    public static EnhancedLrcOptions Default { get; } = new();

    /// <summary>逐字时间标签的开始字符，默认 <c>&lt;</c>。</summary>
    public char TagOpen { get; init; } = '<';

    /// <summary>逐字时间标签的结束字符，默认 <c>&gt;</c>。</summary>
    public char TagClose { get; init; } = '>';

    /// <summary>
    /// 标签之间的空白是否单独成为一个"不带时间"的元素。
    /// </summary>
    /// <remarks>
    /// 默认开启：这样 <c>&lt;t&gt;夜 &lt;t2&gt;空中</c> 里的空格不会被打上时间，
    /// 用户看到的时间粒度也不会包含分隔用的空白。
    /// 关闭时空白会并入前一个元素的文本。
    /// </remarks>
    public bool WhitespaceIsUntimed { get; init; } = true;
}
