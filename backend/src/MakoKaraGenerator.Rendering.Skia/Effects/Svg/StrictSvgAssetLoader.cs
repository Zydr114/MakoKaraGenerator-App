using System.Security.Cryptography;
using MakoKaraGenerator.Core.Effects;
using global::Svg.Model;
using S = ShimSkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia.Effects.Svg;

// SvgService catches image exceptions. Preserve the first failure and rethrow before publishing a picture.
internal sealed class StrictSvgAssetLoader(SvgStaticDocument document, CancellationToken token)
    : ISvgAssetLoader, ISvgTextReferenceRenderingOptions
{
    private readonly Dictionary<string, S.SKImage> _images = new(StringComparer.Ordinal);
    private EffectPackageException? _failure;
    public bool EnableSvgFonts => false;
    public bool EnableTextReferences => false;
    public S.SKImage LoadImage(Stream stream)
    {
        token.ThrowIfCancellationRequested();
        int maximum = document.ValidatedImages.Values.Select(image => image.EncodedBytes.Length).DefaultIfEmpty().Max();
        using var output = new MemoryStream(); byte[] buffer = new byte[8192];
        while (true)
        {
            token.ThrowIfCancellationRequested(); int count = stream.Read(buffer);
            if (count == 0) break;
            if (output.Length > maximum - count) throw Fault("PackageSvgNativeImageMismatch", "上游请求的图片不在已验证字节闭包中。");
            output.Write(buffer, 0, count);
        }
        byte[] bytes = output.ToArray(); string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!document.ValidatedImages.TryGetValue(hash, out var image) || !bytes.AsSpan().SequenceEqual(image.EncodedBytes.AsSpan()))
            throw Fault("PackageSvgNativeImageMismatch", "上游请求的图片不属于唯一静态编译结果。");
        if (!_images.TryGetValue(hash, out var result))
        { result = new() { Data = bytes, Width = image.Width, Height = image.Height }; _images.Add(hash, result); }
        return result;
    }
    internal void ThrowIfFaulted() { token.ThrowIfCancellationRequested(); if (_failure is not null) throw _failure; }
    public List<TypefaceSpan> FindTypefaces(string? text, S.SKPaint paintPreferredTypeface) => throw Text();
    public S.SKFontMetrics GetFontMetrics(S.SKPaint paint) => throw Text();
    public float MeasureText(string? text, S.SKPaint paint, ref S.SKRect bounds) => throw Text();
    public S.SKPath? GetTextPath(string? text, S.SKPaint paint, float x, float y) => throw Text();
    private EffectPackageException Text() => Fault("PackageSvgNativeTextForbidden", "静态 SVG native 编译不得请求字体或文字服务。");
    private EffectPackageException Fault(string code, string message)
        => _failure ??= new(code, document.Inspection.SourcePath, message);
}
