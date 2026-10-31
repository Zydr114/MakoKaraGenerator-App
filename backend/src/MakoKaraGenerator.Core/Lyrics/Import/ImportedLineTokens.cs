using System.Collections.Immutable;

namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>
/// 一行的元素与"去掉逐字标签之后的文本"。
/// </summary>
/// <param name="Tokens">元素序列；文本拼接必须等于 <paramref name="LineText" />。</param>
/// <param name="LineText">该行真正要显示的文本（已去掉逐字标签）。</param>
public sealed record ImportedLineTokens(ImmutableArray<LyricToken> Tokens, string LineText);
