using System.Collections.Immutable;

namespace MakoKaraGenerator.Text;

/// <summary>
/// 一次文字布局的输入选项。
/// </summary>
/// <remarks>
/// <para>
/// 布局结果的正确性完全由本选项 + 文本 + 后端字体环境决定，
/// 因此它同时也是布局缓存的键（docs/development.md §10.4）。
/// </para>
/// <para>
/// 本类型是不可变记录：后续加入字重、字距、斜体等样式属性时，
/// 只需新增 <c>init</c> 属性，既有调用方不受影响。
/// </para>
/// </remarks>
public sealed record TextLayoutOptions
{
    /// <summary>
    /// 字体族名；<see langword="null"/> 或空白表示使用后端默认字体。
    /// </summary>
    /// <remarks>
    /// 缺失字形时后端会做字体回退，因此实际用于绘制某元素的字体可能不是本值；
    /// 每个元素实际使用的字体见 <see cref="ShapedTextElement.FamilyName" />。
    /// </remarks>
    public string? FamilyName { get; init; }

    /// <summary>按顺序尝试的字体回退族名。</summary>
    public ImmutableArray<string> FallbackFamilyNames { get; init; }

    /// <summary>字号（像素）。必须为正。</summary>
    public float FontSizePx { get; init; } = 72f;

    /// <summary>请求的字重，100 到 900。</summary>
    public int FontWeight { get; init; } = 400;

    /// <summary>请求粗体时，有效字重至少为 700。</summary>
    public bool IsBold { get; init; }

    /// <summary>是否请求斜体字形。</summary>
    public bool IsItalic { get; init; }

    /// <summary>相邻文本元素之间的画布像素字距。</summary>
    public float LetterSpacingPx { get; init; }

    /// <summary>默认选项。</summary>
    public static TextLayoutOptions Default { get; } = new();

    /// <inheritdoc/>
    public bool Equals(TextLayoutOptions? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;
        return StringComparer.Ordinal.Equals(FamilyName, other.FamilyName)
            && EqualityComparer<float>.Default.Equals(FontSizePx, other.FontSizePx)
            && FontWeight == other.FontWeight
            && IsBold == other.IsBold
            && IsItalic == other.IsItalic
            && EqualityComparer<float>.Default.Equals(LetterSpacingPx, other.LetterSpacingPx)
            && FallbackSequence(FallbackFamilyNames).SequenceEqual(FallbackSequence(other.FallbackFamilyNames), StringComparer.Ordinal);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(FamilyName, StringComparer.Ordinal);
        hash.Add(FontSizePx);
        hash.Add(FontWeight);
        hash.Add(IsBold);
        hash.Add(IsItalic);
        hash.Add(LetterSpacingPx);
        foreach (string family in FallbackSequence(FallbackFamilyNames))
        {
            hash.Add(family, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }

    private static ImmutableArray<string> FallbackSequence(ImmutableArray<string> values)
        => values.IsDefault ? [] : values;
}

/// <summary>
/// 文字布局引擎：把一行文本整形并切分成带位置的文本元素。
/// </summary>
/// <remarks>
/// <para>
/// 对应 docs/development.md §9 的建议：整形、字形边界与实际绘制被包在这个接口后面，
/// 上层（渲染管线、导出、界面诊断）因此不直接依赖某个具体文字库。
/// </para>
/// <para>
/// 实现应把字体对象缓存在实例内并可复用：布局每帧都会发生，
/// 每次重新打开字体文件会显著拖慢预览（docs/development.md §10.5）。
/// 因此实现是 <see cref="IDisposable" />，且<b>不保证</b>可跨线程共享——
/// 预览与导出各自持有实例更安全（native 字体对象不跨线程共享，见 ADR-0007 的教训）。
/// </para>
/// </remarks>
public interface ITextLayoutEngine : IDisposable
{
    /// <summary>
    /// 解析一个字体族请求实际会命中的字体族名。
    /// </summary>
    /// <param name="requestedFamilyName">请求的族名；<see langword="null"/> 或空白表示默认字体。</param>
    /// <returns>实际命中的族名（请求不存在时是回退结果）。</returns>
    /// <remarks>用于界面诊断"请求的字体到底有没有装上"，而不是绘制路径。</remarks>
    string ResolveFamilyName(string? requestedFamilyName);

    /// <summary>整形一行文本。</summary>
    /// <param name="text">待整形的文本。</param>
    /// <param name="options">布局选项。</param>
    /// <returns>文本元素的位置与宽度。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> 或 <paramref name="options"/> 为 <see langword="null"/> 时抛出。</exception>
    /// <exception cref="ObjectDisposedException">已释放时抛出。</exception>
    ShapedLine Shape(string text, TextLayoutOptions options);
}
