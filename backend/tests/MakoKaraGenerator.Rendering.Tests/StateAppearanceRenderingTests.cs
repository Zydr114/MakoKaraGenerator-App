using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>P3 第 3 步：描边、阴影、辉光按演唱态分开绘制（P3 设计 §4）。</summary>
public sealed class StateAppearanceRenderingTests
{
    private static readonly GraphRect Canvas = new(0, 0, 640, 360);
    private static readonly StrokeSpec Thin = StrokeSpec.Create(new(220, 20, 20), 3);
    private static readonly StrokeSpec Thick = StrokeSpec.Create(new(20, 40, 220), 8);
    private static readonly ShadowSpec Drop = ShadowSpec.Create(new(0, 160, 0, 220), blurRadiusPx: 2);
    private static readonly GlowSpec Warm = GlowSpec.Create(new(240, 120, 20, 220), .8f, 4, 2);
    private static readonly GlowSpec Cool = GlowSpec.Create(new(20, 200, 240, 220), .9f, 7, 6);

    /// <summary>两态不同的装饰：描边与阴影，或只有辉光。</summary>
    public enum Split { Decorations, Glow }

    private static (TextAppearance Unsung, TextAppearance Sung) Appearances(Split split) => split == Split.Decorations
        ? (new() { Strokes = StrokeLayers.FromStroke(Thin) }, new() { Strokes = StrokeLayers.FromStroke(Thick), Shadow = Drop })
        : (new() { Strokes = StrokeLayers.FromStroke(Thin), Glow = Warm }, new() { Strokes = StrokeLayers.FromStroke(Thin), Glow = Cool });

    private static Project Project(TextAppearance unsung, SungAppearance sung, bool ruby = false, bool element = false)
    {
        var tokens = new[] { LyricToken.Create("a", "歌", new(0, 2_000_000), TimingStatus.Edited), LyricToken.Create("b", "詞", new(2_000_000, 4_000_000), TimingStatus.Edited) };
        // 元素覆盖让整行改走逐字族绘制。
        if (element) tokens[1] = tokens[1] with { StyleOverride = new() { Opacity = .8f } };
        // 两个注音相邻：两态辉光须先于全部注音填充，后一个的光晕不能盖住前一个。
        // 注音与正文同宽同时，演唱分界重合，整幅画面仍只分成两片。
        var line = LyricLine.Create("line", "歌詞", new(0, 4_500_000), TimingStatus.Edited, tokens) with
            { RubyAnnotations = ruby ? [RubyAnnotation.Create("ruby", "a", "かな") with { Segments = [RubyTextSegment.Create("kana", "かな", new(0, 2_000_000), TimingStatus.Edited)] },
                RubyAnnotation.Create("ruby-b", "b", "しい") with { Segments = [RubyTextSegment.Create("shii", "しい", new(2_000_000, 4_000_000), TimingStatus.Edited)] }] : [] };
        var style = LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 64 };
        style = style with { Unsung = unsung with { Fill = new() { Color = new(250, 250, 250) } }, Sung = sung with { Fill = new() { Color = new(250, 210, 0) } } };
        return Core.Projects.Project.Create("state", Composition.Create(640, 360, Core.Time.FrameRate.Fps30, outputDurationUs: 5_000_000), LyricsDocument.Create([line]),
            lyricStyle: style) with { EnterAnimation = null };
    }
    private static RenderRequest Request(Project p, long time = 1_000_000) => RenderRequest.At(ProjectSnapshot.FromProject(p), time) with { TransparentBackground = true };
    private static SceneCaptureTarget.Line Target(SkiaRenderPipeline r, RenderRequest q) => new(new(new("line"), new(r.GetSceneFrame(q.Project, q.TimeUs).Lines[0].BindingId!)));
    private static byte[] Pixels(SkiaGraphSurface s) => SkiaTestReadback.Pixels(s.Image);
    private static byte[] Direct(SkiaRenderPipeline r, Project p, long time = 1_000_000) { using var frame = r.Render(Request(p, time)); return frame.PixelMemory.ToArray(); }
    private static byte[] Captured(SkiaRenderPipeline r, Project p)
    {
        var q = Request(p);
        using var surface = r.CaptureSceneTarget(q, Target(r, q), new(true, true, true, false, SceneCaptureRegion.FillAndStroke, true),
            GraphInputSource.TargetColor, Canvas, GraphExecutionBudget.Default);
        return Pixels(surface);
    }

    /// <summary>演唱裁剪把画面分成互不重叠的两片：每个像素都应等于某一态整体绘制的结果，且两片都确实出现。</summary>
    private static void AssertEachPixelFromOneState(byte[] actual, byte[] unsung, byte[] sung, string path)
    {
        Assert.Equal(unsung.Length, actual.Length); Assert.Equal(sung.Length, actual.Length);
        int fromUnsung = 0, fromSung = 0;
        for (int i = 0; i < actual.Length; i += 4)
        {
            bool u = Near(actual, unsung, i), s = Near(actual, sung, i);
            Assert.True(u || s, $"{path}：像素 {i / 4} 两态都对不上");
            if (u && !s) fromUnsung++; else if (s && !u) fromSung++;
        }
        Assert.True(fromUnsung > 50 && fromSung > 50, $"{path}：只属未唱 {fromUnsung}，只属已唱 {fromSung}");
    }
    private static bool Near(byte[] a, byte[] b, int offset)
    {
        for (int i = offset; i < offset + 4; i++) if (Math.Abs(a[i] - b[i]) > 2) return false;
        return true;
    }

    [Theory]
    [InlineData(Split.Decorations, false, false)] [InlineData(Split.Decorations, true, false)] [InlineData(Split.Decorations, false, true)]
    [InlineData(Split.Glow, false, false)] [InlineData(Split.Glow, true, false)] [InlineData(Split.Glow, true, true)]
    public void 两态装饰各在本态区域绘制(Split split, bool ruby, bool element)
    {
        var (unsung, sung) = Appearances(split);
        var both = Project(unsung, SungAppearance.Explicit(sung), ruby, element);
        var unsungOnly = Project(unsung, SungAppearance.Following(new()), ruby, element);
        var sungOnly = Project(sung, SungAppearance.Following(new()), ruby, element);
        using var r = new SkiaRenderPipeline();
        AssertEachPixelFromOneState(Direct(r, both), Direct(r, unsungOnly), Direct(r, sungOnly), "直接绘制");
        AssertEachPixelFromOneState(Captured(r, both), Captured(r, unsungOnly), Captured(r, sungOnly), "目标捕获");
    }

    [Fact]
    public void 行首行尾字框外的装饰随扫字线切换()
    {
        var (unsung, sung) = Appearances(Split.Decorations);
        using var r = new SkiaRenderPipeline();
        // 行首开唱后，第一个字左侧已是已唱的粗描边，不在字框边切开后接未唱的细描边。
        int both = Left(Direct(r, Project(unsung, SungAppearance.Explicit(sung))));
        int thick = Left(Direct(r, Project(sung, SungAppearance.Following(new())))), thin = Left(Direct(r, Project(unsung, SungAppearance.Following(new()))));
        Assert.True(thick < thin, $"{thick} / {thin}"); Assert.Equal(thick, both);
        // 整行唱完后，行尾字框外（含注音末段之外）也全是已唱一态。两态填充取同色：对照样式两态装饰相同、不延边界，
        // 行尾字框外的填充溢出仍按未唱色画，与这里要比较的装饰无关。
        foreach (bool ruby in new[] { false, true })
            Assert.Equal(Direct(r, SameFill(Project(sung, SungAppearance.Following(new()), ruby)), 4_200_000),
                Direct(r, SameFill(Project(unsung, SungAppearance.Explicit(sung), ruby)), 4_200_000));

        static Project SameFill(Project p) => p with { LyricStyle = p.LyricStyle with { SungFill = p.LyricStyle.UnsungFill } };
        static int Left(byte[] pixels)
        {
            for (int x = 0; x < 640; x++) for (int y = 0; y < 360; y++) if (pixels[(y * 640 + x) * 4 + 3] != 0) return x;
            return -1;
        }
    }

    [Theory]
    [InlineData(Split.Decorations)] [InlineData(Split.Glow)]
    public void 两态装饰的八部件重组与整体捕获一致(Split split)
    {
        var (unsung, sung) = Appearances(split);
        var q = Request(Project(unsung, SungAppearance.Explicit(sung), ruby: true));
        using var r = new SkiaRenderPipeline(); var target = Target(r, q);
        SceneColorPart[] kinds = [SceneColorPart.BaseGlow, SceneColorPart.BaseShadow, SceneColorPart.BaseStroke, SceneColorPart.BaseFill,
            SceneColorPart.RubyGlow, SceneColorPart.RubyShadow, SceneColorPart.RubyStroke, SceneColorPart.RubyFill];
        var parts = kinds.Select(k => r.CaptureSceneColorPart(q, target, k, Canvas, GraphExecutionBudget.Default)).ToArray();
        try
        {
            using var combined = r.AssembleSceneColorParts(q, parts, Canvas, GraphExecutionBudget.Default);
            using var expected = r.CaptureSceneTarget(q, target, new(true, true, true, false, SceneCaptureRegion.FillAndStroke, true),
                GraphInputSource.TargetColor, Canvas, GraphExecutionBudget.Default);
            var a = Pixels(expected); var b = Pixels(combined.Surface);
            Assert.Equal(a.Length, b.Length);
            for (int i = 0; i < a.Length; i++) Assert.True(Math.Abs(a[i] - b[i]) <= 2, $"component {i}: {a[i]} / {b[i]}");
        }
        finally { foreach (var part in parts) part.Dispose(); }
    }

    [Fact]
    public void 标记按待唱已过整体取本态装饰()
    {
        var project = LeadInFixture.Create(); var tree = project.LyricPresentation;
        var group = tree.FindNode(tree.FindProgram("primary")!.LeadIns[0].IndicatorContainerId)!;
        var sequence = (CountdownSequenceContent)group.Content!;
        var appearance = sequence.Appearance with
        {
            Pending = sequence.Appearance.Pending with { Strokes = StrokeLayers.FromStroke(StrokeSpec.Create(new(255, 0, 0), 4)) },
            Elapsed = sequence.Appearance.Elapsed with
            {
                Strokes = Followable.Set(StrokeLayers.FromStroke(StrokeSpec.Create(new(0, 0, 255), 4))),
                Glow = Followable.Set<GlowSpec?>(GlowSpec.Create(new(0, 255, 0), 1, 3, 4)),
            },
        };
        tree = tree with { Nodes = tree.Nodes.Replace(group, group with { Content = sequence with { Appearance = appearance } }) };
        var snapshot = ProjectSnapshot.FromProject(project with { LyricPresentation = tree });
        using SkiaRenderPipeline pipeline = new();
        Assert.All(pipeline.GetSceneFrame(snapshot, 9_500_000).TextMarks, mark => Assert.False(mark.IsElapsed));
        var marks = pipeline.GetSceneFrame(snapshot, 10_000_000).TextMarks;
        Assert.True(marks[0].IsElapsed); Assert.All(marks.Skip(1), mark => Assert.False(mark.IsElapsed));
        // 三个标记相邻、装饰会越过各自的框，所以按整帧计数：第一个变为已过后红色描边减少，出现蓝色描边与绿色辉光。
        var pending = Count(9_500_000); var elapsed = Count(10_000_000);
        Assert.True(pending.Red > 100 && pending.Blue == 0 && pending.Green == 0, $"全部待唱：{pending}");
        Assert.True(elapsed.Blue > 20 && elapsed.Green > 20 && elapsed.Red > 20 && elapsed.Red < pending.Red * 3 / 4, $"第一个已过：{elapsed}");

        (int Red, int Green, int Blue) Count(long time)
        {
            using var frame = pipeline.Render(RenderRequest.At(snapshot, time) with { TransparentBackground = true });
            int red = 0, green = 0, blue = 0; var pixels = frame.PixelMemory.Span;
            for (int y = 0; y < frame.Height; y++) for (int x = 0; x < frame.Width; x++)
            {
                int i = y * frame.Stride + x * 4; int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                if (r > 150 && g < 60 && b < 60) red++;
                if (g > 100 && g > r * 2 && g > b * 2) green++;
                if (b > 150 && r < 60 && g < 60) blue++;
            }
            return (red, green, blue);
        }
    }

    [Fact]
    public void 脚本改动的装饰两态一起变而未改的保留已唱一侧()
    {
        var source = LyricStyle.Default with
        {
            Unsung = new() { Strokes = StrokeLayers.FromStroke(Thin), Glow = Warm },
            Sung = SungAppearance.Explicit(new() { Strokes = StrokeLayers.FromStroke(Thick), Shadow = Drop, Glow = Cool }),
        };
        var stroke = StaticStyleProjection.Apply(source, new Fx.StyleUpdate(stroke: new Fx.StrokeUpdate(new Fx.StrokeValue(5, Fx.MaterialValue.Solid(new(0, 1, 0, 1))))));
        Assert.Equal(5, stroke.Stroke!.WidthPx); Assert.True(stroke.Sung.Strokes.FollowsUnsung);
        Assert.Equal(source.Sung.Shadow, stroke.Sung.Shadow); Assert.Equal(source.Sung.Glow, stroke.Sung.Glow);
        var shadow = StaticStyleProjection.Apply(source, new Fx.StyleUpdate(shadow: new Fx.ShadowUpdate(new Fx.ShadowValue(new(0, 0, 0, 1)))));
        Assert.NotNull(shadow.Shadow); Assert.True(shadow.Sung.Shadow.FollowsUnsung);
        Assert.Same(source.Sung.Strokes.Value, shadow.Sung.Strokes.Value); Assert.Equal(source.Sung.Glow, shadow.Sung.Glow);
        // 与基线相同的写入视同未写。
        Assert.Same(source, StaticStyleProjection.Apply(source, new Fx.StyleUpdate(stroke: new Fx.StrokeUpdate(StaticStyleProjection.View(source).Stroke))));
    }
}
