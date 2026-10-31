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

public sealed partial class MixedPackageExportTests
{
    private const string Fingerprint = "mixed-package-export";
    private static readonly Lazy<Task<CompiledEffectPackage>> Package = new(async () =>
    {
        var definition = EffectPackageDefinition.Read(MixedEffectPackageFixture.Contents());
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var package = await CompiledEffectPackage.CompileAsync(definition, compiler, new PackageImageValidator(), new PackageShaderValidator(),
            new PackageImageGraphValidator(), new PackageMaterialValidator());
        Assert.True(package.Succeeded, string.Join('\n', package.Artifact.Diagnostics)); return package;
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 混合包真实控制器导出与CPU图通过主Render协调器编码预览同帧(bool transparent)
    {
        using TemporaryDirectory temp = new(); var package = await Package.Value;
        var project = Project.Create("混合包成片", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 700_000),
            LyricsDocument.Create([LyricLine.Create("row", "歌词", new(0, 700_000), TimingStatus.Edited,
                [LyricToken.Create("token", "歌词", new(0, 700_000), TimingStatus.Edited)])]),
            lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 40,
                SungColor = new(220, 100, 80), UnsungColor = new(220, 100, 80), Stroke = null, Shadow = null, Opacity = .7f });
        var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        var effect = package.CreateEffect("controller", new("mixed-export"), new EffectBinding.Project(), 1);
        await using var source = await new PureFrameSourceFactory([effect], Fingerprint, static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        using var pipeline = new PreparedPackagePipeline(source, snapshot, package.Definition);
        string output = temp.FilePath(transparent ? "mixed.mov" : "mixed.mp4");
        var report = await new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder()).ExportAsync(project,
            new() { OutputPath = output, IncludeAudio = false, Quality = ExportQuality.High,
                Format = transparent ? ExportVideoFormat.TransparentProRes4444 : ExportVideoFormat.OpaqueMp4 });
        Assert.True(report.IsVerified); Assert.Equal(0, report.FailedFrames);
        using var preview = pipeline.Render(RenderRequest.At(snapshot, 500_000) with { TransparentBackground = transparent });
        Assert.Equal(package.Resources.GetGraph("tint"), pipeline.LastHandle);
        string path = temp.FilePath("frame.png"); var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-i", output, "-vf", "select=eq(n\\,15)", "-frames:v", "1", "-y", path }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; string error = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, error);
        using var decoded = SKBitmap.Decode(path); double total = 0; int count = 0, visible = 0;
        for (int y = 0; y < decoded.Height; y++) for (int x = 0; x < decoded.Width; x++)
        {
            Assert.True(preview.TryGetPixel(x, y, out var expected)); var actual = decoded.GetPixel(x, y);
            if (transparent) { Assert.InRange(Math.Abs(expected.Alpha - actual.Alpha), 0, 3); if (expected.Alpha < 24) continue; }
            if (expected.Red > 30) visible++;
            total += Math.Abs(expected.Red - actual.Red) + Math.Abs(expected.Green - actual.Green) + Math.Abs(expected.Blue - actual.Blue); count += 3;
        }
        Assert.True(visible > 100); Assert.InRange(total / count, 0, 9);
        if (transparent) Assert.Equal(0, decoded.GetPixel(0, 0).Alpha);
    }

    // 此适配仅先准备帧；全部目标分区、图执行和主画布合成都由正式 Render 完成。
    private sealed class PreparedPackagePipeline : IRenderPipeline, IDisposable
    {
        private readonly IEvaluatedFrameSource _source;
        private readonly ProjectSnapshot _snapshot;
        private readonly SkiaRenderPipeline _renderer;
        private readonly SceneImageEffectResources _resources;
        private readonly CompiledPackageGraphExports _graphs;
        public PreparedPackagePipeline(IEvaluatedFrameSource source, ProjectSnapshot snapshot, EffectPackageDefinition definition)
        {
            _source = source; _snapshot = snapshot; _graphs = CompiledPackageGraphExports.Compile(definition);
            var bounds = new GraphRect(0, 0, snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight);
            _resources = new([.. definition.Graphs.Select(graph => _graphs.CreateProgram(_graphs.Resources.GetGraph(graph.Id), bounds))], null,
                [.. definition.Materials.Select(material => _graphs.CreateMaterialProgram(_graphs.Resources.GetMaterial(material.Id), bounds))]);
            _renderer = new(null, null, null, _resources);
        }
        public string BackendName => "真实混合包的显式CPU出口";
        public GraphHandle? LastHandle { get; private set; }
        public MaterialHandle? LastMaterial { get; private set; }
        public RenderFrame Render(RenderRequest request)
        {
            var prepared = _source.PrepareAsync(request.TimeUs, request.TransparentBackground ? OutputMode.Transparent : OutputMode.Opaque,
                request.ResolveTarget()).AsTask().GetAwaiter().GetResult();
            if (!prepared.ImageEffects.IsEmpty) LastHandle = Assert.Single(prepared.ImageEffects).Binding.Graph;
            if (!prepared.Materials.IsEmpty) LastMaterial = prepared.Materials[0].Material.Handle;
            var actual = request with { Project = _snapshot, PreparedFrame = prepared };
            return _renderer.Render(actual);
        }
        public void Dispose() { _renderer.Dispose(); _resources.Dispose(); _graphs.Dispose(); }
    }

    [Fact]
    public async Task 主Render透明旁路在导出报告按真实实例去重提示()
    {
        using TemporaryDirectory temp = new();
        var definition = EffectPackageDefinition.Read(MixedEffectPackageFixture.Contents(background: true));
        var package = await CompiledEffectPackage.CompileAsync(definition, new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")),
            new PackageImageValidator(), new PackageShaderValidator(), new PackageImageGraphValidator(), new PackageMaterialValidator());
        var project = Project.Create("透明提示", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 400_000),
            LyricsDocument.Create([LyricLine.Create("row", "歌", new(0, 400_000), TimingStatus.Edited)]));
        var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        await using var source = await new PureFrameSourceFactory([package.CreateEffect("controller", new("warning"), new EffectBinding.Project(), 1)],
            Fingerprint, static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        using var pipeline = new PreparedPackagePipeline(source, snapshot, definition);
        var report = await new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder()).ExportAsync(project,
            new() { OutputPath = temp.FilePath("bypass.mov"), IncludeAudio = false, Format = ExportVideoFormat.TransparentProRes4444 });
        Assert.True(report.IsVerified); Assert.Equal(0, report.FailedFrames);
        var warning = Assert.Single(report.Warnings.Where(warning => warning.Contains("TransparentBackgroundDependency", StringComparison.Ordinal)));
        Assert.Contains("warning", warning); Assert.Contains("background", warning); Assert.Contains("使用原输入", warning);
    }

    [Fact]
    public async Task 主Render中途shader坏像素附加真实实例并保护已有输出()
    {
        using TemporaryDirectory temp = new(); string output = temp.FilePath("existing.mp4"); byte[] original = [2, 7, 1, 8]; await File.WriteAllBytesAsync(output, original);
        var contents = MixedEffectPackageFixture.Contents(controllerSource: """
            using MakoKaraGenerator.Scripting;
            public sealed class PackEffect : EffectBase {
                public override void Evaluate(FrameContext f, FrameWriter w) {
                    foreach(var l in f.Presentations.LinesInBinding) w.ImageEffects.Bind(l.Target,f.Resources.GetGraph("tint"),
                        new([new("amount",new ParameterValue.Number(f.TimeUs>=200000 ? 1 : .5))]));
                }
            }
            """, shaderSource: "uniform float amount; uniform shader original; half4 main(float2 p) { half4 c=original.eval(p); return half4(c.rgb*(amount>.9 ? 4 : 1),c.a); }");
        var definition = EffectPackageDefinition.Read(contents);
        var package = await CompiledEffectPackage.CompileAsync(definition, new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")),
            new PackageImageValidator(), new PackageShaderValidator(), new PackageImageGraphValidator(), new PackageMaterialValidator());
        Assert.True(package.Succeeded);
        var project = Project.Create("像素错误", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 400_000),
            LyricsDocument.Create([LyricLine.Create("row", "歌", new(0, 400_000), TimingStatus.Edited)]),
            lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 40, UnsungColor = new(240, 240, 240, 80), SungColor = new(240, 240, 240, 80), Stroke = null, Shadow = null });
        var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        var effect = package.CreateEffect("controller", new("bad-pixel"), new EffectBinding.Project(), 1);
        await using var source = await new PureFrameSourceFactory([effect], Fingerprint, static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        using var pipeline = new PreparedPackagePipeline(source, snapshot, definition);
        var error = await Assert.ThrowsAsync<EffectFailureException>(() => new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder()).ExportAsync(project,
            new() { OutputPath = output, IncludeAudio = false, Overwrite = true }));
        Assert.Equal("GraphColorNotPremultiplied", error.Code); Assert.Equal(effect.Id, error.InstanceId); Assert.Equal(package.Artifact.ArtifactHash, error.ArtifactHash);
        Assert.Equal(200_000, error.TimeUs); Assert.Equal(original, await File.ReadAllBytesAsync(output)); Assert.Equal([output], Directory.GetFiles(temp.Path));
    }
}
