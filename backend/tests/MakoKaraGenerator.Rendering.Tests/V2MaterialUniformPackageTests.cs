using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Execution;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class V2MaterialUniformPackageTests
{
    [Theory]
    [InlineData(0, "other", false)] // uniform-before-replace clears the old scope.
    [InlineData(1, "ink", true)]  // replace-before-uniform survives.
    [InlineData(2, "ink", false)] // A -> B -> A clears both older scopes.
    [InlineData(4, "other", false)] // absent old shader node disappears on replace.
    [InlineData(8, "ink", true)] // final float overwrites intrinsically valid int.
    public async Task 真实控制器按最终身份归约并保留完整参数与覆盖作用域(int mode, string material, bool uniform)
    {
        var package = V2DirectAndMaterialPackageFixture.Package();
        var snapshot = V2DirectAndMaterialPackageFixture.Snapshot(package, [V2DirectAndMaterialPackageFixture.Instance(package, mode: mode)]);
        var compiled = await V2DirectAndMaterialPackageFixture.Compile(snapshot);
        await using var session = await V2DirectAndMaterialPackageFixture.Factory(snapshot, compiled).CreateAsync(snapshot);
        var frame = await V2DirectAndMaterialPackageFixture.Prepare(session, snapshot);
        Assert.Equal(2, frame.Materials.Length);
        foreach (var binding in frame.Materials)
        {
            Assert.Equal(material, binding.Material.Handle.Identity.ExportId); Assert.Equal(.5, binding.Material.Parameters.GetNumber("amount"));
            Assert.Equal(uniform ? 1 : 0, binding.Material.Uniforms.Values.Count);
            if (uniform) Assert.Equal(.75, Assert.Single(binding.Material.Uniforms.Values).Value.Numbers[0]);
            Assert.Equal(compiled.Effects[0].Id, binding.InstanceId); Assert.Equal(compiled.Effects[0].RuntimeHash, binding.ArtifactHash);
        }
        using var native = CompiledPackageGraphExports.Compile(EffectPackageDefinition.Read(package));
        Assert.Contains(V2DirectAndMaterialPackageFixture.Pixels(snapshot, frame, native), value => value != 0);
        if (uniform)
        {
            var reference = V2DirectAndMaterialPackageFixture.Snapshot(package, [V2DirectAndMaterialPackageFixture.Instance(package, mode: 12)]);
            var referenceCompiled = await V2DirectAndMaterialPackageFixture.Compile(reference);
            await using var referenceSession = await V2DirectAndMaterialPackageFixture.Factory(reference, referenceCompiled).CreateAsync(reference);
            var baseFrame = await V2DirectAndMaterialPackageFixture.Prepare(referenceSession, reference);
            Assert.False(V2DirectAndMaterialPackageFixture.Pixels(reference, baseFrame, native).SequenceEqual(V2DirectAndMaterialPackageFixture.Pixels(snapshot, frame, native)));
        }
    }

    [Fact]
    public async Task 描边关闭和重新安装也完整清除旧覆盖作用域()
    {
        var package = V2DirectAndMaterialPackageFixture.Package(); var snapshot = V2DirectAndMaterialPackageFixture.Snapshot(package,
            [V2DirectAndMaterialPackageFixture.Instance(package, mode: 10)]);
        var compiled = await V2DirectAndMaterialPackageFixture.Compile(snapshot);
        await using var session = await V2DirectAndMaterialPackageFixture.Factory(snapshot, compiled).CreateAsync(snapshot);
        var frame = await V2DirectAndMaterialPackageFixture.Prepare(session, snapshot); var binding = Assert.Single(frame.Materials);
        Assert.Equal(PreparedMaterialSlot.Stroke, binding.Slot); Assert.Equal("ink", binding.Material.Handle.Identity.ExportId); Assert.Empty(binding.Material.Uniforms.Values);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 字族和标记随机远端时刻重复与重开得到相同完整材质及像素(bool mark)
    {
        var package = V2DirectAndMaterialPackageFixture.Package();
        var snapshot = V2DirectAndMaterialPackageFixture.Snapshot(package, [V2DirectAndMaterialPackageFixture.Instance(package, mark: mark ? 1 : 0, vary: 1)], mark);
        var compiled = await V2DirectAndMaterialPackageFixture.Compile(snapshot);
        await using var session = await V2DirectAndMaterialPackageFixture.Factory(snapshot, compiled).CreateAsync(snapshot);
        long[] times = mark ? [10_000_000, 9_250_000, 10_000_000] : [1_250_000, 600_250_000, 2_750_000, 1_250_000];
        using var native = CompiledPackageGraphExports.Compile(EffectPackageDefinition.Read(package));
        var first = await V2DirectAndMaterialPackageFixture.Prepare(session, snapshot, times[0]);
        foreach (long time in times) Assert.Equal(2, (await V2DirectAndMaterialPackageFixture.Prepare(session, snapshot, time)).Materials.Length);
        var repeated = await V2DirectAndMaterialPackageFixture.Prepare(session, snapshot, times[0]);
        Compare(first, repeated); Assert.Equal(V2DirectAndMaterialPackageFixture.Pixels(snapshot, first, native), V2DirectAndMaterialPackageFixture.Pixels(snapshot, repeated, native));
        await using var reopened = await V2DirectAndMaterialPackageFixture.Factory(snapshot, compiled).CreateAsync(snapshot);
        var direct = await V2DirectAndMaterialPackageFixture.Prepare(reopened, snapshot, times[0]); Compare(first, direct);
        Assert.Equal(V2DirectAndMaterialPackageFixture.Pixels(snapshot, first, native), V2DirectAndMaterialPackageFixture.Pixels(snapshot, direct, native));
        static void Compare(PreparedRenderFrame left, PreparedRenderFrame right)
        {
            Assert.Equal(left.Materials.Length, right.Materials.Length);
            for (int i = 0; i < left.Materials.Length; i++)
            {
                var a = left.Materials[i]; var b = right.Materials[i]; Assert.Equal(a.Owner, b.Owner); Assert.Equal(a.Target, b.Target); Assert.Equal(a.Slot, b.Slot);
                Assert.Equal(a.Material.Handle, b.Material.Handle); Assert.Equal(a.Material.Parameters.GetNumber("amount"), b.Material.Parameters.GetNumber("amount"));
                Assert.Equal(a.Material.Uniforms.Values.Keys.Order().ToArray(), b.Material.Uniforms.Values.Keys.Order().ToArray());
                foreach (var key in a.Material.Uniforms.Values.Keys) Assert.Equal(a.Material.Uniforms.Values[key].Numbers.ToArray(), b.Material.Uniforms.Values[key].Numbers.ToArray());
            }
        }
    }

    [Theory]
    [InlineData(3, "MaterialUniformIdentityMismatch", "ComposeOutputs")]
    [InlineData(5, "ShaderUniformNativeRange", "Evaluate")]
    [InlineData(6, "PackageParameterOutOfRange", "ComposeOutputs")]
    [InlineData(7, "MaterialUniformNodeInvalid", "ComposeOutputs")]
    [InlineData(9, "ShaderUniformValueMismatch", "ComposeOutputs")]
    [InlineData(14, "MaterialUniformIdentityMismatch", "ComposeOutputs")]
    public async Task 非法被覆盖值与最终shader失败仍保留真实owner时间及包产物(int mode, string code, string stage)
    {
        var package = V2DirectAndMaterialPackageFixture.Package(); var snapshot = V2DirectAndMaterialPackageFixture.Snapshot(package,
            [V2DirectAndMaterialPackageFixture.Instance(package, "owner", mode)]);
        var compiled = await V2DirectAndMaterialPackageFixture.Compile(snapshot);
        await using var session = await V2DirectAndMaterialPackageFixture.Factory(snapshot, compiled).CreateAsync(snapshot);
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await V2DirectAndMaterialPackageFixture.Prepare(session, snapshot));
        Assert.Equal(code, error.Code); Assert.Equal(stage, error.Stage); Assert.Equal(1_250_000, error.TimeUs);
        Assert.Equal(compiled.Effects[0].Id, error.InstanceId); Assert.Equal(compiled.Effects[0].PackageIdentity, error.PackageIdentity); Assert.Equal(compiled.Effects[0].RuntimeHash, error.ArtifactHash);
    }

    [Fact]
    public async Task 跨实例替换清早期uniform而后写uniform拥有最终部位且预算累计()
    {
        var package = V2DirectAndMaterialPackageFixture.Package();
        var cleared = V2DirectAndMaterialPackageFixture.Snapshot(package, [V2DirectAndMaterialPackageFixture.Instance(package, "early", 11), V2DirectAndMaterialPackageFixture.Instance(package, "replace", 12)]);
        var compiled = await V2DirectAndMaterialPackageFixture.Compile(cleared);
        await using var session = await V2DirectAndMaterialPackageFixture.Factory(cleared, compiled).CreateAsync(cleared);
        var frame = await V2DirectAndMaterialPackageFixture.Prepare(session, cleared);
        Assert.All(frame.Materials, binding => { Assert.Empty(binding.Material.Uniforms.Values); Assert.Equal("replace", binding.InstanceId!.Value.Value); });
        var surviving = V2DirectAndMaterialPackageFixture.Snapshot(package, [V2DirectAndMaterialPackageFixture.Instance(package, "replace", 12), V2DirectAndMaterialPackageFixture.Instance(package, "uniform", 13)]);
        var next = await V2DirectAndMaterialPackageFixture.Compile(surviving);
        await using var nextSession = await V2DirectAndMaterialPackageFixture.Factory(surviving, next).CreateAsync(surviving);
        Assert.All((await V2DirectAndMaterialPackageFixture.Prepare(nextSession, surviving)).Materials, binding => { Assert.Single(binding.Material.Uniforms.Values); Assert.Equal("uniform", binding.InstanceId!.Value.Value); });
        var excess = V2DirectAndMaterialPackageFixture.Snapshot(package, [V2DirectAndMaterialPackageFixture.Instance(package, "first", writes: 63), V2DirectAndMaterialPackageFixture.Instance(package, "last", writes: 64)]);
        var invalid = await V2DirectAndMaterialPackageFixture.Compile(excess);
        await using var invalidSession = await V2DirectAndMaterialPackageFixture.Factory(excess, invalid).CreateAsync(excess);
        var failure = await Assert.ThrowsAsync<EffectFailureException>(async () => await V2DirectAndMaterialPackageFixture.Prepare(invalidSession, excess));
        Assert.Equal("GraphOutputBudgetExceeded", failure.Code); Assert.Equal("ComposeOutputs", failure.Stage); Assert.Equal("last", failure.InstanceId.Value);
    }
}
