using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>
/// P3 第 5 步：扫字羽化的直绘（P3 设计 §8.1、§10.1）。
/// 文字用实心方块「█」（每字恰好 100 px 见方），未唱纯红、已唱纯蓝：带内像素的蓝色分量就是 255·m，红色是 255·(1 − m)。
/// </summary>
public sealed class WipeFeatherRenderingTests
{
    private const int Width = 640, Height = 240, Row = 120;
    private const float FontSize = 100f;
    private static readonly GraphRect Canvas = new(0, 0, Width, Height);

    private static Project Project(double feather, (string Text, TimeRange? Time)[] tokens, RgbaColor? unsung = null, RgbaColor? sung = null,
        RgbaColor? background = null, RubyAnnotation? ruby = null, double?[]? elementFeather = null)
    {
        var items = tokens.Select((token, index) => LyricToken.Create("t" + index, token.Text, token.Time,
            token.Time is null ? TimingStatus.Missing : TimingStatus.Edited) with
            { StyleOverride = elementFeather?[index] is { } ratio ? new() { Wipe = new() { FeatherRatio = ratio } } : null }).ToArray();
        var line = LyricLine.Create("line", string.Concat(tokens.Select(static token => token.Text)), new(0, 5_000_000), TimingStatus.Edited, items);
        if (ruby is not null) line = line with { RubyAnnotations = [ruby] };
        var style = LyricStyle.Default with
        {
            FontFamilyName = "Noto Sans CJK SC", FontSizePx = FontSize, UnsungColor = unsung ?? new(255, 0, 0), SungColor = sung ?? new(0, 0, 255),
            Wipe = new WipeSpec { FeatherRatio = feather },
        };
        return Core.Projects.Project.Create("wipe", Composition.Create(Width, Height, FrameRate.Fps30, outputDurationUs: 5_000_000,
            backgroundColor: background), LyricsDocument.Create([line]), lyricStyle: style) with { EnterAnimation = null };
    }

    private static RenderRequest Request(Project project, long time, bool transparent = true)
        => RenderRequest.At(ProjectSnapshot.FromProject(project), time) with { TransparentBackground = transparent };

    private static byte[] Direct(SkiaRenderPipeline pipeline, Project project, long time, bool transparent = true)
    {
        using var frame = pipeline.Render(Request(project, time, transparent));
        return frame.PixelMemory.ToArray();
    }

    private static byte[] Captured(SkiaRenderPipeline pipeline, Project project, long time, GraphInputSource source = GraphInputSource.TargetColor)
    {
        var request = Request(project, time);
        SceneCaptureTarget.Line target = new(new(new("line"), new(pipeline.GetSceneFrame(request.Project, request.TimeUs).Lines[0].BindingId!)));
        using var surface = pipeline.CaptureSceneTarget(request, target, new(true, true, true, false, SceneCaptureRegion.FillAndStroke, true),
            source, Canvas, GraphExecutionBudget.Default);
        return SkiaTestReadback.Pixels(surface.Image);
    }

    private static SkiaRenderPipeline Pipeline(bool gpu) => gpu
        ? new SkiaRenderPipeline(null, null, null, null, false, BaseMaterialRuntimeTests.Device(true), false)
        : new SkiaRenderPipeline();

    /// <summary>各元素的横向起止（设备像素，世界矩阵为单位阵）。</summary>
    private static (float Left, float Right)[] Spans(SkiaRenderPipeline pipeline, Project project)
        => [.. pipeline.GetSceneFrame(ProjectSnapshot.FromProject(project), 0).TokenGeometries
            .Select(static token => ((float)token.BaseGeometry.AdvanceBounds.Left, (float)token.BaseGeometry.AdvanceBounds.Right))];

    /// <summary>按 P3 设计 §10.1 第 1、2 条在像素中心求 m；从右向左时 left 大于 right。</summary>
    private static double Mask(float left, float right, TimeRange time, long t, double band, double x)
    {
        double position = left + (right - left) * ((double)(t - time.StartUs) / (time.EndUs - time.StartUs));
        return Math.Clamp(.5 + Math.Sign(right - left) * (position - x) / band, 0, 1);
    }

    /// <summary>不透明像素的蓝色分量等于 255·m，红色分量与之互补（误差 1）。</summary>
    private static void AssertMask(byte[] pixels, int x, int y, double mask, string where)
    {
        int offset = (y * Width + x) * 4;
        Assert.True(pixels[offset + 3] == 255, $"{where}：({x},{y}) 不是不透明像素。");
        int expected = (int)Math.Round(255 * mask);
        Assert.True(Math.Abs(pixels[offset] - expected) <= 1 && Math.Abs(pixels[offset + 2] - (255 - expected)) <= 1,
            $"{where}：({x},{y}) 期望 m={mask:F3}（蓝 {expected}），实际 蓝 {pixels[offset]} 红 {pixels[offset + 2]}。");
    }

    [Fact]
    public void 单元素过渡带以演唱位置为中心线性变化()
    {
        TimeRange time = new(1_000_000, 2_000_000);
        var project = Project(.5, [("█", time)]);
        using var pipeline = new SkiaRenderPipeline();
        var (left, right) = Assert.Single(Spans(pipeline, project));
        Assert.Equal(100f, right - left);
        foreach (var (name, render) in new (string, Func<long, byte[]>)[] { ("直绘", t => Direct(pipeline, project, t)), ("捕获", t => Captured(pipeline, project, t)) })
        foreach (long t in new long[] { 1_000_000, 1_250_000, 1_900_000 })
        {
            var pixels = render(t);
            for (int x = (int)left; x < (int)right; x++) AssertMask(pixels, x, Row, Mask(left, right, time, t, 50, x + .5), $"{name} t={t}");
        }
        // t = s 时 x = L 处 m = 0.5（D-P3-5）。
        Assert.InRange(Direct(pipeline, project, 1_000_000)[(Row * Width + (int)left) * 4], 120, 134);
    }

    [Fact]
    public void 过渡带提前半个带宽进入并在结束后半个带宽退出()
    {
        TimeRange time = new(1_000_000, 2_000_000);
        var feathered = Project(.5, [("█", time)]);
        var hard = Project(0, [("█", time)]);
        using var pipeline = new SkiaRenderPipeline();
        // 带宽 50 px、速度 100 px/s：开始前 0.25 s 开始变色，结束后 0.25 s 完全变成已唱；这两刻之外与硬切换逐字节相同。
        foreach (long t in new long[] { 500_000, 750_000, 2_250_000, 3_000_000 })
            Assert.Equal(Direct(pipeline, hard, t), Direct(pipeline, feathered, t));
        foreach (long t in new long[] { 760_000, 2_240_000 })
            Assert.NotEqual(Direct(pipeline, hard, t), Direct(pipeline, feathered, t));
    }

    [Fact]
    public void 两态装饰不同时行首行尾字框外取端点处的m()
    {
        TimeRange time = new(1_000_000, 2_000_000);
        // 两态描边颜色不同：行首字框左侧的描边取元素起点处的 m（t = s 时为 0.5），行尾字框右侧取终点处的 m（t = e 时为 0.5）。
        var project = Project(.5, [("█", time)]);
        project = project with { LyricStyle = project.LyricStyle with
        {
            Stroke = StrokeSpec.Create(new(255, 0, 0), 6),
            Sung = project.LyricStyle.Sung with { Strokes = Followable.Set(StrokeLayers.FromStroke(StrokeSpec.Create(new(0, 0, 255), 6))) },
        } };
        Assert.True(project.LyricStyle.DecorationsDifferByState);
        using var pipeline = new SkiaRenderPipeline();
        var (left, right) = Assert.Single(Spans(pipeline, project));
        AssertMask(Direct(pipeline, project, 1_000_000), (int)left - 3, Row, .5, "行首字框外");
        AssertMask(Direct(pipeline, project, 2_000_000), (int)right + 2, Row, .5, "行尾字框外");
        // 过渡带完全离开后，延伸部分与硬切换相同。
        AssertMask(Direct(pipeline, project, 2_300_000), (int)right + 2, Row, 1, "唱完后行尾字框外");
    }

    [Fact]
    public void 相邻元素各按自己的速度且交界那一刻两侧相等()
    {
        TimeRange first = new(1_000_000, 1_500_000), second = new(1_500_000, 2_500_000);
        var project = Project(.5, [("█", first), ("█", second)]);
        using var pipeline = new SkiaRenderPipeline();
        var spans = Spans(pipeline, project);
        foreach (long t in new long[] { 1_500_000, 1_600_000 })
        {
            var pixels = Direct(pipeline, project, t);
            for (int index = 0; index < 2; index++)
            {
                var (left, right) = spans[index];
                for (int x = (int)left; x < (int)right; x++)
                    AssertMask(pixels, x, Row, Mask(left, right, index == 0 ? first : second, t, 50, x + .5), $"元素 {index} t={t}");
            }
            // 交界两侧的像素：交界那一刻差值只来自半个像素的斜率；之后左快右慢，台阶约为 (v0 − v1)(t − e)/W = 0.2。
            int junction = (int)spans[1].Left;
            int step = Direct(pipeline, project, t)[(Row * Width + junction - 1) * 4] - pixels[(Row * Width + junction) * 4];
            if (t == 1_500_000) Assert.InRange(step, 2, 9);
            else Assert.InRange(step, 55, 64);
        }
    }

    [Fact]
    public void 准备阶段不提前进入过渡带()
    {
        // 前奏准备阶段整行按未唱显示；元素时间清空，过渡带不会在首个元素开唱前 W/(2v) 提前出现。
        var project = LeadInFixture.Create();
        Project Feather(double ratio) => project with { LyricStyle = project.LyricStyle with { Wipe = new WipeSpec { FeatherRatio = ratio } } };
        const long t = 11_900_000;
        var line = LyricSceneSampler.SampleFrame(ProjectSnapshot.FromProject(Feather(.5)), t).Lines.Single(static line => line.Branch.Id == "primary");
        Assert.Equal(SceneDisplayPhase.Preparation, line.DisplayPhase);
        Assert.All(line.Frame.Tokens, static token => Assert.Null(token.Time));
        Assert.All(line.Frame.RubySegments, static segment => Assert.Null(segment.Time));
        using var pipeline = new SkiaRenderPipeline();
        Assert.Equal(Direct(pipeline, Feather(0), t), Direct(pipeline, Feather(.5), t));
        // 开唱后 0.1 s 羽化确实生效。
        Assert.NotEqual(Direct(pipeline, Feather(0), 12_100_000), Direct(pipeline, Feather(.5), 12_100_000));
    }

    [Fact]
    public void 元素覆盖的羽化比例只作用于该元素()
    {
        TimeRange first = new(1_000_000, 2_000_000), second = new(2_000_000, 3_000_000);
        // 行样式不羽化，第二个元素覆盖为 0.5：第一个元素仍是硬切换，第二个元素提前 0.25 s 进入过渡带。
        var project = Project(0, [("█", first), ("█", second)], elementFeather: [null, .5]);
        using var pipeline = new SkiaRenderPipeline();
        var spans = Spans(pipeline, project);
        const long t = 1_950_000;
        var pixels = Direct(pipeline, project, t);
        float reached = spans[0].Left + 95;
        for (int x = (int)spans[0].Left; x < (int)spans[0].Right; x++) AssertMask(pixels, x, Row, x + .5 < reached ? 1 : 0, "硬切换的元素");
        for (int x = (int)spans[1].Left; x < (int)spans[1].Right; x++)
            AssertMask(pixels, x, Row, Mask(spans[1].Left, spans[1].Right, second, t, 50, x + .5), "覆盖羽化的元素");
    }

    [Fact]
    public void 共享字簇内的元素覆盖随整行绘制按元素比例羽化()
    {
        // DejaVu Sans 的 office 中 ffi 连写成一个字形，元素不能单独绘制，覆盖随整行两遍绘制；羽化比例按元素解析。
        var tokens = "office".Select((c, i) => (c.ToString(), (TimeRange?)new TimeRange(i * 500_000L, (i + 1) * 500_000L))).ToArray();
        Project Dejavu(Project p) => p with { LyricStyle = p.LyricStyle with { FontFamilyName = "DejaVu Sans" } };
        var feathered = Dejavu(Project(0, tokens, elementFeather: [null, null, null, .5, null, null]));
        using var pipeline = new SkiaRenderPipeline();
        Assert.False(pipeline.GetSceneFrame(ProjectSnapshot.FromProject(feathered), 0).TokenGeometries[3].BaseGeometry.CanTransformIndependently);
        // 硬切换时不透明像素非红即蓝；按元素比例羽化后连写字形里出现两态混合的不透明像素。
        Assert.Equal(0, Mixed(Dejavu(Project(0, tokens))));
        Assert.True(Mixed(feathered) > 50, $"混合像素 {Mixed(feathered)}");

        int Mixed(Project project)
        {
            var pixels = Direct(pipeline, project, 1_750_000);
            int count = 0;
            for (int i = 0; i < pixels.Length; i += 4) if (pixels[i + 3] == 255 && pixels[i] is > 40 and < 215) count++;
            return count;
        }
    }

    [Fact]
    public void 夹在中间的未定时元素不染色()
    {
        TimeRange first = new(1_000_000, 1_500_000), third = new(2_000_000, 3_000_000);
        var project = Project(.5, [("█", first), ("█", null), ("█", third)]);
        using var pipeline = new SkiaRenderPipeline();
        var spans = Spans(pipeline, project);
        var pixels = Direct(pipeline, project, 2_200_000);
        for (int x = (int)spans[0].Left; x < (int)spans[0].Right; x++) AssertMask(pixels, x, Row, 1, "已唱完的元素");
        for (int x = (int)spans[1].Left; x < (int)spans[1].Right; x++) AssertMask(pixels, x, Row, 0, "未定时元素");
        for (int x = (int)spans[2].Left; x < (int)spans[2].Right; x++)
            AssertMask(pixels, x, Row, Mask(spans[2].Left, spans[2].Right, third, 2_200_000, 50, x + .5), "正在唱的元素");
    }

    [Fact]
    public void 从右向左排版时方向反转()
    {
        TimeRange time = new(1_000_000, 2_000_000);
        // 希伯来字母从右向左排。起止沿用 BuildSungRegions 的 XAt（元素的左缘）：第一个元素 L = 自己的左缘、R = 第二个元素的左缘，
        // R < L，演唱位置从 L 向左走，已唱的一侧在右（P3 设计 §10.1 第 1 条）。
        var project = Project(.5, [("א", time), ("א", new TimeRange(2_000_000, 3_000_000))]);
        using var pipeline = new SkiaRenderPipeline();
        var scene = pipeline.GetSceneFrame(ProjectSnapshot.FromProject(project), 0);
        float left = (float)scene.TokenGeometries[0].BaseGeometry.AdvanceBounds.Left, right = (float)scene.TokenGeometries[1].BaseGeometry.AdvanceBounds.Left;
        Assert.True(right < left, "希伯来文没有按从右向左排版。");
        var pixels = Direct(pipeline, project, 1_500_000);
        int checkedPixels = 0;
        for (int y = 0; y < Height; y++)
        for (int x = (int)Math.Ceiling(right); x < (int)left; x++)
        {
            if (pixels[(y * Width + x) * 4 + 3] != 255) continue;
            AssertMask(pixels, x, y, Mask(left, right, time, 1_500_000, 50, x + .5), "从右向左");
            checkedPixels++;
        }
        Assert.True(checkedPixels > 200, "字形不透明像素太少，无法比较。");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 两态相同时过渡带合成与硬切换相差不超过一(bool gpu)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        TimeRange time = new(1_000_000, 2_000_000);
        RgbaColor color = new(200, 120, 40);
        using var pipeline = Pipeline(gpu);
        pipeline.ForceWipeBand = true;
        var hard = Direct(pipeline, Project(0, [("█", time), ("█", new TimeRange(2_000_000, 3_000_000))], color, color), 1_950_000);
        var feathered = Direct(pipeline, Project(.5, [("█", time), ("█", new TimeRange(2_000_000, 3_000_000))], color, color), 1_950_000);
        Assert.Equal(hard.Length, feathered.Length);
        int worst = hard.Select((value, index) => Math.Abs(value - feathered[index])).Max();
        Assert.True(worst <= 1, $"两态相同时羽化前后最大差 {worst}。");
        // 关掉强制时两态相同直接按硬切换画，逐字节相同。
        pipeline.ForceWipeBand = false;
        Assert.Equal(hard, Direct(pipeline, Project(.5, [("█", time), ("█", new TimeRange(2_000_000, 3_000_000))], color, color), 1_950_000));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 不透明底上半透明两态带外像素与硬切换逐字节相同(bool gpu)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        TimeRange time = new(1_000_000, 2_000_000);
        RgbaColor unsung = new(255, 40, 40, 150), sung = new(40, 40, 255, 110), background = new(30, 90, 60);
        using var pipeline = Pipeline(gpu);
        var feathered = Project(.5, [("█", time), ("█", new TimeRange(2_000_000, 3_000_000))], unsung, sung, background);
        var hard = Project(0, [("█", time), ("█", new TimeRange(2_000_000, 3_000_000))], unsung, sung, background);
        var (left, right) = Spans(pipeline, hard)[0];
        const long t = 1_500_000;
        byte[] a = Direct(pipeline, hard, t, transparent: false), b = Direct(pipeline, feathered, t, transparent: false);
        float position = left + (right - left) * .5f;
        int differences = 0;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int offset = (y * Width + x) * 4;
            bool equal = a.AsSpan(offset, 4).SequenceEqual(b.AsSpan(offset, 4));
            if (x + .5 < position - 25 - 2 || x + .5 > position + 25 + 2) Assert.True(equal, $"带外像素 ({x},{y}) 与硬切换不同。");
            else if (!equal) differences++;
        }
        Assert.True(differences > 1000, "过渡带内应当与硬切换不同。");
    }

    [Fact]
    public void 注音分段有时间与跟随元素各按自己的区间羽化()
    {
        TimeRange time = new(1_000_000, 2_000_000);
        // 注音字号 50（比例 0.5），带宽 25；跟随元素时整段注音按元素时间，分段有时间时各段按自己的时间。
        var following = RubyAnnotation.Create("ruby", "t0", "██") with { GapPx = 20 };
        TimeRange firstSegment = new(1_000_000, 1_200_000), secondSegment = new(1_200_000, 2_000_000);
        var timed = following with { Segments = [RubyTextSegment.Create("s0", "█", firstSegment, TimingStatus.Edited),
            RubyTextSegment.Create("s1", "█", secondSegment, TimingStatus.Edited)] };
        using var pipeline = new SkiaRenderPipeline();
        foreach (var (ruby, name) in new[] { (following, "跟随元素"), (timed, "分段有时间") })
        {
            var project = Project(.5, [("█", time)], ruby: ruby);
            var clusters = pipeline.GetSceneFrame(ProjectSnapshot.FromProject(project), 0).TokenGeometries[0].Clusters
                .Where(static cluster => cluster.Target.RubyId is not null).Select(static cluster => cluster.Geometry).ToArray();
            Assert.Equal(2, clusters.Length);
            int row = (int)clusters[0].InkBounds!.Value.Center.Y;
            const long t = 1_300_000;
            var pixels = Direct(pipeline, project, t);
            for (int index = 0; index < 2; index++)
            {
                var bounds = clusters[index].AdvanceBounds;
                for (int x = (int)Math.Ceiling(bounds.Left); x < (int)bounds.Right; x++)
                {
                    double mask = ruby == following
                        ? Mask((float)clusters[0].AdvanceBounds.Left, (float)clusters[1].AdvanceBounds.Right, time, t, 25, x + .5)
                        : Mask((float)bounds.Left, (float)bounds.Right, index == 0 ? firstSegment : secondSegment, t, 25, x + .5);
                    AssertMask(pixels, x, row, mask, $"{name} 注音第 {index} 字");
                }
            }
        }
    }

    [Fact]
    public void 图场景原样部件重组与整体捕获一致()
    {
        TimeRange time = new(1_000_000, 2_000_000);
        // 带描边与注音：两态填充不同，过渡带内每个原样部件各自按 m 合成后再按部件层序叠回。
        var project = Project(.5, [("█", time), ("█", new TimeRange(2_000_000, 3_000_000))], ruby: RubyAnnotation.Create("ruby", "t0", "██"));
        project = project with { LyricStyle = project.LyricStyle with { Stroke = StrokeSpec.Create(new(20, 160, 20), 4) } };
        var request = Request(project, 1_400_000);
        using var pipeline = new SkiaRenderPipeline();
        SceneCaptureTarget.Line target = new(new(new("line"), new(pipeline.GetSceneFrame(request.Project, request.TimeUs).Lines[0].BindingId!)));
        SceneColorPart[] kinds = [SceneColorPart.BaseShadow, SceneColorPart.BaseStroke, SceneColorPart.BaseFill,
            SceneColorPart.RubyShadow, SceneColorPart.RubyStroke, SceneColorPart.RubyFill];
        var parts = kinds.Select(kind => pipeline.CaptureSceneColorPart(request, target, kind, Canvas, GraphExecutionBudget.Default)).ToArray();
        try
        {
            using var combined = pipeline.AssembleSceneColorParts(request, parts, Canvas, GraphExecutionBudget.Default);
            byte[] expected = Captured(pipeline, project, 1_400_000), actual = SkiaTestReadback.Pixels(combined.Surface.Image);
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++) Assert.True(Math.Abs(expected[i] - actual[i]) <= 2, $"分量 {i}：{expected[i]} / {actual[i]}");
            // 确实落在过渡带内：同一像素既不是纯未唱也不是纯已唱。
            int middle = (Row * Width + (int)Spans(pipeline, project)[0].Left + 40) * 4;
            Assert.InRange(actual[middle], 40, 215);
        }
        finally { foreach (var part in parts) part.Dispose(); }
    }

    [Fact]
    public async Task 填充包材质按两态拆侧时羽化先明确拒绝()
    {
        var package = BaseMaterialPackageFixture.Package();
        var plain = BaseMaterialPackageFixture.Snapshot(package, [BaseMaterialPackageFixture.Instance(package, "leaf", 2)], source: true);
        foreach (bool feathered in new[] { false, true })
        {
            var snapshot = feathered ? plain with { LyricStyle = plain.LyricStyle with { Wipe = new WipeSpec { FeatherRatio = .5 } } } : plain;
            var compiled = await BaseMaterialPackageFixture.Compile(snapshot);
            await using var source = await BaseMaterialPackageFixture.Factory(snapshot, compiled).CreateAsync(snapshot);
            var frame = await BaseMaterialPackageFixture.Prepare(source, snapshot);
            using var device = BaseMaterialRuntimeTests.Device(false);
            using var native = CompiledPackageGraphExports.CompileWithDevice(compiled.Packages[0].Definition, null, device);
            using var resources = BaseMaterialRuntimeTests.Resources(native);
            using var renderer = new SkiaRenderPipeline(null, null, null, resources, false, device, false);
            if (!feathered) { using var pixels = BaseMaterialRuntimeTests.Render(renderer, snapshot, frame); continue; }
            var error = Assert.ThrowsAny<Exception>(() => { using var pixels = BaseMaterialRuntimeTests.Render(renderer, snapshot, frame); });
            Assert.Equal("WipeFeatherMaterialPending", (error as PreparedFrameException ?? Assert.IsType<PreparedFrameException>(error.InnerException)).Code);
        }
    }

    [Fact]
    public void 已唱覆盖在过渡带内为字形覆盖乘m()
    {
        TimeRange time = new(1_000_000, 2_000_000);
        var project = Project(.5, [("█", time)]);
        using var pipeline = new SkiaRenderPipeline();
        var (left, right) = Assert.Single(Spans(pipeline, project));
        const long t = 1_400_000;
        var coverage = Captured(pipeline, project, t, GraphInputSource.SungCoverage);
        for (int x = (int)left; x < (int)right; x++)
        {
            int expected = (int)Math.Round(255 * Mask(left, right, time, t, 50, x + .5));
            Assert.InRange(coverage[(Row * Width + x) * 4 + 3], expected - 1, expected + 1);
        }
        // 其他覆盖两遍都画白色，不受羽化影响。
        Assert.Equal(Captured(pipeline, Project(0, [("█", time)]), t, GraphInputSource.GlyphCoverage),
            Captured(pipeline, project, t, GraphInputSource.GlyphCoverage));
    }
}
