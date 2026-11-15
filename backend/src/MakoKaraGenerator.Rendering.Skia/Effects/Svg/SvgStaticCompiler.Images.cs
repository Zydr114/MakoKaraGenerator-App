using System.Buffers.Binary;
using System.Security.Cryptography;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia.Effects.Svg;

internal sealed partial class SvgStaticCompiler
{
    private void RewriteImages()
    {
        HashSet<string> usedPaths = new(StringComparer.Ordinal);
        // Resolve every href before the first codec: a later unsafe URI must not trigger earlier decoding.
        var images = _nodes.Where(n => n.Name == "image").Select(node => (Node: node, Image: ResolveImage(node))).ToArray();
        foreach (var entry in images)
        {
            _token.ThrowIfCancellationRequested();
            if (entry.Image.Path is { } path) usedPaths.Add(path);
            string mime = DecodeImage(entry.Image.Bytes, entry.Image.Mime, entry.Node, _imageOccurrences.GetValueOrDefault(entry.Node, 1));
            long encodedCharacters = checked(((long)entry.Image.Bytes.Length + 2) / 3 * 4);
            Check(encodedCharacters + 32 <= _limits.MaximumSafeXmlBytes, "图片 data URI 超过受控文档预算。", entry.Node, "PackageSvgBudgetExceeded");
            // Account for the generated UTF-16 base64 copy before creating it. The final XML stream
            // independently charges its actual UTF-8 bytes, without subtracting the source budget.
            _usage.Prepare(checked(encodedCharacters * 2), _sourcePath);
            entry.Node.Attributes["href"] = "data:" + mime + ";base64," + Convert.ToBase64String(entry.Image.Bytes.Span);
        }
        // Whitelisted-but-unused package resources also pass exact format and complete decoding.
        foreach (string path in _definition!.Images.Where(path => !usedPaths.Contains(path)))
        {
            _token.ThrowIfCancellationRequested();
            var file = _contents!.Files.Single(f => f.Path == path && f.Role == "image");
            _ = DecodeImage(file.Content.AsMemory(), null, null, 0);
        }
    }

    private sealed record ResolvedImage(ReadOnlyMemory<byte> Bytes, string? Mime, string? Path);
    private ResolvedImage ResolveImage(Node node)
    {
        string href = node.Get("href");
        if (href.StartsWith("data:", StringComparison.Ordinal))
        {
            string? mime = href.StartsWith("data:image/png;base64,", StringComparison.Ordinal) ? "image/png"
                : href.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal) ? "image/jpeg" : null;
            Check(mime is not null, "data 图片只允许精确 PNG/JPEG base64 声明。", node, "PackageSvgImageUnsafe");
            int comma = href.IndexOf(','); string encoded = href[(comma + 1)..];
            long maximumCharacters = checked((_limits.MaximumImageBytes + 2) / 3 * 4);
            Check(encoded.Length > 0 && encoded.Length <= maximumCharacters && encoded.Length % 4 == 0,
                "base64 图片编码超过预算或长度无效。", node, "PackageSvgBudgetExceeded");
            int padding = encoded.EndsWith("==", StringComparison.Ordinal) ? 2 : encoded.EndsWith('=') ? 1 : 0;
            Check(encoded.Take(encoded.Length - padding).All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/')
                && encoded.Skip(encoded.Length - padding).All(c => c == '='), "base64 必须完整且不含空白/非法字符。", node, "PackageSvgImageUnsafe");
            long size = checked((long)encoded.Length / 4 * 3 - padding);
            Check(size <= _limits.MaximumImageBytes, "单图片编码字节超过预算。", node, "PackageSvgBudgetExceeded");
            _usage.Prepare(size, _sourcePath);
            byte[] bytes;
            try { bytes = Convert.FromBase64String(encoded); }
            catch (FormatException error) { throw Fail("PackageSvgImageUnsafe", "base64 图片无效。", node, error); }
            Check(Convert.ToBase64String(bytes) == encoded, "base64 图片必须使用规范编码。", node, "PackageSvgImageUnsafe");
            return new(bytes, mime, null);
        }
        // No URI normalizer, percent decoding, SVG-relative path, cwd or OS path fallback exists.
        Check(!href.Any(c => c is '%' or '\\' or '?' or '#' or ':') && _definition!.Images.Contains(href, StringComparer.Ordinal),
            "图片 href 必须精确匹配 descriptor 的同包 role:image 白名单且不含 URI 编码/片段/scheme。", node, "PackageSvgImageUnsafe");
        var image = _contents!.Files.SingleOrDefault(f => f.Path == href && f.Role == "image");
        Check(image is not null, "图片缺失或 role 不匹配。", node, "PackageSvgImageUnsafe");
        Check(image!.Content.Length <= _limits.MaximumImageBytes, "单图片编码字节超过预算。", node, "PackageSvgBudgetExceeded");
        return new(image.Content.AsMemory(), null, image.Path);
    }

    private string DecodeImage(ReadOnlyMemory<byte> bytes, string? declaredMime, Node? node, int occurrences)
    {
        _token.ThrowIfCancellationRequested();
        Check(bytes.Length > 0 && bytes.Length <= _limits.MaximumImageBytes, "单图片编码字节超过预算。", node, "PackageSvgBudgetExceeded");
        _usage.EncodedImage(_sourcePath, checked((long)bytes.Length * Math.Max(1, occurrences)), occurrences);
        ValidatePngStructure(bytes.Span, node);
        _usage.Prepare(bytes.Length, _sourcePath); // SKData owns an independent encoded copy.
        using var data = SKData.CreateCopy(bytes.Span);
        using var codec = SKCodec.Create(data);
        Check(codec is not null, "图片编码无法读取。", node, "PackageSvgImageDecodeFailed");
        string mime = codec!.EncodedFormat switch
        { SKEncodedImageFormat.Png => "image/png", SKEncodedImageFormat.Jpeg => "image/jpeg", _ => "" };
        Check(mime.Length > 0 && (declaredMime is null || declaredMime == mime) && codec.FrameCount is 0 or 1,
            "仅支持非动画 PNG/JPEG，且 MIME 必须匹配真实编码。", node, "PackageSvgImageFormatUnsupported");
        int width = codec.Info.Width, height = codec.Info.Height;
        long pixels = checked((long)width * height), decoded = checked(pixels * 4);
        Check(width > 0 && height > 0 && width <= _limits.MaximumImageDimension && height <= _limits.MaximumImageDimension
            && pixels <= _limits.MaximumImagePixels, "图片尺寸/像素超过明确预算。", node, "PackageSvgBudgetExceeded");
        // All aggregate decode/native-residency reservations happen before allocating a bitmap.
        long nativeDecoded = checked(decoded * occurrences);
        _usage.DecodedImage(_sourcePath, decoded, nativeDecoded);
        _decodedBytes = checked(_decodedBytes + decoded); _nativeDecodedBytes = checked(_nativeDecodedBytes + nativeDecoded);
        _token.ThrowIfCancellationRequested();
        using var srgb = SKColorSpace.CreateSrgb();
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
        Check(bitmap.GetPixels() != IntPtr.Zero && codec.GetPixels(bitmap.Info, bitmap.GetPixels()) == SKCodecResult.Success,
            "图片像素解码不完整或失败。", node, "PackageSvgImageDecodeFailed");
        _token.ThrowIfCancellationRequested();
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
        if (!_validatedImages.ContainsKey(hash))
        {
            _usage.Prepare(bytes.Length, _sourcePath);
            _validatedImages.Add(hash, new(hash, width, height, mime, [.. bytes.Span]));
        }
        return mime;
    }

    private void ValidatePngStructure(ReadOnlySpan<byte> bytes, Node? node)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (!bytes.StartsWith(signature)) return; // The real codec still decides PNG versus JPEG.
        int position = 8, chunks = 0; bool header = false, end = false;
        while (position < bytes.Length)
        {
            _token.ThrowIfCancellationRequested();
            Check(bytes.Length - position >= 12 && ++chunks <= 4096, "PNG chunk 闭包不完整或超过结构预算。", node, "PackageSvgImageDecodeFailed");
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(position, 4));
            Check(length <= bytes.Length - position - 12, "PNG chunk 被截断。", node, "PackageSvgImageDecodeFailed");
            ReadOnlySpan<byte> kind = bytes.Slice(position + 4, 4);
            Check(!kind.SequenceEqual("acTL"u8) && !kind.SequenceEqual("fcTL"u8) && !kind.SequenceEqual("fdAT"u8),
                "拒绝动画 PNG。", node, "PackageSvgImageFormatUnsupported");
            if (!header)
            {
                Check(kind.SequenceEqual("IHDR"u8) && length == 13, "PNG 必须从唯一 IHDR 开始。", node, "PackageSvgImageDecodeFailed");
                uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(position + 8, 4));
                uint height = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(position + 12, 4));
                Check(width > 0 && height > 0 && width <= _limits.MaximumImageDimension && height <= _limits.MaximumImageDimension
                    && (ulong)width * height <= (ulong)_limits.MaximumImagePixels, "PNG 头尺寸超过解码前预算。", node, "PackageSvgBudgetExceeded");
                header = true;
            }
            else Check(!kind.SequenceEqual("IHDR"u8), "PNG 不得有多个 IHDR。", node, "PackageSvgImageDecodeFailed");
            position = checked(position + (int)length + 12);
            if (kind.SequenceEqual("IEND"u8))
            { Check(length == 0 && position == bytes.Length, "PNG IEND 后不得有额外内容。", node, "PackageSvgImageDecodeFailed"); end = true; break; }
        }
        Check(header && end, "PNG 缺少完整结束 chunk。", node, "PackageSvgImageDecodeFailed");
    }
}
