using System.Diagnostics;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Execution;
using SkiaSharp;

namespace MakoKaraGenerator.Export.Tests;

public sealed partial class MixedPackageExportTests
{
    private static string ExportUniformSource(string value) => MixedEffectPackageFixture.MaterialSource.Replace(
        "output.Style.SetSungMaterial(token.WithRuby, material);", "output.Style.SetSungMaterial(token.WithRuby, material);\n" +
        "foreach(var slot in new[]{MaterialSlot.UnsungFill,MaterialSlot.SungFill}) output.Materials.SetUniform(frame.Resources.GetMaterial(\"ink\"),token.WithRuby,slot,\"probe\",\"amount\"," + value + ");");

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 逐帧uniform经真正作者与主Render导出MP4或Alpha同帧一致(bool transparent)
    {
        using TemporaryDirectory temp = new();
        var package = await CompileMaterial(MixedEffectPackageFixture.MaterialContents(controllerSource: ExportUniformSource("UniformValue.Float(.2+frame.TimeUs/2000000d)")));
        Assert.True(package.Succeeded, string.Join('\n', package.Artifact.Diagnostics));
        var project = MaterialProject(); var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        await using var source = await new PureFrameSourceFactory([package.CreateEffect("controller", new("uniform-export"), new EffectBinding.Project(), 1)],
            Fingerprint, static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        using var pipeline = new PreparedPackagePipeline(source, snapshot, package.Definition);
        string path = temp.FilePath(transparent ? "uniform.mov" : "uniform.mp4");
        var report = await new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder()).ExportAsync(project,
            new() { OutputPath = path, IncludeAudio = false, Quality = ExportQuality.High,
                Format = transparent ? ExportVideoFormat.TransparentProRes4444 : ExportVideoFormat.OpaqueMp4 });
        Assert.True(report.IsVerified); Assert.Equal(0, report.FailedFrames);
        using var preview = pipeline.Render(RenderRequest.At(snapshot, 500_000) with { TransparentBackground = transparent });
        string image = temp.FilePath("frame.png"); var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-i", path, "-vf", "select=eq(n\\,15)", "-frames:v", "1", "-y", image }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; string error = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, error);
        using var decoded = SKBitmap.Decode(image); double total = 0; int count = 0, visible = 0;
        for (int y = 0; y < decoded.Height; y++) for (int x = 0; x < decoded.Width; x++)
        {
            Assert.True(preview.TryGetPixel(x, y, out var expected)); var actual = decoded.GetPixel(x, y);
            if (transparent) { Assert.InRange(Math.Abs(expected.Alpha - actual.Alpha), 0, 3); if (expected.Alpha < 24) continue; }
            if (expected.Blue > 15) visible++;
            total += Math.Abs(expected.Red - actual.Red) + Math.Abs(expected.Green - actual.Green) + Math.Abs(expected.Blue - actual.Blue); count += 3;
        }
        Assert.True(visible > 100); Assert.InRange(total / count, 0, 9);
        if (transparent) Assert.Equal(0, decoded.GetPixel(0, 0).Alpha);
    }

    [Fact]
    public async Task 中途uniform类型错误阻止编码且保留旧成片()
    {
        using TemporaryDirectory temp = new(); string path = temp.FilePath("existing.mp4"); byte[] original = [2, 7, 1, 8]; await File.WriteAllBytesAsync(path, original);
        var package = await CompileMaterial(MixedEffectPackageFixture.MaterialContents(controllerSource: ExportUniformSource(
            "frame.TimeUs>=200000 ? UniformValue.Int(1) : UniformValue.Float(.5)")));
        Assert.True(package.Succeeded, string.Join('\n', package.Artifact.Diagnostics));
        var project = MaterialProject(); var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        await using var source = await new PureFrameSourceFactory([package.CreateEffect("controller", new("bad-uniform"), new EffectBinding.Project(), 1)],
            Fingerprint, static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        using var pipeline = new PreparedPackagePipeline(source, snapshot, package.Definition);
        var failure = await Assert.ThrowsAsync<EffectFailureException>(() => new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder()).ExportAsync(project,
            new() { OutputPath = path, IncludeAudio = false, Overwrite = true }));
        Assert.Equal("ShaderUniformValueMismatch", failure.Code); Assert.Equal(new EffectInstanceId("bad-uniform"), failure.InstanceId);
        Assert.Equal(package.Artifact.ArtifactHash, failure.ArtifactHash); Assert.Equal(200_000, failure.TimeUs);
        Assert.Equal(original, await File.ReadAllBytesAsync(path)); Assert.Equal([path], Directory.GetFiles(temp.Path));
    }
}
