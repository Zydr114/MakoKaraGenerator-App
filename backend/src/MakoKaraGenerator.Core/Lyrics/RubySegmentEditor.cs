using System.Collections.Immutable;

namespace MakoKaraGenerator.Core.Lyrics;

/// <summary>注音片段的纯替换规则；时间独立于基础元素和行，不要求包含、相邻或无重叠。</summary>
public static class RubySegmentEditor
{
    /// <summary>验证完整非空片段序列并同步兼容正文；拒绝不改变原对象，同值返回原引用。</summary>
    public static bool TryReplaceSegments(RubyAnnotation source, ImmutableArray<RubyTextSegment> segments,
        out RubyAnnotation result)
    {
        ArgumentNullException.ThrowIfNull(source);
        result = source;
        if (segments.IsDefaultOrEmpty || segments.Any(segment => segment is null || !segment.IsValid)
            || segments.Select(segment => segment.Id).Distinct(StringComparer.Ordinal).Count() != segments.Length)
            return false;
        // Text's compatibility setter resets segments; the authoritative sequence must be assigned last.
        RubyAnnotation candidate = source with { Text = string.Concat(segments.Select(segment => segment.Text)), Segments = segments };
        if (!candidate.IsValid) return false;
        if (candidate != source) result = candidate;
        return true;
    }
    /// <summary>基础元素删除后同步移除其注音；仍附着的完整组和片段保持原引用。</summary>
    public static LyricLine RemoveDetachedGroups(LyricLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var bases = line.Tokens.Select(token => token.Id).ToHashSet(StringComparer.Ordinal);
        return line.RubyAnnotations.All(ruby => bases.Contains(ruby.BaseTokenId)) ? line
            : line with { RubyAnnotations = [.. line.RubyAnnotations.Where(ruby => bases.Contains(ruby.BaseTokenId))] };
    }

}
