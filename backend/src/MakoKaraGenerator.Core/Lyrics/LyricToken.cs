using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Lyrics;

/// <summary>
/// 一个文本元素（"逐字"的最小单位）。
/// </summary>
/// <remarks>
/// <para>
/// 元素<b>不是</b> UTF-16 <see cref="char"/>。按 docs/development.md §5.2，
/// 元素可以是一个汉字、一个英文单词、一个 emoji 或用户自定义的多个字符片段；
/// 分割边界由导入格式、用户手工调整或 Unicode grapheme cluster 决定。
/// 本类型只保存原始文本，不改变用户输入。
/// </para>
/// <para>
/// 本类型是<b>不可变</b>的。编辑操作通过 <c>with</c> 表达式产生新实例，
/// 使撤销/重做与渲染快照不需要额外的变更通知机制（docs/development.md §10.2、roadmap CORE-07）。
/// </para>
/// </remarks>
public sealed record LyricToken
{
    /// <summary>元素标识，在同一文档内必须唯一。</summary>
    public required string Id { get; init; }

    /// <summary>元素的原始文本。允许为空字符串（校验器会把空文本报为错误，但编辑过程中允许暂时为空）。</summary>
    public required string Text { get; init; }

    /// <summary>元素的时间区间；<see langword="null"/> 表示时间缺失。</summary>
    public TimeRange? Time { get; init; }

    /// <summary>时间来源状态。</summary>
    public TimingStatus TimingStatus { get; init; }

    /// <summary>
    /// 本元素的样式覆盖；<see langword="null"/> 表示完全继承行与基线样式。
    /// </summary>
    /// <remarks>
    /// 元素级目前只有<b>颜色</b>会真正参与绘制：整行只整形一次是"高亮不跳动"的前提，
    /// 逐元素改字号/字体/纵向位置会改变整行度量（docs/development.md §9.4）。
    /// 设置了这些属性不会被静默忽略，而是由校验器报出来。
    /// </remarks>
    public LyricStyleOverride? StyleOverride { get; init; }

    /// <summary>元素的时长（微秒）。</summary>
    /// <exception cref="InvalidOperationException">元素没有时间，或时间区间非法时抛出。</exception>
    public long DurationUs
    {
        get
        {
            if (Time is not { } range || !range.TryGetDurationUs(out long durationUs))
            {
                throw new InvalidOperationException($"元素 '{Id}' 没有可用的时间区间。");
            }

            return durationUs;
        }
    }

    /// <summary>
    /// 本元素记录的状态是否与其时间数据相符。
    /// </summary>
    /// <remarks>
    /// 语义容易误读，因此刻意使用长名字：当状态为 <see cref="TimingStatus.Invalid"/> 且区间确实非法时，
    /// 二者<b>相符</b>，本属性返回 <see langword="true"/>。
    /// 本属性回答的是“状态有没有如实反映数据”，而不是“数据是否合法”；
    /// 数据是否合法请用 <see cref="TimeRange.IsValid"/>，逐项诊断由校验器负责。
    /// </remarks>
    public bool IsTimeStatusConsistent => IsTimeStatusPairConsistent(Time, TimingStatus);

    /// <summary>判断一个“时间 + 状态”组合是否相符。</summary>
    /// <param name="time">时间区间。</param>
    /// <param name="status">时间来源状态。</param>
    /// <returns>
    /// 满足以下规则时为 <see langword="true"/>：
    /// <list type="bullet">
    ///   <item><see cref="TimingStatus.Missing"/> 必须没有时间；</item>
    ///   <item><see cref="TimingStatus.Invalid"/> 必须有时间且区间非法；</item>
    ///   <item>其余状态必须有合法区间。</item>
    /// </list>
    /// </returns>
    public static bool IsTimeStatusPairConsistent(TimeRange? time, TimingStatus status) => status switch
    {
        TimingStatus.Missing => time is null,
        TimingStatus.Invalid => time.HasValue && !time.Value.IsValid,
        _ => time.HasValue && time.Value.IsValid,
    };

    /// <summary>创建一个已校验的元素。</summary>
    /// <param name="id">元素标识，不可为空白。</param>
    /// <param name="text">元素文本，不可为 <see langword="null"/>（允许为空字符串）。</param>
    /// <param name="time">时间区间，默认没有时间。</param>
    /// <param name="timingStatus">时间来源状态，默认为 <see cref="TimingStatus.Missing"/>。</param>
    /// <param name="styleOverride">元素级样式覆盖，默认不覆盖。</param>
    /// <returns>新的元素实例。</returns>
    /// <exception cref="ArgumentException">标识为空白，或"时间 + 状态"组合不自洽时抛出。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> 为 <see langword="null"/> 时抛出。</exception>
    public static LyricToken Create(
        string id,
        string text,
        TimeRange? time = null,
        TimingStatus timingStatus = TimingStatus.Missing,
        LyricStyleOverride? styleOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(text);

        if (!IsTimeStatusPairConsistent(time, timingStatus))
        {
            throw new ArgumentException(
                $"时间与状态不自洽：状态 {timingStatus} 与时间 {time?.ToString() ?? "<无>"} 不匹配。",
                nameof(timingStatus));
        }

        return new LyricToken
        {
            Id = id,
            Text = text,
            Time = time,
            TimingStatus = timingStatus,
            StyleOverride = styleOverride,
        };
    }
}
