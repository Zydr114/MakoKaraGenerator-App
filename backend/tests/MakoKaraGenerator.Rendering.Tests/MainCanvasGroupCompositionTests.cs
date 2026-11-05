using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

// Real main-canvas fixtures supplement the independent C/E arithmetic checks.
public sealed partial class MainCanvasImageEffectsTests
{
    private static SceneCaptureRecipe GroupWhole => new(true, true, true, true, SceneCaptureRegion.FillAndStroke, true);
    private static Fx.ImageEffectTarget.Group GroupRoot(RenderRequest request)
        => new(new(new(request.Project.LyricPresentation.FindProgram("primary")!.RootContainerId)));
    private static SkiaRenderPipeline GroupRenderer(SceneImageEffectResources resources, ImageSurfaceBudgetSession budget)
        => new(null, null, budget, resources);
    private static CompiledImageGraph GroupIdentity()
        => CompiledImageGraph.Compile(ImageGraphCompiler.Compile(new("identity", ImageGraphDefinition.CurrentVersion,
            [new("color", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)],
            [], [], GraphReference.Input("color"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly, GraphCompositeMode.SourceOver), [], []));
    private static CompiledImageGraph Transparent80()
    {
        var nodeBounds = new GraphNodeBounds(new GraphExpansion.Literal(0), 0, 0, GraphEdgeMode.Transparent);
        var color = new ShaderProgramDefinition("transparent.sksl", "half4 main(float2 p) { return half4(0); }", ShaderProgramDefinition.RasterProfile, [], []);
        var mask = new ShaderProgramDefinition("eighty.sksl", "half4 main(float2 p) { return half4(0,0,0,.8); }", ShaderProgramDefinition.RasterProfile, [], []);
        return CompiledImageGraph.Compile(ImageGraphCompiler.Compile(new("transparent80", ImageGraphDefinition.CurrentVersion, [], [],
            [new ImageGraphNode.RuntimeShader("color", "color", ShaderSurfaceKind.Color, [], [], nodeBounds),
                new ImageGraphNode.RuntimeShader("mask", "mask", ShaderSurfaceKind.Coverage, [], [], nodeBounds)],
            GraphReference.Node("color"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly, GraphCompositeMode.ReplaceCoveredBackdrop,
            GraphReference.Node("mask")), [], [new("color", color), new("mask", mask)]));
    }

    [Theory]
    [InlineData(false, 640, 360)] [InlineData(true, 480, 180)]
    public void G1_父identity读取真实孩子Color且不带Below并保持现有像素(bool transparent, int width, int height)
    {
        var request = Request(Snapshot(), transparent, width, height);
        using var identity = GroupIdentity();
        using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), identity, GroupWhole, Bounds)]);
        using var budget = new ImageSurfaceBudgetSession(); using var renderer = GroupRenderer(resources, budget);
        using var expected = renderer.Render(request);
        using var actual = renderer.Render(Bindings(request, Bind("parent", GroupRoot(request))));
        using var leaf = renderer.Render(Bindings(request, Bind("leaf-reference", Line(request))));
        Close(leaf.Pixels.ToArray(), actual.Pixels.ToArray(), 2);
        Close(expected.Pixels.ToArray(), actual.Pixels.ToArray(), 2);
        Assert.Empty(actual.ImageEffectDiagnostics); Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }

    [Fact]
    public void G2_两个独立孩子透明替换经父identity仍顺序擦除96百分比()
    {
        var request = Request(Snapshot(false, true), false, time: 3_000_000);
        using var child = Transparent80(); using var identity = GroupIdentity();
        using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), child, Whole, Bounds),
            new(Catalog.GetGraph("last"), identity, GroupWhole, Bounds)]);
        using var budget = new ImageSurfaceBudgetSession(); using var renderer = GroupRenderer(resources, budget);
        var targets = request.PreparedFrame!.FinalGeometry.Select(value => new Fx.ImageEffectTarget.Token(value.WithRuby)).ToArray();
        var children = Bindings(request, Bind("child-a", targets[0]), Bind("child-b", targets[1]));
        using var expected = renderer.Render(children);
        using var actual = renderer.Render(Bindings(request, Bind("child-a", targets[0]), Bind("child-b", targets[1]), Bind("parent", GroupRoot(request), "last")));
        Close(expected.Pixels.ToArray(), actual.Pixels.ToArray(), 2);
        // Far outside real glyph ink, only the two full-frame .8 masks affect B.
        Assert.InRange(actual.Pixels[3], (byte)9, (byte)12);
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }

    [Fact]
    public void G3_同父普通图继续淡出不固化前图alpha且孩子完整E仍保留()
    {
        var request = Request(Snapshot());
        using var first = Tint("first", "half4(.8,0,0,.8)"); using var last = Tint("last", "c*.5");
        using var direct = Tint("direct", "half4(.4,0,0,.4)");
        using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), first, GroupWhole, Bounds),
            new(Catalog.GetGraph("last"), last, GroupWhole, Bounds)]);
        using var referenceResources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), direct, GroupWhole, Bounds)]);
        using var budget = new ImageSurfaceBudgetSession(); using var referenceBudget = new ImageSurfaceBudgetSession();
        using var renderer = GroupRenderer(resources, budget); using var reference = GroupRenderer(referenceResources, referenceBudget);
        var group = GroupRoot(request);
        using var actual = renderer.Render(Bindings(request, Bind("first", group), Bind("last", group, "last")));
        using var expected = reference.Render(Bindings(request, Bind("direct", group)));
        Close(expected.Pixels.ToArray(), actual.Pixels.ToArray(), 2);
        // The previous .8 ordinary alpha must not become a permanent floor.
        // Outside glyphs child E=0, so the second own output has alpha/E=.4.
        Assert.InRange(actual.Pixels[3], (byte)100, (byte)104);
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, referenceBudget.Usage.ActiveSurfaces);
    }

    [Fact]
    public void G4_真实孩子和自身图完成后两级共同alpha各一次()
    {
        var request = Request(Snapshot(), false); using var graph = Transparent80();
        using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), graph, GroupWhole, Bounds)]);
        using var budget = new ImageSurfaceBudgetSession(); using var renderer = GroupRenderer(resources, budget);
        var program = request.Project.LyricPresentation.FindProgram("primary")!;
        var target = new Fx.ImageEffectTarget.Group(new(new(program.DefaultContainerId)));
        var bound = Bindings(request, Bind("replace", target));
        using var actual = renderer.Render(bound with { PreparedFrame = bound.PreparedFrame!.WithGroupOpacities([
            new(new("inner"), "alpha-inner", new(target.Target, .5)),
            new(new("outer"), "alpha-outer", new(GroupRoot(request).Target, .5))]) });
        // Outside glyph ink E=.8*.5*.5=.2; blue opaque B becomes alpha .8.
        Assert.InRange(actual.Pixels[3], (byte)202, (byte)206);
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }

    [Fact]
    public void G5_隐藏组先验证资源再Info消费绑定且不执行凭空图()
    {
        var request = Request(ProjectSnapshot.FromProject(LeadInFixture.Create(true)), time: 10_000_000);
        var hidden = request.PreparedFrame!.FinalGroupGeometry.First(group => group.WorldInkCorners.IsEmpty
            && request.PreparedFrame.HierarchyScene.Elements.Any(node => node.Id == group.Target.NodeId.Value && node.SourceNodeId is null));
        var bound = Bindings(request, Bind("hidden", new Fx.ImageEffectTarget.Group(hidden.Target)));
        using var graph = Transparent80(); using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), graph, GroupWhole, Bounds)]);
        using var budget = new ImageSurfaceBudgetSession(); using var renderer = GroupRenderer(resources, budget);
        using var expected = renderer.Render(request); using var actual = renderer.Render(bound);
        Close(expected.Pixels.ToArray(), actual.Pixels.ToArray());
        Assert.Contains(actual.SceneDiagnostics, diagnostic => diagnostic.Diagnostic.Code == "GroupImageEffectNoVisibleChildren"
            && diagnostic.Diagnostic.Severity == Fx.DiagnosticSeverity.Info && diagnostic.InstanceId.Value == "hidden");
        using var missingResources = new SceneImageEffectResources([]); using var missingBudget = new ImageSurfaceBudgetSession();
        using var missing = GroupRenderer(missingResources, missingBudget);
        var error = Assert.Throws<EffectFailureException>(() => missing.Render(bound));
        Assert.Equal("ImageEffectResourceUnavailable", error.Code); Assert.Equal("hidden", error.InstanceId.Value);
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, missingBudget.Usage.ActiveSurfaces);
    }

    [Fact]
    public void G6_错误与取消释放全部组帧表面且保持真实owner()
    {
        var request = Request(Snapshot()); using var identity = GroupIdentity();
        using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), identity, GroupWhole, Bounds)]);
        using var budget = new ImageSurfaceBudgetSession(); using var renderer = GroupRenderer(resources, budget);
        var bad = Bindings(request, Bind("bad-parent", GroupRoot(request), "last"));
        var error = Assert.Throws<EffectFailureException>(() => renderer.Render(bad));
        Assert.Equal("bad-parent", error.InstanceId.Value); Assert.Equal("artifact-bad-parent", error.ArtifactHash);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => renderer.Render(Bindings(request, Bind("parent", GroupRoot(request)))
            with { CancellationToken = cancelled.Token }));
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }
    [Fact]
    public void G7_手工PreparedFrame也拒绝冻结组alias而source组仍合法()
    {
        var request = Request(Snapshot()); var frame = request.PreparedFrame!;
        var source = GroupRoot(request); var sourceId = source.Target.NodeId.Value;
        var alias = new Fx.GroupTarget(new("exit:group-composition-test:" + sourceId));
        var original = frame.HierarchyScene.Elements.Single(node => node.Id == sourceId);
        var geometry = frame.HierarchyScene.GroupGeometries.Single(value => value.Target == source.Target);
        var scene = frame.HierarchyScene with
        {
            Elements = frame.HierarchyScene.Elements.Add(original with { Id = alias.NodeId.Value, SourceNodeId = sourceId }),
            GroupGeometries = frame.HierarchyScene.GroupGeometries.Add(geometry with { Target = alias }),
        };
        var manual = new PreparedRenderFrame(request.Project, request.TimeUs, Fx.OutputMode.Transparent, request.ResolveTarget(), scene, []);
        _ = manual.WithImageEffects([Bind("source", source)]);
        var error = Assert.Throws<PreparedFrameException>(() => manual.WithImageEffects([Bind("alias", new Fx.ImageEffectTarget.Group(alias))]));
        Assert.Equal("InvalidPreparedImageEffectTarget", error.Code);
    }

}
