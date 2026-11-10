using System.Text.Json.Nodes;
using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class MaterialAuthorGraphCacheTests
{
    private static readonly GraphRect Bounds = new(0, 0, 16, 12);
    private const long SurfaceBytes = 16 * 12 * 4;
    private static readonly Dictionary<string, SkiaGraphInput> NoInputs = [];
    private static EffectPackageDefinition Definition(bool declaredInput = false, bool masked = false, string? source = null, bool blur = false)
        => EffectPackageDefinition.Read(MixedEffectPackageFixture.Contents(pure: true,
            manifestChange: manifest => manifest["exports"]![0]!["coverageMode"] = masked ? "masked" : "unmasked",
            graphChange: graph =>
            {
                if (!declaredInput) graph["inputs"] = new JsonArray();
                graph["nodes"]![0]!["children"] = new JsonObject();
                if (blur)
                {
                    graph["nodes"]!.AsArray().Add(new JsonObject { ["id"] = "blur", ["op"] = "Blur", ["input"] = "node:probe", ["sigmaPx"] = 1,
                        ["bounds"] = new JsonObject { ["expandPx"] = 3, ["maxExpandPx"] = 3, ["maxSampleOffsetPx"] = 3 }, ["edge"] = "transparent" });
                    graph["bounds"]!["maxExpandPx"] = 3; graph["output"] = "node:blur";
                }
            }, programChange: program => program["children"] = new JsonArray(),
            shaderSource: source ?? "uniform float amount; half4 main(float2 p) { return half4(amount,0,0,1); }"));
    private static ParameterValues Parameters(double amount = .5) => new([new("amount", new ParameterValue.Number(amount))]);
    private static ImageGraphFrameContext Frame(double amount = .5) => new(1_000_000, 16, 12, Bounds, 1, 1, Parameters(amount), false);
    private static SkiaGraphSurface Surface(ISkiaRenderDevice device, byte alpha = 255, bool coverage = true)
    {
        using var srgb = SKColorSpace.CreateSrgb();
        using var native = device.CreateSurface(new SKImageInfo(16, 12, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
        native.Canvas.Clear(new SKColor(0, 0, 0, alpha)); device.FlushAndSubmit(native);
        return SkiaGraphSurface.Own(native.Snapshot(), new(Bounds, GraphCoordinateSpace.Canvas, SceneMatrix.Identity, 1, 1),
            coverage ? ShaderSurfaceKind.Coverage : ShaderSurfaceKind.Color, device);
    }
    private static ImageGraphResult Execute(CompiledPackageGraphExports package, ImageGraphFrameContext context, SkiaGraphSurface region,
        SkiaGraphSurface fallback, ImageSurfaceBudgetFrame frame, MaterialAuthorGraphCache? cache, double opacity = 1,
        CancellationToken cancellationToken = default)
        => cache is null ? package.ExecuteMaterial(package.Resources.GetMaterial("ink"), SceneCaptureRegion.Fill, context, region, fallback,
            NoInputs, opacity, frame, cancellationToken)
            : package.ExecuteMaterialShared(package.Resources.GetMaterial("ink"), SceneCaptureRegion.Fill, context, region, fallback,
                NoInputs, opacity, frame, cache, cancellationToken);
    private static byte[] Pixels(SkiaGraphSurface surface) => SkiaTestReadback.Pixels(surface.Image);
    private static void Empty(ImageSurfaceBudgetSession budget)
    { Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes); }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SixteenTargetsShareOneAuthorRasterAndKeepFullPixelsDiagnosticsAndOpacity(bool gpu)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        using var device = new CountingDevice(gpu);
        using var package = CompiledPackageGraphExports.CompileWithDevice(Definition(), null, device);
        using var region = Surface(device, 173); using var fallback = Surface(device, coverage: false);
        using var baselineBudget = new ImageSurfaceBudgetSession(); using var baselineFrame = baselineBudget.BeginFrame();
        List<byte[]> expected = [];
        device.Created = 0;
        for (int i = 0; i < 16; i++)
        {
            using var result = Execute(package, Frame(), region, fallback, baselineFrame, null, (i + 1) / 16d);
            expected.Add(Pixels(result.Output)); Assert.Empty(result.Diagnostics);
        }
        Assert.Equal(32, device.Created); Empty(baselineBudget);
        using var budget = new ImageSurfaceBudgetSession(); using var frame = budget.BeginFrame(); using var cache = new MaterialAuthorGraphCache(frame);
        device.Created = 0;
        for (int i = 0; i < 16; i++)
        {
            using var result = Execute(package, Frame(), region, fallback, frame, cache, (i + 1) / 16d);
            Assert.Equal(expected[i], Pixels(result.Output)); Assert.Empty(result.Diagnostics);
        }
        Assert.Equal(17, device.Created); Assert.Equal(1, budget.Usage.ActiveSurfaces); Assert.Equal(SurfaceBytes, budget.Usage.EstimatedBytes);
        cache.Dispose(); Empty(budget);
        using var nextCache = new MaterialAuthorGraphCache(frame);
        using (var next = Execute(package, Frame(), region, fallback, frame, nextCache)) Assert.Equal(19, device.Created);
        nextCache.Dispose(); Empty(budget);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TightSurfaceAndExactByteBoundariesPreserveBaselineFirstFailureAndRelease(bool gpu)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        using var device = new CountingDevice(gpu);
        using var package = CompiledPackageGraphExports.CompileWithDevice(Definition(), null, device);
        using var region = Surface(device, 173); using var fallback = Surface(device, coverage: false);
        foreach (int surfaces in Enumerable.Range(1, 10))
            Compare(new() { MaximumSurfaces = surfaces });
        foreach (long bytes in Enumerable.Range(1, 9).SelectMany(i => new[] { SurfaceBytes * i - 1, SurfaceBytes * i, SurfaceBytes * i + 1 }))
            Compare(new() { MaximumBytes = bytes });
        void Compare(GraphExecutionBudget limits)
        {
            var expected = Run(limits, false); var actual = Run(limits, true);
            Assert.Equal(expected.Failure, actual.Failure); Assert.Equal(expected.Pixels.Count, actual.Pixels.Count);
            for (int i = 0; i < expected.Pixels.Count; i++) Assert.Equal(expected.Pixels[i], actual.Pixels[i]);
        }
        (string? Failure, List<byte[]> Pixels) Run(GraphExecutionBudget limits, bool shared)
        {
            using var budget = new ImageSurfaceBudgetSession(limits); using var frame = budget.BeginFrame();
            using var cache = shared ? new MaterialAuthorGraphCache(frame) : null;
            List<ImageGraphResult> kept = []; List<byte[]> pixels = []; string? failure = null;
            try
            {
                for (int i = 0; i < 8; i++)
                {
                    using var capture = frame.Reserve(2, SurfaceBytes * 2, "CaptureTargetPair");
                    var result = Execute(package, Frame(), region, fallback, frame, cache);
                    kept.Add(result); pixels.Add(Pixels(result.Output));
                }
            }
            catch (ImageSurfaceBudgetException error)
            { failure = $"{error.Operation}/{error.CurrentSurfaces}/{error.AdditionalSurfaces}/{error.CurrentBytes}/{error.AdditionalBytes}"; }
            finally { cache?.Dispose(); foreach (var result in kept) result.Dispose(); }
            Empty(budget); return (failure, pixels);
        }
    }

    [Theory]
    [InlineData("time")] [InlineData("canvasWidth")] [InlineData("canvasHeight")] [InlineData("bounds")]
    [InlineData("densityX")] [InlineData("densityY")] [InlineData("transparent")] [InlineData("parameter")]
    [InlineData("uniform")] [InlineData("uniformSignedZero")] [InlineData("parameterSignedZero")]
    public void AnyObservableFrameDependencyPreventsReuse(string change)
    {
        using var package = CompiledPackageGraphExports.Compile(Definition());
        var graph = package.GetMaterialGraph(package.Resources.GetMaterial("ink"));
        var basis = Frame(0) with { Uniforms = new([new(("probe", "amount"), UniformValue.Float(0))]) };
        var changed = change switch
        {
            "time" => basis with { TimeUs = basis.TimeUs + 1 },
            "canvasWidth" => basis with { CanvasWidth = 17 },
            "canvasHeight" => basis with { CanvasHeight = 13 },
            "bounds" => basis with { TargetBounds = new(1, 0, 17, 12) },
            "densityX" => basis with { DensityX = 2 },
            "densityY" => basis with { DensityY = 2 },
            "transparent" => basis with { Transparent = true },
            "parameter" => basis with { Parameters = Parameters(.25) },
            "parameterSignedZero" => basis with { Parameters = Parameters(BitConverter.Int64BitsToDouble(long.MinValue)) },
            "uniformSignedZero" => basis with { Uniforms = new([new(("probe", "amount"), UniformValue.Float(BitConverter.Int64BitsToDouble(long.MinValue)))]) },
            _ => basis with { Uniforms = new([new(("probe", "amount"), UniformValue.Float(.25))]) },
        };
        Assert.False(MaterialAuthorGraphCache.MatchesFrame(graph, basis, changed));
        Assert.True(MaterialAuthorGraphCache.MatchesFrame(graph, basis, basis with { Parameters = Parameters(0),
            Uniforms = new([new(("probe", "amount"), UniformValue.Float(0))]) }));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CancellationCoverageFailureFrameCloseAndGraphDisposeCannotLeaveReusableResult(bool gpu)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        using var device = new CountingDevice(gpu);
        using var package = CompiledPackageGraphExports.CompileWithDevice(Definition(masked: true), null, device);
        using var region = Surface(device); using var wrongRegion = Surface(device, 0); using var fallback = Surface(device, coverage: false);
        using var budget = new ImageSurfaceBudgetSession(); using var frame = budget.BeginFrame(); using var cache = new MaterialAuthorGraphCache(frame);
        using (var first = Execute(package, Frame(), region, fallback, frame, cache)) { }
        Assert.Equal(1, budget.Usage.ActiveSurfaces);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => Execute(package, Frame(), region, fallback, frame, cache, cancellationToken: cancel.Token)); Empty(budget);
        using (var retry = Execute(package, Frame(), region, fallback, frame, cache)) { }
        Assert.Equal("MaterialOutputOutsideRegion", Assert.Throws<ImageGraphException>(() => Execute(package, Frame(), wrongRegion, fallback, frame, cache)).Code); Empty(budget);
        using (var retry = Execute(package, Frame(), region, fallback, frame, cache)) { }
        frame.Dispose(); Empty(budget);
        Assert.Throws<ObjectDisposedException>(() => Execute(package, Frame(), region, fallback, frame, cache)); Empty(budget);
        cache.Dispose(); Assert.Throws<ObjectDisposedException>(() => cache.Take(package.GetMaterialGraph(package.Resources.GetMaterial("ink")), Frame()));
    }

    [Fact]
    public void DeclaredUnusedInputAndOversizeOutputAreNeverRetained()
    {
        using var device = new CountingDevice(); using var region = Surface(device); using var fallback = Surface(device, coverage: false);
        using var package = CompiledPackageGraphExports.CompileWithDevice(Definition(declaredInput: true), null, device);
        using var budget = new ImageSurfaceBudgetSession(); using var frame = budget.BeginFrame(); using var cache = new MaterialAuthorGraphCache(frame);
        device.Created = 0;
        for (int i = 0; i < 2; i++) using (var result = Execute(package, Frame(), region, fallback, frame, cache)) { }
        Assert.Equal(4, device.Created); Empty(budget);
        using var largePackage = CompiledPackageGraphExports.CompileWithDevice(Definition(), null, device);
        var graph = largePackage.GetMaterialGraph(largePackage.Resources.GetMaterial("ink"));
        using var raw = graph.Execute(Frame() with { TargetBounds = new(0, 0, 2049, 2048) }, NoInputs, frame);
        Assert.False(cache.TryStore(graph, raw)); raw.Dispose(); Empty(budget);
        using var exact = graph.Execute(Frame() with { TargetBounds = new(0, 0, 2048, 2048) }, NoInputs, frame);
        Assert.True(cache.TryStore(graph, exact)); Assert.Equal(16 * 1024 * 1024, budget.Usage.EstimatedBytes);
        cache.Clear(); Empty(budget);
    }



    [Theory]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    [InlineData(false, "readback")] [InlineData(true, "readback")]
    [InlineData(false, "store")] [InlineData(true, "store")]
    public void FailureAfterCacheHitReleasesAuthorAppliedOutputAndReservation(bool gpu, string failure)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        using var device = new CountingDevice(gpu);
        using var package = CompiledPackageGraphExports.CompileWithDevice(Definition(), null, device);
        using var region = Surface(device); using var fallback = Surface(device, coverage: false);
        using var budget = new ImageSurfaceBudgetSession(); using var frame = budget.BeginFrame(); using var cache = new MaterialAuthorGraphCache(frame);
        using (var first = Execute(package, Frame(), region, fallback, frame, cache)) { }
        using var cancel = new CancellationTokenSource();
        if (failure == "cancel") device.BeforeCreate = cancel.Cancel;
        else if (failure == "readback") device.FailRead = true;
        else device.AfterRead = frame.Dispose;
        if (failure == "cancel") Assert.Throws<OperationCanceledException>(() => Execute(package, Frame(), region, fallback, frame, cache, cancellationToken: cancel.Token));
        else if (failure == "store") Assert.Throws<ObjectDisposedException>(() => Execute(package, Frame(), region, fallback, frame, cache));
        else Assert.Equal("GraphPixelsUnavailable", Assert.Throws<ImageGraphException>(() => Execute(package, Frame(), region, fallback, frame, cache)).Code);
        device.BeforeCreate = null; device.AfterRead = null; device.FailRead = false; Empty(budget);
        if (failure != "store")
        {
            using (var retry = Execute(package, Frame(), region, fallback, frame, cache)) { }
            package.Dispose();
            Assert.Throws<ObjectDisposedException>(() => Execute(package, Frame(), region, fallback, frame, cache)); Empty(budget);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ChangedUniformMustExecuteAndRejectInvalidAuthorPixelsEvenAtZeroOpacity(bool gpu)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        using var device = new CountingDevice(gpu);
        using var package = CompiledPackageGraphExports.CompileWithDevice(Definition(source: "uniform float amount; half4 main(float2 p) { return half4(amount,0,0,.25); }"), null, device);
        using var region = Surface(device); using var fallback = Surface(device, coverage: false);
        using var budget = new ImageSurfaceBudgetSession(); using var frame = budget.BeginFrame(); using var cache = new MaterialAuthorGraphCache(frame);
        var valid = Frame(.1) with { Uniforms = new([new(("probe", "amount"), UniformValue.Float(.1))]) };
        using (var first = Execute(package, valid, region, fallback, frame, cache)) { }
        var invalid = valid with { Uniforms = new([new(("probe", "amount"), UniformValue.Float(1))]) };
        Assert.Equal("GraphColorNotPremultiplied", Assert.Throws<ImageGraphException>(() => Execute(package, invalid, region, fallback, frame, cache, opacity: 0)).Code);
        Empty(budget);
    }


    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SharedMultiNodeAuthorMustRecheckOriginalScratchPeakAndFirstError(bool gpu)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        using var device = new CountingDevice(gpu);
        using var package = CompiledPackageGraphExports.CompileWithDevice(Definition(blur: true), null, device);
        using var region = Surface(device); using var fallback = Surface(device, coverage: false);
        foreach (int count in Enumerable.Range(1, 12)) Compare(new() { MaximumSurfaces = count });
        foreach (long bytes in Enumerable.Range(1, 12).SelectMany(i => new[] { SurfaceBytes * i - 1, SurfaceBytes * i, SurfaceBytes * i + 1 }))
            Compare(new() { MaximumBytes = bytes });
        void Compare(GraphExecutionBudget limits)
        {
            var old = Run(false, limits); var shared = Run(true, limits);
            Assert.Equal(old.Failure, shared.Failure); Assert.Equal(old.Pixels.Count, shared.Pixels.Count);
            for (int i = 0; i < old.Pixels.Count; i++) Assert.Equal(old.Pixels[i], shared.Pixels[i]);
        }
        (string? Failure, List<byte[]> Pixels) Run(bool shared, GraphExecutionBudget limits)
        {
            using var budget = new ImageSurfaceBudgetSession(limits); using var frame = budget.BeginFrame(); using var cache = shared ? new MaterialAuthorGraphCache(frame) : null;
            List<ImageGraphResult> kept = []; List<byte[]> pixels = []; string? failure = null;
            try
            {
                for (int i = 0; i < 6; i++)
                { var result = Execute(package, Frame(), region, fallback, frame, cache); kept.Add(result); pixels.Add(Pixels(result.Output)); }
            }
            catch (ImageSurfaceBudgetException error)
            { failure = $"{error.Operation}/{error.CurrentSurfaces}/{error.AdditionalSurfaces}/{error.CurrentBytes}/{error.AdditionalBytes}"; }
            finally { cache?.Dispose(); foreach (var result in kept) result.Dispose(); }
            Empty(budget); return (failure, pixels);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MainRenderSharesAcrossEightTokensAndReleasesBetweenFrames(bool gpu)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        using var device = new CountingDevice(gpu);
        var expected = Render(true, null); var actual = Render(false, null);
        Assert.Null(expected.Failure); Assert.Null(actual.Failure); Assert.Equal(expected.Pixels, actual.Pixels);
        // 无注音行只执行 8 个基础字填充（F3 起注音填充不再执行），共享省下其中 7 次作者图输出。
        Assert.Equal(7, expected.Created - actual.Created);
        var repeat = Render(false, null); Assert.Equal(actual.Pixels, repeat.Pixels); Assert.Equal(actual.Created, repeat.Created);
        foreach (int maximum in Enumerable.Range(1, 64))
            Compare(new() { MaximumSurfaces = maximum });
        const long bytes = 256 * 96 * 4;
        foreach (long maximum in Enumerable.Range(1, 64).SelectMany(i => new[] { bytes * i - 1, bytes * i, bytes * i + 1 }))
            Compare(new() { MaximumBytes = maximum });
        void Compare(GraphExecutionBudget limits)
        {
            var old = Render(true, limits); var shared = Render(false, limits);
            Assert.Equal(old.Failure, shared.Failure); Assert.Equal(old.Pixels, shared.Pixels);
        }
        (string? Failure, byte[]? Pixels, int Created) Render(bool declaredInput, GraphExecutionBudget? limits)
        {
            using var package = CompiledPackageGraphExports.CompileWithDevice(Definition(declaredInput), null, device);
            var tokens = Enumerable.Range(0, 8).Select(i => LyricToken.Create("token-" + i, "字", new(i * 500_000L, (i + 1) * 500_000L), TimingStatus.Edited));
            var line = LyricLine.Create("line", "字字字字字字字字", new(0, 5_000_000), TimingStatus.Edited, tokens);
            var project = Project.Create("author sharing", Composition.Create(256, 96, Core.Time.FrameRate.Fps30, outputDurationUs: 5_000_000),
                LyricsDocument.Create([line]), lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 20, Stroke = null, Shadow = null });
            var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = "author-sharing" };
            using var budget = new ImageSurfaceBudgetSession(limits);
            using var resources = new SceneImageEffectResources([], null, [package.CreateMaterialProgram(package.Resources.GetMaterial("ink"), new(0, 0, 256, 96))]);
            using var renderer = new SkiaRenderPipeline(null, null, budget, resources, false, device, false);
            var scene = renderer.GetSceneFrame(snapshot, 1_000_000);
            ImmutableArray<TokenFamilyStyle>.Builder styles = ImmutableArray.CreateBuilder<TokenFamilyStyle>();
            ImmutableArray<PreparedMaterialBinding>.Builder bindings = ImmutableArray.CreateBuilder<PreparedMaterialBinding>();
            foreach (var geometry in scene.TokenGeometries)
            {
                var material = Assert.IsType<MaterialValue.PackageMaterial>(MaterialValue.PackageExport(package.Resources.GetMaterial("ink"), Parameters()));
                styles.Add(new(geometry.WithRuby, new(unsung: material)));
                bindings.Add(new(new EffectInstanceId("owner-" + geometry.WithRuby.TokenId.Value), "artifact", new ImageEffectTarget.Token(geometry.WithRuby), PreparedMaterialSlot.UnsungFill, material));
            }
            var target = RenderTarget.FromComposition(snapshot.Composition);
            var prepared = new PreparedRenderFrame(snapshot, scene.TimeUs, OutputMode.Transparent, target, scene, [], styles.ToImmutable(), [], [], [], [], [], bindings.ToImmutable());
            byte[]? pixels = null; string? failure = null; device.Created = 0;
            try
            {
                using var rendered = renderer.Render(RenderRequest.At(snapshot, scene.TimeUs) with { Target = target, TransparentBackground = true, PreparedFrame = prepared });
                pixels = rendered.PixelMemory.ToArray(); Assert.Empty(rendered.MaterialDiagnostics); Assert.Empty(rendered.ImageEffectDiagnostics);
            }
            catch (EffectFailureException error)
            {
                failure = $"{error.InstanceId.Value}/{error.Stage}/{error.Code}/" + (error.InnerException is ImageSurfaceBudgetException b
                    ? $"{b.Operation}/{b.CurrentSurfaces}/{b.AdditionalSurfaces}/{b.CurrentBytes}/{b.AdditionalBytes}" : error.InnerException?.Message);
            }
            Empty(budget); return (failure, pixels, device.Created);
        }
    }

    private sealed class CountingDevice(bool gpu = false) : ISkiaRenderDevice
    {
        private readonly ISkiaRenderDevice _inner = SkiaRenderDeviceFactory.Create(new(gpu ? SkiaRenderBackend.Vulkan : SkiaRenderBackend.Cpu));
        internal int Created { get; set; }
        internal Action? BeforeCreate { get; set; }
        internal Action? AfterRead { get; set; }
        internal bool FailRead { get; set; }
        public SkiaRenderDeviceInfo Info => _inner.Info;
        public object SynchronizationRoot => _inner.SynchronizationRoot;
        public SKSurface CreateSurface(SKImageInfo info) { Created++; BeforeCreate?.Invoke(); return _inner.CreateSurface(info); }
        public void FlushAndSubmit(SKSurface surface) => _inner.FlushAndSubmit(surface);
        public SKImage? RasterizeImage(SKImage image) => _inner.RasterizeImage(image);
        public bool ReadPixels(SKSurface surface, SKImageInfo info, IntPtr destination, int rowBytes) => _inner.ReadPixels(surface, info, destination, rowBytes);
        public bool ReadPixels(SKImage image, SKImageInfo info, IntPtr destination, int rowBytes) => _inner.ReadPixels(image, info, destination, rowBytes);
        public bool ReadOwnedImagePixels(SKImage image, SKImageInfo info, IntPtr destination, int rowBytes)
        {
            if (FailRead) return false;
            bool success = _inner.ReadOwnedImagePixels(image, info, destination, rowBytes); AfterRead?.Invoke(); return success;
        }
        public void Dispose() => _inner.Dispose();
    }
}
