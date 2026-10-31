using System.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Spike.Rendering;
using SkiaSharp;

namespace MakoKaraGenerator.Export.Tests;

/// <summary>R1 原生能力样本确实经过现有编码器，不代表工程效果工作区已接入。</summary>
public sealed class RuntimeShaderExportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task shader短片与离屏同帧一致并保留透明基础外观(bool transparent)
    {
        using TemporaryDirectory temp = new();
        string extension = transparent ? ".mov" : ".mp4";
        string output = temp.FilePath("shader" + extension), decodedPath = temp.FilePath("decoded.png");
        Project project = Project.Create("R1 原生能力",
            Composition.Create(320, 180, FrameRate.Fps30, RgbaColor.Black, outputDurationUs: 1_000_000),
            LyricsDocument.Create([LyricLine.Create("r1", "验证", new TimeRange(0, 1_000_000), TimingStatus.Edited)]));
        var pipeline = new ShaderProbePipeline();
        VideoExporter exporter = new(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());
        ExportReport report = await exporter.ExportAsync(project, new()
        {
            OutputPath = output, IncludeAudio = false, Quality = ExportQuality.High,
            Format = transparent ? ExportVideoFormat.TransparentProRes4444 : ExportVideoFormat.OpaqueMp4
        });
        Assert.True(report.IsVerified);
        Assert.Equal(0, report.FailedFrames);
        using RenderFrame preview = pipeline.Render(RenderRequest.At(ProjectSnapshot.FromProject(project), 500_000)
            with { TransparentBackground = transparent });
        await Decode(output, decodedPath);
        using SKBitmap decoded = SKBitmap.Decode(decodedPath);
        double error = 0; int counted = 0; string sample = "";
        for (int y = 0; y < decoded.Height; y++) for (int x = 0; x < decoded.Width; x++)
        {
            Assert.True(preview.TryGetPixel(x, y, out RgbaColor expected));
            SKColor actual = decoded.GetPixel(x, y);
            if (transparent && expected.Alpha > 180 && sample.Length == 0) sample = $"expected={expected}, actual={actual}, decodedType={decoded.AlphaType}";
            if (transparent)
            {
                Assert.InRange(Math.Abs(expected.Alpha - actual.Alpha), 0, 3);
                if (expected.Alpha < 24) continue;
            }
            error += Math.Abs(expected.Red - actual.Red) + Math.Abs(expected.Green - actual.Green) + Math.Abs(expected.Blue - actual.Blue);
            counted += 3;
        }
        Assert.True(counted > 1000);
        Assert.True(error / counted < 9, $"成片与同帧误差过大：{error / counted}; {sample}");
        if (transparent) Assert.Equal(0, decoded.GetPixel(0, 0).Alpha);
        string? proofs = Environment.GetEnvironmentVariable("MAKOKARA_SCRIPT_SPIKE_OUTPUT");
        if (!string.IsNullOrWhiteSpace(proofs))
        {
            Directory.CreateDirectory(proofs);
            File.Copy(output, Path.Combine(proofs, "shader" + extension), overwrite: true);
            File.Copy(decodedPath, Path.Combine(proofs, "shader-decoded-" + transparent + ".png"), overwrite: true);
        }
    }

    private static async Task Decode(string input, string output)
    {
        var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-i", input, "-vf", "select=eq(n\\,15)", "-frames:v", "1", "-y", output })
            start.ArgumentList.Add(arg);
        using Process process = Process.Start(start)!;
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, error);
    }

    private sealed class ShaderProbePipeline : IRenderPipeline
    {
        public string BackendName => "R1 Skia CPU 能力样本";
        public RenderFrame Render(RenderRequest request)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            RenderTarget target = request.ResolveTarget();
            using SKBitmap pixels = RuntimeShaderFrameRenderer.Render(
                request.TransparentBackground ? ShaderSpikeMode.TransparentFallback : ShaderSpikeMode.GlassBackdrop,
                request.TimeUs, pixelWidth: target.PixelWidth, pixelHeight: target.PixelHeight);
            Assert.Equal(SKAlphaType.Premul, pixels.AlphaType);
            return new RenderFrame(pixels.Bytes, target, PixelFormat.Bgra8888Premultiplied, request.TimeUs);
        }
    }
}
