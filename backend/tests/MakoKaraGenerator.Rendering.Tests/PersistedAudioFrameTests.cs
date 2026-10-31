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
    private static async Task<Project> ProjectWithAudio(TemporaryDirectory temp, bool required = true)
    {
        string audio = temp.FilePath("音频.wav"); WavWriter.Write(audio, Enumerable.Range(0, 48624).Select(i => (float)(.7 * Math.Sin(2 * Math.PI * 80 * i / 48000d))).ToArray(), 48000);
        var analysis = await AudioFeatureAnalyzer.CreateDefault().AnalyzeAsync(audio); var package = PersistedAudioEffectFixture.Package(required);
        var source = Project.Create("持久音频效果", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 1400000),
            LyricsDocument.Create([LyricLine.Create("line", "歌", new(0, 1400000), TimingStatus.Edited, [LyricToken.Create("token", "歌", new(0, 1400000), TimingStatus.Edited)])]),
            lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 50 }) with
        { AudioPath = audio, AudioAnalysis = analysis.ToSourceData(), Effects = new([package], [new() { Id = "fx:audio", Name = "低频", PackageId = package.PackageId, Revision = package.Revision, ExportId = "scale", Binding = new(ProjectEffectBindingKind.Tokens, ids: ["token"]) }]) };
        string directory = temp.FilePath("project"); ProjectStore.Save(source, directory); return ProjectStore.Load(directory).Project!;
    }
    [Fact]
    public async Task 保存重开后真实hop历史重放中段及帧率一致且required允许音轨越界()
    {
        using TemporaryDirectory temp = new(); var project = await ProjectWithAudio(temp); var snapshot = ProjectSnapshot.FromProject(project);
        var compiled = await CompiledProjectEffects.CompileAsync(project, Compiler()); Assert.True(compiled.Succeeded); Assert.True(Assert.Single(compiled.Effects).RequiresAudio);
        Assert.Equal(ProjectEffectInputs.Fingerprint(project), snapshot.EffectInputsFingerprint);
        var factory = compiled.CreateFactory(static () => new SkiaRenderPipeline(), Fx.ReplayReason.Export);
        await using var session = await factory.CreateAsync(snapshot); var target = RenderTarget.FromComposition(snapshot.Composition);
        var middle = await session.PrepareAsync(500000, Fx.OutputMode.Opaque, target); var style = Assert.Single(middle.TokenStyles);
        Assert.Contains(middle.Diagnostics, d => d.Diagnostic.Code == "RealHopCount" && d.Diagnostic.Message == "26");
        await session.PrepareAsync(100000, Fx.OutputMode.Opaque, target); var replay = await session.PrepareAsync(500000, Fx.OutputMode.Opaque, target);
        Assert.Equal(style, Assert.Single(replay.TokenStyles));
        foreach (var rate in new[] { FrameRate.Fps24, FrameRate.Fps60 })
        {
            var alternate = ProjectSnapshot.FromProject(project with { Composition = project.Composition with { FrameRate = rate } });
            await using var separate = await factory.CreateAsync(alternate); var frame = await separate.PrepareAsync(500000, Fx.OutputMode.Opaque, RenderTarget.FromComposition(alternate.Composition));
            Assert.Equal(style, Assert.Single(frame.TokenStyles));
        }
        var outside = await session.PrepareAsync(1200000, Fx.OutputMode.Opaque, target); Assert.Empty(outside.TokenStyles);
        Assert.Contains(outside.Diagnostics, d => d.Diagnostic.Code == "ActualAudioAvailability" && d.Diagnostic.Message == "OutsideRange");
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
    public async Task 固定不可用分析保持真实状态required失败而optional可重放(string kind, string availability)
    {
        using TemporaryDirectory temp = new(); var project = await ProjectWithAudio(temp);
        var data = kind == "notAnalyzed" ? null : kind == "failed" ? ProjectAudioAnalysis.Failed("RealFailure", "真实原因") : ProjectAudioAnalysis.NoStream("AudioStreamMissing", "无流");
        project = project with { AudioAnalysis = data }; var compiled = await CompiledProjectEffects.CompileAsync(project, Compiler()); var snapshot = ProjectSnapshot.FromProject(project);
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await compiled.CreateFactory(static () => new SkiaRenderPipeline(), Fx.ReplayReason.Export).CreateAsync(snapshot));
        Assert.Equal("RequiredAudioUnavailable", error.Code); Assert.Equal(availability, ProjectAudioProjection.Create(project).Availability.ToString());
        var optionalPackage = PersistedAudioEffectFixture.Package(false); project = project with { Effects = new([optionalPackage], project.Effects.Instances.Select(i => i with { Revision = optionalPackage.Revision })) };
        var optional = await CompiledProjectEffects.CompileAsync(project, Compiler()); var optionalSnapshot = ProjectSnapshot.FromProject(project);
        await using var session = await optional.CreateFactory(static () => new SkiaRenderPipeline(), Fx.ReplayReason.Export).CreateAsync(optionalSnapshot);
        var frame = await session.PrepareAsync(0, Fx.OutputMode.Opaque, RenderTarget.FromComposition(snapshot.Composition)); Assert.Empty(frame.TokenStyles);
        Assert.Contains(frame.Diagnostics, d => d.Diagnostic.Code == "ActualAudioAvailability" && d.Diagnostic.Message == availability);
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
        var error = await Assert.ThrowsAsync<PreparedFrameException>(async () => await first.CreateFactory(static () => new SkiaRenderPipeline(), Fx.ReplayReason.Export).CreateAsync(ProjectSnapshot.FromProject(next)));
        Assert.Equal("EffectInputsMismatch", error.Code);
    }
}
