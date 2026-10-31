using System.Collections.ObjectModel;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>解析后的样式及每个属性的来源。</summary>
public sealed record LyricStyleResolution
{
    private readonly ReadOnlyDictionary<LyricStyleProperty, LyricStyleSource> _sources;

    /// <summary>最终使用的样式。</summary>
    public LyricStyle Style { get; }

    /// <summary>创建解析结果。</summary>
    /// <param name="style">最终样式。</param>
    /// <param name="sources">属性名到来源的映射。</param>
    public LyricStyleResolution(
        LyricStyle style,
        IReadOnlyDictionary<LyricStyleProperty, LyricStyleSource> sources)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(sources);
        Style = style;
        _sources = new ReadOnlyDictionary<LyricStyleProperty, LyricStyleSource>(
            new Dictionary<LyricStyleProperty, LyricStyleSource>(sources));
    }

    /// <summary>取得指定属性的来源。</summary>
    /// <param name="property">样式属性。</param>
    /// <returns>该属性的来源。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="property"/> 不是已定义的样式属性时抛出。</exception>
    /// <remarks>
    /// 刻意不提供"未知属性返回默认层"的字符串重载：界面里一个拼写错误会安静地显示错误来源，
    /// 而这类错误在编译期就能被枚举挡住（本方法对越界的枚举值也是抛而不是猜）。
    /// </remarks>
    public LyricStyleSource SourceOf(LyricStyleProperty property) =>
        _sources.TryGetValue(property, out LyricStyleSource source)
            ? source
            : throw new ArgumentOutOfRangeException(
                nameof(property),
                property,
                "不是已定义的样式属性。");

    /// <summary>只读的来源映射，供诊断面板使用。</summary>
    public IReadOnlyDictionary<LyricStyleProperty, LyricStyleSource> Sources => _sources;
}
