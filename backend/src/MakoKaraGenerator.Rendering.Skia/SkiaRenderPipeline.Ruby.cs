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
        if (familyStyle is not null && HasGlow(familyStyle) || familyStyle is null && (HasGlow(lineStyle)
            || ruby.Any(entry => line.Tokens[entry.TokenIndex].StyleOverride is { Glow.IsSet: true } or { Sung.Glow.IsSet: true })))
        foreach (RubyLayout entry in ruby)
        {
            LyricStyle style = Style(entry);
            DrawLineGlow(canvas, entry.Shaped, entry.Options, origin + entry.X, baseline + entry.Baseline,
                style, antialias, drawUnitIndices, capture, ruby: true);
            // 两态辉光不同时各段在本态区域各画一次，仍先于全部注音填充，后画注音的光晕不会盖住前一注音（P3 设计 §4）。
            if (style.GlowDiffersByState) DrawSegments(entry, style, glow: true);
        }
        foreach (RubyLayout entry in ruby) DrawSegments(entry, Style(entry), glow: false);

        LyricStyle Style(RubyLayout entry) => familyStyle ?? (line.Tokens[entry.TokenIndex].StyleOverride is { } overlay
            ? ResolveElementStyle(lineStyle, overlay) : lineStyle);

        void DrawSegments(RubyLayout entry, LyricStyle style, bool glow)
        {
            float x = origin + entry.X, y = baseline + entry.Baseline;
            bool edges = style.DecorationsDifferByState;

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
                // 两态装饰不同时，末段唱完后已唱一侧延到右边界，与正文行尾一致（行首本来就延到左边界）。
                if (edges && progress >= 1d && segment.Right == entry.Shaped.Width) split = right;
                DrawPart(false);
                if (progress > 0d) DrawPart(true);
                void DrawPart(bool sung)
                {
                    canvas.Save();
                    canvas.ClipRect(sung ? new(left, clip.Top, Math.Min(right, split), clip.Bottom)
                        : new(Math.Max(left, progress > 0d ? split : left), clip.Top, right, clip.Bottom), antialias: false);
                    if (glow)
                        DrawLineGlow(canvas, entry.Shaped, entry.Options, x, y, style, antialias, drawUnitIndices, capture, ruby: true, state: sung);
                    else
                        DrawStyledLine(canvas, entry.Shaped, entry.Options, x, y,
                            sung ? style.SungFill.Color : style.UnsungFill.Color,
                            sung ? style.SungFill.Gradient : style.UnsungFill.Gradient,
                            style, composition, antialias, drawUnitIndices, capture, sungPass: sung, rubyGlyph: true,
                            lineSpan: new LineSpan(origin, entry.BaseWidth));
                    canvas.Restore();
                }
            }
        }
    }
}
