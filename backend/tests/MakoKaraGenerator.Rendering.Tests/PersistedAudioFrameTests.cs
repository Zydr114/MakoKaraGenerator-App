using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.Projection;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class PersistedAudioFrameTests
{
    private static ScriptCompiler Compiler() => new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));

    private static EffectPackageContents AudioPackage()
    {
        const string source = """
            using System;
            using MakoKaraGenerator.Scripting;
            using MakoKaraGenerator.Scripting.V2;
            public sealed class PackEffect : IPureKaraokeEffect
            {
                private sealed record State : PreparedEffect;
                public PreparedEffect Declare(DeclareContext source) => new State();
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
                {
                    var sample = frame.Audio.GetAt(frame.TimeUs);
                    if (sample.Values is not { } values) return;
                    double green = Math.Round(values.BassLevel * 255) / 255;
                    foreach (var token in frame.Presentations.TokensInBinding)
                        output.SetFill(new TextTarget.Family(token.WithRuby), TextState.Both, MaterialValue.Solid(new ColorRgba(.1, green, .1, 1)));
                }
            }
            """;
        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["src/PackEffect.cs"] = sourceBytes };
        var manifest = new JsonObject
        {
            ["packageVersion"] = EffectPackageProfiles.V2.PackageVersion,
            ["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion,
            ["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile,
            ["packageId"] = "demo.persisted.audio.v2",
            ["name"] = "demo.persisted.audio.v2",
            ["version"] = "1.0.0",
            ["requires"] = new JsonArray(Fx.V2.CapabilityNames.StyleFill),
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
                    ["requires"] = new JsonArray(Fx.V2.CapabilityNames.StyleFill), ["dependsOn"] = new JsonArray(),
                }),
            }),
        };
        return EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files));
    }

    private static async Task<Project> ProjectWithAudio(TemporaryDirectory temp)
    {
        string audio = temp.FilePath("音频.wav"); WavWriter.Write(audio, Enumerable.Range(0, 48624).Select(i => (float)(.7 * Math.Sin(2 * Math.PI * 80 * i / 48000d))).ToArray(), 48000);
        var analysis = await AudioFeatureAnalyzer.CreateDefault().AnalyzeAsync(audio); var package = AudioPackage();
        var source = Project.Create("持久音频效果", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 1400000),
            LyricsDocument.Create([LyricLine.Create("line", "歌", new(0, 1400000), TimingStatus.Edited, [LyricToken.Create("token", "歌", new(0, 1400000), TimingStatus.Edited)])]),
            lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 50 }) with
        { AudioPath = audio, AudioAnalysis = analysis.ToSourceData(), Effects = new([package], [new() { Id = "fx:audio", Name = "低频", PackageId = package.PackageId, Revision = package.Revision, ExportId = "scale", Binding = new(ProjectEffectBindingKind.Tokens, ids: ["token"]) }]) };
        string directory = temp.FilePath("project"); ProjectStore.Save(source, directory); return ProjectStore.Load(directory).Project!;
    }
    [Fact]
    public async Task 保存重开后纯音频采样重放中段及帧率一致且允许音轨越界()
    {
        using TemporaryDirectory temp = new(); var project = await ProjectWithAudio(temp); var snapshot = ProjectSnapshot.FromProject(project);
        var compiled = await CompiledProjectEffects.CompileAsync(project, Compiler());
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Packages.SelectMany(p => p.Artifact.Diagnostics)));
        Assert.Equal(ProjectEffectInputs.Fingerprint(project), snapshot.EffectInputsFingerprint);
        var factory = compiled.CreateFactory(static () => new SkiaRenderPipeline());
        await using var session = await factory.CreateAsync(snapshot); var target = RenderTarget.FromComposition(snapshot.Composition);
        var middle = await session.PrepareAsync(500000, Fx.OutputMode.Opaque, target); var style = Assert.Single(middle.TokenStyles);
        await session.PrepareAsync(100000, Fx.OutputMode.Opaque, target); var replay = await session.PrepareAsync(500000, Fx.OutputMode.Opaque, target);
        Assert.Equal(style, Assert.Single(replay.TokenStyles));
        foreach (var rate in new[] { FrameRate.Fps24, FrameRate.Fps60 })
        {
            var alternate = ProjectSnapshot.FromProject(project with { Composition = project.Composition with { FrameRate = rate } });
            await using var separate = await factory.CreateAsync(alternate); var frame = await separate.PrepareAsync(500000, Fx.OutputMode.Opaque, RenderTarget.FromComposition(alternate.Composition));
            Assert.Equal(style, Assert.Single(frame.TokenStyles));
        }
        var outside = await session.PrepareAsync(1200000, Fx.OutputMode.Opaque, target); Assert.Empty(outside.TokenStyles);
        Assert.Equal(Fx.AudioAvailability.OutsideRange, ProjectAudioProjection.Create(project).GetAt(1200000).Availability);
        using var renderer = new SkiaRenderPipeline(); using var actual = renderer.Render(RenderRequest.At(snapshot, 500000) with { PreparedFrame = middle });
        var features = ProjectAudioProjection.Create(project); byte green = (byte)Math.Round(features.GetAt(500000).Values!.Value.BassLevel * 255);
        var plain = ProjectSnapshot.FromProject(project with { Effects = ProjectEffects.Empty, LyricStyle = project.LyricStyle with { UnsungColor = new(26, green, 26), SungColor = new(26, green, 26) } });
        using var expected = renderer.Render(RenderRequest.At(plain, 500000));
        for (int y = 0; y < 180; y++) for (int x = 0; x < 320; x++)
        { Assert.True(actual.TryGetPixel(x, y, out var a)); Assert.True(expected.TryGetPixel(x, y, out var b)); Assert.Equal(a, b); }
    }
    [Theory]
    [InlineData("notAnalyzed", "NotAnalyzed")]
    [InlineData("failed", "AnalysisFailed")]
    [InlineData("noStream", "NoAudio")]
    public async Task 固定不可用分析保持真实状态且纯求值安全返回空样式(string kind, string availability)
    {
        using TemporaryDirectory temp = new(); var project = await ProjectWithAudio(temp);
        var data = kind == "notAnalyzed" ? null : kind == "failed" ? ProjectAudioAnalysis.Failed("RealFailure", "真实原因") : ProjectAudioAnalysis.NoStream("AudioStreamMissing", "无流");
        project = project with { AudioAnalysis = data }; var compiled = await CompiledProjectEffects.CompileAsync(project, Compiler());
        Assert.True(compiled.Succeeded); var snapshot = ProjectSnapshot.FromProject(project);
        Assert.Equal(availability, ProjectAudioProjection.Create(project).Availability.ToString());
        await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(0, Fx.OutputMode.Opaque, RenderTarget.FromComposition(snapshot.Composition));
        Assert.Empty(frame.TokenStyles);
    }
    [Fact]
    public async Task 完整分析内容进入指纹但不会改变实际CSharp编译产物()
    {
        using TemporaryDirectory temp = new(); var project = await ProjectWithAudio(temp); var first = await CompiledProjectEffects.CompileAsync(project, Compiler());
        var old = project.AudioAnalysis!.Data!; var changed = new AudioAnalysisRevision(old.SourceHash, old.PcmHash, old.SampleCount,
            old.Points.Select(p => new AudioAnalysisPoint(p.TimeUs, .25, p.Pulse, p.BeatPhase, p.BeatConfidence)));
        var next = project with { AudioAnalysis = ProjectAudioAnalysis.Available(changed) }; var second = await CompiledProjectEffects.CompileAsync(next, Compiler());
        Assert.Equal(old.Revision, changed.Revision); Assert.NotEqual(old.ContentHash, changed.ContentHash); Assert.NotEqual(ProjectEffectInputs.Fingerprint(project), ProjectEffectInputs.Fingerprint(next));
        Assert.Equal(first.Packages[0].Artifact.ArtifactHash, second.Packages[0].Artifact.ArtifactHash);
        var error = await Assert.ThrowsAsync<PreparedFrameException>(async () => await first.CreateFactory(static () => new SkiaRenderPipeline()).CreateAsync(ProjectSnapshot.FromProject(next)));
        Assert.Equal("EffectInputsMismatch", error.Code);
    }
}
