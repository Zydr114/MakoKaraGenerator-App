using System.Xml;
using MakoKaraGenerator.Core.Effects;
using SkiaSharp;
using global::Svg.Model;
using global::Svg.Model.Services;
using global::Svg.Skia;
using S = ShimSkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia.Effects.Svg;

internal sealed partial class SvgNativePictureCompiler
{
    internal SvgNativePictureCompiler(string safeXml, double viewportWidth, double viewportHeight,
        string sourcePath, SvgResourceBudget limits, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeXml);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(limits);
        _limits = limits;
        _token = token;
        _inlineSafeXml = safeXml;
        _inlineViewportWidth = viewportWidth;
        _inlineViewportHeight = viewportHeight;
        _inlineSourcePath = sourcePath;
        _settings = new()
        {
            AlphaType = SKAlphaType.Premul,
            ColorType = SKColorType.Bgra8888,
            Srgb = SKColorSpace.CreateSrgb(),
        };
        _convert = new(_settings);
    }

    internal (SKPicture UnitPicture, SKPath SilhouettePath, float AspectRatio) CompileInline()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _token.ThrowIfCancellationRequested();
        string safeXml = _inlineSafeXml ?? _document!.SafeXml;
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 1024,
        };
        using var text = new StringReader(safeXml);
        using var reader = XmlReader.Create(text, settings);
        var fragment = SvgService.Open(reader, false) ?? throw Fail("受控内联 SVG 未生成 SVG 文档。");
        var assets = new InlineSvgAssetLoader(SourcePath);
        float width = (float)Math.Max(1e-3, _inlineViewportWidth > 0 ? _inlineViewportWidth : _document!.Inspection.Viewport.Width);
        float height = (float)Math.Max(1e-3, _inlineViewportHeight > 0 ? _inlineViewportHeight : _document!.Inspection.Viewport.Height);
        var model = SvgSceneRuntime.CreateModel(fragment, assets, DrawAttributes.None, new(0, 0, width, height));
        if (model is null) throw Fail("已准入的内联 SVG 未生成完整模型。");

        using var source = Record(model);
        _token.ThrowIfCancellationRequested();
        using var recorder = new SKPictureRecorder();
        using var canvas = recorder.BeginRecording(SKRect.Create(0, 0, 1, 1));
        canvas.Scale(1 / width, 1 / height);
        canvas.DrawPicture(source);
        SKPicture unitPicture = recorder.EndRecording();

        SKPath? silhouette = null;
        try
        {
            var stack = new Stack<SKMatrix>();
            ExtractSilhouette(model, SKMatrix.CreateScale(1 / width, 1 / height), stack, ref silhouette);
            silhouette ??= new SKPath();
            return (unitPicture, silhouette, width / height);
        }
        catch
        {
            unitPicture.Dispose();
            silhouette?.Dispose();
            throw;
        }
    }

    private void ExtractSilhouette(S.SKPicture picture, SKMatrix initialMatrix, Stack<SKMatrix> matrixStack, ref SKPath? silhouette)
    {
        Enter(picture);
        int savedDepth = matrixStack.Count;
        SKMatrix current = initialMatrix;
        try
        {
            if (picture.Commands is null) return;
            foreach (var command in picture.Commands)
            {
                Charge();
                switch (command)
                {
                    case S.SaveCanvasCommand or S.SaveLayerCanvasCommand:
                        matrixStack.Push(current);
                        break;
                    case S.RestoreCanvasCommand:
                        if (matrixStack.Count > savedDepth)
                            current = matrixStack.Pop();
                        break;
                    case S.SetMatrixCanvasCommand transform:
                        var delta = _convert.ToSKMatrix(transform.DeltaMatrix);
                        current = SKMatrix.Concat(current, delta);
                        break;
                    case S.DrawPictureCanvasCommand { Picture: { } child }:
                        ExtractSilhouette(child, current, matrixStack, ref silhouette);
                        break;
                    case S.DrawPathCanvasCommand { Path: { } path, Paint: { } paint }:
                        AddPaintPathToSilhouette(path, paint, in current, ref silhouette);
                        break;
                    case S.DrawImageCanvasCommand:
                        throw Fail("内联 SVG 禁止位图绘制。");
                }
            }
        }
        finally
        {
            while (matrixStack.Count > savedDepth)
                matrixStack.Pop();
            _stack.Remove(picture);
        }
    }

    private void AddPaintPathToSilhouette(S.SKPath sourcePath, S.SKPaint sourcePaint, in SKMatrix current, ref SKPath? silhouette)
    {
        bool visibleColor = sourcePaint.Shader is not null || (sourcePaint.Color is { } c && c.Alpha > 0);
        if (!visibleColor) return;

        using var nativePath = Path(sourcePath);
        if (nativePath.IsEmpty) return;

        using var scope = new NativeScope();
        var skPaint = Paint(sourcePaint, scope);

        if (sourcePaint.Style is S.SKPaintStyle.Fill or S.SKPaintStyle.StrokeAndFill)
        {
            using var transformedFill = new SKPath(nativePath);
            transformedFill.Transform(current);
            UnionSilhouettePath(ref silhouette, transformedFill);
        }

        if (sourcePaint.Style is S.SKPaintStyle.Stroke or S.SKPaintStyle.StrokeAndFill && sourcePaint.StrokeWidth > 0)
        {
            using var stroked = new SKPath();
            if (skPaint.GetFillPath(nativePath, stroked) && !stroked.IsEmpty)
            {
                stroked.Transform(current);
                UnionSilhouettePath(ref silhouette, stroked);
            }
        }
    }

    private static void UnionSilhouettePath(ref SKPath? silhouette, SKPath candidate)
    {
        if (candidate.IsEmpty) return;
        if (silhouette is null)
        {
            silhouette = new SKPath(candidate);
            return;
        }
        SKPath? combined = silhouette.Op(candidate, SKPathOp.Union);
        if (combined is not null)
        {
            silhouette.Dispose();
            silhouette = combined;
        }
        else
        {
            silhouette.AddPath(candidate);
        }
    }

    private sealed class InlineSvgAssetLoader(string sourcePath) : ISvgAssetLoader, ISvgTextReferenceRenderingOptions
    {
        public bool EnableSvgFonts => false;
        public bool EnableTextReferences => false;
        public S.SKImage LoadImage(Stream stream) => throw Fault("PackageSvgImageUnsafe", "内联 SVG 禁止加载位图。");
        public List<TypefaceSpan> FindTypefaces(string? text, S.SKPaint paintPreferredTypeface) => throw Text();
        public S.SKFontMetrics GetFontMetrics(S.SKPaint paint) => throw Text();
        public float MeasureText(string? text, S.SKPaint paint, ref S.SKRect bounds) => throw Text();
        public S.SKPath? GetTextPath(string? text, S.SKPaint paint, float x, float y) => throw Text();
        private EffectPackageException Text() => Fault("PackageSvgNativeTextForbidden", "内联静态 SVG 不得请求字体或文字服务。");
        private EffectPackageException Fault(string code, string message) => new(code, sourcePath, message);
    }
}
