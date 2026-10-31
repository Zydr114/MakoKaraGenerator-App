using MakoKaraGenerator.Core.Effects;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>使用正式 CPU 后端完整解码包图片；仅验证，不把 native 生命周期交给 SDK。</summary>
public sealed class PackageImageValidator : IPackageImageValidator
{
    private readonly GraphExecutionBudget _budget;
    /// <summary>固定后端图片容量，默认使用已接受的图预算。</summary>
    public PackageImageValidator(GraphExecutionBudget? budget = null)
    {
        _budget = budget ?? GraphExecutionBudget.Default;
        if (_budget.MaximumPixelDimension <= 0 || _budget.MaximumBytes <= 0) throw new ArgumentException("图片预算必须为正。", nameof(budget));
    }
    /// <inheritdoc/>
    public void Validate(EffectPackageFile file, CancellationToken cancellationToken = default)
    { using var image = Decode(file, cancellationToken); }

    /// <summary>完整解码并返回图片尺寸、编码格式和固定解码预算结果。</summary>
    public PackageDecodedImageInfo Inspect(ReadOnlyMemory<byte> content, string logicalPath = "package-image",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var data = SKData.CreateCopy(content.Span);
        using var codec = SKCodec.Create(data);
        if (codec is null) throw new EffectPackageException("PackageImageDecodeFailed", logicalPath, "图片编码无法读取。");
        int width = codec.Info.Width, height = codec.Info.Height;
        if (width <= 0 || height <= 0 || width > _budget.MaximumPixelDimension || height > _budget.MaximumPixelDimension
            || (long)width * height > _budget.MaximumBytes / 4)
            throw new EffectPackageException("PackageImageBudgetExceeded", logicalPath, "图片尺寸或完整解码字节超过明确预算。");
        cancellationToken.ThrowIfCancellationRequested();
        using var srgb = SKColorSpace.CreateSrgb();
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
        if (bitmap.GetPixels() == IntPtr.Zero || codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
            throw new EffectPackageException("PackageImageDecodeFailed", logicalPath, "图片像素解码不完整或失败。");
        cancellationToken.ThrowIfCancellationRequested();
        return new(width, height, codec.EncodedFormat.ToString(), checked(width * height * 4), codec.Info.AlphaType != SKAlphaType.Opaque);
    }

    internal SKImage Decode(EffectPackageFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file); cancellationToken.ThrowIfCancellationRequested();
        if (file.Role != "image") throw new EffectPackageException("PackageImageRoleMismatch", file.Path, "只有明确的图片用途可进入 SDK 图片目录。");
        using var data = SKData.CreateCopy(file.Content.AsSpan());
        using var codec = SKCodec.Create(data);
        if (codec is null) throw new EffectPackageException("PackageImageDecodeFailed", file.Path, "图片编码无法读取。");
        int width = codec.Info.Width, height = codec.Info.Height;
        if (width <= 0 || height <= 0 || width > _budget.MaximumPixelDimension || height > _budget.MaximumPixelDimension
            || (long)width * height > _budget.MaximumBytes / 4)
            throw new EffectPackageException("PackageImageBudgetExceeded", file.Path, "图片尺寸或完整解码字节超过明确预算。");
        cancellationToken.ThrowIfCancellationRequested();
        using var srgb = SKColorSpace.CreateSrgb();
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
        if (bitmap.GetPixels() == IntPtr.Zero || codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
            throw new EffectPackageException("PackageImageDecodeFailed", file.Path, "图片像素解码不完整或失败。");
        cancellationToken.ThrowIfCancellationRequested();
        return SKImage.FromBitmap(bitmap) ?? throw new EffectPackageException("PackageImageDecodeFailed", file.Path, "完整像素无法建立固定图像。");
    }
}

/// <summary>固定图片解码的有界结果；像素本身不会越过资源检查合同。</summary>
public sealed record PackageDecodedImageInfo(int Width, int Height, string EncodedFormat, int DecodedBytes, bool HasAlpha);
