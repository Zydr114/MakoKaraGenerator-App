using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class GlowRenderingTests
{
    private static readonly GraphRect Canvas = new(0, 0, 640, 360);
    private static Project Project(bool ruby = false)
    {
        var line = LyricLine.Create("line", "歌詞", new(0, 4_000_000), TimingStatus.Edited,
            [LyricToken.Create("a", "歌", new(0, 2_000_000), TimingStatus.Edited), LyricToken.Create("b", "詞", new(2_000_000, 4_000_000), TimingStatus.Edited)]) with
            { RubyAnnotations = ruby ? [RubyAnnotation.Create("ruby", "a", "かな") with { Segments = [RubyTextSegment.Create("ka", "か", new(0, 1_000_000), TimingStatus.Edited), RubyTextSegment.Create("na", "な", new(2_000_000, 3_000_000), TimingStatus.Edited)] }] : [] };
        return Core.Projects.Project.Create("glow", Composition.Create(640, 360, Core.Time.FrameRate.Fps30, outputDurationUs: 5_000_000), LyricsDocument.Create([line]),
            lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 64, Stroke = null, Shadow = null,
                UnsungColor = RgbaColor.Transparent, SungColor = RgbaColor.Transparent,
                Glow = GlowSpec.Create(new(240, 20, 80, 180), .8f, 5, 2) });
    }
    private static RenderRequest Request(Project p, long time = 1_000_000) => RenderRequest.At(ProjectSnapshot.FromProject(p), time) with { TransparentBackground = true };
    private static SceneCaptureTarget.Line Target(SkiaRenderPipeline r, RenderRequest q) => new(new(new("line"), new(r.GetSceneFrame(q.Project, q.TimeUs).Lines[0].BindingId!)));
    private static byte[] Pixels(SkiaGraphSurface s) => SkiaTestReadback.Pixels(s.Image);
    private static long Alpha(byte[] p) { long sum = 0; for (int i = 3; i < p.Length; i += 4) sum += p[i]; return sum; }
    private static SkiaGraphSurface Capture(SkiaRenderPipeline r, RenderRequest q, GraphInputSource source = GraphInputSource.TargetColor, bool glow = true)
        => r.CaptureSceneTarget(q, Target(r, q), new(true, true, true, false, SceneCaptureRegion.FillAndStroke, glow), source, Canvas, GraphExecutionBudget.Default);
    private static void Close(byte[] a, byte[] b, int tolerance = 2)
    { Assert.Equal(a.Length, b.Length); for (int i = 0; i < a.Length; i++) Assert.True(Math.Abs(a[i] - b[i]) <= tolerance, $"component {i}: {a[i]} / {b[i]}"); }

    [Fact]
    public void 透明填充仍有外发光且真实字形内部被扣除()
    {
        using var r = new SkiaRenderPipeline(); var q = Request(Project());
        using var glow = Capture(r, q); using var glyph = Capture(r, q, GraphInputSource.GlyphCoverage);
        var p = Pixels(glow); var mask = Pixels(glyph); Assert.True(Alpha(p) > 10_000);
        int opaque = 0, exterior = 0;
        for (int i = 3; i < p.Length; i += 4)
        {
            if (mask[i] == 255) { opaque++; Assert.Equal(0, p[i]); }
            if (mask[i] == 0 && p[i] > 0) exterior++;
        }
        Assert.True(opaque > 100); Assert.True(exterior > 100);
        using var off = Capture(r, q, glow: false); Assert.Equal(0, Alpha(Pixels(off)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 辉光在逐字与注音两态边界外只画一次(bool ruby)
    {
        using var r = new SkiaRenderPipeline(); var p = Project(ruby);
        using var start = Capture(r, Request(p, 0)); using var middle = Capture(r, Request(p, 1_000_000)); using var end = Capture(r, Request(p, 3_999_999));
        Assert.Equal(Pixels(start), Pixels(middle)); Assert.Equal(Pixels(start), Pixels(end));
    }

    [Fact]
    public void 不透明度只乘一次而零不透明度为空()
    {
        using var r = new SkiaRenderPipeline(); var p = Project(); using var full = Capture(r, Request(p));
        using var half = Capture(r, Request(p with { LyricStyle = p.LyricStyle with { Opacity = .5f } }));
        double ratio = Alpha(Pixels(half)) / (double)Alpha(Pixels(full)); Assert.InRange(ratio, .46, .54);
        using var zero = Capture(r, Request(p with { LyricStyle = p.LyricStyle with { Opacity = 0 } })); Assert.Equal(0, Alpha(Pixels(zero)));
    }

    [Theory]
    [InlineData(GraphInputSource.GlyphCoverage)] [InlineData(GraphInputSource.StrokeCoverage)]
    [InlineData(GraphInputSource.SungCoverage)] [InlineData(GraphInputSource.TargetRegionCoverage)]
    public void 覆盖通道完全排除辉光(GraphInputSource source)
    {
        using var r = new SkiaRenderPipeline(); var p = Project(true);
        using var glow = Capture(r, Request(p), source); using var plain = Capture(r, Request(p with { LyricStyle = p.LyricStyle with { Glow = null } }), source);
        Assert.Equal(Pixels(plain), Pixels(glow));
    }

    [Theory]
    [InlineData(640, 360)] [InlineData(480, 180)]
    public void 八部件重组保持原生产颜色和一次共同alpha(int width, int height)
    {
        var p = Project(true); p = p with { EnterAnimation = new() { DurationUs = 2_000_000, StartOpacity = .2f, StartScale = 1, StartOffsetPx = 0, Easing = AnimationEasing.Linear },
            LyricStyle = p.LyricStyle with { UnsungColor = new(20, 40, 180, 100), SungColor = new(180, 20, 40, 140), Opacity = .7f,
                Stroke = StrokeSpec.Create(new(0, 180, 20, 160), 3), Shadow = ShadowSpec.Create(new(180, 0, 20, 140), blurRadiusPx: 3) } };
        using var r = new SkiaRenderPipeline(); var q = Request(p) with { Target = new() { PixelWidth = width, PixelHeight = height } }; var target = Target(r, q);
        SceneColorPart[] kinds = [SceneColorPart.BaseGlow, SceneColorPart.BaseStroke, SceneColorPart.BaseShadow, SceneColorPart.BaseFill,
            SceneColorPart.RubyGlow, SceneColorPart.RubyStroke, SceneColorPart.RubyShadow, SceneColorPart.RubyFill];
        var parts = kinds.Select(k => r.CaptureSceneColorPart(q, target, k, Canvas, GraphExecutionBudget.Default)).ToArray();
        try
        {
            Assert.All(parts, part => Assert.True(Alpha(Pixels(part.Surface)) > 0));
            using var combined = r.AssembleSceneColorParts(q, parts, Canvas, GraphExecutionBudget.Default);
            // Assemble deliberately leaves common opacity for its caller; compare without entrance alpha.
            using var expected = Capture(r, q with { Project = q.Project with { EnterAnimation = null } });
            Close(Pixels(expected), Pixels(combined.Surface));
            Assert.All(parts, part => Assert.InRange(part.CommonOpacity, .59, .61));
        }
        finally { foreach (var part in parts) part.Dispose(); }
    }

    [Fact]
    public void 元素覆盖关闭辉光不残留基线且独立变换随字形移动()
    {
        var p = Project(); var line = p.Lyrics.AllLines.Single();
        p = p with { Lyrics = LyricsDocument.Create([line with { Tokens = line.Tokens.SetItem(0, line.Tokens[0] with { StyleOverride = new() { Glow = (GlowSpecOverride?)null } }) }]) };
        using var r = new SkiaRenderPipeline(); var q = Request(p); var scene = r.GetSceneFrame(q.Project, q.TimeUs); var a = scene.TokenGeometries.Single(x => x.WithRuby.TokenId.Value == "a");
        using var disabled = r.CaptureSceneColorPart(q, new SceneCaptureTarget.Token(a.WithRuby), SceneColorPart.BaseGlow, Canvas, GraphExecutionBudget.Default);
        Assert.Equal(0, Alpha(Pixels(disabled.Surface)));
        var b = scene.TokenGeometries.Single(x => x.WithRuby.TokenId.Value == "b");
        var snapshot = q.Project with { EffectInputsFingerprint = "glow transform" }; scene = r.GetSceneFrame(snapshot, q.TimeUs);
        var prepared = new PreparedRenderFrame(snapshot, q.TimeUs, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition), scene,
            [new(b.WithRuby, Fx.Affine2.Translation(20, -10))], []);
        using var original = r.CaptureSceneColorPart(q, new SceneCaptureTarget.Token(b.WithRuby), SceneColorPart.BaseGlow, Canvas, GraphExecutionBudget.Default);
        using var moved = r.CaptureSceneColorPart(q with { Project = snapshot, PreparedFrame = prepared }, new SceneCaptureTarget.Token(b.WithRuby), SceneColorPart.BaseGlow, Canvas, GraphExecutionBudget.Default);
        var src = Pixels(original.Surface); var dst = Pixels(moved.Surface);
        for (int y = 10; y < 360; y++) for (int x = 0; x < 620; x++) Assert.Equal(src[(y * 640 + x) * 4 + 3], dst[((y - 10) * 640 + x + 20) * 4 + 3]);
    }

    [Fact]
    public void 相邻元素辉光颜色只来自真实所属字形()
    {
        var p = Project(); var line = p.Lyrics.AllLines.Single();
        var red = GlowSpec.Create(new(255, 0, 0), .8f, 6, 10);
        p = p with { LyricStyle = p.LyricStyle with { Glow = red }, Lyrics = LyricsDocument.Create([line with {
            Tokens = line.Tokens.SetItem(1, line.Tokens[1] with { StyleOverride = new() { Glow = new GlowSpecOverride { Color = new(0, 0, 255) } } }) }]) };
        using var r = new SkiaRenderPipeline(); var q = Request(p); var scene = r.GetSceneFrame(q.Project, q.TimeUs);
        using var whole = r.CaptureSceneColorPart(q, Target(r, q), SceneColorPart.BaseGlow, Canvas, GraphExecutionBudget.Default);
        using var a = r.CaptureSceneColorPart(q, new SceneCaptureTarget.Token(scene.TokenGeometries[0].WithRuby), SceneColorPart.BaseGlow, Canvas, GraphExecutionBudget.Default);
        using var b = r.CaptureSceneColorPart(q, new SceneCaptureTarget.Token(scene.TokenGeometries[1].WithRuby), SceneColorPart.BaseGlow, Canvas, GraphExecutionBudget.Default);
        byte[] ap = Pixels(a.Surface), bp = Pixels(b.Surface), all = Pixels(whole.Surface);
        for (int i = 0; i < ap.Length; i += 4) { Assert.Equal(0, ap[i]); Assert.Equal(0, bp[i + 2]); }
        Assert.True(Alpha(all) > Alpha(ap)); Assert.True(Alpha(bp) > 1000);
        // 各族分别捕获后按生产顺序 SrcOver，必须与整行最终辉光一致。
        using var device = SkiaRenderDeviceFactory.Create(); using var surface = device.CreateSurface(new SkiaSharp.SKImageInfo(640, 360)); surface.Canvas.Clear(SkiaSharp.SKColors.Transparent);
        foreach (var part in new[] { a, b }) { var image = part.Surface.ImageForDevice(device, out var raster); try { surface.Canvas.DrawImage(image, 0, 0); } finally { raster?.Dispose(); } }
        device.FlushAndSubmit(surface); using var combined = surface.Snapshot(); Close(all, SkiaTestReadback.Pixels(combined));
    }

    [Fact]
    public void 元素不同辉光与填充交叠时八部件仍重组一致()
    {
        var p = Project(true); var line = p.Lyrics.AllLines.Single();
        p = p with { LyricStyle = p.LyricStyle with { UnsungColor = RgbaColor.White, SungColor = RgbaColor.White, Glow = GlowSpec.Create(new(255, 0, 0), blurRadiusPx: 8, spreadPx: 20) },
            Lyrics = LyricsDocument.Create([line with { Tokens = line.Tokens.SetItem(1, line.Tokens[1] with { StyleOverride = new() { Glow = new GlowSpecOverride { Color = new(0, 0, 255) } } }) }]) };
        using var r = new SkiaRenderPipeline(); var q = Request(p); var target = Target(r, q);
        SceneColorPart[] order = [SceneColorPart.BaseGlow, SceneColorPart.BaseStroke, SceneColorPart.BaseShadow, SceneColorPart.BaseFill,
            SceneColorPart.RubyGlow, SceneColorPart.RubyStroke, SceneColorPart.RubyShadow, SceneColorPart.RubyFill];
        var parts = order.Select(kind => r.CaptureSceneColorPart(q, target, kind, Canvas, GraphExecutionBudget.Default)).ToArray();
        try { using var combined = r.AssembleSceneColorParts(q, parts, Canvas, GraphExecutionBudget.Default); using var actual = Capture(r, q); Close(Pixels(actual), Pixels(combined.Surface)); }
        finally { foreach (var part in parts) part.Dispose(); }
    }

    [Fact]
    public void 大范围辉光不受旧64像素限制且扩张增加可见范围()
    {
        using var r = new SkiaRenderPipeline(); var p = Project();
        using var small = Capture(r, Request(p with { LyricStyle = p.LyricStyle with { Glow = GlowSpec.Create(RgbaColor.White, blurRadiusPx: 0, spreadPx: 4) } }));
        using var large = Capture(r, Request(p with { LyricStyle = p.LyricStyle with { Glow = GlowSpec.Create(RgbaColor.White, blurRadiusPx: 0, spreadPx: 80) } }));
        var a = Pixels(small); var b = Pixels(large); Assert.True(Alpha(b) > Alpha(a) * 4);
        int minSmall = 640, minLarge = 640;
        for (int i = 3; i < a.Length; i += 4) { if (a[i] > 5) minSmall = Math.Min(minSmall, i / 4 % 640); if (b[i] > 5) minLarge = Math.Min(minLarge, i / 4 % 640); }
        Assert.True(minSmall - minLarge > 65);
    }

    [Fact]
    public void 前奏标记辉光使用统一节点透明度且透明填充可见()
    {
        var p = LeadInFixture.Create(true); var style = Project().LyricStyle with { Opacity = .5f };
        p = p with { LyricPresentation = p.LyricPresentation with { Nodes = [.. p.LyricPresentation.Nodes.Select(node =>
            node.Content is CountdownSequenceContent sequence ? node with { Content = sequence with { Appearance = SceneTextAppearance.FromStyle(style) } } : node)] } };
        using var r = new SkiaRenderPipeline(); var q = Request(p, 10_000_000); var scene = r.GetSceneFrame(q.Project, q.TimeUs);
        var target = new SceneCaptureTarget.Mark(scene.MarkGeometries[0].Target);
        using var part = r.CaptureSceneColorPart(q, target, SceneColorPart.BaseGlow, Canvas, GraphExecutionBudget.Default);
        Assert.True(Alpha(Pixels(part.Surface)) > 1000); Assert.Equal(.5, part.CommonOpacity);
        using var full = r.CaptureSceneTarget(q, target, new(false, true, true, false, SceneCaptureRegion.FillAndStroke), GraphInputSource.TargetColor, Canvas, GraphExecutionBudget.Default);
        Assert.InRange(Alpha(Pixels(full)) / (double)Alpha(Pixels(part.Surface)), .46, .54);
    }

    [Fact]
    public void 共享合字不伪造独立元素辉光蒙版()
    {
        var p = Project(); var line = LyricLine.Create("line", "office", new(0, 4_000_000), TimingStatus.Edited,
            [.. "office".Select((c, i) => LyricToken.Create("t" + i, c.ToString()))]);
        p = p with { Lyrics = LyricsDocument.Create([line]), LyricStyle = p.LyricStyle with { FontFamilyName = "DejaVu Sans" } };
        using var r = new SkiaRenderPipeline(); var q = Request(p); var scene = r.GetSceneFrame(q.Project, q.TimeUs);
        var shared = scene.TokenGeometries.First(item => !item.BaseGeometry.CanTransformIndependently);
        var changed = line with { Tokens = [.. line.Tokens.Select(token => token.Id == shared.WithRuby.TokenId.Value
            ? token with { StyleOverride = new() { Glow = (GlowSpecOverride?)null } } : token)] };
        q = Request(p with { Lyrics = LyricsDocument.Create([changed]) });
        Assert.Equal("UnsupportedGlyphPartition", Assert.Throws<PreparedFrameException>(() => Capture(r, q)).Code);
    }

    [Fact]
    public void 辉光图层预算失败后释放账目且超出原生范围明确拒绝()
    {
        using var ledger = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 2 }); using var frame = ledger.BeginFrame(); using var r = new SkiaRenderPipeline(surfaceBudget: ledger);
        var q = Request(Project()); Assert.Throws<ImageSurfaceBudgetException>(() => r.CaptureSceneColorPart(q, Target(r, q), SceneColorPart.BaseGlow, Canvas, GraphExecutionBudget.Default, frame));
        Assert.Equal(0, ledger.Usage.ActiveSurfaces); Assert.Equal(0, ledger.Usage.EstimatedBytes);
        var p = Project(); q = Request(p with { LyricStyle = p.LyricStyle with { Glow = p.LyricStyle.Glow! with { SpreadPx = float.MaxValue } } });
        Assert.Equal("UnsupportedNativeStyleValues", Assert.Throws<PreparedFrameException>(() => Capture(r, q)).Code);
    }
}
