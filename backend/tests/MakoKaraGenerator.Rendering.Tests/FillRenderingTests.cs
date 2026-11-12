using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>P3 第 4 步：硬边分层、按单字坐标、整行参考框改用字体度量、注音的渐变参考框（P3 设计 §5、§7.2）。</summary>
public sealed class FillRenderingTests
{
    private const int Width = 640, Height = 360;
    private static readonly RgbaColor Red = new(230, 20, 20), Blue = new(20, 20, 230);

    private static GradientSpec Halves(double angle, GradientCoordinateSpace space) =>
        GradientSpec.Create([new(0, Red), new(.5, Red), new(.5, Blue), new(1, Blue)], angleDegrees: angle, coordinateSpace: space);
    private static GradientSpec Ramp(double angle, GradientCoordinateSpace space) =>
        GradientSpec.Create([new(0, Red), new(1, Blue)], angleDegrees: angle, coordinateSpace: space);

    /// <summary>一行未开唱的文字，未唱填充用给定渐变；可在某个元素上挂注音。</summary>
    private static Project Project(string text, GradientSpec? fill, StrokeSpec? stroke = null, int? rubyToken = null)
    {
        var tokens = text.Select((ch, index) => LyricToken.Create($"t{index}", ch.ToString(),
            new(1_000_000 + index * 500_000L, 1_500_000 + index * 500_000L), TimingStatus.Edited)).ToArray();
        var line = LyricLine.Create("line", text, new(0, 4_000_000), TimingStatus.Edited, tokens);
        if (rubyToken is { } index) line = line with { RubyAnnotations = [RubyAnnotation.Create("ruby", $"t{index}", "にほん")] };
        var style = LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 120 };
        style = style with
        {
            UnsungFill = fill is null ? new() { Color = new(0, 0, 0, 0) } : new() { Color = Red, Gradient = fill },
            Stroke = stroke,
        };
        return Core.Projects.Project.Create("fill", Composition.Create(Width, Height, Core.Time.FrameRate.Fps30, outputDurationUs: 5_000_000),
            LyricsDocument.Create([line]), lyricStyle: style) with { EnterAnimation = null };
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
            GraphInputSource.TargetColor, new(0, 0, Width, Height), GraphExecutionBudget.Default);
        return SkiaTestReadback.Pixels(surface.Image);
    }

    /// <summary>不透明像素按红、蓝、混色计数（BGRA 预乘；只看 alpha 足够高的内部像素）。</summary>
    private static (int Red, int Blue, int Mixed) Colors(byte[] pixels)
    {
        int red = 0, blue = 0, mixed = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] < 250) continue;
            int b = pixels[i], r = pixels[i + 2];
            if (r > 200 && b < 40) red++;
            else if (b > 200 && r < 40) blue++;
            else if (r > 40 && b > 40) mixed++;
        }
        return (red, blue, mixed);
    }

    [Fact]
    public void 硬边分层的分界不混色()
    {
        using var pipeline = new SkiaRenderPipeline();
        var project = Project("日本", Halves(90, GradientCoordinateSpace.Line));
        foreach (var (path, pixels) in new[] { ("直接绘制", Direct(pipeline, project)), ("目标捕获", Captured(pipeline, project)) })
        {
            var counts = Colors(pixels);
            Assert.True(counts.Red > 500 && counts.Blue > 500, $"{path}：{counts}");
            // 均匀取样会把重合色标抹成渐变长度 1/64 的过渡（字号 120 时约 2 像素），分界处出现整行的混色像素。
            Assert.True(counts.Mixed == 0, $"{path}：{counts}");
        }
    }

    [Fact]
    public void 斜向与径向的硬边留约一像素过渡()
    {
        var gradient = GradientSpec.Create([new(0, Red), new(.25, Red), new(.25, Blue), new(.75, Blue), new(.75, Red), new(1, Red)]);
        var (_, positions) = SkiaRenderPipeline.HardEdgeStops(gradient, 1, .01);
        Assert.Equal(positions.Order(), positions);
        foreach (float edge in new[] { .24f, .26f, .74f, .76f }) Assert.Contains(positions, position => Math.Abs(position - edge) < 1e-5);
        Assert.Equal(0f, positions[0]); Assert.Equal(1f, positions[^1]);
        // 过渡宽度大于相邻取样间隔时向后收拢，位置仍不递减。
        var (_, wide) = SkiaRenderPipeline.HardEdgeStops(Ramp(0, GradientCoordinateSpace.Line) with
            { Stops = [new(0, Red), new(.5, Blue), new(.5, Red), new(1, Blue)] }, 1, .2);
        Assert.Equal(wide.Order(), wide);

        using var pipeline = new SkiaRenderPipeline();
        foreach (var fill in new[] { Halves(45, GradientCoordinateSpace.Line),
            GradientSpec.Create([new(0, Red), new(.5, Red), new(.5, Blue), new(1, Blue)], GradientKind.Radial, coordinateSpace: GradientCoordinateSpace.Line) })
        {
            var counts = Colors(Direct(pipeline, Project("日本", fill)));
            Assert.True(counts.Red > 500 && counts.Blue > 500, counts.ToString());
            Assert.True(counts.Mixed > 0, counts.ToString());
        }
    }

    [Fact]
    public void 径向渐变的半径按参考框比例()
    {
        // 半径 0.5 的径向分层：参考框中心附近为红、外侧为蓝。0.112.76 之前半径被取了倒数（约 1/120 像素），整片几乎都是末端色。
        using var pipeline = new SkiaRenderPipeline();
        var project = Project("日本", GradientSpec.Create([new(0, Red), new(.5, Red), new(.5, Blue), new(1, Blue)], GradientKind.Radial,
            coordinateSpace: GradientCoordinateSpace.Line));
        foreach (var (path, pixels) in new[] { ("直接绘制", Direct(pipeline, project)), ("目标捕获", Captured(pipeline, project)) })
        {
            var counts = Colors(pixels);
            Assert.True(counts.Red > 500 && counts.Blue > 500, $"{path}：{counts}");
        }
    }

    [Fact]
    public void 整行坐标的纵向参考框是字体度量()
    {
        // 单一字体时整行与按单字的纵向参考框都是字体上升部到下降部；竖向分层两者逐像素相同，注音也一样（注音用自己的字体度量）。
        using var pipeline = new SkiaRenderPipeline();
        foreach (var render in new Func<SkiaRenderPipeline, Project, byte[]>[] { Direct, Captured })
            Assert.Equal(render(pipeline, Project("日本", Halves(90, GradientCoordinateSpace.Glyph), rubyToken: 0)),
                render(pipeline, Project("日本", Halves(90, GradientCoordinateSpace.Line), rubyToken: 0)));
    }

    [Fact]
    public void 按单字坐标每个字簇各自取色()
    {
        using var pipeline = new SkiaRenderPipeline();
        // 全角字的字框宽 120 像素：两个相同的字逐像素相同（右边的字整体右移 120 像素）。
        foreach (var project in new[]
        {
            Project("日日", Ramp(0, GradientCoordinateSpace.Glyph)),
            Project("日日", null, new StrokeSpec { Material = new MaterialSpec.Gradient(Ramp(0, GradientCoordinateSpace.Glyph)), WidthPx = 4 }),
        })
        {
            foreach (var (path, pixels) in new[] { ("直接绘制", Direct(pipeline, project)), ("目标捕获", Captured(pipeline, project)) })
            {
                Assert.True(SameHalves(pixels), $"{path}：两个字不同");
                Assert.True(Graded(pixels), $"{path}：字框内没有由红到蓝");
            }
        }
        Assert.False(SameHalves(Direct(pipeline, Project("日日", Ramp(0, GradientCoordinateSpace.Line)))));

        static bool SameHalves(byte[] pixels)
        {
            int first = Enumerable.Range(0, Width).First(x => Enumerable.Range(0, Height).Any(y => pixels[(y * Width + x) * 4 + 3] != 0));
            int painted = 0;
            for (int y = 0; y < Height; y++)
            for (int x = first - 2; x < first + 118; x++)
            {
                int left = (y * Width + x) * 4, right = left + 120 * 4;
                for (int c = 0; c < 4; c++) if (Math.Abs(pixels[left + c] - pixels[right + c]) > 1) return false;
                if (pixels[left + 3] != 0) painted++;
            }
            return painted > 1000;
        }

        // 字框内由左红到右蓝：第一个字左侧偏红、右侧偏蓝（白色画笔或纯色时两侧相同）。
        static bool Graded(byte[] pixels)
        {
            int first = Enumerable.Range(0, Width).First(x => Enumerable.Range(0, Height).Any(y => pixels[(y * Width + x) * 4 + 3] != 0));
            long Lean(int from, int to)
            {
                long sum = 0;
                for (int y = 0; y < Height; y++)
                for (int x = from; x < to; x++) sum += pixels[(y * Width + x) * 4 + 2] - pixels[(y * Width + x) * 4];
                return sum;
            }
            return Lean(first, first + 30) > 0 && Lean(first + 70, first + 100) < 0;
        }
    }

    [Fact]
    public void 注音整行渐变横向取底字整行()
    {
        // 注音挂在行尾的字上：按底字整行取色时在蓝端；若按注音自身宽度取色，注音整体红蓝各半。
        using var pipeline = new SkiaRenderPipeline();
        var project = Project("日本語", Ramp(0, GradientCoordinateSpace.Line), rubyToken: 2);
        foreach (var pixels in new[] { Direct(pipeline, project), Captured(pipeline, project) })
        {
            // 注音在底字上方，两者之间隔着整行透明的间隙（注音间距 2 像素）。
            bool Inked(int y) => Enumerable.Range(0, Width).Any(x => pixels[(y * Width + x) * 4 + 3] != 0);
            int top = Enumerable.Range(0, Height).First(Inked), gap = Enumerable.Range(top, Height - top).First(y => !Inked(y));
            long red = 0, blue = 0; int count = 0;
            for (int i = top * Width * 4; i < gap * Width * 4; i += 4)
            {
                if (pixels[i + 3] < 250) continue;
                blue += pixels[i]; red += pixels[i + 2]; count++;
            }
            Assert.True(count > 200, $"注音像素 {count}");
            Assert.True(blue - red > count * 50L, $"红 {red / count} 蓝 {blue / count}");
        }
    }
}
