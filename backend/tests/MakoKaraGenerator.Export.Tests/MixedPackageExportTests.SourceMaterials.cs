using System.Diagnostics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;
using SkiaSharp;

namespace MakoKaraGenerator.Export.Tests;

public sealed partial class MixedPackageExportTests
{
    private static Project SourceMaterialProject(EffectPackageContents contents)
    {
        var project = MaterialProject(); var material = new MaterialSpec.PackageMaterial(contents.PackageId, contents.Revision, "ink");
        var line = project.Lyrics.Lines[0]; line = line with { RubyAnnotations = [RubyAnnotation.Create("reading", "token", "かし")] };
        var branch = project.Lyrics.PrimaryBranch!;
        return project with { Effects = new([contents], []), Lyrics = project.Lyrics with { Branches = project.Lyrics.Branches.Replace(branch, branch with { Lines = branch.Lines.Replace(branch.Lines[0], line) }) },
            LyricStyle = project.LyricStyle with { UnsungFill = new() { Material = material }, SungFill = new() { Material = material }, Stroke = new() { Material = material, WidthPx = 3 } } };
    }
    private static Task<CompiledProjectEffects> CompileSources(Project project) => CompiledProjectEffects.CompileAsync(project,
        new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")), new PackageImageValidator(), new PackageShaderValidator(), new PackageImageGraphValidator(), new PackageMaterialValidator());

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 纯源材质保存重开无控制器仍真实编码MP4和Alpha同帧(bool transparent)
    {
        using TemporaryDirectory temp = new(); var project = SourceMaterialProject(MixedEffectPackageFixture.Contents(pure: true, background: transparent));
        string directory = temp.FilePath("source.mkgproj"); ProjectStore.Save(project, directory); project = ProjectStore.Load(directory).Project!;
        var compiled = await CompileSources(project); Assert.True(compiled.Succeeded); Assert.Empty(compiled.Effects);
        var package = Assert.Single(compiled.Packages); Assert.Null(package.ControllerArtifact);
        var snapshot = ProjectSnapshot.FromProject(project);
        await using var source = await compiled.CreateFactory(static () => new SkiaRenderPipeline(), ReplayReason.Export).CreateAsync(snapshot);
        using var pipeline = new PreparedPackagePipeline(source, snapshot, package.Definition);
        string output = temp.FilePath(transparent ? "source.mov" : "source.mp4");
        var report = await new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder()).ExportAsync(project,
            new() { OutputPath = output, IncludeAudio = false, Quality = ExportQuality.High, Format = transparent ? ExportVideoFormat.TransparentProRes4444 : ExportVideoFormat.OpaqueMp4 });
        Assert.True(report.IsVerified); Assert.Equal(0, report.FailedFrames);
        if (transparent)
        {
            var warnings = report.Warnings.Where(warning => warning.Contains("TransparentBackgroundDependency", StringComparison.Ordinal)).ToArray();
            Assert.Equal(3, warnings.Length); Assert.All(warnings, warning => { Assert.Contains("源样式 lyricStyle.", warning); Assert.Contains("材质 ink", warning); Assert.Contains("包内基础外观", warning); });
        }
        using var preview = pipeline.Render(RenderRequest.At(snapshot, 500_000) with { TransparentBackground = transparent });
        string image = temp.FilePath("source.png"); var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (string argument in new[] { "-hide_banner", "-loglevel", "error", "-i", output, "-vf", "select=eq(n\\,15)", "-frames:v", "1", "-y", image }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; string error = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, error);
        using var decoded = SKBitmap.Decode(image); double difference = 0; int samples = 0, ink = 0;
        for (int y = 0; y < 180; y++) for (int x = 0; x < 320; x++)
        {
            Assert.True(preview.TryGetPixel(x, y, out var expected)); var actual = decoded.GetPixel(x, y);
            if (transparent) { Assert.InRange(Math.Abs(expected.Alpha - actual.Alpha), 0, 3); if (expected.Alpha < 24) continue; }
            if (expected.Blue > 30) ink++;
            difference += Math.Abs(expected.Red - actual.Red) + Math.Abs(expected.Green - actual.Green) + Math.Abs(expected.Blue - actual.Blue); samples += 3;
        }
        Assert.True(ink > 100); Assert.InRange(difference / samples, 0, 9);
        if (transparent) Assert.Equal(0, decoded.GetPixel(0, 0).Alpha);
    }

    [Fact]
    public async Task 纯源材质中途坏覆盖保留真实源错误并保护原成片()
    {
        using TemporaryDirectory temp = new(); string output = temp.FilePath("existing.mp4"); byte[] original = [2, 7, 1, 8]; await File.WriteAllBytesAsync(output, original);
        var contents = MixedEffectPackageFixture.Contents(pure: true, graphChange: graph => graph["nodes"]![0]!["uniforms"]!["amount"] = "host:timeSeconds",
            shaderSource: "uniform float amount; uniform shader original; half4 main(float2 p) { half4 c=original.eval(p); return amount>=.2 ? half4(c.rgb,1) : c; }");
        var project = SourceMaterialProject(contents); var compiled = await CompileSources(project); Assert.Empty(compiled.Effects);
        var snapshot = ProjectSnapshot.FromProject(project);
        await using var source = await compiled.CreateFactory(static () => new SkiaRenderPipeline(), ReplayReason.Export).CreateAsync(snapshot);
        using var pipeline = new PreparedPackagePipeline(source, snapshot, Assert.Single(compiled.Packages).Definition);
        var failure = await Assert.ThrowsAsync<SourceMaterialFailureException>(() => new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder()).ExportAsync(project,
            new() { OutputPath = output, IncludeAudio = false, Overwrite = true, Format = ExportVideoFormat.OpaqueMp4 }));
        Assert.Equal("MaterialOutputOutsideRegion", failure.Code); Assert.Equal(200_000, failure.TimeUs);
        Assert.StartsWith("lyricStyle.", failure.Owner.Reference.Path); Assert.Equal(contents.Revision, failure.Owner.Reference.Material.Revision);
        Assert.Equal(original, await File.ReadAllBytesAsync(output)); Assert.Equal([output], Directory.GetFiles(temp.Path));
    }
}
