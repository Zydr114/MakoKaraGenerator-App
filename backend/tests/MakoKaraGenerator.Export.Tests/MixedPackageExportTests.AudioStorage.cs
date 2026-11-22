using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
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
using MakoKaraGenerator.Scripting.V2;
using SkiaSharp;

namespace MakoKaraGenerator.Export.Tests;

public sealed partial class MixedPackageExportTests
{
    private static EffectPackageContents StoredAudioPackage(bool failAfter200ms = false)
    {
        string source = """
            using System;
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed class PackEffect : IPureKaraokeEffect
            {
                private sealed record State : PreparedEffect;
                public PreparedEffect Declare(DeclareContext source) => new State();
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
                {
                    if (FAIL && frame.TimeUs >= 200000)
                        throw new InvalidOperationException("PersistedAudioFailure: real author failure after 200ms");
                    var sample = frame.Audio.GetAt(frame.TimeUs);
                    if (sample.Values is not { } values) return;
                    double green = Math.Round(values.BassLevel * 255) / 255;
                    foreach (var token in frame.Presentations.TokensInBinding)
                        output.SetFill(new TextTarget.Family(token.WithRuby), TextState.Both, MaterialValue.Solid(new ColorRgba(.1, green, .1, 1)));
                }
            }
            """.Replace("FAIL", failAfter200ms ? "true" : "false", StringComparison.Ordinal);
        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["src/PackEffect.cs"] = sourceBytes };
        var manifest = new JsonObject
        {
            ["packageVersion"] = EffectPackageProfiles.V2.PackageVersion,
            ["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion,
            ["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile,
            ["packageId"] = "demo.persisted.audio.export.v2",
            ["name"] = "demo.persisted.audio.export.v2",
            ["version"] = "1.0.0",
            ["requires"] = new JsonArray(CapabilityNames.StyleFill),
            ["lineExit"] = new JsonObject { ["durationUs"] = 0 },
            ["emitters"] = new JsonArray(),
            ["files"] = new JsonArray(new JsonObject
            {
                ["path"] = "src/PackEffect.cs", ["role"] = "csharp", ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(sourceBytes)),
            }),
            ["parameters"] = new JsonArray(),
            ["exports"] = new JsonArray(new JsonObject
            {
                ["id"] = "scale", ["kind"] = "controller", ["name"] = "scale", ["entryType"] = "PackEffect",
                ["parameters"] = new JsonArray(),
                ["bindingKinds"] = new JsonArray("project", "node", "branch", "lines", "tokens", "clips"),
                ["outputs"] = new JsonArray(new JsonObject
                {
                    ["id"] = "scale", ["kind"] = "StyleModulation", ["target"] = "tokenFamily",
                    ["requires"] = new JsonArray(CapabilityNames.StyleFill), ["dependsOn"] = new JsonArray(),
                }),
            }),
        };
        return EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files));
    }

    private static async Task<Project> StoredAudioProject(TemporaryDirectory temp, bool fail = false)
    {
        string audio = temp.FilePath("真实低频.wav");
        WavWriter.Write(audio, Enumerable.Range(0, 48000).Select(i => (float)(.7 * Math.Sin(2 * Math.PI * 80 * i / 48000d))).ToArray(), 48000);
        var analysis = await AudioFeatureAnalyzer.CreateDefault().AnalyzeAsync(audio);
        Assert.Equal(AudioAvailability.Available, analysis.Features.Availability);
        var package = StoredAudioPackage(failAfter200ms: fail);
        var project = Project.Create("已保存音频脚本", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 700000),
            LyricsDocument.Create([LyricLine.Create("line", "歌词", new(0, 700000), TimingStatus.Edited,
                [LyricToken.Create("token", "歌词", new(0, 700000), TimingStatus.Edited)])]), audioPath: audio,
            lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 40, Stroke = null, Shadow = null }) with
        { AudioAnalysis = analysis.ToSourceData(), Effects = new([package], [new() { Id = "fx:stored-audio", Name = "真实低频", PackageId = package.PackageId,
            Revision = package.Revision, ExportId = "scale", Binding = new(ProjectEffectBindingKind.Tokens, ids: ["token"]) }]) };
        string directory = temp.FilePath("完整工程"); ProjectStore.Save(project, directory);
        File.Delete(audio);
        var restored = ProjectStore.Load(directory); Assert.True(restored.Loaded, restored.Report.ToSummary()); return restored.Project!;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 保存重开真实音频分析在MP4与透明出口保持预览同帧并携带音轨(bool transparent)
    {
        using TemporaryDirectory temp = new(); var project = await StoredAudioProject(temp);
        var compiled = await CompiledProjectEffects.CompileAsync(project, new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")));
        Assert.True(compiled.Succeeded); var snapshot = ProjectSnapshot.FromProject(project);
        await using var source = await compiled.CreateFactory(static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        using var pipeline = new PreparedPackagePipeline(source, snapshot, Assert.Single(compiled.Packages).Definition);
        string output = temp.FilePath(transparent ? "audio.mov" : "audio.mp4");
        var report = await new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder()).ExportAsync(project,
            new() { OutputPath = output, IncludeAudio = true, Quality = ExportQuality.High,
                Format = transparent ? ExportVideoFormat.TransparentProRes4444 : ExportVideoFormat.OpaqueMp4 });
        Assert.True(report.IsVerified); Assert.Equal(0, report.FailedFrames);
        Assert.True((await new FfmpegMediaProbe().ProbeFileAsync(output))!.HasAudio);
        using var preview = pipeline.Render(RenderRequest.At(snapshot, 500000) with { TransparentBackground = transparent });
        string image = temp.FilePath("audio.png"); var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (string argument in new[] { "-hide_banner", "-loglevel", "error", "-i", output, "-vf", "select=eq(n\\,15)", "-frames:v", "1", "-y", image }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; string error = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, error);
        using var decoded = SKBitmap.Decode(image); double difference = 0; int samples = 0, ink = 0;
        for (int y = 0; y < 180; y++) for (int x = 0; x < 320; x++)
        {
            Assert.True(preview.TryGetPixel(x, y, out var expected)); var actual = decoded.GetPixel(x, y);
            if (transparent) { Assert.InRange(Math.Abs(expected.Alpha - actual.Alpha), 0, 3); if (expected.Alpha < 24) continue; }
            if (expected.Green > 30) ink++;
            difference += Math.Abs(expected.Red - actual.Red) + Math.Abs(expected.Green - actual.Green) + Math.Abs(expected.Blue - actual.Blue); samples += 3;
        }
        Assert.True(ink > 100); Assert.InRange(difference / samples, 0, 9);
        if (transparent) Assert.Equal(0, decoded.GetPixel(0, 0).Alpha);
    }

    [Fact]
    public async Task 已保存真实音频脚本在200ms作者Error时拒绝成片并保护旧产物()
    {
        using TemporaryDirectory temp = new(); var project = await StoredAudioProject(temp, true);
        var compiled = await CompiledProjectEffects.CompileAsync(project, new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")));
        Assert.True(compiled.Succeeded); var snapshot = ProjectSnapshot.FromProject(project);
        await using var source = await compiled.CreateFactory(static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        using var pipeline = new PreparedPackagePipeline(source, snapshot, Assert.Single(compiled.Packages).Definition);
        string output = temp.FilePath("existing.mp4"); byte[] original = [9, 2, 6, 5]; await File.WriteAllBytesAsync(output, original);
        var error = await Assert.ThrowsAsync<EffectFailureException>(() => new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder())
            .ExportAsync(project, new() { OutputPath = output, IncludeAudio = true, Overwrite = true }));
        Assert.Equal("ScriptExecutionFailed", error.Code); Assert.Contains("PersistedAudioFailure", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(200000, error.TimeUs); Assert.Equal(new("fx:stored-audio"), error.InstanceId);
        Assert.Equal(original, await File.ReadAllBytesAsync(output)); Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp*", SearchOption.AllDirectories));
    }
}
