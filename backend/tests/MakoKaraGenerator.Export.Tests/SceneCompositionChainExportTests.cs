using System.Diagnostics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using SkiaSharp;

namespace MakoKaraGenerator.Export.Tests;

/// <summary>真实源码／图片／字形的主 Render 编码和已有独立合成错误保护。</summary>
public sealed class SceneCompositionChainExportTests
{
    private static readonly string[] GraphKinds = ["glass", "glitch"];
    private const string Fingerprint = "scene-chain-export";
    private static readonly ResourceCatalog Resources = new("scene-chain", "sha256:" + new string('e', 64), [], ["glass", "glitch"], []);
    private static readonly Lazy<Task<ScriptCompilationResult>> Script = new(async () =>
    {
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var result = await compiler.CompileAsync([new("src/Chain.cs", """
            using MakoKaraGenerator.Scripting;
            public sealed class Chain : EffectBase
            {
                public override void Evaluate(FrameContext frame, FrameWriter writer)
                {
                    double opacity = frame.Parameters.GetBoolean("fail") && frame.TimeUs >= 200000 ? 2 : 1;
                    var values = new ParameterValues(new[] { System.Collections.Generic.KeyValuePair.Create<string,ParameterValue>("opacity",new ParameterValue.Number(opacity)) });
                    foreach (var line in frame.Presentations.LinesInBinding)
                        writer.ImageEffects.Bind(line.Target, frame.Resources.GetGraph(frame.Parameters.GetEnum("kind")), values);
                }
            }
            """)]);
        Assert.True(result.Succeeded, string.Join('\n', result.Diagnostics)); return result;
    });
    private static Project Project(string path) => Core.Projects.Project.Create("合成链短片", Composition.Create(320, 180, FrameRate.Fps30, new(20, 40, 80), 1_000_000),
        LyricsDocument.Create([LyricLine.Create("row", "歌", new(0, 1_000_000), TimingStatus.Edited,
            [LyricToken.Create("token", "歌", new(0, 1_000_000), TimingStatus.Edited)]) with { RubyAnnotations = [RubyAnnotation.Create("ruby", "token", "か")] }]),
        lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 48, Opacity = .7f,
            UnsungColor = new(90, 220, 40), SungColor = new(230, 90, 50), Stroke = StrokeSpec.Create(new(40, 100, 255), 3), Shadow = null },
        background: BackgroundSource.ForImage(path));
    private static void WriteImage(string path)
    {
        using var bitmap = new SKBitmap(32, 32);
        for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++) bitmap.SetPixel(x, y, new((byte)(60 + x * 4), (byte)(80 + y * 3), 200));
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); using var file = File.Create(path); data.SaveTo(file);
    }
    private static async Task<IEvaluatedFrameSource> Source(ProjectSnapshot snapshot, bool fail)
    {
        var artifact = await Script.Value;
        var factory = new PureFrameSourceFactory([.. GraphKinds.Select(kind => new CompiledFrameEffect(new(kind), artifact, "Chain",
            new([new("kind", new ParameterValue.Choice(kind)), new("fail", new ParameterValue.Toggle(fail && kind == "glitch"))]),
            new EffectBinding.Project(), EffectCapabilities.ImageEffects) { Resources = Resources })], Fingerprint, static () => new SkiaRenderPipeline());
        return await factory.CreateAsync(snapshot);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 真实媒体玻璃接普通改色与透明旁路经MP4和Alpha编码保持同帧(bool transparent)
    {
        using TemporaryDirectory temp = new(); string media = temp.FilePath("media.png"); WriteImage(media);
        var project = Project(media); var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        await using var source = await Source(snapshot, false); using var pipeline = new ChainPipeline(source, snapshot, automatic: true);
        string output = temp.FilePath(transparent ? "chain.mov" : "chain.mp4"), decodedPath = temp.FilePath("decoded.png");
        var exporter = new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());
        var report = await exporter.ExportAsync(project, new() { OutputPath = output, IncludeAudio = false, Quality = ExportQuality.High,
            Format = transparent ? ExportVideoFormat.TransparentProRes4444 : ExportVideoFormat.OpaqueMp4 });
        Assert.True(report.IsVerified); Assert.Equal(0, report.FailedFrames);
        using var preview = pipeline.Render(RenderRequest.At(snapshot, 500_000) with { TransparentBackground = transparent });
        var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-i", output, "-vf", "select=eq(n\\,15)", "-frames:v", "1", "-y", decodedPath }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; string error = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, error);
        using var decoded = SKBitmap.Decode(decodedPath); double total = 0; int channels = 0, visible = 0;
        for (int y = 0; y < decoded.Height; y++) for (int x = 0; x < decoded.Width; x++)
        {
            Assert.True(preview.TryGetPixel(x, y, out var expected)); var actual = decoded.GetPixel(x, y);
            if (transparent) { Assert.InRange(Math.Abs(expected.Alpha - actual.Alpha), 0, 3); if (expected.Alpha < 24) continue; }
            if (expected.Alpha > 24) visible++;
            total += Math.Abs(expected.Red - actual.Red) + Math.Abs(expected.Green - actual.Green) + Math.Abs(expected.Blue - actual.Blue); channels += 3;
        }
        Assert.True(visible > 100); Assert.True(total / channels < 9, $"编码误差 {total / channels}");
        Assert.Equal(transparent ? 1 : 0, pipeline.BypassCount); Assert.Equal(0, pipeline.Usage.ActiveSurfaces); Assert.Equal(0, pipeline.Usage.EstimatedBytes);
        if (transparent) Assert.Equal(0, decoded.GetPixel(0, 0).Alpha);
    }
    [Fact]
    public async Task 合成阶段中途错误带最后实例身份阻止编码保护原输出并回收整个链()
    {
        using TemporaryDirectory temp = new(); string media = temp.FilePath("media.png"); WriteImage(media);
        string output = temp.FilePath("existing.mp4"); byte[] original = [2, 7, 1, 8]; await File.WriteAllBytesAsync(output, original);
        var project = Project(media); var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        await using var source = await Source(snapshot, true); using var pipeline = new ChainPipeline(source, snapshot);
        var exporter = new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());
        var error = await Assert.ThrowsAsync<EffectFailureException>(() => exporter.ExportAsync(project, new() { OutputPath = output, IncludeAudio = false, Overwrite = true }));
        Assert.Equal("InvalidGraphOpacity", error.Code); Assert.Equal("CompositeImageGraph", error.Stage); Assert.Equal(new EffectInstanceId("glitch"), error.InstanceId);
        Assert.Equal((await Script.Value).ArtifactHash, error.ArtifactHash); Assert.Equal(200_000, error.TimeUs); Assert.Equal(6, pipeline.RenderedCount);
        Assert.Equal(original, await File.ReadAllBytesAsync(output)); Assert.Equal(new[] { media, output }.Order(), Directory.GetFiles(temp.Path).Order());
        Assert.Equal(0, pipeline.Usage.ActiveSurfaces); Assert.Equal(0, pipeline.Usage.EstimatedBytes);
    }
    private sealed class ChainPipeline : IRenderPipeline, IDisposable
    {
        private readonly IEvaluatedFrameSource _source;
        private readonly ProjectSnapshot _snapshot;
        private readonly ImageSurfaceBudgetSession _budget = new();
        private readonly SkiaRenderPipeline _native;
        private readonly CompiledImageGraph _glass, _glitch;
        private readonly SceneImageGraphProgram _glassProgram, _glitchProgram;
        private readonly SceneImageEffectResources? _resources;
        private readonly bool _automatic;
        public string BackendName => "真实媒体和完整图链编码接缝";
        public int RenderedCount { get; private set; }
        public int BypassCount { get; private set; }
        public ImageSurfaceUsage Usage => _budget.Usage;
        public ChainPipeline(IEvaluatedFrameSource source, ProjectSnapshot snapshot, bool automatic = false)
        {
            _source = source; _snapshot = snapshot; _automatic = automatic;
            var bounds = new GraphNodeBounds(new GraphExpansion.Literal(0), 0, 0, GraphEdgeMode.Transparent);
            var input = new GraphInputDefinition("target", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas);
            var glass = new ImageGraphDefinition("glass", ImageGraphDefinition.CurrentVersion,
                [input, new("media", GraphInputSource.BackgroundMedia, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.Media, GraphCoordinateSpace.Canvas),
                    new("coverage", GraphInputSource.TargetRegionCoverage, ShaderSurfaceKind.Coverage, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)], [],
                [new ImageGraphNode.Mask("glass", GraphReference.Input("media"), GraphReference.Input("coverage"), bounds)], GraphReference.Node("glass"), GraphCoordinateSpace.Canvas, 0,
                GraphAnchorMode.ColorOnly, GraphCompositeMode.ReplaceCoveredBackdrop, GraphReference.Input("coverage"), GraphReference.Input("target"));
            _glass = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(glass, [], []));
            var shader = new ShaderProgramDefinition("glitch.sksl", "uniform shader target; half4 main(float2 p) { half4 c=target.eval(p); return half4(c.b,c.r,c.g,c.a); }",
                ShaderProgramDefinition.RasterProfile, [], [new("target", ShaderSurfaceKind.Color)]);
            var glitch = new ImageGraphDefinition("glitch", ImageGraphDefinition.CurrentVersion, [input], [],
                [new ImageGraphNode.RuntimeShader("glitch", "glitch", ShaderSurfaceKind.Color, [], [new("target", GraphReference.Input("target"))], bounds)],
                GraphReference.Node("glitch"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly, GraphCompositeMode.SourceOver);
            _glitch = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(glitch, [], [new("glitch", shader)]));
            var recipe = new SceneCaptureRecipe(true, true, true, true, SceneCaptureRegion.FillAndStroke);
            _glassProgram = new(Resources.GetGraph("glass"), _glass, recipe, new(0, 0, 320, 180));
            _glitchProgram = new(Resources.GetGraph("glitch"), _glitch, recipe, new(0, 0, 320, 180));
            if (automatic) _resources = new([_glassProgram, _glitchProgram]);
            _native = new(null, null, _budget, _resources);
        }
        public RenderFrame Render(RenderRequest request)
        {
            var target = request.ResolveTarget(); var mode = request.TransparentBackground ? OutputMode.Transparent : OutputMode.Opaque;
            var prepared = _source.PrepareAsync(request.TimeUs, mode, target, request.CancellationToken).AsTask().GetAwaiter().GetResult();
            request = request with { Project = _snapshot, PreparedFrame = prepared };
            if (_automatic)
            {
                var rendered = _native.Render(request); BypassCount = rendered.ImageEffectDiagnostics.Length; RenderedCount++; return rendered;
            }
            using var frame = _budget.BeginFrame();
            using var first = _native.ExecuteSceneImageEffect(request, prepared.ImageEffects[0], _glassProgram, new Dictionary<string, SkiaGraphInput>(), null, frame);
            using var last = _native.ExecuteSceneImageEffect(request, prepared.ImageEffects[1], _glitchProgram, new Dictionary<string, SkiaGraphInput>(), first.GraphResult.Output, frame);
            BypassCount = first.GraphResult.Diagnostics.Length;
            using var srgb = SKColorSpace.CreateSrgb(); using var surface = SKSurface.Create(new SKImageInfo(target.PixelWidth, target.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
            surface.Canvas.ClipRect(new(0, 0, target.PixelWidth, target.PixelHeight)); surface.Canvas.Clear(SKColors.Transparent);
            if (!request.TransparentBackground)
            {
                using var color = _native.CaptureSceneBackground(request, GraphInputSource.BackgroundColor, _glassProgram.Bounds, _glass.Plan.Budget, frame)!;
                using var media = _native.CaptureSceneBackground(request, GraphInputSource.BackgroundMedia, _glassProgram.Bounds, _glass.Plan.Budget, frame)!;
                surface.Canvas.DrawImage(color.Image, 0, 0); surface.Canvas.DrawImage(media.Image, 0, 0);
            }
            using var belowImage = surface.Snapshot(); using var below = SkiaGraphSurface.Borrow(belowImage,
                new(_glassProgram.Bounds, GraphCoordinateSpace.Canvas, SceneMatrix.Identity, target.PixelWidth / 320d, target.PixelHeight / 180d), ShaderSurfaceKind.Color);
            using var composite = _native.CompositeSceneImageEffectChain(request, [first, last], _glitchProgram, below, frame, prepared.ImageEffects[1].Binding.Parameters.GetNumber("opacity"));
            using var bitmap = SKBitmap.FromImage(composite.Image); RenderedCount++;
            return new(bitmap.Bytes, target, PixelFormat.Bgra8888Premultiplied, request.TimeUs);
        }
        public void Dispose() { _native.Dispose(); _resources?.Dispose(); _glass.Dispose(); _glitch.Dispose(); _budget.Dispose(); }
    }
}
