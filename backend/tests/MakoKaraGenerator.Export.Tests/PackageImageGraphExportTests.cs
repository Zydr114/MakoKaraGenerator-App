using System.Diagnostics;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Export.Tests;

public sealed class PackageImageGraphExportTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 固定包图经真实CPU及短片编码同帧一致透明保留原目标(bool transparent)
    {
        using TemporaryDirectory temp = new(); string output = temp.FilePath(transparent ? "graph.mov" : "graph.mp4");
        var project = Project.Create("包图编码", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 1_000_000),
            LyricsDocument.Create([LyricLine.Create("row", "歌词", new(0, 1_000_000), TimingStatus.Edited)]));
        using var pipeline = new PackagePipeline(); var exporter = new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());
        var report = await exporter.ExportAsync(project, new() { OutputPath = output, IncludeAudio = false, Quality = ExportQuality.High,
            Format = transparent ? ExportVideoFormat.TransparentProRes4444 : ExportVideoFormat.OpaqueMp4 });
        Assert.True(report.IsVerified); Assert.Equal(0, report.FailedFrames);
        using var preview = pipeline.Render(RenderRequest.At(ProjectSnapshot.FromProject(project), 500_000) with { TransparentBackground = transparent });
        Assert.Equal(transparent ? 1 : 0, pipeline.Bypasses);
        string decodedPath = temp.FilePath("frame.png"); var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-i", output, "-vf", "select=eq(n\\,15)", "-frames:v", "1", "-y", decodedPath }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; string error = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, error);
        using var decoded = SKBitmap.Decode(decodedPath); double total = 0; int count = 0;
        for (int y = 0; y < 180; y++) for (int x = 0; x < 320; x++)
        {
            Assert.True(preview.TryGetPixel(x, y, out var expected)); var actual = decoded.GetPixel(x, y);
            if (transparent) { Assert.InRange(Math.Abs(expected.Alpha - actual.Alpha), 0, 3); if (expected.Alpha < 24) continue; }
            total += Math.Abs(expected.Red - actual.Red) + Math.Abs(expected.Green - actual.Green) + Math.Abs(expected.Blue - actual.Blue); count += 3;
        }
        Assert.True(count > 1000); Assert.InRange(total / count, 0, 8);
        if (transparent) Assert.Equal(0, decoded.GetPixel(0, 0).Alpha);
    }

    private sealed class PackagePipeline : IRenderPipeline, IDisposable
    {
        private readonly CompiledImageGraph _graph;
        public string BackendName => "实际包图 CPU";
        public int Bypasses { get; private set; }
        public PackagePipeline()
        {
            var program = EffectPackageShaderFixture.Program(); program["children"] = new JsonArray(new JsonObject { ["name"] = "target", ["type"] = "color" });
            var json = EffectPackageGraphFixture.Graph(); var media = EffectPackageGraphFixture.Input("media", "BackgroundMedia"); media.Remove("capture"); media["backgroundRequirement"] = "media";
            json["inputs"]!.AsArray().Add(media); json["nodes"]![0]!["children"] = new JsonObject { ["target"] = "input:media" }; json["bypassInput"] = "input:original";
            var contents = EffectPackageGraphFixture.Contents(json, program, "uniform shader target; uniform float amount; half4 main(float2 p) { return target.eval(p)*amount; }");
            _graph = CompiledImageGraph.Compile(EffectPackageImageGraphs.Read(contents)[EffectPackageGraphFixture.GraphPath].Plan);
        }
        public RenderFrame Render(RenderRequest request)
        {
            using var srgb = SKColorSpace.CreateSrgb(); var info = new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul, srgb);
            using var original = SKSurface.Create(info); original.Canvas.ClipRect(new(0, 0, 320, 180)); original.Canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = new(40, 120, 230, 160), IsAntialias = true }; original.Canvas.DrawOval(new(40, 40, 280, 140), paint);
            using var media = SKSurface.Create(info); media.Canvas.ClipRect(new(0, 0, 320, 180)); media.Canvas.Clear(new(120, 180, 60));
            using var originalImage = original.Snapshot(); using var mediaImage = media.Snapshot();
            var descriptor = new GraphSurfaceDescriptor(new(0, 0, 320, 180), GraphCoordinateSpace.Canvas, SceneMatrix.Identity, 1, 1);
            using var target = SkiaGraphSurface.Borrow(originalImage, descriptor, ShaderSurfaceKind.Color);
            using var background = SkiaGraphSurface.Borrow(mediaImage, descriptor, ShaderSurfaceKind.Color);
            var inputs = new Dictionary<string, SkiaGraphInput> { ["original"] = new(target, GraphInputSource.TargetColor) };
            if (!request.TransparentBackground) inputs.Add("media", new(background, GraphInputSource.BackgroundMedia));
            using var result = _graph.Execute(new(request.TimeUs, 320, 180, new(0, 0, 320, 180), 1, 1, ParameterValues.Empty, request.TransparentBackground), inputs, request.CancellationToken);
            Bypasses = result.Diagnostics.Length; using var composite = request.TransparentBackground ? null : _graph.Composite(result, background);
            using var bitmap = SKBitmap.FromImage(composite?.Image ?? result.Output.Image);
            return new(bitmap.Bytes, request.ResolveTarget(), PixelFormat.Bgra8888Premultiplied, request.TimeUs);
        }
        public void Dispose() => _graph.Dispose();
    }
}
