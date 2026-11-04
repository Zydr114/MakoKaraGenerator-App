using System.Security.Cryptography;
using MakoKaraGenerator.Core.Effects;
using SkiaSharp;
using S = ShimSkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia.Effects.Svg;

// All descriptors of one immutable package share this store. role:image is borrowed; data bytes are owned.
internal sealed class SvgNativeImageStore(ImageSurfaceBudgetFrame fixedBudget, ImageSurfaceBudgetSession surfaces,
    PackageGraphImages? packageImages, CancellationToken token) : IDisposable
{
    private readonly Dictionary<string, (SKImage Image, ImageSurfaceReservation Reservation, bool Owned)> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _resourceImages = new(StringComparer.Ordinal);
    private bool _disposed;
    internal long DecodedImageBytes => _images.Values.Where(value => value.Owned).Sum(value => checked((long)value.Image.Width * value.Image.Height * 4));
    internal long BorrowedImageBytes => _images.Values.Where(value => !value.Owned).Sum(value => checked((long)value.Image.Width * value.Image.Height * 4));
    internal SKImage Get(SvgStaticDocument document, S.SKImage source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested();
        EffectPackageException Fail(string message) => new("PackageSvgNativeImageMismatch", document.Inspection.SourcePath, message);
        byte[] bytes = source.Data ?? throw Fail("模型图片字节缺失。");
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!document.ValidatedImages.TryGetValue(hash, out var validated) || !bytes.AsSpan().SequenceEqual(validated.EncodedBytes.AsSpan())
            || source.Width != validated.Width || source.Height != validated.Height) throw Fail("模型图片不属于已验证内容。");
        if (_images.TryGetValue(hash, out var existing)) return Track(existing.Image);
        long size = checked((long)validated.Width * validated.Height * 4);
        var dependency = document.Inspection.Dependencies.FirstOrDefault(dependency => dependency.Role == "image" && dependency.Sha256 == hash);
        if (packageImages is not null && dependency is not null)
        {
            var borrowed = packageImages.BorrowImage(dependency.Path);
            if (borrowed.Width != validated.Width || borrowed.Height != validated.Height) throw Fail("共享 role:image 与已验证尺寸不一致。");
            var claim = surfaces.Borrow(fixedBudget, borrowed.Handle, size);
            try { Track(borrowed); _images.Add(hash, (borrowed, claim, false)); return borrowed; }
            catch { claim.Dispose(); throw; }
        }
        var reservation = fixedBudget.Reserve(1, size, "PrepareSvgEmbeddedImage"); SKImage? image = null;
        try
        {
            using var data = SKData.CreateCopy(bytes); using var codec = SKCodec.Create(data) ?? throw Fail("已验证图片无法再次完整解码。");
            using var srgb = SKColorSpace.CreateSrgb();
            using var bitmap = new SKBitmap(new SKImageInfo(validated.Width, validated.Height, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
            if (bitmap.GetPixels() == IntPtr.Zero || codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
                throw Fail("native 图片完整解码失败。");
            token.ThrowIfCancellationRequested(); bitmap.SetImmutable(); image = SKImage.FromBitmap(bitmap) ?? throw Fail("图片 native 创建失败。");
            reservation.BindImage(image.Handle); Track(image); _images.Add(hash, (image, reservation, true)); return image;
        }
        catch { image?.Dispose(); reservation.Dispose(); throw; }

        SKImage Track(SKImage image)
        {
            if (!_resourceImages.TryGetValue(document.Inspection.Id, out var dependencies))
                _resourceImages.Add(document.Inspection.Id, dependencies = new(StringComparer.Ordinal));
            dependencies.Add(hash); return image;
        }
    }
    internal IDisposable BorrowInputs(string resourceId, ImageSurfaceBudgetFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(frame.Closed, frame);
        if (!ReferenceEquals(frame.Session, surfaces))
            throw new InvalidOperationException("SVG 图片借用帧账不属于固定资源所在会话。");
        List<ImageSurfaceReservation> leases = [];
        try
        {
            if (_resourceImages.TryGetValue(resourceId, out var dependencies))
            {
                foreach (string hash in dependencies)
                {
                    var image = _images[hash].Image;
                    leases.Add(surfaces.Borrow(frame, image.Handle, checked((long)image.Width * image.Height * 4)));
                }
            }
            return new ImageSurfaceReservations(leases);
        }
        catch { foreach (var lease in leases) lease.Dispose(); throw; }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var value in _images.Values) { if (value.Owned) value.Image.Dispose(); value.Reservation.Dispose(); }
        _images.Clear(); _resourceImages.Clear();
    }
}
