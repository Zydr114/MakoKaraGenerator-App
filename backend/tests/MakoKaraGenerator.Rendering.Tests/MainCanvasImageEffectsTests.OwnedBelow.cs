using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

// Probe output is far from every test overlay, so final
// real BehindText cannot hide an incorrectly sampled/self-containing input.
// 图按绑定目标的实际区域执行（第 12 条）：探针条画在目标墨迹框内（OwnedSpot），采样点仍在远处的叠加图元上。
public sealed partial class MainCanvasImageEffectsTests
{
    private static readonly Fx.ResourceCatalog OwnedCatalog = new("owned-below-test", "sha256:" + new string('c', 64), [], ["a", "b", "c"], []);
    private static RenderRequest OwnedRequest(bool transparent = true)
    {
        var snapshot = Snapshot(false) with { LyricStyle = Snapshot(false).LyricStyle with { Opacity = 1 } };
        return Request(snapshot, transparent, time: 3_000_000);
    }
    private static PreparedImageEffectBinding OwnedBind(string owner, Fx.ImageEffectTarget target, string graph)
        => new(new(owner), "artifact-" + owner, new(target, OwnedCatalog.GetGraph(graph), Fx.ParameterValues.Empty));
    private static SceneImageGraphProgram OwnedProgram(string id, CompiledImageGraph graph)
        => new(OwnedCatalog.GetGraph(id), graph, Whole, Bounds);
    private static Fx.DotBatch OwnedDot(string owner, string output, Fx.ColorRgba color, double x = 30, double y = 20)
        => new(new(owner), new(output), Fx.OverlayLayer.BehindText, [new(new(x, y), 8, color)]);

    /// <summary>探针条位置：目标墨迹框内左上部第 index 条（每条宽 12、高 16，横向间隔 16）。</summary>
    private static (int X, int Y) OwnedSpot(Fx.GeometrySample geometry, int index = 0)
    {
        var corners = geometry.WorldInkCorners;
        return ((int)Math.Ceiling(corners.Min(point => point.X)) + 2 + 16 * index, (int)Math.Ceiling(corners.Min(point => point.Y)) + 2);
    }
    private static (int X, int Y) OwnedLineSpot(RenderRequest request, int index = 0)
        => OwnedSpot(Assert.Single(request.PreparedFrame!.FinalGeometry).BaseGeometry, index);
    private static byte[] OwnedStripe(RenderFrame frame, (int X, int Y) spot) => OwnedPixel(frame, spot.X + 4, spot.Y + 8);
    private static void OwnedStripeNear(RenderFrame frame, (int X, int Y) spot, byte blue, byte green, byte red, byte alpha)
        => OwnedPixelNear(frame, spot.X + 4, spot.Y + 8, blue, green, red, alpha);
    private static CompiledImageGraph OwnedProbe(string id, int stripe, int sampleX = 30, int sampleY = 20, ISkiaRenderDevice? device = null)
        => OwnedProbe(id, (stripe, 12), sampleX, sampleY, device);
    private static CompiledImageGraph OwnedProbe(string id, (int X, int Y) stripe, int sampleX = 30, int sampleY = 20, ISkiaRenderDevice? device = null)
    {
        string region = $"p.x>={stripe.X} && p.x<{stripe.X + 12} && p.y>={stripe.Y} && p.y<{stripe.Y + 16}";
        var color = new ShaderProgramDefinition(id + ".sksl", "uniform shader color; uniform shader below; half4 main(float2 p) { return " + region
            + $" ? below.eval(float2({sampleX}.5,{sampleY}.5)) : color.eval(p); }}", ShaderProgramDefinition.RasterProfile, [],
            [new("color", ShaderSurfaceKind.Color), new("below", ShaderSurfaceKind.Color)]);
        var coverage = new ShaderProgramDefinition(id + "-mask.sksl", "half4 main(float2 p) { return half4(0,0,0,(" + region + ") ? 1 : 0); }",
            ShaderProgramDefinition.RasterProfile, [], []);
        var nodeBounds = new GraphNodeBounds(new GraphExpansion.Literal(0), 0, 1024, GraphEdgeMode.Transparent);
        var definition = new ImageGraphDefinition(id, ImageGraphDefinition.CurrentVersion,
            [new("color", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas),
                new("below", GraphInputSource.BackdropBelow, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)], [],
            [new ImageGraphNode.RuntimeShader("out", "color", ShaderSurfaceKind.Color, [], [new("color", GraphReference.Input("color")), new("below", GraphReference.Input("below"))], nodeBounds),
                new ImageGraphNode.RuntimeShader("mask", "mask", ShaderSurfaceKind.Coverage, [], [], nodeBounds)],
            GraphReference.Node("out"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly, GraphCompositeMode.ReplaceCoveredBackdrop, GraphReference.Node("mask"));
        var compiled = ImageGraphCompiler.Compile(definition, [], [new("color", color), new("mask", coverage)], new GraphExecutionBudget { MaximumExpandPx = 1024 });
        return device is null ? CompiledImageGraph.Compile(compiled) : CompiledImageGraph.Compile(compiled, device);
    }
    private static SKPath OwnedInkRegion(Fx.GeometrySample geometry)
    {
        Assert.Null(geometry.Mapping); // These fixtures use affine geometry only.
        var corners = geometry.WorldInkCorners; Assert.Equal(4, corners.Length);
        SKPath path = new(); path.MoveTo((float)corners[0].X, (float)corners[0].Y);
        foreach (var corner in corners.Skip(1)) path.LineTo((float)corner.X, (float)corner.Y);
        path.Close(); return path;
    }
    private static byte[] OwnedPixel(RenderFrame frame, int x, int y)
    {
        int offset = checked((y * frame.Target.PixelWidth + x) * 4);
        return frame.Pixels.Slice(offset, 4).ToArray();
    }
    private static void OwnedPixelNear(RenderFrame frame, int x, int y, byte blue, byte green, byte red, byte alpha, int tolerance = 2)
        => Close([blue, green, red, alpha], OwnedPixel(frame, x, y), tolerance);

    [Fact]
    public void B1_异处输出验证A到B到A原序过滤不同owner且同leaf固定进入前缀()
    {
        var request = OwnedRequest(); var target = Line(request);
        var (sa, sb, sc) = (OwnedLineSpot(request, 0), OwnedLineSpot(request, 1), OwnedLineSpot(request, 2));
        using var a = OwnedProbe("a", sa); using var b = OwnedProbe("b", sb); using var c = OwnedProbe("c", sc);
        using var resources = new SceneImageEffectResources([OwnedProgram("a", a), OwnedProgram("b", b), OwnedProgram("c", c)]);
        using var budget = new ImageSurfaceBudgetSession(); using var renderer = new SkiaRenderPipeline(null, null, budget, resources);
        var frame = request.PreparedFrame!.WithOverlayOutput([
            OwnedDot("A", "red-first", new(1, 0, 0, .5)), OwnedDot("B", "green-middle", new(0, 1, 0, .5)),
            OwnedDot("A", "blue-last", new(0, 0, 1, .5))], []).WithImageEffects([
            OwnedBind("A", target, "a"), OwnedBind("B", target, "b"), OwnedBind("C", target, "c")]);
        using var actual = renderer.Render(request with { PreparedFrame = frame });
        // A sees B only; B sees red A then blue A; C sees original A/B/A.
        OwnedStripeNear(actual, sa, 0, 128, 0, 128);
        OwnedStripeNear(actual, sb, 128, 0, 64, 192);
        OwnedStripeNear(actual, sc, 128, 64, 32, 224);
        // The actual screen still includes all overlays at their real location.
        OwnedPixelNear(actual, 30, 20, 128, 64, 32, 224);
        Assert.Empty(actual.ImageEffectDiagnostics); Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }

    [Fact]
    public void B2_组parent读取进入组前缀而孩子读取前序兄弟且不含当前目标()
    {
        var request = OwnedRequest(); var frame = request.PreparedFrame!;
        var parent = GroupRoot(request); var token = new Fx.ImageEffectTarget.Token(Assert.Single(frame.FinalGeometry).WithRuby);
        // 采样点与两个图元放在孩子区域内：孩子的常量红只画满自身区域（第 12 条）。
        var sample = OwnedLineSpot(request);
        using var child = Tint("child", "half4(.8,0,0,.8)"); using var probe = OwnedProbe("parent", 480, sample.X, sample.Y);
        using var resources = new SceneImageEffectResources([OwnedProgram("a", child), new(OwnedCatalog.GetGraph("b"), probe, GroupWhole, Bounds)]);
        using var budget = new ImageSurfaceBudgetSession(); using var renderer = new SkiaRenderPipeline(null, null, budget, resources);
        var prepared = frame.WithOverlayOutput([OwnedDot("P", "own", new(0, 0, 1, 1), sample.X, sample.Y),
                OwnedDot("other", "green", new(0, 1, 0, .5), sample.X, sample.Y)], [])
            .WithImageEffects([OwnedBind("child", token, "a"), OwnedBind("P", parent, "b")]);
        using var actual = renderer.Render(request with { PreparedFrame = prepared });
        // Child paints red .8 over its whole region, including the sample. Parent must not
        // sample that child or its own blue batch; it sees only other green .5.
        OwnedPixelNear(actual, 484, 20, 0, 128, 0, 128);
        Assert.Equal(0, budget.Usage.ActiveSurfaces);
    }

    [Fact]
    public void B3_前序根leaf完整独立擦除经异处Below保留而不能退化为颜色截图()
    {
        var request = Request(ProjectSnapshot.FromProject(LeadInFixture.Create(true, true)), false, time: 9_500_000);
        var frame = request.PreparedFrame!; Assert.True(frame.FinalMarkGeometry.Length >= 3);
        Assert.All(frame.HierarchyScene.TextMarks, mark => Assert.Equal(1, mark.Opacity));
        var first = new Fx.ImageEffectTarget.Mark(frame.FinalMarkGeometry[0].Target);
        var second = new Fx.ImageEffectTarget.Mark(frame.FinalMarkGeometry[1].Target);
        var last = new Fx.ImageEffectTarget.Mark(frame.FinalMarkGeometry[2].Target);
        // 两次擦除各外扩 24 px，在前两个标记之间重叠；采样点取两者墨迹之间（第 12 条）。
        var ink0 = frame.FinalMarkGeometry[0].Geometry.WorldInkCorners; var ink1 = frame.FinalMarkGeometry[1].Geometry.WorldInkCorners;
        int sx = (int)Math.Round((ink0.Max(point => point.X) + ink1.Min(point => point.X)) / 2);
        int sy = (int)Math.Round((ink0.Min(point => point.Y) + ink0.Max(point => point.Y)) / 2);
        var spot = OwnedSpot(frame.FinalMarkGeometry[2].Geometry);
        using var erase = Transparent80(24); using var probe = OwnedProbe("probe", spot, sx, sy);
        using var resources = new SceneImageEffectResources([OwnedProgram("a", erase), OwnedProgram("b", probe)]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        var own = OwnedDot("read", "trigger", new(1, 0, 0, 1));
        var prepared = frame.WithOverlayOutput([own], []).WithImageEffects([
            OwnedBind("erase-1", first, "a"), OwnedBind("erase-2", second, "a"), OwnedBind("read", last, "b")]);
        using var actual = renderer.Render(request with { PreparedFrame = prepared });
        // Independent reference is normal production rendering of the exact two
        // preceding replacement leaves, without any owned resolver or last graph.
        var reference = frame.WithImageEffects([OwnedBind("erase-1", first, "a"), OwnedBind("erase-2", second, "a")]);
        using var expected = renderer.Render(request with { PreparedFrame = reference });
        // At the sample there is no glyph; the last probe must exactly sample its
        // background through BOTH erasures. This fails alpha-only reconstruction.
        Close(OwnedPixel(expected, sx, sy), OwnedStripe(actual, spot), 2);
        Assert.InRange(OwnedPixel(expected, sx, sy)[3], (byte)9, (byte)12);
    }

    [Fact]
    public void B4_普通前序leaf也进入前缀且自身目标从Below排除()
    {
        var request = Request(ProjectSnapshot.FromProject(LeadInFixture.Create(true, true)), false, time: 9_500_000);
        var frame = request.PreparedFrame!; Assert.True(frame.FinalMarkGeometry.Length >= 2);
        Assert.All(frame.HierarchyScene.TextMarks, mark => Assert.Equal(1, mark.Opacity));
        var last = new Fx.ImageEffectTarget.Mark(frame.FinalMarkGeometry[^1].Target);
        var hidden = new PreparedRenderFrame(request.Project, request.TimeUs, frame.OutputMode, frame.Target, frame.Scene, [], [], [], [],
            [new(last.Target, new(opacityFactor: 0))]);
        using var original = new SkiaRenderPipeline(); using var expected = original.Render(request with { PreparedFrame = hidden });
        // Select an actual bright pixel of a preceding ordinary mark. This must
        // differ from the black background; a vacant sample would be vacuous.
        using var earlier = OwnedInkRegion(frame.FinalMarkGeometry[0].Geometry);
        var candidates = (from y in Enumerable.Range(30, 300) from x in Enumerable.Range(40, 560)
            where earlier.Contains(x + .5f, y + .5f) && OwnedPixel(expected, x, y)[2] > 80 select (X: x, Y: y)).ToArray();
        Assert.NotEmpty(candidates); var sample = candidates[0];
        var spot = OwnedSpot(frame.FinalMarkGeometry[^1].Geometry);
        using var probe = OwnedProbe("probe", spot, sample.X, sample.Y);
        using var resources = new SceneImageEffectResources([OwnedProgram("a", probe)]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        var prepared = frame.WithOverlayOutput([OwnedDot("read", "trigger", new(1, 0, 0, 1))], [])
            .WithImageEffects([OwnedBind("read", last, "a")]);
        using var actual = renderer.Render(request with { PreparedFrame = prepared });
        Close(OwnedPixel(expected, sample.X, sample.Y), OwnedStripe(actual, spot), 2);
    }

    [Fact]
    public void B5_背景媒体只读取一次且Below重绘失败取消后归零并恢复()
    {
        var snapshot = Snapshot(false) with { Background = BackgroundSource.ForVideo("/tmp/owned-below-fixture.media"),
            LyricStyle = Snapshot(false).LyricStyle with { Opacity = 1 } };
        var request = Request(snapshot, false, time: 3_000_000); var binding = OwnedBind("read", Line(request), "a");
        var spot = OwnedLineSpot(request);
        using var probe = OwnedProbe("a", spot); using var resources = new SceneImageEffectResources([OwnedProgram("a", probe)]);
        using var media = new OwnedBackground(); using var budget = new ImageSurfaceBudgetSession(); using var device = new OwnedObservedDevice();
        using var renderer = new SkiaRenderPipeline(null, media, budget, resources, false, device, false);
        var prepared = request.PreparedFrame!.WithOverlayOutput([OwnedDot("read", "own", new(1, 0, 0, 1)),
            OwnedDot("other", "other", new(0, 1, 0, .5))], []).WithImageEffects([binding]);
        var bound = request with { PreparedFrame = prepared };
        using (var frame = renderer.Render(bound)) { OwnedStripeNear(frame, spot, 50, 178, 25, 255); }
        Assert.Equal(1, media.Frames); Assert.Equal(0, budget.Usage.ActiveSurfaces);
        using var cancellation = new CancellationTokenSource(); bool insideResolver = false;
        device.BeforeCreate = () =>
        {
            if (!Environment.StackTrace.Contains("Resolve", StringComparison.Ordinal)) return;
            insideResolver = true; cancellation.Cancel();
        };
        Assert.Throws<OperationCanceledException>(() => renderer.Render(bound with { CancellationToken = cancellation.Token }));
        Assert.True(insideResolver); Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
        device.BeforeCreate = () =>
        {
            if (Environment.StackTrace.Contains("Resolve", StringComparison.Ordinal)) throw new InvalidOperationException("owned-below-fixture");
        };
        var failure = Assert.Throws<EffectFailureException>(() => renderer.Render(bound));
        Assert.Equal(binding.InstanceId, failure.InstanceId); Assert.Equal("RenderImageEffects", failure.Stage);
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
        device.BeforeCreate = null; using var recovered = renderer.Render(bound);
        OwnedStripeNear(recovered, spot, 50, 178, 25, 255);
    }

    [Fact]
    public void B10_组内孩子读取前序兄弟完整Color而同owner蓝色图元被排除()
    {
        var request = Request(ProjectSnapshot.FromProject(LeadInFixture.Create(true, true)), true, time: 9_500_000);
        var frame = request.PreparedFrame!; Assert.True(frame.FinalMarkGeometry.Length >= 2);
        var first = new Fx.ImageEffectTarget.Mark(frame.FinalMarkGeometry[0].Target);
        var second = new Fx.ImageEffectTarget.Mark(frame.FinalMarkGeometry[1].Target);
        // 采样点放在前一兄弟的区域内：它的常量红只画满自身区域（第 12 条）。
        var sample = OwnedSpot(frame.FinalMarkGeometry[0].Geometry); var spot = OwnedSpot(frame.FinalMarkGeometry[1].Geometry);
        using var red = Tint("red", "half4(.8,0,0,.8)"); using var probe = OwnedProbe("probe", spot, sample.X, sample.Y); using var identity = GroupIdentity();
        using var resources = new SceneImageEffectResources([OwnedProgram("a", red), OwnedProgram("b", probe),
            new(OwnedCatalog.GetGraph("c"), identity, GroupWhole, Bounds)]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        var prepared = frame.WithOverlayOutput([OwnedDot("read", "blue", new(0, 0, 1, 1))], []).WithImageEffects([
            OwnedBind("first", first, "a"), OwnedBind("read", second, "b"), OwnedBind("parent", GroupRoot(request), "c")]);
        using var actual = renderer.Render(request with { PreparedFrame = prepared });
        OwnedStripeNear(actual, spot, 0, 0, 204, 204);
    }

    [Fact]
    public void B11_重建不同owner下层不会重跑已执行作者图节点()
    {
        var request = OwnedRequest(); using var device = new OwnedObservedDevice();
        using var a = OwnedProbe("a", 480, device: device); using var b = OwnedProbe("b", 510, device: device); using var c = OwnedProbe("c", 540, device: device);
        using var resources = new SceneImageEffectResources([OwnedProgram("a", a), OwnedProgram("b", b), OwnedProgram("c", c)]);
        using var budget = new ImageSurfaceBudgetSession(); using var renderer = new SkiaRenderPipeline(null, null, budget, resources, false, device, false);
        int nodes = 0;
        device.BeforeCreate = () => { if (Environment.StackTrace.Contains("DrawNode", StringComparison.Ordinal)) nodes++; };
        var prepared = request.PreparedFrame!.WithOverlayOutput([OwnedDot("A", "A", new(1, 0, 0, .5)), OwnedDot("B", "B", new(0, 1, 0, .5))], [])
            .WithImageEffects([OwnedBind("A", Line(request), "a"), OwnedBind("B", Line(request), "b"), OwnedBind("C", Line(request), "c")]);
        using var frame = renderer.Render(request with { PreparedFrame = prepared });
        Assert.Equal(6, nodes); // Three author graphs, each one Color and one E node, once.
        Assert.Empty(frame.ImageEffectDiagnostics); Assert.Equal(0, budget.Usage.ActiveSurfaces);
    }

    private sealed class OwnedBackground : IBackgroundFrameSourceResolver, IBackgroundFrameSource
    {
        private readonly SKBitmap _bitmap = new(640, 360);
        internal int Frames { get; private set; }
        internal OwnedBackground() => _bitmap.Erase(new SKColor(50, 100, 100, 255));
        public IBackgroundFrameSource Resolve(BackgroundSource background) => this;
        public SKBitmap GetFrame(long timeUs) { Frames++; return _bitmap; }
        public void Dispose() => _bitmap.Dispose();
    }
    private sealed class OwnedObservedDevice : ISkiaRenderDevice
    {
        private readonly CpuSkiaRenderDevice _inner = new();
        internal Action? BeforeCreate { get; set; }
        public object SynchronizationRoot => _inner.SynchronizationRoot;
        public SkiaRenderDeviceInfo Info => _inner.Info;
        public SKSurface CreateSurface(SKImageInfo info) { BeforeCreate?.Invoke(); return _inner.CreateSurface(info); }
        public void FlushAndSubmit(SKSurface surface) => _inner.FlushAndSubmit(surface);
        public SKImage? RasterizeImage(SKImage image) => _inner.RasterizeImage(image);
        public bool ReadPixels(SKSurface surface, SKImageInfo info, IntPtr destination, int rowBytes) => _inner.ReadPixels(surface, info, destination, rowBytes);
        public bool ReadPixels(SKImage image, SKImageInfo info, IntPtr destination, int rowBytes) => _inner.ReadPixels(image, info, destination, rowBytes);
        public void Dispose() => _inner.Dispose();
    }
}
