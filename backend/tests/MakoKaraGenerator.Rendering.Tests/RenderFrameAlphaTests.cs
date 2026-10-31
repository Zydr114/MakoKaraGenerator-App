using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class RenderFrameAlphaTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(192)]
    [InlineData(255)]
    public void 半透明饱和通道读成完整直乘颜色(byte alpha)
    {
        using RenderFrame frame = new([alpha, 0, alpha, alpha],
            new RenderTarget { PixelWidth = 1, PixelHeight = 1 }, PixelFormat.Bgra8888Premultiplied, 0);
        Assert.True(frame.TryGetPixel(0, 0, out RgbaColor actual));
        Assert.Equal(new RgbaColor(255, 0, 255, alpha), actual);
        Assert.Equal(alpha, frame.Pixels[0]); // 读取只换算返回值，不修改源帧。
    }
}
