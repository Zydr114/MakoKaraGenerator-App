using System.Collections.Immutable;

namespace MakoKaraGenerator.Text;

/// <summary>An annotation's original aggregate ink, anchored to the token's natural, unexpanded range.</summary>
public readonly record struct RubySpacingItem(int TokenIndex, TextInkBounds Ink);

/// <summary>
/// Layout outcomes. Unsupported inputs retain their original coordinates. A shared-cluster conflict
/// preserves relative coordinates inside that rigid group, and may coexist with adjustments between groups.
/// </summary>
[Flags]
public enum RubySpacingStatus
{
    /// <summary>No coordinate changes were needed.</summary>
    Unchanged = 0,
    /// <summary>One or more complete groups were translated.</summary>
    Adjusted = 1,
    /// <summary>A conflict within a rigid shaping group could not be resolved.</summary>
    SharedClusterConflict = 2,
    /// <summary>The source mapping or visual order does not support safe spacing.</summary>
    UnsupportedMapping = 4,
    /// <summary>Source token ranges or annotation associations were invalid.</summary>
    InvalidInput = 8,
    /// <summary>Input or calculated coordinates exceeded the finite layout range.</summary>
    UnsupportedCoordinates = 16,
}

/// <summary>Fixed glyph layout and horizontal offsets in original annotation order.</summary>
/// <param name="Basis">Complete glyph groups translated without stretching or changing natural advances.</param>
/// <param name="RubyOffsets">Translations to add to original natural-token ruby anchors; never re-anchor to an expanded token endpoint.</param>
/// <param name="Status">Adjustments and any retained unsupported group conflicts.</param>
public sealed record RubySpacingResult(ShapedLine Basis, ImmutableArray<float> RubyOffsets, RubySpacingStatus Status);

/// <summary>Resolves aggregate ruby collisions by translating complete token/cluster groups.</summary>
/// <remarks>
/// Uses a finite deterministic scan: pair constraints cost O(r²), suffix translations O(g*r),
/// for r annotations and g rigid groups. This is not a performance acceptance or an optimized bidi solver.
/// </remarks>
public static class RubySpacingSolver
{
    /// <summary>Required one-pixel horizontal ink gap before whole-line scaling; never stretches glyphs or ruby.</summary>
    public const float MinimumGap = 1f;

    /// <summary>
    /// Translate safe groups without reshaping, stretching, changing natural glyph advances, or inspecting timing.
    /// Ruby positions retain their original natural-token anchors plus the rigid group translation.
    /// </summary>
    public static RubySpacingResult Solve(ShapedLine basis, IReadOnlyList<TextSpan> tokens,
        IReadOnlyList<RubySpacingItem> rubies)
    {
        ArgumentNullException.ThrowIfNull(basis);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(rubies);
        RubySpacingResult Original(RubySpacingStatus status) => new(basis,
            ImmutableArray.CreateRange(Enumerable.Repeat(0f, rubies.Count)), status);
        if (rubies.Count < 2) return Original(RubySpacingStatus.Unchanged);
        int end = 0;
        foreach (var token in tokens)
        {
            if (token.Start != end || token.Length <= 0 || token.Start > int.MaxValue - token.Length)
                return Original(RubySpacingStatus.InvalidInput);
            end = token.End;
        }
        if (end != basis.Text.Length || rubies.Any(ruby => ruby.TokenIndex < 0 || ruby.TokenIndex >= tokens.Count))
            return Original(RubySpacingStatus.InvalidInput);
        if (!basis.MappingIsExact || basis.Elements.Count == 0 || basis.DrawUnits.IsEmpty)
            return Original(RubySpacingStatus.UnsupportedMapping);
        if (basis.Elements.Any(element => element.Utf16Start < 0 || element.Utf16Length <= 0
                || element.Utf16Start > basis.Text.Length - element.Utf16Length)
            || basis.DrawUnits.Any(unit => unit.Utf16Start < 0 || unit.Utf16Length <= 0
                || unit.Utf16Start > basis.Text.Length - unit.Utf16Length))
            return Original(RubySpacingStatus.UnsupportedMapping);
        if (!float.IsFinite(basis.Width) || basis.Elements.Any(element => !float.IsFinite(element.X) || !float.IsFinite(element.Advance))
            || basis.DrawUnits.Any(unit => !float.IsFinite(unit.X) || !float.IsFinite(unit.Advance)
                || unit.Glyphs.Any(glyph => !float.IsFinite(glyph.X) || !float.IsFinite(glyph.Y)
                    || glyph.InkBounds is { } ink && !Finite(ink)))
            || rubies.Any(ruby => !Finite(ruby.Ink))) return Original(RubySpacingStatus.UnsupportedCoordinates);
        // Existing shaping is not a complete bidi layout. Never infer logical order from RTL visual coordinates.
        if (!Ordered(basis.Elements.Select(element => element.X)) || !Ordered(basis.DrawUnits.Select(unit => unit.X)))
            return Original(RubySpacingStatus.UnsupportedMapping);

        int[] groups = new int[tokens.Count];
        int[] unsafeBoundaries = new int[tokens.Count + 1];
        int LowerBound(int position)
        {
            int lo = 1, hi = tokens.Count;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (tokens[mid].Start < position) lo = mid + 1; else hi = mid;
            }
            return lo;
        }
        foreach (var range in basis.Elements.Select(element => (Start: element.Utf16Start, End: element.Utf16End))
            .Concat(basis.DrawUnits.Select(unit => (Start: unit.Utf16Start, End: unit.Utf16End))))
        {
            int first = LowerBound(range.Start + 1), last = LowerBound(range.End);
            if (first < last) { unsafeBoundaries[first]++; unsafeBoundaries[last]--; }
        }
        int groupCount = 1;
        int crossing = 0;
        for (int i = 1; i < tokens.Count; i++)
        {
            crossing += unsafeBoundaries[i];
            if (crossing == 0) groupCount++;
            groups[i] = groupCount - 1;
        }
        float[] shifts = new float[groupCount];
        var ordered = rubies.OrderBy(ruby => ruby.TokenIndex).ToArray();
        RubySpacingStatus status = RubySpacingStatus.Unchanged;
        for (int i = 0; i < ordered.Length; i++)
        {
            var current = ordered[i];
            int group = groups[current.TokenIndex];
            float extra = 0;
            for (int j = 0; j < i; j++)
            {
                var previous = ordered[j];
                if (Math.Min(previous.Ink.Bottom, current.Ink.Bottom) <= Math.Max(previous.Ink.Top, current.Ink.Top)) continue;
                int previousGroup = groups[previous.TokenIndex];
                float deficit = previous.Ink.Right + shifts[previousGroup] + MinimumGap - current.Ink.Left - shifts[group];
                if (deficit <= 0) continue;
                if (previousGroup == group) status |= RubySpacingStatus.SharedClusterConflict;
                else extra = Math.Max(extra, deficit);
            }
            if (extra <= 0) continue;
            if (!float.IsFinite(extra)) return Original(RubySpacingStatus.UnsupportedCoordinates);
            for (int g = group; g < shifts.Length; g++)
            {
                shifts[g] += extra;
                if (!float.IsFinite(shifts[g])) return Original(RubySpacingStatus.UnsupportedCoordinates);
            }
            status |= RubySpacingStatus.Adjusted;
        }
        if ((status & RubySpacingStatus.Adjusted) == 0) return Original(status);
        float Shift(int start)
        {
            int lo = 0, hi = tokens.Count - 1;
            while (lo < hi)
            {
                int mid = lo + (hi - lo + 1) / 2;
                if (tokens[mid].Start <= start) lo = mid; else hi = mid - 1;
            }
            return shifts[groups[lo]];
        }
        // Inserted whitespace is not a glyph's natural advance. XAt(end) may sweep this gap;
        // ruby anchors instead keep the original token range plus its rigid group translation.
        var elements = basis.Elements.Select(element => element with { X = element.X + Shift(element.Utf16Start) }).ToArray();
        var units = basis.DrawUnits.Select(unit =>
        {
            float shift = Shift(unit.Utf16Start);
            return unit with { X = unit.X + shift, Glyphs = [.. unit.Glyphs.Select(glyph => glyph with
                { X = glyph.X + shift, InkBounds = glyph.InkBounds is { } ink ? Translate(ink, shift) : null })] };
        }).ToImmutableArray();
        TextInkBounds? bounds = null;
        foreach (var glyph in units.SelectMany(unit => unit.Glyphs))
            if (glyph.InkBounds is { } ink) bounds = bounds is { } prior
                ? new(Math.Min(prior.Left, ink.Left), Math.Min(prior.Top, ink.Top), Math.Max(prior.Right, ink.Right), Math.Max(prior.Bottom, ink.Bottom)) : ink;
        float width = basis.Width + shifts[^1];
        if (!float.IsFinite(width) || elements.Any(element => !float.IsFinite(element.X))
            || units.Any(unit => !float.IsFinite(unit.X) || unit.Glyphs.Any(glyph => !float.IsFinite(glyph.X)
                || glyph.InkBounds is { } ink && !Finite(ink)))) return Original(RubySpacingStatus.UnsupportedCoordinates);
        var result = new ShapedLine(basis.Text, elements, width, basis.MappingIsExact, basis.MissingGlyphCount, bounds, units);
        return new(result, [.. rubies.Select(ruby => shifts[groups[ruby.TokenIndex]])], status);
    }

    private static TextInkBounds Translate(TextInkBounds ink, float x) => ink with { Left = ink.Left + x, Right = ink.Right + x };
    private static bool Finite(TextInkBounds ink) => float.IsFinite(ink.Left) && float.IsFinite(ink.Top)
        && float.IsFinite(ink.Right) && float.IsFinite(ink.Bottom);
    private static bool Ordered(IEnumerable<float> positions)
    {
        float previous = float.NegativeInfinity;
        foreach (float position in positions) { if (position < previous) return false; previous = position; }
        return true;
    }
}
