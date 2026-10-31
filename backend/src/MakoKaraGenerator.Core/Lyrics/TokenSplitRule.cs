namespace MakoKaraGenerator.Core.Lyrics;

/// <summary>
/// 按什么粒度把一行歌词切成文本元素。
/// </summary>
/// <remarks>
/// <para>
/// <b>切分与打点是两件事</b>（docs/development.md §5.2、timeline-ui.md §4.2）：
/// 本枚举只决定"边界落在哪里"，不决定"每个元素什么时候唱"。
/// 由规则切出来的新元素默认没有时间，需要用户自己打点；
/// 只有显式要求估算时才会按比例分配并标 <see cref="TimingStatus.Estimated" />。
/// </para>
/// <para>
/// 规则的作用域是<b>行文本</b>：任何规则切出来的元素文本依次拼接都<b>必须等于</b>行文本，
/// 包括空白与标点（它们依附前一个元素）。这条不变式由
/// <see cref="LyricTokenSplitter" /> 的测试钉住。
/// </para>
/// </remarks>
public enum TokenSplitRule
{
    /// <summary>不切分：保持格式原有的元素粒度。</summary>
    None = 0,

    /// <summary>混合（默认推荐）：CJK 逐字，拉丁字母与数字按词，标点与空白依附前一个元素。</summary>
    Mixed = 1,

    /// <summary>每个 Unicode 文本元素一个元素（拉丁字母也逐字母；与手动拆分的手感一致）。</summary>
    PerTextElement = 2,

    /// <summary>按词：连续的拉丁字母/数字算一个元素，连续的 CJK 也算一个元素（适合"整句一元素"）。</summary>
    LatinWords = 3,

    /// <summary>按标点：遇到标点后在下一个元素处切（逗号、句号、顿号等）。</summary>
    ByPunctuation = 4,

    /// <summary>按空白：遇到空白后在下一个元素处切。</summary>
    ByWhitespace = 5,
}
