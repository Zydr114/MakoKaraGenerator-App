using System.Diagnostics;
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

public sealed partial class ContinuousParticleExportTests
{
    private const string Fingerprint = "particle-encoding";
    private static readonly Lazy<Task<ScriptCompilationResult>> Artifact = new(async () =>
    {
        ScriptCompiler compiler = new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var result = await compiler.CompileAsync([new("src/CursorTrail.cs", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "script-samples/CursorTrail.cs"))),
            new("src/EventBurst.cs", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "script-samples/EventBurst.cs"))),
            new("src/UnavailableBurst.cs", """
            using MakoKaraGenerator.Scripting;
            public sealed class UnavailableBurst : EffectBase
            {
                public override void OnEvent(TimelineEvent evt, EventContext context)
                {
                    if (evt is TokenCompleted token) context.Bursts.Queue(new("burst"), new()
                    { Count = 2, Anchor = new BurstAnchor.SourceToken(token.TokenId), LifetimeUs = 600000,
                        SizePx = 8, Color = new(1,0,0,1), SeedStreamKey = "unavailable" });
                }
            }
            """)]);
        Assert.True(result.Succeeded, string.Join('\n', result.Diagnostics)); return result;
    });
    private static Project Project() => Core.Projects.Project.Create("光标粒子成片", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 800_000),
        LyricsDocument.Create([Row("first", 0, 300_000), Row("next", 400_000, 800_000)]), lyricStyle: LyricStyle.Default with
        { FontSizePx = 40, SungColor = RgbaColor.White, UnsungColor = RgbaColor.White, Stroke = null, Shadow = null });
    private static LyricLine Row(string id, long start, long end) => LyricLine.Create(id, "歌词", new(start, end), TimingStatus.Edited,
        [LyricToken.Create(id + "-token", "歌词", new(start, end), TimingStatus.Edited)]);
    private static async Task<IEvaluatedFrameSource> Source(ProjectSnapshot snapshot, bool fail = false, bool records = false, bool burst = false, bool unavailable = false)
    {
        CompiledFrameEffect effect = new(new("cursor-effect"), await Artifact.Value, unavailable ? "UnavailableBurst" : burst ? "EventBurst" : "CursorTrail", new(new Dictionary<string, ParameterValue>
        {
            ["count"] = new ParameterValue.Number(2),
            ["rate"] = new ParameterValue.Number(records ? 1000 : 60), ["lifeUs"] = new ParameterValue.Duration(records ? 1 : 600_000), ["sizePx"] = new ParameterValue.Number(8),
            ["spreadPx"] = new ParameterValue.Number(8), ["trailUs"] = new ParameterValue.Duration(records ? 0 : 250_000),
            ["transition"] = new ParameterValue.Choice("jump"), ["bridgeUs"] = new ParameterValue.Duration(180_000),
            ["bridgeGapLimitUs"] = new ParameterValue.Duration(2_000_000), ["color"] = new ParameterValue.Color(new(1, .1, .05, .8))
        }), new EffectBinding.Project(), EffectCapabilities.Overlay) { Seed = 4 };
        TimelineFrameSourceFactory factory = new([effect], Fingerprint, static () => new SkiaRenderPipeline(), ReplayReason.Export,
            particleBudget: records ? new(1, 200_000, 2) : fail ? new(burst ? 3 : 10, 200_000) : ParticleSimulationBudget.Default);
        return await factory.CreateAsync(snapshot);
    }
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task 真实公开源码连续与Burst粒子经MP4或Alpha编码保持同帧(bool transparent, bool burst)
    {
        using TemporaryDirectory temp = new(); var project = Project(); var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        await using var source = await Source(snapshot, burst: burst); using PreparedPipeline pipeline = new(source, snapshot);
        string output = temp.FilePath(transparent ? "particles.mov" : "particles.mp4");
        var exporter = new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());
        var report = await exporter.ExportAsync(project, new() { OutputPath = output, IncludeAudio = false, Quality = ExportQuality.High,
            Format = transparent ? ExportVideoFormat.TransparentProRes4444 : ExportVideoFormat.OpaqueMp4 });
        Assert.True(report.IsVerified); Assert.Equal(0, report.FailedFrames);
        using var preview = pipeline.Render(RenderRequest.At(snapshot, 500_000) with { TransparentBackground = transparent });
        var particles = pipeline.LastFrame!.Particles; Assert.Contains(particles, p => p.BirthUs < 300_000); Assert.Contains(particles, p => p.BirthUs >= 400_000);
        string path = temp.FilePath("frame.png"); var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-i", output, "-vf", "select=eq(n\\,15)", "-frames:v", "1", "-y", path }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var error = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, error);
        using var decoded = SKBitmap.Decode(path); double total = 0; int channels = 0, red = 0;
        for (int y = 0; y < decoded.Height; y++) for (int x = 0; x < decoded.Width; x++)
        {
            Assert.True(preview.TryGetPixel(x, y, out var expected)); var actual = decoded.GetPixel(x, y);
            if (transparent) { Assert.InRange(Math.Abs(expected.Alpha - actual.Alpha), 0, 3); if (expected.Alpha < 24) continue; }
            if (expected.Red > expected.Green + 30) red++;
            total += Math.Abs(expected.Red - actual.Red) + Math.Abs(expected.Green - actual.Green) + Math.Abs(expected.Blue - actual.Blue); channels += 3;
        }
        Assert.True(red > 20); Assert.InRange(total / channels, 0, 9);
        if (transparent) Assert.Equal(0, decoded.GetPixel(0, 0).Alpha);
    }
    [Fact]
    public async Task 粒子超预算中途阻止真实编码保留原文件并不产生占位帧()
    {
        using TemporaryDirectory temp = new(); var project = Project(); var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        await using var source = await Source(snapshot, true); using PreparedPipeline pipeline = new(source, snapshot);
        string output = temp.FilePath("existing.mp4"); byte[] original = [3, 1, 4]; await File.WriteAllBytesAsync(output, original);
        var exporter = new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());
        var error = await Assert.ThrowsAsync<EffectFailureException>(() => exporter.ExportAsync(project, new() { OutputPath = output, IncludeAudio = false, Overwrite = true }));
        Assert.Equal("LiveParticleBudgetExceeded", error.Code); Assert.Equal("cursor-effect", error.InstanceId.Value); Assert.Equal(183_334, error.TimeUs);
        Assert.Equal(6, pipeline.RenderedCount); Assert.Equal(original, await File.ReadAllBytesAsync(output)); Assert.Equal([output], Directory.GetFiles(temp.Path));
    }
    [Fact]
    public async Task 短命粒子完整出生记录超限同样中止编码且保护原文件()
    {
        using TemporaryDirectory temp = new(); var project = Project(); var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        await using var source = await Source(snapshot, records: true); using PreparedPipeline pipeline = new(source, snapshot);
        string output = temp.FilePath("existing.mp4"); byte[] original = [2, 7, 1]; await File.WriteAllBytesAsync(output, original);
        var exporter = new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());
        var error = await Assert.ThrowsAsync<EffectFailureException>(() => exporter.ExportAsync(project, new() { OutputPath = output, IncludeAudio = false, Overwrite = true }));
        Assert.Equal("EmissionRecordBudgetExceeded", error.Code); Assert.Equal("cursor-effect", error.InstanceId.Value); Assert.Equal(3_000, error.TimeUs);
        Assert.Equal(1, pipeline.RenderedCount); Assert.Equal(original, await File.ReadAllBytesAsync(output)); Assert.Equal([output], Directory.GetFiles(temp.Path));
    }
    // 正式持久实例及 UI 工厂后续 R4/R7；此处真实会话经现有共享渲染／编码接缝。
    private sealed class PreparedPipeline(IEvaluatedFrameSource source, ProjectSnapshot snapshot) : IRenderPipeline, IDisposable
    {
        private readonly SkiaRenderPipeline _native = new();
        public string BackendName => "固定连续粒子编码";
        public PreparedRenderFrame? LastFrame { get; private set; }
        public int RenderedCount { get; private set; }
        public RenderFrame Render(RenderRequest request)
        {
            LastFrame = source.PrepareAsync(request.TimeUs, request.TransparentBackground ? OutputMode.Transparent : OutputMode.Opaque, request.ResolveTarget())
                .AsTask().GetAwaiter().GetResult();
            var pixels = _native.Render(request with { Project = snapshot, PreparedFrame = LastFrame }); RenderedCount++; return pixels;
        }
        public void Dispose() => _native.Dispose();
    }
}
