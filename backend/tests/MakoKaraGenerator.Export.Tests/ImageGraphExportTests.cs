using System.Diagnostics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using SkiaSharp;
using TimeRange = MakoKaraGenerator.Core.Time.TimeRange;

namespace MakoKaraGenerator.Export.Tests;

/// <summary>正式图后端经真实编码器的同帧证据；不代替生产歌词捕获／包集成验收。</summary>
public sealed class ImageGraphExportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 正式多遍图短片与同帧像素一致且透明旁路保留后续模糊(bool transparent)
    {
        using TemporaryDirectory temp = new();
        string output = temp.FilePath(transparent ? "graph.mov" : "graph.mp4"), decodedPath = temp.FilePath("decoded.png");
        Project project = Project.Create("图后端编码证据", Composition.Create(320, 180, FrameRate.Fps30, RgbaColor.Black, outputDurationUs: 1_000_000),
            LyricsDocument.Create([LyricLine.Create("proof", "验证", new TimeRange(0, 1_000_000), TimingStatus.Edited)]));
        using var pipeline = new GraphProbePipeline();
        var exporter = new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());
        var report = await exporter.ExportAsync(project, new() { OutputPath = output, IncludeAudio = false, Quality = ExportQuality.High,
            Format = transparent ? ExportVideoFormat.TransparentProRes4444 : ExportVideoFormat.OpaqueMp4 });
        Assert.True(report.IsVerified); Assert.Equal(0, report.FailedFrames);
        using var preview = pipeline.Render(RenderRequest.At(ProjectSnapshot.FromProject(project), 500_000) with { TransparentBackground = transparent });
        Assert.Equal(transparent ? 1 : 0, pipeline.LastBypassCount);
        var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-i", output, "-vf", "select=eq(n\\,15)", "-frames:v", "1", "-y", decodedPath }) start.ArgumentList.Add(arg);
        using Process process = Process.Start(start)!; string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, error);
        using var decoded = SKBitmap.Decode(decodedPath);
        double totalError = 0; int channels = 0;
        for (int y = 0; y < decoded.Height; y++) for (int x = 0; x < decoded.Width; x++)
        {
            Assert.True(preview.TryGetPixel(x, y, out var expected)); var actual = decoded.GetPixel(x, y);
            if (transparent) { Assert.InRange(Math.Abs(expected.Alpha - actual.Alpha), 0, 3); if (expected.Alpha < 24) continue; }
            totalError += Math.Abs(expected.Red - actual.Red) + Math.Abs(expected.Green - actual.Green) + Math.Abs(expected.Blue - actual.Blue); channels += 3;
        }
        Assert.True(channels > 1000); Assert.True(totalError / channels < 9, $"成片与图同帧误差：{totalError / channels}");
        if (transparent)
        {
            Assert.Equal(0, decoded.GetPixel(0, 0).Alpha);
            Assert.InRange(decoded.GetPixel(164, 90).Alpha, (byte)157, (byte)163);
            Assert.InRange(decoded.GetPixel(44, 90).Alpha, (byte)1, (byte)120);
        }
    }

    private sealed class GraphProbePipeline : IRenderPipeline, IDisposable
    {
        private readonly CompiledImageGraph _glass, _post;
        public string BackendName => "正式 CPU 图编码样本";
        public int LastBypassCount { get; private set; }
        public GraphProbePipeline()
        {
            var bounds = new GraphNodeBounds(new GraphExpansion.Literal(0), 0, 0, GraphEdgeMode.Transparent);
            GraphInputDefinition Input(string id, ShaderSurfaceKind kind, GraphInputSource source, GraphBackgroundRequirement requirement = GraphBackgroundRequirement.None)
                => new(id, source, kind, true, requirement, GraphCoordinateSpace.Canvas);
            var program = new ShaderProgramDefinition("glass.sksl", "uniform shader media; uniform float time; half4 main(float2 p) { half4 c=media.eval(p); return half4(c.rgb*(.8+.2*sin(time)), c.a); }",
                ShaderProgramDefinition.RasterProfile, [new("time", ShaderUniformKind.Float)], [new("media", ShaderSurfaceKind.Color)]);
            var glass = new ImageGraphDefinition("glass", ImageGraphDefinition.CurrentVersion,
                [Input("target", ShaderSurfaceKind.Color, GraphInputSource.TargetColor), Input("coverage", ShaderSurfaceKind.Coverage, GraphInputSource.GlyphCoverage),
                 Input("media", ShaderSurfaceKind.Color, GraphInputSource.BackgroundMedia, GraphBackgroundRequirement.Media)], [],
                [new ImageGraphNode.RuntimeShader("shader", "glass", ShaderSurfaceKind.Color, [new("time", new GraphUniformBinding.Host(GraphHostValue.TimeSeconds))], [new("media", GraphReference.Input("media"))], bounds),
                 new ImageGraphNode.Mask("mask", GraphReference.Node("shader"), GraphReference.Input("coverage"), bounds)], GraphReference.Node("mask"), GraphCoordinateSpace.Canvas, 0,
                GraphAnchorMode.ColorOnly, GraphCompositeMode.SourceOver, bypassInput: GraphReference.Input("target"));
            _glass = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(glass, [], [new("glass", program)]));
            try
            {
                var post = new ImageGraphDefinition("post", ImageGraphDefinition.CurrentVersion, [Input("target", ShaderSurfaceKind.Color, GraphInputSource.TargetColor)], [],
                    [new ImageGraphNode.Blur("blur", GraphReference.Input("target"), new GraphNumberBinding.Literal(2), new(new GraphExpansion.Literal(6), 6, 6, GraphEdgeMode.Transparent)),
                     new ImageGraphNode.Transform("move", GraphReference.Node("blur"), SceneMatrix.Translate(4, 0), new(38, 34, 290, 146), bounds)],
                    GraphReference.Node("move"), GraphCoordinateSpace.Canvas, 10, GraphAnchorMode.ImageOnly, GraphCompositeMode.SourceOver);
                _post = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(post, [], []));
            }
            catch { _glass.Dispose(); throw; }
        }
        public RenderFrame Render(RenderRequest request)
        {
            var target = request.ResolveTarget(); using var srgb = SKColorSpace.CreateSrgb();
            var info = new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul, srgb);
            using var targetSurface = SKSurface.Create(info); using var coverageSurface = SKSurface.Create(info); using var mediaSurface = SKSurface.Create(info); using var belowSurface = SKSurface.Create(info);
            foreach (var surface in new[] { targetSurface, coverageSurface, mediaSurface, belowSurface }) { surface.Canvas.ClipRect(new(0, 0, 320, 180)); surface.Canvas.Clear(SKColors.Transparent); }
            using var paint = new SKPaint { Color = new(40, 120, 230, 160), IsAntialias = true }; targetSurface.Canvas.DrawOval(new(44, 44, 276, 136), paint);
            paint.Color = SKColors.White; coverageSurface.Canvas.DrawOval(new(44, 44, 276, 136), paint);
            mediaSurface.Canvas.Clear(new(50, 150, 70)); paint.Color = new(130, 60, 160); mediaSurface.Canvas.DrawRect(0, 0, 160, 180, paint);
            using var targetImage = targetSurface.Snapshot(); using var coverageImage = coverageSurface.Snapshot(); using var mediaImage = mediaSurface.Snapshot();
            if (!request.TransparentBackground) belowSurface.Canvas.DrawImage(mediaImage, 0, 0);
            using var belowImage = belowSurface.Snapshot();
            var descriptor = new GraphSurfaceDescriptor(new(0, 0, 320, 180), GraphCoordinateSpace.Canvas, SceneMatrix.Identity, 1, 1);
            using var original = SkiaGraphSurface.Borrow(targetImage, descriptor, ShaderSurfaceKind.Color); using var coverage = SkiaGraphSurface.Borrow(coverageImage, descriptor, ShaderSurfaceKind.Coverage);
            using var media = SkiaGraphSurface.Borrow(mediaImage, descriptor, ShaderSurfaceKind.Color); using var below = SkiaGraphSurface.Borrow(belowImage, descriptor, ShaderSurfaceKind.Color);
            var frame = new ImageGraphFrameContext(request.TimeUs, 320, 180, new(40, 40, 280, 140), 1, 1, ParameterValues.Empty, request.TransparentBackground);
            using var first = _glass.Execute(frame, new Dictionary<string, SkiaGraphInput> { ["target"] = new(original, GraphInputSource.TargetColor), ["coverage"] = new(coverage, GraphInputSource.GlyphCoverage), ["media"] = new(media, GraphInputSource.BackgroundMedia) }, request.CancellationToken);
            LastBypassCount = first.Diagnostics.Length;
            using var post = _post.Execute(frame, new Dictionary<string, SkiaGraphInput> { ["target"] = new(first.Output, GraphInputSource.TargetColor) }, request.CancellationToken);
            using var result = _post.Composite(post, below, cancellationToken: request.CancellationToken); using var bitmap = SKBitmap.FromImage(result.Image);
            return new(bitmap.Bytes, target, PixelFormat.Bgra8888Premultiplied, request.TimeUs);
        }
        public void Dispose() { _glass.Dispose(); _post.Dispose(); }
    }
}
