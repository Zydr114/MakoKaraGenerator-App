using MakoKaraGenerator.Spike.Rendering;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class RuntimeShaderCapabilityTests
{
    [Fact]
    public void 真实字形材质受uniform实时驱动且背景不变()
    {
        using SKBitmap first = RuntimeShaderFrameRenderer.Render(ShaderSpikeMode.Material, 0);
        using SKBitmap second = RuntimeShaderFrameRenderer.Render(ShaderSpikeMode.Material, 1_000_000);
        Assert.True(DifferentPixels(first, second) > 1000);
        Assert.Equal(first.GetPixel(0, 0), second.GetPixel(0, 0));
    }

    [Fact]
    public void 外部描边材质改变外侧而不污染不透明字形内部()
    {
        using SKBitmap original = RuntimeShaderFrameRenderer.Render(ShaderSpikeMode.Baseline);
        using SKBitmap stroke = RuntimeShaderFrameRenderer.Render(ShaderSpikeMode.StrokeMaterial);
        int inner = 0;
        for (int y = 0; y < original.Height; y++) for (int x = 0; x < original.Width; x++)
            if (original.GetPixel(x, y) == SKColors.White) { Assert.Equal(SKColors.White, stroke.GetPixel(x, y)); inner++; }
        Assert.True(inner > 1000);
        Assert.True(DifferentPixels(original, stroke) > 1000);
    }

    [Fact]
    public void 捕获整行与注音的RGB错位是实际像素而且零强度可恢复原图()
    {
        using SKBitmap original = RuntimeShaderFrameRenderer.Render(ShaderSpikeMode.Baseline);
        using SKBitmap zero = RuntimeShaderFrameRenderer.Render(ShaderSpikeMode.Glitch, glitchStrength: 0);
        using SKBitmap glitch = RuntimeShaderFrameRenderer.Render(ShaderSpikeMode.Glitch);
        // 直接字形合成与离屏 coverage 再合成各有 8bit 舍入，每通道至多 1。
        for (int y = 0; y < original.Height; y++) for (int x = 0; x < original.Width; x++)
            Assert.InRange(Distance(original.GetPixel(x, y), zero.GetPixel(x, y)), 0, 3);
        Assert.True(DifferentPixels(original, glitch) > 1000);
        Assert.True(CountDifferentInRows(original, glitch, 115, 142) > 10);
    }

    [Fact]
    public void 背景媒体与下层合成两种玻璃输入实际不同()
    {
        using SKBitmap media = RuntimeShaderFrameRenderer.Render(ShaderSpikeMode.GlassMedia);
        using SKBitmap backdrop = RuntimeShaderFrameRenderer.Render(ShaderSpikeMode.GlassBackdrop);
        Assert.True(DifferentPixels(media, backdrop) > 500);
        Assert.Equal(media.GetPixel(0, 0), backdrop.GetPixel(0, 0));
    }

    [Fact]
    public void 共同父变换和缩略图密度保留玻璃目标位置()
    {
        using SKBitmap original = RuntimeShaderFrameRenderer.Render(ShaderSpikeMode.GlassMedia);
        using SKBitmap moved = RuntimeShaderFrameRenderer.Render(ShaderSpikeMode.GlassMedia, transformGroup: true);
        using SKBitmap half = RuntimeShaderFrameRenderer.Render(ShaderSpikeMode.GlassMedia, pixelWidth: 320, pixelHeight: 180);
        Assert.True(DifferentPixels(original, moved) > 1000);
        int comparable = 0, close = 0;
        for (int y = 8; y < 172; y++) for (int x = 8; x < 312; x++)
        {
            SKColor a = original.GetPixel(x * 2, y * 2), b = half.GetPixel(x, y);
            comparable++; if (Distance(a, b) < 45) close++;
        }
        Assert.True(close > comparable * .90, $"低密度改变语义位置：{close}/{comparable}");
    }

    [Fact]
    public void 透明基础外观不泄露背景且预乘通道合法()
    {
        using SKBitmap bitmap = RuntimeShaderFrameRenderer.Render(ShaderSpikeMode.TransparentFallback);
        Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
        Assert.Contains(bitmap.Bytes.Where((_, index) => index % 4 == 3), a => a is > 0 and < 192);
        byte[] raw = bitmap.Bytes;
        for (int i = 0; i < raw.Length; i += 4)
        {
            Assert.True(raw[i] <= raw[i + 3] && raw[i + 1] <= raw[i + 3] && raw[i + 2] <= raw[i + 3]);
            if (raw[i + 3] == 0) Assert.Equal(0, raw[i] + raw[i + 1] + raw[i + 2]);
        }
    }

    [Fact]
    public void 错误shader提供原生编译诊断()
    {
        var error = Assert.Throws<InvalidOperationException>(() => RuntimeShaderFrameRenderer.Compile("half4 main(float2 p) { return NOT_DEFINED; }"));
        Assert.Contains("NOT_DEFINED", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 不依赖背景的glitch在透明模式仍可执行()
    {
        using SKBitmap original = RuntimeShaderFrameRenderer.Render(ShaderSpikeMode.Baseline, transparentBackground: true);
        using SKBitmap glitch = RuntimeShaderFrameRenderer.Render(ShaderSpikeMode.Glitch, transparentBackground: true);
        Assert.Equal(0, glitch.GetPixel(0, 0).Alpha);
        Assert.True(DifferentPixels(original, glitch) > 1000);
    }

    [Fact]
    public void 原生样本可编码PNG且保留Alpha()
    {
        string? output = Environment.GetEnvironmentVariable("MAKOKARA_SCRIPT_SPIKE_OUTPUT");
        foreach (ShaderSpikeMode mode in Enum.GetValues<ShaderSpikeMode>())
        {
            using SKBitmap bitmap = RuntimeShaderFrameRenderer.Render(mode);
            using SKImage image = SKImage.FromBitmap(bitmap);
            using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
            using SKBitmap decoded = SKBitmap.Decode(png);
            Assert.Equal(bitmap.GetPixel(0, 0).Alpha, decoded.GetPixel(0, 0).Alpha);
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                using FileStream file = File.Create(Path.Combine(output, "native-" + mode + ".png"));
                png.SaveTo(file);
            }
        }
    }

    private static int DifferentPixels(SKBitmap a, SKBitmap b, int tolerance = 0)
        => CountDifferentInRows(a, b, 0, a.Height, tolerance);
    private static int CountDifferentInRows(SKBitmap a, SKBitmap b, int top, int bottom, int tolerance = 0)
    {
        int count = 0;
        for (int y = top; y < bottom; y++) for (int x = 0; x < a.Width; x++)
            if (Distance(a.GetPixel(x, y), b.GetPixel(x, y)) > tolerance) count++;
        return count;
    }
    private static int Distance(SKColor a, SKColor b) => Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green)
        + Math.Abs(a.Blue - b.Blue) + Math.Abs(a.Alpha - b.Alpha);
}
