using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class BaseMaterialOwnershipTests
{
    [Fact]
    public async Task 后写Opacity及Affine不能抢Base材质owner且Prepared副本保持中立外观()
    {
        var package = BaseMaterialPackageFixture.Package(); var snapshot = BaseMaterialPackageFixture.Snapshot(package,
            [BaseMaterialPackageFixture.Instance(package, "actual", 11), BaseMaterialPackageFixture.Instance(package, "unrelated-last", 1)]);
        var compiled = await BaseMaterialPackageFixture.Compile(snapshot);
        await using var source = await BaseMaterialPackageFixture.Factory(snapshot, compiled).CreateAsync(snapshot);
        var frame = await BaseMaterialPackageFixture.Prepare(source, snapshot); var basic = Assert.Single(frame.FinalGeometry).BaseOnly;
        Assert.All(frame.BaseMaterials, binding =>
        {
            var owner = Assert.IsType<PreparedMaterialOwner.Script>(binding.Owner); Assert.Equal("actual", owner.InstanceId.Value);
            Assert.Equal(compiled.Effects[0].RuntimeHash, owner.ArtifactHash);
            Assert.Equal(new EffectPackageIdentity(package.PackageId, package.Revision, "controller"), owner.PackageIdentity);
            Assert.Equal(basic, binding.Target);
        });
        Assert.Equal(.5, frame.GetBaseAppearance(basic).Opacity);
        var copied = frame.WithOverlayOutput([], []).WithGroupOpacities([]).WithImageEffects(frame.ImageEffects);
        Assert.Same(frame.BaseAppearances, copied.BaseAppearances); Assert.Equal(frame.BaseMaterials.ToArray(), copied.BaseMaterials.ToArray());
        using var missing = new SkiaRenderPipeline();
        var error = Assert.Throws<EffectFailureException>(() => BaseMaterialRuntimeTests.Render(missing, snapshot, copied));
        Assert.Equal("actual", error.InstanceId.Value); Assert.Equal("MaterialResourcesRequired", error.Code); Assert.Equal(frame.TimeUs, error.TimeUs);
        Assert.Equal(new TextTarget.Base(basic), Assert.IsType<PreparedFrameException>(error.InnerException).TextTarget);
        var parent = BaseMaterialPackageFixture.Parent(frame);
        Assert.Equal("BaseMaterialOwnershipRequired", Assert.Throws<PreparedFrameException>(() => parent.WithTextPartitions(frame.BaseTransforms, frame.BaseStyles, frame.ClusterTransforms)).Code);
        Assert.Equal("InvalidPreparedBaseMaterial", Assert.Throws<PreparedFrameException>(() => parent.WithTextPartitions(frame.BaseTransforms, frame.BaseStyles, frame.ClusterTransforms,
            [.. frame.BaseMaterials, frame.BaseMaterials[0]])).Code);
    }

    [Fact]
    public async Task Source继承保留真实路径BaseUniform转为真实脚本而Ruby继续源owner()
    {
        var package = BaseMaterialPackageFixture.Package();
        foreach (bool uniform in new[] { false, true })
        {
            var snapshot = BaseMaterialPackageFixture.Snapshot(package, [BaseMaterialPackageFixture.Instance(package, "leaf", uniform ? 9 : 2)], source: true);
            var compiled = await BaseMaterialPackageFixture.Compile(snapshot);
            await using var source = await BaseMaterialPackageFixture.Factory(snapshot, compiled).CreateAsync(snapshot);
            var frame = await BaseMaterialPackageFixture.Prepare(source, snapshot); var token = Assert.Single(frame.FinalGeometry);
            Assert.Equal(2, frame.BaseMaterials.Length);
            foreach (var basic in frame.BaseMaterials)
            {
                var parent = frame.Materials.Single(binding => binding.Target == new ImageEffectTarget.Token(token.WithRuby) && binding.Slot == basic.Slot);
                var original = Assert.IsType<PreparedMaterialOwner.Source>(parent.Owner);
                if (!uniform)
                {
                    Assert.Equal(original, basic.Owner); Assert.Equal(parent.Material, basic.Material);
                    Assert.StartsWith("lyricStyle.", original.Reference.Path);
                }
                else
                {
                    Assert.Equal("leaf", Assert.IsType<PreparedMaterialOwner.Script>(basic.Owner).InstanceId.Value);
                    Assert.Equal(.75, Assert.Single(basic.Material.Uniforms.Values).Value.Numbers[0]); Assert.Empty(parent.Material.Uniforms.Values);
                }
            }
            using var device = BaseMaterialRuntimeTests.Device(false); using var native = CompiledPackageGraphExports.CompileWithDevice(compiled.Packages[0].Definition, null, device);
            using var resources = BaseMaterialRuntimeTests.Resources(native); using var renderer = new SkiaRenderPipeline(null,null,null,resources,false,device,false);
            using var pixels = BaseMaterialRuntimeTests.Render(renderer,snapshot,frame);
            using var parentPixels = BaseMaterialRuntimeTests.Render(renderer,snapshot,BaseMaterialPackageFixture.Parent(frame));
            BaseMaterialRuntimeTests.RubyEqual(parentPixels,pixels,token);
            double amount = uniform ? .75 : .5;
            byte[] expected = BaseMaterialPixelOracle.Expected(renderer,snapshot,frame,amount,amount,uniform ? 1 : .5);
            BaseMaterialPixelOracle.Compare(expected,pixels,2);
        }
    }

    [Theory]
    [InlineData(4, null)] [InlineData(5, "ShaderUniformNativeRange")]
    [InlineData(6, "PackageParameterOutOfRange")] [InlineData(7, "MaterialUniformNodeInvalid")]
    public async Task Base替换清scope但不能吞掉原始NativeSchema错误且最终错属于材质实例(int mode, string? expected)
    {
        var package = BaseMaterialPackageFixture.Package(); var snapshot = BaseMaterialPackageFixture.Snapshot(package,
            [BaseMaterialPackageFixture.Instance(package, "writer", mode), BaseMaterialPackageFixture.Instance(package, "unrelated-last", 1)]);
        var compiled = await BaseMaterialPackageFixture.Compile(snapshot);
        await using var source = await BaseMaterialPackageFixture.Factory(snapshot,compiled).CreateAsync(snapshot);
        if (expected is null)
        {
            var frame = await BaseMaterialPackageFixture.Prepare(source,snapshot); Assert.All(frame.BaseMaterials,b => Assert.Empty(b.Material.Uniforms.Values)); return;
        }
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await BaseMaterialPackageFixture.Prepare(source,snapshot));
        Assert.Equal(expected,error.Code); Assert.Equal("writer",error.InstanceId.Value);
        Assert.Equal(new EffectPackageIdentity(package.PackageId,package.Revision,"controller"),error.PackageIdentity);
        Assert.Equal(BaseMaterialPackageFixture.TimeUs,error.TimeUs);
        if (mode is 6 or 7) Assert.IsType<TextTarget.Base>(Assert.IsType<PreparedFrameException>(error.InnerException).TextTarget);
    }

    [Fact]
    public async Task 所有Base覆盖写入跨实例累计256门限且第257次准确归属()
    {
        var package = BaseMaterialPackageFixture.Package();
        foreach (int last in new[] { 64,65 })
        {
            var snapshot = BaseMaterialPackageFixture.Snapshot(package,[BaseMaterialPackageFixture.Instance(package,"first",11,64),BaseMaterialPackageFixture.Instance(package,"last",11,last)]);
            var compiled = await BaseMaterialPackageFixture.Compile(snapshot);
            await using var source = await BaseMaterialPackageFixture.Factory(snapshot,compiled).CreateAsync(snapshot);
            if (last == 64) Assert.All((await BaseMaterialPackageFixture.Prepare(source,snapshot)).BaseMaterials,b => Assert.Equal("last",Assert.IsType<PreparedMaterialOwner.Script>(b.Owner).InstanceId.Value));
            else
            {
                var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await BaseMaterialPackageFixture.Prepare(source,snapshot));
                Assert.Equal("GraphOutputBudgetExceeded",error.Code); Assert.Equal("last",error.InstanceId.Value); Assert.Equal("ComposeOutputs",error.Stage);
            }
        }
    }
}
