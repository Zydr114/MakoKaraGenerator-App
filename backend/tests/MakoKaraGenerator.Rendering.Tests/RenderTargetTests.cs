using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>
/// 渲染目标的尺寸与缩放规则测试。
/// </summary>
/// <remarks>
/// 这里固化"预览只是同一画面的缩略图"这一前提：
/// 管线按目标尺寸与画布尺寸的比值整体缩放，因此草稿预览不能改变布局语义。
/// </remarks>
public sealed class RenderTargetTests
{
    [Fact]
    public void 按合成设置得到整画布尺寸()
    {
        RenderTarget target = RenderTarget.FromComposition(Composition.Create(1920, 1080, Core.Time.FrameRate.Fps30));

        Assert.Equal(1920, target.PixelWidth);
        Assert.Equal(1080, target.PixelHeight);
    }

    [Fact]
    public void 等比缩小到给定范围内()
    {
        RenderTarget canvas = RenderTarget.FromComposition(Composition.Create(1920, 1080, Core.Time.FrameRate.Fps30));

        RenderTarget fitted = canvas.FitWithin(960, 960);

        // 以较紧的一边为准：960/1920 = 0.5，高度随之变成 540。
        Assert.Equal(960, fitted.PixelWidth);
        Assert.Equal(540, fitted.PixelHeight);
        Assert.Equal(0.5d, fitted.ScaleXOf(Composition.Create(1920, 1080, Core.Time.FrameRate.Fps30)), 1e-9);
        Assert.Equal(0.5d, fitted.ScaleYOf(Composition.Create(1920, 1080, Core.Time.FrameRate.Fps30)), 1e-9);
    }

    [Fact]
    public void 本来就没超过时不做放大()
    {
        RenderTarget canvas = RenderTarget.FromComposition(Composition.Create(1280, 720, Core.Time.FrameRate.Fps30));

        Assert.Same(canvas, canvas.FitWithin(1920, 1080));
    }

    [Fact]
    public void 极小尺寸也会保留至少一个像素()
    {
        RenderTarget canvas = RenderTarget.FromComposition(Composition.Create(1920, 1080, Core.Time.FrameRate.Fps30));

        RenderTarget fitted = canvas.FitWithin(1, 1000);

        Assert.Equal(1, fitted.PixelWidth);
        Assert.Equal(1, fitted.PixelHeight);
    }

    [Fact]
    public void 非法画布尺寸被拒绝()
    {
        Assert.Throws<ArgumentNullException>(() => RenderTarget.FromComposition(null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RenderTarget.FromComposition(new Composition { CanvasWidth = 0, CanvasHeight = 1080 }));
    }

    [Fact]
    public void 非法缩放范围被拒绝()
    {
        RenderTarget canvas = RenderTarget.FromComposition(Composition.Create(1920, 1080, Core.Time.FrameRate.Fps30));

        Assert.Throws<ArgumentOutOfRangeException>(() => canvas.FitWithin(0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => canvas.FitWithin(100, 0));
    }
}
