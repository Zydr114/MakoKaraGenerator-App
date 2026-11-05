using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class MainCanvasImageEffectsTests
{
    private static readonly GraphRect Bounds = new(0, 0, 640, 360);
    private static readonly Fx.ResourceCatalog Catalog = new("canvas-test", "sha256:" + new string('b', 64), [], ["first", "last"], []);
    private static readonly SceneCaptureRecipe Fill = new(false, false, false, false, SceneCaptureRegion.Fill);
    private static readonly SceneCaptureRecipe Whole = new(true, true, true, false, SceneCaptureRegion.FillAndStroke);
    private static readonly SceneColorPart[] Order = [SceneColorPart.BaseStroke, SceneColorPart.BaseShadow, SceneColorPart.BaseFill, SceneColorPart.RubyStroke, SceneColorPart.RubyShadow, SceneColorPart.RubyFill];
    private static ProjectSnapshot Snapshot(bool decorated = true, bool secondToken = false) => ProjectSnapshot.FromProject(Project.Create("主画布", Composition.Create(640, 360, Core.Time.FrameRate.Fps30, new(25, 40, 60), 5_000_000),
        LyricsDocument.Create([LyricLine.Create("line", secondToken ? "歌詞" : "歌", new(0, 5_000_000), TimingStatus.Edited,
            secondToken ? [LyricToken.Create("token", "歌", new(0, 2_000_000), TimingStatus.Edited), LyricToken.Create("token-2", "詞", new(2_000_000, 4_000_000), TimingStatus.Edited)]
                : [LyricToken.Create("token", "歌", new(0, 4_000_000), TimingStatus.Edited)]) with { RubyAnnotations = [RubyAnnotation.Create("ruby", "token", "か")] }]),
        lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 64, UnsungColor = new(0, 0, 240, 180), SungColor = new(0, 0, 255, 200), Opacity = .7f,
            Stroke = decorated ? StrokeSpec.Create(new(0, 240, 0, 180), 4) : null, Shadow = decorated ? ShadowSpec.Create(new(240, 0, 0, 160), angleDegrees: 36.86989765, distancePx: 10, blurRadiusPx: 2) : null })
        with { EnterAnimation = new() { DurationUs = 2_000_000, StartOpacity = .2f, StartScale = 1, StartOffsetPx = 0, Easing = AnimationEasing.Linear } });
    private static RenderRequest Request(ProjectSnapshot snapshot, bool transparent = true, int width = 640, int height = 360, long time = 1_000_000)
    {
        using var measure = new SkiaRenderPipeline(); var scene = measure.GetSceneFrame(snapshot, time);
        var target = new RenderTarget { PixelWidth = width, PixelHeight = height };
        var prepared = new PreparedRenderFrame(snapshot, time, transparent ? Fx.OutputMode.Transparent : Fx.OutputMode.Opaque, target, scene, []);
        return RenderRequest.At(snapshot, time) with { PreparedFrame = prepared, Target = target, TransparentBackground = transparent };
    }
    private static Fx.ImageEffectTarget.Line Line(RenderRequest request) => new(Assert.Single(request.PreparedFrame!.FinalLineGeometry).Target);
    private static PreparedImageEffectBinding Bind(string id, Fx.ImageEffectTarget target, string graph = "first")
        => new(new(id), "artifact-" + id, new(target, Catalog.GetGraph(graph), Fx.ParameterValues.Empty));
    private static SceneImageGraphProgram Program(string graph, CompiledImageGraph compiled, SceneCaptureRecipe? recipe = null)
        => new(Catalog.GetGraph(graph), compiled, recipe ?? Whole, Bounds);
    private static RenderRequest Bindings(RenderRequest request, params PreparedImageEffectBinding[] bindings)
        => request with { PreparedFrame = request.PreparedFrame!.WithImageEffects([.. bindings]) };
    private static CompiledImageGraph Tint(string id, string expression = "half4(c.rgb*.5,c.a)")
    {
        var shader = new ShaderProgramDefinition(id + ".sksl", "uniform shader color; half4 main(float2 p) { half4 c=color.eval(p); return " + expression + "; }",
            ShaderProgramDefinition.RasterProfile, [], [new("color", ShaderSurfaceKind.Color)]);
        var graph = new ImageGraphDefinition(id, ImageGraphDefinition.CurrentVersion,
            [new("color", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)], [],
            [new ImageGraphNode.RuntimeShader("out", id, ShaderSurfaceKind.Color, [], [new("color", GraphReference.Input("color"))], new(new GraphExpansion.Literal(0), 0, 0, GraphEdgeMode.Transparent))],
            GraphReference.Node("out"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly, GraphCompositeMode.SourceOver);
        return CompiledImageGraph.Compile(ImageGraphCompiler.Compile(graph, [], [new(id, shader)]));
    }
    private static byte[] Pixels(SKImage image) { using var bitmap = SKBitmap.FromImage(image); return bitmap.Bytes; }
    private static void Close(byte[] expected, byte[] actual, int tolerance = 2)
    { Assert.Equal(expected.Length, actual.Length); for (int i = 0; i < actual.Length; i++) Assert.True(Math.Abs(expected[i] - actual[i]) <= tolerance, $"字节 {i}: {expected[i]} != {actual[i]}"); }

    private static byte[] ExpectedParts(RenderRequest request, SceneCaptureRecipe recipe, CompiledImageGraph graph)
    {
        using var capture = new SkiaRenderPipeline(); var line = new SceneCaptureTarget.Line(Line(request).Target);
        List<SceneColorPartResult> parts = [];
        try
        {
            foreach (var part in Order) parts.Add(capture.CaptureSceneColorPart(request, line, part, Bounds, graph.Plan.Budget));
            var selected = parts.Where(part => (part.Part is SceneColorPart.BaseFill || recipe.IncludeRuby && part.Part == SceneColorPart.RubyFill)
                || recipe.IncludeStroke && (part.Part == SceneColorPart.BaseStroke || recipe.IncludeRuby && part.Part == SceneColorPart.RubyStroke)
                || recipe.IncludeShadow && (part.Part == SceneColorPart.BaseShadow || recipe.IncludeRuby && part.Part == SceneColorPart.RubyShadow)).ToArray();
            using var input = capture.AssembleSceneColorParts(request, selected, Bounds, graph.Plan.Budget);
            using var result = graph.Execute(new(request.TimeUs, 640, 360, Bounds, request.ResolveTarget().PixelWidth / 640d, request.ResolveTarget().PixelHeight / 360d, Fx.ParameterValues.Empty, request.TransparentBackground),
                new Dictionary<string, SkiaGraphInput> { ["color"] = new(input.Surface, GraphInputSource.TargetColor) });
            using var srgb = SKColorSpace.CreateSrgb(); var output = request.ResolveTarget();
            using var surface = SKSurface.Create(new SKImageInfo(output.PixelWidth, output.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
            surface.Canvas.ClipRect(new(0, 0, output.PixelWidth, output.PixelHeight)); surface.Canvas.Clear(SKColors.Transparent);
            using var opacity = new SKPaint { Color = SKColors.White.WithAlpha(parts[0].OpacityByte) };
            surface.Canvas.SaveLayer(opacity);
            foreach (var part in parts)
                if (part == selected[^1]) surface.Canvas.DrawImage(result.Output.Image, 0, 0);
                else if (!selected.Contains(part)) surface.Canvas.DrawImage(part.Surface.Image, 0, 0);
            surface.Canvas.Restore(); using var image = surface.Snapshot(); return Pixels(image);
        }
        finally { foreach (var part in parts) part.Dispose(); }
    }

    [Theory]
    [InlineData(false, false, 640, 360)] [InlineData(true, false, 640, 360)]
    [InlineData(false, true, 320, 180)] [InlineData(true, true, 480, 180)]
    public void 主Render自动最高层重组保留真实注音描边阴影并只应用一次共同透明度(bool ruby, bool decorations, int width, int height)
    {
        var request = Request(Snapshot(), width: width, height: height); var recipe = new SceneCaptureRecipe(ruby, decorations, decorations, false, SceneCaptureRegion.FillAndStroke);
        using var graph = Tint("first", "half4(c.b,c.r,c.g,c.a)");
        using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), graph, recipe, Bounds)]);
        using var budget = new ImageSurfaceBudgetSession(); using var renderer = new SkiaRenderPipeline(null, null, budget, resources);
        var actualRequest = Bindings(request, Bind("effect", Line(request)));
        using var actual = renderer.Render(actualRequest);
        Close(ExpectedParts(request, recipe, graph), actual.Pixels.ToArray());
        Assert.Empty(actual.ImageEffectDiagnostics); Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }

    [Fact]
    public void 两个整行图读取前图真实彩色结果并拒绝部分融合范围()
    {
        var request = Request(Snapshot()); var target = Line(request);
        using var first = Tint("first"); using var last = Tint("last"); using var quarter = Tint("quarter", "half4(c.rgb*.25,c.a)");
        using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), first, Whole, Bounds), new(Catalog.GetGraph("last"), last, Whole, Bounds)]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        using var actual = renderer.Render(Bindings(request, Bind("first", target), Bind("last", target, "last")));
        Close(ExpectedParts(request, Whole, quarter), actual.Pixels.ToArray());
        using var partialResources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), first, Whole, Bounds), new(Catalog.GetGraph("last"), last, Fill, Bounds)]);
        using var partial = new SkiaRenderPipeline(null, null, null, partialResources);
        var error = Assert.Throws<EffectFailureException>(() => partial.Render(Bindings(request, Bind("first", target), Bind("last", target, "last"))));
        Assert.Equal("ImageEffectPartialFusion", error.Code); Assert.Equal(new Fx.EffectInstanceId("last"), error.InstanceId); Assert.Equal("artifact-last", error.ArtifactHash);
        Assert.Contains("调整效果顺序或捕获范围", error.InnerException!.Message);
    }

    [Fact]
    public void 不相交部件配方独立生效不同配方可完整包含前图融合()
    {
        var request = Request(Snapshot()); var target = Line(request);
        using var first = Tint("first"); using var last = Tint("last", "half4(c.b,c.r,c.g,c.a)");
        using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), first, Fill, Bounds), new(Catalog.GetGraph("last"), last, Whole, Bounds)]);
        using var budget = new ImageSurfaceBudgetSession(); using var renderer = new SkiaRenderPipeline(null, null, budget, resources);
        using var actual = renderer.Render(Bindings(request, Bind("first", target), Bind("last", target, "last")));
        Assert.Contains(actual.Pixels.ToArray(), value => value != 0); Assert.Equal(0, budget.Usage.ActiveSurfaces);
    }

    [Fact]
    public void 元素效果真实排除原字形注音且后续整行读取元素结果()
    {
        var request = Request(Snapshot(false, true)); var line = Line(request);
        var token = new Fx.ImageEffectTarget.Token(request.PreparedFrame!.FinalGeometry.Single(item => item.WithRuby.TokenId.Value == "token").WithRuby);
        using var first = Tint("first"); using var last = Tint("last");
        using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), first, Whole, Bounds), new(Catalog.GetGraph("last"), last, Whole, Bounds)]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        var actualRequest = Bindings(request, Bind("token-effect", token), Bind("line-effect", line, "last"));
        using var actual = renderer.Render(actualRequest);
        // 独立样式 writer 给该元素降一半亮度，再由单个整行图降一半：应和子图→父图一致。
        var source = request.PreparedFrame!;
        var baseline = StaticStyleProjection.SourceStyles(request.Project)[token.Target.TokenId];
        Fx.MaterialValue Color(RgbaColor c) => Fx.MaterialValue.Solid(new(c.Red / 510d, c.Green / 510d, c.Blue / 510d, c.Alpha / 255d));
        var styled = new PreparedRenderFrame(request.Project, request.TimeUs, source.OutputMode, source.Target, source.Scene, [],
            [new(token.Target, new(Color(baseline.UnsungColor), Color(baseline.SungColor)))]);
        using var expected = renderer.Render(request with { PreparedFrame = styled.WithImageEffects([Bind("only-line", line, "last")]) });
        Close(expected.Pixels.ToArray(), actual.Pixels.ToArray(), 3);
    }

    [Fact]
    public void 元素和整行发生部分融合时按后续真实实例拒绝()
    {
        var request = Request(Snapshot(false, true)); var token = new Fx.ImageEffectTarget.Token(request.PreparedFrame!.FinalGeometry[0].WithRuby);
        using var first = Tint("first"); using var last = Tint("last");
        using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), first, Whole, Bounds), new(Catalog.GetGraph("last"), last, Fill, Bounds)]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        var error = Assert.Throws<EffectFailureException>(() => renderer.Render(Bindings(request, Bind("token", token), Bind("line", Line(request), "last"))));
        Assert.Equal("ImageEffectPartialFusion", error.Code); Assert.Equal(new Fx.EffectInstanceId("line"), error.InstanceId);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 无目标输入图按用户规则替换全部部件且真实下层不含自身(bool transparent)
    {
        var request = Request(Snapshot(), transparent); var line = Line(request);
        var graph = new ImageGraphDefinition("below", ImageGraphDefinition.CurrentVersion,
            [new("below", GraphInputSource.BackdropBelow, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)], [], [],
            GraphReference.Input("below"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly, GraphCompositeMode.SourceOver);
        using var compiled = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(graph, [], []));
        using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), compiled, Whole, Bounds)]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        using var actual = renderer.Render(Bindings(request, Bind("below", line)));
        for (int i = 0; i < actual.Pixels.Length; i += 4)
        {
            Assert.Equal(transparent ? 0 : 255, actual.Pixels[i + 3]);
            if (!transparent) { Assert.Equal(60, actual.Pixels[i]); Assert.Equal(40, actual.Pixels[i + 1]); Assert.Equal(25, actual.Pixels[i + 2]); }
        }
    }

    [Theory]
    [InlineData("budget")] [InlineData("missing")] [InlineData("disposed")]
    public void 资源和预算错误附加真实实例失败后回收所有表面(string kind)
    {
        var request = Request(Snapshot()); var binding = Bind("bad", Line(request));
        using var graph = Tint("first"); using var resources = new SceneImageEffectResources(kind == "missing" ? [] : [new(Catalog.GetGraph("first"), graph, Whole, Bounds)]);
        if (kind == "disposed") resources.Dispose();
        using var budget = new ImageSurfaceBudgetSession(kind == "budget" ? new() { MaximumSurfaces = 2 } : null);
        using var renderer = new SkiaRenderPipeline(null, null, budget, resources);
        var error = Assert.Throws<EffectFailureException>(() => renderer.Render(Bindings(request, binding)));
        Assert.Equal(binding.InstanceId, error.InstanceId); Assert.Equal(binding.ArtifactHash, error.ArtifactHash);
        if (kind == "budget") Assert.Equal("ImageSurfaceBudgetExceeded", error.Code);
        if (kind == "missing") Assert.Equal("ImageEffectResourceUnavailable", error.Code);
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }

    [Fact]
    public void 取消与无图路径保持原入口不消费固定资源()
    {
        var request = Request(Snapshot()); using var graph = Tint("first"); using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), graph, Whole, Bounds)]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources); using var original = new SkiaRenderPipeline();
        using var expected = original.Render(request); using var actual = renderer.Render(request);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => renderer.Render(Bindings(request, Bind("cancel", Line(request))) with { CancellationToken = cancel.Token }));
    }

    [Fact]
    public void 正式组调度消费显式空配方而单绑定入口继续独立拒绝()
    {
        var request = Request(Snapshot());
        string root = request.Project.LyricPresentation.FindProgram("primary")!.RootContainerId;
        using var graph = Tint("first");
        using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), graph, Whole, Bounds)]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);

        // The full main-canvas scheduler now owns group composition. The
        // single-binding ExecuteSceneImageEffect API below stays explicitly gated.
        // Whole has no descendant groups, so this real root receives empty source
        // selection: consume with Info and keep its descendants unchanged.
        using var expected = renderer.Render(request);
        using var actual = renderer.Render(Bindings(request,
            Bind("group", new Fx.ImageEffectTarget.Group(new(new(root))))));
        Close(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray(), 2);
        var diagnostic = Assert.Single(actual.SceneDiagnostics.Where(item => item.Diagnostic.Code == "GroupImageEffectNoSelectedChildren"));
        Assert.Equal(new Fx.EffectInstanceId("group"), diagnostic.InstanceId);
        Assert.Equal(Fx.DiagnosticSeverity.Info, diagnostic.Diagnostic.Severity);
    }

    [Fact]
    public void R3Core单绑定执行入口复用组图明确拒绝()
    {
        var request = Request(Snapshot());
        string root = request.Project.LyricPresentation.FindProgram("primary")!.RootContainerId;
        var binding = Bind("group-direct", new Fx.ImageEffectTarget.Group(new(new(root))));
        using var graph = Tint("first");
        using var renderer = new SkiaRenderPipeline();
        var error = Assert.Throws<EffectFailureException>(() => renderer.ExecuteSceneImageEffect(
            Bindings(request, binding), binding, Program("first", graph), new Dictionary<string, SkiaGraphInput>(), null));

        Assert.Equal("GroupImageEffectUnsupported", error.Code);
        Assert.Equal("ExecuteImageGraph", error.Stage);
        Assert.Equal(binding.InstanceId, error.InstanceId);
    }

    [Fact]
    public void 透明旁路不形成融合限制并返回真实诊断()
    {
        var request = Request(Snapshot());
        var definition = new ImageGraphDefinition("background", ImageGraphDefinition.CurrentVersion,
            [new("media", GraphInputSource.BackgroundMedia, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.Media, GraphCoordinateSpace.Canvas),
                new("color", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)], [], [],
            GraphReference.Input("media"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly, GraphCompositeMode.SourceOver, bypassInput: GraphReference.Input("color"));
        using var first = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [], [])); using var last = Tint("last");
        using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), first, Whole, Bounds), new(Catalog.GetGraph("last"), last, Fill, Bounds)]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        using var actual = renderer.Render(Bindings(request, Bind("bypass", Line(request)), Bind("tint", Line(request), "last")));
        Close(ExpectedParts(request, Fill, last), actual.Pixels.ToArray());
        var diagnostic = Assert.Single(actual.ImageEffectDiagnostics);
        Assert.Equal(new Fx.EffectInstanceId("bypass"), diagnostic.InstanceId); Assert.Equal(Catalog.GetGraph("first"), diagnostic.Graph);
        Assert.Equal("media", diagnostic.InputId); Assert.Equal("TransparentBackgroundDependency", diagnostic.Code);
        var cached = new CachedRenderPipeline(renderer);
        var fixedRequest = Bindings(request, Bind("bypass", Line(request)), Bind("tint", Line(request), "last"));
        using var fresh = cached.Render(fixedRequest); using var hit = cached.Render(fixedRequest);
        Assert.Equal(actual.ImageEffectDiagnostics.ToArray(), fresh.ImageEffectDiagnostics.ToArray()); Assert.Equal(fresh.ImageEffectDiagnostics.ToArray(), hit.ImageEffectDiagnostics.ToArray());
        Assert.Equal(fresh.Pixels.ToArray(), hit.Pixels.ToArray());
    }

    [Fact]
    public void 标记图也由主Render处理且不伪造注音部件()
    {
        var request = Request(ProjectSnapshot.FromProject(LeadInFixture.Create(true, true)), time: 10_000_000);
        var mark = request.PreparedFrame!.FinalMarkGeometry[^1].Target;
        using var first = Tint("first");
        using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), first, Fill, Bounds)]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources); using var original = new SkiaRenderPipeline();
        using var actual = renderer.Render(Bindings(request, Bind("mark", new Fx.ImageEffectTarget.Mark(mark))));
        using var expected = original.Render(request);
        Assert.False(expected.Pixels.SequenceEqual(actual.Pixels));
        var source = request.PreparedFrame;
        var hidden = new PreparedRenderFrame(request.Project, request.TimeUs, source.OutputMode, source.Target, source.Scene, [], [], [], [],
            [new(mark, new(opacityFactor: 0))]);
        using var below = original.Render(request with { PreparedFrame = hidden });
        var captureTarget = new SceneCaptureTarget.Mark(mark);
        using var stroke = original.CaptureSceneColorPart(request, captureTarget, SceneColorPart.BaseStroke, Bounds, first.Plan.Budget);
        using var shadow = original.CaptureSceneColorPart(request, captureTarget, SceneColorPart.BaseShadow, Bounds, first.Plan.Budget);
        using var fill = original.CaptureSceneColorPart(request, captureTarget, SceneColorPart.BaseFill, Bounds, first.Plan.Budget);
        using var result = first.Execute(new(request.TimeUs, 640, 360, Bounds, 1, 1, Fx.ParameterValues.Empty, true),
            new Dictionary<string, SkiaGraphInput> { ["color"] = new(fill.Surface, GraphInputSource.TargetColor) });
        using var srgb = SKColorSpace.CreateSrgb(); var info = new SKImageInfo(640, 360, SKColorType.Bgra8888, SKAlphaType.Premul, srgb);
        using var background = new SKBitmap(info); System.Runtime.InteropServices.Marshal.Copy(below.Pixels.ToArray(), 0, background.GetPixels(), below.Pixels.Length);
        using var surface = SKSurface.Create(info);
        surface.Canvas.ClipRect(new(0, 0, 640, 360)); surface.Canvas.Clear(SKColors.Transparent); surface.Canvas.DrawBitmap(background, 0, 0);
        using var opacity = new SKPaint { Color = SKColors.White.WithAlpha(fill.OpacityByte) }; surface.Canvas.SaveLayer(opacity);
        surface.Canvas.DrawImage(stroke.Surface.Image, 0, 0); surface.Canvas.DrawImage(shadow.Surface.Image, 0, 0); surface.Canvas.DrawImage(result.Output.Image, 0, 0);
        surface.Canvas.Restore(); using var image = surface.Snapshot(); Close(Pixels(image), actual.Pixels.ToArray());
    }

    [Fact]
    public void 固定资源遗漏后一个图时归属后一个实例()
    {
        var request = Request(Snapshot()); using var first = Tint("first");
        using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), first, Whole, Bounds)]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        var last = Bind("missing-last", Line(request), "last");
        var error = Assert.Throws<EffectFailureException>(() => renderer.Render(Bindings(request, Bind("first", Line(request)), last)));
        Assert.Equal("ImageEffectResourceUnavailable", error.Code); Assert.Equal(last.InstanceId, error.InstanceId);
    }

    [Fact]
    public void 同实例自身后置粒子下层捕获在异处采样明确排除()
    {
        var request = OwnedRequest(); var binding = Bind("same", Line(request));
        var prepared = request.PreparedFrame!.WithVisualOutput([new(binding.InstanceId, new("dots"), Fx.OverlayLayer.BehindText,
            [new(new(20, 20), 5, new(1, 0, 0, 1))])], []);
        using var graph = OwnedProbe("below", 480);
        using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), graph, Whole, Bounds)]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        using var actual = renderer.Render(request with { PreparedFrame = prepared.WithImageEffects([binding]) });
        OwnedPixelNear(actual, 484, 20, 0, 0, 0, 0);
        Assert.True(OwnedPixel(actual, 20, 20)[2] > 240);
        Assert.Empty(actual.ImageEffectDiagnostics);
    }

    [Fact]
    public void 真实下层保留已合成歌词及前一标记并排除当前标记()
    {
        var request = Request(ProjectSnapshot.FromProject(LeadInFixture.Create(true, true)), transparent: false, time: 10_000_000);
        var source = request.PreparedFrame!; var mark = source.FinalMarkGeometry[1].Target;
        var definition = new ImageGraphDefinition("below", ImageGraphDefinition.CurrentVersion,
            [new("below", GraphInputSource.BackdropBelow, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)], [], [],
            GraphReference.Input("below"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly, GraphCompositeMode.SourceOver);
        using var graph = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [], []));
        using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), graph, Whole, Bounds)]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources); using var original = new SkiaRenderPipeline();
        using var actual = renderer.Render(Bindings(request, Bind("below", new Fx.ImageEffectTarget.Mark(mark))));
        var hidden = new PreparedRenderFrame(request.Project, request.TimeUs, source.OutputMode, source.Target, source.Scene, [], [], [], [],
            [new(mark, new(opacityFactor: 0))]);
        using var expected = original.Render(request with { PreparedFrame = hidden });
        Close(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }
}
