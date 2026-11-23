using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>P3 第 4 步：注音的装饰按注音比例缩放（D-P3-10，P3 设计 §7.2 第 2 条）。</summary>
public sealed class RubyDecorationRenderingTests
{
    private const int Width = 640, Height = 360;
    private static readonly GraphRect Canvas = new(0, 0, Width, Height);

    [Fact]
    public void 装饰缩放覆盖两态且无装饰时不复制样式()
    {
        var shadow = new ShadowSpec { DistancePx = 8, BlurRadiusPx = 6 };
        var glow = new GlowSpec { BlurRadiusPx = 10, SpreadPx = 4 };
        var style = LyricStyle.Default with
        {
            Unsung = LyricStyle.Default.Unsung with { Strokes = [StrokeSpec.Create(RgbaColor.White, 6), StrokeSpec.Create(RgbaColor.Black, 2)], Shadow = shadow },
            Sung = LyricStyle.Default.Sung with { Glow = Followable.Set<GlowSpec?>(glow) },
        };
        var scaled = style.ScaleDecorations(.5);
        Assert.Equal((3f, 1f), (scaled.Unsung.Strokes[0].WidthPx, scaled.Unsung.Strokes[1].WidthPx));
        Assert.Equal((4f, 3f), (scaled.Unsung.Shadow!.DistancePx, scaled.Unsung.Shadow.BlurRadiusPx));
        Assert.Null(scaled.Unsung.Glow);
        // 已唱显式的辉光缩放，跟随的描边与阴影仍跟随。
        Assert.Equal((5f, 2f), (scaled.Sung.Glow.Value!.BlurRadiusPx, scaled.Sung.Glow.Value.SpreadPx));
        Assert.True(scaled.Sung.Strokes.FollowsUnsung && scaled.Sung.Shadow.FollowsUnsung);
        Assert.Equal(style.UnsungFill, scaled.UnsungFill);
        Assert.Same(style, style.ScaleDecorations(1));
        Assert.Same(LyricStyle.Default, LyricStyle.Default.ScaleDecorations(.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => style.ScaleDecorations(0));
    }

    /// <summary>一行「日本」，第一个字挂注音，注音与底字之间留足间隙，便于分开两者的像素。</summary>
    private static Project Project(StrokeLayers strokes)
    {
        var tokens = new[] { LyricToken.Create("t0", "日", new(1_000_000, 1_500_000), TimingStatus.Edited),
            LyricToken.Create("t1", "本", new(1_500_000, 2_000_000), TimingStatus.Edited) };
        var line = LyricLine.Create("line", "日本", new(0, 4_000_000), TimingStatus.Edited, tokens) with
            { RubyAnnotations = [RubyAnnotation.Create("ruby", "t0", "にほん") with { GapPx = 40 }] };
        var style = LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 120 };
        style = style with { Unsung = style.Unsung with { Strokes = strokes, Fill = new() { Color = new(250, 250, 250) } } };
        return Core.Projects.Project.Create("ruby", Composition.Create(Width, Height, Core.Time.FrameRate.Fps30, outputDurationUs: 5_000_000),
            LyricsDocument.Create([line]), lyricStyle: style);
    }

    private static RenderRequest Request(Project project) => RenderRequest.At(ProjectSnapshot.FromProject(project), 500_000) with { TransparentBackground = true };

    private static byte[] Direct(SkiaRenderPipeline pipeline, Project project)
    {
        using var frame = pipeline.Render(Request(project));
        return frame.PixelMemory.ToArray();
    }

    private static byte[] Captured(SkiaRenderPipeline pipeline, Project project)
    {
        var request = Request(project);
        SceneCaptureTarget.Line target = new(new(new("line"), new(pipeline.GetSceneFrame(request.Project, request.TimeUs).Lines[0].BindingId!)));
        using var surface = pipeline.CaptureSceneTarget(request, target, new(true, true, true, false, SceneCaptureRegion.FillAndStroke, true),
            GraphInputSource.TargetColor, Canvas, GraphExecutionBudget.Default);
        return SkiaTestReadback.Pixels(surface.Image);
    }

    [Fact]
    public void 注音的描边按注音比例变细()
    {
        using var pipeline = new SkiaRenderPipeline();
        foreach (var render in new Func<SkiaRenderPipeline, Project, byte[]>[] { Direct, Captured })
        {
            var (rubyBare, baseBare) = Lefts(render(pipeline, Project(StrokeLayers.Empty)));
            var (rubyStroked, baseStroked) = Lefts(render(pipeline, Project([StrokeSpec.Create(new(20, 20, 230), 6), StrokeSpec.Create(new(230, 20, 20), 4)])));
            // 底字的描边总宽 10；注音比例 0.5，注音的描边总宽 5。
            Assert.InRange(baseBare - baseStroked, 9, 11);
            Assert.InRange(rubyBare - rubyStroked, 4, 6);
        }

        // 注音在上、底字在下，中间隔着整行透明的间隙：各自取最左的有色列。
        static (int Ruby, int Base) Lefts(byte[] pixels)
        {
            bool Inked(int y) => Enumerable.Range(0, Width).Any(x => pixels[(y * Width + x) * 4 + 3] != 0);
            int top = Enumerable.Range(0, Height).First(Inked);
            int gap = Enumerable.Range(top, Height - top).First(y => !Inked(y));
            int below = Enumerable.Range(gap, Height - gap).First(Inked);
            int Left(int from, int to) => Enumerable.Range(0, Width).First(x => Enumerable.Range(from, to - from).Any(y => pixels[(y * Width + x) * 4 + 3] != 0));
            return (Left(top, gap), Left(below, Height));
        }
    }
}
