using MakoKaraGenerator.Core.Effects;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class PackageSvgValidatorTests
{
    [Fact]
    public void 同包图片完整解码后只输出内存data并将重复use原生驻留另计()
    {
        byte[] png = ImageBytes(SKEncodedImageFormat.Png);
        var contents = Package(Svg("<defs><image id='i' href='images/exact.bin' width='10' height='10'/></defs><use href='#i'/><use href='#i'/>"),
            images: new() { ["images/exact.bin"] = png });
        var document = Assert.Single(Compile(contents).Documents);
        Assert.Contains("href=\"data:image/png;base64,", document.SafeXml, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"images/exact.bin", document.SafeXml, StringComparison.Ordinal);
        Assert.Equal(2 * 3 * 4, document.Inspection.ValidationDecodedImageBytes);
        Assert.Equal(3 * 2 * 3 * 4, document.Inspection.EstimatedNativeDecodedImageBytes);
        Assert.Contains(document.Inspection.Dependencies, d => d.Path == "images/exact.bin" && d.Role == "image");
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png, "image/png")]
    [InlineData(SKEncodedImageFormat.Jpeg, "image/jpeg")]
    public void data图片只接受真实匹配完整解码的PNG和JPEG(SKEncodedImageFormat format, string mime)
    {
        string data = "data:" + mime + ";base64," + Convert.ToBase64String(ImageBytes(format));
        var resource = Assert.Single(Compile(Package(Svg("<image href='" + data + "' width='10' height='10'/>"))).Inspection.Resources);
        Assert.Equal(2 * 3 * 4, resource.ValidationDecodedImageBytes);
        Assert.Equal(resource.ValidationDecodedImageBytes, resource.EstimatedNativeDecodedImageBytes);
    }

    [Theory]
    [InlineData("https://invalid.example/a.png")]
    [InlineData("file:///tmp/a.png")]
    [InlineData("//invalid.example/a.png")]
    [InlineData("../a.png")]
    [InlineData("images/%2e%2e/a.png")]
    [InlineData("images\\a.png")]
    [InlineData("data:image/svg+xml;base64,PHN2Zy8+")]
    [InlineData("data:image/png;base64,Y Q==")]
    [InlineData("data:image/png;base64,!!!!")]
    [InlineData("data:image/png;base64,YQ=")]
    public void 图片URI不使用网络文件系统规范化或通用resolver(string uri)
        => Assert.Throws<EffectPackageException>(() => Compile(Package(Svg("<image href='" + uri + "' width='10' height='10'/>"))));

    [Fact]
    public void 图片真实MIME截断编码和未使用坏白名单均具名失败()
    {
        byte[] png = ImageBytes(SKEncodedImageFormat.Png);
        string wrongMime = "data:image/jpeg;base64," + Convert.ToBase64String(png);
        Assert.Equal("PackageSvgImageFormatUnsupported", Assert.Throws<EffectPackageException>(() => Compile(Package(Svg(
            "<image href='" + wrongMime + "' width='10' height='10'/>")))).Code);
        Assert.Throws<EffectPackageException>(() => Compile(Package(Svg("<image href='images/broken.bin' width='10' height='10'/>"),
            images: new() { ["images/broken.bin"] = png[..24] })));
        Assert.Equal("PackageSvgImageDecodeFailed", Assert.Throws<EffectPackageException>(() => Compile(Package(Svg(""),
            images: new() { ["images/unused.bin"] = [1, 2, 3] }))).Code);
    }

    [Theory]
    [InlineData("dimension")]
    [InlineData("pixels")]
    [InlineData("encoded")]
    [InlineData("total-encoded")]
    [InlineData("decoded")]
    [InlineData("repeated-decoded")]
    public void 图片分配前预算包括重复展开而不是只看唯一文件(string dimension)
    {
        var limits = SvgResourceBudget.Default; byte[] png = ImageBytes(SKEncodedImageFormat.Png);
        string body = "<image href='images/exact.bin' width='10' height='10'/>";
        switch (dimension)
        {
            case "dimension": limits = limits with { MaximumImageDimension = 1 }; break;
            case "pixels": limits = limits with { MaximumImagePixels = 1 }; break;
            case "encoded": limits = limits with { MaximumImageBytes = 1 }; break;
            case "total-encoded": limits = limits with { MaximumTotalImageBytes = 1 }; break;
            case "decoded": limits = limits with { MaximumDecodedImageBytes = 1 }; break;
            case "repeated-decoded":
                limits = limits with { MaximumDecodedImageBytes = 2 * 3 * 4 };
                body = "<defs><image id='i' href='images/exact.bin' width='10' height='10'/></defs><use href='#i'/>"; break;
        }
        Assert.Equal("PackageSvgBudgetExceeded", Assert.Throws<EffectPackageException>(() => Compile(Package(Svg(body),
            images: new() { ["images/exact.bin"] = png }), limits)).Code);
    }

    private static byte[] ImageBytes(SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(2, 3, SKColorType.Bgra8888, SKAlphaType.Premul)); bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(format, 100);
        return data.ToArray();
    }
}
