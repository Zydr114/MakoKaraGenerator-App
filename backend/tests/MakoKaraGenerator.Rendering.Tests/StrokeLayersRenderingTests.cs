using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>P3 第 4 步：多重描边，阴影与辉光以整体轮廓为来源并画在描边之下，图场景的外圈描边部件（P3 设计 §6、D-P3-9）。</summary>
public sealed class StrokeLayersRenderingTests
{
    private const int Width = 640, Height = 360;
    private static readonly GraphRect Canvas = new(0, 0, Width, Height);
    private static readonly RgbaColor Red = new(230, 20, 20), Blue = new(20, 20, 230), Green = new(20, 230, 20);

    private static StrokeSpec Layer(RgbaColor color, float width) => StrokeSpec.Create(color, width);

    /// <summary>一行未开唱的「日本」；填充默认透明，只看描边。</summary>
    private static Project Project(StrokeLayers strokes, RgbaColor? fill = null, ShadowSpec? shadow = null, GlowSpec? glow = null)
    {
        var tokens = new[] { LyricToken.Create("t0", "日", new(1_000_000, 1_500_000), TimingStatus.Edited),
            LyricToken.Create("t1", "本", new(1_500_000, 2_000_000), TimingStatus.Edited) };
        var line = LyricLine.Create("line", "日本", new(0, 4_000_000), TimingStatus.Edited, tokens);
        var style = LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 120 };
        style = style with { Unsung = style.Unsung with { Strokes = strokes, Shadow = shadow, Glow = glow,
            Fill = new() { Color = fill ?? new(0, 0, 0, 0) } } };
        return Core.Projects.Project.Create("strokes", Composition.Create(Width, Height, Core.Time.FrameRate.Fps30, outputDurationUs: 5_000_000),
            LyricsDocument.Create([line]), lyricStyle: style) with { EnterAnimation = null };
    }

    private static RenderRequest Request(Project project) => RenderRequest.At(ProjectSnapshot.FromProject(project), 500_000) with { TransparentBackground = true };
    private static SceneCaptureTarget.Line Target(SkiaRenderPipeline pipeline, RenderRequest request) =>
        new(new(new("line"), new(pipeline.GetSceneFrame(request.Project, request.TimeUs).Lines[0].BindingId!)));

    private static byte[] Direct(SkiaRenderPipeline pipeline, Project project)
    {
        using var frame = pipeline.Render(Request(project));
        return frame.PixelMemory.ToArray();
    }

    private static byte[] Captured(SkiaRenderPipeline pipeline, Project project)
    {
        var request = Request(project);
        using var surface = pipeline.CaptureSceneTarget(request, Target(pipeline, request), new(true, true, true, false, SceneCaptureRegion.FillAndStroke, true),
            GraphInputSource.TargetColor, Canvas, GraphExecutionBudget.Default);
        return SkiaTestReadback.Pixels(surface.Image);
    }

    private static readonly (string Path, Func<SkiaRenderPipeline, Project, byte[]> Render)[] Paths = [("直接绘制", Direct), ("目标捕获", Captured)];

    private static bool Close(byte[] a, byte[] b, int offset, int tolerance = 2)
    {
        for (int c = 0; c < 4; c++) if (Math.Abs(a[offset + c] - b[offset + c]) > tolerance) return false;
        return true;
    }

    /// <summary>以 (x, y) 为中心 5×5 的像素全部不透明。</summary>
    private static bool Solid(byte[] pixels, int x, int y)
    {
        for (int dy = -2; dy <= 2; dy++)
        for (int dx = -2; dx <= 2; dx++)
            if (pixels[((y + dy) * Width + x + dx) * 4 + 3] != 255) return false;
        return true;
    }

    [Fact]
    public void 多重描边按累计宽度由外向内且内圈不透出外圈()
    {
        using var pipeline = new SkiaRenderPipeline();
        foreach (var (path, render) in Paths)
        {
            // 对照单层取最外层的颜色：Skia 的文字抗锯齿按画笔颜色的亮度调整覆盖率，颜色不同时边缘 alpha 也不同。
            byte[] two = render(pipeline, Project([Layer(Red, 4), Layer(Blue, 4)])), one = render(pipeline, Project([Layer(Blue, 8)]));
            // 覆盖范围与同总宽的单层相同：外层画笔取累计宽度，内圈替换外圈内侧不留缝。
            for (int i = 3; i < two.Length; i += 4) Assert.True(Math.Abs(two[i] - one[i]) <= 1, $"{path}：像素 {i / 4} alpha {two[i]}／{one[i]}");
            int red = 0, blue = 0;
            for (int i = 0; i < two.Length; i += 4)
            {
                if (two[i + 3] != 255) continue;
                if (two[i + 2] > 200 && two[i] < 40) red++; else if (two[i] > 200 && two[i + 2] < 40) blue++;
            }
            Assert.True(red > 300 && blue > 300, $"{path}：红 {red} 蓝 {blue}");

            // 半透明内圈：内圈范围里只有半透明红，不叠上外圈的蓝（普通叠加时整圈会变成不透明的紫色）。
            var translucent = render(pipeline, Project([Layer(new(230, 20, 20, 128), 4), Layer(Blue, 4)]));
            int inner = 0;
            for (int i = 0; i < translucent.Length; i += 4)
                if (translucent[i + 3] is >= 120 and <= 136 && translucent[i + 2] > 100 && translucent[i] < 30) inner++;
            Assert.True(inner > 300, $"{path}：半透明内圈像素 {inner}");
        }
    }

    [Fact]
    public void 阴影以整体轮廓为来源并画在描边之下()
    {
        var shadow = new ShadowSpec { Color = new(0, 0, 0, 255), AngleDegrees = 0, DistancePx = 12, BlurRadiusPx = .5f };
        using var pipeline = new SkiaRenderPipeline();
        foreach (var (path, render) in Paths)
        {
            byte[] plain = render(pipeline, Project([Layer(Blue, 8)], Red)), shaded = render(pipeline, Project([Layer(Blue, 8)], Red, shadow));
            int inspected = 0;
            for (int y = 2; y < Height - 2; y++)
            for (int x = 2; x < Width - 14; x++)
            {
                if (!Solid(plain, x, y)) continue;
                int at = (y * Width + x) * 4;
                // 阴影在描边与填充之下：不透明的描边、填充原样不变（旧画法里填充的阴影会压暗右侧描边）。
                Assert.True(Close(plain, shaded, at, 1), $"{path}：({x}, {y}) 被阴影改动");
                // 整体轮廓平移 12 像素处被阴影盖满（旧画法只有填充字形的投影，描边圈的投影缺失）。
                Assert.True(shaded[at + 12 * 4 + 3] >= 250, $"{path}：({x + 12}, {y}) 阴影 alpha {shaded[at + 12 * 4 + 3]}");
                inspected++;
            }
            Assert.True(inspected > 1000, $"{path}：检查了 {inspected} 个像素");
        }
    }

    [Fact]
    public void 辉光从整体轮廓外缘起算()
    {
        var glow = new GlowSpec { Color = Green, BlurRadiusPx = 4 };
        using var pipeline = new SkiaRenderPipeline();
        foreach (var (path, render) in Paths)
        {
            int bare = Left(render(pipeline, Project(StrokeLayers.Empty, Red, glow: glow)));
            int outlined = Left(render(pipeline, Project([Layer(Blue, 6), Layer(Blue, 4)], Red, glow: glow)));
            // 总宽 10 的描边把辉光整体外推约 10 像素；旧画法的辉光从字形起算，左缘与无描边时相同。
            Assert.InRange(bare - outlined, 8, 12);
        }

        static int Left(byte[] pixels) => Enumerable.Range(0, Width).First(x => Enumerable.Range(0, Height).Any(y => pixels[(y * Width + x) * 4 + 3] != 0));
    }

    [Fact]
    public void 两态轮廓不同时辉光按态分开绘制()
    {
        var style = LyricStyle.Default with { Glow = new GlowSpec { Color = Green, BlurRadiusPx = 4 } };
        style = style with { Unsung = style.Unsung with { Strokes = [Layer(Red, 4)] } };
        LyricStyle Sung(StrokeLayers strokes) => style with { Sung = style.Sung with { Strokes = Followable.Set(strokes) } };
        // 辉光只看整体轮廓：只改颜色或改各层划分而总宽、最外层拐角不变时，两态共用一次辉光。
        Assert.False(Sung([Layer(Blue, 4)]).GlowDiffersByState);
        Assert.False(Sung([Layer(Red, 1), Layer(Blue, 3)]).GlowDiffersByState);
        Assert.True(Sung([Layer(Red, 4), Layer(Blue, 2)]).GlowDiffersByState);
        Assert.True(Sung([StrokeSpec.Create(Red, 4, StrokeJoin.Miter)]).GlowDiffersByState);
        Assert.True(Sung(StrokeLayers.Empty).GlowDiffersByState);
        Assert.False((Sung([Layer(Red, 8)]) with { Glow = null }).GlowDiffersByState);
    }

    [Fact]
    public void 装饰外扩按累计描边宽度且辉光从整体轮廓起算()
    {
        // 描边 6 + 4，辉光扩张 2、σ 4（扩张 + 3σ = 14），无阴影（P3 设计 §10.6）。
        var style = LyricStyle.Default with { Unsung = LyricStyle.Default.Unsung with
            { Strokes = [Layer(Red, 6), Layer(Blue, 4)], Glow = new GlowSpec { Color = Green, BlurRadiusPx = 4, SpreadPx = 2 } } };
        // 元素框：max(min(S + 2, 64), 扩张 + 3σ + 2 + S) = 14 + 2 + 10。
        Assert.Equal(26f, SkiaSceneGeometry.DecorationExtent(style));
        // 排版留白的辉光项不加描边，已有工程行距不变：max(12, 16)。
        Assert.Equal(16f, SkiaSceneGeometry.LayoutDecorationExtent(style));
        // 网格装饰框：S + max(阴影, 辉光) = 10 + 14。
        var bounds = PreparedRenderFrame.WarpDecoratedBounds(new(0, 0, 100, 50), style);
        Assert.Equal((-24d, 124d), (bounds.Left, bounds.Right));
        // 只有描边时 S 取累计宽度。
        Assert.Equal(12f, SkiaSceneGeometry.DecorationExtent(style with { Glow = null }));
    }

    [Fact]
    public void 原样描边部件含全部各圈外圈部件只含第二层起()
    {
        using var pipeline = new SkiaRenderPipeline();
        var request = Request(Project([Layer(Red, 6), Layer(Blue, 6)], Red));
        var target = Target(pipeline, request);
        using var all = pipeline.CaptureSceneColorPart(request, target, SceneColorPart.BaseStroke, Canvas, GraphExecutionBudget.Default);
        using var outer = pipeline.CaptureSceneColorPart(request, target, SceneColorPart.BaseOuterStroke, Canvas, GraphExecutionBudget.Default);
        byte[] whole = SkiaTestReadback.Pixels(all.Surface.Image), outside = SkiaTestReadback.Pixels(outer.Surface.Image);
        byte[] rings = Direct(pipeline, Project([Layer(Red, 6), Layer(Blue, 6)]));
        byte[] innermost = Direct(pipeline, Project([Layer(Red, 6)], Red));
        int outerOnly = 0;
        for (int i = 0; i < rings.Length; i += 4)
        {
            Assert.True(Close(rings, whole, i), $"原样描边部件像素 {i / 4}");
            // 外圈部件减去最内层一圈及其内部；最内层范围之外与全部各圈相同。
            if (innermost[i + 3] == 255) Assert.True(outside[i + 3] <= 2, $"外圈部件像素 {i / 4} alpha {outside[i + 3]}");
            else if (innermost[i + 3] == 0 && rings[i + 3] == 255) { Assert.True(Close(rings, outside, i), $"外圈部件像素 {i / 4}"); outerOnly++; }
        }
        Assert.True(outerOnly > 300, $"外圈像素 {outerOnly}");

        // 单层描边时外圈部件为空。
        var single = Request(Project([Layer(Red, 6)], Red));
        using var empty = pipeline.CaptureSceneColorPart(single, Target(pipeline, single), SceneColorPart.BaseOuterStroke, Canvas, GraphExecutionBudget.Default);
        Assert.All(SkiaTestReadback.Pixels(empty.Surface.Image), value => Assert.Equal(0, value));
    }

    [Fact]
    public void 描边覆盖是全部描边圈的并集()
    {
        using var pipeline = new SkiaRenderPipeline();
        byte[] Coverage(Project project)
        {
            var request = Request(project);
            using var surface = pipeline.CaptureSceneTarget(request, Target(pipeline, request), new(false, true, false, false, SceneCaptureRegion.Stroke, IncludeGlow: false),
                GraphInputSource.StrokeCoverage, Canvas, GraphExecutionBudget.Default);
            return SkiaTestReadback.Pixels(surface.Image);
        }
        Assert.Equal(Coverage(Project([Layer(Red, 8)])), Coverage(Project([Layer(Red, 4), Layer(Blue, 4)])));
    }
}
