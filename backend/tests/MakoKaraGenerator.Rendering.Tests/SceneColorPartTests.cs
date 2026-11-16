using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class SceneColorPartTests
{
    private static readonly GraphRect Canvas = new(0, 0, 640, 360);
    private static Project Project(bool ruby = true) => Core.Projects.Project.Create("color parts",
        Composition.Create(640, 360, Core.Time.FrameRate.Fps30, outputDurationUs: 5_000_000),
        LyricsDocument.Create([LyricLine.Create("line", "歌", new(0, 5_000_000), TimingStatus.Edited,
            [LyricToken.Create("token", "歌", new(0, 4_000_000), TimingStatus.Edited)]) with
            { RubyAnnotations = ruby ? [RubyAnnotation.Create("ruby", "token", "か")] : [] }]),
        lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 64, Opacity = .7f,
            UnsungColor = new(0, 0, 240, 180), SungColor = new(0, 0, 255, 200),
            Stroke = StrokeSpec.Create(new(0, 240, 0, 180), 4), Shadow = ShadowSpec.Create(new(240, 0, 0, 160), angleDegrees: 36.86989765, distancePx: 10, blurRadiusPx: 2) }) with {
        EnterAnimation = new() { DurationUs = 2_000_000, StartOpacity = .2f, StartScale = 1, StartOffsetPx = 0, Easing = AnimationEasing.Linear } };
    private static SceneCaptureTarget.Line Target(SceneFrame frame) => new(new(new("line"), new(frame.Lines[0].BindingId!)));
    private static RenderRequest Request(Project project, long time = 1_000_000, int width = 640, int height = 360)
        => RenderRequest.At(ProjectSnapshot.FromProject(project), time) with { TransparentBackground = true, Target = new() { PixelWidth = width, PixelHeight = height } };
    private static SceneColorPartResult Part(SkiaRenderPipeline renderer, RenderRequest request, SceneCaptureTarget target, SceneColorPart part, ImageSurfaceBudgetFrame? budget = null)
        => renderer.CaptureSceneColorPart(request, target, part, Canvas, GraphExecutionBudget.Default, budget);
    private static byte[] Bytes(SkiaGraphSurface surface) => SkiaTestReadback.Pixels(surface.Image);
    private static long Alpha(byte[] bytes) => Enumerable.Range(0, bytes.Length / 4).Sum(i => (long)bytes[i * 4 + 3]);
    private static byte[] Combine(IEnumerable<SceneColorPartResult> parts, bool grouped = true)
    {
        var values = parts.ToArray(); var image = values[0].Surface.Image;
        using var device = SkiaRenderDeviceFactory.Create();
        using var srgb = SKColorSpace.CreateSrgb(); using var output = device.CreateSurface(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
        output.Canvas.ClipRect(new(0, 0, image.Width, image.Height)); output.Canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha(values[0].OpacityByte) };
        if (grouped) output.Canvas.SaveLayer(paint);
        foreach (var value in values)
        {
            var source = value.Surface.ImageForDevice(device, out var raster);
            try { output.Canvas.DrawImage(source, 0, 0, grouped ? null : paint); }
            finally { raster?.Dispose(); }
        }
        if (grouped) output.Canvas.Restore();
        device.FlushAndSubmit(output);
        using var result = output.Snapshot(); return SkiaTestReadback.Pixels(result);
    }
    private static void Close(byte[] expected, byte[] actual, int tolerance = 2)
    { Assert.Equal(expected.Length, actual.Length); for (int i = 0; i < expected.Length; i++) Assert.True(Math.Abs(expected[i] - actual[i]) <= tolerance, $"Pixel component {i}: expected {expected[i]}, actual {actual[i]}, tolerance {tolerance}."); }

    [Theory]
    [InlineData(640, 360)] [InlineData(320, 180)] [InlineData(480, 180)]
    public void 六类真实彩色部件组合后只应用一次入场alpha保持生产像素(int width, int height)
    {
        var project = Project(); var request = Request(project, width: width, height: height); using var renderer = new SkiaRenderPipeline();
        var target = Target(renderer.GetSceneFrame(request.Project, request.TimeUs));
        // 依照生产顺序：基础阴影、描边、填充，再绘制注音（D-P3-9 起阴影在描边之下）。
        SceneColorPart[] order = [SceneColorPart.BaseShadow, SceneColorPart.BaseStroke, SceneColorPart.BaseFill, SceneColorPart.RubyShadow, SceneColorPart.RubyStroke, SceneColorPart.RubyFill];
        List<SceneColorPartResult> parts = [];
        try
        {
            foreach (var kind in order) parts.Add(Part(renderer, request, target, kind));
            Assert.All(parts, part => { Assert.InRange(part.CommonOpacity, .59, .61); Assert.Equal(parts[0].OpacityByte, part.OpacityByte); Assert.True(Alpha(Bytes(part.Surface)) > 0); Assert.Equal(width / 640d, part.Surface.Descriptor.DensityX); Assert.True(part.Surface.Image.ColorSpace!.IsSrgb); });
            using var complete = renderer.CaptureSceneTarget(request, target, new(true, true, true, false, SceneCaptureRegion.FillAndStroke), GraphInputSource.TargetColor, Canvas, GraphExecutionBudget.Default);
            Close(Bytes(complete), Combine(parts));
            Assert.False(Combine(parts).SequenceEqual(Combine(parts, false))); // 每部件分别应用 alpha 会改变重叠。
        }
        finally { foreach (var part in parts) part.Dispose(); }
    }

    [Theory]
    [InlineData(SceneColorPart.BaseFill, 0)] [InlineData(SceneColorPart.BaseStroke, 1)] [InlineData(SceneColorPart.BaseShadow, 2)]
    [InlineData(SceneColorPart.RubyFill, 0)] [InlineData(SceneColorPart.RubyStroke, 1)] [InlineData(SceneColorPart.RubyShadow, 2)]
    public void 部件保留真实填充描边阴影颜色而不是白色覆盖(SceneColorPart kind, int channel)
    {
        using var renderer = new SkiaRenderPipeline(); var request = Request(Project());
        using var part = Part(renderer, request, Target(renderer.GetSceneFrame(request.Project, request.TimeUs)), kind);
        byte[] bytes = Bytes(part.Surface); int visible = 0;
        for (int i = 0; i < bytes.Length; i += 4)
        {
            if (bytes[i + 3] < 3) continue; visible++;
            Assert.True(bytes[i + channel] > 0); Assert.Equal(0, bytes[i + (channel + 1) % 3]); Assert.Equal(0, bytes[i + (channel + 2) % 3]);
        }
        Assert.True(visible > 20);
    }

    [Fact]
    public void 没有装饰或注音是合法空部件共同透明度不被吞掉()
    {
        var project = Project(false); var request = Request(project with { LyricStyle = project.LyricStyle with { Stroke = null, Shadow = null } });
        using var renderer = new SkiaRenderPipeline(); var target = Target(renderer.GetSceneFrame(request.Project, request.TimeUs));
        foreach (var kind in Enum.GetValues<SceneColorPart>().Where(kind => kind != SceneColorPart.BaseFill))
        {
            using var part = Part(renderer, request, target, kind); Assert.Equal(0, Alpha(Bytes(part.Surface))); Assert.InRange(part.CommonOpacity, .59, .61);
        }
        using var fill = Part(renderer, request, target, SceneColorPart.BaseFill); Assert.True(Alpha(Bytes(fill.Surface)) > 10_000);
    }

    [Fact]
    public void 静态与脚本族外观变换使用同一固定分区不泄漏邻字且缺时间仍保持未唱()
    {
        var project = Project(); var original = project.Lyrics.AllLines.Single();
        var line = original with { Text = "歌詞歌", Tokens = [original.Tokens[0] with { Text = "歌", Time = new(0, 1_000_000) },
            LyricToken.Create("missing", "詞"), LyricToken.Create("later", "歌", new(2_000_000, 3_000_000), TimingStatus.Edited)] };
        project = project with { Lyrics = LyricsDocument.Create([line]), EnterAnimation = null };
        var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = "parts" }; using var renderer = new SkiaRenderPipeline(); var scene = renderer.GetSceneFrame(snapshot, 2_500_000);
        var token = scene.TokenGeometries.Single(item => item.WithRuby.TokenId.Value == "missing");
        var prepared = new PreparedRenderFrame(snapshot, scene.TimeUs, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition), scene,
            [new(token.WithRuby, Fx.Affine2.Translation(12, -8))], [new(token.WithRuby, TestStyle.Update(Fx.MaterialValue.Solid(new(.1, .8, .2, .6)), Fx.MaterialValue.Solid(new(1, 0, 0, 1)), opacityFactor: .4))]);
        var request = RenderRequest.At(snapshot, scene.TimeUs) with { TransparentBackground = true, PreparedFrame = prepared };
        var target = new SceneCaptureTarget.Token(token.WithRuby); using var part = Part(renderer, request, target, SceneColorPart.BaseFill);
        using var reference = renderer.CaptureSceneTarget(request, target, new(false, false, false, false, SceneCaptureRegion.Fill), GraphInputSource.TargetColor, Canvas, GraphExecutionBudget.Default);
        Assert.Equal(Bytes(reference), Bytes(part.Surface)); Assert.Equal(1, part.CommonOpacity);
        byte[] pixels = Bytes(part.Surface); int visible = 0;
        for (int i = 0; i < pixels.Length; i += 4) if (pixels[i + 3] > 4) { visible++; Assert.True(pixels[i + 1] > pixels[i + 2]); }
        Assert.True(visible > 20); Assert.Same(project.Lyrics, snapshot.Lyrics);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 前奏标记共同透明度包含真实静态与脚本alpha附着一次而不重复烘入部件(bool fixedParent)
    {
        var project = LeadInFixture.Create(true); var tree = project.LyricPresentation; var program = tree.FindProgram("primary")!;
        var group = tree.FindNode(program.LeadIns[0].IndicatorContainerId)!; var sequence = (CountdownSequenceContent)group.Content!;
        tree = tree with { Nodes = tree.Nodes.Replace(group, group with { Placement = fixedParent ? ScenePlacement.Create(new(.5, .5)) : group.Placement, Attachment = new() { Mode = fixedParent ? SceneAttachmentMode.FixedParent : SceneAttachmentMode.CueTargetText },
            Content = sequence with { Appearance = sequence.Appearance with { Opacity = .5f, Stroke = StrokeSpec.Create(new(0, 255, 0), 3), Shadow = ShadowSpec.Create(new(255, 0, 0), angleDegrees: 45, distancePx: 6, blurRadiusPx: 2) } } }),
            LineOverrides = tree.LineOverrides.Add(new() { BranchId = "primary", LineId = "first", EnterAnimation = new() { DurationUs = 2_000_000, StartOpacity = 0, StartScale = 1, StartOffsetPx = 0, Easing = AnimationEasing.Linear } }) };
        var snapshot = ProjectSnapshot.FromProject(project with { LyricPresentation = tree }) with { EffectInputsFingerprint = "mark parts" };
        using var renderer = new SkiaRenderPipeline(); var scene = renderer.GetSceneFrame(snapshot, 10_000_000); var target = new SceneCaptureTarget.Mark(scene.MarkGeometries[0].Target);
        var prepared = new PreparedRenderFrame(snapshot, scene.TimeUs, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition), scene, [], [], [],
            markStyles: [new(target.Target, new Fx.StyleUpdate(opacityFactor: .5))]);
        var request = RenderRequest.At(snapshot, scene.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true };
        using var fill = Part(renderer, request, target, SceneColorPart.BaseFill); using var stroke = Part(renderer, request, target, SceneColorPart.BaseStroke); using var shadow = Part(renderer, request, target, SceneColorPart.BaseShadow);
        Assert.True(Alpha(Bytes(fill.Surface)) > 0); Assert.True(Alpha(Bytes(stroke.Surface)) > 0); Assert.True(Alpha(Bytes(shadow.Surface)) > 0);
        Assert.Equal(fixedParent ? .25 : .125, fill.CommonOpacity, 6); Assert.Equal(fill.OpacityByte, stroke.OpacityByte);
        using var full = renderer.CaptureSceneTarget(request, target, new(false, true, true, false, SceneCaptureRegion.FillAndStroke), GraphInputSource.TargetColor, Canvas, GraphExecutionBudget.Default);
        // 阴影以整体轮廓为来源并在描边之下（D-P3-9）。
        Close(Bytes(full), Combine([shadow, stroke, fill]));
        Assert.False(Combine([shadow, stroke, fill]).SequenceEqual(Combine([shadow, stroke, fill], false)));
        Assert.Equal("UnsupportedSceneColorPartTarget", Assert.Throws<PreparedFrameException>(() => Part(renderer, request, target, SceneColorPart.RubyFill)).Code);
    }

    [Fact]
    public void 独立字簇和真实装载校验以及预算失败取消释放均保持()
    {
        var project = Project(); using var ledger = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 1 }); using var frame = ledger.BeginFrame();
        using var renderer = new SkiaRenderPipeline(surfaceBudget: ledger); var request = Request(project); var scene = renderer.GetSceneFrame(request.Project, request.TimeUs); var target = Target(scene);
        Assert.Throws<ImageSurfaceBudgetException>(() => Part(renderer, request, target, SceneColorPart.BaseStroke, frame)); Assert.Equal(0, ledger.Usage.ActiveSurfaces); Assert.Equal(0, ledger.Usage.EstimatedBytes);
        using var fill = Part(renderer, request, target, SceneColorPart.BaseFill, frame); Assert.Equal(1, ledger.Usage.ActiveSurfaces); fill.Dispose(); fill.Dispose(); Assert.Equal(0, ledger.Usage.ActiveSurfaces);
        Assert.Throws<ObjectDisposedException>(() => fill.Surface);
        Assert.Equal("InvalidSceneColorPart", Assert.Throws<PreparedFrameException>(() => Part(renderer, request, target, (SceneColorPart)999)).Code);
        Assert.Equal("CaptureTargetUnavailable", Assert.Throws<PreparedFrameException>(() => Part(renderer, request, new SceneCaptureTarget.Line(target.Target with { BindingId = new("old") }), SceneColorPart.BaseFill)).Code);
        Assert.Equal("UnsupportedSceneColorPartTarget", Assert.Throws<PreparedFrameException>(() => Part(renderer, request, new SceneCaptureTarget.Group(new(new("root"))), SceneColorPart.BaseFill)).Code);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); Assert.Throws<OperationCanceledException>(() => Part(renderer, request with { CancellationToken = cancelled.Token }, target, SceneColorPart.BaseFill));
        Assert.Equal(0, ledger.Usage.ActiveSurfaces);
    }

    [Theory]
    [InlineData(SceneColorPart.BaseFill)] [InlineData(SceneColorPart.RubyFill)]
    public void 真实渐变与alpha取色保持填充部件和生产裁切一致(SceneColorPart kind)
    {
        var project = Project(); var gradient = new GradientSpec { Stops = [new(0, new(20, 70, 230, 80)), new(1, new(220, 40, 50, 220))] };
        project = project with { EnterAnimation = null, LyricStyle = project.LyricStyle with
            { UnsungFill = project.LyricStyle.UnsungFill with { Gradient = gradient }, SungFill = project.LyricStyle.SungFill with { Gradient = gradient } } };
        using var renderer = new SkiaRenderPipeline(); var request = Request(project); var target = Target(renderer.GetSceneFrame(request.Project, request.TimeUs));
        using var part = Part(renderer, request, target, kind); Assert.True(Alpha(Bytes(part.Surface)) > 0);
        if (kind == SceneColorPart.BaseFill)
        {
            using var reference = renderer.CaptureSceneTarget(request, target, new(false, false, false, false, SceneCaptureRegion.Fill), GraphInputSource.TargetColor, Canvas, GraphExecutionBudget.Default);
            Assert.Equal(Bytes(reference), Bytes(part.Surface));
        }
        using var plain = Part(renderer, Request(project with { LyricStyle = project.LyricStyle with
            { UnsungFill = project.LyricStyle.UnsungFill with { Gradient = null }, SungFill = project.LyricStyle.SungFill with { Gradient = null } } }),
            target, kind);
        Assert.False(Bytes(part.Surface).SequenceEqual(Bytes(plain.Surface)));
    }

    [Fact]
    public void 共享合字可捕获行部件但独立源字仍明确失败()
    {
        var line = LyricLine.Create("line", "office", new(0, 6_000_000), TimingStatus.Edited,
            [.. "office".Select((c, i) => LyricToken.Create("t" + i, c.ToString(), new(i * 1_000_000, (i + 1) * 1_000_000), TimingStatus.Edited))]);
        var project = Project(false) with { Lyrics = LyricsDocument.Create([line]), LyricStyle = LyricStyle.Default with { FontFamilyName = TestFonts.LigatureFfi, FontSizePx = 56 } };
        using var renderer = new SkiaRenderPipeline(); var request = Request(project, 2_500_000); var scene = renderer.GetSceneFrame(request.Project, request.TimeUs);
        using var whole = Part(renderer, request, Target(scene), SceneColorPart.BaseFill); Assert.True(Alpha(Bytes(whole.Surface)) > 100_000);
        var shared = scene.TokenGeometries.First(item => !item.BaseGeometry.CanTransformIndependently);
        Assert.Equal("UnsupportedGlyphPartition", Assert.Throws<PreparedFrameException>(() => Part(renderer, request, new SceneCaptureTarget.Token(shared.WithRuby), SceneColorPart.BaseFill)).Code);
    }

    [Fact]
    public void 显式重组保持原颜色尚未应用共同alpha且产物独立于输入生命周期()
    {
        using var ledger = new ImageSurfaceBudgetSession(); using var renderer = new SkiaRenderPipeline(surfaceBudget: ledger);
        var request = Request(Project()); var target = Target(renderer.GetSceneFrame(request.Project, request.TimeUs));
        using var fill = Part(renderer, request, target, SceneColorPart.BaseFill); using var stroke = Part(renderer, request, target, SceneColorPart.BaseStroke); using var shadow = Part(renderer, request, target, SceneColorPart.BaseShadow);
        using var assembled = renderer.AssembleSceneColorParts(request, [shadow, stroke, fill], Canvas, GraphExecutionBudget.Default);
        Assert.Equal(target, assembled.Target); Assert.Equal(request.TimeUs, assembled.TimeUs); Assert.Equal(fill.CommonOpacity, assembled.CommonOpacity); Assert.Equal(fill.OpacityByte, assembled.OpacityByte);
        Assert.Equal(new[] { SceneColorPart.BaseShadow, SceneColorPart.BaseStroke, SceneColorPart.BaseFill }, assembled.Parts);
        var expectedRequest = request with { Project = request.Project with { EnterAnimation = null } };
        using var expected = renderer.CaptureSceneTarget(expectedRequest, target, new(false, true, true, false, SceneCaptureRegion.FillAndStroke), GraphInputSource.TargetColor, Canvas, GraphExecutionBudget.Default);
        Close(Bytes(expected), Bytes(assembled.Surface)); expected.Dispose();
        byte[] bytes = Bytes(assembled.Surface); fill.Dispose(); stroke.Dispose(); shadow.Dispose();
        Assert.Equal(bytes, Bytes(assembled.Surface)); Assert.Equal(1, ledger.Usage.ActiveSurfaces);
        assembled.Dispose(); assembled.Dispose(); Assert.Equal(0, ledger.Usage.ActiveSurfaces); Assert.Equal(0, ledger.Usage.EstimatedBytes);
        Assert.Throws<ObjectDisposedException>(() => assembled.Surface);
    }

    [Theory]
    [InlineData(640, 360)] [InlineData(480, 180)]
    public void 部件在创建管线释放后可在独立设备重组并保留原图(int width, int height)
    {
        using var source = new SkiaRenderPipeline();
        using var destination = new SkiaRenderPipeline();
        var request = Request(Project(), width: width, height: height); var target = Target(source.GetSceneFrame(request.Project, request.TimeUs));
        using var fill = Part(source, request, target, SceneColorPart.BaseFill);
        using var stroke = Part(source, request, target, SceneColorPart.BaseStroke);
        using var expected = source.AssembleSceneColorParts(request, [stroke, fill], Canvas, GraphExecutionBudget.Default);
        var expectedBytes = Bytes(expected.Surface); var fillBytes = Bytes(fill.Surface);
        source.Dispose();
        for (int i = 0; i < 3; i++)
        {
            using var assembled = destination.AssembleSceneColorParts(request, [stroke, fill], Canvas, GraphExecutionBudget.Default);
            Close(expectedBytes, Bytes(assembled.Surface));
            Assert.Equal(fillBytes, Bytes(fill.Surface));
        }
    }

    [Theory]
    [InlineData("time")] [InlineData("quality")] [InlineData("source")] [InlineData("mode")] [InlineData("size")] [InlineData("layout")]
    public void 重组禁止混合不同源时刻质量输出与布局请求(string difference)
    {
        using var renderer = new SkiaRenderPipeline(); var request = Request(Project()); var target = Target(renderer.GetSceneFrame(request.Project, request.TimeUs));
        using var fill = Part(renderer, request, target, SceneColorPart.BaseFill);
        var changed = difference switch
        {
            "time" => request with { TimeUs = request.TimeUs + 1 }, "quality" => request with { Quality = RenderQuality.Draft },
            "source" => request with { Project = request.Project with { Name = "other" } }, "mode" => request with { TransparentBackground = false },
            "size" => request with { Target = new() { PixelWidth = 320, PixelHeight = 180 } }, "layout" => request with { LayoutLineId = "line" }, _ => request,
        };
        Assert.Equal("SceneColorPartFrameMismatch", Assert.Throws<PreparedFrameException>(() => renderer.AssembleSceneColorParts(changed, [fill], Canvas, GraphExecutionBudget.Default)).Code);
    }

    [Fact]
    public void 重组前拒绝重复目标混用裁切空输入并在分配前检查峰值预算()
    {
        using var ledger = new ImageSurfaceBudgetSession(); using var renderer = new SkiaRenderPipeline(surfaceBudget: ledger);
        var request = Request(Project()); var scene = renderer.GetSceneFrame(request.Project, request.TimeUs); var target = Target(scene);
        using var fill = Part(renderer, request, target, SceneColorPart.BaseFill); using var stroke = Part(renderer, request, target, SceneColorPart.BaseStroke);
        Assert.Equal("SceneColorPartsEmpty", Assert.Throws<PreparedFrameException>(() => renderer.AssembleSceneColorParts(request, [], Canvas, GraphExecutionBudget.Default)).Code);
        Assert.Equal("SceneColorPartDuplicate", Assert.Throws<PreparedFrameException>(() => renderer.AssembleSceneColorParts(request, [fill, fill], Canvas, GraphExecutionBudget.Default)).Code);
        Assert.Equal("SceneColorPartBoundsExceeded", Assert.Throws<PreparedFrameException>(() => renderer.AssembleSceneColorParts(request, [fill], new(0, 0, 320, 180), GraphExecutionBudget.Default)).Code);
        using var ruby = Part(renderer, request, new SceneCaptureTarget.Token(scene.TokenGeometries[0].WithRuby), SceneColorPart.RubyFill);
        Assert.Equal("SceneColorPartFrameMismatch", Assert.Throws<PreparedFrameException>(() => renderer.AssembleSceneColorParts(request, [fill, ruby], Canvas, GraphExecutionBudget.Default)).Code);
        ruby.Dispose(); using var frame = ledger.BeginFrame();
        Assert.Equal("SceneColorPartsBudgetExceeded", Assert.Throws<PreparedFrameException>(() => renderer.AssembleSceneColorParts(request, [stroke, fill], Canvas,
            GraphExecutionBudget.Default with { MaximumSurfaces = 2 }, frame)).Code);
        Assert.Equal(2, frame.Usage.PeakSurfaces); Assert.Equal(0, frame.Usage.ActiveSurfaces); Assert.Equal(2, ledger.Usage.ActiveSurfaces);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => renderer.AssembleSceneColorParts(request with { CancellationToken = cancelled.Token }, [fill], Canvas, GraphExecutionBudget.Default));
        Assert.Equal(2, ledger.Usage.ActiveSurfaces);
    }
}
