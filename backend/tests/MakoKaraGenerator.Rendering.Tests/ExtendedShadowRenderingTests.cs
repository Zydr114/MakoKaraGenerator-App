using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>无模糊硬边阴影、外扩张（SpreadPx）与渐变阴影（含模糊组合）渲染及投影测试。</summary>
public sealed class ExtendedShadowRenderingTests
{
    private const int Width = 640, Height = 360;
    private static readonly GraphRect Canvas = new(0, 0, Width, Height);

    private static Project BuildProject(ShadowSpec? shadow, StrokeLayers? strokes = null, RgbaColor? fill = null)
    {
        var tokens = new[]
        {
            LyricToken.Create("t0", "日", new(1_000_000, 1_500_000), TimingStatus.Edited),
            LyricToken.Create("t1", "本", new(1_500_000, 2_000_000), TimingStatus.Edited),
        };
        var line = LyricLine.Create("line", "日本", new(0, 4_000_000), TimingStatus.Edited, tokens);
        var style = LyricStyle.Default with
        {
            FontFamilyName = "Noto Sans CJK SC",
            FontSizePx = 120,
            Unsung = LyricStyle.Default.Unsung with
            {
                Fill = new() { Color = fill ?? new RgbaColor(0, 0, 0, 0) },
                Strokes = strokes ?? StrokeLayers.Empty,
                Shadow = shadow,
            },
        };
        return Project.Create("ext-shadow", Composition.Create(Width, Height, Core.Time.FrameRate.Fps30, outputDurationUs: 5_000_000),
            LyricsDocument.Create([line]), lyricStyle: style);
    }

    private static byte[] RenderDirect(SkiaRenderPipeline pipeline, Project project)
    {
        using var frame = pipeline.Render(RenderRequest.At(ProjectSnapshot.FromProject(project), 500_000) with { TransparentBackground = true });
        return frame.PixelMemory.ToArray();
    }

    private static byte[] CaptureShadowPart(SkiaRenderPipeline pipeline, Project project)
    {
        var request = RenderRequest.At(ProjectSnapshot.FromProject(project), 500_000) with { TransparentBackground = true };
        var target = new SceneCaptureTarget.Line(new(new("line"), new(pipeline.GetSceneFrame(request.Project, request.TimeUs).Lines[0].BindingId!)));
        using var part = pipeline.CaptureSceneColorPart(request, target, SceneColorPart.BaseShadow, Canvas, GraphExecutionBudget.Default);
        return SkiaTestReadback.Pixels(part.Surface.Image);
    }

    private static int CountOpaque(byte[] pixels)
    {
        int count = 0;
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] >= 250) count++;
        return count;
    }

    [Fact]
    public void 无模糊硬边阴影可绘制且外扩张增大轮廓面积()
    {
        using var pipeline = new SkiaRenderPipeline();
        ShadowSpec hard = ShadowSpec.Create(new RgbaColor(20, 40, 240, 255), opacity: 1f, angleDegrees: 90d, distancePx: 14f, blurRadiusPx: 0f, spreadPx: 0f);
        ShadowSpec spread = hard with { SpreadPx = 6f };

        byte[] hardDirect = RenderDirect(pipeline, BuildProject(hard));
        byte[] hardCapture = CaptureShadowPart(pipeline, BuildProject(hard));
        byte[] spreadDirect = RenderDirect(pipeline, BuildProject(spread));

        int hardCount = CountOpaque(hardDirect);
        int captureCount = CountOpaque(hardCapture);
        int spreadCount = CountOpaque(spreadDirect);

        Assert.True(hardCount > 2000, $"硬边阴影不透明像素过少：{hardCount}");
        Assert.True(Math.Abs(hardCount - captureCount) <= 10, $"直接绘制({hardCount})与 BaseShadow 捕获({captureCount})不一致");
        Assert.True(spreadCount > hardCount * 1.15, $"外扩张阴影({spreadCount})应明显大于无扩张({hardCount})");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(4f)]
    public void 渐变阴影在硬边与模糊模式下均呈现上下双色渐变(float blurRadiusPx)
    {
        using var pipeline = new SkiaRenderPipeline();
        GradientSpec vertical = GradientSpec.Create(
            [new GradientStop(0d, new RgbaColor(240, 20, 20)), new GradientStop(1d, new RgbaColor(20, 40, 240))],
            GradientKind.Linear,
            angleDegrees: 90d,
            coordinateSpace: GradientCoordinateSpace.Line);
        ShadowSpec shadow = ShadowSpec.Create(
            new RgbaColor(0, 0, 0, 0),
            opacity: 1f,
            angleDegrees: 45d,
            distancePx: 10f,
            blurRadiusPx: blurRadiusPx,
            spreadPx: 4f,
            gradient: vertical);

        byte[] pixels = CaptureShadowPart(pipeline, BuildProject(shadow, StrokeLayers.FromStroke(StrokeSpec.Create(RgbaColor.Black, 4f))));
        int minY = Height, maxY = -1;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            if (pixels[(y * Width + x) * 4 + 3] >= 180)
            {
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        Assert.True(maxY - minY > 40, $"渐变阴影高度过小：{minY}..{maxY}");

        int midY = (minY + maxY) / 2;
        int topRed = 0, bottomBlue = 0;
        for (int y = minY; y <= maxY; y++)
        for (int x = 0; x < Width; x++)
        {
            int idx = (y * Width + x) * 4;
            byte b = pixels[idx], g = pixels[idx + 1], r = pixels[idx + 2], a = pixels[idx + 3];
            if (a < 180) continue;
            if (y < midY && r > b + 60 && r > g + 60) topRed++;
            if (y > midY && b > r + 60 && b > g + 60) bottomBlue++;
        }
        Assert.True(topRed > 200, $"上半区红色像素过少：{topRed}");
        Assert.True(bottomBlue > 200, $"下半区蓝色像素过少：{bottomBlue}");
    }

    [Fact]
    public void 静态样式投影无损往返阴影外扩张与渐变()
    {
        GradientSpec gradient = GradientSpec.Create(
            [new GradientStop(0d, new RgbaColor(255, 0, 0)), new GradientStop(1d, new RgbaColor(0, 0, 255))],
            GradientKind.Linear,
            angleDegrees: 90d,
            coordinateSpace: GradientCoordinateSpace.Glyph);
        LyricStyle source = LyricStyle.Default with
        {
            Shadow = ShadowSpec.Create(new RgbaColor(10, 20, 30, 200), 0.75f, 120d, 8f, 0f, 3.5f, gradient),
        };

        Fx.StyleView view = StaticStyleProjection.View(source);
        Assert.NotNull(view.Unsung.Shadow);
        Assert.Equal(3.5d, view.Unsung.Shadow!.SpreadPx, 3);
        Assert.NotNull(view.Unsung.Shadow.Gradient);
        Assert.Equal(Fx.GradientCoordinateSpace.Glyph, view.Unsung.Shadow.Gradient!.CoordinateSpace);

        var updatedShadow = new Fx.ShadowValue(new Fx.ColorRgba(0, 0, 0, 1), 0.5, 30, 6, 2, 5, view.Unsung.Shadow.Gradient);
        LyricStyle applied = StaticStyleProjection.Apply(LyricStyle.Default, new Fx.StyleUpdate(new Fx.AppearanceUpdate(shadow: new Fx.ShadowUpdate(updatedShadow))));
        Assert.NotNull(applied.Unsung.Shadow);
        Assert.Equal(5f, applied.Unsung.Shadow!.SpreadPx);
        Assert.Equal(gradient, applied.Unsung.Shadow.Gradient);
    }
}
