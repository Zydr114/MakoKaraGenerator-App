using System.Xml;
using MakoKaraGenerator.Core.Effects;
using SkiaSharp;
using global::Svg.Model.Services;
using global::Svg.Skia;
using S = ShimSkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia.Effects.Svg;

// Svg.Skia owns SVG semantics. This narrow recorder avoids its non-disposable native caches.
// Only public geometry/value conversions are reused; no upstream Draw/ToPicture/ToSKPaint path is called.
internal sealed class SvgNativePictureCompiler : IDisposable
{
    private readonly SvgStaticDocument _document;
    private readonly SvgResourceBudget _limits;
    private readonly SvgNativeImageStore _images;
    private readonly CancellationToken _token;
    private readonly SKSvgSettings _settings;
    private readonly SkiaModel _convert;
    private readonly HashSet<object> _stack = new(ReferenceEqualityComparer.Instance);
    private long _operations;
    private bool _disposed;

    internal SvgNativePictureCompiler(SvgStaticDocument document, SvgResourceBudget limits,
        SvgNativeImageStore images, CancellationToken token)
    {
        _document = document; _limits = limits; _images = images; _token = token;
        _settings = new() { AlphaType = SKAlphaType.Premul, ColorType = SKColorType.Bgra8888,
            EnableSvgFonts = false, EnableTextReferences = false, EnableJavaScript = false,
            EnableExternalJavaScript = false, TypefaceProviders = [] };
        _convert = new(_settings);
    }

    internal long Operations => _operations;

    internal SKPicture Compile()
    {
        ObjectDisposedException.ThrowIf(_disposed, this); _token.ThrowIfCancellationRequested();
        XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = _limits.MaximumSafeXmlBytes, MaxCharactersFromEntities = 1,
            IgnoreProcessingInstructions = false, ConformanceLevel = ConformanceLevel.Document };
        using var text = new StringReader(_document.SafeXml);
        using var reader = XmlReader.Create(text, settings);
        var fragment = SvgService.Open(reader, false) ?? throw Fail("受控 XML 未生成 SVG 文档。");
        _token.ThrowIfCancellationRequested();
        var assets = new StrictSvgAssetLoader(_document, _token);
        var model = SvgSceneRuntime.CreateModel(fragment, assets, global::Svg.Model.DrawAttributes.None,
            new(0, 0, (float)_document.Inspection.Viewport.Width, (float)_document.Inspection.Viewport.Height));
        assets.ThrowIfFaulted();
        if (model is null) throw Fail("已准入的 SVG 未生成完整模型。");
        using var source = Record(model);
        _token.ThrowIfCancellationRequested();
        float width = (float)_document.Inspection.Viewport.Width, height = (float)_document.Inspection.Viewport.Height;
        SKRect bounds = source.CullRect;
        if (!Finite(bounds)) throw Fail("native Picture bounds 非有限。");
        var unitBounds = new SKRect(bounds.Left / width, bounds.Top / height, bounds.Right / width, bounds.Bottom / height);
        using var recorder = new SKPictureRecorder(); var canvas = recorder.BeginRecording(unitBounds);
        canvas.Scale(1 / width, 1 / height); canvas.DrawPicture(source);
        return recorder.EndRecording() ?? throw Fail("无法记录单位空间 Picture。");
    }

    private SKPicture Record(S.SKPicture picture)
    {
        Enter(picture);
        try
        {
            SKRect bounds = _convert.ToSKRect(picture.CullRect);
            if (!Finite(bounds)) throw Fail("模型 bounds 非有限。");
            using var recorder = new SKPictureRecorder(); var canvas = recorder.BeginRecording(bounds);
            int depth = 0;
            foreach (var command in picture.Commands ?? [])
            {
                Charge();
                switch (command)
                {
                    case S.SaveCanvasCommand: canvas.Save(); depth++; break;
                    case S.RestoreCanvasCommand:
                        if (depth == 0) throw Fail("模型 Restore 无匹配 Save。");
                        canvas.Restore(); depth--; break;
                    case S.SetMatrixCanvasCommand transform:
                        var matrix = _convert.ToSKMatrix(transform.DeltaMatrix); canvas.Concat(in matrix); break;
                    case S.ClipRectCanvasCommand clip:
                        canvas.ClipRect(_convert.ToSKRect(clip.Rect), _convert.ToSKClipOperation(clip.Operation), clip.Antialias); break;
                    case S.ClipPathCanvasCommand clip:
                        using (var path = Clip(clip.ClipPath) ?? new SKPath())
                            canvas.ClipPath(path, _convert.ToSKClipOperation(clip.Operation), clip.Antialias);
                        break;
                    case S.DrawPathCanvasCommand { Path: { } path, Paint: { } paint }:
                        using (var nativePath = Path(path))
                        using (var scope = new NativeScope())
                            canvas.DrawPath(nativePath, Paint(paint, scope));
                        break;
                    case S.DrawImageCanvasCommand { Image: { } image } draw:
                        using (var scope = new NativeScope())
                            canvas.DrawImage(_images.Get(_document, image), _convert.ToSKRect(draw.Source), _convert.ToSKRect(draw.Dest),
                                draw.Sampling is { } sampling ? Sampling(sampling)
                                    : _convert.ToSKSamplingOptions(draw.Paint?.FilterQuality ?? S.SKFilterQuality.None),
                                draw.Paint is { } imagePaint ? Paint(imagePaint, scope) : null);
                        break;
                    case S.DrawPictureCanvasCommand { Picture: { } child }:
                        using (var nested = Record(child)) canvas.DrawPicture(nested);
                        break;
                    case S.SaveLayerCanvasCommand layer:
                        using (var scope = new NativeScope())
                        {
                            if (layer.Paint is { } layerPaint) canvas.SaveLayer(Paint(layerPaint, scope));
                            else canvas.SaveLayer();
                        }
                        depth++; break;
                    default: throw Fail("静态 profile 没有实现的 native 命令：" + command.GetType().Name);
                }
            }
            if (depth != 0) throw Fail("模型 Save/Restore 不平衡。");
            return recorder.EndRecording() ?? throw Fail("native Picture 录制失败。");
        }
        finally { _stack.Remove(picture); }
    }

    private SKPath Path(S.SKPath source)
    {
        foreach (var command in source.Commands ?? [])
        {
            Charge();
            if (command is not (S.MoveToPathCommand or S.LineToPathCommand or S.ArcToPathCommand
                or S.QuadToPathCommand or S.CubicToPathCommand or S.ClosePathCommand or S.AddRectPathCommand
                or S.AddRoundRectPathCommand or S.AddOvalPathCommand or S.AddCirclePathCommand or S.AddPolyPathCommand))
                throw Fail("静态 profile 没有实现的路径命令：" + command.GetType().Name);
        }
        return _convert.ToSKPath(source);
    }

    private SKPath? Clip(S.ClipPath? source)
    {
        if (source is null) throw Fail("裁剪路径缺失。");
        if (source.Clips is null) return null;
        Enter(source); SKPath? result = null;
        try
        {
            foreach (var item in source.Clips ?? [])
            {
                if (item.Path is null) throw Fail("裁剪几何缺失。");
                SKPath path = Path(item.Path);
                try
                {
                    if (item.Clip is { } nested)
                    {
                        using var clipped = Clip(nested);
                        if (clipped is not null)
                        { var replacement = path.Op(clipped, SKPathOp.Intersect) ?? throw Fail("裁剪求交失败。"); path.Dispose(); path = replacement; }
                    }
                    if (item.Transform is { } transform) path.Transform(_convert.ToSKMatrix(transform));
                    if (result is null) { result = path; path = null!; }
                    else { var joined = result.Op(path, SKPathOp.Union) ?? throw Fail("裁剪求并失败。"); result.Dispose(); result = joined; }
                }
                finally { path?.Dispose(); }
            }
            // Svg.Model emits empty optional ClipPath placeholders on ordinary geometry.
            // They carry no additional intersection; an authored empty top-level clip is
            // turned into an empty SKPath by the canvas command above.
            if (result is null) return null;
            if (source.Clip is { } outer)
            {
                using var clipped = Clip(outer);
                if (clipped is not null)
                { var replacement = result.Op(clipped, SKPathOp.Intersect) ?? throw Fail("复合裁剪失败。"); result.Dispose(); result = replacement; }
            }
            if (source.Transform is { } matrix) result.Transform(_convert.ToSKMatrix(matrix));
            var completed = result; result = null; return completed;
        }
        finally { result?.Dispose(); _stack.Remove(source); }
    }

    private SKPaint Paint(S.SKPaint source, NativeScope scope)
    {
        if (source.Typeface is not null) throw Fail("静态图形模型携带了不允许的字体。");
        if (source.IsStrokeNonScaling) throw Fail("静态 profile 不允许非缩放描边。");
        var paint = scope.Own(new SKPaint());
        paint.Style = _convert.ToSKPaintStyle(source.Style);
        paint.IsAntialias = source.IsAntialias; paint.IsDither = source.IsDither;
        paint.StrokeWidth = source.StrokeWidth; paint.StrokeCap = _convert.ToSKStrokeCap(source.StrokeCap);
        paint.StrokeJoin = _convert.ToSKStrokeJoin(source.StrokeJoin); paint.StrokeMiter = source.StrokeMiter;
        paint.Color = source.Color is { } color ? _convert.ToSKColor(color) : SKColors.Transparent;
        paint.BlendMode = _convert.ToSKBlendMode(source.BlendMode);
        paint.Shader = Shader(source.Shader, scope); paint.ColorFilter = ColorFilter(source.ColorFilter, scope);
        paint.ImageFilter = Filter(source.ImageFilter, scope);
        paint.PathEffect = source.PathEffect is null ? null : scope.Own(_convert.ToSKPathEffect(source.PathEffect)
            ?? throw Fail("路径效果转换失败。"));
        return paint;
    }

    private SKShader? Shader(S.SKShader? source, NativeScope scope)
    {
        if (source is null) return null;
        if (source is not (S.ColorShader or S.LinearGradientShader or S.RadialGradientShader or S.TwoPointConicalGradientShader))
            throw Fail("静态 profile 没有实现的 shader：" + source.GetType().Name);
        return scope.Own(_convert.ToSKShader(source) ?? throw Fail("渐变转换失败。"));
    }
    private SKColorFilter? ColorFilter(S.SKColorFilter? source, NativeScope scope)
    {
        if (source is null) return null;
        if (source is S.ColorMatrixColorFilter { Matrix: { Length: 20 } values })
        {
            // Svg.Skia 4.9.1's model retains byte-range matrix offsets. Our pinned
            // SkiaSharp 3 binding passes a normalized RGBA matrix directly to Skia.
            // Restore SVG's normalized offsets without changing the shared model.
            var matrix = (float[])values.Clone();
            foreach (int index in new[] { 4, 9, 14, 19 }) matrix[index] /= 255f;
            return scope.Own(SKColorFilter.CreateColorMatrix(matrix) ?? throw Fail("颜色矩阵创建失败。"));
        }
        if (source is not (S.ColorMatrixColorFilter or S.BlendModeColorFilter))
            throw Fail("静态 profile 没有实现的颜色过滤：" + source.GetType().Name);
        return scope.Own(_convert.ToSKColorFilter(source) ?? throw Fail("颜色过滤转换失败。"));
    }

    private SKImageFilter? Filter(S.SKImageFilter? source, NativeScope scope)
    {
        if (source is null) return null;
        Enter(source);
        try
        {
            Charge();
            SKImageFilter? result = source switch
            {
                S.BlurImageFilter f => f.Clip is { } r
                    ? SKImageFilter.CreateBlur(f.SigmaX, f.SigmaY, Filter(f.Input, scope), _convert.ToSKRect(r))
                    : SKImageFilter.CreateBlur(f.SigmaX, f.SigmaY, Filter(f.Input, scope)),
                S.OffsetImageFilter f => f.Clip is { } r
                    ? SKImageFilter.CreateOffset(f.Dx, f.Dy, Filter(f.Input, scope), _convert.ToSKRect(r))
                    : SKImageFilter.CreateOffset(f.Dx, f.Dy, Filter(f.Input, scope)),
                S.ColorFilterImageFilter f => f.Clip is { } r
                    ? SKImageFilter.CreateColorFilter(ColorFilter(f.ColorFilter, scope) ?? throw Fail("滤镜缺少颜色矩阵。"), Filter(f.Input, scope), _convert.ToSKRect(r))
                    : SKImageFilter.CreateColorFilter(ColorFilter(f.ColorFilter, scope) ?? throw Fail("滤镜缺少颜色矩阵。"), Filter(f.Input, scope)),
                S.MergeImageFilter f => Merge(f, scope),
                S.PictureImageFilter { Picture: { } picture } f => PictureFilter(picture, f.Clip, scope),
                S.BlendModeImageFilter f => f.Clip is { } r
                    ? SKImageFilter.CreateBlendMode(_convert.ToSKBlendMode(f.Mode), Filter(f.Background, scope), Filter(f.Foreground, scope), _convert.ToSKRect(r))
                    : SKImageFilter.CreateBlendMode(_convert.ToSKBlendMode(f.Mode), Filter(f.Background, scope), Filter(f.Foreground, scope)),
                _ => throw Fail("静态 profile 没有实现的滤镜：" + source.GetType().Name),
            };
            return scope.Own(result ?? throw Fail("滤镜 native 创建失败。"));
        }
        finally { _stack.Remove(source); }
    }
    private SKImageFilter Merge(S.MergeImageFilter source, NativeScope scope)
    {
        var filters = (source.Filters ?? throw Fail("merge 输入缺失。")).Select(filter => Filter(filter, scope) ?? throw Fail("merge 输入转换失败。")).ToArray();
        return source.Clip is { } clip ? SKImageFilter.CreateMerge(filters, _convert.ToSKRect(clip)) : SKImageFilter.CreateMerge(filters);
    }
    private SKImageFilter PictureFilter(S.SKPicture source, S.SKRect? clip, NativeScope scope)
    {
        var picture = scope.Own(Record(source));
        return SKImageFilter.CreatePicture(picture, _convert.ToSKRect(clip ?? source.CullRect));
    }

    private static SKSamplingOptions Sampling(S.SKSamplingOptions source) => source.UseCubic
        ? new(new SKCubicResampler(source.Cubic.B, source.Cubic.C))
        : new(source.Filter == S.SKFilterMode.Linear ? SKFilterMode.Linear : SKFilterMode.Nearest,
            source.Mipmap switch { S.SKMipmapMode.Linear => SKMipmapMode.Linear, S.SKMipmapMode.Nearest => SKMipmapMode.Nearest, _ => SKMipmapMode.None });
    private void Enter(object value)
    { _token.ThrowIfCancellationRequested(); if (_stack.Count >= (long)_limits.MaximumReferenceDepth + _limits.MaximumXmlDepth + _limits.MaximumFilterPasses + 4 || !_stack.Add(value)) throw Fail("native 模型存在循环或超过引用深度。"); }
    private void Charge()
    { _token.ThrowIfCancellationRequested(); if (++_operations > _limits.MaximumPictureOperations) throw new EffectPackageException("PackageSvgBudgetExceeded", _document.Inspection.SourcePath, "实际 native 录制操作超过预算。"); }
    private EffectPackageException Fail(string message) => new("PackageSvgNativeCompilationFailed", _document.Inspection.SourcePath, message);
    private static bool Finite(SKRect rect) => float.IsFinite(rect.Left) && float.IsFinite(rect.Top) && float.IsFinite(rect.Right) && float.IsFinite(rect.Bottom);
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _settings.Srgb.Dispose(); _settings.SrgbLinear.Dispose();
    }
    private sealed class NativeScope : IDisposable
    {
        private readonly List<IDisposable> _items = [];
        internal T Own<T>(T item) where T : IDisposable { _items.Add(item); return item; }
        public void Dispose() { for (int i = _items.Count - 1; i >= 0; i--) _items[i].Dispose(); }
    }
}
