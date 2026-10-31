using SkiaSharp;
using SkiaSharp.HarfBuzz;
using MakoKaraGenerator.Text;

namespace MakoKaraGenerator.Spike.Rendering;

/// <summary>
/// 按帧描述把歌词画到 Skia 画布上。
/// </summary>
/// <remarks>
/// <para>
/// 渲染的要点是 docs/development.md §9.4 的两阶段策略：
/// <b>先用完整行文本一次整形并确定元素边界，再在稳定布局上做逐字裁切</b>。
/// 因此这里从头到尾不重新排版，只按已算好的 x 绘制并用矩形裁切推进进度，
/// 整行不会因为逐字高亮而跳动。
/// </para>
/// <para>
/// 字体按元素逐个绘制（而不是整行一次绘制），因为回退字体是逐元素的：
/// 一个 emoji 必须用 <c>Noto Color Emoji</c>，相邻汉字必须用中文字体。
/// 由于每个元素的 x 都来自整形结果，逐元素绘制不会丢失字距。
/// </para>
/// <para>
/// 本渲染器同时服务预览与离屏导出，二者只在尺寸和画布上不同（§10.3）。
/// </para>
/// </remarks>
public static class KaraokeFrameRenderer
{
    private const float HorizontalPadding = 32f;

    /// <summary>把一帧画到画布上。</summary>
    /// <param name="canvas">目标画布。</param>
    /// <param name="frame">帧描述。</param>
    /// <exception cref="ArgumentNullException"><paramref name="canvas"/> 或 <paramref name="frame"/> 为 <see langword="null"/> 时抛出。</exception>
    public static void Render(SKCanvas canvas, KaraokeFrame frame)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(frame);

        canvas.Clear(frame.BackgroundColor);

        float lineWidth = frame.Line.Width;
        float lineX = Math.Max(HorizontalPadding, (frame.Width - lineWidth) / 2f);
        float baseline = (frame.Height * 0.5f) + (frame.FontSize * 0.35f);

        DrawGrid(canvas, frame, baseline);

        using FontCache fonts = new(frame.FontSize);
        using SKPaint paint = new() { IsAntialias = true };

        // 第一遍：整行按"未唱"色画一遍，元素各用自己的字体。
        DrawElements(canvas, frame, fonts, paint, lineX, baseline, frame.UnsungColor);

        // 第二遍：裁切到已唱宽度，再按"已唱"色覆盖。
        float progressWidth = Math.Clamp(frame.ProgressX, 0f, lineWidth);

        if (progressWidth > 0f)
        {
            canvas.Save();
            canvas.ClipRect(SKRect.Create(lineX - 1f, 0f, progressWidth + 1f, frame.Height));
            DrawElements(canvas, frame, fonts, paint, lineX, baseline, frame.SungColor);
            canvas.Restore();
        }

        DrawElementBoundaries(canvas, frame, lineX, baseline);
        DrawPlayhead(canvas, frame, lineX + progressWidth);
    }

    private static void DrawGrid(SKCanvas canvas, KaraokeFrame frame, float baseline)
    {
        using SKPaint gridPaint = new()
        {
            IsAntialias = false,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            Color = frame.GridColor,
        };

        canvas.DrawLine(0f, baseline + 2f, frame.Width, baseline + 2f, gridPaint);
        canvas.DrawLine(0f, baseline - (frame.FontSize * 0.75f), frame.Width, baseline - (frame.FontSize * 0.75f), gridPaint);

        using SKPaint centerPaint = new()
        {
            IsAntialias = false,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            Color = frame.GridColor,
            PathEffect = SKPathEffect.CreateDash([4f, 6f], 0f),
        };

        canvas.DrawLine(frame.Width / 2f, 0f, frame.Width / 2f, frame.Height, centerPaint);
    }

    private static void DrawElements(
        SKCanvas canvas,
        KaraokeFrame frame,
        FontCache fonts,
        SKPaint paint,
        float lineX,
        float baseline,
        SKColor color)
    {
        paint.Color = color;

        foreach (ShapedTextElement element in frame.Line.Elements)
        {
            FontCache.Entry entry = fonts.Get(element.FamilyName);

            // 刻意用 DrawShapedText 而不是 DrawText：
            // DrawText 会重新整形且不走 HarfBuzz 的合字规则，
            // ZWJ 序列（如 🏳️‍🌈、👩‍🚀）会被拆成多个字形并重叠绘制。
            // 这里传入按字体缓存的 SKShaper，使绘制与布局使用同一套整形结果。
            canvas.DrawShapedText(
                entry.Shaper,
                element.Text,
                lineX + element.X,
                baseline,
                SKTextAlign.Left,
                entry.Font,
                paint);
        }
    }

    private static void DrawElementBoundaries(SKCanvas canvas, KaraokeFrame frame, float lineX, float baseline)
    {
        using SKPaint boundaryPaint = new()
        {
            IsAntialias = false,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            Color = frame.BoundaryColor,
        };

        float top = baseline - (frame.FontSize * 0.75f);

        foreach (KaraokeElementSpan element in frame.Elements)
        {
            float x = lineX + element.X;
            canvas.DrawLine(x, top, x, top + 6f, boundaryPaint);
        }

        if (frame.Elements.Count > 0)
        {
            float lastX = lineX + frame.Elements[^1].XEnd;
            canvas.DrawLine(lastX, top, lastX, top + 6f, boundaryPaint);
        }
    }

    private static void DrawPlayhead(SKCanvas canvas, KaraokeFrame frame, float x)
    {
        using SKPaint playheadPaint = new()
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            Color = frame.PlayheadColor,
        };

        canvas.DrawLine(x, 0f, x, frame.Height, playheadPaint);
    }

    /// <summary>按字体族名缓存 <see cref="SKTypeface" /> 与 <see cref="SKFont" />。</summary>
    /// <remarks>
    /// 刻意在每次渲染时新建而不是长期持有：native 字体对象不跨线程共享，
    /// 因此这个渲染器可以从 UI 线程（预览）或后台线程（导出）安全调用。
    /// 正式实现应当把字体缓存放到渲染线程一侧，避免每帧重复查找字体。
    /// </remarks>
    internal sealed class FontCache : IDisposable
    {
        private readonly float _size;
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

        public FontCache(float size)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
            _size = size;
        }

        public Entry Get(string familyName)
        {
            if (_entries.TryGetValue(familyName, out Entry? entry))
            {
                return entry;
            }

            SKTypeface typeface = SKTypeface.FromFamilyName(familyName) ?? SKTypeface.Default;
            Entry created = new(typeface, new SKFont(typeface, _size), new SKShaper(typeface));
            _entries[familyName] = created;
            return created;
        }

        public void Dispose()
        {
            foreach (Entry entry in _entries.Values)
            {
                entry.Dispose();
            }

            _entries.Clear();
        }

        internal sealed class Entry : IDisposable
        {
            public Entry(SKTypeface typeface, SKFont font, SKShaper shaper)
            {
                Typeface = typeface;
                Font = font;
                Shaper = shaper;
            }

            public SKTypeface Typeface { get; }

            public SKFont Font { get; }

            public SKShaper Shaper { get; }

            public void Dispose()
            {
                Shaper.Dispose();
                Font.Dispose();
                Typeface.Dispose();
            }
        }
    }
}
