using System.Collections.Immutable;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>歌词文字的排版参数。</summary>
public sealed record TypographySpec
{
    /// <summary>渲染后端使用的默认字体族。</summary>
    public string? FontFamilyName { get; init; }

    /// <summary>按用户顺序尝试的字体回退族名；空序列表示使用后端系统回退。</summary>
    public ImmutableArray<string> FallbackFamilyNames { get; init; } = [];

    /// <summary>字号，单位为画布像素。</summary>
    public float FontSizePx { get; init; } = 72f;

    /// <summary>请求的字重，范围为 100 到 900。</summary>
    public int FontWeight { get; init; } = 400;

    /// <summary>是否请求粗体字形。</summary>
    public bool IsBold { get; init; }

    /// <summary>是否请求斜体字形。</summary>
    public bool IsItalic { get; init; }

    /// <summary>相邻文本元素之间的字距，单位为画布像素。</summary>
    public float LetterSpacingPx { get; init; }

    /// <summary>默认排版参数。</summary>
    public static TypographySpec Default { get; } = new() { FallbackFamilyNames = [] };

    /// <summary>排版参数是否有效。</summary>
    public bool IsValid => FontSizePx > 0f && float.IsFinite(FontSizePx)
        && FontWeight is >= 100 and <= 900
        && float.IsFinite(LetterSpacingPx)
        && !FallbackFamilyNames.IsDefault
        && FallbackFamilyNames.All(name => !string.IsNullOrWhiteSpace(name))
        && FallbackFamilyNames.Distinct(StringComparer.Ordinal).Count() == FallbackFamilyNames.Length;

    /// <summary>按回退族名内容比较字体规范，而不是比较不可变数组的内部引用。</summary>
    public bool Equals(TypographySpec? other) => other is not null
        && string.Equals(FontFamilyName, other.FontFamilyName, StringComparison.Ordinal)
        && FontSizePx.Equals(other.FontSizePx)
        && FontWeight == other.FontWeight
        && IsBold == other.IsBold
        && IsItalic == other.IsItalic
        && LetterSpacingPx.Equals(other.LetterSpacingPx)
        && FallbackFamilyNames.SequenceEqual(other.FallbackFamilyNames);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(FontFamilyName, StringComparer.Ordinal);
        hash.Add(FontSizePx);
        hash.Add(FontWeight);
        hash.Add(IsBold);
        hash.Add(IsItalic);
        hash.Add(LetterSpacingPx);
        foreach (string name in FallbackFamilyNames)
            hash.Add(name, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

/// <summary>字体排版参数的部分覆盖。</summary>
public sealed record TypographySpecOverride
{
    /// <summary>字体族；未设置表示继承，设置为空表示使用后端默认字体。</summary>
    public OptionalValue<string?> FontFamilyName { get; init; }

    /// <summary>字体回退族名覆盖；空数组表示清空当前回退链。</summary>
    public ImmutableArray<string>? FallbackFamilyNames { get; init; }

    /// <summary>字号覆盖。</summary>
    public float? FontSizePx { get; init; }

    /// <summary>字重覆盖。</summary>
    public int? FontWeight { get; init; }

    /// <summary>粗体请求覆盖。</summary>
    public bool? IsBold { get; init; }

    /// <summary>斜体请求覆盖。</summary>
    public bool? IsItalic { get; init; }

    /// <summary>字距覆盖。</summary>
    public float? LetterSpacingPx { get; init; }

    /// <summary>是否没有任何排版覆盖。</summary>
    public bool IsEmpty => !FontFamilyName.IsSet
        && FallbackFamilyNames is null
        && FontSizePx is null
        && FontWeight is null
        && IsBold is null
        && IsItalic is null
        && LetterSpacingPx is null;
}
