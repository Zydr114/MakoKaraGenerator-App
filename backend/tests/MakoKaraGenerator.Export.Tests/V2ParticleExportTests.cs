using System.Diagnostics;
using System.Text.Json;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
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

public sealed class V2ParticleExportTests
{
    [Theory]
    [InlineData("CursorTrail", false)]
    [InlineData("CursorTrail", true)]
    [InlineData("VectorTrail", false)]
    [InlineData("VectorTrail", true)]
    [InlineData("EventBurst", false)]
    [InlineData("EventBurst", true)]
    public async Task 三个真实v2粒子包经正式MP4与Alpha编码仍对应同一固定预览帧(string name, bool transparent)
    {
        using TemporaryDirectory directory = new();
        var (project, snapshot, compiled) = await Input(name);
        var factory = new PureFrameSourceFactory(compiled.Effects, snapshot.EffectInputsFingerprint!, static () => new SkiaRenderPipeline());
        await using var source = await factory.CreateAsync(snapshot);
        using var pipeline = new PreparedPipeline(source, snapshot);
        string output = directory.FilePath(transparent ? "particles.mov" : "particles.mp4");
        var exporter = new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());
        var report = await exporter.ExportAsync(project, new() { OutputPath = output, IncludeAudio = false, Quality = ExportQuality.High,
            Format = transparent ? ExportVideoFormat.TransparentProRes4444 : ExportVideoFormat.OpaqueMp4 });
        Assert.True(report.IsVerified); Assert.Equal(0, report.FailedFrames);
        Assert.Equal(24, pipeline.FrameCount); Assert.NotEmpty(pipeline.EncodedFrame15!.V2Particles);
        await using var independent = await factory.CreateAsync(snapshot);
        var direct = await independent.PrepareAsync(500_000, transparent ? OutputMode.Transparent : OutputMode.Opaque,
            RenderTarget.FromComposition(snapshot.Composition));
        Assert.Equal(JsonSerializer.Serialize(direct.V2ParticleSnapshots), JsonSerializer.Serialize(pipeline.EncodedFrame15.V2ParticleSnapshots));
        using SkiaRenderPipeline renderer = new();
        using var expected = renderer.Render(RenderRequest.At(snapshot, 500_000) with { TransparentBackground = transparent, PreparedFrame = direct });
        string decodedPath = directory.FilePath("frame15.png");
        var info = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (string argument in new[] { "-hide_banner", "-loglevel", "error", "-i", output, "-vf", "select=eq(n\\,15)", "-frames:v", "1", "-y", decodedPath })
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        string errors = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, errors);
        using var decoded = SKBitmap.Decode(decodedPath);
        Assert.Equal(320, decoded.Width); Assert.Equal(180, decoded.Height);
        double difference = 0; int channels = 0, coloredParticlePixels = 0;
        for (int y = 0; y < decoded.Height; y++)
            for (int x = 0; x < decoded.Width; x++)
            {
                Assert.True(expected.TryGetPixel(x, y, out var before)); var after = decoded.GetPixel(x, y);
                if (before.Red > before.Green + 30) coloredParticlePixels++;
                if (transparent)
                {
                    Assert.InRange(Math.Abs(before.Alpha - after.Alpha), 0, 4);
                    if (before.Alpha < 24) continue;
                }
                difference += Math.Abs(before.Red - after.Red) + Math.Abs(before.Green - after.Green) + Math.Abs(before.Blue - after.Blue);
                channels += 3;
            }
        Assert.True(coloredParticlePixels > 20); Assert.True(channels > 0); Assert.InRange(difference / channels, 0, 9);
        if (transparent) Assert.Equal(0, decoded.GetPixel(0, 0).Alpha);
    }

    [Fact]
    public async Task v2晚期预算中止真实编码并保留原文件不发布半份结果()
    {
        using TemporaryDirectory directory = new();
        var (project, snapshot, compiled) = await Input("VectorTrail");
        var factory = new PureFrameSourceFactory(compiled.Effects, snapshot.EffectInputsFingerprint!,
            static () => new SkiaRenderPipeline(), visualBudget: new(1));
        await using var source = await factory.CreateAsync(snapshot); using var pipeline = new PreparedPipeline(source, snapshot);
        string output = directory.FilePath("existing.mp4"); byte[] original = [2, 7, 1, 8]; await File.WriteAllBytesAsync(output, original);
        var exporter = new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());
        var error = await Assert.ThrowsAsync<EffectFailureException>(() => exporter.ExportAsync(project,
            new() { OutputPath = output, IncludeAudio = false, Overwrite = true }));
        Assert.Equal("VisualOutputBudgetExceeded", error.Code); Assert.Equal("particles", error.InstanceId.Value);
        Assert.Equal("makokara.v2.vector-trail", error.PackageIdentity!.PackageId);
        Assert.Equal(66_666, error.TimeUs); Assert.Equal(2, pipeline.FrameCount);
        Assert.Equal(original, await File.ReadAllBytesAsync(output)); Assert.Equal([output], Directory.GetFiles(directory.Path));
    }

    private static async Task<(Project Project, ProjectSnapshot Snapshot, CompiledProjectEffects Compiled)> Input(string name)
    {
        var package = await EffectPackageStorage.ReadSupportedDirectoryAsync(Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", name));
        var row = LyricLine.Create("row", "AB", new(0, 800_000), TimingStatus.Edited,
            [LyricToken.Create("a", "A", new(0, 400_000), TimingStatus.Edited),
                LyricToken.Create("b", "B", new(400_000, 800_000), TimingStatus.Edited)]);
        var project = Project.Create("M08 finite export", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 800_000),
            LyricsDocument.Create([row]), lyricStyle: LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 40,
                SungColor = RgbaColor.White, UnsungColor = RgbaColor.White, Stroke = null, Shadow = null, Glow = null });
        project = project with { EnterAnimation = null, Effects = new([package], [new()
        {
            Id = "particles", Name = "particles", PackageId = package.PackageId, Revision = package.Revision,
            ExportId = Assert.Single(EffectPackageDefinition.Read(package).Controllers).Id, Seed = 17,
        }]) };
        var snapshot = ProjectSnapshot.FromProject(project);
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var compiled = await CompiledProjectEffects.CompileAsync(snapshot, compiler, new PackageImageValidator());
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Packages.SelectMany(item => item.Artifact.Diagnostics)));
        return (project, snapshot, compiled);
    }

    // This is the existing formal encoding seam with real Skia pixels; it is not a hardware Vulkan assertion.
    private sealed class PreparedPipeline(IEvaluatedFrameSource source, ProjectSnapshot snapshot) : IRenderPipeline, IDisposable
    {
        private readonly SkiaRenderPipeline _renderer = new();
        public string BackendName => "M08 fixed package encoding";
        public int FrameCount { get; private set; }
        public PreparedRenderFrame? EncodedFrame15 { get; private set; }
        public RenderFrame Render(RenderRequest request)
        {
            var prepared = source.PrepareAsync(request.TimeUs,
                request.TransparentBackground ? OutputMode.Transparent : OutputMode.Opaque, request.ResolveTarget()).AsTask().GetAwaiter().GetResult();
            var pixels = _renderer.Render(request with { Project = snapshot, PreparedFrame = prepared });
            if (request.TimeUs == 500_000) EncodedFrame15 = prepared;
            FrameCount++; return pixels;
        }
        public void Dispose() => _renderer.Dispose();
    }
}
