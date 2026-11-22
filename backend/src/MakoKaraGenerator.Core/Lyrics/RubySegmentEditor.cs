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

    /// <summary>
    /// 按新提交的注音分段正文列表非破坏性更新基础词元的注音：
    /// <ul>
    ///   <li>空列表表示删除该词元的注音；</li>
    ///   <li>按段序号继承已有 <see cref="RubyTextSegment.Id"/>、<see cref="RubyTextSegment.Time"/> 与 <see cref="RubyTextSegment.TimingStatus"/>；</li>
    ///   <li>超出旧段数的新段分配新标识且初始无时间。</li>
    /// </ul>
    /// </summary>
    public static bool TryBuildNonDestructiveSegments(
        RubyAnnotation? existing,
        string baseTokenId,
        ImmutableArray<string> segmentTexts,
        Func<string> nextRubyId,
        Func<string> nextSegmentId,
        out RubyAnnotation? result,
        out int changedCount)
    {
        ArgumentNullException.ThrowIfNull(baseTokenId);
        ArgumentNullException.ThrowIfNull(nextRubyId);
        ArgumentNullException.ThrowIfNull(nextSegmentId);
        result = existing;
        changedCount = 0;

        if (segmentTexts.IsDefault || segmentTexts.IsEmpty)
        {
            if (existing is null) return true;
            result = null;
            changedCount = existing.Segments.Length;
            return true;
        }

        foreach (string text in segmentTexts)
        {
            if (string.IsNullOrEmpty(text))
                return false;
        }

        if (existing is null)
        {
            var created = ImmutableArray.CreateBuilder<RubyTextSegment>(segmentTexts.Length);
            foreach (string text in segmentTexts)
                created.Add(RubyTextSegment.Create(nextSegmentId(), text));

            string rubyId = nextRubyId();
            RubyAnnotation createdRuby = RubyAnnotation.Create(rubyId, baseTokenId, string.Concat(segmentTexts)) with
            {
                Segments = created.ToImmutable(),
            };
            if (!createdRuby.IsValid) return false;
            result = createdRuby;
            changedCount = segmentTexts.Length;
            return true;
        }

        var updated = ImmutableArray.CreateBuilder<RubyTextSegment>(segmentTexts.Length);
        int changes = 0;
        for (int i = 0; i < segmentTexts.Length; i++)
        {
            if (i < existing.Segments.Length)
            {
                RubyTextSegment old = existing.Segments[i];
                if (string.Equals(old.Text, segmentTexts[i], StringComparison.Ordinal))
                {
                    updated.Add(old);
                }
                else
                {
                    updated.Add(old with { Text = segmentTexts[i] });
                    changes++;
                }
            }
            else
            {
                updated.Add(RubyTextSegment.Create(nextSegmentId(), segmentTexts[i]));
                changes++;
            }
        }
        if (existing.Segments.Length > segmentTexts.Length)
            changes += existing.Segments.Length - segmentTexts.Length;

        if (!TryReplaceSegments(existing, updated.ToImmutable(), out RubyAnnotation nextRuby))
            return false;
        result = nextRuby;
        changedCount = nextRuby == existing ? 0 : Math.Max(1, changes);
        return true;
    }

}
