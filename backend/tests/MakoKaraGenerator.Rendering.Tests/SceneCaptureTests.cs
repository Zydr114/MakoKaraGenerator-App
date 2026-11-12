using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class SceneCaptureTests
{
    private static readonly SceneCaptureRecipe Full = new(true, true, true, true, SceneCaptureRegion.FillAndStroke);
    private static readonly GraphRect Canvas = new(0, 0, 640, 360);
    private static Project Single(bool missing = false)
    {
        var line = LyricLine.Create("row", "歌詞歌", new(0, 5_000_000), TimingStatus.Edited,
            [LyricToken.Create("a", "歌", new(500_000, 1_500_000), TimingStatus.Edited),
             LyricToken.Create("b", "詞", missing ? null : new(1_500_000, 2_000_000), missing ? TimingStatus.Missing : TimingStatus.Edited),
             LyricToken.Create("c", "歌", new(2_000_000, 3_000_000), TimingStatus.Edited)]) with
        { RubyAnnotations = [RubyAnnotation.Create("ruby-a", "a", "か"), RubyAnnotation.Create("ruby-b", "b", "し")] };
        return Project.Create("production capture", Composition.Create(640, 360, Core.Time.FrameRate.Fps30, outputDurationUs: 5_000_000),
            LyricsDocument.Create([line]), lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 64,
                UnsungColor = new(240, 240, 255, 170), SungColor = new(255, 90, 20, 200), Opacity = .65f,
                Stroke = StrokeSpec.Create(new(40, 150, 255, 180), 4), Shadow = ShadowSpec.Create(new(120, 20, 20, 110), blurRadiusPx: 3) });
    }
    private static SceneCaptureTarget.Line Line(SceneFrame frame)
        => new(new(new(frame.Lines[0].Frame.Line!.Id), new(frame.Lines[0].BindingId!)));
    private static byte[] Bytes(SkiaGraphSurface capture) { using var bitmap = SKBitmap.FromImage(capture.Image); return bitmap.Bytes; }
    private static SkiaGraphSurface Capture(SkiaRenderPipeline pipeline, RenderRequest request, SceneCaptureTarget target,
        GraphInputSource source = GraphInputSource.TargetColor, SceneCaptureRecipe? recipe = null, GraphRect? bounds = null, GraphExecutionBudget? budget = null)
        => pipeline.CaptureSceneTarget(request, target, recipe ?? Full, source, bounds ?? Canvas, budget ?? GraphExecutionBudget.Default);
    private static long Alpha(byte[] bytes) => Enumerable.Range(0, bytes.Length / 4).Sum(i => (long)bytes[i * 4 + 3]);

    [Theory]
    [InlineData(500_000L, 640, 360)] [InlineData(1_000_000L, 320, 180)] [InlineData(2_500_000L, 480, 180)]
    public void 真实行捕获与透明生产Render逐字节相等含注音描边阴影和实际密度(long time, int width, int height)
    {
        var snapshot = ProjectSnapshot.FromProject(Single()); using SkiaRenderPipeline pipeline = new();
        var request = RenderRequest.At(snapshot, time) with { TransparentBackground = true, Target = new() { PixelWidth = width, PixelHeight = height } };
        using var rendered = pipeline.Render(request); using var captured = Capture(pipeline, request, Line(pipeline.GetSceneFrame(snapshot, time)));
        Assert.Equal(rendered.PixelMemory.ToArray(), Bytes(captured)); Assert.True(Alpha(Bytes(captured)) > 100_000);
        Assert.True(captured.Image.ColorSpace!.IsSrgb); Assert.Equal(width / 640d, captured.Descriptor.DensityX);
    }

    [Fact]
    public void 捕获复用准备帧最终行族外观和矩阵且不修改源()
    {
        var project = Single(); var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = "capture-fixed" };
        using SkiaRenderPipeline pipeline = new(); var scene = pipeline.GetSceneFrame(snapshot, 1_000_000);
        var token = scene.TokenGeometries[0]; var line = Line(scene);
        var style = TestStyle.Update(sung: Fx.MaterialValue.Solid(new(0, 1, 0, .7)), opacityFactor: .5);
        var prepared = new PreparedRenderFrame(snapshot, scene.TimeUs, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition), scene,
            [new(token.WithRuby, Fx.Affine2.ScaleAt(1.3, token.BaseGeometry.AdvanceBounds.Center))], [new(token.WithRuby, style)], [],
            lineTransforms: [new(line.Target, Fx.Affine2.Translation(14, -9))]);
        var request = RenderRequest.At(snapshot, scene.TimeUs) with { TransparentBackground = true, PreparedFrame = prepared };
        using var rendered = pipeline.Render(request); using var captured = Capture(pipeline, request, line);
        Assert.Equal(rendered.PixelMemory.ToArray(), Bytes(captured)); Assert.Same(project.Lyrics, snapshot.Lyrics);
        using var plain = pipeline.Render(RenderRequest.At(snapshot with { EffectInputsFingerprint = null }, scene.TimeUs) with { TransparentBackground = true });
        Assert.False(plain.PixelMemory.Span.SequenceEqual(rendered.PixelMemory.Span));
        Assert.Throws<PreparedFrameException>(() => Capture(pipeline, request with { TimeUs = scene.TimeUs + 1 }, line));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 真实父组捕获含附着与固定前奏保持源绘制顺序以及单次入场alpha(bool fixedParent)
    {
        var project = LeadInFixture.Create(true, true); project = project with { Lyrics = project.Lyrics with { Branches = [project.Lyrics.Branches[0]] } };
        var tree = project.LyricPresentation; var program = tree.FindProgram("primary")!;
        var indicator = tree.FindNode(program.LeadIns[0].IndicatorContainerId)!;
        tree = tree with { Nodes = tree.Nodes.Replace(indicator, indicator with { Attachment = new()
            { Mode = fixedParent ? SceneAttachmentMode.FixedParent : SceneAttachmentMode.CueTargetText } }),
            LineOverrides = tree.LineOverrides.Add(new() { BranchId = "primary", LineId = "first", EnterAnimation = new()
            { DurationUs = 2_000_000, StartOpacity = .2f, StartScale = .8f, StartOffsetPx = 9, Easing = AnimationEasing.Linear } }) };
        var snapshot = ProjectSnapshot.FromProject(project with { LyricPresentation = tree }); using SkiaRenderPipeline pipeline = new();
        var request = RenderRequest.At(snapshot, 10_000_000) with { TransparentBackground = true };
        using var expected = pipeline.Render(request); using var group = Capture(pipeline, request, new SceneCaptureTarget.Group(new(new(program.RootContainerId))));
        Assert.Equal(expected.PixelMemory.ToArray(), Bytes(group)); Assert.Equal(3, pipeline.GetSceneFrame(snapshot, request.TimeUs).TextMarks.Length);
        using var direct = Capture(pipeline, request, new SceneCaptureTarget.Group(new(new(program.RootContainerId))), recipe: Full with { IncludeDescendantGroups = false });
        Assert.Equal(0, Alpha(Bytes(direct)));
    }

    [Fact]
    public void 字形覆盖独立于透明填充样式和入场alpha外部描边不填内部()
    {
        var project = Single(); project = project with { Lyrics = LyricsDocument.Create([LyricLine.Create("row", "歌", new(0, 5_000_000), TimingStatus.Edited,
            [LyricToken.Create("a", "歌", new(500_000, 3_000_000), TimingStatus.Edited)])]) };
        var transparent = project.LyricStyle with { UnsungColor = RgbaColor.Transparent, SungColor = RgbaColor.Transparent,
            Opacity = 0, Stroke = StrokeSpec.Create(RgbaColor.Transparent, 4) };
        using SkiaRenderPipeline pipeline = new(); var snapshot = ProjectSnapshot.FromProject(project with { LyricStyle = transparent });
        var request = RenderRequest.At(snapshot, 1_000_000) with { TransparentBackground = true }; var target = Line(pipeline.GetSceneFrame(snapshot, request.TimeUs));
        using var color = Capture(pipeline, request, target); Assert.Equal(0, Alpha(Bytes(color)));
        using var glyph = Capture(pipeline, request, target, GraphInputSource.GlyphCoverage);
        using var stroke = Capture(pipeline, request, target, GraphInputSource.StrokeCoverage);
        using var opaque = Capture(pipeline, request with { Project = snapshot with { LyricStyle = transparent with { Opacity = 1, UnsungColor = RgbaColor.White, SungColor = RgbaColor.White } } }, target, GraphInputSource.GlyphCoverage);
        Assert.Equal(Bytes(opaque), Bytes(glyph)); Assert.True(Alpha(Bytes(stroke)) > 100_000);
        byte[] fill = Bytes(glyph), outline = Bytes(stroke); int inside = 0, outside = 0;
        for (int i = 3; i < fill.Length; i += 4)
        { if (fill[i] == 255) { Assert.True(outline[i] == 0, $"pixel={(i-3)/4%640},{(i-3)/4/640} stroke={outline[i]}"); inside++; } if (fill[i] == 0 && outline[i] > 200) outside++; }
        Assert.True(inside > 100); Assert.True(outside > 100);
        using var region = Capture(pipeline, request, target, GraphInputSource.TargetRegionCoverage, Full with { Region = SceneCaptureRegion.Stroke });
        Assert.Equal(outline, Bytes(region));
    }

    [Fact]
    public void 未定时元素及其注音在后字已唱时仍没有Sung覆盖()
    {
        var snapshot = ProjectSnapshot.FromProject(Single(true)); using SkiaRenderPipeline pipeline = new();
        var request = RenderRequest.At(snapshot, 2_500_000) with { TransparentBackground = true }; var scene = pipeline.GetSceneFrame(snapshot, request.TimeUs);
        var middle = scene.TokenGeometries.Single(item => item.WithRuby.TokenId.Value == "b");
        using var mask = Capture(pipeline, request, new SceneCaptureTarget.Token(middle.WithRuby), GraphInputSource.SungCoverage);
        using var baseMask = Capture(pipeline, request, new SceneCaptureTarget.Token(middle.WithRuby), GraphInputSource.GlyphCoverage);
        Assert.Equal(0, Alpha(Bytes(mask))); Assert.True(Alpha(Bytes(baseMask)) > 100_000);
        using var sung = Capture(pipeline, request, Line(scene), GraphInputSource.SungCoverage);
        Assert.True(Alpha(Bytes(sung)) > 100_000);
    }

    [Fact]
    public void 独立族仅画固定自身字形可选择注音不会绘制相邻元素()
    {
        var project = Single(); var snapshot = ProjectSnapshot.FromProject(project with { LyricStyle = project.LyricStyle with { Stroke = null, Shadow = null } });
        using SkiaRenderPipeline pipeline = new(); var request = RenderRequest.At(snapshot, 1_000_000) with { TransparentBackground = true };
        var scene = pipeline.GetSceneFrame(snapshot, request.TimeUs); var target = new SceneCaptureTarget.Token(scene.TokenGeometries[0].WithRuby);
        using var withRuby = Capture(pipeline, request, target, GraphInputSource.GlyphCoverage);
        using var without = Capture(pipeline, request, target, GraphInputSource.GlyphCoverage, Full with { IncludeRuby = false });
        Assert.True(Alpha(Bytes(withRuby)) > Alpha(Bytes(without))); byte[] pixels = Bytes(withRuby);
        var corners = scene.TokenGeometries[0].FamilyGeometry.WorldInkCorners;
        double left = corners.Min(p => p.X) - 2, right = corners.Max(p => p.X) + 2;
        for (int y = 0; y < 360; y++) for (int x = 0; x < 640; x++) if (x < left || x > right) Assert.Equal(0, pixels[(y * 640 + x) * 4 + 3]);
    }

    [Fact]
    public void 真实前奏标记Sung覆盖仅在对应点elapsed时存在且旧Cue拒绝()
    {
        var snapshot = ProjectSnapshot.FromProject(LeadInFixture.Create()); using SkiaRenderPipeline pipeline = new();
        var request = RenderRequest.At(snapshot, 10_500_000) with { TransparentBackground = true }; var scene = pipeline.GetSceneFrame(snapshot, request.TimeUs);
        Assert.Contains(scene.TextMarks, mark => mark.IsElapsed); Assert.Contains(scene.TextMarks, mark => !mark.IsElapsed);
        foreach (var mark in scene.MarkGeometries)
        {
            using var captured = Capture(pipeline, request, new SceneCaptureTarget.Mark(mark.Target), GraphInputSource.SungCoverage);
            Assert.Equal(scene.TextMarks.Single(m => m.NodeId == mark.Target.NodeId.Value).IsElapsed, Alpha(Bytes(captured)) > 0);
        }
        var old = scene.MarkGeometries[0].Target with { CueId = new("old") };
        Assert.Equal("CaptureTargetUnavailable", Assert.Throws<PreparedFrameException>(() => Capture(pipeline, request, new SceneCaptureTarget.Mark(old))).Code);
    }

    [Fact]
    public void 显式框向外对齐实际密度预算取消及不存在装载均不返回部分图像()
    {
        var snapshot = ProjectSnapshot.FromProject(Single()); using SkiaRenderPipeline pipeline = new(); var request = RenderRequest.At(snapshot, 1_000_000) with { Target = new() { PixelWidth = 320, PixelHeight = 180 } };
        var target = Line(pipeline.GetSceneFrame(snapshot, request.TimeUs));
        using var capture = Capture(pipeline, request, target, bounds: new(-.3, -1.2, 20.4, 30.7));
        Assert.Equal(new GraphRect(-2, -2, 22, 32), capture.Descriptor.Bounds); Assert.Equal(12, capture.Image.Width); Assert.Equal(17, capture.Image.Height);
        Assert.Equal("CaptureBudgetExceeded", Assert.Throws<PreparedFrameException>(() => Capture(pipeline, request, target,
            budget: new() { MaximumBytes = 100 })).Code);
        Assert.Equal("CaptureTargetUnavailable", Assert.Throws<PreparedFrameException>(() => Capture(pipeline, request,
            new SceneCaptureTarget.Line(target.Target with { BindingId = new("old") }))).Code);
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => Capture(pipeline, request with { CancellationToken = cancelled.Token }, target));
        Assert.Equal("UnsupportedCaptureChannel", Assert.Throws<PreparedFrameException>(() => Capture(pipeline, request, target, GraphInputSource.BackgroundMedia)).Code);
    }

    [Fact]
    public void 真实组捕获一次alpha合成与给重叠后代分别乘alpha不同()
    {
        var project = Single(); var line = LyricLine.Create("row", "歌歌", new(0, 5_000_000), TimingStatus.Edited,
            [LyricToken.Create("a", "歌", new(0, 2_000_000), TimingStatus.Edited), LyricToken.Create("b", "歌", new(2_000_000, 4_000_000), TimingStatus.Edited)]);
        var snapshot = ProjectSnapshot.FromProject(project with { Lyrics = LyricsDocument.Create([line]), LyricStyle = project.LyricStyle with
            { UnsungColor = RgbaColor.White, SungColor = RgbaColor.White, Opacity = 1, Stroke = null, Shadow = null } });
        using SkiaRenderPipeline pipeline = new(); var scene = pipeline.GetSceneFrame(snapshot, 1_000_000);
        var a = scene.TokenGeometries[0]; var b = scene.TokenGeometries[1];
        var prepared = new PreparedRenderFrame(snapshot, scene.TimeUs, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition), scene,
            [new(b.WithRuby, Fx.Affine2.Translation(a.BaseGeometry.AdvanceBounds.Left - b.BaseGeometry.AdvanceBounds.Left, 0))]);
        var request = RenderRequest.At(snapshot, scene.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true };
        string root = snapshot.LyricPresentation.WithDefaultPrograms(snapshot.Lyrics).FindProgram("primary")!.RootContainerId;
        using var group = Capture(pipeline, request, new SceneCaptureTarget.Group(new(new(root))));
        using var one = Capture(pipeline, request, new SceneCaptureTarget.Token(a.WithRuby));
        using var two = Capture(pipeline, request, new SceneCaptureTarget.Token(b.WithRuby));
        using var srgb = SKColorSpace.CreateSrgb(); using var isolated = SKSurface.Create(new SKImageInfo(640, 360, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
        using var individual = SKSurface.Create(new SKImageInfo(640, 360, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
        foreach (var surface in new[] { isolated, individual }) { surface.Canvas.ClipRect(new(0, 0, 640, 360)); surface.Canvas.Clear(SKColors.Transparent); }
        using var half = new SKPaint { Color = SKColors.White.WithAlpha(128) };
        isolated.Canvas.DrawImage(group.Image, 0, 0, half); individual.Canvas.DrawImage(one.Image, 0, 0, half); individual.Canvas.DrawImage(two.Image, 0, 0, half);
        using var groupAlpha = isolated.Snapshot(); using var childAlpha = individual.Snapshot();
        using var actual = SKBitmap.FromImage(groupAlpha); using var wrong = SKBitmap.FromImage(childAlpha);
        byte[] groupBytes = Bytes(group), oneBytes = Bytes(one), twoBytes = Bytes(two), actualBytes = actual.Bytes, wrongBytes = wrong.Bytes; int overlap = 0;
        for (int i = 3; i < groupBytes.Length; i += 4) if (groupBytes[i] == 255 && oneBytes[i] == 255 && twoBytes[i] == 255)
        { Assert.Equal(128, actualBytes[i]); Assert.InRange(wrongBytes[i], (byte)191, (byte)192); overlap++; }
        Assert.True(overlap > 100);
    }

    [Fact]
    public void 共享合字能捕获整行但不能独立捕获源元素()
    {
        var line = LyricLine.Create("row", "office", new(0, 6_000_000), TimingStatus.Edited,
            [.. "office".Select((c, i) => LyricToken.Create("t" + i, c.ToString(), new(i * 1_000_000, (i + 1) * 1_000_000), TimingStatus.Edited))]);
        var snapshot = ProjectSnapshot.FromProject(Single() with { Lyrics = LyricsDocument.Create([line]), LyricStyle = LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 56 } });
        using SkiaRenderPipeline pipeline = new(); var scene = pipeline.GetSceneFrame(snapshot, 2_500_000); var request = RenderRequest.At(snapshot, scene.TimeUs);
        using var whole = Capture(pipeline, request, Line(scene)); Assert.True(Alpha(Bytes(whole)) > 100_000);
        var shared = scene.TokenGeometries.First(item => !item.BaseGeometry.CanTransformIndependently);
        Assert.Equal("UnsupportedGlyphPartition", Assert.Throws<PreparedFrameException>(() => Capture(pipeline, request, new SceneCaptureTarget.Token(shared.WithRuby))).Code);
    }

    [Fact]
    public void 共享源覆盖颜色保持正式Render而新描边覆盖不截到64px()
    {
        var line = LyricLine.Create("row", "office", new(0, 6_000_000), TimingStatus.Edited,
            [.. "office".Select((c, i) => LyricToken.Create("t" + i, c.ToString(), new(i * 1_000_000, (i + 1) * 1_000_000), TimingStatus.Edited))]);
        line = line with { Tokens = line.Tokens.SetItem(1, line.Tokens[1] with { StyleOverride = new() { Stroke = StrokeSpec.Create(RgbaColor.White, 100f) } }) };
        var snapshot = ProjectSnapshot.FromProject(Single() with { Lyrics = LyricsDocument.Create([line]),
            LyricStyle = LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 56, Stroke = null, Shadow = null } });
        using SkiaRenderPipeline pipeline = new(); var scene = pipeline.GetSceneFrame(snapshot, 500_000);
        var request = RenderRequest.At(snapshot, scene.TimeUs) with { TransparentBackground = true };
        using var original = pipeline.Render(request); using var color = Capture(pipeline, request, Line(scene));
        Assert.Equal(original.PixelMemory.ToArray(), Bytes(color));
        using var stroke = Capture(pipeline, request, Line(scene), GraphInputSource.StrokeCoverage); byte[] coverage = Bytes(stroke);
        var f = scene.TokenGeometries.Single(item => item.WithRuby.TokenId.Value == "t1");
        Assert.False(f.BaseGeometry.CanTransformIndependently);
        double left = f.BaseGeometry.BaseWorldMatrix.Map(new(f.BaseGeometry.AdvanceBounds.Left, 0)).X;
        int beyond = 0;
        for (int y = 0; y < 360; y++) for (int x = Math.Max(0, (int)left - 98); x < left - 68; x++) if (coverage[(y * 640 + x) * 4 + 3] > 0) beyond++;
        Assert.True(beyond > 100);
    }
}
