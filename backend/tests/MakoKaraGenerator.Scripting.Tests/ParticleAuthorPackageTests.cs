using System.Text;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Loading;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class ParticleAuthorPackageTests
{
    private static async Task<CompiledEffectPackage> Import(string name, TemporaryDirectory temp)
    {
        string source = Path.Combine(AppContext.BaseDirectory, "effect-packages", name);
        var package = await EffectPackageTransfer.ReadDirectoryAsync(source, EffectPackageFixture.Profile);
        string archive = temp.FilePath(name + ".mkfx");
        await EffectPackageTransfer.WriteArchiveAsync(archive, package);
        var imported = await EffectPackageTransfer.ReadArchiveAsync(archive, EffectPackageFixture.Profile);
        Assert.Equal(package.Revision, imported.Revision);
        Assert.Equal(package.Files.Select(file => (file.Path, file.Sha256)), imported.Files.Select(file => (file.Path, file.Sha256)));
        var compiled = await CompiledEffectPackage.CompileAsync(EffectPackageDefinition.Read(imported),
            new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")));
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Artifact.Diagnostics));
        return compiled;
    }

    private static ProjectContext EmptyProject()
    {
        ContentCatalog content = new([], [], [], []);
        return new() { Content = content, Scene = new(content, [], [], [], []), CanvasSize = new(640, 360) };
    }

    [Fact]
    public async Task 完整向量包独立分享后保持Jump默认并登记显式桥接及其物理参数()
    {
        const string name = "VectorTrail", export = "vector-trail";
        using TemporaryDirectory temp = new(); var compiled = await Import(name, temp);
        var defaultEffect = compiled.CreateEffect(export, new("default"), new EffectBinding.Project(), 23);
        Assert.Equal("jump", defaultEffect.Parameters.GetEnum("transition"));
        Assert.Equal(180000, defaultEffect.Parameters.GetDuration("bridgeUs"));
        Assert.Equal(2000000, defaultEffect.Parameters.GetDuration("bridgeGapLimitUs"));
        var effect = compiled.CreateEffect(export, new("bridge"), new EffectBinding.Project(), 23,
            Encoding.UTF8.GetBytes("""{"transition":"bridge","bridgeUs":45001,"bridgeGapLimitUs":250000,"rate":37,"lifeUs":1200000}"""));
        using ScriptModule module = new(compiled.Artifact.PeImage.AsMemory(), compiled.Artifact.PdbImage.AsMemory());
        var author = module.CreateInstance<IKaraokeEffect>(effect.EntryType!);
        var project = EmptyProject(); DeterministicRandom random = new(effect.Seed, effect.Id);
        author.Initialize(new() { Project = project, Parameters = effect.Parameters, Resources = effect.Resources, Random = random });
        EmitterWriter writer = new(effect.Id, effect.Capabilities);
        author.EvaluateEmitters(new() { Project = project, Parameters = effect.Parameters, Resources = effect.Resources,
            Random = random, Binding = effect.Binding, FinalGeometry = new() { TimeUs = 0, Presentations = new() } }, writer);
        var emitter = Assert.Single(writer.Complete());
        Assert.Equal(TransitionMode.Bridge, emitter.Descriptor.TransitionMode);
        Assert.Equal(45001, emitter.Descriptor.BridgeDurationUs); Assert.Equal(250000, emitter.Descriptor.BridgeGapLimitUs);
        Assert.Equal(37, emitter.Descriptor.RatePerSecond); Assert.Equal(1200000, emitter.Descriptor.LifetimeUs);
        Assert.Equal(ParticleRenderMode.Custom, emitter.Descriptor.Rendering);
    }

    [Theory]
    [InlineData("token", 0)] [InlineData("line", 6)] [InlineData("cursor", 3)] [InlineData("mark", 2)]
    public async Task 完整Burst包分享编译保留来源选择整数数量与公开默认值(string anchor, int count)
    {
        using TemporaryDirectory temp = new(); var compiled = await Import("EventBurst", temp);
        var defaults = compiled.CreateEffect("event-burst", new("default"), new EffectBinding.Project(), 23);
        Assert.Equal("token", defaults.Parameters.GetEnum("anchor")); Assert.Equal(6, defaults.Parameters.GetNumber("count"));
        Assert.Equal(600000, defaults.Parameters.GetDuration("lifeUs")); Assert.Equal(180000, defaults.Parameters.GetDuration("trailUs"));
        Assert.Equal("burst", Assert.Single(defaults.PackageDeclaration!.Outputs).Id);
        var effect = compiled.CreateEffect("event-burst", new("burst"), new EffectBinding.Project(), 23,
            Encoding.UTF8.GetBytes($$"""{"anchor":"{{anchor}}","count":{{count}}}"""));
        using ScriptModule module = new(compiled.Artifact.PeImage.AsMemory(), compiled.Artifact.PdbImage.AsMemory());
        var author = module.CreateInstance<IKaraokeEffect>(effect.EntryType!);
        author.Initialize(new() { Project = EmptyProject(), Parameters = effect.Parameters, Resources = effect.Resources,
            Random = new(effect.Seed, effect.Id) });
    }

    [Fact]
    public async Task 清单允许的分数数量在真实作者初始化拒绝而非静默取整()
    {
        using TemporaryDirectory temp = new(); var compiled = await Import("EventBurst", temp);
        var effect = compiled.CreateEffect("event-burst", new("burst"), new EffectBinding.Project(), 23,
            Encoding.UTF8.GetBytes("""{"count":1.5}"""));
        using ScriptModule module = new(compiled.Artifact.PeImage.AsMemory(), compiled.Artifact.PdbImage.AsMemory());
        var author = module.CreateInstance<IKaraokeEffect>(effect.EntryType!);
        var failure = Assert.Throws<EffectOutputException>(() => author.Initialize(new() { Project = EmptyProject(),
            Parameters = effect.Parameters, Resources = effect.Resources, Random = new(effect.Seed, effect.Id) }));
        Assert.Equal("InvalidBurst", failure.Code);
    }
}
