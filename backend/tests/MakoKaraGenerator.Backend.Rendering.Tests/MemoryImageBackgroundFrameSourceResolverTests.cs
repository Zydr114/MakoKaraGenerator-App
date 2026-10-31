using System.Security.Cryptography;
using System.Buffers.Binary;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;

namespace MakoKaraGenerator.Backend.Rendering.Tests;

public sealed class MemoryImageBackgroundFrameSourceResolverTests
{
    [Fact]
    public void 构造时完整解码并按内容地址解析()
    {
        byte[] bytes = Png(); string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var resolver = new MemoryImageBackgroundFrameSourceResolver($"data/media/{hash}.media", bytes);
        var background = new BackgroundSource { Kind = BackgroundKind.Image, Path = $"data/media/{hash}.media",
            Media = new(hash, bytes.Length, "image.png"), Fit = BackgroundFit.Cover };
        using var source = resolver.Resolve(background)!;
        using var frame = source.GetFrame(0)!.Copy();
        Assert.Equal(2, frame.Width); Assert.Equal(2, frame.Height);
        var mismatch = background with { Path = "/tmp/image.png" };
        var error = Assert.Throws<ImageBackgroundDecodeException>(() => resolver.Resolve(mismatch));
        Assert.Equal("ProjectImageSourceChanged", error.Code);
    }

    [Fact]
    public void 损坏图片和预算图片在创建时稳定失败()
    {
        byte[] corrupt = [1, 2, 3];
        string corruptHash = Convert.ToHexStringLower(SHA256.HashData(corrupt));
        Assert.Equal("PackageImageDecodeFailed", Assert.Throws<ImageBackgroundDecodeException>(() =>
            new MemoryImageBackgroundFrameSourceResolver("data/media/" + corruptHash + ".media", corrupt)).Code);
        byte[] huge = new byte[2 * 1024 * 1024 + 1];
        Assert.Equal("PackageImageBudgetExceeded", Assert.Throws<ImageBackgroundDecodeException>(() =>
            new MemoryImageBackgroundFrameSourceResolver("data/media/" + new string('a', 64) + ".media", huge)).Code);
    }

    [Theory]
    [InlineData(4097, 1)]
    [InlineData(1, 4097)]
    [InlineData(4096, 4096)]
    [InlineData(2049, 2049)]
    public void 从编码头获取尺寸并在分配完整bitmap前拒绝超限(int width, int height)
    {
        byte[] bytes = Png();
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        uint crc = uint.MaxValue;
        foreach (byte value in bytes.AsSpan(12, 17))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(29, 4), ~crc);
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var error = Assert.Throws<ImageBackgroundDecodeException>(() =>
            new MemoryImageBackgroundFrameSourceResolver("data/media/" + hash + ".media", bytes));
        Assert.Equal("PackageImageBudgetExceeded", error.Code);
    }

    [Fact]
    public void 不完整像素解码明确拒绝且不返回已分配的部分图片()
    {
        byte[] bytes = Png()[..45];
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var error = Assert.Throws<ImageBackgroundDecodeException>(() =>
            new MemoryImageBackgroundFrameSourceResolver("data/media/" + hash + ".media", bytes));
        Assert.Equal("PackageImageDecodeFailed", error.Code);
    }

    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(2, 2); bitmap.Erase(new(20, 40, 60, 180));
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }
}
