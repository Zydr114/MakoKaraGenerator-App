using System.Diagnostics;
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
using SkiaSharp;

namespace MakoKaraGenerator.Export.Tests;

public sealed partial class MixedPackageExportTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 工程内固定混合包重新打开后离线编译并编码同帧(bool transparent)
    {
        using TemporaryDirectory temp = new(); var contents = MixedEffectPackageFixture.Contents();
        var project = Project.Create("持久包成片", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 700_000),
            LyricsDocument.Create([LyricLine.Create("row", "歌词", new(0, 700_000), TimingStatus.Edited,
                [LyricToken.Create("token", "歌词", new(0, 700_000), TimingStatus.Edited)])]),
            lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 40, Stroke = null, Shadow = null }) with
        { Effects = new([contents], [new() { Id = "fx:persistent", Name = "混合图", PackageId = contents.PackageId, Revision = contents.Revision, ExportId = "controller", Seed = ulong.MaxValue }]) };
        string directory = temp.FilePath("project.mkgproj"); ProjectStore.Save(project, directory);
        var restored = ProjectStore.Load(directory).Project!; Assert.Equal(project.Effects, restored.Effects);
        var compiled = await CompiledProjectEffects.CompileAsync(restored.Effects, new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")),
            new PackageImageValidator(), new PackageShaderValidator(), new PackageImageGraphValidator(), new PackageMaterialValidator()); Assert.True(compiled.Succeeded);
        var snapshot = ProjectSnapshot.FromProject(restored);
        await using var source = await compiled.CreateFactory(static () => new SkiaRenderPipeline(), ReplayReason.Export).CreateAsync(snapshot);
        using var pipeline = new PreparedPackagePipeline(source, snapshot, Assert.Single(compiled.Packages).Definition);
        string output = temp.FilePath(transparent ? "stored.mov" : "stored.mp4");
        var report = await new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder()).ExportAsync(restored,
            new() { OutputPath = output, IncludeAudio = false, Quality = ExportQuality.High,
                Format = transparent ? ExportVideoFormat.TransparentProRes4444 : ExportVideoFormat.OpaqueMp4 });
        Assert.True(report.IsVerified); Assert.Equal(0, report.FailedFrames);
        Assert.Equal(compiled.Packages[0].Resources.GetGraph("tint"), pipeline.LastHandle);
        using var preview = pipeline.Render(RenderRequest.At(snapshot, 500000) with { TransparentBackground = transparent });
        string image = temp.FilePath("stored.png"); var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (string argument in new[] { "-hide_banner", "-loglevel", "error", "-i", output, "-vf", "select=eq(n\\,15)", "-frames:v", "1", "-y", image }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; string error = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, error);
        using var decoded = SKBitmap.Decode(image); double difference = 0; int samples = 0, ink = 0;
        for (int y = 0; y < 180; y++) for (int x = 0; x < 320; x++)
        {
            Assert.True(preview.TryGetPixel(x, y, out var expected)); var actual = decoded.GetPixel(x, y);
            if (transparent) { Assert.InRange(Math.Abs(expected.Alpha - actual.Alpha), 0, 3); if (expected.Alpha < 24) continue; }
            if (expected.Red > 30) ink++;
            difference += Math.Abs(expected.Red - actual.Red) + Math.Abs(expected.Green - actual.Green) + Math.Abs(expected.Blue - actual.Blue); samples += 3;
        }
        Assert.True(ink > 100); Assert.InRange(difference / samples, 0, 9);
        if (transparent) Assert.Equal(0, decoded.GetPixel(0, 0).Alpha);
    }
}
