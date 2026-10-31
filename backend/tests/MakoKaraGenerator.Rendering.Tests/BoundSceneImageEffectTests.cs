using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class BoundSceneImageEffectTests
{
    private const string Fingerprint = "bound-production-image";
    private static readonly Fx.ResourceCatalog Resources = new("image-probe", "sha256:" + new string('a', 64), [], ["unused", "image"], []);
    private static readonly Lazy<Task<ScriptCompilationResult>> Script = new(async () =>
    {
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var result = await compiler.CompileAsync([new("src/ImageProbe.cs", """
            using MakoKaraGenerator.Scripting;
            public sealed class ImageProbe : EffectBase
            {
                public override void Evaluate(FrameContext frame, FrameWriter output)
                {
                    var first = frame.Resources.GetGraph("unused"); var last = frame.Resources.GetGraph("image");
                    var amount = new ParameterValues(new[] { System.Collections.Generic.KeyValuePair.Create<string, ParameterValue>("amount", new ParameterValue.Number(frame.Parameters.GetNumber("amount"))) });
                    string mode = frame.Parameters.GetEnum("target");
                    if (mode == "line") foreach (var line in frame.Presentations.LinesInBinding)
                    { output.ImageEffects.Bind(line.Target, first, ParameterValues.Empty); output.ImageEffects.Bind(line.Target, last, amount); }
                    if (mode == "token") foreach (var token in frame.Presentations.TokensInBinding)
                    { output.ImageEffects.Bind(token.WithRuby, first, ParameterValues.Empty); output.ImageEffects.Bind(token.WithRuby, last, amount); }
                    if (mode == "mark") foreach (var mark in frame.Presentations.MarksInBinding)
                    { output.ImageEffects.Bind(mark.Target, first, ParameterValues.Empty); output.ImageEffects.Bind(mark.Target, last, amount); }
                }
                public override void EvaluateVisuals(VisualFrameContext frame, VisualWriter output)
                    => output.Overlay.DrawDots(new("proof"), [new(new(2, 2), 1, new(1, 0, 0, 1))], OverlayLayer.AboveText);
            }
            """)]);
        Assert.True(result.Succeeded, string.Join('\n', result.Diagnostics)); return result;
    });
    private static async Task<PureFrameSourceFactory> Factory(string target, double[] amounts, Fx.EffectBinding? binding = null)
    {
        var artifact = await Script.Value;
        return new([.. amounts.Select((amount, index) => new CompiledFrameEffect(new("image-" + index), artifact, "ImageProbe",
            new([new("amount", new Fx.ParameterValue.Number(amount)), new("target", new Fx.ParameterValue.Choice(target))]),
            binding ?? new Fx.EffectBinding.Project(), Fx.EffectCapabilities.ImageEffects | Fx.EffectCapabilities.Overlay) { Resources = Resources })],
            Fingerprint, static () => new SkiaRenderPipeline());
    }
    private static ProjectSnapshot Snapshot(bool marks = false)
    {
        var project = marks ? LeadInFixture.Create(true, true) : Project.Create("image graph", Composition.Create(640, 360, Core.Time.FrameRate.Fps30, outputDurationUs: 5_000_000),
            LyricsDocument.Create([LyricLine.Create("row", "歌", new(0, 5_000_000), TimingStatus.Edited,
                [LyricToken.Create("token", "歌", new(0, 4_000_000), TimingStatus.Edited)]) with { RubyAnnotations = [RubyAnnotation.Create("ruby", "token", "か")] }]),
            lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 64, Stroke = null, Shadow = null, Opacity = .7f });
        return ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
    }
    private static CompiledImageGraph Graph(GraphExecutionBudget? budget = null)
    {
        var program = new ShaderProgramDefinition("image.sksl", "uniform shader target; uniform float amount; half4 main(float2 p) { return target.eval(p)*amount; }",
            ShaderProgramDefinition.RasterProfile, [new("amount", ShaderUniformKind.Float)], [new("target", ShaderSurfaceKind.Color)]);
        var definition = new ImageGraphDefinition("image", ImageGraphDefinition.CurrentVersion,
            [new("target", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)], ["amount"],
            [new ImageGraphNode.RuntimeShader("multiply", "image", ShaderSurfaceKind.Color, [new("amount", new GraphUniformBinding.Parameter("amount"))],
                [new("target", GraphReference.Input("target"))], new(new GraphExpansion.Literal(0), 0, 0, GraphEdgeMode.Transparent))],
            GraphReference.Node("multiply"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly, GraphCompositeMode.SourceOver);
        return CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [new EffectParameterConstraint.Number("amount", 0, 1)], [new("image", program)], budget));
    }
    private static SceneImageGraphProgram Program(CompiledImageGraph graph) => new(Resources.GetGraph("image"), graph, new(true, true, true, true, SceneCaptureRegion.FillAndStroke), new(0, 0, 640, 360));
    private static byte[] Bytes(SkiaGraphSurface surface) { using var bitmap = SKBitmap.FromImage(surface.Image); return bitmap.Bytes; }

    [Fact]
    public async Task 包内同来源不同配方实际捕获注音差分且不会错误别名复用()
    {
        var programJson = EffectPackageShaderFixture.Program(); programJson["uniforms"] = new System.Text.Json.Nodes.JsonArray();
        programJson["children"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["name"] = "a", ["type"] = "color" },
            new System.Text.Json.Nodes.JsonObject { ["name"] = "b", ["type"] = "color" });
        var json = EffectPackageGraphFixture.Graph(); var second = EffectPackageGraphFixture.Input("base"); second["capture"] = EffectPackageGraphFixture.Capture(false);
        json["inputs"]!.AsArray().Add(second); json["nodes"]![0]!["uniforms"] = new System.Text.Json.Nodes.JsonObject();
        json["nodes"]![0]!["children"] = new System.Text.Json.Nodes.JsonObject { ["a"] = "input:original", ["b"] = "input:base" };
        var package = EffectPackageImageGraphs.Read(EffectPackageGraphFixture.Contents(json, programJson,
            "uniform shader a; uniform shader b; half4 main(float2 p) { return max(a.eval(p)-b.eval(p),half4(0)); }"))[EffectPackageGraphFixture.GraphPath];
        using var graph = CompiledImageGraph.Compile(package.Plan); var bounds = new GraphRect(0, 0, 640, 360);
        var program = SceneImageGraphProgram.FromPackage(Resources.GetGraph("image"), graph, package, bounds);
        var snapshot = Snapshot(); await using var session = await (await Factory("line", [1, 1])).CreateAsync(snapshot);
        var prepared = await session.PrepareAsync(1_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        var request = RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true };
        using var budget = new ImageSurfaceBudgetSession(); using var frame = budget.BeginFrame(); using var pipeline = new SkiaRenderPipeline(surfaceBudget: budget);
        using var result = pipeline.ExecuteSceneImageEffect(request, prepared.ImageEffects[0], program, new Dictionary<string, SkiaGraphInput>(), null, frame);
        var pending = Assert.Throws<EffectFailureException>(() => pipeline.ExecuteSceneImageEffect(request, prepared.ImageEffects[1], program,
            new Dictionary<string, SkiaGraphInput>(), result.GraphResult.Output, frame));
        Assert.Equal("ImageEffectCaptureReassemblyPending", pending.Code); Assert.Equal(prepared.ImageEffects[1].InstanceId, pending.InstanceId);
        Assert.Contains(Bytes(result.GraphResult.Output), value => value != 0);
        var target = new SceneCaptureTarget.Line(Assert.Single(prepared.FinalLineGeometry).Target);
        using var a = pipeline.CaptureSceneTarget(request, target, program.GetInputRecipe("original"), GraphInputSource.TargetColor, bounds, graph.Plan.Budget);
        using var b = pipeline.CaptureSceneTarget(request, target, program.GetInputRecipe("base"), GraphInputSource.TargetColor, bounds, graph.Plan.Budget);
        using var expected = graph.Execute(new(request.TimeUs, 640, 360, bounds, 1, 1, Fx.ParameterValues.Empty, true),
            new Dictionary<string, SkiaGraphInput> { ["original"] = new(a, GraphInputSource.TargetColor), ["base"] = new(b, GraphInputSource.TargetColor) });
        Assert.Equal(Bytes(expected.Output), Bytes(result.GraphResult.Output)); Assert.Equal(3, result.GraphResult.PeakSurfaces);
        expected.Dispose(); a.Dispose(); b.Dispose(); result.Dispose(); Assert.Equal(0, budget.Usage.ActiveSurfaces);
    }

    [Fact]
    public async Task 真实实例合成链遵守完整目标顺序错误和预算附加真实最后实例()
    {
        var snapshot = Snapshot(); await using var session = await (await Factory("line", [1, .5])).CreateAsync(snapshot);
        var prepared = await session.PrepareAsync(1_000_000, Fx.OutputMode.Opaque, RenderTarget.FromComposition(snapshot.Composition));
        var request = RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared };
        using var pipeline = new SkiaRenderPipeline(); using var graph = Graph(); var program = Program(graph);
        using var first = pipeline.ExecuteSceneImageEffect(request, prepared.ImageEffects[0], program, new Dictionary<string, SkiaGraphInput>(), null);
        using var last = pipeline.ExecuteSceneImageEffect(request, prepared.ImageEffects[1], program, new Dictionary<string, SkiaGraphInput>(), first.GraphResult.Output);
        using var below = pipeline.CaptureSceneBackground(request, GraphInputSource.BackgroundColor, program.Bounds, graph.Plan.Budget)!;
        using var combined = pipeline.CompositeSceneImageEffectChain(request, [first, last], program, below);
        using var expected = graph.Composite(last.GraphResult, below); Assert.Equal(Bytes(expected), Bytes(combined));
        var order = Assert.Throws<EffectFailureException>(() => pipeline.CompositeSceneImageEffectChain(request, [last, first], program, below));
        Assert.Equal("ImageEffectChainBindingMismatch", order.Code); Assert.Equal(first.Binding.InstanceId, order.InstanceId);
        var missing = Assert.Throws<EffectFailureException>(() => pipeline.CompositeSceneImageEffectChain(request, [last], program, below));
        Assert.Equal("ImageEffectChainBindingMismatch", missing.Code); Assert.Equal(last.Binding.InstanceId, missing.InstanceId);
        using var tiny = new ImageSurfaceBudgetSession(new() { MaximumBytes = 1 }); using var frame = tiny.BeginFrame();
        var error = Assert.Throws<EffectFailureException>(() => pipeline.CompositeSceneImageEffectChain(request, [first, last], program, below, frame));
        Assert.Equal("ImageSurfaceBudgetExceeded", error.Code); Assert.Equal("CompositeImageGraph", error.Stage);
        Assert.Equal(last.Binding.InstanceId, error.InstanceId); Assert.Equal(last.Binding.ArtifactHash, error.ArtifactHash); Assert.Equal(prepared.TimeUs, error.TimeUs);
        Assert.Equal(0, tiny.Usage.ActiveSurfaces); Assert.True(last.GraphResult.Output.Image.Handle != nint.Zero);
    }

    [Fact]
    public async Task 真实绑定自动读取工程媒体并按来源去重别名透明模式不解码()
    {
        using var resolver = new MediaResolver(); using var budget = new ImageSurfaceBudgetSession();
        using var pipeline = new SkiaRenderPipeline(backgroundResolver: resolver, surfaceBudget: budget);
        var snapshot = Snapshot() with { Background = BackgroundSource.ForVideo("/tmp/media-probe.mp4") };
        await using var session = await (await Factory("line", [1])).CreateAsync(snapshot);
        var target = RenderTarget.FromComposition(snapshot.Composition);
        var prepared = await session.PrepareAsync(1_000_000, Fx.OutputMode.Opaque, target);
        var request = RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared };
        var definition = new ImageGraphDefinition("media-aliases", ImageGraphDefinition.CurrentVersion,
            [new("a", GraphInputSource.BackgroundMedia, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.Media, GraphCoordinateSpace.Canvas),
             new("b", GraphInputSource.BackgroundMedia, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.Media, GraphCoordinateSpace.Canvas),
             new("target", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)], [],
            [new ImageGraphNode.Blend("blend", GraphReference.Input("a"), GraphReference.Input("b"), GraphBlendMode.SourceOver,
                new(new GraphExpansion.Literal(0), 0, 0, GraphEdgeMode.Transparent))], GraphReference.Node("blend"), GraphCoordinateSpace.Canvas, 0,
            GraphAnchorMode.ColorOnly, GraphCompositeMode.SourceOver, bypassInput: GraphReference.Input("target"));
        using var graph = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [], [], new() { MaximumSurfaces = 2, MaximumBytes = 640 * 360 * 4 * 2 }));
        using var result = pipeline.ExecuteSceneImageEffect(request, Assert.Single(prepared.ImageEffects), Program(graph), new Dictionary<string, SkiaGraphInput>(), null);
        Assert.Equal(1, resolver.Calls); Assert.Equal(request.TimeUs, resolver.Time); Assert.Equal(2, result.GraphResult.PeakSurfaces);
        using var image = SKBitmap.FromImage(result.GraphResult.Output.Image); Assert.Equal(SKColors.Red, image.GetPixel(320, 180));
        result.Dispose(); Assert.Equal(0, budget.Usage.ActiveSurfaces);
        prepared = await session.PrepareAsync(request.TimeUs, Fx.OutputMode.Transparent, target);
        using var transparent = pipeline.ExecuteSceneImageEffect(request with { PreparedFrame = prepared, TransparentBackground = true }, Assert.Single(prepared.ImageEffects), Program(graph), new Dictionary<string, SkiaGraphInput>(), null);
        Assert.Equal(1, resolver.Calls); Assert.Equal(2, transparent.GraphResult.Diagnostics.Length); Assert.Null(transparent.GraphResult.Coverage);
        transparent.Dispose(); Assert.Equal(0, budget.Usage.ActiveSurfaces);
    }

    private sealed class MediaResolver : IBackgroundFrameSourceResolver, IBackgroundFrameSource
    {
        private readonly SKBitmap _bitmap = new(32, 32);
        public MediaResolver() => _bitmap.Erase(SKColors.Red);
        public int Calls { get; private set; }
        public long Time { get; private set; }
        public IBackgroundFrameSource Resolve(BackgroundSource background) { Calls++; return this; }
        public SKBitmap GetFrame(long timeUs) { Time = timeUs; return _bitmap; }
        public void Dispose() => _bitmap.Dispose();
    }

    [Theory]
    [InlineData("line")] [InlineData("token")] [InlineData("mark")]
    public async Task 实际Roslyn重复绑定最后生效跨实例顺序保留并执行真实目标捕获(string target)
    {
        var snapshot = Snapshot(target == "mark"); await using var session = await (await Factory(target, [1, .5])).CreateAsync(snapshot);
        long time = target == "mark" ? 10_000_000 : 1_000_000;
        var prepared = await session.PrepareAsync(time, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        Assert.Equal(target == "mark" ? 6 : 2, prepared.ImageEffects.Length); Assert.Equal(2, prepared.DotBatches.Length);
        Assert.All(prepared.ImageEffects, output => { Assert.Equal(Resources.GetGraph("image"), output.Binding.Graph); Assert.Equal(Script.Value.Result.ArtifactHash, output.ArtifactHash); });
        var first = prepared.ImageEffects[0]; var last = prepared.ImageEffects.Single(item => item.InstanceId.Value == "image-1" && item.Binding.Target == first.Binding.Target);
        Assert.Equal(1, first.Binding.Parameters.GetNumber("amount")); Assert.Equal(.5, last.Binding.Parameters.GetNumber("amount"));
        using SkiaRenderPipeline renderer = new(); using var graph = Graph(); var program = Program(graph);
        var request = RenderRequest.At(snapshot, time) with { TransparentBackground = true, PreparedFrame = prepared };
        using var a = renderer.ExecuteSceneImageEffect(request, first, program, new Dictionary<string, SkiaGraphInput>(), null);
        using var b = renderer.ExecuteSceneImageEffect(request, last, program, new Dictionary<string, SkiaGraphInput>(), a.GraphResult.Output);
        byte[] original = Bytes(a.GraphResult.Output), changed = Bytes(b.GraphResult.Output); int visible = 0;
        for (int i = 0; i < original.Length; i++) { Assert.InRange(Math.Abs(original[i] * .5 - changed[i]), 0, 1); if (i % 4 == 3 && original[i] > 0) visible++; }
        Assert.True(visible > 10);
        b.Dispose(); Assert.True(a.GraphResult.Output.Image.Handle != nint.Zero); // 下一实例不夺取上一产物。
        var gate = Assert.Throws<EffectFailureException>(() => renderer.Render(request));
        Assert.Equal("ImageEffectResourcesRequired", gate.Code); Assert.Equal(first.InstanceId, gate.InstanceId);
    }

    [Fact]
    public async Task 图参数资源预算和取消保留真实实例诊断失败后产物生命周期独立()
    {
        var snapshot = Snapshot(); await using var session = await (await Factory("token", [2])).CreateAsync(snapshot);
        var prepared = await session.PrepareAsync(1_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        var binding = Assert.Single(prepared.ImageEffects); using SkiaRenderPipeline renderer = new(); using var graph = Graph();
        var request = RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true };
        var bad = Assert.Throws<EffectFailureException>(() => renderer.ExecuteSceneImageEffect(request, binding, Program(graph), new Dictionary<string, SkiaGraphInput>(), null));
        Assert.Equal("GraphParameterOutOfRange", bad.Code); Assert.Equal("ExecuteImageGraph", bad.Stage); Assert.Equal(binding.InstanceId, bad.InstanceId);
        Assert.Equal(binding.ArtifactHash, bad.ArtifactHash); Assert.Equal(request.TimeUs, bad.TimeUs);
        var other = new Fx.ResourceCatalog("image-probe", "sha256:" + new string('b', 64), [], ["image"], []);
        var wrong = new SceneImageGraphProgram(other.GetGraph("image"), graph, Program(graph).Recipe, Program(graph).Bounds);
        Assert.Equal("ImageEffectResourceMismatch", Assert.Throws<EffectFailureException>(() => renderer.ExecuteSceneImageEffect(request, binding, wrong, new Dictionary<string, SkiaGraphInput>(), null)).Code);
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => renderer.ExecuteSceneImageEffect(request with { CancellationToken = cancelled.Token }, binding, Program(graph), new Dictionary<string, SkiaGraphInput>(), null));
        Assert.Throws<PreparedFrameException>(() => prepared.WithImageEffects([binding, binding]));
        Assert.Throws<PreparedFrameException>(() => prepared.WithImageEffects([binding with { Binding = binding.Binding with { Target = new Fx.ImageEffectTarget.Line(new(new("row"), new("old"))) } }]));
    }

    [Fact]
    public async Task 捕获输入去重并在分配前检查活跃输入预算()
    {
        var snapshot = Snapshot(); await using var session = await (await Factory("line", [.5])).CreateAsync(snapshot);
        var prepared = await session.PrepareAsync(1_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        var binding = Assert.Single(prepared.ImageEffects); using SkiaRenderPipeline renderer = new();
        using var graph = Graph(new() { MaximumBytes = 100 });
        var request = RenderRequest.At(snapshot, prepared.TimeUs) with { TransparentBackground = true, PreparedFrame = prepared };
        var error = Assert.Throws<EffectFailureException>(() => renderer.ExecuteSceneImageEffect(request, binding, Program(graph), new Dictionary<string, SkiaGraphInput>(), null));
        Assert.Equal("SceneImageInputBudgetExceeded", error.Code);
        var bounds = new GraphNodeBounds(new GraphExpansion.Literal(0), 0, 0, GraphEdgeMode.Transparent);
        var definition = new ImageGraphDefinition("aliases", ImageGraphDefinition.CurrentVersion,
            [new("a", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas),
             new("b", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)], [],
            [new ImageGraphNode.Blend("blend", GraphReference.Input("a"), GraphReference.Input("b"), GraphBlendMode.SourceOver, bounds)],
            GraphReference.Node("blend"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly, GraphCompositeMode.SourceOver);
        using var aliases = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [], [], new() { MaximumSurfaces = 2, MaximumBytes = 640 * 360 * 4 * 2 }));
        using var valid = renderer.ExecuteSceneImageEffect(request, binding, Program(aliases), new Dictionary<string, SkiaGraphInput>(), null);
        Assert.Equal(2, valid.GraphResult.PeakSurfaces);
        Assert.Equal(640 * 360 * 4 * 2, valid.GraphResult.PeakBytes);
        Assert.Contains(Bytes(valid.GraphResult.Output), value => value != 0);
        Assert.Equal("CaptureInputOverrideForbidden", Assert.Throws<EffectFailureException>(() => renderer.ExecuteSceneImageEffect(request, binding, Program(aliases),
            new Dictionary<string, SkiaGraphInput> { ["a"] = new(valid.GraphResult.Output, GraphInputSource.TargetColor) }, null)).Code);
    }

    [Fact]
    public async Task 元素绑定可读取行但写行图失败不会扩权()
    {
        var snapshot = Snapshot(); await using var session = await (await Factory("line", [1], new Fx.EffectBinding.Tokens([new("token")]))).CreateAsync(snapshot);
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await session.PrepareAsync(1_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition)));
        Assert.Equal("TargetOutsideBinding", error.Code); Assert.Equal("Evaluate", error.Stage);
    }

    [Fact]
    public async Task 真实绑定透明旁路保有捕获生命周期而不透明必需媒体缺失带实例失败()
    {
        var snapshot = Snapshot(); await using var session = await (await Factory("line", [1])).CreateAsync(snapshot);
        using SkiaRenderPipeline renderer = new();
        var shader = new ShaderProgramDefinition("media.sksl", "uniform shader media; half4 main(float2 p) { return media.eval(p); }",
            ShaderProgramDefinition.RasterProfile, [], [new("media", ShaderSurfaceKind.Color)]);
        var definition = new ImageGraphDefinition("glass", ImageGraphDefinition.CurrentVersion,
            [new("target", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas),
             new("media", GraphInputSource.BackgroundMedia, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.Media, GraphCoordinateSpace.Canvas)], [],
            [new ImageGraphNode.RuntimeShader("glass", "media", ShaderSurfaceKind.Color, [], [new("media", GraphReference.Input("media"))],
                new(new GraphExpansion.Literal(0), 0, 0, GraphEdgeMode.Transparent))], GraphReference.Node("glass"), GraphCoordinateSpace.Canvas, 0,
            GraphAnchorMode.ColorOnly, GraphCompositeMode.SourceOver, bypassInput: GraphReference.Input("target"));
        using var graph = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [], [new("media", shader)]));
        var program = Program(graph); var target = RenderTarget.FromComposition(snapshot.Composition);
        var opaque = await session.PrepareAsync(1_000_000, Fx.OutputMode.Opaque, target);
        var request = RenderRequest.At(snapshot, opaque.TimeUs) with { PreparedFrame = opaque };
        var error = Assert.Throws<EffectFailureException>(() => renderer.ExecuteSceneImageEffect(request, Assert.Single(opaque.ImageEffects), program, new Dictionary<string, SkiaGraphInput>(), null));
        Assert.Equal("GraphRequiredInputUnavailable", error.Code); Assert.Equal(new Fx.EffectInstanceId("image-0"), error.InstanceId);
        var transparent = await session.PrepareAsync(1_000_000, Fx.OutputMode.Transparent, target);
        request = request with { PreparedFrame = transparent, TransparentBackground = true };
        using var result = renderer.ExecuteSceneImageEffect(request, Assert.Single(transparent.ImageEffects), program, new Dictionary<string, SkiaGraphInput>(), null);
        Assert.Equal("TransparentBackgroundDependency", Assert.Single(result.GraphResult.Diagnostics).Reason);
        using var original = renderer.CaptureSceneTarget(request, new SceneCaptureTarget.Line(Assert.Single(transparent.FinalLineGeometry).Target), program.Recipe,
            GraphInputSource.TargetColor, program.Bounds, graph.Plan.Budget);
        Assert.Equal(Bytes(original), Bytes(result.GraphResult.Output));
        var borrowedFromResult = result.GraphResult.Output; result.Dispose();
        Assert.Throws<ObjectDisposedException>(() => borrowedFromResult.Image);
        Assert.True(original.Image.Handle != nint.Zero);
    }
}
