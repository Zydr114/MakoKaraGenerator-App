using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Text;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaSceneGeometry
{
    /// <summary>一条注音的布局；<paramref name="BaseWidth"/> 是所属底字整行的宽度，整行渐变的横向参考取它（P3 设计 §7.2）。</summary>
    internal sealed record RubyLayout(string Id, int TokenIndex, ShapedLine Shaped, TextLayoutOptions Options,
        float X, float Baseline, TextInkBounds Ink, ImmutableArray<RubyLayoutSegment> Segments, float BaseWidth = 0f);

    internal sealed record RubyLayoutSegment(string Id, float Left, float Right);

    internal sealed record LineLayout(ShapedLine Shaped, ImmutableArray<RubyLayout> Ruby, RubySpacingStatus SpacingStatus);

    internal LineLayout GetLineLayout(LyricLine line, LyricStyle style)
    {
        if (line.RubyAnnotations.IsEmpty) return BuildLineLayout(line, style);
        return _layouts.GetRuby(line, style, _layoutEngine, () => BuildLineLayout(line, style));
    }

    private LineLayout BuildLineLayout(LyricLine line, LyricStyle style)
    {
        ShapedLine shaped = GetLayout(line.Text, LayoutOptionsFor(style));
        List<RubyLayout> ruby = MeasureRuby(line, style, shaped);
        if (ruby.Count < 2) return new(shaped, [.. ruby.Select(annotation => annotation with { BaseWidth = shaped.Width })], RubySpacingStatus.Unchanged);
        int offset = 0;
        TextSpan[] spans = [.. line.Tokens.Select(token =>
        {
            var span = new TextSpan(offset, token.Text.Length); offset = span.End; return span;
        })];
        var solved = RubySpacingSolver.Solve(shaped, spans,
            ruby.Select(annotation => new RubySpacingItem(annotation.TokenIndex, annotation.Ink)).ToArray());
        if (!ReferenceEquals(shaped, solved.Basis))
        {
            ruby = [.. ruby.Select((annotation, index) =>
            {
                float shift = solved.RubyOffsets[index];
                return annotation with { X = annotation.X + shift,
                    Ink = annotation.Ink with { Left = annotation.Ink.Left + shift, Right = annotation.Ink.Right + shift } };
            })];
        }
        return new(solved.Basis, [.. ruby.Select(annotation => annotation with { BaseWidth = solved.Basis.Width })], solved.Status);
    }

    private List<RubyLayout> MeasureRuby(LyricLine line, LyricStyle style, ShapedLine shaped)
    {
        List<RubyLayout> result = [];
        if (line.RubyAnnotations.IsEmpty) return result;
        if (string.Concat(line.Tokens.Select(entry => entry.Text)) != line.Text) return result;
        Dictionary<string, (int Index, int Offset)> tokens = new(StringComparer.Ordinal);
        (int Index, int Offset)? nullToken = null;
        int tokenOffset = 0;
        for (int i = 0; i < line.Tokens.Length; i++)
        {
            var token = line.Tokens[i];
            if (token.Id is null) nullToken ??= (i, tokenOffset);
            else tokens.TryAdd(token.Id, (i, tokenOffset));
            tokenOffset = checked(tokenOffset + token.Text.Length);
        }
        TextInkBounds baseInk = shaped.InkBounds ?? new(0, -style.FontSizePx * .85f, shaped.Width, style.FontSizePx * .15f);
        foreach (RubyAnnotation annotation in line.RubyAnnotations)
        {
            var found = annotation.BaseTokenId is null ? nullToken
                : tokens.TryGetValue(annotation.BaseTokenId, out var match) ? match : ((int Index, int Offset)?)null;
            if (!annotation.IsValid || found is null) continue;
            (int index, int offset) = found.Value;
            TextLayoutOptions options = LayoutOptionsFor(style with { FontSizePx = style.FontSizePx * (float)annotation.RelativeScale });
            ShapedLine ruby = GetLayout(annotation.Text, options);
            if (ruby.InkBounds is not { } ink) continue;
            float left = shaped.XAt(offset), right = shaped.XAt(offset + line.Tokens[index].Text.Length);
            float x = annotation.Alignment switch
            {
                RubyAlignment.Start => left - ink.Left,
                RubyAlignment.End => right - ink.Right,
                _ => (left + right - ink.Left - ink.Right) / 2,
            };
            float baseline = baseInk.Top - (float)annotation.GapPx - ink.Bottom;
            ImmutableArray<RubyLayoutSegment>.Builder segmentBuilder = ImmutableArray.CreateBuilder<RubyLayoutSegment>();
            // A segment boundary inside a grapheme or shaping cluster belongs to the
            // segment containing that cluster's first code unit. Never reshape a substring.
            List<(int Start, int End)> clusters = [];
            foreach (var range in ruby.Elements.Select(element => (Start: element.Utf16Start, End: element.Utf16End))
                .Concat(ruby.DrawUnits.Select(unit => (Start: unit.Utf16Start, End: unit.Utf16End))).OrderBy(range => range.Start))
            {
                if (clusters.Count > 0 && range.Start < clusters[^1].End)
                    clusters[^1] = (clusters[^1].Start, Math.Max(clusters[^1].End, range.End));
                else clusters.Add(range);
            }
            int segmentOffset = 0;
            int clusterIndex = 0;
            float segmentLeft = 0;
            foreach (RubyTextSegment segment in annotation.EffectiveSegments)
            {
                segmentOffset += segment.Text.Length;
                while (clusterIndex < clusters.Count && clusters[clusterIndex].End <= segmentOffset) clusterIndex++;
                int boundary = clusterIndex < clusters.Count && clusters[clusterIndex].Start < segmentOffset
                    ? clusters[clusterIndex].End : segmentOffset;
                float segmentRight = ruby.XAt(boundary);
                segmentBuilder.Add(new(segment.Id, segmentLeft, segmentRight));
                segmentLeft = segmentRight;
            }
            result.Add(new(annotation.Id, index, ruby, options, x, baseline,
                new(x + ink.Left, baseline + ink.Top, x + ink.Right, baseline + ink.Bottom), segmentBuilder.ToImmutable()));
        }
        return result;
    }

    internal static TextInkBounds? GroupInk(TextInkBounds? baseInk, IReadOnlyList<RubyLayout> ruby)
    {
        TextInkBounds? result = baseInk;
        foreach (RubyLayout entry in ruby)
            result = result is not { } ink ? entry.Ink : new TextInkBounds(
                Math.Min(ink.Left, entry.Ink.Left), Math.Min(ink.Top, entry.Ink.Top),
                Math.Max(ink.Right, entry.Ink.Right), Math.Max(ink.Bottom, entry.Ink.Bottom));
        return result;
    }
}
