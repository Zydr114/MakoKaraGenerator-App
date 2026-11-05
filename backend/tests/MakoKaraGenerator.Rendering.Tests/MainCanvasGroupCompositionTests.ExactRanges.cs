using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

// Main-canvas regressions for complete and partial group ranges, Below,
// independent erasure and failure recovery.
public sealed partial class MainCanvasImageEffectsTests
{
    [Theory]
    [InlineData(true, 480, 180)] [InlineData(false, 640, 360)]
    public void Exact1_partial成功后whole读取修改后的C与继承E而不能复用旧aggregate(bool transparent, int width, int height)
    {
        var request = Request(Snapshot(), transparent, width, height);
        using var partial = Tint("range-partial", "half4(c.b,c.r,c.g,c.a)");
        using var identity = GroupIdentity();
        using var resources = new SceneImageEffectResources([Program("first", partial, Fill with { IncludeDescendantGroups = true }), Program("last", identity, GroupWhole)]);
        using var budget = new ImageSurfaceBudgetSession();
        using var renderer = new SkiaRenderPipeline(null, null, budget, resources);
        var group = GroupRoot(request);
        using var original = renderer.Render(request);
        var partialRequest = Bindings(request, Bind("range-partial", group));
        using var changed = renderer.Render(partialRequest);
        using var actual = renderer.Render(Bindings(request, Bind("range-partial", group), Bind("range-whole", group, "last")));
        // A stale complete leaf range would restore the unmodified blue base fill.
        Assert.True(original.Pixels.ToArray().Zip(changed.Pixels.ToArray()).Count(pair => Math.Abs(pair.First - pair.Second) > 8) > 20);
        Close(changed.Pixels.ToArray(), actual.Pixels.ToArray(), 2);
        Assert.Empty(actual.ImageEffectDiagnostics);
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }

    [Fact]
    public void Exact2_透明whole旁路不失效完整范围也不阻止后继partial成功()
    {
        var request = Request(Snapshot(), transparent: true, width: 480, height: 180);
        var definition = new ImageGraphDefinition("range-background", ImageGraphDefinition.CurrentVersion,
            [new("media", GraphInputSource.BackgroundMedia, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.Media, GraphCoordinateSpace.Canvas),
                new("color", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)], [], [],
            GraphReference.Input("media"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly, GraphCompositeMode.SourceOver,
            bypassInput: GraphReference.Input("color"));
        using var bypass = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [], []));
        using var partial = Tint("range-after-bypass", "half4(c.b,c.r,c.g,c.a)");
        using var resources = new SceneImageEffectResources([Program("first", bypass, GroupWhole), Program("last", partial, Fill with { IncludeDescendantGroups = true })]);
        using var budget = new ImageSurfaceBudgetSession();
        using var renderer = new SkiaRenderPipeline(null, null, budget, resources);
        var group = GroupRoot(request);
        using var expected = renderer.Render(Bindings(request, Bind("range-partial", group, "last")));
        using var actual = renderer.Render(Bindings(request, Bind("range-bypass", group), Bind("range-partial", group, "last")));
        Close(expected.Pixels.ToArray(), actual.Pixels.ToArray(), 2);
        var diagnostic = Assert.Single(actual.ImageEffectDiagnostics);
        Assert.Equal("range-bypass", diagnostic.InstanceId.Value);
        Assert.Equal("media", diagnostic.InputId); Assert.Equal("TransparentBackgroundDependency", diagnostic.Code);
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }

    [Fact]
    public void Exact5_partial透明replacement之后wholeidentity保留真实E且不复用旧颜色范围()
    {
        var request = Request(Snapshot(), transparent: false, width: 480, height: 180);
        var nodeBounds = new GraphNodeBounds(new GraphExpansion.Literal(0), 0, 0, GraphEdgeMode.Transparent);
        var color = new ShaderProgramDefinition("range-transparent.sksl",
            "uniform shader color; half4 main(float2 p) { return color.eval(p)*0; }",
            ShaderProgramDefinition.RasterProfile, [], [new("color", ShaderSurfaceKind.Color)]);
        var mask = new ShaderProgramDefinition("range-mask.sksl", "half4 main(float2 p) { return half4(0,0,0,.8); }",
            ShaderProgramDefinition.RasterProfile, [], []);
        var definition = new ImageGraphDefinition("range-partial-replace", ImageGraphDefinition.CurrentVersion,
            [new("color", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)], [],
            [new ImageGraphNode.RuntimeShader("out", "out", ShaderSurfaceKind.Color, [], [new("color", GraphReference.Input("color"))], nodeBounds),
                new ImageGraphNode.RuntimeShader("mask", "mask", ShaderSurfaceKind.Coverage, [], [], nodeBounds)],
            GraphReference.Node("out"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly,
            GraphCompositeMode.ReplaceCoveredBackdrop, GraphReference.Node("mask"));
        using var partial = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [], [new("out", color), new("mask", mask)]));
        using var identity = GroupIdentity();
        using var resources = new SceneImageEffectResources([Program("first", partial, Fill with { IncludeDescendantGroups = true }), Program("last", identity, GroupWhole)]);
        using var budget = new ImageSurfaceBudgetSession();
        using var renderer = new SkiaRenderPipeline(null, null, budget, resources);
        var group = GroupRoot(request);
        using var expected = renderer.Render(Bindings(request, Bind("range-partial-replace", group)));
        using var actual = renderer.Render(Bindings(request, Bind("range-partial-replace", group), Bind("range-whole", group, "last")));
        Close(expected.Pixels.ToArray(), actual.Pixels.ToArray(), 2);
        // Far outside glyphs the partial output remains transparent but its
        // independent .8 erase affects real B and must survive the wider parent.
        Assert.InRange(expected.Pixels[3], (byte)50, (byte)52);
        Assert.InRange(actual.Pixels[3], (byte)50, (byte)52);
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }

    [Theory]
    [InlineData(true, 480, 180)] [InlineData(false, 640, 360)]
    public void Exact3_完整identity经child和parent共同alpha仍匹配实际隔离主画布(bool transparent, int width, int height)
    {
        var request = Request(Snapshot(), transparent, width, height);
        var program = request.Project.LyricPresentation.FindProgram("primary")!;
        var inner = new Fx.ImageEffectTarget.Group(new(new(program.DefaultContainerId)));
        var root = GroupRoot(request);
        var alpha = request with { PreparedFrame = request.PreparedFrame!.WithGroupOpacities([
            new(new("range-inner-alpha"), "alpha-inner", new(inner.Target, .5)),
            new(new("range-root-alpha"), "alpha-root", new(root.Target, .5))]) };
        using var identity = GroupIdentity();
        using var resources = new SceneImageEffectResources([Program("first", identity, GroupWhole)]);
        using var budget = new ImageSurfaceBudgetSession();
        using var renderer = new SkiaRenderPipeline(null, null, budget, resources);
        // This reference is the pre-existing isolated group-alpha renderer: no
        // image graph requests activate the new composition/prefix scheduler.
        using var expected = renderer.Render(alpha);
        using var actual = renderer.Render(Bindings(alpha, Bind("range-child", inner), Bind("range-root", root)));
        Close(expected.Pixels.ToArray(), actual.Pixels.ToArray(), 2);
        Assert.Empty(actual.ImageEffectDiagnostics);
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }

    [Theory]
    [InlineData("cancel")] [InlineData("native")] [InlineData("budget")]
    public void Exact4_child复制中段失败保留真实parent归属且释放全部aggregate并可恢复(string failure)
    {
        var request = Request(Snapshot(), transparent: true, width: 480, height: 180);
        var program = request.Project.LyricPresentation.FindProgram("primary")!;
        var inner = new Fx.ImageEffectTarget.Group(new(new(program.DefaultContainerId)));
        var bound = Bindings(request, Bind("range-child", inner), Bind("range-root", GroupRoot(request)));
        using var identity = GroupIdentity();
        using var resources = new SceneImageEffectResources([Program("first", identity, GroupWhole)]);
        using var budget = new ImageSurfaceBudgetSession();
        using var device = new ExactRangeObservedDevice();
        using var renderer = new SkiaRenderPipeline(null, null, budget, resources, false, device, false);
        using var expected = renderer.Render(bound);
        using var cancellation = new CancellationTokenSource();
        ImageSurfaceBudgetFrame? pressureFrame = null;
        ImageSurfaceReservation? pressure = null;
        int copyCreates = 0; bool injected = false;
        device.BeforeCreate = () =>
        {
            if (!Environment.StackTrace.Contains("AppendClosedGroupLayers", StringComparison.Ordinal)) return;
            // One pair has already been copied and parent-owned. This is the
            // foreground allocation of the following pair, after its preflight.
            if (++copyCreates != 3) return;
            injected = true;
            if (failure == "cancel") cancellation.Cancel();
            else if (failure == "native") throw new InvalidOperationException("range-copy-native-fixture");
            else
            {
                // Real shared-session pressure, not a manually thrown exception.
                // This reservation owns no pixel image; the following C/E Raster
                // must reject before another allocation can exceed session bytes.
                pressureFrame = budget.BeginFrame();
                pressure = pressureFrame.Reserve(1, budget.Limits.MaximumBytes - budget.Usage.EstimatedBytes, "ExactRangeFixturePressure");
            }
        };
        try
        {
            if (failure == "cancel")
                Assert.Throws<OperationCanceledException>(() => renderer.Render(bound with { CancellationToken = cancellation.Token }));
            else
            {
                var error = Assert.Throws<EffectFailureException>(() => renderer.Render(bound));
                Assert.Equal("range-root", error.InstanceId.Value); Assert.Equal("artifact-range-root", error.ArtifactHash);
                Assert.Equal(failure == "budget" ? "ImageSurfaceBudgetExceeded" : "SceneBackdropFailed", error.Code);
                if (failure == "budget") Assert.IsType<ImageSurfaceBudgetException>(error.InnerException);
            }
            Assert.True(injected, "The failure must occur inside a real child -> parent payload copy.");
        }
        finally { device.BeforeCreate = null; pressure?.Dispose(); pressureFrame?.Dispose(); }
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
        using var recovered = renderer.Render(bound);
        Close(expected.Pixels.ToArray(), recovered.Pixels.ToArray(), 0);
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }

    private sealed class ExactRangeObservedDevice : ISkiaRenderDevice
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
