using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class V2HostDirectGraphPackageTests
{
    private sealed class ResourceOnlyCompiler : IScriptCompilationService
    {
        private readonly ScriptCompiler _real = new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        public ScriptCompilationIdentity Identity => _real.Identity;
        public int Calls { get; private set; }
        public Task<ScriptCompilationResult> CompileAsync(IEnumerable<ScriptSource> sources, IEnumerable<string> entryTypes, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("resource-only graph must never compile a DLL"); }
    }
    [Theory]
    [InlineData("tokenFamily")] [InlineData("line")] [InlineData("textMark")]
    public async Task 纯资源v3直接图无DLL无作者入口且真实目标重复乱序和重开一致(string target)
    {
        bool mark = target == "textMark"; var package = V2DirectAndMaterialPackageFixture.Package(true, target);
        Assert.DoesNotContain(package.Files, file => file.Role == "csharp");
        var snapshot = V2DirectAndMaterialPackageFixture.Snapshot(package, [V2DirectAndMaterialPackageFixture.Instance(package, direct: true)], mark);
        var compiler = new ResourceOnlyCompiler(); var compiled = await V2DirectAndMaterialPackageFixture.Compile(snapshot, compiler);
        Assert.Equal(0, compiler.Calls); Assert.Null(Assert.Single(compiled.Packages).ControllerArtifact);
        var effect = Assert.Single(compiled.Effects); Assert.Null(effect.Artifact); Assert.Null(effect.EntryType); Assert.True(effect.IsV2); Assert.Equal(package.Revision, effect.RuntimeHash);
        await using var session = await V2DirectAndMaterialPackageFixture.Factory(snapshot, compiled).CreateAsync(snapshot);
        long[] times = mark ? [10_000_000, 9_250_000, 10_000_000] : [1_250_000, 600_250_000, 2_750_000, 1_250_000];
        using var native = CompiledPackageGraphExports.Compile(EffectPackageDefinition.Read(package));
        var first = await V2DirectAndMaterialPackageFixture.Prepare(session, snapshot, times[0]);
        Assert.NotEmpty(first.ImageEffects);
        Assert.All(first.ImageEffects, binding => { Assert.Equal(effect.Id, binding.InstanceId); Assert.Equal(effect.PackageIdentity, binding.PackageIdentity); Assert.Equal(package.Revision, binding.ArtifactHash); Assert.Empty(binding.Binding.Uniforms.Values); Assert.Empty(binding.Binding.Transforms.Values); });
        foreach (long time in times) Assert.NotEmpty((await V2DirectAndMaterialPackageFixture.Prepare(session, snapshot, time)).ImageEffects);
        var repeated = await V2DirectAndMaterialPackageFixture.Prepare(session, snapshot, times[0]);
        Assert.Equal(first.ImageEffects.ToArray(), repeated.ImageEffects.ToArray());
        Assert.Equal(V2DirectAndMaterialPackageFixture.Pixels(snapshot, first, native), V2DirectAndMaterialPackageFixture.Pixels(snapshot, repeated, native));
        Assert.Contains(V2DirectAndMaterialPackageFixture.Pixels(snapshot, first, native), value => value != 0);
        var referenceSnapshot = V2DirectAndMaterialPackageFixture.Snapshot(package, [V2DirectAndMaterialPackageFixture.Instance(package, direct: true, amount: 1)], mark);
        var referenceCompiled = await V2DirectAndMaterialPackageFixture.Compile(referenceSnapshot, compiler);
        await using var referenceSession = await V2DirectAndMaterialPackageFixture.Factory(referenceSnapshot, referenceCompiled).CreateAsync(referenceSnapshot);
        var referenceFrame = await V2DirectAndMaterialPackageFixture.Prepare(referenceSession, referenceSnapshot, times[0]);
        var full = V2DirectAndMaterialPackageFixture.Pixels(referenceSnapshot, referenceFrame, native); var tinted = V2DirectAndMaterialPackageFixture.Pixels(snapshot, first, native);
        int changed = 0;
        for (int i = 0; i < full.Length; i++)
            if (i % 4 == 3) Assert.Equal(full[i], tinted[i]);
            else { if (!mark) Assert.InRange(Math.Abs(full[i] * .5 - tinted[i]), 0, 1); else Assert.True(tinted[i] <= full[i] + 1); if (full[i] != tinted[i]) changed++; }
        Assert.True(changed > 10); Assert.Equal(0, compiler.Calls);
        await using var reopened = await V2DirectAndMaterialPackageFixture.Factory(snapshot, compiled).CreateAsync(snapshot);
        var direct = await V2DirectAndMaterialPackageFixture.Prepare(reopened, snapshot, times[0]); Assert.Equal(first.ImageEffects.ToArray(), direct.ImageEffects.ToArray());
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reopened.PrepareAsync(times[0], OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition), cancel.Token));
        Assert.Equal(direct.ImageEffects.ToArray(), (await V2DirectAndMaterialPackageFixture.Prepare(reopened, snapshot, times[0])).ImageEffects.ToArray());
    }
    [Fact]
    public async Task 纯资源direct不能借v2准入开放组或扩张token绑定为整行()
    {
        var compiler = new ResourceOnlyCompiler();
        foreach (var target in new[] { "group", "line" })
        {
            var definition = EffectPackageDefinition.Read(V2DirectAndMaterialPackageFixture.Package(true, target));
            var package = await CompiledEffectPackage.CompileAsync(definition, compiler, new PackageImageValidator(), new PackageShaderValidator(), new PackageImageGraphValidator(), new PackageMaterialValidator());
            var error = Assert.Throws<EffectPackageException>(() => package.CreateEffect("tint", new("blocked"), target == "group" ? new EffectBinding.Project() : new EffectBinding.Tokens([new("token")]), 0));
            Assert.Equal(target == "group" ? "GroupImageEffectUnsupported" : "PackageBindingNotAllowed", error.Code);
        }
        Assert.Equal(0, compiler.Calls);
    }

}
