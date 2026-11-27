using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class MainCanvasImageEffectsTests
{
    private static CompiledImageGraph FullCanvasTransformGraph(string id, string expression = "half4(c.rgb*.9,c.a)",
        GraphCompositeMode composite = GraphCompositeMode.SourceOver, bool requireMedia = false)
    {
        var shader = new ShaderProgramDefinition(id + ".sksl",
            "uniform shader color; half4 main(float2 p) { half4 c=color.eval(p); return " + expression + "; }",
            ShaderProgramDefinition.RasterProfile, [], [new("color", ShaderSurfaceKind.Color)]);
        List<GraphInputDefinition> inputs =
        [
            new("color", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas),
        ];
        if (requireMedia)
            inputs.Add(new("media", GraphInputSource.BackgroundMedia, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.Media, GraphCoordinateSpace.Canvas));
        if (composite == GraphCompositeMode.ReplaceCoveredBackdrop)
            inputs.Add(new("mask", GraphInputSource.GlyphCoverage, ShaderSurfaceKind.Coverage, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas));
        var nodeBounds = new GraphNodeBounds(new GraphExpansion.Literal(0), 0, 16, GraphEdgeMode.Transparent);
        List<ImageGraphNode> nodes =
        [
            new ImageGraphNode.RuntimeShader("shade", id, ShaderSurfaceKind.Color, [], [new("color", GraphReference.Input("color"))], nodeBounds),
            new ImageGraphNode.Transform("fit", GraphReference.Node("shade"), SceneMatrix.Identity, Bounds, nodeBounds),
        ];
        if (requireMedia)
            nodes.Add(new ImageGraphNode.Blend("mix", GraphReference.Node("fit"), GraphReference.Input("media"), GraphBlendMode.SourceOver, nodeBounds));
        var output = GraphReference.Node(requireMedia ? "mix" : "fit");
        var definition = new ImageGraphDefinition(id, ImageGraphDefinition.CurrentVersion, inputs, [], nodes, output,
            GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly, composite,
            compositeCoverage: composite == GraphCompositeMode.ReplaceCoveredBackdrop ? GraphReference.Input("mask") : null,
            bypassInput: requireMedia ? GraphReference.Input("color") : null);
        return CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [], [new(id, shader)]));
    }

    [Fact]
    public void 连续SourceOver图立即释放临时输入与过期历史使峰值与链长无关()
    {
        var request = Request(WithoutRuby(Snapshot(decorated: true)));
        var target = Line(request);
        using var graph = FullCanvasTransformGraph("chain");
        using var resources = new SceneImageEffectResources([Program("first", graph)]);

        using var shortBudget = new ImageSurfaceBudgetSession();
        using var shortRenderer = new SkiaRenderPipeline(null, null, shortBudget, resources);
        using var shortRender = shortRenderer.Render(Bindings(request, Bind("g0", target), Bind("g1", target)));

        var tenBindings = Enumerable.Range(0, 10).Select(i => Bind("g" + i, target)).ToArray();
        using var longBudget = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = shortBudget.Usage.PeakSurfaces });
        using var longRenderer = new SkiaRenderPipeline(null, null, longBudget, resources);
        using var longRender = longRenderer.Render(Bindings(request, tenBindings));

        Assert.Equal(shortBudget.Usage.PeakSurfaces, longBudget.Usage.PeakSurfaces);
        Assert.Equal(shortBudget.Usage.PeakBytes, longBudget.Usage.PeakBytes);
        Assert.Equal(0, longBudget.Usage.ActiveSurfaces);
        Assert.Equal(0, longBudget.Usage.EstimatedBytes);
        Assert.Contains(longRender.Pixels.ToArray(), value => value != 0);
    }

    [Fact]
    public void 旁路图立即释放临时输入与旁路结果不累积表面峰值()
    {
        var request = Request(WithoutRuby(Snapshot(decorated: false)), transparent: true);
        var target = Line(request);
        using var bypassGraph = FullCanvasTransformGraph("bypass", requireMedia: true);
        using var tintGraph = FullCanvasTransformGraph("tint");
        using var resources = new SceneImageEffectResources([Program("first", bypassGraph), Program("last", tintGraph)]);

        var bindings = Enumerable.Range(0, 8).Select(i => Bind("bypass-" + i, target, "first"))
            .Append(Bind("final", target, "last")).ToArray();
        using var budget = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 7 });
        using var renderer = new SkiaRenderPipeline(null, null, budget, resources);
        using var rendered = renderer.Render(Bindings(request, bindings));

        Assert.Equal(8, rendered.ImageEffectDiagnostics.Length);
        Assert.Equal(0, budget.Usage.ActiveSurfaces);
        Assert.Equal(0, budget.Usage.EstimatedBytes);
        Assert.Contains(rendered.Pixels.ToArray(), value => value != 0);
    }

    [Fact]
    public void 替换合成后接多段SourceOver保留替换覆盖同时释放多余历史()
    {
        var request = Request(WithoutRuby(Snapshot(decorated: false)), transparent: false);
        var target = Line(request);
        using var replace = FullCanvasTransformGraph("replace", "half4(0,0,0,0)", GraphCompositeMode.ReplaceCoveredBackdrop);
        using var pass = FullCanvasTransformGraph("pass", "c");
        using var resources = new SceneImageEffectResources([Program("first", replace), Program("last", pass)]);

        using var twoStepBudget = new ImageSurfaceBudgetSession();
        using var twoStepRenderer = new SkiaRenderPipeline(null, null, twoStepBudget, resources);
        using var twoStep = twoStepRenderer.Render(Bindings(request, Bind("r0", target, "first"), Bind("p0", target, "last"), Bind("p1", target, "last")));

        var many = new[] { Bind("r0", target, "first") }
            .Concat(Enumerable.Range(0, 8).Select(i => Bind("p" + i, target, "last"))).ToArray();
        using var manyBudget = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = twoStepBudget.Usage.PeakSurfaces });
        using var manyRenderer = new SkiaRenderPipeline(null, null, manyBudget, resources);
        using var chained = manyRenderer.Render(Bindings(request, many));

        Assert.Equal(twoStep.Pixels.ToArray(), chained.Pixels.ToArray());
        Assert.Equal(twoStepBudget.Usage.PeakSurfaces, manyBudget.Usage.PeakSurfaces);
        Assert.Equal(0, manyBudget.Usage.ActiveSurfaces);
        Assert.Equal(0, manyBudget.Usage.EstimatedBytes);
    }

    [Fact]
    public void 十四词元全画布Transform图按成员流式执行不撞爆64张全屏预算()
    {
        // 复现 14 词元全装饰（辉光／阴影／描边／填充）行，全部词元绑定全画布 Transform 图（等效 1080p 下 512 MiB ≈ 64 张全屏表面）。
        const int tokenCount = 14;
        string text = "どうして君は泣いているの歌う";
        var tokens = Enumerable.Range(0, tokenCount)
            .Select(i => MakoKaraGenerator.Core.Lyrics.LyricToken.Create($"t-{i:00}", text[i].ToString(),
                new(i * 200_000L, (i + 1) * 200_000L), MakoKaraGenerator.Core.Lyrics.TimingStatus.Edited))
            .ToArray();
        var line = MakoKaraGenerator.Core.Lyrics.LyricLine.Create("line-0021", text, new(0, 5_000_000),
            MakoKaraGenerator.Core.Lyrics.TimingStatus.Edited, tokens);
        var style = LyricStyle.Default with
        {
            FontFamilyName = "Noto Sans CJK SC", FontSizePx = 36, Opacity = 1f,
            UnsungColor = new(220, 230, 255, 255), SungColor = new(255, 200, 120, 255),
            Stroke = StrokeSpec.Create(new(20, 40, 120, 255), 3),
            Shadow = ShadowSpec.Create(new(0, 0, 0, 180), angleDegrees: 45, distancePx: 6, blurRadiusPx: 2),
            Glow = new GlowSpec { SpreadPx = 2, BlurRadiusPx = 4, Color = new(120, 200, 255, 180) },
        };
        var snapshot = ProjectSnapshot.FromProject(Project.Create("14-token-transform",
            Composition.Create(640, 360, MakoKaraGenerator.Core.Time.FrameRate.Fps30, new(10, 14, 24), 5_000_000),
            MakoKaraGenerator.Core.Lyrics.LyricsDocument.Create([line]), lyricStyle: style));
        var request = Request(snapshot, transparent: false, width: 640, height: 360, time: 1_500_000);
        var tokenGeometries = request.PreparedFrame!.FinalGeometry.ToArray();
        Assert.Equal(tokenCount, tokenGeometries.Length);

        using var wind = FullCanvasTransformGraph("wind-token", "half4(c.rgb*.85,c.a)");
        using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), wind,
            new SceneCaptureRecipe(true, true, true, false, SceneCaptureRegion.FillAndStroke, IncludeGlow: true), Bounds)]);
        var bindingValue = new Fx.GraphBindingValue(Catalog.GetGraph("first"), Fx.ParameterValues.Empty, Fx.GraphUniformValues.Empty,
            new([new("fit", new(Fx.Affine2.Translation(2, -1), new(0, 0, 640, 360)))]));
        var bindings = tokenGeometries.Select((geometry, index) =>
            new PreparedImageEffectBinding(new($"wind-{index}"), "artifact-wind",
                new Fx.ImageEffectBinding(new Fx.ImageEffectTarget.Token(geometry.WithRuby), bindingValue)))
            .ToArray();

        long fullSurfaceBytes = 640L * 360 * 4;
        // 严格卡在 28 张全屏表面预算内（原实现需 ~88 张，远超 64 张上限）。
        using var budget = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 28, MaximumBytes = fullSurfaceBytes * 28 });
        using var renderer = new SkiaRenderPipeline(null, null, budget, resources, false, null, true, graphPixelChecks: false);
        using var rendered = renderer.Render(Bindings(request, bindings));

        Assert.Empty(rendered.ImageEffectDiagnostics);
        Assert.True(budget.Usage.PeakSurfaces <= 24, $"预期流式执行峰值 <= 24，实际 {budget.Usage.PeakSurfaces}");
        Assert.Equal(0, budget.Usage.ActiveSurfaces);
        Assert.Equal(0, budget.Usage.EstimatedBytes);
        Assert.Contains(rendered.Pixels.ToArray(), value => value != 0);
    }

    [Fact]
    public void 部分词元流式执行后再交由整行图结果与像素完整且不泄漏表面()
    {
        var request = Request(WithoutRuby(Snapshot(decorated: true, secondToken: true)), transparent: false);
        var token0 = new Fx.ImageEffectTarget.Token(request.PreparedFrame!.FinalGeometry[0].WithRuby);
        var lineTarget = Line(request);
        using var tokenGraph = FullCanvasTransformGraph("token-pass", "half4(c.r*.5,c.g,c.b,c.a)");
        using var lineGraph = FullCanvasTransformGraph("line-pass", "half4(c.r,c.g*.5,c.b,c.a)");
        using var resources = new SceneImageEffectResources([Program("first", tokenGraph), Program("last", lineGraph)]);

        using var budget = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 16 });
        using var renderer = new SkiaRenderPipeline(null, null, budget, resources, false, null, true, graphPixelChecks: false);
        using var rendered = renderer.Render(Bindings(request, Bind("t0", token0, "first"), Bind("line", lineTarget, "last")));

        Assert.Empty(rendered.ImageEffectDiagnostics);
        Assert.Equal(0, budget.Usage.ActiveSurfaces);
        Assert.Equal(0, budget.Usage.EstimatedBytes);
        Assert.Contains(rendered.Pixels.ToArray(), value => value != 0);
    }
}
