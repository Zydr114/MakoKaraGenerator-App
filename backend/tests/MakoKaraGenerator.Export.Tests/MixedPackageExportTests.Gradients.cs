using System.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.V2;
using SkiaSharp;

namespace MakoKaraGenerator.Export.Tests;

public sealed partial class MixedPackageExportTests
{
    private const string GradientSource = """
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;
        public sealed class PackEffect : IPureKaraokeEffect
        {
            public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
            public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
            {
                var fill = MaterialValue.Gradient(new([new(0,new(1,.2,.1,96/255d)),new(1,new(.2,.8,1,160/255d))]));
                var stroke = MaterialValue.Gradient(new([new(0,new(.1,.3,1,128/255d)),new(1,new(1,.4,.1,180/255d))],
                    GradientKind.Radial, coordinateSpace:GradientCoordinateSpace.Canvas, radiusX:.4, radiusY:.7));
                foreach(var token in frame.Presentations.TokensInBinding)
                {
                    output.SetFill(new TextTarget.Family(token.WithRuby), TextState.Unsung, fill);
                    output.SetFill(new TextTarget.Family(token.WithRuby), TextState.Sung, fill);
                    output.SetStrokes(new TextTarget.Family(token.WithRuby), TextState.Sung, new StrokeValue(3,stroke,StrokeJoin.Bevel));
                    output.SetStrokes(new TextTarget.Family(token.WithRuby), TextState.Unsung, new StrokeValue(3,stroke,StrokeJoin.Bevel));
                    output.MultiplyOpacity(new TextTarget.Family(token.WithRuby), .5);
                }
            }
        }
        """;

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 真实包直接渐变填充及描边含注音编码MP4与Alpha保持预览同帧(bool transparent)
    {
        using TemporaryDirectory temp = new();
        var package = await CompileMaterial(V2ExportPackageHelper.UpgradeToV2(
            MixedEffectPackageFixture.MaterialContents(controllerSource: GradientSource),
            [CapabilityNames.StyleFill, CapabilityNames.StyleStroke, CapabilityNames.TokenOpacity]));
        Assert.True(package.Succeeded, string.Join('\n', package.Artifact.Diagnostics));
        var project = MaterialProject(); var line = project.Lyrics.Lines[0];
        project = project with { Lyrics = LyricsDocument.Create([line with { RubyAnnotations = [RubyAnnotation.Create("ruby", "token", "かし")] }]) };
        var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        await using var source = await new PureFrameSourceFactory([package.CreateEffect("controller", new("gradient-export"), new EffectBinding.Project(), 1)],
            Fingerprint, static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        using var pipeline = new PreparedPackagePipeline(source, snapshot, package.Definition);
        string path = temp.FilePath(transparent ? "gradient.mov" : "gradient.mp4");
        var report = await new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder()).ExportAsync(project,
            new() { OutputPath = path, IncludeAudio = false, Quality = ExportQuality.High,
                Format = transparent ? ExportVideoFormat.TransparentProRes4444 : ExportVideoFormat.OpaqueMp4 });
        Assert.True(report.IsVerified); Assert.Equal(0, report.FailedFrames); Assert.Null(pipeline.LastMaterial); Assert.Null(pipeline.LastHandle);
        using var preview = pipeline.Render(RenderRequest.At(snapshot, 500_000) with { TransparentBackground = transparent });
        string image = temp.FilePath("frame.png"); var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-i", path, "-vf", "select=eq(n\\,15)", "-frames:v", "1", "-y", image }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; string error = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, error);
        using var decoded = SKBitmap.Decode(image); double total = 0; int count = 0, visible = 0;
        for (int y = 0; y < decoded.Height; y++) for (int x = 0; x < decoded.Width; x++)
        {
            Assert.True(preview.TryGetPixel(x, y, out var expected)); var actual = decoded.GetPixel(x, y);
            if (transparent) { Assert.InRange(Math.Abs(expected.Alpha - actual.Alpha), 0, 3); if (expected.Alpha < 24) continue; }
            if (expected.Blue > 30 || expected.Red > 30) visible++;
            total += Math.Abs(expected.Red - actual.Red) + Math.Abs(expected.Green - actual.Green) + Math.Abs(expected.Blue - actual.Blue); count += 3;
        }
        Assert.True(visible > 100); Assert.InRange(total / count, 0, 9);
        if (transparent) Assert.Equal(0, decoded.GetPixel(0, 0).Alpha);
    }

    [Fact]
    public async Task 渐变描边中途实际半径退化阻止编码并保护已有输出()
    {
        using TemporaryDirectory temp = new(); string path = temp.FilePath("existing.mp4"); byte[] original = [2, 7, 1, 8]; await File.WriteAllBytesAsync(path, original);
        var package = await CompileMaterial(V2ExportPackageHelper.UpgradeToV2(
            MixedEffectPackageFixture.MaterialContents(controllerSource:
                GradientSource.Replace("radiusX:.4", "radiusX:frame.TimeUs>=200000 ? 1e-20 : .4")),
            [CapabilityNames.StyleFill, CapabilityNames.StyleStroke, CapabilityNames.TokenOpacity]));
        Assert.True(package.Succeeded, string.Join('\n', package.Artifact.Diagnostics));
        var project = MaterialProject(); var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        await using var source = await new PureFrameSourceFactory([package.CreateEffect("controller", new("broken-gradient"), new EffectBinding.Project(), 1)],
            Fingerprint, static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        using var pipeline = new PreparedPackagePipeline(source, snapshot, package.Definition);
        var error = await Assert.ThrowsAsync<PreparedFrameException>(() => new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder()).ExportAsync(project,
            new() { OutputPath = path, IncludeAudio = false, Overwrite = true }));
        Assert.Equal("UnsupportedNativeStyleValues", error.Code); Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }
}
