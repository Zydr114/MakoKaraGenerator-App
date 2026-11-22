using System.Text;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class ProjectEffectsRuntimeTests
{
    private static Project Source(EffectPackageContents? package = null)
    {
        package ??= EffectPackageFixture.Contents();
        return Project.Create("重新打开后实际执行", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 1_000_000),
            LyricsDocument.Create([LyricLine.Create("row", "歌", new(0, 1_000_000), TimingStatus.Edited,
                [LyricToken.Create("token", "歌", new(0, 900_000), TimingStatus.Edited)]) with { RubyAnnotations = [RubyAnnotation.Create("ruby", "token", "か")] }]),
            lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 64 }) with
        {
            Effects = new([package], [new() { Id = "fx:saved", Name = "源包放大", PackageId = package.PackageId, Revision = package.Revision, ExportId = "scale",
                Binding = new(ProjectEffectBindingKind.Tokens, ids: ["token"]), Seed = ulong.MaxValue, Subscription = new("project"), Parameters = EffectParameterOverrides.Parse("{\"peak\":2.25}") }]),
        };
    }
    private static ScriptCompiler Compiler() => new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
    [Fact]
    public async Task 重新打开的包独立离线编译与真实像素及注音族同步放大()
    {
        using TemporaryDirectory directory = new(); var source = Source(); ProjectStore.Save(source, directory.Path);
        var restored = ProjectStore.Load(directory.Path).Project!; var snapshot = ProjectSnapshot.FromProject(restored);
        Assert.Equal(restored.Effects.InputsFingerprint, snapshot.EffectInputsFingerprint);
        using var renderer = new SkiaRenderPipeline();
        Assert.Equal("MissingPreparedFrame", Assert.Throws<PreparedFrameException>(() => renderer.Render(RenderRequest.At(snapshot, 500000))).Code);
        var compiled = await CompiledProjectEffects.CompileAsync(restored.Effects, Compiler()); Assert.True(compiled.Succeeded);
        Assert.Equal(ulong.MaxValue, Assert.Single(compiled.Effects).Seed); Assert.IsType<Fx.SourceSubscription.Project>(compiled.Effects[0].Subscription);
        await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        var prepared = await session.PrepareAsync(500000, Fx.OutputMode.Opaque, RenderTarget.FromComposition(snapshot.Composition));
        Assert.Equal(2.25, Assert.Single(prepared.TokenTransforms).LocalMatrix.A);
        var request = RenderRequest.At(snapshot, 500000); using var actual = renderer.Render(request with { PreparedFrame = prepared });
        var plain = ProjectSnapshot.FromProject(source with { Effects = ProjectEffects.Empty }); using var expected = renderer.Render(RenderRequest.At(plain, 500000));
        int changed = 0; for (int y = 0; y < 360; y++) for (int x = 0; x < 640; x++)
        { Assert.True(actual.TryGetPixel(x, y, out var a)); Assert.True(expected.TryGetPixel(x, y, out var b)); if (a != b) changed++; }
        Assert.True(changed > 100);
        var geometricToken = Assert.Single(prepared.FinalGeometry); Assert.Single(geometricToken.Ruby);
        var changedSource = new ProjectEffects(restored.Effects.Packages, restored.Effects.Instances.Select(i => i with { Parameters = EffectParameterOverrides.Parse("{\"peak\":2.5}") }));
        Assert.NotEqual(snapshot.EffectInputsFingerprint, changedSource.InputsFingerprint);
        var mismatch = await Assert.ThrowsAsync<PreparedFrameException>(async () => await compiled.CreateFactory(static () => new SkiaRenderPipeline())
            .CreateAsync(ProjectSnapshot.FromProject(restored with { Effects = changedSource })));
        Assert.Equal("EffectInputsMismatch", mismatch.Code);
    }
    [Fact]
    public async Task 坏源码可重新打开但编译失败不产生部分会话或旧DLL()
    {
        using TemporaryDirectory directory = new(); var files = EffectPackageFixture.Files(); files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes("invalid !!!");
        var contents = EffectPackageContents.Create(EffectPackageFixture.Bytes(EffectPackageFixture.Manifest(files)), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
        ProjectStore.Save(Source(contents), directory.Path); var restored = ProjectStore.Load(directory.Path).Project!;
        var compiled = await CompiledProjectEffects.CompileAsync(restored.Effects, Compiler()); Assert.False(compiled.Succeeded); Assert.Empty(compiled.Effects);
        Assert.Contains(compiled.Packages[0].Artifact.Diagnostics, d => d.IsError && d.Path == "src/PackEffect.cs");
        Assert.Equal("ProjectEffectsCompilationFailed", Assert.Throws<EffectPackageException>(() => compiled.CreateFactory(static () => new SkiaRenderPipeline())).Code);
    }
    [Fact]
    public async Task 全禁用实例不编译坏源码且顺序种子参数逐实例投影()
    {
        var source = Source(); var first = source.Effects.Instances[0];
        var effects = new ProjectEffects(source.Effects.Packages, [first with { Id = "fx:b", Seed = 123 }, first with { Id = "fx:c", Seed = 456, Parameters = EffectParameterOverrides.Parse("{\"peak\":3}") }]);
        var compiled = await CompiledProjectEffects.CompileAsync(effects, Compiler()); Assert.True(compiled.Succeeded); Assert.Single(compiled.Packages);
        Assert.Equal(new ulong[] {123, 456}, compiled.Effects.Select(i => i.Seed)); Assert.Equal(2.25, compiled.Effects[0].Parameters.GetNumber("peak")); Assert.Equal(3, compiled.Effects[1].Parameters.GetNumber("peak"));
        var disabled = new ProjectEffects(effects.Packages, effects.Instances.Select(i => i with { Enabled = false }));
        var empty = await CompiledProjectEffects.CompileAsync(disabled, Compiler()); Assert.True(empty.Succeeded); Assert.Empty(empty.Packages); Assert.Empty(empty.Effects); Assert.Null(disabled.InputsFingerprint);
    }
}
