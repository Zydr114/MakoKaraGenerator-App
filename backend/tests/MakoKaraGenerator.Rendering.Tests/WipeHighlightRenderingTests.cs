using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>
/// P3 第 6 步：扫字线高亮（P3 设计 §10.3）。文字用实心方块「█」（每字 100 px 见方），未唱纯红、已唱纯蓝、高亮纯绿：
/// 不透明像素的绿色分量就是 255·h；红、蓝分量普通混合时乘 (1 − h)，滤色与相加时不变。
/// </summary>
public sealed class WipeHighlightRenderingTests
{
    private const int Width = 640, Height = 240, Row = 120;
    private const float FontSize = 100f;
    private const double WidthRatio = .4, HalfWidth = WidthRatio * FontSize / 2;
    private const float Strength = .8f;
    private static readonly GraphRect Canvas = new(0, 0, Width, Height);
    private static readonly TimeRange First = new(1_000_000, 2_000_000), Second = new(2_000_000, 3_000_000);

    private static WipeHighlightSpec Spec(WipeHighlightBlend blend = WipeHighlightBlend.Screen)
        => new() { Color = new(0, 255, 0), WidthRatio = WidthRatio, Opacity = Strength, Blend = blend };

    private static Project Project(WipeHighlightSpec? highlight, double feather = 0, RubyAnnotation? ruby = null,
        WipeSpecOverride? secondOverride = null)
    {
        LyricToken[] tokens = [LyricToken.Create("t0", "█", First, TimingStatus.Edited),
            LyricToken.Create("t1", "█", Second, TimingStatus.Edited) with { StyleOverride = secondOverride is null ? null : new() { Wipe = secondOverride } }];
        var line = LyricLine.Create("line", "██", new(0, 5_000_000), TimingStatus.Edited, tokens);
        if (ruby is not null) line = line with { RubyAnnotations = [ruby] };
        var style = LyricStyle.Default with
        {
            FontFamilyName = TestFonts.Cjk, FontSizePx = FontSize, UnsungColor = new(255, 0, 0), SungColor = new(0, 0, 255),
            Wipe = new WipeSpec { FeatherRatio = feather, Highlight = highlight },
        };
        return Core.Projects.Project.Create("highlight", Composition.Create(Width, Height, FrameRate.Fps30, outputDurationUs: 5_000_000),
            LyricsDocument.Create([line]), lyricStyle: style) with { EnterAnimation = null };
    }

    private static RenderRequest Request(Project project, long time) => RenderRequest.At(ProjectSnapshot.FromProject(project), time) with { TransparentBackground = true };

    private static byte[] Direct(SkiaRenderPipeline pipeline, Project project, long time)
    {
        using var frame = pipeline.Render(Request(project, time));
        return frame.PixelMemory.ToArray();
    }

    private static SceneCaptureTarget.Line Target(SkiaRenderPipeline pipeline, RenderRequest request)
        => new(new(new("line"), new(pipeline.GetSceneFrame(request.Project, request.TimeUs).Lines[0].BindingId!)));

    private static byte[] Captured(SkiaRenderPipeline pipeline, Project project, long time, GraphInputSource source)
    {
        var request = Request(project, time);
        using var surface = pipeline.CaptureSceneTarget(request, Target(pipeline, request), new(true, true, true, false, SceneCaptureRegion.FillAndStroke, true),
            source, Canvas, GraphExecutionBudget.Default);
        return SkiaTestReadback.Pixels(surface.Image);
    }

    private static SkiaRenderPipeline Pipeline(bool gpu) => gpu
        ? new SkiaRenderPipeline(null, null, null, null, false, BaseMaterialRuntimeTests.Device(true), false)
        : new SkiaRenderPipeline();

    private static (float Left, float Right)[] Spans(SkiaRenderPipeline pipeline, Project project)
        => [.. pipeline.GetSceneFrame(ProjectSnapshot.FromProject(project), 0).TokenGeometries
            .Select(static token => ((float)token.BaseGeometry.AdvanceBounds.Left, (float)token.BaseGeometry.AdvanceBounds.Right))];

    /// <summary>按时间外推的演唱位置（不夹值）。</summary>
    private static double Position(float left, float right, TimeRange time, long t)
        => left + (right - left) * ((double)(t - time.StartUs) / (time.EndUs - time.StartUs));

    /// <summary>升余弦峰：中心 h = 强度，半宽处为 0。</summary>
    private static double Peak(double position, double x, double halfWidth = HalfWidth)
    {
        double d = Math.Abs(x - position);
        return d >= halfWidth ? 0 : Strength * .5 * (1 + Math.Cos(Math.PI * d / halfWidth));
    }

    /// <summary>不透明像素：绿 = 255·h；红、蓝按混合方式（普通乘 1 − h，其余不变）。</summary>
    private static void AssertHighlight(byte[] pixels, int x, int y, double h, bool sung, WipeHighlightBlend blend, int tolerance, string where)
    {
        int offset = (y * Width + x) * 4;
        Assert.True(pixels[offset + 3] == 255, $"{where}：({x},{y}) 不是不透明像素。");
        double keep = blend == WipeHighlightBlend.Normal ? 1 - h : 1;
        int green = (int)Math.Round(255 * h), red = sung ? 0 : (int)Math.Round(255 * keep), blue = sung ? (int)Math.Round(255 * keep) : 0;
        Assert.True(Math.Abs(pixels[offset + 1] - green) <= tolerance && Math.Abs(pixels[offset + 2] - red) <= tolerance
            && Math.Abs(pixels[offset] - blue) <= tolerance,
            $"{where}：({x},{y}) h={h:F3} 期望 RGB({red},{green},{blue})，实际 ({pixels[offset + 2]},{pixels[offset + 1]},{pixels[offset]})。");
    }

    [Theory]
    [InlineData(WipeHighlightBlend.Normal)]
    [InlineData(WipeHighlightBlend.Screen)]
    [InlineData(WipeHighlightBlend.Add)]
    public void 硬切换时峰以演唱位置为中心且三种混合按未预乘颜色计算_CPU(WipeHighlightBlend blend)
        => VerifyHardWipeHighlightPeak(blend, gpu: false);

    [Theory]
    [Trait("Category", "RequiresVulkan")]
    [InlineData(WipeHighlightBlend.Normal)]
    [InlineData(WipeHighlightBlend.Screen)]
    [InlineData(WipeHighlightBlend.Add)]
    public void 硬切换时峰以演唱位置为中心且三种混合按未预乘颜色计算_GPU(WipeHighlightBlend blend)
        => VerifyHardWipeHighlightPeak(blend, gpu: true);

    private static void VerifyHardWipeHighlightPeak(WipeHighlightBlend blend, bool gpu)
    {
        var project = Project(Spec(blend));
        using var pipeline = Pipeline(gpu);
        var spans = Spans(pipeline, project);
        const long t = 1_500_000;
        double position = Position(spans[0].Left, spans[0].Right, First, t);
        var pixels = Direct(pipeline, project, t);
        int touched = 0;
        for (int x = (int)spans[0].Left; x < (int)spans[1].Right; x++)
        {
            double center = x + .5;
            if (Math.Abs(center - position) < 1) continue;   // 硬切换分界所在的那一列
            double h = Peak(position, center);
            if (h > .05) touched++;
            AssertHighlight(pixels, x, Row, h, center < position, blend, gpu ? 3 : 1, $"{blend} gpu={gpu}");
        }
        Assert.True(touched >= 30, "峰应覆盖约 40 列。");
    }

    [Fact]
    public void 峰外像素与不开高亮逐字节相同且不改变透明度()
    {
        // 带描边与半透明：峰外逐字节相同；峰内只改颜色，α 处处不变（透明处不会被高亮染上）。
        var on = Project(Spec());
        on = on with { LyricStyle = on.LyricStyle with { Stroke = StrokeSpec.Create(new(255, 0, 0), 6), Opacity = .5f } };
        var off = on with { LyricStyle = on.LyricStyle with { Wipe = on.LyricStyle.Wipe with { Highlight = null } } };
        using var pipeline = new SkiaRenderPipeline();
        var spans = Spans(pipeline, on);
        const long t = 1_500_000;
        double position = Position(spans[0].Left, spans[0].Right, First, t);
        byte[] lit = Direct(pipeline, on, t), plain = Direct(pipeline, off, t);
        var ink = pipeline.GetSceneFrame(ProjectSnapshot.FromProject(on), 0).TokenGeometries[0].BaseGeometry.InkBounds!.Value;
        int changed = 0, strokeChanged = 0;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int offset = (y * Width + x) * 4;
            Assert.True(lit[offset + 3] == plain[offset + 3], $"({x},{y}) α 被高亮改变：{plain[offset + 3]} → {lit[offset + 3]}。");
            bool same = lit.AsSpan(offset, 4).SequenceEqual(plain.AsSpan(offset, 4));
            if (Math.Abs(x + .5 - position) >= HalfWidth) Assert.True(same, $"峰外像素 ({x},{y}) 被改变。");
            else if (!same)
            {
                changed++;
                if (y < ink.Top - 1 || y > ink.Bottom + 1) strokeChanged++;   // 字形上下之外只有描边
            }
        }
        Assert.True(changed > 1000, "峰内应有像素被高亮。");
        Assert.True(strokeChanged > 20, "描边也应叠高亮。");
        // 半透明处按未预乘颜色混合：滤色下绿 = α·h，红、蓝不变（按预乘颜色算会把蓝压暗）。
        int interior = 0;
        for (int x = (int)Math.Ceiling(ink.Left) + 2; x < (int)ink.Right - 2; x++)
        {
            double h = Peak(position, x + .5);
            if (h < .05 || Math.Abs(x + .5 - position) < 1) continue;
            int offset = (Row * Width + x) * 4;
            Assert.True(Math.Abs(lit[offset + 1] - lit[offset + 3] * h) <= 2 && lit[offset] == plain[offset] && lit[offset + 2] == plain[offset + 2],
                $"半透明 ({x},{Row}) h={h:F3} α={lit[offset + 3]}：期望绿 {lit[offset + 3] * h:F0}、红蓝 ({plain[offset + 2]},{plain[offset]})，"
                + $"实际 RGB({lit[offset + 2]},{lit[offset + 1]},{lit[offset]})。");
            interior++;
        }
        Assert.True(interior >= 10, "半透明字形内应有被高亮的像素。");
    }

    [Fact]
    public void 峰跨到相邻元素且交接处只取正在演唱的元素()
    {
        var project = Project(Spec());
        using var pipeline = new SkiaRenderPipeline();
        var spans = Spans(pipeline, project);
        // 第一个字快唱完：峰延伸到第二个字上。
        long t = 1_950_000;
        double position = Position(spans[0].Left, spans[0].Right, First, t);
        var pixels = Direct(pipeline, project, t);
        for (int x = (int)spans[1].Left; x < (int)(position + HalfWidth); x++)
            AssertHighlight(pixels, x, Row, Peak(position, x + .5), false, WipeHighlightBlend.Screen, 1, "延伸到第二个字");
        // 交接那一刻只有第二个字的峰（第一个字外推的峰在同一位置，不能叠成两倍）。
        t = 2_000_000;
        position = Position(spans[1].Left, spans[1].Right, Second, t);
        pixels = Direct(pipeline, project, t);
        for (int x = (int)(position - HalfWidth); x < (int)(position + HalfWidth); x++)
        {
            if (Math.Abs(x + .5 - position) < 1) continue;
            AssertHighlight(pixels, x, Row, Peak(position, x + .5), x + .5 < position, WipeHighlightBlend.Screen, 1, "交接处");
        }
    }

    [Fact]
    public void 行首之前滑入行尾之后滑出离开后没有峰()
    {
        var project = Project(Spec());
        var off = Project(null);
        using var pipeline = new SkiaRenderPipeline();
        var spans = Spans(pipeline, project);
        // 速度 100 px/s，半宽 20 px：开始前 0.2 s 内峰从左边滑入，结束后 0.2 s 内从右边滑出。
        long t = 900_000;
        double position = Position(spans[0].Left, spans[0].Right, First, t);
        var pixels = Direct(pipeline, project, t);
        for (int x = (int)spans[0].Left; x < (int)(position + HalfWidth); x++)
            AssertHighlight(pixels, x, Row, Peak(position, x + .5), false, WipeHighlightBlend.Screen, 1, "滑入");
        t = 3_100_000;
        position = Position(spans[1].Left, spans[1].Right, Second, t);
        pixels = Direct(pipeline, project, t);
        for (int x = (int)(position - HalfWidth) + 1; x < (int)spans[1].Right; x++)
            AssertHighlight(pixels, x, Row, Peak(position, x + .5), true, WipeHighlightBlend.Screen, 1, "滑出");
        foreach (long far in new long[] { 750_000, 3_250_000 }) Assert.Equal(Direct(pipeline, off, far), Direct(pipeline, project, far));
    }

    [Fact]
    public void 元素覆盖关闭高亮只作用于该元素()
    {
        var project = Project(Spec(), secondOverride: new() { Highlight = OptionalValue.Set<WipeHighlightOverride?>(null) });
        var off = Project(null);
        using var pipeline = new SkiaRenderPipeline();
        Assert.Equal(Direct(pipeline, off, 2_500_000), Direct(pipeline, project, 2_500_000));
        Assert.NotEqual(Direct(pipeline, off, 1_500_000), Direct(pipeline, project, 1_500_000));
    }

    [Fact]
    public void 羽化时两态各带高亮再交叉淡化()
    {
        // 羽化 0.5（带宽 50）：峰中心处 m = 0.5，红蓝各半；滤色不改红蓝，绿 = 255·h。
        var project = Project(Spec(), feather: .5);
        using var pipeline = new SkiaRenderPipeline();
        var spans = Spans(pipeline, project);
        const long t = 1_500_000;
        double position = Position(spans[0].Left, spans[0].Right, First, t);
        var pixels = Direct(pipeline, project, t);
        int x = (int)Math.Floor(position);
        int offset = (Row * Width + x) * 4;
        Assert.InRange(pixels[offset + 1], (int)Math.Round(255 * Peak(position, x + .5)) - 1, (int)Math.Round(255 * Peak(position, x + .5)) + 1);
        Assert.InRange(pixels[offset + 2], 120, 135);
        Assert.InRange(pixels[offset], 120, 135);
    }

    [Fact]
    public void 注音按自己的进度与字号叠高亮()
    {
        // 注音字号 50：峰半宽 10 px；跟随元素进度，整段注音为一个区间。
        var ruby = RubyAnnotation.Create("ruby", "t0", "██") with { GapPx = 20 };
        var project = Project(Spec(), ruby: ruby);
        var off = Project(null, ruby: ruby);
        using var pipeline = new SkiaRenderPipeline();
        var clusters = pipeline.GetSceneFrame(ProjectSnapshot.FromProject(project), 0).TokenGeometries[0].Clusters
            .Where(static cluster => cluster.Target.RubyId is not null).Select(static cluster => cluster.Geometry).ToArray();
        Assert.Equal(2, clusters.Length);
        int row = (int)clusters[0].InkBounds!.Value.Center.Y;
        float left = (float)clusters[0].AdvanceBounds.Left, right = (float)clusters[1].AdvanceBounds.Right;
        const long t = 1_300_000;
        double position = Position(left, right, First, t);
        byte[] lit = Direct(pipeline, project, t), plain = Direct(pipeline, off, t);
        for (int x = (int)Math.Ceiling(left); x < (int)right; x++)
        {
            double center = x + .5;
            if (Math.Abs(center - position) < 1) continue;
            AssertHighlight(lit, x, row, Peak(position, center, HalfWidth / 2), center < position, WipeHighlightBlend.Screen, 1, "注音");
        }
        Assert.NotEqual(plain, lit);
    }

    [Fact]
    public void 图场景原样部件带高亮且覆盖捕获不带()
    {
        var project = Project(Spec(), ruby: RubyAnnotation.Create("ruby", "t0", "██"));
        project = project with { LyricStyle = project.LyricStyle with { Stroke = StrokeSpec.Create(new(20, 160, 20), 4) } };
        var off = project with { LyricStyle = project.LyricStyle with { Wipe = project.LyricStyle.Wipe with { Highlight = null } } };
        const long t = 1_400_000;
        var request = Request(project, t);
        using var pipeline = new SkiaRenderPipeline();
        SceneColorPart[] kinds = [SceneColorPart.BaseShadow, SceneColorPart.BaseStroke, SceneColorPart.BaseFill,
            SceneColorPart.RubyShadow, SceneColorPart.RubyStroke, SceneColorPart.RubyFill];
        var parts = kinds.Select(kind => pipeline.CaptureSceneColorPart(request, Target(pipeline, request), kind, Canvas, GraphExecutionBudget.Default)).ToArray();
        try
        {
            using var combined = pipeline.AssembleSceneColorParts(request, parts, Canvas, GraphExecutionBudget.Default);
            byte[] expected = Captured(pipeline, project, t, GraphInputSource.TargetColor), actual = SkiaTestReadback.Pixels(combined.Surface.Image);
            for (int i = 0; i < expected.Length; i++) Assert.True(Math.Abs(expected[i] - actual[i]) <= 2, $"分量 {i}：{expected[i]} / {actual[i]}");
            Assert.NotEqual(Captured(pipeline, off, t, GraphInputSource.TargetColor), expected);
        }
        finally { foreach (var part in parts) part.Dispose(); }
        // 覆盖画成白色，滤色、相加叠上去仍是白色；用普通混合才看得出覆盖是否被叠了高亮。
        var normal = project with { LyricStyle = project.LyricStyle with { Wipe = project.LyricStyle.Wipe with { Highlight = Spec(WipeHighlightBlend.Normal) } } };
        foreach (var source in new[] { GraphInputSource.GlyphCoverage, GraphInputSource.StrokeCoverage, GraphInputSource.SungCoverage })
            Assert.Equal(Captured(pipeline, off, t, source), Captured(pipeline, normal, t, source));
    }
}
