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
        IReadOnlyList<HighlightPeak> previousHighlight = _rubyHighlight;
        try
        {
            foreach (RubyLayout entry in ruby)
            {
                // 注音按自己的分段进度叠高亮，峰宽按注音字号（P3 设计 §7.2、§10.3）。
                LyricStyle style = Style(entry);
                _rubyHighlight = BuildRubyHighlights(entry, sample, style.Wipe.Highlight);
                DrawSegments(entry, style, glow: false);
            }
        }
        finally
        {
            _rubyHighlight = previousHighlight;
        }

        // 注音继承所属元素的样式，装饰按注音比例缩放（P3 设计 §7.2）。
        LyricStyle Style(RubyLayout entry) => (familyStyle ?? (line.Tokens[entry.TokenIndex].StyleOverride is { } overlay
            ? ResolveElementStyle(lineStyle, overlay) : lineStyle)).ScaleDecorations(entry.Scale);

        void DrawSegments(RubyLayout entry, LyricStyle style, bool glow)
        {
            float x = origin + entry.X, y = baseline + entry.Baseline;
            bool edges = WipeEdges(style, capture);

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
                // 羽化：带宽按注音字号；分段区间与硬切换相同（单段时取整个注音宽度），首段向左、两态装饰不同时末段向右的延伸取端点处的 m。
                if (FeatheredSegment(state, sample.TimeUs, entry.Segments.Length == 1 ? x : start, entry.Segments.Length == 1 ? x + entry.Shaped.Width : end,
                    (float)(style.Wipe.FeatherRatio * entry.Options.FontSizePx), new(left, clip.Top, right, clip.Bottom),
                    segment.Left == 0, edges && segment.Right == entry.Shaped.Width) is { } wipe)
                {
                    using (wipe)
                    {
                        canvas.Save();
                        canvas.ClipRect(new(left, clip.Top, right, clip.Bottom), antialias: false);
                        DrawWiped(canvas, wipe, () => WipeStatesDiffer(style, capture), capture, Draw);
                        canvas.Restore();
                    }
                    continue;
                }
                // 两态装饰不同时，末段唱完后已唱一侧延到右边界，与正文行尾一致（行首本来就延到左边界）。
                if (edges && progress >= 1d && segment.Right == entry.Shaped.Width) split = right;
                DrawPart(false);
                if (progress > 0d) DrawPart(true);
                void DrawPart(bool sung)
                {
                    canvas.Save();
                    canvas.ClipRect(sung ? new(left, clip.Top, Math.Min(right, split), clip.Bottom)
                        : new(Math.Max(left, progress > 0d ? split : left), clip.Top, right, clip.Bottom), antialias: false);
                    Draw(sung);
                    canvas.Restore();
                }
                void Draw(bool sung)
                {
                    if (glow)
                        DrawLineGlow(canvas, entry.Shaped, entry.Options, x, y, style, antialias, drawUnitIndices, capture, ruby: true, state: sung);
                    else
                        DrawStyledLine(canvas, entry.Shaped, entry.Options, x, y,
                            sung ? style.SungFill.Color : style.UnsungFill.Color,
                            sung ? style.SungFill.Gradient : style.UnsungFill.Gradient,
                            style, composition, antialias, drawUnitIndices, capture, sungPass: sung, rubyGlyph: true,
                            lineSpan: new LineSpan(origin, entry.BaseWidth));
                }
            }
        }
    }

    /// <summary>
    /// 注音分段的扫字分区（P3 设计 §10.1 第 6 条）；没有过渡带时为空，照原来的硬切换绘制。
    /// </summary>
    /// <param name="state">分段采样：分段有时间时用分段时间，跟随元素时用元素时间。</param>
    /// <param name="timeUs">帧时刻。</param>
    /// <param name="start">分段区间的起点（进度 0）。</param>
    /// <param name="end">分段区间的终点（进度 1）。</param>
    /// <param name="width">带宽。</param>
    /// <param name="bounds">分段的裁剪框（首段左延、末段右延到画布边界）。</param>
    /// <param name="before">首段：起点之前取起点处的 m。</param>
    /// <param name="after">末段且两态装饰不同：终点之后取终点处的 m；否则终点之后是未唱。</param>
    private static WipePartition? FeatheredSegment(RubyTextSegmentSample state, long timeUs, float start, float end, float width,
        SKRect bounds, bool before, bool after)
    {
        if (state.Source == ProgressSource.Unavailable
            || SungPosition(state.Time, state.Progress, timeUs, start, end, width) is not { } position)
            return null;
        WipePartition wipe = new(new SKPath());
        AddRamp(wipe, bounds, start, end, position, width);
        if (before) AddConstant(wipe, new(bounds.Left, bounds.Top, start, bounds.Bottom), Mask(start));
        if (after) AddConstant(wipe, new(end, bounds.Top, bounds.Right, bounds.Bottom), Mask(end));
        if (wipe.Band.Count != 0) return wipe;
        wipe.Dispose();
        return null;
        float Mask(float x) => Math.Clamp(.5f + (position - x) / width, 0f, 1f);
    }
}
