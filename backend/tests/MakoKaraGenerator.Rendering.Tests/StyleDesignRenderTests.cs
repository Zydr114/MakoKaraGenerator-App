using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class StyleDesignRenderTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(1_500_000L)]
    public void 元素明确关闭效果后没有基线效果残留(long timeUs)
    {
        using SkiaRenderPipeline pipeline = new();
        var clean = LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 64, UnsungColor = RgbaColor.White, SungColor = RgbaColor.White };
        var decorated = clean with
        {
            Stroke = new StrokeSpec { Color = new(255, 0, 0), WidthPx = 8f },
            Shadow = new ShadowSpec { Color = new(0, 255, 0), AngleDegrees = 90d, DistancePx = 12f },
            UnsungFill = clean.UnsungFill with
            {
                Gradient = GradientSpec.Create([
                    new GradientStop(0d, new(255, 0, 0)),
                    new GradientStop(1d, new(0, 0, 255)),
                ]),
            },
            SungFill = clean.SungFill with
            {
                Gradient = GradientSpec.Create([
                    new GradientStop(0d, new(255, 0, 0)),
                    new GradientStop(1d, new(0, 0, 255)),
                ]),
            },
        };
        var project = Make(decorated);
        var disabled = StyleBatchEditor.Apply(project, StyleTargetLevel.Token, [new(0, 0)], clean, TokenStyleSupport.SupportedAtTokenLevel);
        var expected = Make(clean);
        using var actualFrame = pipeline.Render(RenderRequest.At(ProjectSnapshot.FromProject(disabled), timeUs));
        using var expectedFrame = pipeline.Render(RenderRequest.At(ProjectSnapshot.FromProject(expected), timeUs));
        // 元素按字框裁切：只比较字框内部，避开最外沿的字形悬垂。
        AssertPixelsEqual(actualFrame, expectedFrame, 180, 330);
        using var originalFrame = pipeline.Render(RenderRequest.At(ProjectSnapshot.FromProject(project), timeUs));
        Assert.True(DifferentPixels(originalFrame, actualFrame, 180, 330) > 100);
    }

    [Fact]
    public void 元素透明度零不会露出原始基线文字()
    {
        using SkiaRenderPipeline pipeline = new();
        var project = Make(LyricStyle.Default with { FontSizePx = 64, UnsungColor = RgbaColor.White, SungColor = RgbaColor.White });
        var changed = StyleBatchEditor.Apply(project, StyleTargetLevel.Token, [new(0, 0)], project.LyricStyle with { Opacity = 0 }, [LyricStyleProperty.Opacity]);
        using var frame = pipeline.Render(RenderRequest.At(ProjectSnapshot.FromProject(changed), 500_000));
        for (int y = 0; y < frame.Height; y++)
            for (int x = 180; x < 330; x++)
            {
                Assert.True(frame.TryGetPixel(x, y, out var color));
                Assert.Equal(RgbaColor.Black, color);
            }
    }

    private static void AssertPixelsEqual(RenderFrame left, RenderFrame right, int start, int end) => Assert.Equal(0, DifferentPixels(left, right, start, end));
    private static int DifferentPixels(RenderFrame left, RenderFrame right, int start, int end)
    {
        int count = 0;
        for (int y = 0; y < left.Height; y++)
            for (int x = start; x < end; x++)
            {
                Assert.True(left.TryGetPixel(x, y, out var a)); Assert.True(right.TryGetPixel(x, y, out var b));
                if (a != b) count++;
            }
        return count;
    }
    private static Project Make(LyricStyle style) => Project.Create("像素", Composition.Create(512, 200, FrameRate.Fps30),
        LyricsDocument.Create([LyricLine.Create("line", "夜空", new TimeRange(0, 2_000_000), TimingStatus.Edited,
            [LyricToken.Create("token", "夜空", new TimeRange(0, 1_000_000), TimingStatus.Edited)])]), lyricStyle: style);
}
