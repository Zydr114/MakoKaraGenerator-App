using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Text;
using SkiaSharp;
using static MakoKaraGenerator.Rendering.Skia.SkiaSceneGeometry;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private void DrawRuby(SKCanvas canvas, IReadOnlyList<RubyLayout> ruby, LyricFrameSample sample,
        LyricLine line, LyricStyle lineStyle, Composition composition, float origin, float baseline, bool antialias, LyricStyle? familyStyle = null,
        CapturePaint? capture = null, ImmutableArray<int> drawUnitIndices = default)
    {
        if (familyStyle?.Glow is { IsEnabled: true } || familyStyle is null && (lineStyle.Glow is { IsEnabled: true }
            || ruby.Any(entry => line.Tokens[entry.TokenIndex].StyleOverride is { Glow.IsSet: true })))
        foreach (RubyLayout entry in ruby)
        {
            LyricStyle style = familyStyle ?? (line.Tokens[entry.TokenIndex].StyleOverride is { } overlay
                ? ResolveElementStyle(lineStyle, overlay) : lineStyle);
            DrawLineGlow(canvas, entry.Shaped, entry.Options, origin + entry.X, baseline + entry.Baseline,
                style, antialias, drawUnitIndices, capture, ruby: true);
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
                        style, composition, antialias, drawUnitIndices, capture, sungPass: sung, rubyGlyph: true);
                    canvas.Restore();
                }
            }
        }
    }
}
