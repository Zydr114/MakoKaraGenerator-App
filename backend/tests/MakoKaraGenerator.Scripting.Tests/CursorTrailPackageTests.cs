using MakoKaraGenerator.Application;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Loading;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class CursorTrailPackageTests
{
    [Theory]
    [InlineData("jump")] [InlineData("bridge")]
    public async Task 真实拖尾目录分享后离开原目录仍编译并登记独立发射描述(string mode)
    {
        using TemporaryDirectory temp = new(); string directory = temp.FilePath("editable");
        string sample = Path.Combine(AppContext.BaseDirectory, "effect-packages", "CursorTrail");
        foreach (var path in Directory.EnumerateFiles(sample, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(directory, Path.GetRelativePath(sample, path)); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target);
        }
        var original = await EffectPackageTransfer.ReadDirectoryAsync(directory, EffectPackageFixture.Profile);
        string archive = temp.FilePath("CursorTrail.mkfx"); await EffectPackageTransfer.WriteArchiveAsync(archive, original);
        Directory.Move(directory, temp.FilePath("moved"));
        var imported = await EffectPackageTransfer.ReadArchiveAsync(archive, EffectPackageFixture.Profile);
        Assert.Equal(original.Revision, imported.Revision); Assert.Equal("makokara.cursor-trail", imported.PackageId);
        var compiled = await CompiledEffectPackage.CompileAsync(EffectPackageDefinition.Read(imported),
            new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")));
        Assert.True(compiled.Artifact.Succeeded, string.Join('\n', compiled.Artifact.Diagnostics));
        var definition = compiled.CreateEffect("cursor-trail", new("real"), new EffectBinding.Project(), 7,
            System.Text.Encoding.UTF8.GetBytes("{\"transition\":\"" + mode + "\"}"));
        Assert.Equal(EffectCapabilities.Overlay, definition.Capabilities); Assert.Equal(9, compiled.Definition.Parameters.Length);
        Assert.Equal("cursor", Assert.Single(definition.PackageDeclaration!.Outputs).Id);
        using ScriptModule module = new(compiled.Artifact.PeImage.AsMemory(), compiled.Artifact.PdbImage.AsMemory());
        var author = module.CreateInstance<IKaraokeEffect>(definition.EntryType!);
        ContentCatalog catalog = new([], [], [], []);
        ProjectContext project = new() { Content = catalog, Scene = new(catalog, [], [], [], []), CanvasSize = new(640, 360) };
        DeterministicRandom random = new(definition.Seed, definition.Id);
        author.Initialize(new() { Project = project, Parameters = definition.Parameters, Resources = definition.Resources, Random = random });
        EmitterWriter writer = new(definition.Id, definition.Capabilities);
        author.EvaluateEmitters(new() { Project = project, Parameters = definition.Parameters, Resources = definition.Resources,
            Random = random, Binding = definition.Binding, FinalGeometry = new() { TimeUs = 0, Presentations = new() } }, writer);
        var emitter = Assert.Single(writer.Complete()); Assert.Equal("cursor", emitter.Id.Value); Assert.Equal(60, emitter.Descriptor.RatePerSecond);
        Assert.Equal(600_000, emitter.Descriptor.LifetimeUs); Assert.Equal(250_000, emitter.Descriptor.TrailDurationUs);
        Assert.Equal(mode == "bridge" ? TransitionMode.Bridge : TransitionMode.Jump, emitter.Descriptor.TransitionMode);
        Assert.Equal(180000, emitter.Descriptor.BridgeDurationUs); Assert.Equal(2000000, emitter.Descriptor.BridgeGapLimitUs);
        Assert.Equal(0.5, emitter.Descriptor.Motion.Opacity.Evaluate(.5));
    }
    [Fact]
    public async Task 旧视觉发射接口源码不兼容也不生成可启用DLL()
    {
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var result = await compiler.CompileAsync([new("src/OldCursor.cs", """
            using MakoKaraGenerator.Scripting;
            public sealed class OldCursor : EffectBase
            {
                public override void EvaluateVisuals(VisualFrameContext frame, VisualWriter output) => output.Emitters.Set(new("cursor"), null);
            }
            """)]);
        Assert.False(result.Succeeded); Assert.Empty(result.PeImage);
        Assert.Contains(result.Diagnostics, d => d.IsError && d.Path == "src/OldCursor.cs" && d.Message.Contains("Emitters", StringComparison.Ordinal));
    }
}
