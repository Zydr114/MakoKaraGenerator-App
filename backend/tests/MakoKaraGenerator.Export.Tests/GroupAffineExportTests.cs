using System.Diagnostics;
using System.Text.Json;
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
using MakoKaraGenerator.Scripting.V2;
using SkiaSharp;

namespace MakoKaraGenerator.Export.Tests;

public sealed class GroupAffineExportTests
{
    private const string Fingerprint = "group-affine-export";
    private static readonly Lazy<Task<CompiledEffectPackage>> Artifact = new(async () =>
    {
        ScriptCompiler compiler = new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var contents = V2ExportPackageHelper.UpgradeToV2(EffectPackageFixture.GroupTransform("""
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed class PackEffect : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
                {
                    string node = frame.Parameters.GetText("node");
                    GroupTarget? target = null;
                    foreach (var item in frame.GroupGeometry)
                        if (item.Target.NodeId.Value == node) { target = item.Target; break; }
                    if (frame.Parameters.GetBoolean("badWrite") && frame.TimeUs >= 300000) target = new(new("missing"));
                    output.AppendTransform(new TextTarget.Group(target!), Affine2.Translation(frame.TimeUs / 20000d, -8));
                }
            }
            """),
            [CapabilityNames.GroupAffine],
            manifest => manifest["exports"]![0]!["outputs"]![0]!["requires"] = new System.Text.Json.Nodes.JsonArray(CapabilityNames.GroupAffine));
        var result = await CompiledEffectPackage.CompileAsync(EffectPackageDefinition.Read(contents), compiler);
        Assert.True(result.Artifact.Succeeded, string.Join('\n', result.Artifact.Diagnostics)); return result;
    });
    private static Project Project() => Core.Projects.Project.Create("组仿射成片", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 700_000),
        LyricsDocument.Create([LyricLine.Create("line", "歌词", new(0, 700_000), TimingStatus.Edited,
            [LyricToken.Create("token", "歌词", new(0, 700_000), TimingStatus.Edited)])]), lyricStyle: LyricStyle.Default with
        { FontSizePx = 40, SungColor = RgbaColor.White, UnsungColor = RgbaColor.White, Opacity = .7f, Stroke = null, Shadow = null });
    private static async Task<IEvaluatedFrameSource> Source(ProjectSnapshot snapshot, bool fail = false)
    {
        string root = snapshot.LyricPresentation.FindProgram("primary")!.RootContainerId;
        var effect = (await Artifact.Value).CreateEffect("group-transform", new("group-effect"), new EffectBinding.Node(new(root), false), 1,
            JsonSerializer.SerializeToUtf8Bytes(new { node = root, badWrite = fail }));
        return await new PureFrameSourceFactory([effect], Fingerprint, static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 真实组仿射MP4和Alpha与预览同帧(bool transparent)
    {
        using TemporaryDirectory temp = new(); var project = Project(); var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        await using var source = await Source(snapshot); using PreparedPipeline pipeline = new(source, snapshot);
        string output = temp.FilePath(transparent ? "group.mov" : "group.mp4");
        var exporter = new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());
        var report = await exporter.ExportAsync(project, new() { OutputPath = output, IncludeAudio = false, Quality = ExportQuality.High,
            Format = transparent ? ExportVideoFormat.TransparentProRes4444 : ExportVideoFormat.OpaqueMp4 });
        Assert.True(report.IsVerified); Assert.Equal(0, report.FailedFrames);
        using var preview = pipeline.Render(RenderRequest.At(snapshot, 500_000) with { TransparentBackground = transparent });
        Assert.Single(pipeline.LastFrame!.GroupTransforms);
        string path = temp.FilePath("frame.png"); var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-i", output, "-vf", "select=eq(n\\,15)", "-frames:v", "1", "-y", path }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var error = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, error);
        using var decoded = SKBitmap.Decode(path); double total = 0; int channels = 0, visible = 0;
        for (int y = 0; y < decoded.Height; y++) for (int x = 0; x < decoded.Width; x++)
        {
            Assert.True(preview.TryGetPixel(x, y, out var expected)); var actual = decoded.GetPixel(x, y);
            if (transparent) { Assert.InRange(Math.Abs(expected.Alpha - actual.Alpha), 0, 3); if (expected.Alpha < 24) continue; }
            if (expected.Red > 30) visible++;
            total += Math.Abs(expected.Red - actual.Red) + Math.Abs(expected.Green - actual.Green) + Math.Abs(expected.Blue - actual.Blue); channels += 3;
        }
        Assert.True(visible > 100); Assert.InRange(total / channels, 0, 9);
        if (transparent) Assert.Equal(0, decoded.GetPixel(0, 0).Alpha);
    }
    [Fact]
    public async Task 组目标中途失效停止编码且保护原文件()
    {
        using TemporaryDirectory temp = new(); var project = Project(); var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        await using var source = await Source(snapshot, true); using PreparedPipeline pipeline = new(source, snapshot);
        string output = temp.FilePath("existing.mp4"); byte[] original = [1, 2, 3]; await File.WriteAllBytesAsync(output, original);
        var exporter = new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());
        var error = await Assert.ThrowsAsync<EffectFailureException>(() => exporter.ExportAsync(project, new() { OutputPath = output, IncludeAudio = false, Overwrite = true }));
        Assert.Equal("TargetOutsideBinding", error.Code); Assert.Equal("group-effect", error.InstanceId.Value); Assert.Equal(300_000, error.TimeUs);
        Assert.Equal(9, pipeline.RenderedCount); Assert.Equal(original, await File.ReadAllBytesAsync(output)); Assert.Equal([output], Directory.GetFiles(temp.Path));
    }
    private sealed class PreparedPipeline(IEvaluatedFrameSource source, ProjectSnapshot snapshot) : IRenderPipeline, IDisposable
    {
        private readonly SkiaRenderPipeline _native = new();
        public string BackendName => "真实组仿射编码";
        public PreparedRenderFrame? LastFrame { get; private set; }
        public int RenderedCount { get; private set; }
        public RenderFrame Render(RenderRequest request)
        {
            LastFrame = source.PrepareAsync(request.TimeUs, request.TransparentBackground ? OutputMode.Transparent : OutputMode.Opaque, request.ResolveTarget()).AsTask().GetAwaiter().GetResult();
            var pixels = _native.Render(request with { Project = snapshot, PreparedFrame = LastFrame }); RenderedCount++; return pixels;
        }
        public void Dispose() => _native.Dispose();
    }
}
