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

/// <summary>真实作者绑定及生产字形捕获的受控编码接缝，不代替完整场景输出调度。</summary>
public sealed class SceneCaptureExportTests
{
    private const string Fingerprint = "production-capture-export";
    private static readonly ResourceCatalog Resources = new("capture-export", "sha256:" + new string('c', 64), [], ["tint"], []);
    private static readonly Lazy<Task<ScriptCompilationResult>> Script = new(async () =>
    {
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var result = await compiler.CompileAsync([new("src/CaptureExport.cs", """
            using MakoKaraGenerator.Scripting;
            public sealed class CaptureExport : EffectBase
            {
                public override void Evaluate(FrameContext frame, FrameWriter writer)
                {
                    double amount = frame.Parameters.GetBoolean("fail") && frame.TimeUs >= 200000 ? 2 : 1;
                    double expand = frame.Parameters.GetBoolean("failBudget") && frame.TimeUs >= 200000 ? 3 : 0;
                    var values = new ParameterValues(new[] { System.Collections.Generic.KeyValuePair.Create<string, ParameterValue>("amount", new ParameterValue.Number(amount)),
                        System.Collections.Generic.KeyValuePair.Create<string, ParameterValue>("expand", new ParameterValue.Number(expand)) });
                    foreach (var line in frame.Presentations.LinesInBinding) writer.ImageEffects.Bind(line.Target, frame.Resources.GetGraph("tint"), values);
                }
            }
            """)]);
        Assert.True(result.Succeeded, string.Join('\n', result.Diagnostics)); return result;
    });
    private static Project Project() => Core.Projects.Project.Create("生产捕获短片", Composition.Create(320, 180, FrameRate.Fps30, new(20, 40, 80), 1_000_000),
        LyricsDocument.Create([LyricLine.Create("row", "歌", new(0, 1_000_000), TimingStatus.Edited,
            [LyricToken.Create("token", "歌", new(0, 1_000_000), TimingStatus.Edited)]) with { RubyAnnotations = [RubyAnnotation.Create("ruby", "token", "か")] }]),
        lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 48, Opacity = .7f,
            UnsungColor = new(90, 220, 40), SungColor = new(230, 90, 50), Stroke = StrokeSpec.Create(new(40, 100, 255), 3), Shadow = null });
    private static async Task<IEvaluatedFrameSource> Source(ProjectSnapshot snapshot, bool fail, bool failBudget = false)
    {
        var artifact = await Script.Value;
        var factory = new PureFrameSourceFactory([new(new("capture-instance"), artifact, "CaptureExport",
            new([new("fail", new ParameterValue.Toggle(fail)), new("failBudget", new ParameterValue.Toggle(failBudget))]), new EffectBinding.Project(), EffectCapabilities.ImageEffects) { Resources = Resources }],
            Fingerprint, static () => new SkiaRenderPipeline());
        return await factory.CreateAsync(snapshot);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 实际作者图绑定与生产歌词注音捕获经MP4和Alpha编码保持同帧(bool transparent)
    {
        using TemporaryDirectory temp = new(); var project = Project(); var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        await using var source = await Source(snapshot, false); using var pipeline = new CapturePipeline(source, snapshot);
        string output = temp.FilePath(transparent ? "lyrics.mov" : "lyrics.mp4"), decodedPath = temp.FilePath("decoded.png");
        var exporter = new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());
        var report = await exporter.ExportAsync(project, new() { OutputPath = output, IncludeAudio = false, Quality = ExportQuality.High,
            Format = transparent ? ExportVideoFormat.TransparentProRes4444 : ExportVideoFormat.OpaqueMp4 });
        Assert.True(report.IsVerified); Assert.Equal(0, report.FailedFrames);
        using var preview = pipeline.Render(RenderRequest.At(snapshot, 500_000) with { TransparentBackground = transparent });
        var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-i", output, "-vf", "select=eq(n\\,15)", "-frames:v", "1", "-y", decodedPath }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; string error = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, error);
        using var decoded = SKBitmap.Decode(decodedPath); double totalError = 0; int channels = 0, letters = 0;
        for (int y = 0; y < decoded.Height; y++) for (int x = 0; x < decoded.Width; x++)
        {
            Assert.True(preview.TryGetPixel(x, y, out var expected)); var actual = decoded.GetPixel(x, y);
            if (transparent) { Assert.InRange(Math.Abs(expected.Alpha - actual.Alpha), 0, 3); if (expected.Alpha < 24) continue; }
            if (expected.Red != 20 || expected.Green != 40 || expected.Blue != 80) letters++;
            totalError += Math.Abs(expected.Red - actual.Red) + Math.Abs(expected.Green - actual.Green) + Math.Abs(expected.Blue - actual.Blue); channels += 3;
        }
        Assert.True(letters > 100); Assert.True(totalError / channels < 9, $"编码误差 {totalError / channels}");
        if (transparent) Assert.Equal(0, decoded.GetPixel(0, 0).Alpha);
    }
    [Fact]
    public async Task 真实图执行中途错误带实例身份阻止编码保留原输出且无占位帧()
    {
        using TemporaryDirectory temp = new(); string output = temp.FilePath("existing.mp4"); byte[] original = [2, 7, 1, 8]; await File.WriteAllBytesAsync(output, original);
        var project = Project(); var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        await using var source = await Source(snapshot, true); using var pipeline = new CapturePipeline(source, snapshot);
        var exporter = new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());
        var error = await Assert.ThrowsAsync<EffectFailureException>(() => exporter.ExportAsync(project, new() { OutputPath = output, IncludeAudio = false, Overwrite = true }));
        Assert.Equal("GraphParameterOutOfRange", error.Code); Assert.Equal("ExecuteImageGraph", error.Stage); Assert.Equal(new EffectInstanceId("capture-instance"), error.InstanceId);
        Assert.Equal(200_000, error.TimeUs); Assert.Equal(6, pipeline.RenderedCount);
        Assert.Equal(original, await File.ReadAllBytesAsync(output)); Assert.Equal([output], Directory.GetFiles(temp.Path));
    }
    [Fact]
    public async Task 实际作者增加图外扩触发会话预算失败停止编码保护已有成片并回收产物()
    {
        using TemporaryDirectory temp = new(); string output = temp.FilePath("existing.mp4"); byte[] original = [1, 6, 1, 8]; await File.WriteAllBytesAsync(output, original);
        var project = Project(); var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        await using var source = await Source(snapshot, false, true);
        using var pipeline = new CapturePipeline(source, snapshot, new() { MaximumBytes = 320 * 180 * 4 * 2 });
        var exporter = new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());
        var error = await Assert.ThrowsAsync<EffectFailureException>(() => exporter.ExportAsync(project, new() { OutputPath = output, IncludeAudio = false, Overwrite = true }));
        Assert.Equal("ImageSurfaceBudgetExceeded", error.Code); Assert.Equal("ExecuteImageGraph", error.Stage); Assert.Equal(new EffectInstanceId("capture-instance"), error.InstanceId);
        var resource = Assert.IsType<ImageSurfaceBudgetException>(error.InnerException); Assert.Equal("ExecuteImageGraphPeak", resource.Operation);
        Assert.Equal(200_000, error.TimeUs); Assert.Equal(6, pipeline.RenderedCount); Assert.Equal(0, pipeline.SurfaceUsage.ActiveSurfaces);
        Assert.Equal(0, pipeline.SurfaceUsage.EstimatedBytes); Assert.Equal(original, await File.ReadAllBytesAsync(output)); Assert.Equal([output], Directory.GetFiles(temp.Path));
    }
    private sealed class CapturePipeline : IRenderPipeline, IDisposable
    {
        private readonly IEvaluatedFrameSource _source;
        private readonly ProjectSnapshot _snapshot;
        private readonly SkiaRenderPipeline _native;
        private readonly ImageSurfaceBudgetSession _resources;
        private readonly CompiledImageGraph _graph;
        private readonly SceneImageGraphProgram _program;
        public string BackendName => "生产字形捕获和真实作者图绑定编码接缝";
        public int RenderedCount { get; private set; }
        public ImageSurfaceUsage SurfaceUsage => _resources.Usage;
        public CapturePipeline(IEvaluatedFrameSource source, ProjectSnapshot snapshot, GraphExecutionBudget? limits = null)
        {
            _source = source; _snapshot = snapshot;
            _resources = new(limits); _native = new(surfaceBudget: _resources);
            var program = new ShaderProgramDefinition("tint.sksl", "uniform shader target; uniform float amount; half4 main(float2 p) { half4 c=target.eval(p); return half4(c.b,c.r,c.g,c.a)*amount; }",
                ShaderProgramDefinition.RasterProfile, [new("amount", ShaderUniformKind.Float)], [new("target", ShaderSurfaceKind.Color)]);
            var definition = new ImageGraphDefinition("tint", ImageGraphDefinition.CurrentVersion,
                [new("target", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)], ["amount", "expand"],
                [new ImageGraphNode.RuntimeShader("tint", "tint", ShaderSurfaceKind.Color, [new("amount", new GraphUniformBinding.Parameter("amount"))],
                    [new("target", GraphReference.Input("target"))], new(new GraphExpansion.CeilingMultiple("expand", 1), 3, 0, GraphEdgeMode.Transparent))],
                GraphReference.Node("tint"), GraphCoordinateSpace.Canvas, 3, GraphAnchorMode.ColorOnly, GraphCompositeMode.SourceOver);
            _graph = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, [new EffectParameterConstraint.Number("amount", 0, 1), new EffectParameterConstraint.Number("expand", 0, 3)], [new("tint", program)]));
            _program = new(Resources.GetGraph("tint"), _graph, new(true, true, true, true, SceneCaptureRegion.FillAndStroke), new(0, 0, 320, 180));
        }
        public RenderFrame Render(RenderRequest request)
        {
            var target = request.ResolveTarget(); var mode = request.TransparentBackground ? OutputMode.Transparent : OutputMode.Opaque;
            var frame = _source.PrepareAsync(request.TimeUs, mode, target, request.CancellationToken).AsTask().GetAwaiter().GetResult();
            request = request with { Project = _snapshot, PreparedFrame = frame };
            using var effect = _native.ExecuteSceneImageEffect(request, Assert.Single(frame.ImageEffects), _program, new Dictionary<string, SkiaGraphInput>(), null);
            using var srgb = SKColorSpace.CreateSrgb(); using var belowSurface = SKSurface.Create(new SKImageInfo(target.PixelWidth, target.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
            belowSurface.Canvas.ClipRect(new(0, 0, target.PixelWidth, target.PixelHeight));
            belowSurface.Canvas.Clear(request.TransparentBackground ? SKColors.Transparent : new(20, 40, 80));
            using var belowImage = belowSurface.Snapshot(); using var below = SkiaGraphSurface.Borrow(belowImage,
                new(new(0, 0, 320, 180), GraphCoordinateSpace.Canvas, SceneMatrix.Identity, target.PixelWidth / 320d, target.PixelHeight / 180d), ShaderSurfaceKind.Color);
            using var combined = _graph.Composite(effect.GraphResult, below, cancellationToken: request.CancellationToken); using var bitmap = SKBitmap.FromImage(combined.Image);
            RenderedCount++; return new(bitmap.Bytes, target, PixelFormat.Bgra8888Premultiplied, request.TimeUs);
        }
        public void Dispose() { _graph.Dispose(); _native.Dispose(); _resources.Dispose(); }
    }
}
