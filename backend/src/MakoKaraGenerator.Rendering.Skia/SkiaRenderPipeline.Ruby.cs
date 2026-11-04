using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Text;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private sealed record RubyLayout(string Id, int TokenIndex, ShapedLine Shaped, TextLayoutOptions Options,
        float X, float Baseline, TextInkBounds Ink, ImmutableArray<RubyLayoutSegment> Segments);

    private sealed record RubyLayoutSegment(string Id, float Left, float Right);

    private sealed record LineLayout(ShapedLine Shaped, List<RubyLayout> Ruby, RubySpacingStatus SpacingStatus);

    private LineLayout GetLineLayout(LyricLine line, LyricStyle style)
    {
        if (line.RubyAnnotations.IsEmpty) return BuildLineLayout(line, style);
        lock (_rubyLayoutGate)
        {
            var key = (line, style);
            if (_rubyLayouts.TryGetValue(key, out var cached)) return cached;
            var layout = BuildLineLayout(line, style);
            if (_rubyLayouts.Count >= 256) _rubyLayouts.Clear();
            _rubyLayouts.Add(key, layout);
            return layout;
        }
    }

    private LineLayout BuildLineLayout(LyricLine line, LyricStyle style)
    {
        ShapedLine shaped = GetLayout(line.Text, LayoutOptionsFor(style));
        List<RubyLayout> ruby = MeasureRuby(line, style, shaped);
        if (ruby.Count < 2) return new(shaped, ruby, RubySpacingStatus.Unchanged);
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
        return new(solved.Basis, ruby, solved.Status);
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

    private static TextInkBounds? GroupInk(TextInkBounds? baseInk, IReadOnlyList<RubyLayout> ruby)
    {
        TextInkBounds? result = baseInk;
        foreach (RubyLayout entry in ruby)
            result = result is not { } ink ? entry.Ink : new TextInkBounds(
                Math.Min(ink.Left, entry.Ink.Left), Math.Min(ink.Top, entry.Ink.Top),
                Math.Max(ink.Right, entry.Ink.Right), Math.Max(ink.Bottom, entry.Ink.Bottom));
        return result;
    }

    private void DrawRuby(SKCanvas canvas, IReadOnlyList<RubyLayout> ruby, LyricFrameSample sample,
        LyricLine line, LyricStyle lineStyle, Composition composition, float origin, float baseline, bool antialias, LyricStyle? familyStyle = null, CapturePaint? capture = null)
    {
        if (familyStyle?.Glow is { IsEnabled: true } || familyStyle is null && (lineStyle.Glow is { IsEnabled: true }
            || ruby.Any(entry => line.Tokens[entry.TokenIndex].StyleOverride is { Glow.IsSet: true })))
        foreach (RubyLayout entry in ruby)
        {
            LyricStyle style = familyStyle ?? (line.Tokens[entry.TokenIndex].StyleOverride is { } overlay
                ? ResolveElementStyle(lineStyle, overlay) : lineStyle);
            DrawLineGlow(canvas, entry.Shaped, entry.Options, origin + entry.X, baseline + entry.Baseline,
                style, antialias, capture: capture, ruby: true);
        }
        foreach (RubyLayout entry in ruby)
        {
            LyricStyle style = familyStyle ?? (line.Tokens[entry.TokenIndex].StyleOverride is { } overlay
                ? ResolveElementStyle(lineStyle, overlay) : lineStyle);
            float x = origin + entry.X, y = baseline + entry.Baseline;

            // Aggregate shaping is shared by all segments; only sung/unsung clips vary.
            for (int i = 0; i < entry.Segments.Length; i++)
            {
                RubyLayoutSegment segment = entry.Segments[i];
                if (segment.Right <= segment.Left) continue;
                RubyTextSegmentSample state = sample.RubySegments.FirstOrDefault(value =>
                    value.RubyId == entry.Id && value.SegmentId == segment.Id);
                double progress = state.Source == ProgressSource.Unavailable ? 0d : state.Progress;
                float start = x + segment.Left;
                float end = x + segment.Right;
                float split = entry.Segments.Length == 1 ? x + entry.Shaped.Width * (float)progress
                    : start + (end - start) * (float)progress;
                SKRect clip = canvas.LocalClipBounds;
                float left = segment.Left == 0 ? clip.Left : start;
                float right = segment.Right == entry.Shaped.Width ? clip.Right : end;
                DrawPart(false);
                if (progress > 0d) DrawPart(true);
                void DrawPart(bool sung)
                {
                    canvas.Save();
                    canvas.ClipRect(sung ? new(left, clip.Top, Math.Min(right, split), clip.Bottom)
                        : new(Math.Max(left, progress > 0d ? split : left), clip.Top, right, clip.Bottom), antialias: false);
                    DrawStyledLine(canvas, entry.Shaped, entry.Options, x, y,
                        sung ? style.SungFill.Color : style.UnsungFill.Color,
                        sung ? style.SungFill.Gradient : style.UnsungFill.Gradient,
                        style, composition, antialias, capture: capture, sungPass: sung, rubyGlyph: true);
                    canvas.Restore();
                }
            }
        }
    }
}
