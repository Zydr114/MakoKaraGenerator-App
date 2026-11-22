using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia.Effects.Svg;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private const int MaxInlineSvgCacheEntries = 64;
    private static readonly object InlineSvgCacheLock = new();
    private static readonly Dictionary<string, CachedInlineSvg> InlineSvgCache = new(StringComparer.Ordinal);
    private static readonly Queue<string> InlineSvgCacheOrder = new();

    internal sealed class CachedInlineSvg(SKPicture unitPicture, SKPath silhouettePath, float aspectRatio)
    {
        private int _refCount = 1;

        internal SKPicture UnitPicture { get; } = unitPicture;
        internal SKPath SilhouettePath { get; } = silhouettePath;
        internal float AspectRatio { get; } = aspectRatio;
        internal bool IsDisposed => Volatile.Read(ref _refCount) == 0;

        internal bool TryAddRef()
        {
            while (true)
            {
                int current = Volatile.Read(ref _refCount);
                if (current <= 0) return false;
                if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current) return true;
            }
        }

        internal void Release()
        {
            int remaining = Interlocked.Decrement(ref _refCount);
            if (remaining == 0)
            {
                Exception? error = SkiaNativeResourceCleanup.Run(null, UnitPicture.Dispose, SilhouettePath.Dispose);
                if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            }
            else if (remaining < 0)
            {
                throw new InvalidOperationException("CachedInlineSvg 引用计数下溢。");
            }
        }
    }

    internal sealed class InlineSvgLease : IDisposable
    {
        private CachedInlineSvg? _entry;

        internal InlineSvgLease(CachedInlineSvg entry) => _entry = entry;

        internal CachedInlineSvg Entry => _entry ?? throw new ObjectDisposedException(nameof(InlineSvgLease));

        public void Dispose() => Interlocked.Exchange(ref _entry, null)?.Release();
    }

    internal static InlineSvgLease ResolveInlineSvg(string svgXml)
    {
        lock (InlineSvgCacheLock)
        {
            if (InlineSvgCache.TryGetValue(svgXml, out var cached) && cached.TryAddRef())
                return new InlineSvgLease(cached);
        }

        var budget = SvgResourceBudget.Default;
        var (safeXml, vw, vh) = SvgStaticCompiler.CompileInline(svgXml, budget, CancellationToken.None);
        using var nativeCompiler = new SvgNativePictureCompiler(safeXml, vw, vh, "inline-countdown.svg", budget, CancellationToken.None);
        var (unitPicture, silhouettePath, aspectRatio) = nativeCompiler.CompileInline();
        var compiled = new CachedInlineSvg(unitPicture, silhouettePath, aspectRatio);

        CachedInlineSvg? discarded = null;
        CachedInlineSvg? evicted = null;
        InlineSvgLease lease;
        lock (InlineSvgCacheLock)
        {
            if (InlineSvgCache.TryGetValue(svgXml, out var existing) && existing.TryAddRef())
            {
                discarded = compiled;
                lease = new InlineSvgLease(existing);
            }
            else
            {
                if (InlineSvgCache.Count >= MaxInlineSvgCacheEntries && InlineSvgCacheOrder.TryDequeue(out string? evictedKey)
                    && InlineSvgCache.Remove(evictedKey, out var evictedEntry))
                {
                    evicted = evictedEntry;
                }

                // 缓存持有初始 1 份引用；调用方在入表前通过 TryAddRef 取得独立绘制租约。
                if (!compiled.TryAddRef())
                    throw new InvalidOperationException("新建 CachedInlineSvg 引用计数无效。");
                InlineSvgCache[svgXml] = compiled;
                InlineSvgCacheOrder.Enqueue(svgXml);
                lease = new InlineSvgLease(compiled);
            }
        }

        discarded?.Release();
        evicted?.Release();
        return lease;
    }

    private static void DrawCountdownShapeState(SKCanvas canvas, RenderRequest request,
        SceneTextMarkSample mark, LyricStyle effectiveStyle, bool sungPass, CapturePaint? capture)
    {
        var geometry = mark.Geometry;
        float left = (float)geometry.TopLeft.X;
        float top = (float)geometry.TopLeft.Y;
        float right = (float)geometry.BottomRight.X;
        float bottom = (float)geometry.BottomRight.Y;
        bool antialias = request.Quality != RenderQuality.Draft;

        LyricStyle style = effectiveStyle.ForState(sungPass) with { Opacity = 1 };
        if (capture is not null) style = capture.Style(style);
        FillSpec fill = sungPass ? style.SungFill : style.UnsungFill;
        StrokeLayers strokes = style.Unsung.Strokes;
        float outlineReach = strokes.IsEnabled ? MathF.Max(strokes.TotalWidthPx, 0f) : 0f;

        using InlineSvgLease? svgLease = mark.Shape.Kind == CountdownShapeKind.CustomSvg && !string.IsNullOrWhiteSpace(mark.Shape.SvgXml)
            ? ResolveInlineSvg(mark.Shape.SvgXml)
            : null;
        CachedInlineSvg? svg = svgLease?.Entry;

        (float Left, float Top, float Width, float Height) svgBox = default;
        if (svg is not null)
        {
            float boxW = Math.Max(1f, right - left);
            float boxH = Math.Max(1f, bottom - top);
            float aspect = Math.Clamp(svg.AspectRatio, 0.05f, 20f);
            float drawW = boxW / boxH > aspect ? boxH * aspect : boxW;
            float drawH = boxW / boxH > aspect ? boxH : boxW / aspect;
            svgBox = (left + (boxW - drawW) * 0.5f, top + (boxH - drawH) * 0.5f, drawW, drawH);
        }

        using SKPath path = BuildCountdownShapePath(mark.Shape, left, top, right, bottom, svg, svgBox);
        if (path.IsEmpty && (svg is null || mark.Shape.SvgColorMode != CountdownSvgColorMode.OriginalColor))
            return;

        if (!path.IsEmpty && capture?.IncludesGlyph(false) != false && capture is not { Part: not null, GlowOnly: false }
            && capture?.RequestedSungPass != true && style.Glow is { IsEnabled: true } glow)
        {
            SkiaSceneGeometry.ValidateGlow(glow);
            using var outerReservation = capture?.Layer("CaptureGlowExteriorLayer");
            capture?.MarkDrawn(canvas);
            int exterior = canvas.SaveLayer();
            using var maskReservation = capture?.Layer("CaptureGlowMaskLayer");
            using var blur = glow.BlurRadiusPx > 0 ? SKImageFilter.CreateBlur(glow.BlurRadiusPx, glow.BlurRadiusPx) : null;
            if (glow.BlurRadiusPx > 0 && blur is null)
                throw new PreparedFrameException("GlowFilterUnavailable", "无法创建外发光 Gaussian 滤镜，不能丢弃装饰。");
            using var tint = SKColorFilter.CreateBlendMode(ToSkColorWithOpacity(glow.Color, glow.Opacity * style.Opacity), SKBlendMode.SrcIn);
            using var glowLayerPaint = new SKPaint { ImageFilter = blur, ColorFilter = tint };
            int maskLayer = canvas.SaveLayer(glowLayerPaint);
            DrawOutlinePath(canvas, path, outlineReach + (glow.SpreadPx > 0 ? glow.SpreadPx : 0f),
                glow.SpreadPx > 0 ? SKStrokeJoin.Round : ToSkStrokeJoin(strokes.OuterJoin), SKColors.White, antialias, SKBlendMode.SrcOver);
            canvas.RestoreToCount(maskLayer);
            DrawOutlinePath(canvas, path, outlineReach, ToSkStrokeJoin(strokes.OuterJoin), SKColors.White, antialias, SKBlendMode.DstOut);
            canvas.RestoreToCount(exterior);
        }
        if (capture?.GlowOnly == true) return;

        ShadowSpec? shadowSpec = SkiaSceneGeometry.EffectiveShadow(style);
        if (!path.IsEmpty && shadowSpec is { IsEnabled: true }
            && capture?.StrokeOnly != true && capture?.OuterStrokeOnly != true && capture?.RequestedSungPass != true
            && (capture?.ShadowOnly == true || capture?.Part is null))
        {
            using var shadowFilter = CreateTextShadow(shadowSpec, 1f, shadowOnly: true)
                ?? throw new PreparedFrameException("CaptureShadowFilterUnavailable", "无法创建真实 shadow-only 滤镜，不能当作没有阴影。");
            DrawOutlinePath(canvas, path, outlineReach, ToSkStrokeJoin(strokes.OuterJoin), SKColors.White, antialias, SKBlendMode.SrcOver, shadowFilter);
        }
        if (capture?.ShadowOnly == true) return;

        if (svg is not null && mark.Shape.SvgColorMode == CountdownSvgColorMode.OriginalColor)
        {
            if (capture?.StrokeOnly == true || capture?.OuterStrokeOnly == true || capture?.IncludesGlyph(false) == false) return;
            byte alpha = ToOpacityByte(sungPass ? 1f : 0.38f);
            canvas.Save();
            if (alpha < 255)
            {
                using var layerPaint = new SKPaint { Color = SKColors.White.WithAlpha(alpha) };
                canvas.SaveLayer(layerPaint);
            }
            canvas.Translate(svgBox.Left, svgBox.Top);
            canvas.Scale(svgBox.Width, svgBox.Height);
            canvas.DrawPicture(svg.UnitPicture);
            if (alpha < 255) canvas.Restore();
            canvas.Restore();
            return;
        }

        SKMatrix device = canvas.TotalMatrix;
        var gradientBox = (Left: left, Top: top, Width: Math.Max(1f, right - left), Height: Math.Max(1f, bottom - top));

        if (strokes.IsEnabled)
        {
            int first = capture?.OuterStrokeOnly == true ? 1 : 0;
            if (strokes.Count > first)
            {
                var reach = new float[strokes.Count];
                for (int i = 0; i < strokes.Count; i++)
                    reach[i] = (i == 0 ? 0f : reach[i - 1]) + strokes[i].WidthPx;

                using var strokeReservation = capture?.Layer("CaptureStrokeLayer");
                capture?.MarkDrawn(canvas);
                int strokeLayer = canvas.SaveLayer();
                for (int i = strokes.Count - 1; i >= first; i--)
                {
                    var stroke = strokes[i];
                    using SKShader? shader = stroke.Gradient is { } sg
                        ? CreateGradientShader(sg, gradientBox, style.Opacity, device)
                        : null;
                    using var strokePaint = new SKPaint
                    {
                        Style = SKPaintStyle.Stroke,
                        StrokeWidth = reach[i] * 2f,
                        StrokeJoin = ToSkStrokeJoin(stroke.Join),
                        IsAntialias = antialias,
                        BlendMode = i == strokes.Count - 1 ? SKBlendMode.SrcOver : SKBlendMode.Src,
                        Shader = shader,
                        Color = shader is null ? ToSkColorWithOpacity(stroke.Color ?? RgbaColor.Transparent, style.Opacity) : SKColors.White,
                    };
                    canvas.DrawPath(path, strokePaint);
                }

                if (first == 0)
                    DrawOutlinePath(canvas, path, 0f, SKStrokeJoin.Round, SKColors.White, antialias, SKBlendMode.DstOut);
                else
                    DrawOutlinePath(canvas, path, strokes[0].WidthPx, ToSkStrokeJoin(strokes[0].Join), SKColors.White, antialias, SKBlendMode.DstOut);

                canvas.RestoreToCount(strokeLayer);
            }
        }
        if (capture?.StrokeOnly == true || capture?.IncludesGlyph(false) == false) return;

        using SKShader? fillShader = fill.Gradient is { } fg
            ? CreateGradientShader(fg, gradientBox, style.Opacity, device)
            : null;
        using var fillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = antialias,
            Shader = fillShader,
            Color = fillShader is null ? ToSkColorWithOpacity(fill.Color, style.Opacity) : SKColors.White,
        };
        canvas.DrawPath(path, fillPaint);
    }

    private static SKPath BuildCountdownShapePath(CountdownShapeSpec shape, float left, float top, float right, float bottom,
        CachedInlineSvg? svg, (float Left, float Top, float Width, float Height) svgBox)
    {
        var path = new SKPath();
        float width = Math.Max(1f, right - left);
        float height = Math.Max(1f, bottom - top);
        float cx = (left + right) * 0.5f;
        float cy = (top + bottom) * 0.5f;
        switch (shape.Kind)
        {
            case CountdownShapeKind.Circle:
                path.AddOval(SKRect.Create(left, top, width, height));
                break;
            case CountdownShapeKind.SteppedBars:
            case CountdownShapeKind.Capsule:
                float radius = shape.Kind == CountdownShapeKind.Capsule
                    ? Math.Min(width, height) * 0.5f
                    : Math.Min(width * 0.35f, height * 0.35f);
                path.AddRoundRect(SKRect.Create(left, top, width, height), radius, radius);
                break;
            case CountdownShapeKind.Diamond:
                path.MoveTo(cx, top);
                path.LineTo(right, cy);
                path.LineTo(cx, bottom);
                path.LineTo(left, cy);
                path.Close();
                break;
            case CountdownShapeKind.Star:
                float outerR = Math.Min(width, height) * 0.5f;
                float innerR = outerR * 0.42f;
                for (int i = 0; i < 10; i++)
                {
                    float r = (i & 1) == 0 ? outerR : innerR;
                    float angle = -MathF.PI * 0.5f + i * (MathF.PI / 5f);
                    float px = cx + MathF.Cos(angle) * r;
                    float py = cy + MathF.Sin(angle) * r;
                    if (i == 0) path.MoveTo(px, py);
                    else path.LineTo(px, py);
                }
                path.Close();
                break;
            case CountdownShapeKind.CustomSvg when svg is not null && !svg.SilhouettePath.IsEmpty:
                path.AddPath(svg.SilhouettePath, SKMatrix.CreateScaleTranslation(svgBox.Width, svgBox.Height, svgBox.Left, svgBox.Top));
                path.FillType = svg.SilhouettePath.FillType;
                break;
        }
        return path;
    }

    private static void DrawOutlinePath(SKCanvas canvas, SKPath path, float radius, SKStrokeJoin join, SKColor color,
        bool antialias, SKBlendMode blendMode, SKImageFilter? imageFilter = null)
    {
        using var paint = new SKPaint
        {
            Style = radius > 0f ? SKPaintStyle.StrokeAndFill : SKPaintStyle.Fill,
            StrokeWidth = radius * 2f,
            StrokeJoin = join,
            Color = color,
            IsAntialias = antialias,
            BlendMode = blendMode,
            ImageFilter = imageFilter,
        };
        canvas.DrawPath(path, paint);
    }
}
