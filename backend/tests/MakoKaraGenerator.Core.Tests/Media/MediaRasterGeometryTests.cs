using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Media;

public sealed class MediaRasterGeometryTests
{
    [Theory]
    [InlineData(0, false, new[] { 0, 1, 2, 3, 4, 5 })]
    [InlineData(1, false, new[] { 3, 0, 4, 1, 5, 2 })]
    [InlineData(2, false, new[] { 5, 4, 3, 2, 1, 0 })]
    [InlineData(3, false, new[] { 2, 5, 1, 4, 0, 3 })]
    [InlineData(0, true, new[] { 2, 1, 0, 5, 4, 3 })]
    [InlineData(1, true, new[] { 5, 2, 4, 1, 3, 0 })]
    [InlineData(2, true, new[] { 3, 4, 5, 0, 1, 2 })]
    [InlineData(3, true, new[] { 0, 3, 1, 4, 2, 5 })]
    public void 八种正交方向对应独立手写非方像素表(int turn, bool mirror, int[] canonicalOrder)
    {
        var geometry = MediaRasterGeometry.Create(3, 2, 7, 5, (MediaQuarterTurn)turn, mirror);
        Assert.Equal(turn % 2 == 0 ? 3 : 2, geometry.CanonicalWidth);
        Assert.Equal(turn % 2 == 0 ? 2 : 3, geometry.CanonicalHeight);
        int[] actual = Enumerable.Repeat(-1, 6).ToArray();
        for (int encodedY = 0; encodedY < 2; encodedY++) for (int encodedX = 0; encodedX < 3; encodedX++)
        {
            var canonical = geometry.EncodedToCanonical(encodedX, encodedY);
            actual[canonical.Y * geometry.CanonicalWidth + canonical.X] = encodedY * 3 + encodedX;
            Assert.Equal((encodedX, encodedY), geometry.CanonicalToEncoded(canonical.X, canonical.Y));
        }
        Assert.Equal(canonicalOrder, actual);
        Assert.Throws<MediaAbException>(() => geometry.EncodedToCanonical(-1, 0));
        Assert.Throws<MediaAbException>(() => geometry.CanonicalToEncoded(geometry.CanonicalWidth, 0));
    }

    [Fact]
    public void 非方SAR只改变精确显示度量不改变像素数()
    {
        var normal = MediaRasterGeometry.Create(720, 576, 16, 15, MediaQuarterTurn.None, false);
        Assert.Equal((720, 576), (normal.CanonicalWidth, normal.CanonicalHeight));
        Assert.Equal((768L, 1), (normal.DisplayWidth.Numerator, normal.DisplayWidth.Denominator));
        Assert.Equal((576L, 1), (normal.DisplayHeight.Numerator, normal.DisplayHeight.Denominator));
        var rotated = MediaRasterGeometry.Create(720, 576, 16, 15, MediaQuarterTurn.Clockwise90, true);
        Assert.Equal((576, 720), (rotated.CanonicalWidth, rotated.CanonicalHeight));
        Assert.Equal((576L, 1), (rotated.DisplayWidth.Numerator, rotated.DisplayWidth.Denominator));
        Assert.Equal((768L, 1), (rotated.DisplayHeight.Numerator, rotated.DisplayHeight.Denominator));
        var fractional = MediaRasterGeometry.Create(2, 1, 1, 3, MediaQuarterTurn.None, false);
        Assert.Equal((2L, 3), (fractional.DisplayWidth.Numerator, fractional.DisplayWidth.Denominator));
        Assert.Equal(2d / 3, fractional.DisplayWidth.ToDouble());
        var largeRatio = MediaRasterGeometry.Create(8192, 1, int.MaxValue, int.MaxValue - 1, MediaQuarterTurn.None, false);
        Assert.True(largeRatio.DisplayWidth.Numerator > int.MaxValue);
        Assert.Equal(8192, largeRatio.CanonicalWidth);
    }

    [Theory]
    [InlineData(BackgroundFit.Contain, 0, 125, 1000, 750, 0, 0, 1, 1)]
    [InlineData(BackgroundFit.Cover, 0, 0, 1000, 1000, .125, 0, .75, 1)]
    [InlineData(BackgroundFit.Original, 116, 212, 768, 576, 0, 0, 1, 1)]
    public void 展示度量沿共享入口完成落位裁切和反向查询(BackgroundFit fit,
        double x, double y, double width, double height, double sourceX, double sourceY, double sourceWidth, double sourceHeight)
    {
        var geometry = MediaRasterGeometry.Create(720, 576, 16, 15, MediaQuarterTurn.None, false);
        var placement = MediaDisplayPlacement.Compute(geometry, 1000, 1000, fit);
        Near(x, placement.Destination.X); Near(y, placement.Destination.Y);
        Near(width, placement.Destination.Width); Near(height, placement.Destination.Height);
        Near(sourceX, placement.SourceNormalized.X); Near(sourceY, placement.SourceNormalized.Y);
        Near(sourceWidth, placement.SourceNormalized.Width); Near(sourceHeight, placement.SourceNormalized.Height);
        Assert.True(placement.TryCanvasToSourceNormalized(500, 500, out var middle));
        Near(.5, middle.X); Near(.5, middle.Y);
        var back = placement.SourceNormalizedToCanvas(middle.X, middle.Y);
        Near(500, back.X); Near(500, back.Y);
        Assert.Equal(fit == BackgroundFit.Cover, placement.TryCanvasToSourceNormalized(0, 0, out _));
        Assert.Throws<MediaAbException>(() => placement.TryCanvasToSourceNormalized(-.01, 0, out _));
        Assert.Throws<MediaAbException>(() => placement.SourceNormalizedToCanvas(double.NaN, .5));
        Assert.Throws<MediaAbException>(() => placement.SourceNormalizedToCanvas(1.0001, .5));
        var shared = BackgroundPlacement.Compute(768d, 576d, 1000, 1000, fit);
        Assert.Equal(shared.Destination.X, placement.Destination.X);
        Assert.Equal(shared.Destination.Y, placement.Destination.Y);
    }

    [Fact]
    public void 旋转显示度量与原始像素尺寸和legacy入口均保持()
    {
        var geometry = MediaRasterGeometry.Create(720, 576, 16, 15, MediaQuarterTurn.Clockwise90, false);
        var placement = MediaDisplayPlacement.Compute(geometry, 1000, 1000, BackgroundFit.Contain);
        Near(125, placement.Destination.X); Near(0, placement.Destination.Y);
        Near(750, placement.Destination.Width); Near(1000, placement.Destination.Height);
        // Legacy returns the existing binary32 rectangles; the int method body remains unchanged.
        Assert.Equal(new BackgroundRect(0, 0, 640, 360), BackgroundPlacement.Compute(640, 360, 320, 180, BackgroundFit.Contain).Source);
        Assert.Equal(new BackgroundRect(0, 0, 320, 180), BackgroundPlacement.Compute(640, 360, 320, 180, BackgroundFit.Contain).Destination);
        Assert.Equal(new BackgroundRect(140, 0, 360, 360), BackgroundPlacement.Compute(640, 360, 320, 320, BackgroundFit.Cover).Source);
        Assert.Equal(new BackgroundRect(160, 90, 320, 180), BackgroundPlacement.Compute(640, 360, 320, 180, BackgroundFit.Original).Source);
    }

    [Fact]
    public void 非法比值容量未知方向与非有限显示度量具名拒绝()
    {
        Assert.Equal(MediaAbFailure.PresentationInvalid, Assert.Throws<MediaAbException>(() => MediaRasterGeometry.Create(2, 2, 2, 2, 0, false)).Failure);
        Assert.Throws<MediaAbException>(() => MediaRasterGeometry.Create(2, 2, 0, 1, 0, false));
        Assert.Throws<MediaAbException>(() => MediaRasterGeometry.Create(2, 2, 1, -1, 0, false));
        Assert.Throws<MediaAbException>(() => MediaRasterGeometry.Create(2, 2, 1, 1, (MediaQuarterTurn)4, false));
        Assert.Equal(MediaAbFailure.CapacityExceeded, Assert.Throws<MediaAbException>(() => MediaRasterGeometry.Create(8192, 8192, 1, 1, 0, false)).Failure);
        Assert.Throws<ArgumentOutOfRangeException>(() => BackgroundPlacement.Compute(double.PositiveInfinity, 1d, 32, 32, BackgroundFit.Contain));
        Assert.Throws<ArgumentOutOfRangeException>(() => BackgroundPlacement.Compute(1d, 0d, 32, 32, BackgroundFit.Contain));
        var geometry = MediaRasterGeometry.Create(2, 2, 1, 1, 0, false);
        Assert.Throws<MediaAbException>(() => MediaDisplayPlacement.Compute(geometry, 32, 32, (BackgroundFit)99));
    }

    private static void Near(double expected, double actual) => Assert.InRange(Math.Abs(expected - actual), 0, 1e-10);
}
